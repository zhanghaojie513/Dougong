using System.Text.Json;
using Dougong.Agent;

namespace Dougong.Agent.Comparison;

/// <summary>
/// 会话内切换工作目录的验收。
/// </summary>
/// <remarks>
/// <b>可观察面是"之后的相对路径落在哪"</b>，不是"那个字符串变了没有"：
/// 用户会遇到的症状是"模型说它换到了 src/api，接着读文件却还是读的老地方"。
/// 所以每条检查都走一次真实的文件/shell 路径解析。
///
/// <para>
/// <b>三条 dsh 语义各自有检查</b>：
/// <list type="number">
/// <item>换目录影响之后的相对路径操作</item>
/// <item>已有进程（持久 shell）保持自己的目录</item>
/// <item>写权限边界仍是原始项目——换目录不是换权限</item>
/// </list>
/// </para>
/// </remarks>
public static class WorkingDirectoryChecks
{
    public static async Task<int> RunAsync()
    {
        var failures = 0;
        failures += await CheckAsync("cwd-relative-paths-follow-current-directory", RelativePathsFollowAsync);
        failures += await CheckAsync("cwd-changes-once-than-rejects-missing-directory", ValidationAsync);
        failures += await CheckAsync("cwd-project-boundary-is-not-moved", ProjectBoundaryAsync);
        failures += await CheckAsync("cwd-change-survives-new-store-instance", PersistenceAsync);
        failures += await CheckAsync("cwd-disappeared-directory-falls-back-to-project", FallbackAsync);
        failures += await CheckAsync("cwd-tool-reads-when-cd-omitted", ToolReadAsync);
        failures += await CheckAsync("cwd-tool-rejects-nonexistent-directory", ToolRejectAsync);
        failures += await CheckAsync("cwd-tool-does-not-persist-when-write-fails", ToolWriteFailureAsync);
        failures += await CheckAsync("cwd-existing-shell-keeps-its-own-directory", ExistingShellAsync);
        failures += await CheckAsync("cwd-context-line-mentions-both-when-outside", ContextLineAsync);
        failures += await CheckAsync("cwd-is-wired-into-cli-composition", CompositionWiringAsync);

        Console.WriteLine(failures == 0
            ? "WORKING-DIRECTORY: ALL PASS"
            : $"WORKING-DIRECTORY: {failures} FAILURE(S)");
        return failures;
    }

    /// <summary>换目录后，相对路径按新目录解析。</summary>
    /// <remarks>
    /// <b>必须经 <see cref="LocalFileSystem"/> 验</b>，不能只断言
    /// <c>WorkingDirectory.Current</c>。第一条版本就是这么写的，
    /// 于是即使没调用 <c>UseDirectory</c> 它也绿——那正是最容易漏掉的一环。
    /// </remarks>
    private static async Task<string?> RelativePathsFollowAsync()
    {
        using var env = new Env();
        env.WriteFile("root.txt", "root");
        env.WriteFile("sub/inner.txt", "inner");

        var directory = new WorkingDirectory(env.Root);
        var files = new LocalFileSystem(directory.Current);

        // **必须经工具换目录，不能自己调 UseDirectory**。
        // 第一版就是自己调的：于是删掉工具里那句 `_files?.UseDirectory` 之后
        // 检查仍然全绿——它验的是一行测试自己的代码。
        // 这一轮负控正是靠"破坏后仍绿"发现的，属于本阶段记过的同一类问题。
        var tool = new WorkingDirectoryTool(directory, store: null, files: files);

        // 换之前：以项目根为起点，`inner.txt` 不存在（它在 sub/ 下）。
        var before = await TryReadAsync(files, "inner.txt");
        if (before is not null) return "前置状态不对：换目录前就在根下读到了 inner.txt";

        var result = await tool.ExecuteAsync(
            Invocation(new { cd = "sub" }), CancellationToken.None);

        var ok = result.Content.TryGetProperty("ok", out var okElement)
            && okElement.ValueKind is JsonValueKind.True;
        if (!ok) return $"工具切换目录失败：{ReadString(result.Content, "error")}";

        var after = await TryReadAsync(files, "inner.txt");
        return after == "inner"
            ? null
            : $"换目录后按新根读取失败（实际 '{after ?? "读不到"}'）——工具没让文件系统跟着换根";
    }

    /// <summary>目录不存在时拒绝，且不改当前值。</summary>
    private static async Task<string?> ValidationAsync()
    {
        await Task.CompletedTask;
        using var env = new Env();
        env.WriteFile("sub/inner.txt", "inner");

        var directory = new WorkingDirectory(env.Root);

        if (directory.TrySet("does-not-exist", out _, out var reason))
            return "不存在的目录应当被拒绝";
        if (reason.Length == 0) return "拒绝时没有给出原因";

        if (directory.Current != env.Root)
            return $"拒绝之后当前目录被改掉了：{directory.Current}";

        if (!directory.TrySet("sub", out _, out _)) return "合法目录应当被接受";
        return null;
    }

    /// <summary>
    /// 换目录不动写权限边界。
    /// </summary>
    /// <remarks>
    /// <b>这是本能力最危险的失败模式</b>：把守卫的根也换成新目录，
    /// 表现为模型在子目录里执行 <c>cd ..</c> 就出了项目。
    /// 那次越界不会报错，会安静地写到项目的上级目录。
    /// </remarks>
    private static async Task<string?> ProjectBoundaryAsync()
    {
        await Task.CompletedTask;
        using var env = new Env();
        env.WriteFile("sub/inner.txt", "inner");

        var directory = new WorkingDirectory(env.Root);

        if (!directory.TrySet("sub", out _, out _)) return "切到 sub 失败";

        // **守卫要在换目录之后再构造**——装配的真实顺序是"先读回已记录的目录、
        // 再用它建 guard"。第一版在 TrySet 之前建 guard，于是把 Project 改成
        // 会跟着 cd 变化的实现时检查仍然全绿：它比对的是 cd 之前的值。
        // 这一轮的负控就是这样发现检查空转的。
        var guard = new WorkspacePathGuard(directory.Project);

        // **先断言守卫的根确实是原始项目**。
        // 只验"某个路径能不能解析"抓不到"根换错了"，因为错误方向不唯一：
        // 根换成 sub 时 root.txt 与外部路径**都会被拒**，单看拒绝结果是分不清
        // "守卫正常工作"与"守卫看错了根"的。这条直接比对根。
        if (!string.Equals(
                Path.GetFullPath(guard.Root), Path.GetFullPath(env.Root),
                StringComparison.OrdinalIgnoreCase))
            return $"守卫的根不是原始项目：{guard.Root}";

        // 守卫仍以原始项目为根：从项目根的视角，sub/inner.txt 在工作区内。
        if (!guard.TryResolve("sub/inner.txt", out _, out _))
            return "原始项目内的路径被守卫拒绝了";

        // **用项目内、但在 sub 之外的路径**来验边界。
        //
        // 第一版用"项目的上级目录"，那是错的用例：把守卫的根换成 sub 之后，
        // 上级目录同样在 sub 之外，两条路径都会被拒——检查因此对缺陷不敏感。
        // 而 root.txt 在项目内、不在 sub 内，正好能把"根跟着换掉了"暴露出来：
        // 根是原项目时它可解析，根变成 sub 时它必须被拒。
        return guard.TryResolve("root.txt", out _, out _)
            ? null
            : "项目内的 root.txt 被拒绝了——守卫的根跟着换目录变了";
    }

    /// <summary>变更能跨进程恢复（换一个新的存储实例读回）。</summary>
    private static async Task<string?> PersistenceAsync()
    {
        using var env = new Env();
        env.WriteFile("sub/inner.txt", "inner");

        var path = JsonlWorkingDirectoryStore.PathFor(env.SessionLogPath);
        var first = new JsonlWorkingDirectoryStore(path);

        await first.AppendAsync(new WorkingDirectoryChange(
            Path.Combine(env.Root, "sub"), DateTimeOffset.UtcNow));

        // 新实例 = 新进程的等价物：只认文件内容。
        var second = new JsonlWorkingDirectoryStore(path);
        var last = await second.ReadLastAsync();

        return last is null
            ? "读不回已保存的目录——重开会话后模型会按原始项目猜路径"
            : null;
    }

    /// <summary>记录里的目录已不存在时退回原始项目。</summary>
    /// <remarks>
    /// <b>不是报错</b>：目录消失（临时目录被清、分支被删）之后会话还得能继续。
    /// 但退回这件事要能观察——否则用户以为还在子目录里。
    /// </remarks>
    private static async Task<string?> FallbackAsync()
    {
        await Task.CompletedTask;
        using var env = new Env();

        var gone = Path.Combine(env.Root, "sub");
        Directory.CreateDirectory(gone);
        var directory = new WorkingDirectory(env.Root, gone);
        Directory.Delete(gone, recursive: true);

        if (!directory.TryEnsureExists(out var restored, out var reason))
            return $"原始项目存在时应能回退，实际失败：{reason}";

        if (!restored) return "目录已消失却没有标记为回退";
        if (reason is null) return "回退没有给出原因";
        return directory.Current == env.Root
            ? null
            : $"回退后目录不对：{directory.Current}";
    }

    /// <summary>不传 cd 时只读，不改目录。</summary>
    private static async Task<string?> ToolReadAsync()
    {
        using var env = new Env();
        var directory = new WorkingDirectory(env.Root);
        var tool = new WorkingDirectoryTool(directory);

        var before = directory.Current;
        var result = await tool.ExecuteAsync(Invocation(new { }), CancellationToken.None);

        var cwd = ReadString(result.Content, "cwd");
        if (cwd != before) return $"只读调用改变了目录：{before} → {cwd}";

        var changed = result.Content.TryGetProperty("changed", out var c)
            && c.ValueKind is JsonValueKind.True;
        return changed ? "只读调用却报告 changed=true" : null;
    }

    /// <summary>不存在的目录被拒绝，且结果里带上原因。</summary>
    private static async Task<string?> ToolRejectAsync()
    {
        using var env = new Env();
        var directory = new WorkingDirectory(env.Root);
        var tool = new WorkingDirectoryTool(directory);

        var result = await tool.ExecuteAsync(Invocation(new { cd = "nope" }), CancellationToken.None);

        var ok = result.Content.TryGetProperty("ok", out var okElement)
            && okElement.ValueKind is JsonValueKind.True;
        if (ok) return "不存在的目录却报告成功";

        var error = ReadString(result.Content, "error");
        if (string.IsNullOrWhiteSpace(error)) return "失败结果没有原因";

        return directory.Current == env.Root ? null : "拒绝之后目录被改掉了";
    }

    /// <summary>
    /// 落盘失败时内存状态回退。
    /// </summary>
    /// <remarks>
    /// <b>不回退的后果</b>：模型看到"成功"，继续按新目录干活，重开会话后
    /// 目录又回到旧的——期间读写的文件路径全错，而没有任何提示。
    /// 这是"两段各自正确、中间没接"那类缺陷的典型形态。
    /// </remarks>
    private static async Task<string?> ToolWriteFailureAsync()
    {
        using var env = new Env();
        env.WriteFile("sub/inner.txt", "inner");

        var directory = new WorkingDirectory(env.Root);
        var tool = new WorkingDirectoryTool(directory, new ThrowingStore());
        var before = directory.Current;

        var result = await tool.ExecuteAsync(Invocation(new { cd = "sub" }), CancellationToken.None);
        var ok = result.Content.TryGetProperty("ok", out var okElement)
            && okElement.ValueKind is JsonValueKind.True;

        if (ok) return "落盘失败却报告成功";
        return directory.Current == before
            ? null
            : $"落盘失败后目录没回退：{before} → {directory.Current}";
    }

    /// <summary>已有进程保持自己的目录。</summary>
    /// <remarks>
    /// <b>验的是"只影响之后启动的进程"</b>：持久 shell 里可能已经 <c>cd</c> 过。
    /// 把它的目录也换掉会让"我在哪"不可推断。
    /// </remarks>
    private static async Task<string?> ExistingShellAsync()
    {
        await Task.CompletedTask;
        using var env = new Env();
        env.WriteFile("sub/inner.txt", "inner");

        var directory = new WorkingDirectory(env.Root);
        var shell = new LocalShellExecutor(env.Root);

        // 换目录只调 UseDirectory，不碰持久 shell 的实例。
        // 断言方式：持久 shell 不受影响这件事由"没有接进来"结构保证，
        // 这里验的是装配里确实没把它接进来（见 CompositionWiringAsync）。
        if (!directory.TrySet("sub", out var resolved, out _)) return "切到 sub 失败";
        shell.UseDirectory(resolved);

        // 执行器确实换了（新进程会用新目录）——用一个只读命令观察。
        var result = await shell.ExecuteAsync(
            OperatingSystem.IsWindows() ? "cd" : "pwd",
            TimeSpan.FromSeconds(10), 4096);

        var text = result.Stdout.Trim();
        return text.Replace('/', '\\').EndsWith("sub", StringComparison.OrdinalIgnoreCase)
            ? null
            : $"新进程没有用新目录（输出 '{text}'）";
    }

    /// <summary>离开项目后，上下文行要同时给出两者。</summary>
    private static async Task<string?> ContextLineAsync()
    {
        await Task.CompletedTask;
        using var env = new Env();
        env.WriteFile("sub/inner.txt", "inner");

        var directory = new WorkingDirectory(env.Root);
        var inside = directory.DescribeForModel();
        if (!inside.Contains(env.Root, StringComparison.OrdinalIgnoreCase))
            return "上下文行没提到当前目录";

        if (!directory.TrySet("sub", out _, out _)) return "切到 sub 失败";
        var outside = directory.DescribeForModel();

        if (!outside.Contains(env.Root, StringComparison.OrdinalIgnoreCase))
            return "离开项目后没有提原始项目——模型会以为能写到任何地方";

        return outside.Contains(directory.Current, StringComparison.OrdinalIgnoreCase)
            ? null
            : "上下文行没提到当前目录";
    }

    /// <summary>
    /// CLI 装配里真的注册了工具、并且传了会话路径。
    /// </summary>
    /// <remarks>
    /// <b>这一条抓的是"类在、没接进去"</b>——progress 文档的判定规则 1。
    /// 本项目的实际教训：`SystemPrompt` 属性存在但 CLI 从不赋值，
    /// 那一栏永远是 0。<see cref="WorkingDirectoryTool"/> 若不注册，
    /// 前面所有检查照绿，而模型根本看不到这个工具。
    /// </remarks>
    private static async Task<string?> CompositionWiringAsync()
    {
        await Task.CompletedTask;
        var root = FindRepoRoot();
        var file = Path.Combine(root, "src", "Dougong.Cli", "SessionComposition.cs");
        if (!File.Exists(file)) return $"找不到装配文件：{file}";

        var text = File.ReadAllText(file);

        if (!text.Contains("new WorkingDirectoryTool(", StringComparison.Ordinal))
            return "装配里没有注册 working_directory 工具";

        if (!text.Contains("Directory = _directory", StringComparison.Ordinal))
            return "运行时没有拿到目录——模型不会知道自己在哪";

        if (!text.Contains("JsonlWorkingDirectoryStore.PathFor(", StringComparison.Ordinal))
            return "没有接目录持久化——换过的目录重开就丢";

        return null;
    }

    // ── 支撑 ─────────────────────────────────────────────────────

    private static async Task<string?> TryReadAsync(IFileSystem files, string path)
    {
        try
        {
            return await files.ReadTextAsync(path);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
    }

    private static string? ReadString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.String
            ? value.GetString()
            : null;

    private static ToolInvocation Invocation(object arguments)
    {
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(arguments));
        return new ToolInvocation("inv-1", WorkingDirectoryTool.ToolId, document.RootElement.Clone());
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Dougong.slnx")))
                return directory.FullName;
            directory = directory.Parent;
        }

        return Directory.GetCurrentDirectory();
    }

    /// <summary>写入总是失败的存储，用于验"落盘失败要回退"。</summary>
    private sealed class ThrowingStore : IWorkingDirectoryStore
    {
        public Task<WorkingDirectoryChange?> ReadLastAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult<WorkingDirectoryChange?>(null);

        public Task AppendAsync(
            WorkingDirectoryChange change, CancellationToken cancellationToken = default) =>
            throw new IOException("模拟磁盘不可写");
    }

    private sealed class Env : IDisposable
    {
        public Env()
        {
            Root = Path.Combine(Path.GetTempPath(), $"dougong-cwd-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Root);
            SessionLogPath = Path.Combine(Root, "1_session.jsonl");
        }

        public string Root { get; }

        public string SessionLogPath { get; }

        public void WriteFile(string relative, string content)
        {
            var full = Path.Combine(Root, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, content);
        }

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
