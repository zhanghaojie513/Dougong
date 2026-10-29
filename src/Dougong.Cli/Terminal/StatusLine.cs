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
        [ActivityPhase.Failed] = "出错了",
    };

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
        if (width > 0 && DisplayWidth(line) > width) line = TruncateToWidth(line, width);
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
        var visible = DisplayWidth(content);

        if (visible > limit) content = TruncateToWidth(content, limit);
        visible = DisplayWidth(content);

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
    private static int DisplayWidth(string text)
    {
        var width = 0;
        var inEscape = false;

        foreach (var c in text)
        {
            if (inEscape)
            {
                if (c == 'm') inEscape = false;
                continue;
            }

            if (c == '\u001b') { inEscape = true; continue; }

            width += IsWide(c) ? 2 : 1;
        }

        return width;
    }

    /// <summary>按显示宽度截断，末尾补省略号。</summary>
    private static string TruncateToWidth(string text, int width)
    {
        var builder = new StringBuilder(text.Length);
        var used = 0;

        foreach (var c in text)
        {
            var w = IsWide(c) ? 2 : 1;
            if (used + w > width - 1) break;
            builder.Append(c);
            used += w;
        }

        builder.Append('…');
        return builder.ToString();
    }

    /// <summary>该字符在等宽终端里是否占两格。</summary>
    private static bool IsWide(char c) => c switch
    {
        >= '\u1100' and <= '\u115F' => true,  // 谚文字母
        >= '\u2E80' and <= '\u303E' => true,  // CJK 部首与标点
        >= '\u3041' and <= '\u33FF' => true,  // 假名、CJK 兼容
        >= '\u3400' and <= '\u4DBF' => true,  // CJK 扩展 A
        >= '\u4E00' and <= '\u9FFF' => true,  // CJK 统一表意
        >= '\uA000' and <= '\uA4CF' => true,  // 彝文
        >= '\uAC00' and <= '\uD7A3' => true,  // 谚文音节
        >= '\uF900' and <= '\uFAFF' => true,  // CJK 兼容表意
        >= '\uFE30' and <= '\uFE6F' => true,  // CJK 竖排标点
        >= '\uFF00' and <= '\uFF60' => true,  // 全角 ASCII
        >= '\uFFE0' and <= '\uFFE6' => true,  // 全角符号
        _ => false,
    };
}
