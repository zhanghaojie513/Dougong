using System.Net;
using System.Net.Sockets;

namespace Dougong.Agent;

/// <summary>
/// 把模型调用抛出的异常归类成"值不值得再试一次"。
/// </summary>
/// <remarks>
/// <b>为什么放在 Agent 层而不是复用 <c>UsageFailureKind</c></b>：那个枚举属于
/// <c>Dougong.Ai</c>（统计用），而 <c>Dougong.Agent</c> 不依赖它——依赖方向是
/// Ai → Agent。重试决策又必须发生在循环里（Agent 层），所以这里自带一份判据。
/// <para>
/// <b>两个枚举不该合并</b>：统计回答"发生了什么"，重试回答"要不要再试"。
/// 合并后每加一个统计类别都得顺带回答一个它可能根本不关心的问题，
/// 而且两边的默认值语义相反——统计的未知类是 <c>Unknown</c>（要记录），
/// 重试的未知类必须是"不重试"（要停下来）。
/// </para>
/// <para>
/// <b>判据优先级：状态码 &gt; 异常类型 &gt; 结构化的取消标志</b>。
/// 不从消息文本猜：文本会随 provider 与本地化变化，而误判成"可重试"
/// 的代价是在一个永久性错误上反复重发、反复计费。
/// </para>
/// </remarks>
public static class RetryClassifier
{
    /// <summary>
    /// 归类一个失败的请求尝试。
    /// </summary>
    /// <param name="error">抛出的异常。</param>
    /// <param name="callerCancelled">
    /// 调用方的令牌是否已触发。<b>这一位决定了"超时"与"用户取消"</b>——
    /// 两者都是取消类异常，区别只在谁取消的。用户取消永远不重试。
    /// </param>
    /// <returns>可重试类别；<see cref="RetryableFailure.None"/> 表示不该重试。</returns>
    public static RetryableFailure Classify(Exception error, bool callerCancelled)
    {
        ArgumentNullException.ThrowIfNull(error);

        // 用户取消：绝不重试。它表达的是"停下来"，重试等于无视这个指令。
        if (callerCancelled) return RetryableFailure.None;

        // 上下文超限：能靠压缩历史恢复，但那是另一套机制。
        // 放在这里重试只会用同样的输入再撞一次同样的墙。
        if (error is ContextOverflowException) return RetryableFailure.None;

        // 状态码：最可靠的结构化信息。
        if (FindStatusCode(error) is { } status)
        {
            return (int)status switch
            {
                429 => RetryableFailure.RateLimited,

                // **408 是"服务端等你太久"，不是"你的请求错了"**。
                // 它带了状态码，所以走不到下面那个超时分支；而它也不在 429/5xx
                // 的范围里——漏掉它会让一次纯等待型的失败被当成永久错误。
                // 实测踩到：只写 `>= 500` 与 `429` 时，408 落到 None。
                408 => RetryableFailure.Timeout,

                >= 500 => RetryableFailure.ServerError,
                _ => RetryableFailure.None,
            };
        }

        // 取消类但调用方没取消：是超时（或底层传输超时）。
        if (error is TimeoutException or OperationCanceledException)
            return RetryableFailure.Timeout;

        // 连不上：DNS、TLS、连接被拒、连接被重置。
        if (error is HttpRequestException or SocketException)
            return RetryableFailure.NetworkError;

        // 其余一律不重试。**这个缺省是有意的**：未知错误反复重发
        // 只是把一次清晰的失败变成一次缓慢的失败，还多烧额度。
        return RetryableFailure.None;
    }

    /// <summary>沿内部异常链找 HTTP 状态码。</summary>
    /// <remarks>
    /// 状态码可能被包了几层：连接器异常 → 我们的 <c>HttpRequestException</c>
    /// → 调用方的聚合异常。只看最外层会漏掉。
    /// </remarks>
    private static HttpStatusCode? FindStatusCode(Exception? error)
    {
        for (var current = error; current is not null; current = current.InnerException)
        {
            if (current is HttpRequestException { StatusCode: { } status })
                return status;
        }

        return null;
    }

    /// <summary>
    /// 从异常里取 provider 建议的等待时长（<c>Retry-After</c>）。
    /// </summary>
    /// <remarks>
    /// <b>拿不到就返回 null，不猜</b>。用 <c>Retry-After</c> 响应头而不是正文里的
    /// 文案：前者是协议字段，后者各家写法不同。
    /// <para>
    /// 目前只能从 <c>Exception.Data</c> 里取——连接器需要把
    /// 响应头放进去。取不到时退回本地退避，所以这不是必需路径。
    /// </para>
    /// </remarks>
    public static TimeSpan? FindProviderDelay(Exception? error)
    {
        for (var current = error; current is not null; current = current.InnerException)
        {
            if (current.Data[ProviderDelayKey] is int seconds && seconds > 0)
                return TimeSpan.FromSeconds(seconds);
            if (current.Data[ProviderDelayKey] is TimeSpan span && span > TimeSpan.Zero)
                return span;
        }

        return null;
    }

    /// <summary>
    /// 连接器把 <c>Retry-After</c> 放进异常 <c>Data</c> 时用的键。
    /// </summary>
    /// <remarks>
    /// 用公开常量而不是字符串字面量：写入端与读取端必须一致，
    /// 拼错时不会报错，只会静默退回本地退避——那种 bug 很难发现。
    /// </remarks>
    public const string ProviderDelayKey = "Dougong.RetryAfter";
}
