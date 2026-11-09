using System.Text.Json;

namespace Dougong.Agent;

/// <summary>
/// 委派一个自包含的任务给子 agent。
/// </summary>
/// <remarks>
/// <b>它解决的是上下文预算问题</b>：一次"找出这个模块的调用链"会读十几个文件，
/// 而那些读取记录在任务完成后毫无价值。让子 agent 去做，
/// 主对话只收到一段结论。
///
/// <para>
/// <b>照 dsh 的 <c>dsh-tool-subagent</c>，形状一致：两个必填参数</b>
/// （<c>description</c> 与 <c>prompt</c>）。**没有 <c>cwd</c> 参数**：
/// 子 agent 继承调用时的有效目录（见 D-32）——让模型再指定一次，
/// 它会用相对路径而两边解析基准不同，结果是子 agent 在别处找文件。
/// </para>
///
/// <para>
/// <b>启动立即返回，不等结果</b>：dsh 也是这个形态（"starts an independently
/// managed subagent and immediately returns its id"）。等结果会让并行能力失效——
/// 模型可以连开三个再一起收。
/// </para>
///
/// <para>
/// <b>所以分成三个工具而不是一个</b>：<c>subagent</c>（启动）、
/// <c>subagent_list</c>（看有哪些在跑）、<c>subagent_result</c>（取结果）。
/// 这与后台作业同一形态——用工具名说清动作，不让模型猜 <c>mode</c> 参数。
/// </para>
/// </remarks>
public sealed class SubagentTool : IAgentTool
{
    /// <summary>工具 ID。</summary>
    public const string ToolId = "subagent";

    private readonly SubagentRegistry _registry;
    private readonly Func<AgentRunRequest, IAgentRuntime> _runtimeFactory;
    private readonly string _sessionId;
    private readonly string _parentTurnId;

    /// <summary>构造。</summary>
    /// <param name="registry">注册表。</param>
    /// <param name="runtimeFactory">
    /// 按请求造一个**独立的**子运行时。它必须与主对话的运行时分开：
    /// 共用会让子 agent 的中间消息进入主上下文，那正好抵消了这个能力。
    /// </param>
    /// <param name="sessionId">父会话标识。</param>
    /// <param name="parentTurnId">父回合标识，用于拼子会话标识。</param>
    public SubagentTool(
        SubagentRegistry registry,
        Func<AgentRunRequest, IAgentRuntime> runtimeFactory,
        string sessionId,
        string parentTurnId)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(runtimeFactory);

        _registry = registry;
        _runtimeFactory = runtimeFactory;
        _sessionId = sessionId;
        _parentTurnId = parentTurnId;
    }

    public ToolDescriptor Descriptor { get; } = new(
        ToolId, ToolId, "1.0.0",
        "把一个自包含的任务委派给子 agent 去做。它不共享当前对话的上下文，"
        + "所以任务描述必须完整。立即返回子 agent 的 id；用 subagent_result 取结果。"
        + "适用于：查清某个模块的实现、做一次范围明确的修改、独立的分析。",
        JsonDocument.Parse(
            """
            {
              "type": "object",
              "properties": {
                "description": {
                  "type": "string",
                  "description": "3-5 个词的短语，说明要做什么，用于列表显示。"
                },
                "prompt": {
                  "type": "string",
                  "description": "完整、自包含的任务说明。子 agent 看不到当前对话，需要的信息都要写进来。"
                }
              },
              "required": ["description", "prompt"],
              "additionalProperties": false
            }
            """).RootElement.Clone());

    /// <summary>会启动子进程/子运行，且会消耗额度——报 Execute 以便宿主可拦。</summary>
    public ToolPermission DescribePermission(JsonElement arguments) =>
        new(ToolAccess.Execute, IsKnown: true, Paths: [], Commands: [], Domains: []);

    /// <summary>
    /// 工作区独占。
    /// </summary>
    /// <remarks>
    /// <b>不能与其它工具并行</b>：子 agent 会读写同一批文件，
    /// 而资源闸门看不到它内部的操作。让它与别的写并行，
    /// 等于绕过了互斥——两边都"成功"，后写的覆盖前写的。
    /// </remarks>
    public ToolExecutionPlan DescribeExecution(JsonElement arguments) =>
        new(ToolConcurrency.WorkspaceExclusive);

    public ValueTask<AgentToolResult> ExecuteAsync(
        ToolInvocation invocation, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(invocation);

        var label = ReadString(invocation.Arguments, "description");
        var prompt = ReadString(invocation.Arguments, "prompt");

        if (string.IsNullOrWhiteSpace(label))
            return ValueTask.FromResult(Fail("缺少 description 参数。"));
        if (string.IsNullOrWhiteSpace(prompt))
            return ValueTask.FromResult(Fail("缺少 prompt 参数。"));

        return StartAsync(label, prompt, cancellationToken);
    }

    private async ValueTask<AgentToolResult> StartAsync(
        string label, string prompt, CancellationToken cancellationToken)
    {
        // **并发上限：拒绝而不是排队**。排队会让模型以为已启动。
        if (!await _registry.TryAcquireAsync(cancellationToken).ConfigureAwait(false))
            return Fail($"已经有 {_registry.MaxConcurrent} 个子 agent 在跑，"
                + "等其中一个结束再启动新的（用 subagent_list 查看）。");

        var id = $"sub-{Guid.NewGuid().ToString("N")[..12]}";
        // ParentTurnId 从这一处就填上：界面要能把它归到发起它的那一轮下面。
        var handle = new SubagentHandle(id, label) { ParentTurnId = _parentTurnId };

        await _registry.RegisterAsync(handle, cancellationToken).ConfigureAwait(false);

        // 子会话有自己的标识与空历史：隔离就是这两件事。
        var request = new AgentRunRequest(
            SessionId: $"{_sessionId}/{id}",
            TurnId: $"{_parentTurnId}/{id}/1",
            History: [],
            UserInput: prompt);

        IAgentRuntime runtime;
        try
        {
            runtime = _runtimeFactory(request);
        }
        catch (Exception ex)
        {
            await _registry.UnregisterAsync(id, CancellationToken.None).ConfigureAwait(false);
            handle.Dispose();
            return Fail($"无法创建子 agent：{ex.Message}");
        }

        // 后台跑，立即返回。**不 await**——这是"启动即返回"的全部实现。
        handle.Completion = Task.Run(
            async () => await SubagentRunner.RunAsync(
                _registry, handle, runtime, request, CancellationToken.None)
                .ConfigureAwait(false),
            CancellationToken.None);

        using var document = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            ok = true,
            error = (string?)null,
            id,
            label,
            note = "子 agent 已启动。用 subagent_list 看状态，subagent_result 取结果。",
        }));

        return new AgentToolResult(document.RootElement.Clone());
    }

    private static string? ReadString(JsonElement arguments, string name) =>
        arguments.TryGetProperty(name, out var element)
            && element.ValueKind is JsonValueKind.String
                ? element.GetString()
                : null;

    private static AgentToolResult Fail(string message)
    {
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            ok = false,
            error = message,
            id = (string?)null,
            label = (string?)null,
            note = (string?)null,
        }));

        return new AgentToolResult(document.RootElement.Clone());
    }
}

/// <summary>列出活跃的子 agent。</summary>
/// <remarks>
/// <b>只列活跃的</b>：已完成的从注册表里移除。
/// 保留已完成项会让列表无限增长，而"上次那个的结果"应该由调用方
/// 在 <c>subagent_result</c> 里取走。没取走就没了——这一点要在工具描述里说明，
/// 否则模型会以为随时能回来查。
/// </remarks>
public sealed class SubagentListTool : IAgentTool
{
    /// <summary>工具 ID。</summary>
    public const string ToolId = "subagent_list";

    private readonly SubagentRegistry _registry;

    /// <summary>构造。</summary>
    /// <param name="registry">注册表。</param>
    public SubagentListTool(SubagentRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        _registry = registry;
    }

    public ToolDescriptor Descriptor { get; } = new(
        ToolId, ToolId, "1.0.0",
        "列出当前正在运行的子 agent 及其状态。",
        JsonDocument.Parse(
            """
            { "type": "object", "properties": {}, "additionalProperties": false }
            """).RootElement.Clone());

    /// <summary>纯内存读取。</summary>
    public ToolPermission DescribePermission(JsonElement arguments) =>
        new(ToolAccess.None, IsKnown: true, Paths: [], Commands: [], Domains: []);

    public ToolExecutionPlan DescribeExecution(JsonElement arguments) =>
        new(ToolConcurrency.Serial);

    public ValueTask<AgentToolResult> ExecuteAsync(
        ToolInvocation invocation, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(invocation);

        var items = _registry.Active()
            .Select(h => new
            {
                id = h.Id,
                label = h.Label,
                running = !h.IsCompleted,
            })
            .ToArray();

        using var document = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            count = items.Length,
            agents = items,
            note = items.Length == 0
                ? "没有正在运行的子 agent。"
                : "用 subagent_result <id> 取结果。",
        }));

        return ValueTask.FromResult(new AgentToolResult(document.RootElement.Clone()));
    }
}

/// <summary>取一个子 agent 的结果。</summary>
/// <remarks>
/// <b>要能"还没好"</b>：这是它区别于普通同步调用的地方。模型不该在
/// 子 agent 还在跑时被阻塞——那等于同步。返回 <c>running=true</c> 让它
/// 去做别的事，过会儿再来。
/// <para>
/// <b>取走即消失</b>：结果被读一次之后从注册表移除。保留会让列表无限增长，
/// 而"我忘了刚才的 id"是可以避免的（<c>subagent_list</c>）。
/// </para>
/// </remarks>
public sealed class SubagentResultTool : IAgentTool
{
    /// <summary>工具 ID。</summary>
    public const string ToolId = "subagent_result";

    private readonly SubagentRegistry _registry;

    /// <summary>构造。</summary>
    /// <param name="registry">注册表。</param>
    public SubagentResultTool(SubagentRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        _registry = registry;
    }

    public ToolDescriptor Descriptor { get; } = new(
        ToolId, ToolId, "1.0.0",
        "取一个子 agent 的结果。若它还在跑，返回 running=true——不要反复轮询，"
        + "先做别的事。结果只会返回一次：取走后该子 agent 从列表消失。",
        JsonDocument.Parse(
            """
            {
              "type": "object",
              "properties": {
                "id": { "type": "string", "description": "subagent 返回的 id。" }
              },
              "required": ["id"],
              "additionalProperties": false
            }
            """).RootElement.Clone());

    public ToolPermission DescribePermission(JsonElement arguments) =>
        new(ToolAccess.None, IsKnown: true, Paths: [], Commands: [], Domains: []);

    public ToolExecutionPlan DescribeExecution(JsonElement arguments) =>
        new(ToolConcurrency.Serial);

    public async ValueTask<AgentToolResult> ExecuteAsync(
        ToolInvocation invocation, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(invocation);

        var id = invocation.Arguments.TryGetProperty("id", out var element)
            && element.ValueKind is JsonValueKind.String
                ? element.GetString()
                : null;

        if (string.IsNullOrWhiteSpace(id))
            return Fail("缺少 id 参数。");

        var handle = _registry.Find(id);
        if (handle is null)
            return Fail($"没有这个子 agent：{id}（可能已取走或从未存在）。");

        if (!handle.IsCompleted)
        {
            using var running = JsonDocument.Parse(JsonSerializer.Serialize(new
            {
                ok = true,
                running = true,
                id,
                label = handle.Label,
                status = (string?)null,
                text = (string?)null,
                diagnostic = (string?)null,
                toolRounds = 0,
                note = "还在跑。先去处理别的，稍后再取——不要空转轮询。",
            }));

            return new AgentToolResult(running.RootElement.Clone());
        }

        var result = handle.Result;
        if (result is null)
            return Fail("子 agent 已结束但没有留下结果（内部状态异常）。");

        // **取走即移除**：结果只交付一次。
        await _registry.UnregisterAsync(id, CancellationToken.None).ConfigureAwait(false);

        using var document = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            ok = result.Status is SubagentStatus.Completed,
            running = false,
            id = result.Id,
            label = handle.Label,
            status = result.Status.ToString(),
            text = result.Text,
            diagnostic = result.Diagnostic,
            toolRounds = result.ToolRounds,
            note = result.Status is SubagentStatus.Completed
                ? "这是它的最终结论；中间过程没有回传。"
                : "子 agent 未正常完成，看 diagnostic。",
        }));

        return new AgentToolResult(document.RootElement.Clone());
    }

    private static AgentToolResult Fail(string message)
    {
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            ok = false,
            running = false,
            id = (string?)null,
            label = (string?)null,
            status = (string?)null,
            text = (string?)null,
            diagnostic = message,
            toolRounds = 0,
            note = (string?)null,
        }));

        return new AgentToolResult(document.RootElement.Clone());
    }
}
