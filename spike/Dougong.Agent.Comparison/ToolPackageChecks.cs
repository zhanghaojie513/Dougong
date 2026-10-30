using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using System.Text.Json;

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
        failures += Check("docs-install-version-matches-build", DocsVersionMatchesBuild);
        failures += Check("version-not-already-published", VersionNotAlreadyPublished);
        failures += Check("changelog-heading-matches-version", ChangelogMatchesVersion);
        failures += Check("readme-test-count-matches-reality", ReadmeTestCount);
        failures += Check("docs-have-no-broken-relative-links", NoBrokenDocLinks);
        failures += Check("capability-progress-counts-are-consistent", ProgressCountsConsistent);
        failures += Check("no-hardcoded-version-in-sources", NoHardcodedVersion);
        failures += await CheckAsync("version-is-consistent-across-outputs", VersionConsistentAsync);

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
    /// <summary>
    /// 源码里不得出现硬编码的版本文本。
    /// </summary>
    /// <remarks>
    /// <b>这条防的是一个发版级事故</b>：版本号曾硬编码在 6 处
    /// （CLI 5 处、ACP 1 处）。只改 <c>Directory.Build.props</c> 时，
    /// `dougong --version` 会报旧版本——而用户和脚本正是靠它判断装没装对。
    /// <para>
    /// 判据是"源码里不出现形如 <c>N.N.N-xxx</c> 的字符串字面量"，
    /// **注释除外**（文档里举例是合理的）。用行级判断而不是全文匹配，
    /// 就是为了不误报注释。
    /// </para>
    /// </remarks>
    private static string? NoHardcodedVersion()
    {
        var root = FindRepoRoot();
        var pattern = new System.Text.RegularExpressions.Regex(
            @"\d+\.\d+\.\d+-(alpha|beta|rc|preview)");

        foreach (var dir in new[] { "src", "spike", "samples" })
        {
            var path = Path.Combine(root, dir);
            if (!Directory.Exists(path)) continue;

            foreach (var file in Directory.EnumerateFiles(path, "*.cs", SearchOption.AllDirectories))
            {
                if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}",
                        StringComparison.Ordinal)) continue;

                var lines = File.ReadAllLines(file);
                for (var i = 0; i < lines.Length; i++)
                {
                    var line = lines[i];
                    var trimmed = line.TrimStart();

                    // 注释行放过：文档与示例里写版本号是正常的。
                    if (trimmed.StartsWith("//", StringComparison.Ordinal)
                        || trimmed.StartsWith("*", StringComparison.Ordinal)
                        || trimmed.StartsWith("/*", StringComparison.Ordinal)) continue;

                    if (!pattern.IsMatch(line)) continue;

                    // 行内注释里提到版本号不算违规。
                    if (line.Contains("///", StringComparison.Ordinal)) continue;

                    return $"{Path.GetFileName(file)}:{i + 1} 硬编码了版本号：{trimmed}";
                }
            }
        }

        return null;
    }

    /// <summary>
    /// `--version`、`--help` 首行、ACP <c>agentInfo.version</c> 三者必须一致。
    /// </summary>
    /// <remarks>
    /// 三条路径分别从不同的程序集读版本（CLI 自己、CLI 自己、Acp）。
    /// 只要有人把它们改回字面量，这里就会不等——而单看任何一条都看不出来。
    /// </remarks>
    private static async Task<string?> VersionConsistentAsync()
    {
        var version = await RunCliAsync(["--version"], clearProviderConfig: true);
        if (version.ExitCode != 0) return $"--version 退出码 {version.ExitCode}";

        var declared = version.Stdout.Trim().Split(' ').LastOrDefault();
        if (string.IsNullOrWhiteSpace(declared)) return $"--version 输出无法解析：{Trim(version.Stdout)}";

        var help = await RunCliAsync(["--help"], clearProviderConfig: true);
        var helpFirst = help.Stdout.ReplaceLineEndings("\n").Split('\n')[0];
        if (!helpFirst.Contains(declared, StringComparison.Ordinal))
            return $"--help 首行的版本与 --version 不一致：{Trim(helpFirst)} vs {declared}";

        var acp = await RunAcpInitializeAsync();
        if (acp is null) return "ACP initialize 没有返回 agentInfo.version";
        if (acp != declared)
            return $"ACP agentInfo.version={acp} 与 --version={declared} 不一致";

        return null;
    }

    /// <summary>起一次 ACP，取 agentInfo.version。</summary>
    private static async Task<string?> RunAcpInitializeAsync()
    {
        var cliDll = Path.Combine(
            FindRepoRoot(), "src", "Dougong.Cli", "bin", "Release", "net10.0", "Dougong.Cli.dll");

        if (!File.Exists(cliDll)) return null;

        var startInfo = new ProcessStartInfo("dotnet")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        startInfo.ArgumentList.Add(cliDll);
        startInfo.ArgumentList.Add("--acp");

        // ACP 模式要求 provider 配置存在，否则起不来；给占位值即可，
        // 这条检查只读 initialize 的响应，不发任何模型请求。
        startInfo.Environment["DOUGONG_PROVIDER_BASE"] = "https://example.invalid";
        startInfo.Environment["DOUGONG_PROVIDER_MODEL"] = "probe";
        startInfo.Environment["DOUGONG_PROVIDER_KEY"] = "probe";

        using var process = new Process { StartInfo = startInfo };
        process.Start();

        await process.StandardInput.WriteLineAsync(
            "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{\"protocolVersion\":1}}");
        await process.StandardInput.FlushAsync();

        var readLine = process.StandardOutput.ReadLineAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var completed = await Task.WhenAny(readLine, Task.Delay(Timeout.Infinite, timeout.Token));

        if (completed != readLine)
        {
            try { process.Kill(entireProcessTree: true); } catch (Exception) { }
            return null;
        }

        var line = await readLine;
        try { process.Kill(entireProcessTree: true); } catch (Exception) { }

        if (string.IsNullOrWhiteSpace(line)) return null;

        try
        {
            using var document = JsonDocument.Parse(line);
            return document.RootElement
                .GetProperty("result").GetProperty("agentInfo").GetProperty("version")
                .GetString();
        }
        catch (JsonException)
        {
            return null;
        }
    }
    /// <summary>
    /// 文档里安装命令写的版本号必须等于当前构建版本。
    /// </summary>
    /// <remarks>
    /// <b>已有的 <c>docs-pin-tool-version-on-install</c> 只查"带没带 --version"</b>，
    /// 不查版本号对不对。所以发版后四处安装命令全都指向旧版本，检查照样全绿——
    /// 2026-10-29 发 alpha.2 时就是这样漏掉的，靠人工 grep 才发现。
    /// <para>
    /// 比较对象是 <c>Directory.Build.props</c> 的 <c>&lt;Version&gt;</c>（构建的真实版本），
    /// 不是从 nuget.org 查——检查必须离线可跑。发布失败或包尚未上架时，
    /// 版本号已经在 props 里了，文档就该跟着走。
    /// </para>
    /// </remarks>
    private static string? DocsVersionMatchesBuild()
    {
        var root = FindRepoRoot();

        var props = Path.Combine(root, "Directory.Build.props");
        if (!File.Exists(props)) return "找不到 Directory.Build.props";

        var match = System.Text.RegularExpressions.Regex.Match(
            File.ReadAllText(props), @"<Version>([^<]+)</Version>");
        if (!match.Success) return "Directory.Build.props 里没有 <Version>";

        var expected = match.Groups[1].Value.Trim();

        string[] docs =
        [
            Path.Combine(root, "README.md"),
            Path.Combine(root, "README.en.md"),
            Path.Combine(root, "docs", "getting-started.md"),
        ];

        foreach (var path in docs)
        {
            if (!File.Exists(path)) continue;

            var lines = File.ReadAllLines(path);
            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                if (!line.Contains("dotnet tool install", StringComparison.Ordinal)) continue;
                if (!line.Contains("Dougong.Cli", StringComparison.Ordinal)) continue;

                var versionMatch = System.Text.RegularExpressions.Regex.Match(
                    line, @"--version\s+(\S+)");
                if (!versionMatch.Success) continue;   // 不带版本号由另一条检查负责

                var declared = versionMatch.Groups[1].Value.Trim();
                if (declared != expected)
                {
                    return $"{Path.GetFileName(path)}:{i + 1} 写 --version {declared}，" +
                        $"但当前构建版本是 {expected}";
                }
            }
        }

        return null;
    }
    /// <summary>
    /// 能力对照清单的总览统计必须与逐项表格一致。
    /// </summary>
    /// <remarks>
    /// <b>为什么这条值得自检</b>：那份文档有两处统计——总览表与逐项清单。
    /// 手改一行状态时很容易只改一处，而两处对不上时**没有任何迹象**：
    /// 总览是个正常的百分比，清单看起来也在，只有数一遍才露馅。
    ///
    /// 交给 <c>scripts/check-capability-progress.py</c> 数，这里只跑它并断言退出码。
    /// 重写一遍统计逻辑会引入第二份实现——那正是这类文档最容易出的问题。
    /// </remarks>
    private static string? ProgressCountsConsistent()
    {
        var root = FindRepoRoot();
        var script = Path.Combine(root, "scripts", "check-capability-progress.py");
        if (!File.Exists(script)) return $"找不到核对脚本：{script}";

        var startInfo = new System.Diagnostics.ProcessStartInfo("python")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = root,
        };
        startInfo.ArgumentList.Add("-I");
        startInfo.ArgumentList.Add(script);

        try
        {
            using var process = System.Diagnostics.Process.Start(startInfo)!;
            var stdout = process.StandardOutput.ReadToEnd();
            var stderr = process.StandardError.ReadToEnd();
            process.WaitForExit(30_000);

            // 脚本失败时不要假通过——那会让这条检查变成装饰。
            if (process.ExitCode != 0)
                return $"能力清单统计不一致：{(stderr + stdout).Trim()}";

            return null;
        }
        catch (Exception ex)
        {
            return $"无法运行核对脚本（{ex.GetType().Name}）：{ex.Message}";
        }
    }

    /// <summary>
    /// 所有 Markdown 里的相对链接都必须指向真实存在的文件。
    /// </summary>
    /// <remarks>
    /// <b>这条是 2026-10-29 文档重组时补的</b>：那次把 11 份内部文档移出仓库，
    /// 全仓库有 70 多处引用要改。人工核对容易漏——而漏掉的表现是
    /// GitHub 上一个点了就 404 的链接，读者只会觉得文档烂。
    /// <para>
    /// 只检查相对路径链接（`[x](foo.md)`、`<img src="...">`），
    /// http(s) 链接与锚点不查（前者需要联网，后者靠 GitHub 自己渲染）。
    /// </para>
    /// </remarks>
    private static string? NoBrokenDocLinks()
    {
        var root = FindRepoRoot();
        var broken = new List<string>();

        foreach (var file in Directory.EnumerateFiles(root, "*.md", SearchOption.AllDirectories))
        {
            // 跳过构建产物与第三方目录。
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}",
                    StringComparison.Ordinal)) continue;
            if (file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}",
                    StringComparison.Ordinal)) continue;
            if (file.Contains($"{Path.DirectorySeparatorChar}artifacts{Path.DirectorySeparatorChar}",
                    StringComparison.Ordinal)) continue;

            var directory = Path.GetDirectoryName(file)!;
            var text = File.ReadAllText(file);

            foreach (System.Text.RegularExpressions.Match match in
                System.Text.RegularExpressions.Regex.Matches(text, @"\]\(([^)#\s]+)"))
            {
                var target = match.Groups[1].Value;

                if (target.StartsWith("http", StringComparison.OrdinalIgnoreCase)) continue;
                if (target.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase)) continue;
                if (target.StartsWith("#", StringComparison.Ordinal)) continue;

                // 去掉锚点部分再判存在性。
                var hash = target.IndexOf('#');
                var pathPart = hash >= 0 ? target[..hash] : target;
                if (pathPart.Length == 0) continue;

                var resolved = Path.GetFullPath(Path.Combine(directory, pathPart));
                if (!File.Exists(resolved) && !Directory.Exists(resolved))
                {
                    broken.Add($"{Path.GetRelativePath(root, file)} -> {target}");
                }
            }
        }

        return broken.Count == 0
            ? null
            : $"有 {broken.Count} 个链接指向不存在的路径：{string.Join("; ", broken.Take(5))}";
    }

    /// <summary>
    /// 当前构建版本不能已经在 nuget.org 上。
    /// </summary>
    /// <remarks>
    /// <b>这条是 2026-10-29 补的，起因是一次真实失误</b>：
    /// NuGet 的版本号一旦发布就不能重推，而我当时在已经发布过的
    /// <c>0.1.1-alpha.2</c> 下继续加了 5 项改动，本地包永远推不上去。
    /// 发现时已经白改了一轮，只能升版本号重来。
    /// <para>
    /// 代价：这条检查需要联网查 nuget.org。**查不到时放行**（不阻断离线构建），
    /// 因为"网络不通"与"版本已存在"必须区分——把网络故障判成版本冲突
    /// 会让离线开发寸步难行。
    /// </para>
    /// </remarks>
    private static string? VersionNotAlreadyPublished()
    {
        var root = FindRepoRoot();
        var props = Path.Combine(root, "Directory.Build.props");
        if (!File.Exists(props)) return null;

        var match = System.Text.RegularExpressions.Regex.Match(
            File.ReadAllText(props), @"<Version>([^<]+)</Version>");
        if (!match.Success) return null;

        var version = match.Groups[1].Value.Trim();

        // 只查 Kernel：七个包同版本发布，一个能查到就说明整体已发布。
        var url = $"https://api.nuget.org/v3-flatcontainer/dougong.kernel/{version}/"
            + $"dougong.kernel.{version}.nupkg";

        try
        {
            using var http = new System.Net.Http.HttpClient
            {
                Timeout = TimeSpan.FromSeconds(8),
            };

            using var request = new System.Net.Http.HttpRequestMessage(
                System.Net.Http.HttpMethod.Head, url);
            using var response = http.Send(request);

            if (response.IsSuccessStatusCode)
            {
                return $"版本 {version} 已经发布在 nuget.org 上——"
                    + "版本号不能重用，继续在它下面改会导致包永远推不上去。先升版本号。";
            }

            // 404 = 还没发布，正常。其它状态码不判定（可能是服务端临时问题）。
            return null;
        }
        catch (Exception)
        {
            // 离线或网络故障：不阻断构建。把网络问题判成版本冲突会让离线开发不可用。
            return null;
        }
    }

    /// <summary>
    /// CHANGELOG 里最新一个已定版的小节必须与当前版本号一致。
    /// </summary>
    /// <remarks>
    /// <b>这条的由来</b>：一次发版后又在同一版本号下改了代码，包是从最终代码打的，
    /// 而 CHANGELOG 写成时那些改动还没进——结果"已发布的内容"与"文档记录的内容"
    /// 对不上。复核时只能靠下载 nuget 上的包反射验证才发现。
    /// <para>
    /// 判据：<c>Directory.Build.props</c> 的版本必须出现在 CHANGELOG 的
    /// 某个 <c>## [x.y.z-...]</c> 标题里。这不能保证内容完全正确，
    /// 但能保证"版本号改了而 CHANGELOG 没跟上"立刻被发现。
    /// </para>
    /// </remarks>
    private static string? ChangelogMatchesVersion()
    {
        var root = FindRepoRoot();

        var props = Path.Combine(root, "Directory.Build.props");
        var changelog = Path.Combine(root, "CHANGELOG.md");
        if (!File.Exists(props) || !File.Exists(changelog)) return null;

        var versionMatch = System.Text.RegularExpressions.Regex.Match(
            File.ReadAllText(props), @"<Version>([^<]+)</Version>");
        if (!versionMatch.Success) return null;

        var version = versionMatch.Groups[1].Value.Trim();
        var text = File.ReadAllText(changelog);

        return text.Contains($"## [{version}]", StringComparison.Ordinal)
            ? null
            : $"CHANGELOG 里没有 [ {version} ] 小节——改版本号时忘了同步文档？";
    }

}
