using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Dougong.Agent;

/// <summary>
/// 在工作区里按正则搜索文件内容。
/// </summary>
/// <remarks>
/// <b>与 <see cref="GlobTool"/> 分开的理由</b>：成本差一个量级。列目录是
/// O(目录数)，读文件是 O(字节数)。合成一个工具会让"我只想找文件名"
/// 变成一次全库读取。
///
/// <para>
/// <b>不打包 ripgrep</b>：理由同 <see cref="GlobTool"/>——搜索建在
/// <see cref="IFileSystem"/> seam 上，沙箱或远程实现也能用。性能不足时
/// 优化实现，而不是绕过抽象层挂一个本地二进制。
/// </para>
///
/// <para>
/// <b>必须限制扫描量</b>：一次 grep 在中等仓库上能读几十 MB。这里的上限
/// 不是为了省时间，是为了**避免把整库内容读进内存**——那会在大仓库上
/// 直接 OOM，而模型只是想知道某个函数在哪。
/// </para>
/// </remarks>
/// <param name="files">文件能力来源。</param>
public sealed class GrepTool(IFileSystem files) : IAgentTool
{
    /// <summary>最多扫描多少个文件。</summary>
    /// <remarks>
    /// <b>超限必须如实报告</b>：静默停下会让模型以为"没有匹配"，
    /// 那会导致它换一个不存在的名字再搜一次，白跑一轮。
    /// </remarks>
    private const int MaxFilesScanned = 400;

    /// <summary>单文件超过这个大小就跳过。</summary>
    /// <remarks>
    /// 二进制文件与压缩产物没有意义；而超大文本文件（日志、SQL dump）
    /// 读进来会挤占整个上下文预算。
    /// </remarks>
    private const long MaxFileBytes = 2 * 1024 * 1024;

    /// <summary>最多返回多少条匹配。</summary>
    private const int MaxMatches = 100;

    /// <summary>每条匹配最多保留多少字符。</summary>
    /// <remarks>
    /// 一行可能是压缩过的 JS，几千字符。截断到 300 是让模型能看清上下文
    /// 又不被一行占满——超出时明确加省略号，不静默切掉。
    /// </remarks>
    private const int MaxLineLength = 300;

    private readonly IFileSystem _files = files
        ?? throw new ArgumentNullException(nameof(files));

    public ToolDescriptor Descriptor { get; } = new(
        "grep", "grep", "1.0.0",
        "在工作区文件内容里按正则搜索，返回匹配的文件、行号与该行内容",
        JsonDocument.Parse(
            """
            {"type":"object","properties":{
              "pattern":{"type":"string","description":"正则表达式"},
              "path":{"type":"string","description":"可选的起始目录，相对工作区根；省略表示工作区根"},
              "file_pattern":{"type":"string","description":"可选的文件名过滤，如 *.cs"}
            },"required":["pattern"]}
            """).RootElement.Clone());

    public ToolPermission DescribePermission(JsonElement arguments) =>
        new(ToolAccess.Read, true, [GlobTool.ReadPath(arguments)], [], []);

    public ToolExecutionPlan DescribeExecution(JsonElement arguments) =>
        new(ToolConcurrency.Serial)
        {
            Reads = [FileResourceKeys.ForPath(arguments)],
        };

    public IReadOnlyList<string> DescribeFileDependencies(JsonElement arguments) => [];

    public async ValueTask<AgentToolResult> ExecuteAsync(
        ToolInvocation invocation, CancellationToken cancellationToken)
    {
        var patternText = ToolArguments.RequiredString(invocation.Arguments, "pattern");
        if (string.IsNullOrWhiteSpace(patternText))
            throw new InvalidOperationException("参数 'pattern' 不能为空白。");

        Regex regex;
        try
        {
            regex = new Regex(
                patternText, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2));
        }
        catch (ArgumentException ex)
        {
            // **非法正则要报出来，不能当"没有匹配"**：后者会让模型以为
            // 内容不存在，而实际是它的写法有问题。
            throw new InvalidOperationException($"正则不合法：{ex.Message}");
        }

        var start = GlobTool.ReadPath(invocation.Arguments);
        var fileFilter = ToolArguments.OptionalString(invocation.Arguments, "file_pattern")
            is { } f
                ? GlobTool.ToRegex(f)
                : null;

        var files = await CollectFilesAsync(start, fileFilter, cancellationToken)
            .ConfigureAwait(false);

        var hits = new List<object>();
        var scanned = 0;
        var skippedUnreadable = 0;
        var skippedLarge = 0;

        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (scanned >= MaxFilesScanned) break;
            scanned++;

            // **先问大小再读**：ReadTextAsync 会把整个文件读进内存，
            // 读完再判"太大"没有意义——内存已经吃掉了。
            // 大日志、SQL dump 在中等仓库里很常见，这个顺序差别是 OOM 与否。
            var stat = await _files.StatAsync(file, cancellationToken).ConfigureAwait(false);
            if (stat is { Length: > MaxFileBytes }) { skippedLarge++; continue; }

            string text;
            try
            {
                text = await _files.ReadTextAsync(file, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                or DecoderFallbackException or FileNotFoundException)
            {
                // 读不了的文件跳过：二进制、无权限、编码不认识。
                skippedUnreadable++;
                continue;
            }

            var lineNumber = 0;
            foreach (var line in text.Split('\n'))
            {
                lineNumber++;
                if (!regex.IsMatch(line)) continue;

                hits.Add(new
                {
                    file,
                    line = lineNumber,
                    text = line.Length <= MaxLineLength
                        ? line.TrimEnd('\r')
                        : line[..MaxLineLength].TrimEnd('\r') + "…",
                });

                if (hits.Count >= MaxMatches) break;
            }

            if (hits.Count >= MaxMatches) break;
        }

        using var document = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            pattern = patternText,
            start,
            matchCount = hits.Count,
            filesScanned = scanned,
            // **四个"没做完"的信号必须都报出来**：任何一个静默，
            // 模型都会把"没搜完"当成"没有"。
            truncated = hits.Count >= MaxMatches,
            scanLimitReached = scanned >= MaxFilesScanned,
            skippedUnreadable,
            // 跳过的大文件也要报：模型看不到"这里有 2MB 的文件没搜"，
            // 就会把"没匹配"当成"内容不存在"。
            skippedTooLarge = skippedLarge,
            truncationNote = BuildTruncationNote(
                hits.Count >= MaxMatches, scanned >= MaxFilesScanned),
            matches = hits,
        }));
        return new AgentToolResult(document.RootElement.Clone());
    }

    /// <summary>把"没搜完"的原因写成一句人能直接行动的话。</summary>
    /// <remarks>
    /// <b>只报布尔不够</b>：模型看到 <c>truncated: true</c> 不知道下一步该改什么，
    /// 常见反应是原样重试一次——那必然得到同样的截断。
    /// 所以这里直接给两种可行的收敛方式。
    /// </remarks>
    private static string? BuildTruncationNote(bool truncated, bool scanLimit)
    {
        if (!truncated && !scanLimit) return null;

        var reasons = new List<string>();
        if (truncated) reasons.Add($"匹配数达到上限 {MaxMatches}");
        if (scanLimit) reasons.Add($"扫描文件数达到上限 {MaxFilesScanned}");

        return string.Join("；", reasons)
            + " 后停止，**不代表没有更多**。"
            + "请用更精确的 pattern、加 file_pattern 过滤，或指定 path 缩小范围。";
    }

    /// <summary>收集待扫描的文件，应用文件名过滤。</summary>
    /// <remarks>
    /// 深度与噪声目录的规则复用 <see cref="GlobTool"/> 的判据——
    /// 两个工具对"哪些目录该跳过"必须一致，否则同一个仓库里
    /// glob 找得到而 grep 搜不到。
    /// </remarks>
    private async Task<List<string>> CollectFilesAsync(
        string start, Regex? filter, CancellationToken cancellationToken)
    {
        var result = new List<string>();
        var stack = new Stack<(string Path, int Depth)>();
        stack.Push((start, 0));

        while (stack.Count > 0 && result.Count < MaxFilesScanned)
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
                continue;
            }

            foreach (var entry in entries)
            {
                var childPath = string.IsNullOrEmpty(dir) || dir == "."
                    ? entry.Name
                    : dir.TrimEnd('/') + "/" + entry.Name;

                if (entry.IsDirectory)
                {
                    if (depth + 1 >= 12 || GlobTool.IsNoiseDirectory(entry.Name)) continue;
                    stack.Push((childPath, depth + 1));
                    continue;
                }

                // **不在这里按大小筛**：这里的 entry.Length 是字节，
                // 而读取后判的是字符数，两者单位不同。混用会让同一个上限
                // 在两处表达成不同的阈值（UTF-16 下差一倍）。
                // 大小判断统一放到读取前那一处，用 StatAsync 的字节数。
                if (filter is not null && !filter.IsMatch(entry.Name)) continue;

                result.Add(childPath);
            }
        }

        result.Sort(StringComparer.Ordinal);
        return result;
    }
}
