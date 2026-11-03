using System.Text;
using System.Text.Json;
using Dougong.Agent;
using Microsoft.Extensions.AI;

namespace Dougong.Agent.Comparison;

/// <summary>
/// 会话 resume / branch / list 的验收（docs/architecture.md §4.2「会话即树」）。
/// </summary>
/// <remarks>
/// 重点不是"能从文件读回来"，而是**恢复出的历史能直接用于下一轮请求**：
/// 工具交互若退化成扁平文本，assistant 的 <c>tool_calls</c> 与 tool 的
/// <c>tool_call_id</c> 配对就断了，provider 会拒掉 resumed 会话的第一轮。
/// 这一条只能靠"恢复后真的再跑一次模型"来验证，读 JSON 不够。
/// </remarks>
public static class SessionChecks
{
    public static async Task<int> RunAsync()
    {
        var failures = 0;
        failures += await CheckAsync("resume-rebuilds-tool-exchange", ResumeRebuildsToolExchangeAsync);
        failures += await CheckAsync("resume-blocks-on-unknown-outcome", ResumeBlocksOnUnknownAsync);
        failures += await CheckAsync("branch-truncates-at-fork-point", BranchTruncatesAsync);
        failures += await CheckAsync("branch-does-not-touch-source", BranchKeepsSourceAsync);
        failures += await CheckAsync("branch-rejects-missing-sequence", BranchMissingSequenceAsync);
        failures += await CheckAsync("list-reports-sequence-range", ListReportsRangeAsync);
        failures += await CheckAsync("list-detects-gap", ListDetectsGapAsync);
        failures += await CheckAsync("list-separates-in-use-from-broken", ListSeparatesInUseAsync);
        failures += await CheckAsync("legacy-string-payload-still-reads", LegacyPayloadAsync);
        failures += await CheckAsync("branch-avoids-dangling-tool-call", BranchAvoidsDanglingAsync);

        // 格式版本：写进去、旧文件能读、高版本明确拒绝。
        // 第三条是重点——没有它，格式不兼容会被静默读成空会话。
        failures += await CheckAsync("session-writes-format-version", WritesFormatVersionAsync);
        failures += await CheckAsync("session-reads-old-files-without-version", ReadsOldFileAsync);
        failures += await CheckAsync("session-rejects-newer-format", RejectsNewerFormatAsync);
        failures += await CheckAsync("session-rejects-malformed-version", RejectsMalformedVersionAsync);
        failures += await CheckAsync("branch-writes-format-version", BranchWritesVersionAsync);

        Console.WriteLine(failures == 0
            ? "SESSION: ALL PASS"
            : $"SESSION: {failures} FAILURE(S)");
        return failures;
    }

    // 恢复出的历史必须带上 tool_calls / tool_call_id 配对，并能真的续跑一轮。
    private static async Task<string?> ResumeRebuildsToolExchangeAsync()
    {
        var root = NewRoot();
        try
        {
            var path = Path.Combine(root, "s.jsonl");
            var tool = new FakeTool("read", ["path"]);
            var dispatcher = new ToolDispatcher();
            dispatcher.Register(tool);

            // 第一轮：模型请求工具，运行结束。
            var client = new ScriptedChatClient(
                new ScriptedTurn(
                    new ScriptedUpdate(Text: "reading "),
                    new ScriptedUpdate(FunctionCallId: "c1", FunctionName: "read",
                        ArgumentsJson: """{"path":"a.txt"}""")),
                new ScriptedTurn(new ScriptedUpdate(Text: "done")));

            await using (var store = await JsonlAgentStore.OpenAsync(path, "s1"))
            {
                var runtime = new LoopAgentRuntime(client, dispatcher, new FixedStoreAccessor(store));
                await CollectAsync(runtime, "read a.txt");
            }

            // 重开并恢复。
            await using var reopened = await JsonlAgentStore.OpenAsync(path, "s1");
            var recovered = await SessionRecovery.ReplayAsync(reopened);

            if (recovered.RequiresReconciliation) return "正常会话不该需要人工对账";
            if (recovered.History.Count == 0) return "恢复后没有历史";

            var assistant = recovered.History.FirstOrDefault(m => m.Role == AgentRole.Assistant);
            if (assistant is null) return "恢复历史缺少 assistant 消息";
            var call = assistant.ToolCalls.FirstOrDefault();
            if (call is null) return "恢复历史丢失工具调用（tool_calls 配对已断）";
            if (call.InvocationId != "c1" || call.ToolId != "read") return "调用身份不符";
            if (!call.Arguments.TryGetProperty("path", out var pathValue)
                || pathValue.GetString() != "a.txt")
                return "调用参数丢失";

            var toolMessage = recovered.History.FirstOrDefault(m => m.Role == AgentRole.Tool);
            if (toolMessage is null) return "恢复历史缺少 tool 消息";
            if (toolMessage.ToolCallId != "c1") return "tool 消息缺 tool_call_id";
            if (toolMessage.Content is not { Length: > 0 }) return "tool 消息内容为空";

            // 关键：用恢复出的历史真的再请求一次模型，确认协议层能接受。
            var resumed = new ScriptedChatClient(
                new ScriptedTurn(new ScriptedUpdate(Text: "resumed")));
            var resumedRuntime = new LoopAgentRuntime(resumed, dispatcher);
            await foreach (var _ in resumedRuntime.RunAsync(
                new AgentRunRequest("s1", "turn-2", recovered.HistoryForResume, "continue")))
            {
            }

            if (resumed.ObservedHistory.Count == 0) return "恢复后没有发起模型请求";

            var sent = resumed.ObservedHistory[0];
            var sentAssistant = sent.FirstOrDefault(m => m.Role == ChatRole.Assistant);
            if (sentAssistant is null) return "发给模型的请求里没有 assistant 历史";
            if (!sentAssistant.Contents.OfType<FunctionCallContent>().Any())
                return "发给模型的 assistant 历史缺 FunctionCallContent";

            var sentTool = sent.LastOrDefault(m => m.Role == ChatRole.Tool);
            if (sentTool is null) return "发给模型的请求里没有 tool 历史";
            if (sentTool.AdditionalProperties is null
                || !sentTool.AdditionalProperties.TryGetValue("tool_call_id", out var sentId)
                || sentId?.ToString() != "c1")
                return "发给模型的 tool 历史缺 tool_call_id";
            if (sentTool.Text is not { Length: > 0 }) return "发给模型的 tool 历史内容为空";

            return null;
        }
        finally { TryDelete(root); }
    }

    // 有开始无终态：必须阻塞续跑，并给出可读原因。
    private static async Task<string?> ResumeBlocksOnUnknownAsync()
    {
        var root = NewRoot();
        try
        {
            var path = Path.Combine(root, "u.jsonl");
            await using (var store = await JsonlAgentStore.OpenAsync(path, "s2"))
            {
                await store.CommitAsync(new StoreRecord(0, StoreRecordKind.UserInput,
                    JsonSerializer.SerializeToElement("do it")));
                await store.CommitAsync(new StoreRecord(0, StoreRecordKind.ToolCallStarted,
                    JsonDocument.Parse("""{"id":"c1","tool":"write_file"}""").RootElement.Clone()));
                // 故意不写终态。
            }

            await using var reopened = await JsonlAgentStore.OpenAsync(path, "s2");
            var recovered = await SessionRecovery.ReplayAsync(reopened);

            if (!recovered.RequiresReconciliation) return "未识别出未知结果";
            var reason = recovered.DescribeBlockingReason();
            if (reason is null) return "需要阻塞时未给出原因";
            if (!reason.Contains("c1", StringComparison.Ordinal)) return $"原因里没有调用 ID：{reason}";
            return reason.Contains("不能自动重放", StringComparison.Ordinal)
                ? null
                : $"原因缺少「不能自动重放」的说明：{reason}";
        }
        finally { TryDelete(root); }
    }

    // 分支只保留分叉点及之前的记录。
    private static async Task<string?> BranchTruncatesAsync()
    {
        var root = NewRoot();
        try
        {
            var source = Path.Combine(root, "src.jsonl");
            await using (var store = await JsonlAgentStore.OpenAsync(source, "src"))
            {
                await store.CommitAsync(Record(StoreRecordKind.UserInput, "first"));
                await store.CommitAsync(Record(StoreRecordKind.ModelResponse,
                    """{"text":"one","calls":[]}"""));
                await store.CommitAsync(Record(StoreRecordKind.UserInput, "second"));
            }

            var target = Path.Combine(root, "branch.jsonl");
            var branch = await SessionBranch.CreateAsync(source, target, "br1", atSequence: 2);

            if (branch.LastSequence != 2) return $"分支末尾序号应为 2，实际 {branch.LastSequence}";
            if (branch.RecordCount != 2) return $"应保留 2 条，实际 {branch.RecordCount}";

            await using var reopened = await JsonlAgentStore.OpenAsync(target, "br1");
            var recovered = await SessionRecovery.ReplayAsync(reopened);

            if (recovered.History.Count != 2)
                return $"恢复出 {recovered.History.Count} 条，应为 2 条";
            if (recovered.History.Any(m => m.Content.Contains("second", StringComparison.Ordinal)))
                return "分叉点之后的记录没被丢弃";

            // 分支可以继续追加，且序号从分叉点续接。
            await reopened.CommitAsync(Record(StoreRecordKind.UserInput, "branch-only"));
            var after = await SessionRecovery.ReplayAsync(reopened);
            if (after.LastSequence != 3) return $"分支续写序号应为 3，实际 {after.LastSequence}";

            return null;
        }
        finally { TryDelete(root); }
    }

    // 分支不改动源会话。
    private static async Task<string?> BranchKeepsSourceAsync()
    {
        var root = NewRoot();
        try
        {
            var source = Path.Combine(root, "src.jsonl");
            await using (var store = await JsonlAgentStore.OpenAsync(source, "src"))
            {
                await store.CommitAsync(Record(StoreRecordKind.UserInput, "first"));
                await store.CommitAsync(Record(StoreRecordKind.UserInput, "second"));
            }

            var before = await File.ReadAllBytesAsync(source);
            await SessionBranch.CreateAsync(source, Path.Combine(root, "b.jsonl"), "br", atSequence: 1);
            var after = await File.ReadAllBytesAsync(source);

            if (before.Length != after.Length) return "源会话长度被改变";
            if (!before.SequenceEqual(after)) return "源会话内容被改变";
            return null;
        }
        finally { TryDelete(root); }
    }

    // 分叉点不存在时必须报错，不能悄悄退化成"复制全部"。
    private static async Task<string?> BranchMissingSequenceAsync()
    {
        var root = NewRoot();
        try
        {
            var source = Path.Combine(root, "src.jsonl");
            await using (var store = await JsonlAgentStore.OpenAsync(source, "src"))
                await store.CommitAsync(Record(StoreRecordKind.UserInput, "only"));

            try
            {
                await SessionBranch.CreateAsync(
                    source, Path.Combine(root, "b.jsonl"), "br", atSequence: 99);
                return "不存在的分叉点未报错";
            }
            catch (InvalidOperationException)
            {
                return null;
            }
        }
        finally { TryDelete(root); }
    }

    // list 报出序号范围，用于挑选会话。
    private static async Task<string?> ListReportsRangeAsync()
    {
        var root = NewRoot();
        try
        {
            var path = Path.Combine(root, "a.jsonl");
            await using (var store = await JsonlAgentStore.OpenAsync(path, "a"))
            {
                await store.CommitAsync(Record(StoreRecordKind.UserInput, "x"));
                await store.CommitAsync(Record(StoreRecordKind.UserInput, "y"));
            }

            var list = SessionBranch.List(root);
            if (list.Count != 1) return $"应列出 1 个会话，实际 {list.Count}";
            var item = list[0];
            if (item.RecordCount != 2) return $"记录数应为 2，实际 {item.RecordCount}";
            if (item.FirstSequence != 1 || item.LastSequence != 2)
                return $"序号范围应为 1..2，实际 {item.FirstSequence}..{item.LastSequence}";
            if (item.HasSequenceGap) return "正常会话被报为有缺口";
            return null;
        }
        finally { TryDelete(root); }
    }

    // 序号缺口必须被 list 报出，不能静默当作连续。
    /// <summary>
    /// "正在被使用"与"日志有缺口"必须分开报。
    /// </summary>
    /// <remarks>
    /// <b>这是真实使用中发现的问题</b>：`--list` 跑在用户机器上时，
    /// 正在进行的那个会话显示成"序号缺口（不可续跑）"——因为写者持有
    /// <c>FileShare.None</c> 的写租约，扫描用的 <c>FileShare.ReadWrite</c>
    /// 打不开，走了 <c>catch (IOException)</c> 分支。
    ///
    /// <para>
    /// 两者的处置完全相反：一个是"等一下再看"，另一个是"数据坏了要恢复"。
    /// 混成一句话会把用户引向错误的动作。
    /// </para>
    /// </remarks>
    private static async Task<string?> ListSeparatesInUseAsync()
    {
        var root = NewRoot();
        try
        {
            var path = Path.Combine(root, "inuse.jsonl");
            await using var store = await JsonlAgentStore.OpenAsync(path, "inuse");

            await store.CommitAsync(Record(StoreRecordKind.UserInput, "x"));

            // store 还持有写租约，此时从外面列目录。
            var listed = SessionBranch.List(root);
            var summary = listed.FirstOrDefault(s => s.SessionId == "inuse");

            if (summary is null) return "会话没有被列出来";

            if (!summary.IsUnreadable)
                return "持有写租约的会话没有被标记为不可读";

            if (summary.HasSequenceGap)
                return "不可读被同时报成了序号缺口——两者的处置完全不同";

            return null;
        }
        finally
        {
            TryDelete(root);
        }
    }

    private static async Task<string?> ListDetectsGapAsync()
    {
        var root = NewRoot();
        try
        {
            var path = Path.Combine(root, "g.jsonl");
            await using (var store = await JsonlAgentStore.OpenAsync(path, "g"))
            {
                await store.CommitAsync(Record(StoreRecordKind.UserInput, "x"));
                await store.CommitAsync(Record(StoreRecordKind.UserInput, "y"));
            }

            // 手工改掉第二条的序号，制造缺口。
            var lines = await File.ReadAllLinesAsync(path);
            var edited = new StringBuilder();
            for (var i = 0; i < lines.Length; i++)
            {
                if (string.IsNullOrWhiteSpace(lines[i])) continue;

                // 版本行不是记录：原样保留，不参与改写。
                // 不跳过的话 JsonDocument.Parse 会抛，而那条错误看起来像
                // "会话文件坏了"，实际只是测试没考虑到格式行。
                if (lines[i].StartsWith(SessionFormat.Prefix, StringComparison.Ordinal))
                {
                    edited.AppendLine(lines[i]);
                    continue;
                }

                using var document = JsonDocument.Parse(lines[i]);
                var kind = document.RootElement.GetProperty("Kind").GetString();
                var sequence = i == 1 ? 5 : document.RootElement.GetProperty("Sequence").GetInt64();
                var replacement = JsonSerializer.Serialize(new
                {
                    Sequence = sequence,
                    Kind = kind,
                    Payload = document.RootElement.GetProperty("Payload").Clone(),
                });
                edited.AppendLine(replacement);
            }

            await File.WriteAllTextAsync(path, edited.ToString());

            var list = SessionBranch.List(root);
            if (list.Count != 1) return $"应列出 1 个会话，实际 {list.Count}";
            return list[0].HasSequenceGap ? null : "序号缺口未被报出";
        }
        finally { TryDelete(root); }
    }

    // 旧会话（Payload 被双重编码成字符串）仍应可读，不能让历史会话整体失效。
    private static async Task<string?> LegacyPayloadAsync()
    {
        var root = NewRoot();
        try
        {
            var path = Path.Combine(root, "legacy.jsonl");
            // 双重编码：Payload 是 JSON 字符串，内容本身又是一段 JSON。
            var inner = """{"text":"legacy","calls":[]}""";
            var line = JsonSerializer.Serialize(new
            {
                Sequence = 1,
                Kind = "ModelResponse",
                Payload = inner,
            });

            await File.WriteAllTextAsync(path, line + "\n");

            await using var store = await JsonlAgentStore.OpenAsync(path, "old");
            var recovered = await SessionRecovery.ReplayAsync(store);

            if (recovered.HasSequenceGap) return "旧格式被误判为序号缺口";
            var assistant = recovered.History.FirstOrDefault(m => m.Role == AgentRole.Assistant);
            if (assistant is null) return "旧格式的模型响应未被恢复";
            if (!assistant.Content.Contains("legacy", StringComparison.Ordinal))
                return $"旧格式内容未被正确解析：{assistant.Content}";
            return null;
        }
        finally { TryDelete(root); }
    }

    // 分支点若落在工具交互中间，必须回退到最近的完整点：
    // 只留 assistant 的 tool_calls 而丢掉 tool 结果，会得到悬空调用，
    // provider 会直接拒掉续跑（实测 400 "must be followed by tool messages"）。
    private static async Task<string?> BranchAvoidsDanglingAsync()
    {
        var root = NewRoot();
        try
        {
            var source = Path.Combine(root, "src.jsonl");
            await using (var store = await JsonlAgentStore.OpenAsync(source, "src"))
            {
                await store.CommitAsync(Record(StoreRecordKind.UserInput, "read a.txt"));
                // #2 声明调用，但结果在 #3
                await store.CommitAsync(Record(StoreRecordKind.ModelResponse,
                    """{"text":"","calls":[{"id":"c1","tool":"read","arguments":{"path":"a.txt"}}]}"""));
                await store.CommitAsync(Record(StoreRecordKind.ToolCallCompleted,
                    """{"id":"c1","tool":"read","outcome":"Succeeded","content":"{\"ok\":true}"}"""));
                await store.CommitAsync(Record(StoreRecordKind.UserInput, "next"));
            }

            // 请求在 #2 分叉——正好是悬空点，应回退到 #3。
            var target = Path.Combine(root, "b.jsonl");
            var result = await SessionBranch.CreateAsync(source, target, "br", atSequence: 2);

            if (result.LastSequence != 3)
                return $"应回退到完整的 #3，实际停在 #{result.LastSequence}";

            await using var reopened = await JsonlAgentStore.OpenAsync(target, "br");
            var recovered = await SessionRecovery.ReplayAsync(reopened);

            foreach (var message in recovered.History.Where(m => m.Role == AgentRole.Assistant))
            {
                var declared = message.ToolCalls.Select(c => c.InvocationId).ToList();
                if (declared.Count == 0) continue;
                foreach (var id in declared)
                {
                    if (!recovered.History.Any(m => m.Role == AgentRole.Tool && m.ToolCallId == id))
                        return $"分支历史里调用 {id} 没有对应的工具结果（悬空）";
                }
            }

            // 不应包含 #4。
            if (recovered.History.Any(m => m.Content.Contains("next", StringComparison.Ordinal)))
                return "分叉点之后的记录没被丢弃";

            return null;
        }
        finally { TryDelete(root); }
    }

    private static StoreRecord Record(StoreRecordKind kind, string payload)
    {
        // 与运行时写入一致：JSON 按结构存，纯文本按字符串存。
        JsonElement element;
        try { element = JsonDocument.Parse(payload).RootElement.Clone(); }
        catch (JsonException) { element = JsonSerializer.SerializeToElement(payload); }
        return new StoreRecord(0, kind, element);
    }

    private static async Task<IReadOnlyList<AgentEvent>> CollectAsync(
        IAgentRuntime runtime, string input)
    {
        var events = new List<AgentEvent>();
        await foreach (var e in runtime.RunAsync(new AgentRunRequest("s", "t", [], input)))
            events.Add(e);
        return events;
    }

    private static string NewRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "dg-sess-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(root);
        return root;
    }

    private static void TryDelete(string root)
    {
        try { Directory.Delete(root, recursive: true); } catch { }
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

    // ── 格式版本 ─────────────────────────────────────────────────

    /// <summary>新会话文件第一行必须是版本行。</summary>
    /// <remarks>
    /// <b>没有它，格式不兼容无法判定</b>：旧程序读到新格式会逐行解析失败，
    /// 而扫描逻辑把解析失败当成"尾部半条记录"，于是原地停下、
    /// 把它当空会话继续写——用户的历史看起来凭空消失，且没有任何提示。
    /// </remarks>
    private static async Task<string?> WritesFormatVersionAsync()
    {
        using var tree = new TempSessionTree();
        var path = Path.Combine(tree.Root, "s.jsonl");

        await using (var store = await JsonlAgentStore.OpenAsync(path, "s1"))
        {
            var payload = ToElement("""{"x":1}""");
            await store.CommitAsync(new StoreRecord(0, StoreRecordKind.UserInput, payload));
        }

        var first = File.ReadAllLines(path).FirstOrDefault() ?? string.Empty;
        return first.StartsWith(SessionFormat.Prefix, StringComparison.Ordinal)
            ? null
            : $"首行不是版本行：'{first}'";
    }

    /// <summary>没有版本行的旧文件必须能正常打开（按当前版本处理）。</summary>
    /// <remarks>
    /// <b>这条守的是不能破坏已有数据</b>：机器上已有的会话都没有版本行。
    /// 加了版本校验却让旧文件打不开，等于把"防未来的故障"变成"现在就坏"。
    /// </remarks>
    private static async Task<string?> ReadsOldFileAsync()
    {
        using var tree = new TempSessionTree();
        var path = Path.Combine(tree.Root, "old.jsonl");

        // 手写一份旧格式：没有版本行，直接是记录。
        await File.WriteAllTextAsync(path,
            """
            {"Sequence":1,"Kind":"UserInput","Payload":{"a":1}}
            {"Sequence":2,"Kind":"ModelResponse","Payload":{"text":"hi"}}
            """);

        try
        {
            await using var store = await JsonlAgentStore.OpenAsync(path, "old");
            // 能看到已有两条记录：再提交一条应得到序号 3。
            var payload = ToElement("{}");
            await store.CommitAsync(new StoreRecord(0, StoreRecordKind.RunFinished, payload));
        }
        catch (Exception ex)
        {
            return $"旧文件打不开了（这会破坏机器上已有的全部会话）：{ex.GetType().Name}: {ex.Message}";
        }

        var lines = await File.ReadAllLinesAsync(path);
        var last = lines.LastOrDefault(l => l.Contains("\"Sequence\":3", StringComparison.Ordinal));
        return last is not null
            ? null
            : $"序号没接上已记录的最大值，最后一行：{lines.LastOrDefault()}";
    }

    /// <summary>
    /// 更高格式版本必须明确拒绝，不能尝试解析。
    /// </summary>
    /// <remarks>
    /// <b>硬解析比拒绝更危险</b>：它可能产出"看起来正常但内容错乱"的会话，
    /// 而那种错误只会在用户发现历史不对时才暴露，届时很难区分
    /// 是数据坏了还是读错了。拒绝至少让人知道**数据还在、只是读不了**。
    /// </remarks>
    private static async Task<string?> RejectsNewerFormatAsync()
    {
        using var tree = new TempSessionTree();
        var path = Path.Combine(tree.Root, "future.jsonl");

        await File.WriteAllTextAsync(path,
            $"{SessionFormat.Prefix}{SessionFormat.Current + 1}\n"
            + """{"Sequence":1,"Kind":"UserInput","Payload":{}}""" + "\n");

        try
        {
            await using var store = await JsonlAgentStore.OpenAsync(path, "future");
            return "更高版本被放行了——会静默读出错误内容";
        }
        catch (SessionFormatException ex)
        {
            // 消息里必须说清"不是损坏"，否则用户会以为数据没了。
            return ex.Message.Contains("不是文件损坏", StringComparison.Ordinal)
                ? null
                : $"拒绝了但没说清性质：{ex.Message}";
        }
    }

    /// <summary>
    /// 版本行内容无效（非正整数）必须拒绝，不能当成合法版本。
    /// </summary>
    /// <remarks>
    /// <b>此前只判 <c>fileVersion &gt; Current</c></b>，于是 <c>0</c> 与负数被放行。
    /// 那是"读不懂却当成合法"——比不检查更糟：后续解析会按一个
    /// 根本不存在的格式规则走，而错误只在数据出问题时才暴露。
    /// <para>
    /// 三种形态都要拒：<c>abc</c>（不是数字）、<c>0</c>（版本从 1 开始）、
    /// <c>-5</c>（负数）。
    /// </para>
    /// </remarks>
    private static async Task<string?> RejectsMalformedVersionAsync()
    {
        using var tree = new TempSessionTree();

        foreach (var raw in new[] { "abc", "0", "-5" })
        {
            var path = Path.Combine(tree.Root, $"bad-{Guid.NewGuid():N}.jsonl");
            await File.WriteAllTextAsync(path, $"{SessionFormat.Prefix}{raw}\n");

            try
            {
                await using var store = await JsonlAgentStore.OpenAsync(path, "bad");
                return $"版本行 '{raw}' 被当成合法版本放行了";
            }
            catch (SessionFormatException ex)
            {
                // 消息必须说清这不是损坏，否则用户以为数据没了。
                if (!ex.Message.Contains("不是文件损坏", StringComparison.Ordinal))
                    return $"拒绝了但没说清性质：{ex.Message}";
            }
        }

        return null;
    }

    /// <summary>
    /// 分叉出的新会话必须自带版本行。
    /// </summary>
    /// <remarks>
    /// <b>这条钉的是一个真实漏改</b>：分支逻辑收集了源文件的版本行（<c>formatLine</c>），
    /// 但从未写进目标文件——变量赋值了却没用，编译器不会报。
    /// <para>
    /// 后果不像"丢一个字段"那么轻：分支文件变成"无版本行"，
    /// 而读取端把那种文件按**当前**版本处理。格式升级后，这些分支会被
    /// 按一个它们并不符合的格式解析，**而且不会报错**——正是本类要防的那种静默错误。
    /// </para>
    /// </remarks>
    private static async Task<string?> BranchWritesVersionAsync()
    {
        using var tree = new TempSessionTree();
        var source = Path.Combine(tree.Root, "src.jsonl");

        await using (var store = await JsonlAgentStore.OpenAsync(source, "src"))
        {
            await store.CommitAsync(Record(StoreRecordKind.UserInput, "hello"));
        }

        var target = Path.Combine(tree.Root, "branch.jsonl");
        await SessionBranch.CreateAsync(source, target, "br", atSequence: 1);

        var first = (await File.ReadAllLinesAsync(target)).FirstOrDefault() ?? string.Empty;
        return first.StartsWith(SessionFormat.Prefix, StringComparison.Ordinal)
            ? null
            : $"分支文件没有版本行，首行是：'{first}'";
    }

    /// <summary>把 JSON 文本变成独立拥有的元素。</summary>
    private static System.Text.Json.JsonElement ToElement(string json)
    {
        using var document = System.Text.Json.JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    /// <summary>一次性会话目录。</summary>
    private sealed class TempSessionTree : IDisposable
    {
        public TempSessionTree()
        {
            Root = Path.Combine(
                Path.GetTempPath(), "dougong-sess-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
        }

        public string Root { get; }

        public void Dispose()
        {
            try { Directory.Delete(Root, recursive: true); }
            catch { /* 清理失败不影响结论 */ }
        }
    }
}