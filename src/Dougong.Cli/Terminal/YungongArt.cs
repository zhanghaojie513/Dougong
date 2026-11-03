namespace Dougong.Cli.Terminal;

/// <summary>
/// 云拱字符画：终端欢迎区使用的品牌形象。
/// </summary>
/// <remarks>
/// <b>三档来自品牌素材，不是这里手画的</b>：源文件在
/// <c>artifacts/brand-character/yungong-refined/text/</c>，
/// 由 <c>scripts/gen-yungong-art.py</c> 生成到这个类。
/// 手抄一次就会和源文件漂移，而那类漂移没人会发现——终端图形本来就不好对账。
///
/// <para>
/// <b>每行左侧的字面空格很重要</b>：形象是左对齐的，各行宽度不同（17–30）。
/// <b>不能按行补右侧空格</b>——补齐会把 18 行都变成 30 列，
/// 视觉上右侧多出一堵空白墙（居中排时尤其难看）。原样输出。
/// </para>
///
/// <para>
/// <b>盲文字形一档需要终端字体支持</b>。实测传统 conhost（未设 WT_SESSION）
/// 也能正常显示，所以 <c>ascii</c> 档只在显式降级时用——
/// 判据见 <see cref="Banner"/>。
/// </para>
/// </remarks>
public static class YungongArt
{

    /// <summary>满版：30 列 × 18 行。需要宽 ≥ 66 且高 ≥ 24。</summary>
    public static IReadOnlyList<string> Full => FullArt;

    /// <summary>紧凑版：20 列 × 12 行。</summary>
    public static IReadOnlyList<string> Compact => CompactArt;

    /// <summary>纯 ASCII 兼容版：24 列 × 14 行。盲文字形不可用时用它。</summary>
    public static IReadOnlyList<string> Ascii => AsciiArt;

    private static readonly string[] FullArt =
    [
        @"         ⢠⣤⣄      ⣠⣤⡄",
        @"         ⢸⣿⣿⣤⣤⣤⣤⣤⣤⣿⣿⡇",
        @"         ⠸⠿⠿⠿⠿⠿⠿⠿⠿⠿⠿⠇",
        @"    ⢠⣤⣤                ⣤⣤⡄",
        @"    ⢸⣿⣿⣦⣤⣤⣤⣤⣤⣤⣤⣤⣤⣤⣤⣤⣤⣤⣴⣿⣿⡇",
        @"    ⠸⣿⣿⣿⣿⣿⣿⣿⣿⣿⣿⣿⣿⣿⣿⣿⣿⣿⣿⣿⣿⠇",
        @"        ⢠⣤⣤⣤⣤⣤⣤⣤⣄",
        @"        ⢸⣿⣿⣿⣿⣿⣿⣿⣿",
        @" ⢀⣠⣤⣤⣤  ⢸⣿⣿⣿⡿⠋          ⣤⣤⣤⣄⡀",
        @"⢰⣿⡟⠛⠛⠛  ⢸⣿⣿⡏ ⣠⣾⣿⣿⣿⣿⣿⣿⡇  ⠛⠛⠛⢻⣿⡆",
        @"⠈⠿⣷⣶⣶⣶  ⠸⠿⠿⠇ ⠉⠉⠉⠉⠉⠉⣿⣿⡇  ⣶⣶⣶⣾⠿⠁",
        @"        ⢀⣀⣀⡀       ⣿⣿⡇",
        @"        ⢸⣿⣿⡇ ⣿⣿⣿⣿⣿⣿⣿⣿⡇",
        @"        ⠈⠉⠉⠁ ⠉⠉⠉⠉⠉⠉⠉⠉⠁",
        @"         ⣶⣶⡆      ⢰⣶⣶",
        @"         ⠿⠿⠿⠿⠿⠿⠿⠿⠿⠿⠿⠿",
        @"       ⣤⣤⣤⣤⣤⣤⣤⣤⣤⣤⣤⣤⣤⣤⣤⣤",
        @"       ⠛⠛⠛⠛⠛⠛⠛⠛⠛⠛⠛⠛⠛⠛⠛⠛",
    ];

    private static readonly string[] CompactArt =
    [
        @"      ⣿⣷⣀⣀⣀⣀⣾⣿",
        @"      ⠿⠿⠿⠿⠿⠿⠿⠿",
        @"  ⢰⣶⣆⣀⣀⣀⣀⣀⣀⣀⣀⣀⣀⣰⣶⡆",
        @"  ⠘⢿⣿⣿⣿⣿⣿⣿⣿⣿⣿⣿⣿⣿⡿⠃",
        @"     ⢰⣶⣶⣶⣶⣶⡄",
        @"⢀⣀⣀⣀ ⢸⣿⣿⠟⠛⠛⠁    ⣀⣀⣀⡀",
        @"⣿⠉⠉⠁ ⢸⣿⡇⠠⠾⠿⠿⢿⣿⡇ ⠈⠉⠉⣿",
        @"⠈⠛⠛⠋ ⢈⣉⠁     ⣿⡇ ⠙⠛⠛⠁",
        @"     ⢸⣿⡇⠰⣿⣿⣿⣿⣿⡇",
        @"     ⢀⣤⡀    ⢀⣤⡀",
        @"      ⠿⠷⠶⠶⠶⠶⠾⠿",
        @"    ⢰⣶⣶⣶⣶⣶⣶⣶⣶⣶⣶⡆",
    ];

    private static readonly string[] AsciiArt =
    [
        @"        #      #",
        @"       ##########",
        @"",
        @"    ################",
        @"    ################",
        @"       ######",
        @"       #### #",
        @"#####  ##  ######  #####",
        @" ####  ##      ##  ####",
        @"       ##  ######",
        @"",
        @"       ##      ##",
        @"       ##########",
        @"      ############",
    ];

}
