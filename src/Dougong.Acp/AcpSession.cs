using System.Text.Json;
using System.Text.Json.Nodes;
using Dougong.Agent;

namespace Dougong.Acp;

/// <summary>
/// 创建一个 ACP 会话所需的上下文。宿主据此构造运行时与 dispatcher。
/// </summary>
/// <remarks>
/// 用工厂而不是让 Acp 层直接 new：按分层，<c>Dougong.Acp</c> 只能引用
/// <c>Dougong.Agent</c>（L1），拿不到 <c>Dougong.Ai</c> 的连接器工厂。
/// 因此模型连接由宿主注入，ACP 层只负责协议。
/// </remarks>
/// <param name="RestoredHistory">
/// 从已提交日志恢复出的历史。新建会话时为空；<c>session/load</c> 与
/// <c>session/resume</c> 会带上它，宿主据此重建上下文。
/// </param>
/// <param name="StorePath">
/// 该会话的 JSONL 路径（若已知）。恢复时必须复用原路径，否则会写到一个新文件、
/// 表现为"恢复了但历史没接上"。
/// </param>
public sealed record AcpSessionContext(
    string SessionId,
    string Workspace,
    IReadOnlyList<AgentMessage>? RestoredHistory = null,
    string? StorePath = null);

/// <summary>
/// 一个会话的运行时与工具入口。
/// </summary>
/// <remarks>
/// 两者必须由**同一次**工厂调用产出：dispatcher 里挂着审批器与资源闸门，
/// 若 runtime 与 dispatcher 分别构造，工具执行会绕过审批。
/// </remarks>
public sealed record AcpSessionRuntime(IAgentRuntime Runtime, IToolDispatcher Dispatcher);

/// <summary>
/// 一个 ACP 会话：把 <see cref="AgentEvent"/> 翻译为 <c>session/update</c>，
/// 并把审批转成 <c>session/request_permission</c>。
/// </summary>
internal sealed class AcpSession
{
    private readonly CancellationTokenSource _cancel = new();
    private readonly List<AgentMessage> _history;
    private int _running;

    /// <param name="restoredHistory">
    /// 恢复出的历史。非空表示这是一次恢复，后续轮次必须接着它继续。
    /// </param>
    public AcpSession(
        string sessionId,
        string workspace,
        IAgentRuntime runtime,
        AcpAgentHost host,
        TextWriter log,
        IReadOnlyList<AgentMessage>? restoredHistory = null)
    {
        SessionId = sessionId;
        Workspace = workspace;
        _runtime = runtime;
        _host = host;
        _log = log;
        _history = restoredHistory is null ? [] : [.. restoredHistory];
    }

    private readonly IAgentRuntime _runtime;
    private readonly AcpAgentHost _host;
    private readonly TextWriter _log;

    public string SessionId { get; }
    public string Workspace { get; }

    /// <summary>把已提交历史作为 <c>session/update</c> 重放给客户端。</summary>
    /// <remarks>
    /// <c>session/load</c> 要求 Agent 先重放**整段**对话再响应。
    /// 只回 {sessionId} 而不重放，客户端会显示空会话——看起来"恢复成功"，
    /// 实际历史全丢。
    /// </remarks>
    public async Task ReplayHistoryAsync(CancellationToken cancellationToken)
    {
        foreach (var message in _history)
        {
            switch (message.Role)
            {
                case AgentRole.User:
                    await NotifyAsync("user_message_chunk", new JsonObject
                    {
                        ["content"] = new JsonObject { ["type"] = "text", ["text"] = message.Content },
                    }, cancellationToken).ConfigureAwait(false);
                    break;

                case AgentRole.Assistant:
                    await NotifyAsync("agent_message_chunk", new JsonObject
                    {
                        ["content"] = new JsonObject { ["type"] = "text", ["text"] = message.Content },
                    }, cancellationToken).ConfigureAwait(false);
                    break;

                case AgentRole.Tool:
                    await NotifyAsync("tool_call_update", new JsonObject
                    {
                        ["toolCallId"] = message.ToolCallId ?? "unknown",
                        ["status"] = "completed",
                        ["content"] = new JsonArray(new JsonObject
                        {
                            ["type"] = "content",
                            ["content"] = new JsonObject
                            {
                                ["type"] = "text",
                                ["text"] = message.Content,
                            },
                        }),
                    }, cancellationToken).ConfigureAwait(false);
                    break;
            }
        }
    }

    /// <summary>当前已承载的历史条数，供诊断与检查项使用。</summary>
    public int HistoryCount => _history.Count;

    /// <summary>取消当前轮次。并发调用安全。</summary>
    public void Cancel() => _cancel.Cancel();

    /// <summary>
    /// 跑一轮对话，把所有事件翻译成通知，最后返回 stopReason。
    /// </summary>
    /// <remarks>
    /// 通知必须全部发出后调用方才发响应——否则客户端会丢最后几条增量。
    /// 这里用 <c>await</c> 顺序发送，不并行。
    /// </remarks>
    public async Task<string> RunPromptAsync(string prompt, CancellationToken cancellationToken)
    {
        // 每会话首版只允许一个运行轮次。
        if (Interlocked.Exchange(ref _running, 1) != 0)
            return "refusal";

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, _cancel.Token);

        var request = new AgentRunRequest(
            SessionId, "turn-" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            [.. _history], prompt);

        var assistantText = new System.Text.StringBuilder();

        try
        {
            await foreach (var evt in _runtime.RunAsync(request, linked.Token).ConfigureAwait(false))
            {
                switch (evt)
                {
                    case AgentEvent.TextDelta delta:
                        assistantText.Append(delta.Text);
                        await NotifyAsync("agent_message_chunk", new JsonObject
                        {
                            ["content"] = new JsonObject { ["type"] = "text", ["text"] = delta.Text },
                        }, linked.Token).ConfigureAwait(false);
                        break;

                    case AgentEvent.ToolCallRequested requested:
                        await NotifyAsync("tool_call", new JsonObject
                        {
                            ["toolCallId"] = requested.InvocationId,
                            ["title"] = requested.ToolId,
                            ["name"] = requested.ToolId,
                            ["status"] = "pending",
                            ["rawInput"] = JsonNode.Parse(requested.Arguments.GetRawText()),
                        }, linked.Token).ConfigureAwait(false);
                        break;

                    case AgentEvent.ToolCallCompleted completed:
                        await NotifyAsync("tool_call_update", new JsonObject
                        {
                            ["toolCallId"] = completed.Result.InvocationId,
                            ["status"] = completed.Result.Outcome switch
                            {
                                ToolCallOutcome.Succeeded => "completed",
                                ToolCallOutcome.Failed => "failed",
                                _ => "failed",
                            },
                            ["content"] = new JsonArray(new JsonObject
                            {
                                ["type"] = "content",
                                ["content"] = new JsonObject
                                {
                                    ["type"] = "text",
                                    ["text"] = completed.Result.Diagnostic
                                        ?? completed.Result.Result?.Content.GetRawText()
                                        ?? completed.Result.Outcome.ToString(),
                                },
                            }),
                        }, linked.Token).ConfigureAwait(false);
                        break;

                    case AgentEvent.ContextCompacted compacted:
                        // 压缩是宿主关心的事，但对客户端也要可见：历史被有损改写了。
                        _log.WriteLine(
                            $"[acp] 会话 {SessionId} 压缩：{compacted.RemovedMessages} 条，"
                            + $"applied={compacted.Applied}");
                        break;

                    case AgentEvent.RunFinished finished:
                        _history.Add(new AgentMessage(AgentRole.User, prompt));
                        if (assistantText.Length > 0)
                            _history.Add(new AgentMessage(AgentRole.Assistant, assistantText.ToString()));

                        return finished.Outcome switch
                        {
                            AgentRunOutcome.Completed => "end_turn",
                            AgentRunOutcome.Cancelled => "cancelled",
                            AgentRunOutcome.ToolRoundLimitReached => "max_tokens",
                            _ => "refusal",
                        };
                }
            }

            return "end_turn";
        }
        catch (OperationCanceledException)
        {
            return "cancelled";
        }
        finally
        {
            Volatile.Write(ref _running, 0);
        }
    }

    private Task NotifyAsync(string sessionUpdate, JsonObject update, CancellationToken cancellationToken)
    {
        update["sessionUpdate"] = sessionUpdate;
        return _host.SendNotificationAsync(
            "session/update",
            new JsonObject
            {
                ["sessionId"] = SessionId,
                ["update"] = update,
            },
            cancellationToken);
    }
}

/// <summary>
/// 把 <see cref="IToolApprover"/> 实现为 ACP 的 <c>session/request_permission</c> 请求。
/// </summary>
/// <remarks>
/// 这是审批流的反向适配：Dougong 主动向客户端发请求，等它选一个 optionId。
/// <para>
/// 三条来自协议实践的硬约束：
/// <list type="bullet">
/// <item><c>options</c> 的每种 kind 最多出现一次，否则客户端会退化为"选项列表"模式</item>
/// <item>回复里的 <c>optionId</c> **必须**是请求里给出的值之一，不能自造</item>
/// <item>超时/无回复一律按拒绝处理——无限等待会让会话挂死</item>
/// </list>
/// </para>
/// </remarks>
/// <remarks>
/// 用 <see cref="Func{TResult}"/> 延迟取 host 而不是直接持有实例：
/// host 在构造时就要调用会话工厂，而工厂里要创建审批器——直接持有会形成循环依赖。
/// </remarks>
public sealed class AcpApprover(
    Func<AcpAgentHost> hostAccessor,
    string sessionId,
    TimeSpan? timeout = null) : IToolApprover
{
    /// <summary>与 dispatcher 的审批有效期一致；超时即视为拒绝。</summary>
    private readonly TimeSpan _timeout = timeout ?? TimeSpan.FromMinutes(10);

    public async ValueTask<ToolApprovalDecision> RequestApprovalAsync(
        ToolApprovalRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var options = new JsonArray
        {
            Option("allow-once", "允许一次", "allow_once"),
            Option("reject-once", "拒绝", "reject_once"),
        };

        var parameters = new JsonObject
        {
            ["sessionId"] = sessionId,
            ["toolCall"] = new JsonObject
            {
                ["toolCallId"] = request.InvocationId,
                ["title"] = $"{request.Tool.Name} {Truncate(request.NormalizedArguments.GetRawText(), 200)}",
                ["name"] = request.Tool.Name,
                ["rawInput"] = JsonNode.Parse(request.NormalizedArguments.GetRawText()),
            },
            ["options"] = options,
        };

        using var timeoutSource = new CancellationTokenSource(_timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, timeoutSource.Token);

        try
        {
            var response = await hostAccessor().RequestAsync(
                "session/request_permission", parameters, linked.Token).ConfigureAwait(false);

            return Interpret(response);
        }
        catch (OperationCanceledException)
        {
            // 没有回复必须按拒绝处理，不能当成批准。
            return ToolApprovalDecision.Deny(
                timeoutSource.IsCancellationRequested
                    ? "客户端在超时前未回复权限请求，按拒绝处理。"
                    : "权限请求被取消，按拒绝处理。");
        }
        catch (Exception ex)
        {
            return ToolApprovalDecision.Deny($"权限请求失败：{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>解析客户端的选择；任何异常形状都按拒绝处理。</summary>
    private static ToolApprovalDecision Interpret(JsonNode? response)
    {
        var outcome = response?["outcome"];
        if (outcome is null) return ToolApprovalDecision.Deny("客户端未返回 outcome。");

        var kind = outcome["outcome"]?.GetValue<string>();
        if (kind == "cancelled")
            return ToolApprovalDecision.Deny("客户端取消了权限请求。");

        if (kind != "selected")
            return ToolApprovalDecision.Deny($"未知的 outcome 取值 '{kind}'。");

        var optionId = outcome["optionId"]?.GetValue<string>();
        return optionId switch
        {
            "allow-once" => ToolApprovalDecision.Allow("客户端批准（一次）"),
            // 越界或自造的 optionId 不能被当成批准。
            null => ToolApprovalDecision.Deny("客户端未返回 optionId。"),
            _ => ToolApprovalDecision.Deny($"客户端选择了非批准的选项 '{optionId}'。"),
        };
    }

    private static JsonObject Option(string id, string name, string kind) => new()
    {
        ["optionId"] = id,
        ["name"] = name,
        ["kind"] = kind,
    };

    private static string Truncate(string text, int max) =>
        text.Length <= max ? text : text[..max] + "…";
}
