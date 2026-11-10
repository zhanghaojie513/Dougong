using System.Text.Json;
using Dougong.Agent;

namespace Dougong.Agent.Comparison;

/// <summary>
/// 文件能力（seam + 工具）的验证。
/// </summary>
/// <remarks>
/// <b>重点不是"能读写"，而是三件容易写错的事</b>：
/// <list type="bullet">
/// <item>写必须是原子的——崩溃时不能留下半个目标文件。</item>
/// <item>版本守卫必须真的拦住"读 A 写 B"。</item>
/// <item>工具必须住在 SDK ——CLI 与桌面端共用同一份，否则行为必然分叉。</item>
/// </list>
/// </remarks>
public static class FileSystemChecks
{
    public static async Task<int> RunAsync()
    {
        var failures = 0;
        failures += await CheckAsync("read-write-roundtrip", RoundTripAsync);
        failures += await CheckAsync("write-returns-before-content", BeforeContentAsync);
        failures += await CheckAsync("create-vs-replace-is-distinguished", CreateVsReplaceAsync);
        failures += await CheckAsync("version-guard-blocks-stale-write", VersionGuardAsync);
        failures += await CheckAsync("read-returns-content-and-version-atomically", AtomicReadAsync);
        failures += await CheckAsync("read-file-tool-does-not-split-read-from-stat", ReadToolSingleReadAsync);
        failures += await CheckAsync("must-not-exist-blocks-overwrite", MustNotExistAsync);
        failures += await CheckAsync("directory-listing-is-stable", ListingStableAsync);
        failures += await CheckAsync("atomic-write-leaves-no-partial", NoPartialFileAsync);
        failures += await CheckAsync("file-tools-live-in-sdk", ToolsInSdkAsync);
        failures += await CheckAsync("shell-executor-is-replaceable", ExecutorIsReplaceableAsync);

        Console.WriteLine(failures == 0
            ? "FILESYSTEM: ALL PASS"
            : $"FILESYSTEM: {failures} FAILURE(S)");
        return failures;
    }

    /// <summary>基本读写往返。</summary>
    private static async Task<string?> RoundTripAsync()
    {
        using var dir = new TempDir();
        var files = new LocalFileSystem(dir.Path);

        await files.WriteTextAsync("a.txt", "hello 你好").ConfigureAwait(false);
        var content = await files.ReadTextAsync("a.txt").ConfigureAwait(false);

        return content == "hello 你好" ? null : $"读回的内容不对：{content}";
    }

    /// <summary>
    /// 写操作必须返回写之前的内容——那是 diff 的唯一原料。
    /// </summary>
    /// <remarks>
    /// 写完之后再读一次拿到的是新内容，所以这个值只能在写的那一刻捕获。
    /// 桌面端要做"改动前后对照"，缺了它就只能自己再读，而那时已经读不到旧版了。
    /// </remarks>
    private static async Task<string?> BeforeContentAsync()
    {
        using var dir = new TempDir();
        var files = new LocalFileSystem(dir.Path);

        await files.WriteTextAsync("a.txt", "old").ConfigureAwait(false);
        var outcome = await files.WriteTextAsync("a.txt", "new").ConfigureAwait(false);

        if (outcome.Before != "old")
            return $"Before 应为 \"old\"，实际 {outcome.Before ?? "(null)"}";

        // 新建时 Before 必须是 null，不能是空串——空串会被当成"原来是个空文件"。
        var created = await files.WriteTextAsync("b.txt", "fresh").ConfigureAwait(false);
        if (created.Before is not null)
            return $"新建文件的 Before 应为 null，实际 \"{created.Before}\"";

        return null;
    }

    /// <summary>创建与替换必须可区分：调用方据此决定要不要提示"覆盖了已有文件"。</summary>
    private static async Task<string?> CreateVsReplaceAsync()
    {
        using var dir = new TempDir();
        var files = new LocalFileSystem(dir.Path);

        var first = await files.WriteTextAsync("a.txt", "1").ConfigureAwait(false);
        if (first.Operation != FileWriteOperation.Created)
            return $"首次写应为 Created，实际 {first.Operation}";

        var second = await files.WriteTextAsync("a.txt", "2").ConfigureAwait(false);
        if (second.Operation != FileWriteOperation.Replaced)
            return $"覆盖写应为 Replaced，实际 {second.Operation}";

        return null;
    }

    /// <summary>
    /// 版本守卫必须拦住"读 A 写 B"。
    /// </summary>
    /// <remarks>
    /// <b>这是最容易写错也最要紧的一条</b>：模型读到文件，期间用户手工改了，
    /// 不检查就写回会把用户的改动冲掉，而模型完全不知道自己干了这件事。
    /// </remarks>
    private static async Task<string?> VersionGuardAsync()
    {
        using var dir = new TempDir();
        var files = new LocalFileSystem(dir.Path);

        await files.WriteTextAsync("a.txt", "v1").ConfigureAwait(false);
        var stat = await files.StatAsync("a.txt").ConfigureAwait(false);
        if (stat is null) return "StatAsync 返回 null";

        // 别人改了文件。
        await files.WriteTextAsync("a.txt", "someone else").ConfigureAwait(false);

        // 用旧版本号写：必须被拒。
        try
        {
            await files.WriteTextAsync(
                "a.txt", "my edit",
                new FileWriteExpectation.MustMatchVersion(stat.Version)).ConfigureAwait(false);

            return "用过期版本写入竟然成功了——用户的改动会被静默冲掉";
        }
        catch (FileVersionConflictException)
        {
            // 预期内。
        }

        // 内容必须还是别人写的那份。
        var after = await files.ReadTextAsync("a.txt").ConfigureAwait(false);
        if (after != "someone else")
            return $"冲突后文件内容被改动了：{after}";

        return null;
    }

    /// <summary>"要求不存在"必须拦住覆盖已有文件。</summary>
    private static async Task<string?> MustNotExistAsync()
    {
        using var dir = new TempDir();
        var files = new LocalFileSystem(dir.Path);

        await files.WriteTextAsync("a.txt", "exists").ConfigureAwait(false);

        try
        {
            await files.WriteTextAsync(
                "a.txt", "new", new FileWriteExpectation.MustNotExist()).ConfigureAwait(false);

            return "要求创建新文件时，已存在的文件被覆盖了";
        }
        catch (FileVersionConflictException)
        {
            return null;
        }
    }

    /// <summary>目录列举顺序必须稳定——否则"模型刚才看到的"下次就变了。</summary>
    private static async Task<string?> ListingStableAsync()
    {
        using var dir = new TempDir();
        var files = new LocalFileSystem(dir.Path);

        foreach (var name in new[] { "c.txt", "a.txt", "b.txt" })
            await files.WriteTextAsync(name, "x").ConfigureAwait(false);

        var first = await files.ListDirectoryAsync(".").ConfigureAwait(false);
        var second = await files.ListDirectoryAsync(".").ConfigureAwait(false);

        var firstNames = string.Join(",", first.Select(e => e.Name));
        var secondNames = string.Join(",", second.Select(e => e.Name));

        if (firstNames != secondNames)
            return $"两次列举顺序不同：{firstNames} vs {secondNames}";

        if (firstNames != "a.txt,b.txt,c.txt")
            return $"应按名称排序，实际 {firstNames}";

        return null;
    }

    /// <summary>
    /// 原子写失败时不能留下半个目标文件。
    /// </summary>
    /// <remarks>
    /// 无法在测试里制造"写一半崩溃"，退而验证它的可观察前提：
    /// <b>目标文件在写完之后不残留任何临时文件</b>。
    /// 临时文件残留说明走的是"先写临时再改名"，而不是直接覆盖——
    /// 那正是原子性的结构证据。
    /// </remarks>
    private static async Task<string?> NoPartialFileAsync()
    {
        using var dir = new TempDir();
        var files = new LocalFileSystem(dir.Path);

        await files.WriteTextAsync("a.txt", "content").ConfigureAwait(false);

        var leftovers = Directory.GetFiles(dir.Path, "*.tmp-*");
        if (leftovers.Length > 0)
            return $"写完之后残留临时文件：{string.Join(",", leftovers.Select(Path.GetFileName))}";

        // 目标文件必须是完整内容。
        var content = await files.ReadTextAsync("a.txt").ConfigureAwait(false);
        return content == "content" ? null : $"写入内容不完整：{content}";
    }

    /// <summary>
    /// 文件工具必须住在 SDK 程序集里。
    /// </summary>
    /// <remarks>
    /// <b>这条防的是"又搬回去"</b>：此前它们定义在 <c>Dougong.Cli</c>，
    /// 桌面端只能重写一遍，行为必然分叉。类型住在哪个程序集是可断言的，
    /// 而"两个宿主的工具行为一致"不能靠人盯。
    /// </remarks>
    private static Task<string?> ToolsInSdkAsync()
    {
        var read = typeof(ReadFileTool).Assembly.GetName().Name;
        var write = typeof(WriteFileTool).Assembly.GetName().Name;
        var seam = typeof(IFileSystem).Assembly.GetName().Name;

        const string expected = "Dougong.Agent";

        if (read != expected)
            return Task.FromResult<string?>($"ReadFileTool 住在 {read}，应在 {expected}");

        if (write != expected)
            return Task.FromResult<string?>($"WriteFileTool 住在 {write}，应在 {expected}");

        if (seam != expected)
            return Task.FromResult<string?>($"IFileSystem 住在 {seam}，应在 {expected}");

        return Task.FromResult<string?>(null);
    }

    /// <summary>
    /// 命令执行必须是可替换的 seam，而不是焊在工具里的进程创建。
    /// </summary>
    /// <remarks>
    /// <b>为什么这条值得测</b>：此前 <see cref="ShellTool"/> 里直接
    /// <c>new Process</c>，加沙箱就要改工具本身——而沙箱是"怎么起进程"，
    /// 与"模型怎么调命令"无关。阶段 3 要加的沙箱必须从这一层进。
    /// <para>
    /// 更直接的好处：有了 seam，测试能替换执行器，不必真起进程。
    /// 这条用例本身就是证据——它跑一次真实命令都不需要。
    /// </para>
    /// </remarks>
    private static async Task<string?> ExecutorIsReplaceableAsync()
    {
        var executor = new RecordingExecutor();
        var tool = new ShellTool(executor, timeout: TimeSpan.FromSeconds(5));

        using var document = JsonDocument.Parse(
            """{"command":"echo","args":["a b","c"]}""");

        var result = await tool.ExecuteAsync(
            new ToolInvocation("i1", "shell", document.RootElement.Clone()),
            CancellationToken.None).ConfigureAwait(false);

        if (executor.Calls.Count != 1)
            return $"执行器应被调用 1 次，实际 {executor.Calls.Count} 次";

        var command = executor.Calls[0];

        // 参数必须作为独立 argv 项被引用，不能原样拼进命令文本——
        // 那等于把 shell 语法交给模型。
        //
        // **引用符随平台不同**：cmd 用双引号，POSIX 用单引号。
        // 只断言其中一种会让这条用例在另一个平台上失败，而失败原因与
        // 它要防的问题无关。
        var quoted = command.Contains("\"a b\"", StringComparison.Ordinal)
            || command.Contains("'a b'", StringComparison.Ordinal);

        if (!quoted)
            return $"含空格的参数没有被引用：{command}";

        if (!command.Contains("echo", StringComparison.Ordinal))
            return $"命令丢失：{command}";

        // 工具必须把执行结果原样返回，不做二次解释。
        if (result.Content.GetRawText().Contains("stub-stdout", StringComparison.Ordinal) is false)
            return $"执行器输出没有传给模型：{result.Content.GetRawText()}";

        return null;
    }

    /// <summary>记录调用的替身执行器，不真起进程。</summary>
    private sealed class RecordingExecutor : IShellExecutor
    {
        public List<string> Calls { get; } = [];

        public ValueTask<ShellExecutionResult> ExecuteAsync(
            string command, TimeSpan timeout, int maxOutputBytes,
            CancellationToken cancellationToken = default)
        {
            Calls.Add(command);
            return ValueTask.FromResult(
                new ShellExecutionResult(0, "stub-stdout", "", false, false));
        }
    }

    // ── 支撑 ─────────────────────────────────────────────────────

    /// <summary>一次性临时目录，退出时删除。</summary>
    /// <summary>
    /// **读取内容与版本必须来自同一次读盘。**
    /// </summary>
    /// <remarks>
    /// <b>这条抓的是一个让版本守卫失效的真实缺陷</b>（2026-11-10 实测）：
    /// <c>read_file</c> 原先用 <c>ReadTextAsync</c> + 单独的 <c>StatAsync</c>，
    /// 两次独立读盘。实测复现：读到"版本A"的内容后文件被改成"版本B"，
    /// <c>StatAsync</c> 返回 B 的版本；模型拿 A 的内容配 B 的版本去写，
    /// 守卫判定通过，**静默覆盖别人的改动**。
    ///
    /// <para>
    /// <b>为何不靠竞态去验</b>：时序窗口极短，测试里插不进改动，
    /// 那种"看起来通过"的检查证明不了任何东西（实测过一次：把实现改回两步读，
    /// 检查仍然绿）。所以判据改成**结构性的**——看实现里有没有第二次读。
    /// </para>
    ///
    /// <para>
    /// <b>同时保留一个正例</b>：正常路径下内容与版本必须自洽（版本能用于写入）。
    /// 那条是行为验证，保证方法本身可用。
    /// </para>
    /// </remarks>
    private static async Task<string?> AtomicReadAsync()
    {
        using var dir = new TempDir();
        var files = new LocalFileSystem(dir.Path);

        await files.WriteTextAsync("a.txt", "版本A").ConfigureAwait(false);

        var (content, version) = await files
            .ReadTextWithVersionAsync("a.txt").ConfigureAwait(false);

        if (content != "版本A") return $"读到的内容不对：{content}";

        // 正例：拿到的版本必须能用于写入（自洽）。
        var outcome = await files.WriteTextAsync(
            "a.txt", "基于A修改", new FileWriteExpectation.MustMatchVersion(version))
            .ConfigureAwait(false);

        if (outcome.Before != "版本A")
            return $"写之前的内容不对：{outcome.Before}";

        // **结构性判据**：本地实现不得在读取之后另调 StatAsync。
        var root = RepoRoot();
        if (root is null) return "找不到仓库根";

        var impl = Path.Combine(root, "src", "Dougong.Agent", "LocalFileSystem.cs");
        if (!File.Exists(impl)) return "LocalFileSystem.cs 不存在";

        var text = File.ReadAllText(impl);
        var start = text.IndexOf("ReadTextWithVersionAsync", StringComparison.Ordinal);
        if (start < 0) return "找不到 ReadTextWithVersionAsync 的实现";

        // **必须按方法边界切，不能按固定长度切**：固定窗口会越界截到
        // 下一个方法的签名（`StatAsync`），于是把"相邻的方法"误报成
        // "方法内部的第二次读"。第一版就是 900 字符窗口，假红过一次。
        //
        // 方法边界取"="花括号深度归零"那一刻：从签名后的第一个 `{` 数起。
        var braceStart = text.IndexOf('{', start);
        if (braceStart < 0) return "找不到方法体";

        var depth = 0;
        var braceEnd = -1;
        for (var i = braceStart; i < text.Length; i++)
        {
            if (text[i] == '{') depth++;
            else if (text[i] == '}')
            {
                depth--;
                if (depth == 0) { braceEnd = i; break; }
            }
        }

        if (braceEnd < 0) return "方法体括号不配对";

        var body = text[braceStart..braceEnd];

        // 注释里的 StatAsync 不算（这段代码的注释就提到了它）。
        var codeOnly = string.Join('\n', body
            .Split('\n')
            .Select(l => l.Trim())
            .Where(l => !l.StartsWith("//", StringComparison.Ordinal)));

        return codeOnly.Contains("StatAsync", StringComparison.Ordinal)
            ? "LocalFileSystem 的 ReadTextWithVersionAsync 里调了 StatAsync——"
                + "那就是第二次读盘，竞态窗口还在"
            : null;
    }

    /// <summary>
    /// **`read_file` 工具不能自己拆成两步读。**
    /// </summary>
    /// <remarks>
    /// 上一条验接口实现；这条验**工具有没有用它**。
    /// 接口给了原子方法而工具仍写两步，缺陷原样存在——
    /// 那种写法编译通过、行为“看起来正常”，只在竞态时出错。
    /// </remarks>
    private static Task<string?> ReadToolSingleReadAsync()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "Dougong.slnx")))
            dir = Path.GetDirectoryName(dir);

        if (dir is null) return Task.FromResult<string?>("找不到仓库根");

        var file = Path.Combine(dir, "src", "Dougong.Agent", "FileTools.cs");
        if (!File.Exists(file)) return Task.FromResult<string?>("FileTools.cs 不存在");

        var text = File.ReadAllText(file);
        var start = text.IndexOf("public sealed class ReadFileTool", StringComparison.Ordinal);
        if (start < 0) return Task.FromResult<string?>("找不到 ReadFileTool");

        var end = text.IndexOf("public sealed class WriteFileTool", start, StringComparison.Ordinal);
        var segment = end > start ? text[start..end] : text[start..];

        if (!segment.Contains("ReadTextWithVersionAsync", StringComparison.Ordinal))
            return Task.FromResult<string?>(
                "ReadFileTool 没用 ReadTextWithVersionAsync——内容与版本来自两次读盘");

        // **必须排除注释行**：这段代码的注释里就写着 "StatAsync"（说明历史变迁），
        // 按裸文本匹配会把那条注释当成真实调用——第一版就是这么假红的。
        var callsStat = segment
            .Split('\n')
            .Select(l => l.Trim())
            .Where(l => !l.StartsWith("//", StringComparison.Ordinal))
            .Any(l => l.Contains("StatAsync", StringComparison.Ordinal));

        return Task.FromResult<string?>(callsStat
            ? "ReadFileTool 里仍有独立的 StatAsync 调用——那正是要消除的第二次读"
            : null);
    }

    /// <summary>向上找含 Dougong.slnx 的目录。</summary>
    private static string? RepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "Dougong.slnx")))
            dir = Path.GetDirectoryName(dir);
        return dir;
    }

    private sealed class TempDir : IDisposable
    {
        public TempDir()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), $"dougong-fs-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); }
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
