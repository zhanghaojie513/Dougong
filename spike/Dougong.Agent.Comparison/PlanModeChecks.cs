using System.Text.Json;
using Dougong.Agent;
using Microsoft.Extensions.AI;

namespace Dougong.Agent.Comparison;

/// <summary>
/// 计划模式的验收。
/// </summary>
/// <remarks>
/// <b>可观察面是"宿主看到了什么"</b>，两条硬约束：
/// <list type="number">
/// <item><b>提交计划就停</b>——不是"提示模型停"，而是运行时结束本轮</item>
/// <item><b>审阅前不许动手</b>——写/执行类工具在计划模式下被拒</item>
/// </list>
/// 第 2 条比第 1 条重要：只有第 1 条时，模型可以先改文件再提交计划，
/// 而那时用户的审阅已经没有意义。
/// </remarks>
public static class PlanModeChecks
{
    public static async Task<int> RunAsync()
    {
        var failures = 0;
        failures += await CheckAsync("plan-mode-stops-after-submission", StopsAfterSubmitAsync);
        failures += await CheckAsync("plan-mode-rejects-write-before-review", RejectsWriteAsync);
        failures += await CheckAsync("plan-mode-allows-read-tools", AllowsReadAsync);
        failures += await CheckAsync("plan-mode-off-executes-normally", OffExecutesAsync);
        failures += await CheckAsync("plan-mode-section-only-when-active", SectionGatingAsync);
        failures += await CheckAsync("plan-mode-submission-is-per-turn", PerTurnAsync);
        failures += await CheckAsync("plan-mode-disable-after-review", DisableAfterReviewAsync);

        Console.WriteLine(failures == 0
            ? "PLAN-MODE: ALL PASS"
            : $"PLAN-MODE: {failures} FAILURE(S)");
        return failures;
    }

    /// <summary>提交计划后本轮立即结束。</summary>
    /// <remarks>
    /// <b>判据有两条</b>：结局是 <c>PlanAwaitingReview</c>，
    /// 且模型只被调用一次——只验结局的话，"又跑了一轮才停"也会通过。
    /// </remarks>
    private static async Task<string?> StopsAfterSubmitAsync()
    {
        var client = new ScriptedClient(
            ["""{"invocation":"c1","tool":"todo_write","args":{"todos":[{"content":"改 a.txt","priority":"high","status":"pending"}]}}"""]);

        var mode = new PlanMode();
        mode.Enable();

        var outcome = await RunAsync(client, mode);

        if (outcome is not AgentRunOutcome.PlanAwaitingReview)
            return $"结局应为 PlanAwaitingReview，实际 {outcome}";

        return client.Calls == 1
            ? null
            : $"提交计划后又调了模型 {client.Calls} 次（应只 1 次）";
    }

    /// <summary>审阅前写文件必须被拒。</summary>
    /// <remarks>
    /// <b>这是整个能力的意义所在</b>：只做"提交后停下"的话，
    /// 模型可以先 edit_file 再提交计划——改动已经发生，审阅成了形式。
    /// </remarks>
    private static async Task<string?> RejectsWriteAsync()
    {
        var client = new ScriptedClient(
            ["""{"invocation":"c1","tool":"write_file","args":{"path":"a.txt","content":"x"}}"""]);

        var mode = new PlanMode();
        mode.Enable();

        var (outcome, results) = await RunDetailedAsync(client, mode);

        var write = results.FirstOrDefault(r => r.ToolId == "write_file");
        if (write is null) return "没有 write_file 的结果";

        if (write.Outcome is not ToolCallOutcome.RejectedNotApproved)
            return $"写工具应被拒，实际 {write.Outcome}";

        // 诊断要告诉模型该做什么，而不只是"被拒"。
        if (write.Diagnostic?.Contains("计划", StringComparison.Ordinal) != true)
            return $"拒绝诊断没提到计划模式：{write.Diagnostic}";

        return outcome is AgentRunOutcome.PlanAwaitingReview
            ? null
            : $"结局应为 PlanAwaitingReview，实际 {outcome}";
    }

    /// <summary>只读工具必须放行——计划要"看"才能写。</summary>
    /// <remarks>
    /// <b>全拦会让模型只能凭猜写计划</b>，而猜测的计划没法审阅。
    /// </remarks>
    private static async Task<string?> AllowsReadAsync()
    {
        var client = new ScriptedClient(
            ["""{"invocation":"c1","tool":"read_file","args":{"path":"a.txt"}}"""]);

        var mode = new PlanMode();
        mode.Enable();

        var (_, results) = await RunDetailedAsync(client, mode);

        var read = results.FirstOrDefault(r => r.ToolId == "read_file");
        return read?.Outcome is ToolCallOutcome.Succeeded
            ? null
            : $"只读工具应放行，实际 {read?.Outcome}";
    }

    /// <summary>非计划模式下一切照常。</summary>
    /// <remarks>
    /// <b>防的是"拦截条件写反"</b>：如果判据成了 <c>!IsActive</c>，
    /// 所有场景都会异常，而只测计划模式时看不出来。
    /// </remarks>
    private static async Task<string?> OffExecutesAsync()
    {
        var client = new ScriptedClient(
            ["""{"invocation":"c1","tool":"write_file","args":{"path":"a.txt","content":"x"}}"""]);

        var (outcome, results) = await RunDetailedAsync(client, planMode: null);

        var write = results.FirstOrDefault(r => r.ToolId == "write_file");
        if (write?.Outcome is not ToolCallOutcome.Succeeded)
            return $"非计划模式下写工具应执行，实际 {write?.Outcome}";

        return outcome is AgentRunOutcome.Completed
            ? null
            : $"结局应为 Completed，实际 {outcome}";
    }

    /// <summary>提示分节只在激活时产出。</summary>
    private static async Task<string?> SectionGatingAsync()
    {
        var mode = new PlanMode();
        var section = new PlanModeSection(() => mode.IsActive);

        var off = await section.BuildAsync(new SystemPromptContext("/tmp", []), CancellationToken.None);
        if (off is not null) return $"未激活时不应产出分节，实际：[{off}]";

        mode.Enable();
        var on = await section.BuildAsync(new SystemPromptContext("/tmp", []), CancellationToken.None);

        if (on is null) return "激活后没有产出分节";

        // 分节必须说清"先提交计划再停"，否则模型不知道要等审阅。
        return on.Contains("todo_write", StringComparison.Ordinal)
            && on.Contains("停下", StringComparison.Ordinal)
                ? null
                : $"分节没有说清提交与停止：{on}";
    }

    /// <summary>提交标记是本轮的，不是会话的。</summary>
    /// <remarks>
    /// <b>做成了会话级的话，用户同意之后就再也不需要审阅了</b>——
    /// 而那正是它要防的。重置由每轮开始负责，这里验证 <c>Reset</c> 的效果。
    /// </remarks>
    private static Task<string?> PerTurnAsync()
    {
        var mode = new PlanMode();
        mode.Enable();
        mode.MarkSubmitted();

        if (!mode.ShouldStopForReview) return Task.FromResult<string?>("提交后应停下");

        mode.Reset();
        return Task.FromResult<string?>(mode.ShouldStopForReview
            ? "重置后仍然要停下——那是会话级而不是本轮级"
            : null);
    }

    /// <summary>审阅通过后关闭模式，下一轮可以执行。</summary>
    private static async Task<string?> DisableAfterReviewAsync()
    {
        var mode = new PlanMode();
        mode.Enable();
        mode.MarkSubmitted();

        if (mode.ShouldStopForReview is false)
            return "开启并提交后应停下";

        // 用户同意：关模式。
        mode.Disable();
        if (mode.IsActive) return "Disable 后仍处于激活状态";
        if (mode.ShouldStopForReview) return "Disable 后仍要停下";

        // 关闭后写工具应放行。
        var client = new ScriptedClient(
            ["""{"invocation":"c1","tool":"write_file","args":{"path":"a.txt","content":"x"}}"""]);

        var (outcome, results) = await RunDetailedAsync(client, mode);
        var write = results.FirstOrDefault(r => r.ToolId == "write_file");

        return write?.Outcome is ToolCallOutcome.Succeeded && outcome is AgentRunOutcome.Completed
            ? null
            : $"审阅后仍被拦：{write?.Outcome} / {outcome}";
    }

    // ── 支撑 ─────────────────────────────────────────────────────

    private static async Task<AgentRunOutcome> RunAsync(IChatClient client, PlanMode? planMode)
    {
        var (outcome, _) = await RunDetailedAsync(client, planMode);
        return outcome;
    }

    private static async Task<(AgentRunOutcome Outcome, List<ToolDispatchResult> Tools)>
        RunDetailedAsync(IChatClient client, PlanMode? planMode)
    {
        var runtime = new LoopAgentRuntime(client, new FakeDispatcher())
        {
            PlanMode = planMode,
        };

        var results = new List<ToolDispatchResult>();
        AgentRunOutcome outcome = AgentRunOutcome.Failed;

        await foreach (var e in runtime.RunAsync(
            new AgentRunRequest("s", "t", [], "做点什么"), CancellationToken.None))
        {
            if (e is AgentEvent.ToolCallCompleted done) results.Add(done.Result);
            if (e is AgentEvent.RunFinished finished) outcome = finished.Outcome;
        }

        return (outcome, results);
    }

    /// <summary>按脚本逐轮产出工具调用；脚本用尽后给纯文本。</summary>
    private sealed class ScriptedClient(params string[] rounds) : IChatClient
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

            var index = Calls++;
            if (index >= rounds.Length)
            {
                yield return new ChatResponseUpdate(ChatRole.Assistant, "done");
                yield break;
            }

            using var document = JsonDocument.Parse(rounds[index]);
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

    private sealed class FakeDispatcher : IToolDispatcher
    {
        public IReadOnlyList<ToolDescriptor> Tools { get; } =
        [
            Tool("todo_write", """{"type":"object","properties":{"todos":{"type":"array"}}}"""),
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
