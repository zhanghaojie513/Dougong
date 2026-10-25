using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Dougong.Agent.Comparison;

/// <summary>
/// 真实进程验证：`DOUGONG_KERNEL=1` 下 CLI 能跑完一轮，且工具调用经过内核。
/// </summary>
/// <remarks>
/// 为什么必须跨进程：进程内 harness 里 host 与工具是同一对象图，
/// "装配写错（例如忘了传审批器、或内核路径根本没启用）"它照样能过。
/// 这里从 <c>Process.Start</c> 开始，跨越真实的 stdin/stdout 与真实装配。
/// <para>
/// provider 用本地 HTTP stub（不读任何密钥）：验的是接线，不是模型能力。
/// </para>
/// </remarks>
public static class KernelProcessChecks
{
    public static async Task<int> RunAsync()
    {
        var failures = 0;
        failures += await CheckAsync("kernel-path-runs-a-turn-with-a-tool", KernelTurnAsync);
        failures += await CheckAsync("acp-path-uses-kernel", AcpKernelAsync);
        failures += await CheckAsync("default-path-uses-kernel", DefaultUsesKernelAsync);
        failures += await CheckAsync("opt-out-restores-direct-path", OptOutAsync);

        Console.WriteLine(failures == 0
            ? "KERNEL-PROCESS: ALL PASS"
            : $"KERNEL-PROCESS: {failures} FAILURE(S)");
        return failures;
    }

    /// <summary>
    /// 完整一轮：模型请求 read_file → 工具经内核 dispatcher 执行 → 结果回填 → 最终文本。
    /// </summary>
    /// <remarks>
    /// 判据是 stub 收到的**第二次请求**里含工具结果。若内核路径没启用，
    /// 输出会带"内核：已启用"与否的差别；若装配坏了，工具结果不会回填。
    /// </remarks>
    private static async Task<string?> KernelTurnAsync()
    {
        using var provider = new StubProvider();
        await using var agent = StartAgent(provider.BaseUrl, kernel: true);

        try
        {
            // 先等启动完成再写输入：子进程启动、连 provider、装内核都要时间。
            // 提前写 stdin 时输入可能落在 REPL 就绪之前（实测表现为 0 次模型调用）。
            var bannerSeen = await agent.WaitForOutputAsync(
                "kernel=enabled", TimeSpan.FromSeconds(40));
            if (!bannerSeen)
                return $"CLI 未报告「kernel=enabled」。输出：{Truncate(agent.Transcript, 800)}";

            // 等到 REPL 提示符出现才算真的就绪。只等 banner 不够：
            // banner 打印完到进入 Console.ReadLine 之间还有装配工作，
            // 这段时间写进去的输入可能丢失（实测表现为 provider 收到 0 次调用）。
            await agent.WaitForOutputAsync(">", TimeSpan.FromSeconds(20));

            // CLI 默认是 REPL（不是 ACP）：stdin 一行 = 一轮输入。
            await agent.WriteRawAsync("read a.txt");

            // 等三次 provider 调用：第一次要工具，第二次给最终文本。
            // 这里只等到第二次出现为止（工具结果回填后）。
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(40);
            while (DateTime.UtcNow < deadline && provider.CallCount < 2)
                await Task.Delay(100);

            if (provider.CallCount < 2)
                return $"provider 只收到 {provider.CallCount} 次调用；输出：{Truncate(agent.Transcript, 600)}；stderr：{Truncate(agent.ReadLog(), 600)}";

            var second = provider.SecondRequestText;
            if (second is null) return "没记录到第二次请求";
            if (!second.Contains("tool", StringComparison.OrdinalIgnoreCase)
                && !second.Contains("content", StringComparison.OrdinalIgnoreCase))
                return $"第二次请求里看不到工具结果：{Truncate(second, 600)}";

            return null;
        }
        finally
        {
            await agent.StopAsync();
        }
    }

    /// <summary>
    /// **不设任何环境变量**时，默认就走内核。
    /// </summary>
    /// <remarks>
    /// 默认转正之后必须单独立一条：原先 <c>acp-path-uses-kernel</c> 显式设了
    /// <c>DOUGONG_KERNEL=1</c>，所以默认值改没改它都通过——那就测不出默认行为。
    /// </remarks>
    private static async Task<string?> DefaultUsesKernelAsync()
    {
        using var provider = new StubProvider();
        await using var agent = StartAgent(
            provider.BaseUrl, kernel: null, acp: false);

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(40);
        while (DateTime.UtcNow < deadline)
        {
            if (agent.Transcript.Contains("kernel=enabled", StringComparison.Ordinal)) return null;
            await Task.Delay(150);
        }

        return $"未设置 DOUGONG_KERNEL 时默认未走内核；输出：{Truncate(agent.Transcript, 600)}";
    }

    /// <summary>
    /// <c>DOUGONG_KERNEL=0</c> 必须退回直连路径——这是排查手段，不能失效。
    /// </summary>
    private static async Task<string?> OptOutAsync()
    {
        using var provider = new StubProvider();
        await using var agent = StartAgent(
            provider.BaseUrl, kernel: false, acp: false);

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            if (agent.Transcript.Contains("kernel=disabled", StringComparison.Ordinal)) return null;
            if (agent.Transcript.Contains("kernel=enabled", StringComparison.Ordinal))
                return "DOUGONG_KERNEL=0 未生效，仍走了内核路径";
            await Task.Delay(150);
        }

        // 没打印任何标记也算失败：无法确认走的是哪条路径。
        return $"DOUGONG_KERNEL=0 时未报告路径标记；输出：{Truncate(agent.Transcript, 600)}";
    }

    /// <summary>
    /// ACP 入口在 DOUGONG_KERNEL=1 时应声明内核已启用（走 stderr）。
    /// </summary>
    /// <remarks>
    /// 为什么必须跨进程分开验：REPL 与 ACP 共用同一个程序集，
    /// 元数据检查无法区分是哪个入口调了内核。只有真起 `--acp` 进程才分得清。
    /// </remarks>
    private static async Task<string?> AcpKernelAsync()
    {
        using var provider = new StubProvider();
        await using var agent = StartAgent(provider.BaseUrl, kernel: true, acp: true);

        // ACP 会话是**懒创建**的：不发 session/new 就不会装配，也就不会碰内核。
        // 先走一遍 initialize + session/new，装配才会发生。
        await agent.WriteRawAsync("""{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":1}}""");
        // cwd 必须是绝对路径（宿主的校验要求）。
        var newSession = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = 2,
            ["method"] = "session/new",
            ["params"] = new JsonObject
            {
                ["cwd"] = Directory.GetCurrentDirectory(),
                ["mcpServers"] = new JsonArray(),
            },
        };
        await agent.WriteRawAsync(newSession.ToJsonString());

        // ACP 模式的日志走 stderr（stdout 归协议）。
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(40);
        while (DateTime.UtcNow < deadline)
        {
            var log = agent.ReadLog();
            if (log.Contains("kernel=enabled", StringComparison.Ordinal)) return null;
            await Task.Delay(150);
        }

        return $"ACP 进程 stderr 未出现 kernel=enabled；stderr：{Truncate(agent.ReadLog(), 600)}；" +
            $"stdout：{Truncate(agent.Transcript, 600)}";
    }

    private sealed class StubProvider : IDisposable
    {
        private readonly System.Net.HttpListener _listener = new();
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _loop;
        private int _calls;

        public StubProvider()
        {
            var port = GetFreePort();
            BaseUrl = $"http://127.0.0.1:{port}";
            _listener.Prefixes.Add(BaseUrl + "/");
            _listener.Start();
            _loop = Task.Run(() => AcceptAsync(_stop.Token));
        }

        public string BaseUrl { get; }

        public int CallCount => Volatile.Read(ref _calls);

        public string? SecondRequestText { get; private set; }

        private async Task AcceptAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                System.Net.HttpListenerContext context;
                try { context = await _listener.GetContextAsync(); }
                catch { return; }

                _ = Task.Run(() => HandleAsync(context), cancellationToken);
            }
        }

        private async Task HandleAsync(System.Net.HttpListenerContext context)
        {
            using var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8);
            var body = await reader.ReadToEndAsync();

            var index = Interlocked.Increment(ref _calls);
            if (index == 2) SecondRequestText = body;

            var payload = index == 1 ? ToolTurn() : FinalTurn();

            var bytes = Encoding.UTF8.GetBytes(payload);
            context.Response.StatusCode = 200;
            context.Response.ContentType = "text/event-stream";
            context.Response.ContentLength64 = bytes.Length;
            await context.Response.OutputStream.WriteAsync(bytes);
            context.Response.Close();
        }

        /// <summary>第一轮：请求 read_file。</summary>
        private static string ToolTurn() =>
            Sse(new JsonObject
            {
                ["choices"] = new JsonArray(new JsonObject
                {
                    ["index"] = 0,
                    ["delta"] = new JsonObject
                    {
                        ["role"] = "assistant",
                        ["tool_calls"] = new JsonArray(new JsonObject
                        {
                            ["index"] = 0,
                            ["id"] = "call-1",
                            ["type"] = "function",
                            ["function"] = new JsonObject
                            {
                                ["name"] = "read_file",
                                ["arguments"] = """{"path":"a.txt"}""",
                            },
                        }),
                    },
                    ["finish_reason"] = (string?)null,
                }),
            }) + Sse(new JsonObject
            {
                ["choices"] = new JsonArray(new JsonObject
                {
                    ["index"] = 0,
                    ["delta"] = new JsonObject(),
                    ["finish_reason"] = "tool_calls",
                }),
            });

        private static string FinalTurn() =>
            Sse(new JsonObject
            {
                ["choices"] = new JsonArray(new JsonObject
                {
                    ["index"] = 0,
                    ["delta"] = new JsonObject
                    {
                        ["role"] = "assistant",
                        ["content"] = "读到了。",
                    },
                    ["finish_reason"] = (string?)null,
                }),
            }) + Sse(new JsonObject
            {
                ["choices"] = new JsonArray(new JsonObject
                {
                    ["index"] = 0,
                    ["delta"] = new JsonObject(),
                    ["finish_reason"] = "stop",
                }),
            });

        private static string Sse(JsonObject chunk) => $"data: {chunk.ToJsonString()}\n\n";

        private static int GetFreePort()
        {
            using var socket = new System.Net.Sockets.TcpListener(
                System.Net.IPAddress.Loopback, 0);
            socket.Start();
            return ((System.Net.IPEndPoint)socket.LocalEndpoint).Port;
        }

        public void Dispose()
        {
            _stop.Cancel();
            try { _listener.Stop(); } catch { }
            try { _loop.Wait(TimeSpan.FromSeconds(2)); } catch { }
            _listener.Close();
        }
    }

    private sealed class AgentProcess(
        Process process, StreamReader output, string logPath, string storeDir, StreamWriter log)
        : IAsyncDisposable
    {
        private readonly StringBuilder _transcript = new();

        public string Transcript
        {
            get { lock (_transcript) return _transcript.ToString(); }
        }

        public async Task WriteAsync(JsonObject message) =>
            await WriteRawAsync(message.ToJsonString(new JsonSerializerOptions
            {
                WriteIndented = false,
            }));

        public async Task WriteRawAsync(string line)
        {
            await process.StandardInput.WriteAsync(line + "\n");
            await process.StandardInput.FlushAsync();
        }

        /// <summary>等到输出里出现某个标记，或超时。</summary>
        public async Task<bool> WaitForOutputAsync(string marker, TimeSpan timeout)
        {
            var deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline)
            {
                if (Transcript.Contains(marker, StringComparison.Ordinal)) return true;
                await Task.Delay(100);
            }

            return false;
        }

        /// <summary>后台持续读 stdout，累积到 transcript（否则管道会满、子进程阻塞）。</summary>
        public void StartReading()
        {
            _ = Task.Run(async () =>
            {
                while (true)
                {
                    var line = await output.ReadLineAsync();
                    if (line is null) return;
                    lock (_transcript) _transcript.AppendLine(line);
                }
            });
        }

        public async Task StopAsync()
        {
            try
            {
                if (!process.HasExited)
                {
                    process.StandardInput.Close();
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    try { await process.WaitForExitAsync(cts.Token); }
                    catch (OperationCanceledException) { process.Kill(entireProcessTree: true); }
                }
            }
            catch { }
        }

        public string LogPath => logPath;

        /// <summary>读子进程 stderr（诊断失败时最需要的材料）。</summary>
        public string ReadLog()
        {
            try
            {
                if (!File.Exists(logPath)) return "(无日志)";
                using var stream = new FileStream(
                    logPath, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream, Encoding.UTF8);
                return reader.ReadToEnd();
            }
            catch (IOException ex) { return $"(日志读取失败：{ex.Message})"; }
        }

        public async ValueTask DisposeAsync()
        {
            await StopAsync();
            try { log.Dispose(); } catch { }
            try { process.Dispose(); } catch { }
            try { Directory.Delete(storeDir, recursive: true); } catch (IOException) { }
        }
    }

    /// <param name="kernel">
    /// true=显式启用，false=显式关闭，null=不设置（验证默认值）。
    /// </param>
    private static AgentProcess StartAgent(string baseUrl, bool? kernel, bool acp = false)
    {
        var dll = Path.Combine(
            AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar),
            "..", "..", "..", "..", "..", "src", "Dougong.Cli", "bin", "Release", "net10.0",
            "Dougong.Cli.dll");
        dll = Path.GetFullPath(dll);
        if (!File.Exists(dll))
            throw new FileNotFoundException($"找不到 CLI 构建产物：{dll}");

        var storeDir = Path.Combine(
            Path.GetTempPath(), "dougong-kernel-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(storeDir);
        var workspace = Path.Combine(storeDir, "ws");
        Directory.CreateDirectory(workspace);
        File.WriteAllText(Path.Combine(workspace, "a.txt"), "hello from file");
        var logPath = Path.Combine(storeDir, "stderr.log");

        var info = new ProcessStartInfo("dotnet")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        info.ArgumentList.Add(dll);
        if (acp) info.ArgumentList.Add("--acp");

        info.Environment["DOUGONG_PROVIDER_BASE"] = baseUrl;
        info.Environment["DOUGONG_PROVIDER_KEY"] = "stub-key-not-a-real-secret";
        info.Environment["DOUGONG_PROVIDER_MODEL"] = "stub-model";
        info.Environment["DOUGONG_PROVIDER_PROTOCOL"] = "openai";
        info.Environment["DOUGONG_SESSION_DIR"] = storeDir;
        info.Environment["DOUGONG_WORKSPACE"] = workspace;
        if (kernel is { } enabled) info.Environment["DOUGONG_KERNEL"] = enabled ? "1" : "0";
        info.Environment["DOUGONG_AUTO_APPROVE"] = "1";
        // 控制台编码固定为 UTF-8：子进程的 stdin 由测试写入，若编码不一致，
        // 非 ASCII 输入会被逐字节误读（本机上表现为"0 次模型调用"）。
        info.Environment["DOTNET_SYSTEM_CONSOLE_ALLOW_ANSI_COLOR_REDIRECTION"] = "1";

        var process = Process.Start(info)
            ?? throw new InvalidOperationException("无法启动 dougong");

        var log = new StreamWriter(logPath, append: false, Encoding.UTF8) { AutoFlush = true };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) log.WriteLine(e.Data); };
        process.BeginErrorReadLine();

        var agent = new AgentProcess(process, process.StandardOutput, logPath, storeDir, log);
        agent.StartReading();
        return agent;
    }

    private static string Truncate(string text, int max) =>
        text.Length <= max ? text : text[..max] + "…";

    private static async Task<int> CheckAsync(string name, Func<Task<string?>> action)
    {
        try
        {
            var failure = await action();
            if (failure is null)
            {
                Console.WriteLine($"PASS {name}");
                return 0;
            }

            Console.WriteLine($"FAIL {name}: {failure}");
            return 1;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"FAIL {name}: {ex.GetType().Name}: {ex.Message}");
            return 1;
        }
    }
}
