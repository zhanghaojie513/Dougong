using Dougong.Agent;

namespace Dougong.Agent.Comparison;

/// <summary>
/// 持久 shell 的验收。
/// </summary>
/// <remarks>
/// <b>可观察面是"模型拿到什么"</b>，核心是三条：
/// <list type="number">
/// <item><b>状态跨命令保留</b>——这是它相对 <c>shell</c> 工具的唯一增量</item>
/// <item><b>退出码是这条命令的</b>，不是上一条的（cmd 的
///   <c>%errorlevel%</c> 解析期展开会让它取到旧值）</item>
/// <item><b>输出里没有 shell 的噪音</b>——提示符与回显会让模型把
///   <c>C:\path&gt;</c> 当成命令输出</item>
/// </list>
/// </remarks>
public static class PersistentShellChecks
{
    public static async Task<int> RunAsync()
    {
        var failures = 0;
        failures += await CheckAsync("shell-keeps-cwd-across-commands", KeepsCwdAsync);
        failures += await CheckAsync("shell-exit-code-is-this-command", ExitCodeAsync);
        failures += await CheckAsync("shell-failure-does-not-break-session", SurvivesFailureAsync);
        failures += await CheckAsync("shell-output-has-no-prompt-noise", NoPromptAsync);
        failures += await CheckAsync("shell-reports-current-directory", ReportsCwdAsync);
        failures += await CheckAsync("shell-times-out-instead-of-hanging", TimeoutAsync);
        failures += await CheckAsync("shell-dispose-terminates-process", DisposeAsync);
        failures += await CheckAsync("shell-reuses-one-process", OneProcessAsync);

        Console.WriteLine(failures == 0
            ? "PERSISTENT-SHELL: ALL PASS"
            : $"PERSISTENT-SHELL: {failures} FAILURE(S)");
        return failures;
    }

    /// <summary>工作目录跨命令保留。</summary>
    /// <remarks>
    /// <b>这是它与 <c>shell</c> 工具的唯一区别</b>：那个每次起新进程，
    /// <c>cd</c> 的下一条命令就失效。验证方式是"cd 之后用相对路径读文件"——
    /// 能读到就证明目录真的没变。
    /// </remarks>
    private static async Task<string?> KeepsCwdAsync()
    {
        using var env = new Env();
        Directory.CreateDirectory(Path.Combine(env.Root, "sub"));
        File.WriteAllText(Path.Combine(env.Root, "sub", "marker.txt"), "in-sub");

        using var shell = new PersistentShell(env.Root);

        var cd = await shell.RunAsync("cd sub");
        if (cd.ExitCode != 0) return $"cd 失败：exit={cd.ExitCode} {cd.Stdout}";

        var read = await shell.RunAsync(Os("type marker.txt", "cat marker.txt"));
        if (read.ExitCode != 0) return $"相对路径读文件失败：{read.Stdout}";

        return read.Stdout.Contains("in-sub", StringComparison.Ordinal)
            ? null
            : $"读到的内容不对：{read.Stdout}";
    }

    /// <summary>退出码必须是这条命令的，不是上一条的。</summary>
    /// <remarks>
    /// <b>判据是"成功 → 失败 → 成功 → 失败 → 成功"</b>，五个码必须各自对。
    /// <para>
    /// <b>这条抓不到 <c>%errorlevel%</c> 与 <c>!errorlevel!</c> 的差别</b>——
    /// 实测确认：两者**分两行发**（命令一行、探针一行）时行为相同，
    /// 因为 <c>%errorlevel%</c> 在每一行新输入时才解析，正好取到上一行。
    /// 差别只在**同一行**用 <c>&amp;</c> 串联时才出现（那时是整行解析期展开）。
    /// </para>
    /// <para>
    /// 实现里用 <c>!errorlevel!</c> 并开 <c>/V:ON</c> 仍是对的：它让"同一行"的
    /// 写法也安全，而将来若有人改回单行串联，不会静默引入旧值。但**这条检查
    /// 不证明那个决定必要**——如实说明，不把它算作已验证的部分。
    /// </para>
    /// </remarks>
    private static async Task<string?> ExitCodeAsync()
    {
        using var env = new Env();
        using var shell = new PersistentShell(env.Root);

        var ok1 = await shell.RunAsync(Os("ver >nul", "true"));
        if (ok1.ExitCode != 0) return $"第一条成功命令报 {ok1.ExitCode}";

        var bad = await shell.RunAsync(Os("dir nonexistent-xyz >nul 2>&1", "ls nonexistent-xyz 2>/dev/null"));
        if (bad.ExitCode == 0) return "失败命令报 0——退出码取到了旧值";

        var ok2 = await shell.RunAsync(Os("ver >nul", "true"));
        if (ok2.ExitCode != 0)
            return $"失败后的成功命令报 {ok2.ExitCode}，说明退出码错位";

        // **再连续两次失败**：如果退出码是"上一条"的值，第二次会读到第一次的，
        // 而"成功→失败→成功"这个序列恰好让两种实现都能通过。
        // 这段是补上的——负控发现原序列抓不到解析期展开的错误。
        var bad2 = await shell.RunAsync(Os("dir nonexistent-xyz >nul 2>&1", "ls nonexistent-xyz 2>/dev/null"));
        if (bad2.ExitCode == 0) return "第二次失败命令报 0";

        var ok3 = await shell.RunAsync(Os("ver >nul", "true"));
        return ok3.ExitCode == 0
            ? null
            : $"连续失败后的成功命令报 {ok3.ExitCode}";
    }

    private static async Task<string?> SurvivesFailureAsync()
    {
        using var env = new Env();
        using var shell = new PersistentShell(env.Root);

        await shell.RunAsync(Os("dir nonexistent-xyz >nul 2>&1", "ls nonexistent-xyz 2>/dev/null"));
        var after = await shell.RunAsync("echo still-alive");

        return after.Stdout.Contains("still-alive", StringComparison.Ordinal)
            ? null
            : $"失败命令后 shell 不可用：{after.Stdout}";
    }

    /// <summary>输出里不能有提示符或命令回显。</summary>
    /// <remarks>
    /// <b>噪音会让模型误读</b>：<c>C:\project&gt;</c> 混在输出里，
    /// 模型可能把它当成命令结果的一部分，甚至当成路径去用。
    /// </remarks>
    private static async Task<string?> NoPromptAsync()
    {
        using var env = new Env();
        using var shell = new PersistentShell(env.Root);

        var result = await shell.RunAsync("echo clean-output");

        if (!result.Stdout.Contains("clean-output", StringComparison.Ordinal))
            return $"输出里没有结果本身：{result.Stdout}";

        // 提示符形态：Windows 是 `C:\...>`，POSIX 是 `$ ` 或 `# ` 结尾。
        if (OperatingSystem.IsWindows())
        {
            if (result.Stdout.Contains(":\\", StringComparison.Ordinal))
                return $"输出里混进了路径提示符：{result.Stdout}";
        }
        else if (result.Stdout.Contains("$ ", StringComparison.Ordinal))
        {
            return $"输出里混进了提示符：{result.Stdout}";
        }

        // 回显：命令原文不该出现两次。
        var echoCount = CountOccurrences(result.Stdout, "echo clean-output");
        return echoCount == 0
            ? null
            : $"命令被回显了（可能出现 {echoCount} 次）：{result.Stdout}";
    }

    /// <summary>当前目录要反映 cd 后的真实位置。</summary>
    /// <remarks>
    /// <b>只报初始目录会让模型以为 <c>cd</c> 没生效</b>，
    /// 于是它可能重复 cd，或改用绝对路径绕开。
    /// </remarks>
    private static async Task<string?> ReportsCwdAsync()
    {
        using var env = new Env();
        var sub = Path.Combine(env.Root, "sub");
        Directory.CreateDirectory(sub);

        using var shell = new PersistentShell(env.Root);

        var before = await shell.GetCurrentDirectoryAsync();
        if (!before.Contains("sub", StringComparison.Ordinal)
            && !SamePath(before, env.Root))
            return $"初始目录不对：{before}";

        await shell.RunAsync("cd sub");
        var after = await shell.GetCurrentDirectoryAsync();

        return SamePath(after, sub)
            ? null
            : $"cd 之后报的目录不对：{after}（应为 {sub}）";
    }

    /// <summary>超时后返回错误而不是一直挂着。</summary>
    private static async Task<string?> TimeoutAsync()
    {
        using var env = new Env();
        using var shell = new PersistentShell(env.Root, commandTimeout: TimeSpan.FromSeconds(2));

        var started = System.Diagnostics.Stopwatch.StartNew();
        var result = await shell.RunAsync(Os("ping -n 30 127.0.0.1 >nul", "sleep 30"));
        started.Stop();

        if (started.Elapsed > TimeSpan.FromSeconds(15))
            return $"没有在超时时返回（用了 {started.Elapsed.TotalSeconds:0} 秒）";

        return result.ExitCode == -1 && result.Stdout.Contains("超时", StringComparison.Ordinal)
            ? null
            : $"超时没有如实报告：exit={result.ExitCode} out={result.Stdout}";
    }

    /// <summary>释放要结束进程。</summary>
    private static async Task<string?> DisposeAsync()
    {
        var env = new Env();
        var shell = new PersistentShell(env.Root);

        try
        {
            await shell.RunAsync("echo started");
            shell.Dispose();

            // 释放后再跑应失败（对象已释放），而不是静默成功。
            try
            {
                await shell.RunAsync("echo after-dispose");
                return "释放后仍能执行命令";
            }
            catch (ObjectDisposedException)
            {
                return null;
            }
        }
        finally
        {
            env.Dispose();
        }
    }

    /// <summary>整个会话只用一个进程。</summary>
    /// <remarks>
    /// <b>这是"持久"的定义</b>：如果每次都起新进程，状态保留就无从谈起。
    /// 判据是"命令里能看到上一个进程留下的变量"。
    /// </remarks>
    private static async Task<string?> OneProcessAsync()
    {
        using var env = new Env();
        using var shell = new PersistentShell(env.Root);

        // 设一个环境变量，下一条命令应能读到——新进程不会有它。
        var set = await shell.RunAsync(Os("set DOUGONG_PROBE=persisted", "export DOUGONG_PROBE=persisted"));
        if (set.ExitCode != 0) return $"设置变量失败：{set.Stdout}";

        var read = await shell.RunAsync(Os("echo %DOUGONG_PROBE%", "echo $DOUGONG_PROBE"));

        return read.Stdout.Contains("persisted", StringComparison.Ordinal)
            ? null
            : $"变量没跨命令保留（说明每条命令起了新进程）：{read.Stdout}";
    }

    // ── 支撑 ─────────────────────────────────────────────────────

    private static string Os(string windows, string posix) =>
        OperatingSystem.IsWindows() ? windows : posix;

    private static bool SamePath(string a, string b)
    {
        try
        {
            var na = Path.GetFullPath(a).TrimEnd(Path.DirectorySeparatorChar);
            var nb = Path.GetFullPath(b).TrimEnd(Path.DirectorySeparatorChar);
            return string.Equals(na, nb,
                OperatingSystem.IsWindows()
                    ? StringComparison.OrdinalIgnoreCase
                    : StringComparison.Ordinal);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static int CountOccurrences(string text, string needle)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }

        return count;
    }

    private sealed class Env : IDisposable
    {
        public Env()
        {
            Root = Path.Combine(Path.GetTempPath(), $"dougong-pshell-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Root);
        }

        public string Root { get; }

        public void Dispose()
        {
            try { Directory.Delete(Root, recursive: true); }
            catch { /* 清理失败不影响结论 */ }
        }
    }

    private static async Task<int> CheckAsync(string name, Func<Task<string?>> action)
    {
        try
        {
            var failure = await action().ConfigureAwait(false);
            if (failure is null)
            {
                Console.WriteLine($"PASS {name}");
                return 0;
            }

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
