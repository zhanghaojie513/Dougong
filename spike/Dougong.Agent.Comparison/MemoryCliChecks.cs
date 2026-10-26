using System.Diagnostics;
using System.Text;
using Dougong.Agent;

namespace Dougong.Agent.Comparison;

/// <summary>
/// L1 记忆在真实 CLI 进程里的行为（验收 6 的"可观察、可禁用"）。
/// </summary>
/// <remarks>
/// 为什么必须起真实进程：`/memory` 与 <c>DOUGONG_MEMORY</c> 都是宿主装配层面的东西，
/// 进程内的假 harness 测不到。本项目已多次证明"进程内全绿、真实进程崩"是真实存在的失败模式
/// （<c>LeasedDispatcher.Tools</c> 抛 NotSupportedException 就是这样被抓到的）。
/// <para>
/// 本套检查只驱动本地命令：不调用模型，因此不需要真实 provider，也不会外发任何数据。
/// </para>
/// </remarks>
public static class MemoryCliChecks
{
    public static async Task<int> RunAsync()
    {
        var failures = 0;
        failures += await CheckAsync("memory-off-by-default", OffByDefaultAsync);
        failures += await CheckAsync("memory-lists-disabled-state-and-reason", ReasonVisibleAsync);
        failures += await CheckAsync("memory-lists-source-version-scope", ObservabilityAsync);
        failures += await CheckAsync("memory-cannot-be-switched-on-accidentally", SwitchValuesAsync);

        Console.WriteLine(failures == 0
            ? "MEMORY-CLI: ALL PASS"
            : $"MEMORY-CLI: {failures} FAILURE(S)");
        return failures;
    }

    /// <summary>默认必须关闭：不设 DOUGONG_MEMORY 时，/memory 报未启用。</summary>
    private static async Task<string?> OffByDefaultAsync()
    {
        var dir = TempDir();
        try
        {
            var result = await RunCliAsync(["/memory", "/exit"], dir, memory: null);
            if (result.ExitCode != 0)
                return $"CLI 退出码 {result.ExitCode}：{Trim(result.Stderr)}";

            if (!result.Stdout.Contains("记忆未启用", StringComparison.Ordinal))
                return $"未设 DOUGONG_MEMORY 时应报未启用，实际输出：{Trim(result.Stdout)}";

            // 反向对照：设了就该启用。没有它，这条断言在
            // "/memory 永远报未启用"的实现下也通过。
            var enabled = await RunCliAsync(["/memory", "/exit"], dir, memory: "1");
            if (enabled.Stdout.Contains("记忆未启用", StringComparison.Ordinal))
                return "设了 DOUGONG_MEMORY=1 仍报未启用";

            return null;
        }
        finally { Cleanup(dir); }
    }

    /// <summary>不生效的产物必须显示原因，而不只是"未生效"。</summary>
    /// <remarks>
    /// 这是"可观察"的实际用途：用户看到候选没生效，必须能知道为什么
    /// （未审批？被篡改？过期？作用域不符？），否则只能去读源码。
    /// </remarks>
    private static async Task<string?> ReasonVisibleAsync()
    {
        var dir = TempDir();
        try
        {
            var workspace = Path.Combine(dir, "ws");
            Directory.CreateDirectory(workspace);
            var memoryDir = Path.Combine(dir, "memory");

            // 先用一次运行把记忆文件建出来，再往里写两种不生效状态。
            await RunCliAsync(["/memory", "/exit"], dir, memory: "1", workspace: workspace);

            var memoryFile = Directory
                .EnumerateFiles(memoryDir, "*.jsonl", SearchOption.AllDirectories)
                .FirstOrDefault();
            if (memoryFile is null) return "未创建记忆文件";

            await using (var store = await MemoryStore.OpenAsync(memoryFile))
            {
                await store.PutAsync(new MemoryArtifact(
                    "cand", 1, MemoryArtifact.ComputeHash("language", "中文"),
                    "language", "中文", MemoryScope.Workspace, workspace,
                    "task-1", DateTimeOffset.UtcNow,
                    MemoryArtifactState.Candidate));

                var expiredAt = DateTimeOffset.UtcNow.AddMinutes(-5);
                await store.PutAsync(new MemoryArtifact(
                    "exp", 1, MemoryArtifact.ComputeHash("output.format", "concise"),
                    "output.format", "concise", MemoryScope.Workspace, workspace,
                    "task-2", DateTimeOffset.UtcNow,
                    MemoryArtifactState.Enabled, expiredAt));

                await store.PutAsync(new MemoryArtifact(
                    "good", 1, MemoryArtifact.ComputeHash("naming.convention", "camelCase"),
                    "naming.convention", "camelCase", MemoryScope.Workspace, workspace,
                    "task-3", DateTimeOffset.UtcNow,
                    MemoryArtifactState.Enabled));
            }

            var result = await RunCliAsync(["/memory", "/exit"], dir, memory: "1", workspace: workspace);
            if (result.ExitCode != 0) return $"CLI 退出码 {result.ExitCode}：{Trim(result.Stderr)}";

            var output = result.Stdout;

            // 三种状态各自要有可区分的原因文本
            if (!output.Contains("尚为候选，未审批启用", StringComparison.Ordinal))
                return $"候选未显示原因。输出：{Trim(output)}";
            if (!output.Contains("已过期", StringComparison.Ordinal))
                return $"过期项未显示原因。输出：{Trim(output)}";
            // 唯一的正常项必须标为生效——否则"全部未生效"也能通过上面两条。
            if (!output.Contains("[生效]", StringComparison.Ordinal))
                return $"没有出现任何生效项。输出：{Trim(output)}";

            return null;
        }
        finally { Cleanup(dir); }
    }

    /// <summary>每条都要能看到来源任务、版本、作用域。</summary>
    private static async Task<string?> ObservabilityAsync()
    {
        var dir = TempDir();
        try
        {
            var workspace = Path.Combine(dir, "ws");
            Directory.CreateDirectory(workspace);

            await RunCliAsync(["/memory", "/exit"], dir, memory: "1", workspace: workspace);

            var memoryFile = Directory
                .EnumerateFiles(Path.Combine(dir, "memory"), "*.jsonl", SearchOption.AllDirectories)
                .FirstOrDefault();
            if (memoryFile is null) return "未创建记忆文件";

            await using (var store = await MemoryStore.OpenAsync(memoryFile))
            {
                await store.PutAsync(new MemoryArtifact(
                    "obs", 7, MemoryArtifact.ComputeHash("language", "中文"),
                    "language", "中文", MemoryScope.Workspace, workspace,
                    "task-observable", DateTimeOffset.UtcNow,
                    MemoryArtifactState.Enabled));
            }

            var result = await RunCliAsync(["/memory", "/exit"], dir, memory: "1", workspace: workspace);
            var output = result.Stdout;

            if (!output.Contains("task-observable", StringComparison.Ordinal))
                return $"未显示来源任务。输出：{Trim(output)}";
            if (!output.Contains("版本 7", StringComparison.Ordinal))
                return $"未显示版本号。输出：{Trim(output)}";
            if (!output.Contains("Workspace", StringComparison.Ordinal))
                return $"未显示作用域。输出：{Trim(output)}";

            return null;
        }
        finally { Cleanup(dir); }
    }

    /// <summary>
    /// 只有明确的开启值才启用；其它值不能"看起来像"就开。
    /// </summary>
    /// <remarks>
    /// 这是数据流的默认值问题：任何模糊值都不能被当作开启。
    /// 空串、<c>0</c>、<c>false</c> 都必须是关闭。
    /// </remarks>
    private static async Task<string?> SwitchValuesAsync()
    {
        var dir = TempDir();
        try
        {
            foreach (var off in new[] { "", "0", "false" })
            {
                var result = await RunCliAsync(["/memory", "/exit"], dir, memory: off);
                if (!result.Stdout.Contains("记忆未启用", StringComparison.Ordinal))
                    return $"DOUGONG_MEMORY='{off}' 应关闭，实际：{Trim(result.Stdout)}";
            }

            return null;
        }
        finally { Cleanup(dir); }
    }

    // ── 进程驱动 ─────────────────────────────────────────────────

    private sealed record CliResult(int ExitCode, string Stdout, string Stderr);

    /// <summary>
    /// 起真实 CLI 进程，喂入若干命令。
    /// </summary>
    /// <remarks>
    /// provider 环境变量用假值：本套检查只驱动本地命令，不调用模型。
    /// 但 CLI 要求它们存在（否则直接退出码 2），所以必须给。
    /// </remarks>
    private static async Task<CliResult> RunCliAsync(
        string[] commands, string workDir, string? memory, string? workspace = null)
    {
        var cliProject = Path.Combine(FindRepoRoot(), "src", "Dougong.Cli");

        var startInfo = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = workDir,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        startInfo.ArgumentList.Add("run");
        startInfo.ArgumentList.Add("--project");
        startInfo.ArgumentList.Add(cliProject);
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add("Release");
        startInfo.ArgumentList.Add("--no-build");

        startInfo.Environment["DOUGONG_PROVIDER_BASE"] = "http://127.0.0.1:1";
        startInfo.Environment["DOUGONG_PROVIDER_KEY"] = "test-key-not-real";
        startInfo.Environment["DOUGONG_PROVIDER_MODEL"] = "test-model";
        startInfo.Environment["DOUGONG_SESSION_DIR"] = Path.Combine(workDir, "sessions");
        startInfo.Environment["DOUGONG_WORKSPACE"] = workspace ?? workDir;
        startInfo.Environment["DOUGONG_KERNEL"] = "0";   // 本套检查只测命令层，不测内核
        startInfo.Environment.Remove("DOUGONG_AUTO_APPROVE");

        if (memory is null) startInfo.Environment.Remove("DOUGONG_MEMORY");
        else startInfo.Environment["DOUGONG_MEMORY"] = memory;

        using var process = new Process { StartInfo = startInfo };
        process.Start();

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();

        foreach (var command in commands)
        {
            await process.StandardInput.WriteLineAsync(command);
        }
        process.StandardInput.Close();

        // 超时是检查项的一部分：没有它，卡住会被当成"通过"。
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            return new CliResult(-1, await stdoutTask, "超时：CLI 未在 90 秒内退出");
        }

        return new CliResult(
            process.ExitCode, await stdoutTask, await stderrTask);
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

    private static string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "dougong-memcli-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void Cleanup(string dir)
    {
        try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
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
