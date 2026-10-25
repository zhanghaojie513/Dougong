using System.Text.Json;
using System.Text.Json.Nodes;
using Dougong.Acp;
using Dougong.Agent;
using Microsoft.Extensions.AI;

namespace Dougong.Agent.Comparison;

/// <summary>
/// ACP <c>plan</c> 的验收（G11）。
/// </summary>
/// <remarks>
/// **数据源必须是真实的**：这组检查的核心不是"能不能发出 plan 通知"，
/// 而是"plan 的内容来自模型显式声明，不是运行时推测"。
/// <para>
/// 因此检查链路是：模型调用 `todo_write` → 工具执行 → 运行时投影为
/// `PlanUpdated` → ACP 翻译为 `session/update{plan}`。
/// 任何一环缺失都会让"计划"变成假象。
/// </para>
/// </remarks>
public static class AcpPlanChecks
{
    public static async Task<int> RunAsync()
    {
        var failures = 0;
        failures += Check("todo-write-accepts-entries", AcceptsEntries);
        failures += Check("todo-write-rejects-empty-content", RejectsEmptyContent);
        failures += Check("todo-write-allows-unknown-status-words", AllowsUnknownWords);
        failures += await CheckAsync("plan-update-is-full-replacement", FullReplacementAsync);
        failures += await CheckAsync("acp-emits-plan-notification", AcpNotificationAsync);
        failures += await CheckAsync("failed-todo-write-does-not-emit-plan", NoPlanOnFailureAsync);

        Console.WriteLine(failures == 0
            ? "ACP-PLAN: ALL PASS"
            : $"ACP-PLAN: {failures} FAILURE(S)");
        return failures;
    }

    /// <summary>正常条目能被接受并原样回传。</summary>
    private static string? AcceptsEntries()
    {
        var tool = new TodoWriteTool();
        var result = tool.ExecuteAsync(Invocation("""
            {"entries":[
              {"content":"读代码","priority":"high","status":"in_progress"},
              {"content":"写测试","status":"pending"}
            ]}
            """), default).AsTask().GetAwaiter().GetResult();

        var content = result.Content;
        if (!content.GetProperty("ok").GetBoolean()) return "合法条目被拒绝";

        var entries = content.GetProperty("entries");
        if (entries.GetArrayLength() != 2) return $"应回传 2 条，实际 {entries.GetArrayLength()}";

        var first = entries[0];
        if (first.GetProperty("content").GetString() != "读代码") return "content 不符";
        if (first.GetProperty("priority").GetString() != "high") return "priority 不符";
        if (first.GetProperty("status").GetString() != "in_progress") return "status 不符";

        return null;
    }

    /// <summary>空内容必须被拒绝——空白条目在 UI 上等于没发生任何事。</summary>
    private static string? RejectsEmptyContent()
    {
        var tool = new TodoWriteTool();
        var result = tool.ExecuteAsync(Invocation("""{"entries":[{"content":"  ","status":"pending"}]}"""), default)
            .AsTask().GetAwaiter().GetResult();

        if (result.Content.GetProperty("ok").GetBoolean()) return "空内容未被拒绝";

        var error = result.Content.GetProperty("error").GetString() ?? string.Empty;
        return error.Contains("content", StringComparison.OrdinalIgnoreCase)
            ? null : $"错误信息未指出问题所在：{error}";
    }

    /// <summary>
    /// 未知的 status 词必须被接受。
    /// </summary>
    /// <remarks>
    /// ACP 只要求字符串，不同客户端的词表不同。强行枚举会在遇到没见过的词时丢信息——
    /// 那比多传一个字符串更糟。
    /// </remarks>
    private static string? AllowsUnknownWords()
    {
        var tool = new TodoWriteTool();
        var result = tool.ExecuteAsync(Invocation("""
            {"entries":[{"content":"某步","status":"blocked-on-review"}]}
            """), default).AsTask().GetAwaiter().GetResult();

        if (!result.Content.GetProperty("ok").GetBoolean()) return "未知 status 词被拒绝";

        var status = result.Content.GetProperty("entries")[0].GetProperty("status").GetString();
        return status == "blocked-on-review" ? null : $"status 被改写：{status}";
    }

    /// <summary>
    /// 计划是**全量替换**：第二次声明的条目里不含第一次的，最终就只有第二次的。
    /// </summary>
    private static async Task<string?> FullReplacementAsync()
    {
        var plans = new List<IReadOnlyList<PlanEntry>>();
        var dispatcher = new ToolDispatcher();
        dispatcher.Register(new TodoWriteTool());

        var loop = new LoopAgentRuntime(
            new ScriptedChatClient(
                new ScriptedTurn(new ScriptedUpdate(
                    FunctionCallId: "t1", FunctionName: "todo_write",
                    ArgumentsJson: """{"entries":[{"content":"A","status":"pending"}]}""")),
                new ScriptedTurn(new ScriptedUpdate(
                    FunctionCallId: "t2", FunctionName: "todo_write",
                    ArgumentsJson: """{"entries":[{"content":"B","status":"completed"}]}""")),
                new ScriptedTurn(new ScriptedUpdate(Text: "done"))),
            dispatcher);

        await foreach (var evt in loop.RunAsync(new AgentRunRequest("s", "t", [], "go")))
        {
            if (evt is AgentEvent.PlanUpdated update) plans.Add(update.Entries);
        }

        if (plans.Count != 2) return $"应收到 2 次计划更新，实际 {plans.Count}";

        // 第二次必须是完整的 1 条（B），而不是 A+B。
        if (plans[1].Count != 1)
            return $"覆盖后应为 1 条，实际 {plans[1].Count} 条——不是全量替换";
        if (plans[1][0].Content != "B")
            return $"覆盖后内容应为 B，实际 {plans[1][0].Content}";

        return null;
    }

    /// <summary>ACP 必须把 PlanUpdated 翻译成 session/update 的 plan。</summary>
    private static async Task<string?> AcpNotificationAsync()
    {
        await using var harness = new PlanHarness();

        var initialize = await harness.RequestAsync(new JsonObject
        {
            ["jsonrpc"] = "2.0", ["id"] = 1, ["method"] = "initialize",
            ["params"] = new JsonObject { ["protocolVersion"] = 1 },
        });
        if (initialize["result"] is null) return "initialize 失败";

        var session = await harness.RequestAsync(new JsonObject
        {
            ["jsonrpc"] = "2.0", ["id"] = 2, ["method"] = "session/new",
            ["params"] = new JsonObject
            {
                ["cwd"] = Directory.GetCurrentDirectory(),
                ["mcpServers"] = new JsonArray(),
            },
        });
        var sessionId = session["result"]?["sessionId"]?.GetValue<string>();
        if (string.IsNullOrEmpty(sessionId)) return $"session/new 失败：{session.ToJsonString()}";

        await harness.NotifyAsync(new JsonObject
        {
            ["jsonrpc"] = "2.0", ["id"] = 3, ["method"] = "session/prompt",
            ["params"] = new JsonObject
            {
                ["sessionId"] = sessionId,
                ["prompt"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = "plan" }),
            },
        });

        var plan = await harness.ReadUntilAsync("plan", TimeSpan.FromSeconds(10));
        if (plan is null) return "未收到 plan 通知";

        var update = plan["params"]!["update"]!;
        if (update["sessionUpdate"]?.GetValue<string>() != "plan")
            return $"sessionUpdate 应为 plan，实际 {update["sessionUpdate"]?.ToJsonString()}";

        var entries = update["entries"]?.AsArray();
        if (entries is null || entries.Count != 2)
            return $"plan 条目数不符：{update.ToJsonString()}";

        if (entries[0]?["content"]?.GetValue<string>() != "读代码") return "条目内容不符";
        if (entries[1]?["status"]?.GetValue<string>() != "completed") return "条目状态不符";

        return null;
    }

    /// <summary>工具失败时不得发出计划——否则 UI 会显示一份没被接受过的计划。</summary>
    private static async Task<string?> NoPlanOnFailureAsync()
    {
        var plans = 0;
        var dispatcher = new ToolDispatcher();
        dispatcher.Register(new TodoWriteTool());

        var loop = new LoopAgentRuntime(
            new ScriptedChatClient(
                new ScriptedTurn(new ScriptedUpdate(
                    FunctionCallId: "bad", FunctionName: "todo_write",
                    ArgumentsJson: """{"entries":[{"content":"","status":"pending"}]}""")),
                new ScriptedTurn(new ScriptedUpdate(Text: "done"))),
            dispatcher);

        await foreach (var evt in loop.RunAsync(new AgentRunRequest("s", "t", [], "go")))
        {
            if (evt is AgentEvent.PlanUpdated) plans++;
        }

        return plans == 0 ? null : $"工具失败却发出了 {plans} 次计划更新";
    }

    // ── 支撑 ─────────────────────────────────────────────────────

    private static ToolInvocation Invocation(string json)
    {
        using var document = JsonDocument.Parse(json);
        return new ToolInvocation("c1", PlanTool.ToolId, document.RootElement.Clone());
    }

    /// <summary>驱动真实 AcpAgentHost 的最小 harness。</summary>
    private sealed class PlanHarness : IAsyncDisposable
    {
        private readonly System.IO.Pipelines.Pipe _toHost = new();
        private readonly System.IO.Pipelines.Pipe _fromHost = new();
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _loop;
        private readonly StreamReader _reader;
        private readonly Stream _writer;

        public PlanHarness()
        {
            var host = new AcpAgentHost(
                _toHost.Reader.AsStream(leaveOpen: true),
                _fromHost.Writer.AsStream(leaveOpen: true),
                _ =>
                {
                    var dispatcher = new ToolDispatcher();
                    dispatcher.Register(new TodoWriteTool());

                    // 模型第一轮调用 todo_write，第二轮结束。
                    var client = new ScriptedChatClient(
                        new ScriptedTurn(new ScriptedUpdate(
                            FunctionCallId: "p1", FunctionName: "todo_write",
                            ArgumentsJson: """{"entries":[{"content":"读代码","status":"in_progress"},{"content":"写测试","status":"completed"}]}""")),
                        new ScriptedTurn(new ScriptedUpdate(Text: "done")));

                    return Task.FromResult(new AcpSessionRuntime(
                        new LoopAgentRuntime(client, dispatcher), dispatcher));
                },
                new StringWriter());

            _loop = Task.Run(() => host.RunAsync(_stop.Token));
            _writer = _toHost.Writer.AsStream(leaveOpen: true);
            _reader = new StreamReader(
                _fromHost.Reader.AsStream(leaveOpen: true),
                System.Text.Encoding.UTF8, false, 8192, leaveOpen: true);
        }

        public async Task<JsonNode> RequestAsync(JsonObject message)
        {
            await NotifyAsync(message);

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var id = message["id"]!.ToJsonString();
            while (true)
            {
                var line = await _reader.ReadLineAsync(cts.Token) ?? throw new TimeoutException("宿主关闭");
                var node = JsonNode.Parse(line)!;
                if (node["method"] is null && node["id"]?.ToJsonString() == id) return node;
            }
        }

        public async Task NotifyAsync(JsonObject message)
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes(
                message.ToJsonString(new JsonSerializerOptions { WriteIndented = false }) + "\n");
            await _writer.WriteAsync(bytes);
            await _writer.FlushAsync();
        }

        /// <summary>读到指定 sessionUpdate 的通知。</summary>
        public async Task<JsonNode?> ReadUntilAsync(string sessionUpdate, TimeSpan timeout)
        {
            using var cts = new CancellationTokenSource(timeout);
            while (true)
            {
                string? line;
                try { line = await _reader.ReadLineAsync(cts.Token); }
                catch (OperationCanceledException) { return null; }

                if (line is null) return null;
                var node = JsonNode.Parse(line);
                if (node?["method"]?.GetValue<string>() != "session/update") continue;
                if (node["params"]?["update"]?["sessionUpdate"]?.GetValue<string>() == sessionUpdate)
                    return node;
            }
        }

        public async ValueTask DisposeAsync()
        {
            _stop.Cancel();
            try { await _loop.WaitAsync(TimeSpan.FromSeconds(2)); } catch { }
        }
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
