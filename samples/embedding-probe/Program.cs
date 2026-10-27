using System.Text.Json;
using Dougong.Agent;
using Dougong.Ai;

// ===== §一 最小可用 =====
var config = new ChatProviderConfig
{
    Name = "probe",
    BaseUrl = "https://api.deepseek.com",
    Model = "deepseek-flash",
    Protocol = ChatProtocol.OpenAiChatCompletions,
};
using var client = ChatClientFactory.Create(config, "fake-key");

var approver = new MyApprover();
var dispatcher = new ToolDispatcher(approver: approver);
dispatcher.Register(new MyTool());
var runtime = new LoopAgentRuntime(client, dispatcher);

Console.WriteLine("§一 构造成功");

// ===== §五 事件流：只需定义形状，不真跑（没有真 key） =====
Console.WriteLine("§五 事件类型: RunStarted/TextDelta/ToolCallRequested/ToolCallCompleted/ToolResultTruncated/ContextCompacted/PlanUpdated/RunFinished");

// ===== §七 缺口 2：不传审批器确实编译通过 =====
var unsafeDispatcher = new ToolDispatcher();
Console.WriteLine("§七 缺口2 已确认: new ToolDispatcher() 编译通过且无警告");

// ===== §六 持久化形状 =====
var dir = Path.Combine(Path.GetTempPath(), "embed-probe-" + Guid.NewGuid().ToString("N")[..6]);
Directory.CreateDirectory(dir);
try
{
    var path = Path.Combine(dir, "s.jsonl");
    await using (var store = await JsonlAgentStore.OpenAsync(path, "probe"))
    {
        var r = new LoopAgentRuntime(client, dispatcher, new FixedStoreAccessor(store));
        Console.WriteLine("§六 带 store 构造成功");
        _ = r;
    }
    await using var reopened = await JsonlAgentStore.OpenAsync(path, "probe");
    var recovered = await SessionRecovery.ReplayAsync(reopened);
    Console.WriteLine($"§六 恢复: Unknown={recovered.Unknown.Count} RequiresReconciliation={recovered.RequiresReconciliation} Retryable={recovered.Retryable.Count}");
}
finally { try { Directory.Delete(dir, true); } catch { } }

// ===== §七 缺口 3：压缩三参数 =====
var compacting = new LoopAgentRuntime(client, dispatcher, null,
    limits: new ContextLimits { ContextWindowTokens = 128_000, MaxOutputTokens = 4_096 },
    compactor: new SlidingWindowCompactor(preservedHead: 2, preservedTail: 6),
    summarize: (transcript, ct) => ValueTask.FromResult("summary"));
Console.WriteLine("§七 压缩三参数构造成功");
_ = compacting;

// ===== §三 PermissionPolicyApprover =====
var policy = new PermissionPolicyApprover(new MyApprover());
var policyDispatcher = new ToolDispatcher(approver: policy);
Console.WriteLine("§三 PermissionPolicyApprover 构造成功");
_ = policyDispatcher;

// ===== §四 工具骨架 =====
Console.WriteLine("§四 工具注册成功");

public sealed class MyApprover : IToolApprover
{
    public ValueTask<ToolApprovalDecision> RequestApprovalAsync(
        ToolApprovalRequest request, CancellationToken cancellationToken = default)
    {
        if (!request.Permission.IsKnown)
            return ValueTask.FromResult(ToolApprovalDecision.Deny("工具无法可靠描述自身访问范围"));
        return ValueTask.FromResult(ToolApprovalDecision.Allow("策略放行"));
    }
}

public sealed class MyTool : IAgentTool
{
    public ToolDescriptor Descriptor { get; } = new(
        "my_tool", "my_tool", "1.0.0", "做什么（给模型看）",
        JsonDocument.Parse("""
            {"type":"object","properties":{"q":{"type":"string"}},"required":["q"]}
            """).RootElement.Clone());

    public ToolPermission DescribePermission(JsonElement arguments) =>
        new(ToolAccess.None, IsKnown: true, Paths: [], Commands: [], Domains: []);

    public ToolExecutionPlan DescribeExecution(JsonElement arguments) =>
        ToolExecutionPlan.Serial;

    public ValueTask<AgentToolResult> ExecuteAsync(
        ToolInvocation invocation, CancellationToken cancellationToken)
    {
        using var doc = JsonDocument.Parse("""{"ok":true}""");
        return ValueTask.FromResult(new AgentToolResult(doc.RootElement.Clone()));
    }
}
