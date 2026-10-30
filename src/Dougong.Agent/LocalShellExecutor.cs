using System.Diagnostics;
using System.Text;

namespace Dougong.Agent;

/// <summary>
/// 本地进程实现：用 <see cref="Process"/> 直接起命令。
/// </summary>
/// <remarks>
/// <b>这是"不沙箱"的实现</b>：命令以当前用户身份运行，只在工作区路径上
/// 有工具层的守卫，而 <c>cd /</c> 之类完全可以绕过。
/// 需要真正的限制时，换一个实现注入 <see cref="ShellTool"/> 即可——工具本身不必改。
///
/// <para>
/// <b>输出上限由调用方给</b>：这里是执行层，不知道该显示多少；
/// 上限是呈现约束，属于工具。
/// </para>
/// </remarks>
/// <param name="workspace">命令的工作目录。</param>
public sealed class LocalShellExecutor(string workspace) : IShellExecutor
{
    private readonly string _workspace = Path.GetFullPath(workspace);

    /// <inheritdoc />
    public async ValueTask<ShellExecutionResult> ExecuteAsync(
        string command,
        TimeSpan timeout,
        int maxOutputBytes,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command);

        var process = Start(command);

        // 输出上限是累计的：两个流各自有界，且都会标记是否被截断。
        var stdout = new BoundedBuffer(maxOutputBytes);
        var stderr = new BoundedBuffer(maxOutputBytes);
        var readOut = DrainAsync(process.StandardOutput, stdout);
        var readErr = DrainAsync(process.StandardError, stderr);

        using var timeoutSource = new CancellationTokenSource(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, timeoutSource.Token);

        try
        {
            await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 取消或超时：必须结束整棵进程树，否则子进程继续跑而调用方以为停了，
            // 下一次调用就会与"已取消"的命令重叠执行。
            KillTree(process);
            await AwaitQuietlyAsync(process.WaitForExitAsync(CancellationToken.None))
                .ConfigureAwait(false);
            await AwaitQuietlyAsync(readOut).ConfigureAwait(false);
            await AwaitQuietlyAsync(readErr).ConfigureAwait(false);

            var captured = Summarize(stdout, stderr);

            if (cancellationToken.IsCancellationRequested)
            {
                // 已执行的命令不能伪装成"未发生"：取消时它可能已经产生了副作用。
                throw new OperationCanceledException(
                    "命令被取消并已结束进程树；副作用可能已部分发生。已捕获输出：" + captured,
                    cancellationToken);
            }

            throw new TimeoutException(
                $"命令超过 {timeout.TotalSeconds:0} 秒仍未结束，已结束进程树。"
                + "退出状态未知；副作用可能已部分发生。已捕获输出：" + captured);
        }

        // 进程已退出：读净剩余输出后再取退出码，避免读到一半。
        await AwaitQuietlyAsync(readOut).ConfigureAwait(false);
        await AwaitQuietlyAsync(readErr).ConfigureAwait(false);

        return new ShellExecutionResult(
            process.ExitCode, stdout.Text, stderr.Text, stdout.Truncated, stderr.Truncated);
    }

    /// <summary>起一个进程；失败时抛 <see cref="InvalidOperationException"/>。</summary>
    private Process Start(string command)
    {
        var startInfo = new ProcessStartInfo
        {
            WorkingDirectory = _workspace,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            // 重定向 stdin 并立即关闭：命令等待输入时拿到 EOF，而不是挂住直到超时。
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        if (OperatingSystem.IsWindows())
        {
            startInfo.FileName = Environment.GetEnvironmentVariable("COMSPEC") ?? "cmd.exe";
            startInfo.ArgumentList.Add("/c");
            startInfo.ArgumentList.Add(command);
        }
        else
        {
            startInfo.FileName = "/bin/sh";
            startInfo.ArgumentList.Add("-c");
            startInfo.ArgumentList.Add(command);
        }

        // 从零开始构建环境：不继承宿主环境，避免把密钥交给模型可执行的命令。
        // 必须先 Clear()：ProcessStartInfo.Environment 初始就装着父进程的全部变量，
        // 只往里加白名单项并不等于"只传这些"，密钥仍会一起被继承。
        startInfo.Environment.Clear();
        foreach (var pair in ShellEnvironment.Minimal())
            startInfo.Environment[pair.Key] = pair.Value;

        var process = new Process { StartInfo = startInfo };
        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            process.Dispose();
            throw new InvalidOperationException($"无法启动命令：{ex.Message}", ex);
        }

        // 立刻关闭 stdin：命令若尝试读取输入会得到 EOF。
        try { process.StandardInput.Close(); } catch (IOException) { }

        return process;
    }

    private static async Task DrainAsync(StreamReader reader, BoundedBuffer buffer)
    {
        var chunk = new char[4096];
        try
        {
            while (true)
            {
                var read = await reader.ReadAsync(chunk).ConfigureAwait(false);
                if (read <= 0) break;
                buffer.Append(chunk.AsSpan(0, read));
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            // 进程被结束时流会关闭；已读到的内容仍然有效。
        }
    }

    private static void KillTree(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // 进程可能刚好自行退出；这不是错误。
        }
    }

    private static async Task AwaitQuietlyAsync(Task task)
    {
        try { await task.ConfigureAwait(false); }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException)
        {
        }
    }

    private static string Summarize(BoundedBuffer stdout, BoundedBuffer stderr)
    {
        var builder = new StringBuilder();
        if (stdout.Text.Length > 0) builder.Append("stdout: ").AppendLine(stdout.Text);
        if (stderr.Text.Length > 0) builder.Append("stderr: ").AppendLine(stderr.Text);
        return builder.Length == 0 ? "(无输出)" : builder.ToString();
    }

    /// <summary>有上限的输出缓冲：超出后停止追加并标记截断。</summary>
    private sealed class BoundedBuffer(int capacity)
    {
        private readonly StringBuilder _builder = new();

        public bool Truncated { get; private set; }

        public string Text => _builder.ToString();

        public void Append(ReadOnlySpan<char> chunk)
        {
            if (Truncated) return;

            var remaining = capacity - _builder.Length;
            if (remaining <= 0)
            {
                Truncated = true;
                return;
            }

            if (chunk.Length <= remaining)
            {
                _builder.Append(chunk);
                return;
            }

            _builder.Append(chunk[..remaining]);
            Truncated = true;
        }
    }
}
