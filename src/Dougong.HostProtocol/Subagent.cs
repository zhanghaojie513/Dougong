namespace Dougong.HostProtocol;

// ─────────────────────────────────────────────────────────────
// 子 agent：委派的可见性与生命周期
// ─────────────────────────────────────────────────────────────

/// <summary>
/// 子 agent 的结束方式。
/// </summary>
/// <remarks>
/// <b>与引擎的 <c>SubagentStatus</c> 一一对应但独立定义</b>：本契约包零依赖，
/// 不引用运行时实现。对应关系由宿主层的映射负责，并有检查钉住
/// ——枚举多一个值而映射没跟上，会让那个值在界面上显示成别的东西。
/// </remarks>
public enum SubagentRunState
{
    /// <summary>正在跑。</summary>
    Running,

    /// <summary>正常结束。</summary>
    Completed,

    /// <summary>失败（模型错误、工具反复失败等）。</summary>
    Failed,

    /// <summary>被取消。</summary>
    Cancelled,
}

/// <summary>
/// 一个子 agent 的当前状态。
/// </summary>
/// <remarks>
/// <b>为什么要让宿主看得到子 agent</b>：它消耗的是同一个额度，却不在主对话的
/// 条目流里——模型开了三个子 agent 之后界面一片安静，用户不知道钱花在哪、
/// 也不知道能不能中断。这是"能力在引擎里、界面看不见"的典型缺口。
///
/// <para>
/// <b>不含子 agent 的中间过程</b>：那是它存在的意义（中间过程留在子上下文里，
/// 只回结论）。所以这里只有状态与最终正文，没有它的工具调用流。
/// </para>
/// </remarks>
/// <param name="Id">子 agent 标识。</param>
/// <param name="Label">短语描述，用于列表显示。</param>
/// <param name="State">当前状态。</param>
/// <param name="StartedAt">启动时刻。</param>
/// <param name="ParentTurnId">
/// 由哪一轮发起的。界面据此把它归到对应回合下面，而不是漂在会话级。
/// </param>
/// <param name="ToolRounds">
/// 已跑了几轮工具调用；未知时为 null。供用户判断"这个子 agent 值不值"。
/// </param>
public sealed record SubagentInfo(
    string Id,
    string Label,
    SubagentRunState State,
    DateTimeOffset StartedAt,
    string? ParentTurnId,
    int? ToolRounds);

/// <summary>
/// 子 agent 的最终结果。
/// </summary>
/// <remarks>
/// <b>它只在 <see cref="SubagentRunState"/> 非 <see cref="SubagentRunState.Running"/>
/// 时有意义</b>：正在跑的没有结果可给。失败与取消时
/// <see cref="Text"/> 可能为空，原因在 <see cref="Diagnostic"/>。
/// </remarks>
/// <param name="Id">子 agent 标识。</param>
/// <param name="State">结束方式。</param>
/// <param name="Text">最终正文；失败或取消时可能为空。</param>
/// <param name="Diagnostic">失败原因；成功时为 null。</param>
/// <param name="ToolRounds">它跑了几轮工具调用。</param>
public sealed record SubagentOutcome(
    string Id,
    SubagentRunState State,
    string Text,
    string? Diagnostic,
    int ToolRounds);

/// <summary>
/// 列出某个会话的子 agent。
/// </summary>
/// <remarks>
/// <b>这是只读视图，不是控制面</b>：委派由模型通过 <c>subagent</c> 工具发起，
/// 用户不该能凭空造一个子 agent 出来。用户能做的是"看"与"停"。
/// </remarks>
public sealed record ListSubagentsCommand : ClientCommand
{
    /// <summary>哪个会话。</summary>
    public required string SessionId { get; init; }
}

/// <summary>
/// 取一个子 agent 的结果。
/// </summary>
/// <remarks>
/// <b>这是一次性读取</b>：取走之后该子 agent 从列表消失。引擎里就是这个语义
/// （结果只回一次，没有延续对话）。协议如实传达，不假装它可以反复查——
/// 假装会诱导客户端做"每次刷新都拉一遍"的轮询，那是白耗带宽且会丢结果。
/// <para>
/// 还没跑完时返回 <see cref="SubagentsListedEvent"/> 里该 agent 仍是
/// <see cref="SubagentRunState.Running"/>，且 <see cref="SubagentsListedEvent.Outcome"/>
/// 为 null。
/// </para>
/// </remarks>
public sealed record TakeSubagentResultCommand : ClientCommand
{
    /// <summary>哪个会话。</summary>
    public required string SessionId { get; init; }

    /// <summary>子 agent 标识。</summary>
    public required string SubagentId { get; init; }
}

/// <summary>
/// 停止一个正在跑的子 agent。
/// </summary>
/// <remarks>
/// <b>只是请求，不是立即终止</b>：它可能正在一次工具调用中间，会在那次调用返回后
/// 看到取消。界面必须把"已请求停止"与"已停止"分开显示，否则用户会以为
/// 点了没反应（或以为已经停了而实际上还在烧额度）。
/// </remarks>
public sealed record CancelSubagentCommand : ClientCommand
{
    /// <summary>哪个会话。</summary>
    public required string SessionId { get; init; }

    /// <summary>子 agent 标识。</summary>
    public required string SubagentId { get; init; }

    /// <summary>可选的说明，会记进诊断。</summary>
    public string? Reason { get; init; }
}

/// <summary>
/// 子 agent 列表（查询响应，也是取结果的响应）。
/// </summary>
/// <remarks>
/// <b>一次给全列表，而不是逐条推事件</b>：子 agent 数量少（并发上限默认 4），
/// 一次全量最简单，也不会出现"事件丢了导致列表永久少一个"的残留。
/// 客户端在关注某个子 agent 时可以按自己的节奏重发查询。
/// </remarks>
public sealed record SubagentsListedEvent : ServerEvent
{
    /// <summary>哪个会话。</summary>
    public required string SessionId { get; init; }

    /// <summary>全部子 agent，按启动顺序。</summary>
    public required IReadOnlyList<SubagentInfo> Agents { get; init; }

    /// <summary>
    /// 取结果时的那一条；查询时为 null。
    /// </summary>
    /// <remarks>
    /// <b>取走的语义必须由服务端保证</b>：返回非 null 之后，
    /// 该子 agent 不再出现在后续的 <see cref="Agents"/> 里。
    /// 客户端据"这次有没有 Outcome"判断自己是不是取到了。
    /// </remarks>
    public SubagentOutcome? Outcome { get; init; }

    /// <summary>失败原因（例如 id 不存在）；成功时为 null。</summary>
    public string? Error { get; init; }
}

/// <summary>
/// 一个子 agent 的状态发生了变化。
/// </summary>
/// <remarks>
/// <b>为什么要推事件而不只靠查询</b>：子 agent 跑完可能在一轮中途，
/// 那时主对话还在生成正文。让界面靠轮询发现"它好了"意味着要么太频繁、
/// 要么太慢——而用户的下一步动作（取结果）依赖这个状态。
/// </remarks>
public sealed record SubagentUpdatedEvent : ServerEvent
{
    /// <summary>哪个会话。</summary>
    public required string SessionId { get; init; }

    /// <summary>该子 agent 的最新状态。</summary>
    public required SubagentInfo Agent { get; init; }
}
