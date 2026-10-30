namespace Dougong.Agent;

/// <summary>
/// 工作区文件能力的可替换来源。
/// </summary>
/// <remarks>
/// <b>为什么需要这一层，而不是让工具直接调 <c>File.*</c></b>：
/// <list type="bullet">
/// <item><b>沙箱</b>要在写入前拦截。工具直接调 <c>File.WriteAllText</c> 时，
/// 拦截点只能是调用方——而每个调用方都要记得拦，漏一个就是洞。</item>
/// <item><b>版本守卫</b>（读到的版本与写回时的版本一致）需要在同一处实现。
/// 分散在各工具里，总有一个工具忘了比对。</item>
/// <item><b>原子写</b>同理：临时文件 + 改名的细节不该在每个工具里重写。</item>
/// </list>
///
/// <para>
/// <b>与 dsh 的 <c>dsh-fs</c> 的差别</b>：它还有 <c>processPath</c>、
/// <c>fileUrl</c>、跨执行世界的路径映射、<c>watch</c>。那些服务于"沙箱进程里
/// 的路径与宿主不同"和"远程工作区"，我们目前没有这两种形态。
/// 按 <c>goal.md</c> 的判据——**不做没有消费者的架子**——这里只放
/// 四个 IO 方法加带期望的写。
/// </para>
///
/// <para>
/// <b>路径约定</b>：接口上的所有路径都是**相对于工作区根的相对路径**，
/// 或已解析的绝对路径。越界判定由 <see cref="WorkspacePathGuard"/> 在
/// dispatcher 更早一层做；实现层假定路径已可信，但仍应拒绝明显的跨根写入
/// （多一道防线不贵）。
/// </para>
/// </remarks>
public interface IFileSystem
{
    /// <summary>读取整份文本。文件不存在时抛 <see cref="FileNotFoundException"/>。</summary>
    ValueTask<string> ReadTextAsync(string path, CancellationToken cancellationToken = default);

    /// <summary>取一个文件的版本与尺寸；不存在时返回 null。</summary>
    /// <remarks>
    /// 版本是**不透明**的：调用方只能把它原样传回
    /// <see cref="WriteTextAsync"/>，不能自己解析。
    /// 本地实现用内容哈希，远程实现可能用 ETag——接口不该假定哪种。
    /// </remarks>
    ValueTask<FileStat?> StatAsync(string path, CancellationToken cancellationToken = default);

    /// <summary>列目录的直接子项。</summary>
    /// <remarks>
    /// **返回顺序稳定**：按名称排序。不稳定的顺序会让"模型看到什么"
    /// 每次会话都不同，长任务里表现为"它刚才明明看到了这个文件"。
    /// </remarks>
    ValueTask<IReadOnlyList<FileEntry>> ListDirectoryAsync(
        string path, CancellationToken cancellationToken = default);

    /// <summary>
    /// 写整份文本，可选带"期望"来发现冲突。
    /// </summary>
    /// <remarks>
    /// <b>必须原子</b>：先写同目录临时文件再改名。直接覆盖时若进程中途崩溃，
    /// 目标文件会变成半个文件——而它可能是用户唯一的一份数据。
    /// <para>
    /// <b>期望的意思</b>：调用方声明"我要求文件处于某个状态才写"。
    /// 状态不符时**抛 <see cref="FileVersionConflictException"/>，不静默覆盖**。
    /// 这是防"读 A 写 B"的唯一手段——模型读到第 100 行，期间用户手工改了文件，
    /// 不检查就会把用户的改动冲掉。
    /// </para>
    /// </remarks>
    ValueTask<FileWriteOutcome> WriteTextAsync(
        string path,
        string content,
        FileWriteExpectation? expected = null,
        CancellationToken cancellationToken = default);
}

/// <summary>文件版本与尺寸。版本是不透明值，由实现定义。</summary>
/// <param name="Path">绝对路径。</param>
/// <param name="Length">字节数。</param>
/// <param name="Version">
/// 不透明版本。本地实现是内容哈希；调用方只能原样传回，不能比较大小。
/// </param>
public sealed record FileStat(string Path, long Length, string Version);

/// <summary>目录里的一个直接子项。</summary>
/// <param name="Name">条目名（不含目录部分）。</param>
/// <param name="IsDirectory">是否为目录。</param>
/// <param name="Length">文件字节数；目录为 0。</param>
public sealed record FileEntry(string Name, bool IsDirectory, long Length);

/// <summary>
/// 写操作的期望前提。不符时抛 <see cref="FileVersionConflictException"/>。
/// </summary>
/// <remarks>
/// 两种形态是刻意分开的，不能合并成一个可空字段：
/// "这个文件不该存在"（创建）与"这个文件应该是某个版本"（替换）
/// 在语义上是排他的，而"不关心"是第三种状态（传 null）。
/// </remarks>
public abstract record FileWriteExpectation
{
    private FileWriteExpectation()
    {
    }

    /// <summary>要求文件当前不存在。已存在则冲突。</summary>
    public sealed record MustNotExist : FileWriteExpectation;

    /// <summary>要求文件当前是 <paramref name="Version"/> 那个版本。</summary>
    /// <param name="Version">之前 <see cref="IFileSystem.StatAsync"/> 或
    /// <see cref="IFileSystem.ReadTextAsync"/> 拿到的版本。</param>
    public sealed record MustMatchVersion(string Version) : FileWriteExpectation;
}

/// <summary>一次写操作的结果。</summary>
/// <param name="Operation">创建了新文件还是替换了已有文件。</param>
/// <param name="Version">写之后的版本。</param>
/// <param name="Before">
/// 写之前的完整内容；新建时为 null。
/// <b>这是 diff 的原料</b>：宿主做"改动前后对照"就靠它，不必自己再读一遍
/// （再读一遍会读到写之后的内容，或者撞上并发改动）。
/// </param>
public sealed record FileWriteOutcome(
    FileWriteOperation Operation,
    string Version,
    string? Before);

/// <summary>写操作是创建还是替换。</summary>
public enum FileWriteOperation
{
    /// <summary>文件原本不存在。</summary>
    Created,

    /// <summary>文件原本存在，被替换。</summary>
    Replaced,
}

/// <summary>写操作的期望前提与实际状态不符。</summary>
/// <remarks>
/// <b>必须与普通 IO 异常区分</b>：这个是可以告诉模型"文件被人改过了，重读一次"
/// 的正常情况；而权限不足、磁盘满那些是环境问题，重读也没用。
/// 合成一个异常类型会让模型反复重试注定失败的操作。
/// </remarks>
public sealed class FileVersionConflictException(string path, string reason)
    : IOException($"文件在读取之后被修改：{path}（{reason}）")
{
    /// <summary>冲突的文件路径。</summary>
    public string Path { get; } = path;
}
