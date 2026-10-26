using System.Text.Json;
using Dougong.Agent;

namespace Dougong.Agent.Comparison;

/// <summary>
/// 上下文压缩的确定性验证。重点不是"能压"，而是：
/// 失败不覆盖原历史、工具配对不被拆散、检查点可续跑。
/// </summary>
public static class CompactionChecks
{
    public static async Task<int> RunAsync()
    {
        var failures = 0;
        failures += await CheckAsync("no-plan-under-budget", NoPlanUnderBudget);
        failures += await CheckAsync("plan-under-pressure", PlanUnderPressureAsync);
        failures += await CheckAsync("plan-keeps-tool-pairs", PlanKeepsToolPairsAsync);
        failures += await CheckAsync("summary-failure-does-not-cover-history", FailureKeepsHistoryAsync);
        failures += await CheckAsync("retry-then-succeed", RetryThenSucceedAsync);
        failures += await CheckAsync("completed-checkpoint-applies", ApplyWhenCompletedAsync);
        failures += await CheckAsync("checkpoint-survives-serialisation", CheckpointRoundTrip);
        failures += await CheckAsync("cancellation-preserves-progress", CancellationPreservesAsync);
        failures += await CheckAsync("insufficient-budget-is-reported", InsufficientBudgetAsync);
        failures += await CheckAsync("latest-plan-survives-compaction", LatestPlanSurvivesAsync);
        failures += await CheckAsync("plan-protection-keeps-tool-pairs", PlanProtectionKeepsPairsAsync);
        failures += await CheckAsync("no-plan-means-full-range", NoPlanFullRangeAsync);
        failures += await CheckAsync("plan-near-head-still-compresses-something",
            PlanNearHeadStillCompressesAsync);
        failures += await CheckAsync("plan-at-head-boundary-degrades-safely",
            PlanAtHeadBoundaryDegradesSafelyAsync);

        Console.WriteLine(failures == 0
            ? "COMPACTION: ALL PASS"
            : $"COMPACTION: {failures} FAILURE(S)");
        return failures;
    }

    private static Task<string?> NoPlanUnderBudget()
    {
        var compactor = new SlidingWindowCompactor();
        var history = new List<AgentMessage>
        {
            new(AgentRole.System, "sys"),
            new(AgentRole.User, "short"),
        };

        var plan = compactor.Plan(new CompactionInput(history, 100_000, 1_000));
        return Task.FromResult<string?>(plan is null ? null : "预算充足时仍给出压缩计划");
    }

    private static Task<string?> PlanUnderPressureAsync()
    {
        var compactor = new SlidingWindowCompactor(preservedHead: 1, preservedTail: 2);
        var history = BuildHistory(40, "some long content to burn tokens ");
        var plan = compactor.Plan(new CompactionInput(history, 200, 20, 8));

        if (plan is null) return Task.FromResult<string?>("超出预算却没有给出计划");
        if (plan.EstimatedTokensAfter >= plan.EstimatedTokensBefore)
            return Task.FromResult<string?>("压缩后估算没有下降");
        return Task.FromResult<string?>(null);
    }

    // 压缩区间边界不能把 Tool 消息与其前的 Assistant 拆开。
    private static Task<string?> PlanKeepsToolPairsAsync()
    {
        var history = new List<AgentMessage>
        {
            new(AgentRole.System, "sys"),
            new(AgentRole.User, "question"),
            new(AgentRole.Assistant, "calling tool"),
            new(AgentRole.Tool, "result-1"),
            new(AgentRole.Assistant, "calling again"),
            new(AgentRole.Tool, "result-2"),
            new(AgentRole.Assistant, "answer"),
            new(AgentRole.User, "next"),
        };

        var compactor = new SlidingWindowCompactor(preservedHead: 1, preservedTail: 1);
        // 窗口调小到必然触发：estimate=32 tokens，需 trigger < 32。
        var plan = compactor.Plan(new CompactionInput(history, 30, 4, 2));
        if (plan is null) return Task.FromResult<string?>("未给出计划");

        // 区间内如果出现 Tool，其前一条必须是同类调用的 Assistant；
        // 更实际的不变量：区间起点不能是 Tool。
        if (history[plan.FirstIndex].Role == AgentRole.Tool)
            return Task.FromResult<string?>("压缩区间从 Tool 消息开始，配对被拆散");

        // 区间终点后若紧跟 Tool，说明结果被留在了区间外。
        if (plan.LastIndex + 1 < history.Count && history[plan.LastIndex + 1].Role == AgentRole.Tool)
            return Task.FromResult<string?>("压缩区间结束后紧跟 Tool，配对被拆散");

        return Task.FromResult<string?>(null);
    }

    // 摘要失败：原历史必须原样保留。
    private static async Task<string?> FailureKeepsHistoryAsync()
    {
        var history = BuildHistory(30, "content ");
        var plan = new SlidingWindowCompactor(preservedHead: 1, preservedTail: 2)
            .Plan(new CompactionInput(history, 120, 10, 4))!;

        var task = new SummarizingCompactionTask(
            plan, history,
            (_, _) => throw new InvalidOperationException("摘要服务不可用"),
            Render);

        var checkpoint = await AdvanceToTerminalAsync(task);
        if (checkpoint.Phase != CompactionPhase.Failed)
            return $"期望 Failed，实际 {checkpoint.Phase}";

        var applied = CompactionApply.TryApply(history, checkpoint);
        return applied is null ? null : "失败后仍然替换了历史";
    }

    private static async Task<string?> RetryThenSucceedAsync()
    {
        var history = BuildHistory(30, "content ");
        var plan = new SlidingWindowCompactor(preservedHead: 1, preservedTail: 2)
            .Plan(new CompactionInput(history, 120, 10, 4))!;

        var attempts = 0;
        var task = new SummarizingCompactionTask(
            plan, history,
            (_, _) =>
            {
                attempts++;
                return attempts == 1
                    ? throw new InvalidOperationException("第一次失败")
                    : ValueTask.FromResult("summary");
            },
            Render);

        var checkpoint = await AdvanceToTerminalAsync(task);
        if (checkpoint.Phase != CompactionPhase.Completed)
            return $"期望 Completed，实际 {checkpoint.Phase}（{checkpoint.FailureReason}）";
        if (checkpoint.Attempt != 2) return $"重试次数 {checkpoint.Attempt}，期望 2";
        return null;
    }

    private static async Task<string?> ApplyWhenCompletedAsync()
    {
        var history = BuildHistory(30, "content ");
        var plan = new SlidingWindowCompactor(preservedHead: 1, preservedTail: 2)
            .Plan(new CompactionInput(history, 120, 10, 4))!;

        var task = new SummarizingCompactionTask(
            plan, history,
            (_, _) => ValueTask.FromResult("condensed"),
            Render);

        var checkpoint = await AdvanceToTerminalAsync(task);
        var applied = CompactionApply.TryApply(history, checkpoint);
        if (applied is null) return "完成态却未应用";

        if (!applied.Any(m => m.Content.Contains("condensed")))
            return "应用后找不到摘要内容";
        if (applied.Count >= history.Count)
            return $"应用后条数未减少：{history.Count} → {applied.Count}";

        // 头尾必须保留。
        if (!applied.Any(m => m.Content == history[0].Content))
            return "头部记录丢失";
        if (!applied.Any(m => m.Content == history[^1].Content))
            return "尾部记录丢失";
        return null;
    }

    private static Task<string?> CheckpointRoundTrip()
    {
        var checkpoint = new CompactionCheckpoint(
            CompactionPhase.Retry, 2, 3, 9, 2, 12, "summary text", "last error");
        var json = checkpoint.ToJson();

        var phase = json.GetProperty("phase").GetString();
        var attempt = json.GetProperty("attempt").GetInt32();
        var tail = json.GetProperty("tailIndex").GetInt32();

        if (phase != "Retry") return Task.FromResult<string?>($"phase 丢失：{phase}");
        if (attempt != 2) return Task.FromResult<string?>("attempt 丢失");
        if (tail != 12) return Task.FromResult<string?>("tailIndex 丢失");
        return Task.FromResult<string?>(null);
    }

    // 取消不是失败：检查点保留，可以继续。
    private static async Task<string?> CancellationPreservesAsync()
    {
        var history = BuildHistory(20, "content ");
        var plan = new SlidingWindowCompactor(preservedHead: 1, preservedTail: 2)
            .Plan(new CompactionInput(history, 100, 8, 4))!;

        using var cts = new CancellationTokenSource();
        var task = new SummarizingCompactionTask(
            plan, history,
            async (_, ct) =>
            {
                cts.Cancel();
                ct.ThrowIfCancellationRequested();
                await Task.CompletedTask;
                return "unreachable";
            },
            Render);

        // Select → Summarize
        await task.AdvanceAsync();
        try { await task.AdvanceAsync(cts.Token); }
        catch (OperationCanceledException) { }

        if (task.Checkpoint.Phase == CompactionPhase.Failed)
            return "取消被当成失败";
        if (task.Checkpoint.Phase == CompactionPhase.Completed)
            return "取消却标记为完成";
        return null;
    }

    // 预算不足时必须报错，而不是静默给出负数预算。
    private static Task<string?> InsufficientBudgetAsync()
    {
        try
        {
            TokenBudget.EffectiveInput(contextWindowTokens: 1_000, maxOutputTokens: 900, estimateErrorTokens: 200);
            return Task.FromResult<string?>("预算不足却未报错");
        }
        catch (ArgumentOutOfRangeException)
        {
            return Task.FromResult<string?>(null);
        }
    }

    private static async Task<CompactionCheckpoint> AdvanceToTerminalAsync(
        SummarizingCompactionTask task, int maxSteps = 6)
    {
        for (var i = 0; i < maxSteps && !task.Checkpoint.IsTerminal; i++)
            await task.AdvanceAsync();
        return task.Checkpoint;
    }

    // 最近一次计划更新不能被压进摘要区间。
    // 计划是模型自己写的全局目标；摘要掉它，模型就失去"我在做什么"的显式记录，
    // 这是长任务目标漂移的典型机制。
    private static Task<string?> LatestPlanSurvivesAsync()
    {
        var compactor = new SlidingWindowCompactor(preservedHead: 1, preservedTail: 2);

        var history = BuildHistory(30, "padding content to burn tokens ");
        // 在中间放一次 todo_write 调用（assistant 请求 + tool 结果）
        var planAt = 20;
        history.Insert(planAt, new AgentMessage(AgentRole.Assistant, "")
        {
            ToolCalls = [new AgentToolCallRef("p1", PlanTool.ToolId, EmptyArgs())],
        });
        history.Insert(planAt + 1, new AgentMessage(AgentRole.Tool,
            """{"entries":[{"content":"完成第一步","status":"done"}]}""")
        {
            ToolCallId = "p1",
            ToolId = PlanTool.ToolId,
        });

        var plan = compactor.Plan(new CompactionInput([.. history], 200, 20, 8));
        if (plan is null) return Task.FromResult<string?>("超出预算却没有给出计划");

        if (plan.LastIndex >= planAt)
            return Task.FromResult<string?>(
                $"压缩区间末端 {plan.LastIndex} 覆盖了计划所在 {planAt}：计划会被摘要掉");

        return Task.FromResult<string?>(null);
    }

    // 保护计划时不能把 assistant 请求与 tool 结果拆开。
    private static Task<string?> PlanProtectionKeepsPairsAsync()
    {
        var compactor = new SlidingWindowCompactor(preservedHead: 1, preservedTail: 2);

        var history = BuildHistory(30, "padding content to burn tokens ");
        var planAt = 20;
        history.Insert(planAt, new AgentMessage(AgentRole.Assistant, "")
        {
            ToolCalls = [new AgentToolCallRef("p1", PlanTool.ToolId, EmptyArgs())],
        });
        history.Insert(planAt + 1, new AgentMessage(AgentRole.Tool, """{"entries":[]}""")
        {
            ToolCallId = "p1",
            ToolId = PlanTool.ToolId,
        });

        var plan = compactor.Plan(new CompactionInput([.. history], 200, 20, 8));
        if (plan is null) return Task.FromResult<string?>("超出预算却没有给出计划");

        // 区间末端若落在 Tool 上，说明拆开了调用配对
        if (history[plan.LastIndex].Role is AgentRole.Tool)
            return Task.FromResult<string?>(
                $"压缩区间末端落在 Tool 消息上（index {plan.LastIndex}）：调用配对会被拆散");

        return Task.FromResult<string?>(null);
    }

    // 没有计划时，区间末端应与原来一致（不能被保护逻辑误伤）。
    private static Task<string?> NoPlanFullRangeAsync()
    {
        var compactor = new SlidingWindowCompactor(preservedHead: 1, preservedTail: 2);
        var history = BuildHistory(30, "padding content to burn tokens ");

        var plan = compactor.Plan(new CompactionInput(history, 200, 20, 8));
        if (plan is null) return Task.FromResult<string?>("超出预算却没有给出计划");

        // 期望：末端 = Count - tail - 1 = 30 - 2 - 1 = 27
        var expected = history.Count - 2 - 1;
        if (plan.LastIndex != expected)
            return Task.FromResult<string?>(
                $"无计划时区间末端应为 {expected}，实际 {plan.LastIndex}：保护逻辑误伤了正常区间");

        return Task.FromResult<string?>(null);
    }

    /// <summary>
    /// 一个合法但为空的参数 JSON。
    /// </summary>
    /// <remarks>
    /// 不能用 `default(JsonElement)`：它的 ValueKind 是 Undefined，
    /// 任何访问都会抛 InvalidOperationException。测试数据里用它会得到
    /// "测试自己坏了"的假失败——本文件上一版就是这样，两条检查都报
    /// "Operation is not valid due to the current state of the object"。
    /// </remarks>
    private static JsonElement EmptyArgs()
    {
        using var doc = JsonDocument.Parse("{}");
        return doc.RootElement.Clone();
    }

    /// <summary>
    /// 计划靠近头部时，压缩能力不能被完全吃掉。
    /// </summary>
    /// <remarks>
    /// 保护计划有个代价：若计划紧跟在头部保留区之后，砍掉计划后的可压缩区间为空，
    /// 整个压缩被放弃、上下文继续超限。这一条检查该代价的**实际范围**：
    /// 计划在第 4 条时应当仍能压缩，不能退化成"一有计划就不能压"。
    /// <para>
    /// 如果这条失败，说明保护逻辑过强——需要改成"计划挪到区间外"而不是
    /// "区间截止在计划前"。
    /// </para>
    /// </remarks>
    private static Task<string?> PlanNearHeadStillCompressesAsync()
    {
        var compactor = new SlidingWindowCompactor(preservedHead: 2, preservedTail: 2);

        var history = BuildHistory(30, "padding content to burn tokens ");
        // 计划放在第 4 条：头部保留 2 条后还有 2 条可压空间
        var planAt = 4;
        history.Insert(planAt, new AgentMessage(AgentRole.Assistant, "")
        {
            ToolCalls = [new AgentToolCallRef("p1", PlanTool.ToolId, EmptyArgs())],
        });
        history.Insert(planAt + 1, new AgentMessage(AgentRole.Tool, """{"entries":[]}""")
        {
            ToolCallId = "p1",
            ToolId = PlanTool.ToolId,
        });

        var plan = compactor.Plan(new CompactionInput([.. history], 200, 20, 8));
        if (plan is null)
            return Task.FromResult<string?>(
                "计划在第 4 条时压缩被完全放弃：保护逻辑过强，上下文会一直超限");

        if (plan.LastIndex >= planAt)
            return Task.FromResult<string?>(
                $"压缩区间末端 {plan.LastIndex} 覆盖了计划所在的 {planAt}");

        return Task.FromResult<string?>(null);
    }

    /// <summary>
    /// 计划正好压在头部保留边界上时，必须**安全退化**而不是给出错误区间。
    /// </summary>
    /// <remarks>
    /// 这是保护逻辑的最坏情况：可压缩区间会变成空。此时唯一正确的行为是
    /// 返回 <c>null</c>（放弃压缩），而不是返回一个 first &gt; last 或负数下界的计划——
    /// 后者会让调用方在 <c>Skip(first).Take(count)</c> 上得到意外结果。
    /// <para>
    /// 这条检查钉住的是"退化必须是 null 而不是畸形计划"，
    /// 不要求此时仍能压缩（那在语义上做不到）。
    /// </para>
    /// </remarks>
    private static Task<string?> PlanAtHeadBoundaryDegradesSafelyAsync()
    {
        var compactor = new SlidingWindowCompactor(preservedHead: 2, preservedTail: 2);

        var history = BuildHistory(30, "padding content to burn tokens ");
        // 计划正好放在头部保留区的最后一条
        var planAt = 2;
        history.Insert(planAt, new AgentMessage(AgentRole.Assistant, "")
        {
            ToolCalls = [new AgentToolCallRef("p1", PlanTool.ToolId, EmptyArgs())],
        });
        history.Insert(planAt + 1, new AgentMessage(AgentRole.Tool, """{"entries":[]}""")
        {
            ToolCallId = "p1",
            ToolId = PlanTool.ToolId,
        });

        var plan = compactor.Plan(new CompactionInput([.. history], 200, 20, 8));

        if (plan is null) return Task.FromResult<string?>(null);   // 安全退化

        // 若仍给出计划，区间必须自洽
        if (plan.FirstIndex < 0)
            return Task.FromResult<string?>($"计划的下界是负数：{plan.FirstIndex}");
        if (plan.LastIndex < plan.FirstIndex)
            return Task.FromResult<string?>(
                $"计划区间倒置：first={plan.FirstIndex} last={plan.LastIndex}");
        if (plan.LastIndex >= planAt)
            return Task.FromResult<string?>(
                $"计划在 {planAt}，却被区间末端 {plan.LastIndex} 覆盖");

        return Task.FromResult<string?>(null);
    }

    private static List<AgentMessage> BuildHistory(int count, string body) =>
        [.. Enumerable.Range(0, count).Select(i =>
            new AgentMessage(i % 2 == 0 ? AgentRole.User : AgentRole.Assistant,
                $"{body}{i}"))];

    private static string Render(IReadOnlyList<AgentMessage> messages) =>
        string.Join("\n", messages.Select(m => $"{m.Role}: {m.Content}"));

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
