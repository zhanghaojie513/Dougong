using System.Text;

namespace Dougong.Agent;

/// <summary>
/// 限制单个工具结果进入**模型上下文**时的长度，避免一次大输出挤爆窗口。
/// </summary>
/// <remarks>
/// <b>只作用于模型上下文，不作用于持久化</b>：会话日志里的工具结果是完整原文
/// （<see cref="LoopAgentRuntime"/> 在提交记录时写的是未截断内容），
/// 恢复会话时也从日志重建完整内容。
/// <para>
/// 这个界限是刻意的。如果在 dispatcher 出口或日志写入前截断，
/// 恢复出的会话会比原始会话少数据，而且无法还原——那是数据丢失，不是上下文卫生。
/// </para>
/// <para>
/// <b>截断必须可观察</b>：结果里带上明确的截断标记与原始长度。
/// 模型看不到标记就会把残缺数据当成完整事实，
/// 那比直接报错更危险。这与"上报失败"是同一原则的另一面。
/// </para>
/// </remarks>
public static class ToolResultLimits
{
    /// <summary>
    /// 单个工具结果进入上下文的最大字符数。
    /// </summary>
    /// <remarks>
    /// 取值依据：一个 128K token 窗口约合 200K 字符（中英混合按 1.5 字符/token 估）。
    /// 单条结果 32K 字符约占 16% 窗口，留足够空间给多轮对话与其它工具结果。
    /// <para>
    /// 比这更大就基本可以断定是"把整个文件/整段日志灌进来"，
    /// 而不是模型需要一次读完的信息。
    /// </para>
    /// </remarks>
    public const int MaxContextChars = 32_000;

    /// <summary>截断时保留头部多少字符。</summary>
    /// <remarks>
    /// 头尾都留：头部通常是结构（JSON 起始、表格表头、命令回显的开头），
    /// 尾部通常是结论或错误信息——这两处对判断最有用，中间多是重复内容。
    /// </remarks>
    public const int HeadChars = 24_000;

    /// <summary>截断时保留尾部多少字符。</summary>
    public const int TailChars = 6_000;

    /// <summary>
    /// 按上限裁剪一段工具结果。未超限时原样返回。
    /// </summary>
    /// <param name="content">完整结果文本。</param>
    /// <param name="truncated">是否发生了截断。</param>
    /// <remarks>
    /// <b>裁剪后的内容不再是合法 JSON</b>（若原是 JSON）。这是刻意的、也是必须承认的：
    /// 中间插入的标记会让结构断开。工具结果是以**文本**进入模型历史的
    /// （<c>ChatMessage</c> 的 text 内容），模型不解析它，
    /// 所以"不是合法 JSON"本身不会导致请求失败。
    /// <para>
    /// 但这也意味着模型如果尝试解析它会失败——标记里因此必须写明"已截断"，
    /// 让模型知道自己在看残缺数据，而不是数据本身有问题。
    /// 早先的注释写成"标记写成 JSON 片段、用 <c>__truncated</c> 键"，
    /// 与实现不符，会误导维护者以为 JSON 仍可解析。已按实际行为更正。
    /// </para>
    /// </remarks>
    public static string Clamp(string content, out bool truncated)
    {
        ArgumentNullException.ThrowIfNull(content);

        truncated = false;
        if (content.Length <= MaxContextChars) return content;

        truncated = true;

        // 头尾之和必须小于上限，否则两个切片会重叠、同一段内容出现两次。
        // 当前 24000 + 6000 < 32000 成立，但这是参数关系而不是代码保证——
        // 将来有人调大 HeadChars/TailChars 就会静默出现重复内容。
        var headChars = Math.Min(HeadChars, content.Length / 2);
        var tailChars = Math.Min(TailChars, content.Length - headChars);

        var head = content[..headChars];
        var tail = content[^tailChars..];
        var omitted = content.Length - headChars - tailChars;

        var marker = new StringBuilder()
            .Append("\n\n[结果过长已截断：省略中间 ").Append(omitted)
            .Append(" 字符，原文共 ").Append(content.Length)
            .Append(" 字符。以下内容不是完整数据，请勿按完整结果推断。]\n\n")
            .ToString();

        return head + marker + tail;
    }
}
