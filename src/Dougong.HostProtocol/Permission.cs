namespace Dougong.HostProtocol;

// ─────────────────────────────────────────────────────────────
// 权限预设：审批严格程度的具名档位
// ─────────────────────────────────────────────────────────────

/// <summary>
/// 一个具名的权限预设。
/// </summary>
/// <remarks>
/// <b>它打包的是"要不要问人"，不是"允不允许做"。</b>允许与否仍由审批者的答复
/// 与服务端的路径守卫决定。这个区分必须保留在契约里：把它叫做"权限范围"
/// 会让宿主以为选了最宽松的档就等于打开了沙箱边界，而实际上路径守卫一直在。
///
/// <para>
/// <b>为什么不直接暴露引擎的 <c>PermissionPreset</c></b>：本契约包零依赖，
/// 刻意不引用运行时实现（见 <see cref="HostProtocolContract"/> 的注释）。
/// 每个宿主若各自把引擎类型搬上线，形状就会漂移——而漂移的表现是
/// "同一份配置在两个宿主上表现不同"，那种问题在跨进程时极难定位。
/// </para>
///
/// <para>
/// <b>描述里必须明说"不含沙箱隔离"</b>：这是预设最容易误导人的地方。
/// 用户看到"最宽松"会以为文件被放开、看到"最严格"会以为文件被隔离，
/// 而这两个理解都不成立。
/// </para>
/// </remarks>
/// <param name="Key">稳定键，切换与持久化用它。</param>
/// <param name="Name">显示名。</param>
/// <param name="Description">一句话说明，给选择器用。</param>
/// <param name="Selectable">
/// 是否可作为切换目标。<c>false</c> 表示它只用于显示"当前值"
/// （例如派生的 <c>custom</c>），不应出现在可点击列表里。
/// </param>
public sealed record PermissionPresetInfo(
    string Key,
    string Name,
    string? Description,
    bool Selectable);

/// <summary>
/// 权限预设的完整状态：当前在哪一档、有哪些可选。
/// </summary>
/// <remarks>
/// <b>一次给全，不让客户端分两次问</b>：只给当前键会让界面无法渲染选择器
/// （它需要标签），只给列表会让界面不知道选中哪个。
/// 两次请求之间状态还可能变——那时界面显示的选中项与列表就对不上了。
/// </remarks>
/// <param name="CurrentKey">
/// 当前预设键。可能不在 <see cref="Options"/> 的可选项里——
/// 宿主直接改过策略时它是 <c>custom</c>（派生值，不可选）。
/// </param>
/// <param name="CurrentName">当前档的显示名，供状态栏直接使用。</param>
/// <param name="Options">
/// 全部选项，按服务端声明顺序。含派生的不可选项——
/// 界面需要它才能显示"当前是自定义"。
/// </param>
public sealed record PermissionPresetState(
    string CurrentKey,
    string CurrentName,
    IReadOnlyList<PermissionPresetInfo> Options);

/// <summary>
/// 查询当前的权限预设。
/// </summary>
/// <remarks>
/// <b>只读，不改状态</b>。宿主在打开设置面板、或状态栏需要显示当前档位时发它。
/// <para>
/// 响应是 <see cref="PermissionPresetListedEvent"/>。
/// </para>
/// </remarks>
public sealed record ListPermissionPresetsCommand : ClientCommand
{
    /// <summary>哪个会话；null 表示问服务端全局默认（尚无会话时用）。</summary>
    public string? SessionId { get; init; }
}

/// <summary>
/// 切换到一个权限预设。
/// </summary>
/// <remarks>
/// <b>未知键必须报错，不能静默无操作</b>：用户以为收紧了而实际没变，
/// 在安全方向上是更坏的结果。错误经 <see cref="PermissionPresetListedEvent.Error"/>
/// 或默认响应通道回来。
///
/// <para>
/// <b>这个切换会立即影响下一次工具调用</b>，不等下一轮。所以它可以在
/// 回合进行中发出——用户看到某个工具即将执行、想收紧一档，那是合理时机。
/// </para>
/// </remarks>
public sealed record SwitchPermissionPresetCommand : ClientCommand
{
    /// <summary>哪个会话；null 表示服务端全局默认。</summary>
    public string? SessionId { get; init; }

    /// <summary>要切到的预设键。</summary>
    public required string Key { get; init; }
}

/// <summary>
/// 订阅权限预设的变更广播。
/// </summary>
/// <remarks>
/// <b>默认**不**广播</b>：单窗口客户端自己发起切换、自己收到响应就够了，
/// 再推一条是多余的一帧。只有"多个界面同时看同一个会话"才需要——
/// 例如 Dock 浮动窗与主窗口并存时，一边切了档，另一边要跟着变。
///
/// <para>
/// <b>所以做成显式订阅而不是默认开</b>：让需要它的客户端说一声，
/// 而不是让所有客户端都处理一种多数时候用不到的消息。
/// 这与"推送事件要稀少且有意义"是同一条取舍。
/// </para>
///
/// <para>
/// <b>订阅是幂等的</b>：重复发不会导致重复广播。<see cref="Enabled"/>
/// 为 <c>false</c> 可用于退订（例如那个浮动窗关掉了）。
/// </para>
/// </remarks>
public sealed record WatchPermissionPresetsCommand : ClientCommand
{
    /// <summary>哪个会话；null 表示服务端全局默认。</summary>
    public string? SessionId { get; init; }

    /// <summary>true 订阅、false 退订。</summary>
    public bool Enabled { get; init; } = true;
}

/// <summary>
/// 权限预设的状态（查询的响应，也是切换之后的确认）。
/// </summary>
/// <remarks>
/// <b>切换成功也回这个完整状态</b>，而不是只回 "ok"：客户端可以把结果直接
/// 刷进选择器，不必再发一次查询。少一次往返也少一个"刷到旧状态"的窗口。
/// </remarks>
public sealed record PermissionPresetListedEvent : ServerEvent
{
    /// <summary>哪个会话；null 表示服务端全局默认。</summary>
    public string? SessionId { get; init; }

    /// <summary>完整状态。</summary>
    public required PermissionPresetState State { get; init; }

    /// <summary>
    /// 失败原因；成功时为 null。
    /// </summary>
    /// <remarks>
    /// <b>用字段表达失败，不设一个"失败事件类型"</b>：协议是消息流，
    /// 没有"抛异常"这个动作。失败时 <see cref="State"/> 仍是切换前的值
    /// ——客户端据此既能显示原因，又不会误以为状态已经变了。
    /// </remarks>
    public string? Error { get; init; }
}
