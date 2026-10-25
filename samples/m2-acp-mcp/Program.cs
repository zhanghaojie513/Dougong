using System.Text.Json;
using System.Text.Json.Nodes;
using Dougong.Acp;
using Dougong.Agent;
using Dougong.Mcp;
using Microsoft.Extensions.AI;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Dougong.Sample.AcpMcp;

/// <summary>
/// M2 样例：把 MCP server 的工具桥接进 dispatcher，再用 ACP 协议驱动一轮对话。
/// </summary>
/// <remarks>
/// 演示两件事：
/// <list type="number">
/// <item><b>MCP 工具是普通工具</b>——远端工具经过 <see cref="McpToolRegistry"/>
/// 变成 <see cref="IAgentTool"/>，与本地工具走同一个 dispatcher，同样要审批。</item>
/// <item><b>ACP 是标准入口</b>——外部客户端通过 JSON-RPC 驱动，stdout 只走协议帧。</item>
/// </list>
/// <para>
/// 不需要 API Key：模型是脚本化的假实现。它验证的是**接线**，
/// 不是协议形状——后者必须用真实 provider。
/// </para>
/// </remarks>
public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        // 子进程模式：本程序也充当那个 MCP server。
        if (args is ["--mcp-server"])
            return await RunMcpServerAsync();

        return await RunDemoAsync();
    }

    private static async Task<int> RunDemoAsync()
    {
        Console.WriteLine("== 1. 启动 MCP server 子进程并桥接工具 ==");

        var dispatcher = new ToolDispatcher(new AutoApprover());

        // stdio 传输：启动本程序自身作为 server。
        // InheritEnvironmentVariables=false：与 shell 工具同理，宿主密钥不能交给子进程。
        var transport = new StdioClientTransport(new StdioClientTransportOptions
        {
            Name = "sample-server",
            Command = Environment.ProcessPath
                ?? throw new InvalidOperationException("拿不到当前可执行文件路径"),
            Arguments = [.. ExtraArguments()],
            InheritEnvironmentVariables = false,
        }, null);

        await using var client = await McpClient.CreateAsync(transport, null, null)
            .ConfigureAwait(false);

        // 桥接：MCP 工具变成 IAgentTool 注册进同一个 dispatcher。
        // 注意这里的两个设计点：
        //   ① 远端工具的权限一律 IsKnown=false——annotations 只是提示，不是授权依据
        //   ② 一律取工作区独占——无法知道它是否改动了本工作区
        using var registry = await McpToolRegistry
            .ConnectAsync(dispatcher, "sample", client)
            .ConfigureAwait(false);

        Console.WriteLine($"   桥接完成：{registry.ToolCount} 个工具");
        foreach (var tool in dispatcher.Tools)
            Console.WriteLine($"     - {tool.Name} (v{tool.Version})");

        Console.WriteLine();
        Console.WriteLine("== 2. 用 ACP 驱动一轮对话 ==");

        // ACP 宿主：stdin/stdout 是协议通道，日志必须走 stderr。
        // 用 Pipe 而不是 MemoryStream：后者写完再读要求复位位置，
        // 而这里是两个方向各自独立的流，必须是管道语义。
        var toHost = new System.IO.Pipelines.Pipe();
        var fromHost = new System.IO.Pipelines.Pipe();
        var log = new StringWriter();

        var host = new AcpAgentHost(
            toHost.Reader.AsStream(),
            fromHost.Writer.AsStream(),
            context => Task.FromResult(new AcpSessionRuntime(
                new LoopAgentRuntime(new ScriptedClient(), dispatcher),
                dispatcher)),
            log);

        var running = host.RunAsync();

        // 客户端侧：握手 → 建会话 → 提问 → 读事件。
        var clientSide = new AcpClientSide(
            toHost.Writer.AsStream(), fromHost.Reader.AsStream());

        var initialize = await clientSide.RequestAsync(new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = 1,
            ["method"] = "initialize",
            ["params"] = new JsonObject { ["protocolVersion"] = 1 },
        });
        Console.WriteLine($"   initialize → protocolVersion={initialize["result"]?["protocolVersion"]}");

        var session = await clientSide.RequestAsync(new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = 2,
            ["method"] = "session/new",
            ["params"] = new JsonObject
            {
                ["cwd"] = Directory.GetCurrentDirectory(),
                ["mcpServers"] = new JsonArray(),
            },
        });
        var sessionId = session["result"]?["sessionId"]?.GetValue<string>()
            ?? throw new InvalidOperationException($"session/new 失败：{session.ToJsonString()}");
        Console.WriteLine($"   session/new → {sessionId}");

        await clientSide.NotifyAsync(new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = 3,
            ["method"] = "session/prompt",
            ["params"] = new JsonObject
            {
                ["sessionId"] = sessionId,
                ["prompt"] = new JsonArray(new JsonObject
                {
                    ["type"] = "text",
                    ["text"] = "用远端工具回显一句话",
                }),
            },
        });

        Console.WriteLine("   session/prompt → 等待事件…");

        // tool_call 本身就是一种 sessionUpdate 类型（不是 session/update 里嵌字段）。
        var toolCall = await clientSide.ReadUntilAsync("tool_call", _ => true);

        if (toolCall is null)
        {
            Console.Error.WriteLine("   没有收到 tool_call 事件。");
            return 1;
        }

        var call = toolCall["params"]!["update"]!;
        Console.WriteLine(
            $"   收到 tool_call：{call["title"]} status={call["status"]}");

        await clientSide.WaitForResponseAsync(3, TimeSpan.FromSeconds(10));
        Console.WriteLine("   session/prompt 已结束");

        host.Stop();
        try { await running.WaitAsync(TimeSpan.FromSeconds(5)); }
        catch (OperationCanceledException) { }

        Console.WriteLine();
        Console.WriteLine("样例通过：MCP 工具经 dispatcher 执行，且全程由 ACP 协议驱动。");
        return 0;
    }

    /// <summary>
    /// 以子进程方式运行时的附加参数。
    /// </summary>
    /// <remarks>
    /// 用 dll + dotnet 启动时，需要把宿主 dll 作为第一个参数再传。
    /// 直接以 apphost 启动时不需要。这里统一处理：
    /// 若 <see cref="Environment.ProcessPath"/> 是 dotnet 本身，则补上入口程序集路径。
    /// </remarks>
    private static IEnumerable<string> ExtraArguments()
    {
        yield return "--mcp-server";

        var path = Environment.ProcessPath ?? "";
        if (Path.GetFileNameWithoutExtension(path).Equals("dotnet", StringComparison.OrdinalIgnoreCase)
            && System.Reflection.Assembly.GetEntryAssembly()?.Location is { Length: > 0 } entry)
        {
            // dotnet 需要先拿到 dll 路径：把它放在最前面。
            yield return entry;
        }
    }

    /// <summary>子进程模式：一个只提供 echo 工具的最小 MCP server。</summary>
    private static async Task<int> RunMcpServerAsync()
    {
        var transport = new StreamServerTransport(
            Console.OpenStandardInput(),
            Console.OpenStandardOutput(),
            "dougong-sample-server",
            null);

        var options = new McpServerOptions
        {
            ServerInfo = new Implementation { Name = "dougong-sample-server", Version = "1.0.0" },
            Capabilities = new ServerCapabilities
            {
                Tools = new ToolsCapability { ListChanged = false },
            },
            Handlers = new McpServerHandlers
            {
                ListToolsHandler = (_, _) => ValueTask.FromResult(new ListToolsResult
                {
                    Tools =
                    [
                        new Tool
                        {
                            Name = "remote_echo",
                            Description = "把收到的文本原样回显，前缀 remote:",
                            InputSchema = JsonDocument.Parse("""
                                {
                                  "type": "object",
                                  "properties": { "text": { "type": "string" } },
                                  "required": ["text"]
                                }
                                """).RootElement.Clone(),
                            // annotations 只是提示：标注只读不会让 Dougong 放宽权限，
                            // 远端工具的权限一律 IsKnown=false。
                            Annotations = new ToolAnnotations { ReadOnlyHint = true },
                        },
                    ],
                }),
                CallToolHandler = (request, _) =>
                {
                    var args = request.Params?.Arguments;
                    // Arguments 的值是装箱的 JsonElement；GetString 取字符串值。
                    var text = args is not null
                        && args.TryGetValue("text", out var v)
                        && v is System.Text.Json.JsonElement element
                        ? element.GetString() ?? ""
                        : "";
                    return ValueTask.FromResult(new CallToolResult
                    {
                        Content = [new TextContentBlock { Text = $"remote:{text}" }],
                    });
                },
            },
        };

        await using var server = McpServer.Create(transport, options, null, null);
        await server.RunAsync();
        return 0;
    }

    /// <summary>自动批准：样例聚焦接线，不演示审批交互。</summary>
    private sealed class AutoApprover : IToolApprover
    {
        public ValueTask<ToolApprovalDecision> RequestApprovalAsync(
            ToolApprovalRequest request, CancellationToken cancellationToken = default)
        {
            Console.WriteLine($"   [审批] {request.Tool.Name} 权限已知={request.Permission.IsKnown}");
            return ValueTask.FromResult(ToolApprovalDecision.Allow("样例：自动批准"));
        }
    }

    /// <summary>脚本化模型：第一轮请求远端工具，第二轮给出答复。</summary>
    private sealed class ScriptedClient : IChatClient
    {
        private int _turn;

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("只走流式路径。");

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation]
            CancellationToken cancellationToken = default)
        {
            _turn++;

            if (_turn == 1)
            {
                await Task.CompletedTask;
                yield return new ChatResponseUpdate(ChatRole.Assistant,
                [
                    new FunctionCallContent("call-1", "sample__remote_echo",
                        new Dictionary<string, object?> { ["text"] = "hello" }),
                ]);
                yield break;
            }

            await Task.Yield();
            yield return new ChatResponseUpdate(ChatRole.Assistant, "远端已回显。");
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }

    /// <summary>
    /// 极简 ACP 客户端：往 host 的输入流写、从输出流读。
    /// </summary>
    /// <remarks>
    /// 用 <see cref="MemoryStream"/> 对连同进程的 host，因此不需要真实进程。
    /// 真实场景下这两个流是 stdin/stdout。
    /// </remarks>
    private sealed class AcpClientSide
    {
        private readonly Stream _toHost;
        private readonly StreamReader _fromHost;

        public AcpClientSide(Stream hostInput, Stream hostOutput)
        {
            _toHost = hostInput;
            _fromHost = new StreamReader(hostOutput);
        }

        public async Task<JsonNode> RequestAsync(JsonObject message)
        {
            await NotifyAsync(message);
            return await WaitForResponseAsync(
                message["id"]!.GetValue<int>(), TimeSpan.FromSeconds(10));
        }

        public async Task<JsonNode> WaitForResponseAsync(int id, TimeSpan timeout)
        {
            using var cts = new CancellationTokenSource(timeout);
            while (true)
            {
                var line = await _fromHost.ReadLineAsync(cts.Token)
                    ?? throw new TimeoutException($"等待响应 id={id} 时连接关闭");
                var node = JsonNode.Parse(line)!;
                if (node["method"] is null && node["id"]?.GetValue<int>() == id) return node;
            }
        }

        public async Task NotifyAsync(JsonObject message)
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes(
                message.ToJsonString(new JsonSerializerOptions { WriteIndented = false }) + "\n");
            await _toHost.WriteAsync(bytes);
            await _toHost.FlushAsync();
        }

        /// <summary>读到满足条件的 session/update 通知。</summary>
        public async Task<JsonNode?> ReadUntilAsync(
            string sessionUpdate, Func<JsonNode, bool> predicate)
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (true)
            {
                string? line;
                try { line = await _fromHost.ReadLineAsync(cts.Token); }
                catch (OperationCanceledException) { return null; }

                if (line is null) return null;
                var node = JsonNode.Parse(line);
                if (node?["method"]?.GetValue<string>() != "session/update") continue;
                var update = node["params"]?["update"];
                if (update?["sessionUpdate"]?.GetValue<string>() != sessionUpdate) continue;
                if (predicate(update)) return node;
            }
        }
    }
}
