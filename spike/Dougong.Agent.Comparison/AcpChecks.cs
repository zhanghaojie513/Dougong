using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dougong.Acp;
using Dougong.Agent;
using Microsoft.Extensions.AI;

namespace Dougong.Agent.Comparison;

/// <summary>
/// M2.4 验收：ACP Agent 端能被外部客户端驱动跑通一轮对话。
/// </summary>
/// <remarks>
/// 全部用真实的 <see cref="Stream"/> 配对驱动 <see cref="AcpAgentHost"/>，
/// 不 mock 协议层——mock 只能验证我的假设，测不到"帧格式对不对"。
/// <para>
/// 重点覆盖 docs/acp-reference-survey.md 里记的三个坑：
/// stdout 纯净、事件先于响应、权限请求无回复必须按拒绝。
/// </para>
/// </remarks>
public static class AcpChecks
{
    public static async Task<int> RunAsync()
    {
        var failures = 0;
        failures += await CheckAsync("initialize-negotiates-v1", InitializeAsync);
        failures += await CheckAsync("rejects-other-protocol-version", VersionRejectAsync);
        failures += await CheckAsync("prompt-round-trips-with-updates", PromptAsync);
        failures += await CheckAsync("updates-precede-response", OrderingAsync);
        failures += await CheckAsync("cancel-marks-turn-cancelled", CancelAsync);
        failures += await CheckAsync("unknown-method-is-reported", UnknownMethodAsync);
        failures += await CheckAsync("unimplemented-session-methods-are-explicit", UnimplementedAsync);
        failures += await CheckAsync("request-id-type-is-preserved", IdTypeAsync);
        failures += await CheckAsync("stdout-carries-only-protocol-frames", StdoutPurityAsync);
        failures += await CheckAsync("string-id-request-gets-string-response", StringIdAsync);
        failures += await CheckAsync("approval-goes-through-protocol", ApprovalViaProtocolAsync);
        failures += await CheckAsync("client-denial-blocks-execution", DenialBlocksAsync);
        failures += await CheckAsync("unanswered-approval-is-denied", UnansweredAsync);
        failures += await CheckAsync("load-replays-full-history", LoadReplaysAsync);
        failures += await CheckAsync("resume-does-not-replay-but-keeps-history", ResumeKeepsAsync);
        failures += await CheckAsync("load-rejects-unknown-outcome-session", LoadBlocksAsync);
        failures += await CheckAsync("list-reports-committed-sessions", ListSessionsAsync);
        failures += await CheckAsync("close-removes-session", CloseSessionAsync);
        failures += await CheckAsync("load-declared-capability-is-real", CapabilityAsync);
        failures += await CheckAsync("plain-turn-persists-assistant-reply", PlainTurnPersistsAsync);

        Console.WriteLine(failures == 0
            ? "ACP: ALL PASS"
            : $"ACP: {failures} FAILURE(S)");
        return failures;
    }

    // ── 驱动骨架 ─────────────────────────────────────────────────

    private sealed class Harness : IAsyncDisposable
    {
        private readonly System.IO.Pipelines.Pipe _toHost = new();
        private readonly System.IO.Pipelines.Pipe _fromHost = new();
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _loop;
        private readonly StreamReader _reader;
        private readonly Stream _clientToHost;
        private readonly SemaphoreSlim _writeLock = new(1, 1);
        // 单读泵 + 队列：并发读同一 StreamReader 会冲突，而两个测试步骤
        // （等响应 / 等权限请求）又必须能独立消费消息，因此统一由一个泵读入。
        private readonly System.Threading.Channels.Channel<JsonNode> _inbound =
            System.Threading.Channels.Channel.CreateUnbounded<JsonNode>();
        private readonly JsonObject _permissionReply;

        /// <param name="runtime">直接使用的运行时；为 null 时用 client + tool 装配。</param>
        /// <param name="replyToPermission">
        /// false 表示**不回复**权限请求，用于测超时降级为拒绝。
        /// </param>
        public Harness(
            IChatClient? client = null,
            IAgentTool? tool = null,
            TimeSpan? approvalTimeout = null,
            string permissionOutcome = "allow-once",
            bool replyToPermission = true,
            IAgentRuntime? runtime = null,
            string? storeRoot = null,
            Func<string, string?>? sessionPathFor = null)
        {
            _permissionReply = new JsonObject
            {
                ["outcome"] = new JsonObject
                {
                    ["outcome"] = "selected",
                    ["optionId"] = permissionOutcome,
                },
            };

            AcpAgentHost? host = null;
            host = new AcpAgentHost(
                _toHost.Reader.AsStream(leaveOpen: true),
                _fromHost.Writer.AsStream(leaveOpen: true),
                context =>
                {
                    if (runtime is not null)
                        return Task.FromResult(new AcpSessionRuntime(runtime, new ToolDispatcher()));

                    var dispatcher = new ToolDispatcher(
                        new AcpApprover(() => host!, context.SessionId,
                            approvalTimeout ?? (replyToPermission
                                ? TimeSpan.FromMinutes(5)
                                : TimeSpan.FromMilliseconds(300))),
                        sessionId: context.SessionId,
                        turnId: "turn-1",
                        workspace: context.Workspace,
                        pathGuard: new WorkspacePathGuard(context.Workspace));

                    if (tool is not null) dispatcher.Register(tool);

                    return Task.FromResult(new AcpSessionRuntime(
                        new LoopAgentRuntime(client!, dispatcher), dispatcher));
                },
                new StringWriter(),
                storeResolver: sessionPathFor,
                listRoots: storeRoot is null ? null : [storeRoot]);

            _loop = Task.Run(() => host.RunAsync(_stop.Token));
            _clientToHost = _toHost.Writer.AsStream(leaveOpen: true);
            _reader = new StreamReader(
                _fromHost.Reader.AsStream(leaveOpen: true), Encoding.UTF8, false, 8192, leaveOpen: true);
            _ = Task.Run(async () =>
            {
                try
                {
                    while (!_stop.IsCancellationRequested)
                    {
                        var line = await _reader.ReadLineAsync(_stop.Token);
                        if (line is null) break;
                        JsonNode? node;
                        try { node = JsonNode.Parse(line); }
                        catch (JsonException) { continue; }
                        if (node is not null) await _inbound.Writer.WriteAsync(node);
                    }
                }
                catch (OperationCanceledException) { }
                _inbound.Writer.TryComplete();
            });
        }

        /// <summary>用现成运行时构造（不需要注入 client 与工具的用例）。</summary>
        public Harness(
            IAgentRuntime runtime,
            string? storeRoot = null,
            Func<string, string?>? sessionPathFor = null)
            : this(client: null, tool: null, runtime: runtime,
                   storeRoot: storeRoot, sessionPathFor: sessionPathFor) { }

        public List<JsonNode> Received { get; } = [];

        /// <summary>发送一条消息（紧凑 JSON + LF）。单写者，避免并发写同一 pipe。</summary>
        public async Task SendAsync(JsonObject message)
        {
            var json = message.ToJsonString(new JsonSerializerOptions { WriteIndented = false });
            var bytes = Encoding.UTF8.GetBytes(json + "\n");

            await _writeLock.WaitAsync();
            try
            {
                await _clientToHost.WriteAsync(bytes);
                await _clientToHost.FlushAsync();
            }
            finally { _writeLock.Release(); }
        }

        /// <summary>发请求并读到匹配 id 的响应，途中收到的通知进 <see cref="Received"/>。</summary>
        /// <param name="permissionReply">
        /// 途中收到权限请求时的回复策略：true=批准，false=拒绝，null=不回复。
        /// </param>
        public async Task<JsonNode> RequestAsync(
            JsonObject message, TimeSpan? timeout = null, bool? permissionReply = true)
        {
            await SendAsync(message);

            using var cts = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(10));
            var id = message["id"]!.ToJsonString();

            while (true)
            {
                var node = await ReadLineAsync(cts.Token)
                    ?? throw new TimeoutException("host 没有在时限内响应");

                if (node["id"]?.ToJsonString() == id && node["method"] is null)
                {
                    if (!SawPermissionRequest)
                        return node;

                    return node;
                }

                await HandleAsync(node, permissionReply);
            }
        }

        /// <summary>本次请求期间是否收到过权限请求。</summary>
        public bool SawPermissionRequest { get; private set; }

        private static string? FindOptionId(JsonNode request, string kind)
        {
            if (request["params"]?["options"] is not JsonArray options) return null;
            foreach (var option in options)
                if (option?["kind"]?.GetValue<string>() == kind)
                    return option["optionId"]?.GetValue<string>();
            return null;
        }

        /// <summary>处理途中收到的通知；权限请求按策略回复。</summary>
        private async Task HandleAsync(JsonNode node, bool? permissionReply)
        {
            Received.Add(node);

            if (node["method"]?.GetValue<string>() != "session/request_permission") return;
            SawPermissionRequest = true;

            // null 表示故意不回复，用于测「无回复必须降级为拒绝」。
            if (permissionReply is null) return;

            await SendAsync(new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = node["id"]!.DeepClone(),
                ["result"] = permissionReply.Value
                    ? _permissionReply.DeepClone()
                    : DenyReply(),
            });
        }

        private static JsonObject DenyReply() => new()
        {
            ["outcome"] = new JsonObject
            {
                ["outcome"] = "selected",
                ["optionId"] = "reject-once",
            },
        };

        private async Task<JsonNode?> ReadLineAsync(CancellationToken cancellationToken)
        {
            try
            {
                return await _inbound.Reader.ReadAsync(cancellationToken);
            }
            catch (System.Threading.Channels.ChannelClosedException)
            {
                return null;
            }
        }

        public async ValueTask DisposeAsync()
        {
            _stop.Cancel();
            try { await _loop.WaitAsync(TimeSpan.FromSeconds(2)); } catch { }
            _writeLock.Dispose();
        }
    }

    private static JsonObject Request(long id, string method, JsonObject? parameters = null) => new()
    {
        ["jsonrpc"] = "2.0",
        ["id"] = id,
        ["method"] = method,
        ["params"] = parameters ?? new JsonObject(),
    };

    /// <summary>只回一段文本的运行时。</summary>
    private static IAgentRuntime TextRuntime(string text) =>
        new LoopAgentRuntime(new FixedClient(text), new ToolDispatcher());

    // ── 用例 ─────────────────────────────────────────────────────

    private static async Task<string?> InitializeAsync()
    {
        await using var harness = new Harness(TextRuntime("hi"));
        var response = await harness.RequestAsync(Request(1, "initialize", new JsonObject
        {
            ["protocolVersion"] = 1,
            ["clientCapabilities"] = new JsonObject(),
            ["clientInfo"] = new JsonObject { ["name"] = "probe", ["version"] = "1.0.0" },
        }));

        var result = response["result"];
        if (result is null) return $"initialize 返回了错误：{response["error"]?.ToJsonString()}";

        if (result["protocolVersion"]?.GetValue<int>() != 1)
            return $"协商版本应为 1，实际 {result["protocolVersion"]}";

        // 声明了 loadSession 就必须真的能重放（由 load-replays-full-history 验证）。
        // 这里只确认声明存在；能力的真实性由那条用例负责。
        var caps = result["agentCapabilities"];
        if (caps?["loadSession"] is null)
            return "未声明 loadSession";

        return result["agentInfo"]?["name"]?.GetValue<string>() == "dougong"
            ? null : "agentInfo 缺少名称";
    }

    private static async Task<string?> VersionRejectAsync()
    {
        await using var harness = new Harness(TextRuntime("hi"));
        var response = await harness.RequestAsync(Request(1, "initialize", new JsonObject
        {
            ["protocolVersion"] = 2,
        }));

        if (response["error"] is null)
            return "v2 请求被接受：不应尝试跨主版本兼容";

        var message = response["error"]!["message"]?.GetValue<string>() ?? "";
        return message.Contains("v1", StringComparison.Ordinal)
            ? null : $"拒绝原因未说明支持的版本：{message}";
    }

    private static async Task<string?> PromptAsync()
    {
        await using var harness = new Harness(TextRuntime("hello-acp"));
        var init = await harness.RequestAsync(Request(1, "initialize", new JsonObject
        {
            ["protocolVersion"] = 1,
        }));
        if (init["error"] is not null) return $"initialize 失败：{init["error"]}";

        var session = await harness.RequestAsync(Request(2, "session/new", new JsonObject
        {
            ["cwd"] = Path.GetTempPath(),
            ["mcpServers"] = new JsonArray(),
        }));
        var sessionId = session["result"]?["sessionId"]?.GetValue<string>();
        if (sessionId is null) return $"session/new 失败：{session["error"]?.ToJsonString()}";

        var prompt = await harness.RequestAsync(Request(3, "session/prompt", new JsonObject
        {
            ["sessionId"] = sessionId,
            ["prompt"] = new JsonArray(new JsonObject
            {
                ["type"] = "text",
                ["text"] = "say hello",
            }),
        }));

        if (prompt["error"] is not null) return $"session/prompt 失败：{prompt["error"]}";

        var stopReason = prompt["result"]?["stopReason"]?.GetValue<string>();
        if (stopReason != "end_turn") return $"stopReason 应为 end_turn，实际 {stopReason}";

        // 必须收到文本增量。
        var chunks = harness.Received
            .Where(n => n["method"]?.GetValue<string>() == "session/update")
            .Select(n => n["params"]?["update"])
            .Where(u => u?["sessionUpdate"]?.GetValue<string>() == "agent_message_chunk")
            .ToList();

        if (chunks.Count == 0) return "没有收到 agent_message_chunk";

        var text = string.Concat(chunks.Select(c =>
            c?["content"]?["text"]?.GetValue<string>() ?? ""));
        return text == "hello-acp" ? null : $"文本增量不符：'{text}'";
    }

    /// <summary>
    /// 硬约束：所有 <c>session/update</c> 必须在 <c>session/prompt</c> 响应之前到达。
    /// </summary>
    /// <remarks>
    /// 客户端按顺序处理：响应标志着轮次结束。若增量晚于响应到达，
    /// 客户端会丢掉最后几条输出。这是 zeron 修过两次的陷阱。
    /// </remarks>
    private static async Task<string?> OrderingAsync()
    {
        var client = new MultiChunkClient(["a", "b", "c"]);
        await using var harness = new Harness(new LoopAgentRuntime(client, new ToolDispatcher()));

        await harness.RequestAsync(Request(1, "initialize", new JsonObject { ["protocolVersion"] = 1 }));
        var session = await harness.RequestAsync(Request(2, "session/new", new JsonObject
        {
            ["cwd"] = Path.GetTempPath(),
            ["mcpServers"] = new JsonArray(),
        }));
        var sessionId = session["result"]!["sessionId"]!.GetValue<string>();

        // RequestAsync 会把途中收到的通知收进 Received，然后才返回响应。
        // 因此这里要验证：三条增量全部在 Received 里。
        await harness.RequestAsync(Request(3, "session/prompt", new JsonObject
        {
            ["sessionId"] = sessionId,
            ["prompt"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = "go" }),
        }));

        var chunks = harness.Received
            .Select(n => n["params"]?["update"])
            .Where(u => u?["sessionUpdate"]?.GetValue<string>() == "agent_message_chunk")
            .Select(u => u?["content"]?["text"]?.GetValue<string>() ?? "")
            .ToList();

        return string.Concat(chunks) == "abc"
            ? null : $"增量未全部先于响应到达：'{string.Concat(chunks)}'";
    }

    private static async Task<string?> CancelAsync()
    {
        var client = new SlowClient(TimeSpan.FromSeconds(10));
        await using var harness = new Harness(new LoopAgentRuntime(client, new ToolDispatcher()));

        await harness.RequestAsync(Request(1, "initialize", new JsonObject { ["protocolVersion"] = 1 }));
        var session = await harness.RequestAsync(Request(2, "session/new", new JsonObject
        {
            ["cwd"] = Path.GetTempPath(),
            ["mcpServers"] = new JsonArray(),
        }));
        var sessionId = session["result"]!["sessionId"]!.GetValue<string>();

        // 发 prompt 后立刻取消（通知不需要响应）。
        var promptTask = harness.RequestAsync(Request(3, "session/prompt", new JsonObject
        {
            ["sessionId"] = sessionId,
            ["prompt"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = "go" }),
        }), TimeSpan.FromSeconds(8));

        await Task.Delay(200);
        await harness.SendAsync(new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["method"] = "session/cancel",
            ["params"] = new JsonObject { ["sessionId"] = sessionId },
        });

        var response = await promptTask;
        var stopReason = response["result"]?["stopReason"]?.GetValue<string>();
        return stopReason == "cancelled" ? null : $"取消后 stopReason 应为 cancelled，实际 {stopReason}";
    }

    private static async Task<string?> UnknownMethodAsync()
    {
        await using var harness = new Harness(TextRuntime("hi"));
        var response = await harness.RequestAsync(Request(1, "totally/unknown"));

        if (response["error"] is null) return "未知方法没有返回错误";
        return response["error"]!["code"]?.GetValue<int>() == -32601
            ? null : $"错误码应为 -32601，实际 {response["error"]!["code"]}";
    }

    /// <summary>
    /// 真正未实现的方法必须显式报错，而不是静默成功。
    /// </summary>
    /// <remarks>
    /// 原先这里检查 load/resume/list/close；它们现在已实现，因此改用
    /// 仍未实现的 <c>session/delete</c> 与 <c>session/set_mode</c>，
    /// 否则这条检查会变成"检查一个已实现的方法报错"——那是错的断言。
    /// </remarks>
    private static async Task<string?> UnimplementedAsync()
    {
        await using var harness = new Harness(TextRuntime("hi"));

        foreach (var method in new[] { "session/delete", "session/set_mode" })
        {
            var response = await harness.RequestAsync(Request(1, method, new JsonObject
            {
                ["sessionId"] = "x",
            }));

            if (response["error"] is null)
                return $"'{method}' 未实现却返回成功——不能静默假装支持";

            if (response["error"]!["code"]?.GetValue<int>() != -32601)
                return $"'{method}' 的错误码应为 -32601";
        }

        return null;
    }

    private static async Task<string?> IdTypeAsync()
    {
        await using var harness = new Harness(TextRuntime("hi"));
        var response = await harness.RequestAsync(Request(42, "initialize", new JsonObject
        {
            ["protocolVersion"] = 1,
        }));

        if (response["id"]?.GetValueKind() is not JsonValueKind.Number)
            return $"数字 id 被改成了 {response["id"]?.GetValueKind()}";

        return response["id"]!.GetValue<long>() == 42 ? null : "id 值不符";
    }

    private static async Task<string?> StringIdAsync()
    {
        await using var harness = new Harness(TextRuntime("hi"));
        var message = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = "req-abc",
            ["method"] = "initialize",
            ["params"] = new JsonObject { ["protocolVersion"] = 1 },
        };

        await harness.SendAsync(message);

        var response = await harness.RequestAsync(new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = "req-abc",
            ["method"] = "initialize",
            ["params"] = new JsonObject { ["protocolVersion"] = 1 },
        });
        return response["id"]?.GetValueKind() is JsonValueKind.String
            ? null : $"字符串 id 被改成了 {response["id"]?.GetValueKind()}";
    }

    /// <summary>
    /// 硬约束：stdout 只能有协议帧。
    /// </summary>
    /// <remarks>
    /// 这条最容易踩：任何一处 <c>Console.WriteLine</c> 都会污染协议。
    /// 验证方式是把 host 的输出接到内存流，逐行检查每行都能解析为 JSON。
    /// </remarks>
    private static async Task<string?> StdoutPurityAsync()
    {
        var output = new MemoryStream();
        var input = new MemoryStream();

        var host = new AcpAgentHost(
            input,
            output,
            _ =>
            {
                var dispatcher = new ToolDispatcher();
                return Task.FromResult(new AcpSessionRuntime(
                    new LoopAgentRuntime(new FixedClient("hi"), dispatcher), dispatcher));
            });

        // 让 host 读到一条完整请求后停止。
        var frame = Encoding.UTF8.GetBytes(
            """{"jsonrpc":"2.0","id":1,"method":"terminate-check","params":{}}""" + "\n");
        await input.WriteAsync(frame);
        input.Position = 0;

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var loop = host.RunAsync(cts.Token);
        await Task.Delay(300);
        host.Stop();
        try { await loop.WaitAsync(TimeSpan.FromSeconds(2)); } catch { }

        output.Position = 0;
        using var reader = new StreamReader(output, Encoding.UTF8);
        var index = 0;
        while (await reader.ReadLineAsync() is { } line)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            index++;
            try { JsonNode.Parse(line); }
            catch (JsonException)
            {
                return $"stdout 第 {index} 行不是协议帧：{line[..Math.Min(120, line.Length)]}";
            }
        }

        return index == 0 ? "host 没有向 stdout 写任何东西（协议不可用）" : null;
    }

    /// <summary>
    /// 硬约束：需要审批的工具必须真的走 <c>session/request_permission</c>，
    /// 而不是被默认拒绝或默认放行。
    /// </summary>
    private static async Task<string?> ApprovalViaProtocolAsync()
    {
        var tool = new CountingTool();
        await using var harness = new Harness(new ToolCallingClient("c1"), tool);

        return await RunApprovalScenarioAsync(
            harness, tool, approve: true,
            expectExecuted: true,
            expectPermissionRequest: true);
    }

    /// <summary>客户端拒绝后，工具绝不能执行。</summary>
    private static async Task<string?> DenialBlocksAsync()
    {
        var tool = new CountingTool();
        await using var harness = new Harness(new ToolCallingClient("c1"), tool);

        return await RunApprovalScenarioAsync(
            harness, tool, approve: false,
            expectExecuted: false,
            expectPermissionRequest: true);
    }

    /// <summary>客户端不回复时按拒绝处理，不能挂死。</summary>
    private static async Task<string?> UnansweredAsync()
    {
        var tool = new CountingTool();
        // 审批超时设短：验证「无回复必须降级为拒绝」，而不是无限等待。
        await using var harness = new Harness(
            new ToolCallingClient("c1"), tool,
            approvalTimeout: TimeSpan.FromMilliseconds(300));

        return await RunApprovalScenarioAsync(
            harness, tool, approve: null,
            expectExecuted: false,
            expectPermissionRequest: true);
    }

    /// <summary>
    /// 跑一次需要审批的对话。<paramref name="approve"/> 为 null 表示客户端不回复。
    /// </summary>
    private static async Task<string?> RunApprovalScenarioAsync(
        Harness harness, CountingTool tool, bool? approve,
        bool expectExecuted, bool expectPermissionRequest)
    {
        await harness.RequestAsync(Request(1, "initialize", new JsonObject { ["protocolVersion"] = 1 }));
        var session = await harness.RequestAsync(Request(2, "session/new", new JsonObject
        {
            ["cwd"] = Path.GetTempPath(),
            ["mcpServers"] = new JsonArray(),
        }));
        var sessionId = session["result"]!["sessionId"]!.GetValue<string>();

        // 权限请求的回复由 RequestAsync 在处理途中完成（同一消费循环），
        // 不能另起一个任务去抢消息队列——那正是「两个消费者」的竞态。
        var response = await harness.RequestAsync(Request(3, "session/prompt", new JsonObject
        {
            ["sessionId"] = sessionId,
            ["prompt"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = "go" }),
        }), TimeSpan.FromSeconds(30), approve);

        if (expectPermissionRequest && !harness.SawPermissionRequest)
            return "需要审批的工具没有发出 session/request_permission";

        // 这条断言是核心：审批的最终目的是控制"工具到底有没有被执行"。
        // 只验证"发过权限请求"而不验证执行结果，等于没测审批。
        if (expectExecuted && tool.ExecutionCount == 0)
            return "客户端已批准，但工具没有被执行";

        if (!expectExecuted && tool.ExecutionCount > 0)
            return $"客户端未批准，但工具执行了 {tool.ExecutionCount} 次";

        var stopReason = response["result"]?["stopReason"]?.GetValue<string>();
        return stopReason is null ? $"prompt 失败：{response["error"]?.ToJsonString()}" : null;
    }

    /// <summary>
    /// <c>session/load</c> 必须重放整段历史再响应。
    /// </summary>
    /// <remarks>
    /// 只回 {sessionId} 而不重放，客户端显示空会话——看起来"恢复成功"，历史全丢。
    /// 因此这里验证的是**真收到了重放的历史内容**，不是"没报错"。
    /// </remarks>
    private static async Task<string?> LoadReplaysAsync()
    {
        var dir = NewTempDir("load");
        try
        {
            var sessionPath = Path.Combine(dir, "sess-load.jsonl");
            await WriteSessionAsync(sessionPath, "sess-load", "还记得我说过的暗号吗", "暗号是 BLUE-42");

            await using var harness = new Harness(
                TextRuntime("ignored"), storeRoot: dir, sessionPathFor: _ => sessionPath);

            await harness.RequestAsync(Request(1, "initialize", new JsonObject { ["protocolVersion"] = 1 }));

            var load = await harness.RequestAsync(Request(2, "session/load", new JsonObject
            {
                ["sessionId"] = "sess-load",
                ["cwd"] = dir,
                ["mcpServers"] = new JsonArray(),
            }));

            if (load["error"] is not null) return $"session/load 失败：{load["error"]}";

            var replayedUser = harness.Received
                .Select(n => n["params"]?["update"])
                .Where(u => u?["sessionUpdate"]?.GetValue<string>() == "user_message_chunk")
                .Select(u => u?["content"]?["text"]?.GetValue<string>() ?? "")
                .ToList();

            if (!replayedUser.Any(t => t.Contains("暗号", StringComparison.Ordinal)))
                return $"没有重放用户消息：{string.Join("|", replayedUser)}";

            var replayedAssistant = harness.Received
                .Select(n => n["params"]?["update"])
                .Where(u => u?["sessionUpdate"]?.GetValue<string>() == "agent_message_chunk")
                .Select(u => u?["content"]?["text"]?.GetValue<string>() ?? "")
                .ToList();

            return replayedAssistant.Any(t => t.Contains("BLUE-42", StringComparison.Ordinal))
                ? null : $"没有重放助手消息：{string.Join("|", replayedAssistant)}";
        }
        finally { TryDelete(dir); }
    }

    /// <summary>
    /// <c>session/resume</c> 不重放，但**必须**把历史接回上下文。
    /// </summary>
    /// <remarks>
    /// 两个要求缺一不可：重放了就违背 resume 语义；不接历史则后续轮次从零开始。
    /// 验证方式：resume 后发一轮，检查送给模型的历史里确实含旧内容。
    /// </remarks>
    private static async Task<string?> ResumeKeepsAsync()
    {
        var dir = NewTempDir("resume");
        try
        {
            var sessionPath = Path.Combine(dir, "sess-resume.jsonl");
            await WriteSessionAsync(sessionPath, "sess-resume", "记住这个数字 7", "已记住 7");

            var client = new RecordingClient();
            await using var harness = new Harness(
                client, storeRoot: dir, sessionPathFor: _ => sessionPath);

            await harness.RequestAsync(Request(1, "initialize", new JsonObject { ["protocolVersion"] = 1 }));

            var resume = await harness.RequestAsync(Request(2, "session/resume", new JsonObject
            {
                ["sessionId"] = "sess-resume",
                ["cwd"] = dir,
                ["mcpServers"] = new JsonArray(),
            }));

            if (resume["error"] is not null) return $"session/resume 失败：{resume["error"]}";

            if (harness.Received.Any(n => n["method"]?.GetValue<string>() == "session/update"))
                return "session/resume 不应重放历史";

            await harness.RequestAsync(Request(3, "session/prompt", new JsonObject
            {
                ["sessionId"] = "sess-resume",
                ["prompt"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = "数字是多少" }),
            }));

            var history = client.LastHistory;
            if (history is null) return "模型没有被调用";

            return history.Any(m => (m.Text ?? "").Contains("记住这个数字 7", StringComparison.Ordinal))
                ? null : "resume 后模型没有看到恢复的历史";
        }
        finally { TryDelete(dir); }
    }

    /// <summary>有未知结果的会话必须拒绝恢复，不能假装历史完整。</summary>
    private static async Task<string?> LoadBlocksAsync()
    {
        var dir = NewTempDir("block");
        try
        {
            var sessionPath = Path.Combine(dir, "sess-block.jsonl");
            await using (var store = await JsonlAgentStore.OpenAsync(sessionPath, "sess-block"))
            {
                await store.CommitAsync(new StoreRecord(0, StoreRecordKind.UserInput,
                    JsonSerializer.SerializeToElement("do it")));
                await store.CommitAsync(new StoreRecord(0, StoreRecordKind.ToolCallStarted,
                    JsonDocument.Parse("""{"id":"c1","tool":"write_file"}""").RootElement.Clone()));
            }

            await using var harness = new Harness(
                TextRuntime("x"), storeRoot: dir, sessionPathFor: _ => sessionPath);

            await harness.RequestAsync(Request(1, "initialize", new JsonObject { ["protocolVersion"] = 1 }));

            var load = await harness.RequestAsync(Request(2, "session/load", new JsonObject
            {
                ["sessionId"] = "sess-block",
                ["cwd"] = dir,
                ["mcpServers"] = new JsonArray(),
            }));

            if (load["error"] is null) return "有未知结果的会话被成功恢复";

            var message = load["error"]!["message"]?.GetValue<string>() ?? "";
            return message.Contains("c1", StringComparison.Ordinal)
                ? null : $"拒绝原因未指出具体调用：{message}";
        }
        finally { TryDelete(dir); }
    }

    /// <summary><c>session/list</c> 必须列出真实存在的已提交会话。</summary>
    private static async Task<string?> ListSessionsAsync()
    {
        var dir = NewTempDir("list");
        try
        {
            await WriteSessionAsync(Path.Combine(dir, "a.jsonl"), "sess-a", "hi", "hello");
            await WriteSessionAsync(Path.Combine(dir, "b.jsonl"), "sess-b", "yo", "hey");

            await using var harness = new Harness(TextRuntime("x"), storeRoot: dir);
            await harness.RequestAsync(Request(1, "initialize", new JsonObject { ["protocolVersion"] = 1 }));

            var list = await harness.RequestAsync(Request(2, "session/list", new JsonObject()));
            if (list["error"] is not null) return $"session/list 失败：{list["error"]}";

            if (list["result"]?["sessions"] is not JsonArray sessions)
                return "session/list 没有返回 sessions 数组";

            var ids = sessions
                .Select(s => s?["sessionId"]?.GetValue<string>() ?? "")
                .OrderBy(x => x, StringComparer.Ordinal)
                .ToList();

            // SessionSummary 的 SessionId 取自文件名（与 CLI --list 一致）。
            return ids.SequenceEqual(["a", "b"])
                ? null : $"列出的会话不符：{string.Join(",", ids)}";
        }
        finally { TryDelete(dir); }
    }

    /// <summary><c>session/close</c> 之后该会话不能再接受 prompt。</summary>
    private static async Task<string?> CloseSessionAsync()
    {
        await using var harness = new Harness(TextRuntime("hi"));
        await harness.RequestAsync(Request(1, "initialize", new JsonObject { ["protocolVersion"] = 1 }));

        var session = await harness.RequestAsync(Request(2, "session/new", new JsonObject
        {
            ["cwd"] = Path.GetTempPath(),
            ["mcpServers"] = new JsonArray(),
        }));
        var sessionId = session["result"]!["sessionId"]!.GetValue<string>();

        var close = await harness.RequestAsync(Request(3, "session/close", new JsonObject
        {
            ["sessionId"] = sessionId,
        }));
        if (close["error"] is not null) return $"session/close 失败：{close["error"]}";

        var prompt = await harness.RequestAsync(Request(4, "session/prompt", new JsonObject
        {
            ["sessionId"] = sessionId,
            ["prompt"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = "go" }),
        }));

        return prompt["error"] is null ? "已关闭的会话仍接受 prompt" : null;
    }

    /// <summary>
    /// 声明的能力必须与实现一致：<c>loadSession: true</c> 就意味着 load 真的能用。
    /// </summary>
    /// <remarks>
    /// 声明了却没实现，客户端会按能力走恢复路径然后失败——比不声明更糟。
    /// </remarks>
    private static async Task<string?> CapabilityAsync()
    {
        await using var harness = new Harness(TextRuntime("hi"));
        var init = await harness.RequestAsync(Request(1, "initialize", new JsonObject
        {
            ["protocolVersion"] = 1,
        }));

        var caps = init["result"]?["agentCapabilities"];
        if (caps?["loadSession"]?.GetValue<bool>() != true)
            return "未声明 loadSession，但实现已支持";

        foreach (var name in new[] { "list", "resume", "close" })
        {
            if (caps["sessionCapabilities"]?[name] is null)
                return $"未声明 sessionCapabilities.{name}，但实现已支持";
        }

        return null;
    }

    /// <summary>
    /// 回归：纯文本轮次（无工具调用）也必须把助手回复写进日志。
    /// </summary>
    /// <remarks>
    /// 这是真实缺陷：纯文本轮次原先直接 <c>FinishAsync</c> 返回，从不提交
    /// <c>ModelResponse</c>。于是所有"只是聊天"的会话在恢复后只剩用户消息，
    /// 模型不记得自己说过什么。
    /// <para>
    /// 为什么之前的用例没抓到：它们都是手工写入完整的 JSONL 再测恢复，
    /// 从未让**运行时自己**跑一轮再恢复。这一条补上那个缺口。
    /// </para>
    /// </remarks>
    private static async Task<string?> PlainTurnPersistsAsync()
    {
        var dir = NewTempDir("plain");
        try
        {
            var path = Path.Combine(dir, "sess-plain.jsonl");

            await using (var store = await JsonlAgentStore.OpenAsync(path, "sess-plain"))
            {
                var runtime = new LoopAgentRuntime(
                    new FixedClient("这是助手的回复"), new ToolDispatcher(), store);

                await foreach (var _ in runtime.RunAsync(
                    new AgentRunRequest("sess-plain", "t1", [], "你好")))
                {
                }
            }

            // 直接检查日志：必须有 ModelResponse，且含助手文本。
            await using var reopened = await JsonlAgentStore.OpenAsync(path, "sess-plain");
            var recovered = await SessionRecovery.ReplayAsync(reopened);

            var assistant = recovered.History.FirstOrDefault(m => m.Role == AgentRole.Assistant);
            if (assistant is null)
                return "日志里没有助手消息：纯文本轮次未提交 ModelResponse";

            return assistant.Content.Contains("这是助手的回复", StringComparison.Ordinal)
                ? null : $"助手消息内容不符：{assistant.Content}";
        }
        finally { TryDelete(dir); }
    }

    private static string NewTempDir(string tag)
    {
        var dir = Path.Combine(Path.GetTempPath(),
            $"dg-acp-{tag}-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void TryDelete(string dir)
    {
        try { Directory.Delete(dir, recursive: true); } catch { }
    }

    /// <summary>写一个含一轮完整对话的已提交会话。</summary>
    private static async Task WriteSessionAsync(
        string path, string sessionId, string userInput, string modelText)
    {
        await using var store = await JsonlAgentStore.OpenAsync(path, sessionId);
        await store.CommitAsync(new StoreRecord(0, StoreRecordKind.UserInput,
            JsonSerializer.SerializeToElement(userInput)));
        await store.CommitAsync(new StoreRecord(0, StoreRecordKind.ModelResponse,
            JsonDocument.Parse(JsonSerializer.Serialize(new { text = modelText, calls = Array.Empty<object>() }))
                .RootElement.Clone()));
        await store.CommitAsync(new StoreRecord(0, StoreRecordKind.RunFinished,
            JsonDocument.Parse("""{"outcome":"Completed"}""").RootElement.Clone()));
    }

    // ── 假客户端 ─────────────────────────────────────────────────

    /// <summary>记录模型收到的历史，用于验证恢复是否真的接回了上下文。</summary>
    private sealed class RecordingClient : IChatClient
    {
        public IReadOnlyList<ChatMessage>? LastHistory { get; private set; }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> m, ChatOptions? o = null, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> m, ChatOptions? o = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            LastHistory = [.. m];
            await Task.CompletedTask;
            yield return new ChatResponseUpdate(ChatRole.Assistant, "noted");
        }

        public object? GetService(Type t, object? k = null) => null;
        public void Dispose() { }
    }


    /// <summary>
    /// 把现成的 <see cref="IAgentRuntime"/> 包成 <see cref="IChatClient"/>，
    /// 供 Harness 的统一构造路径使用。
    /// </summary>
    /// <remarks>
    /// 只为复用同一套 Harness 装配，不参与真实调用。
    /// 它返回一段固定文本，让 runtime 立即结束一轮。
    /// </remarks>
    private sealed class RuntimeWrapper : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> m, ChatOptions? o = null, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> m, ChatOptions? o = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.CompletedTask;
            yield return new ChatResponseUpdate(ChatRole.Assistant, "wrapped");
        }

        public object? GetService(Type t, object? k = null) => null;
        public void Dispose() { }
    }


    private static IEnumerable<ChatResponseUpdate> ToUpdates(string text) =>
        [new ChatResponseUpdate(ChatRole.Assistant, text)];

    private sealed class FixedClient(string text) : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> m, ChatOptions? o = null, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> m, ChatOptions? o = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.CompletedTask;
            foreach (var update in ToUpdates(text)) yield return update;
        }

        public object? GetService(Type t, object? k = null) => null;
        public void Dispose() { }
    }

    /// <summary>记录执行次数的工具，用于验证审批是否真的拦住了执行。</summary>
    private sealed class CountingTool : IAgentTool
    {
        private int _executions;
        public int ExecutionCount => Volatile.Read(ref _executions);

        public ToolDescriptor Descriptor { get; } = new(
            "probe_tool", "probe_tool", "1.0.0", "probe",
            JsonDocument.Parse("""{"type":"object","properties":{"x":{"type":"string"}}}""")
                .RootElement.Clone());

        public ToolPermission DescribePermission(JsonElement a) =>
            new(ToolAccess.Read, true, [], [], []);
        public ToolExecutionPlan DescribeExecution(JsonElement a) => ToolExecutionPlan.Serial;

        public ValueTask<AgentToolResult> ExecuteAsync(
            ToolInvocation invocation, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _executions);
            using var document = JsonDocument.Parse("""{"ok":true}""");
            return ValueTask.FromResult(new AgentToolResult(document.RootElement.Clone()));
        }
    }

    /// <summary>第一轮请求工具，之后返回纯文本。</summary>
    private sealed class ToolCallingClient(string invocationId) : IChatClient
    {
        private int _calls;

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> m, ChatOptions? o = null, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> m, ChatOptions? o = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.CompletedTask;
            _calls++;
            if (_calls == 1)
            {
                yield return new ChatResponseUpdate(ChatRole.Assistant,
                    [new FunctionCallContent(invocationId, "probe_tool",
                        new Dictionary<string, object?> { ["x"] = "value" })]);
            }
            else
            {
                yield return new ChatResponseUpdate(ChatRole.Assistant, "done");
            }
        }

        public object? GetService(Type t, object? k = null) => null;
        public void Dispose() { }
    }

    private sealed class MultiChunkClient(string[] chunks) : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> m, ChatOptions? o = null, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> m, ChatOptions? o = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.CompletedTask;
            foreach (var chunk in chunks) yield return new ChatResponseUpdate(ChatRole.Assistant, chunk);
        }

        public object? GetService(Type t, object? k = null) => null;
        public void Dispose() { }
    }

    private sealed class SlowClient(TimeSpan delay) : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> m, ChatOptions? o = null, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> m, ChatOptions? o = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.Delay(delay, ct);
            yield return new ChatResponseUpdate(ChatRole.Assistant, "too late");
        }

        public object? GetService(Type t, object? k = null) => null;
        public void Dispose() { }
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
