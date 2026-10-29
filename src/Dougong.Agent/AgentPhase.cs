namespace Dougong.Agent;

/// <summary>
/// 一次运行当前处于哪个阶段。**宿主用它决定"给用户看什么"。**
/// </summary>
/// <remarks>
/// <b>为什么这个枚举属于 Agent 层而不是某个 UI</b>：它是"运行器对外的状态语义"，
/// 不是某个终端的显示细节。自己做宿主的用户（Desktop、Web、IDE 插件）都需要
/// 一套共同词汇来判断"现在该显示什么"，各写一套必然漂移。
/// <para>
/// <b>它不能只靠事件驱动</b>：<see cref="AgentEvent"/> 里没有"正在等待模型"这种
/// 事件——<see cref="AgentEvent.RunStarted"/> 之后是一段完全静默的等待，
/// 而这段等待占整轮耗时的大头。所以阶段必须由**事件 + 计时**共同推导，
/// 见 <see cref="AgentPhaseTracker"/>。
/// </para>
/// </remarks>
public enum AgentPhase
{
    /// <summary>空闲：等待用户输入。宿主通常隐藏任何"进行中"提示。</summary>
    Idle,

    /// <summary>
    /// 等待模型响应。**没有任何事件会发生**，靠 <see cref="AgentEvent.RunStarted"/>
    /// 进入、以及流式停止后回落。
    /// </summary>
    Thinking,

    /// <summary>
    /// 模型正在产出推理内容（<see cref="AgentEvent.ReasoningDelta"/>）。
    /// </summary>
    /// <remarks>
    /// <b>与 <see cref="Thinking"/> 的区别</b>：Thinking 是"什么都还没来"，
    /// Reasoned 是"模型确实在动，但动的是推理不是回答"。
    /// 对用户的价值不同——长时间没有任何输出时，"在等"和"在想"给人的感受完全不一样，
    /// 后者说明连接正常、模型正在工作。
    /// <para>
    /// 只有启用思考模式的 provider 才会进入本阶段。不支持的模型停在
    /// <see cref="Thinking"/>，不会出现这个阶段。
    /// </para>
    /// </remarks>
    Reasoning,

    /// <summary>执行工具中（<see cref="AgentEvent.ToolCallRequested"/> 之后）。</summary>
    Working,

    /// <summary>压缩历史中（<see cref="AgentEvent.ContextCompacted"/>）。</summary>
    Compacting,

    /// <summary>模型正在流式输出正文（<see cref="AgentEvent.TextDelta"/>）。</summary>
    Streaming,

    /// <summary>已失败，短暂停留提示用户后回到 <see cref="Idle"/>。</summary>
    Failed,
}
