using System.Text;
using System.Text.Json;

namespace Dougong.Agent;

/// <summary>
/// 在会话历史里按文本搜索，返回可定位的命中。
/// </summary>
/// <remarks>
/// <b>解决什么问题</b>：有了 <c>--list</c>，用户能看到"有哪些会话"，但看不到
/// "哪个会话里聊过我昨天那个 bug"。想找回来只能一个个 <c>--resume</c> 进去翻，
/// 而每个会话都可能很长。dsh 的 <c>session-query</c> 就是在补这一环。
///
/// <para>
/// <b>照 dsh 的 <c>session-query</c> 做，但后端不同</b>：dsh 用
/// <c>session-query-sqlite</c> 建全文索引（rankings、snippets、cursor generations）。
/// 我们的会话是 append-only JSONL，直接扫文件即可——**不需要索引**。
/// 建索引要维护一致性（新记录入库、删除、重建），而那层复杂度只有在
/// "会话数量到了扫描扛不住"时才值得付。目录里通常几十到几百个文件，
/// 每个都只有几十 KB，全扫是毫秒级。
/// </para>
///
/// <para>
/// <b>只搜文本字段，不搜工具参数与结果</b>：
/// <list type="bullet">
/// <item>工具结果里常有整文件内容，搜它会让"匹配"变成噪音——
/// 问"我提的那个函数名"却命中二十个读过同一个文件的结果。</item>
/// <item>工具参数同理：一次 <c>read_file</c> 会让文件名出现在每条记录里。</item>
/// </list>
/// 用户找的是"我们聊过什么"，不是"哪些工具碰过这个字符串"。
/// </para>
///
/// <para>
/// <b>命中要带会话标识与序号</b>：光说"这个会话匹配"没有用——用户需要
/// 能跳过去。序号让它能直接 <c>--resume &lt;id&gt; --at &lt;seq&gt;</c>。
/// </para>
/// </remarks>
public static class SessionSearch
{
    /// <summary>单个片段的最大字符数。</summary>
    /// <remarks>
    /// 片段的作用是"让用户判断要不要进去看"，不是"替代原文"。
    /// 超长片段在终端里折行成一团，反而看不出匹配点在哪。
    /// </remarks>
    public const int SnippetChars = 120;

    /// <summary>默认最多返回多少条命中。</summary>
    /// <remarks>
    /// 搜索是给人看的，两百条之后没人会往下翻；而截断必须明说，
    /// 否则用户以为"只有这么多"。
    /// </remarks>
    public const int DefaultLimit = 50;

    /// <summary>
    /// 在目录下所有会话里搜索 <paramref name="query"/>。
    /// </summary>
    /// <param name="sessionDirectory">会话目录。</param>
    /// <param name="query">要找的文本；大小写不敏感。</param>
    /// <param name="limit">最多返回多少条命中。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>命中列表（按会话时间倒序）与被扫过的会话数。</returns>
    public static async Task<SessionSearchResult> SearchAsync(
        string sessionDirectory,
        string query,
        int limit = DefaultLimit,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(query);

        if (limit <= 0) throw new ArgumentOutOfRangeException(nameof(limit));
        if (!Directory.Exists(sessionDirectory))
            return new SessionSearchResult([], 0, 0, Truncated: false);

        var hits = new List<SessionSearchHit>();
        var scanned = 0;
        var truncated = false;

        // 按最后写入倒序：用户找的多半是"最近那个会话"，
        // 先看到近的比先看到按文件名排序的有用。
        foreach (var summary in SessionBranch.List(sessionDirectory))
        {
            cancellationToken.ThrowIfCancellationRequested();

            // **被跳过的会话不计入 scanned**：让"扫了 N 个"与
            // "真的读了 N 个"一致，否则用户会以为搜索覆盖了更多。
            if (hits.Count >= limit)
            {
                truncated = true;
                break;
            }

            IReadOnlyList<SessionSearchHit> found;
            try
            {
                found = await SearchFileAsync(
                    summary.Path, summary.SessionId, query, limit - hits.Count,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 单个文件读不了就跳过，但**要在结果里体现**——
                // 静默跳过会让"没找到"与"没搜到"混成同一件事。
                continue;
            }

            scanned++;
            hits.AddRange(found);

            if (hits.Count >= limit)
            {
                truncated = true;
                break;
            }
        }

        return new SessionSearchResult(hits, scanned, hits.Count, truncated);
    }

    /// <summary>在一个会话文件里搜索，返回逐条命中。</summary>
    private static async Task<IReadOnlyList<SessionSearchHit>> SearchFileAsync(
        string path,
        string sessionId,
        string query,
        int remaining,
        CancellationToken cancellationToken)
    {
        var results = new List<SessionSearchHit>();

        await using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite,
            bufferSize: 4096, useAsync: true);
        using var reader = new StreamReader(
            stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false);

        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            if (results.Count >= remaining) break;

            // 统一的读取判据：格式行跳过、坏行停止。
            // **这里不能手写解析**——那条路已经在六处踩过坑。
            switch (SessionRecordReader.Read(line, out var record))
            {
                case SessionLineKind.End:
                case SessionLineKind.Stop:
                    return results;
                case SessionLineKind.Skip:
                    continue;
            }

            // Read 只在 Record 分支给出非 null，但编译器看不到这层关联。
            if (record is null) continue;

            if (ExtractSearchableText(record) is not { } text) continue;
            if (!text.Contains(query, StringComparison.OrdinalIgnoreCase)) continue;

            results.Add(new SessionSearchHit(
                sessionId,
                path,
                record.Sequence,
                DescribeRole(record.Kind),
                BuildSnippet(text, query)));
        }

        return results;
    }

    /// <summary>从一条记录里取出"该被搜索的文本"，不该搜的返回 null。</summary>
    /// <remarks>
    /// <b>只覆盖用户输入与模型回复</b>。工具记录里有参数与结果，
    /// 搜它们会让命中变成噪音——见类型注释。
    /// <para>
    /// <b>两种形态都要认</b>：`UserInput` 的载荷是裸字符串，
    /// `ModelResponse` 早期是裸字符串、现在是 <c>{text, calls}</c> 对象。
    /// 只认一种会静默漏掉整类会话——而这个 bug 的症状恰好是
    /// "搜索说没找到"，很容易被当成"确实没有"。
    /// 判据与 <see cref="SessionRecovery"/> 的读取侧保持一致。
    /// </para>
    /// </remarks>
    private static string? ExtractSearchableText(SessionRecordLine record)
    {
        var isUser = record.Kind is nameof(StoreRecordKind.UserInput);
        var isModel = record.Kind is nameof(StoreRecordKind.ModelResponse);
        if (!isUser && !isModel) return null;

        return record.Payload.ValueKind switch
        {
            JsonValueKind.String => record.Payload.GetString(),

            JsonValueKind.Object when record.Payload.TryGetProperty("text", out var text)
                => text.GetString(),

            _ => null,
        };
    }

    /// <summary>把记录类型翻成用户看得懂的说话方。</summary>
    private static string DescribeRole(string kind) => kind switch
    {
        nameof(StoreRecordKind.UserInput) => "用户",
        nameof(StoreRecordKind.ModelResponse) => "模型",
        _ => kind,
    };

    /// <summary>
    /// 在匹配位置附近截一段，用 <c>…</c> 标出省略。
    /// </summary>
    /// <remarks>
    /// <b>片段必须包含匹配词本身</b>：只给开头 N 个字符会让用户看不出
    /// 为什么这一条被选中。
    /// </remarks>
    internal static string BuildSnippet(string text, string query)
    {
        // 把换行压成空格：片段要在一行里显示，多行会让终端输出错乱。
        var flat = text.Replace('\r', ' ').Replace('\n', ' ');

        var index = flat.IndexOf(query, StringComparison.OrdinalIgnoreCase);
        if (index < 0) index = 0;

        // 以匹配点为中心取窗口，让匹配词落在中间偏前。
        var half = Math.Max(0, (SnippetChars - query.Length) / 2);
        var start = Math.Max(0, index - half);
        var length = Math.Min(SnippetChars, flat.Length - start);

        var slice = flat.Substring(start, length);

        var prefix = start > 0 ? "…" : string.Empty;
        var suffix = start + length < flat.Length ? "…" : string.Empty;

        return prefix + slice + suffix;
    }
}

/// <summary>一条搜索命中。</summary>
/// <param name="SessionId">会话标识，可直接传给 <c>--resume</c>。</param>
/// <param name="Path">会话文件路径。</param>
/// <param name="Sequence">命中的记录序号，可传给 <c>--at</c>。</param>
/// <param name="Speaker">说话方（用户 / 模型）。</param>
/// <param name="Snippet">匹配点附近的片段。</param>
public sealed record SessionSearchHit(
    string SessionId,
    string Path,
    long Sequence,
    string Speaker,
    string Snippet);

/// <summary>一次搜索的结果摘要。</summary>
/// <param name="Hits">命中的记录。</param>
/// <param name="SessionsScanned">真正读过的会话数（读不了的未计入）。</param>
/// <param name="HitCount">命中条数。</param>
/// <param name="Truncated">是否因为达到上限而停止。</param>
public sealed record SessionSearchResult(
    IReadOnlyList<SessionSearchHit> Hits,
    int SessionsScanned,
    int HitCount,
    bool Truncated);
