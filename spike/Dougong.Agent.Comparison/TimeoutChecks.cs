using System.Text.Json;
using Dougong.Agent;

namespace Dougong.Agent.Comparison;

/// <summary>
/// 工具超时策略的验收。
/// </summary>
/// <remarks>
/// <b>可观察面有三个</b>：
/// <list type="number">
/// <item><b>终态类型</b>——必须是 <c>TimedOut</c>，不能是 <c>CancelledDuringExecution</c>。
/// 宿主靠它决定"要不要重试"，两者答案相反。</item>
/// <item><b>是否算已执行</b>——超时的工具**启动过**，可能已写文件。
/// 当成"未执行"会让宿主安全地重试，而文件可能已经改了一半。</item>
/// <item><b>诊断文本</b>——必须说明上限是多少、副作用可能已发生。
/// 只说"超时"会让排查者以为没有任何影响。</item>
/// </list>
/// </remarks>
public static class TimeoutChecks
{
    public static async Task<int> RunAsync()
    {
        var failures = 0;
        failures += await CheckAsync("hanging-tool-times-out", HangingTimesOutAsync);
        failures += await CheckAsync("timeout-is-not-reported-as-cancellation", NotCancellationAsync);
        failures += await CheckAsync("timeout-counts-as-executed", CountsAsExecutedAsync);
        failures += await CheckAsync("timeout-diagnostic-names-the-limit", DiagnosticHasLimitAsync);
        failures += await CheckAsync("fast-tool-is-not-timed-out", FastNotTimedAsync);
        failures += await CheckAsync("per-tool-timeout-overrides-default", PerToolOverrideAsync);
        failures += await CheckAsync("zero-default-means-unlimited", ZeroMeansUnlimitedAsync);
        failures += await CheckAsync("outer-cancel-still-wins", OuterCancelWinsAsync);
        failures += await CheckAsync("new-outcome-does-not-shift-existing-values", EnumStabilityAsync);
        failures += await CheckAsync("ignoring-cancel-does-not-hang-dispatcher", IgnoringCancelDoesNotHangAsync);
        failures += await CheckAsync("ignoring-cancel-diagnostic-is-honest", IgnoringCancelIsHonestAsync);

        Console.WriteLine(failures == 0
            ? "TIMEOUT: ALL PASS"
            : $"TIMEOUT: {failures} FAILURE(S)");
        return failures;
    }

    /// <summary>挂死且不响应取消的工具必须超时返回，而不是永远等下去。</summary>
    /// <remarks>
    /// <b>这是本项存在的理由</b>：在此之前 dispatcher 对工具是裸 await，
    /// 一个卡住的工具会挂住整轮，用户看到的是"AI 卡死了"而没有任何错误。
    /// </remarks>
    private static async Task<string?> HangingTimesOutAsync()
    {
        var dispatcher = MakeDispatcher(TimeSpan.FromMilliseconds(150));
        dispatcher.Register(new HangingTool(TimeSpan.FromSeconds(30)));

        var started = DateTime.UtcNow;
        var result = await dispatcher.DispatchAsync(Invocation("hang"));
        var elapsed = DateTime.UtcNow - started;

        // 30 秒的工具有 150 毫秒上限:必须在显著短于工具自身耗时内返回。
        if (elapsed > TimeSpan.FromSeconds(15))
            return $"超时没有生效：等了 {elapsed.TotalSeconds:0.#} 秒（工具要跑 30 秒）";

        return result.Outcome == ToolCallOutcome.TimedOut
            ? null
            : $"终态应为 TimedOut，实际 {result.Outcome}（诊断：{result.Diagnostic}）";
    }

    /// <summary>超时不能报成"用户取消"。</summary>
    /// <remarks>
    /// 两者对宿主的含义相反：取消是"用户不想做"，超时是"这次太慢"。
    /// 合成一个值，宿主就无法决定要不要重试——而那是它唯一要做的事。
    /// </remarks>
    private static async Task<string?> NotCancellationAsync()
    {
        var dispatcher = MakeDispatcher(TimeSpan.FromMilliseconds(120));
        dispatcher.Register(new HangingTool(TimeSpan.FromSeconds(30)));

        var result = await dispatcher.DispatchAsync(Invocation("hang"));

        return result.Outcome == ToolCallOutcome.CancelledDuringExecution
            ? "超时被报成了 CancelledDuringExecution——宿主会以为用户主动放弃，从而不再重试"
            : null;
    }

    /// <summary>超时必须算"已执行"，因为它确实启动了。</summary>
    /// <remarks>
    /// <b>这是最容易被写错的一处</b>。直觉上"超时=没结果=没做"，但工具在
    /// 超时前已经跑了一段时间：可能写了一半文件、发了一次请求。
    /// 归到"未执行"会让宿主放心重试，而重试会做第二遍。
    /// </remarks>
    private static async Task<string?> CountsAsExecutedAsync()
    {
        var dispatcher = MakeDispatcher(TimeSpan.FromMilliseconds(120));
        dispatcher.Register(new HangingTool(TimeSpan.FromSeconds(30)));

        var result = await dispatcher.DispatchAsync(Invocation("hang"));

        return result.Executed
            ? null
            : "超时被判为未执行——宿主会认为可以安全重试，而副作用可能已经发生";
    }

    /// <summary>诊断里必须有具体上限，且提示副作用风险。</summary>
    private static async Task<string?> DiagnosticHasLimitAsync()
    {
        var dispatcher = MakeDispatcher(TimeSpan.FromMilliseconds(250));
        dispatcher.Register(new HangingTool(TimeSpan.FromSeconds(30)));

        var result = await dispatcher.DispatchAsync(Invocation("hang"));
        var diagnostic = result.Diagnostic ?? string.Empty;

        if (!diagnostic.Contains("秒", StringComparison.Ordinal)
            && !diagnostic.Contains("毫秒", StringComparison.Ordinal))
        {
            return $"诊断没说明上限是多少：{diagnostic}";
        }

        if (!diagnostic.Contains("副作用", StringComparison.Ordinal))
            return $"诊断没提示副作用风险：{diagnostic}";

        return null;
    }

    /// <summary>正常返回的工具不该被误杀。</summary>
    /// <remarks>
    /// 超时最大的风险不是"没生效"，是"误杀"——把正常工作的工具判为超时，
    /// 结果被静默丢弃，而用户以为操作成功。所以边界两侧都要钉。
    /// </remarks>
    private static async Task<string?> FastNotTimedAsync()
    {
        var dispatcher = MakeDispatcher(TimeSpan.FromSeconds(30));
        dispatcher.Register(new FastTool());

        var result = await dispatcher.DispatchAsync(Invocation("fast"));

        return result.Outcome == ToolCallOutcome.Succeeded
            ? null
            : $"快速工具被误判：{result.Outcome}（诊断：{result.Diagnostic}）";
    }

    /// <summary>逐工具声明的上限优先于 dispatcher 默认值。</summary>
    /// <remarks>
    /// 这是"不同工具耗時差两个数量级"的落地点：文件读毫秒级、
    /// shell 可能几分钟。一个全局值必然一边误杀一边放行。
    /// </remarks>
    private static async Task<string?> PerToolOverrideAsync()
    {
        // 默认 30 秒（很宽），但工具自己声明 150 毫秒。
        var dispatcher = MakeDispatcher(TimeSpan.FromSeconds(30));
        dispatcher.Register(new HangingTool(TimeSpan.FromSeconds(30), planTimeout: TimeSpan.FromMilliseconds(150)));

        var started = DateTime.UtcNow;
        var result = await dispatcher.DispatchAsync(Invocation("hang"));
        var elapsed = DateTime.UtcNow - started;

        if (elapsed > TimeSpan.FromSeconds(10))
            return $"逐工具上限没生效：等了 {elapsed.TotalSeconds:0.#} 秒";

        return result.Outcome == ToolCallOutcome.TimedOut
            ? null
            : $"逐工具上限未生效，终态 {result.Outcome}";
    }

    /// <summary>默认值为零表示不限制——有意的逃生口。</summary>
    /// <remarks>
    /// 某些宿主的工具就是要跑很久（批量迁移、大模型推理）。
    /// "被误杀"比"挂住"更难排查：误杀静默丢结果，挂住至少有现象。
    /// 所以必须保留"完全不限"这个选项，并且它要真的生效。
    /// </remarks>
    private static async Task<string?> ZeroMeansUnlimitedAsync()
    {
        var dispatcher = MakeDispatcher(TimeSpan.Zero);
        dispatcher.Register(new HangingTool(TimeSpan.FromMilliseconds(300)));

        var result = await dispatcher.DispatchAsync(Invocation("hang"));

        // 工具 300 毫秒后自己成功返回，不该被判超时。
        return result.Outcome == ToolCallOutcome.Succeeded
            ? null
            : $"上限为零时应不限制，实际 {result.Outcome}（诊断：{result.Diagnostic}）";
    }

    /// <summary>外层取消仍应报"取消"，不能因为配了超时就变成"超时"。</summary>
    /// <remarks>
    /// 两条取消通道串在同一个 CancellationToken 上。若判据只看
    /// "是否被取消"而不看"谁取消的"，用户按 Esc 会被报成超时。
    /// </remarks>
    private static async Task<string?> OuterCancelWinsAsync()
    {
        var dispatcher = MakeDispatcher(TimeSpan.FromSeconds(30));
        dispatcher.Register(new HangingTool(TimeSpan.FromSeconds(30)));

        using var cts = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromMilliseconds(150));

        var result = await dispatcher.DispatchAsync(Invocation("hang"), cts.Token);

        return result.Outcome == ToolCallOutcome.CancelledDuringExecution
            ? null
            : $"外层取消被报成 {result.Outcome}——宿主会以为是自己超时，而不是用户取消";
    }

    /// <summary>
    /// 完全不响应取消的工具不能把 dispatcher 挂住。
    /// </summary>
    /// <remarks>
    /// <b>这条钉的是我自己埋的坑</b>：第一版超时实现里，超时后
    /// `await work` 会一直等下去——工具不响应取消就永远不返回。
    /// 于是"修复工具卡住整轮"变成了"卡住在超时机制里"，
    /// 症状和修之前完全一样，只是位置换了。
    /// <para>
    /// 验法：20 秒的工具、100 毫秒上限、且工具忽略取消。
    /// 必须在远小于 20 秒内返回。
    /// </para>
    /// </remarks>
    private static async Task<string?> IgnoringCancelDoesNotHangAsync()
    {
        var dispatcher = MakeDispatcher(TimeSpan.FromMilliseconds(100));
        dispatcher.Register(new DeafTool(TimeSpan.FromSeconds(20)));

        var started = DateTime.UtcNow;
        var result = await dispatcher.DispatchAsync(Invocation("deaf"));
        var elapsed = DateTime.UtcNow - started;

        // 宽限期 2 秒。阈值放到 15 秒是刻意的：这条要区分的是
        // "有没有无限等"（20 秒 vs 2 秒），差一个数量级。
        // 卡在 10 秒会在 CI 负载高时误报——不稳定的检查比没有更糟，
        // 因为人开始无视它之后，真正的回归也会被当成噪音。
        if (elapsed > TimeSpan.FromSeconds(15))
        {
            return $"工具忽略取消时 dispatcher 仍在等（已等 {elapsed.TotalSeconds:0.#} 秒）"
                + "——超时机制自己变成了新的挂起点";
        }

        return result.Outcome == ToolCallOutcome.TimedOut
            ? null
            : $"终态应为 TimedOut，实际 {result.Outcome}";
    }

    /// <summary>
    /// 无法确认工具已停止时，诊断必须承认这一点。
    /// </summary>
    /// <remarks>
    /// <b>不能说"已中止"</b>：那意味着副作用没发生，而工具可能还在写文件。
    /// 宿主据此才能决定要不要人工核对磁盘状态。
    /// </remarks>
    private static async Task<string?> IgnoringCancelIsHonestAsync()
    {
        var dispatcher = MakeDispatcher(TimeSpan.FromMilliseconds(100));
        dispatcher.Register(new DeafTool(TimeSpan.FromSeconds(20)));

        var result = await dispatcher.DispatchAsync(Invocation("deaf"));
        var diagnostic = result.Diagnostic ?? string.Empty;

        if (!diagnostic.Contains("无法确认", StringComparison.Ordinal))
        {
            return $"诊断没有承认「无法确认它是否还在运行」：{diagnostic}";
        }

        return null;
    }

    // ── 支撑 ─────────────────────────────────────────────────────

    /// <summary>
    /// 新增枚举成员不能改变已有成员的数值。
    /// </summary>
    /// <remarks>
    /// <b>这是本轮真实踩到的坑</b>：把 <c>TimedOut</c> 插在枚举中间，
    /// 让其后所有成员的隐式数值 +1，ApiCompat 报 CP0011 才拦住。
    /// <para>
    /// 为什么严重：这些数值会被序列化、写进会话日志、存进宿主数据库。
    /// "8" 在改动前后指向不同含义，旧记录会被解读成另一个终态——
    /// 那是**静默的错误结论**，比崩溃难查得多。
    /// </para>
    /// <para>
    /// 这条检查把数值钉死：任何重排都会失败，而不依赖 ApiCompat
    /// （它只在 pack 时跑，本地 build 与 test 都不触发）。
    /// </para>
    /// </remarks>
    private static Task<string?> EnumStabilityAsync()
    {
        // 截至 0.1.11 的已发布数值。新增成员只能往后追加。
        (ToolCallOutcome Value, int Expected)[] pinned =
        [
            (ToolCallOutcome.Succeeded, 0),
            (ToolCallOutcome.Failed, 1),
            (ToolCallOutcome.RejectedInvalidArguments, 2),
            (ToolCallOutcome.RejectedDuplicateInvocation, 3),
            (ToolCallOutcome.RejectedUnknownTool, 4),
            (ToolCallOutcome.CancelledBeforeExecution, 5),
            (ToolCallOutcome.RejectedNotApproved, 6),
            (ToolCallOutcome.CancelledDuringExecution, 7),
            (ToolCallOutcome.OutcomeUnknown, 8),
            (ToolCallOutcome.TimedOut, 9),
        ];

        foreach (var (value, expected) in pinned)
        {
            var actual = (int)value;
            if (actual != expected)
            {
                return Task.FromResult<string?>(
                    $"{value} 的数值变成了 {actual}，应为 {expected}"
                    + "——枚举重排会让已写入日志与数据库的旧值指向另一个含义");
            }
        }

        return Task.FromResult<string?>(null);
    }

    private static ToolDispatcher MakeDispatcher(TimeSpan defaultTimeout) =>
        new()
        {
            // 不设审批器：本套检查只验超时，审批是另一套的事。
            DefaultToolTimeout = defaultTimeout,
        };

    private static ToolInvocation Invocation(string toolId)
    {
        using var document = JsonDocument.Parse("{}");
        return new ToolInvocation($"i-{Guid.NewGuid():N}", toolId, document.RootElement.Clone());
    }

    /// <summary>永不自己结束的工具；可配置是否响应取消。</summary>
    private sealed class HangingTool(TimeSpan duration, TimeSpan? planTimeout = null) : IAgentTool
    {
        public ToolDescriptor Descriptor { get; } = new(
            "hang", "hang", "1.0.0", "测试用挂死工具",
            JsonDocument.Parse("""{"type":"object","properties":{}}""").RootElement.Clone());

        public ToolPermission DescribePermission(JsonElement arguments) =>
            new(ToolAccess.Read, false, [], [], []);

        public ToolExecutionPlan DescribeExecution(JsonElement arguments) =>
            new(ToolConcurrency.Serial) { Timeout = planTimeout };

        public IReadOnlyList<string> DescribeFileDependencies(JsonElement arguments) => [];

        public async ValueTask<AgentToolResult> ExecuteAsync(
            ToolInvocation invocation, CancellationToken cancellationToken)
        {
            // 响应取消：Task.Delay 会抛 OperationCanceledException。
            // 不响应取消的工具是另一个场景，见 HangingTool 的 duration 参数用途。
            await Task.Delay(duration, cancellationToken).ConfigureAwait(false);

            using var document = JsonDocument.Parse("""{"ok":true}""");
            return new AgentToolResult(document.RootElement.Clone());
        }
    }

    /// <summary>
    /// 完全忽略取消的工具。
    /// </summary>
    /// <remarks>
    /// <b>这是最危险的一类</b>：它不检查 <c>CancellationToken</c>，
    /// 所以超时后 dispatcher 的等待不会因为取消而结束。
    /// 若没有宽限期，这里会让超时机制本身挂住整轮——
    /// 那正是本项要解决的问题，绕一圈又回来了。
    /// </remarks>
    private sealed class DeafTool(TimeSpan duration) : IAgentTool
    {
        public ToolDescriptor Descriptor { get; } = new(
            "deaf", "deaf", "1.0.0", "测试用忽略取消的工具",
            JsonDocument.Parse("""{"type":"object","properties":{}}""").RootElement.Clone());

        public ToolPermission DescribePermission(JsonElement arguments) =>
            new(ToolAccess.Read, false, [], [], []);

        public ToolExecutionPlan DescribeExecution(JsonElement arguments) =>
            new(ToolConcurrency.Serial);

        public IReadOnlyList<string> DescribeFileDependencies(JsonElement arguments) => [];

        public async ValueTask<AgentToolResult> ExecuteAsync(
            ToolInvocation invocation, CancellationToken cancellationToken)
        {
            // **故意不传 token**：模拟不响应取消的实现。
            await Task.Delay(duration).ConfigureAwait(false);

            using var document = JsonDocument.Parse("""{"ok":true}""");
            return new AgentToolResult(document.RootElement.Clone());
        }
    }

    /// <summary>立刻返回的工具。</summary>
    private sealed class FastTool : IAgentTool
    {
        public ToolDescriptor Descriptor { get; } = new(
            "fast", "fast", "1.0.0", "测试用快速工具",
            JsonDocument.Parse("""{"type":"object","properties":{}}""").RootElement.Clone());

        public ToolPermission DescribePermission(JsonElement arguments) =>
            new(ToolAccess.Read, false, [], [], []);

        public ToolExecutionPlan DescribeExecution(JsonElement arguments) =>
            new(ToolConcurrency.Serial);

        public IReadOnlyList<string> DescribeFileDependencies(JsonElement arguments) => [];

        public ValueTask<AgentToolResult> ExecuteAsync(
            ToolInvocation invocation, CancellationToken cancellationToken)
        {
            using var document = JsonDocument.Parse("""{"ok":true}""");
            return ValueTask.FromResult(
                new AgentToolResult(document.RootElement.Clone()));
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
