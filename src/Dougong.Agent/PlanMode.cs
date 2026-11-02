namespace Dougong.Agent;

/// <summary>
/// 计划模式：模型先提交计划、停下等审阅，用户同意后才动手。
/// </summary>
/// <remarks>
/// <b>解决什么问题</b>：默认行为是模型边说边改——它调 <c>todo_write</c>
/// 声明计划，然后**立刻开始执行第一步**。用户看到计划时改不动了。
/// 而涉及多文件、不可逆操作时，"先看计划"是必要的。
///
/// <para>
/// <b>照 dsh 的 <c>dsh-plan-mode</c> 做，核心是"带审阅的退出"</b>：
/// dsh 暴露一个 <c>plan:policy</c> 提示段落，在计划模式激活时注入。
/// 我们同样用一个系统提示分节，但**加了一条硬约束**——
/// <see cref="PlanTool"/> 提交计划后本轮直接以
/// <see cref="AgentRunOutcome.PlanAwaitingReview"/> 结束，
/// 不让模型继续调工具。
/// </para>
///
/// <para>
/// <b>为什么靠运行时强制而不是只靠提示</b>：只说"提交计划后停下"是软约束，
/// 模型经常会顺手把第一步也做了——而那时用户还没看过计划。
/// 强制中止让"审阅"真的发生在执行之前，这是本能力的全部意义。
/// </para>
///
/// <para>
/// <b>为什么不阻止读操作</b>：计划模式要"看"才能计划。禁止读会让模型
/// 只能凭猜测写计划，而猜测的计划没法审阅。所以只拦"这一轮继续下去"，
/// 不拦本轮的只读工具。
/// </para>
/// </remarks>
public sealed class PlanModeSection : ISystemPromptSection
{
    private readonly Func<bool> _isActive;

    /// <summary>构造。</summary>
    /// <param name="isActive">
    /// 每次构建提示时判断是否处于计划模式。
    /// 用委托而不是布尔值：模式可以在会话中途切换，而提示每轮重建。
    /// </param>
    public PlanModeSection(Func<bool> isActive)
    {
        ArgumentNullException.ThrowIfNull(isActive);
        _isActive = isActive;
    }

    public string Name => "plan-mode";

    /// <summary>
    /// 排在工具纪律（200）之前、工作区指令（150）之后。
    /// </summary>
    /// <remarks>
    /// 顺序有理由：先知道"这是哪个项目"（150），再知道"这一轮要怎么工作"
    /// （190），最后才是"工具怎么用"（200）。计划模式是工作方式的约束，
    /// 它比具体工具纪律更靠前。
    /// </remarks>
    public int Order => 190;

    public ValueTask<string?> BuildAsync(
        SystemPromptContext context, CancellationToken cancellationToken)
    {
        if (!_isActive()) return ValueTask.FromResult<string?>(null);

        return ValueTask.FromResult<string?>("""
            ## 计划模式

            当前处于**计划模式**。这一轮先给计划，不要动手改。

            1. 需要了解现状时可以先读文件、搜索代码（只读操作不受限）。
            2. 用 `todo_write` 提交完整计划，然后**停下**——
               运行时会结束本轮并要求用户审阅。
            3. 用户同意后你会在下一轮收到明确的开始指令；
               在那之前不要调用任何写入或执行类工具。

            计划要具体到能审阅：每一步做什么、动哪些文件、有什么风险。
            "先分析再修改"这种不叫计划。
            """);
    }
}

/// <summary>
/// 计划模式的状态与判定。
/// </summary>
/// <remarks>
/// <b>为什么不放在 <see cref="PlanTool"/> 里</b>：那个类是计划条目的
/// 数据结构，而这里是"运行模式"。两者生命周期不同——计划每轮变，模式
/// 跨轮保持。合成一个会让"计划内容"与"工作方式"互相牵连。
/// </remarks>
public sealed class PlanMode
{
    private volatile bool _active;

    /// <summary>当前是否处于计划模式。</summary>
    public bool IsActive => _active;

    /// <summary>开启计划模式。</summary>
    /// <remarks>
    /// <b>开启后要重置"已提交"标志</b>：上一轮的提交不应让这一轮立刻中止。
    /// 忘了重置的症状是"开启计划模式后第一次调用就结束，而计划是空的"。
    /// </remarks>
    public void Enable()
    {
        _active = true;
        Reset();
    }

    /// <summary>关闭计划模式（用户审阅通过，或显式退出）。</summary>
    public void Disable()
    {
        _active = false;
        Reset();
    }

    /// <summary>
    /// 本轮的提交标记。计划模式下它决定本轮是否该停下。
    /// </summary>
    /// <remarks>
    /// <b>是"本轮"而不是"会话"</b>：用户审阅后进入下一轮，那一轮应当能正常执行。
    /// 做成会话级的话，用户同意之后就再也不需要审阅了——而那正是它要防的。
    /// </remarks>
    public bool Submitted { get; private set; }

    /// <summary>标记本轮已提交计划。</summary>
    public void MarkSubmitted()
    {
        if (_active) Submitted = true;
    }

    /// <summary>重置提交标记。每轮开始与模式切换时调用。</summary>
    public void Reset() => Submitted = false;

    /// <summary>
    /// 本轮是否应以"等审阅"结束。
    /// </summary>
    /// <remarks>
    /// 只有"计划模式 + 本轮已提交"同时成立才是。任一不成立就该正常跑完——
    /// 非计划模式下提交计划是常态，不该中止。
    /// </remarks>
    public bool ShouldStopForReview => _active && Submitted;
}
