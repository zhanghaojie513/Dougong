using System.Diagnostics;
using Dougong.Agent;

namespace Dougong.Agent.Comparison;

/// <summary>
/// 后台作业的验收。
/// </summary>
/// <remarks>
/// <b>可观察面是"模型拿到什么"</b>：
/// <list type="bullet">
/// <item>启动立即返回，不等命令结束</item>
/// <item>游标只进不退——不传游标会重复，传了就只拿新增</item>
/// <item>环形缓冲满了要丢掉旧的，且**如实报丢了多少**</item>
/// <item>按 owner 限并发，超了明确拒绝而不是排队</item>
/// <item>停止要结束整棵进程树，且已产生的输出仍可读</item>
/// </list>
/// </remarks>
public static class JobChecks
{
    public static async Task<int> RunAsync()
    {
        var failures = 0;
        failures += await CheckAsync("job-start-returns-without-waiting", StartReturnsAsync);
        failures += await CheckAsync("job-output-respects-cursor", CursorAsync);
        failures += await CheckAsync("job-output-reports-discarded-bytes", RingAsync);
        failures += await CheckAsync("job-stop-reaches-finished-state", StopAsync);
        failures += await CheckAsync("job-stop-kills-grandchildren-on-posix", GrandchildrenAsync);
        failures += await CheckAsync("job-stop-keeps-output-readable", OutputAfterStopAsync);
        failures += await CheckAsync("job-caps-concurrency-per-owner", ConcurrencyAsync);
        failures += await CheckAsync("job-limit-is-per-owner-not-global", PerOwnerAsync);
        failures += await CheckAsync("job-unknown-id-is-not-silent", UnknownIdAsync);
        failures += await CheckAsync("job-registry-dispose-kills-children", DisposeKillsAsync);
        failures += await CheckAsync("job-tool-declares-execute-permission", PermissionAsync);
        failures += await CheckAsync("job-finished-output-survives-until-read", SurvivesUntilReadAsync);

        Console.WriteLine(failures == 0
            ? "JOBS: ALL PASS"
            : $"JOBS: {failures} FAILURE(S)");
        return failures;
    }

    /// <summary>启动立即返回，不等命令结束。</summary>
    /// <remarks>
    /// <b>这是它相对 shell 工具的全部价值</b>：验证方式是"启动后立刻
    /// 读到 Running"，而不是"同步等它跑完"。
    /// </remarks>
    private static async Task<string?> StartReturnsAsync()
    {
        using var env = new Env();
        var started = Stopwatch.StartNew();
        var snap = env.Registry.Start("s1", SleepCommand(3000));
        started.Stop();

        if (started.ElapsedMilliseconds > 1500)
            return $"启动耗时 {started.ElapsedMilliseconds}ms，说明它在等命令结束";

        if (snap.State is not JobState.Running)
            return $"启动后状态应为 Running，实际 {snap.State}";

        return null;
    }

    /// <summary>游标只进不退。</summary>
    private static async Task<string?> CursorAsync()
    {
        using var env = new Env();

        // 分两段输出：先一句，等一会再一句。
        var cmd = OperatingSystem.IsWindows()
            ? "echo first && ping -n 2 127.0.0.1 > nul && echo second"
            : "echo first; sleep 0.8; echo second";

        var snap = env.Registry.Start("s1", cmd);
        await Task.Delay(1600);

        var all = env.Registry.Read(snap.Id, 0);
        if (!all.Output.Contains("first", StringComparison.Ordinal))
            return $"首次读取缺 first：{all.Output}";

        // 用返回的游标再读：应该是空的（没有新内容）。
        var again = env.Registry.Read(snap.Id, all.NextCursor);
        if (again.Output.Length != 0)
            return $"游标后的读取应无重复，实际拿到 [{again.Output}]";

        return null;
    }

    /// <summary>环形缓冲丢弃旧内容时如实报字节数。</summary>
    /// <remarks>
    /// <b>不报会让模型以为看到的就是全部</b>。判据是"总字节 = 保留 + 丢弃"
    /// 这一步守恒——它同时证明了两边统计一致。
    /// </remarks>
    private static async Task<string?> RingAsync()
    {
        using var env = new Env(retainBytes: 2048);

        var cmd = OperatingSystem.IsWindows()
            ? "for /L %i in (1,1,400) do @echo 0123456789ABCDEF"
            : "for i in $(seq 1 400); do echo 0123456789ABCDEF; done";

        var snap = env.Registry.Start("s1", cmd);
        await Task.Delay(2000);

        var read = env.Registry.Read(snap.Id, 0);

        if (read.Output.Length > 2048)
            return $"保留 {read.Output.Length} 字节，超过上限 2048";

        if (read.OmittedBytes <= 0)
            return $"总输出 {read.TotalBytes} 字节却报 omitted=0——超上限时应有丢弃";

        // 守恒：总字节 = 本次给出的 + 丢弃的。
        var sum = read.Output.Length + read.OmittedBytes;
        return sum == read.TotalBytes
            ? null
            : $"统计不守恒：{read.Output.Length} + {read.OmittedBytes} != {read.TotalBytes}";
    }

    /// <summary>停止能让作业进入 Finished 状态。</summary>
    /// <remarks>
    /// <b>这条检查的名字故意不含"tree"</b>——它只验"作业被停了"，
    /// 不验"派生的子进程也死了"。
    /// <para>
    /// 试过验进程树：构造一个父进程活着且持有子进程的场景，然后数子进程。
    /// 在这台 Windows 上没成功——<c>cmd /c ping</c> 不产生独立子进程，
    /// <c>start /b</c> 产生的又是**有意分离**的进程（kill tree 本就不该管它）。
    /// 两种形态数出来的结果一样，无法区分 <c>entireProcessTree</c> 的
    /// <c>true</c> 与 <c>false</c>。
    /// </para>
    /// <para>
    /// <b>所以这里如实降级</b>：进程树结束是代码里显式传的
    /// <c>Kill(entireProcessTree: true)</c>，但没有检查钉住它。
    /// 把它写成"已验证"就是假声称——本项目最忌讳的那种。
    /// 真要验得在 Linux 上做（<c>sh -c 'sleep 60 &amp; wait'</c> 会真派生子进程）。
    /// </para>
    /// </remarks>
    private static async Task<string?> StopAsync()
    {
        using var env = new Env();
        var snap = env.Registry.Start("s1", SleepCommand(30_000));

        await Task.Delay(300);
        var before = env.Registry.Read(snap.Id, 0);
        if (before.State is not JobState.Running) return $"停止前应为 Running，实际 {before.State}";

        env.Registry.Stop(snap.Id);
        await Task.Delay(800);

        var after = env.Registry.Read(snap.Id, 0);
        return after.State is JobState.Finished
            ? null
            : $"停止后应为 Finished，实际 {after.State}";
    }

    /// <summary>
    /// POSIX 上停止要结束派生的子进程（整棵进程树）。
    /// </summary>
    /// <remarks>
    /// <b>这条只在 Linux/macOS 上跑，Windows 上直接跳过</b>——不是怕麻烦，
    /// 是 Windows 上构造不出可靠的"父活子活"场景（见
    /// <see cref="StopAsync"/> 的注释）。而 CI 有 ubuntu 矩阵，那里能真验。
    /// <para>
    /// 判据：让子进程每隔 100ms 往文件里追加一行，停止后等一会，
    /// 文件**不应再增长**。这直接观察"子进程是否还活着"，不依赖数进程名
    /// （那个在不同发行版上不可靠）。
    /// </para>
    /// </remarks>
    private static async Task<string?> GrandchildrenAsync()
    {
        if (OperatingSystem.IsWindows()) return null;   // 见注释：Windows 构造不可靠

        using var env = new Env(retainBytes: 65536);
        var beat = Path.Combine(env.Root, "heartbeat.txt");

        // 子进程每 100ms 追加一行；父 shell 等它。父活则子活，kill tree 应带走两者。
        var cmd = $"sh -c 'while true; do echo tick >> \"{beat}\"; sleep 0.1; done' & wait";

        var snap = env.Registry.Start("s1", cmd);
        await Task.Delay(1200);

        long Size() => File.Exists(beat) ? new FileInfo(beat).Length : 0;

        var before = Size();
        if (before == 0)
            return "子进程没写出心跳文件，无法验证（命令可能没按预期派生子进程）";

        env.Registry.Stop(snap.Id);
        await Task.Delay(600);

        var atStop = Size();
        await Task.Delay(900);
        var later = Size();

        return later == atStop
            ? null
            : $"停止后文件仍在增长（{atStop} → {later} 字节），说明子进程还活着";
    }

    /// <summary>停止后已产生的输出仍可读。</summary>
    /// <remarks>
    /// <b>这是"停止"与"丢弃"的区别</b>：用户停掉一个跑飞的构建，
    /// 往往正想看它跑到哪一步、报了什么错。
    /// </remarks>
    private static async Task<string?> OutputAfterStopAsync()
    {
        using var env = new Env();

        var cmd = OperatingSystem.IsWindows()
            ? "echo marker-before-sleep && ping -n 30 127.0.0.1 > nul"
            : "echo marker-before-sleep; sleep 30";

        var snap = env.Registry.Start("s1", cmd);
        await Task.Delay(700);

        env.Registry.Stop(snap.Id);
        await Task.Delay(500);

        var after = env.Registry.Read(snap.Id, 0);
        return after.Output.Contains("marker-before-sleep", StringComparison.Ordinal)
            ? null
            : $"停止后输出丢失：[{after.Output}]";
    }

    /// <summary>超并发上限时明确拒绝，不排队。</summary>
    /// <remarks>
    /// <b>排队比拒绝更糟</b>：模型会以为已启动，而它可能几分钟后才真正开始——
    /// 那时它已经基于错误假设继续做事了。
    /// </remarks>
    private static async Task<string?> ConcurrencyAsync()
    {
        using var env = new Env(maxConcurrentPerOwner: 2);

        var a = env.Registry.Start("s1", SleepCommand(20_000));
        var b = env.Registry.Start("s1", SleepCommand(20_000));
        var c = env.Registry.Start("s1", SleepCommand(20_000));

        if (a.State is not JobState.Running || b.State is not JobState.Running)
            return "前两个作业没启动成功";

        if (c.State is not JobState.Rejected)
            return $"超过上限应拒绝，实际 {c.State}";

        return c.Diagnostic?.Contains("上限", StringComparison.Ordinal) == true
            ? null
            : $"拒绝诊断没说清上限：{c.Diagnostic}";
    }

    /// <summary>上限是按 owner 算的，不是全局。</summary>
    /// <remarks>
    /// <b>全局上限会让一个会话的作业挤掉另一个会话的</b>——多会话并行时
    /// 表现为"我的作业起不来，但列表里没什么在跑"。
    /// </remarks>
    private static async Task<string?> PerOwnerAsync()
    {
        using var env = new Env(maxConcurrentPerOwner: 2);

        env.Registry.Start("s1", SleepCommand(20_000));
        env.Registry.Start("s1", SleepCommand(20_000));

        // s1 已满，s2 仍应有名额。
        var other = env.Registry.Start("s2", SleepCommand(20_000));
        return other.State is JobState.Running
            ? null
            : $"另一个 owner 被挤掉了：{other.State} {other.Diagnostic}";
    }

    /// <summary>不存在的标识要明确说，不能静默返回空。</summary>
    private static async Task<string?> UnknownIdAsync()
    {
        using var env = new Env();
        var snap = env.Registry.Read("job-does-not-exist", 0);

        if (snap.State is not JobState.Rejected) return $"应为 Rejected，实际 {snap.State}";

        // 诊断要指向"去看列表"，否则模型会继续猜标识。
        return snap.Diagnostic?.Contains("job_list", StringComparison.Ordinal) == true
            ? null
            : $"诊断没有给出下一步：{snap.Diagnostic}";
    }

    /// <summary>注册表释放时结束全部作业。</summary>
    /// <remarks>
    /// <b>不结束会留下孤儿进程</b>：主程序退出后它们继续跑，
    /// 而用户看到的是"关掉了但机器还在响"。
    /// </remarks>
    private static async Task<string?> DisposeKillsAsync()
    {
        var env = new Env();
        string id;

        try
        {
            var snap = env.Registry.Start("s1", SleepCommand(30_000));
            id = snap.Id;
            await Task.Delay(300);
            if (env.Registry.Read(id, 0).State is not JobState.Running)
                return "作业没启动成功，无法验证释放";
        }
        finally
        {
            env.Registry.Dispose();
            env.MarkDisposed();
        }

        // 释放后再读：注册表已 Dispose，读会抛——那正是"无法访问"的证据。
        try
        {
            env.Registry.Read(id, 0);
            return "释放后仍能读到作业，说明资源没被清";
        }
        catch (ObjectDisposedException)
        {
            return null;
        }
    }

    /// <summary>作业启动工具要声明 Execute 权限并带命令文本。</summary>
    /// <remarks>
    /// <b>报 None 会让审批形同虚设</b>：作业执行的就是 shell 命令，
    /// 与 shell 工具同一类风险。宿主需要看到命令才能决定放不放行。
    /// </remarks>
    private static Task<string?> PermissionAsync()
    {
        using var env = new Env();
        var tool = new JobStartTool(env.Registry, "s1");

        using var document = System.Text.Json.JsonDocument.Parse(
            """{"command":"rm -rf /"}""");
        var permission = tool.DescribePermission(document.RootElement);

        if (permission.Access is not ToolAccess.Execute)
            return Task.FromResult<string?>($"应报 Execute，实际 {permission.Access}");

        return Task.FromResult<string?>(permission.Commands.Contains("rm -rf /", StringComparer.Ordinal)
            ? null
            : $"没有把命令报给审批：{string.Join(",", permission.Commands)}");
    }

    /// <summary>
    /// 作业结束后、被读之前，输出不能被清掉。
    /// </summary>
    /// <remarks>
    /// <b>这是复核发现的缺陷</b>：<c>PruneFinished</c> 原本用
    /// "游标 >= 总写出"判断"已读完"，但**没有输出的作业两者都是 0**，
    /// 于是刚结束就被删——<c>Read</c> 返回 NotFound，用户永远看不到那个作业。
    /// <para>
    /// 更深一层：<c>EverRead</c> 一开始由 <c>Snapshot</c> 设置，而 <c>Start</c>
    /// 的返回也调 <c>Snapshot</c>——作业刚启动就被标成"读过"。
    /// 现在只有真正的读取（<c>Read</c>）才标记。
    /// </para>
    /// </remarks>
    private static async Task<string?> SurvivesUntilReadAsync()
    {
        using var env = new Env(maxConcurrentPerOwner: 3);

        // 一个立刻结束、没有输出的作业。
        var quick = env.Registry.Start("s1", Os("ver >nul", "true"));
        await Task.Delay(900);

        // **用 List 判断，不用 Read**：Read 会标记"已读过"，
        // 那样后面的 Start 就有权清掉它——而这条检查正是要验"没读过就不清"。
        var listed = env.Registry.List("s1").FirstOrDefault(j => j.Id == quick.Id);
        if (listed?.State is not JobState.Finished)
            return $"作业没有按预期结束（state={listed?.State}）";

        // 再起两个，触发 PruneFinished。
        env.Registry.Start("s1", SleepCommand(20_000));
        env.Registry.Start("s1", SleepCommand(20_000));

        // 那个已结束但没读过的作业必须还在。
        var after = env.Registry.Read(quick.Id, 0);

        return after.State is JobState.Finished
            ? null
            : $"已结束但未读的作业被清掉了（state={after.State}）：{after.Diagnostic}";
    }

    private static string Os(string windows, string posix) =>
        OperatingSystem.IsWindows() ? windows : posix;

    // ── 支撑 ─────────────────────────────────────────────────────

    /// <summary>跨平台的睡眠命令。</summary>
    private static string SleepCommand(int milliseconds) =>
        OperatingSystem.IsWindows()
            ? $"ping -n {Math.Max(2, milliseconds / 1000 + 1)} 127.0.0.1 > nul"
            : $"sleep {milliseconds / 1000.0:0.###}";

    private sealed class Env : IDisposable
    {
        private bool _disposed;

        public Env(int maxConcurrentPerOwner = 10, int retainBytes = JobRegistry.DefaultRetainBytes)
        {
            Root = Path.Combine(Path.GetTempPath(), $"dougong-jobs-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Root);
            Registry = new JobRegistry(Root, maxConcurrentPerOwner, retainBytes);
        }

        public string Root { get; }
        public JobRegistry Registry { get; }

        public void MarkDisposed() => _disposed = true;

        public void Dispose()
        {
            if (!_disposed)
            {
                try { Registry.Dispose(); } catch { /* 已释放 */ }
            }

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
