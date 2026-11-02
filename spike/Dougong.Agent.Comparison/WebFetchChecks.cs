using System.Net;
using System.Text;
using System.Text.Json;
using Dougong.Agent;

namespace Dougong.Agent.Comparison;

/// <summary>
/// 联网抓取的验收。
/// </summary>
/// <remarks>
/// <b>可观察面是"模型拿到的文本"，以及它拒绝什么</b>：
/// <list type="bullet">
/// <item>HTML 变成纯文本，<b>脚本内容不出现</b></item>
/// <item>非 HTML（JSON/纯文本）原样返回，不被"清洗"</item>
/// <item>空正文要说明可能是脚本渲染——否则模型断定页面没内容</item>
/// <item><c>file://</c> 必须拒绝（否则成了绕过路径守卫的旁路）</item>
/// <item>权限声明为 Network 且带目标域名</item>
/// </list>
/// </remarks>
public static class WebFetchChecks
{
    public static async Task<int> RunAsync()
    {
        using var server = new ThrowawayServer();

        var failures = 0;
        failures += await CheckAsync("fetch-extracts-text-from-html", () => HtmlAsync(server));
        failures += await CheckAsync("fetch-drops-script-content", () => ScriptsDroppedAsync(server));
        failures += await CheckAsync("fetch-passes-non-html-through", () => NonHtmlAsync(server));
        failures += await CheckAsync("fetch-explains-empty-body", () => EmptyBodyAsync(server));
        failures += await CheckAsync("fetch-rejects-non-http-scheme", () => SchemeAsync());
        failures += await CheckAsync("fetch-reports-http-status", () => StatusAsync(server));
        failures += await CheckAsync("fetch-declares-network-and-host", () => PermissionAsync());
        failures += await CheckAsync("fetch-permission-unknown-for-bad-url", InvalidUrlPermission);
        failures += await CheckAsync("fetch-caps-download-size", () => SizeCapAsync(server));

        Console.WriteLine(failures == 0
            ? "WEB-FETCH: ALL PASS"
            : $"WEB-FETCH: {failures} FAILURE(S)");
        return failures;
    }

    private static async Task<string?> HtmlAsync(ThrowawayServer server)
    {
        var result = await FetchAsync(server, "/html");
        var text = Read(result, "text");

        if (text is null) return "没有返回 text";

        // 段落必须分行：粘成一行会让模型读不出结构。
        if (!text.Contains("第一段", StringComparison.Ordinal)
            || !text.Contains("第二段", StringComparison.Ordinal))
            return $"正文缺失：{text}";

        if (!text.Contains('\n')) return $"段落没有分行：{text}";

        // HTML 实体要解码，否则模型看到 &amp; 会当成原文。
        return text.Contains('&') && !text.Contains("&amp;")
            ? null
            : $"实体没有解码：{text}";
    }

    /// <summary>脚本与样式内容不能漏进正文。</summary>
    /// <remarks>
    /// <b>这是最容易写错的一处</b>：先删标签再删 script 会让脚本内容
    /// 变成正文的一部分。顺序必须是先整段删 script/style。
    /// </remarks>
    private static async Task<string?> ScriptsDroppedAsync(ThrowawayServer server)
    {
        var result = await FetchAsync(server, "/scripts");
        var text = Read(result, "text");

        if (text is null) return "没有返回 text";
        if (text.Contains("JS_SECRET", StringComparison.Ordinal))
            return $"脚本内容漏进了正文：{text}";
        if (text.Contains("CSS_SECRET", StringComparison.Ordinal))
            return $"样式内容漏进了正文：{text}";

        return text.Contains("真正的正文", StringComparison.Ordinal)
            ? null
            : $"正文被误删：{text}";
    }

    private static async Task<string?> NonHtmlAsync(ThrowawayServer server)
    {
        var result = await FetchAsync(server, "/json");
        var text = Read(result, "text");

        // JSON 不该被"清洗"：那些尖括号判断会把合法数据当标签删掉。
        return text == "{\"a\":1}"
            ? null
            : $"非 HTML 被改动了：{text}";
    }

    /// <summary>空正文要给出原因猜测。</summary>
    /// <remarks>
    /// <b>不说就会误导</b>：模型会断定"这个页面没有内容"，
    /// 而实际是脚本渲染——它无法自己想到这一点。
    /// </remarks>
    private static async Task<string?> EmptyBodyAsync(ThrowawayServer server)
    {
        var result = await FetchAsync(server, "/empty");
        var note = Read(result, "note");

        return note is not null && note.Contains("脚本", StringComparison.Ordinal)
            ? null
            : $"空正文没有解释：note={note}";
    }

    /// <summary>
    /// 只接受 http/https，且拒绝消息要能照做。
    /// </summary>
    /// <remarks>
    /// <b>这条检查的是错误消息，不是安全边界</b>：<c>HttpClient</c> 本身
    /// 就对 <c>file:</c>/<c>ftp:</c> 抛 <c>NotSupportedException</c>（实测确认）。
    /// 我们的守卫的增量价值是"本地文件请用 read_file"这句指引——
    /// 裸 <c>NotSupportedException</c> 会让模型以为 URL 写错了，然后重试。
    /// <para>
    /// 负控方式说明：删掉守卫后这条会红，但**红的原因是异常类型不对**，
    /// 不是"文件被读到了"。把它当成越权防线是误解。
    /// </para>
    /// </remarks>
    private static async Task<string?> SchemeAsync()
    {
        var tool = new WebFetchTool(new HttpClient());

        foreach (var url in new[]
                 {
                     "file:///etc/passwd",
                     "file:///C:/Windows/win.ini",
                     "ftp://example.com/x",
                 })
        {
            var result = await tool.ExecuteAsync(
                new ToolInvocation("c", "web_fetch", Args(url)), CancellationToken.None);

            var error = Read(result, "error");
            if (error is null) return $"{url} 没有被拒绝";

            // 拒绝理由要说清"该用什么"：只报"不支持"会让模型反复试。
            if (!error.Contains("http", StringComparison.OrdinalIgnoreCase))
                return $"{url} 的拒绝理由没提到支持的协议：{error}";
        }

        return null;
    }

    private static async Task<string?> StatusAsync(ThrowawayServer server)
    {
        var result = await FetchAsync(server, "/404");
        var error = Read(result, "error");

        return error is not null && error.Contains("404", StringComparison.Ordinal)
            ? null
            : $"非 2xx 没有报状态码：{error}";
    }

    /// <summary>权限声明必须是 Network + 目标域名。</summary>
    /// <remarks>
    /// <b>报 None 会让"允许联网"的开关形同虚设</b>；报空域名列表会让
    /// 审批界面看起来像"内网请求"。两者都会让用户做出错误判断。
    /// </remarks>
    private static Task<string?> PermissionAsync()
    {
        var tool = new WebFetchTool(new HttpClient());
        var permission = tool.DescribePermission(Args("https://example.com/docs/page"));

        if (permission.Access is not ToolAccess.Network)
            return Task.FromResult<string?>($"权限应为 Network，实际 {permission.Access}");

        if (!permission.IsKnown)
            return Task.FromResult<string?>("合法 URL 的权限不应是未知");

        return Task.FromResult<string?>(permission.Domains.Contains("example.com", StringComparer.Ordinal)
            ? null
            : $"域名没报出来：{string.Join(",", permission.Domains)}");
    }

    /// <summary>URL 解析不了时权限必须标未知，而不是报 None。</summary>
    private static Task<string?> InvalidUrlPermission()
    {
        var tool = new WebFetchTool(new HttpClient());
        var permission = tool.DescribePermission(Args("这不是 URL"));

        if (permission.Access is not ToolAccess.Network)
            return Task.FromResult<string?>($"仍应是 Network（会发请求），实际 {permission.Access}");

        return Task.FromResult<string?>(permission.IsKnown
            ? "无法解析的 URL 不该声称权限已知"
            : null);
    }

    /// <summary>超大响应不会吃满内存，且不会崩。</summary>
    private static async Task<string?> SizeCapAsync(ThrowawayServer server)
    {
        // 服务端发 12 MB；工具只该读 5 MB 上限。
        var result = await FetchAsync(server, "/huge");
        var text = Read(result, "text");

        if (text is null) return "超大响应没有返回 text（可能读崩了）";
        return text.Length <= WebFetchTool.MaxContentChars
            ? null
            : $"正文超过上限：{text.Length}";
    }

    // ── 支撑 ─────────────────────────────────────────────────────

    private static JsonElement Args(string url) =>
        JsonDocument.Parse(JsonSerializer.Serialize(new { url })).RootElement.Clone();

    private static async Task<AgentToolResult> FetchAsync(ThrowawayServer server, string path)
    {
        var tool = new WebFetchTool(new HttpClient { Timeout = TimeSpan.FromSeconds(20) });
        return await tool.ExecuteAsync(
            new ToolInvocation("c", "web_fetch", Args(server.BaseUrl + path)),
            CancellationToken.None);
    }

    private static string? Read(AgentToolResult result, string property) =>
        result.Content.TryGetProperty(property, out var value)
            && value.ValueKind is JsonValueKind.String
            ? value.GetString()
            : null;

    /// <summary>本地 HTTP 服务，按路径给出不同形态的响应。</summary>
    private sealed class ThrowawayServer : IDisposable
    {
        private readonly HttpListener _listener;
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _loop;

        public ThrowawayServer()
        {
            var probe = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
            probe.Start();
            var port = ((System.Net.IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();

            BaseUrl = $"http://127.0.0.1:{port}";
            _listener = new HttpListener();
            _listener.Prefixes.Add(BaseUrl + "/");
            _listener.Start();
            _loop = Task.Run(ServeAsync);
        }

        public string BaseUrl { get; }

        private async Task ServeAsync()
        {
            while (!_stop.IsCancellationRequested)
            {
                HttpListenerContext context;
                try { context = await _listener.GetContextAsync().ConfigureAwait(false); }
                catch { return; }

                var path = context.Request.Url?.AbsolutePath ?? "/";
                var (code, type, body) = path switch
                {
                    "/html" => (200, "text/html; charset=utf-8",
                        "<html><head><title>T</title></head><body>"
                        + "<h1>标题</h1><p>第一段</p><p>第二段 &amp; 转义</p></body></html>"),

                    "/scripts" => (200, "text/html; charset=utf-8",
                        "<html><head><style>.x{color:CSS_SECRET}</style>"
                        + "<script>var s='JS_SECRET';</script></head>"
                        + "<body><p>真正的正文</p></body></html>"),

                    "/json" => (200, "application/json", "{\"a\":1}"),

                    "/empty" => (200, "text/html",
                        "<html><body><script>var x=1;</script></body></html>"),

                    "/404" => (404, "text/html", "not found"),

                    "/huge" => (200, "text/html",
                        "<p>" + new string('x', 12 * 1024 * 1024) + "</p>"),

                    _ => (200, "text/plain", "ok"),
                };

                context.Response.StatusCode = code;
                context.Response.ContentType = type;

                var bytes = Encoding.UTF8.GetBytes(body);
                context.Response.ContentLength64 = bytes.Length;
                await context.Response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
                context.Response.Close();
            }
        }

        public void Dispose()
        {
            _stop.Cancel();
            try { _listener.Stop(); } catch { /* 已停 */ }
            try { _loop.Wait(TimeSpan.FromSeconds(3)); } catch { /* 超时不阻塞 */ }
            _listener.Close();
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
