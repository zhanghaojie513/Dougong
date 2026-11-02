using System.Text.Json;
using Dougong.Agent;
using Microsoft.Extensions.AI;

namespace Dougong.Agent.Comparison;

/// <summary>
/// 计划模式的验收（照 dsh 的 <c>exit_plan_mode</c> 模型）。
/// </summary>
/// <remarks>
/// <b>与第一版的关键差别</b>：提交计划**不结束本轮**，而是工具内部阻塞等审阅。
/// 批准后模型在同一轮继续——判据是"模型被调用了第二次"，
/// 而不只是"结局是什么"。
/// <para>
/// <b>可观察面</b>：
/// <list type="bullet">
/// <item>批准 → 工具结果 <c>PlanApproved</c> 且计划模式关闭</item>
/// <item>要改 → <c>PlanRejected</c> 且模式保持</item>
/// <item>取消 → <c>PlanCancelled</c>，与"拒绝"分开</item>
/// <item>缺标题 / 太长 → 拒收，不打扰用户</item>
/// <item>计划模式下非只读工具被拦</item>
/// </list>
/// </para>
/// </remarks>
public static class PlanModeChecks
{
    private const string GoodPlan = "# 加 --greeting 参数\n\n## 步骤\n\n- 改 Program.cs\n- 加测试";

    public static async Task<int> RunAsync()
    {
        var failures = 0;
        failures += await CheckAsync("plan-approval-continues-same-turn", ApprovalContinuesAsync);
        failures += await CheckAsync("plan-approval-exits-plan-mode", ApprovalExitsModeAsync);
        failures += await CheckAsync("plan-rejection-keeps-plan-mode", RejectionKeepsModeAsync);
        failures += await CheckAsync("plan-cancel-is-distinct-from-reject", CancelDistinctAsync);
        failures += await CheckAsync("plan-requires-markdown-title", RequiresTitleAsync);
        failures += await CheckAsync("plan-rejects-oversized-document", RejectsOversizedAsync);
        failures += await CheckAsync("plan-mode-blocks-write-tools", BlocksWritesAsync);
        failures += await CheckAsync("plan-mode-allows-read-tools", AllowsReadsAsync);
        failures += await CheckAsync("plan-mode-off-executes-normally", OffExecutesAsync);
        failures += await CheckAsync("plan-section-only-when-active", SectionGatingAsync);
        failures += await CheckAsync("plan-intent-carries-approve-label", IntentCarriesLabelAsync);

        Console.WriteLine(failures == 0
            ? "PLAN-MODE: ALL PASS"
            : $"PLAN-MODE: {failures} FAILURE(S)");
        return failures;
    }

    /// <summary>批准后模型在同一轮继续。</summary>
    /// <remarks>
    /// <b>这是与"结束本轮"模型的本质区别</b>：第一种做法下用户批准后要重新起轮，
    /// 而 dsh 是工具返回 <c>Plan approved</c>，模型接着做。
    /// 判据必须是"模型被调了第二次"——只看结局的话两种实现都能过。
    /// </remarks>
    private static async Task<string?> ApprovalContinuesAsync()
    {
        var (outcome, calls, results) = await RunAsync("Approve");

        var result = results.FirstOrDefault(r => r.ToolId == PlanModePolicy.ExitPlanModeToolId);
        if (result?.Result?.Content.GetRawText().Contains("PlanApproved", StringComparison.Ordinal) != true)
            return $"工具结果不是 PlanApproved：{result?.Result?.Content.GetRawText()}";

        if (calls < 2)
            return $"批准后模型只被调了 {calls} 次——说明本轮被结束了，没有继续";

        return outcome is AgentRunOutcome.Completed
            ? null
            : $"结局应为 Completed，实际 {outcome}";
    }

    /// <summary>批准后计划模式必须关闭，否则模型接下来仍会被拦。</summary>
    private static async Task<string?> ApprovalExitsModeAsync()
    {
        var mode = new PlanMode();
        await RunAsync("Approve", mode);

        return mode.IsActive
            ? "批准后计划模式仍是激活状态——后续写操作会被误拦"
            : null;
    }

    /// <summary>要改时保持计划模式，让模型改完重新提交。</summary>
    private static async Task<string?> RejectionKeepsModeAsync()
    {
        var mode = new PlanMode();
        var (_, _, results) = await RunAsync("Keep planning", mode);

        var result = results.FirstOrDefault(r => r.ToolId == PlanModePolicy.ExitPlanModeToolId);
        if (result?.Result?.Content.GetRawText().Contains("PlanRejected", StringComparison.Ordinal) != true)
            return $"工具结果不是 PlanRejected：{result?.Result?.Content.GetRawText()}";

        return mode.IsActive ? null : "未批准却关闭了计划模式——模型会直接动手";
    }

    /// <summary>取消与拒绝必须是不同结果。</summary>
    /// <remarks>
    /// <b>混成一个会让模型误判</b>：取消意味着用户没看到或被打断，
    /// 拒绝意味着方案被否决。前者该重试，后者该改方案。
    /// </remarks>
    private static async Task<string?> CancelDistinctAsync()
    {
        var mode = new PlanMode();
        var (_, _, results) = await RunAsync(planMode: mode, cancelled: true);

        var raw = results.FirstOrDefault(r => r.ToolId == PlanModePolicy.ExitPlanModeToolId)
            ?.Result?.Content.GetRawText() ?? string.Empty;

        if (!raw.Contains("PlanCancelled", StringComparison.Ordinal))
            return $"取消应报 PlanCancelled，实际：{raw}";

        if (raw.Contains("PlanRejected", StringComparison.Ordinal))
            return "取消同时被报成了拒绝";

        return mode.IsActive ? null : "取消却关闭了计划模式";
    }

    /// <summary>没有 Markdown 标题的计划要被拒。</summary>
    /// <remarks>
    /// <b>标题是客户端的唯一标识</b>：审阅卡片用标题显示"这份计划关于什么"。
    /// 没有标题就只剩一句话，用户无法从卡片判断该不该点进去看。
    /// </remarks>
    private static async Task<string?> RequiresTitleAsync()
    {
        var (_, calls, results) = await RunAsync("Approve", plan: "这里没有标题，只有一段正文。");

        var raw = results.FirstOrDefault(r => r.ToolId == PlanModePolicy.ExitPlanModeToolId)
            ?.Result?.Content.GetRawText() ?? string.Empty;

        if (!raw.Contains("PlanRejected", StringComparison.Ordinal))
            return $"缺标题应被拒，实际：{raw}";

        // **不断言模型调用次数**：拒收后模型会拿到工具结果、可以改完重提，
        // 那正是设计意图。要验的是"没有发起审阅"——用 host 是否被调过来判。
        return _lastHostAsked
            ? "缺标题却仍发起了审阅（打扰了用户）"
            : null;
    }

    private static async Task<string?> RejectsOversizedAsync()
    {
        var huge = "# 标题\n\n" + string.Concat(Enumerable.Repeat("很长的一段说明。", 6000));
        var (_, _, results) = await RunAsync("Approve", plan: huge);

        var raw = results.FirstOrDefault(r => r.ToolId == PlanModePolicy.ExitPlanModeToolId)
            ?.Result?.Content.GetRawText() ?? string.Empty;

        return raw.Contains("PlanRejected", StringComparison.Ordinal)
            ? null
            : $"超长计划应被拒，实际：{raw[..Math.Min(80, raw.Length)]}";
    }

    private static async Task<string?> BlocksWritesAsync()
    {
        var client = new ScriptedClient(
            """{"invocation":"c1","tool":"write_file","args":{"path":"a.txt","content":"x"}}""");

        var mode = new PlanMode();
        mode.Enable();

        var results = await RunWithAsync(client, mode);
        var write = results.FirstOrDefault(r => r.ToolId == "write_file");

        return write?.Outcome is ToolCallOutcome.RejectedNotApproved
            ? null
            : $"计划模式下写工具应被拒，实际 {write?.Outcome}";
    }

    private static async Task<string?> AllowsReadsAsync()
    {
        var client = new ScriptedClient(
            """{"invocation":"c1","tool":"read_file","args":{"path":"a.txt"}}""");

        var mode = new PlanMode();
        mode.Enable();

        var results = await RunWithAsync(client, mode);
        var read = results.FirstOrDefault(r => r.ToolId == "read_file");

        return read?.Outcome is ToolCallOutcome.Succeeded
            ? null
            : $"计划模式下只读工具应放行，实际 {read?.Outcome}";
    }

    private static async Task<string?> OffExecutesAsync()
    {
        var client = new ScriptedClient(
            """{"invocation":"c1","tool":"write_file","args":{"path":"a.txt","content":"x"}}""");

        var results = await RunWithAsync(client, planMode: null);
        var write = results.FirstOrDefault(r => r.ToolId == "write_file");

        return write?.Outcome is ToolCallOutcome.Succeeded
            ? null
            : $"非计划模式下写工具应执行，实际 {write?.Outcome}";
    }

    private static async Task<string?> SectionGatingAsync()
    {
        var mode = new PlanMode();
        var section = new PlanModeSection(() => mode.IsActive);
        var context = new SystemPromptContext("/tmp", []);

        var off = await section.BuildAsync(context, CancellationToken.None);
        if (off is not null) return $"未激活时不应产出分节，实际：[{off}]";

        mode.Enable();
        var on = await section.BuildAsync(context, CancellationToken.None);
        if (on is null) return "激活后没有产出分节";

        // dsh 的原文以 "You are in plan mode." 开头，我们也要求明确的状态声明。
        if (!on.Contains("plan mode", StringComparison.OrdinalIgnoreCase))
            return $"分节没有声明计划模式：{on}";

        return on.Contains(PlanModePolicy.ExitPlanModeToolId, StringComparison.Ordinal)
            ? null
            : "分节没有说要调 exit_plan_mode 提交";
    }

    /// <summary>审阅意图要带"哪个选项代表批准"。</summary>
    /// <remarks>
    /// <b>客户端据此决定哪个按钮是主按钮</b>。用选项文本而不是序号：
    /// 序号在不同渲染顺序下不稳定。
    /// </remarks>
    private static async Task<string?> IntentCarriesLabelAsync()
    {
        var host = new RecordingHost("Approve");
        var mode = new PlanMode();
        mode.Enable();

        var tool = new ExitPlanModeTool(host, mode);
        await tool.ExecuteAsync(
            new ToolInvocation("c1", PlanModePolicy.ExitPlanModeToolId, Args(GoodPlan)),
            CancellationToken.None);

        var asked = host.LastQuestion;
        if (asked is null) return "没有发起提问";

        if (asked.Detail != GoodPlan) return "提问没有携带完整计划文档";

        if (asked.Intent?.Kind != AskIntent.PlanReview.Kind)
            return $"intent.kind 应为 plan-review，实际 {asked.Intent?.Kind}";

        return asked.Intent.ApproveLabel == "Approve"
            ? null
            : $"intent 没有标出批准选项：{asked.Intent.ApproveLabel}";
    }

    // ── 支撑 ─────────────────────────────────────────────────────

    private static JsonElement Args(string plan) =>
        JsonDocument.Parse(JsonSerializer.Serialize(new { plan })).RootElement.Clone();

    private static async Task<(AgentRunOutcome Outcome, int Calls, List<ToolDispatchResult> Results)>
        RunAsync(string? verdict = null, PlanMode? planMode = null,
            string plan = GoodPlan, bool cancelled = false)
    {
        // **始终打开**：这些用例测的都是"计划模式下"的行为。
        // 关掉模式的用例走 RunWithAsync（那条路径不传 mode）。
        var mode = planMode ?? new PlanMode();
        mode.Enable();
        _lastHostAsked = false;

        var client = new ScriptedClient(
            "{\"invocation\":\"c1\",\"tool\":\"exit_plan_mode\",\"args\":"
            + JsonSerializer.Serialize(new { plan }) + "}");

        var host = new RecordingHost(cancelled ? null : verdict);
        var dispatcher = new PlanDispatcher(mode, host);

        var runtime = new LoopAgentRuntime(client, dispatcher) { PlanMode = mode };

        var results = new List<ToolDispatchResult>();
        AgentRunOutcome outcome = AgentRunOutcome.Failed;

        await foreach (var e in runtime.RunAsync(
            new AgentRunRequest("s", "t", [], "做事"), CancellationToken.None))
        {
            if (e is AgentEvent.ToolCallCompleted done) results.Add(done.Result);
            if (e is AgentEvent.RunFinished finished) outcome = finished.Outcome;
        }

        return (outcome, client.Calls, results);
    }

    private static async Task<List<ToolDispatchResult>> RunWithAsync(
        IChatClient client, PlanMode? planMode)
    {
        var runtime = new LoopAgentRuntime(client, new PlainDispatcher()) { PlanMode = planMode };

        var results = new List<ToolDispatchResult>();
        await foreach (var e in runtime.RunAsync(
            new AgentRunRequest("s", "t", [], "做事"), CancellationToken.None))
        {
            if (e is AgentEvent.ToolCallCompleted done) results.Add(done.Result);
        }

        return results;
    }

    private static bool _lastHostAsked;

    private sealed class RecordingHost(string? verdict) : IAskUserHost
    {
        public AskedQuestion? LastQuestion { get; private set; }

        public ValueTask<AskResult> AskAsync(
            IReadOnlyList<AskedQuestion> questions, CancellationToken cancellationToken = default)
        {
            LastQuestion = questions.FirstOrDefault();
            _lastHostAsked = true;

            if (verdict is null)
                return ValueTask.FromResult(new AskResult(AskOutcome.Cancelled, "取消", []));

            return ValueTask.FromResult(new AskResult(
                AskOutcome.Answered, null,
                [new AskAnswer(questions[0].Header, verdict)]));
        }
    }

    private sealed class ScriptedClient(string call) : IChatClient
    {
        public int Calls { get; private set; }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> m, ChatOptions? o = null, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? o = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.Yield();

            if (Calls++ > 0)
            {
                yield return new ChatResponseUpdate(ChatRole.Assistant, "done");
                yield break;
            }

            using var document = JsonDocument.Parse(call);
            var root = document.RootElement;

            var args = root.GetProperty("args").EnumerateObject()
                .ToDictionary(p => p.Name, p => (object?)p.Value.Clone());

            yield return new ChatResponseUpdate(ChatRole.Assistant,
            [
                new FunctionCallContent(
                    root.GetProperty("invocation").GetString()!,
                    root.GetProperty("tool").GetString()!,
                    args),
            ]);
        }

        public object? GetService(Type t, object? k = null) => null;
        public void Dispose() { }
    }

    /// <summary>真的调用 <see cref="ExitPlanModeTool"/> 的 dispatcher。</summary>
    private sealed class PlanDispatcher(PlanMode mode, IAskUserHost host) : IToolDispatcher
    {
        public IReadOnlyList<ToolDescriptor> Tools { get; } =
        [
            new ToolDescriptor(
                PlanModePolicy.ExitPlanModeToolId, PlanModePolicy.ExitPlanModeToolId, "1.0.0",
                string.Empty,
                JsonDocument.Parse(
                    """{"type":"object","properties":{"plan":{"type":"string"}}}""")
                    .RootElement.Clone()),
        ];

        public IDisposable Register(IAgentTool tool) => throw new NotSupportedException();

        public async ValueTask<ToolDispatchResult> DispatchAsync(
            ToolInvocation invocation, CancellationToken cancellationToken = default)
        {
            var tool = new ExitPlanModeTool(host, mode);
            var result = await tool.ExecuteAsync(invocation, cancellationToken).ConfigureAwait(false);
            return new ToolDispatchResult(
                invocation.InvocationId, invocation.ToolId, ToolCallOutcome.Succeeded, result);
        }
    }

    /// <summary>只回固定结果的 dispatcher，用于测工具拦截。</summary>
    private sealed class PlainDispatcher : IToolDispatcher
    {
        public IReadOnlyList<ToolDescriptor> Tools { get; } =
        [
            Tool("write_file", """{"type":"object","properties":{"path":{"type":"string"},"content":{"type":"string"}}}"""),
            Tool("read_file", """{"type":"object","properties":{"path":{"type":"string"}}}"""),
        ];

        private static ToolDescriptor Tool(string id, string schema) =>
            new(id, id, "1.0.0", string.Empty,
                JsonDocument.Parse(schema).RootElement.Clone());

        public IDisposable Register(IAgentTool tool) => throw new NotSupportedException();

        public ValueTask<ToolDispatchResult> DispatchAsync(
            ToolInvocation invocation, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new ToolDispatchResult(
                invocation.InvocationId, invocation.ToolId, ToolCallOutcome.Succeeded,
                new AgentToolResult(
                    JsonDocument.Parse("""{"ok":true}""").RootElement.Clone())));
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
