using System.Text;
using Dougong.Agent;

namespace Dougong.Agent.Comparison;

/// <summary>
/// 每轮改动文件清单的验收。
/// </summary>
/// <remarks>
/// <b>可观察面是"用户看到的清单"</b>：
/// <list type="bullet">
/// <item>按用户输入切轮，不是按工具调用</item>
/// <item><b>只认写工具</b>——<c>read_file</c> 也有 <c>path</c> 参数，
/// 按参数名判断会把所有读操作算成改动</item>
/// <item>同一轮改同一个文件两次只报一次</item>
/// <item>shell 的改动<b>不在清单里</b>（真实盲区，不假装覆盖）</item>
/// </list>
/// </remarks>
public static class WorkspaceChangeChecks
{
    public static async Task<int> RunAsync()
    {
        var failures = 0;
        failures += await CheckAsync("changes-splits-by-user-turn", SplitsByTurnAsync);
        failures += await CheckAsync("changes-excludes-read-tools", ExcludesReadsAsync);
        failures += await CheckAsync("changes-dedupes-within-turn", DedupesAsync);
        failures += await CheckAsync("changes-includes-write-and-edit", BothWriteToolsAsync);
        failures += await CheckAsync("changes-omits-shell-commands", OmitsShellAsync);
        failures += await CheckAsync("changes-keeps-final-turn", KeepsFinalTurnAsync);
        failures += await CheckAsync("changes-tolerates-corrupt-tail", CorruptTailAsync);
        failures += await CheckAsync("changes-survives-legacy-payload", LegacyPayloadAsync);

        Console.WriteLine(failures == 0
            ? "WORKSPACE-CHANGES: ALL PASS"
            : $"WORKSPACE-CHANGES: {failures} FAILURE(S)");
        return failures;
    }

    /// <summary>按用户输入切轮，而不是按工具调用。</summary>
    /// <remarks>
    /// 一轮里调三次工具不等于三轮。用工具调用数轮次会让清单显示成
    /// "第 1 轮、第 2 轮、第 3 轮"而用户只问过一次。
    /// </remarks>
    private static async Task<string?> SplitsByTurnAsync()
    {
        using var dir = new TempDir();
        dir.Session(
            UserInput(1, "第一轮"),
            WriteCall(2, "c1", "write_file", "a.txt"),
            ToolDone(3, "c1"),
            WriteCall(4, "c2", "write_file", "b.txt"),
            ToolDone(5, "c2"),
            UserInput(6, "第二轮"),
            WriteCall(7, "c3", "write_file", "c.txt"));

        var turns = await WorkspaceChanges.ReadAsync(dir.SessionFile);

        if (turns.Count != 2) return $"应切成 2 轮，实际 {turns.Count}";
        if (turns[0].DistinctPaths.Count != 2)
            return $"第一轮应改 2 个文件，实际 {turns[0].DistinctPaths.Count}";
        return turns[1].DistinctPaths.SequenceEqual(["c.txt"])
            ? null
            : $"第二轮内容不对：{string.Join(",", turns[1].DistinctPaths)}";
    }

    /// <summary>读工具不算改动。</summary>
    /// <remarks>
    /// <b>按工具名白名单，不按"有没有 path 参数"</b>：<c>read_file</c>、
    /// <c>glob</c> 都带路径，按参数判断会把所有读操作算成改动。
    /// </remarks>
    private static async Task<string?> ExcludesReadsAsync()
    {
        using var dir = new TempDir();
        dir.Session(
            UserInput(1, "看看这些文件"),
            WriteCall(2, "c1", "read_file", "read.txt"),
            WriteCall(3, "c2", "glob", "**/*.cs"),
            WriteCall(4, "c3", "grep", "pattern"));

        var turns = await WorkspaceChanges.ReadAsync(dir.SessionFile);
        return turns.Count == 1 && turns[0].DistinctPaths.Count == 0
            ? null
            : $"读操作被算成了改动：{string.Join(",", turns.SelectMany(t => t.DistinctPaths))}";
    }

    private static async Task<string?> DedupesAsync()
    {
        using var dir = new TempDir();
        dir.Session(
            UserInput(1, "改三次同一个文件"),
            WriteCall(2, "c1", "write_file", "same.txt"),
            WriteCall(3, "c2", "edit_file", "same.txt"),
            WriteCall(4, "c3", "edit_file", "same.txt"));

        var turns = await WorkspaceChanges.ReadAsync(dir.SessionFile);
        var distinct = turns[0].DistinctPaths;

        // 原始列表保留三次（它记录"改了几次"），去重后只一个。
        if (turns[0].Paths.Count != 3) return $"原始记录应保留 3 次，实际 {turns[0].Paths.Count}";
        return distinct.Count == 1 ? null : $"去重后仍有 {distinct.Count} 条";
    }

    /// <summary>两个写工具都要算。</summary>
    private static async Task<string?> BothWriteToolsAsync()
    {
        using var dir = new TempDir();
        dir.Session(
            UserInput(1, "写和改"),
            WriteCall(2, "c1", "write_file", "new.txt"),
            WriteCall(3, "c2", "edit_file", "old.txt"));

        var turns = await WorkspaceChanges.ReadAsync(dir.SessionFile);
        var paths = turns[0].DistinctPaths;

        return paths.Contains("new.txt", StringComparer.Ordinal)
            && paths.Contains("old.txt", StringComparer.Ordinal)
                ? null
                : $"写或改有一个没算进来：{string.Join(",", paths)}";
    }

    /// <summary>
    /// shell 命令的改动不在清单里——这是真实盲区。
    /// </summary>
    /// <remarks>
    /// <b>这条钉的是"不假装覆盖"</b>：命令是自由文本，看不出它动了什么文件。
    /// 检查它"不在清单里"是为了防止将来有人加个"解析 shell 命令里的路径"的
    /// 半吊子实现——那会给出假的完整性。
    /// </remarks>
    private static async Task<string?> OmitsShellAsync()
    {
        using var dir = new TempDir();
        dir.Session(
            UserInput(1, "用 shell 删个文件"),
            WriteCall(2, "c1", "shell", "rm secret.txt"));

        var turns = await WorkspaceChanges.ReadAsync(dir.SessionFile);
        return turns[0].DistinctPaths.Count == 0
            ? null
            : $"shell 被当成了可解析的写操作：{string.Join(",", turns[0].DistinctPaths)}";
    }

    /// <summary>最后一轮必须收尾——否则"刚改完就退出"的那次看不到。</summary>
    private static async Task<string?> KeepsFinalTurnAsync()
    {
        using var dir = new TempDir();
        dir.Session(
            UserInput(1, "只有一轮"),
            WriteCall(2, "c1", "write_file", "only.txt"));

        var turns = await WorkspaceChanges.ReadAsync(dir.SessionFile);
        return turns.Count == 1 && turns[0].DistinctPaths.Contains("only.txt", StringComparer.Ordinal)
            ? null
            : $"最后一轮丢了（{turns.Count} 轮）";
    }

    private static async Task<string?> CorruptTailAsync()
    {
        using var dir = new TempDir();
        dir.Session(
            UserInput(1, "正常内容"),
            WriteCall(2, "c1", "write_file", "ok.txt"),
            "{not valid json");

        var turns = await WorkspaceChanges.ReadAsync(dir.SessionFile);
        return turns.Count == 1 && turns[0].DistinctPaths.Contains("ok.txt", StringComparer.Ordinal)
            ? null
            : "坏行之前的内容没被读出";
    }

    /// <summary>旧格式的裸字符串用户输入也要认。</summary>
    private static async Task<string?> LegacyPayloadAsync()
    {
        using var dir = new TempDir();
        dir.Session(
            """{"Sequence":1,"Kind":"UserInput","Payload":"旧格式"}""",
            WriteCall(2, "c1", "write_file", "legacy.txt"));

        var turns = await WorkspaceChanges.ReadAsync(dir.SessionFile);
        return turns.Count == 1 && turns[0].Prompt == "旧格式"
            ? null
            : $"旧格式用户输入没读出来：'{turns.FirstOrDefault()?.Prompt}'";
    }

    // ── 支撑 ─────────────────────────────────────────────────────

    private static string UserInput(long seq, string text) =>
        "{\"Sequence\":" + seq + ",\"Kind\":\"UserInput\",\"Payload\":"
        + System.Text.Json.JsonSerializer.Serialize(text) + "}";

    private static string WriteCall(long seq, string id, string tool, string path) =>
        "{\"Sequence\":" + seq + ",\"Kind\":\"ToolCallStarted\",\"Payload\":{"
        + "\"id\":\"" + id + "\",\"tool\":\"" + tool + "\","
        + "\"arguments\":" + System.Text.Json.JsonSerializer.Serialize(
            System.Text.Json.JsonSerializer.Serialize(new { path })) + "}}";

    private static string ToolDone(long seq, string id) =>
        "{\"Sequence\":" + seq + ",\"Kind\":\"ToolCallCompleted\",\"Payload\":{"
        + "\"id\":\"" + id + "\",\"content\":\"ok\"}}";

    private sealed class TempDir : IDisposable
    {
        public TempDir()
        {
            Root = Path.Combine(Path.GetTempPath(), $"dougong-chg-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Root);
        }

        public string Root { get; }

        public string SessionFile => System.IO.Path.Combine(Root, "s.jsonl");

        public void Session(params string[] lines)
        {
            var builder = new StringBuilder();
            builder.AppendLine($"{SessionFormat.Prefix}{SessionFormat.Current}");
            foreach (var line in lines) builder.AppendLine(line.Trim());
            File.WriteAllText(SessionFile, builder.ToString());
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
