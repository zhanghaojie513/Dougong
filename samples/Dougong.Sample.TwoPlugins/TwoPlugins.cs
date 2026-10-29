using Dougong.Kernel;

namespace Dougong.Sample.TwoPlugins;

/// <summary>
/// 同一个程序集里的第一个插件。
/// </summary>
/// <remarks>
/// 与 <see cref="SecondPlugin"/> 同在一个 dll：<c>LoadAsync</c> 不带
/// <c>typeName</c> 时必须报"需显式指定"，<c>LoadFromDirectoryAsync</c>
/// 必须报 <c>Ambiguous</c> 并列出两个候选——都不能擅自挑一个。
/// </remarks>
public sealed class FirstPlugin : IPlugin
{
    public PluginManifest Manifest { get; } =
        PluginManifest.Create("dougong.sample.two.first", "1.0.0");

    public ValueTask ActivateAsync(PluginContext context, CancellationToken cancellationToken) =>
        ValueTask.CompletedTask;
}

/// <summary>同一个程序集里的第二个插件。</summary>
public sealed class SecondPlugin : IPlugin
{
    public PluginManifest Manifest { get; } =
        PluginManifest.Create("dougong.sample.two.second", "1.0.0");

    public ValueTask ActivateAsync(PluginContext context, CancellationToken cancellationToken) =>
        ValueTask.CompletedTask;
}
