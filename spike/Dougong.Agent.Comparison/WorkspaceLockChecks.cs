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
