namespace Dougong.Cli.Terminal;

/// <summary>
/// 启动首页。四档，宽度或高度不够就降级。
/// </summary>
/// <remarks>
/// <b>档位判据</b>（照 Hermes 的 <c>HIDE_BELOW</c>/<c>COMPACT_FROM</c> 思路，
/// 按我们的实际尺寸定）：
///
/// <list type="table">
/// <item><term>full</term><description>宽 ≥ 66 且高 ≥ 24：30×18 形象 + 右侧信息区</description></item>
/// <item><term>full-stacked</term><description>宽 46–65 且高 ≥ 24：形象在上、文字在下（不硬挤并排）</description></item>
/// <item><term>compact</term><description>宽 ≥ 46：20×12 形象</description></item>
/// <item><term>ascii</term><description>宽 ≥ 34：纯 ASCII 24×14</description></item>
/// <item><term>text</term><description>其余：单行标题</description></item>
/// </list>
///
/// <para>
/// <b>高度要求不是随手写的</b>：18 行形象 + 3 行信息 + 状态行 + 提示符，
/// 在 24 行终端上刚好放得下。20 行终端放不下，必须降档——
/// 否则底部区域会被挤出屏幕。
/// </para>
///
/// <para>
/// <b>高度未知（0）时按"放得下"处理</b>：那说明终端不报高度，而宽度已经
/// 证明它是真终端。此时给完整形象最多是"挤一点"，降档则是白丢信息。
/// </para>
///
/// <para>
/// <b>Plain 档的输出必须与改造前逐字一致</b>。46 个套件断言的是那些确切文本，
/// 所以这一档不是"精简"，是"保持原样"。
/// </para>
/// </remarks>
public static class Banner
{
    /// <summary>full 档：形象 + 右侧信息区并排所需宽度。</summary>
    /// <remarks>30 列形象 + 4 列间隔 + 信息区最小宽度。实测 66 列时信息区还剩 32 列，够放路径。</remarks>
    public const int FullMinWidth = 66;

    /// <summary>full 档所需最小高度：18 行形象 + 3 行信息 + 状态行 + 提示符，留两行余量。</summary>
    public const int FullMinHeight = 24;

    /// <summary>compact 档：20 列形象所需宽度（含左右留白）。</summary>
    public const int CompactMinWidth = 46;

    /// <summary>ascii 档：24 列纯 ASCII，加上左右留白。</summary>
    public const int AsciiMinWidth = 34;

    /// <summary>
    /// 打印首页。
    /// </summary>
    /// <param name="version">版本号。</param>
    /// <param name="model">模型名。</param>
    /// <param name="protocol">协议名。</param>
    /// <param name="workspace">工作区路径。</param>
    /// <param name="plainLines">
    /// Plain 档要输出的原文行。**由调用方传入改造前的确切文本**，
    /// 这样"兼容"这件事不依赖本类去猜。
    /// </param>
    /// <param name="summary">
    /// 运行时事实（工具 / MCP / 技能）；null 表示**装配还没完成**，
    /// 此时不出面板，退回原来的无框版式。
    /// </param>
    /// <remarks>
    /// <b><paramref name="summary"/> 为 null 是一种正常状态，不是错误</b>：
    /// 装配失败时不该显示"20 个工具已就绪"，那会误导用户去查一个错误的配置。
    /// 调用方在装配成功后才传它。
    /// </remarks>
    public static void Print(
        string version,
        string model,
        string protocol,
        string workspace,
        IReadOnlyList<string> plainLines,
        StartupSummary? summary = null)
    {
        ArgumentNullException.ThrowIfNull(plainLines);

        var capability = Output.Capability;
        var width = capability.Width;
        var height = capability.Height;

        switch (capability.Tier)
        {
            case TerminalTier.Rich when summary is not null:
                // 有运行时清单时优先出面板——那是启动页的主体。
                // **不在这里判高度**：PrintPanel 自己按实际内容行数决定，
                // 放不下就返回 false，由下面几档接手。
                if (PrintPanel(summary, width, height)) break;
                goto case TerminalTier.Rich;

            case TerminalTier.Rich when width >= FullMinWidth && Fits(height):
                PrintFull(version, model, protocol, workspace);
                break;

            case TerminalTier.Rich when width >= CompactMinWidth && Fits(height):
                // 宽够高够但排不下并排：形象在上、文字在下。
                // 使用说明里明确要求"窄窗口将形象单独置于标题上方，不要强行并排"。
                PrintStacked(version, model, protocol, workspace);
                break;

            case TerminalTier.Rich or TerminalTier.Narrow when width >= CompactMinWidth:
                PrintCompact(version, model, protocol, workspace);
                break;

            case TerminalTier.Rich or TerminalTier.Narrow when width >= AsciiMinWidth:
                PrintAscii(version, model, protocol, workspace);
                break;

            case TerminalTier.Rich:
            case TerminalTier.Narrow:
                PrintText(version, model, protocol, workspace);
                break;

            default:
                foreach (var line in plainLines) Output.Line(line);
                break;
        }
    }

    /// <summary>高度是否够放完整形象；未知（0）时按够处理。</summary>
    private static bool Fits(int height) => height <= 0 || height >= FullMinHeight;

    /// <summary>
    /// 面板版式：字标在上，边框盒里左形象、右清单。
    /// </summary>
    /// <returns>false 表示宽度不够画框，调用方退回无框版式。</returns>
    /// <remarks>
    /// <b>按实际内容行数判断，不用固定阈值。</b>面板高度取决于清单长度：
    /// 技能多、MCP 多的时候会长很多。第一版只看 <c>height >= 24</c>，
    /// 于是 24 行终端上输出了 25 行（字标 6 + 面板 19），
    /// 把底部状态行和提示符挤出屏幕——而那个下限正是按"18 行形象"算的，
    /// 换成长清单后就不成立了。
    ///
    /// <para>
    /// <b>高度不足时先丢字标，再丢面板。</b>清单是信息，字标是识别；
    /// 两者冲突时保信息。若连面板都放不下，返回 false 让调用方退回无框版式。
    /// </para>
    /// </remarks>
    private static bool PrintPanel(StartupSummary summary, int width, int height)
    {
        // 先算右栏内容，才知道盒子要几行（左栏要补到同样高）。
        const int borderAndPadding = 2 + Frame.InnerPadding * 2;
        var rightWidth = width - borderAndPadding - Frame.LeftColumnWidth - Frame.ColumnGap;
        if (rightWidth <= 0) return false;

        var right = StartupPanel.RenderRight(summary, rightWidth);
        var left = StartupPanel.RenderLeft(
            summary, YungongArt.Compact, right.Count, Frame.LeftColumnWidth);

        var box = Frame.TryRender(
            $"dougong {summary.Version} · {summary.Model}",
            left, right, width);
        if (box is null) return false;

        // 盒子 + 末尾空行是必须占的；字标是"有位置才画"。
        var required = box.Count + 1;

        // 底部区域要留的行：状态栏 1 + 提示符 1 + 欢迎语 2 + 余量 1。
        const int reservedBottom = 5;
        if (height > 0 && required + reservedBottom > height) return false;

        // 字标只在"画完它之后还剩得下盒子和底部"时才画。
        var wordmarkRows = BigText.TryRender(width, height)?.Count ?? 0;
        var canFitWordmark = height <= 0
            || required + reservedBottom + (wordmarkRows > 0 ? wordmarkRows + 1 : 0) <= height;

        if (canFitWordmark) PrintWordmark(width, height);

        foreach (var line in box) Output.Line(line);

        Output.Line();
        return true;
    }

    /// <summary>
    /// 画顶部字标 `DOUGONG-HARNESS`；放不下时什么也不画。
    /// </summary>
    /// <returns>实际画出的行数（含末尾空行）；0 表示没画。</returns>
    /// <remarks>
    /// <b>字标只在 Rich 档</b>：Narrow 档宽度不足 41 列时两行排法也放不下的情形
    /// 由 <see cref="BigText.TryRender"/> 自己判定，这里不再重复。
    /// Plain 档根本走不到这里——<see cref="Print"/> 的 default 分支直接输出原文。
    /// </remarks>
    private static int PrintWordmark(int width, int height)
    {
        var rows = BigText.TryRender(width, height);
        if (rows is null) return 0;

        for (var i = 0; i < rows.Count; i++)
            Output.Colored(rows[i], BigText.RowColor(i, rows.Count));

        Output.Line();
        return rows.Count + 1;
    }

    /// <summary>full 档：字标在上，形象在左、信息在右。</summary>
    private static void PrintFull(string version, string model, string protocol, string workspace)
    {
        var capability = Output.Capability;
        PrintWordmark(capability.Width, capability.Height);
        Output.Line();
        PrintSideBySide(YungongArt.Full, InfoLines(version, model, protocol, workspace));
        Output.Line();
    }

    /// <summary>形象在上、信息在下（窄窗口的正确排法）。</summary>
    private static void PrintStacked(string version, string model, string protocol, string workspace)
    {
        Output.Line();
        PrintArt(YungongArt.Full);
        Output.Line();
        foreach (var line in InfoLines(version, model, protocol, workspace)) Output.Line(line);
        Output.Line();
    }

    /// <summary>compact 档：20×12 形象 + 下方一行信息。</summary>
    private static void PrintCompact(string version, string model, string protocol, string workspace)
    {
        Output.Line();
        PrintArt(YungongArt.Compact);
        Output.Line();
        Output.Line($"  dougong {version}");
        Output.Line($"  {model} · {protocol}");
        Output.Muted($"  {workspace}");
        Output.Line();
    }

    /// <summary>ascii 档：盲文字形不可用时的兼容版。</summary>
    private static void PrintAscii(string version, string model, string protocol, string workspace)
    {
        Output.Line();
        PrintArt(YungongArt.Ascii);
        Output.Line();
        Output.Line($"  dougong {version}");
        Output.Muted($"  {workspace}");
        Output.Line();
    }

    /// <summary>text 档：单行标题。</summary>
    private static void PrintText(string version, string model, string protocol, string workspace)
    {
        Output.Line();
        Output.Accent($"  dougong {version}");
        Output.Line($"  {model} · {protocol}");
        Output.Muted($"  {workspace}");
        Output.Line();
    }

    /// <summary>
    /// 右侧信息区的内容。
    /// </summary>
    /// <remarks>
    /// <b>四行</b>，照 <c>云拱精修预览.html</c> 的布局。名字与主张各占一行，
    /// 然后是英文 slug 与工作区——用户第一眼要看到"这是什么"与"我在哪"。
    /// </remarks>
    private static IReadOnlyList<string> InfoLines(
        string version, string model, string protocol, string workspace) =>
    [
        string.Empty,
        "DOUGONG",
        "承智成事。",
        "云拱 · 承智之枢",
        "Everything is a Plugin",
        string.Empty,
        $"{version}   {model} · {protocol}",
        workspace,
    ];

    /// <summary>逐行输出字符画，**不补右侧空格**。</summary>
    private static void PrintArt(IReadOnlyList<string> art)
    {
        foreach (var row in art) Output.Accent(row);
    }

    /// <summary>
    /// 左形象、右信息并排。
    /// </summary>
    /// <remarks>
    /// <b>形象宽度取自己那一档的实际最大宽度</b>，不写死 30：
    /// 写死的话，将来换一版形象（比如 26 列）代码不会跟着变，
    /// 而症状是信息区与形象之间多出一段空隙——很难看出是宽度没更新。
    /// </remarks>
    private static void PrintSideBySide(
        IReadOnlyList<string> art, IReadOnlyList<string> info)
    {
        var artWidth = art.Max(r => DisplayWidth.Of(r));
        var gap = 4;

        var rows = Math.Max(art.Count, info.Count);
        for (var i = 0; i < rows; i++)
        {
            var artRow = i < art.Count ? art[i] : string.Empty;
            var infoRow = i < info.Count ? info[i] : string.Empty;

            if (infoRow.Length == 0)
            {
                Output.Accent(artRow);
                continue;
            }

            // 按显示宽度补到 artWidth，让信息区左边缘对齐。
            var pad = artWidth - DisplayWidth.Of(artRow) + gap;
            Output.Accent(artRow + new string(' ', pad) + infoRow);
        }
    }
}
