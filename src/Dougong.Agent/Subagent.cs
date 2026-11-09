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

            // **在这一刻就挂上转发**，而不是让订阅方去遍历现有句柄：
            // 订阅方（协议层 / UI）通常比第一个子 agent 更早存在，
            // 让它"订阅时遍历一遍、之后每个新的再挂一次"是把时序知识
            // 推给调用方。注册表自己知道每次有谁进来，就该由它转发。
            handle.Changed += OnHandleChanged;

            // 登记本身就是"出现了"这件事。**必须在锁内、在释放前触发**：
            // 调用方拿到 Started 之后通常会立刻去查列表，
            // 若此时那边还没登记完，就会查不到刚通知过的那个 id。
            Started?.Invoke(handle);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// 任何一个子 agent 结束时的通知。
    /// </summary>
    /// <remarks>
    /// <b>这是宿主做事件驱动的入口。</b>只报"结束了"（成功/失败/取消都会来），
    /// 不报中途进度——子 agent 的中间过程应当留在子上下文里。
    ///
    /// <para>
    /// <b>回调在子 agent 的收尾线程上执行</b>，不要在回调里做阻塞等待
    /// （例如等子 agent 自己的完成 Task，那是死锁）。要刷新就异步投递。
    /// </para>
    ///
    /// <para>
    /// <b>它只在句柄被取消订阅/注销后停止</b>：注销时转发会摘掉，
    /// 所以不会出现"取走结果之后还推一条"。
    /// </para>
    /// </remarks>
    public event Action<SubagentHandle>? Completed;

    /// <summary>
    /// 有新的子 agent 被登记（即"开始了"）。
    /// </summary>
    /// <remarks>
    /// <b>没有它，界面只能靠轮询发现"多了一个"</b>：只推 <see cref="Completed"/>
    /// 的话，用户在主对话还在生成正文时完全看不到"模型又派了一个子 agent 出去"
    /// ——而那正是额度开始被消耗的时刻。
    ///
    /// <para>
    /// <b>与 <see cref="Completed"/> 成对</b>：一条 Started、一条 Completed，
    /// 界面就能维护完整的生命周期，不必在中间轮询。中间的工具轮数变化
    /// **不推**（那是过程，不该漏回主侧）。
    /// </para>
    ///
    /// <para>
    /// <b>回调在登记方的线程上、且在注册表的锁内执行</b>：回调里不能
    /// 同步等待注册表的其它方法（会死锁）。要查列表就异步投递出去。
    /// </para>
    /// </remarks>
    public event Action<SubagentHandle>? Started;

    /// <summary>把句柄的结束通知转发给注册表级订阅者。</summary>
    private void OnHandleChanged(SubagentHandle handle) => Completed?.Invoke(handle);

    /// <summary>注销（结果已取走，或被丢弃）。</summary>
    /// <param name="id">子 agent 标识。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public async Task UnregisterAsync(string id, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_active.Remove(id, out var removed))
            {
                // **必须摘掉转发**：不摘的话句柄会被事件持有，注销之后
                // 一条迟到的通知仍会推到已经取走结果的订阅方——
                // 而那个 id 已经不在列表里了，界面会显示一个幽灵条目。
                removed.Changed -= OnHandleChanged;
            }
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
        StartedAt = DateTimeOffset.UtcNow;
    }

    /// <summary>子 agent 标识。</summary>
    public string Id { get; }

    /// <summary>短语描述，用于列表展示。</summary>
    public string Label { get; }

    /// <summary>
    /// 启动时刻。
    /// </summary>
    /// <remarks>
    /// <b>宿主需要它才能回答"这个子 agent 跑了多久"</b>：子 agent 消耗的是
    /// 同一份额度却不在主对话的条目流里，用户看不清代价。
    /// 只报"在跑"和报"已经跑了 4 分钟"是两种完全不同的知情程度。
    /// </remarks>
    public DateTimeOffset StartedAt { get; }

    /// <summary>
    /// 发起它的那一轮；未知时为 null。
    /// </summary>
    /// <remarks>
    /// <b>界面据此把它归到对应回合下面</b>，而不是漂在会话级——
    /// 否则用户看到一串子 agent，却不知道它们是因为哪句话被派出去的。
    /// </remarks>
    public string? ParentTurnId { get; init; }

    /// <summary>
    /// 是否已经被请求停止。
    /// </summary>
    /// <remarks>
    /// <b>它回答"点了停止之后到底有没有生效"</b>：取消是请求不是立即终止，
    /// 界面要把"已请求停止"与"已停止"分开显示。没有这个属性时，
    /// 调用方只能靠"状态还没变"来猜，而那是无法区分的
    /// ——"请求丢了"和"正在跑当前工具调用"看起来一模一样。
    /// </remarks>
    public bool IsCancellationRequested
    {
        get
        {
            try { return _cts.IsCancellationRequested; }
            catch (ObjectDisposedException) { return true; }
        }
    }

    /// <summary>已跑的工具轮数；未知时为 null（供判断"值不值"）。</summary>
    public int? ToolRounds { get; internal set; }

    /// <summary>
    /// 任务是否已经结束（完成、失败或取消）。
    /// </summary>
    /// <remarks>
    /// <b>它与 <see cref="Result"/> 是分开维护的</b>：跑完了但结果还没被取走时，
    /// 句柄仍在列表里、<c>IsCompleted</c> 为真、<c>Result</c> 有值。
    /// 所以判断"现在是什么状态"不能只看它——用 <see cref="CurrentStatus"/>。
    /// </remarks>
    public bool IsCompleted => Completion.IsCompleted;

    /// <summary>
    /// 当前状态。**宿主与诊断都应该用这个，不要自己拼 <c>IsCompleted</c> 与 <c>Result</c>。**
    /// </summary>
    /// <remarks>
    /// <b>为什么这条判断只该有一份</b>：只看 <see cref="IsCompleted"/> 会把
    /// "已结束、等你去取结果"报成"还在跑"，于是界面一直显示一个已经结束的
    /// 任务在转圈。每个宿主各写一遍这个三元判断，就会各自错一次。
    /// </remarks>
    public SubagentStatus CurrentStatus => Result?.Status
        ?? (IsCompleted
            // 结束了却没有结果：被取消或异常退出，引擎没留下 SubagentResult。
            ? SubagentStatus.Cancelled
            : SubagentStatus.Running);

    /// <summary>是否仍在运行。</summary>
    public bool IsRunning => !IsCompleted;

    /// <summary>最终结果；未完成时为 null。</summary>
    public SubagentResult? Result { get; internal set; }

    /// <summary>
    /// 状态变化通知。**子 agent 结束（无论成败）时触发一次，且只触发一次。**
    /// </summary>
    /// <remarks>
    /// <b>宿主靠它做事件驱动，而不是轮询。</b>没有这个通知源时，界面只能定时
    /// 调 <c>subagent_list</c> 才知道"跑完了"——延迟等于轮询间隔，
    /// 而子 agent 可能跑几分钟，这期间的每次轮询都是无意义的往返。
    ///
    /// <para>
    /// <b>它只报"结束了"，不报进度</b>：子 agent 的意义是中间过程留在
    /// 子上下文里。逐工具回调会把那些过程漏回主侧，正好抵消隔离。
    /// </para>
    ///
    /// <para>
    /// <b>必须保证只触发一次</b>：重复触发会让界面收到两条"已完成"，
    /// 而消费方通常按"收到即刷新"处理——那会多刷一次，更糟的是
    /// 如果它在回调里取结果，第二条就取不到了（结果已取走）。
    /// </para>
    /// </remarks>
    public event Action<SubagentHandle>? Changed;

    /// <summary>
    /// 记录子 agent 已结束及其结果。**由运行它的那一侧调用**（引擎内部或宿主）。
    /// </summary>
    /// <remarks>
    /// <b>它同时置结果与完成信号，不让调用方分两步做</b>：分两步时很容易
    /// 只做一半——只置结果不置完成信号，<c>IsCompleted</c> 就一直是假，
    /// 界面显示"还在跑"而其实早结束了。这个组合是个不可分的整体，
    /// 所以收进一个方法里。
    ///
    /// <para>
    /// <b>重复调用是安全的，但只通知一次</b>：子 agent 的收尾路径有多条
    /// （正常结束、异常、取消），它们可能在极端时序下都走到这里。
    /// 用 <c>_completionNotified</c> 挡住第二次通知，而不是挡住第二次赋值
    /// ——后者会让结果丢失。
    /// </para>
    /// </remarks>
    /// <param name="result">最终结果。</param>
    public void Complete(SubagentResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        Result = result;
        Completion = Task.CompletedTask;

        RaiseChangedOnce();
    }

    /// <summary>通知一次状态变化；重复调用无副作用。</summary>
    /// <remarks>
    /// <b>回调异常必须吞掉</b>：通知方（通常是 UI 或协议层）抛异常不能
    /// 反过来把子 agent 的收尾搞坏——那会让"跑完了"变成一个失败。
    /// 诊断信息不够就丢掉，不能污染控制流。
    ///
    /// <para>
    /// <b>用 <see cref="Interlocked.Exchange(ref int, int)"/> 而不是
    /// <c>if (flag) { flag = true; }</c></b>：后者是非原子的 check-then-set，
    /// 两个线程同时走到这里会都通过检查、都推送——而这正是本属性
    /// 声称要防的事。概率低不等于不存在，而"只推一次"是契约级承诺。
    /// </para>
    /// </remarks>
    internal void RaiseChangedOnce()
    {
        if (Interlocked.Exchange(ref _completionNotified, 1) != 0) return;

        try { Changed?.Invoke(this); }
        catch (Exception) { /* 订阅方的问题不该影响子 agent 的收尾。 */ }
    }

    /// <summary>是否已经通知过"结束了"。防止重复推送。</summary>
    private int _completionNotified;

    /// <summary>
    /// 完成信号（无论成败都会完成）。
    /// </summary>
    /// <remarks>
    /// <b>初值必须是"永不完成"，不能是 <see cref="Task.CompletedTask"/></b>：
    /// 后者会让一个**刚创建、还没交给运行器**的句柄立刻被判成已结束，
    /// 于是宿主把它报成"已取消"。第一版就是那个初值，只是当时没有人
    /// 用它判断"是否在运行"，所以这个洞一直没暴露。
    /// <para>
    /// 用 <see cref="TaskCompletionSource"/> 的 Task：它只在
    /// <see cref="Complete"/> 或运行器赋新值时完成。
    /// </para>
    /// </remarks>
    internal Task Completion { get; set; } =
        new TaskCompletionSource().Task;

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
/// <remarks>
/// <b><see cref="Running"/> 是后加的，且必须排在末尾</b>：枚举值是公开契约的一部分
/// ——JSON 按名字序列化，但有人会把它们存进配置文件、数据库，或按序号比较。
/// 插在开头会让已有三个值全部后移一位（<c>Completed</c> 从 0 变 1……），
/// 那种错位在日志里就是一个词对不上，而查起来毫无线索。
/// <para>
/// 语义上"Running 排在 Completed 前面"更顺，但**契约兼容优先于阅读顺序**：
/// 名字都写在那儿，读者不需要靠位置理解含义。ApiCompat 的
/// <c>CP0011</c> 正是为这条设的。
/// </para>
/// </remarks>
public enum SubagentStatus
{
    /// <summary>正常结束。</summary>
    Completed,

    /// <summary>失败（模型错误、工具反复失败等）。</summary>
    Failed,

    /// <summary>被取消。</summary>
    Cancelled,

    /// <summary>仍在运行。后加，故排在末尾。</summary>
    Running,
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
            // **必须走 Complete，不能只赋 Result**：那条语句原先只写 Result，
            // 而 IsCompleted 读的是 Completion —— 由 Task.Run 的那条任务自己
            // 结束，所以两者恰好同时成立。但调用方若看到"Result 有了、
            // IsCompleted 还是假"就会误判成还在跑。把两件事合成一步。
            handle.Complete(new SubagentResult(
                handle.Id, status, text.ToString().Trim(), diagnostic, toolRounds));

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
