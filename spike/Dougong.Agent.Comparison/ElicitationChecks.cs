using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dougong.Acp;
using Dougong.Agent;
using Microsoft.Extensions.AI;

namespace Dougong.Agent.Comparison;

/// <summary>
/// elicitation / <c>ask_user</c> 验收。
/// </summary>
/// <remarks>
/// 重点不是"能问出答案"，而是**四种结果不混淆**：
/// 用户答复、用户拒绝、用户关闭、以及"提问不可用"（客户端报错/传输失败）。
/// 调研里 Cursor 把传输失败当成"用户跳过"返回，模型因此误判。
/// <para>
/// 全部走真实的 <see cref="Stream"/> 配对驱动 <see cref="AcpAgentHost"/>：
/// 只有真的读到线上的帧，才能证明"请求确实发出去了"或"确实没发"。
/// </para>
/// </remarks>
public static class ElicitationChecks
{
    public static async Task<int> RunAsync()
    {
        var failures = 0;
        failures += await CheckAsync("accept-returns-answers", AcceptAsync);
        failures += await CheckAsync("decline-is-not-answer", DeclineAsync);
        failures += await CheckAsync("cancel-is-not-decline", CancelAsync);
        failures += await CheckAsync("client-error-is-unavailable", UnavailableAsync);
        failures += await CheckAsync("local-cancel-is-not-user-cancel", LocalCancelAsync);
        failures += await CheckAsync("invalid-shape-sends-nothing", InvalidShapeAsync);
        failures += await CheckAsync("capability-declares-form-not-url", CapabilityAsync);
        failures += await CheckAsync("multi-select-round-trips", MultiSelectAsync);
        failures += await CheckAsync("question-goes-through-dispatcher", DispatchAsync);
        failures += await CheckAsync("policy-still-asks-unknown-permission", PolicyScopeAsync);

        Console.WriteLine(failures == 0
            ? "ELICITATION: ALL PASS"
            : $"ELICITATION: {failures} FAILURE(S)");
        return failures;
    }

    // ── 四条结果路径 ─────────────────────────────────────────────

    /// <summary>用户提交答案：答案必须真的回到模型手里，且请求携带会话与调用标识。</summary>
    private static async Task<string?> AcceptAsync()
    {
        await using var host = new ToolHost();
        var execution = host.Tool.ExecuteAsync(Invocation(SingleQuestion), default).AsTask();

        var request = await host.ReadRequestAsync();
        var parameters = request["params"]!;

        if (parameters["sessionId"]?.GetValue<string>() != "sess-test")
            return $"请求未携带正确的 sessionId：{parameters["sessionId"]?.ToJsonString()}";
        if (parameters["toolCallId"]?.GetValue<string>() != "c1")
            return $"请求未携带 invocationId 作为 toolCallId：{parameters["toolCallId"]?.ToJsonString()}";
        if (parameters["mode"]?.GetValue<string>() != "form")
            return $"mode 应为 form，实际 {parameters["mode"]?.ToJsonString()}";

        // 选项必须真的写进了 schema：否则用户看不到任何可选项。
        var schema = parameters["requestedSchema"]!;
        var enumValues = schema["properties"]?["继续"]?["enum"]?.AsArray()
            .Select(v => v?.GetValue<string>()).ToArray();
        if (enumValues is null || enumValues.Length != 2)
            return $"schema 未包含两个选项：{schema.ToJsonString()}";

        await host.ReplyAsync(request, new JsonObject
        {
            ["action"] = "accept",
            ["content"] = new JsonObject { ["继续"] = "是" },
        });

        var result = await execution;
        var outcome = Read(result, "outcome");
        if (outcome != "Answered") return $"outcome 应为 Answered，实际 {outcome}";

        var answers = result.Content.GetProperty("answers");
        if (answers.GetArrayLength() != 1) return $"应返回 1 条答案，实际 {answers.GetArrayLength()}";

        var answer = answers[0].GetProperty("answer").GetString();
        return answer == "是" ? null : $"答案不符：{answer}";
    }

    /// <summary>用户明确拒绝：outcome 是 Declined，且不能带答案。</summary>
    private static async Task<string?> DeclineAsync()
    {
        await using var host = new ToolHost();
        var execution = host.Tool.ExecuteAsync(Invocation(SingleQuestion), default).AsTask();

        var request = await host.ReadRequestAsync();
        await host.ReplyAsync(request, new JsonObject { ["action"] = "decline" });

        var result = await execution;
        var outcome = Read(result, "outcome");
        if (outcome != "Declined") return $"outcome 应为 Declined，实际 {outcome}";

        var diagnostic = Read(result, "diagnostic");
        if (string.IsNullOrWhiteSpace(diagnostic)) return "拒绝时必须给出诊断文本";

        return result.Content.GetProperty("answers").GetArrayLength() == 0
            ? null : "拒绝时不应返回答案";
    }

    /// <summary>
    /// 用户关闭对话框：必须是 Cancelled，不能算成 Declined。
    /// </summary>
    /// <remarks>
    /// 这两个对模型的含义不同：拒绝意味着"用户看过并否决"，
    /// 关闭意味着"用户根本没作答"。混为一谈会让模型以为自己的方案被否决了。
    /// </remarks>
    private static async Task<string?> CancelAsync()
    {
        await using var host = new ToolHost();
        var execution = host.Tool.ExecuteAsync(Invocation(SingleQuestion), default).AsTask();

        var request = await host.ReadRequestAsync();
        await host.ReplyAsync(request, new JsonObject { ["action"] = "cancel" });

        var result = await execution;
        var outcome = Read(result, "outcome");
        if (outcome != "Cancelled") return $"outcome 应为 Cancelled，实际 {outcome}";

        var diagnostic = Read(result, "diagnostic") ?? string.Empty;
        return diagnostic.Contains("关闭", StringComparison.Ordinal)
            ? null : $"用户关闭的诊断未说明「关闭」：{diagnostic}";
    }

    /// <summary>客户端报错（不支持 elicitation）必须是 Unavailable，不能伪装成用户拒绝。</summary>
    private static async Task<string?> UnavailableAsync()
    {
        await using var host = new ToolHost();
        var execution = host.Tool.ExecuteAsync(Invocation(SingleQuestion), default).AsTask();

        var request = await host.ReadRequestAsync();
        await host.ReplyErrorAsync(request, -32601, "elicitation not supported");

        var result = await execution;
        var outcome = Read(result, "outcome");
        if (outcome != "Unavailable") return $"outcome 应为 Unavailable，实际 {outcome}";

        var diagnostic = Read(result, "diagnostic") ?? string.Empty;
        return diagnostic.Contains("not supported", StringComparison.Ordinal)
            ? null : $"诊断文本未保留客户端原因：{diagnostic}";
    }

    /// <summary>
    /// 本轮被取消时用户根本没看到问题。它与"用户关闭"同为 Cancelled，
    /// 但诊断必须能区分，否则宿主无法判断该不该重问。
    /// </summary>
    private static async Task<string?> LocalCancelAsync()
    {
        await using var host = new ToolHost();
        using var cts = new CancellationTokenSource();

        var execution = host.Tool.ExecuteAsync(Invocation(SingleQuestion), cts.Token).AsTask();
        await host.ReadRequestAsync();
        cts.Cancel();

        var result = await execution;
        var outcome = Read(result, "outcome");
        if (outcome != "Cancelled") return $"outcome 应为 Cancelled，实际 {outcome}";

        var diagnostic = Read(result, "diagnostic") ?? string.Empty;
        if (!diagnostic.Contains("取消", StringComparison.Ordinal))
            return $"诊断未说明本轮被取消：{diagnostic}";
        return diagnostic.Contains("未看到", StringComparison.Ordinal)
            ? null : $"诊断未说明用户没看到问题：{diagnostic}";
    }

    // ── 不打扰用户 ───────────────────────────────────────────────

    /// <summary>
    /// 形状不合法时不该打扰用户：请求根本不能发出。
    /// </summary>
    /// <remarks>
    /// 这里不只信一个计数器（那只说明"我没读"），
    /// 而是真的去读一次管道：有任何帧到达就判失败。
    /// </remarks>
    private static async Task<string?> InvalidShapeAsync()
    {
        await using var host = new ToolHost();

        // 给一个超时：若实现违例发出了请求，它会一直等回复；
        // 没有超时的话这条检查会挂住而不是失败——挂住不叫通过。
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));

        // 只有一个选项：不允许"凑数的第二选项"。
        var result = await host.Tool.ExecuteAsync(Invocation("""
            {"questions":[{"question":"只有一条路？","header":"路径","options":[
              {"label":"唯一","description":"没有别的选择"}]}]}
            """), cts.Token);

        var outcome = Read(result, "outcome");
        if (outcome != "InvalidRequest") return $"outcome 应为 InvalidRequest，实际 {outcome}";

        var diagnostic = Read(result, "diagnostic") ?? string.Empty;
        if (!diagnostic.Contains("凑数", StringComparison.Ordinal))
            return $"诊断未引导模型不要编造凑数选项：{diagnostic}";

        return await host.AssertSilentAsync(TimeSpan.FromMilliseconds(400));
    }

    // ── 能力声明 ─────────────────────────────────────────────────

    /// <summary>只声明 form 模式，不声明 url。</summary>
    private static async Task<string?> CapabilityAsync()
    {
        await using var host = new ToolHost();
        var response = await host.CallAsync(new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = 1,
            ["method"] = "initialize",
            ["params"] = new JsonObject { ["protocolVersion"] = 1 },
        });

        var caps = response["result"]?["agentCapabilities"]?["elicitation"];
        if (caps is null) return "未声明 elicitation 能力";
        if (caps["form"] is null) return "未声明 form 模式";

        // 不声明 url：URL 模式要求客户端在安全上下文打开链接并对 Punycode 域名告警，
        // 我们无法验证这些，声明了就是在承诺做不到的事。
        return caps["url"] is null ? null : "声明了 url 模式，但无法保证其安全要求";
    }

    // ── 其它形状 ─────────────────────────────────────────────────

    /// <summary>多选必须以数组形式往返，不能被折成单值。</summary>
    private static async Task<string?> MultiSelectAsync()
    {
        await using var host = new ToolHost();
        var execution = host.Tool.ExecuteAsync(Invocation("""
            {"questions":[{"question":"启用哪些检查？","header":"检查","multiSelect":true,"options":[
              {"label":"Lint","description":"静态检查"},
              {"label":"Test","description":"单元测试"},
              {"label":"Build","description":"编译"}]}]}
            """), default).AsTask();

        var request = await host.ReadRequestAsync();

        // 多选必须用数组 schema，否则客户端只会让用户挑一个。
        var property = request["params"]?["requestedSchema"]?["properties"]?["检查"];
        if (property?["type"]?.GetValue<string>() != "array")
            return $"多选 schema 应为 array，实际 {property?["type"]?.ToJsonString()}";

        await host.ReplyAsync(request, new JsonObject
        {
            ["action"] = "accept",
            ["content"] = new JsonObject { ["检查"] = new JsonArray("Lint", "Build") },
        });

        var result = await execution;
        var answers = result.Content.GetProperty("answers");
        if (answers.GetArrayLength() != 1) return "多选应返回 1 条答案";

        var answer = answers[0].GetProperty("answer").GetString() ?? string.Empty;
        return answer.Contains("Lint", StringComparison.Ordinal)
            && answer.Contains("Build", StringComparison.Ordinal)
            && !answer.Contains("Test", StringComparison.Ordinal)
            ? null : $"多选答案未完整保留：{answer}";
    }

    /// <summary>
    /// 经 dispatcher 走一遍：提问被当作普通工具调用，
    /// 不触发审批（否则用户会被问两次），终态为成功。
    /// </summary>
    private static async Task<string?> DispatchAsync()
    {
        await using var host = new ToolHost();
        var approver = new RecordingApprover();
        var policy = new PermissionPolicyApprover(approver);
        var dispatcher = new ToolDispatcher(policy);
        dispatcher.Register(host.Tool);

        var dispatch = dispatcher.DispatchAsync(Invocation(SingleQuestion)).AsTask();

        var request = await host.ReadRequestAsync();
        await host.ReplyAsync(request, new JsonObject
        {
            ["action"] = "accept",
            ["content"] = new JsonObject { ["继续"] = "否" },
        });

        var result = await dispatch;
        if (result.Outcome != ToolCallOutcome.Succeeded)
            return $"终态应为 Succeeded，实际 {result.Outcome}：{result.Diagnostic}";
        if (approver.Calls != 0)
            return $"提问经过了 {approver.Calls} 次审批；提问本身没有副作用，不该让用户被打扰两次";
        if (result.Permission is not { IsKnown: true, Access: ToolAccess.None })
            return $"权限声明应为已知的 None，实际 {result.Permission}";

        // 计划必须取工作区独占：否则问答提示可能与审批提示同时弹出。
        if (dispatcher.DispatchedInvocations.Contains("c1") is false)
            return "调用未经过 dispatcher";

        return null;
    }

    /// <summary>
    /// 反证：跳过审批的范围必须真的收窄。
    /// </summary>
    /// <remarks>
    /// 只验"提问没被审批"是不够的——如果策略把所有调用都跳过，那条检查照样通过。
    /// 这里用三种"不该被跳过"的工具确认边界：
    /// 未知权限、声明了路径、声明了读取访问。
    /// </remarks>
    private static async Task<string?> PolicyScopeAsync()
    {
        var approver = new RecordingApprover();
        var policy = new PermissionPolicyApprover(approver);
        var dispatcher = new ToolDispatcher(policy);

        dispatcher.Register(new ProbedTool("unknown", ToolPermission.Unknown));
        dispatcher.Register(new ProbedTool("pathy", new ToolPermission(
            ToolAccess.None, IsKnown: true, Paths: ["/etc"], Commands: [], Domains: [])));
        dispatcher.Register(new ProbedTool("reader", new ToolPermission(
            ToolAccess.Read, IsKnown: true, Paths: [], Commands: [], Domains: [])));

        foreach (var id in new[] { "unknown", "pathy", "reader" })
        {
            var result = await dispatcher.DispatchAsync(
                new ToolInvocation($"c-{id}", id, EmptyArguments()));

            if (!result.Executed)
                return $"'{id}' 应在审批通过后执行，实际 {result.Outcome}：{result.Diagnostic}";
        }

        if (approver.Calls != 3)
            return $"这三种调用都应走审批，实际只问了 {approver.Calls} 次";
        return policy.Skipped == 0
            ? null : $"不应跳过任何调用，实际跳过 {policy.Skipped} 次";
    }

    private sealed class ProbedTool(string id, ToolPermission permission) : IAgentTool
    {
        public ToolDescriptor Descriptor { get; } = new(
            id, id, "1.0.0", id,
            JsonDocument.Parse("""{"type":"object","properties":{}}""").RootElement.Clone());

        public ToolPermission DescribePermission(JsonElement arguments) => permission;

        public ToolExecutionPlan DescribeExecution(JsonElement arguments) =>
            new(ToolConcurrency.Serial);

        public ValueTask<AgentToolResult> ExecuteAsync(
            ToolInvocation invocation, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new AgentToolResult(EmptyArguments()));
    }

    private static JsonElement EmptyArguments()
    {
        using var document = JsonDocument.Parse("{}");
        return document.RootElement.Clone();
    }

    // ── 支撑 ─────────────────────────────────────────────────────

    private const string SingleQuestion = """
        {"questions":[{"question":"继续吗？","header":"继续","options":[
          {"label":"是","description":"继续"},
          {"label":"否","description":"停止"}]}]}
        """;

    private static ToolInvocation Invocation(string argumentsJson)
    {
        using var document = JsonDocument.Parse(argumentsJson);
        return new ToolInvocation("c1", "ask_user", document.RootElement.Clone());
    }

    private static string? Read(AgentToolResult result, string property) =>
        result.Content.TryGetProperty(property, out var value) ? value.GetString() : null;

    /// <summary>
    /// 单 host：一个 <see cref="AcpAgentHost"/> 同时被测试侧驱动、也被工具用来发请求。
    /// </summary>
    /// <remarks>
    /// 初版这里建了两个 host 共用一对管道，等于两个读循环抢同一份输入——
    /// 和之前 ACP harness 踩过的"两个消费者"是同一个错误。
    /// 现在只有一个 host，工具持有它的引用。
    /// </remarks>
    private sealed class ToolHost : IAsyncDisposable
    {
        private readonly System.IO.Pipelines.Pipe _toHost = new();
        private readonly System.IO.Pipelines.Pipe _fromHost = new();
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _loop;
        private readonly StreamReader _reader;
        private readonly Stream _writer;
        private readonly SemaphoreSlim _writeLock = new(1, 1);

        public ToolHost()
        {
            AcpAgentHost? host = null;
            host = new AcpAgentHost(
                _toHost.Reader.AsStream(leaveOpen: true),
                _fromHost.Writer.AsStream(leaveOpen: true),
                _ => Task.FromResult(new AcpSessionRuntime(
                    new LoopAgentRuntime(new NoopClient(), new ToolDispatcher()),
                    new ToolDispatcher())),
                new StringWriter());

            Tool = new AcpElicitationTool(host, "sess-test");
            _writer = _toHost.Writer.AsStream(leaveOpen: true);
            _reader = new StreamReader(
                _fromHost.Reader.AsStream(leaveOpen: true),
                Encoding.UTF8, false, 8192, leaveOpen: true);
            _loop = Task.Run(() => host.RunAsync(_stop.Token));
        }

        public AcpElicitationTool Tool { get; }

        /// <summary>读一条 <c>elicitation/create</c> 请求（跳过其它帧）。</summary>
        public async Task<JsonNode> ReadRequestAsync()
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (true)
            {
                var line = await _reader.ReadLineAsync(cts.Token)
                    ?? throw new TimeoutException("宿主连接已关闭，未收到 elicitation 请求");
                var node = JsonNode.Parse(line)!;
                if (node["method"]?.GetValue<string>() == "elicitation/create") return node;
            }
        }

        /// <summary>断言管道里不会再来任何帧。</summary>
        public async Task<string?> AssertSilentAsync(TimeSpan window)
        {
            using var cts = new CancellationTokenSource(window);
            try
            {
                var line = await _reader.ReadLineAsync(cts.Token);
                return line is null ? "宿主连接被意外关闭" : $"形状不合法却发出了帧：{line}";
            }
            catch (OperationCanceledException)
            {
                return null; // 窗口内没有任何帧，符合预期。
            }
        }

        /// <summary>发一条请求并等对应的响应。</summary>
        public async Task<JsonNode> CallAsync(JsonObject message)
        {
            await SendAsync(message);

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var id = message["id"]!.ToJsonString();

            while (true)
            {
                var line = await _reader.ReadLineAsync(cts.Token)
                    ?? throw new TimeoutException("宿主连接已关闭");
                var node = JsonNode.Parse(line)!;
                if (node["method"] is null && node["id"]?.ToJsonString() == id) return node;
            }
        }

        public Task ReplyAsync(JsonNode request, JsonObject result) =>
            SendAsync(new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = request["id"]!.DeepClone(),
                ["result"] = result,
            });

        public Task ReplyErrorAsync(JsonNode request, int code, string message) =>
            SendAsync(new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = request["id"]!.DeepClone(),
                ["error"] = new JsonObject { ["code"] = code, ["message"] = message },
            });

        private async Task SendAsync(JsonObject message)
        {
            var bytes = Encoding.UTF8.GetBytes(
                message.ToJsonString(new JsonSerializerOptions { WriteIndented = false }) + "\n");
            await _writeLock.WaitAsync();
            try
            {
                await _writer.WriteAsync(bytes);
                await _writer.FlushAsync();
            }
            finally { _writeLock.Release(); }
        }

        public async ValueTask DisposeAsync()
        {
            _stop.Cancel();
            try { await _loop.WaitAsync(TimeSpan.FromSeconds(2)); } catch { }
            _writeLock.Dispose();
        }
    }

    private sealed class RecordingApprover : IToolApprover
    {
        public int Calls { get; private set; }

        public ValueTask<ToolApprovalDecision> RequestApprovalAsync(
            ToolApprovalRequest request, CancellationToken cancellationToken = default)
        {
            Calls++;
            return ValueTask.FromResult(ToolApprovalDecision.Allow("不应被调用"));
        }
    }

    private sealed class NoopClient : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok")));

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield return new ChatResponseUpdate(ChatRole.Assistant, "ok");
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

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
