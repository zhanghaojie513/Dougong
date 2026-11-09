using System.Text.RegularExpressions;
using Dougong.Agent;
using Dougong.Cli.Terminal;

namespace Dougong.Agent.Comparison;

/// <summary>
/// 终端能力探测与三档渲染的验收（CLI 实施计划 S1）。
/// </summary>
/// <remarks>
/// **这一套的全部检查都围绕一条底线**：Plain 档必须与改造前逐字一致。
/// 44 个自检套件全部跑在 Plain 档下，它们断言的是改造前的确切文本。
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

        // P1（全屏化）：高度探测 + 启动清屏。
        failures += Check("height-is-captured-in-rich", HeightCaptured);
        failures += Check("height-failure-does-not-downgrade-tier", HeightFailureKeepsTier);
        failures += Check("startup-clears-screen-only-when-rich", ClearOnlyWhenRich);
        failures += Check("redirected-startup-emits-no-control-sequences", RedirectedEmitsNothing);

        // P3（底部锚定）：只有 Rich + 高度可读才锚定，其余走原路径。
        failures += Check("live-region-anchors-when-height-known", AnchorsWhenHeightKnown);
        failures += Check("live-region-does-not-anchor-without-height", NoAnchorWithoutHeight);
        failures += Check("prompt-sits-above-status-bar-when-anchored", PromptAboveStatusBar);
        failures += Check("prompt-is-drawn-by-region-and-survives-erase", PromptSurvivesErase);
        failures += Check("plain-tier-has-no-decoration", PlainHasNoDecoration);
        failures += Check("plain-tier-output-is-verbatim", PlainOutputIsVerbatim);
        failures += Check("tool-summary-lists-each-call", ToolSummaryListsCalls);
        failures += Check("tool-summary-reports-failures-distinctly", ToolSummaryMarksFailures);
        failures += Check("plain-tier-has-no-summary", PlainHasNoSummary);
        failures += Check("narrow-tier-does-not-rewrap-long-lines", NarrowDoesNotRewrap);
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
        failures += Check("every-phase-has-visible-label", EveryPhaseLabelled);
        failures += Check("live-region-disabled-when-redirected", LiveDisabledWhenRedirected);
        failures += Check("activity-line-never-exceeds-terminal-width", ActivityNeverOverflows);
        failures += Check("status-line-accounting-for-wide-chars", StatusAccountsWideChars);

        // 显示宽度的外部基准。**期望值硬编码**，不复用被测实现——
        // 复用会让"两边一起错"永远通过。
        failures += Check("width-golden-values", WidthGoldenValues);
        failures += Check("width-is-biased-wide-not-narrow", WidthBiasIsWide);
        failures += Check("truncate-never-splits-surrogate-pair", TruncateNoLoneSurrogate);
        failures += Check("spinner-follows-frame-interval", SpinnerFollowsInterval);
        failures += Check("live-region-emits-no-ansi-when-inactive", InactiveEmitsNothing);
        failures += Check("live-region-clears-what-it-painted", LiveRegionClears);
        failures += Check("tui-switch-disables-region", TuiSwitchDisables);
        failures += Check("live-region-write-is-atomic", LiveRegionAtomic);
        failures += Check("vt-replay-shows-activity-then-clean", VtReplayActivity);
        failures += Check("anchored-region-clears-on-dispose", AnchoredClearsOnDispose);
        failures += Check("vt-replay-body-not-overwritten", VtReplayBodyIntact);
        failures += Check("vt-replay-shrink-leaves-no-ghost", VtReplayShrink);
        failures += Check("vt-replay-streaming-keeps-prompt-line", VtReplayPromptLine);
        failures += Check("vt-replay-region-does-not-reset-column", VtReplayColumn);
        failures += Check("live-region-skips-identical-repaint", RepaintSkipsIdentical);
        failures += Check("clear-then-write-does-not-resurrect-region", ClearThenWrite);
        failures += Check("stderr-line-does-not-collide-with-region", StderrDoesNotCollide);
        failures += Check("binary-is-not-stale", BinaryNotStale);
        failures += Check("comparison-uses-release-cli-assembly", ComparisonUsesFreshCli);
        failures += Check("padded-activity-line-does-not-wrap", PaddedLineNoWrap);
        failures += Check("reasoning-phase-shows-distinct-label", ReasoningPhaseDistinct);
        failures += Check("reasoning-falls-back-to-thinking", ReasoningFallsBack);
        failures += Check("reasoning-does-not-downgrade-streaming", ReasoningKeepsStreaming);
        failures += Check("agent-phase-tracker-is-usable-standalone", TrackerStandalone);

        // P4：两槽位（活动行 / 状态栏）+ 渐进披露。
        // 注：检查名沿用旧名，避免外部脚本与文档引用失效；
        // 断言已改为"两行必须是两行"，见各方法上的注释。
        failures += Check("status-is-single-line-when-busy", SingleLineBusy);
        failures += Check("status-persists-when-idle", StatusPersistsIdle);
        failures += Check("status-drops-whole-tail-segments-not-truncates", TailDroppedWhole);
        failures += Check("status-uses-vertical-bar-separator", VerticalBar);
        failures += Check("anchored-region-survives-body-scroll", RegionSurvivesScroll);

        // 三行版式（用户 2026-11-05 的版式要求）。
        // 上面那条验证"块不被正文推走"，下面两条验证"块内部的行序正确"：
        // 一条查真实渲染出来的行相对次序，一条查三行分别落在哪个绝对行。
        failures += Check("activity-line-sits-above-input-line", ActivityAboveInput);
        failures += Check("bottom-block-rows-are-stable", BlockRowsStable);
        failures += Check("turn-elapsed-is-shown-exactly-once", ElapsedShownOnce);

        // P5: theme. Default must be monochrome -- the design doc forbids gold.
        failures += Check("accent-is-not-gold-by-default", AccentNotGold);
        failures += Check("semantic-colors-do-not-follow-theme", SemanticFixed);
        failures += Check("zhugou-theme-is-red-not-gold", ZhugouIsRed);

        // The idle status line must exist before the first turn, not only after one.
        failures += Check("idle-status-line-shows-before-first-turn", IdleBeforeFirstTurn);
        failures += Check("startup-actually-calls-show-idle", StartupCallsShowIdle);

        // P2: yungong art + four-tier banner.
        failures += Check("art-rows-match-spec", ArtRowsMatchSpec);
        failures += Check("art-has-no-wide-chars", ArtNoWideChars);
        failures += Check("art-keeps-ragged-right-edge", ArtRaggedEdge);
        failures += Check("banner-drops-to-stacked-when-narrow", BannerStackedWhenNarrow);
        failures += Check("banner-drops-to-compact-when-short-terminal", BannerShortTerminal);
        failures += Check("banner-picks-compact-when-there-is-room", BannerPicksCompactWhenRoom);

        // Idle status line must still show the context budget.
        failures += Check("idle-status-shows-context-capacity", IdleShowsCapacity);

        // 多行状态时提示符不能压到状态行上（单行是唯一"恰好正确"的情形）。
        failures += Check("prompt-does-not-overlap-multi-row-status", PromptNoOverlapMultiRow);

        // 字标 DOUGONG-HARNESS（用户 2026-11-04 要求做）。
        failures += Check("big-text-glyphs-are-uniform", BigTextGlyphsUniform);
        failures += Check("big-text-wordmark-covers-every-glyph", BigTextCoversWordmark);
        failures += Check("big-text-row-width-matches-declared", BigTextWidthMatches);
        failures += Check("big-text-falls-back-when-too-narrow", BigTextNarrowFallback);
        failures += Check("big-text-drops-when-too-short", BigTextShortFallback);
        failures += Check("big-text-gradient-spans-endpoints", BigTextGradient);
        failures += Check("big-text-glyph-legibility", BigTextLegibility);

        // 启动面板（P8/P9）：边框盒 + 运行期清单。
        failures += Check("frame-lines-are-equal-width", FrameEqualWidth);
        failures += Check("frame-border-is-half-width", FrameBorderHalfWidth);
        failures += Check("frame-refuses-when-too-narrow", FrameTooNarrow);
        failures += Check("frame-title-fits-inside-border", FrameTitleFits);
        failures += Check("panel-tool-count-matches-input", PanelToolCount);
        failures += Check("panel-says-when-mcp-not-connected", PanelMcpHonest);
        failures += Check("panel-truncates-skills-with-count", PanelSkillTruncation);
        failures += Check("panel-tool-count-follows-real-list", PanelCountsAreNotHardcoded);
        failures += Check("panel-says-how-many-mcp-are-hidden", PanelMcpTruncation);
        failures += Check("panel-says-how-many-sources-are-hidden", PanelSourceTruncation);
        failures += Check("panel-distinguishes-unknown-from-zero", PanelUnknownNotZero);

        // 首页总行数不能超出终端高度——超了会把底部状态行与提示符挤出屏幕。
        failures += Check("startup-never-exceeds-terminal-height", StartupFitsHeight);
        failures += Check("startup-without-panel-fits-height", StartupWithoutPanelFits);

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
    /// <summary>Rich + 有高度时应锚定。</summary>
    private static string? AnchorsWhenHeightKnown()
    {
        var cap = new TerminalCapability(TerminalTier.Rich, 120, IsRedirected: false, Height: 30);
        using var region = LiveRegion.Create(cap);

        return region.IsAnchored
            ? null
            : "Rich + 高度 30 时应锚定底部，实际未锚定";
    }

    /// <summary>没高度或非 Rich 时不锚定。</summary>
    /// <remarks>
    /// <b>降级必须是无条件的</b>：高度未知时锚定会画到屏幕外或错位，
    /// 而那比"位置不理想"糟得多。
    /// </remarks>
    private static string? NoAnchorWithoutHeight()
    {
        var noHeight = new TerminalCapability(TerminalTier.Rich, 120, IsRedirected: false, Height: 0);
        using var a = LiveRegion.Create(noHeight);
        if (a.IsAnchored) return "高度为 0 却锚定了";

        var narrow = new TerminalCapability(TerminalTier.Narrow, 70, IsRedirected: false, Height: 30);
        using var b = LiveRegion.Create(narrow);
        if (b.IsAnchored) return "Narrow 档不该锚定";

        var redirected = new TerminalCapability(TerminalTier.Rich, 120, IsRedirected: true, Height: 30);
        using var c = LiveRegion.Create(redirected);
        return c.IsAnchored ? "重定向时不该锚定" : null;
    }

    /// <summary>
    /// 锚定模式下三行块的位置：活动行 <c>height-2</c>、输入行 <c>height-1</c>、
    /// 状态栏贴屏幕最后一行 <c>height</c>。
    /// </summary>
    /// <remarks>
    /// <b>版式是用户 2026-11-05 指定的</b>：活动行（正在发生什么）在输入行**上方**，
    /// 状态栏（常驻参考）贴最底。P4 阶段把活动行与状态栏合并成一行贴底，
    /// 于是"正在忙"跑到了比输入行更低的位置——用户当场指出。
    ///
    /// <para>
    /// <b>为什么读原始控制序列而不是回放屏幕。</b>这条要看的是"块被定位到哪一行"，
    /// 而回放屏幕会把后来的重画覆盖到同一格上，看不出定位意图。
    /// 控制序列是能给出的最小断言：三行的行号必须严格递增且贴底。
    /// </para>
    /// </remarks>
    private static string? PromptAboveStatusBar()
    {
        var cap = new TerminalCapability(TerminalTier.Rich, 60, IsRedirected: false, Height: 20);

        var output = CaptureRegionOutput(cap, region =>
        {
            region.SetActivity("ACTIVITYMARK");
            region.SetStatus("STATUSBARMARK");
            region.BeginPrompt("PROMPTMARK");
        });

        var activityRow = RowOf(output, "ACTIVITYMARK");
        var statusRow = RowOf(output, "STATUSBARMARK");
        var promptRow = RowOf(output, "PROMPTMARK");

        if (activityRow is null) return "活动行从未被定位：" + DescribeControl(output);
        if (statusRow is null) return "状态栏从未被定位：" + DescribeControl(output);
        if (promptRow is null) return "提示符从未被定位：" + DescribeControl(output);

        // **核心断言**：活动行必须在提示符上方。这正是用户报的那个缺陷。
        if (activityRow >= promptRow)
            return $"活动行在第 {activityRow} 行、提示符在第 {promptRow} 行——"
                + "活动行必须在**输入行上方**";

        if (promptRow >= statusRow)
            return $"提示符在第 {promptRow} 行、状态栏在第 {statusRow} 行——提示符必须在状态栏上方";

        if (statusRow != cap.Height)
            return $"状态栏必须贴在最后一行（第 {cap.Height} 行），实际第 {statusRow} 行";

        if (promptRow != cap.Height - 1)
            return $"提示符必须在第 {cap.Height - 1} 行，实际第 {promptRow} 行";

        return activityRow == cap.Height - 2
            ? null
            : $"活动行必须在第 {cap.Height - 2} 行，实际第 {activityRow} 行";
    }

    /// <summary>
    /// 标记文本被写到了第几行（1-based）；找不到返回 null。
    /// </summary>
    /// <remarks>
    /// <b>必须真解析 CSI，不能用"往回找 <c>ESC[</c>"。</b>
    /// 第一版就是这么写的，于是 <c>ESC[2K</c>（清行）也被当成定位序列：
    /// 它取到参数 <c>2K</c>、找不到 <c>;</c>、返回 null。结果是状态栏
    /// "从未被定位"——检查在**实现已经正确**的情况下报红，把人引向错误的修复方向。
    ///
    /// <para>
    /// 现在按字符走：遇到 <c>ESC [</c> 就吃完整条 CSI（参数 + 终止字节），
    /// 只把终止字节为 <c>H</c>/<c>f</c> 的当作定位，并记住当前行；
    /// 遇到普通文本时与标记比对。<c>ESC 7</c>/<c>ESC 8</c>（DECSC/DECRC）
    /// 与 <c>ESC[2K</c> 都不会被误认。
    /// </para>
    /// </remarks>
    private static int? RowOf(string output, string marker)
    {
        const char Escape = '\u001b';
        var row = 0;

        for (var i = 0; i < output.Length;)
        {
            if (output[i] == Escape)
            {
                var j = i + 1;

                // CSI：ESC [ 参数… 终止字节。参数里可能有 ';' 与 '?'。
                if (j < output.Length && output[j] == '[')
                {
                    j++;
                    while (j < output.Length
                        && (char.IsAsciiDigit(output[j]) || output[j] is ';' or '?'))
                        j++;

                    if (j >= output.Length) return null;

                    if (output[j] is 'H' or 'f')
                    {
                        var parameters = output[(i + 2)..j];
                        var semi = parameters.IndexOf(';');
                        var rowText = semi >= 0 ? parameters[..semi] : parameters;
                        if (int.TryParse(rowText, out var parsed)) row = parsed;
                    }

                    i = j + 1;
                    continue;
                }

                // 两字节转义（ESC 7 / ESC 8）：整体跳过。
                i = Math.Min(j + 1, output.Length);
                continue;
            }

            if (string.CompareOrdinal(output, i, marker, 0, marker.Length) == 0)
                return row == 0 ? null : row;

            i++;
        }

        return null;
    }

    /// <summary>
    /// 提示符由区域画，且随后的正文输出不会把它擦掉。
    /// </summary>
    /// <remarks>
    /// <b>这是真机上抓到的一个缺陷</b>：第一版 `BeginPrompt` 只把光标挪到
    /// 最后一行，提示符仍由调用方 `Output.Text("> ")` 写。而那次写会走
    /// <c>Guard</c> → <c>RunExclusive</c> → <c>Erase</c>，把刚写上去的
    /// 提示符擦掉——用户看到的是"没有提示符，光标孤零零停在底部"。
    ///
    /// <para>
    /// 正确的划分：**提示符是底部区域的内容，由区域自己画**；
    /// 输入期间任何写正文的调用都必须先把光标交还正文区，
    /// 绝不能糊在输入行上。
    /// </para>
    ///
    /// <para>
    /// <b>断言用 VT 回放而不是查控制序列</b>：新版布局里正文不再擦除重画
    /// （正文被滚动区限制，碰不到底部三行），所以"有没有擦提示符"
    /// 已经不是一个有意义的问法。真正要证明的是**最终屏幕上提示符还在、
    /// 正文落在它上方**——那正是用户能看到的东西。
    /// </para>
    /// </remarks>
    private static string? PromptSurvivesErase()
    {
        var cap = new TerminalCapability(TerminalTier.Rich, 120, IsRedirected: false, Height: 24);

        var (live, _) = ReplayRegion(cap, region =>
        {
            if (!region.BeginPrompt("PROMPTMARK"))
                throw new InvalidOperationException(
                    "锚定模式下 BeginPrompt 应接管提示符，实际返回 false");

            // 输入期间写正文（例如后台任务打印）：必须回到正文区，不能糊在输入行。
            region.RunExclusive(() => Output.Control("正文一\n"));
            region.RunExclusive(() => Output.Control("正文二\n"));
        });

        var lines = live.Text().Split('\n');

        var promptIndex = Array.FindIndex(lines, l => l.Contains("PROMPTMARK", StringComparison.Ordinal));
        if (promptIndex < 0) return "输入期间写正文之后提示符消失了：\n" + live.Dump();

        var bodyIndex = Array.FindIndex(lines, l => l.Contains("正文二", StringComparison.Ordinal));
        if (bodyIndex < 0) return "正文没写出来：\n" + live.Dump();

        if (bodyIndex >= promptIndex)
            return $"正文写到了第 {bodyIndex} 行、提示符在第 {promptIndex} 行——正文必须在上方：\n"
                + live.Dump();

        if (promptIndex != cap.Height - 2)
            return $"提示符应停在第 {cap.Height - 1} 行（0-based {cap.Height - 2}），实际 {promptIndex}：\n"
                + live.Dump();

        // 非锚定时不接管，调用方照旧写。
        var plain = new TerminalCapability(TerminalTier.Plain, 0, IsRedirected: true);
        using var plainRegion = LiveRegion.Create(plain);
        return plainRegion.BeginPrompt("> ")
            ? "Plain 档不该接管提示符——会破坏逐字断言"
            : null;
    }

    /// <summary>
    /// 捕获 <see cref="LiveRegion"/> 实际写出的控制序列。
    /// </summary>
    /// <remarks>
    /// <b>必须重定向 stdout</b>：<c>LiveRegion</c> 内部走 <c>Output.Control</c>，
    /// 那条路直接写 <see cref="Console.Out"/>。不重定向就看不到它写了什么，
    /// 检查也就验不到东西——第一版正是这个毛病。
    /// </remarks>
    private static string CaptureRegionOutput(
        TerminalCapability capability, Action<LiveRegion> action)
    {
        ArgumentNullException.ThrowIfNull(action);

        var original = Console.Out;
        using var writer = new StringWriter();
        Console.SetOut(writer);
        try
        {
            // **区域在重定向内创建、也在重定向内释放。**
            // 第一版是在调用方 `using var region = ...`，于是 Dispose
            // 发生在 Console.SetOut(original) **之后**——擦除序列泄进真实终端。
            // 检查本身是绿的，却在跑检查的终端上留下一堆 ESC 序列。
            using var region = LiveRegion.Create(capability);
            action(region);
        }
        finally
        {
            Console.SetOut(original);
        }

        return writer.ToString();
    }

    /// <summary>Rich 档要能读到高度。</summary>
    private static string? HeightCaptured()
    {
        var cap = TerminalCapability.Detect(
            isRedirected: false, () => 120, () => 30);

        if (cap.Height != 30) return $"高度应为 30，实际 {cap.Height}";
        if (!cap.CanAnchorBottom) return "Rich + 有高度时应能做底部锚定";
        return null;
    }

    /// <summary>
    /// 高度读不到**不降档**。
    /// </summary>
    /// <remarks>
    /// <b>与宽度的判据不同</b>：宽度读不到说明"这不是终端"（重定向、CI 捕获），
    /// 所以退回 Plain。而高度读不到只说明"这个终端不报高度"，
    /// 宽度已经证明它是真终端——降成 Plain 会让颜色和边框一起丢掉，
    /// 那是把一个排版限制放大成功能损失。
    /// </remarks>
    private static string? HeightFailureKeepsTier()
    {
        var cap = TerminalCapability.Detect(
            isRedirected: false, () => 120,
            readHeight: () => throw new IOException("读不到高度"));

        if (cap.Tier is not TerminalTier.Rich)
            return $"高度读不到不该降档，实际 {cap.Tier}";

        if (cap.Height != 0) return $"读不到时高度应记 0，实际 {cap.Height}";
        return cap.CanAnchorBottom ? "高度为 0 时不该声称能锚定" : null;
    }

    /// <summary>只有 Rich 档才清屏。</summary>
    private static string? ClearOnlyWhenRich()
    {
        // Plain：不该清屏（46 个套件断言逐字文本）。
        Output.OverrideCapability(new TerminalCapability(TerminalTier.Plain, 0, true));
        if (CapturedClearScreen() is { Length: > 0 })
            return "Plain 档清屏了——会污染被断言的输出";

        // Rich：该清屏。
        Output.OverrideCapability(new TerminalCapability(TerminalTier.Rich, 120, false, 30));
        var rich = CapturedClearScreen();
        if (rich.Length == 0) return "Rich 档没有清屏——banner 不会从顶部开始";

        return rich.Contains("[2J", StringComparison.Ordinal)
            && rich.Contains("[H", StringComparison.Ordinal)
                ? null
                : $"清屏序列不对：{DescribeControl(rich)}";
    }

    /// <summary>重定向时整条启动路径不打任何控制序列。</summary>
    private static string? RedirectedEmitsNothing()
    {
        Output.OverrideCapability(
            new TerminalCapability(TerminalTier.Plain, 0, IsRedirected: true));

        if (CapturedClearScreen() is { Length: > 0 }) return "重定向时清屏了";
        return CapturedClearScreen(exit: true) is { Length: > 0 }
            ? "重定向时退出补了换行——会破坏逐字断言"
            : null;
    }

    /// <summary>捕获清屏/退出方法的实际输出（重定向 Console.Out）。</summary>
    private static string CapturedClearScreen(bool exit = false)
    {
        var original = Console.Out;
        using var writer = new StringWriter();
        Console.SetOut(writer);
        try
        {
            if (exit) Output.FinishForExit();
            else Output.ClearScreenForStartup();
        }
        finally
        {
            Console.SetOut(original);
        }

        return writer.ToString();
    }

    private static string DescribeControl(string s) =>
        s.Replace("", "ESC", StringComparison.Ordinal).Trim();

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

    /// <summary>
    /// 每个 <c>ActivityPhase</c> 都必须有可显示的文案与面孔。
    /// </summary>
    /// <remarks>
    /// <b>这条钉的是"加阶段但忘了加文案"</b>：枚举加一个成员是编译期的事，
    /// 而文案表是两个字典——漏了不报错，只在运行时那个阶段显示空白/回落到
    /// 通用文案。新增 <c>Retrying</c> 时正是这种风险，所以补上穷举。
    /// </remarks>
    private static string? EveryPhaseLabelled()
    {
        foreach (var phase in Enum.GetValues<ActivityPhase>())
        {
            // **只查真的会被渲染的阶段**。Idle 与 Streaming 被 ShouldDisplay
            // 排除（前者隐藏、后者正文本身就是内容），它们不需要文案——
            // 要求它们有文案是检查写错了，不是代码缺了。
            if (phase is ActivityPhase.Idle or ActivityPhase.Streaming) continue;

            if (!StatusLine.HasLabel(phase))
                return $"阶段 {phase} 会被显示却没有文案";
        }

        return null;
    }

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

    /// <summary>
    /// Narrow 档下，长行不会被 Spectre 重新折行。
    /// </summary>
    /// <remarks>
    /// <b>这是 <see cref="Output.CreateConsole"/> 设 <c>Profile.Width</c> 的副作用面</b>：
    /// 修复前 Spectre 在非交互模式下按 80 列兜底，修复后按真实宽度。
    /// 对 Plain 档无影响（宽度为 0，不设），但 Narrow 档（60–79 列）
    /// 现在会设成真实宽度——若某处依赖"80 列兜底"这个行为，它会变。
    ///
    /// <para>
    /// 断言的是"长度 75 的一行在 75 列终端里不被拆开"——
    /// 修复前它恰好也不拆（75 &lt; 80），所以这条检查**不能**作为
    /// 修复的负控；它守的是"宽度设置没有把 Narrow 档改坏"。
    /// </para>
    /// </remarks>
    private static string? NarrowDoesNotRewrap()
    {
        const int width = 75;
        var longLine = new string('x', width - 1);

        return CaptureWithTier(TerminalTier.Narrow, width,
            () => Output.Line(longLine),
            captured =>
            {
                var lines = captured.Split('\n', StringSplitOptions.RemoveEmptyEntries);
                return lines.Length == 1
                    ? null
                    : $"{width} 列终端里一行 {width - 1} 字符被拆成 {lines.Length} 行";
            });
    }

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
    /// 但"绝不在管道里写 ANSI"必须钉死——否则 44 个套件的断言会被污染。
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

    /// <summary>状态栏同样不能溢出（模型名 + 中文 + 进度条拼起来更长）。</summary>
    private static string? StatusAccountsWideChars()
    {
        foreach (var width in new[] { 40, 60, 80, 120 })
        {
            var line = StatusLine.RenderStatus(
                "深度求索-很强的模型名字", 120_000, 200_000, width);

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

    /// <summary>
    /// 宽度偏差方向必须一致：只许偏宽，不许偏窄。
    /// </summary>
    /// <remarks>
    /// <b>为什么方向比精确更重要</b>：偏宽的表现是"少显示几个字"，
    /// 用户看不出异常；偏窄会写满最右列触发终端自动折行，
    /// 光标跳到下一行，后续定位序列全部错位——那是布局崩坏，
    /// 2026-10-29 真机上表现为活动行文字消失。
    /// <para>
    /// 具体例子：<c>❤️</c>（U+2764 + 变体选择符）。文本呈现时终端显示 1 格，
    /// emoji 呈现时 2 格。本实现固定按 2 算，即偏宽 1 格——
    /// 这是有意的取舍，不是遗漏。完整字形簇判定需要 ICU 或自带表，
    /// 与"状态行别错位"的收益不成比例。
    /// </para>
    /// </remarks>
    private static string? WidthBiasIsWide()
    {
        // 每个都是"终端实际显示宽度可能小于逐码点累加值"的组合。
        string[] mustNotUnderCount =
        [
            "\u2764\uFE0F",                        // 变体选择符：实际 1 或 2，不可是 0
            "\U0001F3F3\uFE0F",                    // 白旗 + VS16
            "1\uFE0F\u20E3",                       // 键帽 1️⃣
            "\U0001F1E8\U0001F1F3",                // 国旗 🇨🇳（两个区域指示符）
        ];

        foreach (var text in mustNotUnderCount)
        {
            var width = Dougong.Cli.Terminal.DisplayWidth.Of(text);

            // 下限：至少 1 格。算成 0 格意味着"整段看不见"，
            // 那会让这一行在布局里凭空多出空间。
            if (width < 1)
                return $"该文本算成 {width} 格——宽到看不见，方向反了";

            // 上限：不能荒谬地大（每个 UTF-16 单元至多 2 格）。
            if (width > text.Length * 2)
                return $"该文本算成 {width} 格，超过 {text.Length}×2 的上限";
        }

        return null;
    }

    /// <summary>
    /// <remarks>
    /// <b>为什么必须硬编码</b>：此前的做法是测试自己再抄一份区间表，
    /// 然后断言"两份一致"。那验证的是抄写一致性，不是正确性——
    /// 两边同时把 emoji 算错时，检查依然通过。
    /// <para>
    /// 期望值取自 Unicode East Asian Width 的 Wide/Fullwidth 两类，
    /// 以及与终端实测的对照。emoji 一律 2 格：<c>😀</c> 在 UTF-16 里是两个
    /// <c>char</c>，旧实现在按 <c>char</c> 遍历时各算 1 格，
    /// 合计 2 格——**碰巧正确**，所以这个错误长期看不出来。
    /// 真正暴露它的是 <c>👨‍👩‍👧‍👦</c>（11 个 char，真实 2 格）。
    /// </para>
    /// </remarks>
    private static string? WidthGoldenValues()
    {
        (string Text, int Expected, string Why)[] cases =
        [
            ("abc", 3, "ASCII 每字 1 格"),
            ("中文", 4, "CJK 每字 2 格"),
            ("\U0001F600", 2, "单个 emoji 2 格"),
            ("a\U0001F600b", 4, "混合：1 + 2 + 1"),
            ("\U0001F468\u200D\U0001F469\u200D\U0001F467\u200D\U0001F466", 8,
                "👨‍👩‍👧‍👦 的 ZWJ 不占位，四个 emoji 各 2 格=8"
                + "（终端多半渲染成 2，但我们按码点累加，宁可偏宽不可偏窄）"),
        ];

        foreach (var (text, expected, why) in cases)
        {
            // 用被测实现算，与硬编码期望比对。这是外部基准，不是自我一致。
            var actual = Dougong.Cli.Terminal.DisplayWidth.Of(text);
            if (actual != expected)
                return $"'{text}' 宽度应为 {expected}（{why}），实际 {actual}";
        }

        return null;
    }

    /// <summary>
    /// 截断在代理对中间时，不能切出孤立高代理。
    /// </summary>
    /// <remarks>
    /// <b>这是一个会让终端显示乱码的真实缺陷</b>：按 <c>char</c> 遍历截断，
    /// 宽度恰好卡在 <c>😀</c> 的两个代理项之间时会 Append 高代理
    /// <c>U+D83D</c> 就停。那个孤立代理在终端里渲染成 <c>�</c>，
    /// 而且它后面接 ANSI 序列会让后续颜色也一起错。
    /// <para>
    /// 验法：构造一串让截断点正好落在代理对中间的文本，检查结果里没有孤立代理。
    /// </para>
    /// </remarks>
    private static string? TruncateNoLoneSurrogate()
    {
        // 宽度 3 时 limit=2。"a😀b" 的宽度是 1+2+1：
        //   - 正确实现（按 Rune）：a(1) 后 😀 需要 2，1+2=3 > 2，只输出 a
        //   - 错误实现（按 char）：a(1) + 高代理(1) = 2 <= 2，输出 a + 孤立高代理
        var capability = new Dougong.Cli.Terminal.TerminalCapability(
            Dougong.Cli.Terminal.TerminalTier.Rich, Width: 3, IsRedirected: false);

        var original = Console.Out;
        var buffer = new StringWriter();
        try
        {
            Console.SetOut(buffer);
            using var region = Dougong.Cli.Terminal.LiveRegion.Create(capability, force: true);
            region.SetActivity("a\U0001F600b");
        }
        finally
        {
            Console.SetOut(original);
        }

        var raw = buffer.ToString();
        // 去掉 ANSI 控制序列后检查代理对完整性。
        var text = Escape(raw);

        for (var i = 0; i < text.Length; i++)
        {
            if (char.IsHighSurrogate(text[i]))
            {
                if (i + 1 >= text.Length || !char.IsLowSurrogate(text[i + 1]))
                    return $"输出里有孤立高代理（U+{(int)text[i]:X4}）：{Escape(text)}";
                i++; // 跳过合法的低代理
            }
            else if (char.IsLowSurrogate(text[i]))
            {
                return $"输出里有孤立低代理（U+{(int)text[i]:X4}）：{Escape(text)}";
            }
        }

        return null;

        static string Escape(string s) =>
            string.Concat(s.Select(c => c < 0x20 || c > 0x7E ? $"\\u{(int)c:X4}" : c.ToString()));
    }

    /// <summary>
    /// 测宽用的独立实现。**区间必须与 StatusLine.IsWide 一致**。
    /// </summary>
    /// <remarks>
    /// 早先这里只把 CJK 判为 2 格，而被测代码还覆盖 U+2E80–U+303E 等区间
    /// （含 <c>◉</c> 这类符号）。两边口径不一致，"补白有没有补满整行"
    /// 这种检查会永远对不上而假通过——实测踩到过。
    /// </remarks>
    private static int DisplayWidthOf(string text)
    {
        var width = 0;
        foreach (var c in text)
        {
            if (c == '\u001b') continue; // 本套件断言的都是不含 ANSI 的纯文本
            width += IsWideChar(c) ? 2 : 1;
        }
        return width;
    }

    /// <summary>与 <c>StatusLine.IsWide</c> 保持同一套区间。</summary>
    private static bool IsWideChar(char c) => c is
        >= '\u1100' and <= '\u115F' or
        >= '\u2E80' and <= '\u303E' or
        >= '\u3041' and <= '\u33FF' or
        >= '\u3400' and <= '\u4DBF' or
        >= '\u4E00' and <= '\u9FFF' or
        >= '\uA000' and <= '\uA4CF' or
        >= '\uAC00' and <= '\uD7A3' or
        >= '\uF900' and <= '\uFAFF' or
        >= '\uFE30' and <= '\uFE6F' or
        >= '\uFF00' and <= '\uFF60' or
        >= '\uFFE0' and <= '\uFFE6';

    /// <summary>
    /// 不活动的区域**不得向终端写任何字节**。
    /// </summary>
    /// <remarks>
    /// 这是 S3 的安全底线：44 个套件都靠 stdout 与改造前逐字比对，
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
                region.SetStatus("  模型 · 1s");
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
    /// 清空之后屏幕上必须一点残留都没有。
    /// </summary>
    /// <remarks>
    /// <b>这条原先查的是「输出里有没有上移序列」</b>——那是实现细节，
    /// 重写擦除算法（改用 save/restore 光标）之后它立刻误报。
    /// 改成 VT 回放断言屏幕内容：不管用什么序列，清完就该是空的。
    /// 与 <c>vt-replay-shrink-leaves-no-ghost</c> 的区别是这里走 2 → 0
    /// （整块清空），那条走 2 → 1（少画一行）。
    /// </remarks>
    private static string? LiveRegionClears()
    {
        var (live, _) = ReplayRegion(rich =>
        {
            rich.SetActivity("  思考中…");
            rich.SetStatus("  模型 · 1s");
            rich.SetActivity(null);
            rich.SetStatus(null);
        });

        var text = live.Text();
        if (text.Trim().Length != 0)
            return "清空后仍有残留：\n" + live.Dump();

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
    /// **锚定模式下 Dispose 必须把底部三行擦干净。**
    /// </summary>
    /// <remarks>
    /// <b>这条抓的是一个真实缺陷</b>：<c>Clear()</c> 原先只调相对模式的擦除，
    /// 而那个方法在锚定模式下直接返回。于是退出时底部三行原样留在屏幕上——
    /// 用户回到 shell 之后还能看到「重试中…」。
    ///
    /// <para>
    /// <b>为什么原有的 <c>vt-replay-shows-activity-then-clean</c> 没抓到</b>：
    /// 它用默认能力（不带高度）建区域，走的是**相对模式**——那条路径的擦除
    /// 一直是好的。锚定路径完全不同，需要单独的检查。
    /// </para>
    ///
    /// <para>
    /// 判据是**三行都为空**，不只是"活动行没了"：状态栏与输入行同样会残留，
    /// 而只查活动行会让"少清一行"通过。
    /// </para>
    /// </remarks>
    private static string? AnchoredClearsOnDispose()
    {
        var cap = new TerminalCapability(TerminalTier.Rich, 80, IsRedirected: false, Height: 16);

        var (_, afterDispose) = ReplayRegion(cap, rich =>
        {
            rich.SetActivity("ACTIVITYMARK");
            rich.SetStatus("STATUSMARK");
            rich.BeginPrompt("PROMPTMARK");
        });

        var lines = afterDispose.Text().Split('\n');

        var waste = new List<string>();
        foreach (var marker in new[] { "ACTIVITYMARK", "STATUSMARK", "PROMPTMARK" })
        {
            if (afterDispose.Text().Contains(marker, StringComparison.Ordinal)) waste.Add(marker);
        }

        return waste.Count == 0
            ? null
            : $"退出后底部块仍留在屏幕上（{string.Join("、", waste)}）：\n" + afterDispose.Dump();
    }

    /// <summary>
    /// 正文写入不能被区块覆盖，且必须落在活动行**上方**。
    /// </summary>
    /// <remarks>
    /// <b>判据变了，因为结构变了</b>：旧实现是"擦→写→下一 tick 重画"，
    /// 所以只能验"正文完整"。新实现里正文被 DECSTBM 限制在 <c>1..height-3</c>，
    /// 块在区外，两者根本碰不到——于是可以断言更强的性质：
    /// <b>正文的每一行都在活动行上方</b>。
    /// </remarks>
    private static string? VtReplayBodyIntact()
    {
        // **必须用锚定能力**：相对模式（高度未知）下块画在光标下方，
        // 每次写正文前都要把它擦掉让位，所以写完正文时块本来就不在。
        // 真实富终端走的是锚定路径，检查必须验那条。
        var cap = new TerminalCapability(TerminalTier.Rich, 80, IsRedirected: false, Height: 16);

        var (live, _) = ReplayRegion(cap, rich =>
        {
            rich.SetActivity("ACTIVITYMARK");
            rich.RunExclusive(() => Output.Control("正文第一行\n"));
            rich.RunExclusive(() => Output.Control("正文第二行\n"));
        });

        var lines = live.Text().Split('\n');

        if (!live.Text().Contains("正文第一行", StringComparison.Ordinal))
            return "正文第一行丢失：\n" + live.Dump();

        if (!live.Text().Contains("正文第二行", StringComparison.Ordinal))
            return "正文第二行丢失：\n" + live.Dump();

        var activityIndex = Array.FindIndex(lines, l => l.Contains("ACTIVITYMARK", StringComparison.Ordinal));
        if (activityIndex < 0) return "活动行不见了：\n" + live.Dump();

        var secondBody = Array.FindIndex(lines, l => l.Contains("正文第二行", StringComparison.Ordinal));
        if (secondBody >= activityIndex)
            return $"正文写到了第 {secondBody} 行、活动行在第 {activityIndex} 行——正文必须在活动行上方：\n"
                + live.Dump();

        return null;
    }

    /// <summary>
    /// 活动行从有到无时，那一行必须真的消失而不是留下鬼影。
    /// </summary>
    /// <remarks>
    /// 这是 LiveRegion 里最难写对的一处：只画新内容、不清旧行，
    /// 旧的一行会留在屏幕上。VT 回放能直接看到那行鬼影。
    /// <para>
    /// <b>状态栏此时必须留着</b>：它是常驻参考信息，不随忙闲变化。
    /// 只验"活动的没了"会让"顺手把状态栏也擦了"通过——那条同样是缺陷。
    /// </para>
    /// </remarks>
    private static string? VtReplayShrink()
    {
        var cap = new TerminalCapability(TerminalTier.Rich, 80, IsRedirected: false, Height: 16);

        // 相对模式下"收起"会把整块擦掉，状态栏当然也不在——那条路本来就
        // 不该常驻。要验"活动行消失但状态栏留下"，只能在锚定模式里验。
        var (live, _) = ReplayRegion(cap, rich =>
        {
            rich.SetActivity("  活动行内容");
            rich.SetStatus("  状态栏内容");
            rich.SetActivity(null);
        });

        var text = live.Text();

        if (text.Contains("活动行内容", StringComparison.Ordinal))
            return "活动行消失后仍在屏幕上（鬼影）：\n" + live.Dump();

        return text.Contains("状态栏内容", StringComparison.Ordinal)
            ? null
            : "活动行收起时状态栏被一起擦掉了（它是常驻的）：\n" + live.Dump();
    }

    /// <summary>
    /// 在受控的 Console.Out 下跑一段区域操作，返回渲染后的屏幕与原始输出。
    /// </summary>
    private static (VtScreen Live, VtScreen AfterDispose) ReplayRegion(Action<LiveRegion> action) =>
        ReplayRegion(new TerminalCapability(TerminalTier.Rich, 80, IsRedirected: false), action);

    private static (VtScreen Live, VtScreen AfterDispose) ReplayRegion(
        TerminalCapability capability, Action<LiveRegion> action)
    {
        // stderr 与 stdout 写的是同一块屏幕，只是流不同。两者必须拼成一条
        // 字节流再喂给模拟器，否则看不出"stderr 打在活动行上面"这类叠加。
        var captured = new StringWriter();
        var originalOut = Console.Out;
        var originalError = Console.Error;

        // 两个流写进同一个 StringWriter：终端只有一块屏幕。
        Console.SetOut(captured);
        Console.SetError(captured);

        LiveRegion? region = null;
        string whileLive;

        try
        {
            region = LiveRegion.Create(capability);
            if (!region.IsActive) throw new InvalidOperationException("富终端应激活区域");

            action(region);

            // **必须在 Dispose 之前取快照**：Dispose 会调 Clear 把区域擦净，
            // 那之后再读屏幕永远是空的——第一版检查就栽在这里，误报"第一行丢失"。
            whileLive = captured.ToString();
        }
        finally
        {
            region?.Dispose();
            Console.SetOut(originalOut);
            Console.SetError(originalError);
        }

        var screenRows = capability.Height > 0 ? capability.Height : 12;
        var screenColumns = capability.Width > 0 ? capability.Width : 80;

        var live = new VtScreen(columns: screenColumns, rows: screenRows);
        live.Feed(whileLive);

        // 退出后的屏幕：从同一段字节接着喂，看 Clear 是否真的擦干净了。
        var afterDispose = new VtScreen(columns: screenColumns, rows: screenRows);
        afterDispose.Feed(captured.ToString());

        return (live, afterDispose);
    }

    /// <summary>
    /// 极简 VT 模拟器：只实现本产品实际用到的序列。
    /// </summary>
    /// <remarks>
    /// <b>为什么自己写而不是引第三方</b>：需要的语义不多（换行、回车、
    /// 清行、定位、滚动区），引一个完整终端模拟器进来反而要花更多时间
    /// 确认它的行为。未识别的序列一律抛异常——静默跳过会让
    /// "被测代码用了模拟器不支持的序列"伪装成 PASS。
    /// </remarks>
    private sealed class VtScreen(int columns, int rows)
    {
        private readonly char[][] _grid = Enumerable.Range(0, rows)
            .Select(_ => Enumerable.Repeat(' ', columns).ToArray()).ToArray();

        private int _row;
        private int _column;
        private int _savedRow;
        private int _savedColumn;

        /// <summary>滚动区上界（0-based，含）。</summary>
        private int _scrollTop;

        /// <summary>滚动区下界（0-based，含）。</summary>
        private int _scrollBottom = rows - 1;

        /// <summary>
        /// Scroll the region up one row; its last row becomes blank.
        /// </summary>
        /// <remarks>
        /// <b>必须按滚动区滚，不是按整屏。</b>底部固定块靠 DECSTBM 把正文
        /// 限制在上半屏；模拟器若按整屏滚，被限制住的块会跟着正文往上跑，
        /// 于是"块有没有被正文推走"这条检查永远看不出来——它恰恰是要证明的事。
        /// </remarks>
        private void ScrollUp()
        {
            for (var r = _scrollTop; r < _scrollBottom; r++)
                Array.Copy(_grid[r + 1], _grid[r], columns);

            Array.Fill(_grid[_scrollBottom], ' ');
        }

        /// <summary>换行：移到下一行，必要时滚动。</summary>
        /// <remarks>
        /// <b>光标在滚动区下界时滚区</b>；不在区里时按整屏滚。
        /// 早期版本把光标夹在最后一行不滚，于是"底部块被长正文推走"这类
        /// 缺陷在模拟器里完全不可见。
        /// </remarks>
        private void LineFeed()
        {
            if (_row == _scrollBottom)
            {
                ScrollUp();
                return;
            }

            if (_row >= rows - 1) ScrollUp();
            else _row++;
        }

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

                // DECSC / DECRC：两字节序列，不是 CSI。
                if (c == '\u001b' && i + 1 < text.Length)
                {
                    if (text[i + 1] == '7') { _savedRow = _row; _savedColumn = _column; i++; continue; }
                    if (text[i + 1] == '8') { _row = _savedRow; _column = _savedColumn; i++; continue; }
                }

                switch (c)
                {
                    case '\n':
                        // **ONLCR**：真实 tty 在输出 `\n` 时把它转成 `\r\n`
                        // （`stty onlcr` 是 POSIX 的默认），所以换行后列归零。
                        //
                        // 模拟器早先只下移行、保留列，于是被测代码写
                        // `"body 0\n"` 这类不带 `\r` 的正文时，每行都比上一行
                        // 右移一段——那是模拟器的错，不是产品的错。
                        // 这个差异会把"正文写满滚动区"的检查带偏。
                        LineFeed();
                        _column = 0;
                        break;
                    case '\r': _column = 0; break;
                    default:
                        // **写满最后一列要自动折行**。真实终端就是这么做的，
                        // 而底部区域依赖"写完这一行光标还留在这一行"来定位下一行。
                        // 早期版本直接丢弃越界字符、不折行，于是"补白正好补满整行"
                        // 这种缺陷在模拟器里完全看不出来——真机上却是活动行文字
                        // 全部消失（2026-10-29 用户截图）。
                        _grid[_row][_column] = c;
                        _column++;

                        if (_column >= columns)
                        {
                            _column = 0;
                            _row = Math.Min(rows - 1, _row + 1);
                        }
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
                    _row = Math.Max(0, _row - ParseOrOne(parameter));
                    break;

                case 'B':
                    // 光标下移 N 行。
                    _row = Math.Min(rows - 1, _row + ParseOrOne(parameter));
                    break;

                case 'E':
                    // CNL：下移 N 行并回到行首。
                    _row = Math.Min(rows - 1, _row + ParseOrOne(parameter));
                    _column = 0;
                    break;

                case 'K':
                    // 清行：2 = 整行清空。
                    Array.Fill(_grid[_row], ' ');
                    break;

                case 'r':
                {
                    // DECSTBM：设滚动区，参数是 "上界;下界"（1-based，含两端）。
                    //
                    // **无参即复原整屏**，这是退出时还原的写法。
                    // 两个参数都缺省时 CSI 参数串是空串，必须当成整屏。
                    //
                    // 光标会被移到滚动区左上角——这是真实终端的行为，
                    // 不是模拟器的近似。被测代码因此必须自己 save/restore，
                    // 模拟器"体贴地"保留光标就验不出那个缺陷。
                    var parts = parameter.Split(';');
                    var top = parts.Length > 0 ? ParseOrOne(parts[0]) : 1;
                    var bottom = parts.Length > 1 ? ParseOrOne(parts[1]) : rows;

                    if (parameter.Length == 0)
                    {
                        top = 1;
                        bottom = rows;
                    }

                    top = Math.Clamp(top, 1, rows);
                    bottom = Math.Clamp(bottom, 1, rows);

                    // 下界小于上界是非法参数，终端会忽略整条序列。
                    if (bottom <= top)
                    {
                        _row = 0;
                        _column = 0;
                        break;
                    }

                    _scrollTop = top - 1;
                    _scrollBottom = bottom - 1;

                    _row = _scrollTop;
                    _column = 0;
                    break;
                }

                case 'H':
                {
                    // CUP: absolute cursor position, 1-based. Used by the
                    // bottom-anchored region (P3) to place the status line at a
                    // fixed absolute row instead of a relative offset.
                    //
                    // Parameters are "row;column"; a missing part means 1.
                    var parts = parameter.Split(';');
                    var row = parts.Length > 0 ? ParseOrOne(parts[0]) : 1;
                    var column = parts.Length > 1 ? ParseOrOne(parts[1]) : 1;

                    _row = Math.Clamp(row - 1, 0, rows - 1);
                    _column = Math.Clamp(column - 1, 0, columns - 1);
                    break;
                }

                case 's':
                    // **故意不支持**。ESC[s/ESC[u 是 SCO 私有序列，
                    // 真实终端与 pyte 都不保证保存/还原位置——用 pyte 实测过：
                    // ESC[s...ESC[u 之后光标停在错误位置，ESC7/ESC8 才正确。
                    // 这里直接报错，逼实现用 DECSC/DECRC，而不是让模拟器
                    // "宽容地"把它当成能用的序列（那样检查会假通过）。
                    throw new InvalidOperationException(
                        "被测代码用了 ESC[s（SCO 保存光标），真实终端不保证生效，应改用 ESC7");

                case 'u':
                    throw new InvalidOperationException(
                        "被测代码用了 ESC[u（SCO 恢复光标），真实终端不保证生效，应改用 ESC8");

                default:
                    // 未识别的序列必须让检查失败，不能静默跳过。
                    // 静默跳过会让"被测代码用了模拟器不支持的序列"伪装成 PASS。
                    throw new InvalidOperationException($"VT 模拟器不支持序列：ESC[{parameter}{final}");
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
    /// <summary>
    /// 流式输出的第一个 delta 不能覆盖提示符那一行。
    /// </summary>
    /// <remarks>
    /// <b>这是区域擦除最容易造成的副作用</b>：擦除后若把光标停在行首，
    /// 后续的 <see cref="Output.Text"/>（不带换行）就从列 0 开始写，
    /// 把用户刚输入的 "&gt; hello" 覆盖掉。
    /// <para>
    /// 真实的触发路径：区域在"思考中"可见 → 第一个 TextDelta 让状态切到流式 →
    /// 区域被收起（擦除）→ 紧接着写正文。此时光标列必须回到原来的位置。
    /// </para>
    /// </remarks>
    /// <summary>
    /// 忙时活动行是**独立的一行**，含 spinner 与阶段文案；状态栏是另一行，
    /// 含模型名。两者不许合并。
    /// </summary>
    /// <remarks>
    /// <b>这条检查的由来说明问题</b>：它原来是 <c>status-is-single-line-when-busy</c>，
    /// 断言"忙时只有一行、既在动又有模型名"。那是 P4 对 Hermes 版式的误读，
    /// 也是用户报的缺陷的根源——活动行因此在输入行下方。现在两者是两行，
    /// 断言随之反过来：<b>把它们合并回去必须变红</b>。
    /// </remarks>
    private static string? SingleLineBusy()
    {
        var state = new ActivityState();
        state.OnRunStarted(DateTimeOffset.UnixEpoch);

        var activity = StatusLine.RenderActivity(
            state, DateTimeOffset.UnixEpoch.AddSeconds(3), 120);

        var status = StatusLine.RenderStatus(
            "mimo-v2.6-flash", 12_000, 1_000_000, 120);

        if (activity is null) return "忙时应有活动行";

        if (activity.Contains(NewLineChar)) return "活动行含换行——应该是单行";
        if (status.Contains(NewLineChar)) return "状态栏含换行——应该是单行";

        if (!activity.Contains("思考中", StringComparison.Ordinal))
            return $"活动行缺少阶段文案：{activity}";

        if (!status.Contains("mimo-v2.6-flash", StringComparison.Ordinal))
            return $"状态栏缺少模型名：{status}";

        // **模型名不许出现在活动行里**：那是"环境"不是"事件"，
        // 混进来就说明两个槽位又被合并了。
        return activity.Contains("mimo-v2.6-flash", StringComparison.Ordinal)
            ? $"活动行里出现了模型名（两个槽位被合并了）：{activity}"
            : null;
    }

    /// <summary>
    /// 空闲时**状态栏仍然在**，而活动行让位。
    /// </summary>
    /// <remarks>
    /// 轮次结束就整块收起的话，用户看到"底部空荡"——Hermes 的空闲状态栏是常驻的。
    /// <para>
    /// 但<b>活动行不能常驻</b>：空闲时它显示一个转动的 spinner 会让人以为还在跑。
    /// 这两个方向都必须钉住，只验一个方向会让另一个方向的回归通过。
    /// </para>
    /// </remarks>
    private static string? StatusPersistsIdle()
    {
        var idle = new ActivityState();   // 初始就是 Idle

        var status = StatusLine.RenderStatus(
            "mimo-v2.6-flash", 12_000, 1_000_000, 120);

        if (string.IsNullOrWhiteSpace(status)) return "空闲时状态栏是空的——底部会显得空荡";

        if (!status.Contains("mimo-v2.6-flash", StringComparison.Ordinal))
            return $"空闲状态栏没有模型名：{status}";

        var activity = StatusLine.RenderActivity(
            idle, DateTimeOffset.UnixEpoch.AddSeconds(6), 120);

        return activity is null
            ? null
            : $"空闲时不该有活动行（会让人以为还在跑）：{activity}";
    }

    /// <summary>
    /// 放不下时**整段丢弃**，不是截断中间。
    /// </summary>
    /// <remarks>
    /// <b>为什么整段丢</b>：截断中间会切出"12k/1M ██░…"这种半截进度条，
    /// 既不精确也难看。Hermes 的判据是逐段 <c>fits()</c>，放不下就停——
    /// 这样保证"模型名和上下文占用永远完整可见"。
    /// </remarks>
    private static string? TailDroppedWhole()
    {
        var wide = StatusLine.RenderStatus("model", 12_000, 1_000_000, 200);
        var narrow = StatusLine.RenderStatus("model", 12_000, 1_000_000, 45);

        if (!wide.Contains('%')) return $"宽终端应显示百分比：{wide}";
        if (narrow.Contains('%')) return $"窄终端不该显示百分比（应压成 12K tok）：{narrow}";
        if (!narrow.Contains("tok", StringComparison.Ordinal))
            return $"窄终端应压成 compact 形式：{narrow}";

        // 极窄：尾部段全部丢弃，只剩模型名。
        var tiny = StatusLine.RenderStatus("model", 12_000, 1_000_000, 15);

        if (!tiny.Contains("model", StringComparison.Ordinal))
            return $"极窄终端仍应显示模型名（它不可压缩）：{tiny}";

        return tiny.Contains("tok", StringComparison.Ordinal)
            ? $"极窄终端不该有上下文段：{tiny}"
            : null;
    }

    /// <summary>段分隔符是 <c>│</c> 不是 <c>·</c>。</summary>
    private static string? VerticalBar()
    {
        var line = StatusLine.RenderStatus("mimo", 12_000, 1_000_000, 120);

        return line.Contains('│')
            ? null
            : $"状态栏没有用 │ 分隔：{line}";
    }

    /// <summary>换行符常量，避免字符字面量在编辑流程里被转义成真换行。</summary>
    private const char NewLineChar = (char)10;

    /// <summary>
    /// Body output that fills the screen must not make the region vanish.
    /// </summary>
    /// <remarks>
    /// <b>What this check does NOT prove</b>: it does not prove anchoring works.
    /// Verified by negative control -- switching the region back to the
    /// relative path leaves this check green, because a cursor-relative region
    /// follows the body and is therefore never pushed off. Only the three
    /// anchoring-specific checks go red in that case.
    ///
    /// <summary>
    /// **用户报的那个缺陷本身**：忙时活动行必须渲染在输入行上方。
    /// </summary>
    /// <remarks>
    /// <b>这条是整个三槽位改造的验收点</b>（用户 2026-11-05 的截图：
    /// 重试提示与活动行全在 `&gt; 你好` 下面）。
    ///
    /// <para>
    /// <b>必须验渲染后的屏幕，不能只验控制序列里的行号</b>：
    /// 行号正确但被后续写入覆盖、或光标定位被滚动打乱，屏幕上的次序
    /// 仍然可能是错的——而用户看到的就是屏幕。所以这里跑完整的
    /// "提问 → 正文 → 忙起来"流程，再按屏幕行号比较。
    /// </para>
    ///
    /// <para>
    /// <b>同时要有反向对照</b>：空闲时活动行那一行必须是空的。
    /// 只验"忙时在上方"会让"活动行常驻"通过，而常驻的 spinner
    /// 会让用户以为它一直在跑。
    /// </para>
    /// </remarks>
    private static string? ActivityAboveInput()
    {
        var cap = new TerminalCapability(TerminalTier.Rich, 80, IsRedirected: false, Height: 16);

        var (busy, idle) = ReplayRegion(cap, rich =>
        {
            // 用户提了两次问，中间有一轮正文。
            rich.BeginPrompt("> ");
            rich.CommitPrompt("你好");
            rich.RunExclusive(() => Output.Control("回答一\n"));

            rich.BeginPrompt("> ");
            rich.CommitPrompt("再说一次");
            rich.RunExclusive(() => Output.Control("回答二\n"));

            // 第二次提问之后忙起来（这正是用户截图里的时刻）。
            rich.SetActivity("ACTIVITYMARK");
            rich.SetStatus("STATUSMARK");
        });

        var lines = busy.Text().Split('\n');

        var activityIndex = Array.FindIndex(lines, l => l.Contains("ACTIVITYMARK", StringComparison.Ordinal));

        if (activityIndex < 0) return "忙时活动行不在屏幕上：\n" + busy.Dump();

        // **输入行按槽位判定，不能按文本找 `> `**：
        // 转录里提交过的 `> 你好` 也含 `> `，按文本找会命中正文的第一行
        // ——第一版就这么写的，于是检查报"输入行在第 0 行"。
        // 底部块的输入行恒在倒数第二行。
        if (!lines[cap.Height - 2].StartsWith('>'))
            return $"输入行必须停在第 {cap.Height - 1} 行（倒数第二行），实际是「{lines[cap.Height - 2].Trim()}」：\n"
                + busy.Dump();

        if (activityIndex >= cap.Height - 2)
            return $"活动行在第 {activityIndex} 行、输入行在第 {cap.Height - 2} 行"
                + "——活动行必须在**输入行上方**（这正是用户报的缺陷）：\n" + busy.Dump();

        // 提交过的输入必须都留在转录里，且都在活动行上方。
        foreach (var asked in new[] { "> 你好", "> 再说一次" })
        {
            var idx = Array.FindIndex(lines, l => l.Contains(asked, StringComparison.Ordinal));
            if (idx < 0) return $"提交的输入「{asked}」没进正文转录：\n" + busy.Dump();
            if (idx >= activityIndex)
                return $"提交的输入「{asked}」在第 {idx} 行，落在活动行下方：\n" + busy.Dump();
        }

        // 活动行与状态栏必须仍是两行不同的内容（没有合并回去）。
        var statusIndex = Array.FindIndex(lines, l => l.Contains("STATUSMARK", StringComparison.Ordinal));
        if (statusIndex < 0) return "状态栏不在屏幕上：\n" + busy.Dump();

        if (statusIndex != cap.Height - 1)
            return $"状态栏必须贴最后一行（0-based {cap.Height - 1}），实际 {statusIndex}：\n" + busy.Dump();

        return null;
    }

    /// <summary>
    /// 本轮耗时**恰好出现在一个地方**：忙时在活动行，闲时在状态栏。
    /// </summary>
    /// <remarks>
    /// <b>这条防的是"信息丢失"与"信息重复"两种回归，两者都真实发生过：</b>
    /// 拆分槽位时状态栏一度不再带耗时，而活动行在轮次结束后就消失——
    /// 于是本轮跑了多久在任何地方都查不到。反过来，两处都显示会让用户
    /// 看到两个相近但不同的秒数（一个是阶段、一个是整轮），以为是 bug。
    ///
    /// <para>
    /// <b>必须走 <see cref="ActivityReporter"/>，不能只调两个渲染函数</b>：
    /// 第一版就是那样写的，于是把"状态栏永远带耗时"改坏之后检查仍然全绿
    /// ——它验的是纯函数，而缺陷在接线处。这类"查了个没坏的地方"
    /// 在本项目已经出现过一次（见 <c>spinner-follows-frame-interval</c> 的注释）。
    /// 现在从区域捕获真实渲染出的两行。
    /// </para>
    /// </remarks>
    private static string? ElapsedShownOnce()
    {
        // ── 忙：活动行报时间，状态栏不报 ──────────────────
        var busyScreen = RenderThroughReporter(startTurn: true, endTurn: false);
        var busyActivity = busyScreen.Activity;
        var busyStatus = busyScreen.Status;

        if (busyActivity is null) return "忙时应有活动行：" + busyScreen.Dump;

        // 活动行的时间用"阶段耗时"，刚起一轮是 0s；这里只断言它带秒数后缀。
        if (!busyActivity.Contains('s', StringComparison.Ordinal))
            return $"忙时活动行应带耗时：{busyActivity}";

        if (ContainsDuration(busyStatus))
            return $"忙时状态栏不该重复报耗时：{busyStatus}";

        // ── 闲：活动行消失，状态栏接过来 ──────────────────
        var idleScreen = RenderThroughReporter(startTurn: true, endTurn: true);

        if (idleScreen.Activity is not null)
            return $"空闲时不该有活动行：{idleScreen.Activity}";

        return ContainsDuration(idleScreen.Status)
            ? null
            : $"空闲时状态栏应显示上一轮耗时：{idleScreen.Status}";
    }

    /// <summary>文本里是否有一个"时长"样式的段（<c>3s</c> / <c>1m05s</c>）。</summary>
    private static bool ContainsDuration(string? text) =>
        text is not null && Regex.IsMatch(text, @"\d+s\b");

    /// <summary>经 <see cref="ActivityReporter"/> 渲染，读出底部两行的实际内容。</summary>
    private static (string? Activity, string? Status, string Dump) RenderThroughReporter(
        bool startTurn, bool endTurn)
    {
        var cap = new TerminalCapability(TerminalTier.Rich, 120, IsRedirected: false, Height: 20);
        var captured = new StringWriter();
        var originalOut = Console.Out;

        Console.SetOut(captured);
        string whileLive;
        try
        {
            using var region = LiveRegion.Create(cap);
            var reporter = new ActivityReporter(
                region, "model", 1_000_000, () => 12_000);

            try
            {
                if (startTurn) reporter.StartTurn();
                if (endTurn) reporter.FinishTurnAsync().GetAwaiter().GetResult();
                else reporter.ShowIdle();

                // 让泵线程至少跑一拍（它每 100ms 一次）。
                if (startTurn && !endTurn) Thread.Sleep(250);

                // **必须在 Dispose 之前取快照**：Dispose 会调 Clear 把块擦净
                // （那是对的——退出时不该留着「重试中…」），
                // 那之后再读屏幕永远是空的。ReplayRegion 里踩过同一个坑。
                whileLive = captured.ToString();
            }
            finally
            {
                reporter.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
        }
        finally
        {
            Console.SetOut(originalOut);
        }

        var screen = new VtScreen(columns: 120, rows: 20);
        screen.Feed(whileLive);

        var lines = screen.Text().Split('\n');
        var activity = lines[cap.Height - 3].Trim();
        var status = lines[cap.Height - 1].Trim();

        return (
            activity.Length > 0 ? activity : null,
            status.Length > 0 ? status : null,
            screen.Dump());
    }

    /// <summary>
    /// 空闲时活动行那一行必须为空，而状态栏仍在。
    /// </summary>
    /// <remarks>
    /// 与 <c>activity-line-sits-above-input-line</c> 配对：那条保证忙时在上方，
    /// 这条保证闲时那块地方是空的。两条一起才钉住"活动行只在忙时出现"。
    /// </remarks>
    private static string? BlockRowsStable()
    {
        var cap = new TerminalCapability(TerminalTier.Rich, 80, IsRedirected: false, Height: 16);

        var (idle, _) = ReplayRegion(cap, rich =>
        {
            // 空闲：只有状态栏，没有活动行。
            rich.SetStatus("STATUSMARK");
            rich.BeginPrompt("> ");
        });

        var lines = idle.Text().Split('\n');

        var statusIndex = Array.FindIndex(lines, l => l.Contains("STATUSMARK", StringComparison.Ordinal));
        if (statusIndex < 0) return "空闲时状态栏不在屏幕上：\n" + idle.Dump();

        if (statusIndex != cap.Height - 1)
            return $"状态栏必须贴最后一行（0-based {cap.Height - 1}），实际 {statusIndex}：\n" + idle.Dump();

        // 倒数第三行（活动行槽位）必须是空的。
        var activityRow = lines[cap.Height - 3];
        if (activityRow.Trim().Length != 0)
            return $"空闲时活动行槽位不该有内容：第 {cap.Height - 3} 行是「{activityRow.Trim()}」：\n"
                + idle.Dump();

        // 提示符仍在（清活动行不能顺手把它清掉）。
        // **不能用 Contains("> ") 查**：`Text()` 会 TrimEnd，
        // "> " 到了屏幕上只剩 ">"。按行首判断才稳。
        return lines[cap.Height - 2].StartsWith('>')
            ? null
            : $"空闲时提示符必须在第 {cap.Height - 1} 行，实际是「{lines[cap.Height - 2].Trim()}」：\n" + idle.Dump();
    }

    /// <summary>
    /// Body output that overflows the body area must not push the bottom block away.
    /// </summary>
    /// <remarks>
    /// <b>This is the user-facing requirement</b> (2026-11-05): the activity line
    /// must stay directly above the input line no matter how much body text scrolls
    /// past. Under the old "absolute row repaint" approach the block only stayed
    /// put until the body scrolled — then the whole block moved up with the text
    /// and the activity line ended up *below* the input line.
    ///
    /// <para>
    /// With the body confined to <c>1..height-3</c> by DECSTBM the block cannot be
    /// touched by scrolling at all, so this check asserts three things at once:
    /// the block is still there, the body really did scroll (otherwise the check
    /// is vacuous on a screen tall enough to hold everything), and the block sits
    /// exactly on the last three rows.
    /// </para>
    /// </remarks>
    private static string? RegionSurvivesScroll()
    {
        var cap = new TerminalCapability(TerminalTier.Rich, 60, IsRedirected: false, Height: 8);

        var (live, _) = ReplayRegion(cap, rich =>
        {
            // More body lines than the body area can hold: forces real scrolling.
            for (var i = 0; i < 20; i++)
                rich.RunExclusive(() => Output.Control("body " + i + "\n"));

            rich.SetActivity("ACTIVITYLINE");
            rich.SetStatus("STATUSLINE");
        });

        var lines = live.Text().Split('\n');

        var activityIndex = Array.FindIndex(lines, l => l.Contains("ACTIVITYLINE", StringComparison.Ordinal));
        var statusIndex = Array.FindIndex(lines, l => l.Contains("STATUSLINE", StringComparison.Ordinal));

        if (activityIndex < 0) return "活动行在正文溢出后消失了：" + live.Dump();
        if (statusIndex < 0) return "状态栏在正文溢出后消失了：" + live.Dump();

        // 块必须贴在最后三行：活动、输入（空）、状态。
        if (statusIndex != cap.Height - 1)
            return $"状态栏必须贴最后一行（0-based {cap.Height - 1}），实际 {statusIndex}：" + live.Dump();

        if (activityIndex != cap.Height - 3)
            return $"活动行必须在倒数第三行（0-based {cap.Height - 3}），实际 {activityIndex}：" + live.Dump();

        // 正文必须真的滚过：最后一行正文在最下面，早期的已经被滚出屏幕。
        if (!live.Text().Contains("body 19", StringComparison.Ordinal))
            return "正文没写到屏幕上：" + live.Dump();

        // **反向**：早期正文本该被滚出去。若它还在，说明滚动区没生效、整屏都在滚。
        return live.Text().Contains("body 0", StringComparison.Ordinal)
            ? "早期正文没有被滚出滚动区——DECSTBM 没生效：" + live.Dump()
            : null;
    }

    /// <summary>
    /// Default accent must NOT be gold.
    /// </summary>
    /// <remarks>
    /// <b>This guards a design decision that was reversed once.</b>
    /// <c>assets/brand/README.md</c> said "use gold in the terminal, deliberately
    /// unrelated to the logo's black and white". The later
    /// <c>design-principles</c> doc says the opposite: "the terminal stays
    /// black/white/grey by default; do not introduce gold out of mythological
    /// reference".
    ///
    /// Gold is recognisable by hue: red and green channels both far above blue.
    /// </remarks>
    private static string? AccentNotGold()
    {
        var before = Theme.Effective;
        try
        {
            Theme.OverrideForTesting(null);   // fall back to the real default

            var accent = Palette.Accent;
            if (IsGoldish(accent))
                return $"default accent is still gold-ish ({accent}) -- design doc forbids it";

            return IsMonochrome(accent)
                ? null
                : $"default accent is neither grey nor clearly non-gold: {accent}";
        }
        finally
        {
            Theme.OverrideForTesting(before);
        }
    }

    /// <summary>Ok / Warn / Error must be the same in every theme.</summary>
    private static string? SemanticFixed()
    {
        var before = Theme.Effective;
        try
        {
            Theme.OverrideForTesting(Theme.Mono);
            var mono = (Ok: Palette.Ok, Warn: Palette.Warn, Error: Palette.Error);

            Theme.OverrideForTesting(Theme.Zhugou);
            var zhugou = (Ok: Palette.Ok, Warn: Palette.Warn, Error: Palette.Error);

            return mono == zhugou
                ? null
                : "semantic colors changed with the theme -- that breaks red==error";
        }
        finally
        {
            Theme.OverrideForTesting(before);
        }
    }

    /// <summary>The zhugou theme must be red (its name says so), not gold.</summary>
    private static string? ZhugouIsRed()
    {
        var before = Theme.Effective;
        try
        {
            Theme.OverrideForTesting(Theme.Zhugou);
            var accent = Palette.Accent;

            if (IsGoldish(accent))
                return $"zhugou accent is gold-ish ({accent}) -- it should be vermilion";

            var rgb = ParseHex(accent);
            return rgb is { } c && c.R > c.G && c.R > c.B
                ? null
                : $"zhugou accent is not dominated by red: {accent}";
        }
        finally
        {
            Theme.OverrideForTesting(before);
        }
    }

    /// <summary>Gold-ish: red and green both well above blue.</summary>
    private static bool IsGoldish(string hex)
    {
        if (ParseHex(hex) is not { } c) return false;
        return c.R > c.B + 60 && c.G > c.B + 60;
    }

    /// <summary>Grey: the three channels are close together.</summary>
    private static bool IsMonochrome(string hex)
    {
        if (ParseHex(hex) is not { } c) return false;
        var max = Math.Max(c.R, Math.Max(c.G, c.B));
        var min = Math.Min(c.R, Math.Min(c.G, c.B));
        return max - min <= 16;
    }

    private static (int R, int G, int B)? ParseHex(string hex)
    {
        if (hex.Length is not 7 || hex[0] != '#') return null;
        if (!int.TryParse(hex.AsSpan(1, 2), System.Globalization.NumberStyles.HexNumber, null, out var r)) return null;
        if (!int.TryParse(hex.AsSpan(3, 2), System.Globalization.NumberStyles.HexNumber, null, out var g)) return null;
        if (!int.TryParse(hex.AsSpan(5, 2), System.Globalization.NumberStyles.HexNumber, null, out var b)) return null;
        return (r, g, b);
    }

    /// <summary>
    /// The idle status line must be painted at startup, before any turn runs.
    /// </summary>
    /// <remarks>
    /// <b>This was a real regression found on a real terminal.</b> P4 made the
    /// status line persist after a turn ends, but nothing painted it before the
    /// first turn -- the only two triggers were the pump (inside a turn) and
    /// <c>FinishTurnAsync</c> (after one). So between the banner and the first
    /// question the bottom was empty, which is exactly what the user saw.
    ///
    /// Hermes paints its idle status line from the very first frame.
    /// </remarks>
    private static string? IdleBeforeFirstTurn()
    {
        var cap = new TerminalCapability(TerminalTier.Rich, 80, IsRedirected: false, Height: 24);

        var (live, _) = ReplayRegion(cap, region =>
        {
            // ActivityReporter is IAsyncDisposable; DisposeAsync here is a
            // no-op while no turn is running, so blocking on it is safe.
            var activity = new ActivityReporter(
                region, "test-model", 1_000_000, () => 12_000);

            try
            {
                // No StartTurn, no events -- just what happens at startup.
                activity.ShowIdle();
            }
            finally
            {
                activity.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
        });

        var text = live.Text();

        if (!text.Contains("test-model", StringComparison.Ordinal))
            return "no idle status line before the first turn: " + live.Dump();

        // **空闲时不该有活动行**。旧版这里断言的是 `◇`（一个静态空闲符），
        // 那是"活动行与状态栏合并成一行"时的产物。现在活动行是独立槽位、
        // 空闲留空，所以正确的判据反过来：底部只该有状态栏那一行。
        return text.Contains('◇')
            ? "idle state shows an activity marker -- the activity line must stay empty when idle: "
                + live.Dump()
            : null;
    }

    /// <summary>
    /// Startup must actually call ShowIdle, not merely have the method.
    /// </summary>
    /// <remarks>
    /// The behavioural check above calls <c>ShowIdle</c> itself, so it stays
    /// green even if <c>Program.cs</c> never invokes it -- verified by negative
    /// control. That is the gap between "the capability exists" and "the
    /// capability is wired". This check closes it cheaply.
    /// </remarks>
    private static string? StartupCallsShowIdle()
    {
        var root = FindRepoRoot();
        var file = Path.Combine(root, "src", "Dougong.Cli", "Program.cs");
        if (!File.Exists(file)) return "Program.cs not found";

        var text = File.ReadAllText(file);

        // Must be a real call (not inside a comment).
        var called = text.Split(Environment.NewLine)
            .Any(l => l.Contains("activity.ShowIdle()", StringComparison.Ordinal)
                && !l.TrimStart().StartsWith("//", StringComparison.Ordinal));

        return called
            ? null
            : "Program.cs never calls ShowIdle -- the bottom is empty until the first turn";
    }

    /// <summary>Art row counts and max widths must match 字符规格.json.</summary>
    /// <remarks>
    /// The spec file is the source of truth for these numbers; the C# copy is
    /// generated from the same .txt files. Checking them against each other
    /// catches a hand-edit of either side.
    /// </remarks>
    private static string? ArtRowsMatchSpec()
    {
        // (art, expected rows, expected max width)
        var cases = new (IReadOnlyList<string> Art, int Rows, int Width)[]
        {
            (YungongArt.Full, 18, 30),
            (YungongArt.Compact, 12, 20),
            (YungongArt.Ascii, 14, 24),
        };

        foreach (var (art, rows, width) in cases)
        {
            if (art.Count != rows)
                return $"art has {art.Count} rows, spec says {rows}";

            var actualWidth = art.Max(r => DisplayWidth.Of(r));
            if (actualWidth != width)
                return $"art max width is {actualWidth}, spec says {width}";
        }

        return null;
    }

    /// <summary>Braille blocks must be narrow, otherwise rows misalign.</summary>
    private static string? ArtNoWideChars()
    {
        foreach (var art in new[] { YungongArt.Full, YungongArt.Compact })
        {
            foreach (var row in art)
            {
                foreach (var rune in row.EnumerateRunes())
                {
                    if (DisplayWidth.Of(rune) != 1)
                        return $"wide char U+{(int)rune.Value:X4} in art -- rows would misalign";
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Art must keep its ragged right edge: at least one row narrower than max.
    /// </summary>
    /// <remarks>
    /// <b>This guards against "helpfully" padding rows to equal width.</b>
    /// Padding makes the art a solid rectangle -- visually a wall of whitespace
    /// on the right, worst when centered. The source file is deliberately ragged.
    /// </remarks>
    private static string? ArtRaggedEdge()
    {
        var widths = YungongArt.Full.Select(DisplayWidth.Of).Distinct().Count();
        return widths > 1
            ? null
            : "all art rows are the same width -- they were padded, which adds a whitespace wall";
    }

    /// <summary>
    /// Wide but short-of-side-by-side terminal must stack, not squeeze.
    /// </summary>
    private static string? BannerStackedWhenNarrow()
    {
        // **给足高度**，让 stacked 一定能排下（它要 32 行 + 底部 2 行）。
        // 原先用 30 行，而 stacked 放不下——于是正确地降到了 ascii，
        // 检查却因为"没有盲文"报红。那是检查的期望过时，不是实现错。
        var output = CaptureBanner(new TerminalCapability(
            TerminalTier.Rich, 55, IsRedirected: false, Height: 40));

        // Stacked = 形象出现，且信息区另起一行（不并排）。
        if (!output.Contains('⣿', StringComparison.Ordinal))
            return "55 列、40 行足够高时应能排下带形象的一档：" + output;

        var dougongOnOwnLine = output.Split(Environment.NewLine)
            .Any(l => l.Trim() == "DOUGONG");

        return dougongOnOwnLine
            ? null
            : "信息区没有排在形象下方（窄窗口不该硬挤并排）：" + output;
    }

    /// <summary>A short terminal must not print the 18-row full art.</summary>
    /// <remarks>
    /// 18 rows of art + status line + prompt needs 24 rows. On a 20-row
    /// terminal the bottom region would be pushed off screen.
    /// </remarks>
    private static string? BannerShortTerminal()
    {
        var output = CaptureBanner(new TerminalCapability(
            TerminalTier.Rich, 120, IsRedirected: false, Height: 20));

        // Detect the FULL art by its own first row, not the compact one.
        // (The first version used the compact art's opening row as the marker,
        // which made the check report a false failure.)
        var fullFirstRow = YungongArt.Full[0].TrimEnd();
        if (output.Contains(fullFirstRow, StringComparison.Ordinal))
            return "20 行终端仍然画了 full 的 18 行形象——底部会溢出";

        // **降级不必降到带形象的那一档**：20 行里 full 要 28、stacked 要 34、
        // compact 要 31、ascii 要 21，全都放不下，于是正确落到单行 text。
        // 断言"非空"太弱（几乎恒真），改成验降级后的那一档**确实带信息**：
        // 版本号与工作区必须在，否则就是降级成了一句空话。
        if (!output.Contains("dougong", StringComparison.Ordinal))
            return "20 行终端的降级输出里没有版本行：" + output;

        return output.Contains("/ws", StringComparison.Ordinal)
            ? null
            : "20 行终端的降级输出里没有工作区路径：" + output;
    }

    /// <summary>
    /// 降级不降过头：放得下 full 就选 full，放不下才降，且降级后仍有内容。
    /// </summary>
    /// <remarks>
    /// <b>这是高度判据的"两侧守卫"。</b>判据修得偏保守时所有档位都会被跳过、
    /// 屏幕只剩一行标题——那看起来像"能用"，其实丢了全部形象；
    /// 修得偏宽松时又会在矮终端上溢出。两个方向都要钉。
    ///
    /// <para>
    /// 阈值：full 要 28 行、compact 要 31、stacked 要 34、ascii 要 21。
    /// 原先这条检查用 34 行却断言"不该选 full"——那写反了：
    /// 34 ≥ 28，**本来就该选 full**。现在的判据与常量一致。
    /// </para>
    /// </remarks>
    private static string? BannerPicksCompactWhenRoom()
    {
        var fullFirstRow = YungongArt.Full[0].TrimEnd();

        // 31 行：full(28) 放得下 → 必须选 full。
        var at31 = CaptureBanner(new TerminalCapability(
            TerminalTier.Rich, 120, IsRedirected: false, Height: 31));

        if (!at31.Contains(fullFirstRow, StringComparison.Ordinal))
            return "31 行放得下 full（28 行），却没选 full：" + at31;

        // 24 行：full(28) 放不下 → 必须降级，且降级结果非空。
        var at24 = CaptureBanner(new TerminalCapability(
            TerminalTier.Rich, 120, IsRedirected: false, Height: 24));

        if (at24.Contains(fullFirstRow, StringComparison.Ordinal))
            return "24 行放不下 full（需要 28 行），却选了 full：" + at24;

        return at24.Contains("dougong", StringComparison.Ordinal)
            ? null
            : "24 行既没选 full 也没降级出内容：" + at24;
    }

    /// <summary>Capture Banner.Print output under a given capability.</summary>
    private static string CaptureBanner(TerminalCapability capability)
    {
        var original = Console.Out;
        var previous = Output.Capability;

        using var writer = new StringWriter();
        Console.SetOut(writer);
        try
        {
            Output.OverrideCapability(capability);
            Banner.Print("1.0.0", "model", "proto", "/ws", ["plain"]);
        }
        finally
        {
            Output.OverrideCapability(previous);
            Console.SetOut(original);
        }

        return writer.ToString();
    }

    /// <summary>
    /// 三个槽位各占一行，严格递增且不重叠：活动行 → 输入行 → 状态栏。
    /// </summary>
    /// <remarks>
    /// <b>这条检查的前身是 <c>prompt-does-not-overlap-multi-row-status</c></b>：
    /// 那时状态块高度随忙闲变化（活动行与状态栏合并、或多行状态），
    /// 提示符按 <c>height - N</c> 动态计算，算错就重叠。
    ///
    /// <para>
    /// 改成三个固定槽位之后，"重叠"在结构上已经不可能发生，
    /// 但<b>判据反而要加强</b>：块高是常量，任何一处把行号算错都会
    /// 让某个槽位落到别人的位置上。所以这里逐行断言三个绝对行号
    /// 都等于期望值——只断言"互不相同"会让整体偏移一行也通过。
    /// </para>
    /// </remarks>
    private static string? PromptNoOverlapMultiRow()
    {
        var cap = new TerminalCapability(TerminalTier.Rich, 80, IsRedirected: false, Height: 20);

        var output = CaptureRegionOutput(cap, region =>
        {
            region.SetActivity("ACTIVITYMARK");
            region.SetStatus("STATUSMARK");
            region.BeginPrompt("PROMPTMARK");
        });

        var activityRow = RowOf(output, "ACTIVITYMARK");
        var promptRow = RowOf(output, "PROMPTMARK");
        var statusRow = RowOf(output, "STATUSMARK");

        if (activityRow is null || promptRow is null || statusRow is null)
            return "定位失败：" + DescribeControl(output);

        var expected = new (string Name, int? Actual, int Want)[]
        {
            ("活动行", activityRow, cap.Height - 2),
            ("输入行", promptRow, cap.Height - 1),
            ("状态栏", statusRow, cap.Height),
        };

        foreach (var (name, actual, want) in expected)
        {
            if (actual != want)
                return $"{name}应在第 {want} 行，实际第 {actual} 行：" + DescribeControl(output);
        }

        // 三行两两不同——这一条在上面都成立时自动成立，但它把意图写明白：
        // 谁都不许和谁共用一行。
        return activityRow == promptRow || promptRow == statusRow || activityRow == statusRow
            ? "三个槽位出现了重叠：" + DescribeControl(output)
            : null;
    }

    /// <summary>
    /// 字标的每个字形都是 5 列 × 5 行，半角。
    /// </summary>
    /// <remarks>
    /// <b>这一条是字标唯一的结构前提</b>：所有排法都假设等宽，
    /// 一个字形多出一列，后面所有字都右移，整行宽度算错——而
    /// 症状是"最后一列被截掉"或"折行"，看起来像别的问题。
    /// </remarks>
    private static string? BigTextGlyphsUniform()
    {
        foreach (var ch in BigText.AvailableGlyphs)
        {
            var glyph = BigText.GlyphOf(ch);

            if (glyph.Count != BigText.GlyphHeight)
                return $"字形 {ch} 有 {glyph.Count} 行，应为 {BigText.GlyphHeight} 行";

            for (var row = 0; row < glyph.Count; row++)
            {
                if (glyph[row].Length != BigText.GlyphWidth)
                    return $"字形 {ch} 第 {row} 行 {glyph[row].Length} 列，应为 {BigText.GlyphWidth} 列";

                // 必须是半角：块字符 U+2588 在 DisplayWidth 里算 1 格。
                var measured = DisplayWidth.Of(glyph[row]);
                if (measured != BigText.GlyphWidth)
                    return $"字形 {ch} 第 {row} 行显示宽度 {measured}，应为半角的 {BigText.GlyphWidth}";
            }
        }

        return null;
    }

    /// <summary>字标正文用到的每个字符都在字形表里。</summary>
    /// <remarks>
    /// 漏一个字符时 <see cref="BigText.Render"/> 会抛异常——那没关系，
    /// 但会在启动时炸。这条检查把失败提前到构建验证阶段。
    /// </remarks>
    private static string? BigTextCoversWordmark()
    {
        foreach (var ch in BigText.Wordmark)
        {
            if (!BigText.AvailableGlyphs.Contains(ch))
                return $"字标里的 {ch} 没有字形";
        }

        return null;
    }

    /// <summary>渲染出的每行宽度与 <see cref="BigText.Measure"/> 一致。</summary>
    private static string? BigTextWidthMatches()
    {
        foreach (var text in new[] { BigText.Wordmark, BigText.FirstHalf, BigText.SecondHalf })
        {
            var expected = BigText.Measure(text);
            var rows = BigText.Render(text);

            if (rows.Count != BigText.GlyphHeight)
                return $"{text} 渲染出 {rows.Count} 行，应为 {BigText.GlyphHeight} 行";

            for (var i = 0; i < rows.Count; i++)
            {
                // 末尾空格被 TrimEnd 掉，所以是"不超过"而非"等于"。
                var actual = DisplayWidth.Of(rows[i]);
                if (actual > expected)
                    return $"{text} 第 {i} 行宽 {actual}，超过声明的 {expected}";
            }
        }

        // 至少有一行必须是满宽——否则说明所有行都被 TrimEnd 成残的，
        // 而那意味着字形表里没有一行画到最右列（字形本身错了）。
        var wordmarkRows = BigText.Render(BigText.Wordmark);
        return wordmarkRows.Any(r => DisplayWidth.Of(r) == BigText.Measure(BigText.Wordmark))
            ? null
            : "字标没有任何一行画满声明宽度——字形表可能整体偏窄";
    }

    /// <summary>太窄时不画字标（退回云拱形象），不画半截。</summary>
    private static string? BigTextNarrowFallback()
    {
        if (BigText.TryRender(BigText.StackedLineWidth - 1, 40) is not null)
            return $"宽度 {BigText.StackedLineWidth - 1} 时不该还能排下字标";

        var stacked = BigText.TryRender(BigText.StackedLineWidth, 40);
        if (stacked is null) return "宽度刚好够两行排法时应能排下";

        // 两行排法必须是两段各 5 行 + 中间一空行。
        var expected = BigText.GlyphHeight * 2 + 1;
        return stacked.Count == expected
            ? null
            : $"两行排法应有 {expected} 行，实际 {stacked.Count} 行";
    }

    /// <summary>高度不够时不画（否则会把底部区域挤出屏幕）。</summary>
    /// <remarks>
    /// <b><c>TryRender</c> 的 <c>height</c> 现在是"字标可用高度"</b>，
    /// 负数才表示"读不到"。0 是"一行都放不下"这个确定结论。
    /// 这两者混用过一次，造成 66×28 上多画 11 行。
    /// </remarks>
    private static string? BigTextShortFallback()
    {
        // 单行排法要 6 行（5 行字 + 1 空行）。
        if (BigText.TryRender(200, BigText.GlyphHeight) is not null)
            return $"{BigText.GlyphHeight} 行放不下单行排法，不该返回内容";

        if (BigText.TryRender(200, BigText.GlyphHeight + 1) is null)
            return $"{BigText.GlyphHeight + 1} 行应刚好够单行排法";

        // **0 是"没有空间"，不是"读不到"**：必须不画。
        if (BigText.TryRender(200, 0) is not null)
            return "可用高度为 0 时不该画字标——那会顶掉下面所有内容";

        // 负数才是"读不到"，按够处理。
        return BigText.TryRender(200, -1) is not null
            ? null
            : "高度读不到（-1）时应按够处理";
    }

    /// <summary>渐变首行取上端色、末行取下端色。</summary>
    /// <remarks>
    /// 不只验"中间值在变"——那种断言在插值方向写反时照样成立。
    /// 只钉端点：那是外部可核对的事实。
    /// </remarks>
    private static string? BigTextGradient()
    {
        var top = BigText.RowColor(0, 5);
        var bottom = BigText.RowColor(4, 5);

        var expectedTop = Palette.WordmarkTop.ToUpperInvariant();
        var expectedBottom = Palette.WordmarkBottom.ToUpperInvariant();

        if (!string.Equals(top, expectedTop, StringComparison.OrdinalIgnoreCase))
            return $"首行色应为主题上端 {expectedTop}，实际 {top}";

        if (!string.Equals(bottom, expectedBottom, StringComparison.OrdinalIgnoreCase))
            return $"末行色应为主题下端 {expectedBottom}，实际 {bottom}";

        // 中间行必须与两端都不同——否则是"全用一个颜色"的退化。
        var middle = BigText.RowColor(2, 5);
        return middle != top && middle != bottom
            ? null
            : $"中间行色 {middle} 与端点相同——渐变没有生效";
    }

    /// <summary>
    /// 字形能区分开：任意两个字形不得完全相同。
    /// </summary>
    /// <remarks>
    /// <b>这是"压扁"的真实防线</b>：把 5 列压到 4 列时，D 的右上缺口消失，
    /// D 与 O 变成同一个字形。宽度检查发现不了（它们仍然等宽），
    /// 只有"两两不同"能发现。
    /// </remarks>
    private static string? BigTextLegibility()
    {
        var glyphs = BigText.AvailableGlyphs.ToList();

        for (var i = 0; i < glyphs.Count; i++)
        {
            for (var j = i + 1; j < glyphs.Count; j++)
            {
                var a = string.Join('\n', BigText.GlyphOf(glyphs[i]));
                var b = string.Join('\n', BigText.GlyphOf(glyphs[j]));
                if (a == b)
                    return $"字形 {glyphs[i]} 与 {glyphs[j]} 完全相同——无法区分";
            }
        }

        return null;
    }

    /// <summary>
    /// 边框盒每一行的显示宽度都相同。
    /// </summary>
    /// <remarks>
    /// <b>这是"框看起来是框"的唯一判据</b>：任何一行宽度不同，右边框就参差。
    /// 而参差的原因通常是补白按字符数而不是按显示宽度算——中文占 2 格，
    /// 按字符数补会少补一半。
    /// </remarks>
    private static string? FrameEqualWidth()
    {
        var left = new[] { "⣿⣷⣀", "", "abc" };
        var right = new[] { "Available Tools", "中文内容占两格", "x" };

        foreach (var width in new[] { 80, 100, 120 })
        {
            var rows = Frame.TryRender("title", left, right, width);
            if (rows is null) return $"宽度 {width} 时应当能画框";

            for (var i = 0; i < rows.Count; i++)
            {
                var actual = DisplayWidth.Of(rows[i]);
                if (actual != width)
                    return $"宽度 {width}：第 {i} 行显示宽度 {actual}，应为 {width}";
            }
        }

        return null;
    }

    /// <summary>框线是半角，每个边框字符占 1 格。</summary>
    /// <remarks>
    /// <b>不能用双线框</b>：<c>═║╔</c> 在部分等宽字体里按 2 格渲染，
    /// 整框会错位，而症状是"右边框对不齐"，很难归因到字体。
    /// </remarks>
    private static string? FrameBorderHalfWidth()
    {
        foreach (var ch in "─│┌┐└┘")
        {
            var w = DisplayWidth.Of(new System.Text.Rune(ch));
            if (w != 1) return $"框线字符 {ch} 占 {w} 格，应为 1 格";
        }

        return null;
    }

    /// <summary>太窄时返回 null，让调用方退回无框版式。</summary>
    private static string? FrameTooNarrow()
    {
        // 最小宽度 = 2 边框 + 4 边距 + 22 左栏 + 2 间隔 + 20 右栏 = 50。
        const int minimum = 50;

        if (Frame.TryRender("t", ["a"], ["b"], minimum - 1) is not null)
            return $"宽度 {minimum - 1} 时不该能画框——会挤坏内容";

        return Frame.TryRender("t", ["a"], ["b"], minimum) is not null
            ? null
            : $"宽度 {minimum} 时应能画框";
    }

    /// <summary>标题比框还宽时不溢出。</summary>
    /// <remarks>
    /// <b>溢出会让整块框的右边缘错位</b>：标题写出去一格，右边框就对不上。
    /// 超长标题的处置是"退回普通上边线"，不截断标题——截断会让
    /// "这是哪个版本"这句话失去意义。
    /// </remarks>
    private static string? FrameTitleFits()
    {
        var longTitle = new string('x', 200);
        var rows = Frame.TryRender(longTitle, ["a"], ["b"], 80);

        if (rows is null) return "标题很长时不该整个画不出来";
        return DisplayWidth.Of(rows[0]) == 80
            ? null
            : $"超长标题时上边线宽 {DisplayWidth.Of(rows[0])}，应为 80";
    }

    /// <summary>构建一份可控的样例事实。</summary>
    private static StartupSummary SampleSummary(
        IReadOnlyList<string>? tools = null,
        IReadOnlyList<McpServerStatus>? mcp = null,
        IReadOnlyList<SkillSourceGroup>? skillGroups = null,
        int? skillCount = null) =>
        new(
            Version: "0.0.0-test",
            Model: "m",
            Provider: "p",
            Workspace: "C:/a/b",
            SessionId: "s",
            Tools: tools ?? ["read_file", "write_file"],
            McpServers: mcp ?? [],
            SkillCount: skillCount ?? (skillGroups?.Sum(g => g.Names.Count) ?? 0),
            SkillSources: skillGroups ?? []);

    /// <summary>面板报的工具数与传入的清单一致。</summary>
    private static string? PanelToolCount()
    {
        var summary = SampleSummary(tools: ["a", "b", "c"]);
        var text = string.Join('\n', StartupPanel.RenderRight(summary, 200));

        if (!text.Contains("3 tools", StringComparison.Ordinal))
            return "面板没有报出 3 tools：" + text.Replace('\n', '|');

        return text.Contains("a, b, c", StringComparison.Ordinal)
            ? null
            : "面板没有列出工具名：" + text.Replace('\n', '|');
    }

    /// <summary>
    /// MCP 没连上时必须说 failed，不能只写 configured。
    /// </summary>
    /// <remarks>
    /// <b>这是"如实"这条底线</b>：Hermes 截图里写的是 <c>— configured</c>，
    /// 那只是"配置里有一条"。用户看到 configured 会以为它在工作，
    /// 而实际连不上的时候工具根本没注册——那是最难查的一类问题。
    /// </remarks>
    private static string? PanelMcpHonest()
    {
        var summary = SampleSummary(mcp:
        [
            new McpServerStatus("ok-server", "stdio", Connected: true, ToolCount: 7, Error: null),
            new McpServerStatus("bad-server", "stdio", Connected: false, ToolCount: 0, Error: "IOException"),
        ]);

        var text = string.Join('\n', StartupPanel.RenderRight(summary, 200));

        if (!text.Contains("connected · 7 tools", StringComparison.Ordinal))
            return "连上的 server 没有报出工具数：" + text.Replace('\n', '|');

        return text.Contains("failed · IOException", StringComparison.Ordinal)
            ? null
            : "连不上的 server 没有如实报失败：" + text.Replace('\n', '|');
    }

    /// <summary>技能超量时输出 <c>+N more</c>，且 N 与真实差数一致。</summary>
    private static string? PanelSkillTruncation()
    {
        // 7 个技能，每组只显示 4 个 → 应该报 +3 more。
        var names = Enumerable.Range(0, 7).Select(i => $"skill-{i}").ToList();
        var summary = SampleSummary(
            skillGroups: [new SkillSourceGroup("user-dsh", names)], skillCount: 7);

        var text = string.Join('\n', StartupPanel.RenderRight(summary, 200));

        if (!text.Contains("+3 more", StringComparison.Ordinal))
            return "7 个技能、每组显示 4 个时应报 +3 more：" + text.Replace('\n', '|');

        // 少一个就必须变成 +2——防止把数字写死。
        var sixSummary = SampleSummary(
            skillGroups: [new SkillSourceGroup("user-dsh", names.Take(6).ToList())], skillCount: 6);
        var sixText = string.Join('\n', StartupPanel.RenderRight(sixSummary, 200));

        return sixText.Contains("+2 more", StringComparison.Ordinal)
            ? null
            : "6 个技能时应报 +2 more：" + sixText.Replace('\n', '|');
    }

    /// <summary>
    /// 面板的计数来自传入的清单，不是硬编码。
    /// </summary>
    /// <remarks>
    /// <b>这是"数字必须是真的"这条约束的直接检验</b>：如果实现里写死了
    /// "20 tools"，那么工具清单变成 3 个时它仍然报 20——那就是假面板。
    /// </remarks>
    private static string? PanelCountsAreNotHardcoded()
    {
        foreach (var count in new[] { 1, 5, 23 })
        {
            var tools = Enumerable.Range(0, count).Select(i => $"t{i}").ToList();
            var summary = SampleSummary(tools: tools);
            var text = string.Join('\n', StartupPanel.RenderRight(summary, 400));

            if (!text.Contains($"{count} tools", StringComparison.Ordinal))
                return $"{count} 个工具时面板没有报 {count} tools";
        }

        return null;
    }

    /// <summary>
    /// MCP server 超过上限时，必须明说还有几台没列出。
    /// </summary>
    /// <remarks>
    /// <b>静默少列最伤</b>：用户配了 9 台、面板只显示 6 台，他会以为
    /// 其余 3 台没生效，然后去改一个本来就正确的配置。
    /// 截断本身是对的（否则清单占满整屏），但**截断必须说出来**。
    /// </remarks>
    private static string? PanelMcpTruncation()
    {
        const int over = StartupPanel.MaxMcpServers + 3;
        var servers = Enumerable.Range(0, over)
            .Select(i => new McpServerStatus($"s{i}", "stdio", true, 1, null))
            .ToList();

        var text = string.Join('\n',
            StartupPanel.RenderRight(SampleSummary(mcp: servers), 400));

        if (!text.Contains($"还有 {over - StartupPanel.MaxMcpServers} 台未列出", StringComparison.Ordinal))
            return $"{over} 台 MCP 时面板没有说明还藏了几台："
                + text.Replace('\n', '|');

        // 刚好等于上限时必须**不**报"还有 0 台"——那是在制造噪音。
        var atLimit = Enumerable.Range(0, StartupPanel.MaxMcpServers)
            .Select(i => new McpServerStatus($"s{i}", "stdio", true, 1, null))
            .ToList();

        var limitText = string.Join('\n',
            StartupPanel.RenderRight(SampleSummary(mcp: atLimit), 400));

        return limitText.Contains("未列出", StringComparison.Ordinal)
            ? "正好等于上限时不该报未列出"
            : null;
    }

    /// <summary>技能来源组超过上限时，同样要说还有几组没列出。</summary>
    private static string? PanelSourceTruncation()
    {
        const int over = StartupPanel.MaxSkillGroups + 2;
        var groups = Enumerable.Range(0, over)
            .Select(i => new SkillSourceGroup($"src-{i}", [$"skill-{i}"]))
            .ToList();

        var text = string.Join('\n',
            StartupPanel.RenderRight(
                SampleSummary(skillGroups: groups, skillCount: over), 400));

        return text.Contains(
                $"还有 {over - StartupPanel.MaxSkillGroups} 个来源未列出", StringComparison.Ordinal)
            ? null
            : $"{over} 个来源时面板没有说明还藏了几组："
                + text.Replace('\n', '|');
    }

    /// <summary>
    /// 拿不到工具清单时，面板不能说"0 tools"。
    /// </summary>
    /// <remarks>
    /// <b>0 是一个确定的断言，而"读不到"不是。</b>dispatcher 未就绪时
    /// 若显示 <c>0 tools</c>，用户会去查工具注册配置——而问题在装配接线。
    /// 面板的第一职责是不说错话，其次才是说得漂亮。
    /// </remarks>
    private static string? PanelUnknownNotZero()
    {
        var unknown = SampleSummary(tools: []) with { ToolsKnown = false };
        var text = string.Join('\n', StartupPanel.RenderRight(unknown, 400));

        if (text.Contains("0 tools", StringComparison.Ordinal))
            return "拿不到工具清单时报了 0 tools——那是假断言：" + text.Replace('\n', '|');

        if (!text.Contains("读不到工具清单", StringComparison.Ordinal))
            return "拿不到工具清单时没有说明原因：" + text.Replace('\n', '|');

        // 反向：真的没有工具时（ToolsKnown = true 且为空）必须说"无"。
        var empty = SampleSummary(tools: []);
        var emptyText = string.Join('\n', StartupPanel.RenderRight(empty, 400));

        return emptyText.Contains("无已注册工具", StringComparison.Ordinal)
            ? null
            : "确实没有工具时应当明说：" + emptyText.Replace('\n', '|');
    }

    /// <summary>
    /// 首页输出的总行数（含字标与底部预留）不得超过终端高度。
    /// </summary>
    /// <remarks>
    /// <b>这是真机上才会显形的缺陷形态</b>：24 行终端上第一版输出了 25 行
    /// （字标 6 + 面板 19），于是底部状态行与提示符被挤出屏幕——
    /// 而"提示符不见了"看起来像 LiveRegion 的问题，实际根因在 banner。
    ///
    /// <para>
    /// 判据不能用固定阈值：面板高度取决于清单长度（技能/MCP 越多越长）。
    /// 这里用"长清单 + 各种终端高度"组合去撞，任何一档溢出都算失败。
    /// </para>
    /// </remarks>
    private static string? StartupFitsHeight()
    {
        // 一份"很长"的清单：20 个工具、6 台 MCP、5 组技能各 4 个。
        var tools = Enumerable.Range(0, 20).Select(i => $"tool-name-{i}").ToList();
        var mcp = Enumerable.Range(0, 6)
            .Select(i => new McpServerStatus($"server-{i}", "stdio", true, 3, null)).ToList();
        var groups = Enumerable.Range(0, 5)
            .Select(i => new SkillSourceGroup($"src-{i}",
                Enumerable.Range(0, 4).Select(j => $"skill-{i}-{j}").ToList()))
            .ToList();

        var summary = SampleSummary(tools: tools, mcp: mcp, skillGroups: groups, skillCount: 20);

        // **必须扫多档宽度**：字标在宽 ≥ 89 是 5 行、41–88 折成两段是 12 行，
        // 而各档形象（full 18 / compact 12 / ascii 14）也不同。
        // 第一版只测 120 宽，于是 66–80 宽上的折行溢出一项都抓不到。
        var widths = new[] { 34, 50, 66, 70, 89, 90, 120, 160 };
        var heights = new[] { 16, 20, 24, 26, 28, 30, 36, 40 };

        foreach (var width in widths)
        {
            foreach (var height in heights)
            {
                Output.OverrideCapability(new TerminalCapability(
                    TerminalTier.Rich, width, IsRedirected: false, Height: height));

                var captured = CaptureStdout(() => Banner.Print(
                    version: "0.0.0-test",
                    model: "m",
                    protocol: "Chat",
                    workspace: "C:/work/myProject/dougong",
                    plainLines: ["plain"],
                    summary: summary));

                // **数总行数，不能只数非空行。**空行同样占终端的一行，
                // 而 PrintFull/PrintStacked 的首尾空行、字标后的空行都会被漏掉——
                // 第一版就是这么漏掉 66 宽那 6 行溢出的。
                var visible = captured.Replace("\u001b", string.Empty);
                var all = visible.Split('\n');
                if (all.Length > 0 && all[^1].Length == 0) all = all[..^1];
                var lines = all.Length;

                // 底部区域（状态栏 + 提示符）必须还留得下。
                const int reservedBottom = 2;
                if (lines + reservedBottom > height)
                    return $"{width}×{height}：首页打出 {lines} 行 + 底部 "
                        + $"{reservedBottom} 行 = {lines + reservedBottom}，"
                        + $"超了 {lines + reservedBottom - height} 行";
            }
        }

        return null;
    }

    /// <summary>
    /// 没有运行时清单（装配失败/Plain 之外的降级）时，首页同样不许溢出。
    /// </summary>
    /// <remarks>
    /// <b>这是另一条分支</b>：<c>summary</c> 为 null 时不出面板，
    /// 走 full/stacked/compact/ascii/text 五档之一，而那几档各有自己的高度判据。
    /// 第一版只有 full 档判高度，实测 70×25 上 stacked 输出 32 行。
    /// </remarks>
    private static string? StartupWithoutPanelFits()
    {
        var widths = new[] { 30, 34, 50, 66, 70, 89, 90, 120, 160 };
        var heights = new[] { 16, 20, 24, 26, 28, 30, 36, 40 };

        foreach (var width in widths)
        {
            foreach (var height in heights)
            {
                Output.OverrideCapability(new TerminalCapability(
                    TerminalTier.Rich, width, IsRedirected: false, Height: height));

                var captured = CaptureStdout(() => Banner.Print(
                    version: "0.0.0-test",
                    model: "m",
                    protocol: "Chat",
                    workspace: "C:/work/myProject/dougong",
                    plainLines: ["plain"]));

                var all = captured.Replace("\u001b", string.Empty).Split('\n');
                if (all.Length > 0 && all[^1].Length == 0) all = all[..^1];

                const int reservedBottom = 2;
                if (all.Length + reservedBottom > height)
                    return $"{width}×{height}：无面板首页打出 {all.Length} 行 + 底部 "
                        + $"{reservedBottom} 行，超了 {all.Length + reservedBottom - height} 行";
            }
        }

        return null;
    }

    /// <summary>捕获一次动作写到 stdout 的全部内容。</summary>
    private static string CaptureStdout(Action action)
    {
        var original = Console.Out;
        using var writer = new StringWriter();
        Console.SetOut(writer);
        try
        {
            action();
        }
        finally
        {
            Console.SetOut(original);
        }

        return writer.ToString();
    }

    /// <summary>
    /// Before any model call there is no usage data, but the budget is known.
    /// </summary>
    /// <remarks>
    /// The first version skipped the whole context segment when usage was
    /// unknown, leaving just "◇ │ model" -- the user could not see how much
    /// room the session has, which is one of the first things they want to know.
    ///
    /// Reporting capacity without a percentage is deliberate: a 0% would read
    /// as "already used a bit" when in fact nothing has been sent yet.
    /// </remarks>
    private static string? IdleShowsCapacity()
    {
        var line = StatusLine.RenderStatus(
            "model", tokensUsed: null, contextWindow: 1_000_000, width: 120);

        return line.Contains("1.0M", StringComparison.Ordinal)
            ? null
            : $"idle status line hides the context budget: {line}";
    }

    private static string? VtReplayPromptLine()
    {
        var cap = new TerminalCapability(TerminalTier.Rich, 80, IsRedirected: false, Height: 16);

        var (live, _) = ReplayRegion(cap, rich =>
        {
            rich.BeginPrompt("> ");
            rich.CommitPrompt("hello");

            // 模拟一轮开始：活动行出现，正文接写。
            rich.SetActivity("ACTIVITYMARK");
            rich.RunExclusive(() => Output.Control("你好\n"));
        });

        var lines = live.Text().Split('\n');

        // 提交的输入必须进了正文转录——否则用户看不见自己问过什么。
        var transcript = Array.FindIndex(lines, l => l.Contains("> hello", StringComparison.Ordinal));
        if (transcript < 0) return "提交的用户输入没进正文转录：\n" + live.Dump();

        if (!live.Text().Contains("你好", StringComparison.Ordinal))
            return "正文没写出来：\n" + live.Dump();

        // 提交行必须在上方，且不能盖住底部的三个槽位。
        var activity = Array.FindIndex(lines, l => l.Contains("ACTIVITYMARK", StringComparison.Ordinal));
        if (activity < 0) return "活动行不见了：\n" + live.Dump();

        return transcript < activity
            ? null
            : $"提交的输入写到了第 {transcript} 行、活动行在第 {activity} 行——输入必须在正文区上方：\n"
                + live.Dump();
    }

    /// <summary>
    /// 输入期间画块不能把用户正在打的字弄丢，也不能把光标列改掉。
    /// </summary>
    /// <remarks>
    /// <b>这条检查的是新版布局最容易出错的地方</b>：块画在绝对行上，
    /// 而输入行上用户可能已经打了几个字。任何"顺手重画一下"的路径
    /// 都会把光标挪走或把那几个字清掉。判据是两次写入**紧邻**
    /// （"前" 与 "后" 之间没有任何字符间隔）。
    /// </remarks>
    private static string? VtReplayColumn()
    {
        var cap = new TerminalCapability(TerminalTier.Rich, 80, IsRedirected: false, Height: 16);

        var (live, _) = ReplayRegion(cap, rich =>
        {
            rich.RunExclusive(() => Output.Control("前"));
            rich.SetActivity("ACTIVITYMARK");
            rich.SetStatus("STATUSMARK");
            rich.RunExclusive(() => Output.Control("后"));
        });

        if (!live.Text().Contains("前后", StringComparison.Ordinal))
            return "两次写入不连续（光标列被重置）：\n" + live.Dump();

        // 块本身仍须在：不能为了"不打断正文"就整个不画。
        return live.Text().Contains("ACTIVITYMARK", StringComparison.Ordinal)
            ? null
            : "两次写入之间活动行丢了（重画被完全跳过）：\n" + live.Dump();
    }

    /// <summary>
    /// 内容没变时不得重复写终端。
    /// </summary>
    /// <remarks>
    /// 刷新循环每 100ms 调一次 <c>SetLines</c>，而实际内容（模型名、整数秒）
    /// 往往几秒才变。每次都全量擦画的表现是**持续抖动**，在 SSH 上则是
    /// 每秒几十次无意义写入。
    /// <para>
    /// 反证要做两次：只加"相同内容不写"会让"内容变了也不写"同样通过，
    /// 所以下面同时断言"内容变了必须写"。
    /// </para>
    /// </remarks>
    private static string? RepaintSkipsIdentical()
    {
        var captured = new StringWriter();
        var original = Console.Out;
        Console.SetOut(captured);

        try
        {
            var rich = new TerminalCapability(TerminalTier.Rich, 80, IsRedirected: false);
            using var region = LiveRegion.Create(rich);

            region.SetActivity("  思考中… 3s");
            var afterFirst = captured.ToString().Length;
            if (afterFirst == 0) return "首次绘制必须写终端";

            region.SetActivity("  思考中… 3s");
            region.SetActivity("  思考中… 3s");
            region.SetActivity("  思考中… 3s");

            if (captured.ToString().Length != afterFirst)
                return $"相同内容重复写了 {captured.ToString().Length - afterFirst} 个字符";

            region.SetActivity("  思考中… 4s");
            if (captured.ToString().Length == afterFirst)
                return "内容变化后没有重绘";

            // **状态栏也必须有同样的短路**：它与活动行是两个独立槽位，
            // 只给其中一个加判据，另一个仍会每 100ms 刷一次。
            var afterActivity = captured.ToString().Length;
            region.SetStatus("  模型 · 1s");
            var afterStatus = captured.ToString().Length;
            if (afterStatus == afterActivity) return "状态栏首次绘制必须写终端";

            region.SetStatus("  模型 · 1s");
            if (captured.ToString().Length != afterStatus)
                return "状态栏相同内容重复写了";
        }
        finally
        {
            Console.SetOut(original);
        }

        return null;
    }

    /// <summary>
    /// 调过 <see cref="LiveRegion.Clear"/> 之后再写正文，不得把旧内容重新画出来。
    /// </summary>
    /// <remarks>
    /// <b>这条对应真机上看到的缺陷</b>（2026-10-29 用户截图）：一轮失败结束后，
    /// 汇总行都打完了，状态栏 <c>MiniMax-M3.1-Flash · 2s</c> 却还留在最下面。
    /// <para>
    /// 根因：<c>Clear()</c> 只擦屏幕、**没有清空内容**。于是后续任何一次写入
    /// 都会在写完正文之后把那份陈旧内容重新画到下方。
    /// </para>
    /// </remarks>
    private static string? ClearThenWrite()
    {
        var (live, afterDispose) = ReplayRegion(rich =>
        {
            rich.SetActivity("  思考中… 2s");
            rich.SetStatus("  模型 · 2s");
            rich.Clear();
            rich.RunExclusive(() => Output.Control("本轮结束\n"));
        });

        var text = live.Text();

        if (!text.Contains("本轮结束", StringComparison.Ordinal))
            return "正文没写出来：\n" + live.Dump();

        if (text.Contains("模型", StringComparison.Ordinal))
            return "Clear 之后写正文，旧状态栏又被画回来了：\n" + live.Dump();

        if (text.Contains("思考中", StringComparison.Ordinal))
            return "Clear 之后写正文，活动行又被画回来了：\n" + live.Dump();

        // Dispose 之后同样不能有残留。
        if (afterDispose.Text().Contains("模型", StringComparison.Ordinal))
            return "退出后仍有残留：\n" + afterDispose.Dump();

        return null;
    }

    /// <summary>
    /// 写 stderr 时不能压在底部三行上。
    /// </summary>
    /// <remarks>
    /// <b>这条对应真机上看到的第二个缺陷</b>（2026-10-29 用户截图）：
    /// 一轮失败时 <c>[结束：Failed] ...</c> 直接打在底部活动行上面，
    /// 因为那行走的是 <c>Console.Error</c>，绕过了 <see cref="Output"/> 的就位保护。
    /// <para>
    /// 架构检查 <c>Cli_visible_output_goes_through_Output</c> 豁免了
    /// <c>Console.Error</c>，理由是"错误输出不走终端装饰"。**那个理由本身没错，
    /// 但漏了一点**：装饰与否跟占不占屏幕位置是两件事。stderr 与 stdout
    /// 写的是同一块屏幕，只是流不同。
    /// </para>
    /// <para>
    /// 新版布局里这条的结构保证更强：stderr 走 <c>RunExclusive</c>，
    /// 光标恒在正文区，而正文区被 DECSTBM 挡住了底部三行。
    /// 所以断言可以写成"错误行必须出现在活动行上方"——是被验证过的位置关系，
    /// 不只是"没叠在一起"。
    /// </para>
    /// </remarks>
    private static string? StderrDoesNotCollide()
    {
        var (live, _) = ReplayRegion(rich =>
        {
            rich.RunExclusive(() => Output.Control("你好\n"));
            rich.SetActivity("ACTIVITYMARK");
            rich.SetStatus("STATUSMARK");
            Output.Diagnostics("  [结束：Failed] 模型流中断\n");
        });

        var text = live.Text();
        var lines = text.Split('\n');

        var errorLine = Array.FindIndex(lines, l => l.Contains("结束：Failed", StringComparison.Ordinal));

        if (Environment.GetEnvironmentVariable("DUMP_SCREEN") == "1")
            Console.WriteLine("SCREEN>>>\n" + live.Dump());

        if (errorLine < 0) return "错误行没写出来：\n" + live.Dump();

        // 错误行必须完整可读——被底部块覆盖时会缺字或叠字。
        if (!lines[errorLine].Contains("模型流中断", StringComparison.Ordinal))
            return "错误行被底部块覆盖或截断：\n" + live.Dump();

        // 正文不能被挤掉。
        if (!text.Contains("你好", StringComparison.Ordinal))
            return "正文丢失：\n" + live.Dump();

        var activityLine = Array.FindIndex(lines, l => l.Contains("ACTIVITYMARK", StringComparison.Ordinal));
        if (activityLine < 0) return "活动行丢失：\n" + live.Dump();

        return errorLine < activityLine
            ? null
            : $"错误行在第 {errorLine} 行、活动行在第 {activityLine} 行——错误必须在正文区：\n"
                + live.Dump();
    }

    /// <summary>
    /// 补白后的活动行与状态栏不得触发终端自动折行。
    /// </summary>
    /// <remarks>
    /// <b>这条防的是真机上真实发生过的缺陷</b>（2026-10-29 截图）：
    /// 活动行被补白到正好等于终端宽度，写满最后一列时终端自动折行，
    /// 光标跳到下一行；于是定位多走一行，文字全错位，
    /// 屏幕上只剩一个 spinner。
    /// <para>
    /// <b>前几条 VT 检查查不到它</b>：那些用短字符串直接塞槽位，
    /// 根本不经过补白。这条必须真的走 <see cref="StatusLine.RenderActivity"/>
    /// 与 <see cref="StatusLine.RenderStatus"/>。
    /// </para>
    /// </remarks>
    private static string? PaddedLineNoWrap()
    {
        var state = new ActivityState();
        state.OnRunStarted(T0);
        var now = T0.AddSeconds(3);

        // 用几个典型宽度：80 是 Rich 档下限，120 是常见宽屏。
        foreach (var width in new[] { 80, 100, 120 })
        {
            var activity = StatusLine.RenderActivity(state, now, width);
            if (activity is null) return $"宽 {width} 应渲染活动行";

            var status = StatusLine.RenderStatus("model", null, 0, width);

            foreach (var line in new[] { activity, status })
            {
                var display = DisplayWidthOf(line);

                // 显示宽度必须严格小于终端宽度：等于就会折行。
                if (display >= width)
                    return $"宽 {width} 的行占了 {display} 格（写满最后列会折行）：[{line.TrimEnd()}]";
            }
        }

        return null;
    }

    /// <summary>向上找含 Dougong.slnx 的目录。</summary>
    private static string FindRepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "Dougong.slnx")))
        {
            dir = Path.GetDirectoryName(dir);
        }

        return dir ?? throw new InvalidOperationException("找不到仓库根目录（Dougong.slnx）");
    }

    /// <summary>
    /// 被测的 CLI 程序集不得比源码旧。
    /// </summary>
    /// <remarks>
    /// <b>这条防的是本项目最顽固的一类失误：跑了旧 DLL 还当成通过。</b>
    /// 已因此白费至少四次排查（最近一次：2026-10-29，因为用户还开着
    /// 交互会话，Dougong.Cli.exe 被文件锁占用，构建报 MSB3027 失败，
    /// 而我看的是 `grep "error CS"` 的结果 0，误判成构建成功）。
    /// <para>
    /// 判据：CLI 程序集与它所有源码的最晚修改时间比较。<c>obj/</c> 下的
    /// 生成文件不计。程序集比源码旧 ⇒ 现在跑的代码与读到的源码不一致，
    /// 任何结论都无效。
    /// </para>
    /// </remarks>
    /// <summary>
    /// 检查程序集自己引用的 CLI 副本不能比 Release 产物旧。
    /// </summary>
    /// <remarks>
    /// <b>这条钉的是一次真实的判断失误</b>：<c>binary-is-not-stale</c> 只比
    /// "源码 vs Release dll"，但检查代码里 <c>typeof(Program)</c> 这类**编译期引用**
    /// 用的是 spike 输出了目录里的副本。两者可能不同步——此时检查在测旧代码，
    /// 而所有断言看起来都是绿的（或该红的没红）。
    /// <para>
    /// 实测踩到：给参数白名单加了 <c>--stats</c> 但没进 help，<c>doctor</c> 套的
    /// 检查应当报错却没报——因为 spike 里的 <c>Dougong.Cli.dll</c> 比 Release 旧两分钟。
    /// </para>
    /// </remarks>
    private static string? ComparisonUsesFreshCli()
    {
        var root = FindRepoRoot();

        // 本程序集（检查代码自己）所在目录。
        var ownDir = Path.GetDirectoryName(typeof(TerminalChecks).Assembly.Location);
        if (ownDir is null) return "取不到检查程序集路径";

        var inSpike = Path.Combine(ownDir, "Dougong.Cli.dll");
        var release = Path.Combine(root, "src", "Dougong.Cli", "bin", "Release", "net10.0", "Dougong.Cli.dll");

        if (!File.Exists(inSpike)) return "检查目录里没有 Dougong.Cli.dll（引用没带过来）";
        if (!File.Exists(release)) return "找不到 Release 产物（先构建）";

        var spikeTime = File.GetLastWriteTimeUtc(inSpike);
        var releaseTime = File.GetLastWriteTimeUtc(release);

        // **比较用"早于"而不是"晚于等于"**：两者同秒、甚至 Release 略新
        // 都属正常（构建与复制之间有毫秒级差距，而文件时间精度可能只到秒）。
        // 只有 spike 副本明显更旧才说明它没跟上——第一版把方向写反，
        // 在正常构建上就误报了一次。
        var tolerance = TimeSpan.FromSeconds(2);
        return releaseTime - spikeTime <= tolerance
            ? null
            : $"检查用的 Dougong.Cli.dll 比 Release 旧"
                + $"（{spikeTime:HH:mm:ss} vs {releaseTime:HH:mm:ss}）——"
                + "编译期引用的是旧代码，任何负控结论都不作数";
    }

    private static string? BinaryNotStale()
    {
        var root = FindRepoRoot();

        string[] projects = ["Dougong.Cli", "Dougong.Agent", "Dougong.Ai"];

        foreach (var project in projects)
        {
            var source = Path.Combine(root, "src", project);
            var assembly = Path.Combine(source, "bin", "Release", "net10.0", project + ".dll");

            if (!Directory.Exists(source)) continue;
            if (!File.Exists(assembly)) return $"找不到 {project}.dll（先构建）";

            var built = File.GetLastWriteTimeUtc(assembly);

            foreach (var file in Directory.EnumerateFiles(source, "*.cs", SearchOption.AllDirectories))
            {
                if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}",
                        StringComparison.Ordinal)) continue;

                // **逐个程序集与它自己的源码比**，不能拿 Cli.dll 比 Ai 的源码：
                // 改 Ai 时 Ai.dll 会重建、Cli.dll 不必重建（增量构建的正常行为），
                // 那样比会误报。第一版就是这么写的，立刻在正常构建上报了假错。
                var modified = File.GetLastWriteTimeUtc(file);
                if (modified > built)
                {
                    return $"{project}: {Path.GetFileName(file)} 比 {project}.dll 新 "
                        + $"（源码 {modified:HH:mm:ss}，程序集 {built:HH:mm:ss}）——"
                        + "现在跑的是旧构建，任何结论都不作数";
                }
            }
        }

        return null;
    }

    /// <summary>
    /// 推理阶段必须与思考阶段显示不同文案，且推理期间活动行可见。
    /// </summary>
    /// <remarks>
    /// <b>这是新能力的核心价值</b>：长时间没有任何输出时，"在等"和"在想"
    /// 给人的感受完全不同。若两个阶段显示同样的字，这个阶段就白加了。
    /// </remarks>
    private static string? ReasoningPhaseDistinct()
    {
        var state = new ActivityState();
        state.OnRunStarted(T0);

        var thinking = StatusLine.RenderActivity(state, T0.AddSeconds(2), width: 80);
        if (thinking is null) return "思考中应显示活动行";

        state.OnReasoning(T0.AddSeconds(3));
        if (state.Phase != ActivityPhase.Reasoning)
            return $"推理后应进入 Reasoning，实际 {state.Phase}";

        var reasoning = StatusLine.RenderActivity(state, T0.AddSeconds(4), width: 80);
        if (reasoning is null) return "推理期间必须显示活动行（否则看起来像卡死）";

        if (!reasoning.Contains("推理中", StringComparison.Ordinal))
            return $"推理阶段缺少「推理中」文案：[{reasoning}]";

        // 与思考中的文案必须不同：相同就等于阶段白加。
        if (reasoning.Contains("思考中", StringComparison.Ordinal))
            return $"推理阶段仍在显示「思考中」，与思考无法区分：[{reasoning}]";

        if (reasoning == thinking)
            return "推理阶段与思考阶段渲染完全相同";

        return null;
    }

    /// <summary>
    /// 推理停止后必须回落到思考中，不能一直挂着"推理中"。
    /// </summary>
    /// <remarks>
    /// 推理是逐段到达的，停了之后要么开始输出正文、要么开始调工具。
    /// 中间的空档若不做去抖，界面会一直写着"推理中"，而模型其实已经转去做别的了。
    /// </remarks>
    private static string? ReasoningFallsBack()
    {
        var state = new ActivityState();
        state.OnRunStarted(T0);
        state.OnReasoning(T0.AddSeconds(1));

        // 窗口内必须仍是推理。
        state.Tick(T0.AddSeconds(1.2));
        if (state.Phase != ActivityPhase.Reasoning)
            return $"200ms 内不应回落，实际 {state.Phase}";

        // 窗口外必须回落。
        state.Tick(T0.AddSeconds(1.5));
        if (state.Phase != ActivityPhase.Thinking)
            return $"500ms 后应回落到 Thinking，实际 {state.Phase}";

        return null;
    }

    /// <summary>
    /// 正文已经在流式输出时，推理不得把阶段拉回去。
    /// </summary>
    /// <remarks>
    /// 真实 provider 可能交替给出推理和正文。若推理无条件覆盖状态，
    /// 界面会在"推理中"和"输出中"之间反复跳。正文优先是刻意的选择。
    /// </remarks>
    private static string? ReasoningKeepsStreaming()
    {
        var state = new ActivityState();
        state.OnRunStarted(T0);
        state.OnTextDelta(T0.AddSeconds(1));
        state.OnReasoning(T0.AddSeconds(2));

        return state.Phase == ActivityPhase.Streaming
            ? null
            : $"正文流式中推理不得回退，实际 {state.Phase}";
    }

    /// <summary>
    /// <see cref="Dougong.Agent.AgentPhaseTracker"/> 必须能被 SDK 用户单独使用。
    /// </summary>
    /// <remarks>
    /// <b>这条测的是"能力对外可见"</b>，不是 CLI 自己的行为。
    /// 阶段语义属于 Agent 层公开契约：自己做宿主的用户（Desktop / Web / IDE）
    /// 不该各写一套推导逻辑。所以这里完全不经过 CLI 的类型，
    /// 只用 Agent 层 API 走一遍典型序列。
    /// <para>
    /// 若哪天有人把 <c>AgentPhaseTracker</c> 挪回 CLI 内部，这条会编译失败——
    /// 那正是它要防的：能力退化成"只有我们的 CLI 有"。
    /// </para>
    /// </remarks>
    private static string? TrackerStandalone()
    {
        var tracker = new Dougong.Agent.AgentPhaseTracker();
        var t = T0;

        tracker.StartTurn(t);
        if (tracker.Phase != Dougong.Agent.AgentPhase.Thinking)
            return $"StartTurn 后应为 Thinking，实际 {tracker.Phase}";

        tracker.OnEvent(new Dougong.Agent.AgentEvent.ReasoningDelta("想一下"), t.AddSeconds(1));
        if (tracker.Phase != Dougong.Agent.AgentPhase.Reasoning)
            return $"推理事件后应为 Reasoning，实际 {tracker.Phase}";

        tracker.OnEvent(new Dougong.Agent.AgentEvent.TextDelta("4"), t.AddSeconds(2));
        if (tracker.Phase != Dougong.Agent.AgentPhase.Streaming)
            return $"正文事件后应为 Streaming，实际 {tracker.Phase}";

        tracker.OnEvent(new Dougong.Agent.AgentEvent.ToolCallRequested("i", "t", default),
            t.AddSeconds(3));
        if (tracker.Phase != Dougong.Agent.AgentPhase.Working)
            return $"工具事件后应为 Working，实际 {tracker.Phase}";

        tracker.OnEvent(
            new Dougong.Agent.AgentEvent.RunFinished(Dougong.Agent.AgentRunOutcome.Completed),
            t.AddSeconds(4));
        if (tracker.Phase != Dougong.Agent.AgentPhase.Idle)
            return $"完成后应为 Idle，实际 {tracker.Phase}";

        // 计时也要可用：宿主靠它显示"已耗时"。
        if (tracker.TurnElapsed(t.AddSeconds(4)) != TimeSpan.Zero)
            return "结束后 TurnElapsed 应为零";

        return null;
    }

}
