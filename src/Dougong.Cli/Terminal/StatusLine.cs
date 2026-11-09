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
    /// 生成**活动行**（阶段指示）。返回 null 表示当前不该显示。
    /// </summary>
    /// <remarks>
    /// <b>活动行是独立的一行，不在状态栏里</b>（用户 2026-11-05 的版式要求）。
    /// P4 阶段曾把两者合并成一行、贴在屏幕最底，结果"忙"这件事出现在
    /// 用户视线的下方——而它恰恰是最该先被看到的信息。现在它独占
    /// 输入行**上方**那行，忙时出现、闲时留空。
    ///
    /// <para>
    /// <b>只放"正在发生什么"</b>：spinner + 面孔 + 阶段文案 + 阶段耗时。
    /// 模型名与上下文占用归状态栏——那两个是"环境"不是"事件"，
    /// 混在一起会让同一行既变又不变，读起来费劲。
    /// </para>
    ///
    /// <para>
    /// 宽度不足 40 时只留 spinner：那时连阶段文案都放不下，
    /// 而"它在动"是这一行唯一不可让渡的信息。
    /// </para>
    /// </remarks>
    /// <param name="state">活动状态机。</param>
    /// <param name="now">当前时刻（spinner 与面孔轮换用）。</param>
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
    /// 生成**状态栏**（模型 · 上下文）。**常驻**，贴屏幕最底。
    /// </summary>
    /// <remarks>
    /// <b>渐进披露照抄 Hermes 的判据</b>（<c>appChrome.tsx:596-660</c>）：
    /// 模型名**不可压缩**、先占位；尾部段按 <c>bar → tokens</c> 的顺序逐个尝试，
    /// **放不下的整段丢弃，而不是截断中间**。
    /// 这比"整行截断"好：它保证"模型名和上下文占用永远能看到"。
    ///
    /// <para>
    /// <b>不含耗时</b>：耗时是"这一轮跑了多久"，属于事件，已经由活动行显示。
    /// 两处都显示同一件事只会让人怀疑它们不一致。
    /// </para>
    ///
    /// <para>
    /// <b>耗时只在空闲时由状态栏补上</b>（<paramref name="idleTurnElapsed"/>）：
    /// 忙时活动行已经在报"这一阶段跑了多久"，两处同时显示只会让人怀疑
    /// 它们不一致。轮次一结束活动行消失，那时状态栏接过来报本轮总时长——
    /// 于是任何时刻都恰好有一处在报时间，信息不丢也不重复。
    /// </para>
    ///
    /// <para>
    /// 宽度不足 40 时只剩模型名：它是这一行唯一不可让渡的信息，
    /// 而上下文占用随时可以用 <c>/status</c> 查。尾部段按 fits 判据
    /// 逐个尝试，放不下就停。
    /// </para>
    /// </remarks>
    /// <param name="model">模型名。</param>
    /// <param name="tokensUsed">已用 token；null 表示未知。</param>
    /// <param name="contextWindow">上下文窗口；0 表示不显示占用。</param>
    /// <param name="width">可用宽度。</param>
    /// <param name="idleTurnElapsed">
    /// 上一轮耗时；**只在空闲时传**（忙时传 null，活动行负责显示）。
    /// </param>
    public static string RenderStatus(
        string model,
        int? tokensUsed,
        int contextWindow,
        int width,
        TimeSpan? idleTurnElapsed = null)
    {
        ArgumentNullException.ThrowIfNull(model);

        var builder = new StringBuilder("  ");
        builder.Append(model);

        // ── 尾部段：按优先级逐个 fits() 尝试 ────────────────
        var tail = new List<string>();

        if (contextWindow > 0)
        {
            if (tokensUsed is { } used)
            {
                var percent = Math.Min(100, used * 100 / contextWindow);

                // 宽终端：进度条比 token 数更直观，但 token 数是精确值，两个都给。
                // 窄终端放不下整条，按 Hermes 的做法压成 `12K tok` 并丢掉进度条。
                tail.Add(width >= 80
                    ? $"{FormatTokens(used)}/{FormatTokens(contextWindow)} {RenderGauge(percent, 10)} {percent}%"
                    : CompactTokens(used));
            }
            else
            {
                // **还没调用过模型时显示窗口容量，而不是整段消失**。
                //
                // 启动后状态行只剩"模型名"的话，用户看不出这个会话的上下文
                // 预算是多少，而那正是他最想确认的事之一（"这个模型能装多少"）。
                //
                // 只报容量不报占用：报一个 0% 会让人以为"已经用了一点"，
                // 而实际是一次请求都还没发。容量本身就是确定信息。
                tail.Add($"≤{FormatTokens(contextWindow)}");
            }
        }

        // 耗时永远排在最后：它是最容易舍弃的一段。忙时为空——
        // 那时活动行在报时间，见上面 summary 的说明。
        if (idleTurnElapsed is { } elapsed && elapsed > TimeSpan.Zero)
            tail.Add(FormatDuration(elapsed));

        foreach (var segment in tail)
        {
            var candidate = builder + Separator + segment;
            if (DisplayWidth.Of(candidate) > width - 1) break;   // 放不下就停
            builder.Append(Separator).Append(segment);
        }

        return PadToWidth(builder.ToString(), width);
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
