using Dougong.Agent;

namespace Dougong.Cli.Terminal;

/// <summary>
/// 把 <see cref="AgentEvent"/> 流转成底部区域的刷新。
/// **只管"喂数据"，重绘全交给 <see cref="LiveRegion"/>。**
/// </summary>
/// <remarks>
/// <b>为什么要一个独立类</b>：事件循环里每加一处 <c>state.OnXxx(...)</c>
/// 就多一个漏改的地方，而且没法单独测。这里把"事件 → 状态"的映射收在一处。
/// <para>
/// <b>为什么需要定时刷新</b>：等待模型时**没有任何事件**，而这段时间占整轮耗时
/// 的大头。没有定时器，活动行的计时会停在 0s、spinner 也不转——看起来像卡死。
/// </para>
/// <para>
/// <b>区域只在轮次进行中出现</b>：用户打字时光标必须留在 <c>&gt; </c> 后面，
/// 而底部区域会把光标带走。所以轮次结束（回到 Idle）就收起区域。
/// </para>
/// </remarks>
public sealed class ActivityReporter : IAsyncDisposable
{
    private static readonly TimeSpan PumpInterval = TimeSpan.FromMilliseconds(100);

    private readonly LiveRegion _region;
    private readonly ActivityState _state = new();
    private readonly string _model;
    private readonly int _contextWindow;
    private readonly Func<int?> _tokensUsed;
    private readonly TimeProvider _clock;

    /// <summary>
    /// 保护 <see cref="_state"/>。泵线程每 100ms 调 <c>Tick</c>，事件循环同时调
    /// <c>OnEvent</c>——两者都会改写 <c>_phase</c> 与几个时间戳。
    /// </summary>
    /// <remarks>
    /// <b>为什么锁在 Reporter 而不是 ActivityState</b>：状态机要保持"纯逻辑、
    /// 无并发设施"，那样才能被确定性测试完整覆盖。并发是宿主的事，
    /// 由调用方加锁。而且这里每次要连续做"推进 + 读快照 + 渲染"三件事，
    /// 分散加锁仍会读到半更新的状态（例如 phase 已变、TurnElapsed 还是旧的）。
    /// </remarks>
    private readonly object _sync = new();

    /// <summary>上一轮的总耗时。轮次结束时记下，供空闲状态栏显示。</summary>
    /// <remarks>
    /// <b>必须自己记，不能事后问状态机</b>：<c>AgentPhaseTracker.OnRunFinished</c>
    /// 在成功结束时会清掉 <c>_turnStart</c>，此后 <c>TurnElapsed</c> 恒为 0
    /// ——那是刻意的语义（"这一轮结束了"），不该为了显示去改它。
    /// 所以在结束前把它抄下来。
    /// </remarks>
    private TimeSpan _lastTurnElapsed;

    private CancellationTokenSource? _turnCts;
    private Task? _pump;

    /// <summary>构造。</summary>
    /// <param name="region">底部区域。可以是不活动的实例（Plain 档）。</param>
    /// <param name="model">状态栏显示的模型名。</param>
    /// <param name="contextWindow">上下文窗口；0 表示不显示占用率。</param>
    /// <param name="tokensUsed">取当前已用 token；返回 null 表示未知。</param>
    /// <param name="clock">时间源，测试可注入。</param>
    public ActivityReporter(
        LiveRegion region,
        string model,
        int contextWindow,
        Func<int?> tokensUsed,
        TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(region);
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(tokensUsed);

        _region = region;
        _model = model;
        _contextWindow = contextWindow;
        _tokensUsed = tokensUsed;
        _clock = clock ?? TimeProvider.System;
    }

    /// <summary>底层状态机，供诊断与测试读取。</summary>
    public ActivityState State => _state;

    /// <summary>
    /// 立刻画一次空闲底栏。
    /// </summary>
    /// <remarks>
    /// <b>为什么需要显式调用</b>：底栏只由两处触发——泵线程（轮次内每 100ms）
    /// 与 <see cref="FinishTurnAsync"/>（轮次结束）。而**启动到第一次输入之间
    /// 没有任何轮次**，于是那段时间底部是空的。
    ///
    /// 真机验证时看到的正是这个：banner 之后一大片空白，直到首次提问才出现
    /// 状态栏。而 Hermes 的空闲状态栏是**从启动第一刻就在的**。
    /// </remarks>
    public void ShowIdle()
    {
        if (!_region.IsActive) return;

        lock (_sync) Render();
    }

    /// <summary>开始一轮：进入思考中并启动刷新循环。</summary>
    public void StartTurn()
    {
        if (!_region.IsActive) return;

        lock (_sync) _state.OnRunStarted(_clock.GetUtcNow());

        // 上一轮的循环若还活着（理论上不该发生），先掐掉。
        _turnCts?.Cancel();
        _turnCts?.Dispose();
        _turnCts = new CancellationTokenSource();
        _pump = PumpAsync(_turnCts.Token);
    }

    /// <summary>把一条代理事件喂给状态机。</summary>
    public void OnEvent(AgentEvent evt)
    {
        ArgumentNullException.ThrowIfNull(evt);
        if (!_region.IsActive) return;

        var now = _clock.GetUtcNow();

        lock (_sync)
        {
            switch (evt)
            {
                case AgentEvent.RunStarted:
                    _state.OnRunStarted(now);
                    break;

                case AgentEvent.TextDelta:
                    _state.OnTextDelta(now);
                    break;

                case AgentEvent.ReasoningDelta:
                    // 推理阶段维持"思考中"，不切到 Streaming：
                    //   - 推理不是回答，用"输出中"的状态是错的
                    //   - 但它确实是"活着"的证据，所以要刷新计时基准，
                    //     否则长时间推理会被 Tick 判成停滞
                    _state.OnReasoning(now);
                    break;

                case AgentEvent.ToolCallRequested:
                    _state.OnToolStarted(now);
                    break;

                case AgentEvent.ToolCallCompleted:
                    _state.OnToolCompleted(now);
                    break;

                case AgentEvent.ContextCompacted:
                    _state.OnCompacting(now);
                    break;

                // 重试等待期间没有别的输出，状态行必须反映它——
                // 否则转圈动画看起来只是"慢"，用户不知道在等重试。
                case AgentEvent.RetryScheduled retry:
                    _state.OnRetrying(now, retry.Attempt, retry.MaxAttempts);
                    break;

                case AgentEvent.RunFinished finished:
                    _state.OnRunFinished(now, finished.Outcome != AgentRunOutcome.Completed);
                    break;
            }

            // 事件到来立刻刷一次，状态切换即时可见（定时器最多再补 100ms）。
            Render();
        }
    }

    /// <summary>
    /// 结束一轮：让失败状态停留一会儿，然后收起区域。
    /// </summary>
    /// <remarks>
    /// <b>不能等刷新循环自己退</b>：循环的退出条件是"回到空闲"，而取消或异常
    /// 结束时状态机停在 Working/Thinking，永远到不了空闲——直接 await 会挂死。
    /// 所以这里主动取消，只在"失败停留"这一种情况下先等一下。
    /// </remarks>
    public async Task FinishTurnAsync()
    {
        // 失败提示要留够时间让人看见，这是唯一值得等的情况。
        bool failed;
        lock (_sync) failed = _state.Phase == ActivityPhase.Failed;

        if (failed)
            await Task.Delay(ActivityState.FailureLinger, _clock).ConfigureAwait(false);

        _turnCts?.Cancel();

        var pump = _pump;
        if (pump is not null)
        {
            try
            {
                await pump.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // 取消是正常路径。
            }
        }

        _pump = null;
        _turnCts?.Dispose();
        _turnCts = null;

        // **把状态机真正推回空闲**，并在它清掉计时之前抄下本轮总时长。
        //
        // 原来这里只取消循环，然后直接 Render()——而循环的退出条件才是
        // "回到空闲"，取消它并不改变 `_state` 的阶段。于是轮次结束后
        // `Render()` 画的仍是"思考中"，活动行留着不收。
        //
        // 合并成单行版式时看不出来（忙闲共用一格，内容差不多）；
        // 拆成两行之后活动行就明晃晃地挂在输入行上方不消失——
        // 用户会以为它还在跑。必须显式收一轮。
        lock (_sync)
        {
            if (_state.Phase is not (ActivityPhase.Idle or ActivityPhase.Failed))
            {
                _lastTurnElapsed = _state.TurnElapsed(_clock.GetUtcNow());
                _state.OnRunFinished(_clock.GetUtcNow(), failed: false);
            }
        }

        // **不清空区域，改画空闲状态行**。
        //
        // 原来这里是 `_region.Clear()`，于是轮次一结束底部整块消失——
        // 用户看到的就是"底部空荡"。而 Hermes 的空闲状态行是**常驻**的
        // （`ui.statusBar` 默认 `'top'`，只有显式配 `'off'` 才消失）。
        //
        // 常驻的收益是实打实的：不用 `/status` 也能一直看到模型名与上下文占用。
        Render();
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        _turnCts?.Cancel();

        var pump = _pump;
        if (pump is not null)
        {
            try
            {
                await pump.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // 正常退出路径。
            }
        }

        _turnCts?.Dispose();
        _turnCts = null;
        _pump = null;
    }

    /// <summary>刷新循环：每 100ms 推进状态并重画，回到空闲即退出。</summary>
    private async Task PumpAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(PumpInterval);

        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
        {
            bool idle;
            lock (_sync)
            {
                _state.Tick(_clock.GetUtcNow());
                Render();
                idle = _state.Phase == ActivityPhase.Idle;
            }

            if (idle) return;
        }
    }

    /// <summary>
    /// 按当前状态重画底部区域。
    /// </summary>
    /// <remarks>
    /// <b>两个槽位分开喂</b>（用户 2026-11-05 的版式要求）：
    /// <code>
    ///   活动行   ← 仅忙时有内容，闲时留空
    ///   输入行   ← 常驻，由 LiveRegion 管
    ///   状态栏   ← 常驻，贴最底
    /// </code>
    /// P4 阶段把活动行与状态栏合并成一行贴底，于是"正在忙"这件事跑到了
    /// 用户视线下方，比输入行还低——那正是用户报的问题。
    ///
    /// <para>
    /// <b>流式期间仍然收起活动行</b>：正文在逐字滚动，区域留着只会多一次
    /// 擦画开销，而"还活着"这件事正文本身已经证明了（方案 §3.8.8）。
    /// 但<b>状态栏照旧常驻</b>——它是参考信息，与忙闲无关。
    /// </para>
    ///
    /// <para>
    /// <b>耗时只在一个地方显示</b>：忙时归活动行（"这一阶段多久"），
    /// 空闲时归状态栏（"上一轮多久"）。两处都显示同一件事只会让人
    /// 怀疑它们不一致；都不显示则用户看不到本轮用了多久。
    /// </para>
    /// </remarks>
    private void Render()
    {
        var now = _clock.GetUtcNow();

        var busy = _state.Phase is not (ActivityPhase.Idle or ActivityPhase.Streaming);

        // 流式期间不画活动行：正文自己在滚，那是更好的"活着"证据。
        _region.SetActivity(
            _state.Phase is ActivityPhase.Streaming
                ? null
                : StatusLine.RenderActivity(_state, now, _region.Width));

        // 状态栏与忙闲无关，始终在。
        // 耗时只在空闲时由它补上——忙时活动行已经在报。
        // 空闲时用 `_lastTurnElapsed`：状态机在轮末已把计时清零（见那个字段的注释）。
        _region.SetStatus(
            StatusLine.RenderStatus(
                _model,
                _tokensUsed(),
                _contextWindow,
                _region.Width,
                idleTurnElapsed: busy ? null : _lastTurnElapsed));
    }
}
