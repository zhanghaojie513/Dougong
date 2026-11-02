using System.Text.Json;

namespace Dougong.Agent;

/// <summary>
/// 持久 shell 工具：同一会话里连续执行命令，状态跨调用保留。
/// </summary>
/// <remarks>
/// <b>与 <c>shell</c> 工具的区别只有一条：状态保留</b>。那个每次起新进程，
/// 于是"先 <c>cd src</c> 再 <c>npm test</c>"分两次调用就失效——第二次还在原目录。
/// 用户想的是"在这个目录里干活"，而模型只能把命令拼成一行。
///
/// <para>
/// <b>工具描述必须写清它不适合什么</b>：管道模式没有作业控制，
/// <c>vim</c>/<c>top</c>/<c>less</c> 这类交互式程序会挂住或行为异常，
/// <c>Ctrl+C</c> 语义也无法传递。不写清楚的话，模型会拿它去跑交互式命令，
/// 然后卡在超时上。
/// </para>
///
/// <para>
/// <b>工作区独占</b>：共享一个 stdin，两条命令并行会让输出交织，
/// 而那看起来像是"命令返回了别人的输出"。
/// </para>
/// </remarks>
public sealed class PersistentShellTool : IAgentTool
{
    private readonly PersistentShell _shell;

    /// <summary>构造。</summary>
    /// <param name="shell">持久 shell 实例；由装配层持有并随会话释放。</param>
    public PersistentShellTool(PersistentShell shell)
    {
        ArgumentNullException.ThrowIfNull(shell);
        _shell = shell;
    }

    public ToolDescriptor Descriptor { get; } = new(
        "shell_session", "shell_session", "1.0.0",
        "在持久 shell 会话里执行命令：工作目录与环境变量跨调用保留，"
        + "所以可以先 cd 再执行，不必拼成一行。"
        + "适合跑命令序列。**不适合交互式程序**（vim/top/less 会挂住），"
        + "也不支持 Ctrl+C。要跑独立命令用 shell，要后台跑用 job_start。",
        JsonDocument.Parse(
            """
            {
              "type": "object",
              "properties": {
                "command": {
                  "type": "string",
                  "description": "要执行的命令。可以依赖之前命令设置的目录与环境变量。"
                }
              },
              "required": ["command"],
              "additionalProperties": false
            }
            """).RootElement.Clone());

    /// <summary>与 shell 同级：执行命令。</summary>
    public ToolPermission DescribePermission(JsonElement arguments) =>
        new(ToolAccess.Execute, IsKnown: true,
            Paths: [],
            Commands: [arguments.TryGetProperty("command", out var c) ? c.GetString() ?? "" : ""],
            Domains: []);

    /// <summary>工作区独占：共享 stdin，并行会让输出交织。</summary>
    public ToolExecutionPlan DescribeExecution(JsonElement arguments) =>
        new(ToolConcurrency.WorkspaceExclusive);

    public IReadOnlyList<string> DescribeFileDependencies(JsonElement arguments) => [];

    public async ValueTask<AgentToolResult> ExecuteAsync(
        ToolInvocation invocation, CancellationToken cancellationToken)
    {
        var command = ToolArguments.RequiredString(invocation.Arguments, "command");
        var result = await _shell.RunAsync(command, cancellationToken).ConfigureAwait(false);

        // 当前目录也报回去：模型常常需要知道自己"现在在哪"，
        // 尤其在上一条命令是 cd 或某个脚本改了目录之后。
        string? cwd = null;
        if (result.ExitCode >= 0)
        {
            try { cwd = await _shell.GetCurrentDirectoryAsync(cancellationToken).ConfigureAwait(false); }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException) { /* 查不到就不报 */ }
        }

        return new AgentToolResult(JsonSerializer.SerializeToElement(new
        {
            exit_code = result.ExitCode,
            stdout = result.Stdout,
            timed_out = result.TimedOut,
            cwd,
        }));
    }
}
