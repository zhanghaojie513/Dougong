using System.Text;

namespace Dougong.Cli.Terminal;

/// <summary>
/// 终端的呈现能力档次。**这决定同一份输出怎么渲染，不决定输出什么内容。**
/// </summary>
public enum TerminalTier
{
    /// <summary>富终端：色彩 + 边框 + 可用固定栏。</summary>
    Rich,

    /// <summary>窄终端：可着色，但宽度放不下固定栏与边框。</summary>
    Narrow,

    /// <summary>
    /// 管道/重定向：**纯文本，与改造前逐字一致**。
    /// 44 个自检套件全部走这一档，所以它是整个改造的兼容底线。
    /// </summary>
    Plain,
}

/// <summary>
/// 终端能力探测。只在启动时算一次。
/// </summary>
/// <remarks>
/// <b>判据的可靠性顺序，来自实测（2026-10-28）</b>：
/// <list type="number">
/// <item><c>Console.IsOutputRedirected</c> —— 唯一可信。实测本机
/// <c>TERM=xterm-256color</c> 却 <c>IsOutputRedirected=True</c>，
/// 环境变量会骗人。</item>
/// <item><c>Console.WindowWidth</c> —— 重定向时读它抛 <c>IOException</c>，
/// 所以必须先确认没被重定向再读。</item>
/// </list>
/// <para>
/// <b>为什么不问 Spectre</b>：<c>AnsiConsole.Profile.Capabilities.Interactive</c>
/// 也能给出结论，但它依赖 Spectre 的探测时机（首次访问时初始化）。
/// 这里用 BCL 自带的 <c>IsOutputRedirected</c> 更直接，也少一层耦合。
/// 两者结论一致——实测都是 false。
/// </para>
/// </remarks>
/// <param name="Tier">档位。</param>
/// <param name="Width">终端宽度（列）。重定向时为 0。</param>
/// <param name="IsRedirected">输出是否被重定向。</param>
/// <param name="Height">
/// 终端高度（行）。<b>0 表示读不到，这是一个有效状态</b>：某些终端
/// （旧 ConEmu、某些 SSH 客户端）读不到高度。那时不做底部锚定，
/// 退回"画在光标下方"。读错高度只影响美观，不影响正确性。
/// </param>
public readonly record struct TerminalCapability(
    TerminalTier Tier,
    int Width,
    bool IsRedirected,
    int Height = 0)
{
    /// <summary>窄终端阈值：低于它放不下固定栏与边框。</summary>
    private const int NarrowThreshold = 80;

    /// <summary>极窄阈值：低于它连着色都可能错位。</summary>
    private const int MinimalThreshold = 60;

    /// <summary>是否可以做底部锚定（需要 Rich 且有可用的高度）。</summary>
    public bool CanAnchorBottom =>
        Tier is TerminalTier.Rich && !IsRedirected && Height > 0;

    /// <summary>是否值得启用装饰（颜色、符号、边框）。</summary>
    public bool SupportsDecoration => Tier is TerminalTier.Rich or TerminalTier.Narrow;

    /// <summary>是否能用 CJK 与框线字符。Plain 档不用——管道里会变乱码。</summary>
    public bool SupportsUnicode => Tier is TerminalTier.Rich or TerminalTier.Narrow;

    /// <summary>探测当前终端。任何异常都退回 <see cref="TerminalTier.Plain"/>。</summary>
    public static TerminalCapability Detect() => Detect(
        isRedirected: Console.IsOutputRedirected,
        readWidth: static () => Console.WindowWidth,
        readHeight: static () => Console.WindowHeight);

    /// <summary>
    /// 可注入的探测核心。<paramref name="readWidth"/> 在重定向时不会被调用。
    /// </summary>
    /// <remarks>
    /// 拆出来是为了能测：真实终端宽度没法在测试里造，
    /// 但"重定向时必须 Plain"和"不同宽度分档"这两条逻辑必须被钉住。
    /// </remarks>
    public static TerminalCapability Detect(
        bool isRedirected, Func<int> readWidth, Func<int>? readHeight = null)
    {
        ArgumentNullException.ThrowIfNull(readWidth);

        // **必须先在重定向时短路**：`Console.WindowWidth`/`WindowHeight`
        // 在输出被重定向时直接抛 IOException。所以"重定向时不读"不是优化，
        // 是让探测不抛异常的前提。
        //
        // 实测（2026-11-03，重定向下）：三个尺寸属性全抛 IOException，
        // 而 TERM 仍是 xterm-256color——环境变量会骗人，只能靠 BCL。
        if (isRedirected)
            return new TerminalCapability(TerminalTier.Plain, Width: 0, IsRedirected: true);

        int width;
        try
        {
            width = readWidth();
        }
        catch (Exception)
        {
            // 读不到宽度说明这个"终端"其实是别的东西（重定向到文件、被 CI 捕获）。
            return new TerminalCapability(TerminalTier.Plain, Width: 0, IsRedirected: true);
        }

        // 高度读不到**不降档**：宽度已经证明这是个真终端，而高度只用于
        // "排版够不够放"的判断。读不到就记 0，调用方按"高度未知"处理
        // （底部锚定退化为不锚定，而不是整屏变成纯文本）。
        var height = 0;
        if (readHeight is not null)
        {
            try { height = readHeight(); }
            catch (Exception) { height = 0; }
        }

        var tier = width switch
        {
            < MinimalThreshold => TerminalTier.Plain,
            < NarrowThreshold => TerminalTier.Narrow,
            _ => TerminalTier.Rich,
        };

        return new TerminalCapability(tier, width, IsRedirected: false, Height: height);
    }

    /// <summary>供诊断输出用的一行描述。</summary>
    public string Describe() => Tier switch
    {
        TerminalTier.Rich => Height > 0
            ? $"Rich（{Width}×{Height}）"
            : $"Rich（{Width} 列，高度未知）",
        TerminalTier.Narrow => Height > 0
            ? $"Narrow（{Width}×{Height}）"
            : $"Narrow（{Width} 列）",
        _ => IsRedirected ? "Plain（输出被重定向）" : "Plain（终端过窄或被重定向）",
    };
}
