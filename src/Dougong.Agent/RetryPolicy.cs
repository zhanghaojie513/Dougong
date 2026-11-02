using System.Globalization;

namespace Dougong.Agent;

/// <summary>
/// 模型请求失败后的重试策略。
/// </summary>
/// <remarks>
/// <b>为什么需要它</b>：provider 的 429、5xx、超时、连接中断都是**暂时**的——
/// 等一会儿再试通常就成功了。没有重试时，一次瞬时故障会直接结束整轮，
/// 用户看到"模型流中断"然后必须手动重说一遍，而模型那边什么都没发生。
///
/// <para>
/// <b>照 dsh 的 <c>dsh-llm-retry</c> 做，关键决定一致</b>：
/// <list type="bullet">
/// <item><b>只在"整步可重跑"的边界重试</b>：流一旦已经开始吐内容，
/// 重发会让宿主看到重复文本（本地已经 yield 出去了），所以**已经产出过任何
/// 可观察增量就不再重试**。dsh 的理由相同：raw stream 无法干净地撤回首包。</item>
/// <item><b>只重试瞬时类</b>：429 / 5xx / 超时 / 网络。401、402、404、上下文超限
/// 都不是"等一会儿就好"，重试只是浪费时间和额度。</item>
/// <item><b>指数退避 + 抖动</b>：固定间隔会让多个客户端同步重试，
/// 在 provider 侧形成脉冲。抖动打散它们。</item>
/// <item><b>provider 给的 Retry-After 优先</b>：它有真实的服务端知识，
/// 我们的猜测不如它准。</item>
/// </list>
/// </para>
///
/// <para>
/// <b>与 dsh 的差别</b>：它有两种模式（normal 有限次 / always 无限次），
/// 我们只做 normal。always 模式在 CLI 场景里的实际效果是"卡住不动"——
/// 用户看不到任何进展，只能 Ctrl+C，而那与直接失败没有区别，
/// 却多烧了额度。这个判断记录在 <c>porting-differences.md</c>。
/// </para>
/// </remarks>
public sealed record RetryPolicy
{
    /// <summary>默认策略：最多 5 次，500ms 起，最长 10s，10% 抖动。</summary>
    /// <remarks>
    /// 与 dsh 的 normal 模式默认值一致。500ms 的起点足够短，
    /// 让真正瞬时的故障（一次丢包）几乎无感。
    /// </remarks>
    public static readonly RetryPolicy Default = new();

    /// <summary>最多重试几次（不含首次请求）。</summary>
    /// <remarks>
    /// <b>0 表示不重试</b>，用于自动化与检查——它们要的是确定性而不是韧性。
    /// </remarks>
    public int MaxRetries { get; init; } = 5;

    /// <summary>第一次退避的时长。</summary>
    public TimeSpan InitialDelay { get; init; } = TimeSpan.FromMilliseconds(500);

    /// <summary>退避的上限。指数增长到此为止。</summary>
    public TimeSpan MaxDelay { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// 抖动比例（0–1）。
    /// </summary>
    /// <remarks>
    /// 实际延迟在 <c>[1-r, 1+r]</c> 区间内随机。0.1 表示 ±10%。
    /// <b>为 0 会让重试同步</b>：多个进程同时被限流后，它们会以完全相同的
    /// 节奏再次撞上去。
    /// </remarks>
    public double JitterRatio { get; init; } = 0.1;

    /// <summary>哪些失败类别值得重试。</summary>
    /// <remarks>
    /// <b>白名单而不是黑名单</b>：新增的失败类别默认不重试。反过来写的话，
    /// 一个新出现的永久性错误（比如协议变更）会被自动重试到耗尽，
    /// 而那只是把一次清晰的失败变成一次缓慢的失败。
    /// </remarks>
    public IReadOnlySet<RetryableFailure> RetryableFailures { get; init; } =
        new HashSet<RetryableFailure>
        {
            RetryableFailure.Timeout,
            RetryableFailure.RateLimited,
            RetryableFailure.ServerError,
            RetryableFailure.NetworkError,
        };

    /// <summary>不重试任何失败。</summary>
    public static RetryPolicy None => new() { MaxRetries = 0 };

    /// <summary>校验取值是否自洽。</summary>
    public bool IsValid =>
        MaxRetries >= 0
        && InitialDelay >= TimeSpan.Zero
        && MaxDelay >= InitialDelay
        && JitterRatio is >= 0 and <= 1;

    /// <summary>
    /// 算第 <paramref name="retry"/> 次重试（从 1 开始）应等多久。
    /// </summary>
    /// <param name="retry">第几次重试，从 1 开始。</param>
    /// <param name="random">0–1 的随机源；检查里注入固定值以取得确定性。</param>
    /// <param name="providerHint">
    /// provider 通过 Retry-After 给出的建议。有效时优先于本地退避。
    /// </param>
    /// <remarks>
    /// <b>优先用 provider 的建议，但不超过我们的上限</b>：它知道自己的限流窗口，
    /// 我们不知道。但若无条件接受，一个配错的 <c>Retry-After: 3600</c>
    /// 会让程序静默挂一小时——那比失败更糟。
    /// </remarks>
    public TimeSpan DelayFor(int retry, double random, TimeSpan? providerHint = null)
    {
        if (retry < 1) throw new ArgumentOutOfRangeException(nameof(retry));

        if (providerHint is { } hint && hint > TimeSpan.Zero && hint <= MaxDelay)
            return hint;

        // 指数：2^(retry-1)。用 double 算再截断，避免 TimeSpan 乘法溢出。
        var exponent = Math.Min(retry - 1, 30);
        var exponential = InitialDelay.TotalMilliseconds * Math.Pow(2, exponent);
        var capped = Math.Min(exponential, MaxDelay.TotalMilliseconds);

        // 抖动区间 [1-r, 1+r]，再夹回上限。
        var jittered = capped * (1 - JitterRatio + 2 * JitterRatio * random);
        return TimeSpan.FromMilliseconds(Math.Min(jittered, MaxDelay.TotalMilliseconds));
    }
}

/// <summary>
/// 可重试的失败类别。
/// </summary>
/// <remarks>
/// <b>与 <c>UsageFailureKind</c> 分开</b>：那个枚举是"发生了什么"（用于统计），
/// 这个是"要不要再试一次"（用于决策）。合成一个会让两件事互相牵连——
/// 加一个统计类别就得决定它是否可重试，而很多类别根本没有那个问题。
/// <para>
/// <b>也不能直接复用那个枚举</b>：<c>Dougong.Agent</c> 不依赖 <c>Dougong.Ai</c>
/// （依赖方向是 Ai → Agent）。本枚举因此只描述"从重试的角度看这是什么失败"，
/// 由 Ai 侧的适配器把 <c>UsageFailureKind</c> 映射过来。
/// </para>
/// </remarks>
public enum RetryableFailure
{
    /// <summary>不属于任何可重试类别。</summary>
    /// <remarks>
    /// <b>缺省值是"不可重试"</b>：一个没被判定的失败不该被自动重试。
    /// 反过来（缺省可重试）会让未知错误被反复重发，把一次清晰的失败
    /// 变成一次缓慢的失败，还多烧额度。
    /// </remarks>
    None,

    /// <summary>超时：等不到响应，且不是调用方取消。</summary>
    Timeout,

    /// <summary>请求过于频繁（HTTP 429）。</summary>
    RateLimited,

    /// <summary>服务端错误（HTTP 5xx）。</summary>
    ServerError,

    /// <summary>网络层失败：连不上、DNS、TLS。</summary>
    NetworkError,
}
