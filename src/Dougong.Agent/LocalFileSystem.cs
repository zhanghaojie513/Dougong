using System.Security.Cryptography;
using System.Text;

namespace Dougong.Agent;

/// <summary>
/// 本地文件系统实现：原子写 + 内容哈希版本守卫。
/// </summary>
/// <remarks>
/// <b>版本用内容哈希，不用 mtime</b>。mtime 的粒度在不同文件系统上差异很大
/// （FAT 2 秒、ext4 纳秒），两次快速修改可能得到相同 mtime，于是"内容变了但版本没变"。
/// 而内容哈希对"改了什么"是精确的。
/// <para>
/// 代价是每次 <see cref="StatAsync"/> 都要读整个文件。这在编码场景里可接受
/// （文件通常几十 KB）；若要优化，可以记 (length, mtime) 做缓存键，
/// 但那要先证明它是瓶颈——现在没证据。
/// </para>
/// </remarks>
/// <param name="workspace">
/// 工作区根。所有相对路径相对它解析。
/// <b>它是"会话的有效目录"，可以在会话中途变化</b>——所以构造后通过
/// <see cref="UseDirectory"/> 换根，而不是每次新建一个实现。
/// </param>
public sealed class LocalFileSystem(string workspace) : IFileSystem
{
    private string _root = Path.GetFullPath(workspace);

    /// <summary>工作区根的绝对路径。</summary>
    public string Root => _root;

    /// <summary>
    /// 换一个根目录（会话换目录时用）。
    /// </summary>
    /// <remarks>
    /// <b>为什么不是"新建一个 LocalFileSystem 换进去"</b>：工具持有的是这一个实例，
    /// dispatcher 已按实例注册。换实例要实现注册表层面的热替换，而这里要变的
    /// 只是一个字符串。dsh 也是同一个 Session 投影持有有效目录，不是重建工具。
    /// <para>
    /// <b>不做越界校验</b>：守卫的根仍是原始项目（见 <see cref="WorkingDirectory"/>），
    /// 换根只影响相对路径的起点。在这里再拦一次"必须在项目内"会让
    /// "去项目外的目录看东西"变得不可能，而那是这条能力的用途之一。
    /// </para>
    /// </remarks>
    /// <param name="directory">新的绝对根目录。</param>
    public void UseDirectory(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        _root = Path.GetFullPath(directory);
    }

    /// <inheritdoc />
    public async ValueTask<string> ReadTextAsync(
        string path, CancellationToken cancellationToken = default)
    {
        var full = Resolve(path);

        // 明确区分"不存在"与"读不了"：前者模型该换个路径，后者该报告环境问题。
        // File.ReadAllTextAsync 两种情况都抛 IOException 家族，但 FileNotFoundException
        // 是子类，可以区分。
        return await File.ReadAllTextAsync(full, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    /// <remarks>
    /// <b>只读一次盘，版本用同一份内容算</b>——这就是这个方法存在的理由。
    /// 实现里若改成"读一次 + 再 Stat 一次"，两次之间文件被改，
    /// 返回的内容与版本就不对应了（见接口注释里的实测）。
    /// </remarks>
    public async ValueTask<(string Content, string Version)> ReadTextWithVersionAsync(
        string path, CancellationToken cancellationToken = default)
    {
        var full = Resolve(path);
        var content = await File.ReadAllTextAsync(full, cancellationToken).ConfigureAwait(false);

        // 用 VersionOf(content) 而不是 HashAsync(full)：
        // 后者会**再开一次流读盘**，正是要避免的那第二次读。
        return (content, VersionOf(content));
    }

    /// <inheritdoc />
    public async ValueTask<FileStat?> StatAsync(
        string path, CancellationToken cancellationToken = default)
    {
        var full = Resolve(path);
        var info = new FileInfo(full);
        if (!info.Exists) return null;

        var version = await HashAsync(full, cancellationToken).ConfigureAwait(false);
        return new FileStat(full, info.Length, version);
    }

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<FileEntry>> ListDirectoryAsync(
        string path, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var full = Resolve(path);

        if (!Directory.Exists(full))
        {
            throw new DirectoryNotFoundException($"目录不存在：{full}");
        }

        // 顺序必须稳定：按名称排序。不排序时底层枚举顺序依赖文件系统，
        // 同一个目录两次列出的顺序可能不同，长任务里表现为"它刚才明明看到了"。
        var entries = new List<FileEntry>();
        foreach (var directory in Directory.EnumerateDirectories(full))
        {
            entries.Add(new FileEntry(Path.GetFileName(directory), true, 0));
        }

        foreach (var file in Directory.EnumerateFiles(full))
        {
            entries.Add(new FileEntry(
                Path.GetFileName(file), false, new FileInfo(file).Length));
        }

        entries.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.Ordinal));
        return ValueTask.FromResult<IReadOnlyList<FileEntry>>(entries);
    }

    /// <inheritdoc />
    public async ValueTask<FileWriteOutcome> WriteTextAsync(
        string path,
        string content,
        FileWriteExpectation? expected = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        var full = Resolve(path);

        // 先读现状：既用于校验期望，也用于给宿主返回 diff 的原料。
        // **必须在写之前读**：写完再读拿到的是新内容，那什么都证明不了。
        string? before = null;
        string? currentVersion = null;

        var info = new FileInfo(full);
        if (info.Exists)
        {
            before = await File.ReadAllTextAsync(full, cancellationToken).ConfigureAwait(false);
            currentVersion = VersionOf(before);
        }

        VerifyExpectation(full, expected, currentVersion);

        var operation = currentVersion is null
            ? FileWriteOperation.Created
            : FileWriteOperation.Replaced;

        await WriteAtomicAsync(full, content, cancellationToken).ConfigureAwait(false);

        return new FileWriteOutcome(operation, VersionOf(content), before);
    }

    /// <summary>校验期望前提；不符时抛冲突。</summary>
    /// <remarks>
    /// <b>消息要说清"该怎么办"</b>：模型看到"文件被改了"时，正确动作是重读。
    /// 只说"冲突"，它可能选择重试同一个写入——那注定还冲突。
    /// </remarks>
    private static void VerifyExpectation(
        string full, FileWriteExpectation? expected, string? currentVersion)
    {
        switch (expected)
        {
            case FileWriteExpectation.MustNotExist when currentVersion is not null:
                throw new FileVersionConflictException(
                    full, "要求创建新文件，但它已经存在。换一个路径，或先读取它再决定是否覆盖。");

            case FileWriteExpectation.MustMatchVersion mustMatch
                when currentVersion is null:
                throw new FileVersionConflictException(
                    full, "要求替换指定版本，但文件已不存在。重新读取确认它是否被删除了。");

            case FileWriteExpectation.MustMatchVersion mustMatch
                when !string.Equals(mustMatch.Version, currentVersion, StringComparison.Ordinal):
                throw new FileVersionConflictException(
                    full, "文件内容在读取之后被修改过。**重新读取**后再写入，不要重试同一个内容。");
        }
    }

    /// <summary>原子写入：同目录临时文件 + 改名。</summary>
    /// <remarks>
    /// <b>临时文件必须同目录</b>：跨目录 <c>File.Move</c> 在不同卷上会退化为
    /// 复制+删除，那就不是原子的了。
    /// <para>
    /// <b>崩溃时的行为</b>：临时文件残留，目标文件保持旧内容。前者是垃圾，
    /// 后者是正确性——宁可留下垃圾文件，也不要留下半个目标文件。
    /// </para>
    /// </remarks>
    private static async Task WriteAtomicAsync(
        string full, string content, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(full);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temp = full + ".tmp-" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            // 不 BOM：BOM 会被某些工具当成内容的一部分，也可能让 diff 显示异常。
            await File.WriteAllTextAsync(
                temp, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                cancellationToken).ConfigureAwait(false);

            File.Move(temp, full, overwrite: true);
        }
        catch
        {
            // 失败时清掉临时文件，不留下垃圾。清理失败不掩盖原异常。
            try { File.Delete(temp); } catch { /* 尽力而为 */ }
            throw;
        }
    }

    private async ValueTask<string> HashAsync(string full, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            full, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 0, useAsync: true);

        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash);
    }

    /// <summary>对内容算版本。与 <see cref="HashAsync"/> 用同一算法，两者可互比。</summary>
    private static string VersionOf(string content) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content)));

    /// <summary>把相对路径解析成绝对路径。</summary>
    /// <remarks>
    /// 越界检查由 <see cref="WorkspacePathGuard"/> 在 dispatcher 更早一层做。
    /// 这里仍做一次规范化：同一个文件的不同写法必须得到同一个版本与同一个临时文件目录。
    /// </remarks>
    private string Resolve(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return Path.IsPathRooted(path)
            ? Path.GetFullPath(path)
            : Path.GetFullPath(Path.Combine(_root, path));
    }
}
