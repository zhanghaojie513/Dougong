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

            case TerminalTier.Rich when width >= FullMinWidth && FitsIn(width, height, FullMinHeight):
                PrintFull(version, model, protocol, workspace);
                break;

            case TerminalTier.Rich when width >= CompactMinWidth && FitsIn(width, height, StackedMinHeight):
                // 宽够高够但排不下并排：形象在上、文字在下。
                // 使用说明里明确要求"窄窗口将形象单独置于标题上方，不要强行并排"。
                PrintStacked(version, model, protocol, workspace);
                break;

            case TerminalTier.Rich or TerminalTier.Narrow
                when width >= CompactMinWidth && FitsIn(width, height, CompactMinHeight):
                PrintCompact(version, model, protocol, workspace);
                break;

            case TerminalTier.Rich or TerminalTier.Narrow
                when width >= AsciiMinWidth && FitsIn(width, height, AsciiMinHeight):
                PrintAscii(version, model, protocol, workspace);
                break;

            case TerminalTier.Rich:
            case TerminalTier.Narrow:
                // 连 ascii 都放不下：退回单行。它只有 4 行，任何能跑起来的终端都放得下。
                PrintText(version, model, protocol, workspace);
                break;

            default:
                foreach (var line in plainLines) Output.Line(line);
                break;
        }
    }

    /// <summary>
    /// 该档在给定高度下能否放下（**含字标与底部**）。
    /// </summary>
    /// <param name="width">终端宽度（决定字标几行）。</param>
    /// <param name="height">终端高度；0 表示读不到，一律放行。</param>
    /// <param name="required">该档正文占的行数（不含底部）。</param>
    /// <remarks>
    /// <b>字标与正文必须合起来判，不能各判各的。</b>分开判的后果实测过：
    /// 双方都说"我放得下"，加起来却超——120×28 上字标 5 行 + full 正文 26 行
    /// = 31 行，溢出 3 行。
    ///
    /// <para>
    /// <b>但字标是"可丢"的</b>：正文放得下时优先保正文，字标在剩余高度不够时
    /// 自动不画（<see cref="PrintWordmark"/> 里判）。所以这里的判据是
    /// "正文 + 底部" 与 "正文 + 底部 + 字标" 两档：
    /// 前者成立才会选中该档，后者决定字标画不画。
    /// </para>
    ///
    /// <para>
    /// <b>高度未知（0）时一律放行</b>：那说明终端不报高度，而宽度已经证明
    /// 它是真终端；因为读不到高度就降级是白丢信息。
    /// </para>
    /// </remarks>
    private static bool FitsIn(int width, int height, int required)
    {
        if (height <= 0) return true;

        // 正文 + 底部都放得下，才选中该档。字标不参与——它是可丢的装饰。
        return height >= required;
    }

    /// <summary>
    /// 各档占用的总行数（**含空行**，实测值）+ 底部 2 行。
    /// </summary>
    /// <remarks>
    /// <b>这些数字是从实测反推的，不是从形状推算的。</b>推算过两次都错：
    /// <list type="number">
    /// <item>按"18 行形象 + 3 行信息"算出 25，实测 26——信息区实际 8 行（含 2 个空行）。</item>
    /// <item>按"1 + max(形象, 信息) + 1"算出 22，实测仍 26——并排不是取高，
    /// 而是逐行拼接后各个 `<c>Output.Line()</c>` 都会产生一行。</item>
    /// </list>
    /// 两次推算都偏小，所以 24 行终端上照样溢出。现在改用实测值，
    /// 并由 <c>startup-never-exceeds-terminal-height</c> 在多组宽高上钉住。
    ///
    /// <para>
    /// <b>改动任一档的版式（加空行、改形象行数）后必须重新实测并更新这里</b>——
    /// 否则又回到"推算与实际不符"。检查会红，那是提醒不是噪音。
    /// </para>
    /// </remarks>
    private const int FullMinHeight = 26 + BottomRows;

    /// <summary>stacked 档：实测 32 行 + 底部。</summary>
    private const int StackedMinHeight = 32 + BottomRows;

    /// <summary>compact 档：实测 29 行 + 底部。</summary>
    private const int CompactMinHeight = 29 + BottomRows;

    /// <summary>ascii 档：实测 19 行 + 底部。</summary>
    private const int AsciiMinHeight = 19 + BottomRows;

    /// <summary>底部区域要占的行：状态栏 1 + 提示符 1。</summary>
    private const int BottomRows = 2;

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

        // 底部区域要留的行：状态栏 1 + 提示符 1 + 欢迎语 2。
        const int reservedBottom = 4;
        if (height > 0 && required + reservedBottom > height) return false;

        // 字标只在"画完它之后还剩得下盒子和底部"时才画——
        // PrintWordmark 自己会按剩余高度判，这里只传要留的行数。
        PrintWordmark(width, height, required + reservedBottom);

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
    ///
    /// <para>
    /// <b><paramref name="reservedBelow"/> 是字标之下还必须留的行数。</b>
    /// 字标是装饰：内容放得下、字标放不下时，**丢字标而不是丢内容**。
    /// 第一版没这个参数，于是窄终端上要么字标把内容挤出去、
    /// 要么（判据反过来后）字标撑得整屏不画——两个方向都错。
    /// </para>
    /// </remarks>
    private static int PrintWordmark(int width, int height, int reservedBelow)
    {
        // **读不到高度用 -1 传下去**，不能用 0：0 是"一行都放不下"这个确定结论。
        // 混用会造成真实缺陷——实测 66×28 上剩余高度算出 0，
        // 而 BigText 把 0 当"读不到"处理，于是字标照样画了 11 行，
        // 整屏超出 6 行。
        var available = height > 0 ? height - reservedBelow : -1;
        var rows = BigText.TryRender(width, available, reservedRows: 0);
        if (rows is null) return 0;

        for (var i = 0; i < rows.Count; i++)
            Output.Colored(rows[i], BigText.RowColor(i, rows.Count));

        Output.Line();
        return rows.Count + 1;
    }

    /// <summary>
    /// 按终端宽度截断一行，**防止终端自己折行**。
    /// </summary>
    /// <remarks>
    /// <b>窄终端上折行会让行数翻倍</b>，而 banner 的高度判据是按"一行一行"算的。
    /// 实测：66 列终端上信息区里那条长路径被 Spectre 折成两行，
    /// 于是 full 档从 26 行变成 32 行，把底部提示符挤出屏幕。
    ///
    /// <para>
    /// 截断不加省略号：被切掉的是路径尾部，加 <c>…</c> 反而让人以为画的是完整路径。
    /// 这里的目标只是"别折行"。
    /// </para>
    /// </remarks>
    private static string Clip(string text)
    {
        var width = Output.Capability.Width;
        if (width <= 0) return text;

        // 留最后一列：写满最右列同样会触发折行。
        var limit = width - 1;
        if (DisplayWidth.Of(text) <= limit) return text;

        var builder = new System.Text.StringBuilder(text.Length);
        var used = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            var w = DisplayWidth.Of(rune);
            if (used + w > limit) break;
            builder.Append(rune.ToString());
            used += w;
        }
        return builder.ToString();
    }

    /// <summary>full 档：字标在上，形象在左、信息在右。</summary>
    private static void PrintFull(string version, string model, string protocol, string workspace)
    {
        var capability = Output.Capability;
        // 字标之下是 full 档正文（含首尾空行）+ 底部两行。
        PrintWordmark(capability.Width, capability.Height, FullMinHeight);
        Output.Line();
        PrintSideBySide(YungongArt.Full, InfoLines(version, model, protocol, workspace));
        Output.Line();
    }

    /// <summary>形象在上、信息在下（窄窗口的正确排法）。</summary>
    private static void PrintStacked(string version, string model, string protocol, string workspace)
    {
        var capability = Output.Capability;
        PrintWordmark(capability.Width, capability.Height, StackedMinHeight);
        Output.Line();
        PrintArt(YungongArt.Full);
        Output.Line();
        foreach (var line in InfoLines(version, model, protocol, workspace))
            Output.Line(Clip(line));
        Output.Line();
    }

    /// <summary>compact 档：20×12 形象 + 下方一行信息。</summary>
    private static void PrintCompact(string version, string model, string protocol, string workspace)
    {
        var capability = Output.Capability;
        PrintWordmark(capability.Width, capability.Height, CompactMinHeight);
        Output.Line();
        PrintArt(YungongArt.Compact);
        Output.Line();
        Output.Line(Clip($"  dougong {version}"));
        Output.Line(Clip($"  {model} · {protocol}"));
        Output.Muted(Clip($"  {workspace}"));
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
    ///
    /// <para>
    /// <b>拼接后必须按终端宽度截断。</b>不截断时，窄终端（66–80 列）上
    /// 信息区会被 Spectre 折行——于是 full 档从 26 行变成 32 行，
    /// 把底部状态栏和提示符挤出屏幕。而症状是"提示符不见了"，
    /// 看起来像 <see cref="LiveRegion"/> 的问题。实测（2026-11-05）：
    /// 宽度 66/68/70/80 全部输出 32 行，90 以上才是 26 行。
    /// </para>
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
            Output.Accent(Clip(artRow + new string(' ', pad) + infoRow));
        }
    }
}
