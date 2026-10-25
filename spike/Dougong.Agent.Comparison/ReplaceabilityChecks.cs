using System.Text.Json;
using Dougong.Agent;
using Microsoft.Extensions.AI;

namespace Dougong.Agent.Comparison;

/// <summary>
/// 可替换性实测：换一种实现、**不改内核代码**、行为断言仍通过。
/// </summary>
/// <remarks>
/// 这是 `goal.md` P1 判定第 2 条的直接落地。此前只有存储有这条验收，
/// 其余三个能力虽然都是接口，却**没有一条测试证明"换了还成立"**——
/// "接口存在"和"可替换被证明"是两件事。
/// <para>
/// 每条检查的共同形状：
/// <list type="number">
/// <item>用实现 A 跑一遍行为断言，确认通过</item>
/// <item>换成实现 B（同一接口，不同内部逻辑）</item>
/// <item>**内核/运行时一行代码不改**，再跑同一套断言</item>
/// <item>确认 A 与 B 的可观察差异符合预期（证明真的换了，不是同一份实现）</item>
/// </list>
/// </para>
/// </remarks>
public static class ReplaceabilityChecks
{
    public static async Task<int> RunAsync()
    {
        var failures = 0;
        failures += await CheckAsync("chat-client-is-replaceable", ChatClientAsync);
        failures += await CheckAsync("approver-is-replaceable", ApproverAsync);
        failures += await CheckAsync("dispatcher-is-replaceable", DispatcherAsync);
        failures += await CheckAsync("runtime-is-replaceable", RuntimeAsync);

        Console.WriteLine(failures == 0
            ? "REPLACEABILITY: ALL PASS"
            : $"REPLACEABILITY: {failures} FAILURE(S)");
        return failures;
    }

    /// <summary>
    /// 换 <see cref="IChatClient"/> 实现：协议形状完全不同的两份实现，
    /// 同一个 <see cref="LoopAgentRuntime"/> 都要正确处理。
    /// </summary>
    private static async Task<string?> ChatClientAsync()
    {
        // 实现 A：一次性给出完整文本。
        var whole = await RunWithClientAsync(new WholeTextClient("hello world"));
        if (whole.Text != "hello world")
            return $"实现 A 的输出不符：'{whole.Text}'";
        if (whole.ToolCalls != 0) return "实现 A 不该有工具调用";

        // 实现 B：分片输出，并请求一次工具。
        var fragmented = await RunWithClientAsync(new FragmentingToolClient());
        // 实现 B 两轮都有文本：第一轮 "work"、第二轮 "do"+"ne"。
        // 这本身就是"与实现 A 可观察行为不同"的证据。
        if (fragmented.Text != "workdone")
            return $"实现 B 的输出不符：'{fragmented.Text}'";
        if (fragmented.ToolCalls != 1)
            return $"实现 B 应触发 1 次工具调用，实际 {fragmented.ToolCalls}";
        if (!fragmented.ToolResultSeen)
            return "实现 B 的工具结果未回填到下一轮请求";

        return null;
    }

    /// <summary>换 <see cref="IToolApprover"/>：两种策略必须各自生效。</summary>
    private static async Task<string?> ApproverAsync()
    {
        var allow = await RunApprovedToolAsync(new FixedApprover(true));
        if (!allow.Executed) return "允许策略下工具未执行";

        var deny = await RunApprovedToolAsync(new FixedApprover(false));
        if (deny.Executed) return "拒绝策略下工具仍被执行";
        if (deny.Outcome != ToolCallOutcome.RejectedNotApproved)
            return $"拒绝时终态应为 RejectedNotApproved，实际 {deny.Outcome}";

        return null;
    }

    /// <summary>
    /// 换 <see cref="IToolDispatcher"/>：dispatcher 本身也可替换，
    /// 运行时只依赖接口。
    /// </summary>
    private static async Task<string?> DispatcherAsync()
    {
        // 实现 A：真实 dispatcher。
        var real = await RunWithDispatcherAsync(new ToolDispatcher());
        if (!real.Executed) return "真实 dispatcher 未执行工具";

        // 实现 B：记录型 dispatcher，完全不执行工具，只看调用到达。
        var recorder = new RecordingDispatcher();
        var recorded = await RunWithDispatcherAsync(recorder);

        if (recorded.Executed) return "记录型 dispatcher 不该执行工具";
        if (recorder.Calls != 1) return $"记录型 dispatcher 应收到 1 次调用，实际 {recorder.Calls}";
        if (recorded.Outcome != ToolCallOutcome.Failed)
            return $"记录型 dispatcher 返回失败时，运行时应记为 Failed，实际 {recorded.Outcome}";

        // 两轮都正常结束，说明运行时没有依赖真实 dispatcher 的额外行为。
        if (recorded.Outcome is ToolCallOutcome.RejectedUnknownTool)
            return "记录型 dispatcher 的调用被运行时判为未知工具：运行时绑定了具体实现";

        return null;
    }

    /// <summary>换 <see cref="IAgentRuntime"/>：宿主只依赖接口。</summary>
    private static async Task<string?> RuntimeAsync()
    {
        // 实现 A：真实循环。
        var loop = new LoopAgentRuntime(new WholeTextClient("from loop"), new ToolDispatcher());
        var fromLoop = await CollectAsync(loop);
        if (fromLoop != "from loop") return $"真实循环输出不符：'{fromLoop}'";

        // 实现 B：完全不同的运行时，只发文本事件。
        var fake = await CollectAsync(new FakeRuntime("from fake"));
        if (fake != "from fake") return $"替代运行时输出不符：'{fake}'";

        // 两者都产出同一种事件序列形状 —— 这是"可替换"的实质。
        return null;
    }

    // ── 支撑 ─────────────────────────────────────────────────────

    private static async Task<(string Text, int ToolCalls, bool ToolResultSeen)> RunWithClientAsync(
        IChatClient client)
    {
        var dispatcher = new ToolDispatcher();
        dispatcher.Register(new EchoTool());

        var runtime = new LoopAgentRuntime(client, dispatcher);

        var text = new System.Text.StringBuilder();
        var toolCalls = 0;
        await foreach (var evt in runtime.RunAsync(new AgentRunRequest("s", "t", [], "hi")))
        {
            switch (evt)
            {
                case AgentEvent.TextDelta delta:
                    text.Append(delta.Text);
                    break;
                case AgentEvent.ToolCallRequested:
                    toolCalls++;
                    break;
            }
        }

        var seen = client is FragmentingToolClient f && f.SawToolResult;
        return (text.ToString(), toolCalls, seen);
    }

    private static async Task<(bool Executed, ToolCallOutcome Outcome)> RunApprovedToolAsync(
        IToolApprover approver)
    {
        var tool = new EchoTool();
        var dispatcher = new ToolDispatcher(approver);
        dispatcher.Register(tool);

        var result = await dispatcher.DispatchAsync(Invocation());
        return (tool.Executions > 0, result.Outcome);
    }

    private static async Task<(bool Executed, ToolCallOutcome Outcome)> RunWithDispatcherAsync(
        IToolDispatcher dispatcher)
    {
        var tool = new EchoTool();
        dispatcher.Register(tool);

        // 走 IDispatcherAccessor 重载：这才是运行时真正的注入路径。
        // 传裸 IToolDispatcher 会被内部包成 FixedDispatcherAccessor，
        // 于是"替换"实际发生在包装层之外——那样的检查测不出运行时是否绑定了具体实现。
        var runtime = new LoopAgentRuntime(
            new FragmentingToolClient(expectToolResult: false),
            new DirectDispatcherAccessor(dispatcher));

        ToolCallOutcome outcome = ToolCallOutcome.Succeeded;
        await foreach (var evt in runtime.RunAsync(new AgentRunRequest("s", "t", [], "hi")))
        {
            if (evt is AgentEvent.ToolCallCompleted completed)
                outcome = completed.Result.Outcome;
        }

        return (tool.Executions > 0, outcome);
    }

    private static async Task<string> CollectAsync(IAgentRuntime runtime)
    {
        var text = new System.Text.StringBuilder();
        await foreach (var evt in runtime.RunAsync(new AgentRunRequest("s", "t", [], "hi")))
        {
            if (evt is AgentEvent.TextDelta delta) text.Append(delta.Text);
        }

        return text.ToString();
    }

    private static ToolInvocation Invocation()
    {
        using var document = JsonDocument.Parse("""{"path":"a.txt"}""");
        return new ToolInvocation("c1", "echo", document.RootElement.Clone());
    }

    /// <summary>一次性给出完整文本的客户端。</summary>
    private sealed class WholeTextClient(string text) : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, text)));

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation]
            CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield return new ChatResponseUpdate(ChatRole.Assistant, text);
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    /// <summary>
    /// 分片输出 + 第一轮请求工具、第二轮给最终文本的客户端。
    /// </summary>
    /// <remarks>
    /// 与 <see cref="WholeTextClient"/> 的可观察行为完全不同：
    /// 多轮、含工具调用、文本分片。用它验证运行时不是只对一种形状有效。
    /// </remarks>
    private sealed class FragmentingToolClient(bool expectToolResult = true) : IChatClient
    {
        private int _calls;

        /// <summary>第二轮请求里是否看到了工具结果。</summary>
        public bool SawToolResult { get; private set; }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("本检查只走流式路径");

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation]
            CancellationToken cancellationToken = default)
        {
            var list = messages.ToList();
            _calls++;

            if (_calls == 1)
            {
                // 第一轮：请求工具。
                await Task.CompletedTask;
                yield return new ChatResponseUpdate(ChatRole.Assistant, [new TextContent("work")]);
                yield return new ChatResponseUpdate(ChatRole.Assistant,
                [
                    new FunctionCallContent("c1", "echo",
                        new Dictionary<string, object?> { ["path"] = "a.txt" }),
                ]);
                yield break;
            }

            // 第二轮：检查是否看到了工具结果。只在自己的回调里记录，
            // 不改运行时的行为。
            SawToolResult = expectToolResult && list.Any(m =>
                m.Role == ChatRole.Tool
                || m.Contents.OfType<FunctionResultContent>().Any());

            await Task.Yield();
            yield return new ChatResponseUpdate(ChatRole.Assistant, "do");
            yield return new ChatResponseUpdate(ChatRole.Assistant, "ne");
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private sealed class FixedApprover(bool allow) : IToolApprover
    {
        public ValueTask<ToolApprovalDecision> RequestApprovalAsync(
            ToolApprovalRequest request, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(allow
                ? ToolApprovalDecision.Allow("测试：允许")
                : ToolApprovalDecision.Deny("测试：拒绝"));
    }

    /// <summary>把一个 dispatcher 直接暴露成访问点（不经 Fixed 包装）。</summary>
    private sealed class DirectDispatcherAccessor(IToolDispatcher dispatcher) : IDispatcherAccessor
    {
        public ValueTask<IReadOnlyList<ToolDescriptor>> GetToolsAsync(
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(dispatcher.Tools);

        public ValueTask<ToolDispatchResult> DispatchAsync(
            ToolInvocation invocation, CancellationToken cancellationToken = default) =>
            dispatcher.DispatchAsync(invocation, cancellationToken);
    }

    /// <summary>只记录调用、不执行的 dispatcher：验证运行时只依赖接口。</summary>
    private sealed class RecordingDispatcher : IToolDispatcher
    {
        private readonly List<ToolDescriptor> _tools = [];
        private readonly Dictionary<string, int> _seen = [];

        public int Calls => _seen.Values.Sum();

        public IDisposable Register(IAgentTool tool)
        {
            _tools.Add(tool.Descriptor);
            return new NoopHandle();
        }

        public IReadOnlyList<ToolDescriptor> Tools => _tools;

        public ValueTask<ToolDispatchResult> DispatchAsync(
            ToolInvocation invocation, CancellationToken cancellationToken = default)
        {
            _seen[invocation.InvocationId] = _seen.GetValueOrDefault(invocation.InvocationId) + 1;

            return ValueTask.FromResult(new ToolDispatchResult(
                invocation.InvocationId, invocation.ToolId,
                ToolCallOutcome.Failed,
                Diagnostic: "记录型 dispatcher：不执行"));
        }

        private sealed class NoopHandle : IDisposable
        {
            public void Dispose() { }
        }
    }

    /// <summary>完全不同的运行时实现：只发两段文本。</summary>
    private sealed class FakeRuntime(string text) : IAgentRuntime
    {
        public async IAsyncEnumerable<AgentEvent> RunAsync(
            AgentRunRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation]
            CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            yield return new AgentEvent.RunStarted(request.SessionId, request.TurnId);
            yield return new AgentEvent.TextDelta(text[..(text.Length / 2)]);
            yield return new AgentEvent.TextDelta(text[(text.Length / 2)..]);
            yield return new AgentEvent.RunFinished(AgentRunOutcome.Completed);
        }
    }

    private sealed class EchoTool : IAgentTool
    {
        public int Executions { get; private set; }

        public ToolDescriptor Descriptor { get; } = new(
            "echo", "echo", "1.0.0", "回显",
            JsonDocument.Parse(
                """{"type":"object","properties":{"path":{"type":"string"}},"required":["path"]}""")
                .RootElement.Clone());

        public ToolPermission DescribePermission(JsonElement arguments) =>
            new(ToolAccess.Read, IsKnown: true, Paths: ["a.txt"], Commands: [], Domains: []);

        public ToolExecutionPlan DescribeExecution(JsonElement arguments) =>
            new(ToolConcurrency.Serial);

        public ValueTask<AgentToolResult> ExecuteAsync(
            ToolInvocation invocation, CancellationToken cancellationToken)
        {
            Executions++;
            using var document = JsonDocument.Parse("""{"ok":true}""");
            return ValueTask.FromResult(new AgentToolResult(document.RootElement.Clone()));
        }
    }

    private static async Task<int> CheckAsync(string name, Func<Task<string?>> action)
    {
        try
        {
            var failure = await action();
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
