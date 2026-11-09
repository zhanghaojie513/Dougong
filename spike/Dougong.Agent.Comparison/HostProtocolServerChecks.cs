using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dougong.Agent;
using Dougong.HostProtocol;
using Dougong.Hosting;

namespace Dougong.Agent.Comparison;

/// <summary>
/// 宿主协议服务端的端到端验收：真发 JSON 行、真读回来。
/// </summary>
/// <remarks>
/// <b>为什么必须走真实的分帧字节流</b>：对象级往返只能证明"类型能序列化"，
/// 证不了"服务端真的回应了这条命令"。而本项目的核心问题是
/// <b>"契约有了、没人应答"</b>——那正好是对象级测试照不到的地方。
///
/// <para>
/// 每条检查都从 <see cref="MemoryStream"/> 喂进真实 JSON 文本，
/// 再解析输出流里的每一行。这样序列化配置、判别器、RequestId 配对
/// 全都在同一条路径上被验到。
/// </para>
/// </remarks>
public static class HostProtocolServerChecks
{
    public static async Task<int> RunAsync()
    {
        var failures = 0;
        failures += await CheckAsync("permission-presets-are-served", PermissionServedAsync);
        failures += await CheckAsync("permission-switch-changes-state", PermissionSwitchAsync);
        failures += await CheckAsync("permission-unknown-key-is-invalid", PermissionUnknownKeyAsync);
        failures += await CheckAsync("subagent-list-is-served", SubagentListAsync);
        failures += await CheckAsync("subagent-result-is-taken-once", SubagentTakeOnceAsync);
        failures += await CheckAsync("subagent-cancel-is-requested", SubagentCancelAsync);
        failures += await CheckAsync("unsupported-command-is-answered-not-silent", UnsupportedIsAnsweredAsync);
        failures += await CheckAsync("response-carries-request-id", RequestIdPairedAsync);
        failures += await CheckAsync("push-event-refuses-request-id", PushRefusesRequestIdAsync);
        failures += await CheckAsync("malformed-line-does-not-kill-connection", MalformedSurvivesAsync);
        failures += await CheckAsync("missing-capability-is-conflict", MissingCapabilityAsync);
        failures += await CheckAsync("subagent-completion-is-pushed", SubagentPushedAsync);
        failures += await CheckAsync("subagent-push-carries-request-id-null", PushHasNoRequestIdAsync);
        failures += await CheckAsync("subagent-start-is-pushed", SubagentStartPushedAsync);
        failures += await CheckAsync("permission-not-broadcast-by-default", PermissionNoBroadcastAsync);
        failures += await CheckAsync("permission-broadcast-after-watch", PermissionBroadcastAsync);

        // **这两条验的是"真功能 vs 假控件"**：服务端回成功不算数，
        // 要真的影响引擎裁决与列表。
        failures += await CheckAsync("permission-switch-affects-real-approval", PermissionAffectsApprovalAsync);
        failures += await CheckAsync("subagent-list-sees-real-registry", SubagentListSeesRealRegistryAsync);

        Console.WriteLine(failures == 0
            ? "HOST-PROTOCOL-SERVER: ALL PASS"
            : $"HOST-PROTOCOL-SERVER: {failures} FAILURE(S)");
        return failures;
    }

    // ── 检查 ──────────────────────────────────────────────────

    /// <summary>权限预设查询要回完整状态，且带 RequestId。</summary>
    private static async Task<string?> PermissionServedAsync()
    {
        var (events, _) = await RunAsync(
            new TestProvider(),
            """{"method":"permission/presets","requestId":"req-1"}""");

        var listed = events.OfType<PermissionPresetListedEvent>().FirstOrDefault();
        if (listed is null) return "没有回 permission/listed：" + Describe(events);

        if (listed.RequestId != "req-1")
            return $"RequestId 没配对：{listed.RequestId}";

        if (listed.State.CurrentKey != "interactive")
            return $"当前键不对：{listed.State.CurrentKey}";

        return listed.State.Options.Count > 0
            ? null
            : "选项是空的——界面无法渲染选择器";
    }

    /// <summary>切换之后，回的状态必须是新的那一档。</summary>
    /// <remarks>
    /// <b>这条验的是"真的改了"</b>，不只是"回了事件"：服务端如果把
    /// 请求原样回显（常见于只做形状不做实现），检查会抓到。
    /// </remarks>
    private static async Task<string?> PermissionSwitchAsync()
    {
        var provider = new TestProvider();

        var (events, _) = await RunAsync(provider,
            """{"method":"permission/switch","requestId":"req-1","key":"unattended"}""");

        var listed = events.OfType<PermissionPresetListedEvent>().FirstOrDefault();
        if (listed is null) return "切换没有回状态：" + Describe(events);

        if (listed.State.CurrentKey != "unattended")
            return $"回的状态还是 {listed.State.CurrentKey}——切换没生效";

        // **反查引擎**：不能只看服务端自己说改了什么。
        var engine = provider.Permissions(null)!;
        return engine.Current == ApprovalPolicy.Never
            ? null
            : $"引擎里的策略仍是 {engine.Current}——服务端只回了消息没真改";
    }

    /// <summary>未知预设键必须回 Invalid，不能静默成功。</summary>
    private static async Task<string?> PermissionUnknownKeyAsync()
    {
        var provider = new TestProvider();

        var (events, _) = await RunAsync(provider,
            """{"method":"permission/switch","requestId":"req-1","key":"nope"}""");

        var failed = events.OfType<CommandFailedEvent>().FirstOrDefault();
        if (failed is null) return "未知键没有回失败事件：" + Describe(events);

        if (failed.Reason != CommandFailureReason.Invalid)
            return $"原因应是 Invalid，实际 {failed.Reason}";

        if (failed.RequestId != "req-1") return "失败事件没带 RequestId";

        // 策略必须没变——静默改动比报错更糟。
        var engine = provider.Permissions(null)!;
        return engine.Current == ApprovalPolicy.AskOnRisk
            ? null
            : $"失败后策略被改成了 {engine.Current}";
    }

    /// <summary>子 agent 列表要回状态、启动时刻、发起回合。</summary>
    private static async Task<string?> SubagentListAsync()
    {
        var provider = new TestProvider();
        using var handle = provider.CreateSubagent("sub-1", "查调用链", "turn-1");

        var (events, _) = await RunAsync(provider,
            """{"method":"subagent/list","requestId":"req-1","sessionId":"s1"}""");

        var listed = events.OfType<SubagentsListedEvent>().FirstOrDefault();
        if (listed is null) return "没有回 subagent/listed：" + Describe(events);

        if (listed.Agents.Count != 1) return $"条目数不对：{listed.Agents.Count}";

        var agent = listed.Agents[0];
        if (agent.State != SubagentRunState.Running) return $"状态不对：{agent.State}";
        if (agent.ParentTurnId != "turn-1") return "ParentTurnId 丢了——界面归不到回合";
        if (agent.StartedAt == default) return "StartedAt 丢了——界面算不出时长";

        return null;
    }

    /// <summary>取结果：第一次拿到，第二次拿不到（取走即消失）。</summary>
    /// <remarks>
    /// <b>第二次必须报错而不是回空</b>：回空会让客户端以为"结果还没好"
    /// 而一直轮询，而它永远不会好了——结果已经被取走了。
    /// </remarks>
    private static async Task<string?> SubagentTakeOnceAsync()
    {
        var provider = new TestProvider();
        var handle = provider.CreateSubagent("sub-1", "干活", "turn-1");
        handle.Complete(new SubagentResult(
            "sub-1", SubagentStatus.Completed, "结论正文", null, 3));

        // 第一次：拿到结果。
        var (first, _) = await RunAsync(provider,
            """{"method":"subagent/result","requestId":"req-1","sessionId":"s1","subagentId":"sub-1"}""");

        var got = first.OfType<SubagentsListedEvent>().FirstOrDefault();
        if (got?.Outcome is null) return "第一次没取到结果：" + Describe(first);
        if (got.Outcome.Text != "结论正文") return $"结果正文不对：{got.Outcome.Text}";

        // 第二次：必须明确报错。
        var (second, _) = await RunAsync(provider,
            """{"method":"subagent/result","requestId":"req-2","sessionId":"s1","subagentId":"sub-1"}""");

        // **两种失败表达都算数，静默成功不算**：
        // `subagent/result` 的业务失败走 SubagentsListedEvent.Error
        // （契约里这么定的——客户端仍需拿到列表），系统级失败走 CommandFailedEvent。
        // 真正的缺陷是"回了空 outcome 又不带 error"：客户端会以为还没好，
        // 于是永远轮询一个已经取走的结果。
        var listed = second.OfType<SubagentsListedEvent>().FirstOrDefault();
        var failed = second.OfType<CommandFailedEvent>().FirstOrDefault();

        if (failed is not null) return null;

        if (listed is null)
            return "第二次取同一个结果没有回任何事件：" + Describe(second);

        if (listed.Outcome is not null)
            return "'取走即消失'没做到——第二次仍拿到了结果";

        return string.IsNullOrWhiteSpace(listed.Error)
            ? "第二次取已取走的结果：既无 outcome 也无 error——客户端会以为还没好而一直轮询"
            : null;
    }

    /// <summary>取消子 agent：必须真的调了 Cancel，且回的是当前状态。</summary>
    private static async Task<string?> SubagentCancelAsync()
    {
        var provider = new TestProvider();
        using var handle = provider.CreateSubagent("sub-1", "长任务", "turn-1");

        var (events, _) = await RunAsync(provider,
            """{"method":"subagent/cancel","requestId":"req-1","sessionId":"s1","subagentId":"sub-1"}""");

        var listed = events.OfType<SubagentsListedEvent>().FirstOrDefault();
        if (listed is null) return "取消没有回列表：" + Describe(events);

        return handle.IsCancellationRequested
            ? null
            : "取消命令没有真的取消子 agent——只回了消息";
    }

    /// <summary>
    /// **不支持的命令必须回话，不能沉默。**
    /// </summary>
    /// <remarks>
    /// <b>这是整套检查里最重要的一条</b>：它是"看起来能调、实际不生效"的
    /// 唯一防线。沉默的话客户端只能等到超时，用户永远看不到原因。
    /// </remarks>
    private static async Task<string?> UnsupportedIsAnsweredAsync()
    {
        var (events, _) = await RunAsync(new TestProvider(),
            """{"method":"turn/start","requestId":"req-1","sessionId":"s1","input":"hi"}""");

        var failed = events.OfType<CommandFailedEvent>().FirstOrDefault();
        if (failed is null) return "不支持的命令没有回应：" + Describe(events);

        if (failed.Reason != CommandFailureReason.Unsupported)
            return $"原因应是 Unsupported，实际 {failed.Reason}";

        if (failed.Method != "turn/start")
            return $"Method 应回原始判别器，实际 {failed.Method}";

        // 提示里要写清"当前支持哪些"——用户据此知道能做什么。
        return failed.Message.Contains("permission/presets", StringComparison.Ordinal)
            ? null
            : $"提示里没写支持范围：{failed.Message}";
    }

    /// <summary>每条响应都必须带回对应命令的 RequestId。</summary>
    private static async Task<string?> RequestIdPairedAsync()
    {
        var (events, _) = await RunAsync(new TestProvider(),
            """
            {"method":"permission/presets","requestId":"a"}
            {"method":"subagent/list","requestId":"b","sessionId":"s1"}
            """);

        if (events.Count != 2) return $"应有 2 条响应，实际 {events.Count}：" + Describe(events);

        if (events[0].RequestId != "a") return $"第一条 RequestId 是 {events[0].RequestId}";
        if (events[1].RequestId != "b") return $"第二条 RequestId 是 {events[1].RequestId}";

        // 序号必须严格递增：客户端靠它检测缺口。
        return events[1].Sequence > events[0].Sequence
            ? null
            : $"序号没有递增：{events[0].Sequence} → {events[1].Sequence}";
    }

    /// <summary>推送事件带 RequestId 必须被拒——那会冒充成某条命令的响应。</summary>
    private static async Task<string?> PushRefusesRequestIdAsync()
    {
        var (_, server) = await CreatePairAsync(new TestProvider());

        try
        {
            await server.PushAsync(new SubagentUpdatedEvent
            {
                Sequence = 1,
                RequestId = "pretend-response",
                SessionId = "s1",
                Agent = new SubagentInfo(
                    "x", "y", SubagentRunState.Running, DateTimeOffset.UnixEpoch, null, null),
            });

            return "带 RequestId 的推送事件被放行了——它会冒充成命令响应";
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    /// <summary>坏行不能让连接死掉：后续命令仍要被处理。</summary>
    private static async Task<string?> MalformedSurvivesAsync()
    {
        var (events, _) = await RunAsync(new TestProvider(),
            """
            this is not json
            {"method":"permission/presets","requestId":"req-1"}
            """);

        return events.OfType<PermissionPresetListedEvent>().Any()
            ? null
            : "坏行之后的命令没被处理——连接被一条坏行杀死了：" + Describe(events);
    }

    /// <summary>能力缺失必须回 Conflict，而不是假装成功或沉默。</summary>
    private static async Task<string?> MissingCapabilityAsync()
    {
        var provider = new TestProvider { ProvideSubagents = false };

        var (events, _) = await RunAsync(provider,
            """{"method":"subagent/list","requestId":"req-1","sessionId":"s1"}""");

        var failed = events.OfType<CommandFailedEvent>().FirstOrDefault();
        if (failed is null) return "能力缺失时没有回失败：" + Describe(events);

        return failed.Reason == CommandFailureReason.Conflict
            ? null
            : $"原因应是 Conflict，实际 {failed.Reason}";
    }

    /// <summary>
    /// **子 agent 跑完时会主动推一条 `subagent/updated`——不需要任何轮询。**
    /// </summary>
    /// <remarks>
    /// <b>这是"事件驱动 vs 轮询"的决定性检查。</b>契约里写了
    /// <c>subagent/updated</c>，但服务端此前从未发出过一条——
    /// 那正是"契约有了、没人应答"的同一个形态。这条检查的做法是：
    /// 只发一次 <c>subagent/list</c>（让它订阅），
    /// 之后**不再发任何命令**，只等推送。
    ///
    /// <para>
    /// 收到推送就说明是真的主动推，而不是某条命令的响应。
    /// </para>
    /// </remarks>
    private static async Task<string?> SubagentPushedAsync()
    {
        var provider = new TestProvider();
        await using var live = new LiveConnection(provider);

        // 先订阅（服务端在第一次见到注册表时订阅）。
        live.Send("""{"method":"subagent/list","requestId":"req-1","sessionId":"s1"}""");

        var listed = await live.ReadAsync(TimeSpan.FromSeconds(3));
        if (listed is not SubagentsListedEvent)
            return $"订阅阶段的响应不对：{listed?.GetType().Name ?? "超时"}";

        // 造一个子 agent 并让它结束——**之后不再发任何命令**。
        var handle = provider.CreateSubagent("sub-1", "干活", "turn-1");
        handle.Complete(new SubagentResult(
            "sub-1", SubagentStatus.Completed, "结论", null, 2));

        // **会收到两条**： Started（Running）与 Completed。
        // 启动也推是 2026-11-09 加的——只推完成的话界面要等它跑完
        // 才知道它存在过，而那时它已经开始花额度了。
        SubagentUpdatedEvent? completed = null;

        for (var i = 0; i < 4; i++)
        {
            var pushed = await live.ReadAsync(TimeSpan.FromSeconds(3));
            if (pushed is null) break;

            if (pushed is SubagentUpdatedEvent u
                && u.Agent.State != SubagentRunState.Running)
            {
                completed = u;
                break;
            }
        }

        if (completed is null) return "子 agent 完成了，但没有推送完成状态——只能靠轮询";

        if (completed.Agent.Id != "sub-1") return $"推送的 id 不对：{completed.Agent.Id}";

        return completed.Agent.State == SubagentRunState.Completed
            ? null
            : $"推送的完成状态不对：{completed.Agent.State}";
    }

    /// <summary>推送事件不能带 RequestId——那会冒充成某条命令的响应。</summary>
    /// <remarks>
    /// <b>这条防的是"推送被当成响应"的误判</b>：客户端若按 RequestId 分发，
    /// 一条带 id 的推送会被路由到一个已经不存在的等待者，而真正的
    /// 状态更新则被丢弃。
    /// </remarks>
    private static async Task<string?> PushHasNoRequestIdAsync()
    {
        var provider = new TestProvider();
        await using var live = new LiveConnection(provider);

        live.Send("""{"method":"subagent/list","requestId":"req-1","sessionId":"s1"}""");
        await live.ReadAsync(TimeSpan.FromSeconds(3));

        var handle = provider.CreateSubagent("sub-9", "x", null);
        handle.Complete(new SubagentResult("sub-9", SubagentStatus.Completed, "ok", null, 1));

        var pushed = await live.ReadAsync(TimeSpan.FromSeconds(3));
        if (pushed is null) return "没有收到推送";

        return pushed.RequestId is null
            ? null
            : $"推送带了 RequestId={pushed.RequestId}——它会被误认为某条命令的响应";
    }

    /// <summary>
    /// **子 agent 启动时也要推一条**，不是只在结束时推。
    /// </summary>
    /// <remarks>
    /// <b>只推完成的话，界面要等子 agent 跑完才知道它存在过</b>——
    /// 而那时它已经开始花额度了。多子 agent 并发时，"谁先开始"
    /// 在轮询模型下完全看不到。
    /// </remarks>
    private static async Task<string?> SubagentStartPushedAsync()
    {
        var provider = new TestProvider();
        await using var live = new LiveConnection(provider);

        live.Send("""{"method":"subagent/list","requestId":"req-1","sessionId":"s1"}""");
        await live.ReadAsync(TimeSpan.FromSeconds(3));

        // **只登记、不完成**：只有 Started 会触发。
        provider.CreateSubagent("sub-start", "刚派出去", "turn-1");

        var pushed = await live.ReadAsync(TimeSpan.FromSeconds(3));
        if (pushed is null) return "子 agent 启动了，但没有推送——界面要等它跑完才知道它存在";

        if (pushed is not SubagentUpdatedEvent updated)
            return $"推送的不是 subagent/updated，而是 {pushed.GetType().Name}";

        if (updated.Agent.State != SubagentRunState.Running)
            return $"启动时的状态应是 Running，实际 {updated.Agent.State}";

        return updated.Agent.Id == "sub-start"
            ? null
            : $"推送的 id 不对：{updated.Agent.Id}";
    }

    /// <summary>
    /// **默认不广播权限变更**——单窗口会白白收到用不到的一帧。
    /// </summary>
    private static async Task<string?> PermissionNoBroadcastAsync()
    {
        var provider = new TestProvider();
        await using var live = new LiveConnection(provider);

        // 切换一次（没有任何 watch）。
        live.Send("""{"method":"permission/switch","requestId":"r1","key":"unattended"}""");

        var first = await live.ReadAsync(TimeSpan.FromSeconds(3));
        if (first is not PermissionPresetListedEvent)
            return $"切换的响应不对：{first?.GetType().Name ?? "超时"}";

        // **不该再有一条**。等 600ms 足够让广播（如果有）到达。
        var extra = await live.ReadAsync(TimeSpan.FromMilliseconds(600));

        return extra is null
            ? null
            : $"默认就广播了权限变更（多余的一帧）：{extra.GetType().Name}";
    }

    /// <summary>订阅之后才广播，且广播那条不带 RequestId。</summary>
    private static async Task<string?> PermissionBroadcastAsync()
    {
        var provider = new TestProvider();
        await using var live = new LiveConnection(provider);

        // 先订阅。
        live.Send("""{"method":"permission/watch","requestId":"w1"}""");
        var ack = await live.ReadAsync(TimeSpan.FromSeconds(3));
        if (ack is not PermissionPresetListedEvent)
            return $"订阅的响应不对：{ack?.GetType().Name ?? "超时"}";

        // 再切换。
        live.Send("""{"method":"permission/switch","requestId":"r1","key":"unattended"}""");

        // 收到两条：带 RequestId 的响应 + 不带 id 的广播。
        var response = await live.ReadAsync(TimeSpan.FromSeconds(3));
        var broadcast = await live.ReadAsync(TimeSpan.FromSeconds(3));

        if (response is not PermissionPresetListedEvent r || r.RequestId != "r1")
            return "第一条应是带 RequestId 的响应";

        if (broadcast is null) return "订阅了却收不到广播";

        if (broadcast is not PermissionPresetListedEvent b)
            return $"广播类型不对：{broadcast.GetType().Name}";

        if (b.RequestId is not null)
            return $"广播带了 RequestId={b.RequestId}——会被误认为对某条命令的响应";

        return b.State.CurrentKey == "unattended"
            ? null
            : $"广播里的状态不对：{b.State.CurrentKey}";
    }

    /// <summary>
    /// **切换预设必须真的改变引擎的审批裁决。**
    /// </summary>
    /// <remarks>
    /// <b>这条防的是"C5 是假控件"</b>：界面上能切、服务端也回成功，
    /// 但引擎实际行为不变。桌面端明确说过，那比不做更糟。
    ///
    /// <para>
    /// <b>验法要端到端</b>：建一条真实的审批链
    /// （<c>PermissionPolicyApprover(PresetApprover(inner, presets))</c>）、
    /// 真的经 dispatcher 派发一个带副作用的工具调用，
    /// 数内层审批器被问了几次。只查 <c>presets.Current</c> 是不够的
    /// ——那只能证明"对象改了"，证不了"引擎读的是它"。
    /// </para>
    /// </remarks>
    private static async Task<string?> PermissionAffectsApprovalAsync()
    {
        var counter = new CountingApprover();
        var presets = PermissionPresets.CreateDefault(ApprovalPolicy.AskOnRisk);

        // 与 CLI 相同的装饰顺序（Preset 在内层）。
        var approver = new PermissionPolicyApprover(new PresetApprover(counter, presets));

        using var gate = new ToolResourceGate();
        var dispatcher = new ToolDispatcher(
            approver, gate, "s1", "t1", "/tmp",
            TimeSpan.FromMinutes(1), new WorkspacePathGuard("/tmp"), store: null);

        dispatcher.Register(new SideEffectTool());

        var args = JsonDocument.Parse(
            """{"path":"/tmp/a.txt","content":"x"}""").RootElement.Clone();

        // 1. AskOnRisk：应当问。
        counter.Reset();
        var first = await dispatcher.DispatchAsync(new ToolInvocation("i1", "probe_write", args));
        if (counter.Asked == 0)
            return $"AskOnRisk 下应当询问审批，但内层审批器一次都没被调用（{first.Outcome}）";

        // 2. 经**协议命令**切到 unattended。
        var (events, _) = await RunAsync(
            new TestProvider(presets),
            """{"method":"permission/switch","requestId":"r1","key":"unattended"}""");

        if (events.OfType<PermissionPresetListedEvent>().FirstOrDefault() is not { } listed)
            return "切换命令没有回状态：" + Describe(events);

        if (listed.State.CurrentKey != "unattended")
            return $"回的状态不对：{listed.State.CurrentKey}";

        // 3. 同样的调用：不该再问。
        counter.Reset();
        var second = await dispatcher.DispatchAsync(new ToolInvocation("i2", "probe_write", args));

        return counter.Asked == 0
            ? null
            : $"切到 unattended 之后内层审批器仍被问了 {counter.Asked} 次"
                + $"（{second.Outcome}）——界面切了但引擎行为没变，这就是假控件";
    }

    /// <summary>
    /// **协议层看到的子 agent 列表，必须是引擎实际在跑的那一份。**
    /// </summary>
    /// <remarks>
    /// <b>这条防的是"两份注册表"</b>：宿主若在构造工具时 new 一个、
    /// 在提供给协议层时又 new 一个，<c>subagent/list</c> 会永远返回空，
    /// 而引擎实际在跑子 agent——"界面显示零个、额度却在烧"。
    ///
    /// <para>
    /// 验法：从**工具实际持有的**那份注册表里读，与协议层返回的列表比。
    /// 只查协议层自己返回什么是不够的——那证明不了它和引擎同源。
    /// </para>
    /// </remarks>
    private static async Task<string?> SubagentListSeesRealRegistryAsync()
    {
        var provider = new TestProvider();

        // 造一个工具，它持有的注册表就是 provider 会交出去的那份。
        var registry = provider.Subagents("s1")!;
        var tool = new SubagentListTool(registry);

        // 往工具持有的注册表里放一个子 agent。
        var handle = provider.CreateSubagent("sub-real", "引擎在跑", "turn-1");

        var (events, _) = await RunAsync(provider,
            """{"method":"subagent/list","requestId":"r1","sessionId":"s1"}""");

        if (events.OfType<SubagentsListedEvent>().FirstOrDefault() is not { } listed)
            return "没有回列表：" + Describe(events);

        if (listed.Agents.All(a => a.Id != "sub-real"))
            return "协议层返回的列表里没有引擎实际在跑的那个子 agent"
                + "（说明宿主提供了两份不同的注册表）";

        // 工具侧也应当看到同一个——这条是"同源"的直接证据。
        var viaTool = await tool.ExecuteAsync(
            new ToolInvocation("t1", SubagentListTool.ToolId,
                JsonDocument.Parse("""{}""").RootElement.Clone()),
            CancellationToken.None);

        var text = viaTool.Content.GetRawText();
        _ = handle;

        return text.Contains("sub-real", StringComparison.Ordinal)
            ? null
            : "工具侧看不到同一个子 agent——工具与协议层用的不是同一份注册表";
    }

    // ── 驱动 ──────────────────────────────────────────────────

    /// <summary>喂一串命令进去，收集服务端回的所有事件。</summary>
    private static Task<(List<ServerEvent> Events, HostProtocolServer Server)> RunAsync(
        TestProvider provider, string commands) => CreatePairAsync(provider, commands);

    /// <summary>建一对内存流，跑服务端，返回它写出的事件。</summary>
    private static async Task<(List<ServerEvent> Events, HostProtocolServer Server)>
        CreatePairAsync(TestProvider provider, string commands = "")
    {
        var input = new MemoryStream(Encoding.UTF8.GetBytes(
            commands.Length == 0 ? "" : commands.Replace("\r\n", "\n") + "\n"));
        var output = new MemoryStream();
        var log = new StringWriter();

        var server = new HostProtocolServer(input, output, provider, log);

        if (commands.Length > 0)
        {
            await server.RunAsync();
        }

        output.Position = 0;
        var events = new List<ServerEvent>();
        using var reader = new StreamReader(output, Encoding.UTF8, leaveOpen: true);

        while (await reader.ReadLineAsync() is { } line)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;

            var parsed = JsonSerializer.Deserialize<ServerEvent>(
                line, HostProtocolContract.Json);

            if (parsed is not null) events.Add(parsed);
        }

        return (events, server);
    }

    /// <summary>
    /// 让服务端在后台跑，同时可读它写出的帧。
    /// </summary>
    /// <remarks>
    /// <b>为什么不能复用 <see cref="CreatePairAsync"/></b>：那条路是"喂完命令、
    /// 跑完循环、再读输出"。而推送事件的本质是**服务端在没有任何新命令时
    /// 主动写**——一次性的读法永远看不到它。
    /// </remarks>
    private sealed class LiveConnection : IAsyncDisposable
    {
        private readonly LiveWriteBuffer _output = new();
        private readonly LiveCommandStream _input = new();
        private readonly HostProtocolServer _server;
        private readonly Task _pump;

        public LiveConnection(TestProvider provider)
        {
            _server = new HostProtocolServer(_input, _output, provider, new StringWriter());
            _pump = _server.RunAsync();
        }

        /// <summary>把一条命令写进输入流（服务端会立即处理）。</summary>
        public void Send(string json) => _input.Write(json);

        /// <summary>读到一条事件；超时返回 null。</summary>
        public async Task<ServerEvent?> ReadAsync(TimeSpan timeout)
        {
            var deadline = DateTime.UtcNow + timeout;

            while (DateTime.UtcNow < deadline)
            {
                if (_output.TryReadLine(out var line))
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;

                    return JsonSerializer.Deserialize<ServerEvent>(
                        line, HostProtocolContract.Json);
                }

                await Task.Delay(15).ConfigureAwait(false);
            }

            return null;
        }

        public async ValueTask DisposeAsync()
        {
            await _server.DisposeAsync();
            _input.Complete();

            try { await _pump.WaitAsync(TimeSpan.FromSeconds(2)); }
            catch (Exception) { /* 收尾失败不影响检查结论。 */ }
        }
    }

    /// <summary>可被外部追加写入的只增长缓冲区。</summary>
    private sealed class LiveWriteBuffer : Stream
    {
        private readonly List<byte> _buffer = [];
        private readonly SemaphoreSlim _gate = new(1, 1);
        private int _readOffset;

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => _buffer.Count;

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() { }

        public override void Write(byte[] buffer, int offset, int count)
        {
            _gate.Wait();
            try { for (var i = 0; i < count; i++) _buffer.Add(buffer[offset + i]); }
            finally { _gate.Release(); }
        }

        public override Task WriteAsync(
            byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            Task.Run(() => Write(buffer, offset, count), cancellationToken);

        public override ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
            new(Task.Run(() =>
            {
                _gate.Wait();
                try { _buffer.AddRange(buffer.ToArray()); }
                finally { _gate.Release(); }
            }, cancellationToken));

        /// <summary>试着取一整行（以 LF 结尾）。拿不到返回 false。</summary>
        public bool TryReadLine(out string line) => TryReadLineCore(out line);

        private bool TryReadLineCore(out string line)
        {
            _gate.Wait();
            try
            {
                var span = _buffer;
                for (var i = _readOffset; i < span.Count; i++)
                {
                    if (span[i] != (byte)'\n') continue;

                    var length = i - _readOffset;
                    var bytes = new byte[length];
                    for (var j = 0; j < length; j++) bytes[j] = span[_readOffset + j];

                    _readOffset = i + 1;

                    // 压缩已读部分，避免长会话把内存撑大。
                    if (_readOffset > 64 * 1024)
                    {
                        span.RemoveRange(0, _readOffset);
                        _readOffset = 0;
                    }

                    line = Encoding.UTF8.GetString(bytes).TrimEnd('\r');
                    return true;
                }

                line = string.Empty;
                return false;
            }
            finally { _gate.Release(); }
        }

        public void Complete() { }

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();

        public override void SetLength(long value) =>
            throw new NotSupportedException();
    }

    /// <summary>可追加命令的输入流：服务端读到末尾后继续等待。</summary>
    private sealed class LiveCommandStream : Stream
    {
        private readonly List<byte> _pending = [];
        private readonly SemaphoreSlim _gate = new(1, 1);
        private int _offset;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => 0;

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public void Write(string json)
        {
            _gate.Wait();
            try { _pending.AddRange(Encoding.UTF8.GetBytes(json + "\n")); }
            finally { _gate.Release(); }
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            // 阻塞式读：没数据就睡一会再来（模拟"对端还没写"）。
            // 收到 Complete 才返回 0，让服务端循环正常退出。
            while (true)
            {
                _gate.Wait();
                try
                {
                    if (_offset < _pending.Count)
                    {
                        var available = Math.Min(count, _pending.Count - _offset);
                        for (var i = 0; i < available; i++) buffer[offset + i] = _pending[_offset + i];
                        _offset += available;
                        return available;
                    }

                    if (_completed) return 0;
                }
                finally { _gate.Release(); }

                Thread.Sleep(10);
            }
        }

        /// <summary>让阻塞的 Read 返回 0（流结束），使服务端循环退出。</summary>
        public void Complete()
        {
            _gate.Wait();
            try { _completed = true; }
            finally { _gate.Release(); }
        }

        private bool _completed;

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private static string Describe(IEnumerable<ServerEvent> events) =>
        events.Count() == 0
            ? "(没有任何事件)"
            : string.Join(" / ", events.Select(e => e.GetType().Name));

    private static async Task<int> CheckAsync(string name, Func<Task<string?>> action)
    {
        try
        {
            var failure = await action().ConfigureAwait(false);
            if (failure is null)
            {
                Console.WriteLine($"PASS {name}");
                return 0;
            }

            Console.WriteLine($"FAIL {name}: {failure}");
            return 1;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"FAIL {name}: {ex.GetType().Name}: {ex.Message}");
            return 1;
        }
    }

    /// <summary>数内层审批器被问了几次——验"预设切换真的影响裁决"用它。</summary>
    private sealed class CountingApprover : IToolApprover
    {
        public int Asked { get; private set; }

        public void Reset() => Asked = 0;

        public ValueTask<ToolApprovalDecision> RequestApprovalAsync(
            ToolApprovalRequest request, CancellationToken cancellationToken = default)
        {
            Asked++;
            return ValueTask.FromResult(ToolApprovalDecision.Allow("检查用：一律批准。"));
        }
    }

    /// <summary>一个有副作用的工具——审批链只对这类走完整判断。</summary>
    private sealed class SideEffectTool : IAgentTool
    {
        public ToolDescriptor Descriptor { get; } = new(
            "probe_write", "probe_write", "1.0.0", "检查用的写工具",
            JsonDocument.Parse("""{"type":"object"}""").RootElement.Clone());

        public ToolPermission DescribePermission(JsonElement arguments) =>
            new(ToolAccess.Write, IsKnown: true,
                Paths: ["/tmp/a.txt"], Commands: [], Domains: []);

        public ToolExecutionPlan DescribeExecution(JsonElement arguments) =>
            new(ToolConcurrency.Serial);

        public ValueTask<AgentToolResult> ExecuteAsync(
            ToolInvocation invocation, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new AgentToolResult(
                JsonDocument.Parse("""{"ok":true}""").RootElement.Clone()));
    }

    /// <summary>一个最小可用的会话能力提供方。</summary>
    private sealed class TestProvider : IHostSessionProvider
    {
        /// <summary>可以注入宿主自己那份——验"协议层与引擎同源"时必须这样用。</summary>
        private readonly PermissionPresets _presets;

        public TestProvider(PermissionPresets? presets = null) =>
            _presets = presets ?? PermissionPresets.CreateDefault(ApprovalPolicy.AskOnRisk);

        private readonly SubagentRegistry _subagents = new(maxDepth: 2, maxConcurrent: 4);

        /// <summary>关掉它可验"能力缺失时回 Conflict"。</summary>
        public bool ProvideSubagents { get; init; } = true;

        public PermissionPresets? Permissions(string? sessionId) => _presets;

        public SubagentRegistry? Subagents(string sessionId) =>
            ProvideSubagents ? _subagents : null;

        /// <summary>造一个子 agent 句柄。构造是 internal，故走反射。</summary>
        public SubagentHandle CreateSubagent(string id, string label, string? parentTurn)
        {
            var ctor = typeof(SubagentHandle).GetConstructors(
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                .First(c => c.GetParameters().Length == 2);

            // ParentTurnId 是 init-only：要在构造那一刻填，事后赋值编译不过。
            // 这里用反射把 init 属性写进去，仍然走同一个构造函数。
            var handle = (SubagentHandle)ctor.Invoke([id, label]);

            if (parentTurn is not null)
            {
                typeof(SubagentHandle)
                    .GetProperty(nameof(SubagentHandle.ParentTurnId))!
                    .SetValue(handle, parentTurn);
            }

            // 注册要异步；这里同步等，检查没有并发压力。
            _subagents.RegisterAsync(handle).GetAwaiter().GetResult();
            return handle;
        }
    }
}
