namespace Dougong.Kernel;

internal sealed class LifecycleTimeoutException(string phase, Exception inner)
    : TimeoutException($"插件阶段 '{phase}' 超时；代次已隔离，需重启宿主。", inner)
{
    /// <summary>超时的阶段名（cancellation / drain / events / cleanup / activation）。</summary>
    public string Phase { get; } = phase;

    /// <summary>把阶段名映射到诊断阶段。未识别的名字归为 Unknown，不猜测。</summary>
    public PluginFailureStage Stage => Phase switch
    {
        "activation" => PluginFailureStage.Activate,
        "cancellation" or "drain" or "events" => PluginFailureStage.Drain,
        "cleanup" => PluginFailureStage.Cleanup,
        _ => PluginFailureStage.Unknown,
    };
}

public sealed partial class DougongKernel
{
    private TimeSpan _activationTimeout = TimeSpan.FromSeconds(30);
    private TimeSpan _cancellationTimeout = TimeSpan.FromSeconds(30);
    private TimeSpan _eventDrainTimeout = TimeSpan.FromSeconds(30);
    private TimeSpan _cleanupTimeout = TimeSpan.FromSeconds(30);

    public TimeSpan ActivationTimeout { get => _activationTimeout; set => _activationTimeout = ValidateBudget(value); }
    public TimeSpan CancellationTimeout { get => _cancellationTimeout; set => _cancellationTimeout = ValidateBudget(value); }
    public TimeSpan EventDrainTimeout { get => _eventDrainTimeout; set => _eventDrainTimeout = ValidateBudget(value); }
    public TimeSpan CleanupTimeout { get => _cleanupTimeout; set => _cleanupTimeout = ValidateBudget(value); }

    private static TimeSpan ValidateBudget(TimeSpan value)
    {
        if (value != Timeout.InfiniteTimeSpan && (value <= TimeSpan.Zero || value.TotalMilliseconds > uint.MaxValue - 1))
            throw new ArgumentOutOfRangeException(nameof(value));
        return value;
    }

    internal static async Task WithinBudgetAsync(Task task, TimeSpan budget, string phase)
    {
        try { await task.WaitAsync(budget).ConfigureAwait(false); }
        catch (TimeoutException ex) when (!task.IsCompleted)
        {
            _ = task.ContinueWith(t => { _ = t.Exception; }, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            throw new LifecycleTimeoutException(phase, ex);
        }
    }

    private static void EnsureReusable(PluginEntry entry)
    {
        if (entry.CleanupFailed || entry.IsolationReason is not null)
            throw new InvalidOperationException($"插件 '{entry.Id}' 未干净停止：{entry.IsolationReason ?? "清理失败或排空超时"}；需重启宿主。");
    }

    private void Isolate(PluginEntry entry, PluginContext context, string reason, Exception error)
    {
        List<PluginStateChange> changes = [];
        lock (_sync)
        {
            if (!ReferenceEquals(entry.Context, context)) return;
            entry.IsolationReason = reason;
            entry.DesiredActive = false;
            WithdrawServices(entry);

            // 从超时异常里取阶段；非超时异常（观察者/清理回调抛出）归为 Cleanup，
            // 因为 Isolate 的调用点都在清理路径上。
            var stage = error is LifecycleTimeoutException timeout
                ? timeout.Stage
                : PluginFailureStage.Cleanup;

            SetState(entry, changes, PluginState.Failed, reason, error, stage);
        }
        PublishChanges(changes);
    }
}
