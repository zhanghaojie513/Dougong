namespace Dougong.Ai;

/// <summary>
/// 同一协议内部的方言差异开关。
/// </summary>
/// <remarks>
/// **为什么需要它**：OpenAI 兼容端点并不真的兼容。同一件事在不同实现里字段名不同：
/// 思考内容有的叫 <c>reasoning_content</c>（DeepSeek/GLM），有的叫 <c>reasoning</c>；
/// 输出上限有的认 <c>max_tokens</c>，有的只认 <c>max_completion_tokens</c>，
/// 有的完全不接受该字段。写死任何一边，接另一家就静默出错或直接 400（G12/G13）。
/// <para>
/// **为什么不建完整的 compat 矩阵**：`goal.md` §3 把"自研 provider 矩阵"列为反目标。
/// 这里只放**已经踩过或明确知道**的开关，且全部带默认值——
/// 不预设没见过的差异，与 `architecture.md` §4.4 拒绝"预先冻结资源键格式"同一判据。
/// </para>
/// </remarks>
public sealed record WireCompat
{
    /// <summary>
    /// 思考/推理参数在请求体里的写法。
    /// </summary>
    /// <remarks>
    /// <b>为什么需要一个枚举而不是一个字段名</b>：这不是"字段叫什么"的问题，
    /// 而是**参数结构完全不同**。同样是 high，各家要的分别是：
    /// <list type="bullet">
    /// <item>OpenAI：<c>reasoning_effort: "high"</c></item>
    /// <item>DeepSeek / Kimi：<c>thinking: { type: "enabled" }</c> + <c>reasoning_effort: "high"</c></item>
    /// <item>GLM：<c>thinking: { type: "enabled", clear_thinking: false }</c> + <c>reasoning_effort</c></item>
    /// <item>Qwen：<c>enable_thinking: true</c> + <c>reasoning_effort</c></item>
    /// <item>OpenRouter：<c>reasoning: { effort: "high" }</c>（嵌套对象）</item>
    /// </list>
    /// 用一个字段名表达不了这五种形状。
    /// </remarks>
    public enum ThinkingFormat
    {
        /// <summary>
        /// OpenAI 官方写法：只发 <c>reasoning_effort</c> 一个字符串。
        /// </summary>
        ReasoningEffort,

        /// <summary>
        /// DeepSeek / Kimi 系：<c>thinking</c> 开关 + 可选 <c>reasoning_effort</c>。
        /// </summary>
        DeepSeek,

        /// <summary>
        /// 智谱 GLM：<c>thinking: { type, clear_thinking }</c> + <c>reasoning_effort</c>。
        /// </summary>
        Zai,

        /// <summary>
        /// 通义千问：<c>enable_thinking</c> 布尔 + 可选 <c>reasoning_effort</c>。
        /// </summary>
        Qwen,

        /// <summary>
        /// OpenRouter：嵌套的 <c>reasoning: { effort }</c> 对象。
        /// </summary>
        OpenRouter,

        /// <summary>
        /// 不发任何推理参数。默认值——**没配就不发，不会静默发一个对方不认的值**。
        /// </summary>
        None,
    }


    /// <summary>默认值：与当前已实测通过的端点行为一致。</summary>
    public static WireCompat Default { get; } = new();

    /// <summary>
    /// 思考内容在响应流与请求体里的字段名。
    /// </summary>
    /// <remarks>
    /// <c>reasoning_content</c> 是 DeepSeek/GLM 系；部分网关用 <c>reasoning</c>；
    /// 也有把思考当普通 <c>content</c> 返回的（那种情况这里不适用，需要另设开关）。
    /// </remarks>
    public string ReasoningField { get; init; } = "reasoning_content";

    /// <summary>
    /// 输出上限的字段名。<c>null</c> 表示**不发送**该字段（沿用端点默认值）。
    /// </summary>
    /// <remarks>
    /// OpenAI 新模型用 <c>max_completion_tokens</c>，大量兼容服务只认 <c>max_tokens</c>，
    /// 字段名写错会直接 400 且错误信息通常含糊。默认 null 保持现状（不发），
    /// 避免改变已验证端点的行为。
    /// </remarks>
    public string? MaxTokensField { get; init; }

    /// <summary>
    /// 思考参数的写法。默认 <see cref="ThinkingFormat.None"/>（不发）。
    /// </summary>
    /// <remarks>
    /// <b>默认不发是刻意的</b>：发一个对方不认的字段，轻则被忽略（用户以为设置生效了），
    /// 重则 400 且错误信息看不出是哪个参数的问题。所以只有明确知道某家怎么写时才启用。
    /// </remarks>
    public ThinkingFormat Thinking { get; init; } = ThinkingFormat.None;

    /// <summary>
    /// 统一档位到该模型实际档位词的映射。null 表示直接透传档位词。
    /// </summary>
    /// <remarks>
    /// <b>为什么需要映射而不是透传</b>：各家档位词不一样（有的只有 low/medium/high，
    /// 有的支持 xhigh/max，GLM 用 <c>none</c> 表示关闭）。把 <c>max</c> 原样发给只认
    /// <c>high</c> 的端点，会被 400 拒绝或静默忽略。
    /// <para>
    /// <b>值为 null 表示该档位不支持</b>，此时不发任何参数——而不是退化成"发一个相近的"。
    /// 静默降级会让用户以为选的那个档位生效了。
    /// </para>
    /// </remarks>
    public IReadOnlyDictionary<string, string?>? ThinkingLevelMap { get; init; }
}
