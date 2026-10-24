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
    /// <summary>单个问题最多几个选项。</summary>
    public const int MaxOptions = 4;

    /// <summary>一次最多问几个问题。</summary>
    public const int MaxQuestions = 4;

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

        // 形状校验在本地完成：不合法的问题根本不该打扰用户。
        var validation = Validate(invocation.Arguments, out var asked);
        if (validation is not null) return Result(new AskResult(AskOutcome.InvalidRequest, validation, []));

        var parameters = new JsonObject
        {
            ["sessionId"] = _sessionId,
            ["toolCallId"] = invocation.InvocationId,
            ["mode"] = "form",
            ["message"] = asked[0].Question,
            ["requestedSchema"] = BuildSchema(asked),
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
            return Result(new AskResult(
                AskOutcome.Cancelled, "本轮被取消，用户未看到问题。", []));
        }
        catch (Exception ex)
        {
            // 传输失败必须与"用户拒绝"分开——这是 Cursor 踩过的坑。
            return Result(new AskResult(
                AskOutcome.Unavailable, $"提问不可用：{ex.GetType().Name}: {ex.Message}", []));
        }

        if (response is null)
            return Result(new AskResult(AskOutcome.Unavailable, "客户端没有返回答复。", []));

        // 错误响应（客户端不支持 elicitation）也归为不可用，不是用户拒绝。
        if (response["error"] is { } error)
        {
            var message = error["message"]?.GetValue<string>() ?? error.ToJsonString();
            return Result(new AskResult(
                AskOutcome.Unavailable, $"客户端拒绝 elicitation 请求：{message}", []));
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
    private static AskResult Interpret(JsonNode response, IReadOnlyList<AskedQuestion> asked)
    {
        var action = response["action"]?.GetValue<string>();

        switch (action)
        {
            case "accept":
            {
                var answers = new List<Dougong.Acp.AskAnswer>();
                if (response["content"] is JsonObject content)
                {
                    foreach (var question in asked)
                    {
                        // content 的键用 header：多个问题时它是区分答案的唯一稳定标识。
                        var value = content[question.Header];
                        if (value is null) continue;
                        answers.Add(new Dougong.Acp.AskAnswer(
                            question.Header, DescribeValue(value)));
                    }
                }

                return new AskResult(
                    answers.Count > 0 ? AskOutcome.Answered : AskOutcome.Declined,
                    answers.Count > 0 ? null : "用户提交了空答复。",
                    answers);
            }

            case "decline":
                return new AskResult(AskOutcome.Declined, "用户明确拒绝了回答。", []);

            case "cancel":
                return new AskResult(AskOutcome.Cancelled, "用户关闭了提问对话框。", []);

            default:
                return new AskResult(
                    AskOutcome.Unavailable, $"未知的 elicitation 答复 action '{action}'。", []);
        }
    }

    /// <summary>把应答值折叠成文本；数组表示多选。</summary>
    private static string DescribeValue(JsonNode value) => value switch
    {
        JsonArray array => string.Join(", ", array.Select(v => v?.ToString() ?? string.Empty)),
        JsonValue single => single.ToString(),
        _ => value.ToJsonString(),
    };

    /// <summary>构造 form schema：扁平对象，属性名用 header。</summary>
    private static JsonObject BuildSchema(IReadOnlyList<AskedQuestion> asked)
    {
        var properties = new JsonObject();
        var required = new JsonArray();

        foreach (var question in asked)
        {
            var labels = new JsonObject();
            foreach (var option in question.Options)
            {
                labels[option.Label] = option.Description;
            }

            // 单一 schema 同时表达"可选项"与"允许多选"：
            // 单选是 enum，多选是 items.enum 的数组。
            properties[question.Header] = question.MultiSelect
                ? new JsonObject
                {
                    ["type"] = "array",
                    ["title"] = question.Question,
                    ["items"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["enum"] = new JsonArray([.. question.Options.Select(o => (JsonNode)o.Label)]),
                    },
                    ["minItems"] = 1,
                }
                : new JsonObject
                {
                    ["type"] = "string",
                    ["title"] = question.Question,
                    ["enum"] = new JsonArray([.. question.Options.Select(o => (JsonNode)o.Label)]),
                };

            // 选项说明放在 schema 的 description 里，客户端可展示为选项副标题。
            properties[question.Header]!["description"] = string.Join("；",
                question.Options.Select(o => $"{o.Label}：{o.Description}"));

            required.Add(question.Header);
        }

        return new JsonObject
        {
            ["type"] = "object",
            ["properties"] = properties,
            ["required"] = required,
        };
    }

    /// <summary>
    /// 本地校验问题形状。不通过就返回错误结果，不打扰用户。
    /// </summary>
    private static string? Validate(
        JsonElement arguments, out List<AskedQuestion> asked)
    {
        asked = [];

        if (!arguments.TryGetProperty("questions", out var questions)
            || questions.ValueKind is not JsonValueKind.Array)
            return "缺少 questions 参数。";

        if (questions.GetArrayLength() is 0 or > MaxQuestions)
            return $"questions 必须是 1-{MaxQuestions} 个。";

        var questionsSeen = new HashSet<string>(StringComparer.Ordinal);
        var index = 0;

        foreach (var item in questions.EnumerateArray())
        {
            var question = item.TryGetProperty("question", out var q) ? q.GetString() : null;
            var header = item.TryGetProperty("header", out var h) ? h.GetString() : null;

            if (string.IsNullOrWhiteSpace(question))
                return $"第 {index + 1} 个问题缺少 question。";
            if (string.IsNullOrWhiteSpace(header))
                return $"第 {index + 1} 个问题缺少 header。";
            if (!questionsSeen.Add(question!))
                return $"问题重复：'{question}'。";

            if (!item.TryGetProperty("options", out var options)
                || options.ValueKind is not JsonValueKind.Array)
                return $"问题 '{header}' 缺少 options。";

            var optionCount = options.GetArrayLength();
            if (optionCount is < 2 or > MaxOptions)
            {
                // 与同类实现一致：不要让模型编一个凑数的第二选项。
                return $"问题 '{header}' 需要 2-{MaxOptions} 个选项，实际 {optionCount} 个。"
                    + "如果只有一条合理路径，直接说明你的做法并继续，不要编造凑数选项。";
            }

            var labels = new List<string>();
            var parsed = new List<AskOption>();
            foreach (var option in options.EnumerateArray())
            {
                var label = option.TryGetProperty("label", out var l) ? l.GetString() : null;
                var description = option.TryGetProperty("description", out var d)
                    ? d.GetString() : null;

                if (string.IsNullOrWhiteSpace(label))
                    return $"问题 '{header}' 的选项缺少 label。";
                if (string.IsNullOrWhiteSpace(description))
                    return $"问题 '{header}' 的选项 '{label}' 缺少 description。";
                if (labels.Contains(label!, StringComparer.Ordinal))
                    return $"问题 '{header}' 的选项标签重复：'{label}'。";

                labels.Add(label!);
                parsed.Add(new AskOption(label!, description!));
            }

            var multiSelect = item.TryGetProperty("multiSelect", out var m)
                && m.ValueKind is JsonValueKind.True;

            asked.Add(new AskedQuestion(question!, header!, parsed, multiSelect));
            index++;
        }

        return null;
    }

    private static AgentToolResult Result(AskResult result)
    {
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            outcome = result.Outcome.ToString(),
            diagnostic = result.Diagnostic,
            answers = result.Answers.Select(a => new { question = a.Question, answer = a.Answer }),
        }));

        return new AgentToolResult(document.RootElement.Clone());
    }

    private sealed record AskedQuestion(
        string Question, string Header, IReadOnlyList<AskOption> Options, bool MultiSelect);

    private sealed record AskOption(string Label, string Description);

}

/// <summary>
/// 提问的结果类别。
/// </summary>
/// <remarks>
/// 四种情形必须分开：把"传输失败"或"本轮取消"当成"用户拒绝"会误导模型
/// （它可能因此改变方案，实际用户根本没看到问题）。
/// </remarks>
public enum AskOutcome
{
    /// <summary>用户提交了答案。</summary>
    Answered,

    /// <summary>用户明确拒绝回答。</summary>
    Declined,

    /// <summary>用户关闭了对话框，或本轮被取消。</summary>
    Cancelled,

    /// <summary>提问不可用（无客户端、传输失败、客户端不支持）。</summary>
    Unavailable,

    /// <summary>问题形状不合法，未打扰用户。</summary>
    InvalidRequest,
}

/// <summary>一次提问的结果。</summary>
public sealed record AskResult(
    AskOutcome Outcome, string? Diagnostic, IReadOnlyList<AskAnswer> Answers)
{
    /// <summary>答案的键值视图，供宿主直接展示。</summary>
    public IReadOnlyDictionary<string, string> AsDictionary() =>
        Answers.ToDictionary(a => a.Question, a => a.Answer, StringComparer.Ordinal);
}

/// <summary>一条答复：哪个问题、答案是什么。</summary>
public sealed record AskAnswer(string Question, string Answer);
