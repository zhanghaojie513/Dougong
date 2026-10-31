using System.Diagnostics;
using System.Text.Json;

namespace Dougong.Agent;

/// <summary>
/// <see cref="IToolDispatcher"/> 的首版实现。它是唯一执行工具的地方：
/// 校验参数、去重调用 ID、采集权限描述、请求审批、协调执行权、复核后执行并记录终态。
/// </summary>
/// <remarks>
/// 能力边界（与 docs/m1-candidate-comparison.md 一致）：
/// 资源协调只有两级——全局串行 + 按工作区独占，尚无资源级读写键与跨工作区仲裁。
/// schema 校验只覆盖顶层 type 与 required，不是完整校验器。
/// </remarks>
public sealed class ToolDispatcher : IToolDispatcher
{
    private readonly object _sync = new();
    // 存的是"怎么取审批器"，不是审批器本身。
    private readonly IApproverAccessor? _approverAccessor;
    private readonly ToolResourceGate? _gate;
    private readonly string _sessionId;
    private readonly string _turnId;
    private readonly string _workspace;
    private readonly TimeSpan _approvalValidity;
    private readonly WorkspacePathGuard? _pathGuard;

    /// <summary>
    /// 建立 dispatcher。审批器、资源闸门与工作区都可省略：
    /// 省略即表示"无审批要求"，仅用于测试与对比；生产宿主必须显式提供审批器。
    /// </summary>
    public ToolDispatcher(
        IToolApprover? approver = null,
        ToolResourceGate? gate = null,
        string sessionId = "session",
        string turnId = "turn",
        string workspace = ".",
        TimeSpan? approvalValidity = null,
        WorkspacePathGuard? pathGuard = null,
        IAgentStore? store = null)
        : this(approver is null ? null : new FixedApproverAccessor(approver),
              gate, sessionId, turnId, workspace, approvalValidity, pathGuard,
              store is null ? null : new FixedStoreAccessor(store))
    {
    }

    /// <summary>
    /// 用访问点建立 dispatcher。<b>推荐用法</b>：访问点可以每次取到当前有效的存储，
    /// 而裸实例会被固定下来——替换存储后旧实例仍会被使用。
    /// </summary>
    public ToolDispatcher(
        IApproverAccessor? approverAccessor,
        ToolResourceGate? gate,
        string sessionId,
        string turnId,
        string workspace,
        TimeSpan? approvalValidity,
        WorkspacePathGuard? pathGuard,
        IStoreAccessor? storeAccessor)
    {
        _approverAccessor = approverAccessor;
        _gate = gate;
        _sessionId = sessionId;
        _turnId = turnId;
        _workspace = workspace;
        _approvalValidity = approvalValidity ?? TimeSpan.FromMinutes(5);
        _pathGuard = pathGuard;
        _storeAccessor = storeAccessor;
    }

    // 存的是"怎么取存储"，不是存储本身。裸实例在这里不可表达，
    // 因此替换之后不会出现"dispatcher 还拿着旧 store"的路径。
    private readonly IStoreAccessor? _storeAccessor;

    /// <summary>
    /// 超时后等工具真正停下的最长时间。
    /// </summary>
    /// <remarks>
    /// <b>为什么需要这个上限</b>：超时后必须等工具停下——一个还在写文件的
    /// 工具在我们报告"超时"之后继续改磁盘，是最坏的组合。
    /// 但如果工具完全不响应取消，无限等待就等于"超时机制自己挂住了整轮"，
    /// 而那正是本项要解决的问题。
    /// <para>
    /// 2 秒：响应取消的工具远快于此；不响应的工具也不该让我们等太久。
    /// 超过就如实报告"无法确认它是否还在运行"，而不是谎称"已中止"
    /// ——后者意味着副作用没发生，而那是假的。
    /// </para>
    /// </remarks>
    private static readonly TimeSpan GraceCap = TimeSpan.FromSeconds(2);

    /// <summary>
    /// 没有逐工具声明时使用的执行上限。
    /// </summary>
    /// <remarks>
    /// <b>默认 10 分钟</b>：这是"明显卡死"的保守界线。它不该被当作
    /// 细粒度控制——真正的区分靠 <see cref="ToolExecutionPlan.Timeout"/>，
    /// 这里只保证"绝不会无限挂住"。
    /// <para>
    /// 用 <see cref="TimeSpan.Zero"/> 表示不限制（有意的逃生口）：
    /// 某些宿主的工具就是要跑很久（批量迁移、大模型推理），
    /// 而"被误杀"比"挂住"更难排查——误杀会静默丢结果。
    /// </para>
    /// <para>
    /// 用属性而不是构造参数：加参数会改公开签名（ApiCompat CP0002），
    /// 而那会让所有现有调用点与插件被迫重新编译。
    /// </para>
    /// </remarks>
    public TimeSpan DefaultToolTimeout { get; init; } = TimeSpan.FromMinutes(10);

    private readonly Dictionary<string, Registration> _tools = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Registration> _aliases = new(StringComparer.Ordinal);
    private readonly HashSet<string> _seenInvocations = new(StringComparer.Ordinal);

    /// <summary>
    /// 记录所有实际进入 dispatcher 的调用 ID，供旁路检测使用。
    /// 绕过本类直接执行工具的调用不会出现在这里。
    /// </summary>
    public IReadOnlyCollection<string> DispatchedInvocations
    {
        get { lock (_sync) { return [.. _seenInvocations]; } }
    }

    public IReadOnlyList<ToolDescriptor> Tools
    {
        get { lock (_sync) { return [.. _tools.Values.Select(r => r.Descriptor)]; } }
    }

    /// <summary>
    /// 把已注册工具的冻结描述符投影为模型可见的工具声明。
    /// 这是唯一合法的"告知模型有哪些工具"的通道：schema 由宿主冻结，
    /// 执行仍必须回到 <see cref="DispatchAsync"/>，不存在模型侧直接执行路径。
    /// </summary>
    public IReadOnlyList<ToolDescriptor> ModelVisibleTools => Tools;

    public IDisposable Register(IAgentTool tool)
    {
        ArgumentNullException.ThrowIfNull(tool);
        var descriptor = tool.Descriptor
            ?? throw new InvalidOperationException("工具描述符为 null。");
        if (string.IsNullOrWhiteSpace(descriptor.Id))
            throw new InvalidOperationException("工具 Id 不能为空。");
        if (descriptor.InputSchema.ValueKind is not JsonValueKind.Object)
            throw new InvalidOperationException(
                $"工具 '{descriptor.Id}' 的 InputSchema 必须是 JSON 对象。");

        // 冻结：复制 schema，避免注册后外部改动影响已发布给模型的描述。
        var frozen = descriptor with { InputSchema = descriptor.InputSchema.Clone() };
        var registration = new Registration(tool, frozen);
        lock (_sync)
        {
            if (!_tools.TryAdd(descriptor.Id, registration))
                throw new InvalidOperationException($"工具 '{descriptor.Id}' 已注册。");

            // 模型只看得见 Name（见 ToolDeclarations），回传的调用里自然也是 Name。
            // 因此 Name 必须也能路由到同一个注册项，否则"工具已注册却被判未注册"。
            // Id 与 Name 相同时不额外占用键；不同名时保留 Id 优先。
            if (!string.Equals(descriptor.Name, descriptor.Id, StringComparison.Ordinal)
                && !_aliases.TryAdd(descriptor.Name, registration))
            {
                _tools.Remove(descriptor.Id);
                throw new InvalidOperationException(
                    $"工具名 '{descriptor.Name}' 与其他工具冲突（Id 为 '{descriptor.Id}'）。");
            }
        }

        return new RegistrationHandle(this, descriptor.Id, descriptor.Name);
    }

    public async ValueTask<ToolDispatchResult> DispatchAsync(
        ToolInvocation invocation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(invocation);

        if (string.IsNullOrWhiteSpace(invocation.InvocationId))
            return Reject(invocation, ToolCallOutcome.RejectedDuplicateInvocation,
                "调用 ID 为空，无法与模型响应中的调用对应。");

        Registration? registration;
        lock (_sync)
        {
            if (!_seenInvocations.Add(invocation.InvocationId))
                return Reject(invocation, ToolCallOutcome.RejectedDuplicateInvocation,
                    $"调用 ID '{invocation.InvocationId}' 重复。");
            // 先按 Id 找（宿主内部派发），再按 Name 找（模型回传只看得到 Name）。
            if (!_tools.TryGetValue(invocation.ToolId, out registration))
                _aliases.TryGetValue(invocation.ToolId, out registration);
        }

        if (registration is null)
            return Reject(invocation, ToolCallOutcome.RejectedUnknownTool,
                $"工具 '{invocation.ToolId}' 未注册。");

        if (cancellationToken.IsCancellationRequested)
            return Reject(invocation, ToolCallOutcome.CancelledBeforeExecution, "执行前已取消。");

        if (invocation.Arguments.ValueKind is JsonValueKind.Undefined)
            return Reject(invocation, ToolCallOutcome.RejectedInvalidArguments, "调用参数缺失。");

        var validation = ValidateArguments(registration.Descriptor.InputSchema, invocation.Arguments);
        if (validation is not null)
            return Reject(invocation, ToolCallOutcome.RejectedInvalidArguments, validation);

        ToolPermission permission;
        ToolExecutionPlan execution;
        FileVersionSet files;
        try
        {
            permission = registration.Tool.DescribePermission(invocation.Arguments);
            execution = registration.Tool.DescribeExecution(invocation.Arguments);
            files = CaptureFileVersions(registration, invocation);
        }
        catch (Exception ex)
        {
            return new ToolDispatchResult(invocation.InvocationId, invocation.ToolId,
                ToolCallOutcome.Failed, Diagnostic: $"权限/执行计划/文件依赖描述失败：{ex.Message}");
        }

        // 阶段一：预检（不持锁）。审批绑定必须基于规范化参数与本次权限描述。
        var binding = ArgumentCanonicalizer.Bind(
            _sessionId, _turnId, invocation.InvocationId,
            registration.Descriptor, invocation.Arguments, permission,
            _workspace, _approvalValidity) with
        {
            FileVersions = files.ToBindingValue(),
        };

        // 审批段的耗时。没配审批器时保持 null——"没等审批"与"审批瞬间通过"
        // 是两件事，用 0 冒充会让统计里混进一批假的"审批很快"。
        TimeSpan? approvalElapsed = null;

        if (_approverAccessor is { IsConfigured: true })
        {
            // 等待审批期间不持任何锁：慢审批不应阻塞整个工作区。
            //
            // 计时只包住审批调用本身，不含前面的权限描述与绑定计算：
            // 那部分是我们的开销，混进"审批等待"会让用户看到的时间不真实
            // （用户关心的是"我等了多久"，不是"我们的准备花了多久"）。
            var approvalStarted = Stopwatch.GetTimestamp();
            var decision = await _approverAccessor.RequestApprovalAsync(
                new ToolApprovalRequest(
                    _sessionId, _turnId, invocation.InvocationId,
                    registration.Descriptor, invocation.Arguments, permission,
                    execution, _workspace, binding),
                cancellationToken).ConfigureAwait(false);
            approvalElapsed = Stopwatch.GetElapsedTime(approvalStarted);

            // 审批决定必须落盘后再继续：否则"已批准"这件事没有被持久化，
            // 而副作用却发生了，恢复时无法对账。
            if (await CommitApprovalAsync(
                    registration, invocation, decision, binding, cancellationToken)
                    .ConfigureAwait(false) is { } approvalFailure)
            {
                return new ToolDispatchResult(invocation.InvocationId, invocation.ToolId,
                    ToolCallOutcome.Failed,
                    Diagnostic: $"审批记录提交失败，未执行：{approvalFailure}",
                    Permission: permission, ApprovalElapsed: approvalElapsed);
            }

            if (!decision.Approved)
                return new ToolDispatchResult(invocation.InvocationId, invocation.ToolId,
                    ToolCallOutcome.RejectedNotApproved,
                    Diagnostic: decision.Reason ?? "审批未通过。",
                    Permission: permission, ApprovalElapsed: approvalElapsed);
        }

        // 阶段二：进锁后复核（防"批准 A、执行 B"）。
        // 等待执行权时被取消属于"尚未执行"，必须返回终态而不是把取消异常抛给调用方。
        IDisposable? lease;
        try
        {
            lease = _gate is null
                ? null
                : await _gate.EnterAsync(execution, _workspace, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new ToolDispatchResult(invocation.InvocationId, invocation.ToolId,
                ToolCallOutcome.CancelledBeforeExecution,
                Diagnostic: "等待执行权时被取消，未执行。", Permission: permission,
                ApprovalElapsed: approvalElapsed);
        }
        catch (ObjectDisposedException ex)
        {
            // 闸门已被宿主释放：不执行，但不能把异常抛给调用方而丢掉这条调用的终态。
            return new ToolDispatchResult(invocation.InvocationId, invocation.ToolId,
                ToolCallOutcome.Failed,
                Diagnostic: $"资源闸门已释放，未执行：{ex.ObjectName}", Permission: permission,
                ApprovalElapsed: approvalElapsed);
        }

        using (lease)
        {
            // 复核的第一件事：这个注册是否仍是当前生效的那一个。
            // 审批窗口内工具可能被注销并替换（MCP 的 list_changed 随时会到），
            // 此时手中的 registration 是过期对象——继续用它会"批准了 A、执行了 B"。
            if (!IsCurrentRegistration(registration, invocation.ToolId))
                return new ToolDispatchResult(invocation.InvocationId, invocation.ToolId,
                    ToolCallOutcome.RejectedNotApproved,
                    Diagnostic: "执行前复核不通过：该工具在审批期间已被注销或替换，请重新审批。",
                    Permission: permission, ApprovalElapsed: approvalElapsed);

            if (!approvalIsStillValid(registration, invocation, permission, execution, binding, out var staleReason))
                return new ToolDispatchResult(invocation.InvocationId, invocation.ToolId,
                    ToolCallOutcome.RejectedNotApproved,
                    Diagnostic: staleReason, Permission: permission,
                    ApprovalElapsed: approvalElapsed);

            // 执行段的耗时。只包住工具自己的 ExecuteAsync：
            // 前面的复核与闸门获取是我们的开销。
            var executionStarted = Stopwatch.GetTimestamp();
            try
            {
                var outcome = await ExecuteWithTimeoutAsync(
                    registration.Tool, invocation, execution, cancellationToken)
                    .ConfigureAwait(false);
                var executionElapsed = Stopwatch.GetElapsedTime(executionStarted);

                // 超时：终态在那一层就定了，这里只补耗时与权限。
                if (outcome.TimedOut)
                    return new ToolDispatchResult(invocation.InvocationId, invocation.ToolId,
                        ToolCallOutcome.TimedOut, Diagnostic: outcome.Diagnostic,
                        Permission: permission, ApprovalElapsed: approvalElapsed,
                        ExecutionElapsed: executionElapsed);

                // 取消是协作式的：工具若忽略取消并完成，就必须如实报成功。
                // 伪装成"已撤销"会让上层误判副作用没发生。
                return cancellationToken.IsCancellationRequested || outcome.CompletedDespiteCancel
                    ? new ToolDispatchResult(invocation.InvocationId, invocation.ToolId,
                        ToolCallOutcome.Succeeded, outcome.Result,
                        Diagnostic: "取消已请求，但工具未响应取消并完成了执行；副作用可能已发生。",
                        Permission: permission, ApprovalElapsed: approvalElapsed,
                        ExecutionElapsed: executionElapsed)
                    : new ToolDispatchResult(invocation.InvocationId, invocation.ToolId,
                        ToolCallOutcome.Succeeded, outcome.Result, Permission: permission,
                        ApprovalElapsed: approvalElapsed, ExecutionElapsed: executionElapsed);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return new ToolDispatchResult(invocation.InvocationId, invocation.ToolId,
                    ToolCallOutcome.CancelledDuringExecution, Diagnostic: "执行中被取消。",
                    Permission: permission, ApprovalElapsed: approvalElapsed,
                    ExecutionElapsed: Stopwatch.GetElapsedTime(executionStarted));
            }
            catch (Exception ex)
            {
                return new ToolDispatchResult(invocation.InvocationId, invocation.ToolId,
                    ToolCallOutcome.Failed, Diagnostic: $"{ex.GetType().Name}: {ex.Message}",
                    Permission: permission, ApprovalElapsed: approvalElapsed,
                    ExecutionElapsed: Stopwatch.GetElapsedTime(executionStarted));
            }
        }
    }

    /// <summary>执行工具，带策略上限。</summary>
    /// <remarks>
    /// <b>为什么必须在 dispatcher 层做，而不是让每个工具自己超时</b>：
    /// 工具可以来自第三方插件或 MCP 远端，它们的实现不受我们控制。
    /// 只在自家工具里设超时，等于"我们的工具不会挂住，别人的会"——
    /// 而挂住的是整轮对话，用户看到的是"AI 卡死了"。
    /// <para>
    /// <b>超时后必须等工具真的停下</b>，不能 fire-and-forget：那会让一个
    /// 还在写文件的工具在我们已经报告"超时"之后继续改磁盘。
    /// 做法是触发取消后继续 await 原始任务——工具若响应取消就很快返回，
    /// 若忽略取消则会一直跑到自己结束，那时我们才带着真实结果返回。
    /// </para>
    /// <para>
    /// <b>无限等待的风险是真实的</b>：一个完全不响应取消的工具会让这里永远等下去。
    /// 但那是有意的选择——报告"超时"却让它在后台继续改文件更糟。
    /// 这种情况需要宿主在更低层（进程级）解决，见 doc 里的沙箱条目。
    /// </para>
    /// </remarks>
    private async ValueTask<ExecutionOutcome> ExecuteWithTimeoutAsync(
        IAgentTool tool, ToolInvocation invocation, ToolExecutionPlan plan,
        CancellationToken cancellationToken)
    {
        var limit = plan.Timeout ?? DefaultToolTimeout;

        // 没设上限或上限非正:直接执行。负值/零当"不限"处理而不是"立即超时"——
        // 后者会让一个笔误把整个工具集变成不可用。
        if (limit <= TimeSpan.Zero)
        {
            var unbounded = await tool.ExecuteAsync(invocation, cancellationToken)
                .ConfigureAwait(false);
            return new ExecutionOutcome(unbounded, TimedOut: false, Diagnostic: null);
        }

        // 串起来：外层取消（用户按 Esc）与超时共用一条取消通道。
        // 分开两条的话，工具需要同时观察两个 token，而 IAgentTool 的签名只收一个。
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        // CancelAfter 用 TimeSpan 而不是 int 毫秒：TimeSpan 表达更清楚，
        // 也不会因为超过 int 上限而静默溢出成负值（那会变成"永不过期"）。
        cts.CancelAfter(limit);

        var work = tool.ExecuteAsync(invocation, cts.Token).AsTask();

        // **超时判定不能等工具抛异常**。
        //
        // 第一版写的是 `try { await work } catch (OperationCanceledException) { ... }`，
        // 依赖工具在收到取消时抛 OCE。但那只有"响应取消的工具"才成立——
        // 一个忽略 token 的工具（比如内部用未传 token 的 Task.Delay）会安然跑完，
        // 于是 catch 永远不触发，超时形同不存在。实测：20 秒的工具，
        // 100 毫秒上限，等了整整 20 秒才返回。
        //
        // 正确做法是**独立的等待上限**：WaitAsync 到点就抛 TimeoutException，
        // 与工具是否响应取消无关。
        Task<AgentToolResult> pending = work;
        try
        {
            var result = await pending.WaitAsync(limit, cancellationToken).ConfigureAwait(false);
            return new ExecutionOutcome(result, TimedOut: false, Diagnostic: null);
        }
        catch (TimeoutException)
        {
            // 到这儿是**真的超时**：限额内没完成。
            // 下面的等待与上面的超时分开：宽限期只用来确认工具停下。
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // 外层取消（用户按 Esc）。**但这不等于工具没跑完**：
            // 一个忽略 token 的工具会继续进行到底，甚至已经成功了。
            //
            // 原实现直接 throw，把那种情况报成 `CancelledDuringExecution`，
            // 意思是"副作用可能没发生"——而它其实成功了。这会误导宿主
            // 去清理一个已经写好的结果。`failure` 套的
            // `uncooperative-cancel-still-holds-lock` 抓到了这个错误。
            //
            // 所以这里先请求取消，再给一点时间看它是否真的完成：
            // 完成了就如实报成功（带"未响应取消"的说明）。
            cts.Cancel();
            var completed = await TryAwaitStopAsync(pending).ConfigureAwait(false);
            if (completed is not null)
            {
                return new ExecutionOutcome(completed, TimedOut: false,
                    Diagnostic: "取消已请求，但工具未响应取消并完成了执行；副作用可能已发生。",
                    CompletedDespiteCancel: true);
            }

            throw;
        }
        catch (OperationCanceledException)
        {
            // **工具因超时取消而抛了 OCE**——这是最常见的正常路径，
            // 必须归到超时，而不是让它冒到最外层变成 Failed。
            //
            // 时序：`cts.CancelAfter(limit)` 到点 → 工具响应取消抛 OCE →
            // `WaitAsync` 把这个 OCE 传播出来，而它的 `TimeoutException`
            // 还没发生（两者几乎同时，谁先不确定）。若不在这里接住，
            // 表现是"超时被报成 Failed: TaskCanceledException"——
            // `hanging-tool-times-out` 偶发失败就是这个。
            //
            // 落到这里说明不是外层取消（上面那个 catch 有守卫），
            // 所以是 cts 触发的，即超时。
            return await AfterTimeoutAsync(pending, limit).ConfigureAwait(false);
        }

        // 超时了：请求工具停下，然后**有限度地**等它真的停。
        return await AfterTimeoutAsync(pending, limit).ConfigureAwait(false);
    }

    /// <summary>超时已确定：请求工具停下，再有限度地等它真的停。</summary>
    /// <remarks>
    /// 与上面的判定分开成方法，因为两条入口都要走它：
    /// 一是 <see cref="TimeoutException"/>（等待先到点），
    /// 二是工具自己抛的 <see cref="OperationCanceledException"/>（取消先到点）。
    /// 两者都意味着"超时了"，后续处理完全相同。
    /// </remarks>
    private async ValueTask<ExecutionOutcome> AfterTimeoutAsync(
        Task<AgentToolResult> pending, TimeSpan limit)
    {
        try
        {
            await pending.WaitAsync(GraceCap).ConfigureAwait(false);

            return new ExecutionOutcome(null, TimedOut: true,
                Diagnostic: $"工具超过 {FormatLimit(limit)} 未返回，已请求中止；"
                    + "但它在取消后仍然完成了执行，副作用可能已发生。");
        }
        catch (TimeoutException)
        {
            // 宽限期内也没停：**不谎称「已中止」**，那意味着副作用没发生。
            // 如实说"无法确认"——宿主据此才能决定要不要人工核对。
            return new ExecutionOutcome(null, TimedOut: true,
                Diagnostic: $"工具超过 {FormatLimit(limit)} 未返回；"
                    + $"已请求中止，但在随后 {FormatLimit(GraceCap)} 内仍未停止。"
                    + "**无法确认它是否还在运行**，副作用可能仍在发生。");
        }
        catch (OperationCanceledException)
        {
            return new ExecutionOutcome(null, TimedOut: true,
                Diagnostic: $"工具超过 {FormatLimit(limit)} 未返回，已中止。"
                    + "工具已启动过，副作用可能已部分发生。");
        }
        catch (Exception ex)
        {
            return new ExecutionOutcome(null, TimedOut: true,
                Diagnostic: $"工具超过 {FormatLimit(limit)} 未返回，中止时抛出 "
                    + $"{ex.GetType().Name}: {ex.Message}。副作用可能已部分发生。");
        }
    }

    /// <summary>
    /// 请求取消后有限度地等工具停下；若它完成了就返回结果。
    /// </summary>
    /// <remarks>
    /// <b>为什么返回结果而不只是"等"</b>：一个忽略取消的工具会继续跑到成功。
    /// 那时报"被取消"是错的——副作用已经发生，而且结果是好的。
    /// 宿主据此会去清理一个其实已经完成的操作。
    /// <para>
    /// 返回 null 表示"宽限期内没完成或抛了异常"，由调用方按取消处理。
    /// 不抛异常：调用方已经把终态定为取消，这里的异常不该覆盖那条信息。
    /// </para>
    /// </remarks>
    private static async ValueTask<AgentToolResult?> TryAwaitStopAsync(Task<AgentToolResult> pending)
    {
        try
        {
            return await pending.WaitAsync(GraceCap).ConfigureAwait(false);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>把上限格式化成用户能读的文本。</summary>
    /// <remarks>
    /// 不用 <c>ToString()</c>：它按当前文化输出，中文环境下可能变成
    /// "00:02:00" 之外的形态。诊断文本要能跨机器对照，所以固定短语。
    /// </remarks>
    private static string FormatLimit(TimeSpan limit) => limit.TotalSeconds >= 1
        ? $"{limit.TotalSeconds:0.###} 秒"
        : $"{limit.TotalMilliseconds:0} 毫秒";

    /// <summary>执行结果，附带是否因超时中断。</summary>
    private readonly record struct ExecutionOutcome(
        AgentToolResult? Result, bool TimedOut, string? Diagnostic,
        bool CompletedDespiteCancel = false);

    /// <summary>
    /// 执行前复核：重新描述权限与执行计划，重算绑定，比对全部维度。
    /// 任一项变化即拒绝，要求重新审批。
    /// </summary>
    /// <summary>
    /// 捕获工具声明的文件依赖版本。路径必须先经工作区守卫解析，
    /// 越界直接抛错——宁可拒绝调用，也不对越界路径做指纹。
    /// </summary>
    private FileVersionSet CaptureFileVersions(Registration registration, ToolInvocation invocation)
    {
        var declared = registration.Tool.DescribeFileDependencies(invocation.Arguments);
        if (declared.Count == 0) return FileVersionSet.Empty;
        if (_pathGuard is null)
            throw new InvalidOperationException(
                "工具声明了文件依赖，但 dispatcher 未配置工作区路径守卫。");

        var resolved = new List<string>(declared.Count);
        foreach (var path in declared)
        {
            if (!_pathGuard.TryResolve(path, out var absolute, out var why))
                throw new UnauthorizedAccessException($"文件依赖路径被拒绝：{path}（{why}）");
            resolved.Add(absolute);
        }

        return FileVersionSet.Capture(resolved);
    }

    private bool approvalIsStillValid(
        Registration registration, ToolInvocation invocation, ToolPermission approvedPermission,
        ToolExecutionPlan approvedExecution, ApprovalBinding approvedBinding, out string reason)
    {
        reason = string.Empty;

        if (!approvedBinding.IsUnexpired(DateTimeOffset.UtcNow))
        {
            reason = "审批已过期。";
            return false;
        }

        ToolPermission currentPermission;
        ToolExecutionPlan currentExecution;
        try
        {
            currentPermission = registration.Tool.DescribePermission(invocation.Arguments);
            currentExecution = registration.Tool.DescribeExecution(invocation.Arguments);
        }
        catch (Exception ex)
        {
            reason = $"复核时无法重新描述权限：{ex.Message}";
            return false;
        }

        // 只比较"批准了什么"，时刻由 IsUnexpired 单独判断。
        FileVersionSet currentFiles;
        try
        {
            currentFiles = CaptureFileVersions(registration, invocation);
        }
        catch (Exception ex)
        {
            reason = $"复核时无法重新捕获文件版本：{ex.Message}";
            return false;
        }

        var current = approvedBinding with
        {
            ArgumentsDigest = ArgumentCanonicalizer.Digest(invocation.Arguments),
            PermissionScope = ArgumentCanonicalizer.DescribeScope(currentPermission),
            FileVersions = currentFiles.ToBindingValue(),
        };

        if (!approvedBinding.Matches(current))
        {
            reason = "执行前复核不通过：参数、权限或资源计划与批准时不一致。";
            return false;
        }

        if (currentExecution.Concurrency != approvedExecution.Concurrency)
        {
            reason = "执行前复核不通过：并发要求与批准时不一致。";
            return false;
        }

        // 未知权限不能被当作已知放行。
        if (!currentPermission.IsKnown && approvedPermission.IsKnown)
        {
            reason = "执行前复核不通过：权限从已知变为未知。";
            return false;
        }

        return true;
    }

    /// <summary>
    /// 提交一条审批决定。返回 null 表示已提交（或无需提交），非空为失败原因。
    /// </summary>
    /// <remarks>
    /// 记录里带上参数摘要：只记"调用 c1 被批准"不足以证明批准的是**这一次**参数，
    /// 同名工具换参数再跑一次就会共享同一条授权。
    /// </remarks>
    private async ValueTask<string?> CommitApprovalAsync(
        Registration registration,
        ToolInvocation invocation,
        ToolApprovalDecision decision,
        ApprovalBinding binding,
        CancellationToken cancellationToken)
    {
        if (_storeAccessor is null) return null;

        var record = new ApprovalRecord(
            invocation.InvocationId,
            registration.Descriptor.Id,
            registration.Descriptor.Version,
            decision.Approved,
            decision.Reason,
            binding.ArgumentsDigest,
            _workspace,
            DateTimeOffset.UtcNow);

        try
        {
            await _storeAccessor.CommitAsync(
                new StoreRecord(0, StoreRecordKind.ApprovalDecision, record.ToJson()),
                cancellationToken).ConfigureAwait(false);
            return null;
        }
        catch (Exception ex)
        {
            return $"{ex.GetType().Name}: {ex.Message}";
        }
    }

    /// <summary>
    /// 该注册是否仍是 dispatcher 中"当前生效"的那一个。
    /// </summary>
    /// <remarks>
    /// 只比较引用是否相同：同名工具被重新注册后是**另一个** Registration 对象，
    /// 即使 schema 未变也必须重新审批——否则无法证明用户批准的是当前这一份实现。
    /// </remarks>
    private bool IsCurrentRegistration(Registration registration, string toolId)
    {
        lock (_sync)
        {
            if (_tools.TryGetValue(toolId, out var current) && ReferenceEquals(current, registration))
                return true;
            return _aliases.TryGetValue(toolId, out var alias) && ReferenceEquals(alias, registration);
        }
    }

    private static ToolDispatchResult Reject(
        ToolInvocation invocation, ToolCallOutcome outcome, string diagnostic) =>
        new(invocation.InvocationId, invocation.ToolId, outcome, Diagnostic: diagnostic);

    /// <summary>
    /// 按 schema 校验参数。委托给 <see cref="ToolSchemaValidator"/>：
    /// 校验范围与"不支持的关键字必须报错"的立场都定义在那里。
    /// </summary>
    /// <remarks>
    /// 曾经只检查顶层 type 与 required。M2 接入 MCP 之后不再够用——
    /// schema 来自不可信远端，只查 required 意味着它声明的类型约束形同虚设。
    /// </remarks>
    private static string? ValidateArguments(JsonElement schema, JsonElement arguments) =>
        ToolSchemaValidator.Validate(schema, arguments);

    private sealed record Registration(IAgentTool Tool, ToolDescriptor Descriptor);

    private sealed class RegistrationHandle(ToolDispatcher owner, string toolId, string toolName) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            lock (owner._sync)
            {
                owner._tools.Remove(toolId);
                if (!string.Equals(toolName, toolId, StringComparison.Ordinal))
                    owner._aliases.Remove(toolName);
            }
        }
    }
}
