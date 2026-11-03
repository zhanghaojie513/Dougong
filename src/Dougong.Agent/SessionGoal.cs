using System.Text.Json;

namespace Dougong.Agent;

/// <summary>
/// 会话内目标：一条跨轮次追踪的完成判据。
/// </summary>
/// <remarks>
/// <b>解决什么问题</b>：用户说"把这个模块的测试补到 80% 覆盖"，这是一件
/// 要跑很多轮的事，而每一轮结束时模型并不知道"还差多少"。
/// 目标把它写下来：<b>客观的完成判据 + 当前阶段 + 已跑几轮</b>，
/// 于是下一轮开始时模型能自己判断"该继续还是已经够了"。
///
/// <para>
/// <b>照 dsh 的 <c>dsh-goal</c>，语义对齐的部分</b>：
/// <list type="bullet">
/// <item><b>四阶段</b>：<c>active</c> / <c>paused</c> / <c>blocked</c> / <c>complete</c>。
/// <c>blocked</c> 是唯一的"被问题卡住"状态，带机器可路由的 code 与人可读的说明。</item>
/// <item><b>修订号做乐观并发</b>：每次实质修改 +1，改的时候必须带上观察到的修订号。
/// 不带就允许覆盖会让两个来源互相踩掉对方的修改，而两边都以为自己成功了。</item>
/// <item><b>轮次上限</b>：防止"永远差一点"的目标把会话拖死。</item>
/// </list>
/// </para>
///
/// <para>
/// <b>不做自动续轮，这是最重要的取舍</b>：dsh 的目标会驱动一个
/// "自动继续下一轮"的消费者（<c>GoalActivation</c>），于是模型说完一句
/// 就自动被推着往下跑。我们不做——理由三条：
/// </para>
/// <list type="number">
/// <item><b>它与现有边界冲突</b>：<see cref="LoopAgentRuntime"/> 的契约是
/// "一次运行一次返回"。自动续轮要么在里面加外层循环，要么在宿主编排——
/// 前者让"一次运行"不再是可达终点，后者是宿主的事。</item>
/// <item><b>失败时会无限消耗</b>：一个卡住的目标 + 自动续轮 = 烧额度直到
/// 轮次上限，而用户可能只是去开会了。</item>
/// <item><b>没有消费者</b>：能接续轮的宿主（桌面端）还没提出这个需求。</item>
/// </list>
///
/// <para>
/// 所以这里只做<b>状态与工具</b>：目标被记录、能查、能改、能标记完成。
/// "要不要开始下一轮"由人或宿主决定，不由 SDK 自动决定。
/// </para>
/// </remarks>
public sealed class SessionGoal
{
    /// <summary>默认轮次上限。</summary>
    /// <remarks>
    /// <b>8 与工具轮次上限同值</b>：两者都在防"无限做下去"。
    /// 目标轮次是"跨会话轮"，工具轮是"一轮里的工具调用"——
    /// 数量级上取同一个数不至于让目标明显先耗光。
    /// </remarks>
    public const int DefaultMaxGoalRounds = 8;

    /// <summary>最小 <c>blocked</c> 轮次门槛。</summary>
    /// <remarks>
    /// <b>不能一上来就宣布卡住</b>：dsh 的默认是"至少跑满三轮才允许 blocked"。
    /// 理由是模型很容易在第一次失败后就宣布"做不了"，
    /// 而那通常只是尝试得不够。
    /// </remarks>
    public const int MinRoundsBeforeBlocked = 3;

    private readonly object _sync = new();
    private GoalSnapshot? _current;

    /// <summary>当前目标；null 表示没有。</summary>
    public GoalSnapshot? Current
    {
        get { lock (_sync) return _current; }
    }

    /// <summary>
    /// 创建一个目标。
    /// </summary>
    /// <remarks>
    /// <b>已有活跃目标时拒绝</b>：一个会话同时追两个目标，模型无法判断
    /// "现在这一轮是在推进哪个"。要换目标必须先 <c>complete</c> 或 <c>clear</c>。
    /// </remarks>
    /// <param name="objective">完成判据。<b>必须是可判定的</b>，见 create_goal 工具的描述。</param>
    /// <param name="maxGoalRounds">轮次上限；null 用默认。</param>
    /// <exception cref="InvalidOperationException">已有未结束的目标。</exception>
    /// <exception cref="ArgumentException">判据为空或上限非法。</exception>
    public GoalSnapshot Create(string objective, int? maxGoalRounds = null)
    {
        var text = NormalizeObjective(objective);
        var rounds = maxGoalRounds ?? DefaultMaxGoalRounds;

        if (rounds < 1)
            throw new ArgumentException($"轮次上限必须 ≥ 1，实际 {rounds}。", nameof(maxGoalRounds));

        lock (_sync)
        {
            if (_current is { Phase: not GoalPhase.Complete } active)
                throw new InvalidOperationException(
                    $"已经有一个进行中的目标（{active.Id}，{active.Phase}）。"
                    + "先把它标记完成或清除，再创建新的。");

            _current = new GoalSnapshot(
                Id: $"goal-{Guid.NewGuid().ToString("N")[..12]}",
                Revision: 1,
                Objective: text,
                Phase: GoalPhase.Active,
                BlockedReason: null,
                MaxGoalRounds: rounds,
                RoundsStarted: 0,
                CreatedAt: DateTimeOffset.UtcNow,
                UpdatedAt: DateTimeOffset.UtcNow);

            return _current;
        }
    }

    /// <summary>
    /// 修改目标（编辑判据、暂停、恢复、完成、阻塞、清除）。
    /// </summary>
    /// <remarks>
    /// <b>必须带上观察到的修订号</b>，不符即拒绝并回传当前值。
    /// 这是纯乐观并发，与消息反馈同一判据——不带就只能"最后写的赢"，
    /// 而那会让两个来源互相踩掉对方的修改。
    /// </remarks>
    /// <param name="request">修改请求。</param>
    /// <returns>修改结果。</returns>
    public GoalUpdateResult Update(GoalUpdateRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        lock (_sync)
        {
            var current = _current;
            if (current is null)
                return new GoalUpdateResult.NotFound();

            if (!string.Equals(current.Id, request.GoalId, StringComparison.Ordinal))
                return new GoalUpdateResult.NotFound();

            if (current.Revision != request.Revision)
                return new GoalUpdateResult.VersionConflict(current);

            switch (request.Action)
            {
                case GoalAction.Clear:
                    _current = null;
                    return new GoalUpdateResult.Cleared(current);

                case GoalAction.Blocked:
                    // **轮次门槛**：跑得不够多就宣布卡住，通常是尝试不足。
                    if (current.RoundsStarted < Math.Min(MinRoundsBeforeBlocked, current.MaxGoalRounds))
                    {
                        return new GoalUpdateResult.TooEarlyToBlock(
                            current, MinRoundsBeforeBlocked);
                    }

                    if (string.IsNullOrWhiteSpace(request.ReasonCode)
                        || string.IsNullOrWhiteSpace(request.ReasonMessage))
                    {
                        return new GoalUpdateResult.ReasonRequired(current);
                    }

                    return Mutate(current, c => c with
                    {
                        Revision = c.Revision + 1,
                        Phase = GoalPhase.Blocked,
                        BlockedReason = new GoalBlockReason(
                            request.ReasonCode!.Trim(), request.ReasonMessage!.Trim()),
                        UpdatedAt = DateTimeOffset.UtcNow,
                    });

                default:
                    break;
            }

            var next = request.Action switch
            {
                GoalAction.Edit => ApplyEdit(current, request),
                GoalAction.Pause => current with { Phase = GoalPhase.Paused },
                GoalAction.Resume => current with { Phase = GoalPhase.Active },
                GoalAction.Complete => current with { Phase = GoalPhase.Complete },
                _ => null,
            };

            if (next is null) return new GoalUpdateResult.UnknownAction(request.Action.ToString());

            // 阶段没变、判据没变 → 不递增修订号（no-op 不制造假的新版本）。
            var changed = next.Phase != current.Phase
                || !string.Equals(next.Objective, current.Objective, StringComparison.Ordinal)
                || next.MaxGoalRounds != current.MaxGoalRounds;

            if (!changed) return new GoalUpdateResult.Succeeded(current, Changed: false);

            return Mutate(current, _ => next with
            {
                Revision = current.Revision + 1,
                // **离开 blocked 时清掉原因**：留着会让界面一直显示
                // 一个已经不再成立的阻塞理由。
                BlockedReason = next.Phase is GoalPhase.Blocked ? next.BlockedReason : null,
                UpdatedAt = DateTimeOffset.UtcNow,
            });
        }
    }

    /// <summary>
    /// 记一轮开始。
    /// </summary>
    /// <remarks>
    /// <b>只有 active 才计数</b>：暂停或阻塞时开始的轮次不该算进上限，
    /// 否则"暂停一下再继续"会白白消耗额度。
    ///
    /// <para>
    /// <b>创建目标的那一轮不计入</b>：运行时在轮首调用本方法，
    /// 而目标通常是在轮内才被创建的。所以 <see cref="GoalSnapshot.RoundsStarted"/>
    /// 的语义是"目标存在之后被推进的轮数"（与 dsh 的
    /// "admitted continuation rounds" 一致），<b>不含创建轮</b>。
    /// </para>
    /// <para>
    /// 这条有一个连带后果：要标 <c>blocked</c> 需要
    /// 至少 <see cref="MinRoundsBeforeBlocked"/> 轮，而第一轮是创建轮——
    /// 也就是**最早在第 4 轮才能宣布阻塞**。这不是 bug，但排期时要算进去。
    /// 端到端跑过一遍确认了这个行为。
    /// </para>
    /// </remarks>
    /// <returns>本轮的序号；目标的阶段不允许计数时返回 null。</returns>
    public int? BeginRound()
    {
        lock (_sync)
        {
            if (_current is not { Phase: GoalPhase.Active } goal) return null;
            if (goal.RoundsStarted >= goal.MaxGoalRounds) return null;

            _current = goal with { RoundsStarted = goal.RoundsStarted + 1 };
            return _current.RoundsStarted;
        }
    }

    /// <summary>是否已达轮次上限。</summary>
    public bool IsRoundLimitReached
    {
        get
        {
            lock (_sync)
            {
                return _current is { } g && g.RoundsStarted >= g.MaxGoalRounds;
            }
        }
    }

    private GoalUpdateResult Mutate(GoalSnapshot current, Func<GoalSnapshot, GoalSnapshot> change) =>
        new GoalUpdateResult.Succeeded(_current = change(current), Changed: true);

    private static GoalSnapshot ApplyEdit(GoalSnapshot current, GoalUpdateRequest request)
    {
        var objective = request.Objective is null
            ? current.Objective
            : NormalizeObjective(request.Objective);

        var rounds = request.MaxGoalRounds ?? current.MaxGoalRounds;
        if (rounds < 1)
            throw new ArgumentException($"轮次上限必须 ≥ 1，实际 {rounds}。", nameof(request));

        return current with { Objective = objective, MaxGoalRounds = rounds };
    }

    private static string NormalizeObjective(string objective)
    {
        if (string.IsNullOrWhiteSpace(objective))
            throw new ArgumentException("目标判据不能为空。", nameof(objective));

        return objective.Trim();
    }
}

/// <summary>目标的持久阶段。</summary>
public enum GoalPhase
{
    /// <summary>进行中。</summary>
    Active,

    /// <summary>用户暂停。</summary>
    Paused,

    /// <summary>被问题卡住。带原因。</summary>
    Blocked,

    /// <summary>已完成。</summary>
    Complete,
}

/// <summary>目标快照。</summary>
/// <param name="Id">目标标识。</param>
/// <param name="Revision">修订号；每次实质修改 +1。</param>
/// <param name="Objective">完成判据。</param>
/// <param name="Phase">当前阶段。</param>
/// <param name="BlockedReason">仅在 <see cref="GoalPhase.Blocked"/> 时非空。</param>
/// <param name="MaxGoalRounds">轮次上限。</param>
/// <param name="RoundsStarted">已开始的轮数。</param>
/// <param name="CreatedAt">创建时刻。</param>
/// <param name="UpdatedAt">最近修改时刻。</param>
public sealed record GoalSnapshot(
    string Id,
    int Revision,
    string Objective,
    GoalPhase Phase,
    GoalBlockReason? BlockedReason,
    int MaxGoalRounds,
    int RoundsStarted,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt)
{
    /// <summary>还能跑几轮。</summary>
    public int RoundsRemaining => Math.Max(0, MaxGoalRounds - RoundsStarted);
}

/// <summary>阻塞原因。</summary>
/// <param name="Code">稳定的短分类（小写 kebab-case），供路由。</param>
/// <param name="Message">人可读的说明。</param>
public sealed record GoalBlockReason(string Code, string Message);

/// <summary>对目标的操作。</summary>
public enum GoalAction
{
    /// <summary>改判据或上限。</summary>
    Edit,

    /// <summary>暂停。</summary>
    Pause,

    /// <summary>恢复。</summary>
    Resume,

    /// <summary>标记完成。</summary>
    Complete,

    /// <summary>标记为被卡住。</summary>
    Blocked,

    /// <summary>清除目标。</summary>
    Clear,
}

/// <summary>一次目标修改请求。</summary>
/// <param name="GoalId">目标标识（必填）。</param>
/// <param name="Revision">观察到的修订号（必填）。</param>
/// <param name="Action">动作。</param>
/// <param name="Objective">新判据（仅 edit）。</param>
/// <param name="MaxGoalRounds">新上限（仅 edit）。</param>
/// <param name="ReasonCode">阻塞分类（仅 blocked）。</param>
/// <param name="ReasonMessage">阻塞说明（仅 blocked）。</param>
public sealed record GoalUpdateRequest(
    string GoalId,
    int Revision,
    GoalAction Action,
    string? Objective = null,
    int? MaxGoalRounds = null,
    string? ReasonCode = null,
    string? ReasonMessage = null);

/// <summary>目标修改结果。</summary>
public abstract record GoalUpdateResult
{
    /// <summary>成功。</summary>
    /// <param name="Goal">修改后的目标。</param>
    /// <param name="Changed">
    /// 是否发生了实质变化。false 表示请求被接受但值没变（no-op），
    /// 此时修订号不变——不制造假的新版本。
    /// </param>
    public sealed record Succeeded(GoalSnapshot Goal, bool Changed) : GoalUpdateResult;

    /// <summary>目标已被清除。</summary>
    /// <param name="Goal">清除前的目标。</param>
    public sealed record Cleared(GoalSnapshot Goal) : GoalUpdateResult;

    /// <summary>没有这个目标。</summary>
    public sealed record NotFound : GoalUpdateResult;

    /// <summary>修订号不符。</summary>
    /// <param name="Current">当前权威值，调用方据此协调，不必再读一次。</param>
    public sealed record VersionConflict(GoalSnapshot Current) : GoalUpdateResult;

    /// <summary>宣布阻塞太早（轮次不够）。</summary>
    /// <param name="Current">当前目标。</param>
    /// <param name="MinimumRounds">至少需要跑满几轮。</param>
    public sealed record TooEarlyToBlock(GoalSnapshot Current, int MinimumRounds) : GoalUpdateResult;

    /// <summary>标记阻塞但没给原因。</summary>
    /// <param name="Current">当前目标。</param>
    public sealed record ReasonRequired(GoalSnapshot Current) : GoalUpdateResult;

    /// <summary>不认识的动作。</summary>
    /// <param name="Action">收到的动作名。</param>
    public sealed record UnknownAction(string Action) : GoalUpdateResult;
}
