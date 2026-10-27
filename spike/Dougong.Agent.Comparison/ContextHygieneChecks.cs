using System.Text;
using System.Text.Json;
using Dougong.Agent;
using Microsoft.Extensions.AI;

namespace Dougong.Agent.Comparison;

/// <summary>
/// 上下文卫生：工具结果的长度限制。
/// </summary>
/// <remarks>
/// 背景是真缺陷：工具结果原本以完整原文进入模型上下文，没有任何上限。
/// 一次 `read_file` 读大文件、或 MCP 工具返回大 JSON，就够把窗口挤爆——
/// 这正是"长任务跑到一半上下文超限"的直接原因。
/// <para>
/// 三条不变量，缺一不可：
/// <list type="number">
/// <item>超限结果进上下文时被裁剪</item>
/// <item>会话日志仍是完整原文（裁剪不能变成数据丢失）</item>
/// <item>裁剪有可观察的事件（不能让宿主以为模型看到了全部）</item>
/// </list>
/// </para>
/// </remarks>
public static class ContextHygieneChecks
{
    public static async Task<int> RunAsync()
    {
        var failures = 0;
        failures += Check("short-result-untouched", ShortResultUntouched);
        failures += Check("long-result-clamped", LongResultClamped);
        failures += Check("clamp-keeps-head-and-tail", ClampKeepsHeadAndTail);
        failures += Check("clamp-marker-is-explicit", ClampMarkerIsExplicit);
        failures += Check("clamp-boundary-is-exact", ClampBoundaryExact);
        failures += await CheckAsync("history-gets-clamped", HistoryGetsClampedAsync);
        failures += await CheckAsync("store-keeps-full-content", StoreKeepsFullAsync);
        failures += await CheckAsync("truncation-is-observable", TruncationObservableAsync);
        failures += await CheckAsync("restore-path-also-clamps", RestorePathClampsAsync);
        failures += await CheckAsync("restore-with-big-result-stays-under-budget",
            RestoreBigResultBudgetAsync);
        failures += Check("tool-declarations-fit-overhead-budget", ToolDeclarationsFitOverhead);
        failures += Check("overhead-default-matches-real-toolset", OverheadMatchesRealToolset);

        Console.WriteLine(failures == 0
            ? "CONTEXT-HYGIENE: ALL PASS"
            : $"CONTEXT-HYGIENE: {failures} FAILURE(S)");
        return failures;
    }

    private static string? ShortResultUntouched()
    {
        var text = new string('a', 1000);
        var clamped = ToolResultLimits.Clamp(text, out var truncated);

        if (truncated) return "短结果被标记为截断";
        return clamped == text ? null : "短结果被改动";
    }

    private static string? LongResultClamped()
    {
        var text = new string('a', ToolResultLimits.MaxContextChars * 3);
        var clamped = ToolResultLimits.Clamp(text, out var truncated);

        if (!truncated) return "超长结果未被标记截断";
        if (clamped.Length >= text.Length) return "超长结果长度没有下降";
        return null;
    }

    private static string? ClampKeepsHeadAndTail()
    {
        var head = new string('H', 1000);
        var middle = new string('M', ToolResultLimits.MaxContextChars * 2);
        var tail = new string('T', 1000);
        var text = head + middle + tail;

        var clamped = ToolResultLimits.Clamp(text, out _);

        // 头部与尾部都要留：头部是结构（JSON 起始、表头），尾部是结论或错误。
        if (!clamped.Contains(new string('H', 1000), StringComparison.Ordinal))
            return "截断后丢失了头部内容";
        if (!clamped.Contains(new string('T', 1000), StringComparison.Ordinal))
            return "截断后丢失了尾部内容";

        return null;
    }

    private static string? ClampMarkerIsExplicit()
    {
        var text = new string('a', ToolResultLimits.MaxContextChars * 3);
        var clamped = ToolResultLimits.Clamp(text, out _);

        // 没有标记，模型会把残缺数据当成完整事实——那比报错更危险。
        if (!clamped.Contains("截断", StringComparison.Ordinal))
            return "截断后没有明确的截断标记";
        if (!clamped.Contains("原文共", StringComparison.Ordinal))
            return "截断标记没有告知原始长度";

        return null;
    }

    private static string? ClampBoundaryExact()
    {
        // 恰好等于上限：不该截断（边界是"超过"才裁）
        var atLimit = new string('a', ToolResultLimits.MaxContextChars);
        ToolResultLimits.Clamp(atLimit, out var t1);
        if (t1) return "恰好等于上限时被裁了：边界判断差一";

        // 超一个字符：必须截断
        var overLimit = new string('a', ToolResultLimits.MaxContextChars + 1);
        ToolResultLimits.Clamp(overLimit, out var t2);
        if (!t2) return "超过上限一个字符却没有截断";

        return null;
    }

    /// <summary>
    /// 端到端：超长工具结果进入模型历史时必须已被裁剪。
    /// </summary>
    /// <remarks>
    /// 走真实运行时与真实 dispatcher：这一点必须端到端验证。
    /// 只测 <see cref="ToolResultLimits.Clamp"/> 是没用的——
    /// 裁剪可能压根没被接上（本项目已多次出现"能力只在测试里"的缺陷）。
    /// </remarks>
    private static async Task<string?> HistoryGetsClampedAsync()
    {
        var huge = new string('x', ToolResultLimits.MaxContextChars * 3);

        var tool = new FakeTool("big", [],
            (_, _) =>
            {
                using var doc = JsonDocument.Parse(
                    JsonSerializer.Serialize(new { data = huge }));
                return ValueTask.FromResult(new AgentToolResult(doc.RootElement.Clone()));
            });

        var dispatcher = new ToolDispatcher();
        dispatcher.Register(tool);

        var client = new ScriptedChatClient(
            // 第一轮：模型请求 big 工具
            new ScriptedTurn(new ScriptedUpdate(
                FunctionCallId: "c1", FunctionName: "big", ArgumentsJson: "{}")),
            // 第二轮：模型给答复
            new ScriptedTurn(new ScriptedUpdate(Text: "done")));

        var runtime = new LoopAgentRuntime(client, dispatcher);
        var truncationEvents = new List<AgentEvent.ToolResultTruncated>();

        await foreach (var evt in runtime.RunAsync(new AgentRunRequest("s", "t", [], "go")))
        {
            if (evt is AgentEvent.ToolResultTruncated t) truncationEvents.Add(t);
        }

        // 模型第二次被调用时收到的历史里，tool 消息必须是裁剪过的
        var secondCall = client.ObservedHistory.LastOrDefault();
        if (secondCall is null) return "模型只被调用了一次，工具循环没跑起来";

        var toolMessage = secondCall.FirstOrDefault(m => m.Role == ChatRole.Tool);
        if (toolMessage is null) return "模型第二次调用时没有收到 tool 消息";

        var received = toolMessage.Text ?? string.Empty;
        if (received.Length > ToolResultLimits.MaxContextChars + 500)
            return $"模型收到的 tool 内容长度 {received.Length} 未被裁剪"
                + $"（上限 {ToolResultLimits.MaxContextChars}）";

        if (!received.Contains("截断", StringComparison.Ordinal))
            return "裁剪后的内容没有截断标记";

        if (truncationEvents.Count == 0)
            return "发生了裁剪但没有任何 ToolResultTruncated 事件";

        var evt0 = truncationEvents[0];
        if (evt0.ToolId != "big") return $"截断事件的工具 ID 是 '{evt0.ToolId}'，应为 'big'";
        if (evt0.OriginalChars <= evt0.KeptChars)
            return "截断事件里原始长度不大于保留长度";

        return null;
    }

    /// <summary>
    /// 端到端：日志里必须是完整原文，裁剪只影响模型上下文。
    /// </summary>
    /// <remarks>
    /// 必须走真实运行时。直接手工构造 <see cref="StoreRecord"/> 写进日志是**假检查**：
    /// 那样测的是"JsonlAgentStore 不裁剪"，而它本来就不裁剪。真正要防的缺陷是
    /// "有人在提交记录之前就把内容裁了"——只有经过完整运行时才能发现。
    /// </remarks>
    private static async Task<string?> StoreKeepsFullAsync()
    {
        var dir = Path.Combine(Path.GetTempPath(), "dg-hyg-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);

        try
        {
            var path = Path.Combine(dir, "s.jsonl");
            var huge = new string('y', ToolResultLimits.MaxContextChars * 2);

            var tool = new FakeTool("big", [],
                (_, _) =>
                {
                    using var doc = JsonDocument.Parse(
                        JsonSerializer.Serialize(new { data = huge }));
                    return ValueTask.FromResult(new AgentToolResult(doc.RootElement.Clone()));
                });

            var dispatcher = new ToolDispatcher();
            dispatcher.Register(tool);

            var client = new ScriptedChatClient(
                new ScriptedTurn(new ScriptedUpdate(
                    FunctionCallId: "c1", FunctionName: "big", ArgumentsJson: "{}")),
                new ScriptedTurn(new ScriptedUpdate(Text: "done")));

            await using (var store = await JsonlAgentStore.OpenAsync(path, "sess-1"))
            {
                var runtime = new LoopAgentRuntime(client, dispatcher, new FixedStoreAccessor(store));
                await foreach (var _ in runtime.RunAsync(
                    new AgentRunRequest("sess-1", "t", [], "go"))) { }
            }

            // 从日志恢复：内容必须与工具原始输出等长（未被裁剪）
            await using var reopened = await JsonlAgentStore.OpenAsync(path, "sess-1");
            var recovered = await SessionRecovery.ReplayAsync(reopened);
            var toolMsg = recovered.History.FirstOrDefault(m => m.Role == AgentRole.Tool);

            if (toolMsg is null) return "恢复后没有 tool 消息";

            // 日志里的 content 是 JSON 原文（工具返回的整个对象），
            // 因此长度应明显大于裁剪上限——证明没被裁过。
            if (toolMsg.Content.Length <= ToolResultLimits.MaxContextChars)
                return $"日志里的内容长度 {toolMsg.Content.Length} 不超过裁剪上限"
                    + $" {ToolResultLimits.MaxContextChars}：裁剪污染了持久化";

            return null;
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }

    /// <summary>
    /// 恢复会话的路径也必须裁剪。
    /// </summary>
    /// <remarks>
    /// 这是一个真实缺口：`LoopAgentRuntime` 里有**两条**把工具结果放进模型历史的路径——
    /// <list type="number">
    /// <item>本轮工具执行完，经 <c>AppendToolExchange</c></item>
    /// <item>恢复会话/每轮重建历史，经 <c>BuildHistory</c> → <c>ToChatMessage</c></item>
    /// </list>
    /// 第一版只在路径 1 裁剪，路径 2 直接把日志里的完整原文塞进历史。
    /// 后果：恢复一个含大工具结果的会话，第一轮请求就把完整内容送进模型，
    /// 裁剪形同虚设。
    /// <para>
    /// 这个缺口不是测出来的，是**读代码找出来的**——说明"改一处就以为覆盖了"
    /// 本身就是缺陷来源。
    /// </para>
    /// </remarks>
    private static async Task<string?> RestorePathClampsAsync()
    {
        var dir = Path.Combine(Path.GetTempPath(), "dg-hyg-r-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);

        try
        {
            var path = Path.Combine(dir, "s.jsonl");
            var huge = new string('z', ToolResultLimits.MaxContextChars * 2);

            // 先造一个含超长工具结果的会话
            var tool = new FakeTool("big", [],
                (_, _) =>
                {
                    using var doc = JsonDocument.Parse(
                        JsonSerializer.Serialize(new { data = huge }));
                    return ValueTask.FromResult(new AgentToolResult(doc.RootElement.Clone()));
                });

            var dispatcher0 = new ToolDispatcher();
            dispatcher0.Register(tool);

            var client0 = new ScriptedChatClient(
                new ScriptedTurn(new ScriptedUpdate(
                    FunctionCallId: "c1", FunctionName: "big", ArgumentsJson: "{}")),
                new ScriptedTurn(new ScriptedUpdate(Text: "done")));

            await using (var store = await JsonlAgentStore.OpenAsync(path, "sess-1"))
            {
                var runtime = new LoopAgentRuntime(client0, dispatcher0, new FixedStoreAccessor(store));
                await foreach (var _ in runtime.RunAsync(
                    new AgentRunRequest("sess-1", "t", [], "go"))) { }
            }

            // 恢复会话，再跑一轮：模型收到的历史里 tool 消息必须已被裁剪
            await using var reopened = await JsonlAgentStore.OpenAsync(path, "sess-1");
            var recovered = await SessionRecovery.ReplayAsync(reopened);

            var client1 = new ScriptedChatClient(
                new ScriptedTurn(new ScriptedUpdate(Text: "继续")));
            var runtime1 = new LoopAgentRuntime(client1, new ToolDispatcher());

            await foreach (var _ in runtime1.RunAsync(
                new AgentRunRequest("sess-1", "t2", recovered.History, "继续"))) { }

            var firstCall = client1.ObservedHistory.FirstOrDefault();
            if (firstCall is null) return "恢复后模型没有被调用";

            var toolMessage = firstCall.FirstOrDefault(m => m.Role == ChatRole.Tool);
            if (toolMessage is null) return "恢复后的历史里没有 tool 消息";

            var received = toolMessage.Text ?? string.Empty;
            if (received.Length > ToolResultLimits.MaxContextChars + 500)
                return $"恢复路径未裁剪：模型收到 {received.Length} 字符"
                    + $"（上限 {ToolResultLimits.MaxContextChars}）";

            return null;
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }

    /// <summary>
    /// 恢复超大会话后，单轮请求不应超出上下文预算。
    /// </summary>
    /// <remarks>
    /// 与上一条的区别：这条看的是**总量**。即使单条被裁，若历史里有多条大结果，
    /// 整体仍可能超限——那说明裁剪上限对"多条累积"没有约束。
    /// </remarks>
    private static async Task<string?> RestoreBigResultBudgetAsync()
    {
        var dir = Path.Combine(Path.GetTempPath(), "dg-hyg-b-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);

        try
        {
            var path = Path.Combine(dir, "s.jsonl");
            var huge = new string('z', ToolResultLimits.MaxContextChars * 2);

            // 造 3 轮工具调用，每轮结果都超长
            var tool = new FakeTool("big", [],
                (_, _) =>
                {
                    using var doc = JsonDocument.Parse(
                        JsonSerializer.Serialize(new { data = huge }));
                    return ValueTask.FromResult(new AgentToolResult(doc.RootElement.Clone()));
                });

            var dispatcher = new ToolDispatcher();
            dispatcher.Register(tool);

            var turns = new List<ScriptedTurn>();
            for (var i = 0; i < 3; i++)
            {
                turns.Add(new ScriptedTurn(new ScriptedUpdate(
                    FunctionCallId: $"c{i}", FunctionName: "big", ArgumentsJson: "{}")));
            }
            turns.Add(new ScriptedTurn(new ScriptedUpdate(Text: "done")));

            var client0 = new ScriptedChatClient([.. turns]);

            await using (var store = await JsonlAgentStore.OpenAsync(path, "sess-1"))
            {
                var runtime = new LoopAgentRuntime(client0, dispatcher, new FixedStoreAccessor(store));
                await foreach (var _ in runtime.RunAsync(
                    new AgentRunRequest("sess-1", "t", [], "go"))) { }
            }

            await using var reopened = await JsonlAgentStore.OpenAsync(path, "sess-1");
            var recovered = await SessionRecovery.ReplayAsync(reopened);

            var client1 = new ScriptedChatClient(
                new ScriptedTurn(new ScriptedUpdate(Text: "继续")));
            var runtime1 = new LoopAgentRuntime(client1, new ToolDispatcher());

            await foreach (var _ in runtime1.RunAsync(
                new AgentRunRequest("sess-1", "t2", recovered.History, "继续"))) { }

            var firstCall = client1.ObservedHistory.FirstOrDefault();
            if (firstCall is null) return "恢复后模型没有被调用";

            var totalToolChars = firstCall
                .Where(m => m.Role == ChatRole.Tool)
                .Sum(m => (m.Text ?? string.Empty).Length);

            // 3 条各自被裁到上限附近是可接受的；但若完全没裁，总量会是 6 倍上限
            var ceiling = ToolResultLimits.MaxContextChars * 3 + 2000;
            if (totalToolChars > ceiling)
                return $"恢复后 tool 内容总量 {totalToolChars} 字符，超过上限 "
                    + $"{ceiling}：多条累积没有被约束";

            return null;
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }

    /// <summary>短结果不得产生截断事件（不能干扰正常路径）。</summary>
    private static async Task<string?> TruncationObservableAsync()
    {
        var tool = new FakeTool("small", []);
        var dispatcher = new ToolDispatcher();
        dispatcher.Register(tool);

        var client = new ScriptedChatClient(
            new ScriptedTurn(new ScriptedUpdate(
                FunctionCallId: "c1", FunctionName: "small", ArgumentsJson: "{}")),
            new ScriptedTurn(new ScriptedUpdate(Text: "done")));

        var runtime = new LoopAgentRuntime(client, dispatcher);
        var truncations = 0;

        await foreach (var evt in runtime.RunAsync(new AgentRunRequest("s", "t", [], "go")))
        {
            if (evt is AgentEvent.ToolResultTruncated) truncations++;
        }

        return truncations == 0
            ? null
            : $"短结果产生了 {truncations} 次截断事件：裁剪逻辑误伤正常路径";
    }

    private static int Check(string name, Func<string?> action)
    {
        try
        {
            var failure = action();
            if (failure is null) { Console.WriteLine($"PASS {name}"); return 0; }
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
            if (failure is null) { Console.WriteLine($"PASS {name}"); return 0; }
            Console.WriteLine($"FAIL {name}: {failure}");
            return 1;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"FAIL {name}: {ex.GetType().Name}: {ex.Message}");
            return 1;
        }
    }

    /// <summary>
    /// 工具声明的 token 消耗必须小于 <see cref="ContextLimits.OverheadTokens"/> 的默认值。
    /// </summary>
    /// <remarks>
    /// **为什么这是一条必须存在的检查**：<c>OverheadTokens</c> 是压缩预算里
    /// "工具声明 + 系统指令"的预留量。工具声明超出它时，压缩会**系统性低估**
    /// 实际占用——于是触发偏晚，最后撞上 provider 的上下文上限才失败。
    /// 而失败形态是"会话突然不可用"，不是"压缩得少一点"，很难归因。
    /// <para>
    /// 这条检查把"预留值够不够"变成可执行断言，而不是一句注释里的假设。
    /// 加工具（或把 schema 写复杂）时它会失败，提示同步调大 OverheadTokens。
    /// </para>
    /// </remarks>
    private static string? ToolDeclarationsFitOverhead()
    {
        // 与 CLI 的默认工具集对齐：read_file / write_file / todo_write / shell。
        var descriptors = RealToolDescriptors();
        var declared = DeclarationsTokens(descriptors);
        var budget = new ContextLimits
        {
            ContextWindowTokens = 128_000,
            MaxOutputTokens = 4_096,
        }.OverheadTokens;

        if (declared > budget)
        {
            return $"默认工具集声明约 {declared} token，超过 OverheadTokens={budget}。"
                + "压缩预算会低估实际占用、触发偏晚；请调大 ContextLimits.OverheadTokens "
                + "或精简工具 schema。";
        }

        return null;
    }

    /// <summary>
    /// <c>OverheadTokens</c> 的默认值必须对得上真实工具集，不能凭感觉给。
    /// </summary>
    /// <remarks>
    /// 判据是双向的：太小会低估（上一条检查覆盖），**太大则无谓地压缩**——
    /// 把 20 万窗口当 19 万用，长会话提前触发摘要，多花一次模型调用。
    /// 这条要求预留量至少是实测的 1.5 倍（留余量给系统指令与插件带来的额外工具），
    /// 但不超过实测的 8 倍（否则说明这个默认值已经没有指导意义）。
    /// </remarks>
    private static string? OverheadMatchesRealToolset()
    {
        var declared = DeclarationsTokens(RealToolDescriptors());
        var budget = new ContextLimits
        {
            ContextWindowTokens = 128_000,
            MaxOutputTokens = 4_096,
        }.OverheadTokens;

        if (budget < declared * 1.5)
            return $"OverheadTokens={budget} 相对实测工具声明 {declared} token 余量不足 50%；"
                + "再加一两个工具就会低估。";

        if (budget > declared * 8)
            return $"OverheadTokens={budget} 是实测工具声明 {declared} token 的 "
                + $"{budget / (double)declared:F1} 倍，余量过大——会导致过早压缩、"
                + "多花模型调用。请在调大工具集时同步校准这个默认值。";

        return null;
    }

    /// <summary>CLI 默认注册的工具，schema 与 <c>FileTools.cs</c> / <c>TodoWriteTool</c> 一致。</summary>
    private static List<ToolDescriptor> RealToolDescriptors() =>
    [
        new("read_file", "read_file", "1.0.0", "读取工作区内的文本文件",
            JsonDocument.Parse(
                """{"type":"object","properties":{"path":{"type":"string"}},"required":["path"]}""")
                .RootElement.Clone()),
        new("write_file", "write_file", "1.0.0", "写入工作区内的文本文件",
            JsonDocument.Parse(
                """{"type":"object","properties":{"path":{"type":"string"},"content":{"type":"string"}},"required":["path","content"]}""")
                .RootElement.Clone()),
        new(PlanTool.ToolId, PlanTool.ToolId, "1.0.0", "维护当前任务计划（全量替换）",
            JsonDocument.Parse(
                """{"type":"object","properties":{"entries":{"type":"array","items":{"type":"object","properties":{"content":{"type":"string"},"status":{"type":"string"}},"required":["content","status"]}}},"required":["entries"]}""")
                .RootElement.Clone()),
        new("shell", "shell", "1.0.0", "在工作区内执行命令",
            JsonDocument.Parse(
                """{"type":"object","properties":{"command":{"type":"string"},"args":{"type":"array","items":{"type":"string"}}},"required":["command"]}""")
                .RootElement.Clone()),
    ];

    /// <summary>工具声明占用的 token：名称 + 描述 + schema，与连接器实际发送的内容一致。</summary>
    private static int DeclarationsTokens(IReadOnlyList<ToolDescriptor> descriptors) =>
        TokenBudget.Estimate(string.Concat(descriptors.Select(d =>
            d.Name + d.Description + d.InputSchema.GetRawText())));
}
