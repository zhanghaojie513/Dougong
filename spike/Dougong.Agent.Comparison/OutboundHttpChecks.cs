using System.Net;
using Dougong.Ai;

namespace Dougong.Agent.Comparison;

/// <summary>
/// 出站代理的验收。
/// </summary>
/// <remarks>
/// <b>可观察面是"这个请求会走到哪"</b>：代理是否被选上、哪些主机被绕过、
/// 非法配置时是拒绝还是悄悄直连。最后一条最重要——它是安全边界。
/// </remarks>
public static class OutboundHttpChecks
{
    private static readonly string[] AllProxyVariables =
        [.. OutboundHttp.ProxyVariables, .. OutboundHttp.NoProxyVariables];

    public static async Task<int> RunAsync()
    {
        var failures = 0;
        failures += await CheckAsync("proxy-is-read-from-environment", ReadsEnvironmentAsync);
        failures += await CheckAsync("proxy-https-wins-over-http", PriorityAsync);
        failures += await CheckAsync("proxy-invalid-value-really-blocks-traffic", InvalidBlocksForRealAsync);
        failures += await CheckAsync("proxy-no-proxy-bypasses-by-suffix", NoProxyAsync);
        failures += await CheckAsync("proxy-no-proxy-is-escaped-not-raw-regex", NoProxyEscapingAsync);
        failures += await CheckAsync("proxy-absent-falls-back-to-system-default", AbsentAsync);
        failures += await CheckAsync("proxy-is-wired-into-every-client-creation", WiringAsync);
        failures += await CheckAsync("no-product-code-creates-bare-httpclient", NoBareClientAsync);

        Console.WriteLine(failures == 0
            ? "OUTBOUND-HTTP: ALL PASS"
            : $"OUTBOUND-HTTP: {failures} FAILURE(S)");
        return failures;
    }

    /// <summary>环境变量里的代理被读到。</summary>
    private static async Task<string?> ReadsEnvironmentAsync()
    {
        await Task.CompletedTask;
        using var env = new Env();
        env.Set("HTTPS_PROXY", "http://proxy.corp.example:8080");

        var warnings = new List<string>();
        var proxy = OutboundHttp.ResolveProxy(warnings);

        if (proxy is null) return "没有读到 HTTPS_PROXY";
        if (warnings.Count > 0) return $"合法配置却报了警告：{warnings[0]}";

        var destination = new Uri("https://api.example.com/v1/chat");
        var resolved = proxy.GetProxy(destination);

        return resolved?.Host == "proxy.corp.example" && resolved.Port == 8080
            ? null
            : $"解析出的代理不对：{resolved}";
    }

    /// <summary>HTTPS_PROXY 优先于 HTTP_PROXY。</summary>
    /// <remarks>
    /// <b>顺序不能靠"哪个先读"</b>：两套变量在环境里都存在是常态
    /// （企业镜像常同时设），而模型 API 是 https——用 http 那个会走错出口。
    /// </remarks>
    private static async Task<string?> PriorityAsync()
    {
        await Task.CompletedTask;
        using var env = new Env();
        env.Set("HTTP_PROXY", "http://plain.example:1111");
        env.Set("HTTPS_PROXY", "http://secure.example:2222");

        var proxy = OutboundHttp.ResolveProxy();

        var resolved = proxy?.GetProxy(new Uri("https://api.example.com"));
        return resolved?.Host == "secure.example"
            ? null
            : $"HTTPS_PROXY 没有优先：实际 {resolved}";
    }

    /// <summary>
    /// 非法的代理值必须**真的拦住流量**。
    /// </summary>
    /// <remarks>
    /// <b>这是本能力的安全边界，也是第一版检查最严重的失误</b>：
    /// 当时只验对象形态（<c>IsBypassed == false</c> 且 <c>GetProxy == null</c>），
    /// 并断言"于是请求在 handler 层失败"。而 .NET 的语义恰好相反——
    /// <c>GetProxy</c> 返回 null 表示**该请求不走代理、直接发给目标**。
    /// 所以那条检查在缺陷存在时照样通过，把"直连"判成了"拦住"。
    ///
    /// <para>
    /// <b>现在起一个真实的本地服务来验</b>：把负责拦人的代理装进 handler，
    /// 发一个请求给本机端口。若请求成功（且服务端真的收到了连接），
    /// 就说明流量直连了——那就是缺陷。
    /// </para>
    /// </remarks>
    private static async Task<string?> InvalidBlocksForRealAsync()
    {
        using var env = new Env();
        env.Set("HTTPS_PROXY", "这不是一个地址");

        var warnings = new List<string>();
        var proxy = OutboundHttp.ResolveProxy(warnings);

        if (proxy is null)
            return "非法值退回 null——等于悄悄直连，流量会走错出口";

        if (warnings.Count == 0)
            return "非法值没有给出警告，用户无从知道代理没生效";

        // 起本地服务，观察它到底有没有收到连接。
        var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var accepted = 0;

        var acceptor = Task.Run(async () =>
        {
            try
            {
                using var client = await listener.AcceptTcpClientAsync();
                Interlocked.Increment(ref accepted);
                var stream = client.GetStream();
                var buffer = new byte[2048];
                // 这里只等客户端把请求头发过来，读多少无所谓；
                // 用显式丢弃令牌避免 CA2022（本仓库把警告当错误）。
                _ = await stream.ReadAsync(buffer.AsMemory(), CancellationToken.None);
                var body = "HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\nok";
                await stream.WriteAsync(System.Text.Encoding.ASCII.GetBytes(body));
            }
            catch (Exception)
            {
                // 没有连接进来就是我们要的结果。
            }
        });

        using var handler = new SocketsHttpHandler { Proxy = proxy };
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };

        var reached = false;
        try
        {
            using var response = await http.GetAsync($"http://127.0.0.1:{port}/");
            reached = response.IsSuccessStatusCode;
        }
        catch (Exception)
        {
            // 预期：连不上代理，请求失败。
        }

        listener.Stop();
        await Task.WhenAny(acceptor, Task.Delay(500));

        if (reached || accepted > 0)
            return $"非法代理值没有拦住流量：请求成功={reached}，目标服务收到连接={accepted}"
                + "——等于悄悄直连，流量会走错出口";

        return null;
    }

    /// <summary>NO_PROXY 里的主机不走代理。</summary>
    private static async Task<string?> NoProxyAsync()
    {
        await Task.CompletedTask;
        using var env = new Env();
        env.Set("HTTPS_PROXY", "http://proxy.corp.example:8080");
        env.Set("NO_PROXY", "localhost,.corp.example");

        var proxy = OutboundHttp.ResolveProxy();
        if (proxy is null) return "前置：代理没有读到";

        // WebProxy.AnnotateProxyForUri 是内部的，用 IsBypassed 观察。
        if (!proxy.IsBypassed(new Uri("http://localhost:3000")))
            return "localhost 没有被绕过";

        if (!proxy.IsBypassed(new Uri("https://api.corp.example")))
            return "*.corp.example 没有被绕过";

        return proxy.IsBypassed(new Uri("https://api.external.com"))
            ? "外部主机被错误地绕过了"
            : null;
    }

    /// <summary>NO_PROXY 的项要转义，不能当裸正则用。</summary>
    /// <remarks>
    /// <b>裸正则会让 <c>.</c> 匹配任意字符</b>：<c>.corp.example</c> 会被当作
    /// 正则 <c>.corp.example</c>，于是 <c>corpXexample</c> 也被绕过——
    /// 那是把不该走直连的请求放出去了。
    /// </remarks>
    private static async Task<string?> NoProxyEscapingAsync()
    {
        await Task.CompletedTask;
        using var env = new Env();
        env.Set("HTTPS_PROXY", "http://proxy.corp.example:8080");
        env.Set("NO_PROXY", ".corp.example");

        var proxy = OutboundHttp.ResolveProxy();
        if (proxy is null) return "前置：代理没有读到";

        // **这个主机不该被绕过**：它的名字里没有 `.corp.example` 这一段，
        // 只是恰好能被未转义的 `.` 通配到。
        return proxy.IsBypassed(new Uri("https://corpXexample"))
            ? "NO_PROXY 被当成裸正则用了——`corpXexample` 被错误绕过"
            : null;
    }

    /// <summary>没有任何代理变量时返回 null（交给系统默认）。</summary>
    private static async Task<string?> AbsentAsync()
    {
        await Task.CompletedTask;
        using var env = new Env();

        var proxy = OutboundHttp.ResolveProxy();
        return proxy is null
            ? null
            : "没有配置却给出了代理对象——会覆盖系统设置";
    }

    /// <summary>每个客户端构造点都传了带代理的 HttpClient。</summary>
    /// <remarks>
    /// <b>漏一个点就等于"某个功能不走代理"</b>：<c>/model</c> 切模型、
    /// <c>--doctor</c> 连通性检查、主连接器是三条独立路径。
    /// 前一阶段"CLI 参数没进 help"就是同一类漏接。
    /// </remarks>
    private static async Task<string?> WiringAsync()
    {
        await Task.CompletedTask;
        var root = FindRepoRoot();

        var program = Path.Combine(root, "src", "Dougong.Cli", "Program.cs");
        var doctor = Path.Combine(root, "src", "Dougong.Cli", "Doctor.cs");
        foreach (var file in new[] { program, doctor })
            if (!File.Exists(file)) return $"找不到 {file}";

        var missing = new List<string>();

        foreach (var file in new[] { program, doctor })
        {
            var text = File.ReadAllText(file);
            var calls = CountOccurrences(text, "ChatClientFactory.Create(");

            // **按前缀数，不按 "OutboundHttp.Create()" 数**：后者在把警告
            // 接出去之后变成了 `OutboundHttp.Create(Log.Warning)`，
            // 而第一版是精确匹配空括号——改一行调用就把这条检查变成永久红。
            var withProxy = CountOccurrences(text, "OutboundHttp.Create(");

            // 每个构造点都该带一个代理客户端。少一处就是"那条路径不走代理"。
            if (withProxy < calls)
                missing.Add($"{Path.GetFileName(file)}（{calls - withProxy} 处）");
        }

        return missing.Count == 0
            ? null
            : $"这些构造点没有接代理：{string.Join("、", missing)}";
    }

    /// <summary>
    /// 产品代码里不该有裸 <c>new HttpClient</c>。
    /// </summary>
    /// <remarks>
    /// <b>这条抓的是"绕过代理"</b>：<c>WebFetchTool</c> 的注释自己写着
    /// "自己 new HttpClient() 会绕过宿主代理、socket 也不会复用"，
    /// 而它的唯一调用点曾经正是裸 new。症状是企业环境里抓网页不走代理、
    /// 单独超时，而模型 API 那条正常——**只有那个功能坏**，很难归因。
    ///
    /// <para>
    /// 唯一允许的例外是 <c>HttpClientChatClient</c> 的兜底构造：
    /// 那是"调用方没注入"时的退路，生产路径全部显式注入。
    /// </para>
    /// </remarks>
    private static async Task<string?> NoBareClientAsync()
    {
        await Task.CompletedTask;
        var root = FindRepoRoot();
        var src = Path.Combine(root, "src");
        if (!Directory.Exists(src)) return $"找不到 {src}";

        var offenders = new List<string>();

        foreach (var file in Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains(Path.Combine("bin"), StringComparison.Ordinal)
                || file.Contains(Path.Combine("obj"), StringComparison.Ordinal))
                continue;

            var name = Path.GetFileName(file);

            // 允许的例外：连接器自身的兜底、以及 OutboundHttp 自己的实现。
            if (name is "HttpClientChatClient.cs" or "OutboundHttp.cs") continue;

            var text = File.ReadAllText(file);
            foreach (var line in text.Split(Environment.NewLine))
            {
                var trimmed = line.TrimStart();

                // 跳过注释：注释里提到 new HttpClient 是解释，不是创建。
                if (trimmed.StartsWith("//", StringComparison.Ordinal)
                    || trimmed.StartsWith("///", StringComparison.Ordinal))
                    continue;

                // **匹配要精确到"创建 HttpClient 实例"**：
                // - `new HttpClientChatClient` 是另一个类，不是出站客户端本身
                // - `<c>new HttpClient()</c>` 这种出现在注释里的说明文字也不算
                if (!trimmed.Contains("new HttpClient", StringComparison.Ordinal)) continue;
                if (trimmed.Contains("new HttpClientChatClient", StringComparison.Ordinal)) continue;
                if (trimmed.Contains("<c>", StringComparison.Ordinal)
                    || trimmed.Contains("</c>", StringComparison.Ordinal)) continue;

                offenders.Add(name);
            }
        }

        return offenders.Count == 0
            ? null
            : $"这些文件直接建了 HttpClient（会绕过代理）：{string.Join("、", offenders.Distinct())}";
    }

    private static int CountOccurrences(string text, string needle)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }

        return count;
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Dougong.slnx")))
                return directory.FullName;
            directory = directory.Parent;
        }

        return Directory.GetCurrentDirectory();
    }

    /// <summary>临时设置代理相关环境变量，退出时恢复原值。</summary>
    private sealed class Env : IDisposable
    {
        private readonly Dictionary<string, string?> _previous = new(StringComparer.Ordinal);

        public Env()
        {
            // 先全部清空：宿主环境里可能真的有代理设置，
            // 不清会让"没有配置"的用例在 CI 与外网环境里表现不同。
            foreach (var name in AllProxyVariables)
            {
                _previous[name] = Environment.GetEnvironmentVariable(name);
                Environment.SetEnvironmentVariable(name, null);
            }
        }

        public void Set(string name, string value)
        {
            if (!_previous.ContainsKey(name))
                _previous[name] = Environment.GetEnvironmentVariable(name);

            Environment.SetEnvironmentVariable(name, value);
        }

        public void Dispose()
        {
            foreach (var (name, value) in _previous)
                Environment.SetEnvironmentVariable(name, value);
        }
    }

    private static async Task<int> CheckAsync(string name, Func<Task<string?>> action)
    {
        try
        {
            var failure = await action().ConfigureAwait(false);
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
