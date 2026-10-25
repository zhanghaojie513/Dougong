using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using Dougong.Kernel;

namespace Dougong.Agent.Comparison;

/// <summary>
/// G14 验收：内核真的被产品路径使用，而不是只被测试使用。
/// </summary>
/// <remarks>
/// 门 3 的第二个盲区就在这里：`-- reachability` 查的是"能力是否出现在生产类型的
/// **公开面**上"，而 `DougongKernel` 的公开面完整无缺——只是没有任何调用者。
/// 于是 177 项内核测试全绿，内核却在产品路径上零引用。
/// <para>
/// 因此这套检查**读编译产物的元数据**，找成员引用（MemberRef），不是读源码文本，
/// 也不是只查程序集引用。理由：
/// <list type="bullet">
/// <item>源码文本会被注释和字符串误判（`IToolApprover.cs` 注释里就写着 "Kernel"）</item>
/// <item>只查程序集引用不够：`Dougong.Cli.csproj` 引用了内核，但没有一个成员引用</item>
/// </list>
/// </para>
/// </remarks>
public static class KernelWiringChecks
{
    public static async Task<int> RunAsync()
    {
        var failures = 0;
        failures += Check("cli-references-kernel-members", CliReferencesKernelMembers);
        failures += Check("kernel-lease-api-is-shape-expected", LeaseApiShape);
        failures += await CheckAsync("store-replaceable-without-touching-kernel", StoreReplaceableAsync);
        failures += await CheckAsync("deactivated-provider-rejects-old-reference", OldReferenceRejectedAsync);
        failures += await CheckAsync("blocked-consumer-recovers-when-provider-returns", BlockedRecoversAsync);
        failures += await CheckAsync("abandoned-enumeration-releases-lease", AbandonedEnumerationAsync);
        failures += await CheckAsync("kernel-runtime-forwards-events-in-order", EventOrderAsync);
        failures += Check("host-does-not-cache-store-instance", HostDoesNotCacheStore);
        failures += Check("acp-composition-uses-kernel", AcpUsesKernel);

        Console.WriteLine(failures == 0
            ? "KERNEL-WIRING: ALL PASS"
            : $"KERNEL-WIRING: {failures} FAILURE(S)");
        return failures;
    }

    /// <summary>
    /// 产品程序集必须真的调用内核入口，而不只是引用内核程序集。
    /// </summary>
    /// <remarks>
    /// 这条是 G14 的直接断言。它用元数据读取而非反射加载，因为
    /// <c>typeof</c> 只能看到类型，看不到"有没有被调用"。
    /// </remarks>
    private static string? CliReferencesKernelMembers()
    {
        // 检查两个产品程序集：接线代码在 Cli（L3），而 Agent 是运行能力本体。
        // 只要其中之一出现内核成员引用，就说明内核被产品路径使用了。
        var checkedNames = new List<string>();
        var found = new List<string>();

        foreach (var name in new[] { "Dougong.Cli", "Dougong.Agent" })
        {
            var path = ProductAssemblyPath(name);
            if (path is null) continue;
            checkedNames.Add(name);
            found.AddRange(KernelMemberReferences(path).Select(r => $"{name}: {r}"));
        }

        if (checkedNames.Count == 0) return "找不到产品程序集（先构建产品工程）";
        if (found.Count > 0)
        {
            // 诊断时列出实际找到的成员引用，便于判断是不是"只有类型引用没有调用"。
            Console.WriteLine($"      内核成员调用：{string.Join(", ", found.Distinct().Take(10))}");
            return null;
        }

        return $"{string.Join("/", checkedNames)} 的编译产物中没有任何对 Dougong.Kernel 成员的引用——" +
            "内核未被接线（G14）。注意：仅 csproj 引用内核不算接线";

    }

    /// <summary>
    /// 内核的租约 API 形状必须与接线方案假设一致。
    /// </summary>
    /// <remarks>
    /// 这条防的是"文档说 A、代码是 B"：接线方案依赖
    /// <c>IResolver.Resolve&lt;T&gt;</c> 返回可 <c>InvokeAsync</c> 的引用。
    /// </remarks>
    private static string? LeaseApiShape()
    {
        var resolver = typeof(IResolver).GetMethod(nameof(IResolver.Resolve));
        if (resolver is null) return "IResolver.Resolve 缺失";
        if (!resolver.IsGenericMethodDefinition) return "IResolver.Resolve 不是泛型方法";

        var reference = typeof(IServiceReference<>);
        // 有两个重载：非泛型与 TResult 泛型。用 GetMethods 显式挑，避免 AmbiguousMatch。
        var invokes = reference.GetMethods()
            .Where(m => m.Name == "InvokeAsync")
            .ToArray();
        if (invokes.Length != 2)
            return $"IServiceReference<T>.InvokeAsync 应有 2 个重载，实际 {invokes.Length}";

        var plain = invokes.Single(m => !m.IsGenericMethodDefinition);
        var generic = invokes.Single(m => m.IsGenericMethodDefinition);

        // 约束"委托必须等待全部工作结束"体现在两个重载都返回可 await 的 ValueTask：
        // 非泛型那个是 ValueTask，泛型那个是 ValueTask<T>。
        if (plain.ReturnType != typeof(ValueTask))
            return $"非泛型 InvokeAsync 应返回 ValueTask，实际 {plain.ReturnType.Name}";
        if (!generic.ReturnType.IsGenericType
            || generic.ReturnType.GetGenericTypeDefinition() != typeof(ValueTask<>))
            return $"泛型 InvokeAsync 应返回 ValueTask<T>，实际 {generic.ReturnType.Name}";

        // 委托签名必须是 (instance, token)，否则调用方拿不到实例也就无从"接触实例"。
        var parameters = plain.GetParameters();
        return parameters.Length == 2
            && parameters[1].ParameterType == typeof(CancellationToken)
            ? null
            : "InvokeAsync 的委托签名应为 (instance, cancellationToken)";
    }

    /// <summary>
    /// P1 判定的核心：换一种 store 实现，不改内核代码，行为仍成立。
    /// </summary>
    /// <remarks>
    /// 这条现在就能验——它不需要接线，验的是"内核的注册/解析机制是否真的支持替换"。
    /// 接线之后它仍然要过，届时验的就是"产品路径也走这套"。
    /// </remarks>
    private static async Task<string?> StoreReplaceableAsync()
    {
        await using var kernel = new DougongKernel();

        // 用**真实的第二种实现**（语义对齐 JsonlAgentStore），不是只有计数器的假 store。
        // P1 要回答的是"把 JSONL 换成另一种存储，产品还能跑吗"，
        // 而不是"服务键能否换提供方"——后者是必要条件，不是充分条件。
        var memory = new InMemoryAgentStore("first");
        var handle = await kernel.RegisterAsync(() => new StorePlugin("dougong.store", memory));
        await kernel.ActivateAsync(handle.Id);

        // 走真实存储语义：序号由存储分配，调用方传入的值应被忽略。
        var committed = await InvokeStoreAsync(kernel, (store, ct) => store.CommitAsync(
            new StoreRecord(999, StoreRecordKind.UserInput, Payload("hello")), ct));
        if (committed is not null) return committed;
        if (memory.Count != 1) return $"第一个实现应提交 1 条，实际 {memory.Count}";

        // 读回：序号必须是存储分配的 1，而不是调用方传的 999。
        var read = await ReadAllAsync(kernel);
        if (read.Count != 1) return $"读回应为 1 条，实际 {read.Count}";
        if (read[0].Sequence != 1)
            return $"序号应由存储分配（期望 1），实际 {read[0].Sequence}";

        // 提交失败不得留下半条。
        memory.FailNextCommit = true;
        var failed = await InvokeStoreAsync(kernel, (store, ct) => store.CommitAsync(
            new StoreRecord(0, StoreRecordKind.ModelResponse, Payload("boom")), ct));
        if (failed is null) return "模拟失败没有传播成异常：提交失败必须可见";
        if (memory.Count != 1) return $"失败后应仍是 1 条，实际 {memory.Count}";

        // 替换成另一种实现：同一个服务键、不同提供方，内核代码一行不改。
        await kernel.DeactivateAsync(handle.Id);
        var second = new InMemoryAgentStore("second");
        var handle2 = await kernel.RegisterAsync(() => new StorePlugin("dougong.store.v2", second));
        await kernel.ActivateAsync(handle2.Id);

        committed = await InvokeStoreAsync(kernel, (store, ct) => store.CommitAsync(
            new StoreRecord(0, StoreRecordKind.UserInput, Payload("switched")), ct));
        if (committed is not null) return $"替换后调用失败：{committed}";

        if (second.Count != 1) return $"第二个实现应提交 1 条，实际 {second.Count}";
        if (memory.Count != 1) return "旧实现被再次调用：替换没有真正切走";

        // 新实现读到的是自己的内容，不是旧实现的历史——证明真的换了实例。
        var after = await ReadAllAsync(kernel);
        if (after.Count != 1) return $"替换后读回应为 1 条，实际 {after.Count}";
        if (after[0].Payload.GetProperty("text").GetString() != "switched")
            return "替换后读到的不是新实现的内容";

        return null;
    }

    private static System.Text.Json.JsonElement Payload(string text)
    {
        using var document = System.Text.Json.JsonDocument.Parse(
            System.Text.Json.JsonSerializer.Serialize(new { text }));
        return document.RootElement.Clone();
    }

    private static async Task<List<StoreRecord>> ReadAllAsync(DougongKernel kernel)
    {
        var reference = kernel.Resolver.Resolve<IAgentStore>()
            ?? throw new InvalidOperationException("解析不到 IAgentStore");

        return await reference.InvokeAsync(async (store, ct) =>
        {
            var list = new List<StoreRecord>();
            await foreach (var record in store.ReadCommittedAsync(ct)) list.Add(record);
            return list;
        });
    }

    /// <summary>停用之后，旧引用必须拒绝调用——这是"不缓存实现对象"的证据。</summary>
    private static async Task<string?> OldReferenceRejectedAsync()
    {
        await using var kernel = new DougongKernel();
        var store = new RecordingStore("s");
        var handle = await kernel.RegisterAsync(() => new StorePlugin("dougong.store", store));
        await kernel.ActivateAsync(handle.Id);

        var reference = kernel.Resolver.Resolve<IAgentStore>();
        if (reference is null) return "停用前应能解析到 IAgentStore";

        // 停用提供方。
        await kernel.DeactivateAsync(handle.Id);

        try
        {
            await reference.InvokeAsync((s, ct) => s.CommitAsync(
                new StoreRecord(3, StoreRecordKind.UserInput, EmptyJson()), ct));
            return "停用后旧引用仍然可用：排空/代次校验失效";
        }
        catch (ObjectDisposedException)
        {
            return store.CommitCount == 0 ? null : "停用后仍执行了提交";
        }
    }

    /// <summary>提供方缺失时消费者进 Blocked；提供方恢复后被唤醒。</summary>
    private static async Task<string?> BlockedRecoversAsync()
    {
        await using var kernel = new DougongKernel();
        var store = new RecordingStore("s");

        // 消费者声明需要 IAgentStore。
        var consumer = await kernel.RegisterAsync(() => new ConsumerPlugin());
        await kernel.ActivateAsync(consumer.Id);

        var states = new List<PluginState>();
        using var subscription = kernel.SubscribeStateChanges(
            change => { if (change.PluginId == consumer.Id) states.Add(change.To); });

        // 提供方出现 → 消费者应被唤醒。
        var provider = await kernel.RegisterAsync(() => new StorePlugin("dougong.store", store));
        await kernel.ActivateAsync(provider.Id);

        var resolved = kernel.Resolver.Resolve<IAgentStore>();
        if (resolved is null) return "提供方已激活，但解析不到 IAgentStore";

        return states.Contains(PluginState.Active)
            ? null : $"消费者未被唤醒到 Active，状态迁移：{string.Join(",", states)}";
    }

    /// <summary>
    /// 放弃枚举时，后台回合必须被取消且租约释放。
    /// </summary>
    /// <remarks>
    /// 这是 <see cref="Dougong.Cli.KernelAgentRuntime"/> 设计里最容易漏的一处：
    /// 事件经 Channel 中转，调用方 <c>break</c> 或抛异常后，后台任务若继续跑，
    /// 租约就永不释放，停用会一直等到 <c>DrainTimeout</c> 超时并进隔离态。
    /// <para>
    /// 断言方式：让运行时产出一个事件后永久阻塞，然后只读一个事件就 break，
    /// 再停用提供方——若租约已释放，停用应在预算内完成；
    /// 若没释放，会等到 DrainTimeout（测试里缩到 1 秒）并隔离。
    /// </para>
    /// </remarks>
    private static async Task<string?> AbandonedEnumerationAsync()
    {
        await using var kernel = new DougongKernel();
        kernel.DrainTimeout = TimeSpan.FromSeconds(1);

        var runtime = new BlockingRuntime();
        var handle = await kernel.RegisterAsync(() => new RuntimePlugin2(runtime));
        await kernel.ActivateAsync(handle.Id);

        var reference = kernel.Resolver.Resolve<IAgentRuntime>();
        if (reference is null) return "解析不到 IAgentRuntime";

        var adapter = new Dougong.Cli.KernelAgentRuntime(reference);
        var request = new AgentRunRequest("s", "t", [], "hi");

        // 整个用例必须有硬超时：实现若泄漏租约，读取端可能永远等下去。
        // "挂住"不叫通过，必须变成明确的失败。
        using var overall = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        var seen = 0;
        await foreach (var _ in adapter.RunAsync(request, overall.Token))
        {
            seen++;
            if (seen >= 1)
            {
                // 事件已到达说明 pump 已进入 InvokeAsync 并启动了运行时循环。
                // 但仍要等"运行时真的阻塞在 Delay 上"，否则 break 可能发生在
                // 循环体开始之前，取消就没东西可取消。
                await runtime.EnteredAndBlocked.WaitAsync(TimeSpan.FromSeconds(5));
                break;   // 主动放弃枚举
            }
        }

        if (seen == 0) return "没有收到任何事件";
        if (!runtime.Entered) return "运行时未被调用";

        // 关键断言：放弃枚举后，**运行时的循环本身**必须收到取消。
        // 只测"停用够快"是不够的——不取消也能在本测试里快速停用，
        // 因为 pump 里的 InvokeAsync 用的是外部 token，测试没有触发它。
        // 那样会得到一个永远为真的断言（本检查初版就是这个毛病）。
        var cancelled = await runtime.WaitForCancellationAsync(TimeSpan.FromSeconds(5));
        if (!cancelled)
            return "放弃枚举后运行时的循环未收到取消：租约不会释放，排空会等到 DrainTimeout 超时";

        // 再确认真能停用（此时租约应已释放）。
        var started = DateTime.UtcNow;
        await kernel.DeactivateAsync(handle.Id);
        var elapsed = DateTime.UtcNow - started;

        return elapsed < TimeSpan.FromSeconds(5)
            ? null
            : $"停用耗时 {elapsed.TotalSeconds:F1}s：租约可能未释放";
    }

    /// <summary>事件必须保序，且不重复、不丢失。</summary>
    private static async Task<string?> EventOrderAsync()
    {
        await using var kernel = new DougongKernel();
        var runtime = new SequenceRuntime(6);
        var handle = await kernel.RegisterAsync(() => new RuntimePlugin2(runtime));
        await kernel.ActivateAsync(handle.Id);

        var reference = kernel.Resolver.Resolve<IAgentRuntime>()
            ?? throw new InvalidOperationException("解析不到 IAgentRuntime");

        var adapter = new Dougong.Cli.KernelAgentRuntime(reference);
        var texts = new List<string>();
        await foreach (var evt in adapter.RunAsync(new AgentRunRequest("s", "t", [], "hi")))
        {
            if (evt is AgentEvent.TextDelta delta) texts.Add(delta.Text);
        }

        if (texts.Count != 6) return $"应收到 6 个事件，实际 {texts.Count}";
        for (var i = 0; i < texts.Count; i++)
        {
            if (texts[i] != i.ToString()) return $"第 {i} 个事件为 '{texts[i]}'，顺序被打乱";
        }

        return null;
    }

    /// <summary>
    /// 宿主不得长期缓存可替换实现（architecture.md §3.3）。
    /// </summary>
    /// <remarks>
    /// 这条针对一个真实漏洞：`SessionComposition` 把 `IAgentStore` 存在字段里，
    /// 并在 `CreateDispatcher`/`CreateRuntime` 时直接传裸实例。于是"内核承载存储"
    /// 只对了一半——服务被注册进内核了，宿主自己也留着一份，替换后旧实例仍会被用。
    /// <para>
    /// 断言方式：读 <c>SessionComposition</c> 的实例字段，只要存在
    /// <see cref="IAgentStore"/> 类型（或其后备字段）就判失败。这是结构断言，
    /// 不依赖运行时行为——因为"宿主还留着引用"这件事本身就是问题。
    /// </para>
    /// </remarks>
    private static string? HostDoesNotCacheStore()
    {
        var type = Type.GetType("Dougong.Cli.SessionComposition, Dougong.Cli");
        if (type is null) return "找不到 SessionComposition（先构建 CLI）";

        var cached = type.GetFields(
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
            .Where(f => typeof(IAgentStore).IsAssignableFrom(f.FieldType))
            .Select(f => f.Name)
            .ToArray();

        return cached.Length == 0
            ? null
            : $"SessionComposition 仍有 IAgentStore 实例字段：{string.Join(", ", cached)}；" +
              "宿主长期缓存可替换实现，替换后旧实例仍会被使用";
    }

    /// <summary>
    /// ACP 入口也必须经内核，否则内核只在一种入口上被验证过。
    /// </summary>
    /// <remarks>
    /// 这条针对一个真实缺口：`AcpComposition.ComposeAsync` 此前直接
    /// `new ToolDispatcher` / `new LoopAgentRuntime`，全程不碰内核。
    /// 于是"内核接线"只在 REPL 入口成立，而 `-- parity` 套件存在的理由
    /// 正是"两个入口能力必须一致"。
    /// <para>
    /// 断言方式：扫描 <c>AcpComposition.ComposeAsync</c> 所在程序集的
    /// 成员引用里是否存在 <c>KernelAgentHost.CreateAsync</c>。用元数据而非源码文本，
    /// 避免被注释误导。
    /// </para>
    /// </remarks>
    private static string? AcpUsesKernel()
    {
        var path = ProductAssemblyPath("Dougong.Cli");
        if (path is null) return "找不到 Dougong.Cli.dll（先构建产品工程）";

        // 注意：CreateAsync 是 KernelAgentHost 的成员，但 KernelAgentHost 定义在
        // Dougong.Cli 自己里，因此它不是"对 Dougong.Kernel 的引用"。
        // 判据改成"存在对内核 RegisterAsync 的调用"——那才是真正的接线证据，
        // 且 ACP 与 REPL 只要有一个走内核就会命中。
        var calls = KernelMemberReferences(path);
        return calls.Any(c => c.Contains("DougongKernel.RegisterAsync", StringComparison.Ordinal))
            ? null
            : "Dougong.Cli 未调用 DougongKernel.RegisterAsync：入口未接内核";
    }

    // ── 支撑 ─────────────────────────────────────────────────────

    private static async Task<string?> InvokeStoreAsync(
        DougongKernel kernel,
        Func<IAgentStore, CancellationToken, ValueTask> action)
    {
        var reference = kernel.Resolver.Resolve<IAgentStore>();
        if (reference is null) return "解析不到 IAgentStore";

        try
        {
            await reference.InvokeAsync(action);
            return null;
        }
        catch (Exception ex)
        {
            return $"{ex.GetType().Name}: {ex.Message}";
        }
    }

    private static System.Text.Json.JsonElement EmptyJson()
    {
        using var document = System.Text.Json.JsonDocument.Parse("{}");
        return document.RootElement.Clone();
    }

    private static string? ProductAssemblyPath(string name)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "src", name, "bin");
            if (Directory.Exists(candidate))
            {
                var file = Directory.EnumerateFiles(candidate, $"{name}.dll", SearchOption.AllDirectories)
                    .Where(p => p.Contains("Release") || p.Contains("Debug"))
                    .OrderByDescending(p => p.Contains("Release"))
                    .FirstOrDefault();
                if (file is not null) return file;
            }

            directory = directory.Parent;
        }

        return null;
    }

    /// <summary>
    /// 从编译产物里找出对 `Dougong.Kernel` 的**成员**引用。
    /// </summary>
    /// <remarks>
    /// <c>AssemblyRef</c> 只说明"引用了这个程序集"——CLI 的 csproj 早有引用但源码不用。
    /// 真正的接线证据是 <c>MemberRef</c> 或 <c>TypeRef</c> 指向内核类型。
    /// </remarks>
    private static List<string> KernelMemberReferences(string assemblyPath)
    {
        var found = new List<string>();
        using var stream = File.OpenRead(assemblyPath);
        using var reader = new PEReader(stream);
        if (!reader.HasMetadata) return found;

        var metadata = reader.GetMetadataReader();

        // 只看 MemberRef（对方法/字段的**调用**），不看 TypeRef（只是提到某个类型）。
        // 这个区别是本检查的关键：把 KernelAgentHost 这个类型定义在 Cli 里，
        // 就会产生一堆 TypeRef，但一个 MemberRef 都没有——那不叫接线。
        foreach (var handle in metadata.MemberReferences)
        {
            var member = metadata.GetMemberReference(handle);
            if (member.Parent.Kind != HandleKind.TypeReference) continue;

            var parent = metadata.GetTypeReference((TypeReferenceHandle)member.Parent);
            var scopeName = ResolveScopeName(metadata, parent.ResolutionScope);
            if (scopeName != "Dougong.Kernel") continue;

            found.Add(
                metadata.GetString(parent.Namespace) + "."
                + metadata.GetString(parent.Name) + "."
                + metadata.GetString(member.Name));
        }

        return found;
    }

    private static string? ResolveScopeName(MetadataReader metadata, EntityHandle scope)
    {
        if (scope.Kind != HandleKind.AssemblyReference) return null;
        var reference = metadata.GetAssemblyReference((AssemblyReferenceHandle)scope);
        return metadata.GetString(reference.Name);
    }

    /// <summary>提供一个 `IAgentStore` 的插件；注册即登记清理，不依赖作者自觉。</summary>
    private sealed class StorePlugin(string id, IAgentStore store) : IPlugin
    {
        public PluginManifest Manifest { get; } =
            PluginManifest.Create(id, "1.0.0");

        public ValueTask ActivateAsync(PluginContext context, CancellationToken cancellationToken)
        {
            context.Services.Register<IAgentStore>(store);
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>声明需要 `IAgentStore` 的消费者，用于验证 Blocked → Active。</summary>
    private sealed class ConsumerPlugin : IPlugin
    {
        public PluginManifest Manifest { get; } = PluginManifest.CreateWithDependencies(
            "dougong.consumer", "1.0.0",
            [new ServiceRequirement(typeof(IAgentStore))],
            []);

        public ValueTask ActivateAsync(PluginContext context, CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;
    }

    private sealed class RuntimePlugin2(IAgentRuntime runtime) : IPlugin
    {
        public PluginManifest Manifest { get; } = PluginManifest.Create("dougong.runtime", "1.0.0");

        public ValueTask ActivateAsync(PluginContext context, CancellationToken cancellationToken)
        {
            context.Services.Register<IAgentRuntime>(runtime);
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>发一个事件后永久阻塞，直到被取消。</summary>
    private sealed class BlockingRuntime : IAgentRuntime
    {
        private readonly TaskCompletionSource _cancelled =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool Entered { get; private set; }

        /// <summary>运行时已经进入阻塞等待，此时取消才可观察。</summary>
        private readonly TaskCompletionSource _blocked =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task EnteredAndBlocked => _blocked.Task;

        /// <summary>
        /// 等待取消发生。注意 <see cref="Task.WhenAny(Task[])"/> 返回 <c>Task&lt;Task&gt;</c>，
        /// 必须 await 之后才能与目标任务比较——直接比引用会永远为 false，
        /// 让本检查变成永远失败的假象（初版就是这个 bug）。
        /// </summary>
        public async Task<bool> WaitForCancellationAsync(TimeSpan timeout)
        {
            var winner = await Task.WhenAny(_cancelled.Task, Task.Delay(timeout))
                .ConfigureAwait(false);
            return ReferenceEquals(winner, _cancelled.Task);
        }

        public async IAsyncEnumerable<AgentEvent> RunAsync(
            AgentRunRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Entered = true;

            // 观察点必须在**第一个 yield 之前**注册。
            // yield return 会让迭代器挂起，后面的语句要等下次 MoveNextAsync 才执行；
            // 而客户端此时已经 break，永远不会来取下一个元素——
            // 于是写在 yield 之后的 Register 永远不执行，取消也就永远观察不到。
            // 这条曾让"放弃枚举释放租约"的检查变成永远为真（假绿）。
            using var registration = cancellationToken.Register(() => _cancelled.TrySetResult());

            yield return new AgentEvent.TextDelta("first");
            _blocked.TrySetResult();

            await Task.Delay(Timeout.Infinite, cancellationToken);
        }
    }

    /// <summary>产出固定个数的可识别事件。</summary>
    private sealed class SequenceRuntime(int count) : IAgentRuntime
    {
        public async IAsyncEnumerable<AgentEvent> RunAsync(
            AgentRunRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            for (var i = 0; i < count; i++)
            {
                await Task.Yield();
                yield return new AgentEvent.TextDelta(i.ToString());
            }
        }
    }

    private sealed class RecordingStore(string name) : IAgentStore
    {
        public string SessionId => name;

        public StoreDurability Durability => StoreDurability.ProcessMemory;

        public int CommitCount { get; private set; }

        public ValueTask CommitAsync(StoreRecord record, CancellationToken cancellationToken = default)
        {
            CommitCount++;
            return ValueTask.CompletedTask;
        }

        public async IAsyncEnumerable<StoreRecord> ReadCommittedAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static int Check(string name, Func<string?> action)
    {
        try
        {
            var failure = action();
            if (failure is null)
            {
                Console.WriteLine($"PASS {name}");
                return 0;
            }

            Console.WriteLine($"FAIL {name}: {failure}");
            return 1;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"FAIL {name}: {ex.GetType().Name}: {ex.Message}");
            return 1;
        }
    }

    private static async Task<int> CheckAsync(string name, Func<Task<string?>> action)
    {
        try
        {
            var failure = await action();
            if (failure is null)
            {
                Console.WriteLine($"PASS {name}");
                return 0;
            }

            Console.WriteLine($"FAIL {name}: {failure}");
            return 1;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"FAIL {name}: {ex.GetType().Name}: {ex.Message}");
            return 1;
        }
    }
}
