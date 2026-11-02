using System.Text.Json;
using Dougong.Agent;

namespace Dougong.Agent.Comparison;

/// <summary>
/// 向用户提问（<c>ask_user</c>）的验收。
/// </summary>
/// <remarks>
/// <b>可观察面是"模型收到的工具结果"</b>，尤其重要的是
/// <b>四种结局不能混</b>：
/// <list type="bullet">
/// <item>答了 → <c>Answered</c>，带答案</item>
/// <item>明确跳过 → <c>Declined</c></item>
/// <item>本轮取消 / 输入结束 → <c>Cancelled</c> / <c>Unavailable</c></item>
/// <item>问题形状不合法 → <c>InvalidRequest</c>，**且没有打扰用户**</item>
/// </list>
/// 把后三种当成"用户拒绝"会让模型改变方案，而用户可能根本没看到问题。
/// </remarks>
public static class AskUserChecks
{
    public static Task<int> RunAsync()
    {
        var failures = 0;
        failures += Check("ask-accepts-by-index", AcceptsIndex);
        failures += Check("ask-accepts-by-label-text", AcceptsLabel);
        failures += Check("ask-declines-on-skip", Declines);
        failures += Check("ask-eof-is-unavailable-not-declined", EofIsUnavailable);
        failures += Check("ask-cancel-is-distinct-from-decline", CancelIsDistinct);
        failures += Check("ask-invalid-shape-does-not-ask-user", InvalidDoesNotAsk);
        failures += Check("ask-rejects-duplicate-headers", RejectsDuplicateHeaders);
        failures += Check("ask-multi-select-takes-several", MultiSelect);
        failures += Check("ask-validation-shared-with-acp", SharedValidation);
        failures += Check("ask-schema-uses-header-as-key", SchemaUsesHeader);
        failures += Check("ask-partial-answers-survive-eof", PartialSurvivesEof);

        Console.WriteLine(failures == 0
            ? "ASK-USER: ALL PASS"
            : $"ASK-USER: {failures} FAILURE(S)");
        return Task.FromResult(failures);
    }

    private const string OneQuestion =
        """{"questions":[{"question":"用哪个数据库？","header":"数据库","options":[{"label":"SQLite","description":"单文件"},{"label":"Postgres","description":"需要服务"}]}]}""";

    private static string? AcceptsIndex() =>
        Run(OneQuestion, "2") switch
        {
            { Outcome: AskOutcome.Answered } r when r.Answers[0].Answer == "Postgres" => null,
            { } r => $"序号没有解析成答案：{r.Outcome} / {r.Diagnostic}",
        };

    private static string? AcceptsLabel()
    {
        // 用户常直接粘贴选项原文，这比记序号自然。
        var result = Run(OneQuestion, "Postgres");
        return result is { Outcome: AskOutcome.Answered } && result.Answers[0].Answer == "Postgres"
            ? null
            : $"选项原文没有解析成答案：{result.Outcome}";
    }

    private static string? Declines()
    {
        var result = Run(OneQuestion, "s");
        return result.Outcome is AskOutcome.Declined
            ? null
            : $"跳过被报成了 {result.Outcome}";
    }

    /// <summary>输入结束必须是"不可用"，不是"用户拒绝"。</summary>
    /// <remarks>
    /// <b>这条钉的是最容易混淆的一处</b>：EOF 意味着用户可能压根没看到问题
    /// （管道、重定向、进程被关）。报成 Declined 会让模型以为用户否决了方案。
    /// </remarks>
    private static string? EofIsUnavailable()
    {
        var result = Run(OneQuestion);
        return result.Outcome is AskOutcome.Unavailable
            ? null
            : $"EOF 被报成了 {result.Outcome}，应为 Unavailable";
    }

    /// <summary>取消与拒绝必须分开。</summary>
    private static string? CancelIsDistinct()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var queue = new Queue<string?>(["1"]);
        var tool = new Dougong.Cli.ConsoleElicitationTool(
            () => queue.Count > 0 ? queue.Dequeue() : null);

        var result = tool.ExecuteAsync(
            new ToolInvocation("c1", "ask_user", Parse(OneQuestion)), cts.Token)
            .AsTask().GetAwaiter().GetResult();

        var outcome = ReadOutcome(result);
        return outcome is AskOutcome.Cancelled
            ? null
            : $"取消被报成了 {outcome}，应为 Cancelled";
    }

    /// <summary>形状不合法时一次都不该读输入。</summary>
    /// <remarks>
    /// <b>这是"不打扰用户"的硬验证</b>：不是看结果对不对，而是看
    /// 读取器有没有被调用过。读了就说明问题已经被展示出来了。
    /// </remarks>
    private static string? InvalidDoesNotAsk()
    {
        var asked = false;
        var tool = new Dougong.Cli.ConsoleElicitationTool(() => { asked = true; return "1"; });

        var result = tool.ExecuteAsync(
            new ToolInvocation("c1", "ask_user",
                Parse("""{"questions":[{"question":"q","header":"h","options":[{"label":"a","description":"x"}]}]}""")),
            CancellationToken.None).AsTask().GetAwaiter().GetResult();

        var outcome = ReadOutcome(result);
        if (outcome is not AskOutcome.InvalidRequest)
            return $"只给 1 个选项应判非法，实际 {outcome}";
        return asked ? "形状不合法却仍然打扰了用户" : null;
    }

    /// <summary>header 重复必须拒绝。</summary>
    /// <remarks>
    /// header 是答案的键。重复会让两个答案互相覆盖，而模型只看到"少了一个"，
    /// 无法判断是自己没问还是被覆盖了。
    /// </remarks>
    private static string? RejectsDuplicateHeaders()
    {
        const string two =
            "{\"questions\":["
            + "{\"question\":\"q1\",\"header\":\"同一\","
            + "\"options\":[{\"label\":\"a\",\"description\":\"x\"},"
            + "{\"label\":\"b\",\"description\":\"y\"}]},"
            + "{\"question\":\"q2\",\"header\":\"同一\","
            + "\"options\":[{\"label\":\"c\",\"description\":\"x\"},"
            + "{\"label\":\"d\",\"description\":\"y\"}]}]}";

        var error = AskUser.Validate(Parse(two), out _);
        return error is not null && error.Contains("重复", StringComparison.Ordinal)
            ? null
            : $"重复 header 没有被拒：{error ?? "<通过了>"}";
    }

    /// <summary>多选接受逗号分隔的多个序号。</summary>
    private static string? MultiSelect()
    {
        const string multi =
            """{"questions":[{"question":"要哪些？","header":"多","multiSelect":true,"options":[{"label":"A","description":"x"},{"label":"B","description":"y"},{"label":"C","description":"z"}]}]}""";

        var result = Run(multi, "1,3");
        return result is { Outcome: AskOutcome.Answered }
            && result.Answers[0].Answer == "A, C"
                ? null
                : $"多选没解析：{result.Outcome} / {result.Answers.FirstOrDefault()?.Answer}";
    }

    /// <summary>ACP 与 REPL 用的是同一套校验。</summary>
    /// <remarks>
    /// <b>这条钉的是"语义层没有各写一份"</b>：两份校验必然漂移，
    /// 而症状是"同一个问题在 ACP 里合法、在 REPL 里非法"，模型无法理解。
    /// </remarks>
    private static string? SharedValidation()
    {
        // 一个已知非法的形状：两个选项 label 相同。
        const string bad =
            """{"questions":[{"question":"q","header":"h","options":[{"label":"同","description":"x"},{"label":"同","description":"y"}]}]}""";

        var error = AskUser.Validate(Parse(bad), out _);
        if (error is null) return "重复 label 没有被拒";

        // ACP 的实现也必须走同一判据——它调的就是 AskUser.Validate。
        // 这里通过反射确认 AcpElicitationTool 没有自己的 Validate。
        var acpType = Type.GetType("Dougong.Acp.AcpElicitationTool, Dougong.Acp");
        if (acpType is null) return "找不到 AcpElicitationTool（Acp 程序集未引用）";

        var own = acpType.GetMethod("Validate",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        return own is null ? null : "ACP 仍保留自己的 Validate，校验会漂移";
    }

    /// <summary>schema 的属性名用 header。</summary>
    private static string? SchemaUsesHeader()
    {
        AskUser.Validate(Parse(OneQuestion), out var questions);
        var schema = JsonSerializer.Serialize(AskUser.BuildSchema(questions));

        // **反序列化后检查键名，不做字符串包含**：JsonSerializer 默认把非 ASCII
        // 转义成 \uXXXX，直接找 "数据库" 永远失败——那是检查写错了，不是实现错了。
        using var document = JsonDocument.Parse(schema);
        var properties = document.RootElement.GetProperty("properties");

        if (!properties.TryGetProperty("数据库", out var property))
        {
            return "schema 的属性名不是 header："
                + string.Join(",", properties.EnumerateObject().Select(p => p.Name));
        }

        // 选项说明也要在，供宿主展示为副标题。
        return property.TryGetProperty("description", out _)
            ? null
            : "schema 缺少选项说明";
    }

    /// <summary>
    /// 多问题中途输入结束时，已答的答案要保留且说清。
    /// </summary>
    /// <remarks>
    /// <b>丢掉已答部分会让模型全部重问</b>，而用户刚才已经答过的那部分白费了。
    /// 诊断里还要写明已答了几个——只报"输入已结束"会让模型以为一个都没拿到。
    /// </remarks>
    private static string? PartialSurvivesEof()
    {
        const string three =
            "{\"questions\":["
            + "{\"question\":\"q1\",\"header\":\"一\","
            + "\"options\":[{\"label\":\"A\",\"description\":\"a\"},{\"label\":\"B\",\"description\":\"b\"}]},"
            + "{\"question\":\"q2\",\"header\":\"二\","
            + "\"options\":[{\"label\":\"C\",\"description\":\"c\"},{\"label\":\"D\",\"description\":\"d\"}]}]}";

        // 只喂一个答案，第二个问题读到 EOF。
        var result = Run(three, "1");

        if (result.Outcome is not AskOutcome.Unavailable)
            return $"中途 EOF 应报 Unavailable，实际 {result.Outcome}";

        if (result.Answers.Count != 1)
            return $"已答的答案丢了（{result.Answers.Count} 条），模型会全部重问";

        if (result.Answers[0].Answer != "A")
            return $"保留的答案不对：{result.Answers[0].Answer}";

        // 诊断要说清已答数量，否则模型以为一个都没拿到。
        return result.Diagnostic?.Contains("已答", StringComparison.Ordinal) == true
            ? null
            : $"诊断没有说明已答几个：{result.Diagnostic}";
    }

    // ── 支撑 ─────────────────────────────────────────────────────

    private static AskResult Run(string json, params string?[] inputs)
    {
        var queue = new Queue<string?>(inputs);
        var tool = new Dougong.Cli.ConsoleElicitationTool(
            () => queue.Count > 0 ? queue.Dequeue() : null);

        var result = tool.ExecuteAsync(
            new ToolInvocation("c1", "ask_user", Parse(json)), CancellationToken.None)
            .AsTask().GetAwaiter().GetResult();

        return ParseResult(result);
    }

    private static AskOutcome ReadOutcome(AgentToolResult result) => ParseResult(result).Outcome;

    private static AskResult ParseResult(AgentToolResult result)
    {
        var root = result.Content;

        var outcome = root.TryGetProperty("outcome", out var o)
            ? Enum.Parse<AskOutcome>(o.GetString()!)
            : AskOutcome.Unavailable;

        var diagnostic = root.TryGetProperty("diagnostic", out var d) && d.ValueKind is JsonValueKind.String
            ? d.GetString() : null;

        var answers = new List<AskAnswer>();
        if (root.TryGetProperty("answers", out var a) && a.ValueKind is JsonValueKind.Array)
        {
            foreach (var item in a.EnumerateArray())
            {
                answers.Add(new AskAnswer(
                    item.GetProperty("question").GetString()!,
                    item.GetProperty("answer").GetString()!));
            }
        }

        return new AskResult(outcome, diagnostic, answers);
    }

    private static JsonElement Parse(string json) =>
        JsonDocument.Parse(json).RootElement.Clone();

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
}
