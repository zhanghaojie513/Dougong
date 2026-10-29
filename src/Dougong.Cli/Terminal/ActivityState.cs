using Dougong.Agent;

namespace Dougong.Cli.Terminal;

/// <summary>
/// 活动行阶段。**就是 <see cref="AgentPhase"/>**，这里只做别名。
/// </summary>
/// <remarks>
/// <b>为什么不各留一份</b>：阶段语义属于 Agent 层的公开契约（自己做宿主的用户
/// 也要用），CLI 只是它的一个消费者。两套并行定义必然漂移——改了一边忘了另一边，
/// 而症状是"CLI 显示正常、SDK 用户拿到的状态不对"，最难看的那种不一致。
/// <para>
/// 保留别名是为了让 CLI 侧代码读起来仍像终端概念，同时只有一处真定义。
/// </para>
/// </remarks>
public enum ActivityPhase
{
    /// <inheritdoc cref="AgentPhase.Idle"/>
    Idle = AgentPhase.Idle,

    /// <inheritdoc cref="AgentPhase.Thinking"/>
    Thinking = AgentPhase.Thinking,

    /// <inheritdoc cref="AgentPhase.Reasoning"/>
    Reasoning = AgentPhase.Reasoning,

    /// <inheritdoc cref="AgentPhase.Working"/>
    Working = AgentPhase.Working,

    /// <inheritdoc cref="AgentPhase.Compacting"/>
    Compacting = AgentPhase.Compacting,

    /// <inheritdoc cref="AgentPhase.Streaming"/>
    Streaming = AgentPhase.Streaming,

    /// <inheritdoc cref="AgentPhase.Failed"/>
    Failed = AgentPhase.Failed,
}

/// <summary>
/// 活动行的状态机。**内部委托给 <see cref="AgentPhaseTracker"/>。**
/// </summary>
/// <remarks>
/// 阶段推导的全部逻辑（去抖、失败停留、事件映射）都在 Agent 层，
/// 那里是公开契约、有独立测试、SDK 用户可直接用。这里只提供终端侧需要
/// 的额外判断（<see cref="ShouldDisplay"/>）和旧的方法名。
/// <para>
/// <b>为什么不再复制一份实现</b>：复制会让两个版本各自演化。
/// 本项目已经吃过一次"展示层自写判定导致与实际行为不一致"的亏。
/// </para>
/// </remarks>
public sealed class ActivityState
{
    private readonly AgentPhaseTracker _tracker = new();

    /// <summary>推理去抖窗口。见 <see cref="AgentPhaseTracker.ReasoningDebounce"/>。</summary>
    public static TimeSpan ReasoningDebounce => AgentPhaseTracker.ReasoningDebounce;

    /// <summary>流式去抖窗口。见 <see cref="AgentPhaseTracker.StreamingDebounce"/>。</summary>
    public static TimeSpan StreamingDebounce => AgentPhaseTracker.StreamingDebounce;

    /// <summary>失败停留时长。见 <see cref="AgentPhaseTracker.FailureLinger"/>。</summary>
    public static TimeSpan FailureLinger => AgentPhaseTracker.FailureLinger;

    /// <summary>当前阶段。</summary>
    public ActivityPhase Phase => (ActivityPhase)(int)_tracker.Phase;

    /// <summary>当前阶段已持续多久。</summary>
    public TimeSpan Elapsed(DateTimeOffset now) => _tracker.PhaseElapsed(now);

    /// <summary>本轮整体已持续多久。</summary>
    public TimeSpan TurnElapsed(DateTimeOffset now) => _tracker.TurnElapsed(now);

    /// <summary>一轮开始：进入思考中。</summary>
    public void OnRunStarted(DateTimeOffset now) => _tracker.StartTurn(now);

    /// <summary>收到推理片段。切到 <see cref="ActivityPhase.Reasoning"/>。</summary>
    public void OnReasoning(DateTimeOffset now) => _tracker.OnReasoning(now);

    /// <summary>收到文本片段。首次 delta 会切到流式中。</summary>
    public void OnTextDelta(DateTimeOffset now) => _tracker.OnTextDelta(now);

    /// <summary>某次工具开始执行。</summary>
    public void OnToolStarted(DateTimeOffset now) => _tracker.OnToolStarted(now);

    /// <summary>某次工具执行结束，回到思考中等下一次模型响应。</summary>
    public void OnToolCompleted(DateTimeOffset now) => _tracker.OnToolCompleted(now);

    /// <summary>上下文压缩。</summary>
    public void OnCompacting(DateTimeOffset now) => _tracker.OnCompacting(now);

    /// <summary>一轮结束。</summary>
    public void OnRunFinished(DateTimeOffset now, bool failed) =>
        _tracker.OnRunFinished(now, failed);

    /// <summary>把一条运行事件交给状态机。**CLI 走这条，不再逐个方法转调。**</summary>
    public void OnEvent(AgentEvent evt, DateTimeOffset now) => _tracker.OnEvent(evt, now);

    /// <summary>让状态随时间推进。**必须周期性调用。**</summary>
    public void Tick(DateTimeOffset now) => _tracker.Tick(now);

    /// <summary>当前是否应该显示活动行。</summary>
    /// <remarks>
    /// 空闲与流式都不显示：空闲时用户在打字，流式时正文本身在动。
    /// <para>
    /// <b><see cref="ActivityPhase.Reasoning"/> 要显示</b>：推理期间屏幕上没有任何
    /// 正文在滚，不显示就等于"看起来卡死"——而这正是本阶段存在的意义。
    /// </para>
    /// </remarks>
    public bool ShouldDisplay =>
        Phase is not (ActivityPhase.Idle or ActivityPhase.Streaming);
}
