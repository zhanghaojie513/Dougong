using System.Text.Json;
using Dougong.Agent;
using Microsoft.Extensions.AI;

namespace Dougong.Agent.Comparison;

/// <summary>
/// 大结果落盘取回的验收。
/// </summary>
/// <remarks>
/// <b>可观察面有两条，缺一不可</b>：
/// <list type="number">
/// <item><b>模型看到的那条工具消息</b>里必须出现可取回的路径</item>
/// <item><b>那个路径必须真的能读</b>——用 <c>ReadFileTool</c> 从工作区读一遍，
/// 内容要等于完整原文</item>
/// </list>
/// 只验证第 1 条是不够的：标记里写一个读不到的路径比不写更糟，
/// 模型会照着去读、失败，然后浪费一轮去猜。
/// </remarks>
public static class SpillChecks
{
    public static async Task<int> RunAsync()
    {
        var failures = 0;
        failures += await CheckAsync("big-result-is-spilled-and-marked", SpilledAndMarkedAsync);
        failures += await CheckAsync("spilled-path-is-readable-by-read-file", ReadableAsync);
        failures += await CheckAsync("small-result-is-not-spilled", SmallNotSpilledAsync);
        failures += await CheckAsync("spill-path-is-relative-not-absolute", RelativePathAsync);
        failures += await CheckAsync("spill-disabled-still-truncates-honestly", DisabledStillTruncatesAsync);
        failures += await CheckAsync("spill-files-are-hidden-from-glob", GlobHidesSpillAsync);
        failures += await CheckAsync("spill-root-name-is-derived-not-duplicated", RootNameIsDerivedAsync);
        failures += await CheckAsync("identical-content-reuses-one-file", IdenticalContentDedupAsync);

        Console.WriteLine(failures == 0
            ? "SPILL: ALL PASS"
            : $"SPILL: {failures} FAILURE(S)");
        return failures;
    }

    private const string BigPayloadMarker = "MIDDLE-SECRET";

    /// <summary>超限结果落盘，且标记里写出路径。</summary>
    private static async Task<string?> SpilledAndMarkedAsync()
    {
        using var tree = new TempTree();
        var runtime = BuildRuntime(tree, spill: true);

        var toolMessage = await FindToolMessageAsync(runtime);

        if (toolMessage is null) return "没有产生工具消息";
        if (toolMessage.Length > ToolResultLimits.MaxContextChars + 512)
            return $"工具消息没有被裁剪（{toolMessage.Length} 字符）";

        return toolMessage.Contains(SpillStore.DirectoryName, StringComparison.Ordinal)
            ? null
            : $"标记里没有取回路径：{Excerpt(toolMessage)}";
    }

    /// <summary>报出的路径必须能被 <c>read_file</c> 读到完整原文。</summary>
    /// <remarks>
    /// <b>这是整条能力的兑现点</b>：落盘了但读不到，等于没落盘。
    /// </remarks>
    private static async Task<string?> ReadableAsync()
    {
        using var tree = new TempTree();
        var runtime = BuildRuntime(tree, spill: true);

        var toolMessage = await FindToolMessageAsync(runtime);
        if (toolMessage is null) return "没有产生工具消息";

        var path = ExtractSpillPath(toolMessage);
        if (path is null) return $"标记里没有可解析的路径：{Excerpt(toolMessage)}";

        // 用真实的 read_file 工具读，而不是直接 File.ReadAllText：
        // 要证明的是"模型拿这条路径能读到"，而不是"文件在磁盘上存在"。
        var reader = new ReadFileTool(new LocalFileSystem(tree.Root));
        var result = await reader.ExecuteAsync(
            new ToolInvocation(
                "read", "read_file",
                JsonDocument.Parse(JsonSerializer.Serialize(new { path })).RootElement.Clone()),
            CancellationToken.None);

        // read_file 用 AgentToolResult；读失败时它抛异常（由 dispatcher 转成 Failed），
        // 这里直接调工具，所以失败会以异常形式出现——由外层 CheckAsync 捕获并报出。
        var text = result.Content.GetRawText();
        return text.Contains(BigPayloadMarker, StringComparison.Ordinal)
            ? null
            : $"取回的内容里没有原文标志（读到 {text.Length} 字符），说明落盘的不是完整原文";
    }

    /// <summary>未超限的结果不落盘。</summary>
    /// <remarks>
    /// 每次都落盘会让工作区堆满小文件，而它们没有取回价值——
    /// 模型本来就看得到全文。
    /// </remarks>
    private static async Task<string?> SmallNotSpilledAsync()
    {
        using var tree = new TempTree();
        var runtime = BuildRuntime(tree, spill: true, payloadSize: 100);

        var toolMessage = await FindToolMessageAsync(runtime);
        if (toolMessage is null) return "没有产生工具消息";

        if (toolMessage.Contains(SpillStore.DirectoryName, StringComparison.Ordinal))
            return "未超限的结果也被落盘了";

        // 磁盘上也不该出现文件。
        var spillDir = Path.Combine(tree.Root, ".dougong", "spill");
        var count = Directory.Exists(spillDir)
            ? Directory.GetFiles(spillDir).Length
            : 0;
        return count == 0 ? null : $"磁盘上出现了 {count} 个 spill 文件";
    }

    /// <summary>路径必须是相对工作区的。</summary>
    /// <remarks>
    /// <b>绝对路径会带出用户名</b>，进模型上下文与会话日志后无法跨机器使用；
    /// 而且 <c>read_file</c> 的路径守卫对绝对路径的处理与相对路径不同。
    /// </remarks>
    private static async Task<string?> RelativePathAsync()
    {
        using var tree = new TempTree();
        var runtime = BuildRuntime(tree, spill: true);

        var toolMessage = await FindToolMessageAsync(runtime);
        if (toolMessage is null) return "没有产生工具消息";

        var path = ExtractSpillPath(toolMessage);
        if (path is null) return "标记里没有可解析的路径";

        if (Path.IsPathRooted(path)) return $"路径是绝对的：{path}";
        if (path.Contains(tree.Root, StringComparison.OrdinalIgnoreCase))
            return "路径里泄漏了工作区的绝对前缀";

        return path.StartsWith(SpillStore.DirectoryName, StringComparison.Ordinal)
            ? null
            : $"路径不在 {SpillStore.DirectoryName} 下：{path}";
    }

    /// <summary>关闭 spill 时仍然如实说明截断，只是不给路径。</summary>
    /// <remarks>
    /// <b>不能因为关了就假装没截断</b>：那会让模型按完整数据推断，
    /// 而这个错误比"信息不可得"严重得多。
    /// </remarks>
    private static async Task<string?> DisabledStillTruncatesAsync()
    {
        using var tree = new TempTree();
        var runtime = BuildRuntime(tree, spill: false);

        var toolMessage = await FindToolMessageAsync(runtime);
        if (toolMessage is null) return "没有产生工具消息";

        if (!toolMessage.Contains("已截断", StringComparison.Ordinal))
            return "关闭 spill 后不再说明截断";

        if (toolMessage.Contains(SpillStore.DirectoryName, StringComparison.Ordinal))
            return "关闭 spill 后仍给出了取回路径";

        var spillDir = Path.Combine(tree.Root, ".dougong", "spill");
        return Directory.Exists(spillDir) && Directory.GetFiles(spillDir).Length > 0
            ? "关闭 spill 后仍然落盘了"
            : null;
    }

    /// <summary>spill 目录不出现在 glob 结果里。</summary>
    /// <remarks>
    /// <b>那是运行时产物，不是项目文件</b>。让模型在"项目里有哪些文件"里
    /// 看到自己的 spill 副本，会让它把副本当成源码。
    /// </remarks>
    private static async Task<string?> GlobHidesSpillAsync()
    {
        using var tree = new TempTree();

        // 造一个真实的 spill 文件，再用真 glob 扫——走行为而不是内部判据。
        var store = new SpillStore(tree.Root);
        var written = await store.WriteAsync("big", "spilled content", CancellationToken.None);
        if (written is null) return "spill 文件没写成功，这条检查无从验证";

        File.WriteAllText(Path.Combine(tree.Root, "app.cs"), "// app");

        var tool = new GlobTool(new LocalFileSystem(tree.Root));
        var result = await tool.ExecuteAsync(
            new ToolInvocation(
                "g", "glob",
                JsonDocument.Parse("""{"pattern":"*"}""").RootElement.Clone()),
            CancellationToken.None);

        var json = result.Content.GetRawText();
        return json.Contains("spill", StringComparison.Ordinal)
            ? $"glob 扫到了 spill 文件：{json}"
            : null;
    }

    /// <summary>同一份内容落盘两次只得到一个文件。</summary>
    /// <remarks>
    /// <b>这条钉的是"注释说会去重、实现没去重"</b>：第一版的文件名带秒级时间戳，
    /// 跨秒的两次调用生成两个内容完全相同的文件，而注释写着"不会反复落盘"。
    /// 重试正是最常产生重复内容的场景——一次 429 之后重发同一个工具调用。
    /// <para>
    /// 中间刻意等过一秒：不等的话时间戳恰好相同，旧实现也能通过。
    /// </para>
    /// </remarks>
    private static async Task<string?> IdenticalContentDedupAsync()
    {
        using var tree = new TempTree();
        var store = new SpillStore(tree.Root);
        var content = "dedup-" + new string('z', 50_000);

        var first = await store.WriteAsync("big", content, CancellationToken.None);
        if (first is null) return "第一次落盘失败";

        // 跨过一秒，让带时间戳的实现必然生成不同的名字。
        await Task.Delay(1100);
        var second = await store.WriteAsync("big", content, CancellationToken.None);

        if (first != second)
            return $"同样的内容得到两个路径：{first} / {second}";

        var spillDir = Path.Combine(tree.Root, ".dougong", "spill");
        var files = Directory.GetFiles(spillDir);
        return files.Length == 1
            ? null
            : $"磁盘上有 {files.Length} 个文件，应只有 1 个";
    }

    /// <summary>
    /// glob 判据里的目录名必须与 spill 的实际目录同源。
    /// </summary>
    /// <remarks>
    /// <b>这条钉的是"两处各写字面量"</b>：<c>SpillStore.DirectoryName</c> 是
    /// <c>.dougong/spill</c>，而 glob 的噪声判据看的是顶层名 <c>.dougong</c>。
    /// 两处各写一遍时，改一处漏一处**不会报错**——症状是 spill 文件出现在
    /// "项目里有哪些文件"的结果里，模型会把它们当源码。
    /// <para>
    /// 判据：顶层名必须真的是 spill 目录的第一段。
    /// </para>
    /// </remarks>
    private static Task<string?> RootNameIsDerivedAsync()
    {
        var expected = SpillStore.DirectoryName.Split('/')[0];

        if (SpillStore.RootDirectoryName != expected)
            return Task.FromResult<string?>(
                $"RootDirectoryName 与 DirectoryName 不是从同一处来的："
                + $"{SpillStore.RootDirectoryName} vs {expected}");

        // glob 的判据必须认得这个名字。**走行为而不是内部方法**：
        // IsNoiseDirectory 是 internal，而检查要证明的是"用户看得到的结果"。
        var dir = Path.Combine(Path.GetTempPath(), "dougong-rootname-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new SpillStore(dir);
            var written = store.WriteAsync("t", "content", CancellationToken.None)
                .AsTask().GetAwaiter().GetResult();

            if (written is null) return Task.FromResult<string?>("spill 没写成功，无法验证");

            File.WriteAllText(Path.Combine(dir, "project.cs"), "// project");

            var glob = new GlobTool(new LocalFileSystem(dir));
            var result = glob.ExecuteAsync(
                new ToolInvocation("g", "glob",
                    JsonDocument.Parse("""{"pattern":"*"}""").RootElement.Clone()),
                CancellationToken.None).AsTask().GetAwaiter().GetResult();

            var json = result.Content.GetRawText();
            return Task.FromResult<string?>(json.Contains("spill", StringComparison.Ordinal)
                ? $"glob 扫到了 spill 文件（顶层名 {SpillStore.RootDirectoryName} 不在噪声目录里）：{json}"
                : null);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); }
            catch { /* 清理失败不影响结论 */ }
        }
    }

    // ── 支撑 ─────────────────────────────────────────────────────

    /// <summary>造一个 runtime，其工具返回指定大小的载荷。</summary>
    private static LoopAgentRuntime BuildRuntime(
        TempTree tree, bool spill, int payloadSize = 60_000)
    {
        // 中间放标志：它必然落在被丢弃的中段，只有取回原文才看得到。
        var tail = new string('x', payloadSize / 2);
        var payload = new string('a', payloadSize / 2) + BigPayloadMarker + tail;

        var client = new OneToolThenTextClient();
        var runtime = new LoopAgentRuntime(client, new PayloadDispatcher(payload))
        {
            Spill = spill ? new SpillStore(tree.Root) : null,
        };

        return runtime;
    }

    private static async Task<List<AgentEvent>> CollectAsync(LoopAgentRuntime runtime)
    {
        var events = new List<AgentEvent>();
        await foreach (var e in runtime.RunAsync(
            new AgentRunRequest("s", "t", [], "go"), CancellationToken.None))
            events.Add(e);
        return events;
    }

    /// <summary>找到本轮写进历史的工具消息文本。</summary>
    /// <remarks>
    /// 通过第二轮的请求观察：模型在第二轮收到的历史里就有那条 tool 消息。
    /// 这比反过来解析 runtime 内部状态更贴近"模型实际看到什么"。
    /// </remarks>
    private static async Task<string?> FindToolMessageAsync(LoopAgentRuntime runtime)
    {
        // 跑完整两轮：第一轮请求工具，第二轮把工具结果作为历史的一部分收到，
        // 那一刻记下的文本就是"模型实际看到的工具消息"。
        await CollectAsync(runtime);
        return OneToolThenTextClient.LastToolMessage;
    }

    private static string? ExtractSpillPath(string text)
    {
        // 标记形如：完整原文已保存到 .dougong/spill/xxx.txt，需要中间部分时用 read_file 读它。
        var marker = "完整原文已保存到 ";
        var start = text.IndexOf(marker, StringComparison.Ordinal);
        if (start < 0) return null;

        start += marker.Length;
        var end = text.IndexOf('，', start);
        if (end < 0) end = text.IndexOf(']', start);
        return end > start ? text[start..end].Trim() : null;
    }

    private static string Excerpt(string text) =>
        text.Length <= 200 ? text : text[..200] + "…";

    /// <summary>第一轮请求工具，第二轮给出文本并记录收到的历史。</summary>
    private sealed class OneToolThenTextClient : IChatClient
    {
        private int _round;

        public static string? LastToolMessage { get; private set; }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> m, ChatOptions? o = null, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? o = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.Yield();
            _round++;

            if (_round == 1)
            {
                yield return new ChatResponseUpdate(ChatRole.Assistant,
                    [new FunctionCallContent("c1", "big", new Dictionary<string, object?>())]);
                yield break;
            }

            // 第二轮的请求里包含上一轮的工具结果——记下它，那就是"模型看到的"。
            LastToolMessage = messages
                .Where(msg => msg.Role == ChatRole.Tool)
                .Select(msg => msg.Text)
                .LastOrDefault();

            yield return new ChatResponseUpdate(ChatRole.Assistant, "done");
        }

        public object? GetService(Type t, object? k = null) => null;
        public void Dispose() { }
    }

    /// <summary>返回固定载荷的假工具。</summary>
    private sealed class PayloadDispatcher(string payload) : IToolDispatcher
    {
        public IReadOnlyList<ToolDescriptor> Tools { get; } =
        [
            new("big", "big", "1.0.0", "返回一段大载荷",
                JsonDocument.Parse("""{"type":"object","properties":{}}""").RootElement.Clone()),
        ];

        public IDisposable Register(IAgentTool tool) => throw new NotSupportedException();

        public ValueTask<ToolDispatchResult> DispatchAsync(
            ToolInvocation invocation, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new ToolDispatchResult(
                invocation.InvocationId, invocation.ToolId, ToolCallOutcome.Succeeded,
                new AgentToolResult(
                    JsonDocument.Parse(JsonSerializer.Serialize(new { text = payload }))
                        .RootElement.Clone())));
    }

    private sealed class TempTree : IDisposable
    {
        public TempTree()
        {
            Root = Path.Combine(Path.GetTempPath(), $"dougong-spill-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Root);
        }

        public string Root { get; }

        public void Dispose()
        {
            try { Directory.Delete(Root, recursive: true); }
            catch { /* 清理失败不影响结论 */ }
        }
    }

    private static async Task<int> CheckAsync(string name, Func<Task<string?>> action)
    {
        try
        {
            var failure = await action().ConfigureAwait(false);
            if (failure is null)
            {
                Console.WriteLine($"PASS {name}");
                return 0;
            }

            Console.WriteLine($"FAIL {name}: {failure}");
            return 1;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"FAIL {name}: {ex.GetType().Name}: {ex.Message}");
            return 1;
        }
    }
}
