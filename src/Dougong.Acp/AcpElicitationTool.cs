using System.Text.Json;
using System.Text.Json.Nodes;
using Dougong.Agent;

namespace Dougong.Acp;

/// <summary>
/// 向用户提问的工具：把模型的"我需要澄清"变成一次 ACP <c>elicitation/create</c> 请求。
/// </summary>
/// <remarks>
/// 设计依据来自对同类实现的调研（见 docs/acp-reference-survey.md）：
/// Claude Code 的 <c>AskUserQuestion</c>、Codex 的 <c>request_user_input</c>、
/// TKCode 的 <c>ask_user</c> 都是**工具**而不是宿主特权，共同形状是
/// 「1–4 个问题，每个 2–4 个选项，附说明」。
/// <para>
/// 三条从调研里直接采用的结论：
/// <list type="number">
/// <item><b>不经过审批</b>（<see cref="ToolPermission"/> 报 <c>None</c> 且已知）：
/// 提问本身没有副作用，为它加一道审批只会让用户被问两次。</item>
/// <item><b>取工作区独占</b>：同一批里出现提问时整批串行，避免问答提示与审批提示
/// 同时弹出让用户困惑。TKCode 明确这么选，代价（等待期间占锁）被认为可接受——
/// 因为提问本身就是"这一轮停下来等用户"。</item>
/// <item><b>失败原因必须与"用户拒绝"分开</b>：Cursor 曾把传输失败当成
/// "用户跳过"返回，模型因此误判。<see cref="AskOutcome"/> 把四种情形分开表达。</item>
/// </list>
/// </para>
/// </remarks>
public sealed class AcpElicitationTool : IAgentTool
{
    /// <summary>单个问题最多几个选项。数值由 <see cref="AskUser"/> 定义。</summary>
    /// <remarks>
    /// 保留这个常量是为了不破坏已引用它的下游代码（ApiCompat CP0002）。
    /// 新代码请直接用 <see cref="AskUser.MaxOptions"/>。
    /// </remarks>
    public const int MaxOptions = AskUser.MaxOptions;

    /// <summary>一次最多问几个问题。数值由 <see cref="AskUser"/> 定义。</summary>
    public const int MaxQuestions = AskUser.MaxQuestions;

    private readonly AcpAgentHost _host;
    private readonly string _sessionId;

    public AcpElicitationTool(AcpAgentHost host, string sessionId)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        _host = host;
        _sessionId = sessionId;
    }

    public ToolDescriptor Descriptor { get; } = new(
        "ask_user", "ask_user", "1.0.0",
        "向用户提出 1-4 个结构化选择题并等待回答。"
        + "只在确实需要用户决定时使用；能在选项内说清的不要问。",
        JsonDocument.Parse(
            """
            {
              "type": "object",
              "properties": {
                "questions": {
                  "type": "array",
                  "minItems": 1,
                  "maxItems": 4,
                  "items": {
                    "type": "object",
                    "properties": {
                      "question": {
                        "type": "string",
                        "description": "完整的问题，具体明确，以问号结尾。"
                      },
                      "header": {
                        "type": "string",
                        "description": "很短的标签，显示为问题页签，最多 12 个字符。"
                      },
                      "options": {
                        "type": "array",
                        "minItems": 2,
                        "maxItems": 4,
                        "items": {
                          "type": "object",
                          "properties": {
                            "label": {
                              "type": "string",
                              "description": "选项本身，1-5 个词。"
                            },
                            "description": {
                              "type": "string",
                              "description": "选择它意味着什么，或它的代价。"
                            }
                          },
                          "required": ["label", "description"],
                          "additionalProperties": false
                        }
                      },
                      "multiSelect": {
                        "type": "boolean",
                        "description": "允许多选，默认 false。"
                      }
                    },
                    "required": ["question", "header", "options"],
                    "additionalProperties": false
                  }
                }
              },
              "required": ["questions"],
              "additionalProperties": false
            }
            """).RootElement.Clone());

    /// <summary>
    /// 提问不触达文件、网络或进程，因此报 <c>None</c> 且已知——
    /// 这样它不会被送进审批，用户不会先被问"允许提问吗"再被提问。
    /// </summary>
    public ToolPermission DescribePermission(JsonElement arguments) =>
        new(ToolAccess.None, IsKnown: true, Paths: [], Commands: [], Domains: []);

    /// <summary>工作区独占：避免问答提示与其它工具/审批提示同时出现。</summary>
    public ToolExecutionPlan DescribeExecution(JsonElement arguments) =>
        new(ToolConcurrency.WorkspaceExclusive);

    /// <summary>不依赖任何文件内容。</summary>
    public IReadOnlyList<string> DescribeFileDependencies(JsonElement arguments) => [];

    public async ValueTask<AgentToolResult> ExecuteAsync(
        ToolInvocation invocation, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(invocation);

        // 形状校验复用 Agent 层：**不合法的问题根本不该打扰用户**，
        // 而"什么算合法"不能因宿主不同而不同。
        var validation = AskUser.Validate(invocation.Arguments, out var asked);
        if (validation is not null) return Result(AskUser.Invalid(validation));

        var parameters = new JsonObject
        {
            ["sessionId"] = _sessionId,
            ["toolCallId"] = invocation.InvocationId,
            ["mode"] = "form",
            ["message"] = asked[0].Question,
            ["requestedSchema"] = JsonSerializer.SerializeToNode(
                AskUser.BuildSchema(asked)),
        };

        JsonNode? response;
        try
        {
            response = await _host.RequestAsync(
                "elicitation/create", parameters, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // 取消不是"用户拒绝"：这轮被打断，用户从未看到问题。
            // 混为一谈会让模型以为自己被拒绝了。
            return Result(new Dougong.Agent.AskResult(
                Dougong.Agent.AskOutcome.Cancelled, "本轮被取消，用户未看到问题。", []));
        }
        catch (Exception ex)
        {
            // 传输失败必须与"用户拒绝"分开——这是 Cursor 踩过的坑。
            return Result(new Dougong.Agent.AskResult(
                Dougong.Agent.AskOutcome.Unavailable, $"提问不可用：{ex.GetType().Name}: {ex.Message}", []));
        }

        if (response is null)
            return Result(new Dougong.Agent.AskResult(Dougong.Agent.AskOutcome.Unavailable, "客户端没有返回答复。", []));

        // 错误响应（客户端不支持 elicitation）也归为不可用，不是用户拒绝。
        if (response["error"] is { } error)
        {
            var message = error["message"]?.GetValue<string>() ?? error.ToJsonString();
            return Result(new Dougong.Agent.AskResult(
                Dougong.Agent.AskOutcome.Unavailable, $"客户端拒绝 elicitation 请求：{message}", []));
        }

        return Result(Interpret(response, asked));
    }

    /// <summary>
    /// 解析客户端答复。
    /// </summary>
    /// <remarks>
    /// ACP 的 <c>action</c> 有三个值，含义不同，不能合并：
    /// <c>accept</c> 用户提交了答案；<c>decline</c> 用户明确拒绝；
    /// <c>cancel</c> 用户关闭了对话框。三者对模型的含义完全不同。
    /// </remarks>
    private static Dougong.Agent.AskResult Interpret(JsonNode response, IReadOnlyList<AskedQuestion> asked)
    {
        var action = response["action"]?.GetValue<string>();

        switch (action)
        {
            case "accept":
            {
                var answers = new List<Dougong.Agent.AskAnswer>();
                if (response["content"] is JsonObject content)
                {
                    foreach (var question in asked)
                    {
                        // content 的键用 header：多个问题时它是区分答案的唯一稳定标识。
                        var value = content[question.Header];
                        if (value is null) continue;
                        answers.Add(new Dougong.Agent.AskAnswer(
                            question.Header, DescribeValue(value)));
                    }
                }

                return new Dougong.Agent.AskResult(
                    answers.Count > 0 ? Dougong.Agent.AskOutcome.Answered : Dougong.Agent.AskOutcome.Declined,
                    answers.Count > 0 ? null : "用户提交了空答复。",
                    answers);
            }

            case "decline":
                return new Dougong.Agent.AskResult(Dougong.Agent.AskOutcome.Declined, "用户明确拒绝了回答。", []);

            case "cancel":
                return new Dougong.Agent.AskResult(Dougong.Agent.AskOutcome.Cancelled, "用户关闭了提问对话框。", []);

            default:
                return new Dougong.Agent.AskResult(
                    Dougong.Agent.AskOutcome.Unavailable, $"未知的 elicitation 答复 action '{action}'。", []);
        }
    }

    /// <summary>把应答值折叠成文本；数组表示多选。</summary>
    private static string DescribeValue(JsonNode value) => value switch
    {
        JsonArray array => string.Join(", ", array.Select(v => v?.ToString() ?? string.Empty)),
        JsonValue single => single.ToString(),
        _ => value.ToJsonString(),
    };

    /// <summary>把提问结果包成工具结果，供模型阅读。</summary>
    /// <remarks>
    /// <b>保留 Outcome 原文</b>：模型需要能区分"用户拒绝"与"提问不可用"。
    /// 只给它一句自然语言说明会让它按措辞猜，而措辞是我们写的、不是它熟悉的枚举。
    /// </remarks>
    private static AgentToolResult Result(Dougong.Agent.AskResult result)
    {
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            outcome = result.Outcome.ToString(),
            diagnostic = result.Diagnostic,
            answers = result.Answers.Select(a => new { question = a.Question, answer = a.Answer }),
        }));

        return new AgentToolResult(document.RootElement.Clone());
    }
}
