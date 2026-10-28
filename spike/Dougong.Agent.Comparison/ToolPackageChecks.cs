using System.Diagnostics;
using System.IO.Compression;
using System.Text;

namespace Dougong.Agent.Comparison;

/// <summary>
/// `dotnet tool` 安装链路的验收：别人真的装了能不能用。
/// </summary>
/// <remarks>
/// 为什么必须测这一层：本项目此前所有验证都是在源码目录里 `dotnet run`——
/// **从没有人真的 `dotnet tool install` 过一次**。而"装不是能用"恰恰是
/// 发布最常见的失败：依赖没打进包、命令名不对、缺 `--help` 导致新人无从下手。
/// <para>
/// 三条不变量：
/// <list type="number">
/// <item>包里必须自带全部 Dougong 依赖（否则装完报找不到程序集）</item>
/// <item>装完后能在**项目目录之外**运行（否则只是"源码里能跑"）</item>
/// <item>`--help` 不依赖 provider 配置（新人第一件事就是敲它，那时还没配 key）</item>
/// </list>
/// </para>
/// <para>
/// 只验证打包产物内容与 CLI 参数行为，**不真的安装到全局**——
/// 那会污染执行机的 dotnet tool 列表，且需要联网。
/// </para>
/// </remarks>
public static class ToolPackageChecks
{
    public static async Task<int> RunAsync()
    {
        var failures = 0;
        failures += Check("package-contains-all-dougong-assemblies", ContainsDependencies);
        failures += Check("package-declares-tool-command", DeclaresToolCommand);
        failures += await CheckAsync("help-works-without-provider-config", HelpWithoutConfigAsync);
        failures += await CheckAsync("help-lists-every-user-facing-command", HelpListsCommandsAsync);
        failures += Check("gitignore-covers-pack-output", PackOutputIgnored);
        failures += Check("docs-pin-tool-version-on-install", DocsPinVersion);

        Console.WriteLine(failures == 0
            ? "TOOL-PACKAGE: ALL PASS"
            : $"TOOL-PACKAGE: {failures} FAILURE(S)");
        return failures;
    }

    /// <summary>包必须自带全部 Dougong 程序集——否则装完运行时报找不到文件。</summary>
    private static string? ContainsDependencies()
    {
        var nupkg = FindPackOutput();
        if (nupkg is null) return "找不到打包产物（先运行 dotnet pack src/Dougong.Cli）";

        using var zip = ZipFile.OpenRead(nupkg);

        // 这五个是 CLI 直接或间接引用的，缺任何一个运行时都会崩。
        string[] required =
        [
            "Dougong.Cli.dll", "Dougong.Kernel.dll", "Dougong.Agent.dll",
            "Dougong.Ai.dll", "Dougong.Mcp.dll", "Dougong.Acp.dll",
        ];

        var present = zip.Entries
            .Where(e => e.FullName.StartsWith("tools/", StringComparison.Ordinal))
            .Select(e => Path.GetFileName(e.FullName))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var missing = required.Where(r => !present.Contains(r)).ToArray();
        if (missing.Length > 0)
            return $"包里缺少：{string.Join(", ", missing)}"
                + "（装完后运行会报找不到程序集）";

        return null;
    }

    /// <summary>nuspec 必须声明 DotnetTool 类型与命令名。</summary>
    private static string? DeclaresToolCommand()
    {
        var nupkg = FindPackOutput();
        if (nupkg is null) return "找不到打包产物";

        using var zip = ZipFile.OpenRead(nupkg);
        var nuspecEntry = zip.Entries.FirstOrDefault(
            e => e.Name.EndsWith(".nuspec", StringComparison.OrdinalIgnoreCase));

        if (nuspecEntry is null) return "包里没有 nuspec";

        using var reader = new StreamReader(nuspecEntry.Open(), Encoding.UTF8);
        var nuspec = reader.ReadToEnd();

        if (!nuspec.Contains("DotnetTool", StringComparison.Ordinal))
            return "nuspec 没有声明 packageType=DotnetTool：这不被当作工具包";

        // ToolCommandName 写在 csproj 里，不写进 nuspec；
        // 所以这里改查 csproj，确认命令名与文档一致。
        var csproj = File.ReadAllText(Path.Combine(
            FindRepoRoot(), "src", "Dougong.Cli", "Dougong.Cli.csproj"));

        if (!csproj.Contains("<ToolCommandName>dougong</ToolCommandName>", StringComparison.Ordinal))
            return "csproj 的 ToolCommandName 不是 dougong：安装后命令名与文档不符";

        return null;
    }

    /// <summary>
    /// `--help` 必须在没有 provider 配置时也能输出。
    /// </summary>
    /// <remarks>
    /// 真实场景：刚装完工具，第一件事敲 `--help`——那时还没配 key。
    /// 如果 `--help` 排在 provider 配置检查之后，用户看到的是
    /// "缺少 provider 配置"，连有哪些命令都不知道。
    /// </remarks>
    private static async Task<string?> HelpWithoutConfigAsync()
    {
        var result = await RunCliAsync(["--help"], clearProviderConfig: true);
        var combined = result.Stdout + result.Stderr;

        if (result.ExitCode != 0)
            return $"--help 退出码 {result.ExitCode}：{Trim(combined)}";

        if (!combined.Contains("用法", StringComparison.Ordinal))
            return $"没有输出用法：{Trim(combined)}";

        // 最关键：不能因为没配 provider 就拒绝输出帮助
        if (combined.Contains("缺少 provider 配置", StringComparison.Ordinal))
            return "--help 被 provider 配置检查拦住了：新人装完第一件事就是敲它";

        return null;
    }

    /// <summary>帮助里必须列出所有面向用户的命令。</summary>
    private static async Task<string?> HelpListsCommandsAsync()
    {
        var result = await RunCliAsync(["--help"], clearProviderConfig: true);
        var output = result.Stdout;

        string[] commands =
        [
            "--doctor", "--report", "--version", "--help", "--acp",
            "--list", "--resume", "--branch",
        ];

        var missing = commands
            .Where(c => !output.Contains(c, StringComparison.Ordinal))
            .ToArray();

        return missing.Length == 0
            ? null
            : $"帮助里缺少：{string.Join(", ", missing)}";
    }

    /// <summary>打包产物目录不能被提交——它是构建输出。</summary>
    private static string? PackOutputIgnored()
    {
        var gitignore = Path.Combine(FindRepoRoot(), ".gitignore");
        if (!File.Exists(gitignore)) return "找不到 .gitignore";

        var text = File.ReadAllText(gitignore);
        return text.Contains("artifacts/tool", StringComparison.OrdinalIgnoreCase)
            ? null
            : "artifacts/tool 没有被 .gitignore 覆盖：打包产物会被误提交";
    }

    // ── 支撑 ─────────────────────────────────────────────────────

    private static string? FindPackOutput()
    {
        var dir = Path.Combine(FindRepoRoot(), "artifacts", "tool");
        if (!Directory.Exists(dir)) return null;

        return Directory.EnumerateFiles(dir, "Dougong.Cli.*.nupkg")
            .OrderByDescending(f => f)
            .FirstOrDefault();
    }

    private sealed record CliResult(int ExitCode, string Stdout, string Stderr);

    private static async Task<CliResult> RunCliAsync(string[] args, bool clearProviderConfig)
    {
        var cliDll = Path.Combine(
            FindRepoRoot(), "src", "Dougong.Cli", "bin", "Release", "net10.0", "Dougong.Cli.dll");

        if (!File.Exists(cliDll))
            throw new InvalidOperationException($"找不到 CLI 产物：{cliDll}");

        var startInfo = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        startInfo.ArgumentList.Add(cliDll);
        foreach (var a in args) startInfo.ArgumentList.Add(a);

        if (clearProviderConfig)
        {
            foreach (var name in new[]
                     {
                         "DOUGONG_PROVIDER_BASE", "DOUGONG_PROVIDER_MODEL",
                         "DOUGONG_PROVIDER_KEY", "DOUGONG_PROVIDER_PROTOCOL",
                     })
            {
                startInfo.Environment.Remove(name);
            }
        }

        using var process = new Process { StartInfo = startInfo };
        process.Start();

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();

        // 超时是检查项的一部分：--help 理论上立即返回，卡住说明有别的问题。
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            return new CliResult(-1, await stdoutTask, "超时");
        }

        return new CliResult(process.ExitCode, await stdoutTask, await stderrTask);
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

    private static int Check(string name, Func<string?> action)
    {
        try
        {
            var failure = action();
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

    /// <summary>
    /// 文档里的 <c>dotnet tool install</c> 必须带 <c>--version</c>。
    /// </summary>
    /// <remarks>
    /// 真实踩到（2026-10-28 首次发布当天）：<c>dotnet tool install --global Dougong.Cli</c>
    /// 报"找不到 dougong.cli"，而加 <c>--version 0.1.0-alpha.1</c> 立刻成功。
    /// <para>
    /// 根因是 nuget.org 的两套索引不同步：registration 索引已有该版本，
    /// 搜索索引还没有。不带版本号时 NuGet 必须查搜索索引找"最新版"，
    /// 于是失败。这不是我们代码的问题，但**读者会照着文档失败**，
    /// 所以文档必须写带版本号的形式。
    /// </para>
    /// <para>
    /// 判据：三个用户可见入口（README 中/英、getting-started、走查清单）里，
    /// 每条 <c>dotnet tool install ... Dougong.Cli</c> 都要带 <c>--version</c>。
    /// </para>
    /// </remarks>
    private static string? DocsPinVersion()
    {
        var root = FindRepoRoot();
        string[] docs =
        [
            Path.Combine(root, "README.md"),
            Path.Combine(root, "README.en.md"),
            Path.Combine(root, "docs", "getting-started.md"),
            Path.Combine(root, "docs", "walkthrough-checklist.md"),
        ];

        foreach (var path in docs)
        {
            if (!File.Exists(path)) continue;
            var text = File.ReadAllText(path);

            foreach (var line in text.Split('\n'))
            {
                // 只检查真正的命令行，跳过正文里提到它的段落。
                if (!line.Contains("dotnet tool install", StringComparison.Ordinal)) continue;
                if (!line.Contains("Dougong.Cli", StringComparison.Ordinal)) continue;
                if (line.Contains("--version", StringComparison.Ordinal)) continue;

                return $"{Path.GetFileName(path)} 里的安装命令没带 --version：{line.Trim()}"
                    + "  新包在搜索索引收录前，不带版本号会报「找不到 dougong.cli」。";
            }
        }

        return null;
    }
}
