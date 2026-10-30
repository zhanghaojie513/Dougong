using Dougong.Agent;

namespace Dougong.Agent.Comparison;

/// <summary>
/// 上下文占用与用量的验证。
/// </summary>
/// <remarks>
/// <b>这两件事必须分开测，因为它们是两种数</b>：
/// <list type="bullet">
/// <item><c>UsageReported</c> —— provider 回报的**真实**用量，可以对账。</item>
/// <item><c>ContextUsageReported</c> —— 上下文构成本身的**估算**，不能对账。</item>
/// </list>
/// 混用会让成本统计出错，所以套件也按这个界限分。
/// <para>
/// 本套不联网：用 <see cref="ScriptedChatClient"/> 驱动完整循环，
/// 假客户端自己回报 usage，能测出"锚点是否被正确采用与作废"。
/// </para>
/// </remarks>
public static class ContextUsageChecks
{
    public static async Task<int> RunAsync()
    {
        var failures = 0;
        failures += await CheckAsync("usage-reported-from-provider", UsageReportedAsync);
        failures += await CheckAsync("usage-not-reported-yields-no-event", NoUsageNoEventAsync);
        failures += await CheckAsync("breakdown-splits-system-tools-messages", BreakdownSplitsAsync);
        failures += await CheckAsync("breakdown-totals-match-sum", BreakdownTotalsAsync);
        failures += await CheckAsync("anchor-used-after-first-response", AnchorUsedAsync);
        failures += await CheckAsync("anchor-invalidated-by-compaction", AnchorInvalidatedAsync);
        failures += await CheckAsync("window-ratio-uses-window-not-budget", WindowRatioAsync);

        Console.WriteLine(failures == 0
            ? "CONTEXT-USAGE: ALL PASS"
            : $"CONTEXT-USAGE: {failures} FAILURE(S)");
        return failures;
    }

    /// <summary>provider 回报的用量必须原样转成事件，不许换算或补零。</summary>
    private static async Task<string?> UsageReportedAsync()
    {
        var client = new ScriptedChatClient(
            new ScriptedTurn(new ScriptedUpdate(Text: "ok", UsageInput: 1234, UsageOutput: 56)));

        var reports = await CollectUsageAsync(client);

        if (reports.Count != 1)
            return $"应恰好发出 1 个 UsageReported，实际 {reports.Count} 个";

        var report = reports[0];
        if (report.InputTokens != 1234)
            return $"InputTokens 应为 1234，实际 {report.InputTokens}";
        if (report.OutputTokens != 56)
            return $"OutputTokens 应为 56，实际 {report.OutputTokens}";

        // **cacheWrite 拿不到时必须是 null 而不是 0**：0 会被当成
        // "确实没有缓存写入"，而真相是"我们不知道"。
        if (report.CacheWriteTokens is not null)
            return $"M.E.AI 不给 cacheWrite，这里却填了 {report.CacheWriteTokens}——"
                + "填 0 会被当成「确实没有缓存写入」";

        return null;
    }

    /// <summary>provider 不报用量时不许凭空造一个。</summary>
    private static async Task<string?> NoUsageNoEventAsync()
    {
        var client = new ScriptedChatClient(
            new ScriptedTurn(new ScriptedUpdate(Text: "ok")));

        var reports = await CollectUsageAsync(client);

        if (reports.Count != 0)
            return $"provider 没报用量却发出了 {reports.Count} 个事件";

        return null;
    }

    /// <summary>三项估算必须各归各位，不能把系统提示算进消息里。</summary>
    /// <remarks>
    /// <b>这条防的是一个容易犯的错</b>：把系统提示词也当普通消息扫一遍，
    /// 于是"系统提示词"那栏永远是 0、而"对话消息"虚高。
    /// 界面上看不出异常，只是拆分失去意义。
    /// </remarks>
    private static Task<string?> BreakdownSplitsAsync()
    {
        var history = new List<AgentMessage>
        {
            new(AgentRole.User, new string('a', 400)),
        };

        var withoutSystem = ContextBreakdown.Measure(null, [], history);
        if (withoutSystem.SystemPromptTokens != 0)
            return Task.FromResult<string?>(
                $"没有系统提示时该栏应为 0，实际 {withoutSystem.SystemPromptTokens}");

        var prompt = new string('b', 800);
        var withSystem = ContextBreakdown.Measure(prompt, [], history);

        if (withSystem.SystemPromptTokens <= 0)
            return Task.FromResult<string?>("给了系统提示却算出 0");

        // 系统提示不能漏进消息估算：两组的历史相同，消息项必须一致。
        if (withSystem.MessageTokens != withoutSystem.MessageTokens)
        {
            return Task.FromResult<string?>(
                $"系统提示被算进了对话消息：{withoutSystem.MessageTokens} → {withSystem.MessageTokens}");
        }

        return Task.FromResult<string?>(null);
    }

    /// <summary>总量必须等于三项之和——不能各算各的。</summary>
    private static Task<string?> BreakdownTotalsAsync()
    {
        var history = new List<AgentMessage> { new(AgentRole.User, "hello world") };
        var estimate = ContextBreakdown.Measure(new string('x', 100), [], history);

        var expected = estimate.SystemPromptTokens
            + estimate.ToolDefinitionTokens
            + estimate.MessageTokens;

        return Task.FromResult(estimate.TotalTokens == expected
            ? null
            : $"总量 {estimate.TotalTokens} 与三项之和 {expected} 不一致");
    }

    /// <summary>
    /// 收到 provider 用量后，下一轮的总量必须以它为基准，而不是纯估算。
    /// </summary>
    /// <remarks>
    /// <b>为什么必须用基准</b>：纯字符估算与真实 token 能差几倍。
    /// provider 回报的输入 token 已包含系统提示与工具 schema 的开销，
    /// 拿它当基准、只估它之后新增的部分，比整段估算准一个量级。
    /// 这与 pi 的 <c>estimateContextTokens</c> 同一思路。
    /// </remarks>
    private static async Task<string?> AnchorUsedAsync()
    {
        // 两轮：第一轮报一个与估算差距极大的真实用量，第二轮看总量是否靠近它。
        //
        // **必须让第一轮产生工具调用**：纯文本轮次会让循环直接结束，
        // 只跑一轮就测不到"第二轮是否用了基准"。
        //
        // 注意 ScriptedUpdate 的用法：**用量与工具调用不能写在同一个 update 里**——
        // 它先判 usage，给了 UsageInput 就只产出 UsageContent，FunctionName 被忽略。
        // 所以工具调用单独一帧，用量再单独一帧（实测踩到，套件一开始只跑了一轮）。
        var client = new ScriptedChatClient(
            new ScriptedTurn(
                new ScriptedUpdate(FunctionCallId: "c1", FunctionName: "noop", ArgumentsJson: "{}"),
                new ScriptedUpdate(UsageInput: 50_000)),
            new ScriptedTurn(new ScriptedUpdate(Text: "done", UsageInput: 50_100)));

        var reports = await CollectContextUsageAsync(client, withDispatcher: true);

        if (reports.Count < 2)
            return $"应至少发出 2 个 ContextUsageReported，实际 {reports.Count} 个";

        // 第一轮没有基准，纯估算；第二轮必须以第一轮的真实用量为基准。
        var first = reports[0];
        var second = reports[1];

        if (first.VerifiedInputTokens is not null)
            return "第一轮还没有任何回报，VerifiedInputTokens 应为 null";

        if (second.VerifiedInputTokens != 50_000)
        {
            return $"第二轮的基准应为 50000，实际 {second.VerifiedInputTokens}——"
                + "锚点没被采用，说明第二轮的估算退化成了纯字符统计";
        }

        // 总量应接近基准，而不是纯估算值。
        if (second.EstimatedTotalTokens is not { } total)
            return "有基准时总量不该是 null";

        var pureEstimate = first.EstimatedTotalTokens ?? 0;
        if (total <= pureEstimate)
        {
            return $"总量 {total} 不大于纯估算 {pureEstimate}，说明基准没起作用";
        }

        return null;
    }

    /// <summary>
    /// 压缩后必须作废锚点——它测的是压缩前的旧历史。
    /// </summary>
    /// <remarks>
    /// <b>不作废的后果</b>：拿一个压缩前的高数值当基准，再加上压缩后的历史，
    /// 会得出"上下文占用比压缩前还大"的荒谬结果，而用户刚看到压缩成功的提示。
    /// </remarks>
    private static async Task<string?> AnchorInvalidatedAsync()
    {
        // 构造一个必然触发压缩的场景：窗口很小、历史很长。
        var longText = new string('z', 40_000);
        var history = new List<AgentMessage>
        {
            new(AgentRole.User, longText),
            new(AgentRole.Assistant, longText),
            new(AgentRole.User, longText),
        };

        var client = new ScriptedChatClient(
            new ScriptedTurn(new ScriptedUpdate(Text: "ok", UsageInput: 100_000)));

        var runtime = new LoopAgentRuntime(
            client,
            new AcceptAllDispatcher(),
            store: null,
            limits: new ContextLimits
            {
                ContextWindowTokens = 12_000,
                RequestOutputReserve = 1_024,
            },
            compactor: new SlidingWindowCompactor(),
            summarize: (_, _) => ValueTask.FromResult("summary"));

        var events = new List<AgentEvent>();
        await foreach (var evt in runtime.RunAsync(
            new AgentRunRequest("s", "t", history, "hi")).ConfigureAwait(false))
        {
            events.Add(evt);
        }

        // 压缩必须真的发生过，否则这条测的是别的东西。
        if (!events.OfType<AgentEvent.ContextCompacted>().Any(e => e.Applied))
        {
            return "这个场景本该触发压缩，但没发生——用例前提失效，先修用例";
        }

        // 压缩之后发的那份报告必须没有基准。
        var lastUsageIndex = events.FindLastIndex(e => e is AgentEvent.ContextUsageReported);
        if (lastUsageIndex < 0) return "没有发出 ContextUsageReported";

        var last = (AgentEvent.ContextUsageReported)events[lastUsageIndex];
        if (last.VerifiedInputTokens is not null)
        {
            return $"压缩后仍有基准 {last.VerifiedInputTokens}——"
                + "它是压缩前的旧值，会让总量虚高";
        }

        return null;
    }

    /// <summary>百分比的分母是窗口，不是压缩预算。</summary>
    /// <remarks>
    /// 用户看的是"这个模型的窗口用了多少"。用压缩预算当分母会让百分比虚高，
    /// 而预算还扣掉了输出预留与固定预留，那是内部判据，不该出现在界面上。
    /// </remarks>
    private static Task<string?> WindowRatioAsync()
    {
        var report = new AgentEvent.ContextUsageReported(
            SystemPromptTokens: 0,
            ToolDefinitionTokens: 0,
            MessageTokens: 0,
            EstimatedTotalTokens: 100,
            VerifiedInputTokens: null,
            ContextWindowTokens: 1_000);

        if (report.WindowRatio is not { } ratio || Math.Abs(ratio - 0.1) > 1e-9)
            return Task.FromResult<string?>($"100/1000 应为 0.1，实际 {report.WindowRatio}");

        // 总量未知时比例也必须是 null，不能给 0——0% 会被读成"完全没占用"。
        var unknown = report with { EstimatedTotalTokens = null };
        if (unknown.WindowRatio is not null)
            return Task.FromResult<string?>($"总量未知时比例应为 null，实际 {unknown.WindowRatio}");

        return Task.FromResult<string?>(null);
    }

    // ── 支撑 ─────────────────────────────────────────────────────

    private static async Task<List<AgentEvent.UsageReported>> CollectUsageAsync(
        ScriptedChatClient client)
    {
        var reports = new List<AgentEvent.UsageReported>();
        await foreach (var evt in RunAsync(client, []).ConfigureAwait(false))
        {
            if (evt is AgentEvent.UsageReported usage) reports.Add(usage);
        }

        return reports;
    }

    private static async Task<List<AgentEvent.ContextUsageReported>> CollectContextUsageAsync(
        ScriptedChatClient client, bool withDispatcher = false)
    {
        var reports = new List<AgentEvent.ContextUsageReported>();
        await foreach (var evt in RunAsync(client, [], withDispatcher).ConfigureAwait(false))
        {
            if (evt is AgentEvent.ContextUsageReported usage) reports.Add(usage);
        }

        return reports;
    }

    private static async IAsyncEnumerable<AgentEvent> RunAsync(
        ScriptedChatClient client, IReadOnlyList<AgentMessage> history,
        bool withDispatcher = false)
    {
        var runtime = new LoopAgentRuntime(
            client,
            withDispatcher ? new SilentDispatcher() : new AcceptAllDispatcher(),
            store: null,
            limits: null,
            compactor: null,
            summarize: null);

        await foreach (var evt in runtime
            .RunAsync(new AgentRunRequest("s", "t", history, "hi"))
            .ConfigureAwait(false))
        {
            yield return evt;
        }
    }

    /// <summary>工具结果的固定内容。用静态字段而不是内联字面量：
    /// <c>JsonElement</c> 的构造需要一段 JSON 文本，内联会变成三层嵌套转义。</summary>
    private static readonly System.Text.Json.JsonElement OkJson =
        System.Text.Json.JsonDocument.Parse("\"ok\"").RootElement;

    /// <summary>接受任意调用并返回固定结果的 dispatcher，用于驱动多轮。</summary>
    private sealed class SilentDispatcher : IToolDispatcher
    {
        public IDisposable Register(IAgentTool tool) => new Noop();

        public IReadOnlyList<ToolDescriptor> Tools => [];

        public ValueTask<ToolDispatchResult> DispatchAsync(
            ToolInvocation invocation, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new ToolDispatchResult(
                invocation.InvocationId, invocation.ToolId,
                ToolCallOutcome.Succeeded,
                new AgentToolResult(OkJson)));


        private sealed class Noop : IDisposable
        {
            public void Dispose() { }
        }
    }

    /// <summary>不派发任何工具的 dispatcher：本套只测上下文计量，不测工具。</summary>
    private sealed class AcceptAllDispatcher : IToolDispatcher
    {
        public IDisposable Register(IAgentTool tool) => new Noop();

        public IReadOnlyList<ToolDescriptor> Tools => [];

        public ValueTask<ToolDispatchResult> DispatchAsync(
            ToolInvocation invocation, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("本套不派发工具");

        private sealed class Noop : IDisposable
        {
            public void Dispose() { }
        }
    }

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
}
