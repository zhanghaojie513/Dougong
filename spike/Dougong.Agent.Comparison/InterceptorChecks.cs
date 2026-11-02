using System.Text.Json;
using Dougong.Agent;

namespace Dougong.Agent.Comparison;

/// <summary>
/// 工具拦截接缝的验收。
/// </summary>
/// <remarks>
/// <b>可观察面是"工具到底跑没跑、审批有没有被问"</b>，不是"拦截器被调了没有"。
/// 这一区分很重要：拦截器被调用但决策没生效，是本类能力最可能的坏法。
/// 所以每条检查都看 <see cref="FakeTool.ExecutionCount"/> 与审批计数。
/// </remarks>
public static class InterceptorChecks
{
    public static async Task<int> RunAsync()
    {
        var failures = 0;
        failures += await CheckAsync("intercept-approve-runs-tool", ApproveRunsAsync);
        failures += await CheckAsync("intercept-deny-blocks-execution", DenyBlocksAsync);
        failures += await CheckAsync("intercept-deny-skips-approval", DenySkipsApprovalAsync);
        failures += await CheckAsync("intercept-strictest-wins", StrictestWinsAsync);
        failures += await CheckAsync("intercept-stops-at-first-deny", StopsAtFirstDenyAsync);
        failures += await CheckAsync("intercept-ask-without-approver-is-denied", AskWithoutApproverAsync);
        failures += await CheckAsync("intercept-ask-reaches-approver-with-reason", AskReachesApproverAsync);
        failures += await CheckAsync("intercept-throwing-interceptor-denies", ThrowingDeniesAsync);
        failures += await CheckAsync("intercept-rejection-is-not-executed", RejectionNotExecutedAsync);
        failures += await CheckAsync("intercept-no-interceptors-is-distinguishable", NoInterceptorsAsync);

        Console.WriteLine(failures == 0
            ? "INTERCEPT: ALL PASS"
            : $"INTERCEPT: {failures} FAILURE(S)");
        return failures;
    }

    private static async Task<string?> ApproveRunsAsync()
    {
        var tool = new FakeTool("run-me", []);
        var dispatcher = Dispatcher(tool);
        dispatcher.Interceptors = [new Scripted("ok", InterceptionResult.Approve())];

        var result = await dispatcher.DispatchAsync(Invocation("run-me", "i-1"));

        if (result.Outcome != ToolCallOutcome.Succeeded)
            return $"放行后应成功，实际 {result.Outcome}：{result.Diagnostic}";
        return tool.ExecutionCount == 1 ? null : $"工具执行了 {tool.ExecutionCount} 次";
    }

    /// <summary>拒绝必须真的不执行。</summary>
    /// <remarks>
    /// <b>这是本能力的核心断言</b>：拦截器返回 Deny 但工具仍跑了，
    /// 那拦截就只是个日志。只验"结果里的 Outcome 是 Rejected"抓不到它。
    /// </remarks>
    private static async Task<string?> DenyBlocksAsync()
    {
        var tool = new FakeTool("block-me", []);
        var dispatcher = Dispatcher(tool);
        dispatcher.Interceptors = [new Scripted("no", InterceptionResult.Deny("规则不允许"))];

        var result = await dispatcher.DispatchAsync(Invocation("block-me", "i-2"));

        if (result.Outcome != ToolCallOutcome.RejectedByInterceptor)
            return $"应报 RejectedByInterceptor，实际 {result.Outcome}";

        if (tool.ExecutionCount != 0)
            return $"工具被执行了 {tool.ExecutionCount} 次——拦截没生效";

        return string.IsNullOrWhiteSpace(result.Diagnostic) ? "拒绝时没有给出原因" : null;
    }

    /// <summary>被拒的调用不该产生审批项。</summary>
    /// <remarks>
    /// <b>让用户为注定被拒的事点头是净损失</b>：浪费一次交互，还会训练
    /// "闭眼批准"，那比不做审批更糟。
    /// </remarks>
    private static async Task<string?> DenySkipsApprovalAsync()
    {
        var tool = new FakeTool("block-me", []);
        var approver = new CountingApprover();
        var dispatcher = Dispatcher(tool, approver);
        dispatcher.Interceptors = [new Scripted("no", InterceptionResult.Deny("规则不允许"))];

        await dispatcher.DispatchAsync(Invocation("block-me", "i-3"));

        return approver.Requests == 0
            ? null
            : $"被拦截的调用仍然问了审批 {approver.Requests} 次";
    }

    /// <summary>多个拦截器取最严：Deny &gt; Ask &gt; Approve。</summary>
    private static async Task<string?> StrictestWinsAsync()
    {
        // 顺序刻意把 Approve 放最前：若实现是"最后一个赢"，这条会抓到。
        var tool = new FakeTool("strict", []);
        var dispatcher = Dispatcher(tool);
        dispatcher.Interceptors =
        [
            new Scripted("a", InterceptionResult.Approve()),
            new Scripted("b", InterceptionResult.Deny("最严")),
            new Scripted("c", InterceptionResult.Approve()),
        ];

        var result = await dispatcher.DispatchAsync(Invocation("strict", "i-4"));
        if (result.Outcome != ToolCallOutcome.RejectedByInterceptor)
            return $"应被拒，实际 {result.Outcome}";

        // Ask 与 Approve 混合时取 Ask。
        var dispatcher2 = Dispatcher(new FakeTool("strict2", []));
        dispatcher2.Interceptors =
        [
            new Scripted("a", InterceptionResult.Approve()),
            new Scripted("b", InterceptionResult.Ask("要确认")),
        ];

        var result2 = await dispatcher2.DispatchAsync(Invocation("strict2", "i-5"));

        // 没有审批器时 Ask 按拒绝处理。
        return result2.Outcome == ToolCallOutcome.RejectedNotApproved
            ? null
            : $"Ask 与 Approve 混合应走确认路径，实际 {result2.Outcome}";
    }

    /// <summary>到第一个 Deny 就停，后面的拦截器不被调用。</summary>
    /// <remarks>
    /// <b>意义是成本</b>：拦截器可能读文件、起进程。结局已定还继续问
    /// 是在为一个不会有变化的答案付代价。
    /// </remarks>
    private static async Task<string?> StopsAtFirstDenyAsync()
    {
        var tool = new FakeTool("short-circuit", []);
        var dispatcher = Dispatcher(tool);
        var first = new Scripted("first", InterceptionResult.Deny("第一个就拒"));
        var second = new Scripted("second", InterceptionResult.Approve());
        dispatcher.Interceptors = [first, second];

        await dispatcher.DispatchAsync(Invocation("short-circuit", "i-6"));

        if (first.Calls != 1) return $"第一个拦截器应被调用 1 次，实际 {first.Calls}";
        return second.Calls == 0
            ? null
            : $"第一个拒绝之后仍调用了后续拦截器 {second.Calls} 次";
    }

    /// <summary>Ask 但没配审批器：按拒绝处理（fail-closed）。</summary>
    /// <remarks>
    /// <b>反过来做（放行）会让"装一个拦截器"变成静默提权</b>。
    /// </remarks>
    private static async Task<string?> AskWithoutApproverAsync()
    {
        var tool = new FakeTool("ask-me", []);
        var dispatcher = Dispatcher(tool);
        dispatcher.Interceptors = [new Scripted("ask", InterceptionResult.Ask("要人工确认"))];

        var result = await dispatcher.DispatchAsync(Invocation("ask-me", "i-7"));

        if (result.Outcome != ToolCallOutcome.RejectedNotApproved)
            return $"没有审批器时 Ask 应拒绝，实际 {result.Outcome}";

        if (tool.ExecutionCount != 0) return "没有审批器时工具仍被执行了";

        // 诊断里要说明"因为没审批器"，否则用户不知道该去配什么。
        return result.Diagnostic?.Contains("审批器", StringComparison.Ordinal) == true
            ? null
            : $"诊断没说明原因：{result.Diagnostic}";
    }

    /// <summary>Ask 的理由必须传到审批器。</summary>
    /// <remarks>
    /// <b>否则用户看到的是一个没有上下文的确认框</b>——他不知道为什么被问，
    /// 只能选择盲批或盲拒。
    /// </remarks>
    private static async Task<string?> AskReachesApproverAsync()
    {
        var tool = new FakeTool("ask-me", []);
        var approver = new CountingApprover();
        var dispatcher = Dispatcher(tool, approver);
        dispatcher.Interceptors = [new Scripted("ask", InterceptionResult.Ask("这条命令会改生产配置"))];

        await dispatcher.DispatchAsync(Invocation("ask-me", "i-8"));

        if (approver.Requests != 1) return $"应问 1 次审批，实际 {approver.Requests}";

        return approver.LastReason == "这条命令会改生产配置"
            ? null
            : $"审批请求里没有拦截理由（实际 '{approver.LastReason ?? "null"}'）";
    }

    /// <summary>拦截器抛异常按拒绝处理。</summary>
    /// <remarks>
    /// <b>放行等于让"规则坏了"变成"什么都允许"</b>。
    /// </remarks>
    private static async Task<string?> ThrowingDeniesAsync()
    {
        var tool = new FakeTool("boom", []);
        var dispatcher = Dispatcher(tool);
        dispatcher.Interceptors = [new Throwing("broken")];

        var result = await dispatcher.DispatchAsync(Invocation("boom", "i-9"));

        if (result.Outcome != ToolCallOutcome.RejectedByInterceptor)
            return $"抛异常的拦截器应导致拒绝，实际 {result.Outcome}";

        return tool.ExecutionCount == 0 ? null : "拦截器抛异常后工具仍被执行";
    }

    /// <summary>拦截拒绝不算"已执行"。</summary>
    /// <remarks>
    /// <b>这决定了恢复时敢不敢重试</b>：<c>Executed</c> 为 true 的未知结果
    /// 必须人工核实，为 false 的可安全重试。把拦截拒绝算成已执行会让
    /// 一个注定被拒的调用永远卡在待核实状态。
    /// </remarks>
    private static async Task<string?> RejectionNotExecutedAsync()
    {
        await Task.CompletedTask;
        var denied = new ToolDispatchResult("i", "t", ToolCallOutcome.RejectedByInterceptor);
        if (denied.Executed) return "拦截拒绝被算成了已执行";

        var approved = new ToolDispatchResult("i", "t", ToolCallOutcome.RejectedNotApproved);
        return approved.Executed ? "审批拒绝被算成了已执行" : null;
    }

    /// <summary>没有拦截器时返回 null（可区分"没人看"与"看过了、允许"）。</summary>
    private static async Task<string?> NoInterceptorsAsync()
    {
        var result = await ToolInterception.EvaluateAsync(
            [], Context(), CancellationToken.None);

        if (result is not null) return "空拦截器列表却给出了决策";

        var approved = await ToolInterception.EvaluateAsync(
            [new Scripted("ok", InterceptionResult.Approve())], Context(), CancellationToken.None);

        return approved?.Decision is InterceptionDecision.Approve
            ? null
            : "有拦截器并放行时应返回 Approve";
    }

    // ── 支撑 ─────────────────────────────────────────────────────

    private static ToolDispatcher Dispatcher(FakeTool tool, IToolApprover? approver = null)
    {
        var dispatcher = new ToolDispatcher(approver);
        dispatcher.Register(tool);
        return dispatcher;
    }

    private static ToolInterceptionContext Context()
    {
        using var document = JsonDocument.Parse("{}");
        return new ToolInterceptionContext(
            "s", "t", "i", "tool", document.RootElement.Clone(),
            new ToolPermission(ToolAccess.Read, true, [], [], []), ".");
    }

    private static ToolInvocation Invocation(string tool, string id)
    {
        using var document = JsonDocument.Parse("{}");
        return new ToolInvocation(id, tool, document.RootElement.Clone());
    }

    /// <summary>按脚本返回固定结论，并记录被调用次数。</summary>
    private sealed class Scripted(string name, InterceptionResult result) : IToolInterceptor
    {
        public string Name => name;

        public int Calls { get; private set; }

        public ValueTask<InterceptionResult> InterceptAsync(
            ToolInterceptionContext context, CancellationToken cancellationToken = default)
        {
            Calls++;
            return ValueTask.FromResult(result);
        }
    }

    private sealed class Throwing(string name) : IToolInterceptor
    {
        public string Name => name;

        public ValueTask<InterceptionResult> InterceptAsync(
            ToolInterceptionContext context, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("规则文件读不了");
    }

    /// <summary>记录被问了几次、理由是什么；一律批准。</summary>
    private sealed class CountingApprover : IToolApprover
    {
        public int Requests { get; private set; }

        public string? LastReason { get; private set; }

        public ValueTask<ToolApprovalDecision> RequestApprovalAsync(
            ToolApprovalRequest request, CancellationToken cancellationToken = default)
        {
            Requests++;
            LastReason = request.InterceptionReason;
            return ValueTask.FromResult(ToolApprovalDecision.Allow("ok"));
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
