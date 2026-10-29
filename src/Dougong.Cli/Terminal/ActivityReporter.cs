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
        _region.Clear();
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

    /// <summary>按当前状态重画底部区域。</summary>
    /// <remarks>
    /// <b>流式期间整块收起</b>：正文在逐字滚动，区域留着只会多一次擦画开销，
    /// 而"还活着"这件事正文本身已经证明了（方案 §3.8.8）。收起之后
    /// <see cref="LiveRegion.RunExclusive"/> 里没有区域要擦要画，
    /// 流式路径上没有任何多余写操作。
    /// </remarks>
    private void Render()
    {
        if (_state.Phase is ActivityPhase.Streaming or ActivityPhase.Idle)
        {
            _region.SetLines([]);
            return;
        }

        var now = _clock.GetUtcNow();
        var lines = new List<string>(2);

        if (StatusLine.RenderActivity(_state, now, _region.Width) is { } activity)
            lines.Add(activity);

        lines.Add(StatusLine.RenderStatus(
            _model, _tokensUsed(), _contextWindow, _state.TurnElapsed(now), _region.Width));

        _region.SetLines(lines);
    }
}
