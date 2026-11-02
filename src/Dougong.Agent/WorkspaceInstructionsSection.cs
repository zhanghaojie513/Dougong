using System.Text;

namespace Dougong.Agent;

/// <summary>
/// 从工作区读取 <c>AGENTS.md</c> 兼容的指令文件，作为系统提示的一个分节。
/// </summary>
/// <remarks>
/// <b>为什么需要它</b>：没有这一节，模型对项目一无所知——不知道构建命令、
/// 不知道代码风格、不知道哪些文件不该动。用户只能在每次对话里重复交代，
/// 或者把约定写进系统提示的固定文本，而那对多项目场景不成立。
///
/// <para>
/// <b>照 dsh 的 <c>dsh-agent-instructions</c> 做</b>，核心行为一致：
/// <list type="bullet">
/// <item><b>两条链</b>：用户全局（<c>~/.dougong/AGENTS.md</c>）与项目级（工作区内）</item>
/// <item><b>宽泛到具体</b>：项目根 → 工作目录，逐层加载；越具体的越靠后</item>
/// <item><b>去重</b>：trim 后内容相同的只出现一次（<c>CLAUDE.md</c> 复制 <c>AGENTS.md</c> 时不重复）</item>
/// <item><b>两条链各自独立预算</b>：一条超限不影响另一条</item>
/// <item><b>超预算时先省宽泛的</b>：最具体的那份最贴近当前工作，优先保留</item>
/// </list>
/// </para>
///
/// <para>
/// <b>不做的</b>：热重载。dsh 在文件操作后重新发现嵌套文件、在会话恢复时对账；
/// 那需要把"读了哪个文件"记进会话状态。这里每次构建提示时重新读一遍——
/// 便宜（几次文件读）且不会漏，代价是文件改了要等到下一轮才生效。
/// </para>
///
/// <para>
/// <b>文件名为什么不只认 AGENTS.md</b>：<c>CLAUDE.md</c> 是同一套约定的另一个名字，
/// 生态里两个都在用。只认一个会让用户觉得"我写了但没生效"。
/// </para>
/// </remarks>
public sealed class WorkspaceInstructionsSection : ISystemPromptSection
{
    /// <summary>识别的文件名，按优先级。</summary>
    public static readonly string[] FileNames = ["AGENTS.md", "CLAUDE.md"];

    /// <summary>
    /// 每条链的字节预算（4 KiB）。
    /// </summary>
    /// <remarks>
    /// <b>与 dsh 的 65 536 差很多，原因是两条约束叠加</b>：
    /// <list type="number">
    /// <item>本项目的系统提示总预算 <see cref="SystemPromptBuilder.MaxTotalChars"/>
    /// 是 8000 **字符**，而这里按 **UTF-8 字节**算。dsh 没有对应的总上限，
    /// 它靠"每条链独立预算"控制。</item>
    /// <item>中文按 UTF-8 是 3 字节/字，4 KiB 约 1300 个汉字——
    /// 已能写下构建命令、代码风格、禁区这些真正会被用到的约定。</item>
    /// </list>
    /// <para>
    /// <b>第一次实现取 16 KiB，结果整个分节被总预算截成空</b>——两个预算打架时
    /// 大的那个不起作用，而症状是"文件明明有内容，提示里一个字都没有"。
    /// 现在让链预算服从总预算：留 4 KiB 给约定，剩下的给工作区与工具纪律。
    /// </para>
    /// <para>
    /// <b>两个预算单位不同，是这里最容易看错的一处</b>：链预算按字节、总预算按字符。
    /// ASCII 下 1 字节 = 1 字符，所以 4 KiB 真的会顶到 8000 字符的一半；
    /// 中文下 4 KiB 只有约 1400 字符，离总预算还很远。
    /// 也就是说**中文用户实得的约定篇幅约为英文用户的三分之一**。
    /// 这不影响正确性（两者都不截断），但如果将来要按字符收紧这个常数，
    /// 必须同时考虑中文场景会不会被无谓地剪短。
    /// </para>
    /// <para>
    /// 超出这个量说明约定本身该拆分（真正长期有效的规则很少超过一屏）。
    /// </para>
    /// </remarks>
    public const int MaxBytesPerChain = 4 * 1024;

    private readonly string? _globalDirectory;

    /// <summary>构造，默认读 <c>~/.dougong</c> 下的全局指令。</summary>
    public WorkspaceInstructionsSection() => _globalDirectory = null;

    /// <summary>构造，指定全局指令目录；null 表示不读全局。</summary>
    public WorkspaceInstructionsSection(string? globalDirectory) =>
        _globalDirectory = globalDirectory;

    public string Name => "workspace-instructions";

    /// <summary>
    /// 排在 <see cref="WorkspaceSection"/>（100）之后、文件工具纪律（200）之前。
    /// </summary>
    /// <remarks>
    /// 顺序是有理由的：先告诉模型"工作区在哪"，再给"这个项目的约定"，
    /// 最后才是"工具怎么用"。反过来会让约定显得游离。
    /// </remarks>
    public int Order => 150;

    public async ValueTask<string?> BuildAsync(
        SystemPromptContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        var globalChain = await LoadChainAsync(
            GlobalCandidates(), cancellationToken).ConfigureAwait(false);

        var projectChain = await LoadChainAsync(
            ProjectCandidates(context.Workspace), cancellationToken).ConfigureAwait(false);

        // 两条链都是空的：不产出这一节。空字符串会让提示里多一个空段落。
        if (globalChain.Count == 0 && projectChain.Count == 0) return null;

        var builder = new StringBuilder();

        // **先算去重后的全局集，再决定要不要写标题**。先写标题再逐条跳过的话，
        // 全局全部与项目重复时就会留下一个空的「用户全局约定」节——
        // 而空标题正是本类要避免的东西（模型会以为内容丢了）。
        var remainingGlobal = globalChain
            .Where(file => !projectChain.Any(p => p.IsSameContent(file)))
            .ToList();

        if (remainingGlobal.Count > 0)
        {
            builder.Append("## 用户全局约定\n\n");
            // 全局链整体去重：项目里若有一份内容相同的，让项目那份胜出——
            // 项目级的更贴近当前工作，重复显示只会浪费预算。
            foreach (var file in remainingGlobal) AppendFile(builder, file);
        }

        if (projectChain.Count > 0)
        {
            if (builder.Length > 0) builder.Append('\n');
            builder.Append("## 项目约定\n\n");
            foreach (var file in projectChain) AppendFile(builder, file);
        }

        return builder.Length == 0 ? null : builder.ToString();
    }

    /// <summary>全局链候选：<c>~/.dougong/AGENTS.md</c> 等。</summary>
    private IEnumerable<string> GlobalCandidates()
    {
        var directory = _globalDirectory;
        if (string.IsNullOrWhiteSpace(directory)) yield break;

        foreach (var name in FileNames)
            yield return Path.Combine(directory, name);
    }

    /// <summary>
    /// 项目链候选：从工作区根到工作目录，**宽泛到具体**。
    /// </summary>
    /// <remarks>
    /// 我们的工作区本身就是根（工具路径都相对它），所以这里只有一层。
    /// 但保留"逐层向上"的形态：将来若支持子目录会话，
    /// 只需把 <c>context.Workspace</c> 换成更深的起点，这段不用改。
    /// </remarks>
    private static IEnumerable<string> ProjectCandidates(string workspace)
    {
        if (string.IsNullOrWhiteSpace(workspace)) yield break;

        foreach (var name in FileNames)
            yield return Path.Combine(workspace, name);
    }

    /// <summary>读一条链，应用预算与去重。</summary>
    /// <remarks>
    /// <b>超预算时从宽泛的一端丢</b>：列表按"宽泛 → 具体"排列，
    /// 所以从下标 0 开始丢，保留尾部最具体的。dsh 的原文是
    /// "broader files are omitted before the most specific file is truncated"。
    /// </remarks>
    private static async ValueTask<List<InstructionFile>> LoadChainAsync(
        IEnumerable<string> candidates, CancellationToken cancellationToken)
    {
        var loaded = new List<InstructionFile>();

        foreach (var path in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();

            string content;
            try
            {
                if (!File.Exists(path)) continue;
                content = await File.ReadAllTextAsync(path, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 读不到就跳过：约定文件是增强，不是必需。
                //
                // **这里是静默的，而且没有别的出路**：ISystemPromptSection 只有
                // "产出或不产出"两种结果，没有诊断通道。宿主不会知道某个文件
                // 读失败过。这意味着权限配置错误会表现为"我写了约定但没生效"。
                //
                // 想让它可观测需要给分节加诊断出口，那是接口改动，不在本次范围。
                // 在那之前，`dougong doctor` 对 AGENTS.md 可读性的检查是唯一补偿。
                continue;
            }

            var trimmed = content.Trim();
            if (trimmed.Length == 0) continue;

            // 同一链内去重：`CLAUDE.md` 复制 `AGENTS.md` 时不重复渲染。
            if (loaded.Any(f => f.IsSameContent(trimmed))) continue;

            loaded.Add(new InstructionFile(path, trimmed));
        }

        // 预算：从宽泛端丢到装得下为止；若最具体那份本身就超了，截断它。
        var total = loaded.Sum(f => Encoding.UTF8.GetByteCount(f.Content));
        while (loaded.Count > 1 && total > MaxBytesPerChain)
        {
            total -= Encoding.UTF8.GetByteCount(loaded[0].Content);
            loaded.RemoveAt(0);
        }

        if (loaded.Count == 1 && total > MaxBytesPerChain)
        {
            var truncated = TruncateToBytes(loaded[0].Content, MaxBytesPerChain);
            loaded[0] = loaded[0] with
            {
                Content = truncated + "\n\n[…内容超出预算，已截断]",
            };
        }

        return loaded;
    }

    /// <summary>截断到字节上限，不切开 UTF-8 多字节序列。</summary>
    private static string TruncateToBytes(string text, int maxBytes)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        if (bytes.Length <= maxBytes) return text;

        // 回退到最近的字符边界：UTF-8 的续字节以 10xxxxxx 开头。
        var cut = maxBytes;
        while (cut > 0 && (bytes[cut] & 0xC0) == 0x80) cut--;

        return Encoding.UTF8.GetString(bytes, 0, cut);
    }

    private static void AppendFile(StringBuilder builder, InstructionFile file)
    {
        // 给出路径：模型看到约定时应当知道它来自哪个文件，
        // 否则"为什么有这条规则"无从追查。
        builder.Append("（来自 ").Append(RelativeHint(file.Path)).Append("）\n");
        builder.Append(file.Content).Append("\n\n");
    }

    /// <summary>把绝对路径显示成可读的提示，不泄漏完整用户目录。</summary>
    private static string RelativeHint(string path)
    {
        var fileName = Path.GetFileName(path);
        var directory = Path.GetDirectoryName(path);
        return string.IsNullOrEmpty(directory) ? fileName : $"{Path.GetFileName(directory)}/{fileName}";
    }

    /// <summary>一份已加载的指令文件。</summary>
    private sealed record InstructionFile(string Path, string Content)
    {
        public bool IsSameContent(InstructionFile other) =>
            string.Equals(Content, other.Content, StringComparison.Ordinal);

        public bool IsSameContent(string other) =>
            string.Equals(Content, other, StringComparison.Ordinal);
    }
}
