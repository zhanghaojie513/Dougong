using Dougong.Cli.Terminal;

namespace Dougong.Agent.Comparison;

/// <summary>
/// 终端能力探测与三档渲染的验收（CLI 实施计划 S1）。
/// </summary>
/// <remarks>
/// **这一套的全部检查都围绕一条底线**：Plain 档必须与改造前逐字一致。
/// 45 个自检套件全部跑在 Plain 档下，它们断言的是改造前的确切文本。
/// <para>
/// 为什么必须测"分档逻辑"而不只测"最终输出"：最终输出在测试环境里永远是 Plain 档
/// （输出被重定向），Rich/Narrow 两个分支如果写错，测试一条都不会红。
/// 那正是 M2 期间骗过一次的形态——测试全绿而代码路径从未被执行。
/// </para>
/// </remarks>
public static class TerminalChecks
{
    public static Task<int> RunAsync()
    {
        var failures = 0;
        failures += Check("redirected-is-plain", RedirectedIsPlain);
        failures += Check("narrow-terminal-is-narrow", NarrowIsNarrow);
        failures += Check("wide-terminal-is-rich", WideIsRich);
        failures += Check("too-narrow-falls-back-to-plain", TooNarrowIsPlain);
        failures += Check("width-read-failure-is-plain", WidthFailureIsPlain);
        failures += Check("plain-tier-has-no-decoration", PlainHasNoDecoration);
        failures += Check("plain-tier-output-is-verbatim", PlainOutputIsVerbatim);

        Console.WriteLine(failures == 0
            ? "TERMINAL: ALL PASS"
            : $"TERMINAL: {failures} FAILURE(S)");
        return Task.FromResult(failures);
    }

    /// <summary>重定向时必须判为 Plain——这是唯一可信的判据。</summary>
    /// <remarks>
    /// 环境变量会骗人：实测本机 <c>TERM=xterm-256color</c> 却 <c>IsOutputRedirected=True</c>。
    /// 所以这里明确断言：只要 isRedirected 为 true，**不去读宽度**，直接 Plain。
    /// </remarks>
    private static string? RedirectedIsPlain()
    {
        var widthRead = false;
        var cap = TerminalCapability.Detect(isRedirected: true, () =>
        {
            widthRead = true;
            return 200;
        });

        if (cap.Tier != TerminalTier.Plain)
            return $"重定向时应为 Plain，实际 {cap.Tier}";

        if (widthRead)
            return "重定向时不应读取窗口宽度（重定向下读它会抛 IOException）";

        return null;
    }

    private static string? NarrowIsNarrow()
    {
        var cap = TerminalCapability.Detect(isRedirected: false, () => 70);
        return cap.Tier == TerminalTier.Narrow
            ? null
            : $"宽 70 应为 Narrow，实际 {cap.Tier}";
    }

    private static string? WideIsRich()
    {
        var cap = TerminalCapability.Detect(isRedirected: false, () => 120);
        return cap.Tier == TerminalTier.Rich
            ? null
            : $"宽 120 应为 Rich，实际 {cap.Tier}";
    }

    /// <summary>极窄终端退回 Plain：放不下装饰，硬画只会错位。</summary>
    private static string? TooNarrowIsPlain()
    {
        var cap = TerminalCapability.Detect(isRedirected: false, () => 40);
        return cap.Tier == TerminalTier.Plain
            ? null
            : $"宽 40 应为 Plain，实际 {cap.Tier}";
    }

    /// <summary>
    /// 读宽度抛异常（不是真终端）时必须退到 Plain，不能把异常抛出去。
    /// </summary>
    /// <remarks>
    /// 真实场景：输出被 CI 捕获时 <c>Console.WindowWidth</c> 抛 <c>IOException</c>。
    /// 不接住的话整个 CLI 启动就会挂在探测这一步。
    /// </remarks>
    private static string? WidthFailureIsPlain()
    {
        var cap = TerminalCapability.Detect(
            isRedirected: false,
            () => throw new IOException("句柄无效。"));
        return cap.Tier == TerminalTier.Plain
            ? null
            : $"读宽度失败时应为 Plain，实际 {cap.Tier}";
    }

    private static string? PlainHasNoDecoration()
    {
        var plain = new TerminalCapability(TerminalTier.Plain, 0, IsRedirected: true);
        if (plain.SupportsDecoration) return "Plain 档不应支持装饰";
        if (plain.SupportsUnicode) return "Plain 档不应使用 Unicode 框线";

        var rich = new TerminalCapability(TerminalTier.Rich, 120, IsRedirected: false);
        if (!rich.SupportsDecoration) return "Rich 档应支持装饰";

        return null;
    }

    /// <summary>
    /// Plain 档下 Output 的每个方法都必须原样输出——不增删一个字符。
    /// </summary>
    /// <remarks>
    /// 这条是兼容底线的直接断言。它捕获真实 stdout 再比对，
    /// 而不是读代码判断——读代码判断不出 Spectre 的转义/换行行为。
    /// </remarks>
    private static string? PlainOutputIsVerbatim()
    {
        var originalOut = Console.Out;
        var originalCap = Output.Capability;

        try
        {
            Output.OverrideCapability(
                new TerminalCapability(TerminalTier.Plain, 0, IsRedirected: true));

            var buffer = new StringWriter();
            Console.SetOut(buffer);

            // 方括号是重点：Spectre 的 markup 会把 [..] 当标签。
            // Plain 档必须原样输出，不能转义成 [[..]]。
            Output.Line("[通过] 普通行");
            Output.Accent("[强调] 这一行");
            Output.Muted("C:\\path\\to\\file");
            Output.Ok("✓ ok");
            Output.Text("无换行");
            Output.Line();

            var actual = buffer.ToString();
            var expected = "[通过] 普通行" + Environment.NewLine
                + "[强调] 这一行" + Environment.NewLine
                + "C:\\path\\to\\file" + Environment.NewLine
                + "✓ ok" + Environment.NewLine
                + "无换行" + Environment.NewLine;

            return actual == expected
                ? null
                : $"Plain 档输出被改动：\n  期望 [{Escape(expected)}]\n  实际 [{Escape(actual)}]";
        }
        finally
        {
            Console.SetOut(originalOut);
            Output.OverrideCapability(originalCap);
        }
    }

    private static string Escape(string s) =>
        s.Replace("\r", "\\r").Replace("\n", "\\n");

    private static int Check(string name, Func<string?> action)
    {
        string? failure;
        try
        {
            failure = action();
        }
        catch (Exception ex)
        {
            failure = $"{ex.GetType().Name}: {ex.Message}";
        }

        if (failure is null)
        {
            Console.WriteLine($"PASS {name}");
            return 0;
        }

        Console.WriteLine($"FAIL {name}: {failure}");
        return 1;
    }
}
