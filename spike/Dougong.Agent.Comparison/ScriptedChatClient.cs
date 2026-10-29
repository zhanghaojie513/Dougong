using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.AI;

namespace Dougong.Agent.Comparison;

/// <summary>
/// 按脚本产出流式更新的假模型。它不调用任何网络，因此对比的是循环实现，
/// 而不是连接器或模型能力。
/// </summary>
public sealed class ScriptedChatClient(params ScriptedTurn[] turns) : IChatClient
{
    private readonly Queue<ScriptedTurn> _turns = new(turns);
    public int CallCount { get; private set; }
    public List<IReadOnlyList<ChatMessage>> ObservedHistory { get; } = [];

    /// <summary>
    /// 第几次调用抛异常（1 起算）；null 表示不抛。
    /// </summary>
    /// <remarks>
    /// 用于验证"异常路径"的行为，例如上下文溢出后的恢复。
    /// 与 <see cref="ScriptedUpdate.AbortAfter"/> 的区别：那个是流中途断开，
    /// 这个是调用一开始就失败——溢出通常属于后者。
    /// </remarks>
    public int? ThrowOnTurn { get; set; }

    /// <summary>配合 <see cref="ThrowOnTurn"/> 使用的异常实例。</summary>
    public Exception? ThrowException { get; set; }

    /// <summary>
    /// 每次调用都抛这个异常。
    /// </summary>
    /// <remarks>
    /// 用于验证"不该重试"的场景：若实现错误地重试了，调用次数会暴露它。
    /// 与 <see cref="ThrowOnTurn"/> 的区别是那个只抛一次，用来验证恢复后的成功路径。
    /// </remarks>
    public Exception? ThrowOnEveryTurn { get; set; }

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default) =>
        CollectAsync(messages, options, cancellationToken);

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        CallCount++;
        ObservedHistory.Add([.. messages]);

        // 溢出等"调用一开始就失败"的场景：在出队之前抛出，
        // 不消耗脚本轮次——重试时应当还能拿到同一轮脚本。
        if (ThrowOnEveryTurn is { } always) throw always;
        if (ThrowOnTurn == CallCount && ThrowException is { } toThrow) throw toThrow;

        if (!_turns.TryDequeue(out var turn))
            throw new InvalidOperationException("脚本已耗尽：模型被调用了超出预期的次数。");

        foreach (var update in turn.Updates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (update.AbortAfter)
                throw new InvalidOperationException("模拟响应中途中断。");
            yield return update.ToUpdate();
            if (update.Delay is { } delay) await Task.Delay(delay, cancellationToken);
        }
    }

    private async Task<ChatResponse> CollectAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options,
        CancellationToken cancellationToken)
    {
        var contents = new List<AIContent>();
        await foreach (var update in GetStreamingResponseAsync(messages, options, cancellationToken))
            contents.AddRange(update.Contents);
        return new ChatResponse(new ChatMessage(ChatRole.Assistant, contents));
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose() { }
}

/// <summary>一轮模型输出：若干流式片段。</summary>
public sealed record ScriptedTurn(params ScriptedUpdate[] Updates);

/// <summary>
/// 一个流式片段。<see cref="FunctionName"/> 非空时生成函数调用内容，否则生成文本。
/// <see cref="ArgumentsJson"/> 按原文累积，用于模拟被拆分的流式参数。
/// <see cref="Reasoning"/> 非空时把该文本挂到
/// <see cref="Dougong.Agent.AgentPropertyKeys.Reasoning"/> 上（模拟思考模式的 provider）。
/// </summary>
public sealed record ScriptedUpdate(
    string? Text = null,
    string? FunctionCallId = null,
    string? FunctionName = null,
    string? ArgumentsJson = null,
    bool AbortAfter = false,
    TimeSpan? Delay = null,
    long? UsageInput = null,
    long? UsageOutput = null,
    long? UsageTotal = null,
    string? Reasoning = null)
{
    public ChatResponseUpdate ToUpdate()
    {
        if (UsageInput is not null || UsageOutput is not null)
        {
            var input = UsageInput;
            var output = UsageOutput;
            // 用量走标准 UsageContent：自定义附加属性对 M.E.AI 的聚合与中间件不可见。
            return new ChatResponseUpdate(ChatRole.Assistant,
                [new UsageContent(new UsageDetails
                {
                    InputTokenCount = input,
                    OutputTokenCount = output,
                    TotalTokenCount = UsageTotal,
                })]);
        }

        if (FunctionName is null)
        {
            var textUpdate = new ChatResponseUpdate(ChatRole.Assistant, Text ?? string.Empty);
            return WithReasoning(textUpdate);
        }

        var contents = new List<AIContent>
        {
            new FunctionCallContent(
                FunctionCallId ?? string.Empty,
                FunctionName,
                ParseArguments(ArgumentsJson)),
        };

        return WithReasoning(new ChatResponseUpdate(ChatRole.Assistant, contents));
    }

    /// <summary>
    /// 把推理文本挂到中性键上，模拟思考模式的 provider。
    /// </summary>
    /// <remarks>
    /// <b>每帧只带本帧的增量</b>：真实的格式层（<c>OpenAiChatCompletionsFormat</c>）
    /// 挂上 reasoning 后立刻清空缓冲区，所以下游拿到的也是增量。
    /// 这里必须还原同样的语义，否则测出来的行为与生产不一致。
    /// </remarks>
    private ChatResponseUpdate WithReasoning(ChatResponseUpdate update)
    {
        if (Reasoning is null) return update;

        update.AdditionalProperties = new AdditionalPropertiesDictionary
        {
            [Dougong.Agent.AgentPropertyKeys.Reasoning] = Reasoning,
        };
        return update;
    }

    /// <summary>
    /// 构造一个只携带 usage 的片段，用于验证用量记账走标准 <see cref="UsageContent"/>。
    /// </summary>
    public static ScriptedUpdate Usage(long input, long output, long? total = null) =>
        new(UsageInput: input, UsageOutput: output, UsageTotal: total);

    /// <summary>
    /// 返回 null 表示“参数尚未完整”；调用方据此判断是否需要继续聚合。
    /// 刻意不抛异常：畸形 JSON 必须作为可观察结果出现在循环里，而不是在假模型内部炸掉。
    /// </summary>
    private static IDictionary<string, object?>? ParseArguments(string? json)
    {
        if (json is null) return null;
        try
        {
            var element = JsonDocument.Parse(json).RootElement;
            return element.EnumerateObject()
                .ToDictionary(p => p.Name, p => (object?)p.Value.Clone());
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
