namespace Dougong.Agent;

/// <summary>
/// provider 回报的一次真实用量，作为上下文估算的锚点。
/// </summary>
/// <remarks>
/// <b>为什么需要锚点</b>：纯字符估算与真实 token 数能差出几倍——系统提示与
/// 工具 schema 里结构化符号多，实际分词比线性估算更省。而 provider 每次
/// 响应都会回报真实的输入 token，拿它当基准、只估算它之后新增的部分，
/// 比整段估算准一个量级。这与 pi 的 <c>estimateContextTokens</c> 同一思路
/// （<c>compaction.ts:196</c>）。
///
/// <para>
/// <b>锚点会失效</b>，这是最容易写错的地方。三种情况必须作废：
/// <list type="bullet">
/// <item>压缩替换了历史——锚点测的是压缩前的旧历史。</item>
/// <item>历史被截短到锚点位置之前——锚点指向的消息已经不存在。</item>
/// <item>历史在锚点之前被修改——那不是"新增"能表达的。</item>
/// </list>
/// 失效后必须退回纯估算，否则会给出一个凭空的数。
/// </para>
/// </remarks>
/// <param name="InputTokens">provider 回报的输入 token（含缓存命中的部分）。</param>
/// <param name="MessageCount">测量那一刻已提交历史的消息条数。</param>
/// <param name="ContentFingerprint">那一刻历史的内容指纹，用于判断前缀是否被改写。</param>
internal sealed record UsageAnchor(int InputTokens, int MessageCount, string ContentFingerprint)
{
    /// <summary>用当前历史建立一个锚点。</summary>
    public static UsageAnchor Capture(int inputTokens, IReadOnlyList<AgentMessage> history) =>
        new(inputTokens, history.Count, ComputeFingerprint(history, history.Count));

    /// <summary>
    /// 锚点是否仍然描述 <paramref name="history"/> 的前缀。
    /// </summary>
    /// <remarks>
    /// 判据是"历史没变短，且前 <see cref="MessageCount"/> 条的指纹一致"。
    /// 不比整段——那会让每轮新增的消息把锚点判失效，锚点就永远用不上。
    /// </remarks>
    public bool StillValid(IReadOnlyList<AgentMessage> history) =>
        history.Count >= MessageCount
        && string.Equals(ComputeFingerprint(history, MessageCount), ContentFingerprint, StringComparison.Ordinal);

    /// <summary>取前 <paramref name="count"/> 条消息的指纹。</summary>
    /// <remarks>
    /// 用长度与内容哈希而不是完整文本：历史可能有几万条，完整比较是 O(文本总量)，
    /// 而这个判断每轮都要做一次。
    /// </remarks>
    private static string ComputeFingerprint(IReadOnlyList<AgentMessage> history, int count)
    {
        var hash = new HashCode();
        for (var i = 0; i < count; i++)
        {
            hash.Add(history[i].Role);
            hash.Add(history[i].Content.Length);
            hash.Add(history[i].Content.GetHashCode(StringComparison.Ordinal));
        }

        return hash.ToHashCode().ToString("X8");
    }
}
