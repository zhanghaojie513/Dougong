namespace Dougong.HostProtocol;

// ─────────────────────────────────────────────────────────────
// 会话条目：Turn → Item（两层；会话在 Session.cs）
// ─────────────────────────────────────────────────────────────

// ConversationItem 的多态根与判别器登记在 Protocol.cs（契约总表），
// 这里只放派生类型。根类型单独放是为了让"总表"完整——
// 避免以后加条目类型时漏登记判别器。

/// <summary>用户输入。</summary>
public sealed record UserMessageItem : ConversationItem
{
    /// <summary>输入文本。</summary>
    public required string Text { get; init; }
}

/// <summary>模型正文。</summary>
public sealed record AssistantMessageItem : ConversationItem
{
    /// <summary>已生成的文本。流式期间逐步增长。</summary>
    public string Text { get; init; } = string.Empty;

    /// <summary>是否仍在流式生成。界面上据此决定要不要显示光标。</summary>
    public bool IsStreaming { get; init; }
}

/// <summary>模型的思考过程。UI 默认折叠——它不是结论，不该抢占注意力。</summary>
/// <remarks>
/// <b>与正文分开的原因</b>：推理里常带未定稿的自我纠错（"等等，这里算错了"）。
/// 并进正文会让用户以为模型已经答完，而那段话可能是它自己后来否掉的。
/// </remarks>
public sealed record ReasoningItem : ConversationItem
{
    /// <summary>已生成的推理文本。</summary>
    public string Text { get; init; } = string.Empty;

    /// <summary>是否仍在流式生成。</summary>
    public bool IsStreaming { get; init; }
}

/// <summary>一次工具调用。输入输出都是原始 JSON 文本，由 UI 决定如何呈现。</summary>
/// <remarks>
/// <b>参数与结果保持原文而不是解析后的结构</b>：不同工具的参数形状不同，
/// 契约不该为它们定义一个联合类型；而且坏 JSON 也要能显示出来——
/// 解析失败时把原文丢掉，用户就看不到模型到底发了什么。
/// </remarks>
public sealed record ToolCallItem : ConversationItem
{
    /// <summary>调用标识，与 SDK 的 <c>ToolInvocation</c> 对应。</summary>
    public required string InvocationId { get; init; }

    /// <summary>工具名。</summary>
    public required string ToolName { get; init; }

    /// <summary>参数原始 JSON。</summary>
    public string ArgumentsJson { get; init; } = "{}";

    /// <summary>结果文本；未完成时为 null。</summary>
    public string? ResultText { get; init; }

    /// <summary>当前状态。</summary>
    public ToolCallStatus Status { get; init; } = ToolCallStatus.Requested;

    /// <summary>失败或异常时的说明。</summary>
    public string? Diagnostic { get; init; }

    /// <summary>
    /// 等待审批的耗时。null 表示这一段没有发生（没配审批器），不是 0。
    /// </summary>
    /// <remarks>
    /// <b>与执行耗时分开</b>：审批等待取决于人多快点确认，执行耗时取决于工具本身多慢，
    /// 两者的优化方向相反。合成一个数会让"这次调用很慢"无法归因。
    ///
    /// <para>
    /// <b>0 与 null 是两件事</b>：0 表示"审批瞬间通过"，null 表示"根本没有这一阶段"。
    /// 用 0 冒充会让统计里混进一批假的"审批很快"。
    /// </para>
    /// </remarks>
    public TimeSpan? ApprovalElapsed { get; init; }

    /// <summary>工具执行的耗时。null 表示没有执行（被拒绝或被取消）。</summary>
    public TimeSpan? ExecutionElapsed { get; init; }

    /// <summary>
    /// 是否可能产生不可撤销的副作用。
    /// </summary>
    /// <remarks>
    /// UI 据此决定风险提示的强度。与审批请求上的同名字段是两处独立信息：
    /// 那个是"批准前告诉用户"的判断，这个是"事后回看"的记录。
    /// </remarks>
    public bool MayHaveIrreversibleEffects { get; init; }
}

/// <summary>工具调用的状态。</summary>
public enum ToolCallStatus
{
    /// <summary>模型请求了，还没开始处理。</summary>
    Requested,

    /// <summary>等待用户审批。</summary>
    AwaitingApproval,

    /// <summary>执行中。</summary>
    Running,

    /// <summary>已执行并成功。</summary>
    Succeeded,

    /// <summary>已执行但工具报告失败。</summary>
    Failed,

    /// <summary>被拒绝，未执行。</summary>
    Rejected,

    /// <summary>被取消。</summary>
    Cancelled,

    /// <summary>结果未知：有开始记录无终态。不得自动重放，必须人工核实。</summary>
    /// <remarks>
    /// <b>不能映成 <see cref="Failed"/></b>：Failed 的含义是"可以重试"，
    /// 而 Unknown 的含义是"可能已执行，必须人工核实"。混同会诱导用户
    /// 重试一个可能已有副作用的调用。
    /// </remarks>
    Unknown,
}

/// <summary>模型声明的计划。全量替换语义，UI 不做增量合并。</summary>
/// <remarks>
/// <b>为什么不做增量合并</b>：模型重新规划时可能去掉某一步。合并会让
/// 那一步永远留在界面上——用户看到一个已经取消的步骤，以为还在做。
/// </remarks>
public sealed record PlanItem : ConversationItem
{
    /// <summary>全部步骤，按顺序。</summary>
    public IReadOnlyList<PlanStep> Steps { get; init; } = [];
}

/// <summary>计划里的一步。</summary>
/// <param name="Content">步骤内容。</param>
/// <param name="Status">状态（由模型自由填写，UI 按关键字映射图标）。</param>
/// <param name="Priority">优先级（同上）。</param>
public sealed record PlanStep(string Content, string Status, string Priority);

/// <summary>系统级提示：压缩发生、结果被裁剪、连接异常等。必须让用户看到。</summary>
/// <remarks>
/// <b>为什么这些必须是消息流里的一条，而不是状态栏闪一下就没了</b>：
/// 上下文压缩意味着"模型看到的不再是完整历史"，那条信息在后续对话里
/// 一直有效。看不到它，用户会误判模型为什么"忘了"前面说的事。
/// </remarks>
public sealed record NoticeItem : ConversationItem
{
    /// <summary>提示文本。</summary>
    public required string Text { get; init; }

    /// <summary>严重程度。</summary>
    public NoticeLevel Level { get; init; } = NoticeLevel.Info;
}

/// <summary>提示的严重程度。</summary>
public enum NoticeLevel
{
    /// <summary>信息。</summary>
    Info,

    /// <summary>可能有问题。</summary>
    Warning,

    /// <summary>已经出问题。</summary>
    Error,
}

// ── 条目级事件 ──────────────────────────────────────────────

/// <summary>新条目。整条发送而不是只发 delta，避免客户端合并状态出错。</summary>
/// <remarks>
/// <b>整条发送的代价是带宽，换的是正确性</b>：delta 合并要求客户端与服务端
/// 对"追加顺序"完全一致，任何一次乱序都会得到一段错位文本，而那种错误
/// 在界面上看起来像"模型说错了话"。
/// </remarks>
public sealed record ItemAddedEvent : ServerEvent
{
    /// <summary>所属会话。</summary>
    public required string SessionId { get; init; }

    /// <summary>所属回合。</summary>
    public required string TurnId { get; init; }

    /// <summary>完整条目。</summary>
    public required ConversationItem Item { get; init; }
}

/// <summary>已有条目更新（流式文本追加、工具状态变化）。</summary>
public sealed record ItemUpdatedEvent : ServerEvent
{
    /// <summary>所属会话。</summary>
    public required string SessionId { get; init; }

    /// <summary>更新后的完整条目（**不是补丁**）。</summary>
    public required ConversationItem Item { get; init; }
}
