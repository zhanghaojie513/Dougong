using System.Net;

namespace Dougong.Ai;

/// <summary>
/// 出站 HTTP 客户端的统一构造：代理、超时与连接复用。
/// </summary>
/// <remarks>
/// <b>解决什么问题</b>：企业环境里模型 API 只能通过公司代理访问，
/// 而代理配置**必须是一处能查、能说清的地方**。
///
/// <para>
/// <b>关于 <c>HttpClient</c> 的默认行为，第一版的说明是错的</b>：
/// 它写"默认不读 <c>HTTP_PROXY</c>/<c>HTTPS_PROXY</c>"。实际
/// <see cref="HttpClient.DefaultProxy"/> 在三个平台上都会**先读**这些环境变量
/// （含 <c>ALL_PROXY</c> 与 <c>NO_PROXY</c>），读不到才回落到系统设置。
/// 所以本类不是"补上缺失的能力"，而是三件事：
/// <list type="number">
/// <item><b>让"代理是否生效"可观察</b>：非法值给出警告，而不是让请求
/// 表现成莫名其妙的超时；</item>
/// <item><b>保证非法值时请求失败而不是直连</b>——这是安全边界，见 <c>BlockingProxy</c>；</item>
/// <item><b>把配置收敛到一处</b>：避免某个模块自己 <c>new HttpClient()</c>
/// 而绕过宿主的设置。</item>
/// </list>
/// </para>
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
    /// <remarks>
    /// <b>它不再是公开重载</b>：复检发现 <c>OutboundHttp.Create(null)</c> 在加了
    /// <see cref="Create(Action{string}, TimeSpan?)"/> 之后会二义（null 同时能转
    /// <c>TimeSpan?</c> 与 <c>Action&lt;string&gt;?</c>）。而它本身在仓库里
    /// 除内部转发外零调用——收成私有既消掉二义，也少一个没人用的公开入口。
    /// </remarks>
    private static HttpClient CreateWithWarnings(
        TimeSpan? timeout, IList<string>? warnings) =>
        new(BuildHandler(warnings))
        {
            Timeout = timeout ?? DefaultTimeout,
        };

    /// <summary>
    /// 构造客户端，并把代理配置问题交出去。
    /// </summary>
    /// <remarks>
    /// <b>为什么单独一个重载</b>：带 <c>warnings</c> 的那个入口是个可选出口，
    /// 而四个生产调用点全都用了无参形式——于是非法代理那条警告永不触发，
    /// 正是注释说"不能静默忽略"的那件事。这里把出口接到回调上，
    /// 调用方只写一行，不容易再漏。
    /// <para>
    /// <b>用回调而不是直接依赖日志库</b>：Dougong.Ai 不该知道宿主的日志设施。
    /// </para>
    /// </remarks>
    /// <param name="onWarning">收到代理配置问题的回调；null 表示丢弃。</param>
    /// <param name="timeout">超时；null 用 <see cref="DefaultTimeout"/>。</param>
    public static HttpClient Create(Action<string>? onWarning, TimeSpan? timeout = null)
    {
        List<string>? warnings = onWarning is null ? null : [];
        var client = CreateWithWarnings(timeout, warnings);

        if (warnings is not null)
            foreach (var warning in warnings) onWarning!(warning);

        return client;
    }

    /// <summary>
    /// 构造 handler，把代理配置接上。
    /// </summary>
    /// <remarks>
    /// <b>公开是为了让宿主能包自己的 HttpClient</b>（例如设自定义 handler 属性），
    /// 本仓库内只有 <see cref="CreateWithWarnings"/> 在用。
    /// </remarks>
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
    /// 解析代理设置。
    /// </summary>
    /// <returns>
    /// 环境变量配置的代理；<b>没有任何代理变量时返回 null</b>（落到系统默认）；
    /// <b>配置了但值非法时返回一个让请求失败的代理</b>——见 <c>BlockingProxy</c>。
    /// </returns>
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
                $"{fromVariable}='{raw}' 不是合法的 HTTP 代理地址。"
                + "为避免流量绕过代理直连，本次出站请求会失败。"
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
    /// 配置无效时用的代理：让所有出站请求失败。
    /// </summary>
    /// <remarks>
    /// <b>为什么不是"退回直连"</b>：用户设了代理说明流量必须走代理。
    /// 悄悄直连会让内部请求发到外网——那是安全边界的破坏，
    /// 而失败至少是可见的。
    ///
    /// <para>
    /// <b>第一版写错了，而且错的方向正好相反</b>：当时的实现是
    /// <c>IsBypassed =&gt; false</c> 加 <c>GetProxy =&gt; null</c>，
    /// 注释说"于是请求在 handler 层失败"。而实际语义是
    /// <b><see cref="IWebProxy.GetProxy"/> 返回 null 表示"该请求不走代理、
    /// 直接发给目标服务器"</b>——流量照常直连，与承诺的完全相反。
    /// 实测：给 <see cref="SocketsHttpHandler"/> 装上这个代理后，
    /// 本机服务的请求返回 200、连接计数 1。
    /// </para>
    ///
    /// <para>
    /// <b>现在怎么做</b>：返回一个**连不上的代理地址**，
    /// 于是请求去连它并失败——那才是"拦住"。用回环 + 保留端口 9
    /// （discard，通常无人监听）而不是编一个域名：域名解析本身也是一次出站，
    /// 而那正是要避免的。
    /// </para>
    /// </remarks>
    private sealed class BlockingProxy : IWebProxy
    {
        /// <summary>必然连不上的代理地址。</summary>
        private static readonly Uri Unreachable = new("http://127.0.0.1:9/");

        public ICredentials? Credentials { get; set; }

        // 所有请求都"要走代理"，而这个代理连不上 → 请求失败，不是直连。
        public Uri? GetProxy(Uri destination) => Unreachable;

        // 永不绕过：绕过就等于直连。
        public bool IsBypassed(Uri host) => false;
    }
}
