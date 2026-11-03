using Dougong.Agent;

namespace Dougong.Agent.Comparison;

/// <summary>
/// 工作区级互斥的跨会话有效性（G9）。
/// </summary>
/// <remarks>
/// `architecture.md` §4.4 要求同一工作区的串行/独占工具互斥。但
/// <see cref="ToolResourceGate"/> 的锁表是**实例字段**，而每个会话（CLI REPL 一次、
/// ACP 每个 session 一次）都 `new ToolResourceGate()`。于是同一工作区的两个会话
/// 各持一把互不相干的锁——互斥在跨会话时不成立。
/// <para>
/// 这不是理论问题：ACP 天然支持多会话，桌面宿主也会同时开多个。两个会话同时对
/// 同一文件做写操作时，本应串行，实际会重叠。
/// </para>
/// </remarks>
public static class WorkspaceLockChecks
{
    public static async Task<int> RunAsync()
    {
        var failures = 0;
        failures += await CheckAsync("same-workspace-locks-are-shared-across-gates", SharedAcrossGatesAsync);
        failures += await CheckAsync("different-workspaces-do-not-block", DifferentWorkspacesAsync);
        failures += await CheckAsync("shared-lock-table-is-reclaimed", ReclaimedAsync);
        failures += await CheckAsync("parallel-reads-do-not-block-each-other", ParallelReadsAsync);
        failures += await CheckAsync("read-blocks-write-on-same-path", ReadWriteConflictAsync);
        failures += await CheckAsync("unrelated-writes-run-in-parallel", UnrelatedWritesAsync);
        failures += await CheckAsync("no-keys-falls-back-to-serial", NoKeysFallsBackAsync);
        failures += await CheckAsync("file-tools-declare-real-resource-keys", FileToolsDeclareKeysAsync);
        failures += await CheckAsync("resource-key-uses-session-dir-not-process-cwd", KeyBaseIsSessionDirAsync);
        failures += await CheckAsync("resource-key-base-follows-directory-change", KeyBaseFollowsChangeAsync);

        Console.WriteLine(failures == 0
            ? "WORKSPACE-LOCK: ALL PASS"
            : $"WORKSPACE-LOCK: {failures} FAILURE(S)");
        return failures;
    }

    /// <summary>
    /// 两个 gate 实例（模拟两个会话）对同一工作区必须互斥。
    /// </summary>
    /// <remarks>
    /// 判据：第一个 gate 持锁期间，第二个 gate 的 <c>EnterAsync</c> 不能立即返回。
    /// </remarks>
    private static async Task<string?> SharedAcrossGatesAsync()
    {
        const string workspace = "/tmp/shared-workspace";

        using var sessionA = new ToolResourceGate();
        using var sessionB = new ToolResourceGate();

        var exclusive = new ToolExecutionPlan(ToolConcurrency.WorkspaceExclusive);

        using var held = await sessionA.EnterAsync(exclusive, workspace);

        // 第二个会话尝试进入同一工作区：必须被挡住。
        var blocked = await Task.WhenAny(
            sessionB.EnterAsync(exclusive, workspace).AsTask(),
            Task.Delay(300));

        if (blocked is Task<IDisposable>)
        {
            var lease = await (Task<IDisposable>)blocked;
            lease.Dispose();
            return "同一工作区在两个会话中未互斥：第二个会话立即取得了执行权。"
                + "ToolResourceGate 的锁表是实例字段，每个会话各持一把锁";
        }

        return null;
    }

    /// <summary>不同工作区不该互相阻塞——否则"按工作区隔离"就没有意义。</summary>
    private static async Task<string?> DifferentWorkspacesAsync()
    {
        using var gate = new ToolResourceGate();
        var exclusive = new ToolExecutionPlan(ToolConcurrency.WorkspaceExclusive);

        using var heldA = await gate.EnterAsync(exclusive, "/tmp/ws-a");

        var entered = await Task.WhenAny(
            gate.EnterAsync(exclusive, "/tmp/ws-b").AsTask(),
            Task.Delay(300));

        if (entered is not Task<IDisposable>)
            return "不同工作区被错误地互相阻塞";

        var lease = await (Task<IDisposable>)entered;
        lease.Dispose();
        return null;
    }

    /// <summary>
    /// 共享锁表必须能回收：否则长期运行的宿主会随工作区数量无界增长。
    /// </summary>
    /// <remarks>
    /// 断言方式：反复创建/释放 gate，读取共享表条目数。条目数不能随次数增长。
    /// 用反射读私有静态字段——这是"验证资源确实被归还"的唯一直接办法；
    /// 间接行为测不出泄漏。
    /// </remarks>
    private static async Task<string?> ReclaimedAsync()
    {
        var field = typeof(ToolResourceGate).GetField(
            "SharedWorkspaceLocks",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);

        if (field is null) return "找不到 SharedWorkspaceLocks 字段（结构变了，请更新本检查）";

        int Count() => field.GetValue(null) is System.Collections.ICollection c ? c.Count : -1;

        var baseline = Count();
        if (baseline < 0) return "无法读取共享锁表大小";

        var exclusive = new ToolExecutionPlan(ToolConcurrency.WorkspaceExclusive);

        // 反复用不同工作区：若没有引用计数回收，条目数会线性增长。
        for (var i = 0; i < 20; i++)
        {
            using var gate = new ToolResourceGate();
            using var lease = await gate.EnterAsync(exclusive, $"/tmp/reclaim-ws-{i}");
        }

        var after = Count();
        return after <= baseline
            ? null
            : $"共享锁表未回收：从 {baseline} 涨到 {after}（20 轮后）";
    }

    /// <summary>两个只读调用必须能并行——串行化它们是真实损失（G5）。</summary>
    private static async Task<string?> ParallelReadsAsync()
    {
        using var gate = new ToolResourceGate(isolated: true);

        var reader = new ToolExecutionPlan(ToolConcurrency.Serial)
        {
            Reads = ["file:a.txt"],
        };

        using var first = await gate.EnterAsync(reader, "/tmp/g5-ws");

        // 第二个只读调用必须立刻拿到，不能等第一个释放。
        var second = await Task.WhenAny(
            gate.EnterAsync(reader, "/tmp/g5-ws").AsTask(),
            Task.Delay(300));

        if (second is not Task<IDisposable>)
            return "两个只读调用互相阻塞：读-读应该可以并行";

        var lease = await (Task<IDisposable>)second;
        lease.Dispose();
        return null;
    }

    /// <summary>读与写对同一资源必须互斥。</summary>
    private static async Task<string?> ReadWriteConflictAsync()
    {
        using var gate = new ToolResourceGate(isolated: true);

        var reader = new ToolExecutionPlan(ToolConcurrency.Serial) { Reads = ["file:a.txt"] };
        var writer = new ToolExecutionPlan(ToolConcurrency.Serial) { Writes = ["file:a.txt"] };

        using var held = await gate.EnterAsync(reader, "/tmp/g5-ws");

        var write = await Task.WhenAny(
            gate.EnterAsync(writer, "/tmp/g5-ws").AsTask(),
            Task.Delay(300));

        if (write is Task<IDisposable>)
        {
            var lease = await (Task<IDisposable>)write;
            lease.Dispose();
            return "读者持锁期间写者取得了执行权：读-写未互斥";
        }

        return null;
    }

    /// <summary>不同资源的写可以并行——资源键的意义就在于此。</summary>
    private static async Task<string?> UnrelatedWritesAsync()
    {
        using var gate = new ToolResourceGate(isolated: true);

        var writerA = new ToolExecutionPlan(ToolConcurrency.Serial) { Writes = ["file:a.txt"] };
        var writerB = new ToolExecutionPlan(ToolConcurrency.Serial) { Writes = ["file:b.txt"] };

        using var heldA = await gate.EnterAsync(writerA, "/tmp/g5-ws");

        var second = await Task.WhenAny(
            gate.EnterAsync(writerB, "/tmp/g5-ws").AsTask(),
            Task.Delay(300));

        if (second is not Task<IDisposable>)
            return "不同资源的写互相阻塞：资源键没有起作用";

        var lease = await (Task<IDisposable>)second;
        lease.Dispose();
        return null;
    }

    /// <summary>未声明资源键时退回两级保守默认，行为不变。</summary>
    private static async Task<string?> NoKeysFallsBackAsync()
    {
        using var gate = new ToolResourceGate(isolated: true);

        var plain = ToolExecutionPlan.Serial;
        using var held = await gate.EnterAsync(plain, "/tmp/g5-ws");

        var second = await Task.WhenAny(
            gate.EnterAsync(plain, "/tmp/g5-ws").AsTask(),
            Task.Delay(300));

        if (second is Task<IDisposable>)
        {
            var lease = await (Task<IDisposable>)second;
            lease.Dispose();
            return "未声明资源键时应保持串行，实际并行了";
        }

        return null;
    }

    /// <summary>
    /// 真实的文件工具必须声明资源键，而不只是框架支持了键。
    /// </summary>
    /// <remarks>
    /// 这条防的是"能力实现了但没人用"——门 3 的老问题。框架有读写锁，
    /// 而 read_file/write_file 仍声明 Serial，等于什么都没变。
    /// <para>
    /// 同时验证规范化：`./a.txt` 与 `<workspace>/a.txt` 必须是同一个键，
    /// 否则该互斥的没互斥。
    /// </para>
    /// </remarks>
    /// <summary>
    /// 相对路径的资源键必须按**会话目录**解析，不能按进程 CWD。
    /// </summary>
    /// <remarks>
    /// <b>这是修掉的一个真实缺陷</b>：键原先用 <c>Path.GetFullPath(相对路径)</c>，
    /// 取的是进程当前目录；而工具读写用的是会话根。正常启动时两者恰好相同，
    /// 所以平时看不出来。一旦宿主从别的目录启动（或会话换过目录），
    /// 同一个文件就得到两个键——互斥失效，症状是"两个写并发落上去，
    /// 后写的覆盖前写的，两边都说成功"。
    ///
    /// <para>
    /// 验法**不能靠改进程 CWD**（那会影响同进程的其它检查）：
    /// 直接给工具一个与会话根不同的基准，断言键跟随会话根。
    /// </para>
    /// </remarks>
    private static async Task<string?> KeyBaseIsSessionDirAsync()
    {
        await Task.CompletedTask;

        var session = Path.Combine(Path.GetTempPath(), "g5-sess-" + Guid.NewGuid().ToString("N"));
        var unrelated = Path.Combine(Path.GetTempPath(), "g5-unrel-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(session);
        Directory.CreateDirectory(unrelated);

        try
        {
            var files = new LocalFileSystem(session);
            var read = new ReadFileTool(files);
            read.UseResourceBase(session);

            var key = read.DescribeExecution(Args("a.txt")).Reads[0];

            var expected = "file:" + Path.GetFullPath(Path.Combine(session, "a.txt"));
            if (key != expected)
                return $"相对路径没有按会话目录解析：得到 {key}，应为 {expected}";

            // 反向确认：它不等于任何以进程 CWD 为基准的结果（除非 CWD 恰是会话根）。
            var cwdKey = "file:" + Path.GetFullPath("a.txt");
            return key == cwdKey && Path.GetFullPath(session) != Path.GetFullPath(Directory.GetCurrentDirectory())
                ? $"键按进程 CWD 解析了：{key}"
                : null;
        }
        finally
        {
            try { Directory.Delete(session, recursive: true); } catch { }
            try { Directory.Delete(unrelated, recursive: true); } catch { }
        }
    }

    /// <summary>
    /// 换目录之后，资源键基准必须跟着换。
    /// </summary>
    /// <remarks>
    /// <b>只换文件系统的根是不够的</b>：读写走新目录、键按旧目录算，
    /// 同一个文件照样是两个键。这条直接调用工具暴露的
    /// <see cref="IFileResourceKeyOwner.UseResourceBase"/> 来验。
    /// </remarks>
    private static async Task<string?> KeyBaseFollowsChangeAsync()
    {
        await Task.CompletedTask;

        var session = Path.Combine(Path.GetTempPath(), "g5-cwd-" + Guid.NewGuid().ToString("N"));
        var moved = Path.Combine(session, "sub");
        Directory.CreateDirectory(moved);

        try
        {
            var files = new LocalFileSystem(session);
            var read = new ReadFileTool(files);
            read.UseResourceBase(session);

            var before = read.DescribeExecution(Args("a.txt")).Reads[0];

            // 模拟会话换目录：文件系统换根 + 工具的键基准同步换。
            files.UseDirectory(moved);
            read.UseResourceBase(moved);

            var after = read.DescribeExecution(Args("a.txt")).Reads[0];

            if (before == after)
                return $"换目录后键没有变：{before}——同一个相对路径在新旧目录下成了同一个键";

            var expected = "file:" + Path.GetFullPath(Path.Combine(moved, "a.txt"));
            return after == expected
                ? null
                : $"换目录后键不对：得到 {after}，应为 {expected}";
        }
        finally
        {
            try { Directory.Delete(session, recursive: true); } catch { }
        }
    }

    private static async Task<string?> FileToolsDeclareKeysAsync()
    {
        var workspace = Path.Combine(Path.GetTempPath(), "g5-keys-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspace);
        try
        {
            // 工具已从 CLI 搬到 SDK：两个入口共用一个实现，
            // 因此这里直接构造 SDK 的工具（seam 用本地实现）。
            var files = new LocalFileSystem(workspace);
            var read = new ReadFileTool(files);
            var write = new WriteFileTool(files);

            var readPlan = read.DescribeExecution(Args("a.txt"));
            var writePlan = write.DescribeExecution(Args("a.txt"));

            if (readPlan.Reads.Count == 0)
                return "read_file 未声明 Reads：两个读仍会排队";
            if (writePlan.Writes.Count == 0)
                return "write_file 未声明 Writes：写不会与读互斥";

            // 同一文件的不同写法必须得到同一个键。
            var dotted = read.DescribeExecution(Args("./a.txt"));
            if (dotted.Reads[0] != readPlan.Reads[0])
                return $"路径未规范化：'a.txt' → {readPlan.Reads[0]}，'./a.txt' → {dotted.Reads[0]}";

            // 读写必须落在同一个键上，否则两者不互斥。
            if (readPlan.Reads[0] != writePlan.Writes[0])
                return $"读与写用了不同的键：读 {readPlan.Reads[0]} / 写 {writePlan.Writes[0]}";

            // 真实互斥：读持锁期间，写同一文件必须等。
            using var gate = new ToolResourceGate(isolated: true);
            using var held = await gate.EnterAsync(readPlan, workspace);

            var blocked = await Task.WhenAny(
                gate.EnterAsync(writePlan, workspace).AsTask(),
                Task.Delay(300));

            if (blocked is Task<IDisposable>)
            {
                var lease = await (Task<IDisposable>)blocked;
                lease.Dispose();
                return "read_file 持锁期间 write_file 取得了执行权：真实工具未互斥";
            }

            return null;
        }
        finally
        {
            try { Directory.Delete(workspace, recursive: true); } catch (IOException) { }
        }
    }

    private static System.Text.Json.JsonElement Args(string path)
    {
        using var document = System.Text.Json.JsonDocument.Parse(
            System.Text.Json.JsonSerializer.Serialize(new { path }));
        return document.RootElement.Clone();
    }

    private static async Task<int> CheckAsync(string name, Func<Task<string?>> action)
    {
        try
        {
            var failure = await action();
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
