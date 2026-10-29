namespace Dougong.Agent;

/// <summary>
/// provider 判定本次请求的上下文超限。
/// </summary>
/// <remarks>
/// <b>为什么用专用异常而不是让上层解析消息文本</b>：识别逻辑（26 条 provider 文案 +
/// 排除清单）在 <see cref="ContextOverflowDetector"/> 里，只做一次。
/// 上层若也去匹配字符串，就是两份实现——改一边忘另一边，症状是"偶尔能恢复"。
/// <para>
/// <b>为什么带上 <c>InnerException</c></b>：压缩重试失败时要报**原始
/// provider 错误**。用户需要看到"provider 说 prompt is too long: 233153 tokens"，
/// 而不是我们包装过的"压缩失败"。丢失原始信息会让排查从"改配置"变成"猜"。
/// </para>
/// </remarks>
public sealed class ContextOverflowException : InvalidOperationException
{
    /// <summary>构造。</summary>
    /// <param name="message">面向宿主的说明。</param>
    /// <param name="providerError">provider 的原始错误异常，用于耗尽时如实上报。</param>
    /// <param name="reportedInputTokens">
    /// provider 报告的输入 token 数（若能拿到）。为 null 表示拿不到——
    /// 与"是 0"不同，后者会让人以为输入为空。
    /// </param>
    public ContextOverflowException(
        string message,
        Exception? providerError = null,
        long? reportedInputTokens = null)
        : base(message, providerError)
    {
        ProviderError = providerError;
        ReportedInputTokens = reportedInputTokens;
    }

    /// <summary>provider 的原始错误。</summary>
    public Exception? ProviderError { get; }

    /// <summary>provider 报告的输入 token；null 表示未知。</summary>
    public long? ReportedInputTokens { get; }
}
