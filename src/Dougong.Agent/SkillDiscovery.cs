using System.Text;
using System.Text.Json;

namespace Dougong.Agent;

/// <summary>
/// 从一个目录里发现技能：目录包（<c>&lt;name&gt;/SKILL.md</c>）或平铺文件（<c>&lt;name&gt;.md</c>）。
/// </summary>
/// <remarks>
/// <b>解决什么问题</b>：有些流程说明够长、够特定，不适合塞进系统提示——
/// 比如"发布 NuGet 的步骤"、"这个仓库的迁移写法"。它们只在某些任务里用到，
/// 而每次都写进提示会挤掉真正每轮都要遵守的纪律。技能就是"按需加载的指令文件"。
///
/// <para>
/// <b>照 dsh 的 <c>dsh-skill-filesystem</c>，核心行为一致</b>：
/// <list type="bullet">
/// <item><b>两种形态</b>：目录包 <c>&lt;name&gt;/SKILL.md</c> 与平铺 <c>&lt;name&gt;.md</c></item>
/// <item><b>名字是 kebab-case</b>：<c>^[a-z0-9]+(?:-[a-z0-9]+)*$</c>。
/// 不符合的目录直接跳过——它们不是技能，而是别的什么东西</item>
/// <item><b>不递归</b>：dsh 明确不支持嵌套 <c>**/SKILL.md</c> 发现</item>
/// <item><b>按 rank 决定同名胜负</b>：项目级的胜过用户级的，因为项目约定更具体</item>
/// </list>
/// </para>
///
/// <para>
/// <b>扫描顺序照 dsh 的优先级表，但只取我们有的三档</b>：
/// </para>
/// <list type="table">
/// <item><term>100 <c>project-dsh</c></term><description><c>&lt;项目&gt;/.dougong/skills</c></description></item>
/// <item><term>200 <c>project-agents</c></term><description><c>&lt;项目&gt;/.agents/skills</c></description></item>
/// <item><term>400 <c>user-dsh</c></term><description><c>&lt;dougong home&gt;/skills</c></description></item>
/// </list>
/// <para>
/// <b>没有 <c>custom</c>（300）与 <c>bundled</c>（600）</b>：前者要有配置项，
/// 后者要有打包产物。等这两样真的出现时再加——加空的配置键比不加更难维护。
/// <para>
/// <b>也没有 <c>user-agents</c>（500）</b>：那一档读 <c>~/.agents/skills</c>，
/// 是另一个工具生态的约定。我们没有那个目录的既定位置，
/// 加一个"猜出来的"用户级根会让"我这条技能从哪来的"变得说不清。
/// （复检时删掉了这个根，以及只有它在用的 `CreateDefault`——那是个零调用点的死方法。）
/// </para>
/// </para>
///
/// <para>
/// <b>不做热重载与 watcher</b>：dsh 用 Chokidar 盯目录变化并作废缓存。
/// 我们每次调用时重新扫——目录数量是个位数、每个目录只读一个文件头，
/// 毫秒级。watcher 要处理"目录还不存在""在被删的瞬间"这些边界，
/// 换来的只是省掉一次毫秒级扫描。
/// </para>
/// </remarks>
public sealed class SkillDiscovery
{
    /// <summary>技能名允许的形态。</summary>
    public static readonly System.Text.RegularExpressions.Regex NamePattern =
        new("^[a-z0-9]+(?:-[a-z0-9]+)*$", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>目录包里的说明文件名。</summary>
    public const string BundleFileName = "SKILL.md";

    /// <summary>描述的最大字符数，照 dsh 的 <c>catalogDescriptionMaxLength</c> 默认值。</summary>
    public const int DefaultDescriptionMaxLength = 500;

    private readonly IReadOnlyList<SkillRoot> _roots;
    private readonly int _descriptionMaxLength;

    /// <summary>构造。</summary>
    /// <param name="roots">按 rank 升序排列的根目录。</param>
    /// <param name="descriptionMaxLength">描述截断长度。</param>
    public SkillDiscovery(
        IReadOnlyList<SkillRoot> roots,
        int descriptionMaxLength = DefaultDescriptionMaxLength)
    {
        ArgumentNullException.ThrowIfNull(roots);
        _roots = [.. roots.OrderBy(r => r.Rank)];
        _descriptionMaxLength = descriptionMaxLength;
    }

    /// <summary>扫描全部根，返回合并后的目录。</summary>
    /// <remarks>
    /// <b>同名取 rank 最小的那个</b>，并且**整条影子链都记下来**：
    /// 只说"你赢了"不够——用户会想知道"我项目里那个为什么没生效"。
    /// </remarks>
    public SkillCatalog Discover()
    {
        var chosen = new Dictionary<string, SkillSummary>(StringComparer.Ordinal);
        var shadowed = new Dictionary<string, List<string>>(StringComparer.Ordinal);

        foreach (var root in _roots)
        {
            if (!Directory.Exists(root.Path)) continue;

            foreach (var candidate in ScanRoot(root))
            {
                if (chosen.TryGetValue(candidate.Summary.Name, out var existing))
                {
                    // 同名：rank 小的赢。因为 _roots 已按 rank 升序，先到的一定更优先，
                    // 所以这里只记影子，不替换。
                    if (!shadowed.TryGetValue(existing.Name, out var list))
                        shadowed[existing.Name] = list = [];
                    // **只记来源标签，不记路径**：这段文本会进模型上下文，
                    // 而绝对路径含用户名、会随会话日志外发。同一个类的
                    // SkillCatalogText 承诺"不给正文与绝对路径"，这里不能破例。
                    list.Add($"{root.Source}（rank {root.Rank}）");
                    continue;
                }

                chosen[candidate.Summary.Name] = candidate.Summary;
            }
        }

        var summaries = chosen.Values
            .Select(s => s with
            {
                ShadowedBy = shadowed.TryGetValue(s.Name, out var list) ? list : [],
            })
            .OrderBy(s => s.Name, StringComparer.Ordinal)
            .ToList();

        return new SkillCatalog(summaries);
    }

    /// <summary>
    /// 加载一个技能的完整体。
    /// </summary>
    /// <remarks>
    /// <b>每次都重读</b>：dsh 明确说完整定义不缓存（"A definition whose name no
    /// longer matches that candidate is rejected"）。理由很实际——技能正文是
    /// 用户正在编辑的文件，缓存会让"我改了但它还是照旧的来"。
    /// </remarks>
    /// <param name="name">技能名。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>完整体；找不到或读不了时 null（不抛：那是模型的输入问题）。</returns>
    public async Task<SkillDefinition?> LoadAsync(
        string name, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(name) || !NamePattern.IsMatch(name)) return null;

        // 重新走一遍发现，拿"当前该用哪一个"——期间用户可能加了项目级同名技能。
        var catalog = Discover();
        var summary = catalog.Skills.FirstOrDefault(
            s => string.Equals(s.Name, name, StringComparison.Ordinal));
        if (summary is null) return null;

        string body;
        try
        {
            body = await File.ReadAllTextAsync(summary.Path, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        var (metadata, content) = SkillFrontmatter.Split(body);

        // 正文里可能还残留 name 与发现时不一致的情况（文件被换了内容）。
        // 不匹配就当"已经不可用"——返回一份内容与名字对不上的技能比报错更糟。
        if (metadata.Name is { Length: > 0 } declared
            && !string.Equals(declared, name, StringComparison.Ordinal))
            return null;

        return new SkillDefinition(summary, content, metadata);
    }

    private IEnumerable<SkillCandidate> ScanRoot(SkillRoot root)
    {
        string[] directories;
        string[] files;
        try
        {
            directories = Directory.GetDirectories(root.Path);
            files = Directory.GetFiles(root.Path, "*.md");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 单个根读不了就跳过：一个坏目录不该让整份技能目录消失。
            yield break;
        }

        foreach (var directory in directories.OrderBy(d => d, StringComparer.Ordinal))
        {
            var name = Path.GetFileName(directory);
            if (!NamePattern.IsMatch(name)) continue;

            var file = Path.Combine(directory, BundleFileName);
            if (!File.Exists(file)) continue;

            if (ReadSummary(root, name, file) is { } summary)
                yield return new SkillCandidate(summary);
        }

        foreach (var file in files.OrderBy(f => f, StringComparer.Ordinal))
        {
            var name = Path.GetFileNameWithoutExtension(file);
            if (!NamePattern.IsMatch(name)) continue;

            if (ReadSummary(root, name, file) is { } summary)
                yield return new SkillCandidate(summary);
        }
    }

    /// <summary>读文件头，取出 name/description 等元数据。</summary>
    /// <remarks>
    /// <b>只读头部</b>：目录里可能有很多技能，而正文可能各几 KB。
    /// 读整个文件再切 frontmatter 会让一次目录扫描变成读几十 KB。
    /// 这里只读到第二个 <c>---</c> 为止（且设上限），正文留给 <see cref="LoadAsync"/>。
    /// </remarks>
    private SkillSummary? ReadSummary(SkillRoot root, string name, string file)
    {
        string head;
        try
        {
            head = ReadHead(file, MaxFrontmatterBytes);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        var (metadata, _) = SkillFrontmatter.Split(head);

        // 描述缺失时用名字兜底，而不是把技能丢掉：
        // 一个没有 description 的技能仍然可以被加载，只是模型不知道它干什么。
        // 丢掉它对用户更糟——"我明明写了 SKILL.md"。
        var description = metadata.Description;
        if (string.IsNullOrWhiteSpace(description))
            description = $"（未提供描述：{name}）";

        description = Truncate(
            description.Replace('\r', ' ').Replace('\n', ' ').Trim(),
            _descriptionMaxLength);

        return new SkillSummary(
            name,
            description,
            metadata.WhenToUse,
            file,
            root.Source,
            ModelInvocable: metadata.ModelInvocable,
            UserInvocable: metadata.UserInvocable,
            ShadowedBy: []);
    }

    /// <summary>frontmatter 最多读这么多字节；超出的部分不参与元数据。</summary>
    /// <remarks>
    /// **必须有上限**：一个 10 MB 的 Markdown 若是技能，读它会卡住目录扫描，
    /// 而用户看不到原因。4 KiB 足够写下全部元数据字段。
    /// </remarks>
    internal const int MaxFrontmatterBytes = 4096;

    private static string ReadHead(string file, int maxBytes)
    {
        using var stream = new FileStream(
            file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, bufferSize: 1024);
        var buffer = new byte[Math.Min(maxBytes, (int)Math.Min(stream.Length, int.MaxValue))];
        var read = stream.Read(buffer, 0, buffer.Length);
        return Encoding.UTF8.GetString(buffer, 0, read);
    }

    /// <summary>按字符数截断，加省略号。</summary>
    /// <remarks>
    /// <b>截断必须给省略号留位</b>：前一阶段在会话标题上就是这样错的——
    /// 上限 + 一个省略号 = 超出一个字符，而那个数字是被声明的列宽。
    /// </remarks>
    private static string Truncate(string text, int max)
    {
        if (max <= 0 || text.Length <= max) return text;
        if (max == 1) return "…";
        return text[..(max - 1)] + "…";
    }
}

/// <summary>一个技能根目录。</summary>
/// <param name="Rank">优先级；小的赢同名冲突。</param>
/// <param name="Source">来源标签，模型可见的元数据（不是优先级本身）。</param>
/// <param name="Path">绝对路径。</param>
public sealed record SkillRoot(int Rank, string Source, string Path);

/// <summary>目录里的一个技能（发现阶段的中间形态）。</summary>
/// <param name="Summary">摘要（已含正文路径与来源标签）。</param>
/// <remarks>
/// <b>只有摘要</b>：这里曾经另外带 <c>Root</c> 与 <c>Path</c> 两个字段，
/// 而它们与 <see cref="SkillSummary"/> 里的对应字段重复、且**零个读取点**。
/// 同一事实存两处，改一处漏一处不会报错——删掉重复的那份。
/// </remarks>
public sealed record SkillCandidate(SkillSummary Summary);

/// <summary>技能摘要：模型可见的那部分。</summary>
/// <param name="Name">kebab-case 名字。</param>
/// <param name="Description">一句说明，用于让模型判断"要不要用"。</param>
/// <param name="WhenToUse">可选的额外路由提示。</param>
/// <param name="Path">正文文件绝对路径。</param>
/// <param name="Source">来源标签。</param>
/// <param name="ModelInvocable">模型是否可见/可加载。</param>
/// <param name="UserInvocable">用户侧入口是否可见。</param>
/// <param name="ShadowedBy">
/// 被更高优先级根遮住的同名技能，形式是 <c>来源标签（rank N）</c>。
/// <b>必须在目录里报出来</b>：否则用户会问"我那个为什么没生效"。
/// <para>
/// <b>只放来源标签，不放路径</b>：这段文本会进模型上下文，
/// 而绝对路径会把用户名带进对话并随日志外发。
/// </para>
/// </param>
public sealed record SkillSummary(
    string Name,
    string Description,
    string? WhenToUse,
    string Path,
    string Source,
    bool ModelInvocable,
    bool UserInvocable,
    IReadOnlyList<string> ShadowedBy);

/// <summary>发现结果。</summary>
/// <param name="Skills">按名字排序的技能。</param>
public sealed record SkillCatalog(IReadOnlyList<SkillSummary> Skills);

/// <summary>一个技能的完整体。</summary>
/// <param name="Summary">摘要。</param>
/// <param name="Content">Markdown 正文（已去掉 frontmatter）。</param>
/// <param name="Metadata">解析后的元数据。</param>
public sealed record SkillDefinition(
    SkillSummary Summary,
    string Content,
    SkillMetadata Metadata);

/// <summary>技能 frontmatter 里的字段。</summary>
/// <param name="Name">声明的名字；可缺省。</param>
/// <param name="Description">说明。</param>
/// <param name="WhenToUse">路由提示。</param>
/// <param name="ModelInvocable">对应 <c>disable-model-invocation</c> 的反面。</param>
/// <param name="UserInvocable">对应 <c>user-invocable</c>。</param>
public sealed record SkillMetadata(
    string? Name,
    string? Description,
    string? WhenToUse,
    bool ModelInvocable,
    bool UserInvocable);

/// <summary>
/// 极简 frontmatter 解析：只认 <c>---</c> 包起来的 <c>key: value</c>。
/// </summary>
/// <remarks>
/// <b>不引入 YAML 库</b>：技能 frontmatter 用到的只有字符串与布尔。
/// 引一个 YAML 解析器会为"支持嵌套列表与锚点"付出代价，而那些在这里没有语义
/// ——dsh 也只读固定的几个键。
///
/// <para>
/// <b>无法识别的键直接忽略，不报错</b>：技能文件由用户手写，多一个自定义键
/// 不该让整个技能不可用。而**认错的键**（比如把 <c>disable-model-invocation: true</c>
/// 当字符串处理）会让技能意外可见——那是权限方向上的错误，所以布尔解析要严格。
/// </para>
/// </remarks>
public static class SkillFrontmatter
{
    /// <summary>拆分出元数据与正文。</summary>
    /// <param name="text">文件内容（可只是头部）。</param>
    public static (SkillMetadata Metadata, string Content) Split(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var normalized = text.Replace("\r\n", "\n").Replace('\r', '\n');
        if (!normalized.TrimStart().StartsWith("---", StringComparison.Ordinal))
            return (Defaults(), text);

        var start = normalized.IndexOf("---", StringComparison.Ordinal);
        var afterStart = normalized.IndexOf('\n', start);
        if (afterStart < 0) return (Defaults(), text);

        var end = normalized.IndexOf("\n---", afterStart, StringComparison.Ordinal);
        if (end < 0)
        {
            // 只有开头没有结尾：不把它当 frontmatter。
            // 这样"正文里恰好有条水平线"不会被误读成元数据的开始。
            return (Defaults(), text);
        }

        // **`end` 可能正好等于 `afterStart`**：`---\n---\n正文` 这种空 frontmatter，
        // 闭合的 `---` 前面那个换行就是开头那个换行。此时 `(afterStart+1)..end`
        // 是逆序区间，切片直接抛 ArgumentOutOfRangeException——症状是
        // "这个技能读不了"，看起来像文件损坏，而实际只是元数据为空。
        var header = end > afterStart ? normalized[(afterStart + 1)..end] : string.Empty;

        // 正文从闭合的 `---` 之后开始；`end` 指向它前面的换行，所以跳 4 个字符。
        var bodyOffset = end + 4;
        var body = bodyOffset >= normalized.Length
            ? string.Empty
            : normalized[bodyOffset..];

        return (Parse(header), body);
    }

    private static SkillMetadata Parse(string header)
    {
        string? name = null;
        string? description = null;
        string? whenToUse = null;
        var modelInvocable = true;
        var userInvocable = true;

        foreach (var rawLine in header.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;

            var colon = line.IndexOf(':');
            if (colon <= 0) continue;

            var key = line[..colon].Trim();
            var value = line[(colon + 1)..].Trim().Trim('"', '\'');

            switch (key)
            {
                case "name":
                    name = value.Length == 0 ? null : value;
                    break;
                case "description":
                    description = value.Length == 0 ? null : value;
                    break;
                case "when-to-use":
                case "whenToUse":
                    whenToUse = value.Length == 0 ? null : value;
                    break;

                // **两个开关的方向相反，这是最容易写反的一处**：
                // dsh 的 `disable-model-invocation: true` 对应 modelInvocable=false。
                case "disable-model-invocation":
                    modelInvocable = !ParseBool(value, assumeTrue: false);
                    break;
                case "user-invocable":
                    userInvocable = ParseBool(value, assumeTrue: true);
                    break;
            }
        }

        return new SkillMetadata(name, description, whenToUse, modelInvocable, userInvocable);
    }

    /// <summary>严格解析布尔：只认 <c>true</c>/<c>false</c>。</summary>
    /// <remarks>
    /// <b>不认 <c>yes</c>/<c>1</c>/<c>on</c></b>：这些在 frontmatter 里没有统一约定，
    /// 而"认了一部分"会让用户以为全都能用。认不出时取保守值——对
    /// <c>disable-model-invocation</c> 保守就是"允许模型用"（它的默认），
    /// 对 <c>user-invocable</c> 保守也是默认值。也就是说认不出时行为等于没写这个键。
    /// </remarks>
    private static bool ParseBool(string value, bool assumeTrue) =>
        bool.TryParse(value, out var parsed) ? parsed : assumeTrue;

    private static SkillMetadata Defaults() =>
        new(null, null, null, ModelInvocable: true, UserInvocable: true);
}

/// <summary>技能目录与模型之间的呈现。</summary>
/// <remarks>
/// <b>只给名字与描述，不给正文与绝对路径</b>：dsh 的原话是
/// "the model session catalog uses only model-invocable name and description,
/// never the body or absolute file path"。给绝对路径会把用户名带进对话，
/// 给正文会让一次目录注入变成几百 KB。
/// </remarks>
public static class SkillCatalogText
{
    /// <summary>渲染成喂给模型的一行行文本。</summary>
    /// <remarks>
    /// <b>XML 转义描述</b>：描述是用户写的自由文本，出现 <c>&lt;</c> 或 <c>&amp;</c>
    /// 会把整段标记打乱——而那看起来像"提示被截断了"。
    /// </remarks>
    public static string Render(SkillCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);

        var sb = new StringBuilder();
        sb.AppendLine("可用的技能（用 skill 工具按名字加载完整说明）：");

        foreach (var skill in catalog.Skills.Where(s => s.ModelInvocable))
        {
            sb.Append("- ").Append(skill.Name).Append("：")
                .Append(Escape(skill.Description)).AppendLine();

            if (!string.IsNullOrWhiteSpace(skill.WhenToUse))
                sb.Append("  （何时用：").Append(Escape(skill.WhenToUse)).AppendLine("）");

            // **被遮住的同名技能要说出来**。注释在 SkillSummary.ShadowedBy 上
            // 写着"必须在目录里报出来，否则用户会问'我项目里那个为什么没生效'"，
            // 而第一版只把字段填上、没有任何地方读它——承诺与实现脱节。
            //
            // 报给模型而不是只报给用户：模型看到"这个名字还有别的来源"，
            // 在结果不如预期时才会想到去问，而不是默默换一种做法。
            if (skill.ShadowedBy.Count > 0)
            {
                sb.Append("  （同名但优先级更低的来源，未生效：")
                    .Append(Escape(string.Join("；", skill.ShadowedBy)))
                    .AppendLine("）");
            }
        }

        return sb.ToString().TrimEnd();
    }

    private static string Escape(string text) =>
        text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
}
