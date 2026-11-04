using System.Text;

namespace Dougong.Cli.Terminal;

/// <summary>
/// 细边框盒：左右两栏，标题嵌在上边线上。
/// </summary>
/// <remarks>
/// <b>这是启动页的主体</b>，不是装饰边角。Hermes 的启动页重心就是这个盒子，
/// 里面装的是运行时清单（工具 / MCP / 技能）。
///
/// <para>
/// <b>只画半角框线</b>（<c>─│┌┐└┘</c>）：它们在 <see cref="DisplayWidth"/>
/// 里各算 1 格，与 ASCII 一致。**不用双线框**（<c>═║╔</c>）——那在部分
/// 等宽字体里会被渲染成 2 格，整框错位，而症状是"右边框对不齐"，
/// 很难一眼归因。
/// </para>
///
/// <para>
/// <b>内容按显示宽度补白，不按字符数</b>：清单里有中文，按字符数补会让
/// 右边框参差。宽度一律问 <see cref="DisplayWidth"/>。
/// </para>
/// </remarks>
public static class Frame
{
    /// <summary>左边栏（形象）的列数。</summary>
    /// <remarks>
    /// <b>22 = 20 列形象 + 2 列间隔</b>。与 Hermes 的视觉比例相当。
    /// 再窄会把 20 列的 compact 形象挤到折行。
    /// </remarks>
    public const int LeftColumnWidth = 22;

    /// <summary>两栏之间的空格列数。</summary>
    public const int ColumnGap = 2;

    /// <summary>框内左右各留的边距。</summary>
    public const int InnerPadding = 2;

    /// <summary>画一个带标题与左右两栏的盒子。</summary>
    /// <param name="title">嵌在上边线上的标题。</param>
    /// <param name="left">左栏内容（形象）；行数少于右栏时补空行。</param>
    /// <param name="right">右栏内容（清单）。</param>
    /// <param name="width">可用总宽度。</param>
    /// <returns>要输出的行；宽度放不下时返回 null。</returns>
    /// <remarks>
    /// <b>放不下时返回 null 而不是硬挤</b>：调用方据此退回无框竖排。
    /// 硬挤的后果是右边框落到终端外、或者内容被截断——两种都比"没有框"更糟。
    ///
    /// <para>
    /// <b>左栏固定 22 列，右栏吃掉剩余</b>：左栏是形象，宽度由形象决定；
    /// 右栏是清单，越宽越好。反过来（右栏固定）会让窄终端下形象被压坏。
    /// </para>
    /// </remarks>
    public static IReadOnlyList<string>? TryRender(
        string title,
        IReadOnlyList<string> left,
        IReadOnlyList<string> right,
        int width)
    {
        ArgumentNullException.ThrowIfNull(title);
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);

        // 最小宽度：边框 2 + 边距 4 + 左栏 22 + 间隔 2 + 右栏至少 20。
        const int minimum = 2 + InnerPadding * 2 + LeftColumnWidth + ColumnGap + 20;
        if (width < minimum) return null;

        // 边框各占 1 列，边距在内侧。
        var inner = width - 2;                      // 两侧边框之内
        var content = inner - InnerPadding * 2;     // 去掉左右边距
        var rightWidth = content - LeftColumnWidth - ColumnGap;
        if (rightWidth <= 0) return null;

        var lines = new List<string>();

        // ── 上边线（标题嵌在左侧，形如 ┌─ Title ─────┐）──────────
        lines.Add(BuildTopBorder(title, width));

        var rows = Math.Max(left.Count, right.Count);
        for (var i = 0; i < rows; i++)
        {
            var leftText = i < left.Count ? left[i] : string.Empty;
            var rightText = i < right.Count ? right[i] : string.Empty;

            var builder = new StringBuilder(inner);
            builder.Append('│');
            builder.Append(' ', InnerPadding);
            builder.Append(PadTo(leftText, LeftColumnWidth));
            builder.Append(' ', ColumnGap);
            builder.Append(PadTo(rightText, rightWidth));
            builder.Append(' ', InnerPadding);
            builder.Append('│');

            lines.Add(builder.ToString());
        }

        // ── 下边线 ────────────────────────────────────────────
        lines.Add("└" + new string('─', inner) + "┘");

        return lines;
    }

    /// <summary>
    /// 上边线：<c>┌─ 标题 ────────┐</c>。
    /// </summary>
    /// <remarks>
    /// <b>标题两侧各留一个空格</b>：直接贴上框线会让标题与横线黏在一起，
    /// 在等宽字体里尤其难读（横线与文字基线不同）。
    ///
    /// <para>
    /// <b>标题比整个框还宽时丢弃标题，画一条普通边线</b>，不截断标题。
    /// 截断会让"这是哪个版本"这句话失去意义，而溢出去写会顶穿右边框。
    /// 代价是那一行看不到版本号——但它同时出现在面板汇总行里，不丢信息。
    /// </para>
    /// </remarks>
    private static string BuildTopBorder(string title, int width)
    {
        var inner = width - 2;
        var label = $" {title} ";

        if (DisplayWidth.Of(label) >= inner)
            return "┌" + new string('─', inner) + "┐";

        var remaining = inner - DisplayWidth.Of(label) - 1;   // 1 列给左侧那根 ─
        return "┌─" + label + new string('─', Math.Max(0, remaining)) + "┐";
    }

    /// <summary>
    /// 用空格补到指定显示宽度；超宽则截断并加省略号。
    /// </summary>
    /// <remarks>
    /// <b>超宽必须截断而不是溢出</b>：溢出会让这一行写到边框之外，
    /// 表现为右边框被"推开"一格——整块框的右边缘从此参差。
    /// </remarks>
    private static string PadTo(string text, int width)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (width <= 0) return string.Empty;

        var visible = DisplayWidth.Of(text);
        if (visible > width) return Truncate(text, width);

        return visible == width ? text : text + new string(' ', width - visible);
    }

    private static string Truncate(string text, int width)
    {
        var builder = new StringBuilder(text.Length);
        var used = 0;

        foreach (var rune in text.EnumerateRunes())
        {
            var w = DisplayWidth.Of(rune);

            // 留一格给省略号。
            if (used + w > width - 1) break;
            builder.Append(rune.ToString());
            used += w;
        }

        builder.Append('…');
        return builder.ToString();
    }
}
