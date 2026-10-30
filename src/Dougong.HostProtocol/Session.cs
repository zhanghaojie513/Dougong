namespace Dougong.HostProtocol;

// ─────────────────────────────────────────────────────────────
// 会话：摘要、快照，以及会话级命令
// ─────────────────────────────────────────────────────────────

/// <summary>
/// 会话摘要，用于侧栏列表。
/// </summary>
/// <remarks>
/// <b>权威状态由服务端持有，客户端只做投影。</b>
/// 让客户端也维护一份会话状态意味着两处都能改，而那两处迟早不一致——
/// 不一致的表现是"重启后侧栏标题变了"这类难以复现的怪事。
/// </remarks>
/// <param name="Id">会话标识。</param>
/// <param name="Title">显示标题。</param>
/// <param name="Workspace">所属工作区。</param>
/// <param name="UpdatedAt">最后更新时间。</param>
/// <param name="ItemCount">条目数，供列表显示规模。</param>
/// <param name="HasPendingApproval">
/// 是否有未处理的审批。<b>列表要能显示它</b>：审批等着的会话在后台静默发呆，
/// 用户切走之后完全不知道那边卡住了。
/// </param>
public sealed record SessionSummary(
    string Id,
    string Title,
    string Workspace,
    DateTimeOffset UpdatedAt,
    int ItemCount,
    bool HasPendingApproval);

/// <summary>
/// 一个会话的完整快照：附着时用它重建界面，之后只应用增量。
/// </summary>
/// <param name="Id">会话标识。</param>
/// <param name="Title">显示标题。</param>
/// <param name="Workspace">所属工作区。</param>
/// <param name="Sequence">
/// 快照对应的服务端序号。**增量事件的序号必须严格大于它**——
/// 否则客户端会把已经包含在快照里的事件再应用一次，表现为条目重复。
/// </param>
/// <param name="Turns">全部回合，按时间顺序。</param>
/// <param name="IsRunning">是否正在跑回合。</param>
/// <param name="ModelName">
/// 当前使用的模型。null 表示未知或未配置。
/// <b>必须在快照里给</b>：多 provider 下"同一句话发给不同模型结果差异很大"，
/// 用户排查回答质量时第一件要确认的就是这个。
/// </param>
public sealed record SessionSnapshot(
    string Id,
    string Title,
    string Workspace,
    long Sequence,
    IReadOnlyList<TurnSnapshot> Turns,
    bool IsRunning,
    string? ModelName);

/// <summary>一个回合的快照。</summary>
/// <param name="Id">回合标识。</param>
/// <param name="StartedAt">开始时刻。</param>
/// <param name="Status">当前状态。</param>
/// <param name="Items">本回合的全部条目。</param>
public sealed record TurnSnapshot(
    string Id,
    DateTimeOffset StartedAt,
    TurnStatus Status,
    IReadOnlyList<ConversationItem> Items);

/// <summary>会话列表。响应 <see cref="ListSessionsCommand"/>。</summary>
public sealed record SessionListedEvent : ServerEvent
{
    /// <summary>全部会话摘要，按更新时间倒序。</summary>
    public required IReadOnlyList<SessionSummary> Sessions { get; init; }
}

/// <summary>会话已打开。响应 <see cref="OpenSessionCommand"/> 与 <see cref="ResyncCommand"/>。</summary>
public sealed record SessionOpenedEvent : ServerEvent
{
    /// <summary>全量快照。</summary>
    public required SessionSnapshot Snapshot { get; init; }
}

/// <summary>列出全部会话。</summary>
public sealed record ListSessionsCommand : ClientCommand;

/// <summary>新建会话。</summary>
public sealed record CreateSessionCommand : ClientCommand
{
    /// <summary>工作区路径。</summary>
    public required string Workspace { get; init; }

    /// <summary>标题；null 表示由服务端生成。</summary>
    public string? Title { get; init; }
}

/// <summary>打开一个已有会话，返回全量快照。</summary>
public sealed record OpenSessionCommand : ClientCommand
{
    /// <summary>要打开的会话。</summary>
    public required string SessionId { get; init; }
}

/// <summary>
/// 请求重新同步：客户端发现事件序号缺口后，用这条命令要一份当前状态。
/// </summary>
/// <remarks>
/// <b>为什么需要单独一条命令，而不是让客户端"等下一个事件"</b>：缺口意味着
/// 中间有事件丢了，丢的可能正是"回合结束"或"工具完成"。等下去会一直停在
/// 错误状态——界面上那个工具永远转圈。
///
/// <para>
/// <b>为什么要 <see cref="SessionId"/></b>：重同步的目标是"把某个会话的界面
/// 状态拉回正确"。只回一个全局序号没有意义——用户看的是会话内容。
/// </para>
///
/// <para>
/// <b>它应当复用 <c>session/open</c> 的响应路径</b>：宿主把重同步实现成
/// "再开一次会话"，回一份带当前序号的全量快照。两条路径共用同一份快照构造代码，
/// 避免字段不一致——那种不一致只在丢事件之后才暴露。
/// </para>
/// </remarks>
public sealed record ResyncCommand : ClientCommand
{
    /// <summary>要重同步的会话。null 表示只需要一个基准序号（比如还没打开会话时）。</summary>
    public string? SessionId { get; init; }
}
