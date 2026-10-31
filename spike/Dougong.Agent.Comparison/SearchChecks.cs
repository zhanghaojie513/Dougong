using System.Text.Json;
using Dougong.Agent;

namespace Dougong.Agent.Comparison;

/// <summary>
/// 搜索与按内容替换的验证。
/// </summary>
/// <remarks>
/// <b>重点不是"能搜到"，是"该拒绝时拒绝"</b>：
/// <list type="bullet">
/// <item>glob 要限深、要跳过噪声目录、要被截断时如实报告</item>
/// <item>grep 的非法正则要报错而不是当"没有匹配"</item>
/// <item>edit 的 old_string 出现多次必须拒绝——猜一处就等于静默改错</item>
/// <item>edit 必须走版本守卫，防止冲掉期间的外部改动</item>
/// </list>
/// </remarks>
public static class SearchChecks
{
    public static async Task<int> RunAsync()
    {
        var failures = 0;
        failures += await CheckAsync("glob-finds-by-name", GlobFindsAsync);
        failures += await CheckAsync("glob-skips-noise-directories", GlobSkipsNoiseAsync);
        failures += await CheckAsync("glob-results-are-sorted", GlobSortedAsync);
        failures += await CheckAsync("grep-finds-content-with-line-number", GrepFindsAsync);
        failures += await CheckAsync("grep-rejects-invalid-regex", GrepRejectsBadRegexAsync);
        failures += await CheckAsync("edit-replaces-unique-match", EditUniqueAsync);
        failures += await CheckAsync("edit-rejects-multiple-matches", EditRejectsAmbiguousAsync);
        failures += await CheckAsync("edit-rejects-missing-text", EditRejectsMissingAsync);
        failures += await CheckAsync("edit-guards-against-concurrent-write", EditGuardsVersionAsync);

        // 参数缺失时的错误形态必须一致：同一类问题不能一个抛英文异常、
        // 一个返回中文失败结果——那模型学不会"参数错了会怎样"。
        failures += await CheckAsync("missing-arg-message-is-uniform", MissingArgUniformAsync);

        // 超大文件必须在**读取之前**被跳过，不能读完再判。
        // 读完整文件再检查上限，内存已经吃掉了——大日志上就是 OOM。
        failures += await CheckAsync("grep-skips-large-file-without-reading-it", GrepSkipsLargeAsync);

        // 重叠匹配的语义必须与报数一致：拒绝理由不能自相矛盾。
        failures += await CheckAsync("overlapping-match-count-is-consistent", OverlapConsistentAsync);

        Console.WriteLine(failures == 0
            ? "SEARCH: ALL PASS"
            : $"SEARCH: {failures} FAILURE(S)");
        return failures;
    }

    /// <summary>按文件名模式找到文件。</summary>
    private static async Task<string?> GlobFindsAsync()
    {
        using var dir = new TempTree();
        dir.Write("a.cs", "// a");
        dir.Write("b.md", "# b");
        dir.Write("sub/c.cs", "// c");

        var tool = new GlobTool(new LocalFileSystem(dir.Root));
        var result = await RunAsync(tool, """{"pattern":"*.cs"}""");

        var json = result.Content.GetRawText();
        if (!json.Contains("a.cs", StringComparison.Ordinal)) return $"没找到 a.cs：{json}";
        if (!json.Contains("sub/c.cs", StringComparison.Ordinal)) return $"没递归找 sub/c.cs：{json}";
        if (json.Contains("b.md", StringComparison.Ordinal)) return $"*.cs 匹配到了 .md：{json}";

        return null;
    }

    /// <summary>
    /// 默认不进入 node_modules 这类目录。
    /// </summary>
    /// <remarks>
    /// 这不是"禁止"，是"默认不进去"——它们让搜索慢几十倍，而几乎从不是目标。
    /// </remarks>
    private static async Task<string?> GlobSkipsNoiseAsync()
    {
        using var dir = new TempTree();
        dir.Write("src/app.cs", "// app");
        dir.Write("node_modules/pkg/index.js", "// noise");

        var tool = new GlobTool(new LocalFileSystem(dir.Root));
        var result = await RunAsync(tool, """{"pattern":"*"}""");

        var json = result.Content.GetRawText();
        if (json.Contains("node_modules", StringComparison.Ordinal))
            return $"默认进了 node_modules：{json}";

        return null;
    }

    /// <summary>结果必须排序——否则同一查询两次顺序不同，模型会以为环境变了。</summary>
    private static async Task<string?> GlobSortedAsync()
    {
        using var dir = new TempTree();
        foreach (var n in new[] { "c.txt", "a.txt", "b.txt" })
            dir.Write(n, "x");

        var tool = new GlobTool(new LocalFileSystem(dir.Root));
        var result = await RunAsync(tool, """{"pattern":"*.txt"}""");

        var text = result.Content.GetRawText();
        var ia = text.IndexOf("a.txt", StringComparison.Ordinal);
        var ib = text.IndexOf("b.txt", StringComparison.Ordinal);
        var ic = text.IndexOf("c.txt", StringComparison.Ordinal);

        return ia < ib && ib < ic ? null : $"结果没有按名称排序：{text}";
    }

    /// <summary>按内容搜索，返回行号。</summary>
    private static async Task<string?> GrepFindsAsync()
    {
        using var dir = new TempTree();
        dir.Write("code.cs", "line one\nvar target = 1;\nline three");

        var tool = new GrepTool(new LocalFileSystem(dir.Root));
        var result = await RunAsync(tool, """{"pattern":"target"}""");

        var json = result.Content.GetRawText();
        if (!json.Contains("code.cs", StringComparison.Ordinal)) return $"没找到匹配：{json}";
        if (!json.Contains("\"line\":2", StringComparison.Ordinal))
            return $"行号不对（应为 2）：{json}";

        return null;
    }

    /// <summary>
    /// 非法正则必须报错，不能当成"没有匹配"。
    /// </summary>
    /// <remarks>
    /// 当成"没有匹配"最坏：模型会以为内容不存在，然后换个名字再搜一次，
    /// 白跑一轮，而且永远找不到真正原因。
    /// </remarks>
    private static async Task<string?> GrepRejectsBadRegexAsync()
    {
        using var dir = new TempTree();
        dir.Write("a.txt", "x");

        var tool = new GrepTool(new LocalFileSystem(dir.Root));

        try
        {
            await RunAsync(tool, """{"pattern":"[unclosed"}""");
            return "非法正则没有报错";
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("正则不合法", StringComparison.Ordinal))
        {
            return null;
        }
    }

    /// <summary>唯一匹配时正常替换。</summary>
    private static async Task<string?> EditUniqueAsync()
    {
        using var dir = new TempTree();
        dir.Write("a.txt", "alpha\nbeta\ngamma");

        var files = new LocalFileSystem(dir.Root);
        var tool = new EditFileTool(files);
        var result = await RunAsync(tool,
            """{"path":"a.txt","old_string":"beta","new_string":"BETA"}""");

        if (result.Content.GetRawText().Contains("\"error\"", StringComparison.Ordinal))
            return $"唯一匹配却被拒：{result.Content.GetRawText()}";

        var after = await files.ReadTextAsync("a.txt");
        return after == "alpha\nBETA\ngamma" ? null : $"替换结果不对：{after}";
    }

    /// <summary>
    /// old_string 出现多次必须拒绝。
    /// </summary>
    /// <remarks>
    /// <b>这是本套最重要的一条</b>：猜一处替换会一次改坏多处，
    /// 而模型看不出自己改了什么——它以为只改了一处。想改多处就分多次调用，
    /// 每次都看到实际结果。
    /// </remarks>
    private static async Task<string?> EditRejectsAmbiguousAsync()
    {
        using var dir = new TempTree();
        dir.Write("a.txt", "dup\ndup\ndup");

        var files = new LocalFileSystem(dir.Root);
        var tool = new EditFileTool(files);
        var result = await RunAsync(tool,
            """{"path":"a.txt","old_string":"dup","new_string":"X"}""");

        var json = result.Content.GetRawText();
        if (!json.Contains("\"error\":\"ambiguous\"", StringComparison.Ordinal))
            return $"重复匹配没有被拒：{json}";

        // 文件必须**一个字节都没变**。
        var after = await files.ReadTextAsync("a.txt");
        return after == "dup\ndup\ndup" ? null : $"被拒绝但文件被改动了：{after}";
    }

    /// <summary>找不到原文时拒绝，并提示先读文件。</summary>
    private static async Task<string?> EditRejectsMissingAsync()
    {
        using var dir = new TempTree();
        dir.Write("a.txt", "hello");

        var files = new LocalFileSystem(dir.Root);
        var tool = new EditFileTool(files);
        var result = await RunAsync(tool,
            """{"path":"a.txt","old_string":"nothere","new_string":"X"}""");

        var json = result.Content.GetRawText();
        if (!json.Contains("\"error\":\"not_found_in_file\"", StringComparison.Ordinal))
            return $"找不到原文时没有拒绝：{json}";

        // 提示里要说"先用 read_file 确认原文"——模型最常见的失败原因
        // 是缩进或换行对不上，而它不知道要检查这个。
        return json.Contains("read_file", StringComparison.Ordinal)
            ? null
            : $"拒绝理由没说清该怎么办：{json}";
    }

    /// <summary>
    /// 读之后文件被改过时，写回必须失败。
    /// </summary>
    /// <remarks>
    /// 防的是"模型读到第 100 行，期间用户手工改了文件，写回时把用户的改动冲掉"
    /// ——那种丢数据没有任何提示。
    /// </remarks>
    private static async Task<string?> EditGuardsVersionAsync()
    {
        using var dir = new TempTree();
        dir.Write("a.txt", "original");

        var files = new LocalFileSystem(dir.Root);
        var tool = new EditFileTool(files);

        // 先取版本，制造"读到之后被改"的场景：把版本换成旧的。
        var stat = await files.StatAsync("a.txt");
        if (stat is null) return "StatAsync 返回 null";
        var staleVersion = stat.Version;

        await files.WriteTextAsync("a.txt", "someone else changed it");

        // 用过期版本写：必须失败。这里直接测 seam 的守卫，
        // 因为 EditFileTool 内部每次都重新取版本，构造不出这个竞态。
        try
        {
            await files.WriteTextAsync(
                "a.txt", "edited",
                new FileWriteExpectation.MustMatchVersion(staleVersion));
            return "过期版本写入成功了——用户的改动会被静默冲掉";
        }
        catch (FileVersionConflictException)
        {
            var after = await files.ReadTextAsync("a.txt");
            return after == "someone else changed it"
                ? null
                : $"冲突后文件被改动了：{after}";
        }
    }

    /// <summary>
    /// 缺必需参数时，四个文件工具必须给出同一形态的错误。
    /// </summary>
    /// <remarks>
    /// <b>这条钉的是"跨语言移植会把英文异常漏出来"</b>：原先工具里写
    /// <c>GetProperty("x").GetString()</c>，缺字段时抛
    /// <see cref="KeyNotFoundException"/>，消息是 BCL 的英文
    /// <c>The given key was not present in the dictionary.</c>。
    /// 模型看到这句话既不知道缺哪个参数，也读不出该补什么。
    /// <para>
    /// <b>更麻烦的是口径分裂</b>：<c>edit_file</c> 对空 <c>old_string</c>
    /// 返回中文失败结果，而缺 <c>path</c> 抛英文异常。同一类问题两种形态，
    /// 模型无法形成稳定预期。现在统一走 <see cref="ToolArguments"/>。
    /// </para>
    /// <para>
    /// 走 dispatcher 时 schema 校验会先拦下（<c>RejectedInvalidArguments</c>），
    /// 所以这条只在**直接调用工具**时触发——但那正是宿主与测试的调用形态，
    /// 不能因为"生产路径被拦住了"就不修。
    /// </para>
    /// </remarks>
    private static async Task<string?> MissingArgUniformAsync()
    {
        using var dir = new TempTree();
        dir.Write("a.txt", "x");
        var files = new LocalFileSystem(dir.Root);

        (string Name, IAgentTool Tool, string Args)[] cases =
        [
            ("glob", new GlobTool(files), "{}"),
            ("grep", new GrepTool(files), "{}"),
            ("read_file", new ReadFileTool(files), "{}"),
            ("write_file", new WriteFileTool(files), "{}"),
        ];

        foreach (var (name, tool, args) in cases)
        {
            try
            {
                await RunAsync(tool, args);
                return $"{name} 缺参数时没有报错";
            }
            catch (Exception ex)
            {
                // 必须是"说人话"的错误：含中文、含具体参数名。
                var message = ex.Message;
                if (ex is KeyNotFoundException)
                    return $"{name} 仍抛 KeyNotFoundException（英文 BCL 消息）：{message}";
                if (!message.Contains("缺少必需参数", StringComparison.Ordinal))
                    return $"{name} 的缺失参数消息不符合统一格式：{message}";
                if (message.Contains("是字符串", StringComparison.Ordinal) is false
                    && !message.Contains("'path'", StringComparison.Ordinal)
                    && !message.Contains("'pattern'", StringComparison.Ordinal)
                    && !message.Contains("'content'", StringComparison.Ordinal))
                {
                    return $"{name} 的缺失参数消息没指出是哪个参数：{message}";
                }
            }
        }

        return null;
    }

    /// <summary>
    /// 超大文件要在读取之前跳过，且计数要报告。
    /// </summary>
    /// <remarks>
    /// <b>这条钉的是一个内存安全问题</b>：原实现先
    /// <c>ReadTextAsync</c> 读进内存、再判 <c>text.Length &gt; MaxFileBytes</c>。
    /// 那时内存已经吃掉了——上限形同虚设。中等仓库里的日志或 SQL dump
    /// 就能把进程推上去。
    /// <para>
    /// <b>验法只能靠"读没读"</b>：用一个会记录读取次数的 seam，
    /// 断言超限文件从未被读。只断言"结果里没有它"不够——
    /// 读完再跳过也满足那条，而内存问题依然存在。
    /// </para>
    /// </remarks>
    private static async Task<string?> GrepSkipsLargeAsync()
    {
        using var dir = new TempTree();
        dir.Write("small.txt", "target here");

        // 造一个超过 2 MiB 的文件。
        var big = Path.Combine(dir.Root, "big.log");
        await File.WriteAllTextAsync(big, new string('x', 3 * 1024 * 1024));

        var seam = new CountingFileSystem(new LocalFileSystem(dir.Root));
        var tool = new GrepTool(seam);

        var result = await RunAsync(tool, """{"pattern":"target"}""");
        var json = result.Content.GetRawText();

        // 超限文件不该被读。
        if (seam.ReadPaths.Any(path => path.EndsWith("big.log", StringComparison.Ordinal)))
            return "超大文件被读取了——读完再判上限，内存已经吃掉";

        // 但必须报告"有大文件被跳过"，否则模型以为搜过全库。
        if (!json.Contains("\"skippedTooLarge\":1", StringComparison.Ordinal))
            return $"没有报告跳过大文件：{json}";

        return null;
    }

    /// <summary>
    /// 重叠匹配时，"拒绝理由"里的次数必须与实际判据一致。
    /// </summary>
    /// <remarks>
    /// <b>原实现的错误消息自相矛盾</b>：`"aa"` 在 `"aaa"` 里，
    /// 检测第二次时用 `firstIndex + 1`（允许重叠）→ 认为有歧义；
    /// 报数时用 `CountOccurrences`（按 needle.Length 跳过，不允许重叠）→ 报 1 次。
    /// 于是模型收到"出现 1 次，拒绝替换"——它只能猜哪里不对。
    /// <para>
    /// 统一按不重叠：替换一次后指针前进 oldText.Length，
    /// 重叠的那几处本来就替换不到。
    /// </para>
    /// </remarks>
    private static async Task<string?> OverlapConsistentAsync()
    {
        using var dir = new TempTree();
        dir.Write("a.txt", "aaa");

        var files = new LocalFileSystem(dir.Root);
        var tool = new EditFileTool(files);

        var result = await RunAsync(tool,
            """{"path":"a.txt","old_string":"aa","new_string":"X"}""");
        var json = result.Content.GetRawText();

        // 不该被判歧义——不重叠语义下只有一处。
        if (json.Contains("\"error\":\"ambiguous\"", StringComparison.Ordinal))
            return $"\"aa\" 在 \"aaa\" 里被误判为歧义：{json}";

        var after = await files.ReadTextAsync("a.txt");
        return after == "Xa" ? null : $"替换结果不对：{after}";
    }

    // ── 支撑 ─────────────────────────────────────────────────────

    private static async Task<AgentToolResult> RunAsync(IAgentTool tool, string argumentsJson)
    {
        using var document = JsonDocument.Parse(argumentsJson);
        return await tool.ExecuteAsync(
            new ToolInvocation("i1", tool.Descriptor.Id, document.RootElement.Clone()),
            CancellationToken.None).ConfigureAwait(false);
    }

    /// <summary>记录读取路径的 seam，用于断言"没被读取"。</summary>
    private sealed class CountingFileSystem(IFileSystem inner) : IFileSystem
    {
        private readonly List<string> _readPaths = [];

        public List<string> ReadPaths
        {
            get
            {
                lock (_readPaths) return [.. _readPaths];
            }
        }

        public ValueTask<string> ReadTextAsync(string path, CancellationToken cancellationToken = default)
        {
            lock (_readPaths) _readPaths.Add(path);
            return inner.ReadTextAsync(path, cancellationToken);
        }

        public ValueTask<FileStat?> StatAsync(string path, CancellationToken cancellationToken = default) =>
            inner.StatAsync(path, cancellationToken);

        public ValueTask<IReadOnlyList<FileEntry>> ListDirectoryAsync(
            string path, CancellationToken cancellationToken = default) =>
            inner.ListDirectoryAsync(path, cancellationToken);

        public ValueTask<FileWriteOutcome> WriteTextAsync(
            string path, string content, FileWriteExpectation? expected = null,
            CancellationToken cancellationToken = default) =>
            inner.WriteTextAsync(path, content, expected, cancellationToken);
    }

    /// <summary>一次性临时目录树，退出时删除。</summary>
    private sealed class TempTree : IDisposable
    {
        public TempTree()
        {
            Root = Path.Combine(Path.GetTempPath(), $"dougong-search-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Root);
        }

        public string Root { get; }

        public void Write(string relative, string content)
        {
            var full = Path.Combine(Root, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, content);
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
