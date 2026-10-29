namespace Dougong.Cli.Terminal;

/// <summary>
/// 活动行当前处于哪个阶段。**这决定显示哪个面孔与文案。**
/// </summary>
/// <remarks>
/// <b>为什么需要显式状态机</b>：<see cref="Dougong.Agent.AgentEvent"/> 里
/// **没有"正在等待模型"这种事件**。实际事件只有：
/// <code>
/// RunStarted → [等待模型：完全静默] → TextDelta / ToolCallRequested
///            → ToolCallCompleted → ... → RunFinished
/// </code>
/// 而"等待模型"占整轮耗时的大头。所以状态必须靠**事件 + 计时**混合驱动，
/// 不能只靠事件。
/// </remarks>
public enum ActivityPhase
{
    /// <summary>空闲：等待用户输入。此时整块隐藏（方案 §3.8.9）。</summary>
    Idle,

    /// <summary>等待模型响应。没有事件、耗时最长，靠 RunStarted 进入。</summary>
    Thinking,

    /// <summary>执行工具中。</summary>
    Working,

    /// <summary>压缩历史中。</summary>
    Compacting,

    /// <summary>模型正在流式输出文本。</summary>
    Streaming,

    /// <summary>已中断或失败，短暂显示后隐藏。</summary>
    Failed,
}

/// <summary>
/// 活动行的状态机。纯逻辑，不碰终端——因此可以完整测试。
/// </summary>
/// <remarks>
/// <b>流式输出的去抖</b>：<see cref="ActivityPhase.Streaming"/> 不是收到第一个
/// <c>TextDelta</c> 就进入。流式文本是逐字到达的，每个 delta 都切状态会让
/// 面孔疯狂闪烁。判据是"最近 <see cref="StreamingDebounce"/> 内有过 delta"。
/// <para>
/// <b>为什么 Streaming 期间不显示面孔</b>：正文在滚，用户已经能看出它活着。
/// 再挂一个面孔是多余的，而且会和正文抢视觉。
/// </para>
/// </remarks>
public sealed class ActivityState
{
    /// <summary>流式判定窗口：距上一次 delta 不超过这个时长算"正在流式"。</summary>
    public static readonly TimeSpan StreamingDebounce = TimeSpan.FromMilliseconds(300);

    /// <summary>失败状态显示多久后自动隐藏。</summary>
    public static readonly TimeSpan FailureLinger = TimeSpan.FromSeconds(2);

    private ActivityPhase _phase = ActivityPhase.Idle;
    private DateTimeOffset _phaseSince = DateTimeOffset.MinValue;
    private DateTimeOffset _lastDeltaAt = DateTimeOffset.MinValue;
    private DateTimeOffset _failedAt = DateTimeOffset.MinValue;

    /// <summary>当前阶段。</summary>
    public ActivityPhase Phase => _phase;

    /// <summary>当前阶段已持续多久。</summary>
    public TimeSpan Elapsed(DateTimeOffset now) =>
        _phase is ActivityPhase.Idle ? TimeSpan.Zero : now - _phaseSince;

    /// <summary>本轮整体已持续多久，从 <see cref="OnRunStarted"/> 起算。</summary>
    public TimeSpan TurnElapsed(DateTimeOffset now) =>
        _turnStart == DateTimeOffset.MinValue ? TimeSpan.Zero : now - _turnStart;

    private DateTimeOffset _turnStart = DateTimeOffset.MinValue;

    /// <summary>一轮开始：进入思考中。</summary>
    public void OnRunStarted(DateTimeOffset now)
    {
        _turnStart = now;
        Enter(ActivityPhase.Thinking, now);
    }

    /// <summary>收到文本片段。首次 delta 会从思考中切到流式中。</summary>
    public void OnTextDelta(DateTimeOffset now)
    {
        _lastDeltaAt = now;
        if (_phase is not ActivityPhase.Streaming)
            Enter(ActivityPhase.Streaming, now);
    }

    /// <summary>
    /// 收到推理片段。**维持"思考中"，不切到流式。**
    /// </summary>
    /// <remarks>
    /// 推理不是回答，用"输出中"的状态描述它是错的。但它确实是"还在干活"的
    /// 证据，所以要刷新阶段起点——否则长时间推理会让耗时一直涨、
    /// 看起来像卡在同一个阶段。
    /// <para>
    /// 只有 provider 启用思考模式时才会收到这个事件，未启用时行为与之前完全一致。
    /// </para>
    /// </remarks>
    public void OnReasoning(DateTimeOffset now)
    {
        // 已经在流式输出正文时不回退到思考中：正文优先。
        if (_phase is ActivityPhase.Streaming) return;

        _lastDeltaAt = now;
        Enter(ActivityPhase.Thinking, now);
    }

    /// <summary>某次工具开始执行。</summary>
    public void OnToolStarted(DateTimeOffset now) => Enter(ActivityPhase.Working, now);

    /// <summary>某次工具执行结束，回到思考中等下一次模型响应。</summary>
    public void OnToolCompleted(DateTimeOffset now) => Enter(ActivityPhase.Thinking, now);

    /// <summary>上下文压缩。</summary>
    public void OnCompacting(DateTimeOffset now) => Enter(ActivityPhase.Compacting, now);

    /// <summary>一轮结束。失败时短暂停留在 <see cref="ActivityPhase.Failed"/>。</summary>
    public void OnRunFinished(DateTimeOffset now, bool failed)
    {
        if (failed)
        {
            _failedAt = now;
            Enter(ActivityPhase.Failed, now);
            return;
        }

        Enter(ActivityPhase.Idle, now);
        _turnStart = DateTimeOffset.MinValue;
    }

    /// <summary>
    /// 让状态随时间推进。**必须周期性调用**，否则流式判定与失败隐藏都不会发生。
    /// </summary>
    /// <remarks>
    /// 两件事只能靠时间推进，事件驱动做不到：
    /// <list type="number">
    /// <item>流式无新 delta 超过 <see cref="StreamingDebounce"/> → 回到思考中</item>
    /// <item>失败超过 <see cref="FailureLinger"/> → 回到空闲</item>
    /// </list>
    /// </remarks>
    public void Tick(DateTimeOffset now)
    {
        switch (_phase)
        {
            case ActivityPhase.Streaming when now - _lastDeltaAt > StreamingDebounce:
                // 流式停了但本轮还没结束：说明模型在准备下一次响应或工具调用。
                Enter(ActivityPhase.Thinking, now);
                break;

            case ActivityPhase.Failed when now - _failedAt > FailureLinger:
                Enter(ActivityPhase.Idle, now);
                _turnStart = DateTimeOffset.MinValue;
                break;
        }
    }

    /// <summary>当前是否应该显示活动行。</summary>
    /// <remarks>
    /// 空闲与流式都不显示：空闲时用户在打字，流式时正文本身在动。
    /// </remarks>
    public bool ShouldDisplay => _phase is not (ActivityPhase.Idle or ActivityPhase.Streaming);

    private void Enter(ActivityPhase phase, DateTimeOffset now)
    {
        if (_phase == phase) return;
        _phase = phase;
        _phaseSince = now;
    }
}
