using System.Text;

namespace Dougong.Cli.Terminal;

/// <summary>
/// 活动行与状态栏的**文本生成**。只管拼字符串，不碰光标——
/// 因此可以在任何环境里完整测试。
/// </summary>
/// <remarks>
/// <b>为什么把"生成文本"与"重绘"分开</b>：重绘依赖光标定位，在重定向环境里
/// 无法验证（实测 <c>Console.SetCursorPosition</c> 抛 <c>IOException</c>）。
/// 拆开之后，"显示什么"这一半能完整测试，"怎么刷上去"那一半才需要真终端。
/// </remarks>
public static class StatusLine
{
    /// <summary>spinner 帧。4 帧够用，且 ASCII 在任何终端都不会错位。</summary>
    private static readonly string[] SpinnerFrames = ["|", "/", "-", "\\"];

    /// <summary>每帧停留时长。</summary>
    private static readonly TimeSpan SpinnerInterval = TimeSpan.FromMilliseconds(250);

    /// <summary>面孔轮换间隔。比 spinner 慢得多——250ms 换表情会晕。</summary>
    private static readonly TimeSpan FaceInterval = TimeSpan.FromMilliseconds(2500);

    /// <summary>各阶段可用的面孔。宽度已实测（方案 §3.8.4）。</summary>
    private static readonly Dictionary<ActivityPhase, string[]> Faces = new()
    {
        [ActivityPhase.Thinking] = ["(¬‿¬)", "(⊙_⊙)", "◉_◉", "(´･_･`)"],
        // 推理阶段用"认真琢磨"的面孔，与"干等着"区分开。
        [ActivityPhase.Reasoning] = ["(⌐■_■)", "( ͡° ͜ʖ ͡°)", "(¬_¬)"],
        [ActivityPhase.Working] = ["( •_•)", "(¬_¬)", "(◔_◔)"],
        [ActivityPhase.Compacting] = ["(´･_･`)", "(◔_◔)"],
        // 重试是"出了问题但在自己处理"，用略显吃力的脸，
        // 与"思考中"的轻松感区分——用户据此判断要不要干预。
        [ActivityPhase.Retrying] = ["(´･_･`)", "(¬_¬)"],
        [ActivityPhase.Failed] = ["(°ロ°)"],
    };

    /// <summary>各阶段的文案。**不暴露内部机制**（不写"调用 read_file"）。</summary>
    /// <remarks>
    /// <see cref="ActivityPhase.Thinking"/> 与 <see cref="ActivityPhase.Reasoning"/>
    /// 的文案必须不同：前者是"什么都还没来"，后者是"模型确实在动"。
    /// 长时间没有输出时，这两种状态给人的感受完全不一样。
    /// </remarks>
    private static readonly Dictionary<ActivityPhase, string> Labels = new()
    {
        [ActivityPhase.Thinking] = "思考中…",
        [ActivityPhase.Reasoning] = "推理中…",
        [ActivityPhase.Working] = "处理中…",
        [ActivityPhase.Compacting] = "整理中…",
        [ActivityPhase.Retrying] = "重试中…",
        [ActivityPhase.Failed] = "出错了",
    };

    /// <summary>该阶段是否有文案。供覆盖性检查使用。</summary>
    public static bool HasLabel(ActivityPhase phase) => Labels.ContainsKey(phase);

    /// <summary>该阶段是否有面孔。供覆盖性检查使用。</summary>
    public static bool HasFace(ActivityPhase phase) => Faces.ContainsKey(phase);

    /// <summary>状态行各段之间的分隔符。</summary>
    /// <remarks>
    /// <b>用 <c>│</c> 而不是 <c>·</c></b>：段数变多后（指示符 / 模型 / 上下文 /
    /// 耗时 / 成本），光靠空格与圆点分不开——视觉上会连成一句。
    /// Hermes 用的也是竖线（<c>appChrome.tsx</c> 的 status segments）。
    /// </remarks>
    private const string Separator = " │ ";

    /// <summary>窄终端下上下文段的紧凑形式。</summary>
    /// <remarks>
    /// <b>窄终端要压成 <c>12k tok</c> 并去掉进度条</b>：Hermes 的
    /// <c>segs.compactCtx</c> 就是这么做的。我们原来窄终端只是把进度条
    /// 从 10 格缩到 5 格，结果挤掉了模型名——而模型名比进度条精度重要。
    /// </remarks>
    private static string CompactTokens(int used) => $"{FormatTokens(used)} tok";

    /// <summary>
    /// 生成**单行**状态行：忙闲共用同一个槽位。
    /// </summary>
    /// <remarks>
    /// <b>这是 P4 的核心改动</b>：原来底部是"活动行 + 状态栏"两行，
    /// 而 Hermes **从头到尾只有一行**（<c>appChrome.tsx:735-745</c>：
    /// 忙时渲染 <c>FaceTicker</c>，闲时渲染静态 status 文案，同一个槽位）。
    ///
    /// <para>
    /// 合并的收益不只是省一行：<b>空闲时底部不再是空的</b>。原来轮次一结束
    /// 就 <c>SetLines([])</c> 整块收起，用户看到的是"底部空荡"——
    /// 而 Hermes 的空闲状态行始终在，能看到模型名与上下文占用。
    /// </para>
    ///
    /// <para>
    /// <b>渐进披露照抄 Hermes 的判据</b>（<c>appChrome.tsx:596-660</c>）：
    /// 左段（指示符 / 模型）**不可压缩**、先占位；尾部段按
    /// <c>bar → duration → compressions → cost</c> 的顺序逐个尝试，
    /// **放不下的整段丢弃，而不是截断中间**。
    /// 这比"整行截断"好：它保证"模型名和上下文占用永远能看到"。
    /// </para>
    ///
    /// <para>
    /// 宽度不足 40 时只留指示符——那时连模型名都放不下，
    /// 而"它在动"是这一行唯一不可让渡的信息。
    /// </para>
    /// </remarks>
    /// <param name="state">活动状态机；null 表示空闲（显示静态状态行）。</param>
    /// <param name="model">模型名。</param>
    /// <param name="tokensUsed">已用 token；null 表示未知。</param>
    /// <param name="contextWindow">上下文窗口；0 表示不显示占用。</param>
    /// <param name="turnElapsed">本轮耗时；空闲时传上一轮的即可。</param>
    /// <param name="now">当前时刻（spinner 与面孔轮换用）。</param>
    /// <param name="width">可用宽度。</param>
    public static string RenderSingleLine(
        ActivityState? state,
        string model,
        int? tokensUsed,
        int contextWindow,
        TimeSpan turnElapsed,
        DateTimeOffset now,
        int width)
    {
        ArgumentNullException.ThrowIfNull(model);

        var busy = state is { Phase: not ActivityPhase.Idle } && state.ShouldDisplay;

        // ── 左段：不可压缩，先占位 ──────────────────────────
        string indicator;
        if (busy)
        {
            var spinner = PickSpinner(now);
            var face = PickFace(state!.Phase, now);
            var label = Labels.TryGetValue(state.Phase, out var found) ? found : string.Empty;

            // 极窄终端：只留 spinner。连"它在动"都要省掉时这一行就没意义了。
            if (width < 24) return spinner;

            indicator = string.IsNullOrEmpty(face)
                ? $"{spinner} {label}"
                : $"{spinner} {face} {label}";
        }
        else
        {
            // 空闲：不是"什么都没有"，而是"待命"。
            // 用一个静态符号而不是 spinner——转着的东西会让用户以为还在跑。
            indicator = "◇";
        }

        var builder = new StringBuilder(indicator);

        // ── 模型名：不可压缩（放不下就整行只有指示符）──────
        if (width >= 40)
            builder.Append(Separator).Append(model);

        // ── 尾部段：按优先级逐个 fits() 尝试 ────────────────
        var tail = new List<string>();
        if (tokensUsed is { } used && contextWindow > 0)
        {
            var percent = Math.Min(100, used * 100 / contextWindow);
            tail.Add(width >= 80
                ? $"{FormatTokens(used)}/{FormatTokens(contextWindow)} {RenderGauge(percent, 10)} {percent}%"
                : CompactTokens(used));
        }

        if (busy || turnElapsed > TimeSpan.Zero)
            tail.Add(FormatDuration(turnElapsed));

        foreach (var segment in tail)
        {
            var candidate = builder + Separator + segment;
            if (DisplayWidth.Of(candidate) > width - 1) break;   // 放不下就停
            builder.Append(Separator).Append(segment);
        }

        return PadToWidth(builder.ToString(), width);
    }

    /// <summary>
    /// 生成活动行。返回 null 表示当前不该显示。
    /// </summary>
    /// <param name="state">活动状态机。</param>
    /// <param name="now">当前时刻。</param>
    /// <param name="width">可用宽度。</param>
    public static string? RenderActivity(ActivityState state, DateTimeOffset now, int width)
    {
        ArgumentNullException.ThrowIfNull(state);

        if (!state.ShouldDisplay) return null;

        var phase = state.Phase;
        var spinner = PickSpinner(now);
        var face = PickFace(phase, now);
        var label = Labels.TryGetValue(phase, out var found) ? found : string.Empty;
        var elapsed = FormatDuration(state.Elapsed(now));

        // 极窄终端放不下三段，只留 spinner。
        if (width < 40) return spinner;

        var left = string.IsNullOrEmpty(face)
            ? $"  {spinner} {label} · {elapsed}"
            : $"  {spinner} {face} {label} · {elapsed}";

        return PadToWidth(left, width);
    }

    /// <summary>
    /// 生成状态栏（模型 · 上下文 · 耗时）。
    /// </summary>
    /// <param name="model">模型名。</param>
    /// <param name="tokensUsed">已用 token；null 表示未知。</param>
    /// <param name="contextWindow">上下文窗口；0 表示未启用压缩。</param>
    /// <param name="turnElapsed">本轮耗时。</param>
    /// <param name="width">可用宽度。</param>
    public static string RenderStatus(
        string model,
        int? tokensUsed,
        int contextWindow,
        TimeSpan turnElapsed,
        int width)
    {
        ArgumentNullException.ThrowIfNull(model);

        var parts = new List<string> { model };

        if (tokensUsed is { } used && contextWindow > 0)
        {
            var percent = Math.Min(100, used * 100 / contextWindow);
            parts.Add($"{FormatTokens(used)}/{FormatTokens(contextWindow)}");
            parts.Add(RenderGauge(percent, width >= 80 ? 10 : 5) + $" {percent}%");
        }

        parts.Add(FormatDuration(turnElapsed));

        var line = "  " + string.Join(" · ", parts);
        if (width > 0 && DisplayWidth.Of(line) > width) line = TruncateToWidth(line, width);
        return line;
    }

    /// <summary>上下文占用率的小进度条。</summary>
    /// <remarks>
    /// <b>字符选择</b>：<c>█</c> 在几乎所有等宽字体里都是满格宽，<c>░</c> 是浅格。
    /// 不用方块 emoji（🟩）——它在部分终端占 2 格，会让整行错位。
    /// </remarks>
    public static string RenderGauge(int percent, int cells)
    {
        if (cells <= 0) return string.Empty;

        percent = Math.Clamp(percent, 0, 100);
        var filled = (int)Math.Round(percent / 100.0 * cells);
        var builder = new StringBuilder(cells + 2);
        builder.Append('[');
        for (var i = 0; i < cells; i++) builder.Append(i < filled ? '█' : '░');
        builder.Append(']');
        return builder.ToString();
    }

    /// <summary>取当前该显示的面孔；该阶段没有面孔时返回空串。</summary>
    public static string PickFace(ActivityPhase phase, DateTimeOffset now)
    {
        if (!Faces.TryGetValue(phase, out var candidates) || candidates.Length == 0)
            return string.Empty;

        var index = (int)(now.ToUnixTimeMilliseconds() / (long)FaceInterval.TotalMilliseconds
            % candidates.Length);
        return candidates[index];
    }

    /// <summary>取当前 spinner 帧。</summary>
    public static string PickSpinner(DateTimeOffset now) =>
        SpinnerFrames[(int)(now.ToUnixTimeMilliseconds() / (long)SpinnerInterval.TotalMilliseconds
            % SpinnerFrames.Length)];

    /// <summary>把时长格式化成 <c>2s</c> / <c>1m05s</c> 这样的短形式。</summary>
    public static string FormatDuration(TimeSpan elapsed)
    {
        if (elapsed.TotalSeconds < 60) return $"{(int)elapsed.TotalSeconds}s";

        var minutes = (int)elapsed.TotalMinutes;
        var seconds = elapsed.Seconds;
        return $"{minutes}m{seconds:D2}s";
    }

    /// <summary>token 数的短形式：1200 → 1.2K。</summary>
    public static string FormatTokens(int tokens) => tokens switch
    {
        < 1000 => tokens.ToString(),
        < 1_000_000 => $"{tokens / 1000.0:F1}K",
        _ => $"{tokens / 1_000_000.0:F1}M",
    };

    /// <summary>用空格把内容补到整行宽，抹掉上一帧的残留。</summary>
    /// <remarks>
    /// <b>必须留最后一列不写</b>：真实终端在写满最右列时会自动折行，
    /// 光标跳到下一行行首。底部区域依赖"画完这一行光标还在这一行"
    /// 这个前提来定位下一行（<c>ESC[1E</c>），一旦折行，后续每一行都偏移，
    /// 表现为活动行文字消失、内容错位。
    /// <para>
    /// 真机实测（2026-10-29 用户截图）：120 列终端上活动行只剩 <c>  |</c>，
    /// 文字跑到别处去了。自制 VT 模拟器没有自动折行，所以查不出来。
    /// </para>
    /// </remarks>
    private static string PadToWidth(string content, int width)
    {
        if (width <= 0) return content;

        // 可用宽度减 1：最后一列留空，杜绝自动折行。
        var limit = width - 1;
        var visible = DisplayWidth.Of(content);

        if (visible > limit) content = TruncateToWidth(content, limit);
        visible = DisplayWidth.Of(content);

        return visible >= limit ? content : content + new string(' ', limit - visible);
    }

    /// <summary>
    /// 终端显示宽度：ANSI 序列不计，CJK 与全角符号按 2 格计。
    /// </summary>
    /// <remarks>
    /// <b>按 2 格计是必须的</b>：中文在等宽终端里占两格。按 1 格算会让补白多出
    /// "中文字数"个空格，整行超出终端宽度后折行，重绘时上一帧就擦不干净
    /// ——表现为状态栏越用越乱。这一条是实测的，不是理论担忧。
    /// <para>
    /// 只覆盖本文件实际用到的字符类别，不做完整的 East Asian Width 表
    /// ——用不上的复杂度就是 bug 温床。
    /// </para>
    /// </remarks>
    /// <summary>按显示宽度截断，末尾补省略号。</summary>
    /// <remarks>
    /// 按 Rune 迭代：按 <c>char</c> 迭代会在代理对中间切断，输出孤立高代理。
    /// </remarks>
    private static string TruncateToWidth(string text, int width)
    {
        var builder = new StringBuilder(text.Length);
        var used = 0;

        foreach (var rune in text.EnumerateRunes())
        {
            var w = DisplayWidth.Of(rune);
            if (used + w > width - 1) break;
            builder.Append(rune.ToString());
            used += w;
        }

        builder.Append('…');
        return builder.ToString();
    }
}
