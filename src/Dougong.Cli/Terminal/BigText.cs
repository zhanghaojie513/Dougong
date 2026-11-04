using System.Text;

namespace Dougong.Cli.Terminal;

/// <summary>
/// 半角块字符大字标。启动页顶部的 <c>DOUGONG-HARNESS</c>。
/// </summary>
/// <remarks>
/// <b>为什么自己画而不用 figlet</b>：只需要 11 个字形，而外部字体库会带来
/// 一套版本、字符集与对齐规则的差异。自己维护一张 5×5 表，宽度可算、
/// 换行可控、没有依赖。
///
/// <para>
/// <b>只在 <see cref="TerminalTier.Rich"/> 档画</b>：Plain 档必须与改造前
/// 逐字一致，多一行字就是破坏——46 个套件钉着那些确切文本。
/// </para>
///
/// <para>
/// <b>两档排法，不是等比缩放</b>：
/// 宽 ≥ <c>89</c> 单行（15 字 × 5 + 14 间隔），否则折成两行
/// <c>DOUGONG</c> / <c>HARNESS</c>（各 7 字，41 列）。
/// <b>不做横向压扁</b>——5 列是"能分清 D 与 O"的下界；
/// 压到 4 列时 D 的右上缺口消失，D 和 O 变成同一个字。
/// </para>
/// </remarks>
public static class BigText
{
    /// <summary>每个字形的列数。5 是区分 D/O、E/S 的下界，见类注释。</summary>
    public const int GlyphWidth = 5;

    /// <summary>字形之间的空格列数。</summary>
    public const int Gap = 1;

    /// <summary>字形行数。</summary>
    public const int GlyphHeight = 5;

    /// <summary>字标正文。</summary>
    public const string Wordmark = "DOUGONG-HARNESS";

    /// <summary>折行时第一段的文字。</summary>
    public const string FirstHalf = "DOUGONG";

    /// <summary>折行时第二段的文字。</summary>
    public const string SecondHalf = "HARNESS";

    /// <summary>单行排法所需宽度（89）。</summary>
    public static int SingleLineWidth => Measure(Wordmark);

    /// <summary>折行排法每行的宽度（41）。</summary>
    public static int StackedLineWidth => Measure(FirstHalf);

    /// <summary>
    /// 计算一段文本在字标中的显示宽度。全部是半角块字符，
    /// 所以字符数即列数（与 <see cref="DisplayWidth"/> 的结论一致）。
    /// </summary>
    public static int Measure(string text) =>
        string.IsNullOrEmpty(text) ? 0 : text.Length * GlyphWidth + (text.Length - 1) * Gap;

    /// <summary>
    /// 按可用宽高选排法。放不下时返回 null（调用方退回云拱形象）。
    /// </summary>
    /// <param name="width">终端宽度。</param>
    /// <param name="height">
    /// 字标**可用的高度**。<b>负数表示"读不到"，按"够"处理</b>——
    /// 宽度已经证明是真终端，因为读不到高度就降级是白丢信息。
    /// <para>
    /// <b>必须用负数而不是 0 表示"读不到"</b>：0 是"一行都放不下"这个
    /// 确定结论。两者混用会造成真实缺陷——调用方算出剩余高度恰好为 0 时，
    /// 字标反而被"按够处理"画了出来，整屏超出 11 行。实测过（2026-11-05）。
    /// </para>
    /// </param>
    /// <param name="reservedRows">
    /// 字标之外必须留给下方内容的行数。
    /// </param>
    public static IReadOnlyList<string>? TryRender(
        int width, int height, int reservedRows = 0)
    {
        var unknown = height < 0;

        // **两个排法都要把"末尾那个空行"算进去。**
        // PrintWordmark 画完字标后会补一个 Output.Line()，那一行同样占屏幕。
        // 单行排法 5 + 1 = 6；折行排法 5 + 1(中间空行) + 5 + 1(末尾空行) = 12。
        //
        // 第一版把折行写成 11（漏了末尾空行），于是 available=11 时字标被画出，
        // 实际占 12 行——溢出 1 行。单行那条恰好对，所以这个错法不容易被发现。
        var singleHeight = GlyphHeight + 1;
        if (width >= SingleLineWidth
            && (unknown || height >= singleHeight + reservedRows))
            return Render(Wordmark);

        var stackedHeight = GlyphHeight * 2 + 2;
        if (width >= StackedLineWidth
            && (unknown || height >= stackedHeight + reservedRows))
            return [.. Render(FirstHalf), string.Empty, .. Render(SecondHalf)];

        return null;
    }

    /// <summary>渲染一段文本为字标行（不含上色）。</summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// 出现字形表里没有的字符。
    /// </exception>
    /// <remarks>
    /// <b>未知字符直接抛，不画成空白</b>：字标是品牌件，静默画出一个缺字的词
    /// 比构建失败更难发现——它只在真机上才看得出来。
    /// </remarks>
    public static IReadOnlyList<string> Render(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length == 0) return [];

        var glyphs = new List<string[]>(text.Length);
        foreach (var ch in text)
        {
            var key = char.ToUpperInvariant(ch);
            if (!Glyphs.TryGetValue(key, out var glyph))
                throw new ArgumentOutOfRangeException(
                    nameof(text), ch, "字标字形表里没有这个字符。");
            glyphs.Add(glyph);
        }

        var rows = new List<string>(GlyphHeight);
        var builder = new StringBuilder(text.Length * (GlyphWidth + Gap));

        for (var row = 0; row < GlyphHeight; row++)
        {
            builder.Clear();
            for (var i = 0; i < glyphs.Count; i++)
            {
                if (i > 0) builder.Append(' ', Gap);
                builder.Append(glyphs[i][row]);
            }

            // 尾部空格去掉：块字符本身已是实心，"右侧留白"没有视觉意义，
            // 而留着会让宽度断言与真实观感不一致。
            rows.Add(builder.ToString().TrimEnd());
        }

        return rows;
    }

    /// <summary>
    /// 第 <paramref name="index"/> 行（共 <paramref name="count"/> 行）的渐变色。
    /// </summary>
    /// <remarks>
    /// <b>渐变在这里有信息作用，不是花边</b>：整块 5 行实心字在深色终端上
    /// 会糊成一片，上亮下暖给出纵向层次，看得出这是"一个字标"而不是一堵墙。
    /// 端点色定义在 <see cref="Palette"/>，这里只做线性插值。
    /// </remarks>
    public static string RowColor(int index, int count) =>
        Palette.Lerp(Palette.WordmarkTop, Palette.WordmarkBottom,
            count <= 1 ? 0 : (double)index / (count - 1));

    /// <summary>字形表里有哪些字符（供检查对账）。</summary>
    public static IReadOnlyCollection<char> AvailableGlyphs => Glyphs.Keys;

    /// <summary>取一个字形（供检查验宽度）。</summary>
    public static IReadOnlyList<string> GlyphOf(char ch) => Glyphs[char.ToUpperInvariant(ch)];

    /// <summary>
    /// 5 列 × 5 行的半角块字形表。只收录 <see cref="Wordmark"/> 用到的字符。
    /// </summary>
    /// <remarks>
    /// 每个字形必须**恰好 5 个字符宽、5 行**，由
    /// <c>big-text-glyphs-are-uniform</c> 钉住。
    /// </remarks>
    private static readonly Dictionary<char, string[]> Glyphs = new()
    {
        ['D'] =
        [
            "████ ",
            "█   █",
            "█   █",
            "█   █",
            "████ ",
        ],
        ['O'] =
        [
            "█████",
            "█   █",
            "█   █",
            "█   █",
            "█████",
        ],
        ['U'] =
        [
            "█   █",
            "█   █",
            "█   █",
            "█   █",
            "█████",
        ],
        ['G'] =
        [
            "█████",
            "█    ",
            "█  ██",
            "█   █",
            "█████",
        ],
        ['N'] =
        [
            "█   █",
            "██  █",
            "█ █ █",
            "█  ██",
            "█   █",
        ],
        ['H'] =
        [
            "█   █",
            "█   █",
            "█████",
            "█   █",
            "█   █",
        ],
        ['A'] =
        [
            "█████",
            "█   █",
            "█████",
            "█   █",
            "█   █",
        ],
        ['R'] =
        [
            "████ ",
            "█   █",
            "████ ",
            "█  █ ",
            "█   █",
        ],
        ['E'] =
        [
            "█████",
            "█    ",
            "████ ",
            "█    ",
            "█████",
        ],
        ['S'] =
        [
            "█████",
            "█    ",
            "█████",
            "    █",
            "█████",
        ],
        ['-'] =
        [
            "     ",
            "     ",
            "█████",
            "     ",
            "     ",
        ],
    };
}
