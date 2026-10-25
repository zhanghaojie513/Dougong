using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Dougong.Agent;

/// <summary>
/// L1 记忆的持久化：处理游标 + 产物状态。
/// </summary>
/// <remarks>
/// 与 <see cref="JsonlAgentStore"/> 同一套语义：append-only、单写者、返回即已提交。
/// 独立文件而不是塞进会话日志：记忆是**跨会话**的，混进某个会话文件后
/// 换会话就读不到，而 L1 的整个意义是"从历史任务里学"。
/// <para>
/// <b>幂等靠游标</b>：处理前先提交 <c>cursor</c> 记录，再提交产物。
/// 重启后从游标之后继续，因此中断不会重复处理同一条记录。
/// 若游标已提交但产物未提交，那条记录的记忆就丢了——**丢记忆比重复启用安全**。
/// </para>
/// <para>
/// <b>格式</b>：每行一条扁平 JSON，形如
/// <c>{"kind":"cursor","sequence":42}</c> 或
/// <c>{"kind":"artifact","artifactId":...}</c>。
/// **不要再套一层 payload 字符串**——那样读端的 <c>node["kind"]</c> 取不到值。
/// 首版实现犯过这个错：写进去能解析，读出来 0 条，四条验收同时失败才暴露。
/// </para>
/// </remarks>
public sealed class MemoryStore : IAsyncDisposable
{
    private readonly FileStream _stream;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private bool _disposed;

    private MemoryStore(FileStream stream)
    {
        _stream = stream;
    }

    /// <summary>已处理到的会话记录序号；0 表示尚未处理任何记录。</summary>
    public long Cursor { get; private set; }

    /// <summary>打开（或创建）记忆存储。</summary>
    /// <param name="path">记忆文件路径。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public static async Task<MemoryStore> OpenAsync(
        string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        FileStream stream;
        try
        {
            // FileShare.None 即单写者租约：第二个进程立刻失败，不交错写入。
            stream = new FileStream(
                fullPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None,
                bufferSize: 4096, useAsync: true);
        }
        catch (IOException ex)
        {
            throw new IOException($"记忆文件被占用，无法取得写租约：{fullPath}", ex);
        }

        var store = new MemoryStore(stream);
        try
        {
            await store.LoadCursorAsync(cancellationToken).ConfigureAwait(false);
            stream.Seek(0, SeekOrigin.End);
        }
        catch
        {
            await stream.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        return store;
    }

    /// <summary>
    /// 提交游标推进。**必须在处理完之后、产物提交之前调用**。
    /// </summary>
    /// <remarks>
    /// 顺序是刻意的：先提交游标再提交产物，重启后游标已过 → 不重复处理；
    /// 而产物若因中断没提交，那条记录的记忆就丢了——丢记忆比重复启用安全。
    /// 反过来（先产物后游标）会导致重启后重复处理并产生重复候选。
    /// </remarks>
    public async ValueTask AdvanceCursorAsync(
        long recordSequence, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (recordSequence <= Cursor) return;   // 幂等：不倒退、不重复

        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await WriteLineAsync(new JsonObject
            {
                ["kind"] = "cursor",
                ["sequence"] = recordSequence,
            }, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _lock.Release();
        }

        Cursor = recordSequence;
    }

    /// <summary>提交一条产物的当前状态（全量快照，不是增量）。</summary>
    /// <param name="artifact">要提交的产物。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <exception cref="InvalidOperationException">
    /// 提交的版本低于已存储的当前版本。这是**拒绝**而不是覆盖：
    /// 版本倒退几乎总是调用方算错了版本号，静默接受会让撤销被悄悄丢掉。
    /// </exception>
    public async ValueTask PutAsync(
        MemoryArtifact artifact, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        ObjectDisposedException.ThrowIf(_disposed, this);

        var payload = new JsonObject
        {
            ["kind"] = "artifact",
            ["artifactId"] = artifact.ArtifactId,
            ["version"] = artifact.Version,
            ["contentHash"] = artifact.ContentHash,
            ["key"] = artifact.Key,
            ["value"] = artifact.Value,
            ["scope"] = artifact.Scope.ToString(),
            ["workspace"] = artifact.Workspace,
            ["sourceTaskId"] = artifact.SourceTaskId,
            ["createdAt"] = artifact.CreatedAt,
            ["state"] = artifact.State.ToString(),
            ["expiresAt"] = artifact.ExpiresAt,
            ["approvedBy"] = artifact.ApprovedBy,
        };

        // 校验与写入必须在同一个锁里：分开加锁会让两个并发 Put 都通过校验，
        // 后写者仍然可能回退版本。
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // 版本必须单调递增。允许倒退等于允许"用旧快照覆盖撤销"，
            // 而旧快照最常见的来源就是中断后重放。
            var current = ReadArtifactsCore();
            if (current.TryGetValue(artifact.ArtifactId, out var existing)
                && artifact.Version < existing.Version)
            {
                throw new InvalidOperationException(
                    $"产物 '{artifact.ArtifactId}' 的版本倒退：已存储 {existing.Version}，"
                    + $"提交 {artifact.Version}。版本必须递增。");
            }

            await WriteLineAsync(payload, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// 读出全部产物（每个 ArtifactId 取最后写入的那条）。
    /// </summary>
    /// <remarks>
    /// <b>为什么是"最后写入胜"而不是"最高版本胜"</b>：
    /// 这个文件是 append-only 日志，追加顺序就是时间顺序，最后一条即当前状态。
    /// 按版本号选胜会带来一个危险后果——撤销时若版本号没有递增，
    /// 撤销会被**静默忽略**，而调用方拿到的返回值表示成功。
    /// 撤销是朝安全方向的迁移，任何"静默丢掉撤销"的可能都不能留。
    /// <para>
    /// 版本号因此只用于审计追溯与审批绑定，不参与选胜。
    /// <see cref="MemoryArtifact.IsIntact"/> 才是防篡改的依据
    /// （它校验内容哈希，与版本无关）。
    /// </para>
    /// </remarks>
    public async Task<IReadOnlyDictionary<string, MemoryArtifact>> ReadArtifactsAsync(
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            return ReadArtifactsCore();
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>扫描文件的读取核心。**调用方必须已持有 <see cref="_lock"/>**。</summary>
    private IReadOnlyDictionary<string, MemoryArtifact> ReadArtifactsCore()
    {
        var previous = _stream.Position;
        _stream.Seek(0, SeekOrigin.Begin);

        try
        {
            var result = new Dictionary<string, MemoryArtifact>(StringComparer.Ordinal);
            using var reader = new StreamReader(
                _stream, Encoding.UTF8, false, 4096, leaveOpen: true);

            while (reader.ReadLine() is { } line)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;

                MemoryArtifact? artifact;
                try { artifact = ParseArtifact(line); }
                catch (Exception ex) when (ex is JsonException or InvalidOperationException)
                {
                    continue;   // 跳过无法解析的行，不中断读取
                }

                if (artifact is null) continue;

                // 后写者胜：追加顺序即时间顺序。
                result[artifact.ArtifactId] = artifact;
            }

            return result;
        }
        finally
        {
            _stream.Seek(previous, SeekOrigin.Begin);
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        await _stream.DisposeAsync().ConfigureAwait(false);
        _lock.Dispose();
    }

    // ── 内部 ─────────────────────────────────────────────────────

    private async Task LoadCursorAsync(CancellationToken cancellationToken)
    {
        _stream.Seek(0, SeekOrigin.Begin);
        using var reader = new StreamReader(
            _stream, Encoding.UTF8, false, 4096, leaveOpen: true);

        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                var node = JsonNode.Parse(line);
                if (node?["kind"]?.GetValue<string>() == "cursor"
                    && node!["sequence"] is { } sequence)
                {
                    Cursor = Math.Max(Cursor, sequence.GetValue<long>());
                }
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException)
            {
                // 尾部残缺记录：跳过。记忆是可重建的派生数据，
                // 不像会话日志那样需要严格对账。
            }
        }
    }

    /// <summary>写一行。**调用方必须已持有 <see cref="_lock"/>**。</summary>
    private async ValueTask WriteLineAsync(
        JsonObject payload, CancellationToken cancellationToken)
    {
        // 直接写一行扁平 JSON。**不要再包一层 payload 字符串**——
        // 那样读端的 node["kind"] 取不到值，等于所有记录都读不出来。
        var line = payload.ToJsonString();
        var bytes = Encoding.UTF8.GetBytes(line + "\n");

        await _stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        _stream.Flush(flushToDisk: true);
    }

    private static MemoryArtifact? ParseArtifact(string line)
    {
        var node = JsonNode.Parse(line);
        if (node?["kind"]?.GetValue<string>() != "artifact") return null;

        return new MemoryArtifact(
            node["artifactId"]!.GetValue<string>(),
            node["version"]!.GetValue<int>(),
            node["contentHash"]!.GetValue<string>(),
            node["key"]!.GetValue<string>(),
            node["value"]!.GetValue<string>(),
            Enum.Parse<MemoryScope>(node["scope"]!.GetValue<string>()),
            node["workspace"]?.GetValue<string>() ?? string.Empty,
            node["sourceTaskId"]!.GetValue<string>(),
            node["createdAt"]!.GetValue<DateTimeOffset>(),
            Enum.Parse<MemoryArtifactState>(node["state"]!.GetValue<string>()),
            node["expiresAt"]?.GetValue<DateTimeOffset>(),
            node["approvedBy"]?.GetValue<string>());
    }
}
