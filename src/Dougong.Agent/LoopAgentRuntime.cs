using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;

namespace Dougong.Agent;

/// <summary>
/// Dougong 的 agent 循环：直接驱动 <see cref="IChatClient"/>，自己控制轮次与工具派发。
/// </summary>
/// <remarks>
/// 这是 M1 候选对比之后选定的实现（见 docs/m1-candidate-comparison.md）。
/// 刻意不套 <c>FunctionInvokingChatClient</c>：任何会自行执行工具的中间件
/// 都会绕过 <see cref="IToolDispatcher"/>，破坏唯一执行入口。
/// <para>
/// 提交屏障的四个落点：用户输入、完整模型响应、工具调用开始、工具调用终态。
/// 任一处提交失败即停止运行，不重试也不降级。
/// </para>
/// <para>
/// 上下文压缩在每轮调用模型前检查一次，且是可选的：不传 <see cref="ContextLimits"/>
/// 即保持原有行为（把完整历史送进模型）。压缩失败或预算仍不足时**保留原历史并继续**
/// ——中断一个本来能跑的任务，比让它带着过长的上下文被 provider 拒绝更糟，也更难诊断。
/// </para>
/// </remarks>
public sealed class LoopAgentRuntime : IAgentRuntime
{
    private readonly IChatClient _client;
    private readonly IDispatcherAccessor _dispatcher;
    private readonly IStoreAccessor? _store;
    private readonly ContextLimits? _limits;
    private readonly IContextCompactor? _compactor;
    private readonly Func<string, CancellationToken, ValueTask<string>>? _summarize;

    /// <summary>
    /// L1 偏好注入器。为 null 时不注入任何偏好。
    /// </summary>
    /// <remarks>
    /// 用 <c>init</c> 属性而不是构造参数：后者会改变构造签名
    /// （ApiCompat 会报 CP0002，破坏已有调用的兼容性）。
    /// </remarks>
    public IMemoryInjector? Memory { get; init; }

    /// <summary>
    /// 当前工作区。memory 注入按它过滤作用域。
    /// </summary>
    /// <remarks>
    /// 只在使用 <see cref="Memory"/> 时才有意义；默认空串表示"未指定"，
    /// 此时只注入 <see cref="MemoryScope.User"/> 级偏好——跨工作区默认不传播。
    /// </remarks>
    public string MemoryWorkspace { get; init; } = string.Empty;

    /// <summary>用裸 dispatcher 构造：内部包成固定访问点（不参与替换）。</summary>
    public LoopAgentRuntime(
        IChatClient client,
        IToolDispatcher dispatcher,
        IStoreAccessor? store = null,
        ContextLimits? limits = null,
        IContextCompactor? compactor = null,
        Func<string, CancellationToken, ValueTask<string>>? summarize = null)
        : this(client, new FixedDispatcherAccessor(dispatcher), store, limits, compactor, summarize)
    {
    }

    /// <summary>
    /// 用访问点构造：每回合/每次派发都重新取当前 dispatcher。
    /// 内核路径下传入，使替换 dispatcher 后不再用旧实例。
    /// </summary>
    public LoopAgentRuntime(
        IChatClient client,
        IDispatcherAccessor dispatcher,
        IStoreAccessor? store = null,
        ContextLimits? limits = null,
        IContextCompactor? compactor = null,
        Func<string, CancellationToken, ValueTask<string>>? summarize = null)
    {
        _client = client;
        _dispatcher = dispatcher;
        _store = store;
        _limits = limits;
        _compactor = compactor;
        _summarize = summarize;
    }

    public async IAsyncEnumerable<AgentEvent> RunAsync(
        AgentRunRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        yield return new AgentEvent.RunStarted(request.SessionId, request.TurnId);

        // 屏障 1：用户输入必须在任何模型调用之前提交。
        // 提交失败即停止——不继续、不降级、不假装成功。
        if (await CommitOrNullAsync(StoreRecordKind.UserInput,
                    JsonSerializer.SerializeToElement(request.UserInput), cancellationToken)
                is { } inputFailure)
        {
            yield return inputFailure;
            yield break;
        }

        // 压缩只处理进入循环前的基线历史（request.History）。
        // 循环内部追加的 assistant/tool 交互不参与压缩：它们是最新的工作上下文，
        // 且必须成对保留——把其中一半压掉就会得到悬空的 tool_calls。
        IReadOnlyList<AgentMessage> baseline = new List<AgentMessage>(request.History);
        var history = BuildHistory(baseline, request.UserInput);
        // 循环内追加的交互从这一下标开始；压缩时只替换它之前的前缀。
        var tailStart = history.Count;

        for (var round = 0; round < request.MaxToolRounds; round++)
        {
            // 每轮调用模型前检查预算。压缩失败不中断运行——见类型注释。
            if (_limits is not null && _compactor is not null)
            {
                var compaction = await TryCompactAsync(baseline, cancellationToken)
                    .ConfigureAwait(false);
                if (compaction.Event is not null) yield return compaction.Event;

                if (compaction.Replaced is { } replaced)
                {
                    baseline = replaced;
                    // 只替换前缀：[基线转换后...] + [本轮用户输入 ... 尾部追加]
                    // 尾部（循环内产生的 assistant/tool 交互）原样保留，
                    // 它们是当前工作上下文，且必须成对存在。
                    var rebuilt = BuildHistory(baseline, request.UserInput);
                    rebuilt.AddRange(history.Skip(tailStart));
                    history = rebuilt;
                    tailStart = baseline.Count + 1;
                }
            }
            // 聚合本轮流式输出：文本直接投影，函数调用按 ID 收集完整参数。
            var text = new StringBuilder();
            var calls = new List<PendingCall>();
            // 某些 provider（如 DeepSeek 思考模式）要求带工具调用的 assistant 消息
            // 回传思考内容，否则请求被拒。字段名由连接器翻译，本层只用中性键。
            var reasoning = new StringBuilder();
            var fragmentCount = new Dictionary<string, int>(StringComparer.Ordinal);

            // 工具声明是运行时对连接器的正式输出，不是循环拼的提示文本。
            var chatOptions = new ChatOptions().Attach(await _dispatcher.GetToolsAsync(cancellationToken).ConfigureAwait(false));

            // L1 偏好注入：**每轮取一次快照**，整轮使用它。
            // 运行中撤销不影响当前轮——已发送给模型的内容无法撤回。
            var requestHistory = await WithMemoryAsync(history, cancellationToken).ConfigureAwait(false);

            await using var enumerator = _client
                .GetStreamingResponseAsync(requestHistory, chatOptions, cancellationToken)
                .GetAsyncEnumerator(cancellationToken);

            while (true)
            {
                bool moved;
                string? streamFailure = null;
                var cancelled = false;
                try { moved = await enumerator.MoveNextAsync(); }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    moved = false;
                    cancelled = true;
                }
                catch (Exception ex)
                {
                    moved = false;
                    streamFailure = $"模型流中断：{ex.Message}" + (ex.InnerException is { } i ? $" | inner: {i.GetType().Name}: {i.Message}" : "");
                }

                // 取消与失败必须先处理：此时 Current 不可访问。
                if (cancelled)
                {
                    yield return await FinishAsync(AgentRunOutcome.Cancelled, "模型调用被取消。", cancellationToken).ConfigureAwait(false);
                    yield break;
                }

                if (streamFailure is not null)
                {
                    yield return await FinishAsync(AgentRunOutcome.Failed, streamFailure, cancellationToken).ConfigureAwait(false);
                    yield break;
                }

                if (!moved) break;

                if (enumerator.Current.AdditionalProperties is { } updateProps
                    && updateProps.TryGetValue(AgentPropertyKeys.Reasoning, out var reasoningValue)
                    && reasoningValue?.ToString() is { Length: > 0 } reasoningText)
                    reasoning.Append(reasoningText);

                foreach (var content in enumerator.Current.Contents)
                {
                    switch (content)
                    {
                        case TextContent textContent when !string.IsNullOrEmpty(textContent.Text):
                            text.Append(textContent.Text);
                            yield return new AgentEvent.TextDelta(textContent.Text);
                            break;

                        case FunctionCallContent functionCall:
                            // 参数分片的真相：provider（GLM/Anthropic）逐段给出
                            // input_json_delta.partial_json，是字节片段而非解析后的字典。
                            // M.E.AI 的 Arguments 是已解析字典，取不到原始片段，
                            // 因此这里保留"最近一次完整字典快照"的序列化原文，
                            // 并在派发前重新解析——语义等价于拼接后解析。
                            var id = functionCall.CallId;
                            if (string.IsNullOrEmpty(id))
                            {
                                calls.Add(new PendingCall(string.Empty, functionCall.Name,
                                    [], IsMalformed: true, Fragments: null));
                                break;
                            }

                            fragmentCount[id] = fragmentCount.GetValueOrDefault(id) + 1;
                            var fragment = functionCall.Arguments is null
                                ? null
                                : JsonSerializer.Serialize(functionCall.Arguments);
                            var existing = calls.FindIndex(c => c.Id == id);
                            if (existing < 0)
                            {
                                calls.Add(new PendingCall(id, functionCall.Name,
                                    functionCall.Arguments is null
                                        ? []
                                        : new Dictionary<string, object?>(functionCall.Arguments),
                                    IsMalformed: false,
                                    Fragments: fragment ?? "{}"));
                            }
                            else
                            {
                                var merged = new Dictionary<string, object?>(calls[existing].Arguments);
                                if (functionCall.Arguments is not null)
                                    foreach (var pair in functionCall.Arguments)
                                        if (merged.TryGetValue(pair.Key, out var previous)
                                            && !Equals(previous, pair.Value))
                                        {
                                            // 同一键被赋予不同值：冲突而不是分片。
                                            calls[existing] = calls[existing] with { IsMalformed = true };
                                        }
                                        else
                                        {
                                            merged[pair.Key] = pair.Value;
                                        }

                                calls[existing] = calls[existing] with
                                {
                                    Arguments = merged,
                                    Fragments = fragment ?? calls[existing].Fragments,
                                };
                            }

                            break;
                    }
                }
            }

            if (calls.Count == 0)
            {
                // 纯文本轮次才单独补 assistant 消息。
                // 有工具调用时，文本必须与 tool_calls 合进同一条 assistant 消息：
                // 分开写会得到两条相邻的 assistant 消息，思考模式的 provider
                // 会认为带 tool_calls 的那条缺少思考内容而拒掉整轮（实测 400）。
                if (text.Length > 0)
                {
                    history.Add(new ChatMessage(ChatRole.Assistant, text.ToString())
                    {
                        AdditionalProperties = reasoning.Length > 0
                            ? new AdditionalPropertiesDictionary { [AgentPropertyKeys.Reasoning] = reasoning.ToString() }
                            : null,
                    });
                }

                // 纯文本轮次同样要提交模型响应：否则助手回复从不入库，
                // 会话恢复时只剩用户消息，模型不记得自己说过什么。
                // 这是真实缺陷——它只在"恢复一个纯对话会话"时暴露，
                // 因为带工具的轮次本来就会在下面提交。
                var plainResponse = JsonSerializer.Serialize(new
                {
                    text = text.ToString(),
                    reasoning = reasoning.ToString(),
                    calls = Array.Empty<object>(),
                });
                if (await CommitOrNullAsync(
                        StoreRecordKind.ModelResponse, ToElement(plainResponse), cancellationToken)
                        is { } plainFailure)
                {
                    yield return plainFailure;
                    yield break;
                }

                yield return await FinishAsync(AgentRunOutcome.Completed, null, cancellationToken).ConfigureAwait(false);
                yield break;
            }

            // 先整体校验调用集，任一无效则整轮不执行。
            var invalid = calls.FirstOrDefault(c => c.IsMalformed);
            if (invalid is not null)
            {
                foreach (var call in calls)
                {
                    var rejected = new ToolDispatchResult(
                        call.Id, call.ToolId, ToolCallOutcome.RejectedInvalidArguments,
                        Diagnostic: "调用 ID 缺失或参数不完整。");
                    yield return new AgentEvent.ToolCallCompleted(rejected);
                }

                yield return await FinishAsync(
                    AgentRunOutcome.InvalidModelResponse, "模型响应包含无效调用。", cancellationToken).ConfigureAwait(false);
                yield break;
            }

            // 同一 ID 出现多次是分片累积，不算重复；
            // 重复指同一 ID 携带了相同参数键的第二个独立片段，这里以“参数键冲突”识别。
            var duplicate = calls
                .GroupBy(c => c.Id, StringComparer.Ordinal)
                .FirstOrDefault(g => g.Count() > 1);
            if (duplicate is not null)
            {
                foreach (var call in calls)
                {
                    yield return new AgentEvent.ToolCallCompleted(new ToolDispatchResult(
                        call.Id, call.ToolId, ToolCallOutcome.RejectedDuplicateInvocation,
                        Diagnostic: "本轮存在重复调用 ID。"));
                }

                yield return await FinishAsync(
                    AgentRunOutcome.InvalidModelResponse, "模型响应包含重复调用 ID。", cancellationToken).ConfigureAwait(false);
                yield break;
            }

            // 屏障 2：完整模型响应（含工具调用请求）必须在任何派发之前提交。
            // 这是"先提交调用意图、再产生副作用"的核心。
            // 参数必须入库：只记 id 与工具名的话，恢复出的历史无法重新表达调用，
            // resume 之后的第一轮请求就会缺参数。
            var responsePayload = JsonSerializer.Serialize(new
            {
                text = text.ToString(),
                reasoning = reasoning.ToString(),
                calls = calls.Select(c => new
                {
                    id = c.Id,
                    tool = c.ToolId,
                    arguments = ParseArguments(c),
                }),
            });
            if (await CommitOrNullAsync(StoreRecordKind.ModelResponse, ToElement(responsePayload), cancellationToken)
                    is { } responseFailure)
            {
                yield return responseFailure;
                yield break;
            }

            var toolResults = new List<(string Id, string ToolId, ToolDispatchResult Result)>();
            // 取一次工具描述符快照：开始记录要写入"是否有不可撤销副作用"。
            // 用快照而不是每次查询，避免循环内工具被注销导致前后不一致。
            var toolDescriptors =
                (await _dispatcher.GetToolsAsync(cancellationToken).ConfigureAwait(false))
                    .ToDictionary(t => t.Id, StringComparer.Ordinal);

            foreach (var call in calls)
            {
                // 用累积片段的原文重新解析，而不是直接序列化字典：
                // 这样能真实反映 provider 给出的 JSON（含嵌套、数组、数字精度）。
                var args = ParseArguments(call);

                yield return new AgentEvent.ToolCallRequested(call.Id, call.ToolId, args);

                // 屏障 3：工具开始记录必须落盘，才允许执行（可能产生副作用）。
                // 参数必须入库：恢复时若只有 id+tool，宿主连"当时要做什么"都不知道，
                // 无法对账未知结果，更无法判断能否安全重试（G10）。
                // 记录"这次调用是否可能有不可撤销副作用"：
                // 恢复时若发现无终态，宿主据此判断能否安全重试。
                // 工具可能在派发前被注销，取不到描述符时保守按 true 记。
                var mayHaveEffects = toolDescriptors.TryGetValue(call.ToolId, out var descriptor)
                    ? descriptor.MayHaveIrreversibleEffects
                    : true;   // 取不到描述符（工具已注销）时保守按"有副作用"记

                var startedPayload = JsonSerializer.Serialize(new
                {
                    id = call.Id,
                    tool = call.ToolId,
                    arguments = args.GetRawText(),
                    mayHaveIrreversibleEffects = mayHaveEffects,
                });
                if (await CommitOrNullAsync(StoreRecordKind.ToolCallStarted, ToElement(startedPayload), cancellationToken)
                        is { } startedFailure)
                {
                    // 未提交则绝不执行；该调用保持"未开始"语义。
                    yield return startedFailure;
                    yield break;
                }

                var invocation = new ToolInvocation(call.Id, call.ToolId, args);
                var result = await _dispatcher.DispatchAsync(invocation, cancellationToken);

                // 屏障 4：终态也要提交，否则恢复时无法判断副作用是否发生。
                // 结果内容同样入库：恢复出的 tool 消息必须有内容，
                // 空内容会让模型以为工具什么也没做。
                var completedPayload = JsonSerializer.Serialize(new
                {
                    id = call.Id,
                    tool = call.ToolId,
                    outcome = result.Outcome.ToString(),
                    diagnostic = result.Diagnostic,
                    content = result.Result?.Content.ValueKind is JsonValueKind.Undefined or null
                        ? null
                        : result.Result.Content.GetRawText(),
                });
                if (await CommitOrNullAsync(StoreRecordKind.ToolCallCompleted, ToElement(completedPayload), cancellationToken)
                        is { } completedFailure)
                {
                    yield return new AgentEvent.ToolCallCompleted(result);
                    yield return completedFailure;
                    yield break;
                }

                yield return new AgentEvent.ToolCallCompleted(result);

                // 计划工具成功执行 → 投影为 PlanUpdated。
                // 只在成功时发：失败的计划更新不该被当真，否则 UI 会显示
                // 一份"模型以为成功但实际被拒"的计划。
                if (result.Outcome == ToolCallOutcome.Succeeded
                    && TryReadPlan(result, out var plan))
                {
                    yield return new AgentEvent.PlanUpdated(plan);
                }

                toolResults.Add((call.Id, call.ToolId, result));
            }

            if (cancellationToken.IsCancellationRequested)
            {
                yield return await FinishAsync(AgentRunOutcome.Cancelled, null, cancellationToken).ConfigureAwait(false);
                yield break;
            }

            // 回填必须符合协议：assistant 消息携带 tool_calls，每条 tool 结果带 tool_call_id。
            // 早期实现把结果压成一条普通 Tool 文本，缺 tool_call_id 会被 provider 拒绝（422）。
            foreach (var truncation in AppendToolExchange(
                history, calls, toolResults, text.ToString(), reasoning.ToString()))
            {
                // 截断不是错误，但必须让宿主知道模型看到的是裁过的内容。
                yield return truncation;
            }
        }

        yield return await FinishAsync(AgentRunOutcome.ToolRoundLimitReached, null, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 若有注入器，把启用中的偏好拼成一条 system 消息放在最前。
    /// </summary>
    /// <remarks>
    /// **拼在最前**：偏好是背景资料，不应插在对话中间打断上下文连续性。
    /// <para>
    /// 注入失败（存储读不到等）**不阻断本轮**：记忆是增强，不是必需品。
    /// 但失败必须有痕迹——静默丢偏好会让用户以为它生效了。
    /// </para>
    /// </remarks>
    private async ValueTask<List<ChatMessage>> WithMemoryAsync(
        List<ChatMessage> history, CancellationToken cancellationToken)
    {
        if (Memory is null) return history;

        IReadOnlyList<MemoryArtifact> snapshot;
        try
        {
            snapshot = await Memory.SnapshotAsync(MemoryWorkspace, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            // 读不到偏好时按"没有偏好"继续，不中断会话。
            return history;
        }

        var prompt = MemoryPrompt.Compose(snapshot, out _);
        if (prompt is null) return history;

        var withMemory = new List<ChatMessage>(history.Count + 1)
        {
            new(ChatRole.System, prompt),
        };
        withMemory.AddRange(history);
        return withMemory;
    }

    /// <summary>
    /// 从工具结果里读出计划条目。只认 <see cref="PlanTool.ToolId"/> 的成功结果。
    /// </summary>
    /// <remarks>
    /// 解析失败返回 false 而**不抛异常**：计划是展示用的附加信息，
    /// 它坏掉不该让整轮运行失败。但如果静默丢弃，用户会以为"模型没有计划"——
    /// 所以调用方应当在 false 时不发事件（当前行为），而不是发一个空计划。
    /// </remarks>
    private static bool TryReadPlan(ToolDispatchResult result, out IReadOnlyList<PlanEntry> entries)
    {
        entries = [];

        if (!string.Equals(result.ToolId, PlanTool.ToolId, StringComparison.Ordinal))
            return false;
        if (result.Result is not { } value) return false;
        if (value.Content.ValueKind is not JsonValueKind.Object) return false;
        if (!value.Content.TryGetProperty("ok", out var ok) || ok.ValueKind is not JsonValueKind.True)
            return false;
        if (!value.Content.TryGetProperty("entries", out var array)
            || array.ValueKind is not JsonValueKind.Array)
            return false;

        var parsed = new List<PlanEntry>(array.GetArrayLength());
        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind is not JsonValueKind.Object) continue;
            var content = item.TryGetProperty("content", out var c) ? c.GetString() : null;
            var status = item.TryGetProperty("status", out var s) ? s.GetString() : null;
            var priority = item.TryGetProperty("priority", out var p) ? p.GetString() : null;
            if (string.IsNullOrWhiteSpace(content) || string.IsNullOrWhiteSpace(status)) continue;

            parsed.Add(new PlanEntry(content!, priority ?? string.Empty, status!));
        }

        entries = parsed;
        return true;
    }

    /// <summary>
    /// 按协议回填工具交互：先补一条携带 tool_calls 的 assistant 消息，
    /// 再为每个调用追加带 tool_call_id 的 tool 消息。
    /// </summary>
    /// <returns>发生截断的工具结果列表；无截断时为空。</returns>
    private static List<AgentEvent.ToolResultTruncated> AppendToolExchange(
        List<ChatMessage> history,
        IReadOnlyList<PendingCall> calls,
        IReadOnlyList<(string Id, string ToolId, ToolDispatchResult Result)> results,
        string text,
        string reasoning)
    {
        var truncations = new List<AgentEvent.ToolResultTruncated>();
        var toolCalls = new List<AIContent>();
        foreach (var call in calls)
        {
            toolCalls.Add(new FunctionCallContent(
                call.Id,
                call.ToolId,
                ParseArguments(call) is var element && element.ValueKind is JsonValueKind.Object
                    ? element.EnumerateObject().ToDictionary(p => p.Name, p => (object?)p.Value.Clone())
                    : null));
        }

        // 本轮的文本与 tool_calls 必须是同一条 assistant 消息：
        // 拆成两条会让带 tool_calls 的那条失去上下文的连续性，
        // 思考模式的 provider 会因此拒掉整轮请求。
        if (text.Length > 0) toolCalls.Insert(0, new TextContent(text));

        history.Add(new ChatMessage(ChatRole.Assistant, toolCalls)
        {
            AdditionalProperties = reasoning.Length > 0
                ? new AdditionalPropertiesDictionary { [AgentPropertyKeys.Reasoning] = reasoning }
                : null,
        });

        foreach (var (id, toolId, result) in results)
        {
            var raw = result.Result?.Content.ValueKind is JsonValueKind.Undefined or null
                ? JsonSerializer.Serialize(new { outcome = result.Outcome.ToString(), diagnostic = result.Diagnostic })
                : result.Result.Content.GetRawText();

            // 只在这里裁剪：这是"即将进入模型上下文"的唯一位置。
            // 会话日志在上面的提交记录里已写入完整原文，恢复会话时也用完整原文，
            // 所以裁剪不影响数据完整性。
            var content = ToolResultLimits.Clamp(raw, out var truncated);

            if (truncated)
            {
                truncations.Add(new AgentEvent.ToolResultTruncated(
                    id, toolId, raw.Length, content.Length));
            }

            history.Add(new ChatMessage(ChatRole.Tool, content)
            {
                // TextContent 必须在 Contents 里：ChatMessage.Text 由 Contents 派生，
                // 只放 FunctionResultContent 会让 Text 变空，三种协议拿到的工具结果都是空字符串。
                Contents = [new TextContent(content), new FunctionResultContent(id, content)],
                AdditionalProperties = new AdditionalPropertiesDictionary
                {
                    ["tool_call_id"] = id,
                    ["name"] = toolId,
                },
            });
        }

        return truncations;
    }

    /// <summary>
    /// 运行终止的统一出口：先提交终态记录，再返回事件。
    /// </summary>
    /// <remarks>
    /// 收口到一个方法而不是在每个 <c>yield break</c> 前各写一遍：
    /// 终止点有七处（完成、取消、流中断、两种无效响应、轮次上限），
    /// 逐处插入容易漏，漏掉的那一处会让恢复时看不出这个 turn 是否正常结束。
    /// <para>
    /// 提交失败不改变已经发生的事实：仍要发出 <see cref="AgentEvent.RunFinished"/>，
    /// 否则调用方会以为运行还在继续。失败原因并入诊断。
    /// </para>
    /// </remarks>
    private async ValueTask<AgentEvent.RunFinished> FinishAsync(
        AgentRunOutcome outcome, string? diagnostic, CancellationToken cancellationToken)
    {
        if (_store is null) return new AgentEvent.RunFinished(outcome, diagnostic);

        var payload = JsonSerializer.SerializeToElement(new
        {
            outcome = outcome.ToString(),
            diagnostic,
        });

        try
        {
            await _store.CommitAsync(
                new StoreRecord(0, StoreRecordKind.RunFinished, payload),
                cancellationToken).ConfigureAwait(false);
            return new AgentEvent.RunFinished(outcome, diagnostic);
        }
        catch (Exception ex)
        {
            var combined = diagnostic is null
                ? $"运行终态提交失败：{ex.Message}"
                : $"{diagnostic} | 运行终态提交失败：{ex.Message}";
            return new AgentEvent.RunFinished(outcome, combined);
        }
    }

    /// <summary>
    /// 提交一条记录。成功返回 null；失败返回应向外发出的终止事件。
    /// 未配置存储时视为"无持久化要求"，直接放行（仅用于对比与测试）。
    /// </summary>
    private async ValueTask<AgentEvent.RunFinished?> CommitOrNullAsync(
        StoreRecordKind kind, JsonElement payload, CancellationToken cancellationToken)
    {
        if (_store is null) return null;

        try
        {
            await _store.CommitAsync(
                new StoreRecord(0, kind, payload),
                cancellationToken).ConfigureAwait(false);
            return null;
        }
        catch (Exception ex)
        {
            // 提交失败即停止执行：不继续、不重试、不降级为无持久化模式。
            return new AgentEvent.RunFinished(
                AgentRunOutcome.Failed,
                $"提交失败（{kind}）：{ex.Message}");
        }
    }

    /// <summary>
    /// 把已序列化的 JSON 文本变成独立拥有的元素。
    /// 早期实现用 <c>JsonSerializer.Serialize(string)</c> 再包一层，
    /// 把 JSON 变成字符串字面量存进日志，恢复时必须先解开一层才能读。
    /// 那是双重编码，不是格式设计；这里直接按结构存。
    /// </summary>
    private static JsonElement ToElement(string json) =>
        JsonDocument.Parse(json).RootElement.Clone();

    private static ChatRole ToRole(AgentRole role) => role switch
    {
        AgentRole.System => ChatRole.System,
        AgentRole.Assistant => ChatRole.Assistant,
        AgentRole.Tool => ChatRole.Tool,
        _ => ChatRole.User,
    };

    /// <summary>用基线历史构造初始请求消息，末尾追加本轮用户输入。</summary>
    private static List<ChatMessage> BuildHistory(
        IReadOnlyList<AgentMessage> baseline, string userInput)
    {
        var history = new List<ChatMessage>(baseline.Count + 1);
        foreach (var message in baseline) history.Add(ToChatMessage(message));
        history.Add(new ChatMessage(ChatRole.User, userInput));
        return history;
    }

    /// <summary>
    /// 检查预算并按需压缩。返回替换后的基线与应发出的事件。
    /// </summary>
    /// <remarks>
    /// 三条件都不满足才替换：计划存在、任务跑到 <see cref="CompactionPhase.Completed"/>、
    /// 且 <see cref="CompactionApply.TryApply"/> 成功。任一不满足都保留原历史——
    /// 压缩是优化，不能成为新的失败点。
    /// <para>
    /// 没有 <c>summarize</c> 注入时不压缩：当前的 <see cref="SlidingWindowCompactor"/>
    /// 只负责选区，真正的摘要需要一次模型调用。宁可如实不压，也不假装压过。
    /// </para>
    /// </remarks>
    private async ValueTask<(IReadOnlyList<AgentMessage>? Replaced, AgentEvent? Event)>
        TryCompactAsync(IReadOnlyList<AgentMessage> baseline, CancellationToken cancellationToken)
    {
        try
        {
            var plan = _compactor!.Plan(_limits!.ToInput(baseline));
            if (plan is null) return (null, null);

            if (_summarize is null)
            {
                // 只能选区、无法摘要：如实报告"未压缩"，不产出半成品历史。
                return (null, new AgentEvent.ContextCompacted(
                    plan.LastIndex - plan.FirstIndex + 1,
                    plan.EstimatedTokensBefore,
                    plan.EstimatedTokensBefore,
                    Applied: false,
                    Diagnostic: "未配置摘要器，跳过压缩；历史保持不变。"));
            }

            var task = new SummarizingCompactionTask(
                plan, baseline, _summarize, RenderForSummary);

            // 逐阶段推进到终态；中间态可序列化，崩溃后能续跑（由宿主决定是否持久化）。
            var steps = 0;
            while (!task.Checkpoint.IsTerminal && steps++ < 8)
                await task.AdvanceAsync(cancellationToken).ConfigureAwait(false);

            var checkpoint = task.Checkpoint;
            if (checkpoint.Phase != CompactionPhase.Completed)
            {
                return (null, new AgentEvent.ContextCompacted(
                    plan.LastIndex - plan.FirstIndex + 1,
                    plan.EstimatedTokensBefore,
                    plan.EstimatedTokensBefore,
                    Applied: false,
                    Diagnostic: $"压缩未完成（{checkpoint.Phase}）：{checkpoint.FailureReason}"));
            }

            var applied = CompactionApply.TryApply(baseline, checkpoint);
            if (applied is null)
            {
                return (null, new AgentEvent.ContextCompacted(
                    plan.LastIndex - plan.FirstIndex + 1,
                    plan.EstimatedTokensBefore,
                    plan.EstimatedTokensBefore,
                    Applied: false,
                    Diagnostic: "摘要已生成但无法应用（历史已变化）；保留原历史。"));
            }

            return (applied, new AgentEvent.ContextCompacted(
                plan.LastIndex - plan.FirstIndex + 1,
                plan.EstimatedTokensBefore,
                TokenBudget.Estimate(applied),
                Applied: true));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // 压缩自身出错不能中断运行：历史不变，任务继续。
            return (null, new AgentEvent.ContextCompacted(
                0, TokenBudget.Estimate(baseline), TokenBudget.Estimate(baseline),
                Applied: false, Diagnostic: $"压缩失败：{ex.GetType().Name}: {ex.Message}"));
        }
    }

    /// <summary>把历史渲染成供摘要器阅读的文本；保留工具标识以便摘要说明调用关系。</summary>
    private static string RenderForSummary(IReadOnlyList<AgentMessage> messages)
    {
        var builder = new StringBuilder();
        foreach (var message in messages)
        {
            builder.Append('[').Append(message.Role).Append(']');
            if (message.ToolId is { Length: > 0 } toolId) builder.Append('(').Append(toolId).Append(')');
            builder.Append(' ').AppendLine(message.Content);
        }

        return builder.ToString();
    }

    /// <summary>
    /// 把已提交历史还原成协议消息。
    /// </summary>
    /// <remarks>
    /// 工具交互不能退化成扁平文本：assistant 的 <c>tool_calls</c> 与 tool 的
    /// <c>tool_call_id</c> 是配对的协议结构，缺任一侧 provider 都会拒掉整轮。
    /// 恢复出来的历史必须与运行时写的形状一致，否则"能恢复"只是看起来能。
    /// </remarks>
    private static ChatMessage ToChatMessage(AgentMessage message)
    {
        var role = ToRole(message.Role);

        if (role == ChatRole.Assistant && message.ToolCalls.Count > 0)
        {
            var contents = new List<AIContent>();
            if (message.Content is { Length: > 0 } text) contents.Add(new TextContent(text));
            foreach (var call in message.ToolCalls)
            {
                contents.Add(new FunctionCallContent(
                    call.InvocationId, call.ToolId,
                    call.Arguments.ValueKind is JsonValueKind.Object
                        ? call.Arguments.EnumerateObject()
                            .ToDictionary(p => p.Name, p => (object?)p.Value.Clone())
                        : null));
            }

            return new ChatMessage(role, contents);
        }

        if (role == ChatRole.Tool)
        {
            var properties = new AdditionalPropertiesDictionary { ["name"] = message.ToolId };
            if (message.ToolCallId is { Length: > 0 } callId)
                properties["tool_call_id"] = callId;

            return new ChatMessage(role, message.Content)
            {
                Contents =
                [
                    new TextContent(message.Content),
                    .. message.ToolCallId is { Length: > 0 } id
                        ? new AIContent[] { new FunctionResultContent(id, message.Content) }
                        : [],
                ],
                AdditionalProperties = properties,
            };
        }

        return new ChatMessage(role, message.Content);
    }

    private sealed record PendingCall(
        string Id,
        string ToolId,
        Dictionary<string, object?> Arguments,
        bool IsMalformed,
        string? Fragments);

    /// <summary>
    /// 把累积的片段原文解析为 JsonElement；失败则退回字典序列化。
    /// 片段为 null 表示参数从未到达（应判为不完整）。
    /// </summary>
    private static JsonElement ParseArguments(PendingCall call)
    {
        if (call.Fragments is { } raw)
        {
            try
            {
                using var doc = JsonDocument.Parse(raw);
                return doc.RootElement.Clone();
            }
            catch (JsonException) { }
        }

        return JsonSerializer.SerializeToElement(call.Arguments);
    }

}
