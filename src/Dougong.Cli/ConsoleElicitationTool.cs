using System.Text;
using System.Text.Json;
using Dougong.Agent;

using Dougong.Cli.Terminal;

namespace Dougong.Cli;

/// <summary>
/// 向用户提问的工具（REPL 版）：在终端里逐个展示选项，读一行输入。
/// </summary>
/// <remarks>
/// <b>与 <c>AcpElicitationTool</c> 共用全部语义层</b>：问题形状校验、
/// 结果四态、schema 构造都在 <see cref="AskUser"/> 里。这里只做传输——
/// 把问题打在终端上、读一个数字回来。
///
/// <para>
/// <b>为什么提问不该经过审批</b>：它没有副作用。为它加一道审批会让用户
/// 被问两次（"允许提问吗" → 问题本身）。
/// </para>
///
/// <para>
/// <b>输入解析要宽松但明确</b>：接受序号、也接受选项原文（用户复制粘贴时
/// 更自然）。解析不了就重问一次，**不静默当成拒绝**——那是 Cursor 的教训：
/// 把无法解析的输入当"用户跳过"会让模型误判方向。
/// </para>
/// </remarks>
public sealed class ConsoleElicitationTool : IAgentTool
{
    private readonly Func<string?> _readLine;

    /// <summary>构造，默认从标准输入读。</summary>
    public ConsoleElicitationTool() : this(null)
    {
    }

    /// <summary>构造，可注入读取器（供检查使用）。</summary>
    /// <remarks>
    /// <b>注入点只为确定性验证</b>：真实 REPL 用 <c>Console.ReadLine</c>，
    /// 而检查无法驱动真实键盘。
    /// </remarks>
    public ConsoleElicitationTool(Func<string?>? readLine) =>
        _readLine = readLine ?? Console.ReadLine!;

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
                            "label": { "type": "string", "description": "选项本身，1-5 个词。" },
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

    /// <summary>提问不触达文件、网络或进程——报 <c>None</c> 且已知，不进审批。</summary>
    public ToolPermission DescribePermission(JsonElement arguments) =>
        new(ToolAccess.None, IsKnown: true, Paths: [], Commands: [], Domains: []);

    /// <summary>工作区独占：提问期间本就该停下来等用户，与其它工具并行没有意义。</summary>
    public ToolExecutionPlan DescribeExecution(JsonElement arguments) =>
        new(ToolConcurrency.WorkspaceExclusive);

    public IReadOnlyList<string> DescribeFileDependencies(JsonElement arguments) => [];

    public ValueTask<AgentToolResult> ExecuteAsync(
        ToolInvocation invocation, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(invocation);

        // 形状校验复用 Agent 层：不合法的问题根本不该打扰用户。
        var validation = AskUser.Validate(invocation.Arguments, out var questions);
        if (validation is not null)
            return ValueTask.FromResult(Pack(AskUser.Invalid(validation)));

        var answers = new List<AskAnswer>(questions.Count);

        foreach (var question in questions)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                // **取消与拒绝必须分开**：用户按了 Ctrl+C 不等于拒绝了这个选择。
                return ValueTask.FromResult(Pack(new AskResult(
                    AskOutcome.Cancelled,
                    "本轮被取消，用户未答完问题。", answers)));
            }

            var answer = AskOne(question);
            if (answer is null)
            {
                // 用户输入了 EOF（Ctrl+D / 管道结束）：明确报告不可用，
                // 不假装他拒绝——他可能根本没看到问题。
                //
                // **已答的要留在结果里**：中断发生在第几个问题要说清楚。
                // 只报"输入已结束"会让模型以为一个答案都没拿到，从而全部重问——
                // 而用户刚才已经答过的那部分白费了。
                var partial = answers.Count > 0
                    ? $"（已答 {answers.Count} 个：{string.Join("、", answers.Select(a => a.Question))}）"
                    : string.Empty;

                return ValueTask.FromResult(Pack(new AskResult(
                    AskOutcome.Unavailable,
                    $"输入已结束，无法继续提问。{partial}",
                    answers)));
            }

            if (answer.Declined)
            {
                return ValueTask.FromResult(Pack(new AskResult(
                    AskOutcome.Declined, "用户选择了跳过。", answers)));
            }

            answers.Add(new AskAnswer(question.Header, answer.Text));
        }

        return ValueTask.FromResult(Pack(new AskResult(AskOutcome.Answered, null, answers)));
    }

    /// <summary>展示一个问题并读回答；返回 null 表示输入结束。</summary>
    private ParsedAnswer? AskOne(AskedQuestion question)
    {
        Output.Line();
        Output.Line($"── {question.Header} ──────────────────────────────");
        Output.Line($"  {question.Question}");
        Output.Line();

        for (var i = 0; i < question.Options.Count; i++)
        {
            var option = question.Options[i];
            Output.Line($"  {i + 1}) {option.Label}");
            Output.Line($"     {option.Description}");
        }

        Output.Line();
        Output.Line("  输入序号或选项文字；输入 s 跳过。");

        // **给两次机会**：输错一次就放弃不符合"人在终端里"的实际体验，
        // 而无限重问会让管道输入卡死。
        for (var attempt = 0; attempt < 2; attempt++)
        {
            Output.Text("  > ");
            var line = _readLine();
            if (line is null) return null;

            var parsed = ParseAnswer(line, question);
            if (parsed is not null) return parsed;

            Output.Line($"  （无法识别 '{line.Trim()}'，再试一次）");
        }

        // 两次都失败：返回 null 让调用方按"不可用"报告。
        // **不猜他想选什么**，也不当成拒绝——那是两件不同的事。
        Output.Line("  两次未能识别，本次提问作废。");
        return null;
    }

    /// <summary>解析一行输入。</summary>
    /// <remarks>
    /// 接受三种形态：序号、选项原文、跳过标记。
    /// <b>原文匹配先精确后包含</b>：用户常直接粘贴选项，含少量空格差异。
    /// </remarks>
    internal static ParsedAnswer? ParseAnswer(string line, AskedQuestion question)
    {
        var text = line.Trim();
        if (text.Length == 0) return null;

        if (text is "s" or "S" or "skip") return new ParsedAnswer(string.Empty, Declined: true);

        // 序号。
        if (int.TryParse(text, out var index)
            && index >= 1 && index <= question.Options.Count)
        {
            return new ParsedAnswer(question.Options[index - 1].Label, Declined: false);
        }

        // 多选：逗号/空格分隔的序号。
        if (question.MultiSelect)
        {
            var parts = text.Split([',', '、', ' '], StringSplitOptions.RemoveEmptyEntries);
            var picked = new List<string>();

            foreach (var part in parts)
            {
                if (!int.TryParse(part, out var i) || i < 1 || i > question.Options.Count)
                {
                    picked.Clear();
                    break;
                }

                picked.Add(question.Options[i - 1].Label);
            }

            if (picked.Count > 0) return new ParsedAnswer(string.Join(", ", picked), Declined: false);
        }

        // 选项原文：精确优先。
        var exact = question.Options.FirstOrDefault(o =>
            string.Equals(o.Label, text, StringComparison.OrdinalIgnoreCase));
        if (exact is not null) return new ParsedAnswer(exact.Label, Declined: false);

        // 包含匹配只在唯一时采用——多处匹配时猜哪个都是错的。
        var partial = question.Options
            .Where(o => o.Label.Contains(text, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (partial.Count == 1) return new ParsedAnswer(partial[0].Label, Declined: false);

        return null;
    }

    /// <summary>一条解析后的答复。</summary>
    /// <param name="Text">答案文本；跳过时为空串。</param>
    /// <param name="Declined">用户选择了跳过。</param>
    internal sealed record ParsedAnswer(string Text, bool Declined);

    private static AgentToolResult Pack(AskResult result)
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
