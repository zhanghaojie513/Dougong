using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dougong.Agent;
using Microsoft.Extensions.AI;

namespace Dougong.Ai;

/// <summary>
/// 把一个 <see cref="IChatWireFormat"/> 接到 HTTP 上，得到可用的 <see cref="IChatClient"/>。
/// </summary>
/// <remarks>
/// 只有传输与流解析在这里；协议形状全在 <see cref="IChatWireFormat"/> 里。
/// 这样同一份传输代码服务三种协议，而协议差异各自独立、可单独做确定性校验。
/// <para>
/// 不做重试、不做多模态、不做熔断：这些不属于 M1 的连接器职责，
/// 加进来只会让"失败必须可见"这条约束变得模糊。
/// </para>
/// </remarks>
public sealed class HttpClientChatClient : IChatClient
{
    private readonly HttpClient _http;
    private readonly string _baseUrl;
    private readonly string _apiKey;
    private readonly string _model;
    private readonly IChatWireFormat _format;

    public HttpClientChatClient(
        string baseUrl, string apiKey, string model,
        IChatWireFormat format, HttpClient? http = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseUrl);
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        ArgumentNullException.ThrowIfNull(format);

        _baseUrl = baseUrl.TrimEnd('/');
        _apiKey = apiKey;
        _model = model;
        _format = format;
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
    }

    /// <summary>本连接器使用的协议。</summary>
    public ChatProtocol Protocol => _format.Protocol;

    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        // 非流式只是"把流全部收集起来"：这样两种调用路径共用同一套解析，
        // 不会出现流式正确、非流式漏字段的双实现漂移。
        var contents = new List<AIContent>();
        UsageDetails? usage = null;

        await foreach (var update in GetStreamingResponseAsync(messages, options, cancellationToken)
            .ConfigureAwait(false))
        {
            foreach (var content in update.Contents)
            {
                // 用量走标准的 UsageContent，而不是自定义附加属性：
                // M.E.AI 的中间件与 ChatResponse 聚合都识别 UsageContent，
                // 自定义键对它们不可见。
                if (content is UsageContent usageContent)
                    usage = usageContent.Details;
                else
                    contents.Add(content);
            }
        }

        var message = new ChatMessage(ChatRole.Assistant, contents);
        return new ChatResponse(message) { Usage = usage };
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messages);

        var body = _format.BuildBody(_model, [.. messages], options, stream: true);
        using var request = CreateRequest(body);
        using var response = await _http
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync(cancellationToken)
                .ConfigureAwait(false);

            var snippet = error[..Math.Min(400, error.Length)];

            // **必须先判溢出，再抛通用 HTTP 错误。**
            //
            // 溢出是最有希望自动恢复的一类失败：压缩一次就能继续。
            // 若在这里统一抛 HttpRequestException，上层只能看到"400"，
            // 不知道该压还是该报错——而压错一次是有损的（历史被摘要替换）。
            //
            // 识别依据是响应体文本而不是状态码：400 也可能是 schema 错误。
            if (ContextOverflowDetector.IsContextOverflow(error))
            {
                throw new ContextOverflowException(
                    $"provider 判定上下文超限（HTTP {(int)response.StatusCode}）：{snippet}",
                    providerError: new HttpRequestException(
                        $"provider 返回 {(int)response.StatusCode}：{snippet}",
                        inner: null,
                        response.StatusCode));
            }

            // **必须把 StatusCode 传进构造**，不能只写进消息文本。
            // 消息文本要靠正则解析才能取状态码，而解析会失败——
            // 上游任何一处改措辞，失败分类就静默退化成 Unknown。
            // .NET 的 HttpRequestException 有专门的 StatusCode 属性，用它。
            throw new HttpRequestException(
                $"provider 返回 {(int)response.StatusCode}：{snippet}",
                inner: null,
                response.StatusCode);
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);
        using var reader = new StreamReader(stream, Encoding.UTF8);

        var decoder = _format.CreateDecoder();
        var updates = new List<ChatResponseUpdate>();

        // 诊断用的两个计数：看到了多少条 SSE 负载、其中多少条解出了内容。
        //
        // **没有它们就无法区分"模型没说话"与"我们解析不了"**——两种情况
        // 都是零个 update，但前者正常、后者是协议层缺陷。
        // 实测过：provider 返回 200 + 一坨 HTML（网关错误页）时，
        // 这里静默走完，账目记成成功，用户看到空回复而无从判断原因。
        var payloadCount = 0;
        var decodedContentCount = 0;
        var sawTerminalSignal = false;

        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            // 只处理 SSE 的 data 行；注释行与 event 行不含负载。
            if (!line.StartsWith("data: ", StringComparison.Ordinal)) continue;
            var payload = line[6..];
            if (payload is "[DONE]") { sawTerminalSignal = true; break; }

            payloadCount++;
            updates.Clear();
            decoder.Decode(payload, updates);
            foreach (var update in updates) yield return update;
            decodedContentCount += updates.Count;
            if (decoder.IsTerminal) { sawTerminalSignal = true; break; }
        }

        var flushed = decoder.Flush();
        foreach (var update in flushed) yield return update;
        decodedContentCount += flushed.Count;

        // **一条 SSE 负载都没看到** 且没有终结信号 ⇒ 响应体不是 SSE。
        // 常见的 200 + HTML 错误页、被中间设备改写的响应都属于这一类。
        // 有终结信号时允许零内容（模型确实可以什么都不说）。
        if (payloadCount == 0 && !sawTerminalSignal && decoder.Usage is null)
        {
            throw new InvalidWireResponseException(
                "响应体里没有任何 SSE 数据帧，也没有终结信号——"
                + "这不是本协议的有效响应。常见原因：网关返回了 HTML 错误页、"
                + "代理改写了响应、或端点路径指向了非流式接口。"
                + "（HTTP 状态码是 200，所以不会走状态码错误分支。）");
        }

        // 看到了负载但一条内容都没解出来 ⇒ 协议实现与 provider 输出不符。
        // 这是 SDK 侧要修的问题，不能当成"模型没说话"。
        if (payloadCount > 0 && decodedContentCount == 0)
        {
            throw new InvalidWireResponseException(
                $"收到 {payloadCount} 条 SSE 数据帧，但解析不出任何内容——"
                + "协议实现与该 provider 的实际输出不符。");
        }

        if (decoder.Usage is { } usage)
        {
            // 用量用标准的 UsageContent 承载：M.E.AI 的聚合与中间件认这个类型，
            // 自定义附加属性对它们不可见，账目会静默丢失。
            yield return new ChatResponseUpdate(ChatRole.Assistant, [new UsageContent(usage)]);
        }
    }

    private HttpRequestMessage CreateRequest(JsonObject body)
    {
        var request = new HttpRequestMessage(
            HttpMethod.Post, $"{_baseUrl}{_format.RelativePath}")
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        _format.ApplyHeaders(request, _apiKey);
        return request;
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose() => _http.Dispose();
}
