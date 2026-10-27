using Dougong.Agent;
using Dougong.Kernel;

namespace Dougong.Hosting;

/// <summary>
/// 内核接线：把运行能力作为插件注册进内核，宿主只通过解析器使用它们。
/// </summary>
/// <remarks>
/// 解决的缺口（G14）：<c>DougongKernel</c> 此前在产品路径上零引用，
/// CLI 直接 <c>new ToolDispatcher</c> / <c>new LoopAgentRuntime</c> 并存下来，
/// 内核的租约、代次绑定、排空、隔离在真实会话中从未生效。
/// <para>
/// <b>为什么单独成包（2026-10-27 从 <c>Dougong.Cli</c> 移出）</b>：这段接线
/// 是 CLI 与 Desktop 都要写一遍的东西。留在一个可执行工程里，第二个宿主只能
/// 照抄——而抄错的后果是静默的：注册顺序错导致解析不到、异常时忘记清理、
/// 用裸实例构造而不是 accessor 版本导致替换插件后旧实例还在跑。这三类都不会
/// 立刻报错。
/// </para>
/// <para>
/// <b>它不是产品层</b>：本类不含"用哪些工具""提示词怎么写""会话怎么开场"
/// 这类决策——那些留在宿主里（见 <c>docs/host-layering-decision.md</c> §4.2）。
/// 这里只有"怎么把零件装进内核"。
/// </para>
/// <para>
/// <b>分层</b>：Kernel(L0) ← Agent(L1) ← Hosting(L2)。Hosting 只依赖前两层，
/// 不依赖 Ai/Mcp/Acp——那些是具体能力，由宿主决定用不用。
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
    private readonly IDisposable? _stateSubscription;
    private readonly TextWriter _diagnostics;

    /// <param name="kernel">内核实例。</param>
    /// <param name="diagnostics">
    /// 诊断输出。必须是 stderr（ACP 模式下 stdout 归协议）。
    /// </param>
    private KernelAgentHost(DougongKernel kernel, TextWriter? diagnostics)
    {
        _kernel = kernel;
        _diagnostics = diagnostics ?? Console.Error;

        // 订阅状态变化并把失败/阻塞打到诊断出口。
        // 这修的是一个真实缺口：内核一直在产生带 Stage/Hint 的 PluginDiagnostic，
        // 但产品代码里**没有任何订阅者**——诊断通道存在，却没人读，用户看不到。
        _stateSubscription = _kernel.SubscribeStateChanges(ReportFailure);
    }

    /// <summary>
    /// 把失败或阻塞的状态变化写到诊断出口。
    /// </summary>
    /// <remarks>
    /// 只报需要人介入的状态：<see cref="PluginState.Failed"/> 与
    /// <see cref="PluginState.Blocked"/>。正常启停不刷屏。
    /// <para>
    /// 输出包含阶段与建议——这是判据 4"能定位到组件"的落点。
    /// 没有 Hint 的阶段（如观察者异常）只报状态与原因，不编造建议。
    /// </para>
    /// </remarks>
    private void ReportFailure(PluginStateChange change)
    {
        if (change.To is not (PluginState.Failed or PluginState.Blocked)) return;

        var diagnostic = change.Diagnostic;
        var stage = diagnostic?.Stage is { } s and not PluginFailureStage.Unknown
            ? $" [{s}]"
            : string.Empty;

        _diagnostics.WriteLine(
            $"[kernel] 插件 '{change.PluginId}' 进入 {change.To}{stage}：{change.Reason ?? "(无原因)"}");

        if (diagnostic is not null)
        {
            _diagnostics.WriteLine($"  详情：{diagnostic.Code}: {diagnostic.Message}");
            if (diagnostic.Hint is { Length: > 0 } hint)
                _diagnostics.WriteLine($"  建议：{hint}");
        }
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
    /// <param name="approver">审批器；会作为插件注册进内核。</param>
    /// <param name="diagnostics">
    /// 诊断输出出口，默认 <see cref="Console.Error"/>。ACP 模式下必须传 stderr：
    /// stdout 归协议所有。
    /// </param>
    /// <param name="cancellationToken">取消令牌。</param>
    public static async Task<KernelAgentHost> CreateAsync(
        IAgentStore? store,
        Func<IToolApprover, IStoreAccessor?, CancellationToken, Task<IToolDispatcher>> createDispatcher,
        Func<IDispatcherAccessor, IStoreAccessor?, IAgentRuntime> createRuntime,
        IToolApprover approver,
        TextWriter? diagnostics = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(createDispatcher);
        ArgumentNullException.ThrowIfNull(createRuntime);
        ArgumentNullException.ThrowIfNull(approver);

        var kernel = new DougongKernel();
        var host = new KernelAgentHost(kernel, diagnostics);

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

        // 先退订再销毁：销毁过程本身会发出 Deactivating/Failed 状态，
        // 此时仍想看到诊断，所以退订放在最后。
        _stateSubscription?.Dispose();
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
        Func<IToolApprover, IStoreAccessor?, CancellationToken, Task<IToolDispatcher>> factory,
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

            // 传 accessor 而不是解析出的实例：accessor 每次提交都会经内核重新取当前实例，
            // 因此替换之后不会继续用旧 store。传裸实例会把"当前"冻结在激活这一刻。
            var store = kernel.Resolver.Resolve<IAgentStore>() is { } storeRef
                ? new KernelStoreAccessor(storeRef)
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
        Func<IDispatcherAccessor, IStoreAccessor?, IAgentRuntime> factory,
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



            // store 对运行时是可选依赖：没有持久化时传 null。
            var store = kernel.Resolver.Resolve<IAgentStore>() is { } storeRef
                ? new KernelStoreAccessor(storeRef)
                : null;

            // 传 accessor 而非裸实例：每次读声明/派发都经租约重新取当前 dispatcher，
            // 因此替换后不再用旧实现（G15 的一半）。
            context.Services.Register<IAgentRuntime>(
                factory(new KernelDispatcherAccessor(reference), store));
        }
    }

    // ── 适配器：把服务引用包装成裸接口，避免宿主缓存实现实例 ─────────

    /// <summary>把内核服务引用适配成 <see cref="IDispatcherAccessor"/>。</summary>
    /// <remarks>
    /// 每次读声明或派发都经 <c>InvokeAsync</c>：替换 dispatcher 后自动用新实例，
    /// 停用时会等这次调用完成再清理。
    /// </remarks>
    private sealed class KernelDispatcherAccessor(IServiceReference<IToolDispatcher> reference)
        : IDispatcherAccessor
    {
        public async ValueTask<IReadOnlyList<ToolDescriptor>> GetToolsAsync(
            CancellationToken cancellationToken = default)
        {
            try
            {
                return await reference.InvokeAsync(
                    (dispatcher, _) => ValueTask.FromResult(dispatcher.Tools),
                    cancellationToken).ConfigureAwait(false);
            }
            catch (ObjectDisposedException)
            {
                // 已停用：没有工具可用是正确结果，而不是"沿用上次的声明"。
                return [];
            }
        }

        public async ValueTask<ToolDispatchResult> DispatchAsync(
            ToolInvocation invocation, CancellationToken cancellationToken = default)
        {
            try
            {
                return await reference.InvokeAsync(
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

    /// <summary>把内核服务引用适配成 <see cref="IStoreAccessor"/>。</summary>
    /// <remarks>
    /// 每次提交都经 <c>InvokeAsync</c> 重新取实例并计入在途，因此：
    /// ① 替换存储后自动用新实例；② 停用时会等这次提交完成再清理。
    /// </remarks>
    private sealed class KernelStoreAccessor(IServiceReference<IAgentStore> reference)
        : IStoreAccessor
    {
        public async ValueTask CommitAsync(
            StoreRecord record, CancellationToken cancellationToken = default)
        {
            await reference.InvokeAsync(
                (store, token) => store.CommitAsync(record, token),
                cancellationToken).ConfigureAwait(false);
        }
    }

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

        /// <param name="reference">dispatcher 的服务引用；每次派发经它取当前实例。</param>
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
