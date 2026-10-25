using Dougong.Kernel;

namespace Dougong.Kernel.Tests;

/// <summary>
/// 失败可定位到组件（M3 判据 4）。
/// </summary>
/// <remarks>
/// 判据要求："崩溃、取消、超时、副作用结果未知时，系统能说清哪些一定发生了，
/// 哪些一定没发生"。落到插件生命周期，就是**每个失败都要能回答三件事**：
/// 哪个插件、哪个阶段、下一步该做什么。
/// <para>
/// 五类故障的实际形态不同，验收也分开：
/// <list type="bullet">
/// <item><b>Load</b> / <b>Unload</b>：条目被回滚或保留，走**异常**而非状态流——
/// 因此断言异常消息里含可操作建议</item>
/// <item><b>Activate</b> / <b>Drain</b> / <b>Cleanup</b>：条目的终态是
/// <see cref="PluginState.Failed"/>，走**状态流**——因此断言 `PluginDiagnostic`
/// 的 Stage 与 Hint</item>
/// </list>
/// 这个区分不是设计选择，是内核实际行为；验收必须按实际形态写，
/// 否则会写出「测了我以为的行为」。
/// </para>
/// </remarks>
public sealed class KernelFailureDiagnosisTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private static string SampleAssemblyPath => Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "..", "..",
        "samples", "Dougong.Sample.Plugin", "bin",
#if DEBUG
        "Debug",
#else
        "Release",
#endif
        "net10.0", "Dougong.Sample.Plugin.dll"));

    /// <summary>Activate 阶段：激活抛异常 → Stage=Activate，Hint 指向检查插件实现。</summary>
    [Fact]
    public async Task Activation_failure_reports_activate_stage()
    {
        await using var kernel = new DougongKernel();
        using var recorder = new ChangeRecorder(kernel);

        var handle = await kernel.RegisterAsync(() => new ThrowingPlugin())
            .AsTask().WaitAsync(Timeout);

        Assert.Equal(PluginState.Failed, handle.State);

        var change = recorder.Latest(handle.Id, PluginState.Failed);
        Assert.NotNull(change);
        Assert.NotNull(change!.Diagnostic);
        Assert.Equal(PluginFailureStage.Activate, change.Diagnostic!.Stage);
        Assert.Contains("ActivateAsync", change.Diagnostic.Hint);
    }

    /// <summary>Cleanup 阶段：清理回调永久阻塞 → Stage=Cleanup（或 Drain），Hint 指向清理回调。</summary>
    /// <remarks>
    /// 阶段可能是 Cleanup 或 Drain：取决于超时发生在"等取消"还是"等清理"。
    /// 两者都指向同一类根因（回调不响应取消），验收接受这两个值但要求
    /// **不能是 Unknown**——Unknown 意味着内核没能定位到阶段。
    /// </remarks>
    [Fact]
    public async Task Blocking_cleanup_reports_cleanup_or_drain_stage()
    {
        // 不用 await using：隔离代次会让 DisposeAsync 抛
        // "存在未能干净停止的代次"——那是正确行为，不是本用例要断言的。
        var kernel = new DougongKernel
        {
            CleanupTimeout = TimeSpan.FromMilliseconds(150),
            CancellationTimeout = TimeSpan.FromMilliseconds(150),
            DrainTimeout = TimeSpan.FromMilliseconds(150),
        };
        using var recorder = new ChangeRecorder(kernel);

        var handle = await kernel.RegisterAsync(() => new BlockingCleanupPlugin())
            .AsTask().WaitAsync(Timeout);

        // 关键断言之一：排空/清理预算必须真的生效。
        // 修复前这里是 3000ms（同步阻塞的清理回调绕过了预算），修复后约 200ms。
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var result = await kernel.DeactivateAsync(handle.Id).AsTask().WaitAsync(TimeSpan.FromSeconds(8));
        sw.Stop();

        Assert.True(sw.ElapsedMilliseconds < 2000,
            $"停用耗时 {sw.ElapsedMilliseconds}ms：清理预算未生效，"
            + "同步阻塞的回调绕过了 CleanupTimeout（会在第一个 await 之前卡住调用方）。");
        Assert.Equal(PluginState.Failed, result.State);

        var change = recorder.Latest(handle.Id, PluginState.Failed);
        Assert.True(change is not null,
            $"未捕获 Failed 状态。实际变化：{recorder.Describe()}");
        Assert.NotNull(change!.Diagnostic);
        Assert.True(
            change.Diagnostic!.Stage is PluginFailureStage.Cleanup or PluginFailureStage.Drain,
            $"阶段应为 Cleanup 或 Drain，实际 {change.Diagnostic.Stage}");
        Assert.False(string.IsNullOrWhiteSpace(change.Diagnostic.Hint));

        // 隔离开的代次必须拒绝销毁以外的操作，且销毁时如实报错——
        // 这是 P2"隔离后不可再激活"的一部分。
        Assert.Contains("隔离", change.Reason);

        try { await kernel.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)); }
        catch (InvalidOperationException) { /* 正确：如实报告未干净停止。 */ }
    }

    /// <summary>Load 阶段：缺程序集 → 异常消息里给出登记共享契约的建议。</summary>
    /// <remarks>
    /// 加载失败时条目被回滚，没有状态可挂诊断，因此诊断以异常形式给出。
    /// 断言的是**建议存在**，不是异常类型——异常类型是实现细节。
    /// </remarks>
    [Fact]
    public async Task Load_failure_message_is_actionable()
    {
        await using var kernel = new DougongKernel();

        // 插件引用了一个宿主未登记的契约程序集。
        var error = await Record.ExceptionAsync(async () =>
            await kernel.LoadAsync(IsolatedSamplePath).AsTask().WaitAsync(Timeout));

        Assert.NotNull(error);
        var text = Flatten(error!);

        // 必须说清"缺什么"与"怎么办"，不能只有一个 TargetInvocationException。
        Assert.Contains("RegisterSharedContract", text, StringComparison.Ordinal);
    }

    /// <summary>Unload 阶段：静态注册的插件不可卸载 → 消息说明原因。</summary>
    [Fact]
    public async Task Unload_rejection_explains_why()
    {
        await using var kernel = new DougongKernel();
        await kernel.RegisterAsync(() => new TrivialPlugin()).AsTask().WaitAsync(Timeout);

        var error = await Record.ExceptionAsync(async () =>
            await kernel.UnloadAsync(PluginId.Parse("test.trivial")).AsTask().WaitAsync(Timeout));

        Assert.NotNull(error);
        // 消息必须点出"静态/动态"的区别，否则使用者不知道该改什么。
        Assert.Contains("动态", Flatten(error!), StringComparison.Ordinal);
    }

    /// <summary>Blocked 阶段：依赖缺失 → Reason 里点名缺什么。</summary>
    /// <remarks>
    /// Blocked 不是"失败"而是"缺依赖"。它的诊断要求不是 Stage/Hint，
    /// 而是**说清缺哪一个**——否则使用者只能猜。
    /// </remarks>
    [Fact]
    public async Task Blocked_state_names_the_missing_dependency()
    {
        await using var kernel = new DougongKernel();
        using var recorder = new ChangeRecorder(kernel);

        var handle = await kernel.RegisterAsync(() => new RequiresMissingServicePlugin())
            .AsTask().WaitAsync(Timeout);

        Assert.Equal(PluginState.Blocked, handle.State);

        var change = recorder.Latest(handle.Id, PluginState.Blocked);
        Assert.NotNull(change);
        Assert.Contains("IMissingService", change!.Reason, StringComparison.Ordinal);
    }

    // ── 支撑 ─────────────────────────────────────────────────────

    private static string IsolatedSamplePath => Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "..", "..",
        "samples", "Dougong.Sample.IsolatedPlugin", "bin",
#if DEBUG
        "Debug",
#else
        "Release",
#endif
        "net10.0", "Dougong.Sample.IsolatedPlugin.dll"));

    /// <summary>把异常链条拍平成一段文本，便于断言"建议是否出现在某处"。</summary>
    private static string Flatten(Exception error)
    {
        var parts = new List<string>();
        for (Exception? current = error; current is not null; current = current.InnerException)
            parts.Add($"{current.GetType().Name}: {current.Message}");
        return string.Join(" | ", parts);
    }

    /// <summary>
    /// 用同步订阅收集状态变化，而不是从增量流里读。
    /// </summary>
    /// <remarks>
    /// 初版从 <c>view.Changes</c>（异步流）读，用 <c>Wait(200)</c> 等超时——
    /// 在异步枚举上这不可靠，取不到已提交的增量，导致断言拿到 null。
    /// 同步订阅由内核在提交时直接回调，没有这个时序问题。
    /// </remarks>
    private sealed class ChangeRecorder : IDisposable
    {
        private readonly List<PluginStateChange> _changes = [];
        private readonly IDisposable _subscription;

        public ChangeRecorder(DougongKernel kernel) =>
            _subscription = kernel.SubscribeStateChanges(change =>
            {
                lock (_changes) _changes.Add(change);
            });

        public PluginStateChange? Latest(PluginId id, PluginState to)
        {
            lock (_changes)
                return _changes.LastOrDefault(c => c.PluginId == id && c.To == to);
        }

        /// <summary>诊断用：列出实际捕获到的所有状态迁移。</summary>
        public string Describe()
        {
            lock (_changes)
                return _changes.Count == 0
                    ? "(无)"
                    : string.Join(", ", _changes.Select(c => $"{c.PluginId}:{c.From}->{c.To}"));
        }

        public void Dispose() => _subscription.Dispose();
    }

    private sealed class TrivialPlugin : IPlugin
    {
        public PluginManifest Manifest { get; } = PluginManifest.Create("test.trivial", "1.0.0");

        public ValueTask ActivateAsync(PluginContext context, CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;
    }

    private sealed class ThrowingPlugin : IPlugin
    {
        public PluginManifest Manifest { get; } = PluginManifest.Create("test.throwing", "1.0.0");

        public ValueTask ActivateAsync(PluginContext context, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("插件故意失败。");
    }

    private sealed class BlockingCleanupPlugin : IPlugin
    {
        public PluginManifest Manifest { get; } = PluginManifest.Create("test.blocking", "1.0.0");

        public ValueTask ActivateAsync(PluginContext context, CancellationToken cancellationToken)
        {
            // 忽略取消令牌：制造排空/清理超时。
            context.RegisterForCleanup(_ =>
            {
                Thread.Sleep(3000);
                return ValueTask.CompletedTask;
            });

            return ValueTask.CompletedTask;
        }
    }

    private sealed class RequiresMissingServicePlugin : IPlugin
    {
        public PluginManifest Manifest { get; } = PluginManifest.CreateWithDependencies(
            "test.needs", "1.0.0",
            [new ServiceRequirement(typeof(IMissingService))],
            []);

        public ValueTask ActivateAsync(PluginContext context, CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;
    }

    private interface IMissingService;
}
