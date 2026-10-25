using System.Diagnostics;
using System.Text.Json;
using Dougong.Agent;
using Dougong.Kernel;

namespace Dougong.Agent.Comparison;

/// <summary>
/// 性能基线测量（M3 切片 3.7）。
/// </summary>
/// <remarks>
/// **只记录数字，不做性能承诺**。`iteration-roadmap.md` I0 要求的是
/// "把实测数字写进文档，而非宣称"——数字会随硬件、SDK、数据形状变化，
/// 承诺会变成无法兑现的债。
/// <para>
/// 三个场景对应三类真实担心：
/// <list type="bullet">
/// <item><b>大会话</b>：历史很长时，追加与重放是否退化</item>
/// <item><b>长会话</b>：反复装卸插件是否线性增长（泄漏会是超线性的）</item>
/// <item><b>并发会话</b>：多会话争用同一工作区时的实际代价</item>
/// </list>
/// </para>
/// </remarks>
public static class PerformanceBaselineChecks
{
    public static async Task<int> RunAsync()
    {
        Console.WriteLine($"# 环境：{Environment.OSVersion}，{Environment.ProcessorCount} 核，"
            + $"SDK {Environment.Version}");
        Console.WriteLine();

        await MeasureLargeSessionAsync();
        await MeasureLongSessionAsync();
        await MeasureConcurrentSessionsAsync();

        return 0;
    }

    /// <summary>大会话：1000 条历史的追加与重放耗时。</summary>
    private static async Task MeasureLargeSessionAsync()
    {
        var path = Path.Combine(Path.GetTempPath(), $"dougong-perf-large-{Guid.NewGuid():N}.jsonl");
        try
        {
            const int records = 1000;

            var openWatch = Stopwatch.StartNew();
            var store = await JsonlAgentStore.OpenAsync(path, "perf-large");
            openWatch.Stop();

            var appendWatch = Stopwatch.StartNew();
            for (var i = 0; i < records; i++)
            {
                await store.CommitAsync(new StoreRecord(
                    0, StoreRecordKind.UserInput,
                    JsonSerializer.SerializeToElement(new { index = i, text = new string('x', 200) })));
            }

            appendWatch.Stop();
            await store.DisposeAsync();

            // 重放：重新打开并读完全部记录。
            var replayWatch = Stopwatch.StartNew();
            var reopened = await JsonlAgentStore.OpenAsync(path, "perf-large");
            var count = 0;
            await foreach (var _ in reopened.ReadCommittedAsync()) count++;
            replayWatch.Stop();
            await reopened.DisposeAsync();

            var size = new FileInfo(path).Length;

            Console.WriteLine($"## 大会话（{records} 条，每条约 220 字节）");
            Console.WriteLine($"  打开：{openWatch.ElapsedMilliseconds} ms");
            Console.WriteLine($"  追加 {count} 条：{appendWatch.ElapsedMilliseconds} ms"
                + $"（{appendWatch.Elapsed.TotalMilliseconds / count:F2} ms/条）");
            Console.WriteLine($"  重放 {count} 条：{replayWatch.ElapsedMilliseconds} ms"
                + $"（{replayWatch.Elapsed.TotalMilliseconds / count:F2} ms/条）");
            Console.WriteLine($"  文件大小：{size / 1024} KB");
            Console.WriteLine();
        }
        finally
        {
            try { File.Delete(path); } catch (IOException) { }
        }
    }

    /// <summary>长会话：插件装卸轮次增加时的耗时趋势。</summary>
    /// <remarks>
    /// 关心的是**是否线性**。若 20 轮耗时明显超过 1 轮的 20 倍，
    /// 说明有随轮次累积的开销——那通常意味着泄漏或历史表变长。
    /// </remarks>
    private static async Task MeasureLongSessionAsync()
    {
        var samplePath = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "..",
            "samples", "Dougong.Sample.Plugin", "bin", "Release", "net10.0",
            "Dougong.Sample.Plugin.dll"));

        if (!File.Exists(samplePath))
        {
            Console.WriteLine("## 长会话：跳过（找不到样例插件，先构建 samples）");
            Console.WriteLine();
            return;
        }

        Console.WriteLine("## 长会话（插件装卸）");

        foreach (var rounds in new[] { 1, 10, 20 })
        {
            await using var kernel = new DougongKernel();
            var watch = Stopwatch.StartNew();

            for (var i = 0; i < rounds; i++)
            {
                var handle = await kernel.LoadAsync(samplePath);
                await kernel.UnloadAsync(handle.Id);
            }

            watch.Stop();
            Console.WriteLine($"  {rounds,2} 轮：{watch.ElapsedMilliseconds,4} ms"
                + $"（{watch.Elapsed.TotalMilliseconds / rounds:F1} ms/轮）");
        }

        Console.WriteLine();
    }

    /// <summary>并发会话：多会话争用同一工作区。</summary>
    /// <remarks>
    /// 工作区锁是进程级共享的（G9），因此这里测的是**真实的争用代价**，
    /// 不是"每个会话各锁各的"那种假并发。
    /// </remarks>
    private static async Task MeasureConcurrentSessionsAsync()
    {
        const int sessions = 4;
        const int callsPerSession = 50;
        var workspace = Path.Combine(Path.GetTempPath(), "dougong-perf-ws");
        Directory.CreateDirectory(workspace);

        try
        {
            // 顺序执行作为基线。
            var sequential = await RunSessionsAsync(sessions, callsPerSession, workspace, concurrent: false);
            var concurrent = await RunSessionsAsync(sessions, callsPerSession, workspace, concurrent: true);

            Console.WriteLine($"## 并发会话（{sessions} 会话 × {callsPerSession} 次工具执行）");
            Console.WriteLine($"  顺序：{sequential} ms");
            Console.WriteLine($"  并发：{concurrent} ms"
                + $"（加速比 {sequential / (double)Math.Max(concurrent, 1):F2}×）");
            Console.WriteLine("  注：同一工作区，`WorkspaceExclusive` 工具本应互斥；"
                + "不同资源键的读可以并行。");
            Console.WriteLine();
        }
        finally
        {
            try { Directory.Delete(workspace, recursive: true); } catch (IOException) { }
        }
    }

    private static async Task<long> RunSessionsAsync(
        int sessions, int calls, string workspace, bool concurrent)
    {
        async Task OneAsync(int index)
        {
            using var gate = new ToolResourceGate();
            var plan = new ToolExecutionPlan(ToolConcurrency.Serial)
            {
                Reads = [$"file:{workspace}/f{index}.txt"],
            };

            for (var i = 0; i < calls; i++)
            {
                using var lease = await gate.EnterAsync(plan, workspace);
                // 工具本身的开销不应主导测量，因此这里不做实际 IO。
            }
        }

        var watch = Stopwatch.StartNew();
        if (concurrent)
        {
            await Task.WhenAll(Enumerable.Range(0, sessions).Select(OneAsync));
        }
        else
        {
            for (var i = 0; i < sessions; i++) await OneAsync(i);
        }

        watch.Stop();
        return watch.ElapsedMilliseconds;
    }
}
