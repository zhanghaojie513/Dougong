using System.Text.Json;

namespace Dougong.Agent;

/// <summary>
/// 计划条目。**这是模型主动声明的**，不是运行时推测出来的。
/// </summary>
/// <param name="Content">这一步要做什么。</param>
/// <param name="Priority">优先级，透传模型给的值（如 high/medium/low）。</param>
/// <param name="Status">状态，透传模型给的值（如 pending/in_progress/completed）。</param>
/// <remarks>
/// 刻意不把 <see cref="Priority"/> / <see cref="Status"/> 做成枚举：
/// ACP 的 plan 条目就是字符串，而不同客户端认的词不完全一样。
/// 强行枚举会在遇到没见过的值时丢弃信息——那比多传一个字符串更糟。
/// 校验放在工具层（见 <c>TodoWriteTool</c>），那里可以给出可操作的错误。
/// </remarks>
public sealed record PlanEntry(string Content, string Priority, string Status);

/// <summary>
/// 模型维护的计划。
/// </summary>
/// <remarks>
/// **为什么需要它**：ACP 的 <c>plan</c> 是每次全量替换的条目列表。没有真实数据源时，
/// 唯一能做的是从工具调用序列猜"看起来像计划"的东西——那会把推测伪装成计划，
/// 用户无法分辨（P3 的反面）。
/// <para>
/// 因此数据源是**工具**：模型调用 <c>todo_write</c> 显式声明当前计划，
/// 运行时把结果投影成 <see cref="AgentEvent.PlanUpdated"/>，ACP 再翻译成
/// <c>session/update</c> 的 plan 条目。全程没有推测。
/// </para>
/// </remarks>
public static class PlanTool
{
    /// <summary>工具 Id。</summary>
    public const string ToolId = "todo_write";

    /// <summary>一次最多几个条目。超过通常意味着模型把清单当记事本用。</summary>
    public const int MaxEntries = 50;
}
