using System.Text.Json;

namespace Dougong.Agent;

/// <summary>
/// "向用户提问"的语义层：模型问什么、怎么校验、结果怎么表达。
/// </summary>
/// <remarks>
/// <b>为什么这一层属于 Agent 而不是某个传输</b>：同一个 <c>ask_user</c> 工具
/// 要同时服务 ACP 客户端与本地 REPL。问题形状（1–4 个问题、每问题 2–4 个选项、
/// header 不重复）与结果语义（答了 / 拒了 / 取消了 / 不可用）**与传输无关**——
/// 把它们绑在 ACP 上会让第二个宿主只能重写一遍，而两份校验必然漂移。
///
/// <para>
/// <b>照 dsh 的 <c>tool-ask-user</c> 做，形状一致</b>：Claude Code 的
/// <c>AskUserQuestion</c>、Codex 的 <c>request_user_input</c> 都是
/// "1–4 个问题，每个 2–4 个选项，附说明"。
/// </para>
///
/// <para>
/// <b>四种结果必须分开</b>：把"传输失败"或"本轮取消"当成"用户拒绝"会误导模型——
/// 它可能因此改变方案，而用户根本没看到问题。Cursor 踩过这个坑。
/// </para>
/// </remarks>
public static class AskUser
{
    /// <summary>单个问题最多几个选项。</summary>
    public const int MaxOptions = 4;

    /// <summary>一次最多问几个问题。</summary>
    public const int MaxQuestions = 4;

    /// <summary>
    /// 校验模型给出的问题形状。
    /// </summary>
    /// <param name="arguments">工具调用参数。</param>
    /// <param name="questions">校验通过时的问题列表。</param>
    /// <returns>错误说明；null 表示通过。</returns>
    /// <remarks>
    /// <b>在本地校验，不依赖传输层</b>：不合法的问题根本不该打扰用户。
    /// 让每个宿主自己校验会让"哪些问题算合法"随宿主漂移，
    /// 而模型看到的行为会因客户端不同而不同。
    /// </remarks>
    public static string? Validate(JsonElement arguments, out IReadOnlyList<AskedQuestion> questions)
    {
        var parsed = new List<AskedQuestion>();
        questions = parsed;

        if (arguments.ValueKind is not JsonValueKind.Object
            || !arguments.TryGetProperty("questions", out var raw)
            || raw.ValueKind is not JsonValueKind.Array)
        {
            return "缺少 questions 参数。";
        }

        if (raw.GetArrayLength() is 0 or > MaxQuestions)
            return $"questions 必须是 1-{MaxQuestions} 个。";

        var seenHeaders = new HashSet<string>(StringComparer.Ordinal);
        var index = 0;

        foreach (var item in raw.EnumerateArray())
        {
            index++;

            if (item.ValueKind is not JsonValueKind.Object)
                return $"第 {index} 个问题不是对象。";

            var question = item.TryGetProperty("question", out var q) ? q.GetString() : null;
            var header = item.TryGetProperty("header", out var h) ? h.GetString() : null;

            if (string.IsNullOrWhiteSpace(question))
                return $"第 {index} 个问题缺少 question。";

            if (string.IsNullOrWhiteSpace(header))
                return $"第 {index} 个问题缺少 header。";

            if (header.Length > 12)
                return $"第 {index} 个问题的 header 超过 12 个字符：{header}";

            // **header 是答案的键**：重复会让两个问题的答案互相覆盖，
            // 而模型只会看到"少了一个答案"，无法判断是自己没问还是被覆盖了。
            if (!seenHeaders.Add(header))
                return $"header 重复：{header}（它是答案的键，不能重复）";

            if (!item.TryGetProperty("options", out var optionsRaw)
                || optionsRaw.ValueKind is not JsonValueKind.Array)
            {
                return $"第 {index} 个问题缺少 options。";
            }

            // **诊断必须引导而不是只报错**：模型看到"至少 2 个"时最可能的反应
            // 是编一个凑数选项塞进去，那比拒绝更糟——用户会被迫在两个假选项里选。
            // 所以明确告诉它：凑数不如换一种问法。
            if (optionsRaw.GetArrayLength() is < 2 or > MaxOptions)
            {
                return $"第 {index} 个问题的选项必须是 2-{MaxOptions} 个；"
                    + "请给出真实可选的方案，不要用凑数选项填充。";
            }

            var options = new List<AskOption>();
            var seenLabels = new HashSet<string>(StringComparer.Ordinal);

            foreach (var optionRaw in optionsRaw.EnumerateArray())
            {
                if (optionRaw.ValueKind is not JsonValueKind.Object)
                    return $"第 {index} 个问题里有非对象的选项。";

                var label = optionRaw.TryGetProperty("label", out var l) ? l.GetString() : null;
                var description = optionRaw.TryGetProperty("description", out var d)
                    ? d.GetString() : null;

                if (string.IsNullOrWhiteSpace(label))
                    return $"第 {index} 个问题里有选项缺少 label。";

                if (string.IsNullOrWhiteSpace(description))
                    return $"第 {index} 个问题的选项 '{label}' 缺少 description。";

                // 同一问题里 label 是用户看到并点选的文本，重复会让答案有歧义。
                if (!seenLabels.Add(label))
                    return $"第 {index} 个问题里 label 重复：{label}";

                options.Add(new AskOption(label, description));
            }

            var multiSelect = item.TryGetProperty("multiSelect", out var m)
                && m.ValueKind is JsonValueKind.True;

            parsed.Add(new AskedQuestion(question, header, options, multiSelect));
        }

        return null;
    }

    /// <summary>
    /// 把问题形状表达成 JSON Schema（供宿主渲染表单）。
    /// </summary>
    /// <remarks>
    /// <b>属性名用 header</b>：多个问题时它是区分答案的唯一稳定标识，
    /// 也是宿主回传答案时的键。
    /// <para>
    /// <b>单选与多选共用一份 schema 形状</b>：单选是 <c>enum</c>，
    /// 多选是 <c>items.enum</c> 的数组。这样宿主只需处理一种结构。
    /// </para>
    /// </remarks>
    public static Dictionary<string, object?> BuildSchema(IReadOnlyList<AskedQuestion> questions)
    {
        ArgumentNullException.ThrowIfNull(questions);

        var properties = new Dictionary<string, object?>(StringComparer.Ordinal);
        var required = new List<string>(questions.Count);

        foreach (var question in questions)
        {
            var labels = question.Options.Select(o => o.Label).ToArray();

            properties[question.Header] = question.MultiSelect
                ? new Dictionary<string, object?>
                {
                    ["type"] = "array",
                    ["title"] = question.Question,
                    ["items"] = new Dictionary<string, object?>
                    {
                        ["type"] = "string",
                        ["enum"] = labels,
                    },
                    ["minItems"] = 1,
                    // 选项说明放在 description 里，宿主可展示为副标题。
                    ["description"] = string.Join("；",
                        question.Options.Select(o => $"{o.Label}：{o.Description}")),
                }
                : new Dictionary<string, object?>
                {
                    ["type"] = "string",
                    ["title"] = question.Question,
                    ["enum"] = labels,
                    ["description"] = string.Join("；",
                        question.Options.Select(o => $"{o.Label}：{o.Description}")),
                };

            required.Add(question.Header);
        }

        return new Dictionary<string, object?>
        {
            ["type"] = "object",
            ["properties"] = properties,
            ["required"] = required,
        };
    }

    /// <summary>
    /// 构造一个"问题形状不合法"的结果。
    /// </summary>
    /// <remarks>
    /// 单独一个方法是为了让宿主不必记住 <see cref="AskOutcome.InvalidRequest"/>
    /// 要配什么诊断文本——两处各写一遍必然不一致。
    /// </remarks>
    public static AskResult Invalid(string diagnostic) =>
        new(AskOutcome.InvalidRequest, diagnostic, []);
}

/// <summary>模型提出的一个选择题。</summary>
/// <param name="Question">完整问题，具体明确。</param>
/// <param name="Header">很短的标签，作为答案的键。</param>
/// <param name="Options">2–4 个选项。</param>
/// <param name="MultiSelect">是否允许多选。</param>
public sealed record AskedQuestion(
    string Question,
    string Header,
    IReadOnlyList<AskOption> Options,
    bool MultiSelect)
{
    /// <summary>
    /// 问题的完整附件（Markdown）。没有时为空。
    /// </summary>
    /// <remarks>
    /// <b>与 <see cref="Question"/> 分工</b>：后者是一句话的提问，
    /// 前者是可以滚动阅读的完整文档。计划审阅需要"完整计划"而不是摘要——
    /// 摘要会让用户在没看全的情况下批准。
    /// <para>
    /// dsh 的 <c>userQuestions.ask</c> 有这个字段，且明确写着
    /// "its detail is the only complete document"。
    /// </para>
    /// </remarks>
    public string? Detail { get; init; }

    /// <summary>
    /// 这次提问属于哪一类交互。null 表示普通提问。
    /// </summary>
    /// <remarks>
    /// <b>客户端据此选 UI</b>：普通提问显示成问题卡片，计划审阅显示成
    /// 审阅卡片（带"查看完整计划"与"批准"）。dsh 的 e2e 明确断言
    /// "plan-review 请求**不能**落到通用问题流上"。
    /// </remarks>
    public AskIntent? Intent { get; init; }
}

/// <summary>
/// 提问的交互类别。
/// </summary>
/// <remarks>
/// <b>用数据而不是类型区分</b>：客户端的槽位注册要能按 intent 分发，
/// 而"多态问题类型"会让宿主必须为每种类型实现一个渲染器。
/// dsh 用 <c>intent: { kind: 'plan-review', approve: 'Approve' }</c> 这个形状。
/// </remarks>
public sealed record AskIntent
{
    /// <summary>计划审阅：附件是完整计划，批准后退出计划模式。</summary>
    public static readonly AskIntent PlanReview = new("plan-review");

    private AskIntent(string kind) => Kind = kind;

    /// <summary>类别标识。</summary>
    public string Kind { get; }

    /// <summary>
    /// 哪个选项代表"批准"。null 表示该类别没有批准语义。
    /// </summary>
    /// <remarks>
    /// <b>用选项文本而不是序号</b>：序号在不同渲染顺序下不稳定，
    /// 而选项文本是用户看到并点的那一个。
    /// </remarks>
    public string? ApproveLabel { get; init; }
}

/// <summary>一个可选项。</summary>
/// <param name="Label">选项本身，1–5 个词。</param>
/// <param name="Description">选择它意味着什么，或它的代价。</param>
public sealed record AskOption(string Label, string Description);

/// <summary>一次提问的结果。</summary>
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
/// <param name="Outcome">四种结果之一。</param>
/// <param name="Diagnostic">给模型看的说明；成功时为 null。</param>
/// <param name="Answers">答复列表；只在 <see cref="AskOutcome.Answered"/> 时有内容。</param>
public sealed record AskResult(
    AskOutcome Outcome, string? Diagnostic, IReadOnlyList<AskAnswer> Answers)
{
    /// <summary>答案的键值视图，供宿主直接展示。</summary>
    public IReadOnlyDictionary<string, string> AsDictionary() =>
        Answers.ToDictionary(a => a.Question, a => a.Answer, StringComparer.Ordinal);
}

/// <summary>一条答复：哪个问题、答案是什么。</summary>
public sealed record AskAnswer(string Question, string Answer);
