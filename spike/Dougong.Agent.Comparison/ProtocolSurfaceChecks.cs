using System.Text.Json;
using System.Text.Json.Nodes;
using Dougong.Acp;
using Dougong.Agent;
using Microsoft.Extensions.AI;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Dougong.Agent.Comparison;

/// <summary>
/// M2 剩余协议面验收：MCP resources/prompts、HTTP transport、ACP 客户端能力拒绝。
/// </summary>
/// <remarks>
/// 与既有套件的分工：`-- mcp` 覆盖工具桥接与变化检测，这里覆盖**非工具**的远端面。
/// <para>
/// 用真实的 client/server 内存配对，不 mock——资源与 Prompt 的形状只有真实 SDK 说了算。
/// </para>
/// </remarks>
public static class ProtocolSurfaceChecks
{
    public static async Task<int> RunAsync()
    {
        var failures = 0;
        failures += await CheckAsync("lists-remote-resources", ListResourcesAsync);
        failures += await CheckAsync("reads-resource-text", ReadResourceAsync);
        failures += await CheckAsync("resource-read-failure-is-recoverable", ReadFailureAsync);
        failures += await CheckAsync("truncates-oversized-resource", TruncationAsync);
        failures += await CheckAsync("lists-remote-prompts", ListPromptsAsync);
        failures += await CheckAsync("expands-prompt-messages", GetPromptAsync);
        failures += await CheckAsync("parses-stdio-spec", ParseStdioAsync);
        failures += await CheckAsync("parses-http-spec", ParseHttpAsync);
        failures += await CheckAsync("acp-rejects-fs-requests", AcpRejectsFsAsync);
        failures += await CheckAsync("acp-rejects-terminal-requests", AcpRejectsTerminalAsync);

        Console.WriteLine(failures == 0
            ? "PROTOCOL-SURFACE: ALL PASS"
            : $"PROTOCOL-SURFACE: {failures} FAILURE(S)");
        return failures;
    }

    // ── MCP 资源与 Prompt ───────────────────────────────────────

    private static async Task<string?> ListResourcesAsync()
    {
        var (client, _) = await StartServerAsync();
        await using (client)
        {
            var catalog = new Dougong.Mcp.McpResourceCatalog(client, "demo");
            var resources = await catalog.ListResourcesAsync();

            // server 暴露 3 个（a、b、huge），逐个核对而不是只比数量。
            var uris = resources.Select(r => r.Uri).OrderBy(u => u, StringComparer.Ordinal).ToArray();
            return uris.SequenceEqual(["file://docs/a.md", "file://docs/b.md", "file://docs/huge.md"])
                ? null : $"资源 URI 不符：{string.Join(",", uris)}";
        }
    }

    private static async Task<string?> ReadResourceAsync()
    {
        var (client, _) = await StartServerAsync();
        await using (client)
        {
            var catalog = new Dougong.Mcp.McpResourceCatalog(client, "demo");
            var result = await catalog.ReadAsync("file://docs/a.md");

            if (!result.Succeeded) return $"读取失败：{result.Failure}";
            return result.Text?.Contains("alpha content", StringComparison.Ordinal) == true
                ? null : $"内容不符：{result.Text}";
        }
    }

    /// <summary>读取失败必须可恢复（返回失败原因），不能让会话崩掉。</summary>
    private static async Task<string?> ReadFailureAsync()
    {
        var (client, _) = await ServerWithFailingReadAsync();
        await using (client)
        {
            var catalog = new Dougong.Mcp.McpResourceCatalog(client, "demo");

            Dougong.Mcp.McpResourceReadResult result;
            try
            {
                result = await catalog.ReadAsync("file://docs/broken.md");
            }
            catch (Exception ex)
            {
                return $"读取失败未被捕获，直接抛出了 {ex.GetType().Name}";
            }

            if (result.Succeeded) return "远端报错却返回成功";
            return result.Failure is { Length: > 0 } ? null : "失败原因缺失";
        }
    }

    /// <summary>超大资源必须截断并标记，不能静默截断。</summary>
    private static async Task<string?> TruncationAsync()
    {
        var (client, _) = await StartServerAsync();
        await using (client)
        {
            var catalog = new Dougong.Mcp.McpResourceCatalog(client, "demo");
            // 第 3 个资源是超大内容。
            var result = await catalog.ReadAsync("file://docs/huge.md");

            if (!result.Succeeded) return $"读取失败：{result.Failure}";

            if (result.Text is null) return "内容缺失";
            if (result.Text.Length > Dougong.Mcp.McpResourceCatalog.MaxResourceChars)
                return $"内容超过上限：{result.Text.Length}";

            return result.Truncated ? null : "内容被截断却未标记";
        }
    }

    private static async Task<string?> ListPromptsAsync()
    {
        var (client, _) = await StartServerAsync();
        await using (client)
        {
            var catalog = new Dougong.Mcp.McpResourceCatalog(client, "demo");
            var prompts = await catalog.ListPromptsAsync();

            if (prompts.Count != 1) return $"应列出 1 个 Prompt，实际 {prompts.Count}";

            var prompt = prompts[0];
            if (prompt.Name != "review") return $"Prompt 名不符：{prompt.Name}";
            return prompt.Arguments.Count == 1 && prompt.Arguments[0].Name == "code"
                ? null : $"Prompt 参数不符：{string.Join(",", prompt.Arguments.Select(a => a.Name))}";
        }
    }

    private static async Task<string?> GetPromptAsync()
    {
        var (client, _) = await StartServerAsync();
        await using (client)
        {
            var catalog = new Dougong.Mcp.McpResourceCatalog(client, "demo");
            var result = await catalog.GetPromptAsync("review",
                new Dictionary<string, string> { ["code"] = "let x = 1;" });

            if (!result.Succeeded) return $"取 Prompt 失败：{result.Failure}";
            return result.Messages.Any(m => m.Contains("let x = 1;", StringComparison.Ordinal))
                ? null : $"Prompt 展开内容不符：{string.Join("|", result.Messages)}";
        }
    }

    // ── 配置解析 ────────────────────────────────────────────────

    private static Task<string?> ParseStdioAsync()
    {
        const string raw = "a=/usr/bin/server --flag x";
        var specs = Dougong.Cli.SessionComposition.ParseServers(raw);

        if (specs.Count != 1) return Task.FromResult<string?>($"应解析 1 项，实际 {specs.Count}");
        var spec = specs[0];
        if (spec.IsHttp) return Task.FromResult<string?>("stdio 形态被误判为 HTTP");
        if (spec.Command != "/usr/bin/server") return Task.FromResult<string?>($"command 不符：{spec.Command}");
        if (spec.Arguments.Count != 2 || spec.Arguments[0] != "--flag")
            return Task.FromResult<string?>($"参数不符：{string.Join(",", spec.Arguments)}");

        return Task.FromResult<string?>(null);
    }

    private static Task<string?> ParseHttpAsync()
    {
        const string raw = "b=https://example.com/mcp;c=http://localhost:8080/mcp";
        var specs = Dougong.Cli.SessionComposition.ParseServers(raw);

        if (specs.Count != 2) return Task.FromResult<string?>($"应解析 2 项，实际 {specs.Count}");

        if (!specs[0].IsHttp || specs[0].Endpoint?.Host != "example.com")
            return Task.FromResult<string?>("https 形态解析不符");

        if (!specs[1].IsHttp || specs[1].Endpoint?.Port != 8080)
            return Task.FromResult<string?>("http 形态端口不符");

        // HTTP 形态不应带 command。
        return Task.FromResult<string?>(specs[0].Command is null ? null : "HTTP 形态不应有 command");
    }

    // ── ACP 客户端能力拒绝 ───────────────────────────────────────

    private static async Task<string?> AcpRejectsFsAsync()
    {
        foreach (var method in new[] { "fs/read_text_file", "fs/write_text_file" })
        {
            var failure = await ExpectAcpErrorAsync(method, -32601,
                expectHint: "dispatcher");
            if (failure is not null) return $"{method}: {failure}";
        }

        return null;
    }

    private static async Task<string?> AcpRejectsTerminalAsync()
    {
        foreach (var method in new[]
        {
            "terminal/create", "terminal/output", "terminal/release",
            "terminal/wait_for_exit", "terminal/kill",
        })
        {
            var failure = await ExpectAcpErrorAsync(method, -32601, expectHint: "shell");
            if (failure is not null) return $"{method}: {failure}";
        }

        return null;
    }

    /// <summary>
    /// 验证 ACP 对某个方法返回指定错误码，且诊断里给出替代做法的线索。
    /// </summary>
    private static async Task<string?> ExpectAcpErrorAsync(
        string method, int expectedCode, string expectHint)
    {
        await using var harness = new MiniAcpHarness();
        await harness.RequestAsync(new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = 1,
            ["method"] = "initialize",
            ["params"] = new JsonObject { ["protocolVersion"] = 1 },
        });

        var response = await harness.RequestAsync(new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = 2,
            ["method"] = method,
            ["params"] = new JsonObject { ["sessionId"] = "x" },
        });

        if (response["error"] is null) return "未返回错误";

        var code = response["error"]!["code"]?.GetValue<int>();
        if (code != expectedCode) return $"错误码应为 {expectedCode}，实际 {code}";

        var message = response["error"]!["message"]?.GetValue<string>() ?? string.Empty;
        return message.Contains(expectHint, StringComparison.OrdinalIgnoreCase)
            ? null : $"诊断未指出替代做法（应含 '{expectHint}'）：{message}";
    }

    /// <summary>极简 ACP 客户端，只用于发请求读响应。</summary>
    private sealed class MiniAcpHarness : IAsyncDisposable
    {
        private readonly System.IO.Pipelines.Pipe _toHost = new();
        private readonly System.IO.Pipelines.Pipe _fromHost = new();
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _loop;
        private readonly StreamReader _reader;
        private readonly Stream _writer;

        public MiniAcpHarness()
        {
            var host = new AcpAgentHost(
                _toHost.Reader.AsStream(leaveOpen: true),
                _fromHost.Writer.AsStream(leaveOpen: true),
                context => Task.FromResult(new AcpSessionRuntime(
                    new LoopAgentRuntime(new FixedClient(), new ToolDispatcher()),
                    new ToolDispatcher())),
                new StringWriter());

            _loop = Task.Run(() => host.RunAsync(_stop.Token));
            _writer = _toHost.Writer.AsStream(leaveOpen: true);
            _reader = new StreamReader(
                _fromHost.Reader.AsStream(leaveOpen: true),
                System.Text.Encoding.UTF8, false, 8192, leaveOpen: true);
        }

        public async Task<JsonNode> RequestAsync(JsonObject message)
        {
            var json = message.ToJsonString(new JsonSerializerOptions { WriteIndented = false });
            await _writer.WriteAsync(System.Text.Encoding.UTF8.GetBytes(json + "\n"));
            await _writer.FlushAsync();

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var id = message["id"]!.ToJsonString();

            while (true)
            {
                var line = await _reader.ReadLineAsync(cts.Token)
                    ?? throw new TimeoutException("ACP host 未响应");
                var node = JsonNode.Parse(line)!;
                if (node["id"]?.ToJsonString() == id && node["method"] is null) return node;
            }
        }

        public async ValueTask DisposeAsync()
        {
            _stop.Cancel();
            try { await _loop.WaitAsync(TimeSpan.FromSeconds(2)); } catch { }
        }
    }

    private sealed class FixedClient : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> m, ChatOptions? o = null, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> m, ChatOptions? o = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.CompletedTask;
            yield return new ChatResponseUpdate(ChatRole.Assistant, "ok");
        }

        public object? GetService(Type t, object? k = null) => null;
        public void Dispose() { }
    }

    // ── 测试 server ─────────────────────────────────────────────

    /// <summary>带资源与 Prompt 的最小 server。</summary>
    private static async Task<(McpClient Client, string SessionId)> StartServerAsync(
        bool failReads = false)
    {
        var clientToServer = new System.IO.Pipelines.Pipe();
        var serverToClient = new System.IO.Pipelines.Pipe();

        var serverTransport = new StreamServerTransport(
            clientToServer.Reader.AsStream(leaveOpen: true),
            serverToClient.Writer.AsStream(leaveOpen: true),
            "surface-server", null);

        var options = new McpServerOptions
        {
            ServerInfo = new Implementation { Name = "surface-server", Version = "1.0.0" },
            Capabilities = new ServerCapabilities
            {
                Tools = new ToolsCapability { ListChanged = false },
                Resources = new ResourcesCapability { ListChanged = true, Subscribe = false },
                Prompts = new PromptsCapability { ListChanged = true },
            },
            Handlers = new McpServerHandlers
            {
                ListToolsHandler = (_, _) =>
                    ValueTask.FromResult(new ListToolsResult { Tools = [] }),

                ListResourcesHandler = (_, _) => ValueTask.FromResult(new ListResourcesResult
                {
                    Resources =
                    [
                        new Resource { Uri = "file://docs/a.md", Name = "a", MimeType = "text/markdown" },
                        new Resource { Uri = "file://docs/b.md", Name = "b", MimeType = "text/markdown" },
                        new Resource { Uri = "file://docs/huge.md", Name = "huge", MimeType = "text/plain" },
                    ],
                }),

                ReadResourceHandler = (request, _) =>
                {
                    var uri = request.Params?.Uri ?? string.Empty;
                    if (failReads || uri.Contains("broken", StringComparison.Ordinal))
                        throw new InvalidOperationException("模拟远端读取失败");

                    var text = uri.EndsWith("huge.md", StringComparison.Ordinal)
                        // 超过上限的内容：用于验证截断与标记。
                        ? new string('x', Dougong.Mcp.McpResourceCatalog.MaxResourceChars + 1000)
                        : uri.EndsWith("a.md", StringComparison.Ordinal)
                            ? "alpha content"
                            : "beta content";

                    return ValueTask.FromResult(new ReadResourceResult
                    {
                        Contents = [new TextResourceContents { Uri = uri, MimeType = "text/plain", Text = text }],
                    });
                },

                ListPromptsHandler = (_, _) => ValueTask.FromResult(new ListPromptsResult
                {
                    Prompts =
                    [
                        new Prompt
                        {
                            Name = "review",
                            Description = "review code",
                            Arguments = [new PromptArgument { Name = "code", Required = true }],
                        },
                    ],
                }),

                GetPromptHandler = (request, _) =>
                {
                    var code = request.Params?.Arguments is { } args
                        && args.TryGetValue("code", out var value)
                        ? value.ToString()
                        : string.Empty;

                    return ValueTask.FromResult(new GetPromptResult
                    {
                        Description = "review",
                        Messages =
                        [
                            new PromptMessage
                            {
                                Role = Role.User,
                                Content = new TextContentBlock { Text = $"review this: {code}" },
                            },
                        ],
                    });
                },
            },
        };

        _ = Task.Run(async () =>
        {
            await using var server = McpServer.Create(serverTransport, options, null, null);
            await server.RunAsync(CancellationToken.None);
        });

        var clientTransport = new StreamClientTransport(
            clientToServer.Writer.AsStream(leaveOpen: true),
            serverToClient.Reader.AsStream(leaveOpen: true),
            null);

        var client = await McpClient.CreateAsync(clientTransport, null, null, CancellationToken.None);
        return (client, "demo");
    }

    private static Task<(McpClient Client, string SessionId)> ServerWithFailingReadAsync() =>
        StartServerAsync(failReads: true);

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
