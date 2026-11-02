using System.Text;
using Dougong.Agent;

namespace Dougong.Agent.Comparison;

/// <summary>
/// 会话标题推导的验收。
/// </summary>
/// <remarks>
/// <b>可观察面是"列表里显示的那一行"</b>：
/// <list type="bullet">
/// <item>有用户输入时显示它（而不是时间戳）</item>
/// <item>超过上限时截断并带省略号，总长不超过上限</item>
/// <item>**恰好等于上限的完整输入不加省略号**——那会让用户以为还有下文</item>
/// <item>多行压成一行，不会把表格撑乱</item>
/// <item>没有用户输入时给占位而不是空白</item>
/// </list>
/// </remarks>
public static class SessionTitleChecks
{
    public static async Task<int> RunAsync()
    {
        var failures = 0;
        failures += await CheckAsync("title-uses-first-user-input", FromHistoryAsync);
        failures += await CheckAsync("title-truncates-with-ellipsis", TruncatesAsync);
        failures += await CheckAsync("title-exact-fit-has-no-ellipsis", ExactFitAsync);
        failures += await CheckAsync("title-flattens-multiline", FlattensAsync);
        failures += await CheckAsync("title-empty-input-gives-placeholder", EmptyAsync);
        failures += await CheckAsync("title-never-exceeds-max-chars", NeverExceedsAsync);
        failures += await CheckAsync("title-reads-from-session-file", ReadFromFileAsync);
        failures += await CheckAsync("title-is-derived-not-stored", NotStoredAsync);
        failures += await CheckAsync("title-unreadable-is-distinct-from-empty", UnreadableDistinctAsync);

        Console.WriteLine(failures == 0
            ? "SESSION-TITLE: ALL PASS"
            : $"SESSION-TITLE: {failures} FAILURE(S)");
        return failures;
    }

    /// <summary>取首条用户输入，不是最新一条。</summary>
    /// <remarks>
    /// <b>会话的开头定义了它的主题</b>；后面可能飘到别的话题上。
    /// 而"我为什么开这个会话"与"我记得它关于什么"往往是同一个东西。
    /// </remarks>
    private static Task<string?> FromHistoryAsync()
    {
        var history = new List<AgentMessage>
        {
            new(AgentRole.User, "修复登录超时"),
            new(AgentRole.Assistant, "好的"),
            new(AgentRole.User, "顺便看看部署脚本"),
        };

        var title = SessionTitle.FromHistory(history);
        return Task.FromResult<string?>(
            title == "修复登录超时" ? null : $"取错了条目：{title}");
    }

    /// <summary>超长时截断并加省略号。</summary>
    private static Task<string?> TruncatesAsync()
    {
        var title = SessionTitle.FromUserInput(new string('字', 50));

        if (!title.EndsWith('…')) return Task.FromResult<string?>($"没有省略号：{title}");
        return Task.FromResult<string?>(title.Length <= SessionTitle.MaxChars
            ? null
            : $"超长：{title.Length} > {SessionTitle.MaxChars}");
    }

    /// <summary>恰好等于上限的完整输入不加省略号。</summary>
    /// <remarks>
    /// <b>这是"截断判断"最容易写错的一处</b>：用字符数判上限时，
    /// "恰好写满"与"写满后被截断"无法区分，而它们的显示意图完全相反。
    /// </remarks>
    private static Task<string?> ExactFitAsync()
    {
        var exact = new string('字', SessionTitle.MaxChars);
        var title = SessionTitle.FromUserInput(exact);

        return Task.FromResult<string?>(
            title == exact ? null : $"完整输入被误标截断：{title}（长 {title.Length}）");
    }

    /// <summary>多行输入压成一行。</summary>
    private static Task<string?> FlattensAsync()
    {
        var title = SessionTitle.FromUserInput("第一行\n第二行\r\n第三行");

        if (title.Contains('\n') || title.Contains('\r'))
            return Task.FromResult<string?>($"没有压掉换行：{title}");

        return Task.FromResult<string?>(title.Contains("第一行")
            ? null
            : $"内容丢失：{title}");
    }

    /// <summary>空输入给占位而不是空串。</summary>
    private static Task<string?> EmptyAsync()
    {
        var cases = new (string Label, string? Input)[]
        {
            ("null", null),
            ("空串", ""),
            ("纯空白", "   \n\t "),
        };

        foreach (var (label, input) in cases)
        {
            var title = SessionTitle.FromUserInput(input);
            if (title.Length == 0) return Task.FromResult<string?>($"{label} 得到空串，列表会显示空白");
            if (title != SessionTitle.Empty)
                return Task.FromResult<string?>($"{label} 没有得到占位：{title}");
        }

        return Task.FromResult<string?>(null);
    }

    /// <summary>任何输入下标题都不超过 MaxChars。</summary>
    private static Task<string?> NeverExceedsAsync()
    {
        var inputs = new[]
        {
            new string('a', 200),
            new string('字', 200),
            string.Concat(Enumerable.Repeat("中 a 文  混排 ", 40)),
            new string('字', SessionTitle.MaxChars + 1),
        };

        foreach (var input in inputs)
        {
            var title = SessionTitle.FromUserInput(input);
            if (title.Length > SessionTitle.MaxChars)
                return Task.FromResult<string?>($"标题 {title.Length} 字符超过上限：{title}");
        }

        return Task.FromResult<string?>(null);
    }

    /// <summary>能从会话文件读出来（包括旧格式与损坏尾部）。</summary>
    private static async Task<string?> ReadFromFileAsync()
    {
        using var dir = new TempDir();

        // 正常会话
        dir.Session("ok",
            """{"Sequence":1,"Kind":"UserInput","Payload":"第一条用户输入"}""",
            """{"Sequence":2,"Kind":"RunFinished","Payload":"Completed"}""");

        // 首条是模型响应（会话从工具恢复起的情况），用户输入在后面
        dir.Session("later",
            """{"Sequence":1,"Kind":"ModelResponse","Payload":{"text":"模型先说话","calls":[]}}""",
            """{"Sequence":2,"Kind":"UserInput","Payload":"用户随后输入"}""");

        // 损坏尾部
        dir.Session("broken",
            """{"Sequence":1,"Kind":"UserInput","Payload":"损坏之前的内容"}""",
            """{"not json""");

        var ok = await SessionTitle.ReadAsync(Path.Combine(dir.Root, "ok.jsonl"));
        if (ok != "第一条用户输入") return $"正常会话：{ok}";

        var later = await SessionTitle.ReadAsync(Path.Combine(dir.Root, "later.jsonl"));
        if (later != "用户随后输入") return $"首条非用户记录时取错：{later}";

        var broken = await SessionTitle.ReadAsync(Path.Combine(dir.Root, "broken.jsonl"));
        return broken == "损坏之前的内容" ? null : $"损坏尾部影响读取：{broken}";
    }

    /// <summary>标题不写进会话日志——文件里不该多出记录。</summary>
    /// <remarks>
    /// <b>这条钉的是设计约束</b>：往日志里加 <c>SessionTitle</c> 记录类型
    /// 会让旧程序在读新会话时抛异常（<c>Enum.Parse&lt;StoreRecordKind&gt;</c>
    /// 不认识新成员），而那需要格式迁移，本项目明确不做。
    /// <para>
    /// 检查方式是"读完不算数、文件必须没变"——推导过程不该产生任何写入。
    /// </para>
    /// </remarks>
    private static async Task<string?> NotStoredAsync()
    {
        using var dir = new TempDir();
        dir.Session("s",
            """{"Sequence":1,"Kind":"UserInput","Payload":"不会写回文件"}""");

        var path = Path.Combine(dir.Root, "s.jsonl");
        var before = await File.ReadAllTextAsync(path);
        var beforeTime = File.GetLastWriteTimeUtc(path);

        await SessionTitle.ReadAsync(path);

        var after = await File.ReadAllTextAsync(path);
        if (before != after) return "推导标题时改动了会话文件";
        return File.GetLastWriteTimeUtc(path) == beforeTime
            ? null
            : "推导标题时触碰了文件时间戳——说明有写入";
    }

    /// <summary>
    /// 读不到的会话与"还没说话"的会话给出不同占位。
    /// </summary>
    /// <remarks>
    /// <b>两者对用户的含义相反</b>："无标题"是"这个会话还没说话"（可以忽略），
    /// "读不到"是"这里有个会话但我打不开"（可能正是要找的那个，值得去查权限）。
    /// 合并成一个占位会让用户把"打不开"当成"空会话"跳过——
    /// 而那恰好是最需要看的一类。
    /// </remarks>
    private static async Task<string?> UnreadableDistinctAsync()
    {
        // 用字符串比较而不是引用比较：编译器会把常量相等判成恒假。
        if (string.Equals(SessionTitle.Unreadable, SessionTitle.Empty, StringComparison.Ordinal))
            return "两个占位相同，用户无法区分'空会话'与'打不开'";

        using var dir = new TempDir();

        // 情形一：存在的会话但没有用户输入。
        dir.Session("silent",
            """{"Sequence":1,"Kind":"RunFinished","Payload":"Completed"}""");

        var silent = await SessionTitle.ReadAsync(Path.Combine(dir.Root, "silent.jsonl"));
        if (silent != SessionTitle.Empty)
            return $"没有用户输入时应给「无标题」，实际：{silent}";

        // 情形二：文件不存在。
        var missing = await SessionTitle.ReadAsync(Path.Combine(dir.Root, "nope.jsonl"));
        return missing == SessionTitle.Unreadable
            ? null
            : $"读不到时应给「读不到」，实际：{missing}";
    }

    // ── 支撑 ─────────────────────────────────────────────────────

    private sealed class TempDir : IDisposable
    {
        public TempDir()
        {
            Root = Path.Combine(Path.GetTempPath(), $"dougong-title-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Root);
        }

        public string Root { get; }

        public void Session(string id, params string[] lines)
        {
            var builder = new StringBuilder();
            builder.AppendLine($"{SessionFormat.Prefix}{SessionFormat.Current}");
            foreach (var line in lines) builder.AppendLine(line);
            File.WriteAllText(Path.Combine(Root, id + ".jsonl"), builder.ToString());
        }

        public void Dispose()
        {
            try { Directory.Delete(Root, recursive: true); }
            catch { /* 清理失败不影响结论 */ }
        }
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
