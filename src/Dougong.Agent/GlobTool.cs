using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Dougong.Agent;

/// <summary>
/// 在工作区里按文件名模式找文件。
/// </summary>
/// <remarks>
/// <b>为什么不打包 ripgrep</b>：dsh 带了一个约 30 MB 的原生二进制，跨平台分发
/// 要维护多份产物。而我们走 <see cref="IFileSystem"/> seam——沙箱实现将来
/// 可能根本没有本地文件系统，那时外挂二进制就用不上。正确做法是让搜索
/// 建在 seam 上，性能不足时优化实现，而不是绕过抽象层。
///
/// <para>
/// <b>只搜文件名，不搜内容</b>：内容搜索是 <see cref="GrepTool"/> 的事。
/// 分开的理由是成本差一个量级——列目录是 O(目录数)，读文件是 O(字节数)。
/// 合成一个工具会让"我只想找文件名"变成一次全库读取。
/// </para>
/// </remarks>
/// <param name="files">文件能力来源。</param>
public sealed class GlobTool(IFileSystem files) : IAgentTool
{
    /// <summary>最多递归多少层目录。</summary>
    /// <remarks>
    /// <b>必须有上限</b>：<c>node_modules</c> 这类深目录能让一次 glob 跑几十秒，
    /// 而模型只是在找一个文件。超过上限时如实报告"结果可能不完整"，
    /// 而不是静默停下——静默停下会让模型以为真的没有。
    /// </remarks>
    private const int MaxDepth = 12;

    /// <summary>最多返回多少条。</summary>
    /// <remarks>
    /// 上限的作用不是"省流量"，是**避免把模型淹掉**：返回 5000 条路径
    /// 会占满上下文，而模型真正需要的通常前几条。截断时明确告知总数。
    /// </remarks>
    private const int MaxResults = 200;

    private readonly IFileSystem _files = files
        ?? throw new ArgumentNullException(nameof(files));

    public ToolDescriptor Descriptor { get; } = new(
        "glob", "glob", "1.0.0",
        "按文件名模式查找工作区内的文件（支持 * 与 ? 通配，例如 *.cs 或 src/**/*.json）",
        JsonDocument.Parse(
            """
            {"type":"object","properties":{
              "pattern":{"type":"string","description":"文件名模式，支持 * 和 ?；不含路径分隔符时匹配任意目录下的文件名"},
              "path":{"type":"string","description":"可选的起始目录，相对工作区根；省略表示工作区根"}
            },"required":["pattern"]}
            """).RootElement.Clone());

    public ToolPermission DescribePermission(JsonElement arguments) =>
        new(ToolAccess.Read, true, [ReadPath(arguments)], [], []);

    /// <summary>只读工具：多个 glob 可并行，与写同一路径的调用互斥。</summary>
    public ToolExecutionPlan DescribeExecution(JsonElement arguments) =>
        new(ToolConcurrency.Serial)
        {
            Reads = [FileResourceKeys.ForPath(arguments)],
        };

    public IReadOnlyList<string> DescribeFileDependencies(JsonElement arguments) => [];

    public async ValueTask<AgentToolResult> ExecuteAsync(
        ToolInvocation invocation, CancellationToken cancellationToken)
    {
        var pattern = ToolArguments.RequiredString(invocation.Arguments, "pattern");
        if (string.IsNullOrWhiteSpace(pattern))
            throw new InvalidOperationException("参数 'pattern' 不能为空白。");

        var start = ReadPath(invocation.Arguments);
        var regex = ToRegex(pattern);

        var matches = new List<string>();
        var truncated = false;
        var depthLimited = false;

        // 手写 DFS 而不是 Directory.EnumerateFiles(AllDirectories)：
        // 后者无法限深，也无法在 seam 上工作。
        var stack = new Stack<(string Path, int Depth)>();
        stack.Push((start, 0));

        while (stack.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (dir, depth) = stack.Pop();

            IReadOnlyList<FileEntry> entries;
            try
            {
                entries = await _files.ListDirectoryAsync(dir, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (DirectoryNotFoundException)
            {
                // 起始目录不存在是正常情况（用户可能写错路径），报告而不是抛。
                continue;
            }

            foreach (var entry in entries)
            {
                var childPath = string.IsNullOrEmpty(dir) || dir == "."
                    ? entry.Name
                    : dir.TrimEnd('/') + "/" + entry.Name;

                if (entry.IsDirectory)
                {
                    if (depth + 1 >= MaxDepth) { depthLimited = true; continue; }
                    // 跳过常见的巨型产物目录：它们让搜索变慢却几乎总不是目标。
                    if (IsNoiseDirectory(entry.Name)) continue;
                    stack.Push((childPath, depth + 1));
                    continue;
                }

                if (!regex.IsMatch(entry.Name)) continue;

                if (matches.Count >= MaxResults) { truncated = true; break; }
                matches.Add(childPath);
            }

            if (truncated) break;
        }

        // 排序让结果稳定：不排序时底层枚举顺序依赖文件系统，
        // 同一次查询两次结果顺序不同，模型会以为环境变了。
        matches.Sort(StringComparer.Ordinal);

        using var document = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            pattern,
            start,
            count = matches.Count,
            matches,
            truncated,
            depthLimited,
            // **截断时要说清"这不是前 N 个"**：结果是遍历到的前 N 条，
            // 排序只是让它们稳定，不是"字典序最小的 N 条"。
            // 不写这句，模型会以为按名字排在前面的就这些，
            // 于是"没找到 aaa.cs"被理解成"文件不存在"——
            // 而它可能就在没遍历到的部分里。
            truncationNote = truncated
                ? $"结果达到上限 {MaxResults} 条后停止，**不代表只有这些**。"
                    + "请用更精确的 pattern 或指定 path 缩小范围。"
                : null,
        }));
        return new AgentToolResult(document.RootElement.Clone());
    }

    /// <summary>读可选的起始目录；省略表示工作区根。</summary>
    internal static string ReadPath(JsonElement arguments) =>
        ToolArguments.OptionalString(arguments, "path") ?? ".";

    /// <summary>
    /// 该跳过的目录名。
    /// </summary>
    /// <remarks>
    /// 这份名单是**启发式而不是规则**：用户可能有正当理由搜 <c>node_modules</c>。
    /// 所以它只影响"默认不进去"，而不禁止——需要时可以给出明确的 path 参数。
    /// </remarks>
    internal static bool IsNoiseDirectory(string name) => name switch
    {
        "node_modules" or "bin" or "obj" or ".git" or ".vs" or ".idea"
            or "target" or "dist" or "build" or "__pycache__" or ".venv" => true,
        _ => false,
    };

    /// <summary>
    /// 把 glob 模式转成正则。
    /// </summary>
    /// <remarks>
    /// <b>只支持 <c>*</c> 与 <c>?</c></b>：<c>**</c> 在本实现里与单个 <c>*</c> 等价，
    /// 因为匹配只针对文件名（不含目录）。这不是疏漏——真要按路径匹配时，
    /// 用户应当传更具体的 <c>path</c>。声明支持而实现不一致比不支持更糟。
    /// <para>
    /// <b>必须转义正则元字符</b>：模式里出现 <c>.</c> 或 <c>(</c> 时不转义会
    /// 让匹配行为完全错乱，而那看起来像"搜索不工作"。
    /// </para>
    /// </remarks>
    internal static Regex ToRegex(string pattern)
    {
        var builder = new StringBuilder("^");
        foreach (var ch in pattern)
        {
            builder.Append(ch switch
            {
                '*' => ".*",
                '?' => ".",
                _ => Regex.Escape(ch.ToString()),
            });
        }

        builder.Append('$');

        // 不区分大小写：文件名大小写在不同平台上意义不同，而用户搜 "readme"
        // 期望找到 README。代价是可能多几条，收益是少一次"为什么搜不到"。
        return new Regex(builder.ToString(), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }
}
