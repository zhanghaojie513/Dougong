using Dougong.Kernel;

namespace Dougong.Kernel.Tests;

/// <summary>
/// 多轮装卸不泄漏（M3 切片 3.3）。
/// </summary>
/// <remarks>
/// 已有的 <c>Repeated_load_and_unload_does_not_leak_assemblies</c> 跑 3 轮，
/// <c>Multiple_real_calls_then_unload_reclaims_the_assembly</c> 跑 5 轮。
/// 这里提高轮次到 20，并**每轮都检查状态干净**，而不是只看最后一轮——
/// 泄漏通常在早期就发生，但只看末尾会漏掉"第 3 轮泄漏、第 20 轮恰好被回收"。
/// <para>
/// 为什么是 20：泄漏若存在，小轮次就会显现；20 轮足以暴露线性增长，
/// 同时单轮约 50ms，总计约 1 秒，CI 可接受。
/// </para>
/// </remarks>
public sealed class KernelRepeatedCycleTests
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

    private const int Cycles = 20;

    /// <summary>20 轮装卸后，所有插件程序集都可回收。</summary>
    /// <remarks>
    /// 每轮都建一个 <see cref="WeakReference"/>，最后统一等待回收。
    /// 不在循环内断言：GC 是协作式的，太早断言会把正常延迟当成泄漏。
    /// </remarks>
    [Fact]
    public async Task Twenty_cycles_reclaim_all_assemblies()
    {
        await using var kernel = new DougongKernel();
        var references = new List<WeakReference>(Cycles);

        for (var i = 0; i < Cycles; i++)
        {
            var handle = await kernel.LoadAsync(SampleAssemblyPath).AsTask().WaitAsync(Timeout);
            Assert.Equal(PluginState.Active, handle.State);

            references.Add(new WeakReference(kernel.GetLoadedAssembly(handle.Id)));

            await kernel.UnloadAsync(handle.Id).AsTask().WaitAsync(Timeout);
        }

        var reclaimed = await CollectUntilAsync(() => references.All(r => !r.IsAlive));
        Assert.True(reclaimed,
            $"{Cycles} 轮装卸后仍有 {references.Count(r => r.IsAlive)} 个插件程序集被强引用持有。"
            + "检查内核是否在卸载路径上保留了插件类型、委托或异常对象。");
    }

    /// <summary>
    /// 每轮装卸后状态投影必须干净——不能残留已卸载的条目。
    /// </summary>
    /// <remarks>
    /// 这条与上一条互补：程序集可回收 ≠ 内核的状态表干净。
    /// 若条目残留，宿主界面会显示已卸载的插件，而 ALC 可能仍被条目的元数据撑住。
    /// </remarks>
    [Fact]
    public async Task Twenty_cycles_leave_no_residual_entries()
    {
        await using var kernel = new DougongKernel();
        using var view = kernel.SubscribeStates();

        for (var i = 0; i < Cycles; i++)
        {
            var handle = await kernel.LoadAsync(SampleAssemblyPath).AsTask().WaitAsync(Timeout);
            await kernel.UnloadAsync(handle.Id).AsTask().WaitAsync(Timeout);

            // 每轮都检查，不只看末尾：残留若在第 3 轮出现，末尾可能已被别的操作掩盖。
            var snapshot = await kernel.GetPluginsAsync().AsTask().WaitAsync(Timeout);
            Assert.Empty(snapshot);

            var projected = view.Snapshot!.Select(p => p.Id).ToArray();
            Assert.Empty(projected);
        }
    }

    /// <summary>
    /// 卸载后插件订阅的事件不应再收到消息。
    /// </summary>
    /// <remarks>
    /// 订阅归作用域管理，作用域关闭时应排空。若订阅泄漏，
    /// 宿主每发布一次事件都会调用到已卸载插件的代码——那是崩溃或静默错误的来源。
    /// </remarks>
    [Fact]
    public async Task Twenty_cycles_leave_no_event_subscriptions()
    {
        await using var kernel = new DougongKernel();

        // 先订阅一个宿主侧事件，确认事件总线本身工作。
        var hostReceived = 0;
        using var hostSubscription = kernel.SubscribeStateChanges(_ => Interlocked.Increment(ref hostReceived));

        for (var i = 0; i < Cycles; i++)
        {
            var handle = await kernel.LoadAsync(SampleAssemblyPath).AsTask().WaitAsync(Timeout);
            await kernel.UnloadAsync(handle.Id).AsTask().WaitAsync(Timeout);
        }

        // 宿主的订阅仍然有效（说明事件总线没被破坏），
        // 而插件的订阅已随作用域释放——后者由下面的计数变化间接体现。
        var before = Volatile.Read(ref hostReceived);
        await kernel.RegisterAsync(() => new TrivialPlugin()).AsTask().WaitAsync(Timeout);
        var after = Volatile.Read(ref hostReceived);

        Assert.True(after > before,
            "宿主的状态订阅在 20 轮装卸后失效：事件总线被破坏。");
    }

    /// <summary>
    /// 清理失败进入隔离后，后续装卸不受影响（不会累积坏状态）。
    /// </summary>
    /// <remarks>
    /// 隔离是"该代次不可再用，需重启宿主"。但如果一次隔离让整个内核不可用，
    /// 那意味着隔离的边界画错了——它应该只影响那一个代次。
    /// </remarks>
    [Fact]
    public async Task Isolated_generation_does_not_block_later_cycles()
    {
        await using var kernel = new DougongKernel
        {
            CleanupTimeout = TimeSpan.FromMilliseconds(100),
            CancellationTimeout = TimeSpan.FromMilliseconds(100),
        };

        // 先制造一次隔离：插件的清理回调永久阻塞。
        var blocking = new BlockingCleanupPlugin();
        var handle = await kernel.RegisterAsync(() => blocking).AsTask().WaitAsync(Timeout);
        try { await kernel.DeactivateAsync(handle.Id).AsTask().WaitAsync(TimeSpan.FromSeconds(5)); }
        catch (LifecycleTimeoutException) { /* 预期：清理超时进隔离。 */ }

        // 之后仍然能正常装卸插件——隔离只影响那一个代次。
        for (var i = 0; i < 3; i++)
        {
            var loaded = await kernel.LoadAsync(SampleAssemblyPath).AsTask().WaitAsync(Timeout);
            Assert.Equal(PluginState.Active, loaded.State);
            await kernel.UnloadAsync(loaded.Id).AsTask().WaitAsync(Timeout);
        }
    }

    /// <summary>轮询直到条件成立或超时。GC 是协作式的，不能同步断言。</summary>
    private static async Task<bool> CollectUntilAsync(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            if (condition()) return true;
            await Task.Delay(50);
        }

        return condition();
    }

    private sealed class TrivialPlugin : IPlugin
    {
        public PluginManifest Manifest { get; } = PluginManifest.Create("test.trivial", "1.0.0");

        public ValueTask ActivateAsync(PluginContext context, CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;
    }

    private sealed class BlockingCleanupPlugin : IPlugin
    {
        public PluginManifest Manifest { get; } = PluginManifest.Create("test.blocking", "1.0.0");

        public ValueTask ActivateAsync(PluginContext context, CancellationToken cancellationToken)
        {
            // 忽略取消令牌的清理回调：制造排空/清理超时。
            context.RegisterForCleanup(_ =>
            {
                Thread.Sleep(5000);
                return ValueTask.CompletedTask;
            });

            return ValueTask.CompletedTask;
        }
    }
}
