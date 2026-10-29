using System.Text.Json;
using Dougong.Agent;
using Dougong.Ai;
using Microsoft.Extensions.AI;

namespace Dougong.Agent.Comparison;

/// <summary>
/// 用量记账验收（docs/architecture.md §5.1）。
/// </summary>
/// <remarks>
/// 这里最要紧的一条不是"能算出总数"，而是**未知与零能区分开**：
/// provider 不报 usage 时，一个"共 0 tokens"的汇总会让人以为不花钱。
/// 因此 <see cref="UsageSummary.UnreportedCalls"/> 与可空的 TotalTokens 是主要验收对象。
/// </remarks>
public static class UsageChecks
{
    public static async Task<int> RunAsync()
    {
        var failures = 0;
        failures += await CheckAsync("records-streaming-usage", StreamingUsageAsync);
        failures += await CheckAsync("unreported-is-not-zero", UnreportedAsync);
        failures += await CheckAsync("failed-call-is-counted", FailedCallAsync);
        failures += await CheckAsync("failure-kind-classifies-http-status", ClassifyStatusAsync);
        failures += await CheckAsync("failure-kind-separates-cancel-from-timeout", ClassifyCancelAsync);
        failures += await CheckAsync("failure-kind-does-not-guess", ClassifyUnknownAsync);
        failures += await CheckAsync("cancelled-is-not-counted-as-fault", CancelNotFaultAsync);
        failures += await CheckAsync("http-status-reaches-exception", HttpStatusReachesExceptionAsync);
        failures += await CheckAsync("malformed-stream-is-not-silent-success", MalformedStreamAsync);
        failures += await CheckAsync("cancelled-call-keeps-elapsed", CancelledAsync);
        failures += await CheckAsync("summarizes-per-session", PerSessionAsync);
        failures += await CheckAsync("summarizes-per-day", PerDayAsync);
        failures += await CheckAsync("concurrent-adds-are-safe", ConcurrentAsync);
        failures += await CheckAsync("usage-survives-non-streaming", NonStreamingAsync);
        failures += await CheckAsync("text-is-not-polluted-by-usage", NoTextPollutionAsync);
        failures += await CheckAsync("real-client-exposes-usage-as-content", RealClientShapeAsync);
        failures += await CheckAsync("client-emits-usage-content", ClientEmitsUsageContentAsync);

        Console.WriteLine(failures == 0
            ? "USAGE: ALL PASS"
            : $"USAGE: {failures} FAILURE(S)");
        return failures;
    }

    // 流式响应的 usage 必须被记录，且取自标准的 UsageContent。
    private static async Task<string?> StreamingUsageAsync()
    {
        var ledger = new UsageLedger();
        var client = Wrap(new ScriptedChatClient(
            new ScriptedTurn(
                new ScriptedUpdate(Text: "hi"),
                ScriptedUpdate.Usage(10, 5, 15))), ledger, "s1");

        var text = await CollectTextAsync(client);
        if (text != "hi") return $"文本应为 hi，实际 '{text}'";

        var summary = ledger.Summarize("s1");
        if (summary.Calls != 1) return $"调用数应为 1，实际 {summary.Calls}";
        if (summary.InputTokens != 10 || summary.OutputTokens != 5)
            return $"用量不符：in={summary.InputTokens} out={summary.OutputTokens}";
        if (summary.TotalTokens != 15) return $"总量应为 15，实际 {summary.TotalTokens}";
        if (summary.UnreportedCalls != 0) return "有报告却记成未报告";
        return null;
    }

    // provider 不报 usage 时，必须记为"未知"而不是 0。
    private static async Task<string?> UnreportedAsync()
    {
        var ledger = new UsageLedger();
        var client = Wrap(new ScriptedChatClient(
            new ScriptedTurn(new ScriptedUpdate(Text: "hi"))), ledger, "s1");

        await CollectTextAsync(client);
        var summary = ledger.Summarize("s1");

        if (summary.UnreportedCalls != 1)
            return $"未报告调用数应为 1，实际 {summary.UnreportedCalls}";
        if (summary.TotalTokens is not null)
            return $"未报告时总量应为未知，实际 {summary.TotalTokens}";
        if (summary.ReportedRatio != 0) return $"覆盖率应为 0，实际 {summary.ReportedRatio}";
        return null;
    }

    // 失败调用必须计入调用数与失败数，不能只统计成功。
    private static async Task<string?> FailedCallAsync()
    {
        var ledger = new UsageLedger();
        var client = Wrap(new ScriptedChatClient(
            new ScriptedTurn(new ScriptedUpdate(Text: "x", AbortAfter: true))), ledger, "s1");

        try { await CollectTextAsync(client); return "应抛出"; }
        catch (InvalidOperationException) { }

        var summary = ledger.Summarize("s1");
        if (summary.Calls != 1) return $"失败调用也应计入，实际 {summary.Calls}";
        if (summary.FailedCalls != 1) return $"失败数应为 1，实际 {summary.FailedCalls}";

        var record = ledger.Records.Single();
        return record.Succeeded ? "记录被标记为成功" : null;
    }

    // 取消也要留痕：取消前可能已经消耗了输入 token。
    private static async Task<string?> CancelledAsync()
    {
        var ledger = new UsageLedger();
        var inner = new ScriptedChatClient(
            new ScriptedTurn(new ScriptedUpdate(Text: "x", Delay: TimeSpan.FromSeconds(5))));
        var client = Wrap(inner, ledger, "s1");

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        try
        {
            await CollectTextAsync(client, cts.Token);
            return "取消后未抛出";
        }
        catch (OperationCanceledException) { }

        var summary = ledger.Summarize("s1");
        if (summary.Calls != 1) return $"取消也要留痕，实际 {summary.Calls} 条";
        if (summary.FailedCalls != 1) return $"取消应记为失败，实际 {summary.FailedCalls}";
        return null;
    }

    // 会话隔离：不同会话不能混在一起。
    private static async Task<string?> PerSessionAsync()
    {
        var ledger = new UsageLedger();
        await CollectTextAsync(Wrap(new ScriptedChatClient(
            new ScriptedTurn(new ScriptedUpdate(Text: "a"), ScriptedUpdate.Usage(10, 1))),
            ledger, "s1"));
        await CollectTextAsync(Wrap(new ScriptedChatClient(
            new ScriptedTurn(new ScriptedUpdate(Text: "b"), ScriptedUpdate.Usage(20, 2))),
            ledger, "s2"));

        var s1 = ledger.Summarize("s1");
        var s2 = ledger.Summarize("s2");
        var all = ledger.SummarizeAll();

        if (s1.InputTokens != 10) return $"s1 输入应为 10，实际 {s1.InputTokens}";
        if (s2.InputTokens != 20) return $"s2 输入应为 20，实际 {s2.InputTokens}";
        if (all.InputTokens != 30) return $"总计应为 30，实际 {all.InputTokens}";
        if (all.Calls != 2) return $"总调用数应为 2，实际 {all.Calls}";
        return null;
    }

    // 按自然日汇总，用可控时钟避免跨日抖动。
    private static async Task<string?> PerDayAsync()
    {
        var day1 = new DateTimeOffset(2026, 10, 20, 10, 0, 0, TimeSpan.Zero);
        var day2 = day1.AddDays(1);
        var now = day1;
        var ledger = new UsageLedger(() => now);

        await CollectTextAsync(Wrap(new ScriptedChatClient(
            new ScriptedTurn(new ScriptedUpdate(Text: "a"), ScriptedUpdate.Usage(10, 1))),
            ledger, "s1"));

        now = day2;
        await CollectTextAsync(Wrap(new ScriptedChatClient(
            new ScriptedTurn(new ScriptedUpdate(Text: "b"), ScriptedUpdate.Usage(20, 2))),
            ledger, "s1"));

        var first = ledger.SummarizeDay(DateOnly.FromDateTime(day1.LocalDateTime));
        var second = ledger.SummarizeDay(DateOnly.FromDateTime(day2.LocalDateTime));

        if (first.Calls != 1 || first.InputTokens != 10)
            return $"第一日汇总不符：calls={first.Calls} in={first.InputTokens}";
        if (second.Calls != 1 || second.InputTokens != 20)
            return $"第二日汇总不符：calls={second.Calls} in={second.InputTokens}";
        return null;
    }

    // 并发追加不能丢记录或损坏聚合。
    private static async Task<string?> ConcurrentAsync()
    {
        var ledger = new UsageLedger();
        var tasks = Enumerable.Range(0, 40).Select(i => Task.Run(async () =>
        {
            var client = Wrap(new ScriptedChatClient(
                new ScriptedTurn(new ScriptedUpdate(Text: "x"), ScriptedUpdate.Usage(1, 1))),
                ledger, $"s{i % 4}");
            await CollectTextAsync(client);
        }));

        await Task.WhenAll(tasks);

        var all = ledger.SummarizeAll();
        if (all.Calls != 40) return $"调用数应为 40，实际 {all.Calls}";
        if (all.InputTokens != 40) return $"输入应为 40，实际 {all.InputTokens}";

        var perSession = Enumerable.Range(0, 4).Sum(i => ledger.Summarize($"s{i}").Calls);
        return perSession == 40 ? null : $"按会话求和应为 40，实际 {perSession}";
    }

    // 非流式路径同样要记账，不能只有流式被统计。
    private static async Task<string?> NonStreamingAsync()
    {
        var ledger = new UsageLedger();
        var client = Wrap(new FixedResponseClient(new UsageDetails
        {
            InputTokenCount = 7,
            OutputTokenCount = 3,
            TotalTokenCount = 10,
        }), ledger, "s1");

        var response = await client.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")]);
        if (response.Usage?.InputTokenCount != 7)
            return $"响应应带上 usage，实际 {response.Usage?.InputTokenCount}";

        var summary = ledger.Summarize("s1");
        if (summary.Calls != 1) return $"非流式调用未记账，总计 {summary.Calls}";
        return summary.TotalTokens == 10 ? null : $"总量应为 10，实际 {summary.TotalTokens}";
    }

    // 用量内容不能混进模型可见文本，否则模型会读到一串 token 数字。
    private static async Task<string?> NoTextPollutionAsync()
    {
        var ledger = new UsageLedger();
        var client = Wrap(new ScriptedChatClient(
            new ScriptedTurn(
                new ScriptedUpdate(Text: "answer"),
                ScriptedUpdate.Usage(100, 50))), ledger, "s1");

        var text = await CollectTextAsync(client);
        return text == "answer" ? null : $"用量污染了文本：'{text}'";
    }

    // 真实连接器必须把 usage 作为 UsageContent 交给上层，而不是藏在附加属性里。
    private static Task<string?> RealClientShapeAsync()
    {
        var format = new OpenAiChatCompletionsFormat();
        var decoder = format.CreateDecoder();
        var updates = new List<ChatResponseUpdate>();

        decoder.Decode("""{"choices":[{"delta":{"content":"x"}}]}""", updates);
        decoder.Decode("""{"choices":[{"delta":{},"finish_reason":"stop"}],"usage":{"prompt_tokens":9,"completion_tokens":4,"total_tokens":13}}""", updates);

        if (decoder.Usage is null) return Task.FromResult<string?>("连接器未解析出 usage");
        if (decoder.Usage.InputTokenCount != 9)
            return Task.FromResult<string?>($"输入应为 9，实际 {decoder.Usage.InputTokenCount}");

        // 连接器必须用标准 UsageContent 承载，M.E.AI 的聚合与中间件才看得见。
        var usageContent = new UsageContent(decoder.Usage);
        if (usageContent.Details.TotalTokenCount != 13)
            return Task.FromResult<string?>("UsageContent 未携带总量");

        return Task.FromResult<string?>(null);
    }

    // 端到端：走真实 HttpClientChatClient（假传输），确认 usage 真的以 UsageContent 发出。
    // 上面那条只验证"解码器能解析"，不验证"客户端怎么发"——两者必须分开测，
    // 否则把发送端改回自定义附加属性时，检查仍然会通过。
    private static async Task<string?> ClientEmitsUsageContentAsync()
    {
        var sse = string.Join("\n", [
            "data: {\"choices\":[{\"delta\":{\"content\":\"hi\"}}]}",
            "data: {\"choices\":[{\"delta\":{},\"finish_reason\":\"stop\"}],\"usage\":{\"prompt_tokens\":6,\"completion_tokens\":2,\"total_tokens\":8}}",
            string.Empty,
        ]);

        using var http = new HttpClient(new StubHandler(sse));
        IChatClient client = new HttpClientChatClient(
            "https://example.invalid", "key", "m",
            new OpenAiChatCompletionsFormat(), http);

        UsageDetails? seen = null;
        var text = new System.Text.StringBuilder();
        await foreach (var update in client.GetStreamingResponseAsync(
            [new ChatMessage(ChatRole.User, "go")]))
        {
            foreach (var content in update.Contents)
            {
                if (content is UsageContent usageContent) seen = usageContent.Details;
                else if (content is TextContent textContent) text.Append(textContent.Text);
            }
        }

        if (text.ToString() != "hi") return $"文本应为 hi，实际 '{text}'";
        if (seen is null) return "客户端没有以 UsageContent 发出用量（被藏进附加属性了？）";
        return seen.TotalTokenCount == 8 ? null : $"总量应为 8，实际 {seen.TotalTokenCount}";
    }

    private sealed class StubHandler(string sse) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(sse, System.Text.Encoding.UTF8, "text/event-stream"),
            });
    }

    private static UsageRecordingChatClient Wrap(
        IChatClient inner, UsageLedger ledger, string sessionId) =>
        new(inner, ledger, sessionId, "test", "test-model",
            ChatProtocol.OpenAiChatCompletions);

    private static async Task<string> CollectTextAsync(
        IChatClient client, CancellationToken cancellationToken = default)
    {
        var text = new System.Text.StringBuilder();
        await foreach (var update in client.GetStreamingResponseAsync(
            [new ChatMessage(ChatRole.User, "go")], null, cancellationToken))
        {
            if (update.Text is { Length: > 0 } chunk) text.Append(chunk);
        }

        return text.ToString();
    }

    private sealed class FixedResponseClient(UsageDetails usage) : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "fixed"))
            {
                Usage = usage,
            });

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            yield return new ChatResponseUpdate(ChatRole.Assistant, "fixed");
            await Task.CompletedTask;
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

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
    /// <summary>HTTP 状态码要映射到对应的失败类别。</summary>
    /// <remarks>
    /// 这几类在排查上动作完全不同：401 换 key、402 充值、429 降速、
    /// 404 改地址、5xx 等待。全部归成"失败"等于没分类。
    /// </remarks>
    private static async Task<string?> ClassifyStatusAsync()
    {
        (int Status, UsageFailureKind Expected)[] cases =
        [
            (401, UsageFailureKind.Unauthorized),
            (403, UsageFailureKind.Unauthorized),
            (402, UsageFailureKind.QuotaExceeded),
            (404, UsageFailureKind.NotFound),
            (429, UsageFailureKind.RateLimited),
            (500, UsageFailureKind.ServerError),
            (503, UsageFailureKind.ServerError),
        ];

        foreach (var (status, expected) in cases)
        {
            var kind = await KindOfFailedCallAsync(
                new HttpRequestException("boom", inner: null,
                    (System.Net.HttpStatusCode)status));

            if (kind != expected)
                return $"HTTP {status} 应为 {expected}，实际 {kind}";
        }

        return null;
    }

    /// <summary>
    /// 取消与超时必须是两类——**判据是调用方的令牌有没有触发**，不是异常类型。
    /// </summary>
    /// <remarks>
    /// 两者都是 <see cref="OperationCanceledException"/>。只看类型无法区分，
    /// 而混在一起会让"用户按了几次 Ctrl+C"污染故障率。
    /// </remarks>
    private static async Task<string?> ClassifyCancelAsync()
    {
        var cancelledKind = await KindOfFailedCallAsync(
            new OperationCanceledException(), callerCancels: true);
        if (cancelledKind != UsageFailureKind.Cancelled)
            return $"调用方取消应为 Cancelled，实际 {cancelledKind}";

        var timeoutKind = await KindOfFailedCallAsync(
            new OperationCanceledException(), callerCancels: false);
        if (timeoutKind != UsageFailureKind.Timeout)
            return $"非调用方取消的取消异常应为 Timeout，实际 {timeoutKind}";

        // TaskCanceledException 是 OperationCanceledException 的子类，
        // HTTP 客户端超时实际抛的就是它。
        var taskTimeout = await KindOfFailedCallAsync(
            new TaskCanceledException("timeout"), callerCancels: false);
        if (taskTimeout != UsageFailureKind.Timeout)
            return $"TaskCanceledException 应为 Timeout，实际 {taskTimeout}";

        return null;
    }

    /// <summary>
    /// 判不出来的异常必须归 Unknown，不能挑一个"最像的"凑数。
    /// </summary>
    /// <remarks>
    /// <b>错的分类比没有分类更糟</b>：它会让排查往错误方向走。
    /// 例如把"我们自己的 NullReferenceException"归成 NetworkError，
    /// 用户会去查网络，而问题在 SDK 里。
    /// </remarks>
    private static Task<string?> ClassifyUnknownAsync()
    {
        var kind = UsageFailureClassifier.Classify(
            new NullReferenceException("bug in our code"), callerCancelled: false);

        return Task.FromResult<string?>(kind == UsageFailureKind.Unknown
            ? null
            : $"无法判定的异常应归 Unknown，实际 {kind}");
    }

    /// <summary>
    /// 主动取消不计入故障率，其它失败都计入。
    /// </summary>
    private static async Task<string?> CancelNotFaultAsync()
    {
        var cancelled = await KindOfFailedCallAsync(
            new OperationCanceledException(), callerCancels: true);

        if (UsageFailureClassifier.CountsAsFault(cancelled))
            return "主动取消不应计入故障率——那会让用户按 Ctrl+C 污染监控";

        if (!UsageFailureClassifier.CountsAsFault(UsageFailureKind.Timeout))
            return "超时必须计入故障率";

        if (UsageFailureClassifier.CountsAsFault(UsageFailureKind.None))
            return "成功不该计入故障率";

        return null;
    }

    /// <summary>
    /// 让一次调用以指定异常失败，返回账目里记下的失败类别。
    /// </summary>
    /// <remarks>
    /// 走真实的 <see cref="UsageRecordingChatClient"/> 而不是直接调分类器：
    /// 那样才能证明"异常确实被分类并写进了账目"——只测分类器的话，
    /// 记录路径忘了传 kind 也会全绿。
    /// </remarks>
    private static async Task<UsageFailureKind> KindOfFailedCallAsync(
        Exception error, bool callerCancels = false)
    {
        var ledger = new UsageLedger();
        var client = new UsageRecordingChatClient(
            new ThrowingClient(error), ledger, "s", "p", "m",
            ChatProtocol.OpenAiChatCompletions);

        using var cts = new CancellationTokenSource();
        if (callerCancels) cts.Cancel();

        try
        {
            await client.GetResponseAsync(
                [new ChatMessage(ChatRole.User, "hi")], cancellationToken: cts.Token);
        }
        catch (Exception)
        {
            // 预期内：这里只关心账目里记了什么。
        }

        var record = ledger.Records.LastOrDefault();
        return record?.FailureKind ?? UsageFailureKind.Unknown;
    }

    /// <summary>固定抛指定异常的客户端。</summary>
    private sealed class ThrowingClient(Exception error) : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) => Task.FromException<ChatResponse>(error);

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            throw error;

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }

    /// <summary>
    /// 真实的 HTTP 客户端必须把状态码放进异常，分类才有依据。
    /// </summary>
    /// <remarks>
    /// <b>这条是补一个真被漏掉的覆盖</b>：分类器能正确映射状态码，但如果
    /// <c>HttpClientChatClient</c> 只把状态码写进消息文本、不设
    /// <c>HttpRequestException.StatusCode</c>，整条分类链就是空转——
    /// 所有 HTTP 错误都会落到 Unknown。
    /// <para>
    /// 反证实测：把 StatusCode 参数去掉，其它检查仍全绿。
    /// 那是因为分类器测的是手造的异常，没有一条走真实传输层。
    /// </para>
    /// </remarks>
    private static async Task<string?> HttpStatusReachesExceptionAsync()
    {
        using var http = new HttpClient(new StatusOnlyHandler(
            System.Net.HttpStatusCode.Unauthorized, "invalid api key"))
        {
            BaseAddress = new Uri("https://example.invalid"),
        };

        var client = new HttpClientChatClient(
            "https://example.invalid", "key", "model",
            new OpenAiChatCompletionsFormat(), http);

        try
        {
            await CollectTextAsync(client);
            return "应当抛异常（401）";
        }
        catch (HttpRequestException ex)
        {
            // 第一条：状态码必须结构化地带出来，不能只在消息文本里。
            if (ex.StatusCode != System.Net.HttpStatusCode.Unauthorized)
            {
                return $"异常的 StatusCode 是 {ex.StatusCode?.ToString() ?? "null"}，"
                    + "应为 Unauthorized——只写进消息文本的话分类器拿不到";
            }

            // 第二条：分类器据此给出正确类别。
            var kind = UsageFailureClassifier.Classify(ex, callerCancelled: false);
            if (kind != UsageFailureKind.Unauthorized)
                return $"401 应归 Unauthorized，实际 {kind}";

            return null;
        }
    }

    /// <summary>始终返回指定状态码的处理器。</summary>
    private sealed class StatusOnlyHandler(System.Net.HttpStatusCode status, string body)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
            });
    }

    /// <summary>
    /// 畸形响应必须报错，不能静默产出空回复并记成成功。
    /// </summary>
    /// <remarks>
    /// <b>这条对应一个真实的能力空转</b>：<c>UsageFailureKind.InvalidResponse</c>
    /// 早就在枚举里，但没有任何路径能触发它——HTTP 200 之后如果响应体解析不出
    /// 任何内容，<see cref="HttpClientChatClient"/> 静默结束，产出零个 update；
    /// <see cref="UsageRecordingChatClient"/> 看到没抛异常就记成成功。
    /// <para>
    /// 后果是用户在界面上看到"成功但没有任何内容"，无从判断是模型没说话
    /// 还是协议实现出了问题。**枚举里有位置不等于接上了**，这条检查钉住接线。
    /// </para>
    /// </remarks>
    private static async Task<string?> MalformedStreamAsync()
    {
        // 200 + 一坨不是 SSE 的东西（模拟网关错误页）。
        using var http = new HttpClient(new StatusOnlyHandler(
            System.Net.HttpStatusCode.OK, "<html>502 Bad Gateway</html>"))
        {
            BaseAddress = new Uri("https://example.invalid"),
        };

        var client = new HttpClientChatClient(
            "https://example.invalid", "key", "model",
            new OpenAiChatCompletionsFormat(), http);

        var updates = 0;
        try
        {
            await foreach (var _ in client.GetStreamingResponseAsync(
                [new ChatMessage(ChatRole.User, "hi")]))
            {
                updates++;
            }

            // 静默走完：这正是缺陷——零 update 却当成成功。
            return $"畸形响应静默产出 {updates} 个更新，应向调用方报错"
                + "（否则用户看到空回复，且账目记成成功）";
        }
        catch (Exception ex)
        {
            // 抛了异常但要确认能被分成 InvalidResponse，否则分类还是空转。
            var kind = UsageFailureClassifier.Classify(ex, callerCancelled: false);
            return kind == UsageFailureKind.InvalidResponse
                ? null
                : $"抛了 {ex.GetType().Name} 但被分成 {kind}，应为 InvalidResponse";
        }
    }

}
