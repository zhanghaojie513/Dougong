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
    public static string Clamp(string content, out bool truncated)
    {
        ArgumentNullException.ThrowIfNull(content);

        truncated = false;
        if (content.Length <= MaxContextChars) return content;

        truncated = true;

        var head = content[..HeadChars];
        var tail = content[^TailChars..];
        var omitted = content.Length - HeadChars - TailChars;

        // 标记写成 JSON 片段而不是散落的说明文字：
        // 工具结果本身是 JSON，混进纯文本会让模型误以为整体不是 JSON。
        // 用 `__truncated` 这个明显不会与业务字段冲突的键。
        var marker = new StringBuilder()
            .Append("\n...[结果过长已截断：省略中间 ").Append(omitted)
            .Append(" 字符，原文共 ").Append(content.Length).Append(" 字符]...\n")
            .ToString();

        return head + marker + tail;
    }
}
