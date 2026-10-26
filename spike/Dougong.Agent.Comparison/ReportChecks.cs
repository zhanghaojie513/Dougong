using System.Diagnostics;
using System.Text;

namespace Dougong.Agent.Comparison;

/// <summary>
/// `dougong --report` 的验收：信息包能不能真的被安全地贴出去。
/// </summary>
/// <remarks>
/// 这份输出的用途就是被贴到公开 issue 里。所以第一位的不是"信息全不全"，
/// 而是**绝不含密钥与用户内容**——一旦带了，泄露会随截图与日志归档扩散，
/// 而且撤销不了。
/// <para>
/// 全部检查走真实进程：`--report` 的价值之一是"在配置不完整时也能跑"，
/// 而那个顺序（排在 provider 配置检查之前）只有真实进程能验证。
/// </para>
/// </remarks>
public static class ReportChecks
{
    /// <summary>用于检测泄露的哨兵值：出现在输出里就说明漏了。</summary>
    private const string KeyCanary = "sk-CANARY-must-not-appear-0123456789";

    public static async Task<int> RunAsync()
    {
        var failures = 0;
        failures += await CheckAsync("report-runs-without-config", RunsWithoutConfigAsync);
        failures += await CheckAsync("report-never-contains-key", NeverContainsKeyAsync);
        failures += await CheckAsync("report-never-contains-session-content", NoSessionContentAsync);
        failures += await CheckAsync("report-flags-suspicious-key-shape", FlagsKeyShapeAsync);
        failures += await CheckAsync("report-has-version-and-commit", HasVersionAsync);
        failures += await CheckAsync("report-lists-session-metadata-only", SessionMetadataAsync);

        Console.WriteLine(failures == 0
            ? "REPORT: ALL PASS"
            : $"REPORT: {failures} FAILURE(S)");
        return failures;
    }

    /// <summary>
    /// 没有任何 provider 配置时也必须能生成。
    /// </summary>
    /// <remarks>
    /// 若 `--report` 排在配置检查之后，缺配置时会先退出 2，
    /// 用户拿不到信息包——而那正是最想发给别人看的时候。
    /// </remarks>
    private static async Task<string?> RunsWithoutConfigAsync()
    {
        var result = await RunReportAsync(new Dictionary<string, string?>());

        if (!result.Stdout.Contains("诊断信息包", StringComparison.Ordinal))
            return $"没有输出信息包。stdout={Trim(result.Stdout)} stderr={Trim(result.Stderr)}";

        if (result.ExitCode != 0)
            return $"生成信息包应返回 0，实际 {result.ExitCode}";

        if (!result.Stdout.Contains("(未设置)", StringComparison.Ordinal))
            return "没有标明哪些配置未设置";

        return null;
    }

    /// <summary>
    /// 密钥不得出现在输出里。
    /// </summary>
    /// <remarks>
    /// 这条是整套里最重要的。加了正向对照：确认 key 确实被读到了
    /// （否则"没泄露"可能只是因为压根没用上）。
    /// </remarks>
    private static async Task<string?> NeverContainsKeyAsync()
    {
        var result = await RunReportAsync(new Dictionary<string, string?>
        {
            ["DOUGONG_PROVIDER_BASE"] = "https://api.example.com/v1",
            ["DOUGONG_PROVIDER_MODEL"] = "m",
            ["DOUGONG_PROVIDER_KEY"] = KeyCanary,
        });

        var combined = result.Stdout + result.Stderr;

        if (combined.Contains(KeyCanary, StringComparison.Ordinal))
            return "信息包里出现了完整密钥";

        // 连片段也不允许：给出前几位会缩小猜测空间，而它对诊断没有价值。
        if (combined.Contains("CANARY", StringComparison.Ordinal))
            return "信息包里出现了密钥片段";

        // 正向对照
        if (!combined.Contains("已设置", StringComparison.Ordinal))
            return "没有报告密钥已设置：本项可能什么都没验证";

        return null;
    }

    /// <summary>
    /// 不得读取会话内容——只允许元信息。
    /// </summary>
    /// <remarks>
    /// 会话日志含用户输入原文。信息包只需要"有几个文件、多大、几条记录"，
    /// 一旦把内容带进去，就等于把用户对话贴到 issue 里。
    /// 这里造一个带哨兵的会话文件，确认哨兵没有出现在输出里。
    /// </remarks>
    private static async Task<string?> NoSessionContentAsync()
    {
        const string canary = "USER-PRIVATE-CONTENT-MUST-NOT-APPEAR";

        var dir = Path.Combine(Path.GetTempPath(), "dg-rep-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);

        try
        {
            var file = Path.Combine(dir, "20261026-000000.jsonl");
            await File.WriteAllTextAsync(file,
                $"{{\"content\":\"{canary}\"}}\n{{\"content\":\"second line\"}}\n");

            var result = await RunReportAsync(new Dictionary<string, string?>
            {
                ["DOUGONG_SESSION_DIR"] = dir,
            });

            var combined = result.Stdout + result.Stderr;

            if (combined.Contains(canary, StringComparison.Ordinal))
                return "信息包里出现了会话内容原文";

            // 正向对照：确认它确实看到了这个文件（否则"没泄露"是因为没读到）
            if (!combined.Contains("20261026-000000.jsonl", StringComparison.Ordinal))
                return "没有列出会话文件：本项可能什么都没验证";

            return null;
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }

    /// <summary>
    /// 密钥含首尾空白时要明确提示——那是常见的复制粘贴错误。
    /// </summary>
    private static async Task<string?> FlagsKeyShapeAsync()
    {
        var result = await RunReportAsync(new Dictionary<string, string?>
        {
            ["DOUGONG_PROVIDER_BASE"] = "https://api.example.com/v1",
            ["DOUGONG_PROVIDER_MODEL"] = "m",
            // 末尾带一个空格：真实场景里很常见
            ["DOUGONG_PROVIDER_KEY"] = "sk-abcdefghijklmnopqrstuvwxyz ",
        });

        if (!result.Stdout.Contains("空白", StringComparison.Ordinal))
            return "密钥含首尾空白时没有提示";

        if (result.Stdout.Contains("abcdefghijklmnop", StringComparison.Ordinal))
            return "提示密钥形态时泄露了密钥内容";

        return null;
    }

    /// <summary>版本与 commit 必须来自程序集，不能是硬编码字符串。</summary>
    private static async Task<string?> HasVersionAsync()
    {
        var result = await RunReportAsync(new Dictionary<string, string?>());

        if (!result.Stdout.Contains("版本", StringComparison.Ordinal))
            return "没有输出版本";

        // SourceLink 会把 commit 附在 InformationalVersion 里。
        // 没有它说明构建方式变了，报问题时无法定位到具体代码。
        if (!result.Stdout.Contains("commit", StringComparison.Ordinal))
            return "没有输出 commit：报问题时无法定位到具体代码版本";

        return null;
    }

    /// <summary>会话只给元信息：文件名、大小、记录数，不给路径以外的东西。</summary>
    private static async Task<string?> SessionMetadataAsync()
    {
        var dir = Path.Combine(Path.GetTempPath(), "dg-rep2-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);

        try
        {
            await File.WriteAllTextAsync(
                Path.Combine(dir, "20261026-111111.jsonl"), "a\nb\nc\n");

            var result = await RunReportAsync(new Dictionary<string, string?>
            {
                ["DOUGONG_SESSION_DIR"] = dir,
            });

            // 记录数必须真的数出来：0 条会让"有记录但解析失败"与"空文件"无法区分
            if (!result.Stdout.Contains("3 条", StringComparison.Ordinal))
                return $"没有正确统计记录数。stdout={Trim(result.Stdout)}";

            return null;
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }

    // ── 进程驱动 ─────────────────────────────────────────────────

    private sealed record ReportResult(int ExitCode, string Stdout, string Stderr);

    private static async Task<ReportResult> RunReportAsync(
        IReadOnlyDictionary<string, string?> environment)
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
            // 明确用 UTF-8 读：控制台默认代码页会把中文读坏，
            // 那会让"输出里有没有某个词"这类断言得出错误结论。
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        startInfo.ArgumentList.Add(cliDll);
        startInfo.ArgumentList.Add("--report");
        startInfo.Environment["DOTNET_CLI_UI_LANGUAGE"] = "en";

        foreach (var name in new[]
                 {
                     "DOUGONG_PROVIDER_BASE", "DOUGONG_PROVIDER_MODEL",
                     "DOUGONG_PROVIDER_KEY", "DOUGONG_PROVIDER_PROTOCOL",
                     "DOUGONG_SESSION_DIR", "DOUGONG_MEMORY", "DOUGONG_LOG",
                 })
        {
            startInfo.Environment.Remove(name);
        }

        foreach (var (k, v) in environment)
        {
            if (v is null) startInfo.Environment.Remove(k);
            else startInfo.Environment[k] = v;
        }

        using var process = new Process { StartInfo = startInfo };
        process.Start();

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            return new ReportResult(-1, await stdoutTask, "超时");
        }

        return new ReportResult(process.ExitCode, await stdoutTask, await stderrTask);
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
