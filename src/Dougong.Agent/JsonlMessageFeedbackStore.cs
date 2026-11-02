using System.Text;
using System.Text.Json;

namespace Dougong.Agent;

/// <summary>
/// 把人类评价存成会话文件旁的一个 append-only JSONL。
/// </summary>
/// <remarks>
/// <b>为什么是旁文件，不是会话日志里的记录类型</b>：
/// <list type="number">
/// <item><b>加记录类型会让旧程序读不了整个会话。</b><see cref="JsonlAgentStore"/>
/// 读记录时用 <c>Enum.Parse&lt;StoreRecordKind&gt;</c>，遇到不认识的名字直接抛
/// <see cref="ArgumentException"/>。不是跳过那一条，是**整个会话打不开**。
/// 这与 D-15（不把标题写进日志）同一个判据，而后果更严重。</item>
/// <item><b>评价是"关于对话的元数据"，不是对话。</b>会话恢复、分支复制、
/// 上下文压缩都不该因为它而变化；放进日志就得在每一处读取逻辑里多判一次。</item>
/// <item><b>不进模型上下文这件事因此是结构保证的</b>，而不是"记得别读它"。</item>
/// </list>
///
/// <para>
/// <b>与 dsh 的差别要说清</b>：dsh 把评价写进权威日志
/// （<c>feedback/message-put</c>），它有一套事件词汇、投影与版本目录来支撑这件事。
/// 我们加记录类型的代价是一整类读端崩溃，收益只是"与对话在同一文件里"——
/// 那正是 dsh 的归档笔记里评估过、并因为他们那时**还没有**这套支撑而放弃的方案。
/// 取舍记在 <c>porting-differences.md</c> D-24。
/// </para>
///
/// <para>
/// <b>为什么也是 append-only</b>：与主日志同一个理由——修改要留下痕迹。
/// 删除是墓碑，早期评分留在文件里。这让"用户改过主意"这件事实可见，
/// 而原地覆盖会让它彻底消失。
/// </para>
///
/// <para>
/// <b>并发模型是"单进程单写者"</b>：与 <see cref="JsonlAgentStore"/> 相同，
/// 由调用方保证同一会话只有一个写入者。跨进程用文件锁，
/// 多进程同时写会让版本校验失去意义（两边都读到旧版本，都认为自己是第一次改）。
/// </para>
/// </remarks>
public sealed class JsonlMessageFeedbackStore : IMessageFeedbackStore, IDisposable
{
    /// <summary>伴生文件的后缀。</summary>
    /// <remarks>
    /// <b>刻意不是 <c>.jsonl</c> 结尾</b>：会话目录按 <c>*.jsonl</c> 枚举
    /// （<see cref="SessionBranch.List"/>、诊断包），若不改后缀，
    /// 评价文件会被当成一个会话列出来——点开是"格式版本无效"。
    /// <para>
    /// 取名规则集中在 <see cref="PathFor"/> 一处：调用方若自己拼一遍，
    /// 改后缀时漏改一处不报错，症状就是那个假会话。
    /// </para>
    /// </remarks>
    public const string Suffix = ".feedback";

    /// <summary>默认备注上限（UTF-8 字节）。</summary>
    public const int DefaultMaxNoteBytes = MessageFeedback.DefaultMaxNoteBytes;

    private readonly string _path;
    private readonly int _maxNoteBytes;
    private readonly SemaphoreSlim _lock = new(1, 1);

    /// <summary>构造。</summary>
    /// <param name="path">伴生文件路径；由 <see cref="PathFor"/> 算出。</param>
    /// <param name="maxNoteBytes">单条备注的 UTF-8 字节上限。</param>
    public JsonlMessageFeedbackStore(string path, int maxNoteBytes = DefaultMaxNoteBytes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (maxNoteBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxNoteBytes), maxNoteBytes, "上限必须为正");

        _path = Path.GetFullPath(path);
        _maxNoteBytes = maxNoteBytes;
    }

    /// <summary>由会话文件路径算出伴生文件路径。</summary>
    /// <remarks>
    /// <b>规则只有这一份</b>：写、读、以及宿主自己找文件都用它。
    /// </remarks>
    /// <param name="sessionLogPath">会话文件路径，例如 <c>~/.dougong/sessions/x/1_abc.jsonl</c>。</param>
    public static string PathFor(string sessionLogPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionLogPath);
        var full = Path.GetFullPath(sessionLogPath);
        return full + Suffix;
    }

    /// <summary>伴生文件路径。</summary>
    public string FilePath => _path;

    public async Task<MessageFeedbackList> ListAsync(CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var (state, truncated) = await ReadStateAsync(cancellationToken).ConfigureAwait(false);
            return new MessageFeedbackList(state.Values.ToList(), truncated);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<FeedbackPutResult> PutAsync(
        MessageFeedbackPut request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.RecordSequence <= 0)
            throw new ArgumentOutOfRangeException(
                nameof(request), request.RecordSequence, "记录序号必须为正");

        // 备注校验在锁外：它与文件状态无关，先做能少占一次锁。
        var note = NormalizeNote(request.Note);
        if (note.Rejected is { } rejected) return rejected;

        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var (state, _) = await ReadStateAsync(cancellationToken).ConfigureAwait(false);

            state.TryGetValue(request.RecordSequence, out var current);
            var currentVersion = current?.Version;

            // **严格比较，no-op 也算冲突**：值一样不代表调用方看到的是当前版本。
            // 放过 no-op 会让一个陈旧的、以为自己是首次提交的请求静默成功。
            if (!string.Equals(currentVersion, request.IfVersion, StringComparison.Ordinal))
                return new FeedbackPutResult.VersionConflict(current);

            // 值与已存值完全相同：保留版本与时间戳，不追加事件。
            if (current is not null
                && current.Rating == request.Rating
                && string.Equals(current.Note, note.Value, StringComparison.Ordinal)
                && current.Category == request.Category)
            {
                return new FeedbackPutResult.Succeeded(current);
            }

            var now = DateTimeOffset.UtcNow;
            var item = new MessageFeedbackItem(
                request.RecordSequence,
                request.Rating,
                note.Value,
                request.Category,
                NewVersion(),
                current?.CreatedAt ?? now,
                now);

            await AppendAsync(item, deleted: false, cancellationToken).ConfigureAwait(false);
            return new FeedbackPutResult.Succeeded(item);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<FeedbackDeleteResult> DeleteAsync(
        MessageFeedbackDelete request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.RecordSequence <= 0)
            throw new ArgumentOutOfRangeException(
                nameof(request), request.RecordSequence, "记录序号必须为正");

        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var (state, _) = await ReadStateAsync(cancellationToken).ConfigureAwait(false);

            // 已经不存在：成功，不追加墓碑。删除的后置条件是"之后它不存在"，
            // 重试应当照样成功——这正是幂等删除的意义。
            if (!state.TryGetValue(request.RecordSequence, out var current))
                return new FeedbackDeleteResult.Succeeded();

            if (!string.Equals(current.Version, request.IfVersion, StringComparison.Ordinal))
                return new FeedbackDeleteResult.VersionConflict(current);

            await AppendAsync(current, deleted: true, cancellationToken).ConfigureAwait(false);
            return new FeedbackDeleteResult.Succeeded();
        }
        finally
        {
            _lock.Release();
        }
    }

    // ── 内部 ─────────────────────────────────────────────────

    /// <summary>备注规整：去首尾空白，判空，判字节上限。</summary>
    /// <remarks>
    /// <b>空白串是拒绝而不是"当成没有"</b>：调用方传了一个只含空格的说明，
    /// 说明它想表达点什么。静默丢掉会让它以为写进去了。
    /// </remarks>
    private (string? Value, FeedbackPutResult? Rejected) NormalizeNote(string? note)
    {
        if (note is null) return (null, null);

        var trimmed = note.Trim();
        if (trimmed.Length == 0) return (null, new FeedbackPutResult.NoteBlank());

        var bytes = Encoding.UTF8.GetByteCount(trimmed);
        if (bytes > _maxNoteBytes)
            return (null, new FeedbackPutResult.NoteTooLarge(_maxNoteBytes, bytes));

        return (trimmed, null);
    }

    /// <summary>读全文件，折叠出每个序号当前生效的条目。</summary>
    /// <remarks>
    /// <b>折叠顺序决定"当前"</b>：按文件顺序逐条应用，put 覆盖、delete 移除，
    /// 于是最后一条生效的写入就是当前值。
    /// <para>
    /// <b>尾部坏行不抛异常</b>：append-only 文件的坏行只可能出现在末尾（写一半崩溃）。
    /// 停止读取并标记 <c>Truncated</c>，已读出的部分仍然有效——这与
    /// <see cref="SessionRecordReader"/> 的判据一致。但要**如实上报**，
    /// 否则用户会以为"只打过两条分"。中间坏行则是文件被外部改动，抛异常。
    /// </para>
    /// </remarks>
    private async Task<(Dictionary<long, MessageFeedbackItem> Items, bool Truncated)>
        ReadStateAsync(CancellationToken cancellationToken)
    {
        var items = new Dictionary<long, MessageFeedbackItem>();
        if (!File.Exists(_path)) return (items, false);

        await using var stream = new FileStream(
            _path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite,
            bufferSize: 4096, useAsync: true);
        using var reader = new StreamReader(
            stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false);

        var lineNumber = 0;
        var sawRecord = false;

        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            lineNumber++;
            if (string.IsNullOrWhiteSpace(line)) continue;

            if (MessageFeedbackLine.TryParse(line, out var parsed))
            {
                sawRecord = true;
                if (parsed!.Deleted) items.Remove(parsed.RecordSequence);
                else items[parsed.RecordSequence] = parsed.Item!;
                continue;
            }

            // 无法解析：文件末尾的半条记录算"写一半"，中间的算文件被改动。
            // 判据是"后面还有没有内容"——这里无法预知，所以再看能否继续读出一行。
            if (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } next)
            {
                throw new MessageFeedbackFormatException(
                    _path,
                    $"第 {lineNumber} 行无法解析，而它后面还有内容——"
                    + "这不是写一半留下的尾行，文件可能被改动过。");
            }

            return (items, Truncated: sawRecord || lineNumber > 1);
        }

        return (items, false);
    }

    /// <summary>追加一条写入或墓碑。</summary>
    private async Task AppendAsync(
        MessageFeedbackItem item, bool deleted, CancellationToken cancellationToken)
    {
        var line = MessageFeedbackLine.Serialize(item, deleted);
        var bytes = Encoding.UTF8.GetBytes(line + "\n");

        var directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        await using var stream = new FileStream(
            _path, FileMode.Append, FileAccess.Write, FileShare.Read,
            bufferSize: 4096, useAsync: true);
        await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);

        // 与主日志同一口径：把 OS 缓冲刷到设备，但不宣称断电安全
        // （见 JsonlAgentStore 的说明——那需要掉电测试条件）。
        stream.Flush(flushToDisk: true);
    }

    /// <summary>新版本令牌。</summary>
    /// <remarks>
    /// <b>随机而不是自增</b>：自增会让不同进程、不同分支产生同样的序号，
    /// 而版本只做相等比较——两个不同来源的同号版本会被误认为"一直是这一版"。
    /// </remarks>
    private static string NewVersion() => Guid.NewGuid().ToString("N");

    public void Dispose() => _lock.Dispose();
}

/// <summary>
/// 反馈文件里的一行。
/// </summary>
/// <remarks>
/// <b>解析与序列化放在一起</b>：两边字段名必须一致，分开写是典型的漂移点。
/// </remarks>
internal sealed class MessageFeedbackLine
{
    /// <summary>解析一行；无法解析时返回 false。</summary>
    public static bool TryParse(string line, out MessageFeedbackLine? parsed)
    {
        parsed = null;
        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (root.ValueKind is not JsonValueKind.Object) return false;

            if (!root.TryGetProperty("seq", out var seqElement)
                || seqElement.ValueKind is not JsonValueKind.Number
                || !seqElement.TryGetInt64(out var sequence))
                return false;

            var deleted = root.TryGetProperty("deleted", out var deletedElement)
                && deletedElement.ValueKind is JsonValueKind.True;

            if (deleted)
            {
                parsed = new MessageFeedbackLine(sequence, null, Deleted: true);
                return true;
            }

            var rating = MessageFeedbackVocabulary.ParseRating(ReadString(root, "rating"));
            var version = ReadString(root, "version");
            var createdAt = ReadTime(root, "createdAt");
            var updatedAt = ReadTime(root, "updatedAt");

            // 缺任何一个必需字段就不认这一行——半条记录比没有记录更危险，
            // 它会以一个"未知版本的条目"形式挤进状态里。
            if (rating is null || string.IsNullOrEmpty(version)
                || createdAt is null || updatedAt is null)
                return false;

            var category = MessageFeedbackVocabulary.ParseCategory(ReadString(root, "category"));

            parsed = new MessageFeedbackLine(
                sequence,
                new MessageFeedbackItem(
                    sequence, rating.Value, ReadString(root, "note"), category,
                    version, createdAt.Value, updatedAt.Value),
                Deleted: false);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>序列化一行。</summary>
    public static string Serialize(MessageFeedbackItem item, bool deleted)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteNumber("seq", item.RecordSequence);

            if (deleted)
            {
                writer.WriteBoolean("deleted", true);
            }
            else
            {
                writer.WriteString("rating", MessageFeedbackVocabulary.ToId(item.Rating));

                if (item.Note is { } note) writer.WriteString("note", note);
                if (item.Category is { } category)
                    writer.WriteString("category", MessageFeedbackVocabulary.ToId(category));

                writer.WriteString("version", item.Version);
                writer.WriteString("createdAt", item.CreatedAt);
                writer.WriteString("updatedAt", item.UpdatedAt);
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static string? ReadString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var element) && element.ValueKind is JsonValueKind.String
            ? element.GetString()
            : null;

    private static DateTimeOffset? ReadTime(JsonElement root, string name) =>
        root.TryGetProperty(name, out var element)
            && element.ValueKind is JsonValueKind.String
            && element.TryGetDateTimeOffset(out var value)
                ? value
                : null;

    private MessageFeedbackLine(long recordSequence, MessageFeedbackItem? item, bool Deleted)
    {
        RecordSequence = recordSequence;
        Item = item;
        this.Deleted = Deleted;
    }

    /// <summary>目标记录序号。</summary>
    public long RecordSequence { get; }

    /// <summary>写入的值；墓碑为 null。</summary>
    public MessageFeedbackItem? Item { get; }

    /// <summary>是否为墓碑。</summary>
    public bool Deleted { get; }
}
