using System.Diagnostics;
using Dougong.Agent;
using Dougong.Kernel;

namespace Dougong.Agent.Comparison;

/// <summary>
/// 内核热路径的性能测量：租约获取、服务解析、事件发布。
/// </summary>
/// <remarks>
/// **只输出数字，不参与通过/失败判定**，因此不进 <c>all-checks.sh`——
/// 与 <c>PerformanceBaselineChecks</c> 同样的理由：数字随机器变，判定会变成噪声。
/// <para>
/// 为什么单独测内核：每个工具派发、每次存储提交都要过一次
/// <see cref="IServiceReference{T}.InvokeAsync"/>。它在真实会话里是**每轮多次**的
/// 固定开销，而此前的性能基线只测了 Agent 层与存储，没测过它。
/// </para>
/// </remarks>
public static class KernelPerfChecks
{
    public static async Task<int> RunAsync()
    {
        Console.WriteLine($"# 环境：{Environment.OSVersion}，{Environment.ProcessorCount} 核，"
            + $"SDK {Environment.Version}");
        Console.WriteLine();

        await MeasureInvokeOverheadAsync();
        await MeasureResolveOverheadAsync();
        await MeasureInvokeUnderContentionAsync();
        await MeasureEventPublishOverheadAsync();

        return 0;
    }

    /// <summary>
    /// 租约调用：每次 <c>InvokeAsync</c> 的代价。
    /// </summary>
    /// <remarks>
    /// 这是内核最热的一条路径。一次工具派发 = 一次审批租约 + 一次执行租约；
    /// 一次存储提交 = 一次租约。量它才知道"内核对每轮对话加了多少税"。
    /// </remarks>
    private static async Task MeasureInvokeOverheadAsync()
    {
        Console.WriteLine("## 租约调用（InvokeAsync）");

        await using var kernel = new DougongKernel();
        var state = 0;
        var id = (await kernel.RegisterAsync(() => new CounterPlugin(() => state++))).Id;
        await kernel.ActivateAsync(id);

        var reference = kernel.Resolver.Resolve<ICounter>()
            ?? throw new InvalidOperationException("解析不到 ICounter");

        // 预热
        for (var i = 0; i < 100; i++)
            await reference.InvokeAsync((s, _) => { s.Touch(); return ValueTask.CompletedTask; });

        const int iterations = 10_000;
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        var before = GC.GetTotalAllocatedBytes(precise: true);

        var watch = Stopwatch.StartNew();
        for (var i = 0; i < iterations; i++)
            await reference.InvokeAsync((s, _) => { s.Touch(); return ValueTask.CompletedTask; });
        watch.Stop();

        var allocated = GC.GetTotalAllocatedBytes(precise: true) - before;
        Console.WriteLine($"  {iterations:N0} 次：{watch.Elapsed.TotalMilliseconds:F1} ms"
            + $"，{watch.Elapsed.TotalMilliseconds * 1000 / iterations:F2} µs/次"
            + $"，分配 {allocated / (double)iterations:F0} B/次");
        Console.WriteLine();

        await kernel.DeactivateAsync(id);
    }

    /// <summary>
    /// 并发争用下的租约调用：锁是否成为瓶颈。
    /// </summary>
    /// <remarks>
    /// 串行测量说明不了锁竞争。真实宿主可能同时有多个会话/多路工具，
    /// 而 <c>InvokeAsync</c> 全程持 <c>kernel._sync</c> 取实例——
    /// 若这个锁真的成为瓶颈，并发吞吐会**低于**串行。
    /// <para>
    /// 判据：并发吞吐应 ≥ 串行的 50%。显著低于则说明锁粒度需要重新考虑。
    /// 这一条只记录，不判定（本套件只出数字），但数字本身要有解释。
    /// </para>
    /// </remarks>
    private static async Task MeasureInvokeUnderContentionAsync()
    {
        Console.WriteLine("## 并发争用下的租约调用");

        await using var kernel = new DougongKernel();
        var state = 0;
        var id = (await kernel.RegisterAsync(() => new CounterPlugin(() => state++))).Id;
        await kernel.ActivateAsync(id);

        var reference = kernel.Resolver.Resolve<ICounter>()
            ?? throw new InvalidOperationException("解析不到 ICounter");

        const int perWorker = 2_000;
        foreach (var workers in new[] { 1, 4, 16 })
        {
            // 预热
            for (var i = 0; i < 200; i++)
                await reference.InvokeAsync((s, _) => { s.Touch(); return ValueTask.CompletedTask; });

            var watch = Stopwatch.StartNew();
            var tasks = new Task[workers];
            for (var w = 0; w < workers; w++)
            {
                tasks[w] = Task.Run(async () =>
                {
                    for (var i = 0; i < perWorker; i++)
                        await reference.InvokeAsync(
                            (s, _) => { s.Touch(); return ValueTask.CompletedTask; });
                });
            }
            await Task.WhenAll(tasks);
            watch.Stop();

            var total = workers * perWorker;
            Console.WriteLine($"  {workers,2} 并发 × {perWorker:N0} 次：{watch.Elapsed.TotalMilliseconds,7:F1} ms"
                + $"，{total / watch.Elapsed.TotalSeconds,10:F0} 次/秒");
        }

        Console.WriteLine();
        await kernel.DeactivateAsync(id);
    }

    /// <summary>服务解析：<c>Resolver.Resolve&lt;T&gt;</c> 的代价（不含调用）。</summary>
    private static async Task MeasureResolveOverheadAsync()
    {
        Console.WriteLine("## 服务解析（Resolver.Resolve）");

        await using var kernel = new DougongKernel();
        var id = (await kernel.RegisterAsync(() => new CounterPlugin(() => { }))).Id;
        await kernel.ActivateAsync(id);

        for (var i = 0; i < 100; i++) kernel.Resolver.Resolve<ICounter>();

        const int iterations = 100_000;
        GC.Collect();
        var before = GC.GetTotalAllocatedBytes(precise: true);

        var watch = Stopwatch.StartNew();
        for (var i = 0; i < iterations; i++) kernel.Resolver.Resolve<ICounter>();
        watch.Stop();

        var allocated = GC.GetTotalAllocatedBytes(precise: true) - before;
        Console.WriteLine($"  {iterations:N0} 次：{watch.Elapsed.TotalMilliseconds:F1} ms"
            + $"，{watch.Elapsed.TotalMilliseconds * 1000 / iterations:F2} µs/次"
            + $"，分配 {allocated / (double)iterations:F0} B/次");
        Console.WriteLine();

        await kernel.DeactivateAsync(id);
    }

    /// <summary>事件发布：订阅者数量增加时的代价。</summary>
    /// <remarks>
    /// 走真实的 <see cref="IEventBus"/>（插件订阅 + 插件发布），不是内部实现。
    /// 投递是队列式的（发布者只入队、不执行插件代码），所以这里量的是
    /// **入队代价**，而不是处理器执行时间——后者由订阅者自己决定。
    /// <para>
    /// <b>测量设计上的一个坑（第一版踩到）</b>：订阅队列容量是 256，订阅者
    /// 不消费就必然积压、满了被断开。第一版直接连发 10 000 次，于是
    /// "1 个订阅者"最先溢出、测到的是断开后的快速路径，得出
    /// "1 个订阅者比 10 个慢 10 倍"这种不合理的数。
    /// 现在每批只发 <c>capacity/4</c> 条并等处理器消费完，使测量落在
    /// **正常入队路径**上。
    /// </para>
    /// </remarks>
    private static async Task MeasureEventPublishOverheadAsync()
    {
        Console.WriteLine("## 事件发布（订阅者数，入队代价）");

        foreach (var subscriberCount in new[] { 1, 10, 100 })
        {
            await using var kernel = new DougongKernel();
            var probe = new EventProbe(subscriberCount);
            var id = (await kernel.RegisterAsync(() => probe)).Id;
            await kernel.ActivateAsync(id);

            // 预热
            for (var i = 0; i < 64; i++) probe.Publish("warm");
            await probe.DrainAsync();

            // 队列容量 256：每批发 64 条后等消费完，避免积压导致订阅被断开。
            const int batch = 64;
            const int batches = 100;
            var iterations = batch * batches;

            GC.Collect();
            var before = GC.GetTotalAllocatedBytes(precise: true);

            var watch = Stopwatch.StartNew();
            for (var b = 0; b < batches; b++)
            {
                for (var i = 0; i < batch; i++) probe.Publish("x");
                await probe.DrainAsync();
            }
            watch.Stop();

            var allocated = GC.GetTotalAllocatedBytes(precise: true) - before;
            Console.WriteLine($"  {subscriberCount,3} 个订阅者 × {iterations:N0} 次："
                + $"{watch.Elapsed.TotalMilliseconds:F1} ms"
                + $"，{watch.Elapsed.TotalMilliseconds * 1000 / iterations:F2} µs/次"
                + $"，分配 {allocated / (double)iterations:F0} B/次"
                + (probe.Drained ? "" : "（警告：队列积压，数字偏乐观）"));

            await kernel.DeactivateAsync(id);
        }

        Console.WriteLine("  注：只计发布入队；处理器执行在各自的队列泵上，不计入。");
        Console.WriteLine();
    }

    /// <summary>探针服务：只提供一次计数递增，用于测调用开销本身。</summary>
    public interface ICounter
    {
        void Touch();
    }

    private sealed class CounterPlugin(Action onTouch) : IPlugin
    {
        public PluginManifest Manifest { get; } =
            PluginManifest.Create("perf.counter", "1.0.0");

        public ValueTask ActivateAsync(PluginContext context, CancellationToken cancellationToken)
        {
            context.Services.Register<ICounter>(new Counter(onTouch));
            return ValueTask.CompletedTask;
        }
    }

    private sealed class Counter(Action onTouch) : ICounter
    {
        public void Touch() => onTouch();
    }

    /// <summary>订阅 N 次并对外暴露发布入口，用于测事件总线入队代价。</summary>
    /// <remarks>
    /// 订阅处理器只做一次计数自增——测的是**入队**代价，不是业务处理代价。
    /// 处理器必须真的消费，否则队列积压到 256 就会断开订阅（第一版踩过）。
    /// </remarks>
    private sealed class EventProbe(int subscriberCount) : IPlugin
    {
        private IEventBus? _events;
        private readonly List<IDisposable> _subscriptions = [];
        private int _published;
        private long _consumed;

        public PluginManifest Manifest { get; } =
            PluginManifest.Create("perf.events", "1.0.0");

        public ValueTask ActivateAsync(PluginContext context, CancellationToken cancellationToken)
        {
            _events = context.Events;
            for (var i = 0; i < subscriberCount; i++)
                _subscriptions.Add(_events.Subscribe<string>(_ => Interlocked.Increment(ref _consumed)));
            return ValueTask.CompletedTask;
        }

        public void Publish(string message)
        {
            _events!.Publish(message);
            Interlocked.Increment(ref _published);
        }

        /// <summary>
        /// 等到处理器把已发布事件全部消费掉。
        /// 每条发布会产生 <c>subscriberCount</c> 次消费（每个订阅一次）。
        /// </summary>
        /// <summary>上一次 Drain 是否成功等到消费完；false 表示队列积压、数字不可信。</summary>
        public bool Drained { get; private set; } = true;

        public async Task DrainAsync()
        {
            var expected = (long)Volatile.Read(ref _published) * subscriberCount;
            for (var spin = 0; spin < 10_000; spin++)
            {
                if (Interlocked.Read(ref _consumed) >= expected) { Drained = true; return; }
                await Task.Yield();
            }

            Drained = false;
        }
    }
}
