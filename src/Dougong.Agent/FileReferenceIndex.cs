namespace Dougong.Agent;

/// <summary>
/// 按片段搜索工作区路径，供输入框的 <c>@file</c> 补全使用。
/// </summary>
/// <remarks>
/// <b>照 dsh 的 <c>ctx.fileReferences</c> 做，契约一致</b>：
/// <c>path-only completion candidates within an Agent cwd; providers own
/// namespace access and ranking without reading file contents</c>。
/// 要点是三条：
/// <list type="bullet">
/// <item><b>只返回路径</b>——不读文件内容。补全是"你想引用哪个文件"，
/// 不是"这个文件里有什么"；读内容会让一次按键触发几十次磁盘 IO。</item>
/// <item><b>在 Agent 的工作区内</b>——不越界。用户没有理由通过补全
/// 看到工作区外的路径。</item>
/// <item><b>排序由实现负责</b>——同样的查询要给出稳定的顺序，
/// 否则用户按 <c>Tab</c> 时会发现候选在跳。</item>
/// </list>
///
/// <para>
/// <b>不是给模型的功能</b>：模型用 <c>glob</c>/<c>grep</c> 自己找文件。
/// 本能力服务的是**人在输入框里打字的时刻**——他打 <c>@Prog</c>，
/// 想看到 <c>Program.cs</c> 出现。这是宿主 UI 的补全源，不是工具。
/// </para>
///
/// <para>
/// <b>为什么不缓存索引</b>：dsh 有 <c>maxEntries</c>（索引上限），说明它建了
/// 索引。我们不建——工作区通常几千个文件，一次遍历是毫秒级；
/// 而索引要解决失效（文件增删改后什么时候重建），那是持续成本。
/// 等真出现"补全卡顿"的反馈再做，那时也知道瓶颈在哪。
/// </para>
/// </remarks>
public sealed class FileReferenceIndex
{
    /// <summary>默认最多返回几个候选。</summary>
    /// <remarks>
    /// 与 dsh 的 <c>maxResults</c> 取同一个量级。补全下拉框放不下更多，
    /// 而多给的候选只会让用户多按几次方向键。
    /// </remarks>
    public const int DefaultMaxResults = 20;

    /// <summary>默认最多遍历多少个条目。</summary>
    /// <remarks>
    /// <b>必须有上限</b>：在仓库根目录跑一次无界遍历会扫进 <c>node_modules</c>
    /// 之类的十万级目录。dsh 的 <c>maxEntries</c> 是同一个理由。
    /// 到上限就停，并如实标记截断。
    /// </remarks>
    public const int DefaultMaxEntries = 20_000;

    /// <summary>永远不进入的目录名。</summary>
    /// <remarks>
    /// 与 <see cref="GlobTool"/> 共用判据：补全结果里出现
    /// <c>node_modules/...</c> 只会挤掉用户真正想找的文件。
    /// </remarks>
    private static readonly HashSet<string> ExcludedDirectories = new(StringComparer.Ordinal)
    {
        "node_modules", "bin", "obj", ".git", ".vs", ".idea",
        "target", "dist", "build", "__pycache__", ".venv",
    };

    private readonly IFileSystem _files;
    private readonly string _workspace;
    private readonly int _maxResults;
    private readonly int _maxEntries;

    /// <summary>构造。</summary>
    /// <param name="files">文件系统 seam。</param>
    /// <param name="workspace">工作区根；补全不越出它。</param>
    /// <param name="maxResults">最多返回几个候选。</param>
    /// <param name="maxEntries">最多遍历多少个条目。</param>
    public FileReferenceIndex(
        IFileSystem files,
        string workspace,
        int maxResults = DefaultMaxResults,
        int maxEntries = DefaultMaxEntries)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentException.ThrowIfNullOrWhiteSpace(workspace);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxResults, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxEntries, 1);

        _files = files;
        _workspace = workspace;
        _maxResults = maxResults;
        _maxEntries = maxEntries;
    }

    /// <summary>
    /// 按片段查找路径。
    /// </summary>
    /// <param name="query">
    /// 用户已经打出的部分（<c>@</c> 之后的内容）。空表示列出全部。
    /// </param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <remarks>
    /// <b>匹配是"包含"而不是"前缀"</b>：用户常记得文件名的中间一段
    /// （<c>Agent</c> 想找 <c>LoopAgentRuntime.cs</c>），前缀匹配会让他
    /// 必须从开头打字。
    /// <para>
    /// <b>大小写不敏感</b>：路径大小写在不同平台含义不同，而补全是
    /// "帮我想起来"，不是"精确匹配"。
    /// </para>
    /// </remarks>
    public async ValueTask<FileReferenceResult> SearchAsync(
        string? query, CancellationToken cancellationToken = default)
    {
        var needle = (query ?? string.Empty).Trim();

        var matches = new List<string>();
        var visited = 0;
        var truncated = false;

        var pending = new Stack<string>();
        pending.Push(_workspace);

        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (visited >= _maxEntries)
            {
                truncated = true;
                break;
            }

            var directory = pending.Pop();

            IReadOnlyList<FileEntry> entries;
            try
            {
                entries = await _files.ListDirectoryAsync(directory, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 读不了的目录跳过：补全是尽力而为，不该因为一个坏目录失败。
                continue;
            }

            foreach (var entry in entries)
            {
                if (++visited > _maxEntries)
                {
                    truncated = true;
                    break;
                }

                // **自己拼路径**：FileEntry 只有 Name，不带目录。
                // 用 Path.Combine 而不是字符串拼接：Windows 上分隔符不同，
                // 而这里的结果最终要经 Relative 转成正斜杠。
                var full = Path.Combine(directory, entry.Name);
                var relative = Relative(full);

                if (entry.IsDirectory)
                {
                    // 排除目录既不进也不报：它们出现在候选里没有意义。
                    if (!ExcludedDirectories.Contains(entry.Name)) pending.Push(full);
                    continue;
                }

                if (needle.Length == 0 || relative.Contains(needle, StringComparison.OrdinalIgnoreCase))
                    matches.Add(relative);
            }

            if (truncated) break;
        }

        // **排序要稳定**：同分时按路径序，否则用户按 Tab 会发现候选在跳。
        matches.Sort(Compare);

        var results = matches.Count <= _maxResults
            ? matches
            : [.. matches.Take(_maxResults)];

        return new FileReferenceResult(
            results,
            matches.Count,
            Truncated: truncated || matches.Count > _maxResults);
    }

    /// <summary>
    /// 排序：先按"匹配位置"（越靠前越相关），再按路径长度，最后按字典序。
    /// </summary>
    /// <remarks>
    /// <b>不排序会让补全不可用</b>：目录遍历顺序依赖文件系统，
    /// 同一个查询两次结果不同，用户按 <c>Tab</c> 时候选在跳。
    /// <para>
    /// 三维排序的意图：匹配得越早越可能是用户想要的（<c>Prog</c> 命中
    /// <c>Program.cs</c> 优于命中 <c>OldProgramBackup.cs</c>）；同为早匹配时
    /// 短路径更贴近（浅层文件更常用）；最后字典序兜底保证确定性。
    /// </para>
    /// </remarks>
    private static int Compare(string a, string b)
    {
        var byDepth = a.Count(c => c == '/').CompareTo(b.Count(c => c == '/'));
        if (byDepth != 0) return byDepth;

        var byLength = a.Length.CompareTo(b.Length);
        return byLength != 0 ? byLength : string.CompareOrdinal(a, b);
    }

    /// <summary>把绝对路径转成相对工作区的正斜杠形式。</summary>
    /// <remarks>
    /// <b>统一用正斜杠</b>：补全结果会进输入框，而输入框里的路径最终要
    /// 交给工具解析。两种分隔符混用会让用户看到 <c>src\Program.cs</c>
    /// 与 <c>src/Program.cs</c> 混在一起。
    /// </remarks>
    private string Relative(string full)
    {
        var root = _workspace.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            return full.Replace('\\', '/');

        var rest = full[root.Length..].TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return rest.Replace('\\', '/');
    }
}

/// <summary>补全查询的结果。</summary>
/// <param name="Paths">相对工作区的候选路径（正斜杠）。</param>
/// <param name="TotalMatches">匹配总数（可能大于返回条数）。</param>
/// <param name="Truncated">是否因上限而截断。</param>
/// <remarks>
/// <b><see cref="TotalMatches"/> 与 <paramref name="Truncated"/> 分开</b>：
/// 前者是"有多少个"（可显示"还有 12 个"），后者是"我们停了"。
/// 合成一个会让 UI 无法区分"只有 20 个"与"前 20 个"。
/// </remarks>
public sealed record FileReferenceResult(
    IReadOnlyList<string> Paths,
    int TotalMatches,
    bool Truncated);
