using System.Diagnostics;
using System.Text;

namespace Dougong.Agent.Comparison;

/// <summary>
/// API 兼容机制的验收：基线存在，且破坏性变更真的能被检出。
/// </summary>
/// <remarks>
/// 背景：`docs/compat-policy.md` 声称"六个包的公开 API 变更都会被自动检出"。
/// 这句话必须被验证，否则它只是一句声明——本项目已出现过
/// "能力只在文档里"的情况（G1：上下文压缩从未接入循环）。
/// <para>
/// 最要紧的一条是**插入一个真实的破坏性变更，确认 `CP0002` 出现**：
/// 只检查 `EnablePackageValidation` 属性有没有写，测不出机制是否真的在工作。
/// 那属于"检查配置而不是检查行为"。
/// </para>
/// </remarks>
public static class ApiCompatChecks
{
    /// <summary>
    /// 受 ApiCompat 保护的包。必须与 <c>scripts/update-api-baseline.sh</c> 一致。
    /// </summary>
    private static readonly string[] Packages =
        ["Dougong.Kernel", "Dougong.Agent", "Dougong.Hosting", "Dougong.Ai", "Dougong.Mcp", "Dougong.Acp"];

    public static async Task<int> RunAsync()
    {
        var failures = 0;
        failures += Check("all-sdk-packages-have-baseline", AllHaveBaseline);
        failures += Check("baseline-list-matches-script", BaselineMatchesScript);
        failures += await CheckAsync("breaking-change-is-detected", BreakingChangeDetectedAsync);
        failures += Check("cli-is-deliberately-excluded", CliExcluded);

        Console.WriteLine(failures == 0
            ? "APICOMPAT: ALL PASS"
            : $"APICOMPAT: {failures} FAILURE(S)");
        return failures;
    }

    /// <summary>六个包的基线包都必须存在——缺一个就等于那个包没有保护。</summary>
    private static string? AllHaveBaseline()
    {
        var root = FindRepoRoot();
        var missing = new List<string>();

        foreach (var pkg in Packages)
        {
            var path = Path.Combine(root, "artifacts", "baseline", $"{pkg}.nupkg");
            if (!File.Exists(path)) missing.Add(pkg);
        }

        if (missing.Count > 0)
            return $"缺少基线：{string.Join(", ", missing)}"
                + "（跑 scripts/update-api-baseline.sh）";

        return null;
    }

    /// <summary>
    /// 基线脚本维护的包列表必须与本检查一致。
    /// </summary>
    /// <remarks>
    /// 两处清单不一致时最容易出现的事：新包加了 ApiCompat 但基线脚本没加，
    /// 于是那个包永远没有基线，pack 时静默跳过校验。
    /// </remarks>
    private static string? BaselineMatchesScript()
    {
        var root = FindRepoRoot();
        var script = Path.Combine(root, "scripts", "update-api-baseline.sh");

        if (!File.Exists(script)) return "找不到 scripts/update-api-baseline.sh";

        var text = File.ReadAllText(script);

        foreach (var pkg in Packages)
        {
            if (!text.Contains(pkg, StringComparison.Ordinal))
                return $"基线脚本里没有 {pkg}：那个包将永远没有基线";
        }

        return null;
    }

    /// <summary>
    /// 插入一个真实的破坏性变更，确认 ApiCompat 报 `CP0002`。
    /// </summary>
    /// <remarks>
    /// 必须**真的改代码再改回来**，不能只读配置。
    /// 只检查 <c>EnablePackageValidation</c> 属性存在，测的是配置而不是行为——
    /// 那是本项目反复避免的假通过。
    /// <para>
    /// 改动落在一个只读方法上：加一个必填参数属于签名破坏，
    /// ApiCompat 报的是 CP0002（成员在基线上存在、当前不存在）。
    /// </para>
    /// </remarks>
    private static async Task<string?> BreakingChangeDetectedAsync()
    {
        var root = FindRepoRoot();
        var target = Path.Combine(root, "src", "Dougong.Ai", "ChatClientFactory.cs");

        if (!File.Exists(target)) return "找不到 Doungong.Ai/ChatClientFactory.cs";

        // 只匹配单行片段：文件是 CRLF，跨行字面量在 C# 里匹配不上。
        // 上一版就是这样让本项"永远跳过"的。
        const string anchor = "string apiKey, HttpClient? http = null)";
        const string brokenAnchor = "string apiKey, HttpClient? http = null, bool breakingProbe = false)";
        const string original = anchor;
        const string broken = brokenAnchor;

        var text = await File.ReadAllTextAsync(target).ConfigureAwait(false);
        if (!text.Contains(original, StringComparison.Ordinal))
            return "找不到预期的签名：本项无从验证（方法签名变了就更新这条检查）";

        try
        {
            await File.WriteAllTextAsync(
                target, text.Replace(original, broken)).ConfigureAwait(false);

            var (exitCode, output) = await RunPackAsync(
                Path.Combine(root, "src", "Dougong.Ai")).ConfigureAwait(false);

            if (!output.Contains("CP0002", StringComparison.Ordinal))
                return $"插入破坏性变更后没有报 CP0002（退出码 {exitCode}）。"
                    + "ApiCompat 可能没有真正生效。";

            return null;
        }
        finally
        {
            // 还原源码**之后必须重建**。
            // 只还原源码是不够的：bin/ 里留着被探测版编译出的 DLL，
            // 而后续步骤（CI 里的 dotnet pack --no-build）会直接用那个产物，
            // 于是"我改过了又改回来"变成了"最终产物被永久污染"。
            // 这个缺陷真实发生过一次：CI 在 pack 阶段报 CP0002，
            // 而源码与基线都是干净的。
            await File.WriteAllTextAsync(target, text).ConfigureAwait(false);
            await RebuildAsync(Path.Combine(root, "src", "Dougong.Ai")).ConfigureAwait(false);
        }
    }

    /// <summary>`Dougong.Cli` 必须**不在**保护范围——它是工具，不是库。</summary>
    /// <remarks>
    /// 这条看似多余，但它防止将来有人"顺手"给 CLI 也加上校验：
    /// 那会让每次调整命令行输出的内部类型都触发 API 破坏告警，
    /// 而 CLI 的公开类型本来就不是给 API 消费者用的。
    /// </remarks>
    private static string? CliExcluded()
    {
        var root = FindRepoRoot();
        var csproj = Path.Combine(root, "src", "Dougong.Cli", "Dougong.Cli.csproj");

        if (!File.Exists(csproj)) return "找不到 Dougong.Cli.csproj";

        var text = File.ReadAllText(csproj);
        if (text.Contains("EnablePackageValidation", StringComparison.Ordinal))
            return "Dougong.Cli 不应启用 ApiCompat：它是工具不是库，见 docs/compat-policy.md §二";

        return null;
    }

    /// <summary>
    /// 还原源码后重建，使 bin/ 里的产物与源码一致。
    /// </summary>
    /// <remarks>
    /// 返回值不重要——这里只关心"产物被刷新了"。若构建失败，
    /// 后续检查会以各自的方式失败，比吞掉错误更好诊断。
    /// </remarks>
    private static async Task RebuildAsync(string projectDir)
    {
        var startInfo = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add("build");
        startInfo.ArgumentList.Add(projectDir);
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add("Release");
        startInfo.Environment["DOTNET_CLI_UI_LANGUAGE"] = "en";

        using var process = new Process { StartInfo = startInfo };
        process.Start();
        _ = process.StandardOutput.ReadToEndAsync();
        _ = process.StandardError.ReadToEndAsync();

        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
        }
    }

    private static async Task<(int ExitCode, string Output)> RunPackAsync(string projectDir)
    {
        var startInfo = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add("pack");
        startInfo.ArgumentList.Add(projectDir);
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add("Release");
        startInfo.Environment["DOTNET_CLI_UI_LANGUAGE"] = "en";

        using var process = new Process { StartInfo = startInfo };
        process.Start();

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();

        // 超时是检查项的一部分：pack 卡住会让这条检查挂起而不是失败。
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
}
