using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Dougong.Agent.Comparison;

/// <summary>
/// 真实进程验证：起一个真的 <c>dougong --acp</c>，用本地 HTTP stub 扮演 provider，
/// 让模型（脚本化的）走完整路径调用 <c>ask_user</c>。
/// </summary>
/// <remarks>
/// 为什么不只用进程内 harness：harness 里 host 与工具都是同一个对象图，
/// 装配写错（例如忘记传 hostAccessor、或 dispatcher 装了两套）它照样能过。
/// 这里从 <c>Process.Start</c> 开始，跨的是真实的 stdin/stdout 与真实的会话装配。
/// <para>
/// provider 用本地 stub 而不是真实 key：验的是接线，不是模型能力。
/// 因此不读任何密钥环境变量，也不宣称"模型验证通过"。
/// </para>
/// </remarks>
public static class ElicitationProcessChecks
{
    public static async Task<int> RunAsync()
    {
        var failures = 0;
        failures += await CheckAsync("real-process-ask-user-round-trip", RoundTripAsync);

        Console.WriteLine(failures == 0
            ? "ELICITATION-PROCESS: ALL PASS"
            : $"ELICITATION-PROCESS: {failures} FAILURE(S)");
        return failures;
    }

    /// <summary>
    /// 完整一轮：模型请求调用 ask_user → 进程发出 elicitation/create →
    /// 客户端答复 → 结果回到模型 → 模型给出最终文本。
    /// </summary>
    private static async Task<string?> RoundTripAsync()
    {
        using var provider = new StubProvider();
        using var agent = StartAgent(provider.BaseUrl);
        var process = agent.Process;
        var output = agent.Output;
        var logPath = agent.LogPath;

        try
        {
            var initialize = await SendAsync(process, output, new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = 1,
                ["method"] = "initialize",
                ["params"] = new JsonObject { ["protocolVersion"] = 1 },
            });
            if (initialize["result"] is null)
                return $"initialize 失败：{initialize.ToJsonString()}";

            var session = await SendAsync(process, output, new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = 2,
                ["method"] = "session/new",
                ["params"] = new JsonObject
                {
                    ["cwd"] = Directory.GetCurrentDirectory(),
                    ["mcpServers"] = new JsonArray(),
                },
            });
            var sessionId = session["result"]?["sessionId"]?.GetValue<string>();
            if (string.IsNullOrEmpty(sessionId))
                return $"session/new 未返回 sessionId：{session.ToJsonString()}";

            // session/prompt 是长请求：先发出去，再等它中途发来的 elicitation。
            await WriteAsync(process, new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = 3,
                ["method"] = "session/prompt",
                ["params"] = new JsonObject
                {
                    ["sessionId"] = sessionId,
                    ["prompt"] = new JsonArray(new JsonObject
                    {
                        ["type"] = "text",
                        ["text"] = "请向用户提问",
                    }),
                },
            });

            var (elicitation, seenMethods) = await ReadUntilAsync(output, "elicitation/create");

            // 提问本身不该先弹一次审批：同一个问题让用户回答两次是明确的体验缺陷。
            // 先判这一条，否则会退化成"没收到 elicitation"这种掩盖真因的报错。
            if (seenMethods.Contains("session/request_permission"))
                return "ask_user 触发了审批请求；提问没有副作用，不该让用户先批准再回答";

            if (elicitation is null)
                return $"进程没有发出 elicitation/create。provider 收到的请求次数={provider.CallCount}；"
                    + $"已收到的帧={string.Join(",", seenMethods)}；日志={ReadLog(logPath)}";

            var paramsNode = elicitation["params"]!;
            if (paramsNode["sessionId"]?.GetValue<string>() != sessionId)
                return $"elicitation 的 sessionId 与会话不符：{paramsNode["sessionId"]?.ToJsonString()}";

            var schema = paramsNode["requestedSchema"];
            if (schema?["properties"] is not JsonObject properties || properties.Count != 1)
                return $"requestedSchema 形状不符：{schema?.ToJsonString()}";

            var header = properties.First().Key;
            await WriteAsync(process, new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = elicitation["id"]!.DeepClone(),
                ["result"] = new JsonObject
                {
                    ["action"] = "accept",
                    ["content"] = new JsonObject { [header] = "Postgres" },
                },
            });

            // 等 session/prompt 的响应：只有整轮结束才会到。
            var promptResponse = await ReadResponseAsync(output, 3);

            // 模型必须真的看到了答案：stub 记录第二次请求的入参。
            var secondRequest = provider.SecondRequestText;
            if (secondRequest is null)
                return "provider 没收到第二次调用（工具结果未回填给模型）";
            if (!secondRequest.Contains("Postgres", StringComparison.Ordinal))
                return $"工具结果未包含用户答案。第二次请求片段：{Truncate(secondRequest, 600)}";
            if (!secondRequest.Contains("Answered", StringComparison.Ordinal))
                return $"工具结果未包含 outcome。第二次请求片段：{Truncate(secondRequest, 600)}";

            if (promptResponse["result"] is null)
                return $"session/prompt 未正常结束：{promptResponse.ToJsonString()}";

            if (!provider.CallCount.Equals(2))
                return $"预期 2 次模型调用，实际 {provider.CallCount}";

            return null;
        }
        finally
        {
            // 先停进程，再由 agent 的 Dispose 释放日志并删临时目录。
            await StopAsync(process);
        }
    }

    /// <summary>一个只会按脚本回答的 OpenAI 兼容端点。</summary>
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

        /// <summary>第二次请求的原文（含工具结果回填）。</summary>
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

            var payload = index == 1 ? FirstTurn() : SecondTurn();

            var bytes = Encoding.UTF8.GetBytes(payload);
            context.Response.StatusCode = 200;
            context.Response.ContentType = "text/event-stream";
            context.Response.ContentLength64 = bytes.Length;
            await context.Response.OutputStream.WriteAsync(bytes);
            context.Response.Close();
        }

        /// <summary>第一轮：模型要求调用 ask_user。</summary>
        private static string FirstTurn() =>
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
                                ["name"] = "ask_user",
                                ["arguments"] = JsonSerializer.Serialize(new
                                {
                                    questions = new[]
                                    {
                                        new
                                        {
                                            question = "用哪个数据库？",
                                            header = "DB",
                                            options = new[]
                                            {
                                                new { label = "Postgres", description = "关系型" },
                                                new { label = "SQLite", description = "嵌入式" },
                                            },
                                        },
                                    },
                                }),
                            },
                        }),
                    },
                }),
                ["finish_reason"] = (string?)null,
            }) + Sse(new JsonObject
            {
                ["choices"] = new JsonArray(new JsonObject
                {
                    ["index"] = 0,
                    ["delta"] = new JsonObject { ["content"] = "" },
                    ["finish_reason"] = "tool_calls",
                }),
            });

        /// <summary>第二轮：模型给出最终文本。</summary>
        private static string SecondTurn() =>
            Sse(new JsonObject
            {
                ["choices"] = new JsonArray(new JsonObject
                {
                    ["index"] = 0,
                    ["delta"] = new JsonObject { ["role"] = "assistant", ["content"] = "好的，用 Postgres。" },
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

    private sealed record AgentProcess(
        Process Process, StreamReader Output, string LogPath, string StoreDir, StreamWriter Log)
        : IDisposable
    {
        public void Dispose()
        {
            // 日志写入器必须在进程退出后显式释放：它一直持有临时目录里的文件，
            // 不释放会让目录删不掉，跑一次留一堆。
            try { Log.Dispose(); } catch { }
            try { Process.Dispose(); } catch { }
            try { Directory.Delete(StoreDir, recursive: true); } catch (IOException) { }
        }
    }

    private static AgentProcess StartAgent(string baseUrl)
    {
        var dll = Path.Combine(
            AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar),
            "..", "..", "..", "..", "..", "src", "Dougong.Cli", "bin", "Release", "net10.0",
            "Dougong.Cli.dll");
        dll = Path.GetFullPath(dll);
        if (!File.Exists(dll))
            throw new FileNotFoundException($"找不到 CLI 构建产物：{dll}");

        var storeDir = Path.Combine(
            Path.GetTempPath(), "dougong-elicit-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(storeDir);
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
        info.ArgumentList.Add("--acp");

        info.Environment["DOUGONG_PROVIDER_BASE"] = baseUrl;
        info.Environment["DOUGONG_PROVIDER_KEY"] = "stub-key-not-a-real-secret";
        info.Environment["DOUGONG_PROVIDER_MODEL"] = "stub-model";
        info.Environment["DOUGONG_PROVIDER_PROTOCOL"] = "openai";
        info.Environment["DOUGONG_SESSION_DIR"] = storeDir;
        info.Environment["DOUGONG_WORKSPACE"] = Directory.GetCurrentDirectory();

        var process = Process.Start(info)
            ?? throw new InvalidOperationException("无法启动 dougong --acp");

        // stderr 必须持续读走：管道写满会阻塞子进程，表现为"卡住"。
        var log = new StreamWriter(logPath, append: false, Encoding.UTF8) { AutoFlush = true };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) log.WriteLine(e.Data); };
        process.BeginErrorReadLine();

        return new AgentProcess(process, process.StandardOutput, logPath, storeDir, log);
    }

    private static async Task<JsonNode> SendAsync(
        Process process, StreamReader output, JsonObject message)
    {
        await WriteAsync(process, message);
        return await ReadResponseAsync(output, message["id"]!.GetValue<int>());
    }

    private static async Task WriteAsync(Process process, JsonObject message)
    {
        var line = message.ToJsonString(new JsonSerializerOptions { WriteIndented = false });
        await process.StandardInput.WriteAsync(line + "\n");
        await process.StandardInput.FlushAsync();
    }

    private static async Task<JsonNode> ReadResponseAsync(StreamReader output, int id)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while (true)
        {
            var line = await output.ReadLineAsync(cts.Token)
                ?? throw new TimeoutException($"进程在响应 id={id} 之前关闭了 stdout");
            var node = JsonNode.Parse(line)!;
            if (node["method"] is null && node["id"]?.GetValue<int>() == id) return node;
        }
    }

    private static async Task<(JsonNode? Message, List<string> Methods)> ReadUntilAsync(
        StreamReader output, string method)
    {
        var methods = new List<string>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while (true)
        {
            string? line;
            try { line = await output.ReadLineAsync(cts.Token); }
            catch (OperationCanceledException) { return (null, methods); }

            if (line is null) return (null, methods);
            var node = JsonNode.Parse(line);
            var seen = node?["method"]?.GetValue<string>();
            if (seen is null) continue;

            methods.Add(seen);
            if (seen == method) return (node, methods);
        }
    }

    private static async Task StopAsync(Process process)
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

    private static string ReadLog(string path)
    {
        try
        {
            if (!File.Exists(path)) return "(无日志)";

            // 子进程可能仍持有写句柄：用共享读打开，否则拿不到最需要的诊断。
            using var stream = new FileStream(
                path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            return Truncate(reader.ReadToEnd(), 1500);
        }
        catch (IOException ex)
        {
            return $"(日志读取失败：{ex.Message})";
        }
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
