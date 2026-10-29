using System.Text.RegularExpressions;

namespace Dougong.Agent;

/// <summary>
/// 上下文溢出的识别。
/// </summary>
/// <remarks>
/// <b>为什么需要单独一层</b>：provider 报"上下文太长"的方式极不统一——
/// 有的返回 400 + 特定 error code，有的只在消息文本里写一句话，
/// 有的**根本不报错**而是静默接受（然后下一轮才炸）。
/// 不识别就没法做恢复，而用户看到的只是一个 400。
/// <para>
/// <b>模式表的来源</b>：照 <c>pi.net</c> 的 <c>Pi.Ai/Utils/Overflow.cs</c>
/// （MIT）整理，每条注明是哪个 provider 的文案。这不是猜的——
/// 那些措辞是各家的实际报错文本，分散在多个 issue 里。
/// </para>
/// <para>
/// <b>为什么必须有排除清单</b>：Bedrock 的限流错误 <c>ThrottlingException</c>
/// 文案里含 "too many tokens"，会被当成溢出。若不排除，一次限流就会
/// **白压一次历史**——有损操作，代价不可逆。
/// </para>
/// </remarks>
public static class ContextOverflowDetector
{
    /// <summary>单条正则的匹配上限，防止恶意/畸形输入造成回溯爆炸。</summary>
    private static readonly TimeSpan MatchTimeout = TimeSpan.FromSeconds(1);

    /// <summary>各 provider 报"输入超窗"的文案。</summary>
    /// <remarks>
    /// 顺序无关（任一命中即算），但每条都保留来源注释——
    /// 将来某家改文案时，能直接定位要改哪一条。
    /// </remarks>
    private static readonly Regex[] OverflowPatterns =
    [
        // Anthropic / z.ai
        new(@"prompt (?:is )?too long", RegexOptions.IgnoreCase, MatchTimeout),
        // z.ai 中国区
        new(@"prompt exceeds max length", RegexOptions.IgnoreCase, MatchTimeout),
        // Anthropic 413：请求体字节数超限
        new(@"request_too_large", RegexOptions.IgnoreCase, MatchTimeout),
        // Bedrock
        new(@"input is too long for requested model", RegexOptions.IgnoreCase, MatchTimeout),
        // OpenAI
        new(@"exceeds the context window", RegexOptions.IgnoreCase, MatchTimeout),
        // OpenAI 兼容代理（LiteLLM）
        new(@"exceeds (?:the )?(?:model'?s )?maximum context length",
            RegexOptions.IgnoreCase, MatchTimeout),
        // Google
        new(@"input token count.*exceeds the maximum", RegexOptions.IgnoreCase, MatchTimeout),
        // xAI
        new(@"maximum prompt length is \d+", RegexOptions.IgnoreCase, MatchTimeout),
        // Groq
        new(@"reduce the length of the messages", RegexOptions.IgnoreCase, MatchTimeout),
        // OpenRouter
        new(@"maximum context length is \d+ tokens", RegexOptions.IgnoreCase, MatchTimeout),
        // OpenRouter / Poolside
        new(@"exceeds (?:the )?maximum allowed input length", RegexOptions.IgnoreCase, MatchTimeout),
        // Together AI
        new(@"input \(\d+ tokens\) is longer than the model'?s context length",
            RegexOptions.IgnoreCase, MatchTimeout),
        // GitHub Copilot
        new(@"exceeds the limit of \d+", RegexOptions.IgnoreCase, MatchTimeout),
        // llama.cpp
        new(@"exceeds the available context size", RegexOptions.IgnoreCase, MatchTimeout),
        // LM Studio
        new(@"greater than the context length", RegexOptions.IgnoreCase, MatchTimeout),
        // MiniMax
        new(@"context window exceeds limit", RegexOptions.IgnoreCase, MatchTimeout),
        // Kimi For Coding
        new(@"exceeded model token limit", RegexOptions.IgnoreCase, MatchTimeout),
        // Mistral
        new(@"too large for model with \d+ maximum context length",
            RegexOptions.IgnoreCase, MatchTimeout),
        // Ollama
        new(@"prompt too long; exceeded (?:max )?context length", RegexOptions.IgnoreCase, MatchTimeout),
        // DashScope / Qwen
        new(@"range of input length should be", RegexOptions.IgnoreCase, MatchTimeout),
        // 通用兜底：结构化 code 与常见措辞
        new(@"context[_ ]length[_ ]exceeded", RegexOptions.IgnoreCase, MatchTimeout),
        new(@"too many tokens", RegexOptions.IgnoreCase, MatchTimeout),
        new(@"token limit exceeded", RegexOptions.IgnoreCase, MatchTimeout),
        // 中文 provider（部分国内厂商直接返回中文）
        new(@"超过最大长度", RegexOptions.None, MatchTimeout),
        new(@"上下文长度", RegexOptions.None, MatchTimeout),
    ];

    /// <summary>
    /// 命中任一条即视为非溢出。**必须先于溢出判定检查。**
    /// </summary>
    /// <remarks>
    /// 目前只有限流/服务不可用两类会误命中溢出文案：
    /// Bedrock 的 <c>ThrottlingException</c> 含 "too many tokens"。
    /// </remarks>
    private static readonly Regex[] NonOverflowPatterns =
    [
        new(@"^(Throttling error|Service unavailable):", RegexOptions.IgnoreCase, MatchTimeout),
        new(@"rate limit", RegexOptions.IgnoreCase, MatchTimeout),
        new(@"too many requests", RegexOptions.IgnoreCase, MatchTimeout),
    ];

    /// <summary>
    /// 错误文本是否表示上下文溢出。
    /// </summary>
    /// <param name="errorText">provider 的错误消息（含响应体片段亦可）。</param>
    /// <remarks>
    /// <b>只依据文本，不看状态码</b>：400 也可能是 schema 错误、参数不合法。
    /// 状态码能提示"这是客户端错误"，但区分不了具体哪一种——而只有溢出能靠
    /// 压缩恢复，压错一次是有损的。
    /// </remarks>
    public static bool IsContextOverflow(string? errorText)
    {
        if (string.IsNullOrWhiteSpace(errorText)) return false;

        // 排除优先：命中排除清单就直接判定不是溢出，不再看正向模式。
        if (NonOverflowPatterns.Any(pattern => pattern.IsMatch(errorText))) return false;

        return OverflowPatterns.Any(pattern => pattern.IsMatch(errorText));
    }

    /// <summary>
    /// 静默溢出：调用成功，但 provider 报的输入 token 数已超过我们以为的窗口。
    /// </summary>
    /// <param name="inputTokens">provider 报告的输入 token（含缓存命中部分）。</param>
    /// <param name="contextWindow">宿主配置的上下文窗口；0 或负数表示未知。</param>
    /// <remarks>
    /// <b>这是 pi.net 覆盖而我们没有的一种形态</b>：provider 不报错、正常返回，
    /// 但 <c>usage.input</c> 已经超过配置窗口。含义是**我们对窗口的认知是错的**
    /// （配置填小了，或 provider 的实际上限更小）。
    /// <para>
    /// 不处理它的后果：这一轮"成功"了，下一轮必然溢出——而那时用户看到的是
    /// 一个 400，与"配置填错"这个真实原因相距很远。
    /// </para>
    /// </remarks>
    public static bool IsSilentOverflow(long? inputTokens, int contextWindow) =>
        contextWindow > 0
        && inputTokens is { } tokens
        && tokens > contextWindow;

    /// <summary>
    /// 截断型溢出：正常结束但输出为 0，且输入几乎填满窗口。
    /// </summary>
    /// <param name="stopReason">provider 的结束原因（如 <c>length</c>）。</param>
    /// <param name="outputTokens">输出 token 数。</param>
    /// <param name="inputTokens">输入 token 数（含缓存命中部分）。</param>
    /// <param name="contextWindow">配置的窗口；0 或负数表示未知。</param>
    /// <remarks>
    /// <b>判据里的 0.99 是必要的</b>：只用"输出为 0"会把"模型主动不回答"
    /// 也判成溢出，而那是正常行为。加上"输入几乎填满窗口"才排除了那种情况。
    /// <para>
    /// 这种形态见于小米 MiMo 一类：输入吃满窗口后模型没有空间输出，
    /// <c>finish_reason</c> 给 <c>length</c> 而 output 为 0。
    /// </para>
    /// </remarks>
    public static bool IsTruncatedOverflow(
        string? stopReason, long? outputTokens, long? inputTokens, int contextWindow)
    {
        if (contextWindow <= 0) return false;
        if (!string.Equals(stopReason, "length", StringComparison.OrdinalIgnoreCase)) return false;
        if (outputTokens is not 0) return false;

        return inputTokens is { } tokens && tokens >= contextWindow * 0.99;
    }

    /// <summary>供测试使用的模式数量，用于确保模式表没有被意外清空。</summary>
    internal static int PatternCount => OverflowPatterns.Length;
}
