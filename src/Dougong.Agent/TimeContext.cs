using System.Globalization;

namespace Dougong.Agent;

/// <summary>
/// 往对话里注入当前时间与上一条消息的间隔。
/// </summary>
/// <remarks>
/// <b>为什么模型需要这个</b>：没有时钟时，"三天前你说过"这类判断全错——
/// 模型不知道现在几点，只能从对话推断，而推断必错。用户说"今天"、"上周"
/// 更是无从解析。
///
/// <para>
/// <b>照 dsh 的 <c>dsh-time-context</c> 做，关键决定一致</b>：
/// <list type="bullet">
/// <item><b>注入的是 user 角色消息，不是系统提示</b>。它是一条"可观察的读数"，
/// 不是行为纪律。混进系统提示会让它看起来像长期规则，而它每次都在变。</item>
/// <item><b>带时区偏移与 IANA 名</b>：只说"2026-11-02 14:30"在跨时区协作时
/// 毫无意义，而模型会把它当本地时间解读。</item>
/// <item><b>报间隔秒数</b>：模型借此知道"用户是刚回复还是隔了一小时"，
/// 那影响它该不该假定上下文还热。</item>
/// <item><b>有最小间隔</b>：每轮都注入是纯噪音——同一分钟内问三次，
/// 三次都收到一条时间消息，只会挤占预算。</item>
/// </list>
/// </para>
///
/// <para>
/// <b>与 dsh 的差别</b>：它还要报"浏览器时区"（Web 客户端的请求上下文），
/// 我们没有那个概念，只报服务端时区。这不是简化——多源时区在没有浏览器
/// 客户端的场景里就是单一来源。
/// </para>
/// </remarks>
public sealed class TimeContext
{
    /// <summary>
    /// 默认最小注入间隔：10 分钟，与 dsh 一致。
    /// </summary>
    /// <remarks>
    /// 短于此间隔的连续轮次不重复注入——一分钟内问三句，模型不需要三个时间读数。
    /// <para>
    /// 设为零表示每次都可注入（调试用）。那会产生大量重复消息，不属于常规用法。
    /// </para>
    /// </remarks>
    public static readonly TimeSpan DefaultMinimumInterval = TimeSpan.FromMinutes(10);

    private readonly TimeProvider _time;
    private readonly TimeSpan _minimumInterval;

    // 上次注入的读数。用 null 表示还没注入过——那时 elapsed 应为 unavailable。
    private DateTimeOffset? _lastInjectedAt;

    /// <summary>用系统时钟与默认间隔构造。</summary>
    public TimeContext() : this(TimeProvider.System, DefaultMinimumInterval)
    {
    }

    /// <summary>构造，可注入时钟与间隔（供检查用）。</summary>
    public TimeContext(TimeProvider time, TimeSpan minimumInterval)
    {
        ArgumentNullException.ThrowIfNull(time);
        ArgumentOutOfRangeException.ThrowIfLessThan(minimumInterval, TimeSpan.Zero);

        _time = time;
        _minimumInterval = minimumInterval;
    }

    /// <summary>
    /// 生成一条读数；不满足最小间隔时返回 null。
    /// </summary>
    /// <remarks>
    /// <b>返回 null 而不是"距上次太久就不报"</b>：调用方据此决定要不要追加消息，
    /// 而"不追加"与"追加一条说没有时间"是两件事——后者只会浪费预算。
    /// </remarks>
    public string? TryRender()
    {
        var now = _time.GetUtcNow();

        // **先算间隔，再更新基线**。顺序反了的话 previous == now，
        // 渲染时算不出间隔而报 unavailable——实测踩到过，
        // 症状是"第二条及以后的时间读数全都说没有上次"。
        TimeSpan? elapsed = null;
        if (_lastInjectedAt is { } last)
        {
            // **时钟回退**（NTP 校正、手动改表）时 elapsed 归零而不是负数。
            // 负的间隔会让模型以为事件倒序，而它无法判断这是时钟问题。
            var raw = now - last;
            elapsed = raw < TimeSpan.Zero ? TimeSpan.Zero : raw;

            if (elapsed.Value < _minimumInterval) return null;
        }

        _lastInjectedAt = now;
        return Render(now, elapsed);
    }

    /// <summary>把读数渲染成三行文本。</summary>
    private static string Render(DateTimeOffset now, TimeSpan? elapsed)
    {
        // 本地时间带偏移，另附 IANA 名——只有偏移没有名字时，
        // 模型分不清夏令时切换前后的同一偏移。
        var local = now.ToLocalTime();
        var zone = TimeZoneInfo.Local;

        // 负数到这里已不可能：TryRender 在算间隔时就把回退钳成了零。
        // 这里不再重复判负——那会是永不执行的死代码，而它看起来像一道防线。
        var elapsedText = elapsed is { } span ? FormatElapsed(span) : "unavailable";

        return $"""
            [时间] {local.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)} {FormatOffset(local)}（{zone.Id}）
            [时区] 服务端本地时区
            [距上次] {elapsedText}
            """;
    }

    /// <summary>格式化成 <c>+08:00</c> 这样的偏移。</summary>
    /// <remarks>
    /// 手工拼而不是 <c>ToString("zzz")</c>：后者受当前文化影响，
    /// 中文环境下格式可能不同。这条文本要跨机器对照，必须固定形态。
    /// </remarks>
    private static string FormatOffset(DateTimeOffset value)
    {
        var offset = value.Offset;
        var sign = offset < TimeSpan.Zero ? '-' : '+';
        offset = offset.Duration();
        return $"{sign}{offset.Hours:00}:{offset.Minutes:00}";
    }

    /// <summary>
    /// 格式化间隔：紧凑的整秒单位。
    /// </summary>
    /// <remarks>
    /// 用 <c>45s</c> / <c>12m30s</c> / <c>2h5m</c> 这种紧凑形态而不是
    /// "45 秒"：它要出现在每一轮的对话里，长度直接换成预算成本。
    /// <para>
    /// <b>只报整秒</b>：毫秒精度对模型判断"隔了多久"毫无帮助。
    /// </para>
    /// </remarks>
    private static string FormatElapsed(TimeSpan span)
    {
        var totalSeconds = (int)span.TotalSeconds;

        if (totalSeconds < 60) return $"{totalSeconds}s";

        if (totalSeconds < 3600)
        {
            var minutes = totalSeconds / 60;
            var seconds = totalSeconds % 60;
            return seconds == 0 ? $"{minutes}m" : $"{minutes}m{seconds}s";
        }

        var hours = totalSeconds / 3600;
        var remainingMinutes = totalSeconds % 3600 / 60;
        return remainingMinutes == 0 ? $"{hours}h" : $"{hours}h{remainingMinutes}m";
    }
}
