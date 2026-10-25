using System.Text.Json;
using Dougong.Agent;

namespace Dougong.Agent.Comparison;

/// <summary>
/// <see cref="IAgentStore"/> 的内存实现，用于验证"换一种存储、不改内核"。
/// </summary>
/// <remarks>
/// 为什么需要第二个实现：`-- kernel-wiring` 里原有的 <c>RecordingStore</c> 只有一个
/// <c>CommitCount++</c>，它验证的是"服务键能换提供方"，不是"换一种存储后行为仍成立"。
/// P1 判定要求的是后者。
/// <para>
/// 因此这里刻意对齐 <see cref="JsonlAgentStore"/> 的可观察语义：
/// <list type="bullet">
/// <item>序号由存储分配，**忽略**调用方传入的 <see cref="StoreRecord.Sequence"/></item>
/// <item>提交失败时不留半条记录（先写暂存区，成功才并入）</item>
/// <item><see cref="ReadCommittedAsync"/> 只返回已提交内容</item>
/// <item>释放后再用抛 <see cref="ObjectDisposedException"/></item>
/// </list>
/// 与 JSONL 版的差异只在持久化级别（这里是 <see cref="StoreDurability.ProcessMemory"/>）。
/// </para>
/// </remarks>
public sealed class InMemoryAgentStore : IAgentStore
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly List<Persisted> _committed = [];
    private long _sequence;
    private bool _disposed;

    public InMemoryAgentStore(string sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        SessionId = sessionId;
    }

    public string SessionId { get; }

    public StoreDurability Durability => StoreDurability.ProcessMemory;

    /// <summary>已提交记录条数，供验收断言。</summary>
    public int Count
    {
        get { lock (_committed) return _committed.Count; }
    }

    /// <summary>下一次提交是否应失败。用于验证失败不留半条。</summary>
    public bool FailNextCommit { get; set; }

    public ValueTask CommitAsync(StoreRecord record, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        ObjectDisposedException.ThrowIf(_disposed, this);

        // 与 JSONL 版一致：序号自己分配，忽略调用方给的值。
        var next = Interlocked.Increment(ref _sequence);

        if (FailNextCommit)
        {
            FailNextCommit = false;
            // 失败不回滚序号吗？JSONL 版在失败时截断回已提交位置，
            // 因此序号不会前进。这里同样不并入记录、序号回退，
            // 让"已提交序列连续"这一性质在两种实现下都成立。
            Interlocked.Decrement(ref _sequence);
            throw new InvalidOperationException("模拟提交失败。");
        }

        var persisted = new Persisted(
            next, record.Kind, record.Payload.Clone());

        lock (_committed) _committed.Add(persisted);
        return ValueTask.CompletedTask;
    }

    public async IAsyncEnumerable<StoreRecord> ReadCommittedAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        Persisted[] snapshot;
        lock (_committed) snapshot = [.. _committed];

        foreach (var item in snapshot)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Yield();
            yield return new StoreRecord(item.Sequence, item.Kind, item.Payload.Clone());
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _gate.Dispose();
        await Task.CompletedTask;
    }

    private sealed record Persisted(long Sequence, StoreRecordKind Kind, JsonElement Payload);
}
