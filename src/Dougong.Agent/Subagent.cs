using System.Text;
using System.Text.Json;

namespace Dougong.Agent;

/// <summary>
/// 子 agent 的委派与生命周期管理。
/// </summary>
/// <remarks>
/// <b>解决什么问题</b>：有些任务本身是个"小工程"——找清楚某个模块的调用链、
/// 把五个文件里重复的实现收成一个。让主对话亲自做，它的上下文会被
/// 大量的中间读取记录填满，而那些记录在任务完成后毫无用处。
/// 子 agent 的价值就是：<b>中间过程留在子上下文里，只有结论回到主对话</b>。
///
/// <para>
/// <b>照 dsh 的 <c>dsh-subagent</c>，核心语义一致</b>：
/// <list type="bullet">
/// <item><b>不共享上下文</b>：子 agent 从空历史开始，任务必须自包含。
/// 这是它的全部意义——共享了就没有隔离，等于主对话自己跑一遍。</item>
/// <item><b>只回结果，不回过程</b>：dsh 的原话是 "The subagent returns its
/// result, not its intermediate steps"。</item>
/// <item><b>启动立即返回 id</b>，完成时通知。让主对话在等子 agent 时什么都不做，
/// 等于把并行能力又浪费掉。</item>
/// </list>
/// </para>
///
/// <para>
/// <b>深度的硬约束比 dsh 更强</b>：dsh 用可选的 <c>maxDepth</c> 参数控制，
/// 我们**没有"不限制"这个选项**——子 agent 能调 <c>subagent</c> 工具就意味着
/// 无限递归的可能，而那种失败是"跑了几百层之后栈溢出或额度烧光"，
/// 排查起来毫无线索。所以深度上限是构造参数而不是调用参数，
/// 且子 agent 的工具集里默认不注册这个工具（见 <c>SubagentTool</c>）。
/// </para>
///
/// <para>
/// <b>不做延续对话（continuation）</b>：dsh 支持"子 agent 完成后还能
/// <c>send_message</c> 接着聊"，那要求子会话可恢复、有独立日志与身份。
/// 我们每个子 agent 是一次性的：跑完就丢。理由是没有消费者——
/// 而"能接着聊"会带来一串状态（谁的上下文、谁的生命周期、断线怎么算），
/// 在没有真实需求时先不做。
/// </para>
/// </remarks>
public sealed class SubagentRegistry : IAsyncDisposable
{
    /// <summary>默认最大委派深度。</summary>
    /// <remarks>
    /// <b>3 层够用</b>：主 → 子 → 孙。再深的话每层都在消耗延迟与额度，
    /// 而收益（"更细的分工"）急剧下降。dsh 的默认也是有限值。
    /// </remarks>
    public const int DefaultMaxDepth = 3;

    /// <summary>默认并发上限。</summary>
    /// <remarks>
    /// <b>与后台作业同一判据</b>：并行数由宿主资源决定，而不是由模型请求多少决定。
    /// 超过上限时**拒绝而不是排队**——排队会让模型以为已启动，
    /// 而它可能几分钟后才真正开始，那时它已经基于错误假设继续做事了。
    /// </remarks>
    public const int DefaultMaxConcurrent = 4;

    private readonly int _maxDepth;
    private readonly int _maxConcurrent;
    private readonly Dictionary<string, SubagentHandle> _active = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _disposed;

    /// <summary>已完成的累计数，供诊断（不影响并发判定）。</summary>
    private int _completedCount;

    /// <summary>构造。</summary>
    /// <param name="maxDepth">最大委派深度（≥1）。</param>
    /// <param name="maxConcurrent">最大同时运行数（≥1）。</param>
    public SubagentRegistry(
        int maxDepth = DefaultMaxDepth,
        int maxConcurrent = DefaultMaxConcurrent)
    {
        if (maxDepth < 1)
            throw new ArgumentOutOfRangeException(nameof(maxDepth), maxDepth, "深度上限必须 ≥ 1。");
        if (maxConcurrent < 1)
            throw new ArgumentOutOfRangeException(
                nameof(maxConcurrent), maxConcurrent, "并发上限必须 ≥ 1。");

        _maxDepth = maxDepth;
        _maxConcurrent = maxConcurrent;
    }

    /// <summary>最大深度。</summary>
    public int MaxDepth => _maxDepth;

    /// <summary>最大并发数。</summary>
    public int MaxConcurrent => _maxConcurrent;

    /// <summary>当前活跃的子 agent 数，供诊断。</summary>
    public int ActiveCount
    {
        get { lock (_active) return _active.Count; }
    }

    /// <summary>
    /// 尝试占一个并发槽。满了返回 false 而不是排队。
    /// </summary>
    /// <remarks>
    /// <b>拒绝优于排队</b>：排队会让模型误以为已经启动。
    /// 拒绝让它立刻知道要么等要么自己来。
    /// </remarks>
    public async Task<bool> TryAcquireAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // 名额按"仍在跑的"算：完成的子 agent 不该继续占位。
            if (_active.Values.Count(h => !h.IsCompleted) >= _maxConcurrent) return false;
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>登记一个活跃的子 agent。</summary>
    /// <param name="handle">句柄。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public async Task RegisterAsync(
        SubagentHandle handle, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(handle);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _active[handle.Id] = handle;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>注销（结果已取走，或被丢弃）。</summary>
    /// <param name="id">子 agent 标识。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public async Task UnregisterAsync(string id, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _active.Remove(id);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// 标记完成：释放并发名额，但**保留在列表里**等结果被取走。
    /// </summary>
    /// <remarks>
    /// <b>为什么分开两个动作</b>：
    /// <list type="bullet">
    /// <item>释放名额要早——否则一个跑完的子 agent 一直占着并发数，
    /// 模型会以为"满了"而实际上没事在做。</item>
    /// <item>移除要晚——结果没被取走就移除，等于把结果扔了。</item>
    /// </list>
    /// 第一版把两者揉在一起（完成即注销），结果一个也取不到。
    /// </remarks>
    /// <param name="id">子 agent 标识。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public async Task MarkCompletedAsync(string id, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _completedCount++;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>取一个活跃句柄；不存在时 null。</summary>
    /// <param name="id">子 agent 标识。</param>
    public SubagentHandle? Find(string id)
    {
        lock (_active) return _active.GetValueOrDefault(id);
    }

    /// <summary>列出全部活跃子 agent（供 <c>subagent_list</c>）。</summary>
    public IReadOnlyList<SubagentHandle> Active()
    {
        lock (_active) return [.. _active.Values];
    }

    /// <summary>
    /// 释放：取消所有活跃子 agent 并等待它们退出。
    /// </summary>
    /// <remarks>
    /// <b>必须等</b>：异步任务在宿主释放后才继续跑，会在日志文件关闭后
    /// 还往里写——那是"关掉会话后进程还在动"的一种形态。
    /// </remarks>
    public async ValueTask DisposeAsync()
    {
        List<SubagentHandle> snapshot;
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed) return;
            _disposed = true;
            snapshot = [.. _active.Values];
            _active.Clear();
        }
        finally
        {
            _gate.Release();
        }

        foreach (var handle in snapshot) handle.Cancel();

        // 有界等待：卡死的子 agent 不该让整个宿主退不出去。
        var wait = Task.WhenAll(snapshot.Select(h => h.Completion));
        await Task.WhenAny(wait, Task.Delay(TimeSpan.FromSeconds(5))).ConfigureAwait(false);

        _gate.Dispose();
    }
}

/// <summary>一个运行中的子 agent。</summary>
public sealed class SubagentHandle : IDisposable
{
    private readonly CancellationTokenSource _cts = new();

    internal SubagentHandle(string id, string label)
    {
        Id = id;
        Label = label;
    }

    /// <summary>子 agent 标识。</summary>
    public string Id { get; }

    /// <summary>短语描述，用于列表展示。</summary>
    public string Label { get; }

    /// <summary>任务是否已经结束（完成、失败或取消）。</summary>
    public bool IsCompleted => Completion.IsCompleted;

    /// <summary>最终结果；未完成时为 null。</summary>
    public SubagentResult? Result { get; internal set; }

    /// <summary>完成信号（无论成败都会完成）。</summary>
    internal Task Completion { get; set; } = Task.CompletedTask;

    /// <summary>子 agent 的取消令牌。</summary>
    internal CancellationToken Token => _cts.Token;

    /// <summary>请求停止。</summary>
    /// <remarks>
    /// <b>只是请求</b>：子 agent 可能正在一次工具调用中间，
    /// 它会在这个调用返回后看到取消。与 <see cref="IToolInterceptor"/> 的
    /// "拒绝"不同——那是执行前的判断，这是执行中的打断。
    /// </remarks>
    public void Cancel()
    {
        try { _cts.Cancel(); }
        catch (ObjectDisposedException) { }
    }

    public void Dispose() => _cts.Dispose();
}

/// <summary>子 agent 的最终结果。</summary>
/// <param name="Id">子 agent 标识。</param>
/// <param name="Status">结束方式。</param>
/// <param name="Text">子 agent 的最终正文；失败或取消时可能为空。</param>
/// <param name="Diagnostic">失败原因。</param>
/// <param name="ToolRounds">它跑了几轮工具调用（供判断"值不值"）。</param>
public sealed record SubagentResult(
    string Id,
    SubagentStatus Status,
    string Text,
    string? Diagnostic,
    int ToolRounds);

/// <summary>子 agent 的结束方式。</summary>
public enum SubagentStatus
{
    /// <summary>正常结束。</summary>
    Completed,

    /// <summary>失败（模型错误、工具反复失败等）。</summary>
    Failed,

    /// <summary>被取消。</summary>
    Cancelled,
}

/// <summary>
/// 把一次子 agent 运行包成可等待的结果。
/// </summary>
/// <remarks>
/// <b>只收集最终正文</b>：中间的工具调用与推理都不回传。
/// 这正是子 agent 的意义——否则"隔离上下文"就只是把记录换个地方存。
/// </remarks>
public static class SubagentRunner
{
    /// <summary>
    /// 运行一个子 agent 并把结果写进句柄。
    /// </summary>
    /// <param name="registry">注册表（用于注销）。</param>
    /// <param name="handle">句柄。</param>
    /// <param name="runtime">子运行时。<b>必须是独立实例</b>，不能复用主对话的。</param>
    /// <param name="request">运行请求（历史为空）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public static async Task RunAsync(
        SubagentRegistry registry,
        SubagentHandle handle,
        IAgentRuntime runtime,
        AgentRunRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(handle);
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(request);

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, handle.Token);

        var text = new StringBuilder();
        var toolRounds = 0;
        var status = SubagentStatus.Completed;
        string? diagnostic = null;

        try
        {
            await foreach (var evt in runtime.RunAsync(request, linked.Token)
                .ConfigureAwait(false))
            {
                switch (evt)
                {
                    case AgentEvent.TextDelta delta:
                        text.Append(delta.Text);
                        break;

                    case AgentEvent.ToolCallCompleted:
                        toolRounds++;
                        break;

                    case AgentEvent.RunFinished finished:
                        // **取消与失败必须分开处理，而且取消不能漏**。
                        //
                        // 第一版只看了 Failed：而运行时在取消时是**正常结束流**
                        // 并发出 RunFinished(Cancelled)——不抛异常。
                        // 于是被取消的子 agent 报 Completed，调用方看不出
                        // "它没跑完"；更糟的是空结果会被当成"任务做完了，没有结论"。
                        switch (finished.Outcome)
                        {
                            case AgentRunOutcome.Failed:
                                status = SubagentStatus.Failed;
                                diagnostic = finished.Diagnostic;
                                break;

                            case AgentRunOutcome.Cancelled:
                                status = SubagentStatus.Cancelled;
                                diagnostic = "子 agent 被取消。";
                                break;
                        }

                        break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            status = SubagentStatus.Cancelled;
            diagnostic = "子 agent 被取消。";
        }
        catch (Exception ex)
        {
            status = SubagentStatus.Failed;
            diagnostic = $"{ex.GetType().Name}: {ex.Message}";
        }
        finally
        {
            handle.Result = new SubagentResult(
                handle.Id, status, text.ToString().Trim(), diagnostic, toolRounds);

            // **不在这里注销**。第一版在这里注销了，于是 `subagent_result`
            // 永远取不到结果——"取走即消失"变成了"完成后立刻消失"，
            // 而模型拿到的是一个不存在的 id。
            //
            // 正确的生命周期是：完成后**留在注册表里**，等 `subagent_result`
            // 取走时再移除。这也让 `subagent_list` 能显示"已完成但未取走"，
            // 否则模型看不到任何痕迹，只能重新委派一遍。
            await registry.MarkCompletedAsync(handle.Id, CancellationToken.None)
                .ConfigureAwait(false);
        }
    }
}
