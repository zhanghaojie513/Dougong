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
}
