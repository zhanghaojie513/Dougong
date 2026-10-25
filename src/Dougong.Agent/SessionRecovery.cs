using System.Text.Json;

namespace Dougong.Agent;

/// <summary>
/// 从已提交记录重建会话。恢复的唯一依据是 JSONL 真相源，不依赖任何进程内状态。
/// </summary>
/// <remarks>
/// 设计约束（docs/architecture.md §4.2、§6.4）：
/// <list type="bullet">
/// <item>只重放已提交记录；未提交流式文本不在恢复保证内</item>
/// <item>有调用开始但无终态的调用判为未知结果，宿主必须暂停该 turn，<b>不得自动重放副作用</b></item>
/// <item>序号缺口说明日志被破坏，不能静默跳过</item>
/// <item>恢复出的历史必须能直接用于下一轮请求：工具交互的
/// <c>tool_calls</c> / <c>tool_call_id</c> 配对要完整</item>
/// </list>
/// </remarks>
public static class SessionRecovery
{
    /// <summary>重放已提交记录，返回可直接续跑的会话状态。</summary>
    public static async Task<RecoveredSession> ReplayAsync(
        IAgentStore store, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);

        var history = new List<AgentMessage>();
        var started = new List<(string Id, string Tool, string? ArgumentsJson, bool MayHaveEffects)>();
        var completed = new Dictionary<string, string?>(StringComparer.Ordinal);
        var truncated = false;
        long lastSequence = 0;

        await foreach (var record in store.ReadCommittedAsync(cancellationToken))
        {
            // 序号必须严格递增；缺口说明日志被破坏，不能静默跳过。
            if (record.Sequence != lastSequence + 1) truncated = true;
            lastSequence = record.Sequence;

            switch (record.Kind)
            {
                case StoreRecordKind.UserInput:
                    if (ReadUserInput(record.Payload) is { Length: > 0 } input)
                        history.Add(new AgentMessage(AgentRole.User, input));
                    break;

                case StoreRecordKind.ModelResponse:
                    AppendModelResponse(history, record.Payload);
                    break;

                case StoreRecordKind.ToolCallStarted:
                    if (Parse(record.Payload) is { } s)
                        started.Add((s.Id, s.Tool, s.ArgumentsJson, s.MayHaveIrreversibleEffects));
                    break;

                case StoreRecordKind.ToolCallCompleted:
                    if (Parse(record.Payload) is { } c)
                    {
                        completed[c.Id] = c.Content;
                        AppendToolResult(history, c);
                    }

                    break;
            }
        }

        var unknown = started
            .Where(s => !completed.ContainsKey(s.Id))
            .Select(s => new UnknownOutcome(
                s.Id, s.Tool,
                DescribeUnknown(s),
                s.ArgumentsJson)
            {
                MayHaveIrreversibleEffects = s.MayHaveEffects,
            })
            .ToList();

        return new RecoveredSession(lastSequence, history, unknown, truncated);
    }

    /// <summary>
    /// 解析一条 ModelResponse。文本与工具调用要合回同一条 assistant 消息：
    /// 拆成两条会让 provider 认为带 tool_calls 的那条缺少上下文。
    /// </summary>
    private static void AppendModelResponse(List<AgentMessage> history, JsonElement payload)
    {
        string? text = null;
        var calls = new List<AgentToolCallRef>();

        if (payload.ValueKind is JsonValueKind.String)
        {
            // 旧格式（只有纯文本）也要能读，否则历史会话会整体失效。
            text = payload.GetString();
        }
        else if (payload.ValueKind is JsonValueKind.Object)
        {
            text = payload.TryGetProperty("text", out var textElement)
                ? textElement.GetString() : null;

            if (payload.TryGetProperty("calls", out var callsElement)
                && callsElement.ValueKind is JsonValueKind.Array)
            {
                foreach (var item in callsElement.EnumerateArray())
                {
                    var id = item.TryGetProperty("id", out var idElement)
                        ? idElement.GetString() : null;
                    var tool = item.TryGetProperty("tool", out var toolElement)
                        ? toolElement.GetString() : null;
                    if (id is null || tool is null) continue;

                    var args = item.TryGetProperty("arguments", out var argsElement)
                        ? argsElement.Clone()
                        : JsonSerializer.SerializeToElement(new { });
                    calls.Add(new AgentToolCallRef(id, tool, args));
                }
            }
        }

        if (string.IsNullOrEmpty(text) && calls.Count == 0) return;

        history.Add(new AgentMessage(AgentRole.Assistant, text ?? string.Empty)
        {
            ToolCalls = calls,
        });
    }

    /// <summary>回填一条工具结果。内容缺失时如实写"无内容"，不伪造成功。</summary>
    private static void AppendToolResult(List<AgentMessage> history, ToolRecord call)
    {
        var content = call.Content;
        if (string.IsNullOrEmpty(content))
        {
            content = JsonSerializer.Serialize(new
            {
                outcome = call.Outcome,
                diagnostic = call.Diagnostic,
            });
        }

        history.Add(new AgentMessage(AgentRole.Tool, content)
        {
            ToolCallId = call.Id,
            ToolId = call.Tool,
        });
    }

    /// <summary>
    /// 读用户输入。字符串即原文；对象形式是早期双重编码留下的痕迹，
    /// 按 raw text 读仍然可解释，不必为了格式洁癖让旧会话失效。
    /// </summary>
    private static string? ReadUserInput(JsonElement payload) =>
        payload.ValueKind is JsonValueKind.String ? payload.GetString() : payload.GetRawText();

    private static ToolRecord? Parse(JsonElement payload)
    {
        if (payload.ValueKind is JsonValueKind.String) return null;
        if (payload.ValueKind is not JsonValueKind.Object) return null;

        var id = payload.TryGetProperty("id", out var idElement) ? idElement.GetString() : null;
        var tool = payload.TryGetProperty("tool", out var toolElement) ? toolElement.GetString() : null;
        if (id is null || tool is null) return null;

        var outcome = payload.TryGetProperty("outcome", out var outcomeElement)
            ? outcomeElement.GetString() : null;
        var diagnostic = payload.TryGetProperty("diagnostic", out var diagElement)
            && diagElement.ValueKind is JsonValueKind.String ? diagElement.GetString() : null;
        var content = payload.TryGetProperty("content", out var contentElement)
            && contentElement.ValueKind is JsonValueKind.String ? contentElement.GetString() : null;

        // 参数是"开始"记录才有的字段；"完成"记录没有，这里返回 null 即可。
        var arguments = payload.TryGetProperty("arguments", out var argsElement)
            && argsElement.ValueKind is JsonValueKind.String ? argsElement.GetString() : null;

        // 旧记录没有这个字段：按 true（有副作用）保守处理——
        // 宁可要求人工核实，也不要让宿主误以为可以安全重试。
        var mayHaveEffects = !payload.TryGetProperty("mayHaveIrreversibleEffects", out var effectsElement)
            || effectsElement.ValueKind is not JsonValueKind.False;

        return new ToolRecord(id, tool, outcome, diagnostic, content, arguments, mayHaveEffects);
    }

    private sealed record ToolRecord(
        string Id, string Tool, string? Outcome, string? Diagnostic, string? Content,
        string? ArgumentsJson, bool MayHaveIrreversibleEffects);

    /// <summary>未知结果的说明文本。区分"可能已产生副作用"与"可安全重试"。</summary>
    private static string DescribeUnknown(
        (string Id, string Tool, string? ArgumentsJson, bool MayHaveEffects) started)
    {
        var basis = started.ArgumentsJson is null
            ? "（该会话记录不含调用参数）"
            : "参数见 ArgumentsJson。";

        return started.MayHaveEffects
            ? "已提交调用开始但没有终态；该工具可能产生不可撤销副作用，"
              + "必须人工核实外部状态，不得自动重放。" + basis
            : "已提交调用开始但没有终态；该工具声明为无不可撤销副作用，可安全重试。" + basis;
    }
}

/// <summary>
/// 恢复结果。<see cref="Unknown"/> 非空时宿主必须暂停该 turn 并要求对账，
/// 不能用"没有结果"推断"没有执行"。
/// </summary>
public sealed record RecoveredSession(
    long LastSequence,
    IReadOnlyList<AgentMessage> History,
    IReadOnlyList<UnknownOutcome> Unknown,
    bool HasSequenceGap)
{
    /// <summary>是否需要人工对账才能继续。为 true 时不得自动续跑。</summary>
    /// <summary>是否存在需要人工核实的未知结果（可能有不可撤销副作用）。</summary>
    public bool RequiresReconciliation => Unknown.Any(u => u.MayHaveIrreversibleEffects);

    /// <summary>
    /// 可安全重试的未知结果：工具声明为无不可撤销副作用。
    /// </summary>
    /// <remarks>
    /// 与 <see cref="Unknown"/> 的区别在于**能否自动重试**。
    /// 一次只读工具崩溃不该让整个会话无法恢复。
    /// </remarks>
    public IReadOnlyList<UnknownOutcome> Retryable => [.. Unknown.Where(u => !u.MayHaveIrreversibleEffects)];

    /// <summary>可以直接用于 <see cref="AgentRunRequest.History"/>。</summary>
    public IReadOnlyList<AgentMessage> HistoryForResume => History;

    /// <summary>阻塞续跑的原因；可继续时返回 null。</summary>
    /// <summary>
    /// 恢复时是否需要人工介入。返回 null 表示可以继续。
    /// </summary>
    /// <remarks>
    /// **区分两种未知**：可能有不可撤销副作用的，必须人工核实；
    /// 声明为无副作用的，只是"没跑完"，可以安全重试。
    /// 把两者都拒掉会让只读工具的一次崩溃变成"会话无法恢复"。
    /// </remarks>
    public string? DescribeBlockingReason()
    {
        if (RequiresReconciliation)
        {
            var blocking = Unknown
                .Where(u => u.MayHaveIrreversibleEffects)
                .Select(u => $"{u.InvocationId}({u.ToolId})")
                .ToList();

            if (blocking.Count > 0)
            {
                return $"存在结果未知且可能已产生副作用的工具调用：{string.Join(", ", blocking)}。"
                    + "必须先对账，不能自动重放。";
            }

            // 剩下的都是声明为无副作用的：不阻塞恢复。
            // 调用方仍可通过 Unknown 列表看到它们，自行决定是否重试。
        }

        if (HasSequenceGap)
            return "会话日志序号存在缺口，日志可能被破坏；拒绝自动续跑。";

        return null;
    }
}
