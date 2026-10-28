using Dougong.Agent;
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
        failures += Check("tool-summary-lists-each-call", ToolSummaryListsCalls);
        failures += Check("tool-summary-reports-failures-distinctly", ToolSummaryMarksFailures);
        failures += Check("plain-tier-has-no-summary", PlainHasNoSummary);
        failures += Check("summary-starts-on-new-line-without-tools", SummaryStartsOnNewLine);

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

    /// <summary>延后输出时，每个工具调用都要出现一次。</summary>
    private static string? ToolSummaryListsCalls()
    {
        return CaptureWithTier(TerminalTier.Rich, 120, () =>
        {
            Output.PrintToolSummary(
            [
                ("read_file", ToolDispatchResultFor("read_file", ToolCallOutcome.Succeeded)),
                ("write_file", ToolDispatchResultFor("write_file", ToolCallOutcome.Succeeded)),
            ], TimeSpan.FromSeconds(4.2));
        }, text =>
        {
            if (!text.Contains("read_file", StringComparison.Ordinal))
                return "汇总里没有 read_file";
            if (!text.Contains("write_file", StringComparison.Ordinal))
                return "汇总里没有 write_file";
            if (!text.Contains("4.2s", StringComparison.Ordinal))
                return "汇总里没有耗时";
            return null;
        });
    }

    /// <summary>失败必须与成功在文本上可区分，而不是只靠颜色。</summary>
    /// <remarks>
    /// 只看颜色是不行的：Plain 档没有颜色，色弱用户也分不出。
    /// 所以断言要求成功与失败用的是**不同的标记字符**。
    /// </remarks>
    private static string? ToolSummaryMarksFailures()
    {
        return CaptureWithTier(TerminalTier.Rich, 120, () =>
        {
            Output.PrintToolSummary(
            [
                ("ok_tool", ToolDispatchResultFor("ok_tool", ToolCallOutcome.Succeeded)),
                ("bad_tool", ToolDispatchResultFor("bad_tool", ToolCallOutcome.Failed)),
            ], TimeSpan.FromSeconds(1));
        }, text =>
        {
            var lines = text.Split(Environment.NewLine);
            var okLine = lines.FirstOrDefault(l => l.Contains("ok_tool", StringComparison.Ordinal));
            var badLine = lines.FirstOrDefault(l => l.Contains("bad_tool", StringComparison.Ordinal));

            if (okLine is null) return "找不到成功工具行";
            if (badLine is null) return "找不到失败工具行";

            var okMark = okLine.TrimStart().FirstOrDefault(c => c is '✓' or '✗' or '·');
            var badMark = badLine.TrimStart().FirstOrDefault(c => c is '✓' or '✗' or '·');

            if (okMark != '✓') return $"成功行标记应为 ✓，实际 '{okMark}'";
            if (badMark != '✗') return $"失败行标记应为 ✗，实际 '{badMark}'";

            return null;
        });
    }

    /// <summary>
    /// Plain 档不调用 PrintToolSummary——它的工具行在原来那个位置，
    /// 由调用方在事件循环里直接输出。
    /// </summary>
    /// <remarks>
    /// 这条钉住的是"分支判据"：<c>deferToolLines = SupportsDecoration</c>。
    /// 如果哪天有人把判据改成恒真，Plain 档的 diff 会立刻变红——
    /// 但那时已经很难定位到具体原因，不如在这里直接断言。
    /// </remarks>
    private static string? PlainHasNoSummary()
    {
        var plain = new TerminalCapability(TerminalTier.Plain, 0, IsRedirected: true);
        return plain.SupportsDecoration
            ? "Plain 档不应启用延后输出（SupportsDecoration 应为 false）"
            : null;
    }

    private static ToolDispatchResult ToolDispatchResultFor(string toolId, ToolCallOutcome outcome) =>
        new($"inv-{toolId}", toolId, outcome);

    /// <summary>在指定档位下执行一段输出并捕获文本，交给断言检查。</summary>
    private static string? CaptureWithTier(TerminalTier tier, int width, Action action, Func<string, string?> assert)
    {
        var originalOut = Console.Out;
        var originalCap = Output.Capability;
        try
        {
            Output.OverrideCapability(new TerminalCapability(tier, width, IsRedirected: false));
            var buffer = new StringWriter();
            Console.SetOut(buffer);
            action();
            return assert(buffer.ToString());
        }
        finally
        {
            Console.SetOut(originalOut);
            Output.OverrideCapability(originalCap);
        }
    }

    /// <summary>
    /// 没有工具调用的轮次，汇总行必须另起一行，不能贴在正文后面。
    /// </summary>
    /// <remarks>
    /// 真实缺陷（2026-10-29 复检发现）：正文是流式输出的，末尾不带换行；
    /// 原来的空行只写在"有工具"那个分支里，于是无工具的轮次输出成
    /// <c>回答。  ─ 0.8s ────</c>——汇总直接黏在正文末尾。
    /// <para>
    /// 这条检查写出来的理由：原来的三条检查都用了非空的工具列表，
    /// **空列表这个分支根本没被覆盖**。
    /// </para>
    /// </remarks>
    private static string? SummaryStartsOnNewLine()
    {
        return CaptureWithTier(TerminalTier.Rich, 100, () =>
        {
            Output.Text("  回答正文。");
            Output.PrintToolSummary([], TimeSpan.FromSeconds(0.8));
        }, text =>
        {
            var lines = text.Split(Environment.NewLine);
            if (lines.Length < 2)
                return $"汇总行没有另起一行，实际内容：[{text}]";

            if (!lines[0].Contains("回答正文", StringComparison.Ordinal))
                return $"第一行应只有正文，实际：[{lines[0]}]";

            if (!lines[1].Contains("0.8s", StringComparison.Ordinal))
                return $"第二行应是汇总，实际：[{lines[1]}]";

            return null;
        });
    }
}
