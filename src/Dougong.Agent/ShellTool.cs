using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace Dougong.Agent;

/// <summary>
/// 在工作区内执行命令的 shell 工具。
/// </summary>
/// <remarks>
/// 设计依据（docs/architecture.md §4.3、§4.4）：
/// <list type="bullet">
/// <item>shell 按 <see cref="ToolAccess.Execute"/> 处理，取工作区独占锁——
/// 不能靠命令文本静态推断它改了什么</item>
/// <item>退出状态必须由进程明确报告，不从输出文本或提示符猜测</item>
/// <item>取消时结束整个进程树；已开始的副作用可能已发生，必须如实说明而不是报"已撤销"</item>
/// <item>输出有上限；截断要显式标记，不能让模型以为看到了全部输出</item>
/// </list>
/// <para>
/// **不宣称隔离**：命令可以读写进程权限内的一切，本类只保证它经过唯一 dispatcher、
/// 取到工作区独占锁、且结果如实上报。审批约束的是执行入口，不是沙箱。
/// </para>
/// <para>
/// 子进程默认使用最小环境变量集合（见 <see cref="ShellEnvironment"/>）：
/// 直接继承宿主环境会把 provider 密钥交给模型可以随意执行的命令。
/// </para>
/// </remarks>
public sealed class ShellTool : IAgentTool
{
    private readonly IShellExecutor _executor;
    private readonly TimeSpan _timeout;
    private readonly int _maxOutputBytes;

    /// <summary>
    /// 权限描述里要报的路径。
    /// </summary>
    /// <remarks>
    /// <b>单独存而不是问 executor</b>：执行 seam 不该暴露"工作区在哪"——
    /// 沙箱实现、远程实现可能根本没有本地路径。审批者要看到的是
    /// "这条命令会影响哪个目录"，那是工具层的信息。
    /// <para>
    /// <b>可为 null</b>：走不带 workspace 的构造时我们真的不知道根在哪。
    /// 那时不假装知道——用当前目录冒充会让守卫"看起来在工作"却拦错地方。
    /// </para>
    /// </remarks>
    private readonly string? _workspace;

    /// <summary>
    /// 命令策略闸门。默认就是一个普通实例，宿主可换更严的实现。
    /// </summary>
    /// <remarks>
    /// <b>为什么是字段而不是每次都 new</b>：策略要实现自己的状态时
    /// （比如记住上一轮 cd 到哪了），复用同一实例是必须的。
    /// 每次 new 会让状态丢失，而那正好是本策略最需要的上下文。
    /// </remarks>
    private readonly ShellCommandPolicy _policy;

    /// <summary>用本地实现构造（不沙箱）。</summary>
    /// <param name="workspace">工作目录。</param>
    /// <param name="timeout">超时；默认 120 秒。</param>
    /// <param name="maxOutputBytes">单流输出上限；默认 64 KiB。</param>
    public ShellTool(string workspace, TimeSpan? timeout = null, int maxOutputBytes = 64 * 1024)
        : this(new LocalShellExecutor(workspace), timeout, maxOutputBytes)
    {
        _workspace = Path.GetFullPath(workspace);
    }

    /// <summary>
    /// 用指定的执行器构造。
    /// </summary>
    /// <remarks>
    /// <b>换沙箱实现就换这一个参数</b>，工具本身不改。
    /// 这是把执行 seam 抽出来的全部理由——此前进程创建焊在这个类里，
    /// 加沙箱就要修改工具。
    /// </remarks>
    /// <param name="executor">命令执行来源。</param>
    /// <param name="timeout">超时。</param>
    /// <param name="maxOutputBytes">单流输出上限。</param>
    /// <remarks>
    /// <b>这个重载不知道工作区在哪</b>，因此 <c>cd</c> 越界检查无从判定：
    /// <see cref="WorkspacePathGuard"/> 需要一个基准根。保守做法是
    /// **不放行任何改变目录的命令**，而不是拿进程当前目录当根——
    /// 后者会"看起来在检查"却拦错地方。
    /// <para>
    /// 生产装配请用带 <c>workspace</c> 的重载。保留这个签名是为了不破坏
    /// 已有调用点（ApiCompat CP0002）。
    /// </para>
    /// </remarks>
    public ShellTool(IShellExecutor executor, TimeSpan? timeout = null, int maxOutputBytes = 64 * 1024)
        : this(executor, timeout, maxOutputBytes, workspace: null)
    {
    }

    /// <summary>
    /// 用指定的执行器与已知工作区构造。
    /// </summary>
    /// <param name="executor">命令执行来源。</param>
    /// <param name="timeout">超时。</param>
    /// <param name="maxOutputBytes">单流输出上限。</param>
    /// <param name="workspace">
    /// 工作区路径。<b>生产装配必须传</b>——命令策略用它判 <c>cd</c> 是否越界。
    /// 显式传而不是从环境推断：<c>Environment.CurrentDirectory</c> 是
    /// **进程启动时**的目录，与"这次会话的工作区"没有必然关系，
    /// 拿它当根会让守卫拦错地方。
    /// </param>
    public ShellTool(IShellExecutor executor, TimeSpan? timeout, int maxOutputBytes,
        string? workspace)
    {
        ArgumentNullException.ThrowIfNull(executor);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxOutputBytes, 1024);
        _executor = executor;
        _timeout = timeout ?? TimeSpan.FromSeconds(120);
        _maxOutputBytes = maxOutputBytes;
        // null 表示"工作区未知"，不是"用当前目录冒充"。
        _workspace = workspace is null ? null : Path.GetFullPath(workspace);
        _policy = new ShellCommandPolicy();
    }

    public ToolDescriptor Descriptor { get; } = new(
        "shell", "shell", "1.0.0",
        "在工作区内执行一条命令并返回退出码、标准输出与标准错误。",
        JsonDocument.Parse(
            """
            {
              "type": "object",
              "properties": {
                "command": { "type": "string", "description": "要执行的命令（经系统 shell 解释）" },
                "args": {
                  "type": "array",
                  "items": { "type": "string" },
                  "description": "可选：作为独立参数追加到命令后，不参与 shell 解释"
                }
              },
              "required": ["command"]
            }
            """).RootElement.Clone());

    public ToolPermission DescribePermission(JsonElement arguments)
    {
        // 无法枚举命令的影响范围，但可以如实告知将要执行什么：
        // 审批者看到的是具体命令行，而不是"某些命令"。
        var command = arguments.TryGetProperty("command", out var value)
            ? value.GetString() ?? string.Empty
            : string.Empty;

        return new ToolPermission(
            ToolAccess.Execute,
            IsKnown: true,
            // 工作区未知时不声明路径：声明一个假的会让审批者看到错误的范围。
            Paths: _workspace is null ? [] : [_workspace],
            Commands: [command],
            Domains: []);
    }

    /// <summary>始终工作区独占：shell 能改变进程与文件状态，不能与其他调用重叠。</summary>
    /// <remarks>
    /// <b>上限比内部超时宽一倍</b>：本工具自己有 <c>_timeout</c>（默认 120 秒），
    /// dispatcher 是对第三方实现的兜底。让它先于外层触发，是为了拿到
    /// 更精确的诊断——内部知道"是哪个进程超时"，外层只知道"这个工具超时"。
    /// <para>
    /// 若内层因某种原因没生效（例如子进程不响应 kill），外层这道仍会兜住，
    /// 终态报 <c>TimedOut</c> 而不是永远挂住。
    /// </para>
    /// </remarks>
    public ToolExecutionPlan DescribeExecution(JsonElement arguments) =>
        new(ToolConcurrency.WorkspaceExclusive)
        {
            Timeout = _timeout + TimeSpan.FromSeconds(30),
        };

    /// <summary>
    /// 不声明文件依赖：无法从命令文本可靠推断它读写哪些文件。
    /// 返回空表示"不做文件版本复核"，而不是"没有文件依赖"。
    /// </summary>
    public IReadOnlyList<string> DescribeFileDependencies(JsonElement arguments) => [];
    public async ValueTask<AgentToolResult> ExecuteAsync(
        ToolInvocation invocation, CancellationToken cancellationToken)
    {
        var command = invocation.Arguments.TryGetProperty("command", out var commandElement)
            ? commandElement.GetString()
            : null;
        if (string.IsNullOrWhiteSpace(command))
            throw new InvalidOperationException("缺少 command 参数。");

        var args = ReadArgs(invocation.Arguments);

        // args 作为独立参数追加：**不参与 shell 解释**。
        // 拼进去而不是交给 shell 分词，是因为模型给的参数可能含空格与引号，
        // 交给 shell 会变成命令注入。
        var fullCommand = args.Count == 0
            ? command
            : command + " " + string.Join(' ', args.Select(QuoteShellArgument));

        // 策略闸门在**执行前**，且判的是拼好的完整命令：
        // 危险模式可能藏在 args 里（模型把 "rm -rf /" 拆成 command+args 会更隐蔽）。
        //
        // 工作区未知时不传它：策略层会把"无法判定 cd 是否越界"当成拒绝，
        // 而不是放行。那比拿一个错误的根去猜安全。
        var verdict = _policy.Evaluate(fullCommand, _workspace!);
        if (!verdict.Allowed)
        {
            // 拒绝**返回失败结果而不是抛异常**：这是模型可自我纠正的常规情况
            // （换一条命令就行）。抛异常会让整轮以 Failed 结束。
            using var rejected = JsonDocument.Parse(JsonSerializer.Serialize(new
            {
                exitCode = (int?)null,
                stdout = string.Empty,
                stderr = string.Empty,
                rejected = true,
                detail = verdict.Reason,
            }));
            return new AgentToolResult(rejected.RootElement.Clone());
        }

        var result = await _executor
            .ExecuteAsync(fullCommand, _timeout, _maxOutputBytes, cancellationToken)
            .ConfigureAwait(false);

        using var document = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            exitCode = result.ExitCode,
            stdout = result.Stdout,
            stderr = result.Stderr,
            stdoutTruncated = result.StdoutTruncated,
            stderrTruncated = result.StderrTruncated,
        }));
        return new AgentToolResult(document.RootElement.Clone());
    }

    /// <summary>读可选的 args 数组。</summary>
    private static IReadOnlyList<string> ReadArgs(JsonElement arguments)
    {
        if (!arguments.TryGetProperty("args", out var args)
            || args.ValueKind is not JsonValueKind.Array)
            return [];

        var result = new List<string>();
        foreach (var item in args.EnumerateArray())
        {
            if (item.ValueKind is JsonValueKind.String && item.GetString() is { } value)
                result.Add(value);
        }

        return result;
    }

    /// <summary>
    /// 把一个参数按当前平台规则引用，使其作为单个 argv 项传给命令。
    /// </summary>
    /// <remarks>
    /// <b>为什么要自己引用而不是拼进命令</b>：模型给的参数可能含空格、引号、
    /// <c>&amp;&amp;</c> 或 <c>;</c>。直接拼进命令文本等于把 shell 语法交给模型，
    /// 而这里的前提恰恰是"参数不参与 shell 解释"。
    /// <para>
    /// cmd 没有统一转义规则，这里按最常见的双写方式处理，
    /// **不保证覆盖所有情形**——所以带复杂引号的场景应当直接写进 <c>command</c>，
    /// 由调用方自己决定怎么引用。
    /// </para>
    /// </remarks>
    private static string QuoteShellArgument(string argument)
    {
        if (OperatingSystem.IsWindows())
        {
            if (argument.Length > 0 && argument.IndexOfAny([' ', '	', '"']) < 0) return argument;
            return "\"" + argument.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
        }

        return "'" + argument.Replace("'", "'\''", StringComparison.Ordinal) + "'";
    }

}

/// <summary>
/// 子进程的环境变量集合。
/// </summary>
/// <remarks>
/// 刻意采用**白名单**而不是黑名单：黑名单永远漏（自定义变量名、凭据管理器的注入变量）。
/// 宿主自己的密钥（例如 <c>DOUGONG_PROVIDER_KEY</c>）绝不能出现在模型可以随意执行的
/// 命令环境里——那等于把审批与权限描述全部绕过。
/// </remarks>
public static class ShellEnvironment
{
    private static readonly string[] PreservedNames =
    [
        "PATH", "PATHEXT", "HOME", "USERPROFILE", "USERNAME", "USER",
        "SystemRoot", "SystemDrive", "windir", "TEMP", "TMP", "TMPDIR",
        "COMSPEC", "SHELL", "LANG", "LC_ALL", "TERM", "PWD",
    ];

    /// <summary>取当前进程里允许传给子进程的变量。</summary>
    public static IReadOnlyDictionary<string, string> Minimal()
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in PreservedNames)
        {
            var value = Environment.GetEnvironmentVariable(name);
            if (!string.IsNullOrEmpty(value)) result[name] = value;
        }

        return result;
    }

    /// <summary>判断某个变量名是否在允许传递的白名单里。</summary>
    public static bool IsPreserved(string name) =>
        PreservedNames.Any(preserved =>
            string.Equals(preserved, name, StringComparison.OrdinalIgnoreCase));
}
