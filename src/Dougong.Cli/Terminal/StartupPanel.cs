using System.Text;

namespace Dougong.Cli.Terminal;

/// <summary>
/// 启动面板右栏的内容生成。**只拼字符串，不碰终端。**
/// </summary>
/// <remarks>
/// <b>为什么独立于 <see cref="Frame"/></b>：框负责几何（补白、对齐、边框），
/// 这里负责"显示哪些字、怎么分组、截断到几个"。两者分开才能各自测：
/// 框的被测对象是"每行宽度是否一致"，这里的被测对象是"条数与实际数据是否一致"。
///
/// <para>
/// <b>全部内容来自 <see cref="StartupSummary"/>，不在这里编</b>：
/// 面板说 20 个工具而那台机器上注册了 18 个，用户就会去查一个不存在的问题。
/// </para>
/// </remarks>
public static class StartupPanel
{
    /// <summary>每行最多列出的技能名数；超出显示 <c>+N more</c>。</summary>
    /// <remarks>
    /// <b>必须有上限</b>：Hermes 那台机器有 297 个技能，全列出来会占满整屏，
    /// 把下面的欢迎语和输入行挤出去。4 是"看得出有哪些"与"留得下别的内容"的折中。
    /// </remarks>
    public const int SkillsPerGroup = 4;

    /// <summary>最多列出多少组技能来源。</summary>
    public const int MaxSkillGroups = 5;

    /// <summary>每个 MCP server 占一行，最多列几个。</summary>
    public const int MaxMcpServers = 6;

    /// <summary>
    /// 生成右栏的行。
    /// </summary>
    /// <param name="summary">运行时事实。</param>
    /// <param name="width">右栏可用宽度（字符数，显示宽度）。</param>
    /// <remarks>
    /// 顺序照 Hermes：工具 → MCP → 技能 → 汇总。工具在前，
    /// 因为"这个 agent 能做什么"是用户第一眼要确认的事。
    /// </remarks>
    public static IReadOnlyList<string> RenderRight(StartupSummary summary, int width)
    {
        ArgumentNullException.ThrowIfNull(summary);

        var lines = new List<string>();

        AddTools(lines, summary, width);
        AddBlank(lines);
        AddMcp(lines, summary, width);
        AddSkills(lines, summary, width);
        AddBlank(lines);

        // 汇总行：一眼看到"这个会话有什么"。
        // 拿不到工具数时写 `? tools`，不写 `0`——0 是个确定的断言。
        var toolText = summary.ToolsKnown ? summary.Tools.Count.ToString() : "?";
        lines.Add(Fit(
            $"{toolText} tools · {summary.SkillCount} skills · {summary.Version}",
            width));

        return lines;
    }

    /// <summary>
    /// 生成左栏的行：形象 + 底部元信息。
    /// </summary>
    /// <param name="summary">运行时事实。</param>
    /// <param name="art">形象各行（可为空）。</param>
    /// <param name="minRows">右栏需要的最少行数——左栏要补到这么高，边框才对得齐。</param>
    /// <param name="width">左栏宽度。</param>
    /// <remarks>
    /// <b>元信息贴在左栏底部</b>：照 Hermes 的版式（模型 · 供应商 / 路径 / Session）。
    /// 这三条是"出问题时最先要问的"，放在固定位置比散在正文里更容易找到。
    /// </remarks>
    public static IReadOnlyList<string> RenderLeft(
        StartupSummary summary,
        IReadOnlyList<string> art,
        int minRows,
        int width)
    {
        ArgumentNullException.ThrowIfNull(summary);
        ArgumentNullException.ThrowIfNull(art);

        // 模型与供应商分两行，而不是"模型 · 供应商"一行挤在 22 列里。
        // 挤在一行时两个都会挨截断（`qfmodel · Nous Resear…`），
        // 而这两条正是出问题时最先要问的信息——截断一半等于没说。
        var meta = new List<string>
        {
            Fit(summary.Model, width),
            Fit(summary.Provider, width),
            Fit(ShortenPath(summary.Workspace), width),
            Fit($"Session: {summary.SessionId}", width),
        };

        // 形象 + 空行 + 元信息；总高不足 minRows 时在形象与元信息之间补空。
        var needed = art.Count + 1 + meta.Count;
        var filler = Math.Max(0, minRows - needed);

        var lines = new List<string>(art.Count + filler + meta.Count);
        lines.AddRange(art);
        for (var i = 0; i < filler + 1; i++) lines.Add(string.Empty);
        lines.AddRange(meta);

        return lines;
    }

    private static void AddTools(List<string> lines, StartupSummary summary, int width)
    {
        lines.Add(Fit("Available Tools", width));

        if (!summary.ToolsKnown)
        {
            lines.Add("（读不到工具清单——dispatcher 未就绪）");
            return;
        }

        if (summary.Tools.Count == 0)
        {
            lines.Add("（无已注册工具）");
            return;
        }

        // 名字太长时折成多行，但不做 "and N more" 的过度截断：
        // 工具数是几个到二十几个，全列出来通常放得下，而"少了哪几个"
        // 正是排查工具不可用的起点。
        var builder = new StringBuilder();
        foreach (var tool in summary.Tools)
        {
            var piece = builder.Length == 0 ? tool : ", " + tool;
            if (DisplayWidth.Of(builder.ToString() + piece) > width && builder.Length > 0)
            {
                lines.Add(builder.ToString());
                builder.Clear();
            }

            builder.Append(builder.Length == 0 ? tool : ", " + tool);
        }

        if (builder.Length > 0) lines.Add(builder.ToString());
    }

    private static void AddMcp(List<string> lines, StartupSummary summary, int width)
    {
        lines.Add(Fit("MCP Servers", width));

        if (summary.McpServers.Count == 0)
        {
            lines.Add("（未配置）");
            return;
        }

        foreach (var server in summary.McpServers.Take(MaxMcpServers))
            lines.Add(Fit($"{server.Name} ({server.Transport}) — {server.Describe()}", width));

        // **截断要说出来**：静默少列几台会让用户以为其余几台没生效。
        if (summary.McpServers.Count > MaxMcpServers)
            lines.Add($"（还有 {summary.McpServers.Count - MaxMcpServers} 台未列出）");
    }

    private static void AddSkills(List<string> lines, StartupSummary summary, int width)
    {
        lines.Add(Fit("Available Skills", width));

        if (summary.SkillCount == 0)
        {
            lines.Add("（未发现技能）");
            return;
        }

        foreach (var group in summary.SkillSources.Take(MaxSkillGroups))
        {
            var shown = group.Names.Take(SkillsPerGroup).ToList();
            var hidden = group.Names.Count - shown.Count;

            var text = $"{group.Source}: {string.Join(", ", shown)}";
            if (hidden > 0) text += $", +{hidden} more";

            lines.Add(Fit(text, width));
        }

        // 来源组本身被截断时也要说。
        if (summary.SkillSources.Count > MaxSkillGroups)
            lines.Add($"（还有 {summary.SkillSources.Count - MaxSkillGroups} 个来源未列出）");
    }

    private static void AddBlank(List<string> lines) => lines.Add(string.Empty);

    /// <summary>
    /// 把路径压成"末两级目录"，前缀用 <c>…/</c> 表示。
    /// </summary>
    /// <remarks>
    /// <b>为什么不直接截断长路径</b>：22 列的左栏里
    /// <c>C:/work/myProject/doug…</c> 完全看不出是哪个项目——
    /// 从左往右截断恰好丢掉了最有用的一段。用户要确认的是"我在哪个项目里"，
    /// 而那是路径的**末尾**。
    ///
    /// <para>
    /// 只留两级而不是一级：一级（<c>dougong</c>）在有同名目录时会混淆，
    /// 两级（<c>myProject/dougong</c>）通常已经唯一。
    /// </para>
    /// </remarks>
    private static string ShortenPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return path;

        var parts = path.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length <= 2) return path;

        return "…/" + string.Join('/', parts[^2..]);
    }

    /// <summary>按显示宽度截断并加省略号；不补白（补白是 <see cref="Frame"/> 的事）。</summary>
    private static string Fit(string text, int width)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (width <= 0) return string.Empty;
        if (DisplayWidth.Of(text) <= width) return text;

        var builder = new StringBuilder(text.Length);
        var used = 0;

        foreach (var rune in text.EnumerateRunes())
        {
            var w = DisplayWidth.Of(rune);
            if (used + w > width - 1) break;
            builder.Append(rune.ToString());
            used += w;
        }

        builder.Append('…');
        return builder.ToString();
    }
}
