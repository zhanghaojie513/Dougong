using System.Text;
using System.Text.Json;

namespace Dougong.Agent;

/// <summary>
/// 会话即树：一次分支 = 从某个已提交点复制出新文件。
/// </summary>
/// <remarks>
/// 为什么不能简单复制整份文件：分支点的意义是"从此处重来"，
/// 分叉点之后的记录必须被丢掉，否则新会话会带着旧决定继续跑。
/// <para>
/// 分支不修改源会话文件一个字节：源会话可能仍被另一个进程持写租约，
/// 而且"分支"不该让原会话变得不可解释。
/// </para>
/// </remarks>
public static class SessionBranch
{
    /// <summary>
    /// 从 <paramref name="sourcePath"/> 的 <paramref name="atSequence"/> 处复制出新会话。
    /// </summary>
    /// <param name="sourcePath">源会话文件路径。</param>
    /// <param name="targetPath">新会话文件路径；必须与源不同。</param>
    /// <param name="newSessionId">新会话的标识。</param>
    /// <param name="atSequence">
    /// 保留到哪一条（含）。传 null 表示保留全部已提交记录，只换一个新身份。
    /// </param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>新会话文件的路径与它的会话标识。</returns>
    /// <exception cref="InvalidOperationException">
    /// 指定序号不存在，或该点之后还有记录但调用方意图不明（例如序号落在半条记录里）。
    /// </exception>
    public static async Task<BranchResult> CreateAsync(
        string sourcePath,
        string targetPath,
        string newSessionId,
        long? atSequence = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(newSessionId);
        if (string.Equals(
                Path.GetFullPath(sourcePath), Path.GetFullPath(targetPath),
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("分支目标不能与源会话相同。");

        // 源会话以只读方式打开：分支不该要求源会话先被关闭或接管写租约。
        await using var source = new FileStream(
            sourcePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite,
            bufferSize: 4096, useAsync: true);

        var kept = new List<string>();
        long lastSequence = 0;
        var reachedEnd = false;

        // 分支点不能停在工具交互中间：把 assistant 的 tool_calls 留下、
        // 却把对应的 tool 结果丢掉，会得到一条悬空调用，provider 会直接拒掉续跑
        // （实测 400 "must be followed by tool messages"）。
        // 处理方式是**向后补齐**到配对完成的位置，而不是回退：
        // 用户选的分叉点代表"保留到这里为止的上下文"，回退会悄悄少给一段。
        var pendingCalls = new HashSet<string>(StringComparer.Ordinal);

        using var reader = new StreamReader(
            source, Encoding.UTF8, detectEncodingFromByteOrderMarks: false,
            bufferSize: 4096, leaveOpen: true);

        // 版本行要原样保留（分叉后的新文件仍需声明格式），
        // 但它不是记录、不参与序号与配对计算。
        string? formatLine = null;
        var reachedFileEnd = false;

        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            // 格式行的识别在 SessionRecordReader 里统一做：Read 返回 Skip 时，
            // 若这一行是格式行就记下来（要带到新文件），否则只是空行。
            switch (SessionRecordReader.Read(line, out var parsed))
            {
                case SessionLineKind.End:
                case SessionLineKind.Stop:
                    reachedFileEnd = true;
                    break;
                case SessionLineKind.Skip:
                    if (SessionRecordReader.IsFormatLine(line)) formatLine ??= line;
                    continue;
            }

            if (reachedFileEnd) break;

            long sequence = parsed!.Sequence;
            TrackPendingCalls(parsed.Root, pendingCalls);

            kept.Add(line);
            lastSequence = sequence;

            if (atSequence is { } limit && sequence >= limit)
            {
                reachedEnd = true;

                // 补齐：继续往后读，直到所有已声明的调用都有结果。
                // 补齐只为了消除悬空，不会越过下一个"新的用户输入"——
                // 那已经属于后续对话，不该被并进来。
                while (pendingCalls.Count > 0
                    && await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } extra)
                {
                    // 补齐循环里也用同一个读取器：格式行、空行、坏行的判据三处必须一致。
                    switch (SessionRecordReader.Read(extra, out var extraParsed))
                    {
                        case SessionLineKind.End:
                        case SessionLineKind.Stop:
                            break;   // 无法继续补齐
                        case SessionLineKind.Skip:
                            continue;
                    }

                    if (extraParsed!.Kind is nameof(StoreRecordKind.UserInput))
                    {
                        throw new InvalidOperationException(
                            "分支点处的工具调用在下一个用户输入之前没有结果；"
                            + "该分支会留下悬空的 tool_calls，provider 无法接受。");
                    }

                    TrackPendingCalls(extraParsed.Root, pendingCalls);
                    kept.Add(extra);
                    lastSequence = extraParsed.Sequence;
                }

                break;
            }
        }

        if (atSequence is { } requested && !reachedEnd && lastSequence < requested)
            throw new InvalidOperationException(
                $"分支点 #{requested} 不存在；源会话最后一条已提交记录是 #{lastSequence}。");

        if (pendingCalls.Count > 0)
            throw new InvalidOperationException(
                "会话末尾存在没有结果的工具调用；该分支无法续跑，请先对账。");

        var directory = Path.GetDirectoryName(Path.GetFullPath(targetPath));
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        // FileShare.None：新会话立刻取得自己的写租约，避免两个进程写同一个分支。
        await using (var target = new FileStream(
            targetPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            bufferSize: 4096, useAsync: true))
        {
            // **先写版本行**：分支是新会话，必须自带格式声明。
            // 源文件有版本行就继承它；源是没有版本行的旧文件时按当前版本写。
            //
            // 漏了这一步的后果：分支文件变成"无版本行"，而读取端把那种文件
            // 按**当前**版本处理——于是格式升级后，这些分支会被按一个
            // 它们并不符合的格式解析，而且不会报错。
            // 构造格式行统一走 SessionRecordReader.FormatLine：写入侧的
            // 拼法也必须只有一处，否则改前缀时又会漏。
            var headerBytes = Encoding.UTF8.GetBytes(SessionRecordReader.FormatLine(formatLine));
            await target.WriteAsync(headerBytes, cancellationToken).ConfigureAwait(false);

            foreach (var line in kept)
            {
                var bytes = Encoding.UTF8.GetBytes(line + "\n");
                await target.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
            }

            await target.FlushAsync(cancellationToken).ConfigureAwait(false);
            target.Flush(flushToDisk: true);
        }

        return new BranchResult(targetPath, newSessionId, lastSequence, kept.Count);
    }

    /// <summary>
    /// 跟踪工具交互配对：ModelResponse 里声明的调用记为待完成，
    /// 对应的 ToolCallCompleted 把它销账。集合非空即表示"有调用无结果"。
    /// </summary>
    private static void TrackPendingCalls(JsonElement record, HashSet<string> pending)
    {
        if (!record.TryGetProperty("Kind", out var kindElement)) return;
        if (!record.TryGetProperty("Payload", out var payload)) return;

        var kind = kindElement.GetString();
        if (kind is nameof(StoreRecordKind.ModelResponse))
        {
            if (payload.ValueKind is not JsonValueKind.Object) return;
            if (!payload.TryGetProperty("calls", out var calls)
                || calls.ValueKind is not JsonValueKind.Array) return;

            foreach (var call in calls.EnumerateArray())
            {
                if (call.TryGetProperty("id", out var id) && id.GetString() is { Length: > 0 } value)
                    pending.Add(value);
            }
        }
        else if (kind is nameof(StoreRecordKind.ToolCallCompleted))
        {
            if (payload.ValueKind is not JsonValueKind.Object) return;
            if (payload.TryGetProperty("id", out var id) && id.GetString() is { Length: > 0 } value)
                pending.Remove(value);
        }
    }

    /// <summary>
    /// 列出目录下的会话，按最后写入时间倒序。
    /// </summary>
    /// <remarks>
    /// 这里只报文件与基本统计，不解析完整历史：列表要为"选一个会话"服务，
    /// 解析全部记录会让列一个大目录变得很慢，而且没必要。
    /// 每条会话的摘要仍以打开后的重放结果为准。
    /// </remarks>
    public static IReadOnlyList<SessionSummary> List(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        if (!Directory.Exists(directory)) return [];

        var results = new List<SessionSummary>();
        foreach (var file in Directory.EnumerateFiles(directory, "*.jsonl"))
        {
            FileInfo info;
            try
            {
                info = new FileInfo(file);
                if (!info.Exists) continue;
            }
            catch (IOException)
            {
                continue;
            }

            var (firstSequence, lastSequence, records, hasGap, unreadable) = Scan(file);
            results.Add(new SessionSummary(
                file,
                Path.GetFileNameWithoutExtension(file),
                info.LastWriteTimeUtc,
                info.Length,
                records,
                firstSequence,
                lastSequence,
                hasGap)
            {
                IsUnreadable = unreadable,
            });
        }

        return [.. results.OrderByDescending(r => r.LastWriteUtc)];
    }

    private static (long First, long Last, int Records, bool HasGap, bool Unreadable) Scan(string path)
    {
        long first = 0, last = 0, expected = 1;
        var records = 0;
        var gap = false;

        try
        {
            using var stream = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream, Encoding.UTF8);

            while (reader.ReadLine() is { } line)
            {
                // 格式行的识别统一在 SessionRecordReader 里：此前这段手写在六处。
                switch (SessionRecordReader.Read(line, out var parsed))
                {
                    case SessionLineKind.End:
                    case SessionLineKind.Stop:
                        return (first, last, records, gap, false);   // 坏行或结束：停止统计
                    case SessionLineKind.Skip:
                        continue;
                }

                var sequence = parsed!.Sequence;
                if (records == 0) first = sequence;
                if (sequence != expected) gap = true;
                expected = sequence + 1;
                last = sequence;
                records++;
            }
        }
        catch (IOException)
        {
            // **读不到不等于有缺口**。写者持有 FileShare.None 的写租约，
            // 所以"这个会话正在被使用"与"日志真被破坏"在这里看起来一样。
            // 分开之后调用方才能说清是哪一种——两者的处置完全不同：
            // 前者等一下再看，后者要恢复数据。
            //
            // 第一版把两者都报成 (0,0,0,true)，于是正在使用的会话在
            // --list 里显示"序号缺口（不可续跑）"，用户会以为数据坏了。
            return (0, 0, 0, false, true);
        }

        return (first, last, records, gap, false);
    }
}

/// <summary>一次分支的结果。</summary>
public sealed record BranchResult(
    string Path,
    string SessionId,
    long LastSequence,
    int RecordCount);

/// <summary>会话列表中的一项。只描述文件与序号范围，不含消息内容。</summary>
public sealed record SessionSummary(
    string Path,
    string SessionId,
    DateTime LastWriteUtc,
    long SizeBytes,
    int RecordCount,
    long FirstSequence,
    long LastSequence,
    bool HasSequenceGap)
{
    /// <summary>
    /// 这个会话当前读不到（通常是被另一个进程持有写租约）。
    /// </summary>
    /// <remarks>
    /// <b>与 <see cref="HasSequenceGap"/> 是两件事</b>：那个是"日志真有问题"
    /// （序号不连续，说明被外部改动过），这个只是"现在看不了"。
    /// 混成一个时，正在使用的会话会被报成"序号缺口（不可续跑）"——
    /// 用户会以为数据坏了。
    /// <para>
    /// 用 <c>init</c> 属性追加而不是构造参数：后者改变公开签名，
    /// ApiCompat 会报 CP0002。
    /// </para>
    /// </remarks>
    public bool IsUnreadable { get; init; }
}
