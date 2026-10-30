using System.Reflection;
using System.Xml.Linq;
using Dougong.Kernel;

namespace Dougong.Kernel.Tests;

/// <summary>
/// 架构边界守卫（docs/architecture.md §2、§2.1）。
/// 目标是让"SDK 与桌面端 UI 互不耦合"成为可执行断言，而不是文档约定：
/// 依赖方向、程序集引用、工程引用或内核公开 API 一旦越界，这里立即失败。
/// </summary>
public sealed class ArchitectureBoundaryTests
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));

    /// <summary>UI / 桌面 / 宿主专属技术栈。SDK 任何工程引用它们都算耦合。</summary>
    private static readonly string[] ForbiddenUiPrefixes =
    [
        "Avalonia",
        "AtomUI",
        "DougongDesktop",
        "ReactiveUI",
        "CommunityToolkit.Mvvm",
        "System.Windows",
        "Microsoft.AspNetCore",
    ];

    /// <summary>分层等级：数字越小越底层。引用只能指向更底层，同层不得互引。</summary>
    /// <remarks>
    /// <c>Dougong.Hosting</c> 在 L2：它需要 Kernel（接线）与 Agent（契约），
    /// 因此必然高于 Agent；但它**不依赖 Ai/Mcp/Acp**——那些是具体能力，
    /// 由宿主决定用不用。同层互引被下面的测试禁止，所以 Hosting 不能引用
    /// Ai/Mcp/Acp，这正好守住"装配不绑定具体能力"这条边界。
    /// </remarks>
    private static readonly Dictionary<string, int> Layers = new(StringComparer.Ordinal)
    {
        // HostProtocol 与 Kernel 同层（都 0）且互相不引用：
        // 它是**宿主契约**，零依赖，不引用 Kernel 也不被 Kernel 引用。
        // 放最底层是为了说明"它可以被任何东西引用，而它自己什么都不依赖"——
        // 契约一旦依赖运行时实现，别人的宿主就用不了了。
        ["Dougong.HostProtocol"] = 0,
        ["Dougong.Kernel"] = 0,
        ["Dougong.Agent"] = 1,
        ["Dougong.Hosting"] = 2,
        ["Dougong.Ai"] = 3,
        ["Dougong.Mcp"] = 3,
        ["Dougong.Acp"] = 3,
        ["Dougong.Cli"] = 4,
    };

    [Fact]
    public void Kernel_references_no_ui_or_desktop_assembly()
    {
        var referenced = typeof(DougongKernel).Assembly
            .GetReferencedAssemblies()
            .Select(a => a.Name ?? string.Empty)
            .ToArray();

        var violations = referenced
            .Where(name => ForbiddenUiPrefixes.Any(prefix =>
                name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
            .ToArray();

        Assert.True(
            violations.Length == 0,
            $"Dougong.Kernel 不得引用 UI/桌面程序集，实际引用：{string.Join(", ", violations)}");
    }

    [Fact]
    public void Kernel_public_api_stays_in_kernel_namespace()
    {
        var offenders = typeof(DougongKernel).Assembly
            .GetExportedTypes()
            .Where(t => t.Namespace is null ||
                        !t.Namespace.StartsWith("Dougong.Kernel", StringComparison.Ordinal))
            .Select(t => t.FullName ?? t.Name)
            .ToArray();

        Assert.True(
            offenders.Length == 0,
            $"内核公开类型必须位于 Dougong.Kernel 命名空间，越界类型：{string.Join(", ", offenders)}");
    }

    [Fact]
    public void Sdk_projects_follow_the_acyclic_dependency_direction()
    {
        var violations = new List<string>();

        foreach (var (project, references) in LoadProjectGraph())
        {
            if (!Layers.TryGetValue(project, out var ownLayer)) continue;

            foreach (var reference in references)
            {
                if (!Layers.TryGetValue(reference, out var referencedLayer)) continue;

                if (referencedLayer >= ownLayer)
                {
                    violations.Add($"{project}（L{ownLayer}）→ {reference}（L{referencedLayer}）");
                }
            }
        }

        Assert.True(
            violations.Count == 0,
            "SDK 依赖方向被破坏，只允许指向更底层（Kernel ← Agent ← {Ai,Mcp,Acp} ← Cli）：" +
            string.Join("；", violations));
    }

    [Fact]
    public void No_sdk_project_references_desktop_or_ui_dependencies()
    {
        var violations = new List<string>();

        foreach (var file in AllProjectFiles())
        {
            var document = XDocument.Load(file);
            var project = Path.GetFileNameWithoutExtension(file);

            foreach (var reference in document.Descendants("ProjectReference"))
            {
                var include = (string?)reference.Attribute("Include") ?? string.Empty;
                if (include.Contains("DougongDesktop", StringComparison.OrdinalIgnoreCase))
                {
                    violations.Add($"{project} → {include}");
                }
            }

            foreach (var package in document.Descendants("PackageReference"))
            {
                var include = (string?)package.Attribute("Include") ?? string.Empty;
                if (ForbiddenUiPrefixes.Any(prefix =>
                        include.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
                {
                    violations.Add($"{project} → {include}");
                }
            }
        }

        Assert.True(
            violations.Count == 0,
            "SDK 工程不得引用桌面端工程或 UI 依赖：" + string.Join("；", violations));
    }

    /// <summary>
    /// Agent 层必须能脱离内核单独工作。
    /// </summary>
    /// <remarks>
    /// 这条此前只靠约定：`Dougong.Agent.csproj` 从 M0 建仓起就引用了
    /// `Dougong.Kernel`，而源码从没用过它的任何类型。那行引用让
    /// "Agent 可脱离内核"看起来不成立，也让"L1 零引用"需要额外解释。
    /// <para>
    /// 断言两层：工程图上不许有这条引用；已编译程序集的依赖里也不许有。
    /// 只查 csproj 不够——传递引用也可能把 Kernel 带进来。
    /// </para>
    /// </remarks>
    [Fact]
    public void Agent_layer_does_not_depend_on_kernel()
    {
        var agentProject = Path.Combine(RepoRoot, "src", "Dougong.Agent", "Dougong.Agent.csproj");

        var declared = XDocument.Load(agentProject)
            .Descendants("ProjectReference")
            .Select(r => (string?)r.Attribute("Include") ?? string.Empty)
            .Where(include => include.Contains("Dougong.Kernel", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        Assert.True(
            declared.Length == 0,
            "Dougong.Agent 不得引用 Dougong.Kernel（Agent 要能脱离内核单独工作）："
                + string.Join(", ", declared));

        // 传递依赖也要查：csproj 干净但某处间接引入同样破坏独立性。
        // 注意本测试工程不引用 Dougong.Agent（这本身就是分层正确的体现），
        // 所以只能按程序集名加载，不能用 typeof。
        var agentPath = Path.Combine(
            RepoRoot, "src", "Dougong.Agent", "bin", "Release", "net10.0", "Dougong.Agent.dll");
        if (!File.Exists(agentPath))
        {
            agentPath = Path.Combine(
                RepoRoot, "src", "Dougong.Agent", "bin", "Debug", "net10.0", "Dougong.Agent.dll");
        }

        Assert.True(File.Exists(agentPath), $"找不到 Dougong.Agent.dll：{agentPath}");

        var kernelName = typeof(DougongKernel).Assembly.GetName().Name;
        var agentAssembly = System.Reflection.Assembly.LoadFile(agentPath);

        var referenced = agentAssembly.GetReferencedAssemblies()
            .Select(a => a.Name)
            .ToArray();

        Assert.DoesNotContain(kernelName, referenced);
    }

    [Fact]
    public void Kernel_has_no_packages_beyond_extensions_abstractions()
    {
        var kernelProject = Path.Combine(RepoRoot, "src", "Dougong.Kernel", "Dougong.Kernel.csproj");

        var offenders = XDocument.Load(kernelProject)
            .Descendants("PackageReference")
            .Select(package => (string?)package.Attribute("Include") ?? string.Empty)
            .Where(name => !name.StartsWith("Microsoft.Extensions.", StringComparison.Ordinal))
            .ToArray();

        Assert.True(
            offenders.Length == 0,
            "内核第三方依赖只允许 Microsoft.Extensions.* 抽象，越界包：" + string.Join(", ", offenders));
    }

    private static Dictionary<string, string[]> LoadProjectGraph()
    {
        var graph = new Dictionary<string, string[]>(StringComparer.Ordinal);

        foreach (var file in SdkProjectFiles())
        {
            graph[Path.GetFileNameWithoutExtension(file)] = XDocument.Load(file)
                .Descendants("ProjectReference")
                .Select(reference => (string?)reference.Attribute("Include"))
                .Where(include => include is not null)
                .Select(include => Path.GetFileNameWithoutExtension(
                    include!.Replace('\\', Path.DirectorySeparatorChar)))
                .ToArray();
        }

        return graph;
    }

    private static IEnumerable<string> SdkProjectFiles() =>
        Directory.EnumerateFiles(Path.Combine(RepoRoot, "src"), "*.csproj", SearchOption.AllDirectories);

    private static IEnumerable<string> AllProjectFiles() =>
        SdkProjectFiles().Concat(
            Directory.EnumerateFiles(Path.Combine(RepoRoot, "samples"), "*.csproj", SearchOption.AllDirectories));

    /// <summary>
    /// CLI 的可见输出必须全部经过 <c>Terminal/Output.cs</c>，不得散落 Console 调用。
    /// </summary>
    /// <remarks>
    /// <b>这条断言的作用是"改造不漏"</b>：把终端呈现改成按能力分档之后，
    /// 只要还有一处直接 <c>Console.Write</c>，那一处就会在任何档位下都是裸文本——
    /// 表现为"某些行有颜色、某些行没有"，而这在测试里看不出来（测试只比对文本）。
    /// <para>
    /// 靠人眼检查 30 多处调用会漏，所以用静态扫描钉住。它失败时会直接指出文件与行号。
    /// </para>
    /// <para>
    /// <b>允许的例外只有两个</b>：<c>Output.cs</c> 自己（它就是实现），
    /// 以及 <c>Console.Error</c> 的直接调用（错误输出不走终端装饰，
    /// 它必须永远能在最差的环境里工作）。本断言只查 <c>Console.Write</c>/<c>WriteLine</c>。
    /// </para>
    /// </remarks>
    [Fact]
    public void Cli_visible_output_goes_through_Output()
    {
        var cliSource = Path.Combine(RepoRoot, "src", "Dougong.Cli");
        var offenders = new List<string>();

        foreach (var file in Directory.EnumerateFiles(cliSource, "*.cs", SearchOption.AllDirectories))
        {
            // 构建产物目录不该被扫到。
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                || file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
                continue;

            // Output.cs 是实现本体；Diagnostics 内部用 Console.Error，本断言不查 Error。
            if (Path.GetFileName(file) is "Output.cs") continue;

            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i];

                // 注释里提到 Console 不算。
                var trimmed = line.TrimStart();
                if (trimmed.StartsWith("//", StringComparison.Ordinal)
                    || trimmed.StartsWith("///", StringComparison.Ordinal)
                    || trimmed.StartsWith("*", StringComparison.Ordinal))
                    continue;

                // 只查标准输出。Console.Error 允许直接调用。
                var isStdout = line.Contains("Console.Write(", StringComparison.Ordinal)
                    || line.Contains("Console.WriteLine(", StringComparison.Ordinal);
                if (!isStdout) continue;

                offenders.Add($"{Path.GetFileName(file)}:{i + 1}  {trimmed}");
            }
        }

        var detail = string.Join(Environment.NewLine + "  ", offenders);
        Assert.True(
            offenders.Count == 0,
            "CLI 的标准输出必须经过 Terminal/Output.cs，以下调用绕过了它："
            + Environment.NewLine + "  " + detail
            + Environment.NewLine + "（Console.Error 不受此限——错误输出不走终端装饰。）");
    }
}
