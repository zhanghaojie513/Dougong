using System.Diagnostics;
using System.Text;

namespace Dougong.Agent.Comparison;

/// <summary>
/// `-- load-plugin` 的验收：新人能不能用它确认"我的插件装上了"。
/// </summary>
/// <remarks>
/// 这个入口存在的理由是补上指南 §6 的断点：读者能编译出 dll，却没有办法确认
/// 内核认不认它。所以它必须：
/// <list type="number">
/// <item>成功时明确说"装上了"（而不只是打印一个状态枚举）</item>
/// <item>失败时给出**可操作的**原因，不是抛个异常了事</item>
/// <item>路径写错时给出正确用法</item>
/// </list>
/// <para>
/// 三条都走真实进程：`load-plugin` 会真的加载程序集进 ALC，
/// 进程内测会和宿主已有的类型产生干扰。
/// </para>
/// </remarks>
public static class LoadPluginChecks
{
    public static async Task<int> RunAsync()
    {
        var failures = 0;
        failures += await CheckAsync("loads-a-real-plugin", LoadsRealPluginAsync);
        failures += await CheckAsync("missing-path-shows-usage", MissingPathShowsUsageAsync);
        failures += await CheckAsync("bad-file-gives-actionable-reasons", BadFileGivesReasonsAsync);
        failures += await CheckAsync("unload-is-verified", UnloadIsVerifiedAsync);

        Console.WriteLine(failures == 0
            ? "LOAD-PLUGIN: ALL PASS"
            : $"LOAD-PLUGIN: {failures} FAILURE(S)");
        return failures;
    }

    /// <summary>按指南原样生成的插件必须能加载到 Active。</summary>
    private static async Task<string?> LoadsRealPluginAsync()
    {
        var dll = await BuildGuideSampleAsync();
        if (dll is null) return "样例插件构建失败";

        try
        {
            var result = await RunAsync(["load-plugin", dll]);
            var combined = result.Stdout + result.Stderr;

            if (!combined.Contains("状态：Active", StringComparison.Ordinal))
                return $"没有加载到 Active。输出：{Trim(combined)}";

            if (result.ExitCode != 0)
                return $"成功加载时退出码应为 0，实际 {result.ExitCode}";

            // 只说"状态：Active"对新人不构成确认，必须有明确结论
            if (!combined.Contains("成功", StringComparison.Ordinal))
                return "只打了状态枚举，没有明确告诉用户「装上了」";

            return null;
        }
        finally
        {
            CleanupSample();
        }
    }

    /// <summary>不给路径时要打印用法，而不是崩。</summary>
    private static async Task<string?> MissingPathShowsUsageAsync()
    {
        var result = await RunAsync(["load-plugin"]);
        var combined = result.Stdout + result.Stderr;

        if (!combined.Contains("用法", StringComparison.Ordinal))
            return "没有打印用法";

        if (!combined.Contains("load-plugin", StringComparison.Ordinal))
            return "用法里没有给出命令形式";

        if (result.ExitCode != 2)
            return $"参数不全时退出码应为 2，实际 {result.ExitCode}";

        return null;
    }

    /// <summary>
    /// 加载非插件 DLL 时，要列出可操作的原因而不是只抛异常。
    /// </summary>
    /// <remarks>
    /// 用一个真实存在的、但不是插件的 dll（内核自己）。
    /// 这是新人最可能踩的坑：把内核 dll 当插件传进来。
    /// </remarks>
    private static async Task<string?> BadFileGivesReasonsAsync()
    {
        var kernelDll = Path.Combine(
            FindRepoRoot(), "src", "Dougong.Kernel", "bin", "Release", "net10.0", "Dougong.Kernel.dll");

        if (!File.Exists(kernelDll)) return "找不到内核 dll（先构建 solution）";

        var result = await RunAsync(["load-plugin", kernelDll]);
        var combined = result.Stdout + result.Stderr;

        if (result.ExitCode == 0)
            return "把内核 dll 当插件加载却返回成功";

        // 必须给出可操作的原因，不能只是一个异常类型名
        if (!combined.Contains("常见原因", StringComparison.Ordinal))
            return $"失败时没有给出排查方向。输出：{Trim(combined)}";

        if (!combined.Contains("Private", StringComparison.Ordinal)
            && !combined.Contains("EnableDynamicLoading", StringComparison.Ordinal))
            return "排查方向里没有提到最常见的两类配置错误";

        return null;
    }

    /// <summary>卸载必须被验证——只会加载不会卸载的插件会泄漏。</summary>
    private static async Task<string?> UnloadIsVerifiedAsync()
    {
        var dll = await BuildGuideSampleAsync();
        if (dll is null) return "样例插件构建失败";

        try
        {
            var result = await RunAsync(["load-plugin", dll]);
            var combined = result.Stdout + result.Stderr;

            if (!combined.Contains("卸载：成功", StringComparison.Ordinal))
                return $"没有验证卸载。输出：{Trim(combined)}";

            return null;
        }
        finally
        {
            CleanupSample();
        }
    }

    // ── 样例插件 ─────────────────────────────────────────────────

    /// <summary>
    /// 探针工程的目录。
    /// </summary>
    /// <remarks>
    /// 名字带进程 ID：两件事会互相干扰——并发跑两个检查、以及读者自己正好有个
    /// 同名目录。这个目录在 samples/ 下而不是临时目录，是因为指南里 csproj 的
    /// `<ProjectReference>` 是相对路径（`..\..\src\...`），
    /// 换个层级就测不到"照抄能不能编译"这件事。
    /// </remarks>
    /// <remarks>
    /// **固定名字，不带进程 ID**：Windows 上加载过的 dll 会被锁住，
    /// 进程退出前删不掉。带 PID 的话每次运行都留一个新目录，越跑越多。
    /// 固定名字只是覆盖同一个目录，不累积。
    /// </remarks>
    private static readonly string SampleDir =
        Path.Combine(FindRepoRoot(), "samples", "_load-plugin-probe");

    /// <summary>
    /// 按指南 §1.1/§1.2 原样生成并构建样例插件。
    /// </summary>
    /// <remarks>
    /// **从指南里提取代码块**，而不是在这里复制一份：
    /// 复制会出现"测试通过但指南里的代码是错的"——本项目刚发生过一次
    /// （§1.2 缺 SystemClock，照抄报 CS0246）。
    /// </remarks>
    private static async Task<string?> BuildGuideSampleAsync()
    {
        try
        {
            var root = FindRepoRoot();
            var guide = await File.ReadAllTextAsync(
                Path.Combine(root, "docs", "plugin-author-guide.md")).ConfigureAwait(false);

            var xml = ExtractBlock(guide, "### 1.1 工程文件", "```xml");
            var cs = ExtractBlock(guide, "### 1.2 插件类", "```csharp");

            if (xml is null || cs is null) return null;

            Directory.CreateDirectory(SampleDir);
            await File.WriteAllTextAsync(
                Path.Combine(SampleDir, "Probe.csproj"), xml).ConfigureAwait(false);
            await File.WriteAllTextAsync(
                Path.Combine(SampleDir, "Plugin.cs"), cs).ConfigureAwait(false);

            var build = await RunProcessAsync(
                "dotnet", ["build", SampleDir, "-c", "Release", "-v", "q", "--nologo"]);

            if (build.ExitCode != 0)
            {
                Console.WriteLine($"      [probe] 构建失败：{Trim(build.Output)}");
                return null;
            }

            var dll = Path.Combine(SampleDir, "bin", "Release", "net10.0", "Probe.dll");
            return File.Exists(dll) ? Path.GetFullPath(dll) : null;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"      [probe] {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    private static string? ExtractBlock(string text, string section, string fence)
    {
        var start = text.IndexOf(section, StringComparison.Ordinal);
        if (start < 0) return null;

        var open = text.IndexOf(fence, start, StringComparison.Ordinal);
        if (open < 0) return null;

        var bodyStart = text.IndexOf('\n', open) + 1;
        var close = text.IndexOf("```", bodyStart, StringComparison.Ordinal);
        if (close < 0) return null;

        return text[bodyStart..close];
    }

    private static void CleanupSample()
    {
        try { Directory.Delete(SampleDir, recursive: true); } catch (IOException) { }
    }

    // ── 进程驱动 ─────────────────────────────────────────────────

    private sealed record Result(int ExitCode, string Stdout, string Stderr);

    private static async Task<Result> RunAsync(string[] args)
    {
        var spikeDll = Path.Combine(
            FindRepoRoot(), "spike", "Dougong.Agent.Comparison",
            "bin", "Release", "net10.0", "Dougong.Agent.Comparison.dll");

        if (!File.Exists(spikeDll))
            throw new InvalidOperationException($"找不到 spike 产物：{spikeDll}");

        var (exitCode, output) = await RunProcessAsync("dotnet", [spikeDll, .. args]);
        return new Result(exitCode, output, output);
    }

    private static async Task<(int ExitCode, string Output)> RunProcessAsync(
        string file, string[] args)
    {
        var startInfo = new ProcessStartInfo(file)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in args) startInfo.ArgumentList.Add(a);
        startInfo.Environment["DOTNET_CLI_UI_LANGUAGE"] = "en";

        using var process = new Process { StartInfo = startInfo };
        process.Start();

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();

        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            return (-1, "超时");
        }

        return (process.ExitCode, await stdoutTask + await stderrTask);
    }

    private static string FindRepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "Dougong.slnx")))
        {
            dir = Path.GetDirectoryName(dir);
        }
        return dir ?? throw new InvalidOperationException("找不到仓库根目录（Dougong.slnx）");
    }

    private static string Trim(string text)
    {
        var single = text.ReplaceLineEndings(" ").Trim();
        return single.Length <= 300 ? single : single[..300] + "…";
    }

    private static async Task<int> CheckAsync(string name, Func<Task<string?>> action)
    {
        try
        {
            var failure = await action();
            if (failure is null) { Console.WriteLine($"PASS {name}"); return 0; }
            Console.WriteLine($"FAIL {name}: {failure}");
            return 1;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"FAIL {name}: {ex.GetType().Name}: {ex.Message}");
            return 1;
        }
    }
}
