using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;

namespace Dougong.Agent;

/// <summary>
/// 后台作业注册表：启动子进程后立即返回，稍后按需读取输出。
/// </summary>
/// <remarks>
/// <b>解决什么问题</b>：模型跑一次构建或测试套件可能几分钟。现在它只能
/// 同步等——整轮对话被占住，用户看不到任何进展，也没法让它中途干别的。
/// dsh 的 <c>dsh-jobs</c> 就是让"启动 → 稍后查结果"成为可能。
///
/// <para>
/// <b>照 dsh 的 <c>dsh-jobs-local</c> 做，两条关键决定一致</b>：
/// <list type="bullet">
/// <item><b>按 owner 限并发</b>：不限制的话，模型可以一口气开二十个作业
/// 把机器拖垮。dsh 默认每个 owner 10 个。</item>
/// <item><b>环形缓冲按字节保留，游标只进不退</b>：输出可能远超内存，
/// 而模型是一次次来读的。丢掉它没读过的那段等于让输出消失；
/// 丢掉它已读过的那段才是安全的。</item>
/// </list>
/// </para>
///
/// <para>
/// <b>与 <see cref="IShellExecutor"/> 的关系是并列而非继承</b>：那个接口
/// 明确只描述"起一个进程并等它结束"（见其类型注释），而后台作业的形态是
/// "起进程、立刻返回、之后多次读"。硬把两者合成一个接口会让
/// "换沙箱实现"变成"换语义"——那不是替换。
/// </para>
///
/// <para>
/// <b>不做持久化</b>：作业活在进程内，进程退出就没了。理由是后台任务的
/// 生命周期本来就短于会话（跑完就结束），而把「进程内句柄」落盘再恢复
/// 是不可能的——重启后那些子进程已经不存在。这点必须让模型知道，
/// 所以 <see cref="JobSnapshot"/> 里带 <see cref="JobState"/>。
/// </para>
/// </remarks>
public sealed class JobRegistry : IDisposable
{
    /// <summary>默认每个 owner 最多同时跑几个作业。</summary>
    /// <remarks>
    /// 与 dsh 的 <c>maxConcurrentJobsPerOwner</c> 默认值一致。10 的理由：
    /// 再多就不是"并行做事"而是"把机器拖垮"，而模型的判断力不足以保证克制。
    /// </remarks>
    public const int DefaultMaxConcurrentPerOwner = 10;

    /// <summary>每个作业的环形缓冲保留多少字节。</summary>
    /// <remarks>
    /// 与 dsh 的 <c>retainBytes</c> 默认值一致（256 KiB）。这个量的取向是
    /// "够放下一屏构建日志的尾巴"，而不够完整日志——完整的应该让模型
    /// 重定向到文件再 <c>read_file</c>，那才是有界的做法。
    /// </remarks>
    public const int DefaultRetainBytes = 256 * 1024;

    private readonly int _maxConcurrentPerOwner;
    private readonly int _retainBytes;
    private readonly ConcurrentDictionary<string, Job> _jobs = new(StringComparer.Ordinal);
    private readonly string _workspace;
    private int _nextId;
    private bool _disposed;

    /// <summary>构造。</summary>
    /// <param name="workspace">作业的工作目录。</param>
    /// <param name="maxConcurrentPerOwner">每个 owner 的并发上限。</param>
    /// <param name="retainBytes">每作业环形缓冲的字节上限。</param>
    public JobRegistry(
        string workspace,
        int maxConcurrentPerOwner = DefaultMaxConcurrentPerOwner,
        int retainBytes = DefaultRetainBytes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspace);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxConcurrentPerOwner, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(retainBytes, 1024);

        _workspace = Path.GetFullPath(workspace);
        _maxConcurrentPerOwner = maxConcurrentPerOwner;
        _retainBytes = retainBytes;
    }

    /// <summary>启动一个后台作业，立即返回它的标识。</summary>
    /// <param name="owner">作业归属者（通常是一个会话）。</param>
    /// <param name="command">要执行的命令。</param>
    /// <returns>作业快照；达到并发上限时返回错误说明。</returns>
    /// <remarks>
    /// <b>不等待命令结束</b>：这正是它相对 <c>shell</c> 工具的全部价值。
    /// <para>
    /// <b>达到上限时不排队而是拒绝</b>：排队会让模型以为已启动，
    /// 而它可能几分钟后才真正开始——那时它已经基于错误假设继续做事了。
    /// 明确拒绝让模型立刻知道要等或要停。
    /// </para>
    /// </remarks>
    public JobSnapshot Start(string owner, string command)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command);
        ObjectDisposedException.ThrowIf(_disposed, this);

        owner = string.IsNullOrWhiteSpace(owner) ? "(unowned)" : owner;

        // 先清理已结束的，再判上限——否则跑完的作业也占着名额。
        PruneFinished(owner);

        var active = _jobs.Values.Count(j => j.Owner == owner && j.IsActive);
        if (active >= _maxConcurrentPerOwner)
        {
            return JobSnapshot.Rejected(
                $"该会话已有 {active} 个作业在跑（上限 {_maxConcurrentPerOwner}）。"
                + "先等它们结束，或用 job_stop 停掉不需要的。");
        }

        var id = $"job-{Interlocked.Increment(ref _nextId)}";
        var job = new Job(id, owner, command, _workspace, _retainBytes);

        if (!_jobs.TryAdd(id, job))
        {
            // 自增 ID 不会重复，走到这里说明状态被外部改坏了。
            job.Dispose();
            return JobSnapshot.Rejected($"作业标识冲突：{id}");
        }

        try
        {
            job.Start();
        }
        catch (Exception ex)
        {
            // 起不动就把条目删掉：留一个永远 Running 的幽灵条目比直接报错更糟。
            _jobs.TryRemove(id, out _);
            job.Dispose();
            return JobSnapshot.Rejected($"无法启动：{ex.GetType().Name}: {ex.Message}");
        }

        return job.Snapshot(cursor: 0);
    }

    /// <summary>
    /// 读一个作业的输出。游标只进不退。
    /// </summary>
    /// <param name="jobId">作业标识。</param>
    /// <param name="cursor">上次读到的位置；0 表示从头。</param>
    /// <remarks>
    /// <b>返回的 <see cref="JobSnapshot.NextCursor"/> 要用于下一次调用</b>。
    /// 不传游标（或传 0）会重复拿到已经读过的内容——那是调用方的选择，
    /// 不是这里的缺陷，但它会让模型浪费上下文。
    /// <para>
    /// <b>输出在环形缓冲里被覆盖时如实说明</b>：模型需要知道"前面有一段丢了"，
    /// 否则它会以为看到的就是全部。
    /// </para>
    /// </remarks>
    public JobSnapshot Read(string jobId, long cursor = 0)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!_jobs.TryGetValue(jobId, out var job))
            return JobSnapshot.NotFound(jobId);

        return job.Snapshot(cursor);
    }

    /// <summary>停止一个作业（结束其进程树）。</summary>
    /// <remarks>
    /// <b>结束整棵进程树</b>：只杀父进程会留下孤儿——一个 <c>npm test</c>
    /// 可能派生出十几个子进程，它们会继续占着端口与文件锁。
    /// </remarks>
    public JobSnapshot Stop(string jobId)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!_jobs.TryGetValue(jobId, out var job))
            return JobSnapshot.NotFound(jobId);

        job.RequestStop();
        return job.Snapshot(cursor: 0);
    }

    /// <summary>列出作业；只给摘要，不含输出。</summary>
    /// <remarks>
    /// <b>列表不带输出</b>：列五个作业各带 256 KiB 输出会直接吃满上下文。
    /// 要看内容就单独 <see cref="Read"/>。
    /// </remarks>
    public IReadOnlyList<JobSnapshot> List(string? owner = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        return [.. _jobs.Values
            .Where(j => owner is null || j.Owner == owner)
            .OrderByDescending(j => j.StartedAt)
            .Select(j => j.Snapshot(cursor: j.Consumed) with { Output = string.Empty, OmittedBytes = 0 })];
    }

    /// <summary>清理已结束且被读过的作业。</summary>
    /// <remarks>
    /// <b>只清"结束且已读"的</b>：结束但没人读过的作业留着——它的输出
    /// 可能是这次工作的唯一证据，清掉就再也看不到。
    /// </remarks>
    private void PruneFinished(string owner)
    {
        foreach (var (id, job) in _jobs)
        {
            if (job.Owner != owner) continue;
            if (job.IsActive) continue;
            if (job.Consumed < job.TotalWritten) continue;

            if (_jobs.TryRemove(id, out var removed)) removed.Dispose();
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // 释放时结束全部作业：留着的子进程会在主程序退出后变成孤儿。
        foreach (var job in _jobs.Values)
        {
            try { job.RequestStop(); } catch { /* 已结束 */ }
            job.Dispose();
        }

        _jobs.Clear();
    }

    /// <summary>一个作业的完整状态。</summary>
    private sealed class Job : IDisposable
    {
        private readonly StringBuilder _buffer = new();
        private readonly object _sync = new();
        private readonly string _workspace;
        private readonly int _retainBytes;
        private Process? _process;
        private long _totalWritten;
        private long _discarded;

        public Job(string id, string owner, string command, string workspace, int retainBytes)
        {
            Id = id;
            Owner = owner;
            Command = command;
            _workspace = workspace;
            _retainBytes = retainBytes;
            StartedAt = DateTimeOffset.UtcNow;
        }

        public string Id { get; }
        public string Owner { get; }
        public string Command { get; }
        public DateTimeOffset StartedAt { get; }

        /// <summary>模型已经读过多少字节（游标位置）。</summary>
        public long Consumed { get; set; }

        /// <summary>累计写入过多少字节（含已被环形缓冲丢弃的）。</summary>
        public long TotalWritten
        {
            get { lock (_sync) return _totalWritten; }
        }

        public bool IsActive => _process is { HasExited: false };

        public void Start()
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/sh",
                WorkingDirectory = _workspace,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };

            if (OperatingSystem.IsWindows())
            {
                startInfo.ArgumentList.Add("/c");
                startInfo.ArgumentList.Add(Command);
            }
            else
            {
                startInfo.ArgumentList.Add("-c");
                startInfo.ArgumentList.Add(Command);
            }

            _process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Process.Start 返回 null");

            // 立刻关掉 stdin：多数命令会等它，而我们从不需要往作业里写东西。
            try { _process.StandardInput.Close(); } catch { /* 已关闭 */ }

            // **两个流都读**：只读 stdout 会让填满 stderr 管道缓冲的进程挂死。
            _ = Task.Run(() => DrainAsync(_process.StandardOutput));
            _ = Task.Run(() => DrainAsync(_process.StandardError));
        }

        private async Task DrainAsync(StreamReader reader)
        {
            var chunk = new char[4096];
            try
            {
                while (true)
                {
                    var read = await reader.ReadAsync(chunk).ConfigureAwait(false);
                    if (read == 0) break;
                    Append(new string(chunk, 0, read));
                }
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                // 进程被杀时流会断：不是错误，读数到此为止。
            }
        }

        private void Append(string text)
        {
            lock (_sync)
            {
                _buffer.Append(text);
                _totalWritten += text.Length;

                // 环形：超出上限时从头部丢，丢掉多少要记下来——
                // 只报"还剩多少"会让模型以为那就是全部输出。
                var excess = _buffer.Length - _retainBytes;
                if (excess > 0)
                {
                    _buffer.Remove(0, excess);
                    _discarded += excess;
                }
            }
        }

        public void RequestStop()
        {
            var process = _process;
            if (process is null) return;

            try
            {
                if (!process.HasExited)
                {
                    // **结束整棵进程树**：只杀父进程会留下孤儿，
                    // 它们继续占着端口与文件锁。
                    process.Kill(entireProcessTree: true);
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                // 已经退出，或权限不足。都不该让 Stop 失败——
                // 调用方要的是"尽量停"，而不是"证明停成功了"。
            }
        }

        public JobSnapshot Snapshot(long cursor)
        {
            lock (_sync)
            {
                var state = State();
                var omitted = _discarded;

                // 游标可能落在已丢弃区间里：此时只能从缓冲开头给。
                var bufferStart = _totalWritten - _buffer.Length;
                var effective = Math.Max(cursor, bufferStart);
                var from = (int)Math.Max(0, effective - bufferStart);
                var output = from >= _buffer.Length ? string.Empty : _buffer.ToString(from, _buffer.Length - from);

                if (cursor > 0 && cursor < bufferStart) omitted += bufferStart - cursor;

                Consumed = effective + output.Length;

                return new JobSnapshot(
                    Id,
                    Owner,
                    Command,
                    state,
                    StartedAt,
                    ExitCode: ExitCodeOf(_process),
                    Output: output,
                    NextCursor: Consumed,
                    OmittedBytes: (int)Math.Min(int.MaxValue, omitted),
                    TotalBytes: (int)Math.Min(int.MaxValue, _totalWritten),
                    Diagnostic: null);
            }
        }

        private static int? ExitCodeOf(Process? process)
        {
            try { return process is { HasExited: true } ? process.ExitCode : null; }
            catch (InvalidOperationException) { return null; }
        }

        private JobState State()
        {
            var process = _process;
            if (process is null) return JobState.Rejected;

            try
            {
                if (!process.HasExited) return JobState.Running;
                return JobState.Finished;
            }
            catch (InvalidOperationException)
            {
                // 进程对象已失效（被回收）：按结束处理，但没有退出码。
                return JobState.Finished;
            }
        }

        public void Dispose()
        {
            try { _process?.Dispose(); } catch { /* 已释放 */ }
            _process = null;
        }
    }
}

/// <summary>作业状态。</summary>
public enum JobState
{
    /// <summary>正在运行。</summary>
    Running,

    /// <summary>已结束（正常或异常退出）。</summary>
    Finished,

    /// <summary>没启动成功，或标识不存在。</summary>
    Rejected,
}

/// <summary>一个作业在某一刻的快照。</summary>
/// <param name="Id">作业标识。</param>
/// <param name="Owner">归属者。</param>
/// <param name="Command">执行的命令。</param>
/// <param name="State">当前状态。</param>
/// <param name="StartedAt">启动时刻。</param>
/// <param name="ExitCode">退出码；仍在运行或未启动时为 null。</param>
/// <param name="Output">从游标起的新增输出；列表视图为空。</param>
/// <param name="NextCursor">下次读取应传的游标。</param>
/// <param name="OmittedBytes">因环形缓冲丢弃而永久失去的字节数。</param>
/// <param name="TotalBytes">累计输出字节数。</param>
/// <param name="Diagnostic">拒绝或失败时的说明。</param>
public sealed record JobSnapshot(
    string Id,
    string Owner,
    string Command,
    JobState State,
    DateTimeOffset StartedAt,
    int? ExitCode,
    string Output,
    long NextCursor,
    int OmittedBytes,
    int TotalBytes,
    string? Diagnostic)
{
    /// <summary>构造一个"没启动成功"的快照。</summary>
    public static JobSnapshot Rejected(string diagnostic) =>
        new(string.Empty, string.Empty, string.Empty, JobState.Rejected,
            DateTimeOffset.UtcNow, null, string.Empty, 0, 0, 0, diagnostic);

    /// <summary>构造一个"标识不存在"的快照。</summary>
    /// <remarks>
    /// <b>与"拒绝启动"分开</b>：前者是"你给错了标识"，后者是"这次起不来"。
    /// 模型对两者的反应不同——前者该去查作业列表，后者该等或换做法。
    /// </remarks>
    public static JobSnapshot NotFound(string jobId) =>
        new(jobId, string.Empty, string.Empty, JobState.Rejected,
            DateTimeOffset.UtcNow, null, string.Empty, 0, 0, 0,
            $"没有这个作业：{jobId}。用 job_list 查看现有作业。");
}
