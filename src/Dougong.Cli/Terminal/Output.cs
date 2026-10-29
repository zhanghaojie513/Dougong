using Dougong.Agent;
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

    private static LiveRegion? _region;

    /// <summary>当前终端能力。测试与诊断可读。</summary>
    public static TerminalCapability Capability => _capability;

    /// <summary>
    /// 底部固定区域。启动时设置一次，之后所有输出自动与它协作。
    /// </summary>
    /// <remarks>
    /// <b>为什么放在这里而不是让调用方自己协调</b>：底部区域一旦存在，
    /// 任何直接写终端的动作都会把它的位置挤乱。把"擦掉再写、写完重画"
    /// 挂在唯一出口上，调用方（包括第三方插件打日志）就不需要知道它存在。
    /// </remarks>
    public static LiveRegion? LiveRegion
    {
        get => _region;
        set => _region = value;
    }

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
        Guard(() => Console.Write(value));
    }

    /// <summary>输出一行正文。</summary>
    public static void Line(string value = "")
    {
        ArgumentNullException.ThrowIfNull(value);
        Guard(() => Console.WriteLine(value));
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

    /// <summary>
    /// 写入原始控制序列（光标定位、清行）。**只给 <see cref="LiveRegion"/> 用。**
    /// </summary>
    /// <remarks>
    /// <b>为什么收到这里而不是让 LiveRegion 自己写 Console</b>：架构边界测试要求
    /// CLI 里所有可见输出都经过本类，一个出口才好审计"到底往终端写了什么"。
    /// 这条路径不经 <see cref="Guard"/>：它本身就是底部区域的重绘动作，
    /// 再加一层擦除会造成递归。
    /// </remarks>
    internal static void Control(string sequence)
    {
        ArgumentNullException.ThrowIfNull(sequence);
        Console.Write(sequence);
    }

    /// <summary>
    /// 一轮结束后统一列出本轮的工具调用，并给出一行汇总。
    /// </summary>
    /// <remarks>
    /// <b>只在 SupportsDecoration 档调用</b>。Plain 档不走这里——那一档的工具行
    /// 必须插在流式文本中间的原位置，与改造前逐字一致。
    /// <para>
    /// <b>为什么延后输出</b>：工具行原本插在流式文本里，把一段完整的回答切成几块。
    /// 延后之后回答连续，工具调用集中列在下方，看起来像"说完话再做动作"。
    /// </para>
    /// </remarks>
    public static void PrintToolSummary(
        IReadOnlyList<(string ToolId, ToolDispatchResult Result)> tools,
        TimeSpan elapsed)
    {
        ArgumentNullException.ThrowIfNull(tools);

        // 空行必须无条件打：正文是流式输出的，末尾不带换行，
        // 少这一行会让汇总直接贴在回答后面（实测踩到过）。
        Line();

        foreach (var (toolId, result) in tools)
        {
            var marker = result.Outcome switch
            {
                ToolCallOutcome.Succeeded => "✓",
                ToolCallOutcome.Failed => "✗",
                _ => "·",
            };

            var line = $"  {marker} {toolId} · {result.Outcome}";
            if (result.Diagnostic is { Length: > 0 } diagnostic)
                line += $"（{diagnostic}）";

            switch (result.Outcome)
            {
                case ToolCallOutcome.Succeeded: Ok(line); break;
                case ToolCallOutcome.Failed: Error(line); break;
                default: Muted(line); break;
            }
        }

        // 汇总行：本轮耗时 + 工具数。
        var seconds = elapsed.TotalSeconds;
        var summary = tools.Count > 0
            ? $"  ─ {seconds:F1}s · {tools.Count} 个工具"
            : $"  ─ {seconds:F1}s";
        Muted(summary + $" {new string('─', Math.Max(0, Math.Min(20, _capability.Width - summary.Length - 4)))}");
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
            Guard(() => Console.WriteLine(value));
            return;
        }

        // Spectre 会把 markup 里的方括号当标签解释。文本内容必须转义，
        // 否则诸如 "[已压缩 8 条]" 这样的输出会被吞掉或报错。
        //
        // **不能用静态 AnsiConsole**：它在首次使用时缓存了当时的 Console.Out，
        // 之后 Console.SetOut 换流对它无效——表现为"第一次输出正常，之后全部丢失"。
        // 实测确认（2026-10-29）。这里每次新建实例并绑定当前 Console.Out。
        Guard(() => CreateConsole().MarkupLine($"[{color}]{Escape(value)}[/]"));
    }

    /// <summary>
    /// 执行一次输出，期间让底部区域让位。
    /// </summary>
    /// <remarks>
    /// <b>这是"底部区域不被打乱"的唯一保证</b>：区域画在光标下方，正文写出来
    /// 就会跑到区域上面，区域随即错位。所以在每次写之前擦掉、写之后重画。
    /// 没有活动区域时（Plain 档、重定向）两条都是空操作，不产生任何额外输出。
    /// </remarks>
    private static void Guard(Action write)
    {
        var region = _region;

        if (region is null || !region.IsActive)
        {
            write();
            return;
        }

        // 擦、写、画必须是同一段临界区，否则后台刷新会插进正文中间。
        region.RunExclusive(write);
    }

    /// <summary>
    /// 新建一个绑定到当前 <see cref="Console.Out"/> 的 Spectre 控制台。
    /// </summary>
    /// <remarks>
    /// 每次输出都新建，代价是一次小对象分配；换来的是"流被替换后仍然输出到正确的流"。
    /// 静态 <c>AnsiConsole</c> 会在首次访问时记住当时的 writer，此后不再跟随
    /// <c>Console.SetOut</c>——那种行为在测试里会表现为输出凭空消失。
    /// </remarks>
    private static IAnsiConsole CreateConsole() =>
        AnsiConsole.Create(new AnsiConsoleSettings
        {
            Out = new AnsiConsoleOutput(Console.Out),
            ColorSystem = ColorSystemSupport.Detect,
            Interactive = InteractionSupport.No,
        });

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
