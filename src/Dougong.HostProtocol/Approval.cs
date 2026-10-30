namespace Dougong.HostProtocol;

// ─────────────────────────────────────────────────────────────
// 审批：一等公民，不是弹窗附属
// ─────────────────────────────────────────────────────────────

/// <summary>
/// 审批请求。决策粒度不是"允许/拒绝"二元——那样会让用户在两难之间
/// 被迫选"拒绝后重来"。
/// </summary>
/// <param name="RequestId">请求标识，响应带同一个回来配对。</param>
/// <param name="SessionId">所属会话。</param>
/// <param name="TurnId">所属回合。</param>
/// <param name="InvocationId">对应的工具调用。</param>
/// <param name="ToolName">工具名。</param>
/// <param name="Summary">
/// 一句话说明将要做什么。**审批要让人能判断，不是只看工具名**——
/// 只显示"shell"和只显示"将执行 rm -rf build/" 是两种完全不同的知情程度。
/// </param>
/// <param name="ArgumentsJson">完整参数。UI 按键值对展开，坏 JSON 也要原样给出。</param>
/// <param name="PermissionScope">
/// 权限范围描述（例如涉及的路径）。供界面突出显示；它不是授权依据，
/// 授权依据在服务端的策略里。
/// </param>
/// <param name="ExpiresAt">
/// 过期时刻。**界面与引擎必须用同一个超时算出来**，不能各算一套——
/// 两边差几秒就会表现为"点了允许却已经被判过期"。
/// </param>
/// <param name="MayHaveIrreversibleEffects">
/// 是否可能产生不可撤销的副作用。UI 据此提高确认强度。
/// </param>
public sealed record ApprovalRequest(
    string RequestId,
    string SessionId,
    string TurnId,
    string InvocationId,
    string ToolName,
    string Summary,
    string ArgumentsJson,
    string PermissionScope,
    DateTimeOffset ExpiresAt,
    bool MayHaveIrreversibleEffects);

/// <summary>审批决策。四档。</summary>
public enum ApprovalDecision
{
    /// <summary>只允许这一次。</summary>
    AllowOnce,

    /// <summary>
    /// 本次会话内，同样的调用不再询问。
    /// </summary>
    /// <remarks>
    /// <b>作用域键里必须含参数摘要</b>：只按工具名放行等于把"允许读 A 文件"
    /// 变成"允许读任意文件"。这是这套粒度最容易写错的地方。
    /// </remarks>
    AllowForSession,

    /// <summary>拒绝这一次。模型会收到"工具被拒"然后继续下一轮。</summary>
    Deny,

    /// <summary>
    /// 拒绝并中断整个回合。
    /// </summary>
    /// <remarks>
    /// <b>它不是普通拒绝</b>：普通拒绝后模型会继续，这一档要把回合也停掉。
    /// 所以宿主必须真的去取消运行令牌——如果 SDK 的审批契约只有
    /// "批准/拒绝"，表达不了"还要停回合"，那就由宿主补这一动作，
    /// **不能靠模型自己理解**。
    /// </remarks>
    DenyAndStop,
}

/// <summary>对一次审批的回答。</summary>
/// <param name="RequestId">对应哪一次请求。</param>
/// <param name="Decision">决策。</param>
/// <param name="Reason">可选的说明；会进审计日志。</param>
public sealed record ApprovalResponse(string RequestId, ApprovalDecision Decision, string? Reason);

/// <summary>有工具在等审批。</summary>
public sealed record ApprovalRequestedEvent : ServerEvent
{
    /// <summary>完整请求。</summary>
    public required ApprovalRequest Request { get; init; }
}

/// <summary>回答一次审批。</summary>
public sealed record RespondApprovalCommand : ClientCommand
{
    /// <summary>回答内容。</summary>
    public required ApprovalResponse Response { get; init; }
}
