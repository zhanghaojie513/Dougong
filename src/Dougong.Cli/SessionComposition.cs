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
    private readonly IAgentStore? _store;
    private readonly IToolApprover _approver;
    private readonly bool _shellEnabled;
    private readonly int _contextWindow;
    private readonly IReadOnlyList<McpServerSpec> _mcpServers;

    private SessionComposition(
        string sessionId,
        string workspace,
        IChatClient client,
        IAgentStore? store,
        IToolApprover approver,
        bool shellEnabled,
        int contextWindow,
        IReadOnlyList<McpServerSpec> mcpServers)
    {
        _sessionId = sessionId;
        _workspace = workspace;
        _client = client;
        _store = store;
        _approver = approver;
        _shellEnabled = shellEnabled;
        _contextWindow = contextWindow;
        _mcpServers = mcpServers;
    }

    /// <summary>从环境变量读取共用配置，构造装配器。</summary>
    /// <remarks>
    /// 环境变量是当前两个入口唯一的共同配置来源。ACP 模式的审批器由协议实现，
    /// 因此作为参数传入；其余全部从环境读取。
    /// </remarks>
    public static SessionComposition FromEnvironment(
        string sessionId, string workspace, IChatClient client,
        IAgentStore? store, IToolApprover approver)
    {
        var shellEnabled = Environment.GetEnvironmentVariable("DOUGONG_SHELL") is "1" or "true";
        var contextWindow = int.TryParse(
            Environment.GetEnvironmentVariable("DOUGONG_CONTEXT_WINDOW"), out var window) && window > 0
            ? window
            : 0;

        return new SessionComposition(
            sessionId, workspace, client, store, approver,
            shellEnabled, contextWindow, ParseMcpServers());
    }

    /// <summary>本次会话是否启用了 shell 工具。</summary>
    public bool ShellEnabled => _shellEnabled;

    /// <summary>本次会话的上下文窗口；0 表示不压缩。</summary>
    public int ContextWindow => _contextWindow;

    /// <summary>本次会话配置的 MCP server 列表。</summary>
    public IReadOnlyList<McpServerSpec> McpServers => _mcpServers;

    /// <summary>
    /// 建立 dispatcher 并注册全部工具。
    /// </summary>
    /// <remarks>
    /// 工具集是「最小可信集 + 显式开启」：文件读写默认有，shell 与 MCP 需要显式配置。
    /// 这一点两个入口必须一致，否则会出现"CLI 能做但桌面端不能做"的困惑。
    /// </remarks>
    public IToolDispatcher CreateDispatcher()
    {
        var dispatcher = new ToolDispatcher(
            _approver,
            new ToolResourceGate(),
            _sessionId, "turn-1", _workspace,
            TimeSpan.FromMinutes(5),
            new WorkspacePathGuard(_workspace),
            _store);

        dispatcher.Register(new ReadFileTool(_workspace));
        dispatcher.Register(new WriteFileTool(_workspace));

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
    public LoopAgentRuntime CreateRuntime(IToolDispatcher dispatcher, UsageLedger? ledger = null)
    {
        IChatClient effective = ledger is null
            ? _client
            : new UsageRecordingChatClient(
                _client, ledger, _sessionId,
                _client.ProtocolName(), _client.ModelName(), ChatProtocol.OpenAiChatCompletions);

        if (_contextWindow <= 0)
            return new LoopAgentRuntime(effective, dispatcher, _store);

        return new LoopAgentRuntime(
            effective, dispatcher, _store,
            new ContextLimits
            {
                ContextWindowTokens = _contextWindow,
                MaxOutputTokens = 4_096,
            },
            new SlidingWindowCompactor(preservedHead: 2, preservedTail: 6),
            (transcript, ct) => SummarizeAsync(effective, transcript, ct));
    }

    /// <summary>
    /// 连接全部 MCP server 并注册其工具，返回需要随会话释放的注册器。
    /// </summary>
    /// <remarks>
    /// 连不上不阻止启动，但必须报错——静默跳过会让用户以为工具已就绪。
    /// </remarks>
    public async Task<(IReadOnlyList<McpToolRegistry> Registries,
        IReadOnlyList<ModelContextProtocol.Client.McpClient> Clients)> ConnectMcpAsync(
        IToolDispatcher dispatcher, TextWriter log, CancellationToken cancellationToken = default)
    {
        var registries = new List<McpToolRegistry>();
        var clients = new List<ModelContextProtocol.Client.McpClient>();

        foreach (var spec in _mcpServers)
        {
            try
            {
                var options = new ModelContextProtocol.Client.StdioClientTransportOptions
                {
                    Name = spec.Name,
                    Command = spec.Command,
                    Arguments = [.. spec.Arguments],
                    // 不继承宿主环境：与 shell 工具同理，密钥不能交给远端进程。
                    InheritEnvironmentVariables = false,
                };

                var transport = new ModelContextProtocol.Client.StdioClientTransport(options, null);
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
    public static IReadOnlyList<McpServerSpec> ParseMcpServers()
    {
        var raw = Environment.GetEnvironmentVariable("DOUGONG_MCP_SERVERS");
        if (string.IsNullOrWhiteSpace(raw)) return [];

        var results = new List<McpServerSpec>();
        foreach (var entry in raw.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var separator = entry.IndexOf('=');
            if (separator <= 0) continue;

            var name = entry[..separator].Trim();
            var commandLine = entry[(separator + 1)..].Trim();
            if (name.Length == 0 || commandLine.Length == 0) continue;

            var parts = commandLine.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length == 0) continue;

            results.Add(new McpServerSpec(name, parts[0], parts[1..]));
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
    public static async Task<AcpSessionRuntime> ComposeAsync(
        AcpSessionContext context,
        IChatClient client,
        IToolApprover approver,
        string? storePath,
        TextWriter onMcpError,
        CancellationToken cancellationToken = default)
    {
        var store = storePath is null ? null : OpenStore(storePath, context.SessionId);

        var composition = SessionComposition.FromEnvironment(
            context.SessionId, context.Workspace, client, store, approver);

        var dispatcher = composition.CreateDispatcher();

        // MCP 工具：与 REPL 同一套 DOUGONG_MCP_SERVERS 配置。
        var (registries, clients) = await composition
            .ConnectMcpAsync(dispatcher, onMcpError, cancellationToken)
            .ConfigureAwait(false);

        // 账目：与 REPL 一样包一层装饰器。
        var ledger = new UsageLedger();
        var runtime = composition.CreateRuntime(dispatcher, ledger);

        // 注册器与客户端都要随会话释放：否则子进程与注册长期挂着。
        // 释放顺序与创建相反：先注销工具注册，再断开客户端。
        List<IAsyncDisposable> resources = [.. registries];
        resources.AddRange(clients);

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

/// <summary>一个 MCP server 的启动规格。</summary>
public sealed record McpServerSpec(
    string Name, string Command, IReadOnlyList<string> Arguments);

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
