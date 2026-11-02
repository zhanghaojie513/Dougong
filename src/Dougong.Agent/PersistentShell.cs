using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;

namespace Dougong.Agent;

/// <summary>
/// 持久 shell：同一个 shell 会话里连续执行命令，状态（<c>cd</c>、<c>export</c>）跨调用保留。
/// </summary>
/// <remarks>
/// <b>解决什么问题</b>：现在的 <c>shell</c> 工具每次调用起一个新进程。于是
/// <c>cd src &amp;&amp; npm test</c> 能work，但"先 cd 再测"分两次调用就失效——
/// 第二次还在原目录。用户想的是"在这个目录里干活"，而模型只能把命令拼成一行，
/// 既脆弱又难读。
///
/// <para>
/// <b>这是"持久 shell"，不是 PTY——两者别混</b>：
/// <list type="bullet">
/// <item><b>模型要的是状态保留</b>，不是终端渲染。它不需要光标、不需要
/// ANSI 屏幕控制、不需要 IME。</item>
/// <item><b>PTY 是给终端 UI 的</b>：桌面端那个标签页要跑 vim、top 这类
/// 全屏程序，那必须有真终端。而它由宿主的终端组件负责（桌面端已确认
/// <c>TerminalView</c> 可用，只差进程侧）。</item>
/// <item><b>不用 PTY 让跨平台一致</b>：ConPTY 与 forkpty 是两套完全不同的
/// API，行为也有差异（信号、窗口大小、回显）。用普通管道两边同一条代码路径。</item>
/// </list>
/// </para>
///
/// <para>
/// <b>代价必须说清</b>：管道模式没有作业控制——
/// <list type="bullet">
/// <item><c>Ctrl+C</c> 语义无法传递（我们不发信号）</item>
/// <item>交互式程序（<c>vim</c>、<c>top</c>、<c>less</c>）会挂住或行为异常</item>
/// <item>没有 TTY 时部分工具会切到非交互模式（这通常是好事，但不总是）</item>
/// </list>
/// 所以它只适合"跑命令序列"，不适合"操作终端"。工具描述里要写明。
/// </para>
///
/// <para>
/// <b>怎么知道一条命令结束了</b>：这是管道模式下最难的一点。做法是在每条
/// 命令后追加一个哨兵（打印一个随机标记 + 退出码），读到标记就认为这条结束。
/// dsh 用 PTY 的回显与提示符探测；我们没有提示符，所以用显式哨兵——
/// 它不依赖 shell 配置，比解析提示符可靠。
/// </para>
/// </remarks>
public sealed class PersistentShell : IDisposable
{
    /// <summary>默认单条命令的超时。</summary>
    /// <remarks>
    /// 比一次性 <c>shell</c> 宽松：持久会话里的命令往往是长序列中的一环。
    /// </remarks>
    public static readonly TimeSpan DefaultCommandTimeout = TimeSpan.FromMinutes(2);

    /// <summary>输出保留上限。</summary>
    /// <remarks>
    /// 与 <see cref="JobRegistry"/> 同一取向量级，但更小：持久 shell 是
    /// "边跑边看"的形态，模型会频繁读取，不像作业那样可能一次产出很久。
    /// </remarks>
    public const int DefaultRetainBytes = 128 * 1024;

    private readonly string _workspace;
    private readonly TimeSpan _commandTimeout;
    private readonly int _retainBytes;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly StringBuilder _pending = new();
    private readonly object _sync = new();

    private Process? _process;
    private StreamWriter? _stdin;
    private bool _disposed;

    /// <summary>构造。</summary>
    /// <param name="workspace">shell 的起始目录。</param>
    /// <param name="commandTimeout">单条命令超时。</param>
    /// <param name="retainBytes">输出保留上限。</param>
    public PersistentShell(
        string workspace,
        TimeSpan? commandTimeout = null,
        int retainBytes = DefaultRetainBytes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspace);
        _workspace = Path.GetFullPath(workspace);
        _commandTimeout = commandTimeout ?? DefaultCommandTimeout;
        _retainBytes = retainBytes;
    }

    /// <summary>shell 是否已启动。</summary>
    public bool IsRunning => _process is { HasExited: false };

    /// <summary>当前工作目录（从 shell 里查一次）。</summary>
    /// <remarks>
    /// <b>不能只报初始目录</b>：用户执行过 <c>cd</c> 之后，两者不同。
    /// 报错的目录会让模型以为 <c>cd</c> 没生效。
    /// </remarks>
    public async Task<string> GetCurrentDirectoryAsync(CancellationToken cancellationToken = default)
    {
        var result = await RunAsync(
            OperatingSystem.IsWindows() ? "cd" : "pwd", cancellationToken).ConfigureAwait(false);

        return result.Stdout.Trim() is { Length: > 0 } dir ? dir : _workspace;
    }

    /// <summary>
    /// 在持久 shell 里执行一条命令。
    /// </summary>
    /// <param name="command">命令文本。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <remarks>
    /// <b>同一时刻只允许一条命令</b>：共享一个 stdin，两条命令的输出会交织，
    /// 而哨兵会把对方的输出当成自己的一部分。用信号量串行化。
    /// <para>
    /// <b>超时后 shell 状态不可信</b>：命令可能还在跑，而我们已经读到别的东西了。
    /// 这时如实报告并把 shell 标记为需要重启，而不是继续用。
    /// </para>
    /// </remarks>
    public async Task<ShellRunResult> RunAsync(
        string command, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command);
        ObjectDisposedException.ThrowIf(_disposed, this);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!EnsureStarted()) return ShellRunResult.Failed("shell 启动失败。");

            // 哨兵：随机串避免与命令输出撞车；带退出码让调用方能区分成败。
            var marker = $"__DOUGONG_{Guid.NewGuid():N}__";

            // **这里不清缓冲**：启动阶段的横幅已被 WaitForBarrier 截断，
            // 而在此 Clear 会与 Drain 线程竞态——它可能在 Clear 之后
            // 才把已经读到的字节追加进来。

            // **通知用 `&&`/`&` 串联，必须分两行发**。实测踩到两层坑：
            //
            // ① cmd 的 `%errorlevel%` 是**解析期**展开：写
            //    `dir x & echo %errorlevel%` 拿到执行**前**的值（失败命令报 0）。
            //    必须开 /V:ON 改用 `!errorlevel!`。
            // ② 即使换成延迟展开，写成 `cmd & echo !errorlevel!` 仍不可靠——
            //    同一行里 `&` 两侧是同一个解析单元。分成两行发才稳定：
            //    第一行是命令本身，第二行才读 errorlevel。
            //
            // sh 侧同理：`$?` 要单独一行才拿到上一条的退出码。
            var exitProbe = OperatingSystem.IsWindows()
                ? $"echo {marker}:!errorlevel!"
                : $"echo {marker}:$?";

            try
            {
                await _stdin!.WriteLineAsync(command.AsMemory(), cancellationToken)
                    .ConfigureAwait(false);
                await _stdin.FlushAsync(cancellationToken).ConfigureAwait(false);
                await _stdin.WriteLineAsync(exitProbe.AsMemory(), cancellationToken)
                    .ConfigureAwait(false);
                await _stdin.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                return ShellRunResult.Failed($"写入 shell 失败：{ex.Message}");
            }

            return await WaitForMarkerAsync(marker, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>等到哨兵出现，返回它之前的输出与退出码。</summary>
    private async Task<ShellRunResult> WaitForMarkerAsync(
        string marker, CancellationToken cancellationToken)
    {
        using var timeout = new CancellationTokenSource(_commandTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, timeout.Token);

        var deadline = Stopwatch.StartNew();

        while (true)
        {
            string snapshot;
            lock (_sync) snapshot = _pending.ToString();

            var index = snapshot.IndexOf(marker, StringComparison.Ordinal);
            if (index >= 0)
            {
                // 标记之后还有 ":<退出码>" 与换行：等它们也到齐。
                var lineEnd = snapshot.IndexOf('\n', index);
                if (lineEnd >= 0)
                {
                    var tail = snapshot[(index + marker.Length)..lineEnd].TrimStart(':').Trim();
                    var exitCode = int.TryParse(tail, out var parsed) ? parsed : -1;

                    var stdout = snapshot[..index].TrimEnd('\r', '\n');

                    lock (_sync)
                    {
                        // 把哨兵之后可能残留的内容留下（下一条命令的早期输出）。
                        var rest = lineEnd + 1 < snapshot.Length ? snapshot[(lineEnd + 1)..] : string.Empty;
                        _pending.Clear();
                        _pending.Append(rest);
                    }

                    return new ShellRunResult(exitCode, stdout, string.Empty, TimedOut: false);
                }
            }

            if (linked.IsCancellationRequested)
            {
                var timedOut = timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested;

                lock (_sync) _pending.Clear();

                if (!timedOut)
                    return ShellRunResult.Failed("命令被取消。");

                // **超时后重启 shell**，而不是让它永久不可用。
                //
                // 原因：命令可能还在跑，残留在管道里的输出会污染后续每一条命令。
                // 实测踩到：一次超时之后，后面所有命令都返回同一条超时消息——
                // 会话看起来"坏了"，而用户唯一能做的重启整个程序。
                //
                // 重启的代价是丢掉 cd/export 状态，但那比"永久不可用"好得多；
                // 而且模型会从诊断里知道状态丢了。
                Restart();

                return ShellRunResult.Failed(
                    $"命令超时（{_commandTimeout.TotalSeconds:0} 秒），shell 已重启。"
                    + "工作目录与环境变量的改动已丢失。", timedOut: true);
            }

            if (_process is { HasExited: true })
            {
                return ShellRunResult.Failed(
                    $"shell 已退出（退出码 {_process.ExitCode}）。命令未完成。");
            }

            // 轮询而不是阻塞读：需要同时观察超时、进程退出与哨兵。
            // 20ms 对交互延迟无感，对 CPU 是可忽略的。
            try { await Task.Delay(20, linked.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { /* 下一轮处理 */ }
        }
    }

    /// <summary>
    /// 杀掉当前 shell 并让下一次调用重新启动。
    /// </summary>
    /// <remarks>
    /// <b>同步方法</b>：它在超时路径里被调用，而那已经在一个 async 方法内，
    /// 包一层 await 只会让"重启"这个动作看起来比实际复杂。杀进程是同步的。
    /// </remarks>
    private void Restart()
    {
        lock (_sync)
        {
            try { _stdin?.Dispose(); } catch { /* 已断 */ }
            try { if (_process is { HasExited: false }) _process.Kill(entireProcessTree: true); }
            catch { /* 已退出 */ }
            try { _process?.Dispose(); } catch { /* 已释放 */ }

            _process = null;
            _stdin = null;
            _pending.Clear();
        }
    }

    /// <summary>确保 shell 已启动。</summary>
    private bool EnsureStarted()
    {
        if (IsRunning) return true;

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/sh",
                WorkingDirectory = _workspace,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };

            if (OperatingSystem.IsWindows())
            {
                // /Q  关回显：否则每条命令都被原样打回，模型会把它当输出。
                // /V:ON 开延迟展开：!errorlevel! 才取到真实退出码（见 RunAsync 注释）。
                startInfo.ArgumentList.Add("/Q");
                startInfo.ArgumentList.Add("/V:ON");
            }

            // /D 禁掉 AutoRun：否则注册表里的启动脚本会污染第一条输出。
            if (OperatingSystem.IsWindows()) startInfo.ArgumentList.Add("/D");

            _process = Process.Start(startInfo);
            if (_process is null) return false;

            _stdin = _process.StandardInput;
            _stdin.AutoFlush = false;

            // **关掉提示符**：cmd 交互式会给每条命令回一个 `C:\path>`，
            // 它会混进输出里，模型看到的是"路径 + 结果"而不是结果。
            //
            // 注意顺序：这行必须在 `_stdin` 赋值**之后**发，否则写的是 null。
            // 第一版写在了前面，`prompt` 从未生效——而症状（提示符混进输出）
            // 看起来像 cmd 不支持关提示符。
            //
            // `prompt $` 后面留空即不打印；`$` 是 cmd 的转义符，
            // 写 `$$` 反而会输出字面 `$`。
            // **两个流都收到同一缓冲**：stderr 单独丢会给模型缺一半信息，
            // 而持久 shell 的用途恰恰是"看看那串命令报了什么错"。
            //
            // **必须先启动 Drain，再发 prompt/barrier**：Drain 是唯一的写入方，
            // 它没起来时 `_pending` 永远是 0，barrier 会一直等到超时。
            // 第一版把这段放在了发送之后，症状是"barrier 找不到但输出里明明有它"。
            _ = Task.Run(() => DrainAsync(_process.StandardOutput));
            _ = Task.Run(() => DrainAsync(_process.StandardError));

            // **关掉提示符**：cmd 交互式会给每条命令回一个 `C:\path>`，
            // 它会混进输出里，模型看到的是"路径 + 结果"而不是结果。
            //
            // `prompt $` 后面留空即不打印；`$` 是 cmd 的转义符，
            // 写 `$$` 反而会输出字面 `$`。
            if (OperatingSystem.IsWindows())
            {
                _stdin.WriteLine("prompt $");
                _stdin.Flush();

                // **用一个哨兵把启动横幅冲掉**：cmd 交互式启动时会打印版本号、
                // 版权行与初始提示符，而那发生在设 prompt 之前。
                //
                // 靠 Sleep 等不行——横幅与异步读取有竞态。改成"发哨兵命令，
                // 读到它之后把此前的全部截掉"：横幅必然出现在哨兵之前。
                var barrier = $"__DOUGONG_READY_{Guid.NewGuid():N}__";
                _stdin.WriteLine($"echo {barrier}");
                _stdin.Flush();

                // 不要再 Clear：WaitForBarrier 内部已把哨兵之前全部截掉。
                WaitForBarrier(barrier, TimeSpan.FromSeconds(3));
            }

            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>同步等哨兵出现（只用于启动阶段）。</summary>
    /// <remarks>
    /// <b>启动阶段不能 await</b>：<see cref="EnsureStarted"/> 是同步方法，
    /// 而改成异步会波及 <see cref="RunAsync"/> 的调用形态。
    /// 这里的阻塞是有界的（最多 <paramref name="limit"/>），且只发生一次。
    /// </remarks>
    private bool WaitForBarrier(string barrier, TimeSpan limit)
    {
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < limit)
        {
            lock (_sync)
            {
                var text = _pending.ToString();
                var index = text.IndexOf(barrier, StringComparison.Ordinal);
                if (index >= 0)
                {
                    // **从哨兵之后截断，而不是清空**。
                    // 清空是错的：Drain 线程可能在我们 Clear 之后又追加
                    // 一批早已读到的字节，于是横幅重新出现。截断到哨兵
                    // 之后就切掉了"哨兵之前的一切"，且不受后续追加影响。
                    var after = index + barrier.Length;
                    var lineEnd = text.IndexOf((char)10, after);
                    _pending.Clear();
                    if (lineEnd >= 0 && lineEnd + 1 < text.Length)
                    {
                        // **跳过紧随其后的换行**：cmd 的 echo 输出是 CRLF，
                        // 而屏障行本身也可能是最后一行。不跳过的话，下一条命令
                        // 的输出会以一个前导换行开头——实测每条命令都如此。
                        var rest = text[(lineEnd + 1)..].TrimStart((char)13, (char)10);
                        if (rest.Length > 0) _pending.Append(rest);
                    }

                    return true;
                }
            }

            Thread.Sleep(10);
        }

        return false;   // 超时不清缓冲
    }

    private async Task DrainAsync(StreamReader reader)
    {
        var chunk = new char[4096];
        try
        {
            while (true)
            {
                var read = await reader.ReadAsync(chunk).ConfigureAwait(false);
                if (read == 0) break;

                lock (_sync)
                {
                    _pending.Append(chunk, 0, read);

                    // 环形：超出上限丢头部。持久 shell 的输出上限比作业小，
                    // 因为它是"边跑边看"，不像作业可能一次产出很久。
                    var excess = _pending.Length - _retainBytes;
                    if (excess > 0) _pending.Remove(0, excess);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            // 进程退出时流会断：不是错误。
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try
        {
            if (_process is { HasExited: false })
            {
                // **先发 exit 再杀**：直接 kill 会让 shell 来不及清理临时文件。
                // 但也不能只发 exit——它可能正卡在某条命令里。
                try { _stdin?.WriteLine("exit"); _stdin?.Flush(); } catch { /* 已断 */ }

                if (!_process.WaitForExit(500))
                    _process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception)
        {
            // 释放失败不改变结论。
        }
        finally
        {
            try { _stdin?.Dispose(); } catch { /* 已释放 */ }
            try { _process?.Dispose(); } catch { /* 已释放 */ }
            _process = null;
            _stdin = null;
            _gate.Dispose();
        }
    }
}

/// <summary>一次持久 shell 命令的结果。</summary>
/// <param name="ExitCode">退出码；-1 表示没拿到。</param>
/// <param name="Stdout">标准输出（含标准错误）。</param>
/// <param name="Stderr">
/// 恒为空——持久 shell 把两个流合并到同一缓冲，见类型注释。
/// 保留这个字段是为了与 <see cref="ShellExecutionResult"/> 的形态一致。
/// </param>
/// <param name="TimedOut">是否因超时而返回。</param>
public sealed record ShellRunResult(
    int ExitCode,
    string Stdout,
    string Stderr,
    bool TimedOut)
{
    /// <summary>构造一个失败结果。</summary>
    /// <param name="diagnostic">给模型看的说明。</param>
    /// <param name="timedOut">是否因超时失败。</param>
    /// <remarks>
    /// <b><paramref name="timedOut"/> 必须如实传</b>：模型据此决定"等一下再试"
    /// 还是"换个做法"。第一版恒为 false，于是超时看起来像普通失败。
    /// </remarks>
    public static ShellRunResult Failed(string diagnostic, bool timedOut = false) =>
        new(-1, diagnostic, string.Empty, timedOut);
}
