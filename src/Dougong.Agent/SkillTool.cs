using System.Text;
using System.Text.Json;

namespace Dougong.Agent;

/// <summary>
/// 按名字加载一个技能的完整说明。
/// </summary>
/// <remarks>
/// <b>为什么是"按需加载"而不是把全部技能写进系统提示</b>：技能正文可以很长
/// （发布流程、迁移写法、排查步骤），而每轮真正需要的通常只有零个。
/// 全塞进提示等于每轮都付这笔预算。
///
/// <para>
/// <b>照 dsh 的 <c>dsh-tool-skill</c>，形状一致：一个必填参数 <c>name</c></b>。
/// 没有"列出全部"的入参——目录已经在对话里给过一次（见
/// <see cref="SkillCatalogText"/>），让模型再列一次只会多一轮往返。
/// </para>
///
/// <para>
/// <b>加载时重新校验可见性</b>：目录里过滤掉了 <c>modelInvocable: false</c> 的技能，
/// 但模型仍可能凭记忆拼出那个名字。只在目录里过滤不够——
/// 加载路径必须再查一遍，否则那个开关只是"看不见但能用"。
/// </para>
/// </remarks>
public sealed class SkillTool : IAgentTool
{
    /// <summary>工具 ID。</summary>
    public const string ToolId = "skill";

    private readonly SkillDiscovery _discovery;

    /// <summary>构造。</summary>
    /// <param name="discovery">技能发现来源。</param>
    public SkillTool(SkillDiscovery discovery)
    {
        ArgumentNullException.ThrowIfNull(discovery);
        _discovery = discovery;
    }

    public ToolDescriptor Descriptor { get; } = new(
        ToolId, ToolId, "1.0.0",
        "按名字加载一个技能的完整说明。名字取自对话里给出的可用技能列表。"
        + "技能是本地 Markdown 文件，加载后按其中的步骤执行。",
        JsonDocument.Parse(
            """
            {
              "type": "object",
              "properties": {
                "name": {
                  "type": "string",
                  "description": "技能名（kebab-case），必须是可用技能列表里的名字。"
                }
              },
              "required": ["name"],
              "additionalProperties": false
            }
            """).RootElement.Clone());

    /// <summary>只读本地文件，但报路径以便审批与诊断看清范围。</summary>
    public ToolPermission DescribePermission(JsonElement arguments)
    {
        var name = arguments.TryGetProperty("name", out var element)
            && element.ValueKind is JsonValueKind.String
                ? element.GetString()
                : null;

        // **路径报不出来时置 IsKnown=false**：此时下游无法判断会读什么，
        // 报一个空路径数组会被当成"不读任何文件"。
        if (string.IsNullOrWhiteSpace(name) || !SkillDiscovery.NamePattern.IsMatch(name))
            return new ToolPermission(ToolAccess.Read, IsKnown: false, Paths: [], Commands: [], Domains: []);

        // 只标出技能根，不猜具体文件：同名技能的胜者取决于发现时的顺序，
        // 在这里再推一遍会与真实加载走两条判断。
        var paths = _discovery.Discover().Skills
            .Where(s => string.Equals(s.Name, name, StringComparison.Ordinal))
            .Select(s => s.Path)
            .ToArray();

        return new ToolPermission(
            ToolAccess.Read, IsKnown: true, Paths: paths, Commands: [], Domains: []);
    }

    /// <summary>
    /// 读技能文件：按被读的文件声明读键，于是与别的读取可以并行，
    /// 而与同一个技能文件的写冲突。
    /// </summary>
    public ToolExecutionPlan DescribeExecution(JsonElement arguments)
    {
        var name = arguments.TryGetProperty("name", out var element)
            && element.ValueKind is JsonValueKind.String
                ? element.GetString()
                : null;

        if (string.IsNullOrWhiteSpace(name) || !SkillDiscovery.NamePattern.IsMatch(name))
            return ToolExecutionPlan.Serial;

        // 键用绝对路径：守按 Ordinal 比较，写法不统一就等于没有互斥。
        // **键带 "file:" 前缀，与文件工具同一格式**。ToolResourceGate 按 Ordinal
        // 比较字符串，框架不猜两种写法是否指同一个文件。
        //
        // 旧的写法是裸绝对路径：与文件工具的 "file:<abs>" 是两个不同的键，
        // 于是"读技能文件"与"写同一个技能文件"不会互斥——那条修复就是这个。
        var reads = _discovery.Discover().Skills
            .Where(s => string.Equals(s.Name, name, StringComparison.Ordinal))
            .Select(s => "file:" + Path.GetFullPath(s.Path))
            .ToArray();

        return new ToolExecutionPlan(ToolConcurrency.Serial) { Reads = reads };
    }

    public async ValueTask<AgentToolResult> ExecuteAsync(
        ToolInvocation invocation, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(invocation);

        var name = invocation.Arguments.TryGetProperty("name", out var element)
            && element.ValueKind is JsonValueKind.String
                ? element.GetString()?.Trim()
                : null;

        if (string.IsNullOrWhiteSpace(name))
            return Error("缺少 name 参数。");

        if (!SkillDiscovery.NamePattern.IsMatch(name))
            return Error($"技能名必须是 kebab-case（小写字母、数字与连字符）：'{name}'");

        var definition = await _discovery.LoadAsync(name, cancellationToken)
            .ConfigureAwait(false);

        // **找不到与不可见共用一条消息，但要说清"可能不可见"**：
        // 若分开报，等于告诉模型"这个技能存在，只是你没权限"——
        // 那是一个本不该暴露的事实。
        if (definition is null)
            return Error(
                $"没有可用的技能 '{name}'。它可能不存在、名字写错，或对模型不可调用。"
                + "可用技能见对话里的列表。");

        if (!definition.Summary.ModelInvocable)
            return Error(
                $"技能 '{name}' 不允许模型调用。");

        return Success(definition);
    }

    private static AgentToolResult Success(SkillDefinition definition)
    {
        // 正文原样返回：它是 Markdown，模型读 Markdown 比读 JSON 字符串自然。
        // 两侧的标签让模型知道"这段是技能内容，不是用户说的话"——
        // 与 dsh 的 `<skill_content>` / `<skill_instructions>` 同一意图。
        var sb = new StringBuilder();
        sb.Append("<skill_content name=\"").Append(definition.Summary.Name).Append("\">\n");

        if (!string.IsNullOrWhiteSpace(definition.Summary.Description))
            sb.Append(definition.Summary.Description).Append('\n').Append('\n');

        sb.Append(definition.Content.TrimEnd()).Append('\n');
        sb.Append("</skill_content>");

        using var document = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            ok = true,
            error = (string?)null,
            name = definition.Summary.Name,
            source = definition.Summary.Source,
            content = sb.ToString(),
        }));

        return new AgentToolResult(document.RootElement.Clone());
    }

    private static AgentToolResult Error(string message)
    {
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            ok = false,
            error = message,
            name = (string?)null,
            source = (string?)null,
            content = (string?)null,
        }));

        return new AgentToolResult(document.RootElement.Clone());
    }
}
