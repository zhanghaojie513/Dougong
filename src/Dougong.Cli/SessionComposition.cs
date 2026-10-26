using Dougong.Acp;
using Dougong.Agent;
using Dougong.Ai;
using Dougong.Mcp;
using Microsoft.Extensions.AI;

namespace Dougong.Cli;

/// <summary>
/// 会话装配：把「一个会话需要哪些零件」集中在一处。
/// </summary>
/// <remarks>
/// 存在的理由是一个真实缺口：REPL 与 ACP 曾各自写一套装配，结果 ACP 模式
/// 只注册了 read_file/write_file，MCP 工具、shell、上下文压缩、用量记账全都没接，
/// 而 REPL 全都有。同一份 SDK 从两个入口暴露出的能力不一致。
/// <para>
/// 治法是让两个入口都调用这里，而不是各自维护一份清单——否则下次加能力还会漏掉一边。
/// </para>
/// </remarks>
public sealed class SessionComposition
{
    private readonly string _workspace;
    private readonly string _sessionId;
    private readonly IChatClient _client;
    private readonly IToolApprover _approver;
    private readonly bool _shellEnabled;
    private readonly int _contextWindow;
    private readonly IReadOnlyList<McpServerSpec> _mcpServers;

    private SessionComposition(
        string sessionId,
        string workspace,
        IChatClient client,
        IToolApprover approver,
        bool shellEnabled,
        int contextWindow,
        IReadOnlyList<McpServerSpec> mcpServers,
        IMemoryInjector? memory = null,
        MemoryStore? memoryStore = null)
    {
        _sessionId = sessionId;
        _workspace = workspace;
        _client = client;
        _approver = approver;
        _shellEnabled = shellEnabled;
        _contextWindow = contextWindow;
        _mcpServers = mcpServers;
        Memory = memory;
        MemoryStore = memoryStore;
    }

    /// <summary>
    /// 按 <c>DOUGONG_MEMORY</c> 决定记忆目录。
    /// </summary>
    /// <remarks>
    /// 默认**关闭**。这不是保守，是数据流决定的：启用记忆意味着
    /// 用户的历史偏好会被写进后续每一次 prompt，并且可能发给远端模型。
    /// `skills-evolution.md §4` 要求这条数据流"分别可观察、可禁用"，
    /// 因此它必须显式开启。
    /// <para>
    /// 取值：<c>1</c>/<c>true</c> 开启（用默认目录），或一个目录路径。
    /// </para>
    /// </remarks>
    /// <param name="sessionDirectory">会话目录，用于推出默认记忆目录。</param>
    public static string? MemoryDirectoryFromEnvironment(string sessionDirectory)
    {
        var value = Environment.GetEnvironmentVariable("DOUGONG_MEMORY");
        return value switch
        {
            null or "" or "0" or "false" => null,
            "1" or "true" => Path.Combine(sessionDirectory, "..", "memory"),
            _ => value,
        };
    }

    /// <summary>从环境变量读取共用配置，构造装配器（不启用记忆）。</summary>
    /// <remarks>
    /// 环境变量是当前两个入口唯一的共同配置来源。ACP 模式的审批器由协议实现，
    /// 因此作为参数传入；其余全部从环境读取。
    /// <para>
    /// 本重载不启用 L1 记忆。需要记忆时用 <see cref="CreateAsync"/>。
    /// 保留它是为了不破坏已有调用点与检查项——"加能力"不该顺手改掉旧签名。
    /// </para>
    /// </remarks>
    public static SessionComposition FromEnvironment(
        string sessionId, string workspace, IChatClient client, IToolApprover approver) =>
        Create(sessionId, workspace, client, approver, memory: null, memoryStore: null);

    /// <summary>
    /// 从环境变量读取共用配置，并按需启用 L1 记忆。
    /// </summary>
    /// <param name="sessionId">会话标识。</param>
    /// <param name="workspace">工作区绝对路径。</param>
    /// <param name="client">模型连接器。</param>
    /// <param name="approver">审批器。</param>
    /// <param name="memoryDirectory">
    /// 记忆文件目录；null 表示不启用记忆。启用与否由调用方按
    /// <c>DOUGONG_MEMORY</c> 决定，这里不读该变量——两个入口的默认值可能不同，
    /// 而"是否启用数据流"必须是显式决定，不能藏在第二个地方。
    /// </param>
    /// <param name="cancellationToken">取消令牌。</param>
    public static async Task<SessionComposition> CreateAsync(
        string sessionId, string workspace, IChatClient client, IToolApprover approver,
        string? memoryDirectory = null, CancellationToken cancellationToken = default)
    {
        // 记忆按工作区分文件：跨工作区默认不传播（skills-evolution.md §3）。
        // 用路径哈希而不是文件名，避免不同工作区撞到同一个文件。
        IMemoryInjector? memory = null;
        MemoryStore? memoryStore = null;
        if (!string.IsNullOrWhiteSpace(memoryDirectory))
        {
            var key = Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(
                    System.Text.Encoding.UTF8.GetBytes(Path.GetFullPath(workspace))))[..16];
            var path = Path.Combine(memoryDirectory, $"{key}.jsonl");
            memoryStore = await MemoryStore.OpenAsync(path, cancellationToken).ConfigureAwait(false);
            memory = new StoreMemoryInjector(memoryStore);
        }

        return Create(sessionId, workspace, client, approver, memory, memoryStore);
    }

    private static SessionComposition Create(
        string sessionId, string workspace, IChatClient client, IToolApprover approver,
        IMemoryInjector? memory, MemoryStore? memoryStore)
    {
        var shellEnabled = Environment.GetEnvironmentVariable("DOUGONG_SHELL") is "1" or "true";
        var contextWindow = int.TryParse(
            Environment.GetEnvironmentVariable("DOUGONG_CONTEXT_WINDOW"), out var window) && window > 0
            ? window
            : 0;

        return new SessionComposition(
            sessionId, workspace, client, approver,
            shellEnabled, contextWindow, ParseMcpServers(), memory, memoryStore);
    }

    /// <summary>本次会话是否启用了 shell 工具。</summary>
    public bool ShellEnabled => _shellEnabled;

    /// <summary>本次会话的上下文窗口；0 表示不压缩。</summary>
    public int ContextWindow => _contextWindow;

    /// <summary>本次会话配置的 MCP server 列表。</summary>
    public IReadOnlyList<McpServerSpec> McpServers => _mcpServers;

    /// <summary>L1 偏好注入器；未启用记忆时为 null。</summary>
    /// <remarks>
    /// 为 null 就是"这两条数据流关闭"的表达：运行时完全不读记忆，
    /// 也不会有任何偏好内容进入提示（验收 6 的"可禁用"）。
    /// </remarks>
    public IMemoryInjector? Memory { get; }

    /// <summary>记忆存储；未启用记忆时为 null。<c>/memory</c> 用它列出全部产物（含未启用）。</summary>
    public MemoryStore? MemoryStore { get; }

    /// <summary>当前工作区。<c>/memory</c> 用它判断作用域是否匹配。</summary>
    public string MemoryWorkspace => _workspace;

    /// <summary>
    /// 建立 dispatcher 并注册全部工具。
    /// </summary>
    /// <remarks>
    /// 工具集是「最小可信集 + 显式开启」：文件读写默认有，shell 与 MCP 需要显式配置。
    /// 这一点两个入口必须一致，否则会出现"CLI 能做但桌面端不能做"的困惑。
    /// </remarks>
    public IToolDispatcher CreateDispatcher(IAgentStore? store) =>
        CreateDispatcher(_approver, store is null ? null : new FixedStoreAccessor(store));

    /// <summary>用访问点建立 dispatcher：内核路径下传入，使替换后自动取新实现。</summary>
    public IToolDispatcher CreateDispatcher(IToolApprover approver, IStoreAccessor? store)

    /// <summary>
    /// 用指定的审批器建立 dispatcher。
    /// </summary>
    /// <param name="approver">
    /// 审批器。内核接线时传入的是**由内核解析得到的适配器**，
    /// 而不是构造本对象时那个实例——否则审批器又成了绕开内核的裸引用。
    /// </param>
    {
        ArgumentNullException.ThrowIfNull(approver);

        // 已知无访问的工具（如 ask_user）不产生审批项：让用户先批准"允许提问"、
        // 再看到问题，等于把一次交互拆成两次。策略收得很窄，见 PermissionPolicyApprover。
        // 显式走 accessor 重载：审批器与存储都经访问点，避免重载解析产生歧义。
        var dispatcher = new ToolDispatcher(
            new PermissionPolicyApprover(approver) is var policy
                ? new FixedApproverAccessor(policy) : null,
            new ToolResourceGate(),
            _sessionId, "turn-1", _workspace,
            TimeSpan.FromMinutes(5),
            new WorkspacePathGuard(_workspace),
            store);

        dispatcher.Register(new ReadFileTool(_workspace));
        dispatcher.Register(new WriteFileTool(_workspace));
        // 计划工具：ACP plan 的唯一数据源。两个入口都要有，否则
        // "CLI 能看计划、ACP 看不到"——正是入口能力不一致的老问题。
        dispatcher.Register(new TodoWriteTool());

        if (_shellEnabled) dispatcher.Register(new ShellTool(_workspace));

        return dispatcher;
    }

    /// <summary>
    /// 建立运行时。用量记账在这里包一层，两个入口自动都有账目。
    /// </summary>
    /// <remarks>
    /// 压缩的摘要器复用同一个连接器：压缩与对话用同一模型，
    /// 避免为摘要单独引入一个 provider 配置。
    /// </remarks>
    public LoopAgentRuntime CreateRuntime(
        IToolDispatcher dispatcher, IAgentStore? store, UsageLedger? ledger = null) =>
        CreateRuntime(
            new FixedDispatcherAccessor(dispatcher),
            store is null ? null : new FixedStoreAccessor(store), ledger);

    /// <summary>用访问点建立运行时；语义同 <see cref="CreateDispatcher(IToolApprover, IStoreAccessor?)"/>。</summary>
    public LoopAgentRuntime CreateRuntime(
        IDispatcherAccessor dispatcher, IStoreAccessor? store, UsageLedger? ledger = null)
    {
        IChatClient effective = ledger is null
            ? _client
            : new UsageRecordingChatClient(
                _client, ledger, _sessionId,
                _client.ProtocolName(), _client.ModelName(), ChatProtocol.OpenAiChatCompletions);

        // 偏好在两条返回路径上都要接：漏掉一条就会出现
        // "开了压缩就没偏好"这类分支相关的怪事。
        if (_contextWindow <= 0)
            return new LoopAgentRuntime(effective, dispatcher, store)
            {
                Memory = Memory,
                MemoryWorkspace = _workspace,
            };

        return new LoopAgentRuntime(
            effective, dispatcher, store,
            new ContextLimits
            {
                ContextWindowTokens = _contextWindow,
                MaxOutputTokens = 4_096,
            },
            new SlidingWindowCompactor(preservedHead: 2, preservedTail: 6),
            (transcript, ct) => SummarizeAsync(effective, transcript, ct))
        {
            Memory = Memory,
            MemoryWorkspace = _workspace,
        };
    }

    /// <summary>
    /// 连接全部 MCP server 并注册其工具，返回需要随会话释放的注册器。
    /// </summary>
    /// <remarks>
    /// 连不上不阻止启动，但必须报错——静默跳过会让用户以为工具已就绪。
    /// </remarks>
    /// <param name="transportFactory">
    /// 可选的传输工厂。为 null 时用内置的 stdio / HTTP 两种构造。
    /// 提供后由它决定每个 server 用什么传输——这是第三方扩展新传输的入口，
    /// **不需要修改本方法**（G16）。
    /// <para>
    /// 参数是规格与日志出口；返回的传输由调用方负责（MCP SDK 会接管其生命周期）。
    /// </para>
    /// </param>
    public async Task<(IReadOnlyList<McpToolRegistry> Registries,
        IReadOnlyList<ModelContextProtocol.Client.McpClient> Clients)> ConnectMcpAsync(
        IToolDispatcher dispatcher,
        TextWriter log,
        CancellationToken cancellationToken = default,
        Func<McpServerSpec, TextWriter, ModelContextProtocol.Client.IClientTransport>? transportFactory = null)
    {
        var registries = new List<McpToolRegistry>();
        var clients = new List<ModelContextProtocol.Client.McpClient>();

        foreach (var spec in _mcpServers)
        {
            try
            {
                ModelContextProtocol.Client.IClientTransport transport = transportFactory is not null
                    ? transportFactory(spec, log)
                    : spec.IsHttp
                    ? new ModelContextProtocol.Client.HttpClientTransport(
                        new ModelContextProtocol.Client.HttpClientTransportOptions
                        {
                            Name = spec.Name,
                            Endpoint = spec.Endpoint!,
                            // Streamable HTTP 是当前标准；SSE 已废弃，不提供开关。
                            TransportMode = ModelContextProtocol.Client.HttpTransportMode.StreamableHttp,
                            ConnectionTimeout = TimeSpan.FromSeconds(60),
                        },
                        null)
                    : new ModelContextProtocol.Client.StdioClientTransport(
                        new ModelContextProtocol.Client.StdioClientTransportOptions
                        {
                            Name = spec.Name,
                            Command = spec.Command
                                ?? throw new InvalidOperationException("stdio server 缺少 command。"),
                            Arguments = [.. spec.Arguments],
                            // 不继承宿主环境：与 shell 工具同理，密钥不能交给远端进程。
                            InheritEnvironmentVariables = false,
                        },
                        null);
                var client = await ModelContextProtocol.Client.McpClient.CreateAsync(
                    transport, null, null, cancellationToken).ConfigureAwait(false);
                clients.Add(client);

                var registry = await McpToolRegistry.ConnectAsync(
                    dispatcher, spec.Name, client, cancellationToken).ConfigureAwait(false);
                registries.Add(registry);

                log.WriteLine($"MCP '{spec.Name}'：已桥接 {registry.ToolCount} 个工具");
            }
            catch (Exception ex)
            {
                log.WriteLine($"MCP '{spec.Name}' 连接失败：{ex.GetType().Name}: {ex.Message}");
            }
        }

        return (registries, clients);
    }

    /// <summary>用同一个连接器生成摘要；失败由循环降级为"保留原历史"。</summary>
    private static async ValueTask<string> SummarizeAsync(
        IChatClient client, string transcript, CancellationToken cancellationToken)
    {
        var messages = new List<ChatMessage>
        {
            new(ChatRole.System,
                "把下面的对话历史压缩成要点，保留任务目标、已做的决定、失败过的尝试与未完成事项。"
                + "不要编造未出现的内容。只输出摘要正文。"),
            new(ChatRole.User, transcript),
        };

        var builder = new System.Text.StringBuilder();
        await foreach (var update in client.GetStreamingResponseAsync(
            messages, cancellationToken: cancellationToken).ConfigureAwait(false))
        {
            if (update.Text is { Length: > 0 } chunk) builder.Append(chunk);
        }

        return builder.ToString();
    }

    /// <summary>解析 <c>DOUGONG_MCP_SERVERS</c>：分号分隔，每个形如 <c>name=command args</c>。</summary>
    public static IReadOnlyList<McpServerSpec> ParseMcpServers() =>
        ParseServers(Environment.GetEnvironmentVariable("DOUGONG_MCP_SERVERS"));

    /// <summary>
    /// 解析一段 server 配置文本。
    /// </summary>
    /// <remarks>
    /// 独立成可传入文本的方法，让检查项能验证解析规则而不依赖进程环境变量。
    /// 直接读环境变量的写法无法被测试——改环境会影响并行执行的其他用例。
    /// </remarks>
    public static IReadOnlyList<McpServerSpec> ParseServers(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return [];

        var results = new List<McpServerSpec>();
        foreach (var entry in raw.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var separator = entry.IndexOf('=');
            if (separator <= 0) continue;

            var name = entry[..separator].Trim();
            var commandLine = entry[(separator + 1)..].Trim();
            if (name.Length == 0 || commandLine.Length == 0) continue;

            // 三种形态，靠值的前缀区分，保持 `name=command args` 的老写法不变：
            //   name=/path/to/server args        → stdio
            //   name=http://host/mcp             → Streamable HTTP
            //   name=https://host/mcp            → Streamable HTTP（TLS）
            if (commandLine.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                || commandLine.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                if (Uri.TryCreate(commandLine, UriKind.Absolute, out var endpoint))
                    results.Add(McpServerSpec.Http(name, endpoint));
                continue;
            }

            var parts = commandLine.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length == 0) continue;

            results.Add(McpServerSpec.Stdio(name, parts[0], parts[1..]));
        }

        return results;
    }
}

/// <summary>
/// ACP 会话的装配：把「一个 ACP 会话需要哪些零件」集中在一处。
/// </summary>
/// <remarks>
/// 独立成方法而不是内联在 <c>Program</c> 的 lambda 里：测试必须走**同一条路径**，
/// 否则测的是测试自己的装配，不是生产装配。
/// 这个区别在本次改动里立刻显现——spike 手写装配时漏了用量记账，
/// 而真实 CLI 有，于是测试报告"ACP 没有账目"，实际是测试装置的问题。
/// </remarks>
public static class AcpComposition
{
    /// <summary>
    /// 构造一个 ACP 会话的运行时与 dispatcher。
    /// </summary>
    /// <param name="approver">
    /// 审批器。生产路径传入 <see cref="AcpApprover"/>；测试可传固定策略。
    /// </param>
    /// <param name="storePath">会话日志路径；null 表示不持久化。</param>
    /// <param name="onMcpError">MCP 连接错误的输出出口。</param>
    /// <param name="hostAccessor">
    /// 延迟取 ACP host。<c>ask_user</c> 工具要向客户端发请求，而 host 在装配之后才存在，
    /// 因此用委托而不是直接传实例——与 <see cref="AcpApprover"/> 同样的处理。
    /// </param>
    public static async Task<AcpSessionRuntime> ComposeAsync(
        AcpSessionContext context,
        IChatClient client,
        IToolApprover approver,
        string? storePath,
        TextWriter onMcpError,
        Func<AcpAgentHost>? hostAccessor = null,
        CancellationToken cancellationToken = default)
    {
        var store = storePath is null ? null : OpenStore(storePath, context.SessionId);

        var composition = await SessionComposition.CreateAsync(
            context.SessionId, context.Workspace, client, approver,
            SessionComposition.MemoryDirectoryFromEnvironment(
                storePath is null
                    ? Path.Combine(context.Workspace, ".dougong")
                    : (Path.GetDirectoryName(Path.GetFullPath(storePath)) ?? context.Workspace)),
            cancellationToken).ConfigureAwait(false);

        // 账目：与 REPL 一样包一层装饰器。
        var ledger = new UsageLedger();

        // 内核接线（G14）：默认启用，与 REPL 一致。DOUGONG_KERNEL=0 退回直连。
        var useKernel = Environment.GetEnvironmentVariable("DOUGONG_KERNEL") is not "0" and not "false";

        IReadOnlyList<McpToolRegistry> registries = [];
        IReadOnlyList<ModelContextProtocol.Client.McpClient> clients = [];
        KernelAgentHost? kernelHost = null;
        IAgentRuntime runtime;
        IToolDispatcher dispatcher;

        if (useKernel)
        {
            kernelHost = await KernelAgentHost.CreateAsync(
                store,
                async (resolvedApprover, resolvedStore, ct) =>
                {
                    var built = composition.CreateDispatcher(resolvedApprover, resolvedStore);
                    // 提问工具只在有 ACP 客户端可问时注册：没有 host 就没有人回答。
                    if (hostAccessor is not null)
                        built.Register(new AcpElicitationTool(hostAccessor(), context.SessionId));

                    var (connectedRegistries, connectedClients) =
                        await composition.ConnectMcpAsync(built, onMcpError, ct)
                            .ConfigureAwait(false);
                    registries = connectedRegistries;
                    clients = connectedClients;
                    return built;
                },
                (resolvedDispatcher, resolvedStore) =>
                    composition.CreateRuntime(resolvedDispatcher, resolvedStore, ledger),
                approver,
                // ACP 模式下 stdout 归协议，诊断必须走调用方给的出口。
                diagnostics: onMcpError,
                cancellationToken: cancellationToken).ConfigureAwait(false);

            runtime = kernelHost.ResolveRuntime();
            // 会话记录里的 dispatcher 与 runtime 出自**同一次**内核装配，
            // 因此仍由内核托管：它只用于诊断/一致性检查，不在会话层派发工具
            // （派发发生在运行时内部，已经过内核租约）。
            dispatcher = kernelHost.ResolveDispatcherView()
                ?? throw new InvalidOperationException(
                    "内核已启用但解析不到 IToolDispatcher：插件装配不完整。");
            onMcpError.WriteLine("[acp] kernel=enabled（运行能力由内核解析）");
        }
        else
        {
            dispatcher = composition.CreateDispatcher(store);
            if (hostAccessor is not null)
                dispatcher.Register(new AcpElicitationTool(hostAccessor(), context.SessionId));

            var (connectedRegistries, connectedClients) = await composition
                .ConnectMcpAsync(dispatcher, onMcpError, cancellationToken)
                .ConfigureAwait(false);
            registries = connectedRegistries;
            clients = connectedClients;
            runtime = composition.CreateRuntime(dispatcher, store, ledger);
        }

        // 注册器与客户端都要随会话释放：否则子进程与注册长期挂着。
        // 释放顺序与创建相反：先注销工具注册，再断开客户端，最后释放内核。
        List<IAsyncDisposable> resources = [.. registries];
        resources.AddRange(clients);
        if (kernelHost is not null) resources.Add(kernelHost);

        return new AcpSessionRuntime(runtime, dispatcher) { SessionResources = resources };
    }

    private static IAgentStore? OpenStore(string path, string sessionId)
    {
        try
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(path));
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            return JsonlAgentStore.OpenAsync(path, sessionId).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"会话存储打开失败：{ex.Message}", ex);
        }
    }
}

/// <summary>MCP 传输形态。</summary>
/// <remarks>
/// 显式枚举而不是靠"哪个字段非空"推断：后者无法容纳第四种形态，
/// 而第三方需要新传输时不该被迫改这个类型（G16）。
/// </remarks>
public enum McpTransportKind
{
    /// <summary>子进程 + stdin/stdout。</summary>
    Stdio,

    /// <summary>Streamable HTTP（含 TLS）。</summary>
    Http,
}

/// <summary>一个 MCP server 的启动规格。</summary>
/// <param name="Command">stdio 形态的可执行文件；HTTP 形态为 null。</param>
/// <param name="Endpoint">HTTP 形态的端点；stdio 形态为 null。</param>
public sealed record McpServerSpec(
    string Name,
    string? Command,
    IReadOnlyList<string> Arguments,
    Uri? Endpoint,
    McpTransportKind Kind = McpTransportKind.Stdio)
{
    public bool IsHttp => Kind is McpTransportKind.Http;

    /// <summary>用 stdio 形态构造。</summary>
    public static McpServerSpec Stdio(string name, string command, IReadOnlyList<string> arguments) =>
        new(name, command, arguments, null, McpTransportKind.Stdio);

    /// <summary>用 HTTP 形态构造。</summary>
    public static McpServerSpec Http(string name, Uri endpoint) =>
        new(name, null, [], endpoint, McpTransportKind.Http);
}

/// <summary>从连接器上取用于记账标签的模型名与协议名。</summary>
/// <remarks>
/// 连接器是任意 <see cref="IChatClient"/>，未必暴露这些信息；
/// 取不到时用占位值，不因为记账而影响调用。
/// </remarks>
public static class ChatClientLabels
{
    public static string ProtocolName(this IChatClient client) =>
        client is Dougong.Ai.HttpClientChatClient http ? http.Protocol.ToString() : "unknown";

    public static string ModelName(this IChatClient client) =>
        client.GetService(typeof(string)) as string ?? "unknown";
}
