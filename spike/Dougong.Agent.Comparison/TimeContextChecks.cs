using Dougong.Agent;

namespace Dougong.Agent.Comparison;

/// <summary>
/// 时间上下文的验收。
/// </summary>
/// <remarks>
/// <b>可观察面是"模型收到的那条消息"</b>，以及它在什么条件下**不**出现：
/// <list type="bullet">
/// <item>读数含时间、时区偏移、IANA 名、间隔</item>
/// <item>不足最小间隔时**不产生消息**（不是产生一条说"没有"）</item>
/// <item>第一次读数报 <c>unavailable</c>（没有基线）</item>
/// <item>时钟回退时间隔归零，不出现负数</item>
/// <item>它是 user 角色，不是 system</item>
/// </list>
/// </remarks>
public static class TimeContextChecks
{
    public static Task<int> RunAsync()
    {
        var failures = 0;
        failures += Check("time-reading-has-timestamp-and-zone", HasTimestampAndZone);
        failures += Check("time-first-reading-says-unavailable", FirstIsUnavailable);
        failures += Check("time-respects-minimum-interval", RespectsInterval);
        failures += Check("time-reports-elapsed-compactly", ElapsedFormat);
        failures += Check("time-clock-rollback-clamps-to-zero", RollbackClampsToZero);
        failures += Check("time-offset-is-culture-independent", OffsetStable);

        Console.WriteLine(failures == 0
            ? "TIME-CONTEXT: ALL PASS"
            : $"TIME-CONTEXT: {failures} FAILURE(S)");
        return Task.FromResult(failures);
    }

    /// <summary>读数必须含时间戳、偏移与 IANA 时区名。</summary>
    /// <remarks>
    /// <b>只有时间没有时区是危险的</b>：模型会把它当本地时间解读，
    /// 而跨时区协作时同一个"14:30"指的不是同一刻。
    /// </remarks>
    private static string? HasTimestampAndZone()
    {
        var clock = new FakeClock(new DateTimeOffset(2026, 11, 2, 6, 30, 0, TimeSpan.Zero));
        var text = new TimeContext(clock, TimeSpan.Zero).TryRender();

        if (text is null) return "首次读数没有产出";

        // 时间戳：本地时间形态。
        if (!System.Text.RegularExpressions.Regex.IsMatch(text, @"\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}"))
            return $"没有时间戳：{text}";

        // 偏移：+08:00 这种形态。
        if (!System.Text.RegularExpressions.Regex.IsMatch(text, @"[+-]\d{2}:\d{2}"))
            return $"没有时区偏移：{text}";

        // 时区名：括号里那一段。
        return text.Contains("（", StringComparison.Ordinal)
            && text.Contains("）", StringComparison.Ordinal)
                ? null
                : $"没有时区名：{text}";
    }

    /// <summary>第一次读数没有基线，间隔应为 unavailable。</summary>
    private static string? FirstIsUnavailable()
    {
        var clock = new FakeClock(DateTimeOffset.UtcNow);
        var text = new TimeContext(clock, TimeSpan.Zero).TryRender();

        return text?.Contains("unavailable", StringComparison.Ordinal) == true
            ? null
            : $"首次读数没有报 unavailable：{text}";
    }

    /// <summary>不足最小间隔时不产出读数。</summary>
    /// <remarks>
    /// <b>返回 null 而不是产出一条"距上次不足 10 分钟"</b>：
    /// 那只是在浪费预算，模型不需要知道"这次不报时间"。
    /// </remarks>
    private static string? RespectsInterval()
    {
        var clock = new FakeClock(DateTimeOffset.UtcNow);
        var context = new TimeContext(clock, TimeSpan.FromMinutes(10));

        if (context.TryRender() is null) return "首次读数没有产出";

        clock.Advance(TimeSpan.FromMinutes(5));
        if (context.TryRender() is not null) return "5 分钟后又产出了一条（间隔 10 分钟）";

        clock.Advance(TimeSpan.FromMinutes(6));   // 累计 11 分钟
        return context.TryRender() is not null
            ? null
            : "过了间隔仍不产出";
    }

    /// <summary>间隔用紧凑形态，不用长句。</summary>
    private static string? ElapsedFormat()
    {
        var clock = new FakeClock(DateTimeOffset.UtcNow);
        var context = new TimeContext(clock, TimeSpan.Zero);

        context.TryRender();   // 建立基线

        clock.Advance(TimeSpan.FromSeconds(45));
        var shortSpan = context.TryRender();
        if (shortSpan?.Contains("45s", StringComparison.Ordinal) != true)
            return $"45 秒没有渲染成 45s：{shortSpan}";

        clock.Advance(TimeSpan.FromMinutes(12) + TimeSpan.FromSeconds(30));
        var medium = context.TryRender();
        return medium?.Contains("12m30s", StringComparison.Ordinal) == true
            ? null
            : $"12 分 30 秒没有渲染成 12m30s：{medium}";
    }

    /// <summary>时钟回退时间隔归零，不出现负数。</summary>
    /// <remarks>
    /// <b>这是真实会发生的事</b>：NTP 校正、用户手动改表、虚拟机时间回拨。
    /// 负的间隔会让模型以为事件倒序，而它无法判断那是时钟问题。
    /// </remarks>
    private static string? RollbackClampsToZero()
    {
        var clock = new FakeClock(DateTimeOffset.UtcNow);
        var context = new TimeContext(clock, TimeSpan.Zero);

        context.TryRender();   // 基线

        clock.Advance(TimeSpan.FromMinutes(-5));   // 时钟回退 5 分钟
        var text = context.TryRender();

        if (text is null) return "时钟回退后没有产出";
        if (text.Contains('-', StringComparison.Ordinal) && text.Contains("s\n", StringComparison.Ordinal))
        {
            // 只关心"距上次"那一行是否为负。
            var lastLine = text.Split('\n').FirstOrDefault(l => l.Contains("距上次", StringComparison.Ordinal));
            if (lastLine?.Contains('-') == true)
                return $"间隔出现了负数：{lastLine}";
        }

        return text.Contains("0s", StringComparison.Ordinal)
            ? null
            : $"回退后间隔不是 0s：{text}";
    }

    /// <summary>偏移格式不受当前文化影响。</summary>
    /// <remarks>
    /// <b>这条钉的是可移植性</b>：<c>ToString("zzz")</c> 在部分文化下
    /// 输出不同形态。这条文本要跨机器对照（用户把诊断贴出来），
    /// 形态必须固定。
    /// </remarks>
    private static string? OffsetStable()
    {
        var original = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture =
                new System.Globalization.CultureInfo("ar-SA");

            var clock = new FakeClock(DateTimeOffset.UtcNow);
            var text = new TimeContext(clock, TimeSpan.Zero).TryRender();

            // 阿拉伯语文化下数字可能变成阿拉伯-印度数字；检查仍是 ASCII 数字。
            return text is not null
                && System.Text.RegularExpressions.Regex.IsMatch(text, @"[+-]\d{2}:\d{2}")
                    ? null
                    : $"偏移格式随文化变了：{text}";
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = original;
        }
    }

    // ── 支撑 ─────────────────────────────────────────────────────

    /// <summary>可控时钟。</summary>
    private sealed class FakeClock(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public void Advance(TimeSpan delta) => _now += delta;

        public override DateTimeOffset GetUtcNow() => _now;
    }

    private static int Check(string name, Func<string?> action)
    {
        try
        {
            var failure = action();
            if (failure is null)
            {
                Console.WriteLine($"PASS {name}");
                return 0;
            }

            Console.WriteLine($"FAIL {name}: {failure}");
            return 1;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"FAIL {name}: {ex.GetType().Name}: {ex.Message}");
            return 1;
        }
    }
}
