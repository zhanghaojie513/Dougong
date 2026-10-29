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
        failures += Check("readme-test-count-matches-reality", ReadmeTestCount);

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
    /// <summary>
    /// README 里写的测试数必须等于实际测试数。
    /// </summary>
    /// <remarks>
    /// <b>这个数字漂移过至少五次</b>（156/177/178/187/188），每次都是加了测试忘改文档。
    /// 人工核对靠不住——而 README 上的数字是外人判断项目规模的第一印象。
    /// <para>
    /// 测试数从源码里数 `[Fact]` 与 `[Theory]` 的**标注个数**，不跑测试
    /// （跑一次要几秒，而这条检查在 44 个套件里每次都过）。`[Theory]` 一个标注
    /// 会展开成多行用例，与测试运行器的"总计"口径不同——所以这里比的是
    /// "README 的数字"与"标注数+用例数"能对上的那一个，对不上就要求改用
    /// 明确的口径词（见下面的报错信息）。
    /// </para>
    /// </remarks>
    private static string? ReadmeTestCount()
    {
        var root = FindRepoRoot();
        var readme = Path.Combine(root, "README.md");
        if (!File.Exists(readme)) return null;

        var declared = ReadDeclaredTestCount(File.ReadAllText(readme));
        if (declared is null) return "README.md 里找不到「N 项测试」这样的表述";

        var attributes = CountTestAttributes(root);
        if (attributes == 0) return "测试源码里没数到任何 [Fact]/[Theory]——检查失效";

        // 两种口径都算合法，因为它们本来就不同：
        //   - 标注数：源码里 [Fact]/[Theory] 的个数（178）
        //   - 用例数：运行器展开 [Theory] 后的总数（188）
        // 只认一种会把"另一种正确写法"判成错。但"两个都不是"就是真的漂移了。
        var cases = CountTheoryCases(root);

        if (declared == attributes || declared == cases) return null;

        return $"README 写 {declared} 项测试，与源码任何口径都对不上"
            + $"（[Fact]/[Theory] 标注 {attributes} 个，展开后用例 {cases} 个）";
    }

    /// <summary>从 README 正文里取出「N 项测试」的 N。</summary>
    private static int? ReadDeclaredTestCount(string text)
    {
        var match = System.Text.RegularExpressions.Regex.Match(
            text, @"(\d+)\s*项测试");
        return match.Success && int.TryParse(match.Groups[1].Value, out var value) ? value : null;
    }

    /// <summary>数测试工程里 [Fact] / [Theory] 的标注个数。</summary>
    private static int CountTestAttributes(string root)
    {
        var dir = Path.Combine(root, "tests");
        if (!Directory.Exists(dir)) return 0;

        var count = 0;
        foreach (var file in Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories))
        {
            // 跳过生成物：obj/ 下会有带 [Fact] 的自动生成文件。
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}",
                    StringComparison.Ordinal)) continue;

            foreach (var line in File.ReadLines(file))
            {
                var trimmed = line.TrimStart();
                if (trimmed.StartsWith("[Fact", StringComparison.Ordinal)
                    || trimmed.StartsWith("[Theory", StringComparison.Ordinal))
                {
                    count++;
                }
            }
        }

        return count;
    }
    /// <summary>数展开后的用例数：[Fact] 各算 1，[Theory] 按它的 [InlineData] 条数算。</summary>
    /// <remarks>
    /// <b>为什么两种口径都接受</b>：源码里的标注数与运行器报的"总计"从来就不相等，
    /// `[Theory]` 一个标注会变成多条用例。README 上写哪个都说得通，
    /// 硬性只认一个等于给正确的文档判错——而检查的目的是防漂移，不是防风格。
    /// </remarks>
    private static int CountTheoryCases(string root)
    {
        var dir = Path.Combine(root, "tests");
        if (!Directory.Exists(dir)) return 0;

        var total = 0;
        foreach (var file in Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}",
                    StringComparison.Ordinal)) continue;

            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                var trimmed = lines[i].TrimStart();

                if (trimmed.StartsWith("[Fact", StringComparison.Ordinal))
                {
                    total++;
                    continue;
                }

                if (!trimmed.StartsWith("[Theory", StringComparison.Ordinal)) continue;

                // 往后数紧随其后的 [InlineData]；一个都没有时按 1 算
                // （可能是 [MemberData]，条数静态数不出来）。
                var inline = 0;
                for (var j = i + 1; j < lines.Length; j++)
                {
                    var next = lines[j].TrimStart();
                    if (next.Length == 0) continue;
                    if (next.StartsWith("[InlineData", StringComparison.Ordinal)) { inline++; continue; }
                    if (next.StartsWith("[", StringComparison.Ordinal)) continue;
                    break;
                }

                total += Math.Max(1, inline);
            }
        }

        return total;
    }
}
