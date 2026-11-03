using System.Text.Json;
using Dougong.Agent;

namespace Dougong.Agent.Comparison;

/// <summary>
/// 会话投影与轮次大纲的验收。
/// </summary>
/// <remarks>
/// <b>可观察面是"投影出的状态对不对"与"该不该变"</b>：
/// <list type="bullet">
/// <item>轮次切分正确（按用户输入切，不按模型响应）</item>
/// <item>预览预算（50 字符 / 3 行 / 每行 120）</item>
/// <item><b>无关事件返回同一个引用</b>——这是投影契约里唯一不可让步的一条</item>
/// <item>未结束的轮次单独表示，不与已完成项混在一起</item>
/// </list>
/// </remarks>
public static class SessionProjectionChecks
{
    public static async Task<int> RunAsync()
    {
        var failures = 0;
        failures += await CheckAsync("outline-splits-turns-by-user-input", SplitsByUserInputAsync);
        failures += await CheckAsync("outline-clamps-prompt-and-response", ClampsAsync);
        failures += await CheckAsync("outline-ignores-unrelated-records-same-reference", SameReferenceAsync);
        failures += await CheckAsync("outline-keeps-open-turn-separate-from-committed", OpenTurnAsync);
        failures += await CheckAsync("outline-commits-prior-turn-when-interrupted", InterruptedAsync);
        failures += await CheckAsync("outline-excludes-reasoning-from-preview", NoReasoningAsync);
        failures += await CheckAsync("outline-projects-real-session-file", RealFileAsync);
        failures += await CheckAsync("outline-survives-corrupt-tail", CorruptTailAsync);
        failures += await CheckAsync("outline-flag-is-registered-and-documented", FlagRegisteredAsync);

        Console.WriteLine(failures == 0
            ? "SESSION-PROJECTION: ALL PASS"
            : $"SESSION-PROJECTION: {failures} FAILURE(S)");
        return failures;
    }

    /// <summary>按用户输入切轮，模型的一串工具调用不产生假轮次。</summary>
    private static async Task<string?> SplitsByUserInputAsync()
    {
        await Task.CompletedTask;
        var projection = new TurnOutlineProjection();
        var state = projection.CreateInitial("s");

        // 第 1 轮：用户问 → 模型答（中间夹两次工具调用）→ 轮结束。
        state = projection.Apply(state, Record(1, StoreRecordKind.UserInput, "问一"));
        state = projection.Apply(state, Record(2, StoreRecordKind.ModelResponse, Payload("答一")));
        state = projection.Apply(state, Record(3, StoreRecordKind.ToolCallStarted, "{\"id\":\"c1\"}"));
        state = projection.Apply(state, Record(4, StoreRecordKind.ToolCallCompleted, "{\"id\":\"c1\"}"));
        state = projection.Apply(state, Record(5, StoreRecordKind.RunFinished, "\"Completed\""));

        // 第 2 轮。
        state = projection.Apply(state, Record(6, StoreRecordKind.UserInput, "问二"));
        state = projection.Apply(state, Record(7, StoreRecordKind.ModelResponse, Payload("答二")));
        state = projection.Apply(state, Record(8, StoreRecordKind.RunFinished, "\"Completed\""));

        if (state.Entries.Count != 2)
            return $"应切出 2 轮，实际 {state.Entries.Count}——工具调用被当成轮次了";

        if (state.Entries[0].Prompt != "问一" || state.Entries[1].Prompt != "问二")
            return "用户输入没有对上";

        if (state.Entries[0].Response.Count == 0)
            return "第一轮的回答没有进大纲";

        // 序号要能用来跳转：它是该轮的用户输入记录序号。
        return state.Entries[0].Sequence == 1 && state.Entries[1].Sequence == 6
            ? null
            : $"轮次序号不对：{state.Entries[0].Sequence}, {state.Entries[1].Sequence}";
    }

    /// <summary>预览按预算截断，且省略号计入上限。</summary>
    private static async Task<string?> ClampsAsync()
    {
        await Task.CompletedTask;
        var projection = new TurnOutlineProjection();

        var longPrompt = new string('问', 120);
        var longAnswer = string.Join('\n', Enumerable.Repeat(new string('答', 200), 6));

        var state = projection.CreateInitial("s");
        state = projection.Apply(state, Record(1, StoreRecordKind.UserInput, longPrompt));
        state = projection.Apply(state, Record(2, StoreRecordKind.ModelResponse, Payload(longAnswer)));
        state = projection.Apply(state, Record(3, StoreRecordKind.RunFinished, "\"Completed\""));

        var entry = state.Entries[0];

        if (entry.Prompt.Length > TurnOutlineProjection.PromptMaxChars)
            return $"用户输入预览 {entry.Prompt.Length} 字符，超过上限 {TurnOutlineProjection.PromptMaxChars}";

        if (!entry.Prompt.EndsWith('…'))
            return "截断了却没有省略号";

        if (entry.Response.Count > TurnOutlineProjection.ResponseMaxLines)
            return $"回答 {entry.Response.Count} 行，超过上限 {TurnOutlineProjection.ResponseMaxLines}";

        foreach (var line in entry.Response)
            if (line.Length > TurnOutlineProjection.ResponseLineMaxChars)
                return $"回答行 {line.Length} 字符，超过上限 {TurnOutlineProjection.ResponseLineMaxChars}";

        return null;
    }

    /// <summary>
    /// 无关记录必须返回**同一个引用**。
    /// </summary>
    /// <remarks>
    /// <b>这是投影契约里唯一不可让步的一条</b>：框架靠引用相等判断"没变"，
    /// 从而跳过下游工作。返回一个等值的新对象在语义上没错，但会让每一次
    /// 工具输出都触发一次全量重绘——而那正是这个能力的用途所在。
    /// </remarks>
    private static async Task<string?> SameReferenceAsync()
    {
        await Task.CompletedTask;
        var projection = new TurnOutlineProjection();

        var state = projection.CreateInitial("s");
        state = projection.Apply(state, Record(1, StoreRecordKind.UserInput, "问"));
        state = projection.Apply(state, Record(2, StoreRecordKind.ModelResponse, Payload("答")));

        var before = state;

        // 工具调用与审批记录都不影响大纲。
        foreach (var kind in new[]
                 {
                     StoreRecordKind.ToolCallStarted,
                     StoreRecordKind.ToolCallCompleted,
                     StoreRecordKind.ApprovalDecision,
                 })
        {
            var after = projection.Apply(before, Record(9, kind, "{}"));
            if (!ReferenceEquals(before, after))
                return $"{kind} 让投影产生了新对象——下游会因此重绘";
        }

        return null;
    }

    /// <summary>未结束的轮单独表示，不混进已完成项。</summary>
    /// <remarks>
    /// <b>合并会让宿主把一轮显示成两条</b>：草稿一提交，列表就多一项，
    /// 而用户看到的"已有两轮"其实只发生过一轮。
    /// </remarks>
    private static async Task<string?> OpenTurnAsync()
    {
        await Task.CompletedTask;
        var projection = new TurnOutlineProjection();

        var state = projection.CreateInitial("s");
        state = projection.Apply(state, Record(1, StoreRecordKind.UserInput, "问"));
        state = projection.Apply(state, Record(2, StoreRecordKind.ModelResponse, Payload("答")));

        if (state.Entries.Count != 0)
            return $"未结束的轮被算成了已完成项（{state.Entries.Count} 项）";

        if (state.Draft is null) return "没有草稿——进行中的轮消失了";

        return state.Draft.Prompt == "问" && state.Draft.Turn == 1
            ? null
            : $"草稿内容不对：{state.Draft.Prompt} / 第 {state.Draft.Turn} 轮";
    }

    /// <summary>上一轮没等到结束就被新输入打断时，按空回答收尾而不是丢弃。</summary>
    /// <remarks>
    /// <b>丢弃会让序号与轮次对不上</b>：大纲少一轮，用户按编号跳转就会错位。
    /// </remarks>
    private static async Task<string?> InterruptedAsync()
    {
        await Task.CompletedTask;
        var projection = new TurnOutlineProjection();

        var state = projection.CreateInitial("s");
        state = projection.Apply(state, Record(1, StoreRecordKind.UserInput, "问一"));
        // 没有 RunFinished，直接来第二条用户输入。
        state = projection.Apply(state, Record(5, StoreRecordKind.UserInput, "问二"));

        if (state.Entries.Count != 1)
            return $"被打断的轮应被收尾，实际已完成 {state.Entries.Count} 项";

        if (state.Entries[0].Prompt != "问一")
            return "收尾的不是被打断的那一轮";

        return state.Draft?.Turn == 2 ? null : $"新轮的编号应为 2，实际 {state.Draft?.Turn}";
    }

    /// <summary>推理内容不进预览。</summary>
    /// <remarks>
    /// <b>推理不是回答</b>：里面常带未定稿的自我纠错（"等等，这里算错了"）。
    /// 放进大纲会让用户以为那就是结论。
    /// </remarks>
    private static async Task<string?> NoReasoningAsync()
    {
        await Task.CompletedTask;
        var projection = new TurnOutlineProjection();

        var payload = JsonSerializer.Serialize(new
        {
            text = "正式回答",
            reasoning = "我先想想……这里算错了，重来",
        });

        var state = projection.CreateInitial("s");
        state = projection.Apply(state, Record(1, StoreRecordKind.UserInput, "问"));
        state = projection.Apply(state, Record(2, StoreRecordKind.ModelResponse, payload));
        state = projection.Apply(state, Record(3, StoreRecordKind.RunFinished, "\"Completed\""));

        var text = string.Join(" ", state.Entries[0].Response);
        if (text.Contains("算错了", StringComparison.Ordinal))
            return "推理内容进了大纲——用户会以为那是回答";

        return text.Contains("正式回答", StringComparison.Ordinal) ? null : "正文没有进大纲";
    }

    /// <summary>对真实会话文件跑一次投影。</summary>
    private static async Task<string?> RealFileAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "g24-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "1_session.jsonl");

        try
        {
            var id = Guid.NewGuid().ToString("N");
            await using (var store = await JsonlAgentStore.OpenAsync(path, id))
            {
                await store.CommitAsync(new StoreRecord(
                    0, StoreRecordKind.UserInput, JsonDocument.Parse("\"给我看看迁移\"").RootElement.Clone()));
                await store.CommitAsync(new StoreRecord(
                    0, StoreRecordKind.ModelResponse,
                    JsonDocument.Parse("{\"text\":\"迁移在 migrations/ 下\"}").RootElement.Clone()));
                await store.CommitAsync(new StoreRecord(
                    0, StoreRecordKind.RunFinished, JsonDocument.Parse("\"Completed\"").RootElement.Clone()));
            }

            var state = await SessionProjector.ProjectAsync(new TurnOutlineProjection(), path);

            if (state.Entries.Count != 1) return $"应投影出 1 轮，实际 {state.Entries.Count}";
            if (state.Entries[0].Prompt != "给我看看迁移") return "prompt 没读出来";
            return state.Entries[0].Response.Count > 0 ? null : "回答没读出来";
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    /// <summary>尾部坏行只截断，不丢掉前面的轮次。</summary>
    private static async Task<string?> CorruptTailAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "g24c-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "1_session.jsonl");

        try
        {
            var id = Guid.NewGuid().ToString("N");
            await using (var store = await JsonlAgentStore.OpenAsync(path, id))
            {
                await store.CommitAsync(new StoreRecord(
                    0, StoreRecordKind.UserInput, JsonDocument.Parse("\"问\"").RootElement.Clone()));
                await store.CommitAsync(new StoreRecord(
                    0, StoreRecordKind.RunFinished, JsonDocument.Parse("\"Completed\"").RootElement.Clone()));
            }

            // 模拟写一半崩溃。
            File.AppendAllText(path, "{\"Sequence\":3,\"Kin");

            var state = await SessionProjector.ProjectAsync(new TurnOutlineProjection(), path);

            return state.Entries.Count == 1
                ? null
                : $"尾部坏行让前面的轮次也丢了（{state.Entries.Count} 项）";
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    /// <summary>
    /// <c>--outline</c> 既在参数解析清单里、也出现在 <c>--help</c>。
    /// </summary>
    /// <remarks>
    /// <b>这是 P1 真实踩过的坑</b>：<c>--search</c> 与 <c>--changes</c> 实现完、
    /// 检查全绿，但 <c>--help</c> 里没有——用户只能靠读源码发现。
    /// 参数解析清单与帮助文本是两处，改一处漏一处不报错。
    /// </remarks>
    private static async Task<string?> FlagRegisteredAsync()
    {
        await Task.CompletedTask;
        var root = FindRepoRoot();
        var file = Path.Combine(root, "src", "Dougong.Cli", "Program.cs");
        if (!File.Exists(file)) return $"找不到 {file}";

        var text = File.ReadAllText(file);

        // 带值开关清单里必须有它。
        if (!text.Contains("\"--outline\"", StringComparison.Ordinal))
            return "--outline 不在参数解析清单里——会被判成未知参数";

        // 帮助文本里也要有（同一行里出现开关与说明）。
        var helpLines = text.Split(Environment.NewLine).Where(l => l.Contains("--outline", StringComparison.Ordinal)).ToList();
        return helpLines.Any(l => l.Contains("outline <会话>", StringComparison.Ordinal))
            ? null
            : "--outline 没有出现在 --help 里——用户只能靠读源码发现";
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Dougong.slnx")))
                return directory.FullName;
            directory = directory.Parent;
        }

        return Directory.GetCurrentDirectory();
    }

    // ── 支撑 ─────────────────────────────────────────────────────

    /// <summary>构造一条记录。裸文本按 JSON 字符串处理。</summary>
    /// <remarks>
    /// <b>要区分"已经是 JSON"与"是裸文本"</b>：用户输入的载荷本身就是一个
    /// JSON 字符串（<c>"问一"</c>），而调用处写的是裸的 <c>问一</c>。
    /// 第一版直接 Parse，于是所有用例都抛 JsonReaderException——
    /// 而错误消息（"0xE9 不是合法起始值"）完全没指向真正的原因。
    /// </remarks>
    private static StoreRecord Record(long sequence, StoreRecordKind kind, string payload)
    {
        var json = payload.TrimStart();
        var looksLikeJson = json.StartsWith('"') || json.StartsWith('{') || json.StartsWith('[')
            || json is "true" or "false" or "null"
            || (json.Length > 0 && (char.IsDigit(json[0]) || json[0] == '-'));

        using var document = JsonDocument.Parse(
            looksLikeJson ? payload : JsonSerializer.Serialize(payload));
        return new StoreRecord(sequence, kind, document.RootElement.Clone());
    }

    /// <summary>构造模型响应的载荷（含 text 字段）。</summary>
    private static string Payload(string text)
    {
        var newNode = JsonSerializer.Serialize(new { text });
        return newNode;
    }

    private static async Task<int> CheckAsync(string name, Func<Task<string?>> action)
    {
        try
        {
            var failure = await action().ConfigureAwait(false);
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
