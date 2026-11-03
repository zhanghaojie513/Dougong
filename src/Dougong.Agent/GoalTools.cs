using System.Text.Json;

namespace Dougong.Agent;

/// <summary>
/// 创建一个会话内目标。
/// </summary>
/// <remarks>
/// <b>它不是"待办列表"</b>：待办是这一轮要做哪几步，目标是**跨轮次的完成判据**。
/// 两者混用会让模型把"记下步骤"当成"立了目标"。
///
/// <para>
/// <b>判据必须可判定</b>：工具描述里说清这一点，因为"让代码更健壮"这种目标
/// 永远无法宣布完成，而"把 <c>OrderService</c> 的测试覆盖到 80%"可以。
/// 这不是靠校验能拦住的（自然语言判不出），只能靠描述引导——
/// 而描述会随上下文被压缩/遗忘，所以 <c>get_goal</c> 要能把判据原样回给模型。
/// </para>
///
/// <para>
/// <b>权限口径照 dsh</b>：create / edit / pause / resume 要求"直接的人类请求"。
/// 我们把它落成 <see cref="ToolPermission"/> 的 Execute——会改变会话的走向，
/// 宿主因此可以拦。**不区分"是不是人类请求"**：那需要会话里追踪
/// "这条用户输入来自人还是自动续轮"，而我们不做自动续轮（见 D-33），
/// 所以这个区分目前没有意义。
/// </para>
/// </remarks>
public sealed class CreateGoalTool : IAgentTool
{
    /// <summary>工具 ID。</summary>
    public const string ToolId = "create_goal";

    private readonly SessionGoal _goal;

    /// <summary>构造。</summary>
    /// <param name="goal">目标状态。</param>
    public CreateGoalTool(SessionGoal goal)
    {
        ArgumentNullException.ThrowIfNull(goal);
        _goal = goal;
    }

    public ToolDescriptor Descriptor { get; } = new(
        ToolId, ToolId, "1.0.0",
        "立一个跨轮次的目标：写清**可判定的完成标准**。适用于需要多轮才能做完的事"
        + "（补测试覆盖、重构一个模块），不适用于单轮就能做完的工作。"
        + "判据要能被验证（\"把 X 的测试覆盖到 80%\"），不要写无法判定的"
        + "（\"让代码更健壮\"）。一个会话同时只能有一个进行中的目标。",
        JsonDocument.Parse(
            """
            {
              "type": "object",
              "properties": {
                "objective": {
                  "type": "string",
                  "description": "可判定的完成标准。写清\"怎样算做完\"，而不是\"要做什么\"。"
                },
                "max_goal_rounds": {
                  "type": "number",
                  "description": "可选的轮次上限（正整数）。省略则用默认值。"
                }
              },
              "required": ["objective"],
              "additionalProperties": false
            }
            """).RootElement.Clone());

    /// <summary>改变会话走向，报 Execute 以便宿主可拦。</summary>
    public ToolPermission DescribePermission(JsonElement arguments) =>
        new(ToolAccess.Execute, IsKnown: true, Paths: [], Commands: [], Domains: []);

    public ToolExecutionPlan DescribeExecution(JsonElement arguments) =>
        new(ToolConcurrency.Serial);

    public ValueTask<AgentToolResult> ExecuteAsync(
        ToolInvocation invocation, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(invocation);

        var objective = ReadString(invocation.Arguments, "objective");
        if (string.IsNullOrWhiteSpace(objective))
            return ValueTask.FromResult(GoalResults.Fail("缺少 objective 参数。"));

        int? rounds = null;
        if (invocation.Arguments.TryGetProperty("max_goal_rounds", out var r)
            && r.ValueKind is JsonValueKind.Number
            && r.TryGetInt32(out var value))
        {
            rounds = value;
        }

        try
        {
            var goal = _goal.Create(objective, rounds);
            return ValueTask.FromResult(GoalResults.Ok(goal, "目标已创建。"));
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return ValueTask.FromResult(GoalResults.Fail(ex.Message));
        }
    }

    private static string? ReadString(JsonElement arguments, string name) =>
        arguments.TryGetProperty(name, out var element)
            && element.ValueKind is JsonValueKind.String
                ? element.GetString()
                : null;
}

/// <summary>读当前目标。</summary>
/// <remarks>
/// <b>它要把判据原样回给模型</b>：那是模型判断"还差多少"的唯一依据。
/// 而上下文压缩可能把创建目标那轮压掉——此时只有这里能拿到判据。
/// </remarks>
public sealed class GetGoalTool : IAgentTool
{
    /// <summary>工具 ID。</summary>
    public const string ToolId = "get_goal";

    private readonly SessionGoal _goal;

    /// <summary>构造。</summary>
    /// <param name="goal">目标状态。</param>
    public GetGoalTool(SessionGoal goal)
    {
        ArgumentNullException.ThrowIfNull(goal);
        _goal = goal;
    }

    public ToolDescriptor Descriptor { get; } = new(
        ToolId, ToolId, "1.0.0",
        "读当前目标：判据、阶段、已跑几轮、还剩几轮。"
        + "改目标前也要先调它拿到 id 与 revision。",
        JsonDocument.Parse(
            """
            { "type": "object", "properties": {}, "additionalProperties": false }
            """).RootElement.Clone());

    /// <summary>纯内存读取。</summary>
    public ToolPermission DescribePermission(JsonElement arguments) =>
        new(ToolAccess.None, IsKnown: true, Paths: [], Commands: [], Domains: []);

    public ToolExecutionPlan DescribeExecution(JsonElement arguments) =>
        new(ToolConcurrency.Serial);

    public ValueTask<AgentToolResult> ExecuteAsync(
        ToolInvocation invocation, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(invocation);

        var goal = _goal.Current;
        return ValueTask.FromResult(goal is null
            ? GoalResults.Fail("当前没有目标。")
            : GoalResults.Ok(goal, "这是当前目标。"));
    }
}

/// <summary>修改当前目标。</summary>
/// <remarks>
/// <b>一个工具带 action 参数，而不是五个工具</b>：与 <c>subagent</c> 那边相反。
/// 判据是**模型会不会选错**：<c>subagent</c> 的三个动作后果完全不同
/// （启动 / 列表 / 取结果），混在一起它经常选错；而这里几个动作共享同一套参数
/// （id + revision），且它们在语义上是"对同一个目标做不同的事"——
/// 拆开反而要在五个描述里重复"先拿 id 和 revision"。
/// </remarks>
public sealed class UpdateGoalTool : IAgentTool
{
    /// <summary>工具 ID。</summary>
    public const string ToolId = "update_goal";

    private readonly SessionGoal _goal;

    /// <summary>构造。</summary>
    /// <param name="goal">目标状态。</param>
    public UpdateGoalTool(SessionGoal goal)
    {
        ArgumentNullException.ThrowIfNull(goal);
        _goal = goal;
    }

    public ToolDescriptor Descriptor { get; } = new(
        ToolId, ToolId, "1.0.0",
        "修改当前目标。必须带上 get_goal 返回的 goal_id 与 revision"
        + "（修订号不符会被拒绝，并回传当前值）。"
        + "blocked 需要先跑满若干轮，且必须给出 reason_code 与 reason_message。",
        JsonDocument.Parse(
            """
            {
              "type": "object",
              "properties": {
                "goal_id": { "type": "string", "description": "get_goal 返回的目标 id。" },
                "revision": { "type": "number", "description": "get_goal 返回的修订号。" },
                "action": {
                  "type": "string",
                  "enum": ["edit", "pause", "resume", "complete", "blocked", "clear"],
                  "description": "要做什么。clear 会删除目标。"
                },
                "objective": { "type": "string", "description": "新的完成判据（仅 edit）。" },
                "max_goal_rounds": { "type": "number", "description": "新的轮次上限（仅 edit）。" },
                "reason_code": {
                  "type": "string",
                  "description": "阻塞分类（仅 blocked），小写 kebab-case，如 test-fails。"
                },
                "reason_message": {
                  "type": "string",
                  "description": "阻塞说明（仅 blocked），说清卡在哪。"
                }
              },
              "required": ["goal_id", "revision", "action"],
              "additionalProperties": false
            }
            """).RootElement.Clone());

    /// <summary>改变会话走向，报 Execute。</summary>
    public ToolPermission DescribePermission(JsonElement arguments) =>
        new(ToolAccess.Execute, IsKnown: true, Paths: [], Commands: [], Domains: []);

    public ToolExecutionPlan DescribeExecution(JsonElement arguments) =>
        new(ToolConcurrency.WorkspaceExclusive);

    public ValueTask<AgentToolResult> ExecuteAsync(
        ToolInvocation invocation, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(invocation);

        var id = ReadString(invocation.Arguments, "goal_id");
        var actionName = ReadString(invocation.Arguments, "action");

        if (string.IsNullOrWhiteSpace(id))
            return ValueTask.FromResult(GoalResults.Fail("缺少 goal_id 参数（先调 get_goal）。"));
        if (string.IsNullOrWhiteSpace(actionName))
            return ValueTask.FromResult(GoalResults.Fail("缺少 action 参数。"));

        if (!invocation.Arguments.TryGetProperty("revision", out var rev)
            || rev.ValueKind is not JsonValueKind.Number
            || !rev.TryGetInt32(out var revision))
        {
            return ValueTask.FromResult(
                GoalResults.Fail("缺少 revision 参数（先调 get_goal）。"));
        }

        if (!Enum.TryParse<GoalAction>(actionName, ignoreCase: true, out var action))
            return ValueTask.FromResult(GoalResults.Fail(
                $"不认识的 action：'{actionName}'。可用："
                + "edit / pause / resume / complete / blocked / clear。"));

        var request = new GoalUpdateRequest(
            id!, revision, action,
            Objective: ReadString(invocation.Arguments, "objective"),
            MaxGoalRounds: ReadInt(invocation.Arguments, "max_goal_rounds"),
            ReasonCode: ReadString(invocation.Arguments, "reason_code"),
            ReasonMessage: ReadString(invocation.Arguments, "reason_message"));

        try
        {
            return ValueTask.FromResult(Describe(_goal.Update(request)));
        }
        catch (ArgumentException ex)
        {
            return ValueTask.FromResult(GoalResults.Fail(ex.Message));
        }
    }

    private static AgentToolResult Describe(GoalUpdateResult result) => result switch
    {
        GoalUpdateResult.Succeeded s => GoalResults.Ok(
            s.Goal, s.Changed ? "已更新。" : "值没有变化，修订号保持不变。"),

        GoalUpdateResult.Cleared c => GoalResults.Ok(c.Goal, "目标已清除。"),

        GoalUpdateResult.NotFound => GoalResults.Fail(
            "没有这个目标（id 不存在，或已被清除）。用 get_goal 重新确认。"),

        GoalUpdateResult.VersionConflict v => GoalResults.Conflict(v.Current),

        GoalUpdateResult.TooEarlyToBlock t => GoalResults.Fail(
            $"还不能标记为 blocked：需要先跑满 {t.MinimumRounds} 轮，"
            + $"当前 {t.Current.RoundsStarted} 轮。先多试几种办法。"),

        GoalUpdateResult.ReasonRequired => GoalResults.Fail(
            "标记 blocked 时必须给 reason_code 与 reason_message——"
            + "只有\"卡住了\"这三个字，下一轮无法判断该绕开还是重试。"),

        GoalUpdateResult.UnknownAction a => GoalResults.Fail($"不认识的 action：'{a.Action}'。"),

        _ => GoalResults.Fail("无法识别的结果。"),
    };

    private static string? ReadString(JsonElement arguments, string name) =>
        arguments.TryGetProperty(name, out var element)
            && element.ValueKind is JsonValueKind.String
                ? element.GetString()
                : null;

    private static int? ReadInt(JsonElement arguments, string name) =>
        arguments.TryGetProperty(name, out var element)
            && element.ValueKind is JsonValueKind.Number
            && element.TryGetInt32(out var value)
                ? value
                : null;
}

/// <summary>目标工具的共用结果形状。</summary>
/// <remarks>
/// <b>成功与失败用同一个形状</b>：模型不需要区分"字段缺失的失败"与
/// "业务失败的失败"，它只需要知道 <c>ok</c> 与 <c>error</c>。
/// 而统一形状让模型不必为两种情况写两套读取逻辑。
/// </remarks>
internal static class GoalResults
{
    public static AgentToolResult Ok(GoalSnapshot goal, string note)
    {
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            ok = true,
            error = (string?)null,
            goal = Shape(goal),
            note,
        }));

        return new AgentToolResult(document.RootElement.Clone());
    }

    /// <summary>修订号冲突：回传权威值。</summary>
    /// <remarks>
    /// <b>回传当前值而不是只报错</b>：调用方可以据此直接协调，
    /// 不必再调一次 <c>get_goal</c>——而那次读可能又落在下一次修改之后。
    /// </remarks>
    public static AgentToolResult Conflict(GoalSnapshot current)
    {
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            ok = false,
            error = "修订号不符：这个目标在你看过之后被改过。用下面的当前值重试。",
            goal = Shape(current),
            note = "重试时把 revision 换成这里的值。",
        }));

        return new AgentToolResult(document.RootElement.Clone());
    }

    public static AgentToolResult Fail(string message)
    {
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            ok = false,
            error = message,
            goal = (object?)null,
            note = (string?)null,
        }));

        return new AgentToolResult(document.RootElement.Clone());
    }

    private static object Shape(GoalSnapshot goal) => new
    {
        id = goal.Id,
        revision = goal.Revision,
        objective = goal.Objective,
        phase = goal.Phase.ToString().ToLowerInvariant(),
        blockedReason = goal.BlockedReason is { } r
            ? new { code = r.Code, message = r.Message }
            : null,
        maxGoalRounds = goal.MaxGoalRounds,
        roundsStarted = goal.RoundsStarted,
        roundsRemaining = goal.RoundsRemaining,
    };
}
