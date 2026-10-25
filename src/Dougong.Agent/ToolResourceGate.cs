namespace Dougong.Agent;

/// <summary>
/// 首版的资源协调：工具串行 + 工作区独占。
/// </summary>
/// <remarks>
/// 设计依据（docs/architecture.md §4.4）：
/// <list type="bullet">
/// <item>未声明或无法验证并发能力的工具默认 <see cref="ToolConcurrency.Serial"/></item>
/// <item>shell、未知资源、改变进程/环境状态的工具取工作区独占</item>
/// <item>首版只实现这两级；资源级读写键与跨工作区协调属后续扩展，
/// 不预先冻结键格式</item>
/// </list>
/// <para>
/// 与审批的顺序也很重要：**等待审批期间不持锁**。调用方必须先取审批、
/// 再进锁执行，否则一个慢审批会阻塞整个工作区。
/// </para>
/// </remarks>
public sealed class ToolResourceGate : IDisposable
{
    /// <summary>
    /// 进程内共享的工作区锁表。
    /// </summary>
    /// <remarks>
    /// **为什么必须是进程级**：<c>architecture.md</c> §4.4 要求同一工作区的
    /// 串行/独占工具互斥。而每个会话都会 <c>new ToolResourceGate()</c>
    /// （CLI REPL 一次、ACP 每个 session 一次）。若锁表是实例字段，
    /// 同一工作区的两个会话各持一把互不相干的锁——**跨会话互斥不成立**（G9）。
    /// <para>
    /// 用静态表而不是让宿主注入共享表：宿主很容易忘记注入，而忘记的后果
    /// （两个会话同时写同一文件）静默且不可察觉。默认正确比配置正确可靠。
    /// </para>
    /// <para>
    /// 锁按工作区**规范化路径**分键，避免 <c>./ws</c> 与 <c>ws</c> 被当成两个工作区。
    /// </para>
    /// </remarks>
    private static readonly Dictionary<string, (SemaphoreSlim Gate, int RefCount)> SharedWorkspaceLocks =
        new(StringComparer.OrdinalIgnoreCase);

    private static readonly object SharedSync = new();

    private readonly object _sync = new();
    private readonly SemaphoreSlim _serial = new(1, 1);
    private readonly bool _isolated;
    private readonly Dictionary<string, SemaphoreSlim> _privateLocks = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ResourceSlot> _resources = new(StringComparer.Ordinal);

    // 本实例在共享表里持有引用的工作区键，Dispose 时归还。
    private readonly List<string> _sharedKeys = [];
    private bool _disposed;

    /// <summary>
    /// 建立独立的 gate：锁表私有，不与其他 gate 共享工作区互斥。
    /// </summary>
    /// <remarks>
    /// 仅用于测试隔离。生产宿主应使用无参构造（共享进程级锁表），
    /// 否则跨会话互斥不成立（G9）。
    /// </remarks>
    public ToolResourceGate(bool isolated = false)
    {
        _isolated = isolated;
    }

    /// <summary>
    /// 取得执行权。<see cref="ToolConcurrency.Serial"/> 先取全局串行权、再取所属工作区的执行权；
    /// <see cref="ToolConcurrency.WorkspaceExclusive"/> 只取工作区执行权。
    /// </summary>
    /// <remarks>
    /// 两级共用同一把工作区执行权，所以同一工作区内的<em>任何</em>两个调用都不会重叠执行——
    /// 若只给两级各自一把锁，串行工具就能与独占工具并行，等于取消没有真正生效。
    /// 跨工作区协调仍属后续扩展：这里的全局串行权只保证保守默认，不做跨工作区资源仲裁。
    /// </remarks>
    public async ValueTask<IDisposable> EnterAsync(
        ToolExecutionPlan plan, string workspace, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentException.ThrowIfNullOrWhiteSpace(workspace);

        // 声明了资源键就走读写锁：读-读可并行，读-写与写-写互斥（G5）。
        // 未声明时退回两级保守默认，保持既有行为不变。
        if (plan.HasResourceKeys && plan.Concurrency != ToolConcurrency.WorkspaceExclusive)
            return await EnterResourceAsync(plan, cancellationToken).ConfigureAwait(false);

        // 规范化工作区键：./ws 与 ws 是同一个工作区，不能各给一把锁。
        var key = NormalizeWorkspace(workspace);

        SemaphoreSlim gate;
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_isolated)
            {
                if (!_privateLocks.TryGetValue(key, out gate!))
                {
                    gate = new SemaphoreSlim(1, 1);
                    _privateLocks[key] = gate;
                }
            }
            else
            {
                // 共享表要在自己的同步区里访问：多个 gate 实例会并发进来。
                // 引用计数是为了让表能回收：工作区用一次就留一条，长期运行的宿主会泄漏。
                lock (SharedSync)
                {
                    if (SharedWorkspaceLocks.TryGetValue(key, out var entry))
                    {
                        gate = entry.Gate;
                        SharedWorkspaceLocks[key] = (entry.Gate, entry.RefCount + 1);
                    }
                    else
                    {
                        gate = new SemaphoreSlim(1, 1);
                        SharedWorkspaceLocks[key] = (gate, 1);
                    }

                    _sharedKeys.Add(key);
                }
            }
        }

        if (plan.Concurrency == ToolConcurrency.Serial)
        {
            await _serial.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                _serial.Release();
                throw;
            }

            return new Releaser(gate, _serial);
        }

        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        return new Releaser(gate);
    }

    /// <summary>
    /// 释放本实例的资源。
    /// </summary>
    /// <remarks>
    /// **不清理共享锁表**：那是进程级资源，一个会话结束不代表其他会话不用它了。
    /// 清理它会让仍在运行的会话拿到已释放的信号量。
    /// 私有表（隔离模式）才随实例释放。
    /// </remarks>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _serial.Dispose();

        lock (_sync)
        {
            if (_isolated)
            {
                foreach (var gate in _privateLocks.Values) gate.Dispose();
                _privateLocks.Clear();
                foreach (var slot in _resources.Values) slot.WriteGate.Dispose();
                _resources.Clear();
                return;
            }

            // 归还引用：计数归零才从共享表移除。
            // 注意**不 Dispose 信号量**——仍可能有其他实例在用同一个 gate 对象。
            lock (SharedSync)
            {
                foreach (var key in _sharedKeys)
                {
                    if (!SharedWorkspaceLocks.TryGetValue(key, out var entry)) continue;
                    if (entry.RefCount <= 1) SharedWorkspaceLocks.Remove(key);
                    else SharedWorkspaceLocks[key] = (entry.Gate, entry.RefCount - 1);
                }

                _sharedKeys.Clear();
            }
        }
    }

    /// <summary>
    /// 按资源键取得执行权：读-读并行，读写互斥。
    /// </summary>
    /// <remarks>
    /// **为什么不用 <see cref="ReaderWriterLockSlim"/>**：它是线程亲和的
    /// （进入与退出必须在同一线程），而这里的租约可能跨 await 传递。
    /// 因此用"每资源一把信号量 + 读计数"自己实现。
    /// <para>
    /// **加锁顺序**：所有资源键按序数排序后依次取得，避免两个调用以不同顺序
    /// 请求同一组资源而死锁。这是必须的——一个工具同时写 a 和 b，
    /// 另一个先读 b 再写 a，无序加锁会互相等。
    /// </para>
    /// </remarks>
    private async ValueTask<IDisposable> EnterResourceAsync(
        ToolExecutionPlan plan, CancellationToken cancellationToken)
    {
        // 排序去重：同一批里重复的键只取一次，且顺序一致避免死锁。
        var keys = plan.Reads.Concat(plan.Writes)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(k => k, StringComparer.Ordinal)
            .ToArray();

        var lease = new ResourceLease(this);
        try
        {
            foreach (var key in keys)
            {
                var slot = GetResourceSlot(key);
                var isWrite = plan.Writes.Contains(key, StringComparer.Ordinal);

                if (isWrite)
                {
                    // 写：独占 WriteGate，并等待已有读者全部退出。
                    await slot.WriteGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                    try
                    {
                        await slot.Drained.WaitAsync(cancellationToken).ConfigureAwait(false);
                    }
                    catch
                    {
                        slot.WriteGate.Release();
                        throw;
                    }

                    lease.Add(slot, writer: true);
                }
                else
                {
                    // 读：瞬时占用 WriteGate 与写者互斥，取得计数后立刻归还，
                    // 因此多个读者可以同时通过。
                    await slot.WriteGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                    slot.EnterRead();
                    slot.WriteGate.Release();
                    lease.Add(slot, writer: false);
                }
            }

            return lease;
        }
        catch
        {
            // 中途取消或失败：已取得的必须释放，否则这些资源永久锁死。
            lease.Dispose();
            throw;
        }
    }

    private ResourceSlot GetResourceSlot(string key)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_resources.TryGetValue(key, out var existing)) return existing;

            var slot = new ResourceSlot();
            _resources[key] = slot;
            return slot;
        }
    }

    /// <summary>
    /// 一个资源的读写状态。
    /// </summary>
    /// <remarks>
    /// **语义**：<see cref="WriteGate"/> 保证写者独占；读者在取得时**瞬时**占用它
    /// 再立刻归还，因此多个读者可并发，只有写者被挡。写者还要等
    /// <see cref="Readers"/> 归零——否则"写者拿到 WriteGate 时读者已经在读"
    /// 这件事不会被发现。
    /// <para>
    /// 用 <see cref="TaskCompletionSource"/> 而不是轮询等待读者清零：
    /// 轮询在长读（大文件）下会白烧 CPU，也让取消响应变慢。
    /// </para>
    /// </remarks>
    private sealed class ResourceSlot
    {
        public SemaphoreSlim WriteGate { get; } = new(1, 1);
        public int Readers;

        private TaskCompletionSource? _drained;

        public Task Drained
        {
            get
            {
                lock (this)
                {
                    if (Readers == 0) return Task.CompletedTask;
                    return (_drained ??= new TaskCompletionSource(
                        TaskCreationOptions.RunContinuationsAsynchronously)).Task;
                }
            }
        }

        public void EnterRead()
        {
            lock (this) Readers++;
        }

        public void ExitRead()
        {
            TaskCompletionSource? signal;
            lock (this)
            {
                Readers--;
                if (Readers > 0) return;
                signal = _drained;
                _drained = null;
            }

            signal?.TrySetResult();
        }
    }

    /// <summary>按取得顺序**逆序**释放：先还写者/读者，再还资源槽。</summary>
    private sealed class ResourceLease(ToolResourceGate owner) : IDisposable
    {
        private readonly List<(ResourceSlot Slot, bool Writer)> _held = [];
        private int _released;

        public void Add(ResourceSlot slot, bool writer) => _held.Add((slot, writer));

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) != 0) return;

            // 逆序释放，与取得顺序相反。
            for (var i = _held.Count - 1; i >= 0; i--)
            {
                var (slot, writer) = _held[i];
                if (writer) slot.WriteGate.Release();
                else slot.ExitRead();   // 计数归零时唤醒等待的写者
            }

            _held.Clear();
            _ = owner;
        }
    }

    private static string NormalizeWorkspace(string workspace)
    {
        try { return Path.GetFullPath(workspace); }
        catch (Exception) { return workspace; }
    }

    /// <summary>
    /// 释放顺序与取得顺序相反：先放工作区执行权，再放全局串行权。
    /// </summary>
    private sealed class Releaser(SemaphoreSlim gate, SemaphoreSlim? outer = null) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) != 0) return;
            gate.Release();
            outer?.Release();
        }
    }
}
