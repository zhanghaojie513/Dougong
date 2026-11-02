using Dougong.Agent;

namespace Dougong.Agent.Comparison;

/// <summary>
/// 文件引用补全的验收。
/// </summary>
/// <remarks>
/// <b>可观察面是"输入框收到什么"</b>：
/// <list type="bullet">
/// <item><b>只返回相对路径，不读内容</b>——这是 dsh 契约的原话</item>
/// <item><b>排序稳定</b>：同一查询两次结果必须一样，否则用户按 Tab 时候选在跳</item>
/// <item><b>不越出工作区</b>、<b>不进噪声目录</b></item>
/// <item>命中多时如实报告总数与截断</item>
/// </list>
/// </remarks>
public static class FileReferenceChecks
{
    public static async Task<int> RunAsync()
    {
        var failures = 0;
        failures += await CheckAsync("refs-returns-relative-slash-paths", RelativePathsAsync);
        failures += await CheckAsync("refs-match-is-substring-and-case-insensitive", MatchesAsync);
        failures += await CheckAsync("refs-order-is-stable", StableOrderAsync);
        failures += await CheckAsync("refs-prefers-shallow-paths", ShallowFirstAsync);
        failures += await CheckAsync("refs-skips-noise-directories", SkipsNoiseAsync);
        failures += await CheckAsync("refs-reports-total-and-truncation", ReportsTotalsAsync);
        failures += await CheckAsync("refs-does-not-read-file-contents", NoReadAsync);
        failures += await CheckAsync("refs-empty-query-lists-all", EmptyQueryAsync);

        Console.WriteLine(failures == 0
            ? "FILE-REFERENCES: ALL PASS"
            : $"FILE-REFERENCES: {failures} FAILURE(S)");
        return failures;
    }

    /// <summary>返回相对工作区、正斜杠的路径。</summary>
    /// <remarks>
    /// <b>不能返回绝对路径</b>：补全结果会进输入框，而绝对路径会把用户名
    /// 带进对话。dsh 的契约也写的是 "within an Agent cwd"。
    /// </remarks>
    private static async Task<string?> RelativePathsAsync()
    {
        using var env = new Env();
        env.Write("src/Program.cs", "");

        var result = await env.SearchAsync("Program");

        var path = result.Paths.FirstOrDefault();
        if (path is null) return "没有候选";

        if (Path.IsPathRooted(path)) return $"返回了绝对路径：{path}";
        if (path.Contains('\\')) return $"返回了反斜杠路径：{path}";

        return path == "src/Program.cs" ? null : $"路径形态不对：{path}";
    }

    private static async Task<string?> MatchesAsync()
    {
        using var env = new Env();
        env.Write("README.md", "");

        // 小写查询要命中大写文件名。
        var lower = await env.SearchAsync("readme");
        if (lower.Paths.Count == 0) return "小写查询没命中";

        // **包含匹配而非前缀**：用户常记得中间一段。
        env.Write("LoopAgentRuntime.cs", "");
        var middle = await env.SearchAsync("Agent");
        return middle.Paths.Any(p => p.Contains("LoopAgentRuntime", StringComparison.Ordinal))
            ? null
            : $"中间片段没命中：{string.Join(",", middle.Paths)}";
    }

    /// <summary>排序必须确定：按深度、长度、字典序。</summary>
    /// <remarks>
    /// <b>只验"两次结果相同"是不够的</b>——负控证据：去掉 <c>Sort</c> 后
    /// 那条检查仍然通过，因为这台机器上 <c>ListDirectoryAsync</c> 的顺序
    /// 恰好稳定。那样的检查在别的文件系统上会失效。
    /// <para>
    /// 改成直接验排序**规则**：给出必然乱序的输入，断言输出有序。
    /// </para>
    /// </remarks>
    private static async Task<string?> StableOrderAsync()
    {
        // **用必然乱序的文件系统**：真实文件系统的枚举顺序恰好有序，
        // 去掉 Sort 的负控也过不了。这里注入一个按倒序返回的装饰器，
        // 于是"不排序"必然失败。
        var root = Path.Combine(Path.GetTempPath(), $"dougong-fref-order-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            foreach (var name in new[] { "aa.cs", "zz.cs", "src/bb.cs" })
            {
                var full = Path.Combine(root, name.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                File.WriteAllText(full, "");
            }

            var index = new FileReferenceIndex(new ReverseListing(new LocalFileSystem(root)), root);
            var paths = (await index.SearchAsync("")).Paths;
        if (paths.Count < 3) return $"候选不足：{string.Join(",", paths)}";

        // 断言一：深度单调不减。
        var depths = paths.Select(p => p.Count(c => c == '/')).ToArray();
        for (var i = 1; i < depths.Length; i++)
        {
            if (depths[i] < depths[i - 1])
                return $"深度非单调：{string.Join(" | ", paths)}";
        }

        // 断言二：同深度内按长度、再按字典序。
        for (var i = 1; i < paths.Count; i++)
        {
            if (depths[i] != depths[i - 1]) continue;

            var a = paths[i - 1];
            var b = paths[i];
            if (a.Length > b.Length || (a.Length == b.Length && string.CompareOrdinal(a, b) > 0))
                return $"同深度内未按长度/字典序：{a} 在 {b} 之前";
        }

            return null;
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); }
            catch { /* 清理失败不影响结论 */ }
        }
    }

    private static async Task<string?> ShallowFirstAsync()
    {
        using var env = new Env();
        env.Write("Program.cs", "");
        env.Write("src/Program.cs", "");
        env.Write("src/deep/Program.cs", "");

        var paths = (await env.SearchAsync("Program")).Paths;

        if (paths.Count < 3) return $"候选不足：{string.Join(",", paths)}";

        // 浅层应排在前面：用户更可能想引用根目录那个。
        return paths[0] == "Program.cs"
            ? null
            : $"浅层文件没排第一：{string.Join(" | ", paths)}";
    }

    private static async Task<string?> SkipsNoiseAsync()
    {
        using var env = new Env();
        env.Write("src/real.cs", "");
        env.Write("node_modules/pkg/index.js", "");
        env.Write("bin/x.dll", "");

        var result = await env.SearchAsync("");

        var noise = result.Paths
            .Where(p => p.Contains("node_modules", StringComparison.Ordinal)
                || p.StartsWith("bin/", StringComparison.Ordinal))
            .ToArray();

        return noise.Length == 0
            ? null
            : $"候选里出现噪声目录：{string.Join(",", noise)}";
    }

    /// <summary>命中多时如实报告总数与截断。</summary>
    /// <remarks>
    /// <b>只报"返回了几条"不够</b>：UI 要能显示"还有 12 个"。
    /// 而那需要知道总数，不是只看返回条数。
    /// </remarks>
    private static async Task<string?> ReportsTotalsAsync()
    {
        using var env = new Env(maxResults: 2);
        foreach (var i in Enumerable.Range(1, 6)) env.Write($"f{i}.cs", "");

        var result = await env.SearchAsync("");

        if (result.Paths.Count != 2) return $"应返回 2 条，实际 {result.Paths.Count}";
        if (result.TotalMatches != 6) return $"总数应为 6，实际 {result.TotalMatches}";
        return result.Truncated ? null : "截断了却没标记";
    }

    /// <summary>不读文件内容。</summary>
    /// <remarks>
    /// <b>这是 dsh 契约的明确要求</b>（"without reading file contents"）。
    /// 读内容会让一次按键触发几十次磁盘 IO，而补全不需要知道内容。
    /// <para>
    /// 验证方式是给文件塞一个"读到就会爆炸"的实现——用一个会在
    /// <c>ReadTextAsync</c> 里抛异常的 <see cref="IFileSystem"/> 装饰器。
    /// </para>
    /// </remarks>
    private static async Task<string?> NoReadAsync()
    {
        using var env = new Env();
        env.Write("a.cs", "content");

        var spy = new ReadGuard(new LocalFileSystem(env.Root));
        var index = new FileReferenceIndex(spy, env.Root);

        var result = await index.SearchAsync("a");

        if (spy.ReadAttempted)
            return "补全读取了文件内容——契约要求只返回路径";

        return result.Paths.Count > 0 ? null : "没有候选";
    }

    private static async Task<string?> EmptyQueryAsync()
    {
        using var env = new Env();
        env.Write("a.cs", "");
        env.Write("b.cs", "");

        var all = await env.SearchAsync("");
        return all.Paths.Count == 2 ? null : $"空查询应列出全部，实际 {all.Paths.Count} 条";
    }

    // ── 支撑 ─────────────────────────────────────────────────────

    /// <summary>按倒序返回目录项的装饰器，用于逼出排序逻辑。</summary>
    private sealed class ReverseListing(IFileSystem inner) : IFileSystem
    {
        public ValueTask<string> ReadTextAsync(
            string path, CancellationToken cancellationToken = default) =>
            inner.ReadTextAsync(path, cancellationToken);

        public ValueTask<FileStat?> StatAsync(
            string path, CancellationToken cancellationToken = default) =>
            inner.StatAsync(path, cancellationToken);

        public async ValueTask<IReadOnlyList<FileEntry>> ListDirectoryAsync(
            string path, CancellationToken cancellationToken = default)
        {
            var entries = await inner.ListDirectoryAsync(path, cancellationToken)
                .ConfigureAwait(false);

            return [.. entries.OrderByDescending(e => e.Name, StringComparer.Ordinal)];
        }

        public ValueTask<FileWriteOutcome> WriteTextAsync(
            string path, string content, FileWriteExpectation? expected = null,
            CancellationToken cancellationToken = default) =>
            inner.WriteTextAsync(path, content, expected, cancellationToken);
    }

    /// <summary>记录是否被要求读内容的装饰器。</summary>
    private sealed class ReadGuard(IFileSystem inner) : IFileSystem
    {
        public bool ReadAttempted { get; private set; }

        public ValueTask<string> ReadTextAsync(
            string path, CancellationToken cancellationToken = default)
        {
            ReadAttempted = true;
            return inner.ReadTextAsync(path, cancellationToken);
        }

        public ValueTask<FileStat?> StatAsync(
            string path, CancellationToken cancellationToken = default) =>
            inner.StatAsync(path, cancellationToken);

        public ValueTask<IReadOnlyList<FileEntry>> ListDirectoryAsync(
            string path, CancellationToken cancellationToken = default) =>
            inner.ListDirectoryAsync(path, cancellationToken);

        public ValueTask<FileWriteOutcome> WriteTextAsync(
            string path, string content, FileWriteExpectation? expected = null,
            CancellationToken cancellationToken = default) =>
            inner.WriteTextAsync(path, content, expected, cancellationToken);
    }

    private sealed class Env : IDisposable
    {
        public Env(int maxResults = FileReferenceIndex.DefaultMaxResults)
        {
            Root = Path.Combine(Path.GetTempPath(), $"dougong-fref-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Root);
            MaxResults = maxResults;
        }

        public string Root { get; }
        private int MaxResults { get; }

        public void Write(string relative, string content)
        {
            var full = Path.Combine(Root, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, content);
        }

        public ValueTask<FileReferenceResult> SearchAsync(string query) =>
            new FileReferenceIndex(new LocalFileSystem(Root), Root, MaxResults)
                .SearchAsync(query);

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
