using System.Reflection;

namespace Dougong.Acp;

/// <summary>
/// 本包自己的版本号，用于 ACP <c>initialize</c> 的 <c>agentInfo.version</c>。
/// </summary>
/// <remarks>
/// <b>为什么 Acp 不共用 CLI 的那份</b>：分层是
/// <c>Kernel ← Agent ← {Ai,Mcp,Acp} ← Cli</c>，Acp 不认识 Cli
/// （有 `ArchitectureBoundaryTests` 守着）。两处都从各自程序集的
/// <see cref="AssemblyInformationalVersionAttribute"/> 读同一个 MSBuild 属性，
/// 因此值天然一致，不会漂移。
/// <para>
/// 注意：这里报的是**包的版本**（Acp 自己的构建），不是产品版本。
/// 两者在同一个仓库里由同一个 <c>Version</c> 属性生成，所以当前相同；
/// 将来若各包独立发版，这里应当保持"报自己的版本"。
/// </para>
/// </remarks>
internal static class AcpVersion
{
    private static readonly Lazy<string> Cached = new(Read);

    /// <summary>版本文本，例如 <c>0.1.0-alpha.1</c>；不含构建元数据。</summary>
    public static string Value => Cached.Value;

    private static string Read()
    {
        var assembly = typeof(AcpVersion).Assembly;

        var informational = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;

        if (!string.IsNullOrEmpty(informational))
        {
            var plus = informational.IndexOf('+', StringComparison.Ordinal);
            return plus < 0 ? informational : informational[..plus];
        }

        return assembly.GetName().Version?.ToString(3) ?? "0.0.0";
    }
}
