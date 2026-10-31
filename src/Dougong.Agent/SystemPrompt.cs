namespace Dougong.Agent;

/// <summary>
/// 组装系统提示词时可用的事实。
/// </summary>
/// <remarks>
/// 只放"分节需要而运行时才知道"的东西。刻意不把工具对象本身传进去——
/// 分节要的是"有哪些工具"而不是"工具怎么执行"，传对象会让分节有权调用工具，
/// 而系统提示词是纯文本，一旦能调用工具就不再可预估。
/// </remarks>
/// <param name="Workspace">工作区绝对路径。</param>
/// <param name="ToolIds">本次请求声明给模型的工具 id，按注册顺序。</param>
public sealed record SystemPromptContext(
    string Workspace,
    IReadOnlyList<string> ToolIds);

/// <summary>
/// 系统提示词的一个分节。
/// </summary>
/// <remarks>
/// <b>为什么要分节而不是一个字符串</b>：一个字符串只有"整体替换"这一种改法。
/// 加一条工具纪律就得把整段提示词复制到宿主里改，于是 SDK 与宿主各有一份，
/// 下次 SDK 改了前者不同步——这正是"入口能力不一致"的老问题的另一种形态。
/// <para>
/// <b>分节可以缺席</b>：返回 null 表示这一节本次不适用（例如没有 shell 工具时
/// 不该有 shell 纪律）。空字符串与 null 等价，都不产出空行。
/// </para>
/// </remarks>
public interface ISystemPromptSection
{
    /// <summary>分节名，只用于诊断与去重。</summary>
    string Name { get; }

    /// <summary>排序权重，小的在前。</summary>
    /// <remarks>
    /// 用数字而不是"插入位置"：插入位置会让两个分节争同一个下标，
    /// 而谁先谁后应该是显式声明的属性，不是装配顺序的副产物。
    /// </remarks>
    int Order { get; }

    /// <summary>生成分节正文；null 或空白表示本次不适用。</summary>
    ValueTask<string?> BuildAsync(SystemPromptContext context, CancellationToken cancellationToken);
}

/// <summary>
/// 按分节组装系统提示词。
/// </summary>
/// <remarks>
/// <b>总长必须有上限</b>：分节是扩展点，第三方可以加任意多。
/// 没有上限时一个啰嗦的分节会把上下文预算吃掉，表现是"好好的会话突然开始压缩"，
/// 而原因在提示词里，用户看不到。超限时截断并显式说明，不静默。
/// </remarks>
public sealed class SystemPromptBuilder
{
    /// <summary>组装结果的总字符上限。</summary>
    /// <remarks>
    /// 8000 字符按 <see cref="TokenBudget"/> 的估算约 2000 token——
    /// 对一个 200K 窗口是 1%，对最小的 8K 窗口是 25%。
    /// 这个数量级能容纳指令而不喧宾夺主。
    /// </remarks>
    public const int MaxTotalChars = 8_000;

    private readonly List<ISystemPromptSection> _sections = [];

    /// <summary>加入一个分节；同名分节会被替换而不是并列。</summary>
    /// <remarks>
    /// 同名替换而不是追加：两个同名分节同时存在时，模型会看到两段互相矛盾的规则，
    /// 而它无法判断哪个更新。替换让"后来者覆盖"成为确定行为。
    /// </remarks>
    public SystemPromptBuilder Add(ISystemPromptSection section)
    {
        ArgumentNullException.ThrowIfNull(section);
        _sections.RemoveAll(existing =>
            string.Equals(existing.Name, section.Name, StringComparison.Ordinal));
        _sections.Add(section);
        return this;
    }

    /// <summary>按 <see cref="ISystemPromptSection.Order"/> 组装；没有任何分节时返回 null。</summary>
    public async ValueTask<string?> BuildAsync(
        SystemPromptContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        var ordered = _sections.OrderBy(section => section.Order).ToList();
        var parts = new List<string>(ordered.Count);
        var used = 0;
        var truncated = false;

        foreach (var section in ordered)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var text = await section.BuildAsync(context, cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(text)) continue;

            // 分节之间留一个空行，Markdown 才会渲染成独立段落；
            // 紧贴在一起时模型会把两节的句子读成同一段。
            var cost = text.Length + (parts.Count == 0 ? 0 : 2);
            if (used + cost > MaxTotalChars)
            {
                truncated = true;
                break;
            }

            parts.Add(text.Trim());
            used += cost;
        }

        if (parts.Count == 0) return null;

        var prompt = string.Join("\n\n", parts);
        if (truncated)
        {
            // **必须说出来**：静默截断会让模型以为自己看全了规则，
            // 然后按不完整的纪律行动。告诉它"还有没给出的部分"，
            // 它至少会在不确定时先确认。
            prompt += "\n\n[系统提示词超出长度上限，后续分节未包含。]";
        }

        return prompt;
    }
}

/// <summary>
/// 说明当前工作区在哪。
/// </summary>
/// <remarks>
/// <b>这不是废话</b>：模型不知道自己的相对路径解析到哪个目录。
/// 没有这一句时，它会把 <c>src/foo.cs</c> 当成"某个绝对路径的一部分"，
/// 于是先 <c>glob **/foo.cs</c> 全库找一遍，或者干脆在回复里写一个
/// 用户机器上不存在的路径。给出绝对根之后，第一次尝试就落在正确位置。
/// </remarks>
public sealed class WorkspaceSection : ISystemPromptSection
{
    public string Name => "workspace";

    /// <summary>最先出现：后面所有纪律都以此为前提。</summary>
    public int Order => 100;

    public ValueTask<string?> BuildAsync(
        SystemPromptContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        return ValueTask.FromResult<string?>(
            string.IsNullOrWhiteSpace(context.Workspace)
                ? null
                : $"当前工作区：{context.Workspace}。工具里的相对路径都相对这个目录解析。");
    }
}

/// <summary>
/// 文件工具的用法纪律。
/// </summary>
/// <remarks>
/// <b>为什么必须有这一节</b>：工具 schema 只说明"参数是什么"，
/// 不说明"什么时候用哪个"。实测最容易出现的两种坏行为：
/// <list type="number">
/// <item>不知道文件在哪时直接 <c>read_file</c> 猜路径，连错三次才开始搜</item>
/// <item>只改一行却 <c>write_file</c> 重写整个文件——费 token，且容易在无关处改错</item>
/// </list>
/// 这两条都只要一句话就能纠正，而纠正它比让模型自己撞墙便宜得多。
/// <para>
/// <b>哪一节在不在，由注册了哪些工具决定</b>：没有 <c>edit_file</c> 的宿主
/// 不该被告知"改动用 edit_file"——那是给它一条走不通的路。
/// </para>
/// </remarks>
public sealed class FileToolPolicySection : ISystemPromptSection
{
    public string Name => "file-tools";

    public int Order => 200;

    public ValueTask<string?> BuildAsync(
        SystemPromptContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        var ids = context.ToolIds;
        bool Has(string id) => ids.Contains(id, StringComparer.Ordinal);

        var lines = new List<string>();

        // 搜与读的顺序：先定位再读。反过来会在整个仓库上盲读。
        if (Has("glob") || Has("grep"))
        {
            lines.Add("不知道文件在哪时，先用 glob（按文件名）或 grep（按内容）定位，"
                + "不要猜路径直接读——猜错的代价是整轮白跑。");
        }

        if (Has("read_file"))
        {
            lines.Add("读文件只读需要的那一段；整文件读进来会挤占上下文预算。");
        }

        // 这条是本次新增工具的主要动机，必须写出来，否则模型会继续重写全文。
        if (Has("edit_file") && Has("write_file"))
        {
            lines.Add("改已有文件用 edit_file：old_string 必须唯一出现。"
                + "它出现多次时会被拒绝——那是有意的，请带上足够上下文重试，"
                + "不要改成 write_file 重写全文。write_file 只用于新建文件。");
        }

        return ValueTask.FromResult<string?>(
            lines.Count == 0 ? null : string.Join("\n", lines));
    }
}
