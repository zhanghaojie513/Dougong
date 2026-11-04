namespace Dougong.Cli.Terminal;

/// <summary>
/// 语义色。**颜色在这里是信息编码，不是装饰**——用户扫一眼就知道哪行是错误。
/// </summary>
/// <remarks>
/// <b>默认主题是黑白灰，不是金色。</b>这一点经过一次纠正：
/// <para>
/// 早先的 <c>assets/brand/README.md</c> 写"终端里用金色做强调色，与主标志的
/// 黑白刻意无关"，于是 accent 用了 <c>#C9A227</c>（暗黄）。而后来新增的
/// <c>artifacts/brand-character/yungong-refined/设计理念.md</c> §后续视觉约束
/// 明确写：**"终端默认保持黑白灰，朱构可作为已有主题的延展；
/// 不因神话参照引入黄金配色。"**
/// </para>
/// <para>
/// 两份文档冲突时以**后者**为准——它的时间戳更晚（2026-10-30 vs 10-29），
/// 是后加的约束。按项目一贯的判据，"后来明确写下的限制"覆盖"更早的默认做法"。
/// </para>
///
/// <para>
/// <b>语义色不动</b>：<see cref="Ok"/> / <see cref="Warn"/> / <see cref="Error"/>
/// 是信息编码，不是装饰。改了会让"红=错误"这条约定失效——
/// 那是全终端世界的共识，不该为了配色统一去动它。
/// </para>
///
/// <para>
/// <b>黑白灰之后 accent 还剩什么用</b>：只剩"亮/暗"一档区分。
/// 所以它只能用在**小面积**（spinner 一帧、提示符、banner 少量字形），
/// 正文全走默认前景色。用亮白去刷大面积文本会让整屏发灰、失去层次。
/// </para>
///
/// <para>
/// <b>Plain 档下所有颜色被忽略</b>。这里只定义"是什么色"，
/// "要不要上色"由 <see cref="Output"/> 按终端档次决定。
/// </para>
/// </remarks>
public static class Palette
{
    /// <summary>主色：版本号、模型名、框线。</summary>
    /// <remarks>
    /// <b>黑白灰主题下它是近白</b>，只比正文亮一档——用在少量字形上做强调，
    /// 不刷大面积。朱构主题下换成朱红。
    /// </remarks>
    public static string Accent => Theme.Effective.Accent;

    /// <summary>次要：路径、耗时、分隔线。</summary>
    public static string Muted => Theme.Effective.Muted;

    /// <summary>成功：✓ 工具完成、检查通过。</summary>
    /// <remarks>
    /// <b>不随主题变</b>：语义色是信息编码。"成功=绿"在任何配色下都成立。
    /// </remarks>
    public const string Ok = "#4CAF50";

    /// <summary>警告：⚠ 降级、跳过。</summary>
    /// <remarks>不随主题变，理由同 <see cref="Ok"/>。</remarks>
    public const string Warn = "#FF8C00";

    /// <summary>失败：✗ 工具失败、检查未通过。</summary>
    /// <remarks>不随主题变，理由同 <see cref="Ok"/>。</remarks>
    public const string Error = "#EF5350";

    /// <summary>提示符与用户输入的回显。</summary>
    public static string Prompt => Theme.Effective.Accent;

    /// <summary>字标渐变的上端（亮）。</summary>
    /// <remarks>
    /// <b>字标是"默认黑白灰"这条规则的唯一例外，且是有意的。</b>
    /// 默认主题下正文与强调都是灰阶，而启动页顶部那个 5 行实心字标
    /// 用灰阶会糊成一堵墙，看不出是个字标。所以给它一组暖色渐变
    /// （亮橙 → 朱红），面积局限在启动页顶部，用户滚走就看不见。
    ///
    /// <para>
    /// 这**不是**把默认主题改回暖色：<see cref="Accent"/> 仍是近白，
    /// 正文仍全走默认前景色。要整体暖色请用 <c>DOUGONG_THEME=zhugou</c>。
    /// </para>
    /// </remarks>
    public static string WordmarkTop => Theme.Effective.WordmarkTop ?? "#FFB347";

    /// <summary>字标渐变的下端（暖）。</summary>
    public static string WordmarkBottom => Theme.Effective.WordmarkBottom ?? "#C0392B";

    /// <summary>
    /// 两个 <c>#RRGGBB</c> 之间按比例线性插值。
    /// </summary>
    /// <param name="from">起点色（<paramref name="t"/> = 0）。</param>
    /// <param name="to">终点色（<paramref name="t"/> = 1）。</param>
    /// <param name="t">比例，会被夹到 [0, 1]。</param>
    /// <exception cref="FormatException">颜色不是 <c>#RRGGBB</c> 形式。</exception>
    /// <remarks>
    /// <b>非法色值抛异常而不是退回黑色</b>：颜色来自主题表，写错了是编码错误；
    /// 悄悄画成黑色会把"字标不见了"变成一个难查的现象题。
    /// </remarks>
    public static string Lerp(string from, string to, double t)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(from);
        ArgumentException.ThrowIfNullOrWhiteSpace(to);

        t = Math.Clamp(t, 0, 1);
        var (ar, ag, ab) = ParseRgb(from);
        var (br, bg, bb) = ParseRgb(to);

        static int Mix(int x, int y, double k) => (int)Math.Round(x + (y - x) * k);

        return $"#{Mix(ar, br, t):X2}{Mix(ag, bg, t):X2}{Mix(ab, bb, t):X2}";
    }

    private static (int R, int G, int B) ParseRgb(string hex)
    {
        var text = hex.StartsWith('#') ? hex[1..] : hex;
        if (text.Length != 6
            || !int.TryParse(text, System.Globalization.NumberStyles.HexNumber,
                System.Globalization.CultureInfo.InvariantCulture, out var value))
            throw new FormatException($"颜色必须是 #RRGGBB：{hex}");

        return ((value >> 16) & 0xFF, (value >> 8) & 0xFF, value & 0xFF);
    }
}

/// <summary>
/// 一组配色。**只有强调类的两个是主题相关的**，语义色不在这里。
/// </summary>
/// <param name="Accent">主色。</param>
/// <param name="Muted">次要色。</param>
/// <param name="WordmarkTop">
/// 字标渐变上端。null 表示用 <see cref="Palette"/> 的默认端点。
/// </param>
/// <param name="WordmarkBottom">字标渐变下端。null 同上。</param>
public sealed record PaletteSet(
    string Accent,
    string Muted,
    string? WordmarkTop = null,
    string? WordmarkBottom = null);

/// <summary>
/// 终端主题：默认黑白灰，可选朱构。
/// </summary>
/// <remarks>
/// <b>为什么只有两个值</b>：配色方案一旦多了就要维护一张表、还要考虑
/// 每个新颜色的语义。两个足够覆盖"朴素"与"品牌"两种意图。
///
/// <para>
/// <b>从环境变量读，且冻结一次</b>：与其它安全/呈现开关一致
/// （见 <c>SecuritySwitches</c>）——运行期改环境变量不该影响已经渲染的内容，
/// 而那会让同一屏出现两种配色。
/// </para>
///
/// <para>
/// <b>识别不到的取值退回默认</b>，不报错：主题拼错不该阻止程序启动。
/// 但要在 <c>/status</c> 里如实显示生效的是哪一个。
/// </para>
/// </remarks>
public static class Theme
{
    /// <summary>环境变量名。</summary>
    public const string VariableName = "DOUGONG_THEME";

    /// <summary>默认主题：黑白灰。</summary>
    /// <remarks>
    /// <b>它是默认而不是"其中一种"</b>：默认值决定了"什么都没配的人看到什么"，
    /// 而那必须是设计理念里写下的那个。
    /// <para>
    /// 字标渐变端点留 null，走 <see cref="Palette"/> 的默认暖色——
    /// 那是唯一一处允许暖色的地方，理由见 <see cref="Palette.WordmarkTop"/>。
    /// </para>
    /// </remarks>
    public static PaletteSet Mono { get; } = new(
        Accent: "#E8E8E8",   // 近白：比正文亮一档，且不是纯白（纯白在多数终端上就是正文色）
        Muted: "#7A7A7A");

    /// <summary>朱构主题：品牌朱红。</summary>
    /// <remarks>
    /// 取自设计理念里"朱构"的字面色——朱红，不是金黄。
    /// <b>它不是默认</b>：需要显式设 <c>DOUGONG_THEME=zhugou</c>。
    /// <para>
    /// 字标跟着改成朱红系渐变：主题换了而字标还是橙色会很突兀。
    /// </para>
    /// </remarks>
    public static PaletteSet Zhugou { get; } = new(
        Accent: "#C0392B",
        Muted: "#7A7A7A",
        WordmarkTop: "#E74C3C",
        WordmarkBottom: "#8E1B10");

    /// <summary>当前生效的主题。进程内冻结一次。</summary>
    public static PaletteSet Current => Frozen.Value;

    /// <summary>当前生效的主题名（供 <c>/status</c> 显示）。</summary>
    public static string CurrentName =>
        ReferenceEquals(Current, Zhugou) ? "zhugou" : "mono";

    private static readonly Lazy<PaletteSet> Frozen = new(Detect);

    private static PaletteSet Detect() =>
        string.Equals(Environment.GetEnvironmentVariable(VariableName), "zhugou",
            StringComparison.OrdinalIgnoreCase)
            ? Zhugou
            : Mono;

    /// <summary>覆盖主题。**仅供测试**——真实运行不要调。</summary>
    /// <remarks>
    /// <b>为什么是 public 而不是 internal</b>：检查程序集在另一个项目里，
    /// 而 CLI 没有开 <c>InternalsVisibleTo</c>。为了测一个分支去改项目的
    /// 可见性策略不值得——名字里的 <c>OverrideForTesting</c> 已经把意图写清。
    ///
    /// <para>
    /// 存在的理由：主题分支必须能测，而进程环境变量改不了已经求值的
    /// <see cref="Lazy{T}"/>.
    /// </para>
    /// </remarks>
    /// <param name="palette">要覆盖成的配色；null 表示恢复按环境变量探测。</param>
    public static void OverrideForTesting(PaletteSet? palette) => _override = palette;

    private static PaletteSet? _override;

    /// <summary>取当前配色（测试覆盖优先）。</summary>
    public static PaletteSet Effective => _override ?? Current;
}
