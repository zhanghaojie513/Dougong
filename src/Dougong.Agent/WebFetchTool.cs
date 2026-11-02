using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Dougong.Agent;

/// <summary>
/// 抓取一个网页并转成纯文本，供模型阅读。
/// </summary>
/// <remarks>
/// <b>解决什么问题</b>：模型没有网络时，用户贴一个链接它只能请用户复制内容。
/// 而"看看这个文档页"是极常见的诉求。dsh 的 <c>web-fetch-http</c> 就在补这一环。
///
/// <para>
/// <b>照 dsh 的 <c>dsh-web</c> 做，但只做抓取、不做搜索</b>：
/// <list type="bullet">
/// <item><b>抓取是"给我这个 URL 的内容"</b>，只需要 HTTP，不需要第三方账号。</item>
/// <item><b>搜索需要 provider</b>（dsh 有 exa / perplexity / deepseek 三个后端），
/// 每个都要 key 与计费。而"没有搜索"可以用抓取 + 已知文档站弥补，
/// 反过来不成立——所以先做抓取。</item>
/// <item>dsh 把两者做成同一 seam 下的两个能力，我们也保持这个划分：
/// 将来加搜索时是新增一个工具，不是改这个。</item>
/// </list>
/// </para>
///
/// <para>
/// <b>必须报 <see cref="ToolAccess.Network"/> 与目标域名</b>：这个工具会向外发请求，
/// 宿主有权据此决定要不要审批。报成 <c>None</c> 会让"允许联网"这个开关形同虚设。
/// </para>
///
/// <para>
/// <b>只接受 http/https——但要写清这不是安全边界</b>。<c>HttpClient</c> 本身
/// 就对 <c>file:</c>/<c>ftp:</c> 抛 <c>NotSupportedException</c>，实测确认过。
/// 这道检查的价值是**给出能照做的错误消息**（"本地文件请用 read_file"），
/// 而不是拦住本可能成功的越权——把那当成防线会让后来的人误以为它在防什么。
/// 真实的安全边界在内核与审批：抓取报 <see cref="ToolAccess.Network"/>，
/// 由宿主决定放不放行。
/// </para>
///
/// <para>
/// <b>不执行脚本</b>：这是文本抓取，不是浏览器。JS 渲染的页面拿到的会是空壳，
/// 而那必须如实告诉模型——否则它会以为页面真的没内容。
/// </para>
/// </remarks>
public sealed class WebFetchTool : IAgentTool
{
    /// <summary>正文最多保留多少字符。</summary>
    /// <remarks>
    /// 与工具结果上限一致（<see cref="ToolResultLimits.MaxContextChars"/>）。
    /// 网页正文常远超这个量，截断后由 spill 机制落盘可取回。
    /// </remarks>
    public const int MaxContentChars = ToolResultLimits.MaxContextChars;

    /// <summary>下载上限（字节）。</summary>
    /// <remarks>
    /// <b>必须在读的时候限，不能读完全量再判</b>：一个 2 GB 的文件会先吃满内存。
    /// 5 MB 足够装下绝大多数文档页，而超出这个量的页面模型也读不完。
    /// </remarks>
    public const int MaxDownloadBytes = 5 * 1024 * 1024;

    /// <summary>允许的协议。</summary>
    private static readonly string[] AllowedSchemes = ["http", "https"];

    private readonly HttpClient _http;

    /// <summary>构造，使用给定的 <see cref="HttpClient"/>。</summary>
    /// <remarks>
    /// <b>注入而不是自建</b>：宿主可能已经配了代理、超时、自定义 user-agent。
    /// 自己 <c>new HttpClient()</c> 会绕过那些设置，而且 socket 不会被复用。
    /// </remarks>
    public WebFetchTool(HttpClient http)
    {
        ArgumentNullException.ThrowIfNull(http);
        _http = http;
    }

    public ToolDescriptor Descriptor { get; } = new(
        "web_fetch", "web_fetch", "1.0.0",
        "抓取一个 http/https 链接并返回纯文本。不执行 JavaScript，动态渲染的页面可能为空。",
        JsonDocument.Parse(
            """
            {
              "type": "object",
              "properties": {
                "url": { "type": "string", "description": "要抓取的完整链接，含 http:// 或 https://。" }
              },
              "required": ["url"],
              "additionalProperties": false
            }
            """).RootElement.Clone());

    /// <summary>声明网络访问与目标域名——宿主据此决定是否需要审批。</summary>
    public ToolPermission DescribePermission(JsonElement arguments)
    {
        var url = arguments.TryGetProperty("url", out var u) ? u.GetString() : null;

        // 域名解析不出来时**仍然报 Network 但标 IsKnown=false**：
        // 报 None 会让"允许联网"的开关失效，而报一个空域名列表会让审批看起来是"内网请求"。
        if (url is null || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return new(ToolAccess.Network, IsKnown: false, [], [], []);

        return new(ToolAccess.Network, IsKnown: true, [], [], [uri.Host]);
    }

    /// <summary>
    /// 按 URL 声明读键：同一 URL 的两次抓取是读-读，不冲突；不同 URL 也互不阻塞。
    /// </summary>
    /// <remarks>
    /// <b>必须声明键</b>：不声明时退回 <see cref="ToolConcurrency.Serial"/>，
    /// 模型一次抓三个文档页就得排队。而这三次之间没有任何共享状态。
    /// <para>
    /// 键用规范化后的绝对 URL（小写 scheme/host）：<c>HTTP://Example.com</c>
    /// 与 <c>http://example.com</c> 指向同一资源，键不同就起不到互斥作用。
    /// </para>
    /// </remarks>
    public ToolExecutionPlan DescribeExecution(JsonElement arguments)
    {
        var url = arguments.TryGetProperty("url", out var u) ? u.GetString() : null;

        return Uri.TryCreate(url, UriKind.Absolute, out var uri)
            ? new(ToolConcurrency.Serial) { Reads = [uri.GetComponents(UriComponents.HttpRequestUrl, UriFormat.UriEscaped)] }
            : ToolExecutionPlan.Serial;
    }

    public IReadOnlyList<string> DescribeFileDependencies(JsonElement arguments) => [];

    public async ValueTask<AgentToolResult> ExecuteAsync(
        ToolInvocation invocation, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(invocation);

        var raw = ToolArguments.RequiredString(invocation.Arguments, "url");

        if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri))
            return Failure($"不是合法的绝对链接：{raw}");

        if (!AllowedSchemes.Contains(uri.Scheme, StringComparer.OrdinalIgnoreCase))
        {
            // **明确说出为什么**：模型分不清"协议不支持"与"网络不通"，
            // 而前者换协议就能解决，后者要等。
            return Failure(
                $"只支持 http/https，不接受 {uri.Scheme}。"
                + "本地文件请用 read_file（它有工作区限制）。");
        }

        try
        {
            var (status, contentType, body) = await DownloadAsync(uri, cancellationToken)
                .ConfigureAwait(false);

            var code = (int)status;
            if (code is < 200 or >= 300)
                return Failure($"HTTP {code}：{raw}");

            var text = ExtractText(body, contentType);

            // **空正文要说明可能是脚本渲染**：否则模型会断定页面没有内容。
            if (string.IsNullOrWhiteSpace(text))
            {
                return new AgentToolResult(JsonSerializer.SerializeToElement(new
                {
                    url = uri.ToString(),
                    status = (int)status,
                    contentType,
                    text = string.Empty,
                    note = "页面没有可提取的文本。可能是脚本渲染（本工具不执行 JavaScript）"
                        + "或内容需要登录。",
                }));
            }

            if (text.Length > MaxContentChars)
            {
                // 交给统一的截断逻辑（含 spill 取回）。这里只报长度，
                // 真正的裁剪发生在进入模型上下文时——与其它工具一致。
                text = text[..MaxContentChars];
            }

            return new AgentToolResult(JsonSerializer.SerializeToElement(new
            {
                url = uri.ToString(),
                status = (int)status,
                contentType,
                chars = text.Length,
                text,
            }));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (HttpRequestException ex)
        {
            return Failure($"抓取失败：{ex.Message}");
        }
        catch (TaskCanceledException)
        {
            // 没有调用方取消却超时：说清是超时，不是"用户取消"。
            return Failure($"抓取超时：{raw}");
        }
    }

    /// <summary>下载并按上限截断字节。</summary>
    private async Task<(HttpStatusCode Status, string? ContentType, string Body)>
        DownloadAsync(Uri uri, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);

        // 有些站点对没有 UA 的请求直接 403。给一个明确的身份，
        // 而不是伪装成浏览器——伪装会让站点以为能给它渲染 JS。
        request.Headers.UserAgent.ParseAdd("Dougong/1.0 (+https://github.com/zhanghaojie513/Dougong)");
        request.Headers.Accept.ParseAdd("text/html,application/xhtml+xml,text/plain,application/json;q=0.9,*/*;q=0.8");

        using var response = await _http
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);

        var contentType = response.Content.Headers.ContentType?.ToString();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);

        // **边读边限**：只读 MaxDownloadBytes + 1 字节就够判断是否超限，
        // 不需要把整个响应读完再比较大小。
        var buffer = new byte[MaxDownloadBytes];
        var total = 0;

        while (total < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(total), cancellationToken)
                .ConfigureAwait(false);
            if (read == 0) break;
            total += read;
        }

        // 编码：优先用声明的 charset，否则按 UTF-8 解（现代网页的绝大多数）。
        var charset = response.Content.Headers.ContentType?.CharSet;
        var encoding = ResolveEncoding(charset);

        return (response.StatusCode, contentType, encoding.GetString(buffer, 0, total));
    }

    /// <summary>按 charset 取编码；不认识就 UTF-8。</summary>
    private static Encoding ResolveEncoding(string? charset)
    {
        if (string.IsNullOrWhiteSpace(charset)) return Encoding.UTF8;

        try { return Encoding.GetEncoding(charset.Trim('"', ' ')); }
        catch (ArgumentException)
        {
            // 不认识的编码退回 UTF-8，不猜——猜错会让整页变成乱码，
            // 而乱码比"说明编码不支持"更难排查。
            return Encoding.UTF8;
        }
    }

    /// <summary>
    /// 从响应体里提取可读文本。
    /// </summary>
    /// <remarks>
    /// <b>不引 HTML 解析库</b>：目标只是"去掉标签、保留文字"。引一个解析器会带来
    /// 依赖与它自己的容错行为（而我们的输入是不受信任的任意网页）。
    /// 这里用保守的正则：宁可留下一点标签残留，也不要把正文吃掉。
    /// <para>
    /// <b>先删 script/style 整段，再删其余标签</b>：顺序反了会让脚本内容漏进正文。
    /// </para>
    /// </remarks>
    internal static string ExtractText(string body, string? contentType)
    {
        // 非 HTML 原样返回：JSON 与纯文本不该被"清洗"。
        if (contentType is not null
            && !contentType.Contains("html", StringComparison.OrdinalIgnoreCase)
            && !contentType.Contains("xml", StringComparison.OrdinalIgnoreCase))
        {
            return body.Trim();
        }

        if (body.Length == 0) return string.Empty;

        // **不能加 NonBacktracking**：这个模式用了反向引用（\1），
        // 而 NonBacktracking 明确不支持它——一跑就抛 NotSupportedException。
        // 改用显式超时防 ReDoS：输入是任意网页，灾难性回溯会挂住整轮。
        var withoutScripts = Regex.Replace(
            body, @"<(script|style|noscript|svg)\b[^>]*>.*?</\1>",
            " ",
            RegexOptions.IgnoreCase | RegexOptions.Singleline,
            TimeSpan.FromSeconds(2));

        // 块级元素换行：否则相邻段落会粘成一行，模型读不出结构。
        // 这两条没有反向引用，可以用 NonBacktracking（线性时间，不受输入影响）。
        var withBreaks = Regex.Replace(
            withoutScripts, @"<(br|/p|/div|/li|/h[1-6]|/tr)\b[^>]*>",
            "\n", RegexOptions.IgnoreCase | RegexOptions.NonBacktracking);

        var withoutTags = Regex.Replace(
            withBreaks, @"<[^>]+>", " ",
            RegexOptions.IgnoreCase | RegexOptions.NonBacktracking);

        var decoded = WebUtility.HtmlDecode(withoutTags);

        // 压空白：HTML 里的缩进与换行对模型没有意义，只会浪费预算。
        var lines = decoded
            .Split('\n')
            .Select(line => Regex.Replace(line, @"[ \t\u00A0]+", " ").Trim())
            .Where(line => line.Length > 0);

        return string.Join('\n', lines).Trim();
    }

    private static AgentToolResult Failure(string diagnostic) =>
        new(JsonSerializer.SerializeToElement(new { error = diagnostic }));
}
