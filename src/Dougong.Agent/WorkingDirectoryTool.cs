using System.Text.Json;

namespace Dougong.Agent;

/// <summary>
/// 读取或切换会话的工作目录。
/// </summary>
/// <remarks>
/// <b>为什么是工具而不是一个斜杠命令</b>：需要它的是**模型**——它在读到
/// "这个仓库的 API 在 <c>src/api</c> 下"之后，应该自己换过去，而不是
/// 每次都写全路径。用户侧换目录是另一个入口（宿主自定），不经这个工具。
///
/// <para>
/// <b>形状照 dsh 的 <c>working_directory</c></b>：一个可选参数 <c>cd</c>。
/// 不传 = 读取当前目录；传了 = 换过去并返回新目录。用"可选参数"而不是
/// 两个工具（<c>get_cwd</c> / <c>change_cwd</c>）——读与写是同一个事实的两种
/// 动作，拆成两个会让模型在"我只是想确认一下"时多一次决策。
/// </para>
///
/// <para>
/// <b>它不改的东西（模型必须被告知）</b>：
/// <list type="bullet">
/// <item><b>已有的持久 shell 与后台作业</b>保持它们自己的目录。</item>
/// <item><b>写权限的边界</b>仍是原始项目。换到项目外之后相对路径操作会失败——
/// 那不是 bug，是"换目录不等于换权限"。</item>
/// </list>
/// 不说明这两点，模型会以为换目录等于"我现在能写任何地方"。
/// </para>
/// </remarks>
public sealed class WorkingDirectoryTool : IAgentTool
{
    /// <summary>工具 ID。</summary>
    public const string ToolId = "working_directory";

    private readonly WorkingDirectory _directory;
    private readonly IWorkingDirectoryStore? _store;
    private readonly LocalFileSystem? _files;
    private readonly LocalShellExecutor? _shell;

    /// <summary>
    /// 换目录时要同步资源键基准的文件工具。
    /// </summary>
    /// <remarks>
    /// <b>不传就会出真实缺陷</b>：换目录之后，文件工具按新目录读写，
    /// 而它们的互斥键仍按旧目录算——同一个文件得到两个键，
    /// 并发写不再互斥。
    /// </remarks>
    private readonly IReadOnlyList<IFileResourceKeyOwner> _keyOwners;

    /// <summary>构造。</summary>
    /// <param name="directory">会话目录状态。</param>
    /// <param name="store">变更存储；null 表示不持久化（仅测试与对比用）。</param>
    /// <param name="files">要跟着换根的文件系统；null 表示没有。</param>
    /// <param name="shell">要跟着换根的 shell 执行器；null 表示没有。</param>
    /// <param name="keyOwners">换目录时要同步资源键基准的文件工具。</param>
    public WorkingDirectoryTool(
        WorkingDirectory directory,
        IWorkingDirectoryStore? store = null,
        LocalFileSystem? files = null,
        LocalShellExecutor? shell = null,
        IReadOnlyList<IFileResourceKeyOwner>? keyOwners = null)
    {
        ArgumentNullException.ThrowIfNull(directory);
        _directory = directory;
        _store = store;
        _files = files;
        _shell = shell;
        _keyOwners = keyOwners ?? [];
    }

    public ToolDescriptor Descriptor { get; } = new(
        ToolId, ToolId, "1.0.0",
        "读取当前工作目录，或用 cd 切换。相对路径按当前目录解析。"
        + "已有的 shell 会话与后台作业保持它们自己的目录，不会被切换影响。",
        JsonDocument.Parse(
            """
            {
              "type": "object",
              "properties": {
                "cd": {
                  "type": "string",
                  "description": "要进入的已存在目录（绝对路径，或相对当前目录）。省略则只读取当前目录。"
                }
              },
              "additionalProperties": false
            }
            """).RootElement.Clone());

    /// <summary>不改任何外部状态：目录是会话内的执行上下文，不产生审批项。</summary>
    public ToolPermission DescribePermission(JsonElement arguments) =>
        new(ToolAccess.None, IsKnown: true, Paths: [], Commands: [], Domains: []);

    /// <summary>
    /// 独占执行。
    /// </summary>
    /// <remarks>
    /// <b>必须独占</b>：换目录会影响同批其它工具的相对路径解析。若并行，
    /// "read_file a.txt" 可能按切换前或切换后的目录解析——同一批调用得到
    /// 不确定的结果，那比慢一点糟得多。
    /// </remarks>
    public ToolExecutionPlan DescribeExecution(JsonElement arguments) =>
        new(ToolConcurrency.WorkspaceExclusive);

    public async ValueTask<AgentToolResult> ExecuteAsync(
        ToolInvocation invocation, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(invocation);

        var requested = invocation.Arguments.TryGetProperty("cd", out var cdElement)
            && cdElement.ValueKind is JsonValueKind.String
                ? cdElement.GetString()
                : null;

        // 只读：不换目录，直接返回当前值。
        if (string.IsNullOrWhiteSpace(requested))
            return Result(ok: true, error: null, _directory.Current, changed: false, _directory.Project);

        var previous = _directory.Current;
        if (!_directory.TrySet(requested, out var resolved, out var reason))
            return Result(ok: false, error: reason, previous, changed: false, _directory.Project);

        // 目录已换：**先落盘再改工具**。顺序不能反——先改工具再落盘失败的话，
        // 模型看到"成功"、重开会话后目录又回去了。那是静默的不一致。
        if (_store is not null)
        {
            try
            {
                await _store.AppendAsync(
                    new WorkingDirectoryChange(resolved, DateTimeOffset.UtcNow),
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 落盘失败就回退内存状态：不留"只在本次进程内有效"的目录。
                _directory.TrySet(previous, out _, out _);
                return Result(
                    ok: false,
                    error: $"目录变更未能保存：{ex.Message}",
                    previous, changed: false, _directory.Project);
            }
        }

        // 只影响之后的相对路径解析与之后启动的进程。
        _files?.UseDirectory(resolved);
        _shell?.UseDirectory(resolved);
        foreach (var owner in _keyOwners) owner.UseResourceBase(resolved);

        return Result(ok: true, error: null, resolved, changed: true, _directory.Project);
    }

    private static AgentToolResult Result(
        bool ok, string? error, string cwd, bool changed, string project)
    {
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            ok,
            error,
            cwd,
            project,
            changed,
            // 模型需要知道的三件事，写在结果里而不是只写在工具描述里：
            // 描述会被压缩/遗忘，工具结果刚发生，它一定会读到。
            note = "相对路径按 cwd 解析。已有的 shell 会话与后台作业保持它们自己的目录；"
                + "可写范围仍以 project 为界。",
        }));

        return new AgentToolResult(document.RootElement.Clone());
    }
}
