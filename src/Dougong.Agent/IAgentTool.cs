using System.Text.Json;

namespace Dougong.Agent;

/// <summary>
/// Agent 工具的宿主契约。描述符在注册时冻结，模型只看到 Name/Description/InputSchema；
/// 实际执行必须经过唯一的 <see cref="IToolDispatcher"/>，不得由框架直接调用。
/// </summary>
public interface IAgentTool
{
    /// <summary>注册时冻结的工具身份与 schema。</summary>
    ToolDescriptor Descriptor { get; }

    /// <summary>根据参数描述本次调用需要的权限。无法可靠描述时必须返回 <c>IsKnown=false</c>。</summary>
    ToolPermission DescribePermission(JsonElement arguments);

    /// <summary>描述本次调用的资源并发要求。</summary>
    ToolExecutionPlan DescribeExecution(JsonElement arguments);

    /// <summary>
    /// 声明本次调用依赖哪些文件的版本。审批时捕获、执行前比对；
    /// 返回空表示不依赖文件内容（例如纯内存工具）。
    /// </summary>
    IReadOnlyList<string> DescribeFileDependencies(JsonElement arguments) => [];

    /// <summary>实际执行。只能由 dispatcher 调用。</summary>
    ValueTask<AgentToolResult> ExecuteAsync(ToolInvocation invocation, CancellationToken cancellationToken);
}

/// <summary>冻结后的工具描述符；<see cref="InputSchema"/> 必须是宿主侧可校验的 JSON Schema。</summary>
/// <param name="Id">稳定 ID，注册表用它索引；同一内核内必须唯一。</param>
/// <param name="Name">模型可见名。必须匹配 provider 的字符集（`^[a-zA-Z0-9_-]+$`）。</param>
/// <param name="Version">版本。工具被替换时版本变化会使旧审批失效。</param>
/// <param name="Description">给模型看的说明，决定它何时会调用这个工具。</param>
/// <param name="InputSchema">宿主侧可校验的 JSON Schema；不支持的构造必须报错而非静默跳过。</param>
public sealed record ToolDescriptor(
    string Id,
    string Name,
    string Version,
    string Description,
    JsonElement InputSchema)
{
    /// <summary>
    /// 是否可能产生**不可撤销**的副作用（写外部系统、发请求、改远端状态）。
    /// 默认 false 表示"只读或可安全重放"。
    /// </summary>
    /// <remarks>
    /// <b>唯一用途</b>：恢复会话时发现某调用「有开始记录、无终态」
    /// （<see cref="ToolCallOutcome.OutcomeUnknown"/>），宿主据此决定：
    /// false → 可安全重试；true → 必须人工核实，不得自动重放。
    /// <para>
    /// <b>默认 false 的理由</b>：只读工具占多数，不该为它们逐个加声明。
    /// 但会改外部状态的工具**必须显式声明 true**——不声明而实际有副作用的工具，
    /// 会让宿主错误地重试。框架无法从参数推断工具会做什么，这是使用方的责任。
    /// </para>
    /// <para>
    /// <b>它不是幂等键</b>：幂等键让外部系统识别重复请求，需要远端配合。
    /// 这个声明只表达"要不要人工看一眼"。
    /// </para>
    /// <para>
    /// 用 <c>init</c> 属性而不是构造参数：后者会改变构造函数签名，
    /// 破坏已有的 <c>new ToolDescriptor(a, b, c, d, e)</c> 调用。
    /// ApiCompat 在 CI 上精确报出了这一点（CP0002）。
    /// </para>
    /// </remarks>
    public bool MayHaveIrreversibleEffects { get; init; }
}

/// <summary>工具可能触达的访问类别，可组合。</summary>
[Flags]
public enum ToolAccess
{
    None = 0,
    Read = 1,
    Write = 2,
    Execute = 4,
    Network = 8,
    External = 16,
}

/// <summary>
/// 工具自描述的权限声明。<see cref="IsKnown"/> 为 false 表示无法可靠描述，
/// 调用方不得把它当作 <see cref="ToolAccess.None"/> 放行。
/// </summary>
public sealed record ToolPermission(
    ToolAccess Access,
    bool IsKnown,
    IReadOnlyList<string> Paths,
    IReadOnlyList<string> Commands,
    IReadOnlyList<string> Domains)
{
    public static ToolPermission Unknown { get; } = new(
        ToolAccess.None, false, [], [], []);
}

/// <summary>首版只有两级并发：串行与工作区独占。</summary>
public enum ToolConcurrency
{
    Serial,
    WorkspaceExclusive,
}

/// <summary>
/// 本次调用的资源计划。
/// </summary>
/// <remarks>
/// <see cref="Concurrency"/> 是保守的两级默认；<see cref="Reads"/> 与
/// <see cref="Writes"/> 是资源级键——声明得越准，并发度越高。
/// <para>
/// **键的语义**：只是标识"同一个东西"，不要求是路径。字符串比较用
/// <see cref="StringComparer.Ordinal"/>，因此工具必须自己统一写法
/// （例如都用绝对路径）。框架不猜测两个不同字符串是否指同一资源——
/// 猜错会导致该互斥的没互斥，比不并发更糟。
/// </para>
/// <para>
/// **冲突规则**：写-写冲突、读-写冲突都互斥；读-读不冲突。
/// 未声明任何键时退回 <see cref="Concurrency"/> 的保守行为。
/// </para>
/// </remarks>
public sealed record ToolExecutionPlan(ToolConcurrency Concurrency)
{
    public static ToolExecutionPlan Serial { get; } = new(ToolConcurrency.Serial);

    /// <summary>本次调用只读取的资源键。可为空。</summary>
    public IReadOnlyList<string> Reads { get; init; } = [];

    /// <summary>本次调用会修改的资源键。可为空。</summary>
    public IReadOnlyList<string> Writes { get; init; } = [];

    /// <summary>是否声明了资源级键。未声明时按 <see cref="Concurrency"/> 处理。</summary>
    public bool HasResourceKeys => Reads.Count > 0 || Writes.Count > 0;

    /// <summary>
    /// 本次调用的执行上限；null 表示用 dispatcher 的默认值。
    /// </summary>
    /// <remarks>
    /// <b>为什么要逐工具而不是一个全局值</b>：不同工具的正常耗时差两个数量级。
    /// <c>read_file</c> 正常在毫秒级，<c>shell</c> 跑一次构建可以是几分钟。
    /// 用一个全局值必然两难：按快的设，shell 会被误杀；按慢的设，
    /// 卡死的文件读要等几分钟才报错。
    /// <para>
    /// <b>声明在 <see cref="ToolExecutionPlan"/> 而不是新接口方法</b>：
    /// 加接口方法会破坏所有已实现的插件（ApiCompat 报 CP0002），
    /// 而计划对象本来就是"这次调用打算怎么做"的载体，超时属于同一类信息。
    /// </para>
    /// <para>
    /// <b>它只是上限，不是承诺</b>：工具内部仍可更早超时（如
    /// <c>ShellTool</c> 的 120 秒）。两层都设时取更早的那个生效——
    /// 先返回总比外层先杀好。
    /// </para>
    /// </remarks>
    public TimeSpan? Timeout { get; init; }
}

/// <summary>
/// 一次已通过 schema 校验并规范化参数的工具调用。
/// <see cref="InvocationId"/> 来自模型响应，用于聚合流式参数与去重。
/// </summary>
public sealed record ToolInvocation(
    string InvocationId,
    string ToolId,
    JsonElement Arguments);

/// <summary>
/// 工具执行结果。<see cref="Content"/> 进入模型历史，<see cref="Details"/> 仅供宿主 UI，
/// 两者都必须是复制过的独立 JSON 数据，不得引用已释放的 JsonDocument。
/// </summary>
public sealed record AgentToolResult(JsonElement Content, JsonElement? Details = null);
