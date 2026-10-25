using System.Text.Json;

namespace Dougong.Agent;

/// <summary>
/// 让模型显式维护计划的工具。它是 ACP <c>plan</c> 的**唯一**数据源。
/// </summary>
/// <remarks>
/// **为什么是工具**：ACP 的 plan 需要"当前完整计划"。若由运行时从工具调用序列推测，
/// 用户无法分辨哪些是真计划、哪些是推断——这正是 P3「故障可解释」的反面。
/// 让模型显式声明，代价是模型要主动调用，收益是数据有明确来源。
/// <para>
/// **全量替换**：每次调用给出完整列表，不是增量。这与 ACP 语义一致，
/// 也让"撤销一步"变得简单——模型少写一条即可，不需要反向操作。
/// </para>
/// <para>
/// **不校验 Priority/Status 的取值**：ACP 只要求字符串，不同客户端的词表不同。
/// 只校验结构（非空、数量上限），不校验词表——否则遇到没见过的词就丢信息。
/// </para>
/// </remarks>
public sealed class TodoWriteTool : IAgentTool
{
    public ToolDescriptor Descriptor { get; } = new(
        PlanTool.ToolId, PlanTool.ToolId, "1.0.0",
        "声明或更新当前任务计划。整份列表全量替换。"
        + "多步骤任务开始前调用一次，每完成一步更新一次。",
        JsonDocument.Parse(
            $$"""
            {
              "type": "object",
              "properties": {
                "entries": {
                  "type": "array",
                  "maxItems": {{PlanTool.MaxEntries}},
                  "items": {
                    "type": "object",
                    "properties": {
                      "content": {
                        "type": "string",
                        "description": "这一步要做什么，一句话说清。"
                      },
                      "priority": {
                        "type": "string",
                        "description": "优先级，如 high / medium / low。"
                      },
                      "status": {
                        "type": "string",
                        "description": "状态，如 pending / in_progress / completed。"
                      }
                    },
                    "required": ["content", "status"],
                    "additionalProperties": false
                  }
                }
              },
              "required": ["entries"],
              "additionalProperties": false
            }
            """).RootElement.Clone());

    /// <summary>不改任何外部状态，因此报已知 <c>None</c>：不产生审批项。</summary>
    public ToolPermission DescribePermission(JsonElement arguments) =>
        new(ToolAccess.None, IsKnown: true, Paths: [], Commands: [], Domains: []);

    /// <summary>纯内存操作，但用工作区独占保证"最后一次声明"的顺序可预期。</summary>
    public ToolExecutionPlan DescribeExecution(JsonElement arguments) =>
        new(ToolConcurrency.WorkspaceExclusive);

    public IReadOnlyList<string> DescribeFileDependencies(JsonElement arguments) => [];

    public ValueTask<AgentToolResult> ExecuteAsync(
        ToolInvocation invocation, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(invocation);

        var error = Parse(invocation.Arguments, out var entries);
        if (error is not null)
        {
            return ValueTask.FromResult(Result(new
            {
                ok = false,
                error,
                entries = Array.Empty<object>(),
            }));
        }

        return ValueTask.FromResult(Result(new
        {
            ok = true,
            error = (string?)null,
            entries = entries.Select(e => new
            {
                content = e.Content,
                priority = e.Priority,
                status = e.Status,
            }),
        }));
    }

    /// <summary>
    /// 解析并校验条目。
    /// </summary>
    /// <remarks>
    /// 只有一处硬校验：<c>content</c> 非空、<c>status</c> 非空。
    /// 空内容或空状态在 UI 上就是一个空白行，用户看不出发生了什么——
    /// 这种"看起来有但实际没有"的条目比报错更糟。
    /// </remarks>
    private static string? Parse(JsonElement arguments, out IReadOnlyList<PlanEntry> entries)
    {
        entries = [];

        if (!arguments.TryGetProperty("entries", out var array)
            || array.ValueKind is not JsonValueKind.Array)
            return "缺少 entries 参数。";

        if (array.GetArrayLength() > PlanTool.MaxEntries)
            return $"entries 最多 {PlanTool.MaxEntries} 条，实际 {array.GetArrayLength()} 条。";

        var parsed = new List<PlanEntry>(array.GetArrayLength());
        var index = 0;

        foreach (var item in array.EnumerateArray())
        {
            var content = item.TryGetProperty("content", out var c) ? c.GetString() : null;
            var status = item.TryGetProperty("status", out var s) ? s.GetString() : null;
            var priority = item.TryGetProperty("priority", out var p) ? p.GetString() : null;

            if (string.IsNullOrWhiteSpace(content))
                return $"第 {index + 1} 条缺少 content。";
            if (string.IsNullOrWhiteSpace(status))
                return $"第 {index + 1} 条缺少 status。";

            // priority 可省略：省略时给空串，由宿主决定怎么展示。
            parsed.Add(new PlanEntry(content!, priority ?? string.Empty, status!));
            index++;
        }

        entries = parsed;
        return null;
    }

    private static AgentToolResult Result(object payload)
    {
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(payload));
        return new AgentToolResult(document.RootElement.Clone());
    }
}
