namespace Dougong.HostProtocol;

// ─────────────────────────────────────────────────────────────
// 回合：开始、取消、完成
// ─────────────────────────────────────────────────────────────

/// <summary>回合状态。</summary>
public enum TurnStatus
{
    /// <summary>正在运行。</summary>
    Running,

    /// <summary>正常结束。</summary>
    Completed,

    /// <summary>被用户取消。<b>已产生的副作用可能已经发生</b>。</summary>
    Cancelled,

    /// <summary>失败。原因在 <see cref="TurnCompletedEvent.Diagnostic"/> 里。</summary>
    Failed,
}

/// <summary>开始一个回合。</summary>
/// <remarks>
/// <b>历史由服务端从存储重建，客户端不传历史。</b>
/// 让客户端传历史意味着两处都能改历史，而那两处迟早不一致——
/// 不一致的表现是"恢复会话后模型不记得自己说过什么"。
/// 服务端从存储重建是唯一真源。
/// </remarks>
public sealed record StartTurnCommand : ClientCommand
{
    /// <summary>在哪个会话里跑。</summary>
    public required string SessionId { get; init; }

    /// <summary>用户这一轮的输入。</summary>
    public required string Input { get; init; }
}

/// <summary>取消正在跑的回合。</summary>
public sealed record CancelTurnCommand : ClientCommand
{
    /// <summary>要取消的会话。</summary>
    public required string SessionId { get; init; }
}

/// <summary>回合已开始。客户端据此新建一个运行中的回合区块。</summary>
public sealed record TurnStartedEvent : ServerEvent
{
    /// <summary>所属会话。</summary>
    public required string SessionId { get; init; }

    /// <summary>本回合标识。</summary>
    public required string TurnId { get; init; }
}

/// <summary>回合已结束。</summary>
public sealed record TurnCompletedEvent : ServerEvent
{
    /// <summary>所属会话。</summary>
    public required string SessionId { get; init; }

    /// <summary>本回合标识。</summary>
    public required string TurnId { get; init; }

    /// <summary>结束状态。</summary>
    public required TurnStatus Status { get; init; }

    /// <summary>
    /// 失败或取消时的说明。<b>只在非 <see cref="TurnStatus.Completed"/> 时出现</b>：
    /// 成功时给一段空诊断会让界面显示一条无意义的提示。
    /// </summary>
    public string? Diagnostic { get; init; }
}
