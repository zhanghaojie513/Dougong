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

        // S3：活动状态机与状态栏渲染（纯逻辑，可完整验证）
        failures += Check("state-starts-thinking-on-run-start", StateStartsThinking);
        failures += Check("state-goes-streaming-on-first-delta", StateGoesStreaming);
        failures += Check("state-falls-back-to-thinking-after-stream-stops", StateStreamDebounce);
        failures += Check("state-hides-when-idle", StateHidesWhenIdle);
        failures += Check("state-failure-lingers-then-hides", StateFailureLingers);
        failures += Check("activity-line-includes-elapsed", ActivityLineHasElapsed);
        failures += Check("activity-line-hidden-while-streaming", ActivityHiddenWhileStreaming);
        failures += Check("narrow-terminal-activity-line-is-minimal", ActivityNarrow);
        failures += Check("gauge-fills-proportionally", GaugeProportional);
        failures += Check("token-format-is-compact", TokenFormat);
        failures += Check("live-region-disabled-when-redirected", LiveDisabledWhenRedirected);
        failures += Check("activity-line-never-exceeds-terminal-width", ActivityNeverOverflows);
        failures += Check("status-line-accounting-for-wide-chars", StatusAccountsWideChars);
        failures += Check("spinner-follows-frame-interval", SpinnerFollowsInterval);
        failures += Check("live-region-emits-no-ansi-when-inactive", InactiveEmitsNothing);
        failures += Check("live-region-clears-what-it-painted", LiveRegionClears);
        failures += Check("tui-switch-disables-region", TuiSwitchDisables);
        failures += Check("live-region-write-is-atomic", LiveRegionAtomic);
        failures += Check("vt-replay-shows-activity-then-clean", VtReplayActivity);
        failures += Check("vt-replay-body-not-overwritten", VtReplayBodyIntact);
        failures += Check("vt-replay-shrink-leaves-no-ghost", VtReplayShrink);

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

    // ── S3：活动状态机与状态栏 ────────────────────────────────

    private static readonly DateTimeOffset T0 = new(2026, 10, 29, 12, 0, 0, TimeSpan.Zero);

    /// <summary>RunStarted 后进入思考中——因为"等待模型"没有任何事件可用。</summary>
    private static string? StateStartsThinking()
    {
        var state = new ActivityState();
        state.OnRunStarted(T0);

        if (state.Phase != ActivityPhase.Thinking)
            return $"应进入 Thinking，实际 {state.Phase}";

        if (!state.ShouldDisplay)
            return "思考中应该显示活动行";

        return null;
    }

    private static string? StateGoesStreaming()
    {
        var state = new ActivityState();
        state.OnRunStarted(T0);
        state.OnTextDelta(T0.AddSeconds(1));

        if (state.Phase != ActivityPhase.Streaming)
            return $"应进入 Streaming，实际 {state.Phase}";

        return null;
    }

    /// <summary>
    /// 流式停止超过去抖窗口后必须回到思考中，否则面孔会一直停在"输出中"。
    /// </summary>
    /// <remarks>
    /// <b>用绝对毫秒数，不引用 <c>ActivityState.StreamingDebounce</c></b>：引用了就是自证——
    /// 把常量从 300ms 改成 10s，测试的预期时间点会跟着一起变，照样通过。
    /// 实测过一次：第一版正是这么写的，反证没抓到。这里改成"窗口必须落在 [200ms, 500ms]"。
    /// </remarks>
    private static string? StateStreamDebounce()
    {
        var state = new ActivityState();
        state.OnRunStarted(T0);
        state.OnTextDelta(T0.AddSeconds(1));

        // 200ms 时仍在输出中——窗口不能短到让面孔闪。
        state.Tick(T0.AddSeconds(1.2));
        if (state.Phase != ActivityPhase.Streaming)
            return $"200ms 内不应切换（窗口过短），实际 {state.Phase}";

        // 500ms 时必须已经回到思考中——窗口不能长到让人以为卡死。
        state.Tick(T0.AddSeconds(1.5));
        if (state.Phase != ActivityPhase.Thinking)
            return $"500ms 后应回到 Thinking（窗口过长），实际 {state.Phase}";

        return null;
    }

    /// <summary>空闲时不显示活动行——用户正在打字，不该有东西在动。</summary>
    private static string? StateHidesWhenIdle()
    {
        var state = new ActivityState();

        if (state.ShouldDisplay)
            return "初始（Idle）不应显示活动行";

        state.OnRunStarted(T0);
        state.OnRunFinished(T0.AddSeconds(3), failed: false);

        if (state.Phase != ActivityPhase.Idle)
            return $"结束后应为 Idle，实际 {state.Phase}";

        if (state.ShouldDisplay)
            return "结束后不应显示活动行";

        return null;
    }

    /// <summary>失败状态短暂显示后自动隐藏，不能一直挂着。</summary>
    private static string? StateFailureLingers()
    {
        var state = new ActivityState();
        state.OnRunStarted(T0);
        state.OnRunFinished(T0.AddSeconds(2), failed: true);

        if (state.Phase != ActivityPhase.Failed)
            return $"失败后应进入 Failed，实际 {state.Phase}";

        if (!state.ShouldDisplay)
            return "失败应该可见（要让用户看到出错了）";

        // 同样用绝对时间：失败提示必须停留 1s 以上（让人看见），3s 内消失（不占屏）。
        state.Tick(T0.AddSeconds(3));
        if (state.Phase != ActivityPhase.Failed)
            return $"1s 时就消失太快（用户会错过），实际 {state.Phase}";

        state.Tick(T0.AddSeconds(5));
        if (state.Phase != ActivityPhase.Idle)
            return $"3s 后仍在显示（停留过长），实际 {state.Phase}";

        return null;
    }

    private static string? ActivityLineHasElapsed()
    {
        var state = new ActivityState();
        state.OnRunStarted(T0);

        var line = StatusLine.RenderActivity(state, T0.AddSeconds(5), width: 80);
        if (line is null) return "思考中应渲染活动行";

        if (!line.Contains("5s", StringComparison.Ordinal))
            return $"活动行缺少耗时，实际：[{line}]";

        if (!line.Contains("思考中", StringComparison.Ordinal))
            return $"活动行缺少文案，实际：[{line}]";

        return null;
    }

    /// <summary>流式输出期间不显示活动行（正文在滚，不需要额外提示）。</summary>
    private static string? ActivityHiddenWhileStreaming()
    {
        var state = new ActivityState();
        state.OnRunStarted(T0);
        state.OnTextDelta(T0.AddSeconds(1));

        var line = StatusLine.RenderActivity(state, T0.AddSeconds(1.1), width: 80);
        return line is null
            ? null
            : $"流式期间不应渲染活动行，实际：[{line}]";
    }

    private static string? ActivityNarrow()
    {
        var state = new ActivityState();
        state.OnRunStarted(T0);

        var line = StatusLine.RenderActivity(state, T0.AddSeconds(1), width: 30);
        if (line is null) return "极窄终端仍应显示 spinner";

        // 极窄档只留 spinner 一个字符。
        if (line.Trim().Length != 1)
            return $"宽 30 应只输出 spinner，实际：[{line}]";

        return null;
    }

    private static string? GaugeProportional()
    {
        var empty = StatusLine.RenderGauge(0, 10);
        var half = StatusLine.RenderGauge(50, 10);
        var full = StatusLine.RenderGauge(100, 10);

        var expectedEmpty = "[" + new string('░', 10) + "]";
        if (empty != expectedEmpty)
            return $"0% 应为全空（10 格），实际 {empty}";

        if (half.Count(c => c == '█') != 5)
            return $"50% 应有 5 格实心，实际 {half}";

        if (full.Count(c => c == '█') != 10)
            return $"100% 应满格，实际 {full}";

        // 越界不能抛异常，要夹紧。
        if (StatusLine.RenderGauge(150, 10).Count(c => c == '█') != 10)
            return "150% 应夹紧到满格";

        return null;
    }

    private static string? TokenFormat()
    {
        if (StatusLine.FormatTokens(999) != "999") return "999 应原样输出";
        if (StatusLine.FormatTokens(1200) != "1.2K") return "1200 应为 1.2K";
        if (StatusLine.FormatTokens(1_500_000) != "1.5M") return "1.5M 格式不符";
        return null;
    }

    /// <summary>
    /// 重定向时必须完全不启用重绘——即使调用方误传了 force=true。
    /// </summary>
    /// <remarks>
    /// 这条是 S3 唯一能自动验证的安全底线：真终端里的观感测不了，
    /// 但"绝不在管道里写 ANSI"必须钉死——否则 45 个套件的断言会被污染。
    /// </remarks>
    private static string? LiveDisabledWhenRedirected()
    {
        var redirected = new TerminalCapability(TerminalTier.Plain, 0, IsRedirected: true);

        if (LiveRegion.Create(redirected).IsActive)
            return "重定向时不应启用 LiveRegion";

        if (LiveRegion.Create(redirected, force: true).IsActive)
            return "重定向时即使 force=true 也不应启用 LiveRegion";

        var rich = new TerminalCapability(TerminalTier.Rich, 120, IsRedirected: false);
        if (!LiveRegion.Create(rich).IsActive)
            return "富终端下应启用 LiveRegion";

        if (LiveRegion.Create(rich, force: false).IsActive)
            return "force=false 时应禁用 LiveRegion";

        return null;
    }

    /// <summary>
    /// 活动行宽度**必须**小于等于终端宽度——否则折行，上一帧擦不掉，状态栏越用越乱。
    /// </summary>
    /// <remarks>
    /// 这条抓的是 CJK 宽度 bug：初期实现按 <c>String.Length</c> 数格，中文各占两格，
    /// 于是 40 格内容被算成 30 格，补白后实际 50 格 → 40 宽终端折行。
    /// <para>
    /// 中文文案是<b>必然出现</b>的（"思考中…"），所以这条检查对所有宽度都要过。
    /// </para>
    /// </remarks>
    private static string? ActivityNeverOverflows()
    {
        var state = new ActivityState();
        state.OnRunStarted(T0);

        foreach (var width in new[] { 40, 50, 60, 78, 80, 100, 120, 200 })
        {
            var line = StatusLine.RenderActivity(state, T0.AddSeconds(65), width);
            if (line is null) return $"宽 {width} 应显示活动行";

            var display = DisplayWidthOf(line);
            if (display > width)
                return $"宽 {width} 溢出到 {display} 格：[{line}]";
        }

        return null;
    }

    /// <summary>状态栏同样不能溢出（模型名 + 中文 + 进度条 + 耗时四段拼起来更长）。</summary>
    private static string? StatusAccountsWideChars()
    {
        foreach (var width in new[] { 40, 60, 80, 120 })
        {
            var line = StatusLine.RenderStatus("深度求索-很强的模型名字", 120_000, 200_000,
                TimeSpan.FromSeconds(90), width);

            var display = DisplayWidthOf(line);
            if (display > width)
                return $"宽 {width} 溢出到 {display} 格：[{line}]";
        }

        return null;
    }

    /// <summary>
    /// spinner 必须按帧间隔轮换，不能一秒转好几圈。
    /// </summary>
    /// <remarks>
    /// 初期实现里写死成 125ms（帧间隔常量是 250ms），实际帧率是设计的两倍。
    /// 这类"魔法数与常量不一致"只有跨帧采样才看得出，单帧断言抓不到。
    /// <para>
    /// <b>必须从 <see cref="StatusLine.RenderActivity"/> 的输出里取样</b>，
    /// 不能直接调 <c>PickSpinner</c>：bug 就在 RenderActivity 里——它曾自己算帧而
    /// 不用那个常量。查 PickSpinner 等于查了个没坏的地方，反证实测过一次没抓到。
    /// </para>
    /// </remarks>
    private static string? SpinnerFollowsInterval()
    {
        var state = new ActivityState();
        state.OnRunStarted(T0);

        var changes = 0;
        string? previous = null;

        // 1 秒内采样 100 次（每 10ms）：按 250ms 一帧，一秒内换 4 次。
        for (var ms = 10; ms <= 1000; ms += 10)
        {
            var frame = SpinnerCharOf(state, T0.AddMilliseconds(ms));
            if (frame is null) return "活动行应始终带 spinner";

            if (previous is not null && frame != previous) changes++;
            previous = frame;
        }

        if (changes is < 3 or > 5)
            return $"1 秒内应换约 4 次帧，实际 {changes} 次（帧间隔不是 250ms）";

        return null;
    }

    /// <summary>从活动行里抠出 spinner 字符（固定在第 3 列）。</summary>
    private static string? SpinnerCharOf(ActivityState state, DateTimeOffset now)
    {
        var line = StatusLine.RenderActivity(state, now, width: 80);
        if (line is null || line.Length < 4) return null;
        return line[2].ToString();
    }

    /// <summary>测宽用的独立实现：不复用被测代码，否则宽度 bug 会一起错。</summary>
    private static int DisplayWidthOf(string text)
    {
        var width = 0;
        foreach (var c in text)
        {
            if (c == '') continue; // 本套件断言的都是不含 ANSI 的纯文本
            width += c is >= '一' and <= '鿿' or >= '　' and <= '〿' ? 2 : 1;
        }
        return width;
    }

    /// <summary>
    /// 不活动的区域**不得向终端写任何字节**。
    /// </summary>
    /// <remarks>
    /// 这是 S3 的安全底线：45 个套件都靠 stdout 与改造前逐字比对，
    /// 只要有一个 ANSI 字节漏出去，全部会变红。所以这里断言的是"零字节"，
    /// 不是"没有可见变化"。
    /// </remarks>
    private static string? InactiveEmitsNothing()
    {
        var captured = new StringWriter();
        var original = Console.Out;
        Console.SetOut(captured);

        try
        {
            var plain = new TerminalCapability(TerminalTier.Plain, 80, IsRedirected: false);
            var redirected = new TerminalCapability(TerminalTier.Plain, 0, IsRedirected: true);

            foreach (var capability in new[] { plain, redirected })
            {
                using var region = LiveRegion.Create(capability);
                if (region.IsActive)
                    return $"能力 {capability} 不应激活区域";

                region.SetActivity("  思考中…");
                region.SetLines(["一行", "两行"]);
                region.RunExclusive(() => { });
                region.Clear();
            }

            // force=true 在重定向下也必须被拒。
            using var forced = LiveRegion.Create(redirected, force: true);
            if (forced.IsActive) return "重定向 + force=true 不应激活区域";

            forced.SetActivity("x");
        }
        finally
        {
            Console.SetOut(original);
        }

        return captured.ToString().Length == 0
            ? null
            : $"不活动的区域写了 {captured.ToString().Length} 个字符：{captured}";
    }

    /// <summary>
    /// 行数变少时必须擦掉多出来的行，否则会留下残行。
    /// </summary>
    /// <remarks>
    /// <b>这是 <c>Erase</c> 唯一不可替代的职责</b>：每行写之前本来就会清行，
    /// 所以"内容被覆盖"不需要额外擦除。真正的坑是 2 行变 1 行时，
    /// 第 2 行的旧内容没有任何新内容去覆盖它，它就留在屏幕上了。
    /// <para>
    /// 判据用结构而不是文本：擦两行区域必须先把光标上移（<c>ESC[1A</c>）。
    /// 不擦直接画时不会出现任何上移序列。第一版检查只看"有没有 ESC[2K"，
    /// 而 <c>Clear()</c> 自己也会产生它，于是反证没抓到——记在这里。
    /// </para>
    /// </remarks>
    private static string? LiveRegionClears()
    {
        var captured = new StringWriter();
        var original = Console.Out;
        Console.SetOut(captured);

        try
        {
            var rich = new TerminalCapability(TerminalTier.Rich, 80, IsRedirected: false);
            using var region = LiveRegion.Create(rich);
            if (!region.IsActive) return "富终端应激活区域";

            region.SetLines(["  思考中…", "  模型 · 1s"]);
            region.SetLines(["  思考中…"]);
        }
        finally
        {
            Console.SetOut(original);
        }

        var output = captured.ToString();
        if (output.Length == 0) return "区域应写入控制序列";

        // 擦掉两行区域需要上移一行。
        if (!output.Contains("[1A", StringComparison.Ordinal))
            return "行数从 2 减到 1 时未清除多余行（缺少上移序列）";

        if (!output.Contains("思考中", StringComparison.Ordinal))
            return "应写入活动行文本";

        return null;
    }

    /// <summary>
    /// <c>DOUGONG_TUI=0</c> 必须让区域彻底不启用——这是用户唯一的逃生阀。
    /// </summary>
    /// <remarks>
    /// 验证的是"关掉开关后回到 S2 行为"这件事本身：区域不活动即代表
    /// <see cref="Output"/> 的擦/画两条都退化为空操作，输出与 S2 完全一致。
    /// </remarks>
    private static string? TuiSwitchDisables()
    {
        var rich = new TerminalCapability(TerminalTier.Rich, 120, IsRedirected: false);

        using var off = LiveRegion.Create(rich, force: false);
        if (off.IsActive) return "DOUGONG_TUI=0 时应禁用区域";

        using var auto = LiveRegion.Create(rich);
        if (!auto.IsActive) return "默认（DOUGONG_TUI 未设）时富终端应启用区域";

        return null;
    }

    /// <summary>
    /// 正文写入必须与重绘互斥：区域不能插进一次写入的中间。
    /// </summary>
    /// <remarks>
    /// <b>这条防的是最隐蔽的一类缺陷</b>：刷新循环在后台线程每 100ms 画一次，
    /// 正文写一半时如果区域插进来，终端上会出现"半句话 + 状态栏 + 后半句"。
    /// 这种撕裂在真终端上难以复现，但在这里可以用"写入期间区域不许画"直接钉死。
    /// <para>
    /// 判据：<paramref name="write"/> 内部先画一次（应被阻塞），
    /// 若能观察到画的内容出现在正文中间，说明互斥失效。
    /// </para>
    /// </remarks>
    private static string? LiveRegionAtomic()
    {
        var captured = new StringWriter();
        var original = Console.Out;
        Console.SetOut(captured);

        try
        {
            var rich = new TerminalCapability(TerminalTier.Rich, 80, IsRedirected: false);
            using var region = LiveRegion.Create(rich);

            // 先让区域里有点东西，这样重绘会产生可见输出。
            region.SetActivity("  思考中…");
            captured.GetStringBuilder().Clear();

            var interleaved = false;

            region.RunExclusive(() =>
            {
                Output.Control("[前半句]");

                // 试图在写入过程中重画：必须被阻塞（同一把锁，同线程会死锁，
                // 所以这里只检查"区域此刻不画"这一可观察事实）。
                // 换成从另一个线程发起，才能真正验证互斥。
                var other = Task.Run(() => region.SetActivity("  思考中… 9s"));
                interleaved = other.Wait(TimeSpan.FromMilliseconds(200));

                Output.Control("[后半句]");
            });

            // 若后台重绘在等待超时前完成，说明它没被挡住 —— 但它是在正文写完之后
            // 才拿到锁的，那不算撕裂。真正的判据是：正文两半必须相邻。
            var text = captured.ToString();
            var first = text.IndexOf("[前半句]", StringComparison.Ordinal);
            var second = text.IndexOf("[后半句]", StringComparison.Ordinal);

            if (first < 0 || second < 0) return $"正文未完整写入：[{text}]";

            if (second - first != "[前半句]".Length)
                return $"正文被重绘打断：[{text}]";
        }
        finally
        {
            Console.SetOut(original);
        }

        return null;
    }

    // ── VT 回放：把真实的字节流喂给最小终端模拟器，看屏幕上剩下什么 ──
    //
    // 这一组是本片唯一能替代"真终端观感"的验证方式。它不检查"有没有输出某个序列"，
    // 而是真的按 VT 语义把输出渲染到一块字符网格上，然后断言网格内容。
    // 覆盖不到的只剩：字体宽度实测、颜色观感、动画流畅度。

    /// <summary>
    /// 活动行在进行中可见，退出后被擦净。
    /// </summary>
    private static string? VtReplayActivity()
    {
        var (live, afterDispose) = ReplayRegion(rich => rich.SetActivity("  思考中… 2s"));

        // 轮次进行中必须看得见活动行。
        if (!live.Text().Contains("思考中", StringComparison.Ordinal))
            return "进行中应显示活动行：\n" + live.Dump();

        // 退出后必须擦干净。
        if (afterDispose.Text().Contains("思考中", StringComparison.Ordinal))
            return "退出后仍残留活动行：\n" + afterDispose.Dump();

        return null;
    }

    /// <summary>
    /// 正文写入不能被区域覆盖——这是底部固定区域最容易出的问题。
    /// </summary>
    private static string? VtReplayBodyIntact()
    {
        var (live, _) = ReplayRegion(rich =>
        {
            rich.SetActivity("  思考中…");
            rich.RunExclusive(() => Output.Control("正文第一行\n"));
            rich.RunExclusive(() => Output.Control("正文第二行"));
        });

        var text = live.Text();

        if (!text.Contains("正文第一行", StringComparison.Ordinal))
            return "正文第一行丢失：\n" + live.Dump();

        if (!text.Contains("正文第二行", StringComparison.Ordinal))
            return "正文第二行丢失：\n" + live.Dump();

        // 活动行必须还在，但不能压在正文上。
        if (!text.Contains("思考中", StringComparison.Ordinal))
            return "进行中活动行应当仍可见：\n" + live.Dump();

        var firstLine = text.Split('\n')[0];
        if (firstLine.Contains("思考中", StringComparison.Ordinal))
            return "活动行覆盖了第一行正文：\n" + live.Dump();

        return null;
    }

    /// <summary>
    /// 区域从两行缩到一行时，第二行必须真的消失。
    /// </summary>
    /// <remarks>
    /// 这是 LiveRegion 里最难写对的一处：只写新内容不清旧行，
    /// 第一行会被覆盖而第二行会留着。VT 回放能直接看到那行鬼影。
    /// </remarks>
    private static string? VtReplayShrink()
    {
        var (live, _) = ReplayRegion(rich =>
        {
            rich.SetLines(["  第一行内容", "  第二行内容"]);
            rich.SetLines(["  第一行内容"]);
        });

        var text = live.Text();

        if (!text.Contains("第一行内容", StringComparison.Ordinal))
            return "第一行丢失：\n" + live.Dump();

        if (text.Contains("第二行内容", StringComparison.Ordinal))
            return "缩行后第二行仍在屏幕上（鬼影）：\n" + live.Dump();

        return null;
    }

    /// <summary>
    /// 在受控的 Console.Out 下跑一段区域操作，返回渲染后的屏幕与原始输出。
    /// </summary>
    private static (VtScreen Live, VtScreen AfterDispose) ReplayRegion(Action<LiveRegion> action)
    {
        var captured = new StringWriter();
        var original = Console.Out;
        Console.SetOut(captured);

        LiveRegion? region = null;
        string whileLive;

        try
        {
            var rich = new TerminalCapability(TerminalTier.Rich, 80, IsRedirected: false);
            region = LiveRegion.Create(rich);
            if (!region.IsActive) throw new InvalidOperationException("富终端应激活区域");

            action(region);

            // **必须在 Dispose 之前取快照**：Dispose 会调 Clear 把区域擦净，
            // 那之后再读屏幕永远是空的——第一版检查就栽在这里，误报"第一行丢失"。
            whileLive = captured.ToString();
        }
        finally
        {
            region?.Dispose();
            Console.SetOut(original);
        }

        var live = new VtScreen(columns: 80, rows: 12);
        live.Feed(whileLive);

        // 退出后的屏幕：从同一段字节接着喂，看 Clear 是否真的擦干净了。
        var afterDispose = new VtScreen(columns: 80, rows: 12);
        afterDispose.Feed(captured.ToString());

        return (live, afterDispose);
    }

    /// <summary>
    /// 极简 VT 模拟器：只实现本产品实际用到的序列。
    /// </summary>
    /// <remarks>
    /// <b>为什么自己写而不是引第三方</b>：需要的语义只有四种（换行、回车、
    /// 清行、上移），引一个完整终端模拟器进来反而要花更多时间确认它的行为。
    /// 未识别的序列一律丢弃——那本身就是被测代码不该产生的东西。
    /// </remarks>
    private sealed class VtScreen(int columns, int rows)
    {
        private readonly char[][] _grid = Enumerable.Range(0, rows)
            .Select(_ => Enumerable.Repeat(' ', columns).ToArray()).ToArray();

        private int _row;
        private int _column;

        public void Feed(string text)
        {
            for (var i = 0; i < text.Length; i++)
            {
                var c = text[i];

                if (c == '\u001b' && i + 1 < text.Length && text[i + 1] == '[')
                {
                    i = ApplyCsi(text, i + 2);
                    continue;
                }

                switch (c)
                {
                    case '\n': _row = Math.Min(rows - 1, _row + 1); break;
                    case '\r': _column = 0; break;
                    default:
                        if (_column < columns) _grid[_row][_column++] = c;
                        break;
                }
            }
        }

        /// <summary>解析 CSI 序列，返回序列最后一个字符的下标。</summary>
        private int ApplyCsi(string text, int start)
        {
            var end = start;
            while (end < text.Length && !char.IsLetter(text[end])) end++;
            if (end >= text.Length) return text.Length - 1;

            var final = text[end];
            var parameter = text[start..end];

            switch (final)
            {
                case 'A':
                    // 光标上移 N 行。
                    _row = Math.Max(0, _row - (ParseOrOne(parameter)));
                    break;

                case 'K':
                    // 清行：2 = 整行清空。
                    Array.Fill(_grid[_row], ' ');
                    break;
            }

            return end;
        }

        private static int ParseOrOne(string parameter) =>
            int.TryParse(parameter, out var value) && value > 0 ? value : 1;

        /// <summary>屏幕上的全部文字（行用换行连接）。</summary>
        public string Text() => string.Join("\n", _grid.Select(line => new string(line).TrimEnd()));

        /// <summary>带行号的屏幕转储，失败时用来看清到底剩了什么。</summary>
        public string Dump()
        {
            var lines = _grid.Select((line, index) =>
                $"{index,2}|{new string(line).TrimEnd()}");
            return string.Join("\n", lines);
        }
    }
}
