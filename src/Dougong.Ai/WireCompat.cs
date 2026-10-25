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
}
