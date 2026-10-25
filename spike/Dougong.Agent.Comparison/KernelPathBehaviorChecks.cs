using System.Text.Json;
using Dougong.Agent;
using Dougong.Kernel;

namespace Dougong.Agent.Comparison;

/// <summary>
/// 内核路径下的行为验收（g14 方案的验收 3 与 7）。
/// </summary>
/// <remarks>
/// 为什么单独一个文件：`KernelWiringChecks` 验的是"接上了没有"，这里验的是
/// "接上之后行为对不对"。前者靠元数据和结构断言，后者必须真的跑一遍。
/// <para>
/// 这两条此前从未实现——`-- kernel-wiring` 里一个审批相关断言都没有。
/// 缺它的风险很具体：如果 `LeasedApprover` 适配器吞掉异常、或把"取不到审批器"
/// 误判成批准，内核路径下审批会**静默失效**，而全部测试仍然全绿。
/// </para>
/// </remarks>
public static class KernelPathBehaviorChecks
{
    public static async Task<int> RunAsync()
    {
        var failures = 0;
        failures += await CheckAsync("kernel-path-still-asks-approval", ApprovalIsAskedAsync);
        failures += await CheckAsync("kernel-path-denial-blocks-execution", DenialBlocksAsync);
        failures += await CheckAsync("deactivated-runtime-blocks-next-turn", DeactivatedRuntimeAsync);

        Console.WriteLine(failures == 0
            ? "KERNEL-PATH: ALL PASS"
            : $"KERNEL-PATH: {failures} FAILURE(S)");
        return failures;
    }

    /// <summary>内核路径下，工具执行仍必须经过审批。</summary>
    private static async Task<string?> ApprovalIsAskedAsync()
    {
        var approver = new CountingApprover();
        var executed = 0;
        await using var host = await CreateHostAsync(approver, () => executed++);

        var dispatcher = host.ResolveDispatcherView()
            ?? throw new InvalidOperationException("解析不到 dispatcher");

        var result = await dispatcher.DispatchAsync(Invocation("c1"));

        if (result.Outcome != ToolCallOutcome.Succeeded)
            return $"工具应在批准后成功，实际 {result.Outcome}：{result.Diagnostic}";
        if (approver.Calls != 1)
            return $"审批应被调用 1 次，实际 {approver.Calls} 次——内核路径绕过了审批";
        if (executed != 1) return $"工具应执行 1 次，实际 {executed}";

        return null;
    }

    /// <summary>
    /// 内核路径下，审批被拒绝必须阻止执行。
    /// </summary>
    /// <remarks>
    /// 只验"被调用"不够：一个把决定丢掉、照常执行的适配器也能通过上一条。
    /// 这条验的是拒绝真的落到执行上。
    /// </remarks>
    private static async Task<string?> DenialBlocksAsync()
    {
        var approver = new CountingApprover { Deny = true };
        var executed = 0;
        await using var host = await CreateHostAsync(approver, () => executed++);

        var dispatcher = host.ResolveDispatcherView()
            ?? throw new InvalidOperationException("解析不到 dispatcher");

        var result = await dispatcher.DispatchAsync(Invocation("c2"));

        if (result.Outcome != ToolCallOutcome.RejectedNotApproved)
            return $"审批拒绝时终态应为 RejectedNotApproved，实际 {result.Outcome}";
        if (executed != 0) return $"审批拒绝后工具仍被执行了 {executed} 次";

        return null;
    }

    /// <summary>
    /// 停用运行时插件后，旧引用不能静默继续跑。
    /// </summary>
    /// <remarks>
    /// 这条是 g14 方案的"验收 7"。已有的
    /// <c>blocked-consumer-recovers-when-provider-returns</c> 验的是
    /// **恢复**方向（提供方回来 → 消费者唤醒），方向相反。
    /// </remarks>
    private static async Task<string?> DeactivatedRuntimeAsync()
    {
        var executed = 0;
        var host = await CreateHostAsync(new CountingApprover(), () => executed++);
        try
        {
            var runtime = host.ResolveRuntime();

            var plugins = await host.GetPluginsAsync();
            var runtimePlugin = plugins.FirstOrDefault(p => p.Id.ToString().Contains("runtime"));
            if (runtimePlugin is null) return "找不到运行时插件";

            await host.Kernel.DeactivateAsync(runtimePlugin.Id);

            // 旧引用必须"可观察地失败"，而不是静默跑完。
            // 注意：KernelAgentRuntime 会把异常转成 RunFinished 事件
            // （与"流永不抛异常"的取向一致），所以不能只看是否抛异常。
            var outcome = AgentRunOutcome.Completed;
            string? diagnostic = null;
            try
            {
                await foreach (var evt in runtime.RunAsync(
                    new AgentRunRequest("s", "t", [], "hi")))
                {
                    if (evt is AgentEvent.RunFinished finished)
                    {
                        outcome = finished.Outcome;
                        diagnostic = finished.Diagnostic;
                    }
                }
            }
            catch (Exception)
            {
                outcome = AgentRunOutcome.Failed;
            }

            if (outcome == AgentRunOutcome.Completed)
                return "运行时已停用，但旧引用仍报告 Completed：代次校验未生效";

            Console.WriteLine($"      (停用后终态={outcome}，诊断={Truncate(diagnostic, 80)})");
            return outcome == AgentRunOutcome.Failed
                ? null
                : $"停用后终态应为 Failed，实际 {outcome}";
        }
        finally
        {
            await host.DisposeAsync();
        }
    }

    private static async Task<Dougong.Cli.KernelAgentHost> CreateHostAsync(
        IToolApprover approver, Action onExecute)
    {
        var store = new InMemoryAgentStore("kernel-path");

        return await Dougong.Cli.KernelAgentHost.CreateAsync(
            store,
            (resolvedApprover, resolvedStore, ct) =>
            {
                // 显式走 accessor 重载，与 CLI 生产路径一致。
                var dispatcher = new ToolDispatcher(
                    new FixedApproverAccessor(resolvedApprover), null, "s", "t", ".",
                    TimeSpan.FromMinutes(5), null, resolvedStore);
                dispatcher.Register(new CountingTool(onExecute));
                return Task.FromResult<IToolDispatcher>(dispatcher);
            },
            (resolvedDispatcher, resolvedStore) =>
                new LoopAgentRuntime(new SilentClient(), resolvedDispatcher, resolvedStore),
            approver);
    }

    private static ToolInvocation Invocation(string id)
    {
        using var document = JsonDocument.Parse("""{"path":"a.txt"}""");
        return new ToolInvocation(id, "count", document.RootElement.Clone());
    }

    private sealed class CountingApprover : IToolApprover
    {
        public bool Deny { get; init; }
        public int Calls { get; private set; }

        public ValueTask<ToolApprovalDecision> RequestApprovalAsync(
            ToolApprovalRequest request, CancellationToken cancellationToken = default)
        {
            Calls++;
            return ValueTask.FromResult(Deny
                ? ToolApprovalDecision.Deny("测试：拒绝")
                : ToolApprovalDecision.Allow("测试：批准"));
        }
    }

    private sealed class CountingTool(Action onExecute) : IAgentTool
    {
        public ToolDescriptor Descriptor { get; } = new(
            "count", "count", "1.0.0", "计数工具",
            JsonDocument.Parse(
                """{"type":"object","properties":{"path":{"type":"string"}},"required":["path"]}""")
                .RootElement.Clone());

        public ToolPermission DescribePermission(JsonElement arguments) =>
            new(ToolAccess.Read, IsKnown: true, Paths: ["a.txt"], Commands: [], Domains: []);

        public ToolExecutionPlan DescribeExecution(JsonElement arguments) =>
            new(ToolConcurrency.Serial);

        public ValueTask<AgentToolResult> ExecuteAsync(
            ToolInvocation invocation, CancellationToken cancellationToken)
        {
            onExecute();
            using var document = JsonDocument.Parse("""{"ok":true}""");
            return ValueTask.FromResult(new AgentToolResult(document.RootElement.Clone()));
        }
    }

    private sealed class SilentClient : Microsoft.Extensions.AI.IChatClient
    {
        public Task<Microsoft.Extensions.AI.ChatResponse> GetResponseAsync(
            IEnumerable<Microsoft.Extensions.AI.ChatMessage> messages,
            Microsoft.Extensions.AI.ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new Microsoft.Extensions.AI.ChatResponse(
                new Microsoft.Extensions.AI.ChatMessage(
                    Microsoft.Extensions.AI.ChatRole.Assistant, "ok")));

        public async IAsyncEnumerable<Microsoft.Extensions.AI.ChatResponseUpdate>
            GetStreamingResponseAsync(
                IEnumerable<Microsoft.Extensions.AI.ChatMessage> messages,
                Microsoft.Extensions.AI.ChatOptions? options = null,
                [System.Runtime.CompilerServices.EnumeratorCancellation]
                CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield return new Microsoft.Extensions.AI.ChatResponseUpdate(
                Microsoft.Extensions.AI.ChatRole.Assistant, "ok");
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }

    private static string Truncate(string? text, int max) =>
        text is null ? "(无)" : text.Length <= max ? text : text[..max] + "...";

    private static async Task<int> CheckAsync(string name, Func<Task<string?>> action)
    {
        try
        {
            var failure = await action();
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
}
