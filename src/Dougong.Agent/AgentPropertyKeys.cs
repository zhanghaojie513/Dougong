namespace Dougong.Agent;

/// <summary>
/// 跨层的扩展数据键。<b>用中性名，不用任何 provider 的字段名。</b>
/// </summary>
/// <remarks>
/// 为什么必须中性：`reasoning_content` 是 DeepSeek/GLM 系的字段名，`reasoning`
/// 是另一批网关的字段名。若 Agent 层直接读写 `<c>reasoning_content</c>`，
/// 接一家用别的字段名的 provider 时，思考内容会**静默丢失**——不报错，
/// 只是下一轮上下文里少了东西；若对方要求回传，则退化为 400 且错误信息不含字段名（G12）。
/// <para>
/// 分工：Agent 层只认这里的键，负责"这段是思考内容"这个**语义**；
/// 连接器负责把它翻译成具体 provider 的字段名。翻译表在
/// <c>ChatProviderConfig</c> 上（见 <c>WireCompat</c>）。
/// </para>
/// </remarks>
public static class AgentPropertyKeys
{
    /// <summary>
    /// 模型产出的思考内容（纯文本）。连接器写入、Agent 层读取并回传。
    /// </summary>
    public const string Reasoning = "dougong.reasoning";

    /// <summary>
    /// 推理强度档位（<c>minimal</c> / <c>low</c> / <c>medium</c> / <c>high</c> /
    /// <c>xhigh</c> / <c>max</c>，或 <c>off</c> 表示关闭）。
    /// </summary>
    /// <remarks>
    /// <b>由宿主写进 <see cref="Microsoft.Extensions.AI.ChatOptions.AdditionalProperties"/>，
    /// 由连接器翻译成具体 provider 的参数。</b>
    /// 和 <see cref="Reasoning"/> 一样用中性名：`reasoning_effort`、`thinking.type`、
    /// `enable_thinking` 都是某一家或某一族的写法，写进 Agent 层就等于把这层绑死到具体 provider。
    /// <para>
    /// <b>档位词是统一词汇，不是各家的词</b>。映射到对方支持的子集是连接器的责任
    /// （见 <c>WireCompat.ThinkingLevelMap</c>）——某家只认 low/medium/high 时，
    /// 把 max 原样发过去会被 400 或静默忽略。
    /// </para>
    /// </remarks>
    public const string ReasoningEffort = "dougong.reasoning-effort";
}
