using System.Text;
using System.Text.Json;

namespace Dougong.Agent;

/// <summary>
/// 会话状态投影：把 append-only 日志折叠成宿主可直接读的状态。
/// </summary>
/// <remarks>
/// <b>解决什么问题</b>：会话日志是事件流，而界面要的是"现在长什么样"。
/// 此前每个宿主自己扫一遍 JSONL 再算——CLI 算标题、算改动清单、算用量，
/// 桌面端算轮次与预览。同一份日志被反复解析，而两边算出来的东西
/// 迟早会不一致（一边改了判据、另一边没改）。
///
/// <para>
/// <b>照 dsh 的 <c>dsh-session-projection</c>，核心契约一致</b>：
/// <list type="bullet">
/// <item><b>纯同步 fold</b>：<c>上一状态 + 一条事件 → 下一状态</c>。
/// 同步是硬要求——异步单元会让"同一个游标读到的状态"不确定，
/// 而宿主要用它做增量推送。</item>
/// <item><b>无关事件必须返回同一个引用</b>：框架据此跳过下游工作。
/// 每次都 new 一个等值对象仍然正确，但会让"状态没变"变成"状态变了"，
/// 于是每一次工具输出都触发一次全量重绘。</item>
/// <item><b>只读已提交记录</b>：投影不参与屏障，也不产生副作用。
/// 它是一面镜子，不是参与者。</item>
/// </list>
/// </para>
///
/// <para>
/// <b>与 dsh 的差别</b>：它有一套按 scope 分层的注册表与若干载体
/// （tail-page 种子、`session/projection` 推送帧、projcache）。
/// 我们只需要"从日志算出状态"这一层——推送形态由宿主决定
/// （CLI 打印、桌面端走自己的 IPC）。所以这里是具体实现而不是注册表：
/// 等真的出现"两个同类型投影并存"再抽。
/// </para>
/// </remarks>
public interface ISessionProjection<TState>
    where TState : class
{
    /// <summary>空日志时的初始状态。</summary>
    /// <param name="sessionId">会话标识。</param>
    TState CreateInitial(string sessionId);

    /// <summary>
    /// 折叠一条已提交记录。
    /// </summary>
    /// <remarks>
    /// <b>无关的记录必须原样返回 <paramref name="state"/> 这个引用</b>。
    /// 返回一个等值的新对象在语义上没错，但会让"这段日志不改变投影"
    /// 这件事无法被下游观察到。
    /// </remarks>
    /// <param name="state">覆盖此前全部记录的状态。</param>
    /// <param name="record">下一条已提交记录。</param>
    TState Apply(TState state, StoreRecord record);
}

/// <summary>
/// 跑一遍投影：从日志起点折叠到当前。
/// </summary>
/// <remarks>
/// <b>每次都从头折叠，不做增量缓存</b>：缓存要回答"这份缓存对应哪个游标"、
/// "日志被外部改动过怎么办"。而一次会话的日志是几百到几千条记录，
/// 折叠是纯内存的，毫秒级。等真的出现慢的例子再加增量
/// （那时才知道瓶颈在哪个字段）。
/// </remarks>
public static class SessionProjector
{
    /// <summary>折叠整个日志。</summary>
    /// <param name="projection">投影定义。</param>
    /// <param name="sessionLogPath">会话文件路径。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public static async Task<TState> ProjectAsync<TState>(
        ISessionProjection<TState> projection,
        string sessionLogPath,
        CancellationToken cancellationToken = default)
        where TState : class
    {
        ArgumentNullException.ThrowIfNull(projection);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionLogPath);

        var state = projection.CreateInitial(Path.GetFileNameWithoutExtension(sessionLogPath));

        try
        {
            await using var stream = new FileStream(
                sessionLogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite,
                bufferSize: 4096, useAsync: true);
            using var reader = new StreamReader(
                stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false);

            while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
            {
                // 统一读取判据：格式行跳过、坏行停止。与其它读取方同一份逻辑。
                switch (SessionRecordReader.Read(line, out var parsed))
                {
                    case SessionLineKind.End:
                    case SessionLineKind.Stop:
                        return state;
                    case SessionLineKind.Skip:
                        continue;
                }

                if (!Enum.TryParse<StoreRecordKind>(parsed!.Kind, out var kind))
                    continue;   // 不认识的记录类型：跳过，不影响已有状态

                state = projection.Apply(
                    state, new StoreRecord(parsed.Sequence, kind, parsed.Payload));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 读不到就返回已折叠的部分：投影是展示用的，不该因为它让会话打不开。
            return state;
        }

        return state;
    }
}

/// <summary>轮次大纲里的一项。</summary>
/// <param name="Sequence">该轮起点（用户输入）的记录序号；可用来 <c>--resume</c> 跳转。</param>
/// <param name="Turn">第几轮（从 1 开始）。</param>
/// <param name="Prompt">
/// 用户输入的预览：单行、最多 <see cref="TurnOutlineProjection.PromptMaxChars"/> 字符。
/// </param>
/// <param name="Response">
/// 模型回答的预览：最多 <see cref="TurnOutlineProjection.ResponseMaxLines"/> 行，
/// 每行最多 <see cref="TurnOutlineProjection.ResponseLineMaxChars"/> 字符。
/// </param>
public sealed record TurnOutlineEntry(
    long Sequence,
    int Turn,
    string Prompt,
    IReadOnlyList<string> Response);

/// <summary>轮次大纲投影的状态。</summary>
/// <remarks>
/// <b>不可变</b>：每次变化返回新实例。这样"状态没变"可以靠引用相等判断，
/// 而宿主可以安全地持有旧状态做对比。
/// </remarks>
public sealed class TurnOutlineState
{
    /// <summary>构造。</summary>
    /// <param name="sessionId">会话标识。</param>
    /// <param name="entries">已完成的轮次。</param>
    /// <param name="draft">当前轮的草稿；null 表示不在轮内。</param>
    public TurnOutlineState(
        string sessionId,
        IReadOnlyList<TurnOutlineEntry> entries,
        TurnOutlineDraft? draft = null)
    {
        SessionId = sessionId;
        Entries = entries;
        Draft = draft;
    }

    /// <summary>会话标识。</summary>
    public string SessionId { get; }

    /// <summary>已收尾的轮次，按发生顺序。</summary>
    public IReadOnlyList<TurnOutlineEntry> Entries { get; }

    /// <summary>
    /// 进行中的轮次；null 表示当前没有开着的轮。
    /// </summary>
    /// <remarks>
    /// <b>草稿与已完成项分开</b>（照 dsh）：草稿只在轮结束那一刻提交。
    /// 合在一起的话，一轮之内每次文本增量都会产生一个新的"已完成项"，
    /// 而宿主会把它当成一条新轮次显示出来。
    /// </remarks>
    public TurnOutlineDraft? Draft { get; }
}

/// <summary>进行中的一轮。</summary>
/// <param name="Sequence">该轮的起点序号。</param>
/// <param name="Turn">轮次编号（它的位置由已完成项数决定）。</param>
/// <param name="Prompt">用户输入预览。</param>
/// <param name="ResponseDraft">这一轮目前收到的模型文本。</param>
public sealed record TurnOutlineDraft(
    long Sequence,
    int Turn,
    string Prompt,
    string ResponseDraft);

/// <summary>
/// 从会话日志折叠出"每一轮说了什么"。
/// </summary>
/// <remarks>
/// <b>回答的是"跳到哪一轮"这个问题</b>：用户想回到"上次那个问迁移的地方"，
/// 而会话文件是几千行 JSONL。给一份大纲（轮次 + 用户问了什么 + 模型答了什么），
/// 就能按序号跳过去。
///
/// <para>
/// <b>预算照 dsh 的轨道卡片</b>：用户输入 50 字符一行；回答最多 3 行、
/// 每行 120 字符。这样一轮在两行内可读，而大纲不会因为某个长回答而失去形状。
/// </para>
///
/// <para>
/// <b>切轮判据是"用户输入"，不是"模型响应"</b>：模型可能在一轮里调十次工具，
/// 按响应切会得到十个假轮次。
/// </para>
///
/// <para>
/// <b>草稿在轮结束时才提交</b>：模型一次回答是多条增量拼起来的，
/// 而日志里只有最终那一条 <see cref="StoreRecordKind.ModelResponse"/>——
/// 所以这里不需要处理增量，但要处理"轮没结束"的中间态。
/// </para>
/// </remarks>
public sealed class TurnOutlineProjection : ISessionProjection<TurnOutlineState>
{
    /// <summary>用户输入预览的最大字符数。</summary>
    public const int PromptMaxChars = 50;

    /// <summary>回答预览的最大行数。</summary>
    public const int ResponseMaxLines = 3;

    /// <summary>回答每行的最大字符数。</summary>
    public const int ResponseLineMaxChars = 120;

    public TurnOutlineState CreateInitial(string sessionId) =>
        new(sessionId, []);

    public TurnOutlineState Apply(TurnOutlineState state, StoreRecord record)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(record);

        switch (record.Kind)
        {
            case StoreRecordKind.UserInput:
            {
                var prompt = Clamp(ReadUserInput(record.Payload), PromptMaxChars);

                // 上一轮如果没等到 ModelResponse 就开了新轮（用户打断、
                // 或上一轮失败），把它按空回答收尾——**不能丢**，
                // 否则大纲会少一轮，而序号会跳。
                var entries = state.Draft is { } open
                    ? [.. state.Entries, Commit(open)]
                    : state.Entries;

                return new TurnOutlineState(
                    state.SessionId,
                    entries,
                    new TurnOutlineDraft(
                        record.Sequence, entries.Count + 1, prompt, string.Empty));
            }

            case StoreRecordKind.ModelResponse when state.Draft is { } draft:
            {
                var text = ReadModelText(record.Payload);
                if (text.Length == 0) return state;   // 空回答不改变草稿

                return new TurnOutlineState(
                    state.SessionId,
                    state.Entries,
                    draft with { ResponseDraft = text });
            }

            case StoreRecordKind.RunFinished when state.Draft is { } finished:
                // 轮结束：提交草稿。
                return new TurnOutlineState(
                    state.SessionId,
                    [.. state.Entries, Commit(finished)],
                    draft: null);

            default:
                // **无关记录原样返回同一个引用**：框架与宿主据此判断"没变"。
                return state;
        }
    }

    private static TurnOutlineEntry Commit(TurnOutlineDraft draft) =>
        new(draft.Sequence, draft.Turn, draft.Prompt, WrapResponse(draft.ResponseDraft));

    /// <summary>把回答折成最多 N 行、每行最多 M 字符的预览。</summary>
    /// <remarks>
    /// <b>先按段落折行，再截断</b>：直接把整段切成固定宽度会把一个词的中间切断，
    /// 而大纲要的是"大致说了什么"，可读性优先于塞进更多字。
    /// </remarks>
    private static IReadOnlyList<string> WrapResponse(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return [];

        var lines = new List<string>(ResponseMaxLines);

        foreach (var raw in text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;

            lines.Add(Clamp(line, ResponseLineMaxChars));
            if (lines.Count >= ResponseMaxLines) break;
        }

        return lines;
    }

    /// <summary>截断到 <paramref name="max"/> 字符，省略号计入上限。</summary>
    /// <remarks>
    /// <b>省略号要占位</b>：这是本阶段反复踩到的一处——上限 + 省略号
    /// 等于超出一个字符，而那个数字是被声明的宽度。
    /// </remarks>
    private static string Clamp(string text, int max)
    {
        var single = text.Replace("\r", " ").Replace('\n', ' ').Trim();
        if (max <= 0 || single.Length <= max) return single;
        return max == 1 ? "…" : single[..(max - 1)] + "…";
    }

    /// <summary>读用户输入；两种历史载荷形态都认。</summary>
    private static string ReadUserInput(JsonElement payload) =>
        payload.ValueKind is JsonValueKind.String
            ? payload.GetString() ?? string.Empty
            : payload.TryGetProperty("text", out var text)
                ? text.GetString() ?? string.Empty
                : string.Empty;

    /// <summary>读模型正文；忽略工具调用与推理。</summary>
    /// <remarks>
    /// <b>只要正文</b>：推理是模型的草稿（"等等，这里算错了"），
    /// 放进大纲会让用户以为那就是回答；工具调用没有可读内容。
    /// </remarks>
    private static string ReadModelText(JsonElement payload)
    {
        if (payload.ValueKind is JsonValueKind.String)
            return payload.GetString() ?? string.Empty;

        return payload.ValueKind is JsonValueKind.Object
            && payload.TryGetProperty("text", out var text)
            && text.ValueKind is JsonValueKind.String
                ? text.GetString() ?? string.Empty
                : string.Empty;
    }
}
