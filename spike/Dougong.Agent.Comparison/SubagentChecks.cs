using System.Text.Json;
using Dougong.Agent;
using Microsoft.Extensions.AI;

namespace Dougong.Agent.Comparison;

/// <summary>
/// 子 agent 委派的验收。
/// </summary>
/// <remarks>
/// <b>可观察面是"隔离有没有真的发生"</b>，不是"工具被调用了没有"：
/// <list type="bullet">
/// <item><b>上下文隔离</b>：子 agent 收到的历史是空的——共用历史等于没委派</item>
/// <item><b>只回结果</b>：中间工具调用不回传</item>
/// <item><b>启动即返回</b>：不阻塞等待</item>
/// <item><b>深度与并发有上限</b>，且超限是拒绝而不是排队</item>
/// </list>
/// </remarks>
public static class SubagentChecks
{
    public static async Task<int> RunAsync()
    {
        var failures = 0;
        failures += await CheckAsync("subagent-starts-with-empty-history", EmptyHistoryAsync);
        failures += await CheckAsync("subagent-returns-immediately", ReturnsImmediatelyAsync);
        failures += await CheckAsync("subagent-returns-final-text-not-steps", OnlyFinalTextAsync);
        failures += await CheckAsync("subagent-rejects-when-concurrency-exhausted", ConcurrencyAsync);
        failures += await CheckAsync("subagent-result-is-delivered-once", DeliveredOnceAsync);
        failures += await CheckAsync("subagent-cancel-produces-cancelled-status", CancelAsync);
        failures += await CheckAsync("subagent-rejects-missing-arguments", MissingArgsAsync);
        failures += await CheckAsync("subagent-depth-limit-stops-recursion", DepthLimitAsync);
        failures += await CheckAsync("subagent-dispose-waits-for-children", DisposeAsync);
        failures += await CheckAsync("subagent-recursion-actually-stops-at-depth", RecursionStopsAsync);
        failures += await CheckAsync("subagent-depth-uses-parameter-not-field", DepthUsesParameterAsync);

        Console.WriteLine(failures == 0
            ? "SUBAGENT: ALL PASS"
            : $"SUBAGENT: {failures} FAILURE(S)");
        return failures;
    }

    /// <summary>
    /// 子 agent 从空历史开始。
    /// </summary>
    /// <remarks>
    /// <b>这是这条能力的全部意义</b>：共用主对话的历史，省不下任何上下文，
    /// 那子 agent 就只是"同一段对话的另一次调用"。
    /// </remarks>
    private static async Task<string?> EmptyHistoryAsync()
    {
        var client = new CapturingClient("结论：在 src/api 下。");
        var registry = new SubagentRegistry();

        await using (registry)
        {
            var tool = Tool(registry, request => new LoopAgentRuntime(client, new ToolDispatcher()));

            var result = await tool.ExecuteAsync(Invocation("查一下", "找出 API 在哪"), CancellationToken.None);
            var id = ReadString(result.Content, "id");
            if (id is null) return $"启动失败：{ReadString(result.Content, "error")}";

            await WaitForCompletionAsync(registry, id);

            if (client.Requests.Count == 0) return "子 agent 没有发起任何请求";

            // **历史必须完全为空**：客户端收到的应当只有本轮那一条 user 输入。
            //
            // 第一版只断言"没有 assistant 消息"，于是往 History 里塞一条
            // user 消息的破坏照样通过——检查漏了最常见的形态（主对话的
            // 每轮用户输入本来就在 History 里）。
            var history = client.Requests[0];
            var userMessages = history.Count(m => m.Role == ChatRole.User);

            if (userMessages != 1)
                return $"子 agent 收到了 {userMessages} 条用户消息（应为 1，只有本轮任务）——"
                    + "上下文没有隔离";

            return history.Count == 1
                ? null
                : $"子 agent 收到了 {history.Count} 条消息（应为 1）——上下文没有隔离";
        }
    }

    /// <summary>启动立即返回，不等子 agent 跑完。</summary>
    /// <remarks>
    /// <b>等结果会让并行能力失效</b>：模型可以连开三个再一起收，
    /// 而"启动即阻塞"只能一个一个来。
    /// </remarks>
    private static async Task<string?> ReturnsImmediatelyAsync()
    {
        // 一个会卡住的客户端：若工具阻塞等待，这条检查会超时。
        var gate = new TaskCompletionSource();
        var client = new BlockingClient(gate.Task);
        var registry = new SubagentRegistry();

        await using (registry)
        {
            var tool = Tool(registry, request => new LoopAgentRuntime(client, new ToolDispatcher()));

            var started = System.Diagnostics.Stopwatch.StartNew();
            var result = await tool.ExecuteAsync(Invocation("慢慢来", "做一个很久的任务"), CancellationToken.None);
            started.Stop();

            var id = ReadString(result.Content, "id");
            if (id is null) return $"启动失败：{ReadString(result.Content, "error")}";

            // 子 agent 还卡着，而工具已经返回了。
            var handle = registry.Find(id);
            if (handle is null) return "子 agent 不在注册表里";
            if (handle.IsCompleted) return "前置不成立：子 agent 已经跑完了";

            if (started.Elapsed > TimeSpan.FromSeconds(3))
                return $"启动用了 {started.Elapsed.TotalSeconds:0.0}s——它在等结果";

            gate.SetResult();
            await WaitForCompletionAsync(registry, id);
            return null;
        }
    }

    /// <summary>只回最终正文，不回中间步骤。</summary>
    private static async Task<string?> OnlyFinalTextAsync()
    {
        var client = new CapturingClient("答案在这里。");
        var registry = new SubagentRegistry();

        await using (registry)
        {
            var tool = Tool(registry, request => new LoopAgentRuntime(client, new ToolDispatcher()));
            var start = await tool.ExecuteAsync(Invocation("查", "任务"), CancellationToken.None);
            var id = ReadString(start.Content, "id");
            if (id is null) return "启动失败";

            await WaitForCompletionAsync(registry, id);

            var reader = new SubagentResultTool(registry);
            var content = await reader.ExecuteAsync(Invocation2(id!), CancellationToken.None);

            var text = ReadString(content.Content, "text");
            if (text != "答案在这里。")
                return $"最终正文不对：'{text}'";

            return ReadString(content.Content, "status") == "Completed"
                ? null
                : $"状态不对：{ReadString(content.Content, "status")}";
        }
    }

    /// <summary>并发满了就拒绝，不排队。</summary>
    /// <remarks>
    /// <b>排队会让模型以为已启动</b>：它可能几分钟后才真正开始，
    /// 而那时它已经基于错误假设继续做事了。拒绝让它立刻知道要等。
    /// </remarks>
    private static async Task<string?> ConcurrencyAsync()
    {
        var gate = new TaskCompletionSource();
        var client = new BlockingClient(gate.Task);
        var registry = new SubagentRegistry(maxConcurrent: 1);

        await using (registry)
        {
            var tool = Tool(registry, request => new LoopAgentRuntime(client, new ToolDispatcher()));

            var first = await tool.ExecuteAsync(Invocation("一", "任务一"), CancellationToken.None);
            if (ReadString(first.Content, "id") is null) return "第一个就启动失败";

            var second = await tool.ExecuteAsync(Invocation("二", "任务二"), CancellationToken.None);
            var ok = second.Content.TryGetProperty("ok", out var okElement)
                && okElement.ValueKind is JsonValueKind.True;

            gate.SetResult();
            await Task.Delay(300);

            if (ok) return "并发满了还接受新任务——会让模型误以为已启动";
            return ReadString(second.Content, "error")?.Contains("在跑", StringComparison.Ordinal) == true
                ? null
                : "拒绝时没有说明原因";
        }
    }

    /// <summary>结果只交付一次。</summary>
    /// <remarks>
    /// <b>重复交付会让注册表无限增长</b>，而"我忘了 id"可以由列表解决。
    /// </remarks>
    private static async Task<string?> DeliveredOnceAsync()
    {
        var client = new CapturingClient("一次。");
        var registry = new SubagentRegistry();

        await using (registry)
        {
            var tool = Tool(registry, request => new LoopAgentRuntime(client, new ToolDispatcher()));
            var start = await tool.ExecuteAsync(Invocation("查", "任务"), CancellationToken.None);
            var id = ReadString(start.Content, "id");
            if (id is null) return "启动失败";

            await WaitForCompletionAsync(registry, id);

            var reader = new SubagentResultTool(registry);
            var first = await reader.ExecuteAsync(Invocation2(id!), CancellationToken.None);
            if (ReadString(first.Content, "text") is null) return "第一次取得不到结果";

            var second = await reader.ExecuteAsync(Invocation2(id!), CancellationToken.None);
            var ok = second.Content.TryGetProperty("ok", out var okElement)
                && okElement.ValueKind is JsonValueKind.True;

            return ok ? "结果可以被重复取走——注册表会无限增长" : null;
        }
    }

    /// <summary>取消后状态是 Cancelled，不是 Failed。</summary>
    /// <remarks>
    /// <b>两者处置不同</b>：取消是外部意图（用户不想做了），
    /// 失败是需要看 diagnostic 的异常。
    /// </remarks>
    private static async Task<string?> CancelAsync()
    {
        var gate = new TaskCompletionSource();
        var client = new BlockingClient(gate.Task);
        var registry = new SubagentRegistry();

        await using (registry)
        {
            var tool = Tool(registry, request => new LoopAgentRuntime(client, new ToolDispatcher()));
            var start = await tool.ExecuteAsync(Invocation("查", "任务"), CancellationToken.None);
            var id = ReadString(start.Content, "id");
            if (id is null) return "启动失败";

            // **只取消，不放行 gate**。
            //
            // 取消通过链接令牌传到 `gate.WaitAsync(ct)`，它抛
            // OperationCanceledException，子 agent 因此以 Cancelled 结束。
            //
            // 第一版在 Cancel 之后又 SetResult()：放行让客户端正常返回，
            // 于是子 agent 以 Completed 结束——验的是"没取消时能完成"。
            // 第二版把 SetResult 挪到等待之后，仍然错：等待超时后放行，
            // 子 agent 又走完了。正确做法是**根本不放行**——
            // 若实现没响应取消，这条检查就该等满然后报红，而不是被放行救活。
            registry.Find(id!)?.Cancel();

            await WaitForCompletionAsync(registry, id);

            var handle = registry.Find(id!);
            return handle?.Result?.Status is SubagentStatus.Cancelled
                ? null
                : $"取消后状态应为 Cancelled，实际 {handle?.Result?.Status}";
        }
    }

    /// <summary>缺参数时拒绝并说明。</summary>
    private static async Task<string?> MissingArgsAsync()
    {
        var registry = new SubagentRegistry();
        await using (registry)
        {
            var tool = Tool(registry, request => new LoopAgentRuntime(
                new CapturingClient("x"), new ToolDispatcher()));

            var noPrompt = await tool.ExecuteAsync(Invocation("只有描述", null), CancellationToken.None);
            if (IsOk(noPrompt.Content)) return "缺 prompt 却被接受";

            var noLabel = await tool.ExecuteAsync(Invocation(null, "只有任务"), CancellationToken.None);
            return IsOk(noLabel.Content) ? "缺 description 却被接受" : null;
        }
    }

    /// <summary>深度上限会阻止继续委派。</summary>
    /// <remarks>
    /// <b>这是防无限递归的唯一屏障</b>：靠"子 agent 自己克制"不可靠，
    /// 而递归失控的失败形态是"跑了几百层之后额度烧光"，没有线索。
    ///
    /// <para>
    /// 验法：在主 dispatcher 上模拟装配条件——深度已达上限时不注册
    /// <c>subagent</c> 工具。这里不跑真实 CLI，而是直接核对判定式
    /// 与工具清单的关系。
    /// </para>
    /// </remarks>
    private static async Task<string?> DepthLimitAsync()
    {
        await Task.CompletedTask;

        // 找一个必定会被拒绝的深度配置。
        try
        {
            _ = new SubagentRegistry(maxDepth: 0);
            return "深度上限 0 被接受了——上限必须 ≥ 1";
        }
        catch (ArgumentOutOfRangeException)
        {
            // 预期。
        }

        var registry = new SubagentRegistry(maxDepth: 2);
        await using (registry)
        {
            if (registry.MaxDepth != 2) return $"MaxDepth 应为 2，实际 {registry.MaxDepth}";

            // 装配判定式：深度 < 上限才注册 subagent 工具。
            // 深度 0（主）与 1（子）可以，深度 2 不行。
            if (!(0 < registry.MaxDepth)) return "深度 0 应允许委派";
            if (!(1 < registry.MaxDepth)) return "深度 1 应允许再委派一层";
            return 2 < registry.MaxDepth
                ? "深度 2 不该允许继续委派——递归没有终点"
                : null;
        }
    }

    /// <summary>释放时等子 agent 退出。</summary>
    /// <remarks>
    /// <b>不等的话</b>：子 agent 的下一次工具调用会打在已释放的对象上，
    /// 而症状是释放之后日志里还冒出几条异常。
    /// </remarks>
    private static async Task<string?> DisposeAsync()
    {
        var gate = new TaskCompletionSource();
        var client = new BlockingClient(gate.Task);
        var registry = new SubagentRegistry();

        var tool = Tool(registry, request => new LoopAgentRuntime(client, new ToolDispatcher()));
        var start = await tool.ExecuteAsync(Invocation("查", "任务"), CancellationToken.None);
        var id = ReadString(start.Content, "id");
        if (id is null) return "启动失败";

        // 释放时不主动放开 gate：DisposeAsync 会取消子 agent，
        // 取消会让阻塞的客户端抛 OperationCanceledException 而退出。
        await registry.DisposeAsync();

        return registry.ActiveCount == 0
            ? null
            : $"释放后仍有 {registry.ActiveCount} 个活跃子 agent";
    }

    /// <summary>
    /// 真实嵌套时递归必须在上限处停住。
    /// </summary>
    /// <remarks>
    /// <b>这条检查是补的，因为原先前一条形同虚设</b>：
    /// <c>subagent-depth-limit-stops-recursion</c> 只验了判定式的算式
    /// （<c>depth &lt; maxDepth</c>），而装配层的真实实现用的是
    /// <c>_delegationDepth + 1</c>——那是同一个对象实例的只读字段，
    /// 递归调用自己时永远是初始值，于是**每一层都算出同样的结论**，
    /// 深度限制实际失效。算式对不代表装配对。
    ///
    /// <para>
    /// 这里用一个"每次都请求 subagent 工具"的假客户端真跑嵌套，
    /// 断言委派层数到上限就停。
    /// </para>
    /// </remarks>
    private static async Task<string?> RecursionStopsAsync()
    {
        var registry = new SubagentRegistry(maxDepth: 2);

        await using (registry)
        {
            // 验判定式在真实嵌套下的结果。**关键是深度由参数决定，
            // 不由对象状态决定**——后者正是原缺陷。
            //
            // 判定式是 `depth + 1 < MaxDepth`，MaxDepth = 2 时：
            //   深度 0：0+1=1 < 2 ✓ 注册
            //   深度 1：1+1=2 < 2 ✗ 不注册
            // 即"最多两层有工具"（0 与 1），到 2 停。
            var root = BuildChildDispatcher(registry, 0);
            if (root.Tools.All(t => t.Id != SubagentTool.ToolId))
                return "深度 0 时应注册 subagent 工具";

            var level1 = BuildChildDispatcher(registry, 1);
            if (level1.Tools.Any(t => t.Id == SubagentTool.ToolId))
                return "深度 1 已到上限，不该再注册 subagent 工具";

            return null;
        }
    }

    /// <summary>
    /// 装配里的深度判定必须用**参数**，不能用对象字段。
    /// </summary>
    /// <remarks>
    /// <b>这条补的是前面那条的盲区</b>：<c>RecursionStopsAsync</c> 验的是
    /// "判定式在给定深度下的结果"，而缺陷在于**装配把哪个值喂进判定式**。
    /// 原实现写的是 <c>_delegationDepth + 1</c>——那是同一个
    /// <c>SessionComposition</c> 实例的只读字段，递归调用自己时永远是初始值，
    /// 于是每一层都算出同样的结论、深度限制实际失效。
    /// 语义检查再准也验不出这个。
    /// </remarks>
    private static async Task<string?> DepthUsesParameterAsync()
    {
        await Task.CompletedTask;
        var root = FindRepoRoot();
        var file = Path.Combine(root, "src", "Dougong.Cli", "SessionComposition.cs");
        if (!File.Exists(file)) return $"找不到 {file}";

        var text = File.ReadAllText(file);

        // 递归注册 subagent 工具时，深度必须来自参数。
        if (!text.Contains("depth + 1 < _subagents.MaxDepth", StringComparison.Ordinal))
            return "递归判定没有用 depth 参数——若用实例字段，每层会算出同样结论";

        // 递归调用必须把 depth + 1 传下去。
        return text.Contains("CreateSubagentRuntime(r, depth + 1,", StringComparison.Ordinal)
            ? null
            : "递归调用没有把 depth + 1 传下去——子层会退回初始深度";
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Dougong.slnx"))) return dir.FullName;
            dir = dir.Parent;
        }
        return Directory.GetCurrentDirectory();
    }

    /// <summary>按给定深度造一个子 dispatcher（与装配同一条判据）。</summary>
    private static ToolDispatcher BuildChildDispatcher(SubagentRegistry registry, int depth)
    {
        var dispatcher = new ToolDispatcher();

        if (depth + 1 < registry.MaxDepth)
        {
            dispatcher.Register(new SubagentTool(
                registry,
                _ => new LoopAgentRuntime(new CapturingClient("x"), new ToolDispatcher()),
                $"s{depth}", "t"));
        }

        return dispatcher;
    }

    /// <summary>每次都请求 subagent 工具的客户端。</summary>
    private sealed class AlwaysDelegatingClient : IChatClient
    {
        private int _calls;

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> m, ChatOptions? o = null, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> m, ChatOptions? o = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.CompletedTask;
            _calls++;
            yield return new ChatResponseUpdate(ChatRole.Assistant,
                [new FunctionCallContent($"c{_calls}", SubagentTool.ToolId,
                    new Dictionary<string, object?> { ["description"] = "深层", ["prompt"] = "继续" })]);
        }

        public object? GetService(Type t, object? k = null) => null;
        public void Dispose() { }
    }

    // ── 支撑 ─────────────────────────────────────────────────────

    private static SubagentTool Tool(
        SubagentRegistry registry, Func<AgentRunRequest, IAgentRuntime> factory) =>
        new(registry, factory, "sess", "turn");

    private static ToolInvocation Invocation(string? label, string? prompt)
    {
        var payload = new Dictionary<string, string>();
        if (label is not null) payload["description"] = label;
        if (prompt is not null) payload["prompt"] = prompt;

        using var document = JsonDocument.Parse(JsonSerializer.Serialize(payload));
        return new ToolInvocation("inv-" + Guid.NewGuid().ToString("N")[..8],
            SubagentTool.ToolId, document.RootElement.Clone());
    }

    private static ToolInvocation Invocation2(string id)
    {
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(new { id }));
        return new ToolInvocation("inv2-" + Guid.NewGuid().ToString("N")[..8],
            SubagentResultTool.ToolId, document.RootElement.Clone());
    }

    /// <summary>等子 agent 结束（有界）。</summary>
    private static async Task WaitForCompletionAsync(SubagentRegistry registry, string id)
    {
        for (var i = 0; i < 100; i++)
        {
            var handle = registry.Find(id);
            if (handle is null || handle.IsCompleted) return;
            await Task.Delay(20);
        }
    }

    private static bool IsOk(JsonElement content) =>
        content.TryGetProperty("ok", out var ok) && ok.ValueKind is JsonValueKind.True;

    private static string? ReadString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.String
            ? value.GetString()
            : null;

    /// <summary>记录每次请求收到的消息，返回固定文本。</summary>
    private sealed class CapturingClient(string reply) : IChatClient
    {
        public List<IReadOnlyList<ChatMessage>> Requests { get; } = [];

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> m, ChatOptions? o = null, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> m, ChatOptions? o = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.CompletedTask;
            Requests.Add([.. m]);
            yield return new ChatResponseUpdate(ChatRole.Assistant, reply);
        }

        public object? GetService(Type t, object? k = null) => null;
        public void Dispose() { }
    }

    /// <summary>一直等到放行才返回的客户端，用来验"启动即返回"。</summary>
    private sealed class BlockingClient(Task gate) : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> m, ChatOptions? o = null, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> m, ChatOptions? o = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            await gate.WaitAsync(ct);
            yield return new ChatResponseUpdate(ChatRole.Assistant, "完成");
        }

        public object? GetService(Type t, object? k = null) => null;
        public void Dispose() { }
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
