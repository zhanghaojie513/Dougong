namespace Dougong.Agent;

/// <summary>
/// 命令执行的来源。可替换成沙箱实现、远程执行，或测试替身。
/// </summary>
/// <remarks>
/// <b>为什么需要这一层</b>：进程创建此前焊死在 <see cref="ShellTool"/> 里
/// （<c>System.Diagnostics.Process</c> 直接构造）。这带来两个后果：
/// <list type="bullet">
/// <item>换成沙箱执行要改工具本身——而沙箱是"怎么起进程"的问题，
/// 与"模型怎么调命令"无关，两者不该耦合。</item>
/// <item>测试里无法替换：跑一次就要真起进程，跑不了"命令被拒"这类分支。</item>
/// </list>
///
/// <para>
/// <b>与 dsh 的 <c>dsh-shell</c> 的对应</b>：它分的正是执行 seam 与工具
/// （<c>dsh-shell</c> vs <c>dsh-tool-bash</c>）。我们此前把两者合成一个类，
/// 而阶段 3 要加的沙箱必须从这一层进。
/// </para>
///
/// <para>
/// <b>接口只描述"起一个进程并等它结束"</b>，不包含：
/// <list type="bullet">
/// <item>命令白名单/黑名单——那是策略，由实现决定或不做</item>
/// <item>输出上限——那是工具的呈现约束</item>
/// <item>超时的语义——那是调用方的决定，接口只接受 <see cref="CancellationToken"/></item>
/// </list>
/// 把策略塞进 seam 会让"换实现"变成"换语义"，那不是替换。
/// </para>
/// </remarks>
public interface IShellExecutor
{
    /// <summary>
    /// 执行一条命令并等它结束。
    /// </summary>
    /// <remarks>
    /// <b>取消时必须结束整棵进程树</b>，不能只放弃等待：子进程继续跑而调用方
    /// 以为停了，下一次调用就会与"已取消"的命令重叠执行，两个写操作撞在一起。
    /// <para>
    /// <b>取消/超时可能已经产生副作用</b>：实现必须在异常消息里说明这一点，
    /// 不能让它看起来像"什么都没发生"。
    /// </para>
    /// </remarks>
    /// <param name="command">要执行的命令文本。</param>
    /// <param name="timeout">超时。到时必须结束进程树。</param>
    /// <param name="maxOutputBytes">单流输出上限；超出部分截断并标记。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <exception cref="TimeoutException">超时，进程树已被结束。</exception>
    /// <exception cref="OperationCanceledException">被取消，进程树已被结束。</exception>
    ValueTask<ShellExecutionResult> ExecuteAsync(
        string command,
        TimeSpan timeout,
        int maxOutputBytes,
        CancellationToken cancellationToken = default);
}

/// <summary>一次命令执行的结果。</summary>
/// <param name="ExitCode">退出码。</param>
/// <param name="Stdout">标准输出（可能被截断）。</param>
/// <param name="Stderr">标准错误（可能被截断）。</param>
/// <param name="StdoutTruncated">标准输出是否被截断。</param>
/// <param name="StderrTruncated">标准错误是否被截断。</param>
public sealed record ShellExecutionResult(
    int ExitCode,
    string Stdout,
    string Stderr,
    bool StdoutTruncated,
    bool StderrTruncated);
