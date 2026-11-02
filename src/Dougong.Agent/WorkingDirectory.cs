using System.Text;

namespace Dougong.Agent;

/// <summary>
/// 会话的有效工作目录。
/// </summary>
/// <remarks>
/// <b>解决什么问题</b>：模型在一个会话里需要换到子目录去干活（"接下来都在
/// <c>src/api</c> 里改"），而此前目录在装配期就固化进了文件系统与 shell 执行器——
/// 用户唯一能做的是退出重开一个会话。
///
/// <para>
/// <b>与 dsh 的语义对齐，三个区分必须保留</b>：
/// <list type="number">
/// <item><b>原始项目 vs 有效目录</b>。会话头里的工作区是**原始项目**，
/// 它决定路径守卫的边界——换目录不能把守卫的根也换掉，否则
/// <c>cd ..</c> 一次就出了沙箱。dsh 的原话是"沙箱可写根目录与项目归属
/// 继续绑定原始项目"。</item>
/// <item><b>新进程用新目录，已有进程保持自己的目录</b>。持久 shell 里
/// 已经跑着的 <c>cd</c> 状态不因为会话换目录而改变——那是两件事。
/// 这个 distinction 不做的话，"我在 shell 里 cd 到 A，模型说它 cd 到 B"
/// 会让人完全不知道当前在哪个目录。</item>
/// <item><b>变更必须能重新算出</b>。它记在会话日志里，不是一个进程内变量——
/// 否则恢复会话后模型看到的目录和它上次以为的不一样。</item>
/// </list>
/// </para>
///
/// <para>
/// <b>为什么用 <c>RecordSequence</c> 式的事件记录而不是新记录类型</b>：
/// 与 D-24 同一个约束——加 <see cref="StoreRecordKind"/> 的新值会让旧程序在
/// <c>Enum.Parse&lt;StoreRecordKind&gt;</c> 抛异常，整个会话打不开。
/// 目录变更复用已有的 <see cref="StoreRecordKind.UserInput"/> 之外的通道吗？
/// 不行：那样会污染对话历史（模型会看到一条凭空出现的"用户输入"）。
/// 因此目录变更与消息反馈一样，落到会话旁的伴生文件。
/// </para>
/// </remarks>
public sealed class WorkingDirectory
{
    /// <summary>伴生文件后缀（沿用反馈文件的命名约定）。</summary>
    public const string Suffix = ".cwd";

    /// <summary>原始项目：路径守卫与写权限的锚点，永不改变。</summary>
    public string Project { get; }

    /// <summary>当前有效目录。</summary>
    public string Current { get; private set; }

    /// <summary>构造。</summary>
    /// <param name="project">原始项目根；若 <paramref name="current"/> 为 null 则用它。</param>
    /// <param name="current">已记录的有效目录；null 表示还没有换过。</param>
    public WorkingDirectory(string project, string? current = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(project);

        Project = Path.GetFullPath(project);
        Current = current is null ? Project : Path.GetFullPath(current);
    }

    /// <summary>是否已经离开原始项目。</summary>
    public bool IsOutsideProject =>
        !string.Equals(Current, Project, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 尝试换到一个目录。
    /// </summary>
    /// <remarks>
    /// <b>只校验"存在且是目录"</b>，不校验"必须在项目内"——
    /// 与 dsh 一致：换目录是"去哪里干活"，不是"拿到更多权限"。
    /// 越界的代价由路径守卫承担（守卫的根仍是 <see cref="Project"/>），
    /// 所以离开项目之后相对路径操作会失败，而不是悄悄获得写权限。
    /// 这是本设计里最容易被做错的一处：先"忘记校验项目边界"不会报错，
    /// 它会安静地让模型写到项目的上级目录。
    /// </remarks>
    /// <param name="path">绝对路径，或相对当前目录的路径。</param>
    /// <param name="resolved">规范化后的绝对路径。</param>
    /// <param name="reason">失败原因。</param>
    public bool TrySet(string path, out string resolved, out string reason)
    {
        resolved = string.Empty;
        reason = string.Empty;

        if (string.IsNullOrWhiteSpace(path))
        {
            reason = "目录为空。";
            return false;
        }

        try
        {
            resolved = Path.IsPathRooted(path)
                ? Path.GetFullPath(path)
                : Path.GetFullPath(Path.Combine(Current, path));
        }
        catch (Exception ex)
        {
            reason = $"路径无法规范化：{ex.Message}";
            return false;
        }

        if (!Directory.Exists(resolved))
        {
            reason = $"目录不存在：{resolved}";
            return false;
        }

        Current = resolved;
        return true;
    }

    /// <summary>
    /// 目录已经不存在时的处置：退回原始项目。
    /// </summary>
    /// <remarks>
    /// <b>不是"报错让用户处理"</b>：目录消失（临时目录被清、分支被删）之后，
    /// 会话还能不能继续是个更重要的问题。dsh 的做法是 <c>ensure</c> 里
    /// "恢复原始项目"。退回原始项目同时保证守卫的根是有效的。
    /// <para>
    /// <b>原始项目也不存在时如实失败</b>：那时没有任何可靠目录可用，
    /// 假装退回没意义。
    /// </para>
    /// </remarks>
    /// <param name="restored">是否发生了回退。</param>
    /// <param name="reason">回退原因；未回退时为 null。</param>
    public bool TryEnsureExists(out bool restored, out string? reason)
    {
        restored = false;
        reason = null;

        if (Directory.Exists(Current)) return true;

        if (!Directory.Exists(Project))
        {
            reason = $"当前目录与原始项目都不存在：{Project}";
            return false;
        }

        reason = $"当前目录已不存在，已退回原始项目：{Current} → {Project}";
        Current = Project;
        restored = true;
        return true;
    }

    /// <summary>相对当前目录解析一个路径。</summary>
    public string Resolve(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return Path.IsPathRooted(path)
            ? Path.GetFullPath(path)
            : Path.GetFullPath(Path.Combine(Current, path));
    }

    /// <summary>给模型的上下文行。</summary>
    /// <remarks>
    /// <b>必须让模型知道它在哪</b>：换过目录之后，如果提示里不出现当前目录，
    /// 模型会按原始项目猜相对路径，然后连续几次"文件不存在"。
    /// <para>
    /// <b>进 user 角色而不是系统提示</b>：与时间上下文同一判据——它是
    /// 随会话变化的运行时状态，不是行为的纪律。dsh 也明确说
    /// "目录上下文绝不进入系统提示词"。
    /// </para>
    /// </remarks>
    public string DescribeForModel()
    {
        var builder = new StringBuilder("当前工作目录：").Append(Current);

        if (IsOutsideProject)
            builder.Append("\n（原始项目：").Append(Project)
                .Append("；相对路径按当前目录解析，但写权限仍以原始项目为界。）");

        return builder.ToString();
    }
}

/// <summary>目录变更的持久化记录。</summary>
/// <param name="Cwd">变更后的绝对目录。</param>
/// <param name="ChangedAt">变更时刻。</param>
public sealed record WorkingDirectoryChange(string Cwd, DateTimeOffset ChangedAt);

/// <summary>
/// 会话目录变更的存储。
/// </summary>
/// <remarks>
/// <b>与消息反馈同一形态</b>（见 <see cref="JsonlMessageFeedbackStore"/> 的说明）：
/// 只追加、伴生文件、后缀不与 <c>*.jsonl</c> 冲突。
/// 差别只是这里关心"最后一条"，不需要折叠。
/// </remarks>
public interface IWorkingDirectoryStore
{
    /// <summary>读取最后一条变更；从未变更时返回 null。</summary>
    Task<WorkingDirectoryChange?> ReadLastAsync(CancellationToken cancellationToken = default);

    /// <summary>追加一条变更。</summary>
    Task AppendAsync(WorkingDirectoryChange change, CancellationToken cancellationToken = default);
}

/// <summary>会话目录变更的 JSONL 实现。</summary>
public sealed class JsonlWorkingDirectoryStore : IWorkingDirectoryStore
{
    private readonly string _path;
    private readonly SemaphoreSlim _lock = new(1, 1);

    /// <summary>构造。</summary>
    /// <param name="path">伴生文件路径；由 <see cref="PathFor"/> 算出。</param>
    public JsonlWorkingDirectoryStore(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = Path.GetFullPath(path);
    }

    /// <summary>由会话文件路径算出伴生文件路径。</summary>
    /// <remarks>
    /// <b>规则只有这一份</b>：与会话文件名同源，改后缀只改这里。
    /// </remarks>
    public static string PathFor(string sessionLogPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionLogPath);
        return Path.GetFullPath(sessionLogPath) + WorkingDirectory.Suffix;
    }

    /// <summary>伴生文件路径。</summary>
    public string FilePath => _path;

    public async Task<WorkingDirectoryChange?> ReadLastAsync(
        CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(_path)) return null;

            WorkingDirectoryChange? last = null;
            await using var stream = new FileStream(
                _path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite,
                bufferSize: 4096, useAsync: true);
            using var reader = new StreamReader(
                stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false);

            while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
            {
                if (TryParse(line, out var change)) last = change;
            }

            // **坏行跳过而不是抛**：与反馈文件不同，这里只要最后一条有效记录。
            // 写到一半崩溃留下半行时，前一条仍然是对的——那比"整个会话打不开"好。
            return last;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task AppendAsync(
        WorkingDirectoryChange change, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(change);

        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var directory = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

            var bytes = Encoding.UTF8.GetBytes(Serialize(change) + "\n");
            await using var stream = new FileStream(
                _path, FileMode.Append, FileAccess.Write, FileShare.Read,
                bufferSize: 4096, useAsync: true);
            await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            stream.Flush(flushToDisk: true);
        }
        finally
        {
            _lock.Release();
        }
    }

    private static string Serialize(WorkingDirectoryChange change) =>
        System.Text.Json.JsonSerializer.Serialize(new
        {
            cwd = change.Cwd,
            changedAt = change.ChangedAt,
        });

    private static bool TryParse(string line, out WorkingDirectoryChange? change)
    {
        change = null;
        if (string.IsNullOrWhiteSpace(line)) return false;

        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(line);
            var root = document.RootElement;
            if (root.ValueKind is not System.Text.Json.JsonValueKind.Object) return false;

            if (!root.TryGetProperty("cwd", out var cwd)
                || cwd.ValueKind is not System.Text.Json.JsonValueKind.String)
                return false;

            var value = cwd.GetString();
            if (string.IsNullOrWhiteSpace(value)) return false;

            var at = root.TryGetProperty("changedAt", out var atElement)
                && atElement.ValueKind is System.Text.Json.JsonValueKind.String
                && atElement.TryGetDateTimeOffset(out var parsed)
                    ? parsed
                    : DateTimeOffset.MinValue;

            change = new WorkingDirectoryChange(value, at);
            return true;
        }
        catch (System.Text.Json.JsonException)
        {
            return false;
        }
    }
}
