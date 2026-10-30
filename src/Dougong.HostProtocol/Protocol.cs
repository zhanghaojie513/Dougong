using System.Text.Json;
using System.Text.Json.Serialization;

namespace Dougong.HostProtocol;

/// <summary>
/// 宿主契约信封：版本、序列化配置，以及三条多态流的根。
/// <para>
/// <b>类名带 Contract 后缀的理由</b>：命名空间已经是 <c>Dougong.HostProtocol</c>，
/// 同名类会让 <c>HostProtocol.Json</c> 被解析成命名空间，编译不过。
/// </para>
/// </summary>
/// <remarks>
/// <b>这个包只定义"消息长什么样"，不含传输、不含鉴权、不含服务端实现。</b>
/// TCP、命名管道、WebSocket、进程内直调都由宿主选；用什么凭证进来也由宿主定。
/// 分开的理由：这三件事每个宿主的答案都不同，而消息形状应当一致——
/// 否则同一份 SDK 的不同宿主彼此不能复用客户端。
///
/// <para>
/// <b>所有 <c>[JsonDerivedType]</c> 登记集中在本项目的 <c>Protocol.cs</c></b>，
/// 因此看一个文件就知道总共有哪些命令与事件。具体消息按域分文件。
/// </para>
///
/// <para>
/// <b>这里刻意不含"工作区浏览"这类消息</b>（列目录、目录条目、文件变更摘要）。
/// 判据是：<c>fs/*</c> 服务的是**某个 UI 的形态**——右栏文件树、改动 tab。
/// 把它们放进契约，等于强迫所有客户端接受一种界面组织方式，
/// 以及一种"怎么算变更"的实现（桌面端用的是文件版本指纹比对，
/// 而指纹里没有文件原内容，给不出行级 diff）。
/// </para>
///
/// <para>
/// <b>那"用户能看工作区哪些部分"怎么办</b>：那是**权限策略**，不是消息契约。
/// 每个宿主的答案不同（桌面端是本地可信用户，web 要鉴权），
/// 共享的应当是路径边界判断的**函数**，不是协议消息。
/// </para>
///
/// <para>
/// <b>不认识的判别器要忽略，不是崩</b>：这一条对三个方向都成立——
/// 旧客户端遇到新命令、旧服务端遇到新事件、旧 UI 遇到新条目类型（降级为通用卡片）。
/// 加新类型因此不需要升 <see cref="Version"/>。
/// </para>
/// </remarks>
public static class HostProtocolContract
{
    /// <summary>
    /// 协议版本。
    /// </summary>
    /// <remarks>
    /// <b>改这个数字的判据</b>：类型被**删除**、或字段**语义**改变时加一。
    /// 只加新类型、或加可空字段不需要——旧客户端会忽略不认识的判别器。
    ///
    /// <para>
    /// <b>当前协商状态：未实现。</b>这个字段存在，但**没有任何代码比较它**。
    /// 用它之前必须先补协商流程（谁先发、不匹配时怎么拒绝、能力位如何取交集），
    /// 否则会出现"字段设了但没生效"——那比没有这个字段更危险，
    /// 因为调用方会以为已经做了兼容检查。
    /// </para>
    ///
    /// <para>
    /// <b>不匹配时的正确行为是拒绝连接，不是"尽量解析"</b>：
    /// 尽量解析会把"字段缺失"变成"字段是默认值"，而那种错误只在
    /// 某个特定操作上才暴露。
    /// </para>
    /// </remarks>
    public const int Version = 1;

    /// <summary>
    /// JSON 序列化配置。枚举写字符串，便于抓包定位。
    /// </summary>
    /// <remarks>
    /// <b>双方必须用同一份配置</b>：宿主各写一份 <see cref="JsonSerializerOptions"/>
    /// 是这类协议最常见的裂缝——一侧忽略 null、另一侧不忽略时，
    /// "字段不存在"与"字段是 null"在线上就无法区分。
    /// </remarks>
    public static JsonSerializerOptions Json { get; } = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };
}

// ─────────────────────────────────────────────────────────────
// 条目
// ─────────────────────────────────────────────────────────────

/// <summary>会话条目的多态根。判别字段是 <c>kind</c>。</summary>
/// <remarks>
/// <b>不认识的 kind 必须降级为通用卡片</b>：SDK 或某个宿主加新条目类型时，
/// 旧客户端不该崩。这与命令/事件侧"忽略不认识的判别器"是同一条原则。
/// </remarks>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(UserMessageItem), "userMessage")]
[JsonDerivedType(typeof(AssistantMessageItem), "assistantMessage")]
[JsonDerivedType(typeof(ReasoningItem), "reasoning")]
[JsonDerivedType(typeof(ToolCallItem), "toolCall")]
[JsonDerivedType(typeof(PlanItem), "plan")]
[JsonDerivedType(typeof(NoticeItem), "notice")]
public abstract partial record ConversationItem
{
    /// <summary>条目标识。同一会话内唯一。</summary>
    public required string Id { get; init; }

    /// <summary>产生时刻。</summary>
    public DateTimeOffset At { get; init; } = DateTimeOffset.Now;
}

// ─────────────────────────────────────────────────────────────
// 命令（客户端 → 服务端）
// ─────────────────────────────────────────────────────────────

/// <summary>客户端命令的多态根。判别字段是 <c>method</c>。</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "method")]
[JsonDerivedType(typeof(ListSessionsCommand), "session/list")]
[JsonDerivedType(typeof(CreateSessionCommand), "session/create")]
[JsonDerivedType(typeof(OpenSessionCommand), "session/open")]
[JsonDerivedType(typeof(ResyncCommand), "session/resync")]
[JsonDerivedType(typeof(StartTurnCommand), "turn/start")]
[JsonDerivedType(typeof(CancelTurnCommand), "turn/cancel")]
[JsonDerivedType(typeof(RespondApprovalCommand), "approval/respond")]
public abstract record ClientCommand
{
    /// <summary>请求 ID。响应带同一个 ID 回来，用于配对。</summary>
    public required string RequestId { get; init; }
}

// ─────────────────────────────────────────────────────────────
// 事件（服务端 → 客户端）
// ─────────────────────────────────────────────────────────────

/// <summary>
/// 服务端推送的增量。每条带 <see cref="Sequence"/>。判别字段是 <c>method</c>。
/// </summary>
/// <remarks>
/// <b>序号的语义，以及它现在还做不到什么——按现状如实写</b>：
/// <list type="bullet">
/// <item>序号由**单一计数器**分配，同一连接内严格单调、不重复。</item>
/// <item>客户端发现缺口会发 <see cref="ResyncCommand"/> 请求重同步；
/// 重同步走"重开一次会话"路径，回到全量快照。</item>
/// <item><b>断线期间没有事件时</b>，重连后第一帧的序号会远大于本地记录，
/// 看起来像漏了几千条。所以断线一律整条重建、重新 <c>session/open</c>，
/// 不做"从某个序号继续"的增量恢复——**本契约没有定义回放能力**。</item>
/// </list>
/// <para>
/// <b>为什么把这段写清楚而不是留空</b>：单人单连接下有漏洞也不会暴露，
/// 但一旦有人按"序号可靠"去实现增量同步，就会踩到一个**看起来能用、
/// 断线后才错**的坑。契约里没实现的条款，比没有条款更危险。
/// </para>
/// <para>
/// <b>回放能力若要补</b>：服务端需要能"从序号 N 起重发"，
/// 且必须保证重发的内容与首次发送逐字节一致，否则客户端无法去重。
/// 这条要求没写进类型里，因为它是**行为**而非形状。
/// </para>
/// </remarks>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "method")]
[JsonDerivedType(typeof(SessionListedEvent), "session/listed")]
[JsonDerivedType(typeof(SessionOpenedEvent), "session/opened")]
[JsonDerivedType(typeof(TurnStartedEvent), "turn/started")]
[JsonDerivedType(typeof(TurnCompletedEvent), "turn/completed")]
[JsonDerivedType(typeof(ItemAddedEvent), "item/added")]
[JsonDerivedType(typeof(ItemUpdatedEvent), "item/updated")]
[JsonDerivedType(typeof(ApprovalRequestedEvent), "approval/requested")]
[JsonDerivedType(typeof(EngineStatusEvent), "engine/status")]
public abstract record ServerEvent
{
    /// <summary>单调递增序号，用于检测缺口。</summary>
    public required long Sequence { get; init; }
}
