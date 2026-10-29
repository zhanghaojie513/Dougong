namespace Dougong.Kernel;

/// <summary>
/// 内核承载的插件。实现者不提供停用方法：停用即释放 <see cref="PluginContext"/> 作用域，
/// 由 <see cref="PluginContext.RegisterForCleanup"/> 登记的收尾回调被执行。
/// </summary>
public interface IPlugin
{
    PluginManifest Manifest { get; }

    ValueTask ActivateAsync(PluginContext context, CancellationToken cancellationToken);
}

/// <summary>
/// 宿主可用的插件快照。只包含可跨 ALC 边界的共享数据：不再携带
/// <see cref="PluginManifest"/>，以免长期保留的动态插件类型阻止程序集回收。
/// 需要完整清单时显式调用 <c>DougongKernel.GetPluginManifestAsync</c>。
/// </summary>
public sealed record PluginSummary(PluginId Id, string Version, PluginState State)
{
    /// <summary>
    /// 展示元信息；插件未声明时为 null，宿主回退到 <see cref="Id"/>。
    /// </summary>
    /// <remarks>
    /// <b>用 <c>init</c> 属性而不是构造参数</b>：改 positional 构造会替换
    /// 构造签名与 <c>Deconstruct</c>，已编译的调用方会 <c>MissingMethodException</c>
    /// （`ToolDispatchResult` 那次就是这么断的，ApiCompat 的 CP0002 抓到）。
    /// 属性是纯增量，二进制兼容。
    /// <para>
    /// <b>只带字符串、不带 <see cref="PluginManifest"/></b>：manifest 里有
    /// <c>ServiceRequirement.Type</c>，对动态插件而言那是插件 ALC 里的
    /// 类型，长期保留会阻止程序集回收。展示信息是纯值，可以安全跨边界。
    /// </para>
    /// </remarks>
    public PluginDisplayInfo? Display { get; init; }
}

/// <summary>宿主可用的插件句柄；状态是快照，后续变化通过内核的状态订阅观察。</summary>
public sealed record PluginHandle(PluginId Id, string Version, PluginState State);
