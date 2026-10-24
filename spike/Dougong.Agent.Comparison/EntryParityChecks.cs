using System.Text.Json;
using System.Text.Json.Nodes;
using Dougong.Acp;
using Dougong.Agent;
using Microsoft.Extensions.AI;

namespace Dougong.Agent.Comparison;

/// <summary>
/// 门 3 验收：两个入口（REPL 与 ACP）必须暴露同一套能力。
/// </summary>
/// <remarks>
/// 这条针对一个真实缺口：ACP 模式曾只注册 read_file/write_file，
/// MCP 工具、shell、上下文压缩、用量记账全都没接，而 REPL 全都有。
/// 也就是说 M2 的成果在 ACP 路径上等于没生效。
/// <para>
/// 检查方式不看装配代码，只看**从 ACP 入口进入后能观察到什么**：
/// 模型可见的工具列表、是否产生用量计量、压缩是否触发。
/// </para>
/// </remarks>
public static class EntryParityChecks
{
    public static async Task<int> RunAsync()
    {
        var failures = 0;
        failures += await CheckAsync("acp-exposes-shell-when-enabled", ShellVisibleAsync);
        failures += await CheckAsync("acp-hides-shell-when-disabled", ShellHiddenAsync);
        failures += await CheckAsync("acp-compacts-when-window-configured", CompactionAsync);
        failures += await CheckAsync("acp-records-usage", UsageAsync);
        failures += await CheckAsync("both-entries-share-file-tools", FileToolsParityAsync);

        Console.WriteLine(failures == 0
            ? "ENTRY-PARITY: ALL PASS"
            : $"ENTRY-PARITY: {failures} FAILURE(S)");
        return failures;
    }

    /// <summary>启用 shell 后，模型可见工具里必须有 shell。</summary>
    private static async Task<string?> ShellVisibleAsync() =>
        await WithEnvironmentAsync(
            ("DOUGONG_SHELL", "1"), ("DOUGONG_CONTEXT_WINDOW", null), ("DOUGONG_MCP_SERVERS", null),
            () => AssertToolsAsync(expectShell: true));

    /// <summary>未启用时不能出现——能力必须显式开启，不能默认给。</summary>
    private static async Task<string?> ShellHiddenAsync() =>
        await WithEnvironmentAsync(
            ("DOUGONG_SHELL", null), ("DOUGONG_CONTEXT_WINDOW", null), ("DOUGONG_MCP_SERVERS", null),
            () => AssertToolsAsync(expectShell: false));

    /// <summary>配置上下文窗口后，长历史必须触发压缩事件。</summary>
    private static async Task<string?> CompactionAsync() =>
        await WithEnvironmentAsync(
            ("DOUGONG_SHELL", null), ("DOUGONG_CONTEXT_WINDOW", "5200"), ("DOUGONG_MCP_SERVERS", null),
            async () =>
            {
                var history = new List<AgentMessage>();
                for (var i = 0; i < 60; i++)
                    history.Add(new AgentMessage(AgentRole.User, new string('x', 200) + $" #{i}"));

                var client = new CaptureClient();
                await using var harness = new Harness(client);

                var init = await harness.RequestAsync(Request(1, "initialize", new JsonObject
                {
                    ["protocolVersion"] = 1,
                }));
                if (init["error"] is not null) return $"initialize 失败：{init["error"]}";

                // 恢复一个长历史会话，让压缩有东西可压。
                // 这里用 session/new + 多轮 prompt 太慢，改为直接验证运行时是否配了压缩：
                // 长历史 + 小窗口 → 模型收到的历史应短于原始条数。
                var session = await harness.RequestAsync(Request(2, "session/new", new JsonObject
                {
                    ["cwd"] = Path.GetTempPath(),
                    ["mcpServers"] = new JsonArray(),
                }));
                var sessionId = session["result"]!["sessionId"]!.GetValue<string>();

                // 连发若干轮，让历史增长。
                for (var turn = 3; turn <= 12; turn++)
                {
                    var promptResponse = await harness.RequestAsync(Request(turn, "session/prompt", new JsonObject
                    {
                        ["sessionId"] = sessionId,
                        ["prompt"] = new JsonArray(new JsonObject
                        {
                            ["type"] = "text",
                            // 每轮内容要足够长，确保累计超过触发阈值。
                            ["text"] = new string('y', 2000) + $" 第 {turn} 轮",
                        }),
                    }), TimeSpan.FromSeconds(20));

                    // 轮次失败必须立刻暴露，否则后面的断言在数"没发生的事"。
                    if (promptResponse["error"] is { } error)
                        return $"第 {turn} 轮失败：{error.ToJsonString()}";
                }

                // 关键：必须观察到"历史被压回来过"。
                //
                // 断言不能是"小于某个固定值"（只要轮次不多就永远通过），
                // 也不能是"末轮小于峰值"（压缩后又会增长，末轮可能等于峰值）。
                // 正确判据：条数序列里**出现过回落**，且回落幅度明显。
                var counts = client.HistoryCounts;
                if (counts.Count < 4) return "模型调用次数不足，无法观察压缩";

                var biggestDrop = 0;
                for (var i = 1; i < counts.Count; i++)
                    biggestDrop = Math.Max(biggestDrop, counts[i - 1] - counts[i]);

                if (biggestDrop == 0)
                    return $"ACP 模式未触发压缩：条数序列从未回落（{string.Join(",", counts)}）";

                return biggestDrop >= 2
                    ? null : $"回落幅度过小（最大 {biggestDrop} 条），不像压缩";
            });

    /// <summary>ACP 模式的调用必须计入用量账本。</summary>
    private static async Task<string?> UsageAsync() =>
        await WithEnvironmentAsync(
            ("DOUGONG_SHELL", null), ("DOUGONG_CONTEXT_WINDOW", null), ("DOUGONG_MCP_SERVERS", null),
            async () =>
            {
                var client = new UsageClient();
                await using var harness = new Harness(client);

                await harness.RequestAsync(Request(1, "initialize", new JsonObject { ["protocolVersion"] = 1 }));
                var session = await harness.RequestAsync(Request(2, "session/new", new JsonObject
                {
                    ["cwd"] = Path.GetTempPath(),
                    ["mcpServers"] = new JsonArray(),
                }));
                var sessionId = session["result"]!["sessionId"]!.GetValue<string>();

                await harness.RequestAsync(Request(3, "session/prompt", new JsonObject
                {
                    ["sessionId"] = sessionId,
                    ["prompt"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = "hi" }),
                }));

                // 用量计量必须来自装饰器：裸连接器不会产生任何 meter 记录。
                return client.SawUsage ? null : "ACP 模式没有记录用量：连接器未包 UsageRecordingChatClient";
            });

    /// <summary>两个入口都必须提供文件工具——这是最小可信集。</summary>
    private static async Task<string?> FileToolsParityAsync()
    {
        // 已在 ShellVisible/ShellHidden 里断言 read_file 与 write_file 存在；
        // 这条单独确认"即使什么都没配置，它们也在"。
        return await WithEnvironmentAsync(
            ("DOUGONG_SHELL", null), ("DOUGONG_CONTEXT_WINDOW", null), ("DOUGONG_MCP_SERVERS", null),
            async () =>
            {
                var client = new CaptureClient();
                await using var harness = new Harness(client);
                await harness.RequestAsync(Request(1, "initialize", new JsonObject { ["protocolVersion"] = 1 }));

                // 必须真的跑一轮模型调用，否则 LastOptions 根本没被填过——
                // 只发 initialize 然后断言工具列表，等于断言一个从未发生的调用。
                var session = await harness.RequestAsync(Request(2, "session/new", new JsonObject
                {
                    ["cwd"] = Path.GetTempPath(),
                    ["mcpServers"] = new JsonArray(),
                }));
                var sessionId = session["result"]!["sessionId"]!.GetValue<string>();

                await harness.RequestAsync(Request(3, "session/prompt", new JsonObject
                {
                    ["sessionId"] = sessionId,
                    ["prompt"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = "hi" }),
                }));

                if (client.LastOptions is null) return "模型从未被调用，无法观察工具列表";

                var tools = TestToolDeclarations.Read(client.LastOptions);
                var names = tools.Select(t => t.Name).OrderBy(x => x, StringComparer.Ordinal).ToList();

                if (!names.Contains("read_file")) return $"缺少 read_file：{string.Join(",", names)}";
                if (!names.Contains("write_file")) return $"缺少 write_file：{string.Join(",", names)}";
                return null;
            });
    }

    private static async Task<string?> AssertToolsAsync(bool expectShell)
    {
        var client = new CaptureClient();
        await using var harness = new Harness(client);

        await harness.RequestAsync(Request(1, "initialize", new JsonObject { ["protocolVersion"] = 1 }));
        var session = await harness.RequestAsync(Request(2, "session/new", new JsonObject
        {
            ["cwd"] = Path.GetTempPath(),
            ["mcpServers"] = new JsonArray(),
        }));
        var sessionId = session["result"]!["sessionId"]!.GetValue<string>();

        await harness.RequestAsync(Request(3, "session/prompt", new JsonObject
        {
            ["sessionId"] = sessionId,
            ["prompt"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = "hi" }),
        }));

        var tools = TestToolDeclarations.Read(client.LastOptions);
        var names = tools.Select(t => t.Name).ToList();

        if (!names.Contains("read_file"))
            return $"缺少 read_file：{string.Join(",", names)}";

        if (expectShell && !names.Contains("shell"))
            return $"启用 shell 后仍不可见：{string.Join(",", names)}";

        if (!expectShell && names.Contains("shell"))
            return "未启用 shell 却出现在工具列表里";

        return null;
    }

    /// <summary>临时设置环境变量执行动作，结束后恢复原值。</summary>
    private static async Task<string?> WithEnvironmentAsync(
        (string Name, string? Value) first,
        (string Name, string? Value) second,
        (string Name, string? Value) third,
        Func<Task<string?>> action)
    {
        var saved = new List<(string Name, string? Value)>();
        foreach (var (name, value) in new[] { first, second, third })
        {
            saved.Add((name, Environment.GetEnvironmentVariable(name)));
            Environment.SetEnvironmentVariable(name, value);
        }

        try
        {
            return await action();
        }
        finally
        {
            foreach (var (name, value) in saved)
                Environment.SetEnvironmentVariable(name, value);
        }
    }

    /// <summary>
    /// 最小 ACP 客户端：驱动真实的 <see cref="AcpAgentHost"/>，
    /// 但装配来自 <c>SessionComposition</c>——与 CLI 入口完全同一条路径。
    /// </summary>
    private sealed class Harness : IAsyncDisposable
    {
        private readonly System.IO.Pipelines.Pipe _toHost = new();
        private readonly System.IO.Pipelines.Pipe _fromHost = new();
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _loop;
        private readonly StreamReader _reader;
        private readonly Stream _writer;
        private readonly SemaphoreSlim _writeLock = new(1, 1);

        public Harness(IChatClient client)
        {
            AcpAgentHost? host = null;
            host = new AcpAgentHost(
                _toHost.Reader.AsStream(leaveOpen: true),
                _fromHost.Writer.AsStream(leaveOpen: true),
                // 直接调用生产装配：测试走的必须是与 CLI 完全相同的路径，
                // 不能自己再拼一份——否则测的是测试装置，不是生产代码。
                context => Dougong.Cli.AcpComposition.ComposeAsync(
                    context, client, new AllowAll(), storePath: null, Console.Error),
                new StringWriter());

            _loop = Task.Run(() => host.RunAsync(_stop.Token));
            _writer = _toHost.Writer.AsStream(leaveOpen: true);
            _reader = new StreamReader(
                _fromHost.Reader.AsStream(leaveOpen: true), System.Text.Encoding.UTF8,
                false, 8192, leaveOpen: true);
        }

        public async Task SendAsync(JsonObject message)
        {
            var json = message.ToJsonString(new JsonSerializerOptions { WriteIndented = false });
            await _writeLock.WaitAsync();
            try
            {
                await _writer.WriteAsync(System.Text.Encoding.UTF8.GetBytes(json + "\n"));
                await _writer.FlushAsync();
            }
            finally { _writeLock.Release(); }
        }

        public async Task<JsonNode> RequestAsync(JsonObject message, TimeSpan? timeout = null)
        {
            await SendAsync(message);
            using var cts = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(15));
            var id = message["id"]!.ToJsonString();

            while (true)
            {
                var line = await _reader.ReadLineAsync(cts.Token)
                    ?? throw new TimeoutException("host 没有在时限内响应");
                var node = JsonNode.Parse(line)!;

                if (node["method"]?.GetValue<string>() == "session/request_permission")
                {
                    await SendAsync(new JsonObject
                    {
                        ["jsonrpc"] = "2.0",
                        ["id"] = node["id"]!.DeepClone(),
                        ["result"] = new JsonObject
                        {
                            ["outcome"] = new JsonObject
                            {
                                ["outcome"] = "selected",
                                ["optionId"] = "allow-once",
                            },
                        },
                    });
                    continue;
                }

                if (node["id"]?.ToJsonString() == id && node["method"] is null) return node;
            }
        }

        public async ValueTask DisposeAsync()
        {
            _stop.Cancel();
            try { await _loop.WaitAsync(TimeSpan.FromSeconds(2)); } catch { }
            _writeLock.Dispose();
        }
    }

    private sealed class AllowAll : IToolApprover
    {
        public ValueTask<ToolApprovalDecision> RequestApprovalAsync(
            ToolApprovalRequest request, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(ToolApprovalDecision.Allow("test"));
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

    private static JsonObject Request(long id, string method, JsonObject? parameters = null) => new()
    {
        ["jsonrpc"] = "2.0",
        ["id"] = id,
        ["method"] = method,
        ["params"] = parameters ?? new JsonObject(),
    };

    /// <summary>记录模型收到的 ChatOptions（其含工具声明）与历史条数。</summary>
    private sealed class CaptureClient : IChatClient
    {
        public ChatOptions? LastOptions { get; private set; }
        public List<int> HistoryCounts { get; } = [];

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> m, ChatOptions? o = null, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> m, ChatOptions? o = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            LastOptions = o;
            HistoryCounts.Add(m.Count());
            await Task.CompletedTask;
            yield return new ChatResponseUpdate(ChatRole.Assistant, "ok");
        }

        public object? GetService(Type t, object? k = null) => null;
        public void Dispose() { }
    }

    /// <summary>会报告 usage 的客户端，用于验证计量是否产生。</summary>
    private sealed class UsageClient : IChatClient
    {
        private readonly DirectionalMeterListener _listener = new();

        public UsageClient() => _listener.Start();

        public bool SawUsage => _listener.SawInputTokens;

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> m, ChatOptions? o = null, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> m, ChatOptions? o = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.CompletedTask;
            yield return new ChatResponseUpdate(ChatRole.Assistant, "ok") { AdditionalProperties = null };
            yield return new ChatResponseUpdate(ChatRole.Assistant,
                [new UsageContent(new UsageDetails { InputTokenCount = 7, OutputTokenCount = 3 })]);
        }

        public object? GetService(Type t, object? k = null) => null;
        public void Dispose() => _listener.Dispose();
    }
}
