using System.Reflection;

namespace Dougong.Cli;

/// <summary>
/// 产品版本号的**唯一读取入口**。
/// </summary>
/// <remarks>
/// <b>为什么不再写字面量</b>：版本号原先硬编码在 6 处（`Program.cs` 5 处、
/// `AcpAgentHost.cs` 1 处）。发版时只改 `Directory.Build.props` 会让
/// `dougong --version` 输出旧值——而这正是用户和脚本用来判断装没装对的依据，
/// 报错版本号比不报还糟。
/// <para>
/// 源头是 `Directory.Build.props` 的 <c>&lt;Version&gt;</c>，它被 MSBuild
/// 写进 <see cref="AssemblyInformationalVersionAttribute"/>。该属性的值形如
/// <c>0.1.0-alpha.1+2c17cc7</c>（构建时追加 commit 短哈希），
/// 展示时要切掉 <c>+</c> 之后的部分——那是溯源信息，不是版本号。
/// </para>
/// <para>
/// 用 <c>GetCustomAttribute</c> 而不是读进程文件名：单文件发布下
/// `Assembly.Location` 为空（见 `PluginLoadContext` 的同类处理）。
/// </para>
/// </remarks>
public static class ProductInfo
{
    private static readonly Lazy<string> CachedVersion = new(ReadVersion);

    /// <summary>版本文本，例如 <c>0.1.0-alpha.1</c>。</summary>
    public static string Version => CachedVersion.Value;

    private static string ReadVersion()
    {
        var assembly = typeof(ProductInfo).Assembly;

        var informational = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;

        if (!string.IsNullOrEmpty(informational))
        {
            // 切掉构建元数据（+commit）。
            var plus = informational.IndexOf('+', StringComparison.Ordinal);
            return plus < 0 ? informational : informational[..plus];
        }

        // 兜底：Version 属性至少是 0.1.0.0 这种四段数字。
        return assembly.GetName().Version?.ToString(3) ?? "0.0.0";
    }
}
