using System.Diagnostics;
using System.Text.Json;
using Dougong.Agent;
using Dougong.Kernel;
using Microsoft.Extensions.AI;

namespace Dougong.Agent.Comparison;

/// <summary>
/// 性能基线测量（M3 切片 3.7）。
/// </summary>
/// <remarks>
/// **只记录数字，不做性能承诺**。`iteration-roadmap.md` I0 要求的是
/// "把实测数字写进文档，而非宣称"——数字会随硬件、SDK、数据形状变化，
/// 承诺会变成无法兑现的债。
/// <para>
/// 三个场景对应三类真实担心：
/// <list type="bullet">
/// <item><b>大会话</b>：历史很长时，追加与重放是否退化</item>
/// <item><b>长会话</b>：反复装卸插件是否线性增长（泄漏会是超线性的）</item>
/// <item><b>并发会话</b>：多会话争用同一工作区时的实际代价</item>
/// </list>
/// </para>
/// </remarks>
public static class PerformanceBaselineChecks
{
    public static async Task<int> RunAsync()
    {
        Console.WriteLine($"# 环境：{Environment.OSVersion}，{Environment.ProcessorCount} 核，"
            + $"SDK {Environment.Version}");
        Console.WriteLine();

        await MeasureLargeSessionAsync();
        await MeasureTurnOverheadAsync();
        MeasureToolDeclarationCost();
        MeasureTokenEstimateCost();
        await MeasureLongSessionAsync();
        await MeasureConcurrentSessionsAsync();

        return 0;
    }

    /// <summary>
    /// 每轮对话的**本地开销**：除模型调用之外，我们自己的代码花了多少。
    /// </summary>
    /// <remarks>
    /// 为什么加这一项：原有三项测的都是存储、装卸、锁，而真实会话的耗时大头
    /// 是模型调用（数百毫秒到数秒）。**存储不是瓶颈**（0.73ms/条），
    /// 但"每轮把全部历史重新转成 ChatMessage"这件事的代价没被量过——
    /// 它随历史长度线性增长，而模型延迟不随历史线性增长。
    /// <para>
    /// 测法：构造 N 条历史，反复调用与运行时内部相同的转换路径
    /// （<see cref="LoopAgentRuntime"/> 每轮在 <c>BuildHistory</c> 里做的事），
    /// 统计单轮耗时。**只用公开 API**：跑一轮真实的 RunAsync，
    /// 用假模型让模型调用耗时接近零，剩下的就是本地开销。
    /// </para>
    /// </remarks>
    private static async Task MeasureTurnOverheadAsync()
    {
        Console.WriteLine("## 每轮本地开销（假模型，模型延迟≈0）");

        foreach (var historySize in new[] { 0, 100, 1000, 5000 })
        {
            var history = new List<AgentMessage>(historySize);
            for (var i = 0; i < historySize; i++)
            {
                history.Add(i % 2 == 0
                    ? new AgentMessage(AgentRole.User, $"问题 {i}：" + new string('x', 180))
                    : new AgentMessage(AgentRole.Assistant, "回答 " + new string('y', 180)));
            }

            var client = new InstantChatClient();
            var dispatcher = new ToolDispatcher();
            var runtime = new LoopAgentRuntime(client, dispatcher);

            // 预热：JIT + 首次反射，不计入测量。
            await foreach (var _ in runtime.RunAsync(
                new AgentRunRequest("perf-turn", "t0", history, "预热"))) { }

            const int turns = 20;
            // 同时测分配：时间小不代表分配小。长会话里 GC 压力是隐形成本，
            // 而"每轮重建全部历史"这种写法最容易体现为分配量随历史线性增长。
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            var before = GC.GetTotalAllocatedBytes(precise: true);

            var watch = Stopwatch.StartNew();
            for (var t = 0; t < turns; t++)
            {
                await foreach (var _ in runtime.RunAsync(
                    new AgentRunRequest("perf-turn", $"t{t + 1}", history, "下一个问题"))) { }
            }

            watch.Stop();
            var allocated = GC.GetTotalAllocatedBytes(precise: true) - before;
            var perTurn = watch.Elapsed.TotalMilliseconds / turns;
            var perTurnBytes = allocated / turns;

            Console.WriteLine($"  {historySize,5} 条历史：{perTurn,7:F2} ms/轮"
                + $"，分配 {perTurnBytes / 1024.0,8:F1} KB/轮"
                + $"（共 {turns} 轮，{watch.ElapsedMilliseconds} ms）");
        }

        Console.WriteLine("  注：这是 RoundTrip 的本地部分，不含真实网络与模型生成时间。");
        Console.WriteLine();
    }

    /// <summary>大会话：1000 条历史的追加与重放耗时。</summary>
    private static async Task MeasureLargeSessionAsync()
    {
        var path = Path.Combine(Path.GetTempPath(), $"dougong-perf-large-{Guid.NewGuid():N}.jsonl");
        try
        {
            const int records = 1000;

            var openWatch = Stopwatch.StartNew();
            var store = await JsonlAgentStore.OpenAsync(path, "perf-large");
            openWatch.Stop();

            var appendWatch = Stopwatch.StartNew();
            for (var i = 0; i < records; i++)
            {
                await store.CommitAsync(new StoreRecord(
                    0, StoreRecordKind.UserInput,
                    JsonSerializer.SerializeToElement(new { index = i, text = new string('x', 200) })));
            }

            appendWatch.Stop();
            await store.DisposeAsync();

            // 重放：重新打开并读完全部记录。
            var replayWatch = Stopwatch.StartNew();
            var reopened = await JsonlAgentStore.OpenAsync(path, "perf-large");
            var count = 0;
            await foreach (var _ in reopened.ReadCommittedAsync()) count++;
            replayWatch.Stop();
            await reopened.DisposeAsync();

            var size = new FileInfo(path).Length;

            Console.WriteLine($"## 大会话（{records} 条，每条约 220 字节）");
            Console.WriteLine($"  打开：{openWatch.ElapsedMilliseconds} ms");
            Console.WriteLine($"  追加 {count} 条：{appendWatch.ElapsedMilliseconds} ms"
                + $"（{appendWatch.Elapsed.TotalMilliseconds / count:F2} ms/条）");
            Console.WriteLine($"  重放 {count} 条：{replayWatch.ElapsedMilliseconds} ms"
                + $"（{replayWatch.Elapsed.TotalMilliseconds / count:F2} ms/条）");
            Console.WriteLine($"  文件大小：{size / 1024} KB");
            Console.WriteLine();
        }
        finally
        {
            try { File.Delete(path); } catch (IOException) { }
        }
    }

    /// <summary>长会话：插件装卸轮次增加时的耗时趋势。</summary>
    /// <remarks>
    /// 关心的是**是否线性**。若 20 轮耗时明显超过 1 轮的 20 倍，
    /// 说明有随轮次累积的开销——那通常意味着泄漏或历史表变长。
    /// </remarks>
    private static async Task MeasureLongSessionAsync()
    {
        var samplePath = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "..",
            "samples", "Dougong.Sample.Plugin", "bin", "Release", "net10.0",
            "Dougong.Sample.Plugin.dll"));

        if (!File.Exists(samplePath))
        {
            Console.WriteLine("## 长会话：跳过（找不到样例插件，先构建 samples）");
            Console.WriteLine();
            return;
        }

        Console.WriteLine("## 长会话（插件装卸）");

        foreach (var rounds in new[] { 1, 10, 20 })
        {
            await using var kernel = new DougongKernel();
            var watch = Stopwatch.StartNew();

            for (var i = 0; i < rounds; i++)
            {
                var handle = await kernel.LoadAsync(samplePath);
                await kernel.UnloadAsync(handle.Id);
            }

            watch.Stop();
            Console.WriteLine($"  {rounds,2} 轮：{watch.ElapsedMilliseconds,4} ms"
                + $"（{watch.Elapsed.TotalMilliseconds / rounds:F1} ms/轮）");
        }

        Console.WriteLine();
    }

    /// <summary>并发会话：多会话争用同一工作区。</summary>
    /// <remarks>
    /// 工作区锁是进程级共享的（G9），因此这里测的是**真实的争用代价**，
    /// 不是"每个会话各锁各的"那种假并发。
    /// </remarks>
    private static async Task MeasureConcurrentSessionsAsync()
    {
        const int sessions = 4;
        const int callsPerSession = 50;
        var workspace = Path.Combine(Path.GetTempPath(), "dougong-perf-ws");
        Directory.CreateDirectory(workspace);

        try
        {
            // 顺序执行作为基线。
            var sequential = await RunSessionsAsync(sessions, callsPerSession, workspace, concurrent: false);
            var concurrent = await RunSessionsAsync(sessions, callsPerSession, workspace, concurrent: true);

            Console.WriteLine($"## 并发会话（{sessions} 会话 × {callsPerSession} 次工具执行）");
            Console.WriteLine($"  顺序：{sequential} ms");
            Console.WriteLine($"  并发：{concurrent} ms"
                + $"（加速比 {sequential / (double)Math.Max(concurrent, 1):F2}×）");
            Console.WriteLine("  注：同一工作区，`WorkspaceExclusive` 工具本应互斥；"
                + "不同资源键的读可以并行。");
            Console.WriteLine();
        }
        finally
        {
            try { Directory.Delete(workspace, recursive: true); } catch (IOException) { }
        }
    }

    private static async Task<long> RunSessionsAsync(
        int sessions, int calls, string workspace, bool concurrent)
    {
        async Task OneAsync(int index)
        {
            using var gate = new ToolResourceGate();
            var plan = new ToolExecutionPlan(ToolConcurrency.Serial)
            {
                Reads = [$"file:{workspace}/f{index}.txt"],
            };

            for (var i = 0; i < calls; i++)
            {
                using var lease = await gate.EnterAsync(plan, workspace);
                // 工具本身的开销不应主导测量，因此这里不做实际 IO。
            }
        }

        var watch = Stopwatch.StartNew();
        if (concurrent)
        {
            await Task.WhenAll(Enumerable.Range(0, sessions).Select(OneAsync));
        }
        else
        {
            for (var i = 0; i < sessions; i++) await OneAsync(i);
        }

        watch.Stop();
        return watch.ElapsedMilliseconds;
    }

    /// <summary>
    /// 工具声明序列化的代价：每次模型请求、每个工具都重新 parse 一遍 schema。
    /// </summary>
    /// <remarks>
    /// 这是连接器构造请求体的必经路径。若代价显著，说明"每请求重复解析
    /// 不可变 DTO"是值得缓存的地方；若不显著，则不动它——优化要对着数据做。
    /// </remarks>
    private static void MeasureToolDeclarationCost()
    {
        Console.WriteLine("## 工具声明序列化（连接器构造请求体的路径）");

        // 一个接近真实的 schema：文件工具那种带 required 与 properties 的形状。
        foreach (var toolCount in new[] { 4, 10, 30 })
        {
            var descriptors = new List<ToolDescriptor>(toolCount);
            for (var i = 0; i < toolCount; i++)
            {
                descriptors.Add(new ToolDescriptor(
                    $"tool_{i}", $"tool_{i}", "1.0.0", "读文件",
                    JsonDocument.Parse("""
                        {"type":"object","properties":{
                          "path":{"type":"string","description":"工作区内的相对路径"},
                          "encoding":{"type":"string","enum":["utf8","utf16"]},
                          "maxBytes":{"type":"integer","minimum":1,"maximum":1048576}
                        },"required":["path"]}
                        """).RootElement.Clone()));
            }

            // 预热
            for (var w = 0; w < 20; w++)
                foreach (var d in descriptors) ToolDeclarations.ToSchemaNode(d);

            const int iterations = 200;
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            var before = GC.GetTotalAllocatedBytes(precise: true);

            var watch = Stopwatch.StartNew();
            for (var it = 0; it < iterations; it++)
                foreach (var d in descriptors) ToolDeclarations.ToSchemaNode(d);
            watch.Stop();

            var allocated = GC.GetTotalAllocatedBytes(precise: true) - before;
            var perRequest = watch.Elapsed.TotalMilliseconds / iterations;
            var perRequestBytes = allocated / iterations;

            Console.WriteLine($"  {toolCount,2} 个工具：{perRequest,6:F3} ms/请求"
                + $"，分配 {perRequestBytes / 1024.0,7:F1} KB/请求");
        }

        Console.WriteLine("  注：一次会话每轮都要构造一次请求体，这里只测声明序列化本身。");
        Console.WriteLine();
    }

    /// <summary>
    /// 压缩决策的估算成本：每轮都要遍历全部历史字符做 token 估算。
    /// </summary>
    /// <remarks>
    /// 这是"启用压缩后每轮固定开销"的主要来源。它随历史长度线性增长，
    /// 且在每次模型调用前都要跑一遍（见 <c>SlidingWindowCompactor.Plan</c>）。
    /// 量它才能判断"要不要引入增量估算"这类改动。
    /// </remarks>
    private static void MeasureTokenEstimateCost()
    {
        Console.WriteLine("## token 估算（压缩决策路径）");

        foreach (var historySize in new[] { 100, 1000, 5000 })
        {
            var history = new List<AgentMessage>(historySize);
            for (var i = 0; i < historySize; i++)
                history.Add(new AgentMessage(AgentRole.User, $"问题 {i}：" + new string('x', 180)));

            var compactor = new SlidingWindowCompactor(preservedHead: 2, preservedTail: 6);
            // 预算拉到极大，使 Plan 走到"估算完 → 不触发"这条最常见的路径。
            var input = new ContextLimits
            {
                ContextWindowTokens = 100_000_000,
                MaxOutputTokens = 1024,
            }.ToInput(history);

            // 预热
            for (var w = 0; w < 10; w++) compactor.Plan(input);

            const int iterations = 100;
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            var before = GC.GetTotalAllocatedBytes(precise: true);

            var watch = Stopwatch.StartNew();
            for (var it = 0; it < iterations; it++) compactor.Plan(input);
            watch.Stop();

            var allocated = GC.GetTotalAllocatedBytes(precise: true) - before;
            var perCall = watch.Elapsed.TotalMilliseconds / iterations;

            Console.WriteLine($"  {historySize,5} 条历史：{perCall,6:F3} ms/次决策"
                + $"，分配 {allocated / iterations / 1024.0,7:F1} KB/次");
        }

        Console.WriteLine("  注：每次模型调用前跑一次；未启用压缩时无此开销。");
        Console.WriteLine();
    }

    /// <summary>
    /// 零延迟模型：只测本地开销，不含网络与生成时间。
    /// </summary>
    /// <remarks>
    /// 单 chunk 直接返回固定文本。用它时，一轮 <c>RunAsync</c> 的耗时
    /// 几乎全部是我们自己的代码——历史重建、记忆注入、事件封装。
    /// </remarks>
    private sealed class InstantChatClient : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> m, ChatOptions? o = null, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> m, ChatOptions? o = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.CompletedTask;
            yield return new ChatResponseUpdate(ChatRole.Assistant, "ok");
        }

        public object? GetService(Type t, object? k = null) => null;
        public void Dispose() { }
    }
}
