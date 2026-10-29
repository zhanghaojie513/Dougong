namespace Dougong.Kernel;

/// <summary>
/// 运行状态。用户是否希望启用（desired state）与此分开保存：
/// 插件可以在被期望启用的情况下处于 <see cref="Blocked"/>。
/// </summary>
public enum PluginState
{
    /// <summary>已登记，尚未尝试激活。</summary>
    Registered,

    /// <summary>正在执行激活，服务尚未提交到解析视图。</summary>
    Activating,

    /// <summary>已激活，服务已提交，可被解析。</summary>
    Active,

    /// <summary>正在排空并清理。</summary>
    Deactivating,

    /// <summary>已停止，但代码可能仍处于已加载状态，可再次激活。</summary>
    Stopped,

    /// <summary>仍期望启用，但必需依赖不满足；提供方恢复后重新评估。</summary>
    Blocked,

    /// <summary>本次操作在受控边界内失败，带诊断信息。</summary>
    Failed
}

/// <summary>一次状态迁移的记录。诊断只保留共享字符串数据，不持有插件异常对象。</summary>
/// <remarks>
/// <see cref="Elapsed"/> 只在**有意义的迁移**上非空：目前是
/// <c>Activating → Active</c>（激活耗时）。其它迁移保持 null——
/// 给它们填 0 会让"这段没计时"与"这段瞬间完成"混为一谈。
/// <para>
/// <b>为什么挂在状态迁移上而不是单独一条事件</b>：耗时天然属于某一次迁移
/// （"激活花了多久"）。拆成两条消息会要求订阅方自己配对，而配对在并发
/// 场景下容易错，且配错时没有任何迹象。
/// </para>
/// </remarks>
public sealed record PluginStateChange(
    PluginId PluginId,
    PluginState From,
    PluginState To,
    string? Reason = null,
    PluginDiagnostic? Diagnostic = null,
    TimeSpan? Elapsed = null);
