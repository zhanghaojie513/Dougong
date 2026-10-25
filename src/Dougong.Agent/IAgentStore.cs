using System.Text.Json;

namespace Dougong.Agent;

/// <summary>
/// 会话持久化契约。**唯一真相源**，也是放行副作用的屏障：
/// 只有 <see cref="CommitAsync"/> 返回成功，后续执行才可继续。
/// </summary>
/// <remarks>
/// 设计约束（docs/architecture.md §4.2、§6.4）：
/// <list type="bullet">
/// <item>事件订阅顺序<b>不</b>承担屏障职责；必须显式 await 提交结果</item>
/// <item>提交失败或存储不可用时停止后续执行，不静默降级</item>
/// <item>缺少终态的工具调用必须能恢复为 <see cref="ToolCallOutcome.OutcomeUnknown"/>，
/// 且不得自动重放副作用</item>
/// <item>实现需声明自身持久化级别；杀进程测试不能代替断电测试</item>
/// </list>
/// </remarks>
public interface IAgentStore : IAsyncDisposable
{
    /// <summary>本次打开的会话标识。</summary>
    string SessionId { get; }

    /// <summary>存储实现自述的持久化保证，供宿主如实展示而非假设。</summary>
    StoreDurability Durability { get; }

    /// <summary>
    /// 追加一条记录并等待其落盘。返回即代表已提交；抛异常代表<b>未</b>提交。
    /// 实现必须保证：返回成功之后，即使进程崩溃也能恢复出这条记录。
    /// </summary>
    ValueTask CommitAsync(StoreRecord record, CancellationToken cancellationToken = default);

    /// <summary>按提交顺序读取已提交记录，用于恢复。不得返回未提交内容。</summary>
    IAsyncEnumerable<StoreRecord> ReadCommittedAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// 存储实现的持久化级别。宿主必须如实转述，不能把进程崩溃存活说成断电安全。
/// </summary>
public enum StoreDurability
{
    /// <summary>仅内存；进程退出即丢失。只用于测试。</summary>
    ProcessMemory,

    /// <summary>进程崩溃后仍可恢复，但不保证断电安全（例如未 fsync）。</summary>
    SurvivesProcessCrash,

    /// <summary>声明断电安全，需实现侧提供独立的持久化证明。</summary>
    SurvivesPowerLoss,
}

/// <summary>一条已提交记录的封装，带顺序号以支持单写者校验与缺口检测。</summary>
public sealed record StoreRecord(long Sequence, StoreRecordKind Kind, JsonElement Payload)
{
    public override string ToString() => $"#{Sequence} {Kind}";
}

/// <summary>记录类型。恢复时需要据此判断“有开始无终态”的调用。</summary>
public enum StoreRecordKind
{
    /// <summary>用户输入。执行前必须先提交。</summary>
    UserInput,

    /// <summary>完整的模型响应（含工具调用请求）。执行工具前必须先提交。</summary>
    ModelResponse,

    /// <summary>工具调用开始。提交后才允许产生副作用。</summary>
    ToolCallStarted,

    /// <summary>工具调用终态。与 Started 配对。</summary>
    ToolCallCompleted,

    /// <summary>审批决定。</summary>
    ApprovalDecision,

    /// <summary>运行终态。</summary>
    RunFinished,
}

/// <summary>提交失败时抛出的异常。携带失败阶段便于诊断，不包装底层异常对象。</summary>
public sealed class StoreCommitException(string stage, string message, Exception? inner = null)
    : Exception($"提交失败（阶段：{stage}）：{message}", inner)
{
    public string Stage { get; } = stage;
}

/// <summary>
/// 恢复会话时对账出的未知结果。有开始记录却无终态的工具调用会落到这里，
/// 宿主必须暂停该 turn，不得自动重放。
/// </summary>
/// <param name="ArgumentsJson">
/// 该调用**当时的参数原文**。没有它，宿主连"当时要做什么"都不知道，
/// 无从对账（G10 的一部分）。旧会话记录可能不含此字段，此时为 null。
/// </param>
/// <remarks>
/// <b>为什么需要参数</b>：`OutcomeUnknown` 的整个意义是"让宿主能判断副作用是否发生"。
/// 只有 invocation id 和工具名时，宿主唯一能做的就是问用户"你还记得刚才那个写操作吗"——
/// 那不叫对账。
/// <para>
/// <b>关于幂等键</b>：工具若能声明自己的幂等键（例如"这次写的是文件 X 的版本 Y"），
/// 宿主就能去外部核实。当前 <see cref="IAgentTool"/> 没有这个通道，因此本记录只提供
/// 参数原文，由宿主结合工具语义自行核实。不预设键格式——与 `architecture.md` §4.4
/// 拒绝"预先冻结资源键格式"同一判据。
/// </para>
/// </remarks>
/// <param name="InvocationId">产生未知结果的那次调用 ID。</param>
/// <param name="ToolId">调用的工具 ID。</param>
/// <param name="Reason">人可读的说明，区分"可安全重试"与"须人工核实"。</param>
public sealed record UnknownOutcome(
    string InvocationId,
    string ToolId,
    string Reason,
    string? ArgumentsJson = null)
{
    /// <summary>
    /// 该工具是否声明了可能不可撤销的副作用。默认 <c>true</c>。
    /// </summary>
    /// <remarks>
    /// 宿主据此决定处置方式：<c>false</c> 表示可安全重试；<c>true</c> 表示必须人工核实。
    /// <para>
    /// 默认 true（保守）：旧代码构造的 UnknownOutcome、以及旧会话记录（不含此字段）
    /// 都会被当作"需人工核实"——宁可多让人看一眼，也不要让宿主误以为可以安全重放。
    /// </para>
    /// <para>
    /// 用 <c>init</c> 属性而不是构造参数，避免破坏已有调用的构造函数签名
    /// （ApiCompat 在 CI 上会报 CP0002）。
    /// </para>
    /// </remarks>
    public bool MayHaveIrreversibleEffects { get; init; } = true;
}
