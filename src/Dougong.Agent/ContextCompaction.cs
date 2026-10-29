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
    double triggerRatio = 0.75,
    double retainRatio = 0.16) : IContextCompactor
{
    /// <summary>保留最近历史的条数下限。</summary>
    /// <remarks>
    /// <b>条数下限与 token 比例互补，不是二选一</b>：小窗口下 16% 可能只够
    /// 2–3 条，需要条数保底；大窗口下条数远远不够，需要比例兜住
    /// "最近若干轮完整对话"。
    /// </remarks>
    private readonly int _preservedTail = preservedTail >= 0
        ? preservedTail
        : throw new ArgumentOutOfRangeException(nameof(preservedTail), "保留条数不能为负。");

    /// <summary>保留最近历史占有效预算的比例。</summary>
    /// <remarks>
    /// <b>必须严格小于触发比例</b>，构造时校验。
    /// 否则压缩后仍越过阈值，每次调用都压一次——摘要要花模型调用，
    /// 表现是会话越来越慢、账单越来越高，且**没有任何错误提示**。
    /// <para>
    /// 0.16 与 dsh 的 <c>retainRatio</c> 一致，按 1M 窗口设计：触发 0.75、
    /// 保留 0.16，压缩后降到 0.16–0.2，能跑很久再触发。
    /// 按 128K 那种小窗口反推会得出偏小的值，而 128K 已不是典型值。
    /// </para>
    /// </remarks>
    private readonly double _retainRatio = retainRatio > 0 && retainRatio < 1
        ? retainRatio
        : throw new ArgumentOutOfRangeException(nameof(retainRatio), "保留比例必须落在 (0, 1)。");

    private readonly double _triggerRatio = triggerRatio > 0 && triggerRatio < 1
        && retainRatio < triggerRatio
            ? triggerRatio
            : throw new ArgumentOutOfRangeException(
                nameof(triggerRatio),
                $"触发比例必须落在 (0, 1) 且大于保留比例 {retainRatio}；"
                + "否则压缩后仍高于阈值，每次调用都会重复压缩。");

    private readonly int _preservedHead = preservedHead >= 0
        ? preservedHead
        : throw new ArgumentOutOfRangeException(nameof(preservedHead), "保留条数不能为负。");

    public CompactionPlan? Plan(CompactionInput input, bool force = false)
    {
        ArgumentNullException.ThrowIfNull(input);
        var effective = TokenBudget.EffectiveInput(
            input.ContextWindowTokens, input.MaxOutputTokens, input.EstimateErrorTokens);

        var before = TokenBudget.Estimate(input.CommittedHistory);
        var trigger = (int)(effective * _triggerRatio);

        // force：跳过阈值检查。用于 provider 已判定溢出的情形——
        // 那时按窗口算出的阈值本身就是错的（窗口配置不可信），
        // 再拿它当门槛会让恢复逻辑永远压不了东西。实测踩到过：
        // 窗口配 200 万、历史 2 万，provider 报溢出，恢复时 Plan 返回 null。
        // 结构约束（头尾保留、工具配对）仍然遵守，压不了就返回 null。
        if (!force && before <= trigger) return null;

        // 必须同时保留头尾，否则没有可压缩空间。
        var head = Math.Min(_preservedHead, input.CommittedHistory.Count);
        var tail = ResolveTail(input.CommittedHistory, head, effective);
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
            $"历史 {before} tokens 超过触发阈值 {trigger}（有效预算 {effective} 的 {_triggerRatio:P0}）。");
    }

    /// <summary>
    /// 算出尾部保留多少条：条数下限与 token 比例取较大者，且至少留 1 条可压。
    /// </summary>
    /// <remarks>
    /// <b>为什么不能只用条数</b>：固定 6 条在两种极端下都不对——6 条短对话约
    /// 300 token（保住了但没意义），6 条含工具输出的消息可能两万 token
    /// （保留太多，压完等于没压，很快又触发）。
    /// <para>
    /// <b>为什么不能只用比例</b>：小窗口下 16% 可能只够 2–3 条，会把最近的工作上下文切碎。
    /// </para>
    /// <para>
    /// <b>必须留至少 1 条可压</b>：否则 <c>last &lt; first</c>，压缩永远返回 null。
    /// 这是加比例约束后引入的新风险——比例算出的保留量可能吃掉整个历史，
    /// 不加这个上限就会让小窗口用户从"压得太狠"变成"完全压不动"。
    /// </para>
    /// </remarks>
    private int ResolveTail(IReadOnlyList<AgentMessage> history, int head, int effective)
    {
        // 至少留 1 条能被压缩，否则没有可压区间。
        var maxTail = Math.Max(0, history.Count - head - 1);
        if (maxTail == 0) return 0;

        // 条数下限：保证最近若干条完整（短会话靠它）。
        var byCount = Math.Min(_preservedTail, maxTail);
        if (effective <= 0) return byCount;

        // 比例下限：保证最近若干轮完整（长会话靠它）。
        // 从尾部往前累加，直到达到预算或碰到 head。
        var tailBudget = (int)(effective * _retainRatio);
        if (tailBudget <= 0) return byCount;

        var accumulated = 0;
        var byRatio = 0;

        for (var i = history.Count - 1; i >= head; i--)
        {
            var cost = TokenBudget.Estimate(history[i].Content);
            if (accumulated + cost > tailBudget) break;

            accumulated += cost;
            byRatio++;
        }

        // 取较大者：条数保底优先，比例在长会话时提供更大的保留量。
        return Math.Min(maxTail, Math.Max(byCount, byRatio));
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
