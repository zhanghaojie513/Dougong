namespace Dougong.Agent;

/// <summary>
/// 由 <see cref="AgentEvent"/> 流 + 时间推进推导出 <see cref="AgentPhase"/>。
/// **纯逻辑，不碰任何 UI。**
/// </summary>
/// <remarks>
/// <b>为什么需要它、而不是让宿主自己从事件推断</b>：两件事做不到纯事件驱动。
/// <list type="number">
/// <item><b>"等待模型"没有任何事件</b>。<see cref="AgentEvent.RunStarted"/> 之后
/// 可能几十秒静默，这期间宿主无从知道"是在等，还是卡死了"。</item>
/// <item><b>推理结束也没有事件</b>。推理是逐段来的，停了之后要么开始输出正文，
/// 要么开始调工具；中间的空档只能靠时间去抖。</item>
/// </list>
/// 所以必须有一个"事件喂进来 + 定时 <see cref="Tick"/>"的状态机。
/// 各宿主自己写会各错一遍，这里给一份经过测试的。
/// <para>
/// <b>线程安全</b>：本类**不加锁**。它假定调用方串行调用（事件循环在一个线程、
/// 定时器回调串行进同一个地方）。需要并发时由调用方加锁——把并发策略塞进
/// 状态机会让"纯逻辑可测"这条优点消失。CLI 的做法见 <c>ActivityReporter</c>。
/// </para>
/// </remarks>
public sealed class AgentPhaseTracker
{
    /// <summary>
    /// 推理停止多久后回落到 <see cref="AgentPhase.Thinking"/>。
    /// </summary>
    /// <remarks>
    /// 太短：推理是逐段到达的，段间空隙会让阶段反复跳；
    /// 太长：模型已经转去调工具了，界面还写着"正在推理"。
    /// 300ms 是实测下来既能盖住段间空隙、又不会明显滞后的值。
    /// </remarks>
    public static readonly TimeSpan ReasoningDebounce = TimeSpan.FromMilliseconds(300);

    /// <summary>流式正文停止多久后回落到 <see cref="AgentPhase.Thinking"/>。</summary>
    public static readonly TimeSpan StreamingDebounce = TimeSpan.FromMilliseconds(300);

    /// <summary>失败提示停留多久后回到 <see cref="AgentPhase.Idle"/>。</summary>
    public static readonly TimeSpan FailureLinger = TimeSpan.FromSeconds(2);

    private AgentPhase _phase = AgentPhase.Idle;
    private DateTimeOffset _phaseSince = DateTimeOffset.MinValue;
    private DateTimeOffset _turnStart = DateTimeOffset.MinValue;
    private DateTimeOffset _lastDeltaAt = DateTimeOffset.MinValue;
    private DateTimeOffset _failedAt = DateTimeOffset.MinValue;

    /// <summary>当前阶段。</summary>
    public AgentPhase Phase => _phase;

    /// <summary>当前阶段已持续多久；<see cref="AgentPhase.Idle"/> 时为零。</summary>
    public TimeSpan PhaseElapsed(DateTimeOffset now) =>
        _phase is AgentPhase.Idle ? TimeSpan.Zero : now - _phaseSince;

    /// <summary>本轮整体已持续多久；未开始或已结束时为零。</summary>
    public TimeSpan TurnElapsed(DateTimeOffset now) =>
        _turnStart == DateTimeOffset.MinValue ? TimeSpan.Zero : now - _turnStart;

    /// <summary>交给一条事件。</summary>
    public void OnEvent(AgentEvent evt, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(evt);

        switch (evt)
        {
            case AgentEvent.RunStarted: StartTurn(now); break;
            case AgentEvent.ReasoningDelta: OnReasoning(now); break;
            case AgentEvent.TextDelta: OnTextDelta(now); break;
            case AgentEvent.ToolCallRequested: OnToolStarted(now); break;
            case AgentEvent.ToolCallCompleted: OnToolCompleted(now); break;
            case AgentEvent.ContextCompacted: OnCompacting(now); break;
            case AgentEvent.RunFinished finished:
                OnRunFinished(now, finished.Outcome != AgentRunOutcome.Completed);
                break;
        }
    }

    /// <summary>显式开始一轮（在收到 <see cref="AgentEvent.RunStarted"/> 之前调用）。</summary>
    /// <remarks>
    /// <b>这是必要的</b>：等第一个事件到来才进入 <see cref="AgentPhase.Thinking"/>，
    /// 会让整轮最长的静默段完全没有指示。真实运行器会先发 RunStarted，
    /// 但宿主可能在拉取事件流之前就要显示"已经开始了"。
    /// </remarks>
    public void StartTurn(DateTimeOffset now)
    {
        _turnStart = now;
        Enter(AgentPhase.Thinking, now);
    }

    /// <summary>收到推理片段：进入 <see cref="AgentPhase.Reasoning"/>。</summary>
    /// <remarks>
    /// 与 <see cref="OnEvent"/> 等价，但不需要构造事件对象。
    /// 宿主若已有事件流，直接用 <c>OnEvent</c> 更方便；只做 UI 透传时用这个。
    /// </remarks>
    public void OnReasoning(DateTimeOffset now)
    {
        if (_phase is AgentPhase.Streaming) return;
        _lastDeltaAt = now;
        Enter(AgentPhase.Reasoning, now);
    }

    /// <summary>收到正文片段：进入 <see cref="AgentPhase.Streaming"/>。</summary>
    public void OnTextDelta(DateTimeOffset now)
    {
        _lastDeltaAt = now;
        Enter(AgentPhase.Streaming, now);
    }

    /// <summary>工具开始执行。</summary>
    public void OnToolStarted(DateTimeOffset now) => Enter(AgentPhase.Working, now);

    /// <summary>工具执行结束，回到思考中。</summary>
    public void OnToolCompleted(DateTimeOffset now) => Enter(AgentPhase.Thinking, now);

    /// <summary>上下文压缩。</summary>
    public void OnCompacting(DateTimeOffset now) => Enter(AgentPhase.Compacting, now);

    /// <summary>一轮结束。</summary>
    public void OnRunFinished(DateTimeOffset now, bool failed)
    {
        if (!failed)
        {
            Enter(AgentPhase.Idle, now);
            _turnStart = DateTimeOffset.MinValue;
            return;
        }

        _failedAt = now;
        Enter(AgentPhase.Failed, now);
    }

    /// <summary>
    /// 让阶段随时间推进。**必须周期性调用**（建议 100ms 一次）。
    /// </summary>
    /// <remarks>
    /// 三件事只能靠时间推进，事件驱动做不到：
    /// <list type="number">
    /// <item>推理停止超过 <see cref="ReasoningDebounce"/> → 回到思考中</item>
    /// <item>流式停止超过 <see cref="StreamingDebounce"/> → 回到思考中</item>
    /// <item>失败超过 <see cref="FailureLinger"/> → 回到空闲</item>
    /// </list>
    /// 不调用 `Tick` 的后果不是"界面不刷新"，而是**阶段永远停在最后一个事件上**——
    /// 例如推理结束后一直显示"正在推理"。
    /// </remarks>
    public void Tick(DateTimeOffset now)
    {
        switch (_phase)
        {
            case AgentPhase.Reasoning when now - _lastDeltaAt > ReasoningDebounce:
                Enter(AgentPhase.Thinking, now);
                break;

            case AgentPhase.Streaming when now - _lastDeltaAt > StreamingDebounce:
                Enter(AgentPhase.Thinking, now);
                break;

            case AgentPhase.Failed when now - _failedAt > FailureLinger:
                Enter(AgentPhase.Idle, now);
                _turnStart = DateTimeOffset.MinValue;
                break;
        }
    }

    private void Enter(AgentPhase phase, DateTimeOffset now)
    {
        if (_phase == phase) return;
        _phase = phase;
        _phaseSince = now;
    }
}
