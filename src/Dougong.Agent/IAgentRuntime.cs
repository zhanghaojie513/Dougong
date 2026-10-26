using System.Text.Json;

namespace Dougong.Agent;

/// <summary>
/// Agent 运行入口。候选 A（MAF 包装）与候选 B（自研循环）都实现同一接口，
/// 以便用同一套反例比较行为，而不是比较代码写法。
/// </summary>
public interface IAgentRuntime
{
    /// <summary>
    /// 运行一个 turn，按发生顺序产出事件。事件序列本身是验收对象：
    /// 文本片段必须先于工具调用、工具调用必须先于其终态、未执行的调用也必须有终态。
    /// </summary>
    IAsyncEnumerable<AgentEvent> RunAsync(
        AgentRunRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>一次运行请求。本切片不涉及持久化，历史由调用方以已提交消息传入。</summary>
public sealed record AgentRunRequest(
    string SessionId,
    string TurnId,
    IReadOnlyList<AgentMessage> History,
    string UserInput,
    int MaxToolRounds = 8);

/// <summary>
/// 已提交历史中的一条消息。内容使用共享 DTO，不携带插件对象。
/// </summary>
/// <remarks>
/// <see cref="ToolCalls"/> 与 <see cref="ToolCallId"/> 是为了让恢复出来的历史
/// 能重新表达工具交互：只留 "assistant 说了什么" 与 "tool 返回了什么" 的扁平文本，
/// 下一轮请求会缺少 <c>tool_calls</c> 与 <c>tool_call_id</c> 的配对，
/// provider 会拒掉整轮。二者都可选，纯文本历史不受影响。
/// </remarks>
public sealed record AgentMessage(AgentRole Role, string Content)
{
    /// <summary>本条 assistant 消息请求的工具调用（按请求顺序）。</summary>
    public IReadOnlyList<AgentToolCallRef> ToolCalls { get; init; } = [];

    /// <summary>本条 tool 消息对应的调用 ID。</summary>
    public string? ToolCallId { get; init; }

    /// <summary>本条 tool 消息对应的工具 ID。</summary>
    public string? ToolId { get; init; }
}

/// <summary>
/// 历史中记录的一次工具调用请求。参数保留原始 JSON，
/// 以便恢复后重新派发时经过同一套 schema 校验与规范化。
/// </summary>
public sealed record AgentToolCallRef(string InvocationId, string ToolId, JsonElement Arguments);

public enum AgentRole
{
    System,
    User,
    Assistant,
    Tool,
}

/// <summary>运行期间产生的事件。</summary>
public abstract record AgentEvent
{
    /// <summary>运行开始。</summary>
    public sealed record RunStarted(string SessionId, string TurnId) : AgentEvent;

    /// <summary>模型产出的文本片段；只是暂存投影，不构成执行依据。</summary>
    public sealed record TextDelta(string Text) : AgentEvent;

    /// <summary>聚合完成、即将派发的工具调用。</summary>
    public sealed record ToolCallRequested(string InvocationId, string ToolId, JsonElement Arguments)
        : AgentEvent;

    /// <summary>工具调用的最终状态。每个 ToolCallRequested 必须恰好对应一个该事件。</summary>
    public sealed record ToolCallCompleted(ToolDispatchResult Result) : AgentEvent;

    /// <summary>
    /// 上下文已压缩。宿主应如实展示这件事：压缩是有损的，
    /// 用户需要知道"模型看到的不再是完整历史"。
    /// </summary>
    /// <remarks>
    /// <see cref="ContextCompacted.Applied"/> 为 false 表示压缩已决定但未完成（失败或预算不足），
    /// 此时历史未被替换，必须继续按原历史运行。
    /// </remarks>
    public sealed record ContextCompacted(
        int RemovedMessages,
        int EstimatedTokensBefore,
        int EstimatedTokensAfter,
        bool Applied,
        string? Diagnostic = null) : AgentEvent;

    /// <summary>
    /// 模型更新了计划（通过 <c>todo_write</c> 工具）。
    /// </summary>
    /// <remarks>
    /// **全量替换语义**：<see cref="Entries"/> 就是当前完整计划，不是增量。
    /// 这与 ACP 的 plan 一致，避免宿主自己做增量合并（那容易出错且难以验证）。
    /// <para>
    /// 这是模型**声明**的计划，不是运行时推测的。宿主可以据此展示，
    /// 但不应把它当作"模型一定会这么做"的承诺。
    /// </para>
    /// </remarks>
    public sealed record PlanUpdated(IReadOnlyList<PlanEntry> Entries) : AgentEvent;

    /// <summary>
    /// 某个工具结果因为过长，进入上下文时被裁剪过。
    /// </summary>
    /// <remarks>
    /// 这是**必须让宿主知道**的事：模型看到的不是完整输出。静默裁剪会让
    /// "为什么模型没注意到那段错误信息"这类问题无从排查。
    /// <para>
    /// 注意作用范围：只裁剪**模型上下文**，会话日志与会话恢复仍使用完整原文。
    /// 所以这不是数据丢失，是上下文卫生。
    /// </para>
    /// </remarks>
    public sealed record ToolResultTruncated(
        string InvocationId,
        string ToolId,
        int OriginalChars,
        int KeptChars) : AgentEvent;

    /// <summary>运行结束，携带终止原因。</summary>
    public sealed record RunFinished(AgentRunOutcome Outcome, string? Diagnostic = null) : AgentEvent;
}

/// <summary>一次运行的终止原因。</summary>
public enum AgentRunOutcome
{
    /// <summary>模型不再请求工具，正常结束。</summary>
    Completed,

    /// <summary>达到工具轮次上限。</summary>
    ToolRoundLimitReached,

    /// <summary>调用方取消。</summary>
    Cancelled,

    /// <summary>模型响应无效（重复/缺失调用 ID、畸形参数等），未执行后续调用。</summary>
    InvalidModelResponse,

    /// <summary>运行时内部错误。</summary>
    Failed,
}
