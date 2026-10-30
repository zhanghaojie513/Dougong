using Dougong.Agent;
using Microsoft.Extensions.AI;

namespace Dougong.Agent.Comparison;

/// <summary>
/// 上下文溢出识别与恢复的验收。
/// </summary>
/// <remarks>
/// 覆盖三层，每层都要能单独证明：
/// <list type="number">
/// <item><b>识别</b>：provider 的各种报错文案能命中，限流类不误命中</item>
/// <item><b>静默溢出</b>：调用"成功"但输入超窗也算溢出</item>
/// <item><b>恢复</b>：溢出后压一次并重试；压不动、或已试过一次就报**原始 provider 错误**</item>
/// </list>
/// </remarks>
public static class OverflowChecks
{
    public static async Task<int> RunAsync()
    {
        var failures = 0;

        failures += Check("overflow-detects-provider-wording", DetectsWording);
        failures += Check("overflow-does-not-match-rate-limit", DoesNotMatchRateLimit);
        failures += Check("overflow-requires-context-object", RequiresContextObject);
        failures += Check("silent-overflow-detected", DetectsSilentOverflow);
        failures += Check("truncated-overflow-detected", DetectsTruncatedOverflow);
        failures += Check("truncated-overflow-ignores-idle-model", IgnoresIdleModel);
        failures += await CheckAsync("overflow-compacts-and-retries", CompactsAndRetriesAsync);
        failures += await CheckAsync("overflow-fails-with-provider-error-when-no-progress",
            ReportsOriginalErrorAsync);
        failures += await CheckAsync("overflow-retries-only-once", RetriesOnlyOnceAsync);

        Console.WriteLine(failures == 0
            ? "OVERFLOW: ALL PASS"
            : $"OVERFLOW: {failures} FAILURE(S)");
        return failures;
    }

    /// <summary>各 provider 的实际报错文案都要能命中。</summary>
    /// <remarks>
    /// 这些措辞来自 pi.net 的 <c>Overflow.cs</c>，每条对应一家的真实报错。
    /// 逐条断言而不是"随便挑一条过就算"——漏掉一条就是漏掉一家 provider。
    /// </remarks>
    private static string? DetectsWording()
    {
        string[] samples =
        [
            "prompt is too long: 233153 tokens > 200000 maximum",        // Anthropic
            "This model's maximum context length is 128000 tokens",       // OpenAI 兼容代理
            "exceeds the available context size",                         // llama.cpp
            "context window exceeds limit",                               // MiniMax
            "exceeded model token limit",                                 // Kimi
            "input token count 5000 exceeds the maximum",                 // Google
            // 结构化 code：宾语必须是 context（照 dsh 的收紧写法）
            "context_length_exceeded",
            "context window exceeded",
            "maximum context length is 128000 tokens",
            // "too long for the model context"：宾语必须是 model context
            "This request is too long for the model's context window",
            "超过最大长度",                                                // 中文
            "上下文长度超限",
        ];

        foreach (var sample in samples)
        {
            if (!ContextOverflowDetector.IsContextOverflow(sample))
                return $"未识别为溢出：[{sample}]";
        }

        return null;
    }

    /// <summary>
    /// 限流错误不能判成溢出。
    /// </summary>
    /// <remarks>
    /// <b>Bedrock 的 <c>ThrottlingException</c> 文案里含 "too many tokens"</b>，
    /// 会被正向模式命中。不排除的话，一次限流就会白压一次历史——
    /// 而压缩是**有损**的（原历史被摘要替换），压错无法撤销。
    /// </remarks>
    private static string? DoesNotMatchRateLimit()
    {
        string[] rateLimited =
        [
            "Throttling error: too many tokens per minute",
            "Rate limit reached for too many tokens",
            "too many requests, please slow down",
        ];

        foreach (var sample in rateLimited)
        {
            if (ContextOverflowDetector.IsContextOverflow(sample))
                return $"限流被误判成溢出：[{sample}]——会白压一次历史";
        }

        return null;
    }

    /// <summary>
    /// 没有上下文的裸措辞不得判为溢出。
    /// </summary>
    /// <remarks>
    /// <b>这条是照 dsh 收紧后的结果</b>。pi.net 的模式表里有
    /// <c>too many tokens</c> 和 <c>token limit exceeded</c> 两条裸措辞，
    /// 宾语不明——可能是输出上限、可能是限流、可能是配额。
    /// <para>
    /// dsh 的原则（<c>error.ts</c> 注释原文）："'Exceeds' wording is safe
    /// only when its object is explicitly the model context."
    /// 按这条原则，裸措辞不匹配。
    /// </para>
    /// <para>
    /// <b>代价是可能漏掉某些 provider 的简写报错</b>。这个代价是刻意的：
    /// 漏判的后果是"这次没自动恢复"，误判的后果是"白压一次历史"——
    /// 后者有损且不可撤销。
    /// </para>
    /// </remarks>
    private static string? RequiresContextObject()
    {
        string[] bareWording =
        [
            "too many tokens",                  // 无宾语
            "token limit exceeded",             // 无宾语
            "exceeds the limit of 100",         // 无宾语
            "contextual length exceeded",       // 不是 context 这个词
        ];

        foreach (var sample in bareWording)
        {
            if (ContextOverflowDetector.IsContextOverflow(sample))
                return $"裸措辞被误判成溢出：[{sample}]——宾语不明确，可能是输出上限或限流";
        }

        return null;
    }

    /// <summary>provider 不报错，但输入已超窗 → 静默溢出。</summary>
    private static string? DetectsSilentOverflow()
    {
        if (!ContextOverflowDetector.IsSilentOverflow(130_000, 128_000))
            return "输入 130K 超过窗口 128K，应判为静默溢出";

        if (ContextOverflowDetector.IsSilentOverflow(100_000, 128_000))
            return "输入未超窗，不应判为溢出";

        // 窗口未知（0）时不能判——不知道就没有依据。
        if (ContextOverflowDetector.IsSilentOverflow(999_999, 0))
            return "窗口未配置时不应判溢出（无从比较）";

        return null;
    }

    /// <summary>length + output=0 + 输入填满窗口 → 截断型溢出。</summary>
    private static string? DetectsTruncatedOverflow()
    {
        if (!ContextOverflowDetector.IsTruncatedOverflow("length", 0, 127_500, 128_000))
            return "length + output=0 + 输入 99.6% 应判为截断溢出";

        // 输出不为 0：模型说了话，不是被挤掉。
        if (ContextOverflowDetector.IsTruncatedOverflow("length", 500, 127_500, 128_000))
            return "有输出时不应判为截断溢出";

        // 输入没填满：只是正常被 max_tokens 截断。
        if (ContextOverflowDetector.IsTruncatedOverflow("length", 0, 50_000, 128_000))
            return "输入远未填满时不应判为截断溢出";

        return null;
    }

    /// <summary>
    /// 模型主动不回答（stop + 0 输出）不能被判成溢出。
    /// </summary>
    /// <remarks>
    /// 这是"输出为 0"这个判据最容易误伤的场景：模型可以合法地什么都不说，
    /// 那时 <c>finish_reason</c> 是 <c>stop</c> 而不是 <c>length</c>。
    /// 如果只看输出为 0，就会把正常行为当成溢出并去压缩历史。
    /// </remarks>
    private static string? IgnoresIdleModel()
    {
        if (ContextOverflowDetector.IsTruncatedOverflow("stop", 0, 127_500, 128_000))
            return "stop + 0 输出是模型主动不回答，不是溢出";

        return null;
    }

    /// <summary>溢出后压缩并重试，第二轮成功。</summary>
    /// <remarks>
    /// 用两轮脚本化的 client：第一次抛溢出，第二次正常返回。
    /// 断言最终成功，且压缩事件被发出（证明真的压了，不是悄悄重试）。
    /// </remarks>
    private static async Task<string?> CompactsAndRetriesAsync()
    {
        var client = new ScriptedChatClient(
            new ScriptedTurn(new ScriptedUpdate(AbortAfter: false, Text: null)),
            new ScriptedTurn(new ScriptedUpdate(Text: "recovered")));

        // 让第一轮抛溢出。
        client.ThrowOnTurn = 1;
        client.ThrowException = new ContextOverflowException(
            "provider 说上下文太长",
            new HttpRequestException("prompt is too long: 200000 tokens"));

        var runtime = BuildRuntime(client, historyLength: 40);

        var events = new List<AgentEvent>();
        await foreach (var evt in runtime.RunAsync(
            new AgentRunRequest("s", "t", LongHistory(40), "hi")))
        {
            events.Add(evt);
        }

        var finished = events.OfType<AgentEvent.RunFinished>().LastOrDefault();
        if (finished is null) return "没有 RunFinished 事件";

        if (finished.Outcome != AgentRunOutcome.Completed)
        {
            var diag=string.Join(" | ", events.OfType<AgentEvent.ContextCompacted>().Select(e=>e.Diagnostic));
            return $"应恢复成功，实际 {finished.Outcome}：{finished.Diagnostic} || 压缩诊断={diag}";
        }

        // 必须有压缩事件：证明走的是"压缩后重试"而不是静默重发。
        if (!events.OfType<AgentEvent.ContextCompacted>().Any())
            return "没有压缩事件——可能只是重发同一个请求";

        return null;
    }

    /// <summary>
    /// 压缩没有缩减时，报**原始 provider 错误**。
    /// </summary>
    /// <remarks>
    /// 用户需要看到 provider 说的"prompt is too long"，而不是我们包装过的
    /// "压缩失败"。后者会让排查从"清理会话"退化成"猜 SDK 出了什么问题"。
    /// </remarks>
    private static async Task<string?> ReportsOriginalErrorAsync()
    {
        // 每一轮都抛：压缩无进展时不该重发，但万一重发了也要能观察到。
        var client = new ScriptedChatClient(new ScriptedTurn(new ScriptedUpdate(Text: "x")))
        {
            ThrowOnEveryTurn = new ContextOverflowException(
                "provider 说上下文太长",
                new HttpRequestException("prompt is too long: 233153 tokens > 200000")),
        };

        // 摘要器返回原文（不缩小）→ 压缩无进展。
        var runtime = BuildRuntime(client, historyLength: 40, summarizerReturnsInput: true);

        var events = new List<AgentEvent>();
        await foreach (var evt in runtime.RunAsync(
            new AgentRunRequest("s", "t", LongHistory(40), "hi")))
        {
            events.Add(evt);
        }

        var finished = events.OfType<AgentEvent.RunFinished>().LastOrDefault();
        if (finished is null) return "没有 RunFinished 事件";

        if (finished.Outcome == AgentRunOutcome.Completed)
            return "压缩无进展却报成功——重发只会得到同一个溢出";

        var diagnostic = finished.Diagnostic ?? string.Empty;

        // 关键：诊断里必须有 provider 的原始文案。
        if (!diagnostic.Contains("prompt is too long", StringComparison.Ordinal))
        {
            return $"诊断里丢失了 provider 原始错误，实际是：[{diagnostic}]"
                + "——用户需要看到真实原因，不是「压缩失败」";
        }

        return null;
    }

    /// <summary>
    /// 只重试一次：第二次还溢出就失败，不无限循环。
    /// </summary>
    /// <remarks>
    /// 用固定的抛异常 client 而不是 ScriptedChatClient：后者是脚本化的替身，
    /// 它的异常注入时机与真实 client 不完全一致（第一版就因为替身行为
    /// 得出"只调用了一次却成功"的假象，白查了半小时）。
    /// <b>验证"不该重试"时，替身必须只做一件事：每次都抛。</b>
    /// </remarks>
    private static async Task<string?> RetriesOnlyOnceAsync()
    {
        var client = new AlwaysThrowingClient(new ContextOverflowException(
            "provider 说上下文太长",
            new HttpRequestException("context length exceeded")));

        var runtime = BuildRuntime(client);

        var events = new List<AgentEvent>();
        await foreach (var evt in runtime.RunAsync(
            new AgentRunRequest("s", "t", LongHistory(40), "hi")))
        {
            events.Add(evt);
        }

        var finished = events.OfType<AgentEvent.RunFinished>().LastOrDefault();
        if (finished is null) return "没有 RunFinished 事件";

        if (finished.Outcome == AgentRunOutcome.Completed)
            return $"每次都溢出却报成功（calls={client.CallCount}）";

        // 一次原始 + 一次重试 = 2；多于 2 说明重试逻辑没有上限。
        if (client.CallCount > 2)
            return $"模型被调用了 {client.CallCount} 次，重试应当只用一次";

        return null;
    }

    /// <summary>每次调用都抛指定异常的客户端。</summary>
    /// <remarks>
    /// <b>必须每次返回一个新枚举器</b>（由下面的嵌套迭代器保证）。
    /// async iterator 抛异常后状态机会进入完成态，之后对**同一个**枚举器
    /// 调 MoveNextAsync 返回 false 而不是再抛——第一版就踩了这个坑：
    /// runtime 用 <c>await using var</c> 持有枚举器，重试时复用同一个，
    /// 于是第二次 MoveNext 静默返回 false，看起来像"只调一次就成功"，
    /// 白查了半小时。
    /// <para>
    /// 真实 provider 每次调用都是新 HTTP 请求、新枚举器，所以这里也必须是新的。
    /// </para>
    /// </remarks>
    private sealed class AlwaysThrowingClient(Exception error) : IChatClient
    {
        public int CallCount { get; private set; }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) => Task.FromException<ChatResponse>(error);

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            return ThrowOnce(error);
        }

        /// <summary>每次调用都新建一个会抛的迭代器。</summary>
        private static async IAsyncEnumerable<ChatResponseUpdate> ThrowOnce(Exception error)
        {
            await Task.Yield();
            if (error is not null) throw error;
            yield return new ChatResponseUpdate(ChatRole.Assistant, string.Empty);
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    /// <summary>
    /// 构造被测运行时。
    /// </summary>
    /// <param name="proactiveTriggerRatio">
    /// 主动压缩的触发比例。**测溢出恢复时必须调高到 1.0**：
    /// 否则 round 开头的主动压缩会先把历史压小，溢出恢复时就没东西可压，
    /// 测到的是"压不动"而不是"恢复成功"。
    /// 这一点是踩出来的——第一版用默认 0.75，两次压缩互相干扰。
    /// </param>
    private static LoopAgentRuntime BuildRuntime(
        IChatClient client, int historyLength = 40,
        bool summarizerReturnsInput = false, double proactiveTriggerRatio = 0.75)
    {
        var compactor = new SlidingWindowCompactor(triggerRatio: proactiveTriggerRatio);

        return new LoopAgentRuntime(
            client,
            new FixedDispatcherAccessor(new ToolDispatcher()),
            store: null,
            // 窗口开得很大：让 round 开头的**主动压缩**不触发。
            // 溢出由 client 主动抛出（模拟 provider 判定），测的是恢复路径本身。
            // 若窗口设小，主动压缩会先把历史压小，恢复时就压不动了——
            // 第一版就是这样，测到的是"压不动"而不是"恢复成功"。
            limits: new ContextLimits
            {
                ContextWindowTokens = 2_000_000,
                RequestOutputReserve = 1_000,
                OverheadTokens = 200,
            },
            compactor: compactor,
            summarize: (text, ct) => ValueTask.FromResult(
                summarizerReturnsInput ? text : "summary"));
    }

    private static List<AgentMessage> LongHistory(int count) =>
        [.. Enumerable.Range(0, count).Select(i =>
            new AgentMessage(
                i % 2 == 0 ? AgentRole.User : AgentRole.Assistant,
                $"message {i} " + new string('x', 2000)))];

    private static int Check(string name, Func<string?> action)
    {
        try
        {
            var failure = action();
            if (failure is null) { Console.WriteLine($"PASS {name}"); return 0; }
            Console.WriteLine($"FAIL {name}: {failure}");
            return 1;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"FAIL {name}: {ex.GetType().Name}: {ex.Message}");
            return 1;
        }
    }

    private static async Task<int> CheckAsync(string name, Func<Task<string?>> action)
    {
        try
        {
            var failure = await action();
            if (failure is null) { Console.WriteLine($"PASS {name}"); return 0; }
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
