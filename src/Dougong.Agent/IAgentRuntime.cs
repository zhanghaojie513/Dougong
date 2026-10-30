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

    /// <summary>
    /// 模型的推理片段（reasoning / thinking），**与正文分开**。
    /// </summary>
    /// <remarks>
    /// <b>为什么不并进 <see cref="TextDelta"/></b>：推理不是回答。把它当正文显示
    /// 会让用户以为模型已经答完；而且推理里常带着未定稿的自我纠错
    /// （"等等，这里算错了"），当结论呈现就是误导。
    /// <para>
    /// <b>它只是投影，不构成执行依据</b>，与 <see cref="TextDelta"/> 同一性质。
    /// 完整推理仍按 <see cref="AgentPropertyKeys.Reasoning"/> 存入历史，
    /// 会话恢复时才拿到全量。
    /// </para>
    /// <para>
    /// <b>是否出现取决于 provider</b>：只有启用思考模式的模型才给推理内容。
    /// 不支持时一个都不会出现，宿主不能把它当作必然信号。
    /// </para>
    /// </remarks>
    /// <param name="Text">本片段的增量文本，不是累计值。</param>
    public sealed record ReasoningDelta(string Text) : AgentEvent;

    /// <summary>聚合完成、即将派发的工具调用。</summary>
    public sealed record ToolCallRequested(string InvocationId, string ToolId, JsonElement Arguments)
        : AgentEvent;

    /// <summary>工具调用的最终状态。每个 ToolCallRequested 必须恰好对应一个该事件。</summary>
    public sealed record ToolCallCompleted(ToolDispatchResult Result) : AgentEvent;

    /// <summary>
    /// 本轮调用的**真实用量**（provider 回报）。每一轮成功调用后发一次。
    /// </summary>
    /// <remarks>
    /// <b>这是账，可以对账。</b>数值直接来自 provider 的 <c>usage</c> 字段，
    /// 不是估算——所以它能用来算成本、比对账单。
    /// <para>
    /// <b>与 <see cref="ContextUsageReported"/> 的区别</b>：那个是"上下文装了多满"，
    /// 这个是"这次调用花了多少"。两者数值不同（前者含缓存命中、后者是计费口径），
    /// 用途也不同。**混用会让成本统计出错**，所以拆成两个事件。
    /// </para>
    /// <para>
    /// <b>不出现的两种情况</b>：provider 没有回报 usage；本轮流式输出中途失败。
    /// 宿主不能把它当作每轮必然发出的信号。
    /// </para>
    /// </remarks>
    /// <param name="InputTokens">输入 token（含缓存命中部分）。未知时 null。</param>
    /// <param name="OutputTokens">输出 token。未知时 null。</param>
    /// <param name="CacheReadTokens">从缓存读取的输入 token。未知时 null。</param>
    /// <param name="CacheWriteTokens">写入缓存的 token。未知时 null。</param>
    /// <param name="TotalTokens">provider 报告的合计。未知时 null。</param>
    public sealed record UsageReported(
        int? InputTokens,
        int? OutputTokens,
        int? CacheReadTokens,
        int? CacheWriteTokens,
        int? TotalTokens) : AgentEvent;

    /// <summary>
    /// 本轮请求前上下文的**估算**构成。**这是估算，不是实测。**
    /// </summary>
    /// <remarks>
    /// <b>为什么必须是 SDK 提供</b>：三项里有两项宿主拿不到——
    /// <list type="bullet">
    /// <item><b>系统提示词</b>：SDK 拼的，宿主看不到最终文本。</item>
    /// <item><b>工具定义</b>：SDK 在构造请求时把 <c>IAgentTool</c> 序列化成 schema，
    /// 宿主手里只有工具对象。要自己算就得重做一遍序列化，而那必然与
    /// 真正发出去的不一致。</item>
    /// <item><b>对话消息</b>：这个宿主能算，但只有 SDK 能把三项凑成总数。</item>
    /// </list>
    /// <para>
    /// <b>名字与文档都必须带"估算"</b>。它的用途是显示"上下文由什么构成"
    /// （见 dsh 的 <c>contextBreakdown</c>，语义相同）。**不要拿它对账**——
    /// 对账用 <see cref="UsageReported"/>。
    /// </para>
    /// <para>
    /// <b>为什么 <see cref="EstimatedTotalTokens"/> 可为 null</b>：
    /// 压缩刚发生、下一次模型响应还没回来时，唯一可信的真实用量是压缩**之前**的，
    /// 那时给一个数只会误导。pi 在同样情形下返回 <c>null</c>
    /// （<c>getContextUsage()</c> 的注释：<i>context token count is unknown until
    /// the next LLM response</i>），这里照做。
    /// </para>
    /// </remarks>
    /// <param name="SystemPromptTokens">最后一个非空系统提示词的估算。</param>
    /// <param name="ToolDefinitionTokens">本次请求工具 schema 的估算。</param>
    /// <param name="MessageTokens">其余可见历史的估算（含被压缩掉的摘要消息）。</param>
    /// <param name="EstimatedTotalTokens">
    /// 三项之和，即"下一个请求大概会发多少"。压缩后尚未获得新用量时为 null。
    /// </param>
    /// <param name="VerifiedInputTokens">
    /// provider 最近一次回报的**真实**输入 token。有它时
    /// <paramref name="EstimatedTotalTokens"/> 以它为基准加上后续增量，
    /// 比纯估算准得多。null 表示还没有任何可信基准。
    /// </param>
    /// <param name="ContextWindowTokens">模型原始上下文窗口，供显示分母。</param>
    public sealed record ContextUsageReported(
        int SystemPromptTokens,
        int ToolDefinitionTokens,
        int MessageTokens,
        int? EstimatedTotalTokens,
        int? VerifiedInputTokens,
        int ContextWindowTokens) : AgentEvent
    {
        /// <summary>
        /// 占上下文窗口的比例（0..1）。总量未知时为 null。
        /// </summary>
        /// <remarks>
        /// <b>分母是窗口不是压缩预算</b>——这一点与 pi 一致
        /// （<c>percent = tokens / contextWindow</c>）。用户看的是"这个模型的窗口
        /// 用了多少"，而压缩预算是内部判据，把它当分母会让百分比虚高。
        /// </remarks>
        public double? WindowRatio => EstimatedTotalTokens is { } total && ContextWindowTokens > 0
            ? (double)total / ContextWindowTokens
            : null;
    }

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
