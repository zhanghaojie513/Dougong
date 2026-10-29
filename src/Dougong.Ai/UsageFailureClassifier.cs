using System.Net;
using System.Text.Json;
using Microsoft.Extensions.AI;

namespace Dougong.Ai;

/// <summary>
/// 把模型调用抛出的异常归类成 <see cref="UsageFailureKind"/>。
/// </summary>
/// <remarks>
/// <b>为什么单独一个类</b>：分类规则需要被单独测试，而它只依赖异常对象。
/// 混进 <see cref="UsageRecordingChatClient"/> 里就只能靠"造一次完整调用"
/// 来验证，而那种测试很难覆盖 401/429/超时 全部组合。
/// <para>
/// <b>判据优先级：HTTP 状态码 &gt; 异常类型 &gt; 消息文本</b>。
/// 前两者是结构化信息；消息文本是最后手段，只在异常确实没带状态码时用
/// （例如某些 provider 把状态码写进 JSON 错误体）。
/// </para>
/// </remarks>
public static class UsageFailureClassifier
{
    /// <summary>分类。<paramref name="callerCancelled"/> 表示调用方的令牌已触发。</summary>
    /// <param name="error">抛出的异常。</param>
    /// <param name="callerCancelled">
    /// 调用方的 <see cref="CancellationToken"/> 是否已取消。
    /// **这一位决定了 <see cref="UsageFailureKind.Cancelled"/> 与
    /// <see cref="UsageFailureKind.Timeout"/> 的区别**，不能从异常本身推断。
    /// </param>
    public static UsageFailureKind Classify(Exception error, bool callerCancelled)
    {
        ArgumentNullException.ThrowIfNull(error);

        // 1. 状态码最可靠。HttpClientChatClient 会把 StatusCode 写进异常。
        if (FindStatusCode(error) is { } status)
            return FromStatusCode(status);

        // 2. 取消 vs 超时：两者都是取消类异常，区别只在"谁取消的"。
        if (error is OperationCanceledException or TimeoutException)
            return callerCancelled ? UsageFailureKind.Cancelled : UsageFailureKind.Timeout;

        // 3. 解析失败：协议实现与 provider 输出不符，属于 SDK 侧问题。
        if (error is JsonException or FormatException or InvalidOperationException)
            return UsageFailureKind.InvalidResponse;

        // 4. 走不通的路：连接被拒、DNS、TLS。
        if (error is HttpRequestException or System.Net.Sockets.SocketException)
            return UsageFailureKind.NetworkError;

        return UsageFailureKind.Unknown;
    }

    /// <summary>沿内部异常链找 HTTP 状态码。</summary>
    /// <remarks>
    /// 状态码可能被包了几层：连接器的异常 → 我们的 <c>HttpRequestException</c>
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

    private static UsageFailureKind FromStatusCode(HttpStatusCode status) => (int)status switch
    {
        401 or 403 => UsageFailureKind.Unauthorized,
        402 => UsageFailureKind.QuotaExceeded,
        404 => UsageFailureKind.NotFound,
        429 => UsageFailureKind.RateLimited,
        >= 500 => UsageFailureKind.ServerError,
        _ => UsageFailureKind.Unknown,
    };

    /// <summary>
    /// 该类别是否值得计入"故障率"。
    /// </summary>
    /// <remarks>
    /// <see cref="UsageFailureKind.Cancelled"/> 返回 false：用户主动取消不是故障。
    /// 把它算进故障率会让"我按了几次 Ctrl+C"污染监控。
    /// </remarks>
    public static bool CountsAsFault(UsageFailureKind kind) =>
        kind is not (UsageFailureKind.None or UsageFailureKind.Cancelled);
}
