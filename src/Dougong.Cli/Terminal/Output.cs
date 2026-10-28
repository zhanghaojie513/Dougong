using Spectre.Console;

namespace Dougong.Cli.Terminal;

/// <summary>
/// 终端输出的唯一出口。**CLI 里所有可见输出都必须经过这里。**
/// </summary>
/// <remarks>
/// <b>为什么要有这一层</b>：改造前 `Program.cs` 里散着 30 多处
/// <c>Console.WriteLine</c>，想按终端能力换渲染方式就没有统一的地方下手。
/// 现在只要改这一个类。
/// <para>
/// <b>兼容底线</b>：<see cref="TerminalTier.Plain"/> 档下，
/// 每个方法都退化为**原样输出传入的字符串**，不增删一个字符。
/// 45 个自检套件全部走 Plain 档，它们断言的是改造前的确切文本。
/// </para>
/// <para>
/// <b>这不是"美化开关"，是"信息编码开关"</b>：Rich 档多出来的只有颜色与符号，
/// 文本内容本身在各档完全一致。所以任何一档都不会丢失信息。
/// </para>
/// </remarks>
public static class Output
{
    private static TerminalCapability _capability = TerminalCapability.Detect();

    /// <summary>当前终端能力。测试与诊断可读。</summary>
    public static TerminalCapability Capability => _capability;

    /// <summary>
    /// 覆盖探测结果。**仅供测试**——真实运行不要调。
    /// </summary>
    /// <remarks>
    /// 存在的理由：A/B 档的分支逻辑必须能测，而真实终端宽度造不出来。
    /// </remarks>
    public static void OverrideCapability(TerminalCapability capability) => _capability = capability;

    /// <summary>重新探测。宿主在窗口尺寸变化后可调用。</summary>
    public static void Refresh() => _capability = TerminalCapability.Detect();

    // ── 输出原语 ─────────────────────────────────────────────

    /// <summary>输出正文，不换行。</summary>
    public static void Text(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        Console.Write(value);
    }

    /// <summary>输出一行正文。</summary>
    public static void Line(string value = "")
    {
        ArgumentNullException.ThrowIfNull(value);
        Console.WriteLine(value);
    }

    /// <summary>主色文本（版本号、模型名这类）。</summary>
    public static void Accent(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        WriteStyled(value, Palette.Accent, decorate: true);
    }

    /// <summary>次要文本（路径、耗时）。</summary>
    public static void Muted(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        WriteStyled(value, Palette.Muted, decorate: true);
    }

    /// <summary>成功标记。</summary>
    public static void Ok(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        WriteStyled(value, Palette.Ok, decorate: true);
    }

    /// <summary>警告标记。</summary>
    public static void Warn(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        WriteStyled(value, Palette.Warn, decorate: true);
    }

    /// <summary>失败标记。</summary>
    public static void Error(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        WriteStyled(value, Palette.Error, decorate: true);
    }

    /// <summary>写到 stderr。ACP 模式下所有日志都必须走这里。</summary>
    public static void Diagnostics(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        Console.Error.WriteLine(value);
    }

    // ── 实现 ─────────────────────────────────────────────────

    /// <summary>
    /// 带样式输出一行。Plain 档原样输出。
    /// </summary>
    /// <param name="decorate">
    /// 该调用是否属于"装饰性"输出。当前所有 Styled 调用都是，
    /// 保留参数是为了将来可能需要"Plain 档也要区分但不上色"的场景。
    /// </param>
    private static void WriteStyled(string value, string color, bool decorate)
    {
        if (!_capability.SupportsDecoration || !decorate)
        {
            Console.WriteLine(value);
            return;
        }

        // Spectre 会把 markup 里的方括号当标签解释。文本内容必须转义，
        // 否则诸如 "[已压缩 8 条]" 这样的输出会被吞掉或报错。
        AnsiConsole.MarkupLine($"[{color}]{Escape(value)}[/]");
    }

    /// <summary>
    /// 转义 Spectre markup 的特殊字符。
    /// </summary>
    /// <remarks>
    /// <b>这一条不是防御性编程，是必须的</b>：我们的输出里本来就有方括号
    /// （候选标记、区间、状态标签）。不转义会被当成样式标签——
    /// 轻则丢失内容，重则抛异常中断会话。
    /// </remarks>
    private static string Escape(string value) => value.Replace("[", "[[").Replace("]", "]]");
}
