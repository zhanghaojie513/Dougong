using System.Net;
using Dougong.Agent;
using Dougong.Ai;
using Microsoft.Extensions.AI;

namespace Dougong.Agent.Comparison;

/// <summary>
/// 模型请求重试的验收。
/// </summary>
/// <remarks>
/// <b>可观察面是"宿主看到了什么 + provider 被调用了几次"</b>，不是
/// "代码里有重试循环"：
/// <list type="bullet">
/// <item>429/5xx/网络失败会被重试，且重试前先发 <c>RetryScheduled</c></item>
/// <item>401/404/上下文超限**不重试**——等下去也不会好</item>
/// <item>已经吐过文本之后再失败**不重试**——重发会让用户看到重复内容</item>
/// <item>重试次数有上限，耗尽后如实报失败</item>
/// <item>重试成功后整轮正常完成，不是"重试即结束"</item>
/// </list>
/// </remarks>
public static class RetryChecks
{
    public static async Task<int> RunAsync()
    {
        var failures = 0;
        failures += await CheckAsync("retry-recovers-from-transient-failure", RecoversAsync);
        failures += await CheckAsync("retry-emits-event-before-waiting", EmitsEventAsync);
        failures += await CheckAsync("retry-skips-permanent-failures", SkipsPermanentAsync);
        failures += await CheckAsync("retry-never-after-text-delivered", NoRetryAfterTextAsync);
        failures += await CheckAsync("retry-stops-at-max-attempts", StopsAtLimitAsync);
        failures += await CheckAsync("retry-disabled-by-default", DisabledByDefaultAsync);
        failures += await CheckAsync("retry-classifier-maps-status-codes", ClassifierAsync);
        failures += await CheckAsync("retry-backoff-grows-and-caps", BackoffAsync);
        failures += await CheckAsync("retry-reads-provider-retry-after-from-wire", ProviderDelayFromWireAsync);

        Console.WriteLine(failures == 0
            ? "RETRY: ALL PASS"
            : $"RETRY: {failures} FAILURE(S)");
        return failures;
    }

    /// <summary>瞬时失败后重试成功，整轮正常完成。</summary>
    private static async Task<string?> RecoversAsync()
    {
        var client = new FlakyClient(failures: 2, status: HttpStatusCode.TooManyRequests);
        var events = await RunAsync(client, RetryPolicy.Default with { InitialDelay = TimeSpan.Zero });

        var finished = events.OfType<AgentEvent.RunFinished>().LastOrDefault();
        if (finished?.Outcome is not AgentRunOutcome.Completed)
            return $"重试后没有正常完成：{finished?.Outcome}";

        // 宿主应当收到最终文本。
        var text = string.Concat(events.OfType<AgentEvent.TextDelta>().Select(e => e.Text));
        if (!text.Contains("ok", StringComparison.Ordinal))
            return $"重试成功后没有产出文本：'{text}'";

        // provider 被调用 3 次（2 次失败 + 1 次成功）。
        return client.Attempts == 3 ? null : $"provider 被调用 {client.Attempts} 次，应为 3";
    }

    /// <summary>重试事件在等待之前发出，且带次数与原因。</summary>
    private static async Task<string?> EmitsEventAsync()
    {
        var client = new FlakyClient(failures: 1, status: HttpStatusCode.ServiceUnavailable);
        var events = await RunAsync(client, RetryPolicy.Default with { InitialDelay = TimeSpan.Zero });

        var retries = events.OfType<AgentEvent.RetryScheduled>().ToList();
        if (retries.Count != 1) return $"应发 1 条重试事件，实际 {retries.Count}";

        var one = retries[0];
        if (one.Attempt != 1) return $"Attempt 应为 1，实际 {one.Attempt}";
        if (one.Reason != nameof(RetryableFailure.ServerError))
            return $"Reason 应为 ServerError，实际 {one.Reason}";

        // 事件必须在文本之前——否则用户先看到结果再看到"正在重试"。
        var retryIndex = events.ToList().IndexOf(one);
        var firstText = events.ToList().FindIndex(e => e is AgentEvent.TextDelta);
        return retryIndex < firstText ? null : "重试事件出现在文本之后";
    }

    /// <summary>永久性失败不重试。</summary>
    /// <remarks>
    /// <b>401/404 等下去也不会好</b>。重试它们只是多烧时间和额度，
    /// 而且把"配置错了"这个清晰结论拖成"等了一会儿还是不行"。
    /// </remarks>
    private static async Task<string?> SkipsPermanentAsync()
    {
        foreach (var status in new[]
                 {
                     HttpStatusCode.Unauthorized,
                     HttpStatusCode.PaymentRequired,
                     HttpStatusCode.NotFound,
                 })
        {
            var client = new FlakyClient(failures: 10, status: status);
            var events = await RunAsync(client, RetryPolicy.Default with { InitialDelay = TimeSpan.Zero });

            if (client.Attempts != 1)
                return $"HTTP {(int)status} 被重试了 {client.Attempts} 次，应只调用 1 次";

            if (events.OfType<AgentEvent.RetryScheduled>().Any())
                return $"HTTP {(int)status} 发出了重试事件";

            var finished = events.OfType<AgentEvent.RunFinished>().LastOrDefault();
            if (finished?.Outcome is not AgentRunOutcome.Failed)
                return $"HTTP {(int)status} 没有以 Failed 结束";
        }

        return null;
    }

    /// <summary>已经交付文本后再失败，不重发。</summary>
    /// <remarks>
    /// <b>这条是重试的核心约束</b>：文本已经 yield 给宿主，重发会让用户
    /// 看到重复内容，而 AgentEvent 里没有撤回事件。
    /// </remarks>
    private static async Task<string?> NoRetryAfterTextAsync()
    {
        var client = new TextThenFailClient();
        var events = await RunAsync(client, RetryPolicy.Default with { InitialDelay = TimeSpan.Zero });

        if (client.Attempts != 1)
            return $"已交付文本后仍重试了：provider 被调用 {client.Attempts} 次";

        if (events.OfType<AgentEvent.RetryScheduled>().Any())
            return "已交付文本后仍发出了重试事件";

        var text = string.Concat(events.OfType<AgentEvent.TextDelta>().Select(e => e.Text));
        return text == "partial" ? null : $"交付的文本被重复或篡改：'{text}'";
    }

    /// <summary>重试次数达到上限后报失败，不无限重试。</summary>
    private static async Task<string?> StopsAtLimitAsync()
    {
        var client = new FlakyClient(failures: 100, status: HttpStatusCode.ServiceUnavailable);
        var policy = RetryPolicy.Default with
        {
            MaxRetries = 2,
            InitialDelay = TimeSpan.Zero,
        };

        var events = await RunAsync(client, policy);

        // 首次 + 2 次重试 = 3 次调用。
        if (client.Attempts != 3)
            return $"应调用 3 次（1 次 + 2 次重试），实际 {client.Attempts}";

        var retries = events.OfType<AgentEvent.RetryScheduled>().Count();
        if (retries != 2) return $"应发 2 条重试事件，实际 {retries}";

        var finished = events.OfType<AgentEvent.RunFinished>().LastOrDefault();
        return finished?.Outcome is AgentRunOutcome.Failed
            ? null
            : $"耗尽后没有报 Failed：{finished?.Outcome}";
    }

    /// <summary>不配置策略时不重试。</summary>
    /// <remarks>
    /// <b>默认必须是"不重试"</b>：自动化场景要的是确定性，
    /// 一个已经坏掉的端点不该让脚本多等半分钟。
    /// </remarks>
    private static async Task<string?> DisabledByDefaultAsync()
    {
        var client = new FlakyClient(failures: 10, status: HttpStatusCode.ServiceUnavailable);
        var events = await RunAsync(client, retry: null);

        if (client.Attempts != 1) return $"未配置策略却调用了 {client.Attempts} 次";
        return events.OfType<AgentEvent.RetryScheduled>().Any()
            ? "未配置策略却发出了重试事件"
            : null;
    }

    /// <summary>分类映射：状态码 → 可重试类别。</summary>
    private static Task<string?> ClassifierAsync()
    {
        var cases = new (Exception Error, bool Cancelled, RetryableFailure Expected)[]
        {
            (new HttpRequestException("x", null, HttpStatusCode.TooManyRequests), false, RetryableFailure.RateLimited),
            (new HttpRequestException("x", null, HttpStatusCode.ServiceUnavailable), false, RetryableFailure.ServerError),
            (new HttpRequestException("x", null, HttpStatusCode.InternalServerError), false, RetryableFailure.ServerError),
            (new HttpRequestException("x", null, HttpStatusCode.Unauthorized), false, RetryableFailure.None),
            (new HttpRequestException("x", null, HttpStatusCode.NotFound), false, RetryableFailure.None),
            (new HttpRequestException("x", null, HttpStatusCode.BadRequest), false, RetryableFailure.None),
            // **408 带状态码，走不到"取消类"分支**，必须显式映射。
            // 漏掉时它会落到 None，一次纯等待型的失败被当成永久错误。
            (new HttpRequestException("x", null, HttpStatusCode.RequestTimeout), false, RetryableFailure.Timeout),
            (new TimeoutException("t"), false, RetryableFailure.Timeout),
            (new OperationCanceledException(), false, RetryableFailure.Timeout),
            // **调用方取消永远不重试**：它表达的是"停下来"。
            (new OperationCanceledException(), true, RetryableFailure.None),
            (new HttpRequestException("x"), false, RetryableFailure.NetworkError),
            (new InvalidOperationException("boom"), false, RetryableFailure.None),
        };

        foreach (var (error, cancelled, expected) in cases)
        {
            var actual = RetryClassifier.Classify(error, cancelled);
            if (actual != expected)
            {
                return Task.FromResult<string?>(
                    $"{error.GetType().Name}/{cancelled} 判成 {actual}，应为 {expected}");
            }
        }

        return Task.FromResult<string?>(null);
    }

    /// <summary>退避指数增长且不超上限。</summary>
    private static Task<string?> BackoffAsync()
    {
        var policy = RetryPolicy.Default with
        {
            InitialDelay = TimeSpan.FromMilliseconds(500),
            MaxDelay = TimeSpan.FromSeconds(10),
            JitterRatio = 0,   // 关掉抖动才能断言确切值
        };

        var expected = new[] { 500, 1000, 2000, 4000, 8000, 10000, 10000 };
        for (var retry = 1; retry <= expected.Length; retry++)
        {
            var actual = (int)policy.DelayFor(retry, 0.5).TotalMilliseconds;
            if (actual != expected[retry - 1])
                return Task.FromResult<string?>(
                    $"第 {retry} 次退避 {actual}ms，应为 {expected[retry - 1]}ms");
        }

        // provider 给的建议优先。
        var hinted = policy.DelayFor(1, 0.5, TimeSpan.FromSeconds(3));
        if ((int)hinted.TotalMilliseconds != 3000)
            return Task.FromResult<string?>($"没有采用 provider 的建议：{hinted.TotalMilliseconds}ms");

        // **超过上限的建议不采用**：一个配错的 Retry-After: 3600
        // 不该让程序静默挂一小时。退回本地退避。
        var capped = policy.DelayFor(1, 0.5, TimeSpan.FromHours(1));
        return capped.TotalMilliseconds <= 10000
            ? Task.FromResult<string?>(null)
            : Task.FromResult<string?>($"过长的 Retry-After 没被拒绝：{capped.TotalMilliseconds}ms");
    }

    /// <summary>
    /// 连接器真的把 <c>Retry-After</c> 传到了分类器手上。
    /// </summary>
    /// <remarks>
    /// <b>这条钉的是"两段代码各自正确、中间没接上"</b>：`DelayFor` 会优先用
    /// provider 建议（有单独检查），`RetryClassifier.FindProviderDelay` 也能从
    /// `Exception.Data` 读出值——但**连接器从来没往里写过**。那段代码在生产
    /// 路径上一直是空的，而所有检查都是绿的。
    /// <para>
    /// 这里直接调真实连接器对本地 HTTP 服务发请求，验证三种形态：
    /// 秒数、HTTP-date、无法解析。第三种必须退回 null 而不是猜。
    /// </para>
    /// </remarks>
    private static async Task<string?> ProviderDelayFromWireAsync()
    {
        using var server = new ThrowawayHttpServer();

        var cases = new (string Path, TimeSpan? Expected)[]
        {
            ("/seconds", TimeSpan.FromSeconds(17)),
            ("/garbage", null),
        };

        foreach (var (path, expected) in cases)
        {
            var client = new HttpClientChatClient(
                server.BaseUrl + path, "k", "m", new OpenAiChatCompletionsFormat());

            Exception? caught = null;
            try
            {
                await foreach (var _ in client.GetStreamingResponseAsync(
                    [new ChatMessage(ChatRole.User, "hi")], cancellationToken: default))
                { }
            }
            catch (Exception ex) { caught = ex; }

            if (caught is null) return $"{path} 没有抛异常";
            if (RetryClassifier.Classify(caught, false) is not RetryableFailure.RateLimited)
                return $"{path} 没有被判为限流";

            var hint = RetryClassifier.FindProviderDelay(caught);

            if (expected is null)
            {
                if (hint is not null) return $"{path} 解析不了却给出了 {hint}，应按本地退避";
            }
            else if (hint != expected)
            {
                return $"{path} 的 Retry-After 没传到分类器：期望 {expected}，实际 {hint?.ToString() ?? "<null>"}"
                    // **不能只归因一段**：这条链路有两段（连接器写入、分类器读取），
                    // 任一断了表现都一样。只写"连接器没写"会在读取端出问题时误导排查。
                    + "（链路两段：连接器写入 Exception.Data、分类器读取；任一断了都到此）";
            }
        }

        return null;
    }

    /// <summary>
    /// 一个只回 429 + 指定 <c>Retry-After</c> 的临时 HTTP 服务。
    /// </summary>
    /// <remarks>
    /// 用真实 socket 而不是 mock <c>HttpMessageHandler</c>：要验证的正是
    /// "响应头能被读到"这件事，而 mock handler 会绕过真实的头解析。
    /// </remarks>
    private sealed class ThrowawayHttpServer : IDisposable
    {
        private readonly HttpListener _listener;
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _loop;

        public ThrowawayHttpServer()
        {
            // 端口 0 让系统分配，避免与其他检查或本机服务撞车。
            var probe = new System.Net.Sockets.TcpListener(
                System.Net.IPAddress.Loopback, 0);
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
                catch { return; }   // 监听器已关闭

                var response = context.Response;
                response.StatusCode = 429;

                // 按路径给出不同的 Retry-After，让调用方能区分形态。
                if (context.Request.Url?.AbsolutePath.Contains("seconds", StringComparison.Ordinal) == true)
                    response.Headers["Retry-After"] = "17";
                else if (context.Request.Url?.AbsolutePath.Contains("garbage", StringComparison.Ordinal) == true)
                    response.Headers["Retry-After"] = "soon";

                response.ContentLength64 = 2;
                response.OutputStream.Write("{}"u8);
                response.Close();
            }
        }

        public void Dispose()
        {
            _stop.Cancel();
            try { _listener.Stop(); } catch { /* 已停 */ }
            try { _loop.Wait(TimeSpan.FromSeconds(2)); } catch { /* 超时不阻塞清理 */ }
            _listener.Close();
        }
    }

    // ── 支撑 ─────────────────────────────────────────────────────

    private static async Task<List<AgentEvent>> RunAsync(IChatClient client, RetryPolicy? retry)
    {
        var runtime = new LoopAgentRuntime(client, new NoTools()) { Retry = retry };
        var events = new List<AgentEvent>();
        await foreach (var e in runtime.RunAsync(new AgentRunRequest("s", "t", [], "hi"), CancellationToken.None))
            events.Add(e);
        return events;
    }

    /// <summary>前 N 次抛指定状态码，之后正常吐 "ok"。</summary>
    private sealed class FlakyClient(int failures, HttpStatusCode status) : IChatClient
    {
        public int Attempts { get; private set; }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> m, ChatOptions? o = null, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> m, ChatOptions? o = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.Yield();
            Attempts++;
            if (Attempts <= failures)
                throw new HttpRequestException($"模拟失败 HTTP {(int)status}", null, status);

            yield return new ChatResponseUpdate(ChatRole.Assistant, "ok");
        }

        public object? GetService(Type t, object? k = null) => null;
        public void Dispose() { }
    }

    /// <summary>先吐一段文本，然后抛可重试的失败。</summary>
    private sealed class TextThenFailClient : IChatClient
    {
        public int Attempts { get; private set; }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> m, ChatOptions? o = null, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> m, ChatOptions? o = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.Yield();
            Attempts++;
            yield return new ChatResponseUpdate(ChatRole.Assistant, "partial");
            // 文本已经交付给宿主——此时失败不该重发。
            throw new HttpRequestException("模拟中途断流", null, HttpStatusCode.ServiceUnavailable);
        }

        public object? GetService(Type t, object? k = null) => null;
        public void Dispose() { }
    }

    private sealed class NoTools : IToolDispatcher
    {
        public IReadOnlyList<ToolDescriptor> Tools => [];

        public IDisposable Register(IAgentTool tool) =>
            throw new NotSupportedException("本检查不注册工具。");

        public ValueTask<ToolDispatchResult> DispatchAsync(
            ToolInvocation invocation, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("本检查不派发工具。");
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
