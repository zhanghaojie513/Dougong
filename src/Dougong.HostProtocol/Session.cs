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

// ─────────────────────────────────────────────────────────────
// 模型目录：多 provider 场景下，UI 要能列出"有哪些模型可选"
// ─────────────────────────────────────────────────────────────

/// <summary>
/// 一个可选模型的描述。
/// </summary>
/// <remarks>
/// <b>为什么由服务端提供而不是客户端自己查</b>：模型能力表在 SDK 里
/// （<c>ProviderCatalog</c>），而界面的层通常不该引用 SDK。
/// 经协议传纯数据既守住了分层，又让 UI 不必知道 SDK 的类型。
/// <para>
/// <b>只传界面需要的字段</b>：上下文窗口、输出上限、是否支持思考。
/// 价格与营销文案不传——SDK 里也没有，编造会让用户看到假信息。
/// </para>
/// </remarks>
/// <param name="ProviderId">所属 provider。</param>
/// <param name="Id">模型标识。</param>
/// <param name="ContextWindow">上下文窗口（token）。</param>
/// <param name="MaxOutputTokens">输出容量上限（token）。</param>
/// <param name="SupportsReasoning">是否支持思考模式。</param>
public sealed record ModelInfo(
    string ProviderId,
    string Id,
    int ContextWindow,
    int MaxOutputTokens,
    bool SupportsReasoning);

/// <summary>模型目录。响应 <see cref="ListModelsCommand"/>。</summary>
/// <remarks>
/// <b>载荷曾经只有 <see cref="Models"/></b>，而那是残缺的：客户端拿不到
/// "哪些 provider 可用"，也标不出"现在用的是哪个"。这两个缺口不是界面偏好——
/// 一个 provider 当前没有任何可用模型时（凭据未配、目录为空），
/// 从 <see cref="Models"/> 反推就完全看不到它，用户会以为那个 provider 不存在。
///
/// <para>
/// <b>为什么之前没发现</b>：这个事件在 SDK 里只登记过判别器，
/// 从未被构造或消费——有类型不等于被验证过。
/// </para>
/// </remarks>
public sealed record ModelsListedEvent : ServerEvent
{
    /// <summary>全部可选模型。</summary>
    public required IReadOnlyList<ModelInfo> Models { get; init; }

    /// <summary>
    /// 已知的 provider 标识，按字母序。**可能包含没有模型的 provider**。
    /// </summary>
    /// <remarks>
    /// 单独给出而不是让客户端从 <see cref="Models"/> 去重：
    /// 一个 provider 可能因为凭据未配、或目录暂时为空而没有模型，
    /// 但它仍然应该出现在"选一个 provider"的下拉里。
    /// <para>
    /// <b>必需而不是可空</b>：可空会让"服务端真的没有 provider"与
    /// "这条消息来自旧版服务端、它不知道该字段"混在一起，
    /// 而客户端无法区分。0.x 不承诺跨版本兼容（<c>compat-policy.md</c> §7.1），
    /// 所以这里选择让旧服务端明确失败，而不是让它发一条语义含糊的消息。
    /// </para>
    /// </remarks>
    public required IReadOnlyList<string> Providers { get; init; }

    /// <summary>
    /// 当前实际在用的模型 Id；null 表示**未配置**（这是一个有效状态）。
    /// </summary>
    /// <remarks>
    /// 这里的 null 有确切含义（用户还没选模型），与"服务端不知道该字段"不同。
    /// 前者是正常的初始状态，界面应显示"未选择"。
    /// </remarks>
    public string? CurrentModelId { get; init; }
}

/// <summary>列出当前可选的模型。</summary>
/// <remarks>
/// <b>不需要参数</b>：模型目录由服务端的 provider 配置决定，
/// 客户端没有可以筛选的维度。要按 provider 过滤是界面的显示决策，
/// 不该变成协议参数。
/// </remarks>
public sealed record ListModelsCommand : ClientCommand;
