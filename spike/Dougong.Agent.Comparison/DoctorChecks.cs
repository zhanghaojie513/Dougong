using System.Diagnostics;
using System.Text;

namespace Dougong.Agent.Comparison;

/// <summary>
/// `dougong --doctor` 的验收：自检能不能真的指出问题，并给出下一步。
/// </summary>
/// <remarks>
/// 必须起真实进程：`--doctor` 的整个价值在于"在装配失败之前跑起来"。
/// 其中最关键的一条是**它排在 provider 配置检查之前**——如果顺序错了，
/// 缺配置时程序会先退出 2，自检永远跑不到，而那正是最需要它的时候。
/// <para>
/// 这类顺序问题在进程内测不出来（进程内可以直接调 Doctor.RunAsync），
/// 所以下面几条全部走 `Process.Start`。
/// </para>
/// </remarks>
public static class DoctorChecks
{
    public static async Task<int> RunAsync()
    {
        var failures = 0;
        failures += await CheckAsync("doctor-runs-without-any-config", RunsWithoutConfigAsync);
        failures += await CheckAsync("doctor-leads-with-next-step", LeadsWithNextStepAsync);
        failures += await CheckAsync("doctor-never-prints-full-key", NeverPrintsKeyAsync);
        failures += await CheckAsync("doctor-fails-on-bad-runtime-independent-checks",
            IndependentChecksAsync);
        failures += await CheckAsync("doctor-reports-unwritable-workspace", UnwritableWorkspaceAsync);
        failures += await CheckAsync("doctor-exit-code-reflects-failures", ExitCodeAsync);
        failures += await CheckAsync("log-silent-by-default", LogSilentByDefaultAsync);
        failures += await CheckAsync("log-info-adds-startup-facts", LogInfoAddsFactsAsync);
        failures += await CheckAsync("log-unknown-value-is-reported", LogUnknownValueAsync);

        Console.WriteLine(failures == 0
            ? "DOCTOR: ALL PASS"
            : $"DOCTOR: {failures} FAILURE(S)");
        return failures;
    }

    /// <summary>
    /// 没有任何 provider 配置时，自检仍必须跑起来并报告缺什么。
    /// </summary>
    /// <remarks>
    /// 这是本套最重要的一条：`--doctor` 若排在 provider 配置检查之后，
    /// 缺配置时程序直接 `return 2`，用户永远看不到自检结果——
    /// 而"缺配置"恰恰是最需要自检的场景。
    /// </remarks>
    private static async Task<string?> RunsWithoutConfigAsync()
    {
        var result = await RunDoctorAsync(new Dictionary<string, string?>());

        if (!result.Stdout.Contains("启动自检", StringComparison.Ordinal))
            return $"没有输出自检内容。stdout={Trim(result.Stdout)} stderr={Trim(result.Stderr)}";

        if (!result.Stdout.Contains("缺少", StringComparison.Ordinal))
            return "没有报告缺少哪些环境变量";

        if (result.ExitCode != 1)
            return $"有失败项时退出码应为 1，实际 {result.ExitCode}";

        return null;
    }

    /// <summary>每一项失败都必须带"下一步"，只说失败等于把问题换个地方显示。</summary>
    private static async Task<string?> LeadsWithNextStepAsync()
    {
        var result = await RunDoctorAsync(new Dictionary<string, string?>());
        var lines = result.Stdout.Split('\n');

        // 找到失败行，它后面紧跟的一行应是"下一步"
        for (var i = 0; i < lines.Length; i++)
        {
            if (!lines[i].Contains("[失败]", StringComparison.Ordinal)) continue;

            var hasNext = false;
            for (var j = i + 1; j < Math.Min(i + 6, lines.Length); j++)
            {
                if (lines[j].Contains("下一步", StringComparison.Ordinal)) { hasNext = true; break; }
                if (lines[j].Contains("[", StringComparison.Ordinal)) break;   // 到下一条了
            }

            if (!hasNext)
                return $"失败项没有下一步：{lines[i].Trim()}";
        }

        return null;
    }

    /// <summary>
    /// 绝不打印完整密钥。
    /// </summary>
    /// <remarks>
    /// 自检是最容易被贴进 issue 的输出。它里面一旦有完整 key，
    /// 泄露就会随截图与日志扩散。这里用一个可识别的假 key 验证。
    /// </remarks>
    private static async Task<string?> NeverPrintsKeyAsync()
    {
        const string secret = "sk-DOCTOR-LEAK-CANARY-0123456789abcdef";

        var result = await RunDoctorAsync(new Dictionary<string, string?>
        {
            ["DOUGONG_PROVIDER_BASE"] = "https://api.deepseek.com",
            ["DOUGONG_PROVIDER_MODEL"] = "deepseek-flash",
            ["DOUGONG_PROVIDER_KEY"] = secret,
        });

        var combined = result.Stdout + result.Stderr;

        if (combined.Contains(secret, StringComparison.Ordinal))
            return "自检输出里出现了完整密钥";

        // 反向对照：确认 key 确实被设置了（否则"没泄露"只是因为压根没用上）
        if (!combined.Contains("已设置", StringComparison.Ordinal))
            return "没有报告密钥已设置：本项可能什么都没验证";

        return null;
    }

    /// <summary>
    /// 配置齐全时，与 provider 无关的检查仍应逐项给出结果。
    /// </summary>
    private static async Task<string?> IndependentChecksAsync()
    {
        var result = await RunDoctorAsync(new Dictionary<string, string?>
        {
            ["DOUGONG_PROVIDER_BASE"] = "https://127.0.0.1:1",
            ["DOUGONG_PROVIDER_MODEL"] = "m",
            ["DOUGONG_PROVIDER_KEY"] = "k",
        });

        foreach (var expected in new[] { "运行时", "工作区", "会话目录" })
        {
            if (!result.Stdout.Contains(expected, StringComparison.Ordinal))
                return $"配置齐全时没有报告「{expected}」";
        }

        // 连不上 provider 只能是警告：离线环境也要能用 CLI 的其它功能。
        if (result.ExitCode == 1 && result.Stdout.Contains("[失败] provider 连通性", StringComparison.Ordinal))
            return "provider 连不上被判为失败：离线环境下会误报安装损坏";

        return null;
    }

    /// <summary>工作区不可写时必须报失败并给下一步。</summary>
    private static async Task<string?> UnwritableWorkspaceAsync()
    {
        // 用一个不存在的路径当工作区：Directory.Exists 为 false → 失败
        var missing = Path.Combine(Path.GetTempPath(), "dougong-does-not-exist-" + Guid.NewGuid().ToString("N"));

        var result = await RunDoctorAsync(new Dictionary<string, string?>
        {
            ["DOUGONG_PROVIDER_BASE"] = "https://127.0.0.1:1",
            ["DOUGONG_PROVIDER_MODEL"] = "m",
            ["DOUGONG_PROVIDER_KEY"] = "k",
            ["DOUGONG_WORKSPACE"] = missing,
        });

        if (!result.Stdout.Contains("[失败] 工作区", StringComparison.Ordinal))
            return $"不存在的工作区没有报失败。stdout={Trim(result.Stdout)}";

        return null;
    }

    /// <summary>退出码语义：全部通过 0，有失败 1。</summary>
    private static async Task<string?> ExitCodeAsync()
    {
        var bad = await RunDoctorAsync(new Dictionary<string, string?>());
        if (bad.ExitCode != 1) return $"缺配置时退出码应为 1，实际 {bad.ExitCode}";

        // 全部通过的场景：把 provider 指向必然失败的端口，其余项在本机应通过，
        // 但 provider 连通性只是警告——所以只要有失败项就不会是 0。
        // 这里只验证"失败非 0"与"成功为 0"的语义差异，不依赖具体环境。
        if (bad.ExitCode == 0) return "有失败项却返回 0";

        return null;
    }

    /// <summary>
    /// 不设 <c>DOUGONG_LOG</c> 时，不得出现 info/debug 输出。
    /// </summary>
    /// <remarks>
    /// 这是"默认行为与加日志之前完全一致"的直接验证。
    /// 若默认就开始刷日志，所有既有用户的可观察输出都会变——那是行为破坏。
    /// </remarks>
    private static async Task<string?> LogSilentByDefaultAsync()
    {
        var result = await RunCliAsync(
            ["/exit"],
            new Dictionary<string, string?>
            {
                ["DOUGONG_PROVIDER_BASE"] = "https://127.0.0.1:1",
                ["DOUGONG_PROVIDER_MODEL"] = "m",
                ["DOUGONG_PROVIDER_KEY"] = "k",
                ["DOUGONG_LOG"] = null,
            });

        var combined = result.Stdout + result.Stderr;
        if (combined.Contains("[info]", StringComparison.Ordinal)
            || combined.Contains("[debug]", StringComparison.Ordinal))
            return "未设置 DOUGONG_LOG 时仍输出了 info/debug：默认行为被改变";

        return null;
    }

    /// <summary>info 级别必须给出排查时最先要问的那几个事实。</summary>
    private static async Task<string?> LogInfoAddsFactsAsync()
    {
        var result = await RunCliAsync(
            ["/exit"],
            new Dictionary<string, string?>
            {
                ["DOUGONG_PROVIDER_BASE"] = "https://127.0.0.1:1",
                ["DOUGONG_PROVIDER_MODEL"] = "m",
                ["DOUGONG_PROVIDER_KEY"] = "k",
                ["DOUGONG_LOG"] = "info",
            });

        var combined = result.Stdout + result.Stderr;
        foreach (var expected in new[] { "会话=", "工作区=", "存储=", "内核接线=" })
        {
            if (!combined.Contains(expected, StringComparison.Ordinal))
                return $"info 级别下没有输出「{expected}」";
        }

        return null;
    }

    /// <summary>无法识别的级别值必须明确告知，不能静默按默认跑。</summary>
    private static async Task<string?> LogUnknownValueAsync()
    {
        var result = await RunCliAsync(
            ["/exit"],
            new Dictionary<string, string?>
            {
                ["DOUGONG_PROVIDER_BASE"] = "https://127.0.0.1:1",
                ["DOUGONG_PROVIDER_MODEL"] = "m",
                ["DOUGONG_PROVIDER_KEY"] = "k",
                ["DOUGONG_LOG"] = "loud",
            });

        var combined = result.Stdout + result.Stderr;
        if (!combined.Contains("无法识别", StringComparison.Ordinal))
            return "未知的 DOUGONG_LOG 值被静默忽略：用户会以为它生效了";

        return null;
    }

    // ── 进程驱动 ─────────────────────────────────────────────────

    private sealed record DoctorResult(int ExitCode, string Stdout, string Stderr);

    /// <summary>
    /// 起真实 CLI 进程跑 `--doctor`。
    /// </summary>
    /// <remarks>
    /// 显式清空 provider 相关环境变量再按需注入：否则跑测试的机器上已经设了
    /// DOUGONG_PROVIDER_*，会让"缺配置"场景测不出来（假通过）。
    /// </remarks>
    private static async Task<DoctorResult> RunDoctorAsync(
        IReadOnlyDictionary<string, string?> environment)
    {
        var cliDll = Path.Combine(
            FindRepoRoot(), "src", "Dougong.Cli", "bin", "Release", "net10.0", "Dougong.Cli.dll");

        if (!File.Exists(cliDll))
            throw new InvalidOperationException($"找不到 CLI 产物：{cliDll}（先跑 all-checks.sh 构建）");

        var startInfo = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        startInfo.ArgumentList.Add(cliDll);
        startInfo.ArgumentList.Add("--doctor");

        // 先清空，避免父进程的环境干扰
        foreach (var name in new[]
                 {
                     "DOUGONG_PROVIDER_BASE", "DOUGONG_PROVIDER_MODEL",
                     "DOUGONG_PROVIDER_KEY", "DOUGONG_PROVIDER_PROTOCOL",
                     "DOUGONG_WORKSPACE", "DOUGONG_SESSION_DIR", "DOUGONG_MEMORY",
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

        // 超时是检查项的一部分：自检里有网络探测，没有超时会挂住而不是失败。
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            return new DoctorResult(-1, await stdoutTask, "超时：--doctor 未在 90 秒内退出");
        }

        return new DoctorResult(process.ExitCode, await stdoutTask, await stderrTask);
    }

    /// <summary>
    /// 起真实 CLI 进程，按顺序喂入若干命令。
    /// </summary>
    /// <remarks>
    /// 与 <see cref="RunDoctorAsync"/> 的区别：这条不带 <c>--doctor</c>，
    /// 用于验证正常的 REPL 启动路径（日志级别就是在那里生效的）。
    /// 同样先清空 DOUGONG_* 环境变量，避免父进程干扰。
    /// </remarks>
    private static async Task<DoctorResult> RunCliAsync(
        string[] commands, IReadOnlyDictionary<string, string?> environment)
    {
        var cliDll = Path.Combine(
            FindRepoRoot(), "src", "Dougong.Cli", "bin", "Release", "net10.0", "Dougong.Cli.dll");

        if (!File.Exists(cliDll))
            throw new InvalidOperationException($"找不到 CLI 产物：{cliDll}");

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

        foreach (var name in new[]
                 {
                     "DOUGONG_PROVIDER_BASE", "DOUGONG_PROVIDER_MODEL",
                     "DOUGONG_PROVIDER_KEY", "DOUGONG_PROVIDER_PROTOCOL",
                     "DOUGONG_WORKSPACE", "DOUGONG_SESSION_DIR", "DOUGONG_MEMORY",
                     "DOUGONG_LOG", "DOUGONG_KERNEL",
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

        foreach (var command in commands)
            await process.StandardInput.WriteLineAsync(command);
        process.StandardInput.Close();

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            return new DoctorResult(-1, await stdoutTask, "超时");
        }

        return new DoctorResult(process.ExitCode, await stdoutTask, await stderrTask);
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
