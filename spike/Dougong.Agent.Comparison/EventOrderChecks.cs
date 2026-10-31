using System.Text.Json;
using Dougong.Agent;
using Microsoft.Extensions.AI;

namespace Dougong.Agent.Comparison;

/// <summary>
/// 可观察事件顺序的验收。
/// </summary>
/// <remarks>
/// <b>移走"编译通过"这个判据</b>：事件类型全在、每个都能序列化，
/// 编译当然过。但宿主真正依赖的是**顺序**——桌面端按
/// <c>RunStarted → TextDelta… → ToolCallRequested → ToolCallCompleted → RunCompleted</c>
/// 推进状态机，顺序错一个位置，UI 就会在错误的时刻切到下一步。
///
/// <para>
/// 这套检查断言的是"同一轮里事件的相对顺序"，不是"事件总数"。
/// 总数会随 provider 实现变化（推理、用量、缓存命中），
/// 而顺序约束是宿主可依赖的契约。
/// </para>
/// </remarks>
public static class EventOrderChecks
{
    public static async Task<int> RunAsync()
    {
        var failures = 0;
        failures += await CheckAsync("runs-starts-first-and-completes-last", FirstAndLastAsync);
        failures += await CheckAsync("every-requested-has-exactly-one-completed", PairingAsync);
        failures += await CheckAsync("text-precedes-its-tool-call", TextBeforeCallAsync);
        failures += await CheckAsync("context-usage-precedes-request", ContextBeforeRequestAsync);
        failures += await CheckAsync("usage-reported-after-stream-not-before", UsageAfterStreamAsync);
        failures += await CheckAsync("failure-still-ends-with-terminal-event", FailureTerminalAsync);

        Console.WriteLine(failures == 0
            ? "EVENT-ORDER: ALL PASS"
            : $"EVENT-ORDER: {failures} FAILURE(S)");
        return failures;
    }

    /// <summary>RunStarted 必须是第一个事件，RunFinished 必须是最后一个。</summary>
    /// <remarks>
    /// 宿主用第一个事件初始化状态机、用最后一个事件收尾。
    /// 若中间插入了别的事件在最前，宿主会在状态机未初始化时收到更新。
    /// </remarks>
    private static async Task<string?> FirstAndLastAsync()
    {
        var client = new ScriptedClient("hello", []);
        var events = await CollectAsync(client, "hi");

        if (events.Count == 0) return "没有产生任何事件";
        if (events[0] is not AgentEvent.RunStarted)
            return $"第一个事件是 {events[0].GetType().Name}，不是 RunStarted";
        if (events[^1] is not AgentEvent.RunFinished)
            return $"最后一个事件是 {events[^1].GetType().Name}，不是 RunFinished";

        return null;
    }

    /// <summary>每个 ToolCallRequested 恰好对应一个 ToolCallCompleted，且后者在后。</summary>
    /// <remarks>
    /// <b>这是宿主最容易踩的不变量</b>：桌面端为每个 Requested 建一个"执行中"
    /// 卡片，收到 Completed 才更新结果。少一个 Completed 就是一张永远转圈的卡片；
    /// 多一个 Completed 就是更新到不存在的卡片。
    /// </remarks>
    private static async Task<string?> PairingAsync()
    {
        var tool = StubTool.Always("echo", """{"ok":true}""");
        var client = new ScriptedClient("done", [("call-1", "echo", "{}")]);
        var events = await CollectAsync(client, "hi", tool);

        var requested = events.OfType<AgentEvent.ToolCallRequested>().ToList();
        var completed = events.OfType<AgentEvent.ToolCallCompleted>().ToList();

        if (requested.Count != 1) return $"应有 1 次 ToolCallRequested，实际 {requested.Count}";
        if (completed.Count != 1) return $"应有 1 次 ToolCallCompleted，实际 {completed.Count}";

        var requestedIndex = events.IndexOf(requested[0]);
        var completedIndex = events.IndexOf(completed[0]);
        if (completedIndex < requestedIndex)
            return "ToolCallCompleted 出现在 ToolCallRequested 之前";

        if (!string.Equals(completed[0].Result.ToolId, requested[0].ToolId, StringComparison.Ordinal))
            return $"Completed 的 ToolId 与 Requested 不一致：{completed[0].Result.ToolId} vs {requested[0].ToolId}";

        return null;
    }

    /// <summary>工具的文本输出先于它的调用请求。</summary>
    /// <remarks>
    /// 模型通常先说话再调工具（"我来查一下"）。若顺序反了，
    /// 宿主会先看到工具卡片、后看到解释文字，界面显得跳脱。
    /// 这不是硬契约，但**两家实现对不上时用户一眼能看出来**，
    /// 所以要钉住——它定义了"我们承诺的呈现顺序"。
    /// </remarks>
    private static async Task<string?> TextBeforeCallAsync()
    {
        var tool = StubTool.Always("echo", """{"ok":true}""");
        var client = new ScriptedClient("我先查一下", [("call-1", "echo", "{}")]);
        var events = await CollectAsync(client, "hi", tool);

        // 比第一个 TextDelta 与第一个 ToolCallRequested。
        // **不能用 FindLastIndex**：第二轮回复的 TextDelta 必然在工具调用之后
        // （那时工具已经执行完了），拿它比会把正常序列判成错误。
        var firstText = events.FindIndex(e => e is AgentEvent.TextDelta);
        var firstCall = events.FindIndex(e => e is AgentEvent.ToolCallRequested);

        if (firstText < 0) return "没有产生任何 TextDelta";
        if (firstCall < 0) return "没有产生 ToolCallRequested";
        if (firstText > firstCall)
            return "首条 TextDelta 出现在 ToolCallRequested 之后（应先说话再调工具）";

        return null;
    }

    /// <summary>上下文构成报告在工具调用请求之前。</summary>
    /// <remarks>
    /// 它描述的是"这次请求发出去时装了多少"。出现在工具调用之后，
    /// 宿主会把它挂到错误的轮次上，显示的占用对不上当前请求。
    /// </remarks>
    private static async Task<string?> ContextBeforeRequestAsync()
    {
        var tool = StubTool.Always("echo", """{"ok":true}""");
        var client = new ScriptedClient("ok", [("call-1", "echo", "{}")]);
        var events = await CollectAsync(client, "hi", tool);

        var context = events.FindIndex(e => e is AgentEvent.ContextUsageReported);
        var call = events.FindIndex(e => e is AgentEvent.ToolCallRequested);

        if (context < 0) return "没有产生 ContextUsageReported";
        if (call < 0) return "没有产生 ToolCallRequested";
        if (context > call)
            return "ContextUsageReported 出现在 ToolCallRequested 之后";

        return null;
    }

    /// <summary>
    /// 用量报告在流结束之后、轮次结束之前。
    /// </summary>
    /// <remarks>
    /// provider 的 usage 附着在流的末尾。在流开始前发就是 0，
    /// 那会污染成本统计——而成本正是用户最会拿来对账的数字。
    /// </remarks>
    private static async Task<string?> UsageAfterStreamAsync()
    {
        var client = new ScriptedClient("ok", [], usageIn: 11, usageOut: 7);
        var events = await CollectAsync(client, "hi");

        var usageIndex = events.FindIndex(e => e is AgentEvent.UsageReported);
        if (usageIndex < 0) return "provider 报了 usage 却没有 UsageReported 事件";

        var lastText = events.FindLastIndex(e => e is AgentEvent.TextDelta);
        if (lastText >= 0 && usageIndex < lastText)
            return "UsageReported 出现在 TextDelta 之前（流的用量在末尾才到）";

        var reported = (AgentEvent.UsageReported)events[usageIndex];
        if (reported.InputTokens != 11 || reported.OutputTokens != 7)
            return $"用量数值被改动：{reported.InputTokens}/{reported.OutputTokens}，应为 11/7";

        return null;
    }

    /// <summary>
    /// 失败路径也必须以终态事件结束，不能半途没有收尾。
    /// </summary>
    /// <remarks>
    /// <b>这是"不挂住"的底线</b>：宿主收到 RunStarted 后进入"运行中"，
    /// 只有终态事件能让它退出。若流中途抛错而没有终态事件，
    /// UI 会永远显示"运行中"，用户只能重启应用。
    /// </remarks>
    private static async Task<string?> FailureTerminalAsync()
    {
        var client = new ScriptedClient(failWith: new InvalidOperationException("模拟 provider 断开"));
        var events = await CollectAsync(client, "hi");

        if (events.Count == 0) return "失败时没有任何事件";
        if (events[^1] is not AgentEvent.RunFinished)
            return $"失败后最后一个事件是 {events[^1].GetType().Name}，不是 RunFinished";

        var final = (AgentEvent.RunFinished)events[^1];
        if (final.Outcome == AgentRunOutcome.Completed)
            return "provider 抛错了但终态是 Completed";

        return null;
    }

    // ── 支撑 ─────────────────────────────────────────────────────

    private static async Task<List<AgentEvent>> CollectAsync(
        ScriptedClient client, string input, params IAgentTool[] tools)
    {
        var dispatcher = new RecordingDispatcher();
        foreach (var tool in tools) dispatcher.Register(tool);

        var runtime = new LoopAgentRuntime(client, (IToolDispatcher)dispatcher);

        var events = new List<AgentEvent>();
        await foreach (var e in runtime.RunAsync(
            new AgentRunRequest("s1", "t1", [], input, MaxToolRounds: 3),
            CancellationToken.None))
        {
            events.Add(e);
        }

        return events;
    }

    /// <summary>按脚本产出一条回复；可带工具调用，可模拟失败。</summary>
    private sealed class ScriptedClient : IChatClient
    {
        private readonly string _text;
        private readonly (string Id, string Name, string Args)[] _calls;
        private readonly Exception? _failure;
        private readonly int? _usageIn;
        private readonly int? _usageOut;
        private int _round;

        public ScriptedClient(
            string text = "", (string, string, string)[]? calls = null,
            Exception? failWith = null, int? usageIn = null, int? usageOut = null)
        {
            _text = text;
            _calls = calls ?? [];
            _failure = failWith;
            _usageIn = usageIn;
            _usageOut = usageOut;
        }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            GetStreamingResponseAsync(messages, options, cancellationToken).ToChatResponseAsync();

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation]
            CancellationToken cancellationToken = default)
        {
            var round = _round++;

            if (_failure is not null)
            {
                await Task.Yield();
                throw _failure;
            }

            if (_text.Length > 0)
                yield return new ChatResponseUpdate(ChatRole.Assistant, _text);

            // 工具只在第一轮给出；第二轮返回纯文本，让循环正常结束。
            if (round == 0 && _calls.Length > 0)
            {
                foreach (var (id, name, args) in _calls)
                {
                    yield return new ChatResponseUpdate(ChatRole.Assistant, [new FunctionCallContent(id, name,
                        JsonSerializer.Deserialize<Dictionary<string, object?>>(args))]);
                }
            }

            if (_usageIn is { } input && _usageOut is { } output)
            {
                yield return new ChatResponseUpdate(
                    ChatRole.Assistant, [new UsageContent(new UsageDetails
                    {
                        InputTokenCount = input,
                        OutputTokenCount = output,
                    })]);
            }
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }

    /// <summary>永远成功的桩工具。</summary>
    private sealed class StubTool(string id, string content) : IAgentTool
    {
        public static StubTool Always(string id, string content) => new(id, content);

        public ToolDescriptor Descriptor { get; } = new(
            id, id, "1.0.0", $"桩工具 {id}",
            JsonDocument.Parse("""{"type":"object","properties":{}}""").RootElement.Clone());

        public ToolPermission DescribePermission(JsonElement arguments) =>
            new(ToolAccess.Read, false, [], [], []);

        public ToolExecutionPlan DescribeExecution(JsonElement arguments) =>
            new(ToolConcurrency.Serial);

        public IReadOnlyList<string> DescribeFileDependencies(JsonElement arguments) => [];

        public ValueTask<AgentToolResult> ExecuteAsync(
            ToolInvocation invocation, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new AgentToolResult(
                JsonDocument.Parse(content).RootElement.Clone()));
    }

    /// <summary>真注册表：走真实注册与查找路径，不自己另写映射。</summary>
    private sealed class RecordingDispatcher : IToolDispatcher
    {
        private readonly List<IAgentTool> _tools = [];

        public IReadOnlyList<ToolDescriptor> Tools => [.. _tools.Select(t => t.Descriptor)];

        public IDisposable Register(IAgentTool tool)
        {
            _tools.Add(tool);
            return new Unregister(_tools, tool);
        }

        public ValueTask<ToolDispatchResult> DispatchAsync(
            ToolInvocation invocation, CancellationToken cancellationToken = default)
        {
            var tool = _tools.FirstOrDefault(
                t => string.Equals(t.Descriptor.Id, invocation.ToolId, StringComparison.Ordinal));
            if (tool is null)
            {
                return ValueTask.FromResult(new ToolDispatchResult(
                    invocation.InvocationId, invocation.ToolId,
                    ToolCallOutcome.RejectedUnknownTool, Diagnostic: "未注册"));
            }

            return DispatchCore(tool, invocation, cancellationToken);
        }

        private static async ValueTask<ToolDispatchResult> DispatchCore(
            IAgentTool tool, ToolInvocation invocation, CancellationToken cancellationToken)
        {
            var result = await tool.ExecuteAsync(invocation, cancellationToken).ConfigureAwait(false);
            return new ToolDispatchResult(
                invocation.InvocationId, invocation.ToolId,
                ToolCallOutcome.Succeeded, result);
        }

        private sealed class Unregister(List<IAgentTool> tools, IAgentTool tool) : IDisposable
        {
            public void Dispose() => tools.Remove(tool);
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
