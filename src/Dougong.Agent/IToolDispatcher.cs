using System.Text.Json;

namespace Dougong.Agent;

/// <summary>
/// 工具调用的唯一执行入口。所有宿主、Skills、MCP 与框架适配器都必须经由这里，
/// 不允许任何旁路直接调用 <see cref="IAgentTool.ExecuteAsync"/>。
/// </summary>
/// <remarks>
/// 本切片实现：schema 校验、参数规范化、调用 ID 去重、权限描述采集、
/// 审批与执行前复核、两级资源协调（串行 / 工作区独占）、终态记录。
/// 尚未实现：资源级读写键、跨工作区仲裁、完整 JSON Schema 校验。
/// 这些缺口在 docs/m1-candidate-comparison.md 中如实记录，不宣称已具备。
/// </remarks>
public interface IToolDispatcher
{
    /// <summary>注册工具；返回的句柄释放后撤销注册。</summary>
    IDisposable Register(IAgentTool tool);

    /// <summary>已注册工具的冻结描述符，供构造模型工具 schema。</summary>
    IReadOnlyList<ToolDescriptor> Tools { get; }

    /// <summary>
    /// 派发一次调用。无论成功、失败、取消还是被拒绝，都必须返回一个明确的终态，
    /// 不能抛出未归类的异常让调用方失去这条调用的结果。
    /// </summary>
    ValueTask<ToolDispatchResult> DispatchAsync(
        ToolInvocation invocation,
        CancellationToken cancellationToken = default);
}

/// <summary>一次工具调用的终态。每个模型请求的调用都必须有且只有一个终态。</summary>
public enum ToolCallOutcome
{
    /// <summary>已执行并成功。</summary>
    Succeeded,

    /// <summary>已执行但工具报告失败。</summary>
    Failed,

    /// <summary>参数不是合法 JSON 或不满足 schema，未执行。</summary>
    RejectedInvalidArguments,

    /// <summary>调用 ID 重复或缺失，未执行。</summary>
    RejectedDuplicateInvocation,

    /// <summary>工具未注册，未执行。</summary>
    RejectedUnknownTool,

    /// <summary>执行前被取消，未执行。</summary>
    CancelledBeforeExecution,

    /// <summary>审批被拒绝，或执行前复核发现与批准时不一致，未执行。</summary>
    RejectedNotApproved,

    /// <summary>执行过程中被取消。</summary>
    CancelledDuringExecution,


    /// <summary>
    /// 恢复时发现"有开始记录、无终态"。无法确认副作用是否发生，
    /// 必须由宿主对账，不得自动重放。
    /// </summary>
    OutcomeUnknown,

    /// <summary>
    /// 执行超过策略上限，被中止。
    /// </summary>
    /// <remarks>
    /// <b>与 <see cref="CancelledDuringExecution"/> 分开，不合并</b>：两者都是
    /// "执行中被打断"，但原因与后果都不同，宿主对它们的处理也应该不同。
    /// <list type="bullet">
    /// <item>取消是**外部意图**（用户按了 Esc）。宿主不该重试——用户不想做这件事了。</item>
    /// <item>超时是**策略判定**（工具太久没返回）。宿主可以考虑重试、换参数、或告诉用户去调上限。</item>
    /// </list>
    /// 合成一个枚举值，宿主就无法区分"用户不想做"和"这次太慢"，
    /// 而那决定了它下一步该做什么。
    /// <para>
    /// <b>它属于"已执行"</b>（见 <see cref="ToolDispatchResult.Executed"/>）：
    /// 工具已经启动过，可能已经写了文件、发了请求。所以超时后**不能**当作
    /// "什么都没发生"来重试——这一点与"执行前取消"截然不同。
    /// </para>
    /// <para>
    /// <b>为什么排在最后</b>：枚举值是隐式的 0..N。插在中间会让其后所有成员的
    /// 数值+1，而它们会被序列化、也被宿主写进日志与数据库——
    /// 那些地方的 "8" 从此指向另一个含义。ApiCompat 会报 CP0011 拦住这种改动。
    /// 新成员一律追加在末尾。
    /// </para>
    /// </remarks>
    TimedOut,
}

/// <summary>
/// 派发结果。<see cref="Result"/> 仅在有内容时非空（成功或工具返回了错误内容）。
/// <see cref="Diagnostic"/> 用于宿主记录失败原因，不进入模型历史。
/// </summary>
/// <remarks>
/// <b>耗时分成两段，不是一段。</b>审批等待与工具执行是两种完全不同的时间：
/// 前者取决于人多快点确认，后者取决于工具本身多慢。合成一个总耗时会让
/// "这次调用很慢"无法归因——而这两段的优化方向恰恰相反
/// （前者要改交互，后者要改实现）。
/// <para>
/// 各段在"该段没有发生"时为 <c>null</c>，不是 <see cref="TimeSpan.Zero"/>：
/// 没配审批器与"审批瞬间通过"是两件事，用 0 冒充会让统计里混进一批
/// 假的"审批很快"。
/// </para>
/// </remarks>
public sealed record ToolDispatchResult(
    string InvocationId,
    string ToolId,
    ToolCallOutcome Outcome,
    AgentToolResult? Result = null,
    string? Diagnostic = null,
    ToolPermission? Permission = null,
    TimeSpan? ApprovalElapsed = null,
    TimeSpan? ExecutionElapsed = null)
{
    public bool Executed => Outcome is ToolCallOutcome.Succeeded
        or ToolCallOutcome.Failed
        or ToolCallOutcome.CancelledDuringExecution
        // 超时也算"已执行"：工具确实启动过。把它归到未执行会让宿主
        // 以为可以安全重试，而那时文件可能已经写了一半。
        or ToolCallOutcome.TimedOut;
}
