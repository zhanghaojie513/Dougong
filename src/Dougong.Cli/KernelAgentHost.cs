using Dougong.Agent;
using Dougong.Kernel;

namespace Dougong.Cli;

/// <summary>
/// 内核接线：把运行能力作为插件注册进内核，宿主只通过解析器使用它们。
/// </summary>
/// <remarks>
/// 解决的缺口（G14）：<c>DougongKernel</c> 此前在产品路径上零引用，
/// CLI 直接 <c>new ToolDispatcher</c> / <c>new LoopAgentRuntime</c> 并存下来，
/// 内核的租约、代次绑定、排空、隔离在真实会话中从未生效。
/// <para>
/// <b>为什么不新增工程</b>：<c>ArchitectureBoundaryTests</c> 的分层是
/// Kernel(L0) ← Agent(L1) ← {Ai,Mcp,Acp}(L2) ← Cli(L3)。接线代码同时需要
/// Kernel 与 Agent，只有 L3 的 <c>Dougong.Cli</c> 满足。新增工程要改分层表，
/// 不在本切片范围。
/// </para>
/// <para>
/// <b>审慎的默认</b>：本类只在 <c>DOUGONG_KERNEL=1</c> 时被使用，默认路径不变。
/// 结构改动一次性切默认会让 27 个套件同时受影响，失败时无法归因。
/// </para>
/// </remarks>
public sealed class KernelAgentHost : IAsyncDisposable
{
    /// <summary>存储插件的 Id。</summary>
    public const string StorePluginId = "dougong.store";

    /// <summary>工具 dispatcher 插件的 Id。</summary>
    public const string ToolPluginId = "dougong.tools";

    /// <summary>运行时插件的 Id。</summary>
    public const string RuntimePluginId = "dougong.runtime";

    private readonly DougongKernel _kernel;
    private readonly List<PluginId> _registered = [];

    private KernelAgentHost(DougongKernel kernel)
    {
        _kernel = kernel;
    }

    /// <summary>
    /// 建立内核并注册全部插件。
    /// </summary>
    /// <param name="store">会话存储。为 null 表示该会话不持久化。</param>
    /// <param name="createDispatcher">
    /// 构造 dispatcher 的委托。dispatcher 依赖审批器，但审批器必须由内核解析——
    /// 因此这里要的是一个**接受适配后审批器**的工厂，而不是已经建好的实例。
    /// </param>
    /// <param name="createRuntime">
    /// 构造运行时的委托。它接受 dispatcher 实例；dispatcher 同样由内核解析。
    /// </param>
    public static async Task<KernelAgentHost> CreateAsync(
        IAgentStore? store,
        Func<IToolApprover, IAgentStore?, CancellationToken, Task<IToolDispatcher>> createDispatcher,
        Func<IToolDispatcher, IAgentStore?, IAgentRuntime> createRuntime,
        IToolApprover approver,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(createDispatcher);
        ArgumentNullException.ThrowIfNull(createRuntime);
        ArgumentNullException.ThrowIfNull(approver);

        var kernel = new DougongKernel();
        var host = new KernelAgentHost(kernel);

        try
        {
            // 审批器先注册：dispatcher 要解析它。
            host._registered.Add((await kernel.RegisterAsync(
                () => new ApproverPlugin(approver), cancellationToken)).Id);

            if (store is not null)
            {
                host._registered.Add((await kernel.RegisterAsync(
                    () => new StorePlugin(store), cancellationToken)).Id);
            }

            host._registered.Add((await kernel.RegisterAsync(
                () => new ToolPlugin(createDispatcher, kernel), cancellationToken)).Id);

            host._registered.Add((await kernel.RegisterAsync(
                () => new RuntimePlugin(createRuntime, kernel), cancellationToken)).Id);

            // 按注册顺序激活；内核会按服务需求排序，不依赖这里的手工顺序。
            foreach (var id in host._registered)
                await kernel.ActivateAsync(id, cancellationToken);
        }
        catch
        {
            await host.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        return host;
    }

    /// <summary>供宿主诊断用的内核实例。</summary>
    public DougongKernel Kernel => _kernel;

    /// <summary>
    /// 解析运行时。返回的适配器每次 <c>RunAsync</c> 取一次租约。
    /// </summary>
    /// <exception cref="InvalidOperationException">运行时未注册或当前不可解析。</exception>
    public IAgentRuntime ResolveRuntime()
    {
        var reference = _kernel.Resolver.Resolve<IAgentRuntime>()
            ?? throw new InvalidOperationException(
                "解析不到 IAgentRuntime：运行时插件的必需依赖可能缺失（见内核状态）。");
        return new KernelAgentRuntime(reference);
    }

    /// <summary>解析 dispatcher 的服务引用；供需要自行控制租约粒度的宿主使用。</summary>
    public IServiceReference<IToolDispatcher>? ResolveToolReference() =>
        _kernel.Resolver.Resolve<IToolDispatcher>();

    /// <summary>
    /// 取一个只读视图：可派发，但注册面关闭。
    /// </summary>
    /// <remarks>
    /// 给会话层做一致性检查与诊断用。<see cref="IToolDispatcher.Register"/> 会抛
    /// <see cref="NotSupportedException"/>——注册属于插件激活期，运行期注册会绕过
    /// 内核的生命周期协调。
    /// </remarks>
    public IToolDispatcher? ResolveDispatcherView()
    {
        var reference = _kernel.Resolver.Resolve<IToolDispatcher>();
        if (reference is null) return null;

        var tools = reference.InvokeAsync(
            (dispatcher, _) => ValueTask.FromResult(dispatcher.Tools)).AsTask()
            .GetAwaiter().GetResult();

        return new LeasedDispatcher(reference, tools);
    }

    /// <summary>解析存储的服务引用。</summary>
    public IServiceReference<IAgentStore>? ResolveStoreReference() =>
        _kernel.Resolver.Resolve<IAgentStore>();

    /// <summary>当前内核中所有插件的状态快照，供宿主如实展示。</summary>
    public async ValueTask<IReadOnlyList<PluginSummary>> GetPluginsAsync(
        CancellationToken cancellationToken = default) =>
        await _kernel.GetPluginsAsync(cancellationToken).ConfigureAwait(false);

    public async ValueTask DisposeAsync()
    {
        // 逆序停用：消费者（运行时、dispatcher）先于提供方（存储、审批器）。
        // 内核本会按依赖拓扑排序，这里显式逆序只是让意图可读。
        for (var i = _registered.Count - 1; i >= 0; i--)
        {
            try { await _kernel.DeactivateAsync(_registered[i]).ConfigureAwait(false); }
            catch (Exception) { /* 停用失败不能阻止其余清理；诊断从状态订阅取。 */ }
        }

        await _kernel.DisposeAsync().ConfigureAwait(false);
    }

    // ── 插件 ─────────────────────────────────────────────────────

    private sealed class StorePlugin(IAgentStore store) : IPlugin
    {
        public PluginManifest Manifest { get; } =
            PluginManifest.Create(StorePluginId, "1.0.0");

        public ValueTask ActivateAsync(PluginContext context, CancellationToken cancellationToken)
        {
            context.Services.Register<IAgentStore>(store);
            // 注册即登记清理，不依赖插件作者记得调用 RegisterForCleanup。
            context.RegisterForCleanup(_ => store.DisposeAsync());
            return ValueTask.CompletedTask;
        }
    }

    private sealed class ApproverPlugin(IToolApprover approver) : IPlugin
    {
        public PluginManifest Manifest { get; } =
            PluginManifest.Create("dougong.approver", "1.0.0");

        public ValueTask ActivateAsync(PluginContext context, CancellationToken cancellationToken)
        {
            context.Services.Register<IToolApprover>(approver);
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>
    /// dispatcher 插件。<b>它必须解析审批器，不能自己 new</b>——
    /// 否则审批器又变成裸引用，代次校验对它失效。
    /// </summary>
    private sealed class ToolPlugin(
        Func<IToolApprover, IAgentStore?, CancellationToken, Task<IToolDispatcher>> factory,
        DougongKernel kernel) : IPlugin
    {
        public PluginManifest Manifest { get; } = PluginManifest.CreateWithDependencies(
            ToolPluginId, "1.0.0",
            [new ServiceRequirement(typeof(IToolApprover))],
            []);

        public async ValueTask ActivateAsync(
            PluginContext context, CancellationToken cancellationToken)
        {
            var reference = kernel.Resolver.Resolve<IToolApprover>()
                ?? throw new InvalidOperationException("dispatcher 插件解析不到 IToolApprover。");

            // store 同样必须经内核解析，不能由宿主传裸实例进来——
            // 否则"内核承载存储"只说了一半：服务注册了，宿主自己也留着一份。
            var store = kernel.Resolver.Resolve<IAgentStore>() is { } storeRef
                ? await storeRef.InvokeAsync(
                    (s, _) => ValueTask.FromResult<IAgentStore?>(s), cancellationToken)
                    .ConfigureAwait(false)
                : null;

            // 适配器只持有服务引用，不持有实例；每次审批都走新的租约。
            // 工厂是异步的：MCP 工具注册要在激活期内完成，那是异步的（子进程/HTTP 握手），
            // 而"工具注册"属于 dispatcher 的构造，不能被推到激活之后——
            // 激活后内核已发布服务快照，再往里塞注册会绕过生命周期协调。
            var dispatcher = await factory(
                new LeasedApprover(reference), store, cancellationToken).ConfigureAwait(false);
            context.Services.Register<IToolDispatcher>(dispatcher);
        }
    }

    /// <summary>
    /// 运行时插件。<b>它必须解析 dispatcher，不能自己 new</b>，理由同审批器。
    /// </summary>
    private sealed class RuntimePlugin(
        Func<IToolDispatcher, IAgentStore?, IAgentRuntime> factory,
        DougongKernel kernel) : IPlugin
    {
        public PluginManifest Manifest { get; } = PluginManifest.CreateWithDependencies(
            RuntimePluginId, "1.0.0",
            [new ServiceRequirement(typeof(IToolDispatcher))],
            []);

        public async ValueTask ActivateAsync(
            PluginContext context, CancellationToken cancellationToken)
        {
            var reference = kernel.Resolver.Resolve<IToolDispatcher>()
                ?? throw new InvalidOperationException("运行时插件解析不到 IToolDispatcher。");

            // 描述符快照必须在租约内取：InvokeAsync 是唯一能接触实例的入口。
            // 取到的是冻结副本（Register 时已 clone），不含实现对象，
            // 因此之后长期持有它不构成"捕获实现实例"。
            var tools = await reference.InvokeAsync(
                (dispatcher, _) => ValueTask.FromResult(dispatcher.Tools),
                cancellationToken).ConfigureAwait(false);

            // store 对运行时是可选依赖：没有持久化时传 null。
            var store = kernel.Resolver.Resolve<IAgentStore>() is { } storeRef
                ? await storeRef.InvokeAsync(
                    (s, _) => ValueTask.FromResult<IAgentStore?>(s), cancellationToken)
                    .ConfigureAwait(false)
                : null;

            context.Services.Register<IAgentRuntime>(
                factory(new LeasedDispatcher(reference, tools), store));
        }
    }

    // ── 适配器：把服务引用包装成裸接口，避免宿主缓存实现实例 ─────────

    /// <summary>
    /// 把 <see cref="IServiceReference{IToolApprover}"/> 适配成 <see cref="IToolApprover"/>。
    /// </summary>
    /// <remarks>
    /// dispatcher 的构造签名要的是裸接口，无法改成"每次传引用"。因此这个适配器
    /// 会被 dispatcher 长期持有——但它持有的是**引用**（只含服务键、提供方 Id、代次），
    /// 每次调用才通过 <c>InvokeAsync</c> 取实例，符合"不长期捕获实现对象"的约束。
    /// </remarks>
    private sealed class LeasedApprover(IServiceReference<IToolApprover> reference) : IToolApprover
    {
        public async ValueTask<ToolApprovalDecision> RequestApprovalAsync(
            ToolApprovalRequest request, CancellationToken cancellationToken = default)
        {
            try
            {
                return await reference.InvokeAsync(
                    (approver, token) => approver.RequestApprovalAsync(request, token),
                    cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (ObjectDisposedException)
            {
                // 审批器已停用：不能当作"批准"，也不能当作"用户拒绝"。
                // 归为拒绝是唯一安全的方向（fail-closed），但原因必须说清。
                return ToolApprovalDecision.Deny(
                    "审批器已停用或不可用；按拒绝处理（无法确认用户意图）。");
            }
        }
    }

    /// <summary>
    /// 把 <see cref="IServiceReference{IToolDispatcher}"/> 适配成 <see cref="IToolDispatcher"/>。
    /// </summary>
    /// <remarks>
    /// dispatcher 的停用语义比审批器更严：一次工具执行可能正在跑，
    /// 所以这里不吞掉 <see cref="ObjectDisposedException"/>，而是转成明确的拒绝终态，
    /// 让调用方看到"未执行"而不是"执行失败"——两者的副作用含义完全不同。
    /// </remarks>
    private sealed class LeasedDispatcher : IToolDispatcher
    {
        private readonly IServiceReference<IToolDispatcher> _reference;
        private readonly IReadOnlyList<ToolDescriptor> _tools;

        /// <param name="tools">
        /// 构造时取的描述符快照。为什么可以在构造期取：
        /// 描述符本身已是**冻结副本**（<c>Register</c> 时 clone 过），不含实现对象，
        /// 缓存它不构成"长期捕获实现实例"。而 <see cref="Tools"/> 是每轮同步读取的
        /// 属性，无法套异步租约——真要拒绝，主循环根本发不出工具声明。
        /// </param>
        public LeasedDispatcher(
            IServiceReference<IToolDispatcher> reference, IReadOnlyList<ToolDescriptor> tools)
        {
            _reference = reference;
            _tools = tools;
        }

        public IDisposable Register(IAgentTool tool) =>
            throw new NotSupportedException(
                "经内核解析的 dispatcher 不暴露注册面：工具注册属于插件激活期，" +
                "宿主在运行期注册会绕过内核的生命周期协调。");

        public IReadOnlyList<ToolDescriptor> Tools => _tools;

        public async ValueTask<ToolDispatchResult> DispatchAsync(
            ToolInvocation invocation, CancellationToken cancellationToken = default)
        {
            try
            {
                return await _reference.InvokeAsync(
                    (dispatcher, token) => dispatcher.DispatchAsync(invocation, token),
                    cancellationToken).ConfigureAwait(false);
            }
            catch (ObjectDisposedException)
            {
                return new ToolDispatchResult(
                    invocation.InvocationId, invocation.ToolId,
                    ToolCallOutcome.CancelledBeforeExecution,
                    Diagnostic: "工具提供方已停用，调用未执行。");
            }
        }
    }
}
