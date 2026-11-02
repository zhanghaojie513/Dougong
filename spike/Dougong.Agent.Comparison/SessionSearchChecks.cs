using System.Text;
using Dougong.Agent;

namespace Dougong.Agent.Comparison;

/// <summary>
/// 会话内容搜索的验收。
/// </summary>
/// <remarks>
/// <b>可观察面是"用户看到哪些命中"</b>，重点在两件事：
/// <list type="number">
/// <item><b>该找到的都能找到</b>：对象载荷、旧格式裸字符串、损坏文件里的有效前缀</item>
/// <item><b>不该找到的不出现</b>：工具参数与工具结果里碰巧含关键词的不算命中</item>
/// </list>
/// 第 2 条比第 1 条更容易写错——工具结果里常有整文件内容，
/// 搜它会让一次"找我们聊过什么"退化成"哪些工具碰过这个字符串"。
/// </remarks>
public static class SessionSearchChecks
{
    public static async Task<int> RunAsync()
    {
        var failures = 0;
        failures += await CheckAsync("search-finds-user-and-model-text", FindsTextAsync);
        failures += await CheckAsync("search-reads-legacy-string-payload", LegacyPayloadAsync);
        failures += await CheckAsync("search-excludes-tool-content", ExcludesToolsAsync);
        failures += await CheckAsync("search-is-case-insensitive", CaseInsensitiveAsync);
        failures += await CheckAsync("search-survives-corrupt-tail", CorruptTailAsync);
        failures += await CheckAsync("search-snippet-contains-match", SnippetHasMatchAsync);
        failures += await CheckAsync("search-respects-limit-and-says-so", LimitAsync);
        failures += await CheckAsync("search-hit-carries-resume-locator", LocatorAsync);

        Console.WriteLine(failures == 0
            ? "SESSION-SEARCH: ALL PASS"
            : $"SESSION-SEARCH: {failures} FAILURE(S)");
        return failures;
    }

    /// <summary>用户输入与模型回复都能被搜到。</summary>
    private static async Task<string?> FindsTextAsync()
    {
        using var dir = new TempDir();
        dir.Session("s1",
            """{"Sequence":1,"Kind":"UserInput","Payload":"我想修登录超时"}""",
            """{"Sequence":2,"Kind":"ModelResponse","Payload":{"text":"先看 auth.cs","calls":[]}}""");

        var result = await SessionSearch.SearchAsync(dir.Root, "登录超时");

        if (result.HitCount != 1) return $"用户输入应有 1 条命中，实际 {result.HitCount}";
        return result.Hits[0].Speaker == "用户" ? null : $"说话方标错：{result.Hits[0].Speaker}";
    }

    /// <summary>旧格式的裸字符串载荷也要能搜到。</summary>
    /// <remarks>
    /// <b>只认 <c>{text}</c> 对象会静默漏掉整类会话</b>，而症状恰好是
    /// "搜索说没找到"——很容易被当成"确实没有"。
    /// </remarks>
    private static async Task<string?> LegacyPayloadAsync()
    {
        using var dir = new TempDir();
        dir.Session("old",
            """{"Sequence":1,"Kind":"UserInput","Payload":"旧格式的用户输入"}""",
            """{"Sequence":2,"Kind":"ModelResponse","Payload":"旧格式的纯文本回复"}""");

        var result = await SessionSearch.SearchAsync(dir.Root, "旧格式");
        return result.HitCount == 2
            ? null
            : $"旧格式应有 2 条命中，实际 {result.HitCount}";
    }

    /// <summary>工具参数与结果不参与搜索。</summary>
    private static async Task<string?> ExcludesToolsAsync()
    {
        using var dir = new TempDir();
        dir.Session("s1",
            """{"Sequence":1,"Kind":"UserInput","Payload":"读一下配置"}""",
            """{"Sequence":2,"Kind":"ToolCallStarted","Payload":{"id":"c1","tool":"read_file","arguments":"{\"path\":\"SECRET_TOKEN_PATH\"}"}}""",
            """{"Sequence":3,"Kind":"ToolCallCompleted","Payload":{"id":"c1","content":"SECRET_TOKEN_VALUE"}}""",
            // **工具载荷也带 `text` 键**：这是最容易漏的一条。
            // 只按"有没有 text 属性"判断的实现会把它当成可搜文本，
            // 而 `edit_file` 这类工具的结果恰好长这样。
            """{"Sequence":4,"Kind":"ToolCallCompleted","Payload":{"id":"c2","text":"SECRET_TOKEN_IN_TEXT_KEYS"}}""");

        var result = await SessionSearch.SearchAsync(dir.Root, "SECRET_TOKEN");

        return result.HitCount == 0
            ? null
            : $"工具参数/结果被搜到了（{result.HitCount} 条）——那会让命中变成噪音";
    }

    /// <summary>大小写不敏感。</summary>
    private static async Task<string?> CaseInsensitiveAsync()
    {
        using var dir = new TempDir();
        dir.Session("s1",
            """{"Sequence":1,"Kind":"UserInput","Payload":"Fix the LoginTimeout bug"}""");

        var lower = await SessionSearch.SearchAsync(dir.Root, "logintimeout");
        var upper = await SessionSearch.SearchAsync(dir.Root, "LOGINTIMEOUT");

        if (lower.HitCount != 1) return $"小写查询没命中（{lower.HitCount}）";
        return upper.HitCount == 1 ? null : $"大写查询没命中（{upper.HitCount}）";
    }

    /// <summary>文件末尾损坏时，坏行之前的内容仍然可搜。</summary>
    /// <remarks>
    /// JSONL 是 append-only，坏行只可能出现在末尾（写一半崩溃）。
    /// 整文件放弃会让"昨天那个会话"搜不到——而它其实是完整的。
    /// </remarks>
    private static async Task<string?> CorruptTailAsync()
    {
        using var dir = new TempDir();
        dir.Session("broken",
            """{"Sequence":1,"Kind":"UserInput","Payload":"这条应该能搜到"}""",
            """{"not valid json""");

        var result = await SessionSearch.SearchAsync(dir.Root, "应该能搜到");
        return result.HitCount == 1
            ? null
            : $"坏行之前的内容没被搜到（命中 {result.HitCount}）";
    }

    /// <summary>片段必须包含匹配词本身。</summary>
    /// <remarks>
    /// <b>只给开头 N 个字符等于没给片段</b>：用户看不出为什么这条被选中。
    /// 长文本 + 匹配词在末尾是最容易暴露这个问题的情形。
    /// </remarks>
    private static async Task<string?> SnippetHasMatchAsync()
    {
        using var dir = new TempDir();
        var filler = new string('前', 400);
        dir.Session("long",
            $$"""{"Sequence":1,"Kind":"UserInput","Payload":"{{filler}}目标关键词"}""");

        var result = await SessionSearch.SearchAsync(dir.Root, "目标关键词");
        if (result.HitCount != 1) return $"没有命中（{result.HitCount}）";

        var snippet = result.Hits[0].Snippet;
        if (!snippet.Contains("目标关键词", StringComparison.Ordinal))
            return $"片段里没有匹配词：{snippet}";

        return snippet.Length <= SessionSearch.SnippetChars + 2
            ? null
            : $"片段太长：{snippet.Length} 字符";
    }

    /// <summary>达到上限时停止并如实报告。</summary>
    private static async Task<string?> LimitAsync()
    {
        using var dir = new TempDir();
        var lines = new List<string>();
        for (var i = 1; i <= 20; i++)
            lines.Add($$"""{"Sequence":{{i}},"Kind":"UserInput","Payload":"重复关键词 #{{i}}"}""");
        dir.Session("many", [.. lines]);

        var result = await SessionSearch.SearchAsync(dir.Root, "重复关键词", limit: 5);

        if (result.HitCount != 5) return $"应限制在 5 条，实际 {result.HitCount}";
        return result.Truncated ? null : "被截断了却没标记 Truncated，用户会以为只有这么多";
    }

    /// <summary>命中里带会话标识与序号，能拼出 <c>--resume</c>。</summary>
    private static async Task<string?> LocatorAsync()
    {
        using var dir = new TempDir();
        dir.Session("20261101-100000",
            """{"Sequence":1,"Kind":"UserInput","Payload":"无关"}""",
            """{"Sequence":2,"Kind":"UserInput","Payload":"目标在这里"}""");

        var result = await SessionSearch.SearchAsync(dir.Root, "目标在这里");
        if (result.HitCount != 1) return $"没有命中（{result.HitCount}）";

        var hit = result.Hits[0];
        if (hit.SessionId != "20261101-100000") return $"会话标识不对：{hit.SessionId}";
        if (hit.Sequence != 2) return $"序号不对：{hit.Sequence}";
        return File.Exists(hit.Path) ? null : $"路径不可用：{hit.Path}";
    }

    // ── 支撑 ─────────────────────────────────────────────────────

    private sealed class TempDir : IDisposable
    {
        public TempDir()
        {
            Root = Path.Combine(Path.GetTempPath(), $"dougong-ssearch-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Root);
        }

        public string Root { get; }

        /// <summary>写一个会话文件；首行是格式声明。</summary>
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
