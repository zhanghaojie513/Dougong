namespace Dougong.Agent;

/// <summary>
/// 首版压缩器：滑动窗口 + 保留头尾。
/// </summary>
/// <remarks>
/// 策略刻意保守：
/// <list type="bullet">
/// <item>保留开头若干条（通常是系统指令与任务定义）</item>
/// <item>保留最近若干条（当前工作上下文）</item>
/// <item>只压缩中间区间，且不切断工具调用与其结果的配对</item>
/// <item>不把 assistant 的 tool 请求和随后的 tool 结果分开</item>
/// </list>
/// <para>
/// 真正的摘要需要一次模型调用，由 <see cref="SummarizingCompactionTask"/> 承担；
/// 这里只负责选区间（Select 阶段），不做摘要。
/// </para>
/// </remarks>
public sealed class SlidingWindowCompactor(
    int preservedHead = 2,
    int preservedTail = 6,
    double triggerRatio = 0.75) : IContextCompactor
{
    public CompactionPlan? Plan(CompactionInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var effective = TokenBudget.EffectiveInput(
            input.ContextWindowTokens, input.MaxOutputTokens, input.EstimateErrorTokens);

        var before = TokenBudget.Estimate(input.CommittedHistory);
        var trigger = (int)(effective * triggerRatio);
        if (before <= trigger) return null;

        // 必须同时保留头尾，否则没有可压缩空间。
        var head = Math.Min(preservedHead, input.CommittedHistory.Count);
        var tail = Math.Min(preservedTail, input.CommittedHistory.Count - head);
        var first = head;
        var last = input.CommittedHistory.Count - tail - 1;

        if (last < first)
            return null;  // 历史太短，压缩不了；由调用方决定是否暂停

        // 不把工具调用与它的结果拆开：若区间边界切在 Tool 消息上，向前扩展到 Assistant。
        (first, last) = ExpandToKeepToolPairs(input.CommittedHistory, first, last);

        // 绝不压缩最近一次计划更新。
        // 计划是模型自己写的全局目标，被摘要掉之后模型就"忘了自己在做什么"——
        // 这正是长任务在第 N 步跑偏的典型机制。保留它对长任务最有用。
        last = TrimToExcludeLatestPlan(input.CommittedHistory, first, last);

        var compressedCount = last - first + 1;
        if (compressedCount <= 0) return null;

        var middle = input.CommittedHistory.Skip(first).Take(compressedCount).ToList();
        var after = before - TokenBudget.Estimate(middle) + EstimatedSummaryTokens(middle);

        return new CompactionPlan(
            first, last, head, before, after,
            $"历史 {before} tokens 超过触发阈值 {trigger}（有效预算 {effective} 的 {triggerRatio:P0}）。");
    }

    /// <summary>
    /// 若最近一次计划更新落在待压缩区间内，把区间末端前移到它之前。
    /// </summary>
    /// <remarks>
    /// <b>为什么单独保护计划</b>：`todo_write` 是模型用于维护全局目标的工具
    /// （见 <see cref="PlanTool"/>）。它的结果就是当前完整计划。
    /// 一旦被摘要掉，模型就失去了"我要做什么"的显式记录，
    /// 而摘要本身可能概括不出足够的任务约束——这是长任务目标漂移的典型成因。
    /// <para>
    /// <b>为什么不保护所有工具结果</b>：那等于取消压缩。计划是唯一一类
    /// "内容本身就是当前状态、一旦丢失无法从别处重建"的工具结果。
    /// </para>
    /// <para>
    /// <b>边界</b>：若计划就在区间起点之前（已被头部保留或被更早的压缩处理过），
    /// 这里不做任何事。
    /// </para>
    /// <para>
    /// <b>代价（必须承认）</b>：若计划紧跟在头部保留区之后，砍掉它之后的可压缩区间
    /// 会变成空，<see cref="Plan"/> 随之返回 <c>null</c>——也就是说
    /// **整个压缩被放弃，上下文继续超限**。这是刻意的取舍：
    /// 宁可让预算紧张被上层如实报告（<see cref="CompactionPlan"/> 返回 null 时
    /// 调用方必须自己决定怎么办），也不要静默丢掉模型的全局目标。
    /// 计划越早写、后续历史越长，这个代价越大。
    /// </para>
    /// </remarks>
    private static int TrimToExcludeLatestPlan(
        IReadOnlyList<AgentMessage> history, int first, int last)
    {
        var planIndex = -1;
        for (var i = last; i >= first; i--)
        {
            if (IsPlanMessage(history[i])) { planIndex = i; break; }
        }

        if (planIndex < 0) return last;

        // 计划所在的整组调用（assistant + tool）都要保留：
        // 只留 tool 消息会让 tool_call_id 找不到对应的调用，provider 会拒整轮请求。
        // ExpandToKeepToolPairs 已保证区间边界不会切在 Tool 上，
        // 所以这里向前跳过 Tool 后，落点要么是发起它的 Assistant，要么是普通消息。
        var boundary = planIndex;
        while (boundary > first && history[boundary].Role == AgentRole.Tool)
            boundary--;

        return boundary - 1;
    }

    private static bool IsPlanMessage(AgentMessage message) =>
        message.Role is AgentRole.Tool
        && string.Equals(message.ToolId, PlanTool.ToolId, StringComparison.Ordinal);

    /// <summary>摘要本身也占 token，按压缩区间的 10% 估算并设下限。</summary>
    private static int EstimatedSummaryTokens(IReadOnlyList<AgentMessage> middle) =>
        Math.Max(64, TokenBudget.Estimate(middle) / 10);

    /// <summary>
    /// 把区间边界外扩到工具配对完整：Tool 消息必须与其前的 Assistant 请求同进退。
    /// </summary>
    private static (int First, int Last) ExpandToKeepToolPairs(
        IReadOnlyList<AgentMessage> history, int first, int last)
    {
        // 若末尾落在 Tool 上，向前扩展包含对应的 Assistant。
        while (last + 1 < history.Count && history[last].Role == AgentRole.Tool)
            last++;

        // 若开头是 Tool，向前扩展直到包含发起它的 Assistant（或到 0）。
        while (first > 0 && history[first].Role == AgentRole.Tool)
            first--;

        // 若开头是 Assistant 且其后紧跟 Tool，说明这是一组调用，必须整体包含。
        while (first > 0
               && history[first].Role == AgentRole.Assistant
               && first + 1 < history.Count
               && history[first + 1].Role == AgentRole.Tool)
            first--;

        return (first, last);
    }
}

/// <summary>
/// 带检查点的摘要任务：Select → Summarize →（失败）Retry → Completed / Failed。
/// </summary>
/// <remarks>
/// 每次 <see cref="AdvanceAsync"/> 只推进一个阶段，使崩溃后能从检查点续跑，
/// 而不是把一次长摘要压进单个不可恢复的调用。
/// </remarks>
public sealed class SummarizingCompactionTask : ICompactionTask
{
    private readonly CompactionPlan _plan;
    private readonly Func<string, CancellationToken, ValueTask<string>> _summarize;
    private readonly Func<IReadOnlyList<AgentMessage>, string> _render;
    private readonly IReadOnlyList<AgentMessage> _history;
    private readonly int _maxAttempts;

    private CompactionCheckpoint _checkpoint;

    public SummarizingCompactionTask(
        CompactionPlan plan,
        IReadOnlyList<AgentMessage> history,
        Func<string, CancellationToken, ValueTask<string>> summarize,
        Func<IReadOnlyList<AgentMessage>, string> render,
        int maxAttempts = 2)
    {
        _plan = plan;
        _history = history;
        _summarize = summarize;
        _render = render;
        _maxAttempts = maxAttempts;
        _checkpoint = new CompactionCheckpoint(
            CompactionPhase.Select, 0,
            plan.FirstIndex, plan.LastIndex, plan.PreservedHeadCount,
            // TailIndex 是"被压缩区间的末端"，不是历史末端：
            // 应用时 LastIndex+1 之后的内容都原样保留（含压缩期间新增的记录）。
            plan.LastIndex,
            null, null);
    }

    public CompactionCheckpoint Checkpoint => _checkpoint;

    public async ValueTask<CompactionCheckpoint> AdvanceAsync(
        CancellationToken cancellationToken = default)
    {
        if (_checkpoint.IsTerminal) return _checkpoint;

        switch (_checkpoint.Phase)
        {
            case CompactionPhase.Select:
                _checkpoint = _checkpoint with { Phase = CompactionPhase.Summarize };
                return _checkpoint;

            case CompactionPhase.Summarize:
            case CompactionPhase.Retry:
                return await SummarizeAsync(cancellationToken).ConfigureAwait(false);

            default:
                return _checkpoint;
        }
    }

    private async ValueTask<CompactionCheckpoint> SummarizeAsync(CancellationToken cancellationToken)
    {
        var attempt = _checkpoint.Attempt + 1;
        var middle = _history.Skip(_plan.FirstIndex)
            .Take(_plan.LastIndex - _plan.FirstIndex + 1)
            .ToList();

        try
        {
            var summary = await _summarize(_render(middle), cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(summary))
                throw new InvalidOperationException("摘要为空。");

            _checkpoint = _checkpoint with
            {
                Phase = CompactionPhase.Completed,
                Attempt = attempt,
                Summary = summary,
                FailureReason = null,
            };
            return _checkpoint;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // 取消不是失败：保留检查点，下次可继续。原历史不受影响。
            throw;
        }
        catch (Exception ex)
        {
            var exhausted = attempt >= _maxAttempts;
            _checkpoint = _checkpoint with
            {
                Phase = exhausted ? CompactionPhase.Failed : CompactionPhase.Retry,
                Attempt = attempt,
                FailureReason = $"{ex.GetType().Name}: {ex.Message}",
            };
            return _checkpoint;
        }
    }
}

/// <summary>
/// 把摘要应用到历史。**只有 Completed 的检查点才允许替换**；
/// 失败时返回原历史，绝不覆盖。
/// </summary>
public static class CompactionApply
{
    /// <summary>
    /// 用摘要替换被压缩区间，保留头尾。
    /// 返回 null 表示检查点未完成，调用方必须继续使用原历史。
    /// </summary>
    public static IReadOnlyList<AgentMessage>? TryApply(
        IReadOnlyList<AgentMessage> history, CompactionCheckpoint checkpoint)
    {
        ArgumentNullException.ThrowIfNull(history);
        ArgumentNullException.ThrowIfNull(checkpoint);

        if (checkpoint.Phase != CompactionPhase.Completed || checkpoint.Summary is null)
            return null;

        // 检查点可能来自更早的时刻；历史若已变长，只替换到检查点记录的末端。
        var tailStart = checkpoint.TailIndex + 1;
        if (tailStart > history.Count) return null;

        var result = new List<AgentMessage>(history.Count);
        result.AddRange(history.Take(checkpoint.FirstIndex));
        result.Add(new AgentMessage(AgentRole.System,
            $"[已压缩 {checkpoint.LastIndex - checkpoint.FirstIndex + 1} 条历史]\n{checkpoint.Summary}"));
        if (tailStart <= history.Count)
            result.AddRange(history.Skip(tailStart));

        return result;
    }
}
