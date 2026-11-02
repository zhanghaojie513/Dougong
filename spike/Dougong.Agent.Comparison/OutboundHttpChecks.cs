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
        failures += await CheckAsync("proxy-invalid-value-blocks-instead-of-direct", InvalidBlocksAsync);
        failures += await CheckAsync("proxy-no-proxy-bypasses-by-suffix", NoProxyAsync);
        failures += await CheckAsync("proxy-no-proxy-is-escaped-not-raw-regex", NoProxyEscapingAsync);
        failures += await CheckAsync("proxy-absent-falls-back-to-system-default", AbsentAsync);
        failures += await CheckAsync("proxy-is-wired-into-every-client-creation", WiringAsync);

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
    /// 非法的代理值必须**拦住**，不能悄悄直连。
    /// </summary>
    /// <remarks>
    /// <b>这是本能力的安全边界</b>：用户设了代理说明流量必须走代理。
    /// 静默直连会让内部请求发到外网，而失败至少是可见的。
    /// </remarks>
    private static async Task<string?> InvalidBlocksAsync()
    {
        await Task.CompletedTask;
        using var env = new Env();
        env.Set("HTTPS_PROXY", "这不是一个地址");

        var warnings = new List<string>();
        var proxy = OutboundHttp.ResolveProxy(warnings);

        if (proxy is null)
            return "非法值退回 null——等于悄悄直连，流量会走错出口";

        if (warnings.Count == 0)
            return "非法值没有给出警告，用户无从知道代理没生效";

        // 拦住的形态：IsBypassed 为 false 且 GetProxy 返回 null → 请求在 handler 层失败。
        if (proxy.IsBypassed(new Uri("https://api.example.com")))
            return "非法值被判为绕过代理——那等于直连";

        return proxy.GetProxy(new Uri("https://api.example.com")) is null
            ? null
            : "非法值仍给出了一个代理地址";
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
            var withProxy = CountOccurrences(text, "OutboundHttp.Create()");

            // 每个构造点都该带一个代理客户端。少一处就是"那条路径不走代理"。
            if (withProxy < calls)
                missing.Add($"{Path.GetFileName(file)}（{calls - withProxy} 处）");
        }

        return missing.Count == 0
            ? null
            : $"这些构造点没有接代理：{string.Join("、", missing)}";
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
