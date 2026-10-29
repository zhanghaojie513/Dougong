namespace Dougong.Kernel;

/// <summary>
/// 该 <see cref="PluginId"/> 已经在内核里登记过。
/// </summary>
/// <remarks>
/// <b>为什么需要专用类型而不是一段消息文本</b>：调用方要区分"已存在"与
/// "加载失败"，而这两种情况的用户动作相反——已存在不用做任何事，
/// 失败要去排查。靠匹配 <c>Exception.Message</c> 里的措辞来区分是脆弱的：
/// 消息一改，判断就静默失效，而且失效时没有任何迹象。
/// <para>
/// <see cref="PluginId"/> 带上是被哪个 Id 挡住了，供宿主把这条结果
/// 关联到列表里已有的那一行。
/// </para>
/// </remarks>
public sealed class PluginAlreadyRegisteredException : InvalidOperationException
{
    /// <summary>构造。</summary>
    /// <param name="pluginId">已登记的插件 Id。</param>
    public PluginAlreadyRegisteredException(PluginId pluginId)
        : base($"插件 '{pluginId.Value}' 已登记。")
    {
        PluginId = pluginId;
    }

    /// <summary>已登记的插件 Id。</summary>
    public PluginId PluginId { get; }
}
