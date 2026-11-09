using Dougong.Agent;
using Dougong.HostProtocol;

namespace Dougong.Hosting;

/// <summary>
/// 引擎类型与宿主协议形状之间的映射。**所有宿主共用这一份。**
/// </summary>
/// <remarks>
/// <b>为什么必须有这一层，而不是让每个宿主自己撞</b>：<c>Dougong.HostProtocol</c>
/// 是零依赖契约包（刻意不引用 <c>Dougong.Agent</c>），所以协议里定义的是
/// 自己的形状。而"引擎的枚举 A 对应协议的枚举 B"这条知识如果留在各宿主里，
/// 每个宿主都要重写一遍——<b>而写错的后果是静默的</b>：枚举多一个值时
/// 映射两边都编译通过，只是那个值在界面上显示成别的东西。
///
/// <para>
/// <b>放在 Hosting（L2）而不是 HostProtocol（L0）</b>：HostProtocol 不能引用
/// Agent，而映射必须同时看到两者。Hosting 的层次正好允许（Kernel L0 ←
/// Agent L1 ← Hosting L2）。
/// </para>
///
/// <para>
/// <b>映射必须穷尽</b>：两个方向都用 <c>switch</c> 的穷尽形式，
/// 新增枚举值时编译器会报"未处理所有枚举值"——而不是悄悄落到 default。
/// 这条由 <c>hosting-mappings-are-exhaustive</c> 系列检查钉住。
/// </para>
/// </remarks>
public static class ProtocolMapping
{
    /// <summary>把引擎的权限预设状态映射成协议形状。</summary>
    /// <param name="presets">引擎的预设注册表。</param>
    /// <param name="sessionId">所属会话；null 表示全局默认。</param>
    /// <returns>可直接上线的完整状态。</returns>
    /// <remarks>
    /// <b>直接取引擎的 <c>Options()</c> 而不是自己拼</b>：那里已经处理了
    /// "派生的 custom 不可选"这条规则。自己拼会让这条知识复制到每个宿主，
    /// 而漏掉它的表现是——界面允许用户点选"自定义"，点下去抛异常。
    /// </remarks>
    public static PermissionPresetState ToProtocol(
        this PermissionPresets presets, string? sessionId = null)
    {
        ArgumentNullException.ThrowIfNull(presets);
        _ = sessionId;   // 当前引擎的预设表是会话级的，这里不需要它；留参数是为了将来分作用域。

        var options = presets.Options()
            .Select(o => new PermissionPresetInfo(
                o.Value,
                o.Name,
                o.Description,
                o.Selectable))
            .ToList();

        // 当前档的显示名：从选项里找，找不到就用键本身。
        // **不回退成空串**：状态栏显示空白比显示键名更让人困惑。
        var current = presets.CurrentKey;
        var name = options
            .FirstOrDefault(o => string.Equals(o.Key, current, StringComparison.Ordinal))
            ?.Name ?? current;

        return new PermissionPresetState(current, name, options);
    }

    /// <summary>把引擎的子 agent 句柄映射成协议形状。</summary>
    /// <param name="handle">引擎句柄。</param>
    /// <returns>协议形状的状态。</returns>
    /// <remarks>
    /// <b>状态取 <see cref="SubagentHandle.CurrentStatus"/>，不在这里重算</b>：
    /// "已结束但没有结果"该算取消还是完成，是一条只该有一份的判断
    /// （见那个属性的注释）。映射层重复实现它，就会与引擎各错一次。
    /// </remarks>
    public static SubagentInfo ToProtocol(this SubagentHandle handle)
    {
        ArgumentNullException.ThrowIfNull(handle);

        return new SubagentInfo(
            handle.Id,
            handle.Label,
            Map(handle.CurrentStatus),
            handle.StartedAt,
            handle.ParentTurnId,
            handle.ToolRounds);
    }

    /// <summary>把引擎的子 agent 结果映射成协议形状。</summary>
    public static SubagentOutcome ToProtocol(this SubagentResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        return new SubagentOutcome(
            result.Id,
            Map(result.Status),
            result.Text,
            result.Diagnostic,
            result.ToolRounds);
    }

    /// <summary>引擎的结束方式 → 协议形状。</summary>
    /// <remarks>
    /// <b>用穷尽 switch 而不是 <c>Enum.Parse</c> 或强转</b>：名字碰巧一致时，
    /// 强转能工作；但一旦哪一边加了个值，强转会把新值映射成一个
    /// **语义无关**的旧值（按序号对齐），而界面上不会报错，只是显示成错的。
    /// 穷尽 switch 让这类改动在编译期就暴露。
    /// </remarks>
    private static SubagentRunState Map(SubagentStatus status) => status switch
    {
        SubagentStatus.Running => SubagentRunState.Running,
        SubagentStatus.Completed => SubagentRunState.Completed,
        SubagentStatus.Failed => SubagentRunState.Failed,
        SubagentStatus.Cancelled => SubagentRunState.Cancelled,
        _ => throw new ArgumentOutOfRangeException(
            nameof(status), status,
            "引擎新增了子 agent 状态但协议映射没跟上——补 Map 里的分支与协议枚举。"),
    };
}
