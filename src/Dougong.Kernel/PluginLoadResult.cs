namespace Dougong.Kernel;

/// <summary>
/// 批量加载中单个程序集的结果。
/// </summary>
/// <remarks>
/// <b>为什么需要状态而不只是成功/失败</b>：批量加载目录时，宿主 UI 要把
/// 结果分成几类显示，而"失败"这一类的用户动作完全不同：
/// <list type="bullet">
/// <item>装上了 → 可以启用/停用</item>
/// <item>不是插件（依赖库）→ 什么都不用做，甚至不该显示给用户</item>
/// <item>是插件但加载失败 → 需要用户处理（换版本、补依赖、联系作者）</item>
/// <item>是插件但需要指定类型 → 宿主要给用户一个选择界面</item>
/// </list>
/// 前两类混在一起会让"9 个依赖库"看起来像"9 个失败"。
/// </remarks>
public enum PluginLoadStatus
{
    /// <summary>已加载并登记。可用状态见对应的 <see cref="PluginLoadResult.PluginId"/> 查询。</summary>
    Loaded,

    /// <summary>
    /// 程序集里没有 IPlugin 实现，**按设计跳过**，不是错误。
    /// </summary>
    /// <remarks>
    /// 插件包通常自带依赖库（Contracts、Helpers）。它们被扫描到是正常的，
    /// 宿主不该把它们列为失败，也不该显示给用户。
    /// </remarks>
    Skipped,

    /// <summary>是插件但加载失败（缺依赖、契约不兼容、构造抛异常等）。</summary>
    Failed,

    /// <summary>
    /// 程序集内有多个 IPlugin 实现，无法自动判定该装哪个。
    /// </summary>
    /// <remarks>
    /// <b>刻意不自动挑一个</b>：挑错的后果是装了个用户没想要的东西，
    /// 而用户看不到"其实还有别的"——比直接报错更糟。
    /// 宿主应当把这个条目呈现成"请选择"，用 <see cref="PluginLoadResult.AmbiguousTypeNames"/>
    /// 列出候选，然后逐个调 <c>LoadAsync(path, typeName)</c>。
    /// </remarks>
    Ambiguous,
}

/// <summary>
/// 批量加载中单个程序集的结果。
/// </summary>
/// <param name="AssemblyPath">程序集的完整路径。</param>
/// <param name="Status">结果分类。</param>
/// <param name="PluginId">仅在 <see cref="PluginLoadStatus.Loaded"/> 时非空。</param>
/// <param name="Reason">失败或跳过的人类可读原因；成功时为 null。</param>
/// <param name="AmbiguousTypeNames">
/// 仅在 <see cref="PluginLoadStatus.Ambiguous"/> 时非空，列出程序集内的全部
/// IPlugin 类型全名，供宿主做选择界面。
/// </param>
public sealed record PluginLoadResult(
    string AssemblyPath,
    PluginLoadStatus Status,
    PluginId? PluginId = null,
    string? Reason = null,
    IReadOnlyList<string>? AmbiguousTypeNames = null)
{
    /// <summary>该条目是否值得让用户看到。</summary>
    /// <remarks>
    /// <see cref="PluginLoadStatus.Skipped"/> 返回 false：依赖库被扫到是正常的，
    /// 显示出来只会让用户以为出了问题。
    /// </remarks>
    public bool IsUserVisible => Status is not PluginLoadStatus.Skipped;
}
