using System.Text;

namespace Dougong.Agent;

/// <summary>
/// 从会话历史推导一个标题，供会话列表显示。
/// </summary>
/// <remarks>
/// <b>解决什么问题</b>：<c>--list</c> 现在只显示 <c>20261102-153012</c> 这样的时间戳。
/// 半个月后回来，没有任何线索能对上"我当时在干哪个"。dsh 的
/// <c>session-title</c> 就是在补这一环。
///
/// <para>
/// <b>照 dsh 的 <c>session-title</c> 做，但只用它的确定性兜底部分</b>。
/// dsh 的契约是三件事：<b>确定性兜底</b>（不调模型也能出标题）、
/// <b>最新标题折叠</b>（后写的覆盖先写的）、<b>可选异步提供者</b>
/// （挂上模型生成更好看的标题）。我们只做第一件。
/// </para>
///
/// <para>
/// <b>为什么不做 LLM 生成</b>：
/// <list type="number">
/// <item><b>它要求会话能调模型</b>——<c>--list</c> 的应用场景恰恰包括
/// "密钥过期了，我想先找回上次的会话"。给标题加这个前提等于把列表也废掉。</item>
/// <item><b>成本与延迟</b>：列 200 个会话要发 200 次请求，或者建缓存——
/// 而缓存要解决失效问题（会话被继续追加后标题该不该更新）。</item>
/// <item><b>确定性标题已经够用</b>：用户要的是"能认出这是哪个会话"，
/// 首条用户输入的前若干字通常就是最好的线索，因为那是他自己写的话。</item>
/// </list>
/// </para>
///
/// <para>
/// <b>为什么不把标题写进会话日志</b>：那是 dsh 的做法（log-backed），
/// 但对我们有一个具体代价——<c>JsonlAgentStore</c> 用
/// <c>Enum.Parse&lt;StoreRecordKind&gt;</c> 读 Kind，**旧程序遇到新增的
/// 记录类型会抛异常**而不是跳过。加一种记录等于让所有旧版本读不了新会话，
/// 而那需要真正的格式版本迁移，本项目明确不做（见 <c>compat-policy.md</c> §7.1）。
/// 从历史推导没有这个问题：它不写入任何新东西。
/// </para>
///
/// <para>
/// <b>因此标题是"算出来的"而不是"存下来的"</b>。代价是每次列会话都要重读
/// 首条用户输入——但那只是一次流式扫描的首行，比打开完整会话便宜得多。
/// </para>
/// </remarks>
public static class SessionTitle
{
    /// <summary>标题的最大字符数，**含省略号**。</summary>
    /// <remarks>
    /// 太短认不出，太长会把列表表格撑破。20 字在终端里大约是
    /// 一个中等宽度会话名列的容量，同时容得下一句完整的诉求。
    /// <para>
    /// <b>把省略号算在预算内</b>：列宽是固定的，正文写满 20 再追加"…"
    /// 会变成 21 字符，把后面那一列挤掉。所以正文最多 19 字符。
    /// </para>
    /// </remarks>
    public const int MaxChars = 20;

    /// <summary>没有可用用户输入时的占位。</summary>
    /// <remarks>
    /// <b>不返回空串</b>：列表里空着会让人以为"这个会话坏了"，
    /// 而它可能只是一个刚创建、还没说话的会话。
    /// </remarks>
    public const string Empty = "（无标题）";

    /// <summary>会话文件读不到时的占位。</summary>
    /// <remarks>
    /// <b>必须与 <see cref="Empty"/> 区分</b>：两者对用户的含义完全相反——
    /// "无标题"是"这个会话还没说话"（可以忽略），"读不到"是
    /// "这里有个会话但我打不开"（可能正是要找的那个，值得去查权限）。
    /// 合并成同一个占位会让用户把"打不开"当成"空会话"跳过。
    /// </remarks>
    public const string Unreadable = "（读不到）";

    /// <summary>
    /// 从首条用户输入推导标题。
    /// </summary>
    /// <param name="firstUserInput">首条用户输入；null 或空白表示没有。</param>
    /// <remarks>
    /// <b>取首条而不是最新一条</b>：会话的开头通常定义了它的主题，
    /// 后面可能飘到别的话题上。而"我为什么开这个会话"和"我记得它关于什么"
    /// 往往是同一个东西。
    /// </remarks>
    public static string FromUserInput(string? firstUserInput)
    {
        if (string.IsNullOrWhiteSpace(firstUserInput)) return Empty;

        // 先按"正文上限"规范化，再决定要不要省略号。
        // **分两步是必要的**：一步写完的情况下，"恰好写满"与"写满后被截断"
        // 在字符数上无法区分，而它们的显示意图完全相反——
        // 完整的一句话不该带"…"，那会让用户以为还有下文。
        var (text, exhausted) = Normalize(firstUserInput, MaxChars);

        if (exhausted) return text;

        // 超长了：正文退一位，给省略号腾位置，总长仍不超过 MaxChars。
        var (shortened, _) = Normalize(firstUserInput, MaxChars - 1);
        return shortened + "…";
    }

    /// <summary>
    /// 压掉首尾与连续空白，取前 <paramref name="limit"/> 个字符。
    /// </summary>
    /// <param name="input">原始输入。</param>
    /// <param name="limit">正文字符上限。</param>
    /// <returns>处理后的文本；以及输入是否被完整消费（true = 没有更多内容）。</returns>
    private static (string Text, bool Exhausted) Normalize(string input, int limit)
    {
        var builder = new StringBuilder(Math.Min(input.Length, limit + 8));
        var lastWasSpace = false;
        var exhausted = true;

        foreach (var ch in input)
        {
            var isSpace = char.IsWhiteSpace(ch);

            if (isSpace)
            {
                lastWasSpace = true;
                continue;
            }

            // 非空字符：若前面积了一个空格且放得下，先补上。
            if (lastWasSpace && builder.Length > 0)
            {
                if (builder.Length + 1 + 1 > limit) { exhausted = false; break; }
                builder.Append(' ');
            }

            lastWasSpace = false;

            if (builder.Length + 1 > limit) { exhausted = false; break; }
            builder.Append(ch);
        }

        return (builder.ToString(), exhausted);
    }

    /// <summary>
    /// 从恢复出来的历史里取首条用户输入，再推导标题。
    /// </summary>
    /// <param name="history">已恢复的历史；空表示没有用户输入。</param>
    public static string FromHistory(IReadOnlyList<AgentMessage> history)
    {
        ArgumentNullException.ThrowIfNull(history);

        foreach (var message in history)
        {
            if (message.Role is AgentRole.User)
                return FromUserInput(message.Content);
        }

        return Empty;
    }

    /// <summary>
    /// 直接从一个会话文件读首条用户输入并推导标题。
    /// </summary>
    /// <param name="sessionPath">会话文件路径。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <remarks>
    /// <b>读到首条用户输入就停</b>：列表场景要逐个扫几十上百个文件，
    /// 读完全部历史会让列会话变得可感地慢。这里只需要第一句。
    /// <para>
    /// 读失败返回 <see cref="Empty"/> 而不是抛：一个坏文件不该让整个列表失败。
    /// </para>
    /// </remarks>
    public static async Task<string> ReadAsync(
        string sessionPath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionPath);

        try
        {
            await using var stream = new FileStream(
                sessionPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite,
                bufferSize: 4096, useAsync: true);
            using var reader = new StreamReader(
                stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false);

            while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
            {
                // 统一读取判据：格式行跳过、坏行停止、空行跳过。
                // **不能手写解析**——那条路已经在六处踩过坑。
                switch (SessionRecordReader.Read(line, out var record))
                {
                    case SessionLineKind.End:
                    case SessionLineKind.Stop:
                        return Empty;
                    case SessionLineKind.Skip:
                        continue;
                }

                if (record is null) continue;
                if (record.Kind is not nameof(StoreRecordKind.UserInput)) continue;

                // UserInput 的载荷是裸字符串（见 SessionRecovery 的读取侧）。
                // 这里只认字符串形态：若将来改成对象，此处会返回 Empty 而
                // 不会静默出错——列表里显示"（无标题）"是可发现的那种失败。
                return record.Payload.ValueKind is System.Text.Json.JsonValueKind.String
                    ? FromUserInput(record.Payload.GetString())
                    : Empty;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 与"没有用户输入"分开：这条会话可能存在且重要，只是现在打不开。
            return Unreadable;
        }

        return Empty;
    }
}
