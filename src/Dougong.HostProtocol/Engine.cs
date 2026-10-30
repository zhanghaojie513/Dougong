namespace Dougong.HostProtocol;

// ─────────────────────────────────────────────────────────────
// 引擎：健康状态（不属于任何会话）
// ─────────────────────────────────────────────────────────────

/// <summary>
/// 引擎健康状态。用于状态栏，**不属于任何会话**。
/// </summary>
/// <remarks>
/// <b>为什么带 <see cref="ModelName"/></b>：多 provider 场景下，同一句话发给
/// 不同模型的结果差异很大。用户排查"回答怎么变差了"时，第一件要确认的事
/// 就是"现在到底连的哪个模型"。让他去翻配置文件才能看到，等于没有这个信息。
///
/// <para>
/// <b>状态名与失败原因分开</b>：<see cref="State"/> 是机器可判的四档，
/// <see cref="Detail"/> 是给人看的原因。混成一个字符串会让界面无法按状态分色。
/// </para>
/// </remarks>
public sealed record EngineStatusEvent : ServerEvent
{
    /// <summary>机器可判的状态。</summary>
    public required EngineState State { get; init; }

    /// <summary>当前模型名；null 表示未配置或未知。</summary>
    public string? ModelName { get; init; }

    /// <summary>给人看的原因说明；正常时为 null。</summary>
    public string? Detail { get; init; }
}

/// <summary>引擎状态。</summary>
public enum EngineState
{
    /// <summary>正在启动。</summary>
    Starting,

    /// <summary>就绪，可以接回合。</summary>
    Ready,

    /// <summary>忙（正在跑回合）。<b>不等于不可用</b>，只是不接新的。</summary>
    Busy,

    /// <summary>失败。原因在 <see cref="EngineStatusEvent.Detail"/> 里。</summary>
    Failed,
}
