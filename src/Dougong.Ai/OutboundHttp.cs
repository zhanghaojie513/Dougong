using System.Net;

namespace Dougong.Ai;

/// <summary>
/// 出站 HTTP 客户端的统一构造：代理、超时与连接复用。
/// </summary>
/// <remarks>
/// <b>解决什么问题</b>：企业环境里模型 API 只能通过公司代理访问，
/// 而 <c>HttpClient</c> 的默认行为**不读 <c>HTTP_PROXY</c> / <c>HTTPS_PROXY</c>
/// 环境变量**——它用 <see cref="HttpClient.DefaultProxy"/>，那是系统级设置
/// （Windows 读 WinINET，Linux 往往是空的）。于是同一份配置在开发机上能跑、
/// 进了公司网络就全部超时，而错误消息只会说"连接超时"。
///
/// <para>
/// <b>不做成"自动探测"</b>：显式读环境变量，读不到就用系统默认。
/// 自动去猜（比如试探常见代理端口）会让"为什么我的请求走了代理"
/// 变成一个查不出来的问题——而它涉及流量去向，不该猜。
/// </para>
///
/// <para>
/// <b>凭据从哪里来</b>：<see cref="Uri.UserInfo"/>（<c>http://user:pass@proxy</c>）
/// 会被用上。<b>但那条路径会把密码写进配置与环境变量</b>，
/// 所以文档里要说明：优先用不带凭据的代理地址 + 系统凭据，
/// 只有在必须时才对地址嵌入凭据。
/// </para>
///
/// <para>
/// <b>超时与连接复用的分工</b>：超时在 <see cref="HttpClient.Timeout"/> 上设，
/// 连接复用靠 <see cref="SocketsHttpHandler"/> 的默认行为——
/// <b>不复用会导致每次请求重新 TLS 握手</b>，在流式场景里表现为每个 chunk
/// 之间都有可疑的间隔。
/// </para>
/// </remarks>
public static class OutboundHttp
{
    /// <summary>读代理配置的环境变量名，按优先级。</summary>
    /// <remarks>
    /// <b>两套都认</b>：<c>HTTP_PROXY</c> 是 curl 系约定，<c>ALL_PROXY</c> 是
    /// 它不支持分协议时的兜底。企业环境里两者都可能出现，只认一个会让
    /// "我明明设了"变成排查负担。
    /// </remarks>
    public static readonly string[] ProxyVariables =
        ["HTTPS_PROXY", "https_proxy", "HTTP_PROXY", "http_proxy", "ALL_PROXY", "all_proxy"];

    /// <summary>不走代理的主机列表变量。</summary>
    public static readonly string[] NoProxyVariables = ["NO_PROXY", "no_proxy"];

    /// <summary>默认超时：5 分钟。</summary>
    /// <remarks>
    /// <b>流式请求要看整体</b>：<see cref="HttpClient.Timeout"/> 管的是
    /// "从发出到读完响应体"，一次长回答可能跑几分钟。设短了会在中途被砍，
    /// 而错误看起来像"provider 断流"。
    /// </remarks>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(5);

    /// <summary>
    /// 按环境变量（或系统默认）构造一个客户端。
    /// </summary>
    /// <param name="timeout">超时；null 用 <see cref="DefaultTimeout"/>。</param>
    /// <param name="warnings">
    /// 代理配置有问题时的说明。**不能静默忽略**：用户设了 <c>HTTPS_PROXY</c>
    /// 却没生效，而程序一声不吭，那比报错更难查。
    /// </param>
    public static HttpClient Create(TimeSpan? timeout = null, IList<string>? warnings = null) =>
        new(BuildHandler(warnings))
        {
            Timeout = timeout ?? DefaultTimeout,
        };

    /// <summary>
    /// 构造 handler，把代理配置接上。
    /// </summary>
    /// <param name="warnings">代理配置问题说明。</param>
    public static SocketsHttpHandler BuildHandler(IList<string>? warnings = null)
    {
        var handler = new SocketsHttpHandler
        {
            // 连接复用：不复用会让每个请求重新握手，流式场景表现为
            // 每个 chunk 之间都有可疑的间隔。
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
        };

        if (ResolveProxy(warnings) is { } proxy)
            handler.Proxy = proxy;

        return handler;
    }

    /// <summary>
    /// 解析代理设置：环境变量优先，否则返回 null（交给系统默认）。
    /// </summary>
    /// <param name="warnings">解析失败的说明出口。</param>
    public static IWebProxy? ResolveProxy(IList<string>? warnings = null)
    {
        string? raw = null;
        string? fromVariable = null;

        foreach (var name in ProxyVariables)
        {
            var value = Environment.GetEnvironmentVariable(name);
            if (string.IsNullOrWhiteSpace(value)) continue;

            raw = value;
            fromVariable = name;
            break;
        }

        if (raw is null) return null;

        if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            // **不退回系统默认**：用户明确设了代理而它无效，
            // 悄悄走直连会让流量去了不该去的地方。
            warnings?.Add(
                $"{fromVariable}='{raw}' 不是合法的 HTTP 代理地址，本次请求不会使用代理。"
                + "正确形态：http://proxy.example.com:8080");

            return new BlockingProxy();
        }

        var proxy = new WebProxy(uri);

        if (ParseNoProxy() is { Count: > 0 } bypass)
            proxy.BypassList = [.. bypass];

        return proxy;
    }

    /// <summary>
    /// 解析 <c>NO_PROXY</c>：逗号分隔的主机或域名。
    /// </summary>
    /// <remarks>
    /// <b>转成正则</b>：<see cref="WebProxy.BypassList"/> 只接受正则。
    /// 而用户写的是 <c>localhost,.corp.example</c> 这种形态——
    /// 直接当正则用会把 <c>.</c> 解释成"任意字符"，于是 <c>notcorpXexample</c>
    /// 也被绕过。这里逐段转义后拼成"以它结尾"的模式。
    /// </remarks>
    public static IReadOnlyList<string> ParseNoProxy()
    {
        foreach (var name in NoProxyVariables)
        {
            var value = Environment.GetEnvironmentVariable(name);
            if (string.IsNullOrWhiteSpace(value)) continue;

            var patterns = new List<string>();
            foreach (var part in value.Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                var host = part.Trim().TrimStart('.');
                if (host.Length == 0 || host == "*") continue;

                // **`WebProxy.BypassList` 匹配的是整个 URI 文本，不是主机名**。
                // 因此模式要允许任意 scheme/端口夹在中间，并且不能用 `$` 锚定末尾
                // （URL 后面还有路径，`http://localhost:3000/x` 的末尾不是 `localhost`）。
                //
                // 第一版写成 `^.*host$`：它对 `https://api.corp.example` 恰好成立
                // （那串正好以它结尾），但 `http://localhost:3000` 不成立——
                // 检查因此红在 localhost 上。这个坑只在有端口或路径时暴露。
                var escaped = System.Text.RegularExpressions.Regex.Escape(host);
                patterns.Add(
                    "^.*://[^/]*" + escaped + "(:[0-9]+)?((/.*)?)$");
            }

            return patterns;
        }

        return [];
    }

    /// <summary>
    /// 配置无效时用的代理：拒绝一切出站请求。
    /// </summary>
    /// <remarks>
    /// <b>为什么不是"退回直连"</b>：用户设了代理说明流量必须走代理。
    /// 悄悄直连会让内部请求发到外网——那是安全边界的破坏，
    /// 而失败至少是可见的。
    /// </remarks>
    private sealed class BlockingProxy : IWebProxy
    {
        public ICredentials? Credentials { get; set; }

        public Uri? GetProxy(Uri destination) => null;

        // 永远"需要代理"，但 GetProxy 返回 null，于是请求在 handler 层失败。
        public bool IsBypassed(Uri host) => false;
    }
}
