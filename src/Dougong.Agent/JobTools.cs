using System.Text.Json;

namespace Dougong.Agent;

/// <summary>
/// 后台作业工具：启动、读取、停止、列出。
/// </summary>
/// <remarks>
/// <b>为什么是四个工具而不是一个带 mode 参数的</b>：模型的工具选择靠名字与描述，
/// 一个 <c>job(mode=read)</c> 会让它频繁选错 mode——而每次选错都是一整轮浪费。
/// 四个名字各自说清做什么，选错概率低得多。dsh 的 <c>dsh-tool-jobs</c>
/// 也是分动作的工具集。
///
/// <para>
/// <b>启动工具不经过审批，停/读/列也不</b>：作业的审批应当发生在
/// <b>启动</b>那一刻的命令上——但命令本身与 <c>shell</c> 同一类风险，
/// 所以 <see cref="JobStartTool"/> 报 <see cref="ToolAccess.Execute"/>
/// 与命令文本，由宿主的审批策略处理。
/// </para>
///
/// <para>
/// <b>模型必须知道作业不持久</b>：进程退出后作业就没了。这个事实写在
/// 工具描述里，因为模型会合理地假设"我启动的东西还在"——而那个假设
/// 在会话恢复后是错的。
/// </para>
/// </remarks>
public sealed class JobStartTool(JobRegistry jobs, string owner) : IAgentTool
{
    private readonly JobRegistry _jobs = jobs ?? throw new ArgumentNullException(nameof(jobs));

    public ToolDescriptor Descriptor { get; } = new(
        "job_start", "job_start", "1.0.0",
        "在后台启动一条命令并立即返回作业标识。"
        + "适合耗时的构建、测试、长时间脚本——它不会占住当前回合。"
        + "用 job_output 读取输出，job_stop 停止。"
        + "注意：作业只活在本次进程内，程序退出后不存在。",
        JsonDocument.Parse(
            """
            {
              "type": "object",
              "properties": {
                "command": {
                  "type": "string",
                  "description": "要执行的命令。执行时工作目录是会话工作区。"
                }
              },
              "required": ["command"],
              "additionalProperties": false
            }
            """).RootElement.Clone());

    /// <summary>与 <c>shell</c> 同级：都执行命令，都该被审批覆盖。</summary>
    public ToolPermission DescribePermission(JsonElement arguments) =>
        new(ToolAccess.Execute, IsKnown: true,
            Paths: [],
            Commands: [arguments.TryGetProperty("command", out var c) ? c.GetString() ?? "" : ""],
            Domains: []);

    /// <summary>启动动作本身没有共享资源，但作业之间互不影响。</summary>
    public ToolExecutionPlan DescribeExecution(JsonElement arguments) =>
        ToolExecutionPlan.Serial;

    public IReadOnlyList<string> DescribeFileDependencies(JsonElement arguments) => [];

    public ValueTask<AgentToolResult> ExecuteAsync(
        ToolInvocation invocation, CancellationToken cancellationToken)
    {
        var command = ToolArguments.RequiredString(invocation.Arguments, "command");
        var snapshot = _jobs.Start(owner, command);
        return ValueTask.FromResult(Json(snapshot));
    }

    internal static AgentToolResult Json(JobSnapshot snapshot) =>
        new(JsonSerializer.SerializeToElement(new
        {
            id = snapshot.Id,
            state = snapshot.State.ToString(),
            command = snapshot.Command,
            exit_code = snapshot.ExitCode,
            output = snapshot.Output,
            next_cursor = snapshot.NextCursor,
            omitted_bytes = snapshot.OmittedBytes,
            total_bytes = snapshot.TotalBytes,
            diagnostic = snapshot.Diagnostic,
        }));

    internal static AgentToolResult JsonList(IReadOnlyList<JobSnapshot> snapshots) =>
        new(JsonSerializer.SerializeToElement(new
        {
            jobs = snapshots.Select(s => new
            {
                id = s.Id,
                state = s.State.ToString(),
                command = s.Command,
                started_at = s.StartedAt,
                exit_code = s.ExitCode,
                total_bytes = s.TotalBytes,
            }),
        }));
}

/// <summary>读取后台作业的输出。</summary>
public sealed class JobOutputTool(JobRegistry jobs) : IAgentTool
{
    private readonly JobRegistry _jobs = jobs ?? throw new ArgumentNullException(nameof(jobs));

    public ToolDescriptor Descriptor { get; } = new(
        "job_output", "job_output", "1.0.0",
        "读取后台作业自上次游标以来的新输出。"
        + "把上次返回的 next_cursor 传回来只拿新增部分；不传会从头读（可能重复）。",
        JsonDocument.Parse(
            """
            {
              "type": "object",
              "properties": {
                "id": { "type": "string", "description": "job_start 返回的作业标识。" },
                "cursor": {
                  "type": "integer",
                  "description": "上次读取返回的 next_cursor。省略表示从头读。"
                }
              },
              "required": ["id"],
              "additionalProperties": false
            }
            """).RootElement.Clone());

    /// <summary>纯读取，没有副作用。</summary>
    public ToolPermission DescribePermission(JsonElement arguments) =>
        new(ToolAccess.None, IsKnown: true, [], [], []);

    public ToolExecutionPlan DescribeExecution(JsonElement arguments) =>
        ToolExecutionPlan.Serial;

    public IReadOnlyList<string> DescribeFileDependencies(JsonElement arguments) => [];

    public ValueTask<AgentToolResult> ExecuteAsync(
        ToolInvocation invocation, CancellationToken cancellationToken)
    {
        var id = ToolArguments.RequiredString(invocation.Arguments, "id");

        var cursor = invocation.Arguments.TryGetProperty("cursor", out var c)
            && c.TryGetInt64(out var parsed)
                ? parsed
                : 0;

        return ValueTask.FromResult(JobStartTool.Json(_jobs.Read(id, cursor)));
    }
}

/// <summary>停止后台作业。</summary>
public sealed class JobStopTool(JobRegistry jobs) : IAgentTool
{
    private readonly JobRegistry _jobs = jobs ?? throw new ArgumentNullException(nameof(jobs));

    public ToolDescriptor Descriptor { get; } = new(
        "job_stop", "job_stop", "1.0.0",
        "停止后台作业并结束它的整棵进程树。已产生的输出仍可用 job_output 读取。",
        JsonDocument.Parse(
            """
            {
              "type": "object",
              "properties": {
                "id": { "type": "string", "description": "要停止的作业标识。" }
              },
              "required": ["id"],
              "additionalProperties": false
            }
            """).RootElement.Clone());

    /// <summary>结束进程等同于影响系统状态，报 Execute。</summary>
    public ToolPermission DescribePermission(JsonElement arguments) =>
        new(ToolAccess.Execute, IsKnown: true, [], ["job_stop"], []);

    public ToolExecutionPlan DescribeExecution(JsonElement arguments) =>
        ToolExecutionPlan.Serial;

    public IReadOnlyList<string> DescribeFileDependencies(JsonElement arguments) => [];

    public ValueTask<AgentToolResult> ExecuteAsync(
        ToolInvocation invocation, CancellationToken cancellationToken)
    {
        var id = ToolArguments.RequiredString(invocation.Arguments, "id");
        return ValueTask.FromResult(JobStartTool.Json(_jobs.Stop(id)));
    }
}

/// <summary>列出后台作业。</summary>
public sealed class JobListTool(JobRegistry jobs, string owner) : IAgentTool
{
    private readonly JobRegistry _jobs = jobs ?? throw new ArgumentNullException(nameof(jobs));

    public ToolDescriptor Descriptor { get; } = new(
        "job_list", "job_list", "1.0.0",
        "列出本会话的后台作业（不含输出）。要看内容用 job_output。",
        JsonDocument.Parse(
            """{"type":"object","properties":{},"additionalProperties":false}""")
            .RootElement.Clone());

    public ToolPermission DescribePermission(JsonElement arguments) =>
        new(ToolAccess.None, IsKnown: true, [], [], []);

    public ToolExecutionPlan DescribeExecution(JsonElement arguments) =>
        ToolExecutionPlan.Serial;

    public IReadOnlyList<string> DescribeFileDependencies(JsonElement arguments) => [];

    public ValueTask<AgentToolResult> ExecuteAsync(
        ToolInvocation invocation, CancellationToken cancellationToken) =>
        ValueTask.FromResult(JobStartTool.JsonList(_jobs.List(owner)));
}
