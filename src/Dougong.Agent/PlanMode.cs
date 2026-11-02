using System.Text.Json;

namespace Dougong.Agent;

/// <summary>
/// 计划模式的状态：本会话是否处于"只出计划、不动手"。
/// </summary>
/// <remarks>
/// <b>照 dsh 的 <c>dsh-plan-mode</c> 做</b>：它是一个**会话级开关**，
/// 激活后靠两件事生效——
/// <list type="number">
/// <item>系统提示注入一段 policy（<c>You are in plan mode.</c>）</item>
/// <item>模型必须调 <see cref="PlanModePolicy.ExitPlanModeToolId"/> 提交计划，工具**阻塞等待审阅**</item>
/// </list>
///
/// <para>
/// <b>与第一版的差别（重要）</b>：第一版让"提交计划"以
/// <see cref="AgentRunOutcome.PlanAwaitingReview"/> 结束本轮，用户下一轮
/// 再让它开始。dsh 不是这样——<c>exit_plan_mode</c> **在工具内部阻塞**，
/// 用户批准后工具返回 <c>"Plan approved"</c>，模型在**同一轮**继续。
/// 后者少一次往返，而且"批准"这个动作直接变成工具结果，
/// 模型不需要猜"用户那句'开始吧'是不是批准"。
/// </para>
///
/// <para>
/// <b>为什么不靠提示约束"不要动手"</b>：只说"先给计划"是软约束。
/// dsh 的做法是在计划模式下**不提供写工具**（它靠 tools 注入面控制），
/// 而我们的工具集是装配期固定的。这里用"运行时拦非只读工具"达到同样效果。
/// </para>
/// </remarks>
public sealed class PlanMode
{
    private volatile bool _active;

    /// <summary>当前是否处于计划模式。</summary>
    public bool IsActive => _active;

    /// <summary>开启。</summary>
    public void Enable() => _active = true;

    /// <summary>关闭（审阅通过或用户退出）。</summary>
    public void Disable() => _active = false;
}

/// <summary>
/// 计划模式下允许执行的工具。**白名单，不是黑名单。**
/// </summary>
/// <remarks>
/// <b>为什么白名单</b>：黑名单让新增工具默认放行——那正是最危险的方向，
/// 一个没被列进黑名单的写工具会在"审阅前"执行。白名单让新工具默认被拦，
/// 漏加只表现成"计划模式下用不了"，可发现。
/// <para>
/// <b>为什么不按工具声明的权限判断</b>：<c>IDispatcherAccessor</c> 只暴露
/// <c>GetToolsAsync</c>（返回 <c>ToolDescriptor</c>），拿不到
/// <c>DescribePermission</c>。按权限判断要先扩接口，那是更大的改动。
/// 这里接受"新增只读工具要在两处登记"的代价。
/// </para>
/// </remarks>
public static class PlanModePolicy
{
    /// <summary>计划工具本身的 Id。</summary>
    public const string ExitPlanModeToolId = "exit_plan_mode";

    /// <summary>计划模式下放行的工具。</summary>
    public static readonly HashSet<string> AllowedTools = new(StringComparer.Ordinal)
    {
        ExitPlanModeToolId,

        // 只读的文件与搜索：计划要"看"才能写。
        "read_file", "glob", "grep",

        // 只读的作业/会话查询。
        "job_list", "job_output",
    };

    /// <summary>计划模式下这个工具是否允许执行。</summary>
    public static bool IsAllowed(string toolId) => AllowedTools.Contains(toolId);
}

/// <summary>
/// 系统提示里的计划模式分节。
/// </summary>
/// <remarks>
/// <b>dsh 的原文是 <c>You are in plan mode.</c></b>，我们同样用一句明确的
/// 状态声明开头——模型需要立刻知道自己处于特殊模式，而不是从后面的
/// 细节里推断。
/// </remarks>
public sealed class PlanModeSection(Func<bool> isActive) : ISystemPromptSection
{
    private readonly Func<bool> _isActive = isActive
        ?? throw new ArgumentNullException(nameof(isActive));

    public string Name => "plan-mode";

    /// <summary>排在工具纪律（200）之前：这是"这一轮怎么工作"，比"工具怎么用"更靠前。</summary>
    public int Order => 190;

    public ValueTask<string?> BuildAsync(
        SystemPromptContext context, CancellationToken cancellationToken)
    {
        if (!_isActive()) return ValueTask.FromResult<string?>(null);

        return ValueTask.FromResult<string?>($$"""
            You are in plan mode. 当前处于计划模式。

            这一轮只出计划，不要动手改。

            1. 需要了解现状时可以先读文件、搜索代码（只读操作不受限）。
            2. 用 `{{PlanModePolicy.ExitPlanModeToolId}}` 提交计划：
               参数 `plan` 是一份**完整的 Markdown 文档**，以 `# 标题` 开头。
            3. 提交后工具会阻塞等待用户审阅。批准后你在同一轮继续执行；
               被拒绝时请修改计划重新提交。

            计划要写到能审阅：每一步做什么、动哪些文件、有什么风险、
            为什么这么选。一两句话的摘要不算计划——用户会看不清代价就批准。
            """);
    }
}

/// <summary>
/// <c>exit_plan_mode</c>：提交完整计划并**阻塞等待审阅**。
/// </summary>
/// <remarks>
/// <b>这是 dsh 计划模式的核心工具</b>，不是 <c>todo_write</c> 的别名：
/// <list type="bullet">
/// <item><c>todo_write</c> 是**持续维护的进度清单**，可以调多次</item>
/// <item>本工具是**一次性动作**——提交文档、请求退出计划模式</item>
/// </list>
/// 复用 <c>todo_write</c> 会让模型分不清"更新进度"与"提交计划等批准"。
///
/// <para>
/// <b>为什么用 <see cref="IAskUserHost"/> 而不是自己造一套交互</b>：
/// dsh 的实现走 <c>ctx.userQuestions.ask</c>，靠 <c>intent.kind = 'plan-review'</c>
/// 让客户端选审阅 UI。复用同一个提问通道意味着：
/// 宿主只需实现一次交互（ACP 与 REPL 各一个传输），
/// 而"计划审阅"是那个通道上的一个类别，不是第二套 UI。
/// </para>
///
/// <para>
/// <b>阻塞不是挂死</b>：等待带取消令牌，用户取消时返回
/// <see cref="AskOutcome.Cancelled"/>，模型会看到"本轮被取消"而不是"计划被否决"。
/// </para>
/// </remarks>
public sealed class ExitPlanModeTool(IAskUserHost host, PlanMode mode) : IAgentTool
{
    /// <summary>计划文档的最大字符数。</summary>
    /// <remarks>
    /// 与工具结果上限同量级。计划是给人读的，超出这个量说明它该拆分——
    /// 而一个几万字的"计划"用户根本审不了。
    /// </remarks>
    public const int MaxPlanChars = 32_000;

    private readonly IAskUserHost _host = host
        ?? throw new ArgumentNullException(nameof(host));
    private readonly PlanMode _mode = mode
        ?? throw new ArgumentNullException(nameof(mode));

    public ToolDescriptor Descriptor { get; } = new(
        PlanModePolicy.ExitPlanModeToolId, PlanModePolicy.ExitPlanModeToolId, "1.0.0",
        "提交完整计划并等待用户审阅。批准后退出计划模式并在本轮继续执行；"
        + "被拒绝时修改计划重新提交。plan 参数是完整 Markdown 文档，以 # 标题开头。",
        JsonDocument.Parse(
            """
            {
              "type": "object",
              "properties": {
                "plan": {
                  "type": "string",
                  "description": "完整计划，Markdown 格式，第一行是 # 标题。写清每步做什么、动哪些文件、有什么风险。"
                }
              },
              "required": ["plan"],
              "additionalProperties": false
            }
            """).RootElement.Clone());

    /// <summary>提问没有副作用，不进审批。</summary>
    public ToolPermission DescribePermission(JsonElement arguments) =>
        new(ToolAccess.None, IsKnown: true, [], [], []);

    /// <summary>工作区独占：审阅期间本就该停下来等人。</summary>
    public ToolExecutionPlan DescribeExecution(JsonElement arguments) =>
        new(ToolConcurrency.WorkspaceExclusive);

    public IReadOnlyList<string> DescribeFileDependencies(JsonElement arguments) => [];

    public async ValueTask<AgentToolResult> ExecuteAsync(
        ToolInvocation invocation, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(invocation);

        var plan = ToolArguments.RequiredString(invocation.Arguments, "plan");

        // **标题必须有**：客户端用它显示卡片标题，没有标题就只剩一句话，
        // 用户无法从标题判断这份计划是关于什么的。
        if (PlanTitle(plan) is not { } title)
        {
            return Result("PlanRejected",
                "计划缺少 Markdown 标题。第一行写成 `# 简短标题` 再提交。");
        }

        if (plan.Length > MaxPlanChars)
        {
            return Result("PlanRejected",
                $"计划 {plan.Length} 字符，超过上限 {MaxPlanChars}。"
                + "请拆分成更小的步骤，或把细节留给实现阶段。");
        }

        var question = new AskedQuestion(
            $"批准这份计划：{title}？",
            "plan",
            [
                new AskOption("Approve", "同意按这份计划执行。"),
                new AskOption("Keep planning", "还需要修改，暂不执行。"),
            ],
            MultiSelect: false)
        {
            Detail = plan,
            Intent = AskIntent.PlanReview with { ApproveLabel = "Approve" },
        };

        AskResult answer;
        try
        {
            answer = await _host.AskAsync([question], cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return Result("PlanUnavailable", $"审阅通道不可用：{ex.GetType().Name}: {ex.Message}");
        }

        switch (answer.Outcome)
        {
            case AskOutcome.Answered:
            {
                var verdict = answer.Answers.FirstOrDefault()?.Answer ?? string.Empty;

                if (string.Equals(verdict, "Approve", StringComparison.Ordinal))
                {
                    // **批准后退出计划模式**：模型在同一轮继续，可以动手了。
                    _mode.Disable();
                    return Result("PlanApproved", "Plan approved. 可以按计划执行。");
                }

                // 未批准但也没拒绝：保持计划模式，让它改。
                return Result("PlanRejected",
                    $"用户选择「{verdict}」，计划未获批准。请修改后重新提交。");
            }

            case AskOutcome.Declined:
                return Result("PlanRejected", "用户拒绝了审阅请求。请修改计划重新提交。");

            case AskOutcome.Cancelled:
                // **与"拒绝"分开**：取消意味着用户没看到或被打断，
                // 报成"否决"会让模型以为方案被否。
                return Result("PlanCancelled", "本轮被取消，用户未做决定。");

            default:
                return Result("PlanUnavailable",
                    answer.Diagnostic ?? "审阅不可用：没有客户端能展示计划。");
        }
    }

    /// <summary>取 Markdown 的一级标题。没有则返回 null。</summary>
    /// <remarks>
    /// <b>只认行首的单个 <c>#</c></b>：<c>##</c> 是二级标题，
    /// 而 <c>#foo</c> 在 Markdown 里不是标题。dsh 的判据是
    /// <c>/^#\s+(\S[^\r\n]*)/</c>，与这里一致。
    /// </remarks>
    internal static string? PlanTitle(string plan)
    {
        foreach (var line in plan.Split('\n'))
        {
            var trimmed = line.TrimEnd('\r');
            if (trimmed.Length == 0) continue;

            // 第一行非空内容必须是标题：允许前面有空行，不允许有正文。
            if (!trimmed.StartsWith("# ", StringComparison.Ordinal)) return null;

            var title = trimmed[2..].Trim();
            return title.Length > 0 ? title : null;
        }

        return null;
    }

    private static AgentToolResult Result(string status, string message) =>
        new(JsonSerializer.SerializeToElement(new
        {
            status,
            message,
        }));
}

/// <summary>
/// 向人类提问的传输接口。
/// </summary>
/// <remarks>
/// <b>为什么从工具里抽出接口</b>：同一个提问要服务 ACP 与 REPL，
/// 而两者的传输完全不同（<c>elicitation/create</c> 请求 vs 终端读一行）。
/// 语义（问什么、怎么校验、结果怎么表达）在 <see cref="AskUser"/>，
/// 传输在这里——工具依赖接口，不依赖具体宿主。
/// <para>
/// <b>与 <c>AcpElicitationTool</c> 的关系</b>：那个类同时做了语义与传输，
/// 本轮把语义部分留在 <see cref="AskUser"/>，传输下沉到本接口。
/// </para>
/// </remarks>
public interface IAskUserHost
{
    /// <summary>提出一组问题并等待答复。</summary>
    /// <param name="questions">已通过校验的问题。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    ValueTask<AskResult> AskAsync(
        IReadOnlyList<AskedQuestion> questions,
        CancellationToken cancellationToken = default);
}
