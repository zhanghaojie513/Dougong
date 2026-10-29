# 嵌入指南：在 .NET 服务里跑一个 Agent

> **这份文档给谁**：想把 Dougong 嵌进自己的 .NET 程序（Web 服务、桌面应用、批处理），
> 而不是用 CLI 的人。
>
> **它和 [plugin-author-guide.md](plugin-author-guide.md) 的区别**：那份讲怎么写插件；
> 这份讲**怎么把已有的零件装成一个能跑的 agent**。
>
> **状态**：2026-10-27 写，代码全部实际编译并运行过。文末列出**写这份文档时发现的真实缺口**。

---

## 一、最小可用：20 行

```csharp
using Dougong.Agent;
using Dougong.Ai;
using Microsoft.Extensions.AI;

// 1) 模型连接。apiKey 由你的应用自己管（配置中心、密钥管理服务…）
var config = new ChatProviderConfig
{
    Name = "my-app",          // required：用于多 provider 场景下区分
    BaseUrl = "https://api.deepseek.com",
    Model = "deepseek-flash",
    Protocol = ChatProtocol.OpenAiChatCompletions,
};
using var client = ChatClientFactory.Create(config, apiKey);

// 2) 审批器 —— 生产环境必须传，见 §三
var approver = new MyApprover();

// 3) 工具派发器：唯一执行入口
var dispatcher = new ToolDispatcher(approver: approver);

// 4) 注册工具（这里用你自己的工具，见 §四）
dispatcher.Register(new MyTool());

// 5) 运行时
var runtime = new LoopAgentRuntime(client, dispatcher);

// 6) 跑一轮
var history = new List<AgentMessage>();
await foreach (var evt in runtime.RunAsync(
    new AgentRunRequest("session-1", "turn-1", history, "你好")))
{
    if (evt is AgentEvent.TextDelta delta) Console.Write(delta.Text);
    if (evt is AgentEvent.RunFinished end) Console.WriteLine($"\n[{end.Outcome}]");
}
```

**这就是全部。** 没有注册表、没有 DI 容器、没有 builder——五个对象，直接 `new`。

> **为什么不提供 `DougongHost.Create(...)` 这样的一行封装**：这 5 步已经是 5 行，
> 包一层只是把 5 行藏成 1 行，代价是多一个公开类型进入 ApiCompat 契约。
> 而契约比便利更贵——见 [host-layering-decision.md](host-layering-decision.md)。

---

## 二、五个对象各管什么

| 对象 | 职责 | 换掉它会怎样 |
|---|---|---|
| `IChatClient` | 模型连接 | 换 provider / 换协议；可换成假模型做测试 |
| `IToolApprover` | 决定某次工具调用是否放行 | 换审批策略：控制台、弹窗、白名单、自动拒绝 |
| `IToolDispatcher` | **唯一执行入口**：schema 校验 → 审批 → 资源锁 → 执行 | 换调度策略（很少需要） |
| `IAgentTool` | 一个工具 | 加/减能力 |
| `IAgentRuntime` | 多轮循环：调模型 → 派发工具 → 回填 → 再调模型 | 换编排策略（几乎不需要） |

**五个都可替换。** 这是 `goal.md` P1「可组合」的落地方式——
它们都是接口，你可以在不改内核的前提下换成自己的实现。

---

## 三、审批器：生产环境必须传

`ToolDispatcher` 的 `approver` 参数**省略即表示"无审批要求"**。
源码的原话是：

> 省略即表示"无审批要求"，仅用于测试与对比；**生产宿主必须显式提供审批器**。

**这不是可选的礼貌建议。** 不传审批器意味着模型请求的任何工具调用都会直接执行——
包括写文件、跑命令。生产环境这样做等于把文件系统交给模型。

### 最小可用的审批器

```csharp
public sealed class MyApprover : IToolApprover
{
    public ValueTask<ToolApprovalDecision> RequestApprovalAsync(
        ToolApprovalRequest request, CancellationToken cancellationToken = default)
    {
        // request.Permission 是工具自述的权限，request.Binding 是完整绑定摘要。
        // 判断依据只能是这两个——不要用"上次批准过"推断这次也批准。
        if (!request.Permission.IsKnown)
        {
            // 权限未知 = "我不知道"，不是"没有权限"。拒绝是最安全的默认。
            return ValueTask.FromResult(
                ToolApprovalDecision.Deny("工具无法可靠描述自身访问范围"));
        }

        return ValueTask.FromResult(ToolApprovalDecision.Allow("策略放行"));
    }
}
```

### 三条必须知道的语义

1. **`IsKnown == false` 不能当无风险放行。** 它表示"我不知道这个工具会碰什么"。
2. **批准只对那一次绑定有效。** `request.Binding` 是参数、版本、工作区等的完整摘要；
   工具被替换或参数变了，旧批准自动失效——**你不需要自己实现这个判断**。
3. **批准不等于跳过执行前复核。** dispatcher 在执行前会再核对一次绑定。
   你返回 `Allow` 之后文件被改过，这次调用仍会被拒。

### 想省掉审批噪音？

有一个现成的装饰器：`PermissionPolicyApprover`。

```csharp
var dispatcher = new ToolDispatcher(
    approver: new PermissionPolicyApprover(realApprover));
```

它只在**同时满足**以下四条时跳过询问，其余照常问：

- `Permission.IsKnown == true`
- `Permission.Access == ToolAccess.None`
- 无路径、无命令、无域名

**跳过的决定权在宿主，不在工具。** 工具只能如实声明，信不信由你。

---

## 四、工具：实现 `IAgentTool`

这里只给骨架，完整说明（`DescribePermission` 六种填法、`Reads`/`Writes` 冲突规则）
在 [plugin-author-guide.md §2.5](plugin-author-guide.md)。

```csharp
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
```

> **返回的 `JsonElement` 必须是独立拥有的。** 用 `using` + `Clone()` 是最省心的写法；
> 直接返回 `doc.RootElement` 会在 `doc` 释放后变成无效引用。

### 常用工具要自己写

`read_file` / `write_file` 目前在 **CLI 内部**（`internal`），SDK 没有公开。
所以嵌入时你需要自己实现——大约 30 行一个，参照上面骨架。

**为什么还没公开**：路径规范化规则、权限声明、返回格式都还没有第二个宿主的验证。
详见本文 §七。

---

## 五、事件流：怎么把进展显示出来

`RunAsync` 是 `IAsyncEnumerable<AgentEvent>`，九种事件：

| 事件 | 什么时候 | 宿主通常做什么 |
|---|---|---|
| `RunStarted` | 开跑 | — |
| `ReasoningDelta` | 模型在推理 | **与正文分开显示**，见下文 |
| `TextDelta` | 模型吐字 | 追加到界面 / 流式输出 |
| `ToolCallRequested` | 要调用工具 | 显示"正在调用 X" |
| `ToolCallCompleted` | 工具结束 | 显示结果或失败原因 |
| `ToolResultTruncated` | 结果过长被裁 | **提示用户"模型看到的是裁剪版"** |
| `ContextCompacted` | 历史被压缩 | 提示"上下文已压缩" |
| `PlanUpdated` | 模型更新了计划 | 显示计划 |
| `RunFinished` | 结束 | 看 `Outcome` 决定后续 |

### 5.1 推理内容（`ReasoningDelta`）

只有启用思考模式的模型才产生它；不支持时一个都不会有，**不能当作必然信号**。

**必须与 `TextDelta` 分开显示。** 推理不是回答，而且常带未定稿的自我纠错
（"等等，这里算错了"）——当正文显示会让用户以为模型已经答完，还会误导。

推理内容同时也按 `AgentPropertyKeys.Reasoning` 存进了历史，所以恢复会话后仍是完整的。

### 5.2 状态显示：别自己推导，用 `AgentPhaseTracker`

想知道"现在该显示什么"（等待中？推理中？输出中？），
**不要自己从事件推断**——有两件事纯事件驱动做不到：

1. `RunStarted` 之后、第一个片段之前是**完全静默**的，而这段等待占整轮耗时的大头
2. 推理和正文都会停下来，停了之后要回落到"思考中"，这个只能靠计时

用现成的：

```csharp
var tracker = new AgentPhaseTracker();

tracker.StartTurn(DateTimeOffset.UtcNow);   // 在拉取事件流之前调

await foreach (var evt in runtime.RunAsync(request))
{
    tracker.OnEvent(evt, DateTimeOffset.UtcNow);
    Render(tracker.Phase, tracker.TurnElapsed(DateTimeOffset.UtcNow));
}
```

同时在宿主侧起一个 100ms 的定时器调 `tracker.Tick(now)`——**不调 `Tick` 的话阶段会永远停在最后一个事件上**，
例如推理结束后界面一直写着"推理中"。

`AgentPhase` 有七个值：`Idle` / `Thinking` / `Reasoning` / `Working` /
`Compacting` / `Streaming` / `Failed`。`Thinking` 与 `Reasoning` 的区别是
"什么都还没来"与"模型确实在动"——长时间没输出时，这两者给用户的感受完全不同。

### 5.3 跨进程宿主：事件怎么过线

如果你的宿主是**独立进程**（UI 进程 + Agent 进程，像桌面端那样），
事件要序列化过线。三条实践，来自桌面端的真实问题：

**① 推理与正文必须是不同的条目类型。** 不要共用一个 `text` 字段加个 `isReasoning`
标志——UI 折叠、样式、历史重放的处理方式都不同，后期再分家要改协议。

**② 每个事件带单调递增序号。** 客户端据此检测缺口。序号**必须全连接唯一**，
不能每会话从 0 计数——否则同一条连接上多会话时序号会错乱，客户端误判成丢消息。

**③ 流式条目的"结束"要显式发。** 流式文本推进时是"更新同一条目"，
回合结束时要把 `isStreaming` 关掉，否则 UI 的光标永远在闪。
这一点在 `RunFinished` 之后、且 `Cancelled` / `Failed` 路径上也要做到——
异常退出时最容易漏。

### 5.4 结束状态：`RunFinished.Outcome`

有五种，**必须处理，不能只认 `Completed`**：

```csharp
switch (end.Outcome)
{
    case AgentRunOutcome.Completed:            break;   // 正常
    case AgentRunOutcome.ToolRoundLimitReached: break;  // 轮次上限，可能要续跑
    case AgentRunOutcome.Cancelled:             break;  // 调用方取消
    case AgentRunOutcome.InvalidModelResponse:  break;  // 模型输出不合法
    case AgentRunOutcome.Failed:                break;  // 运行时内部错误
}
```

**把 `Cancelled` 和 `Failed` 混为一谈是常见错误**——前者是你主动取消的，后者需要排查。

---

## 六、持久化与恢复

不传 `store` 时，运行时**不持久化**：进程退出后历史就没了，也不能恢复。

需要一个能恢复的会话时：

```csharp
await using var store = await JsonlAgentStore.OpenAsync("/path/to/session.jsonl", sessionId);
// 注意：第三个参数是 IStoreAccessor，不是 IAgentStore。
// 直接传 store 会编译失败（CS1503）——这是本文写作时实际踩到的。
var runtime = new LoopAgentRuntime(client, dispatcher, new FixedStoreAccessor(store));

// 重启后恢复
await using var reopened = await JsonlAgentStore.OpenAsync(path, sessionId);
var recovered = await SessionRecovery.ReplayAsync(reopened);
if (recovered.RequiresReconciliation)
{
    // 有调用的结果未知（进程在派发中途死了）。不能自动重放，
    // 必须由人核实：问 "这些操作做了吗"。
    foreach (var unknown in recovered.Unknown)
    {
        Console.WriteLine($"未知结果：{unknown.ToolId}，参数 {unknown.ArgumentsJson}");
    }
}
```

**`RequiresReconciliation` 是 P3「故障可解释」的核心。** 它区分：

- 只读工具崩溃 → `Retryable`，可安全重试
- 可能产生副作用的工具 → 必须人工核实

**不要把它当成"没什么可选的就跳过"**——那等于把"不知道"当成"没发生"。

---

## 七、写这份文档时发现的真实缺口

写之前我以为现有 API 够用。实际写下来发现两处不对，如实记录。

### 缺口 1：`read_file` / `write_file` 不公开（已确认）

任何嵌入宿主都要自己写这两个工具。它们现在在 `Dougong.Cli` 里是 `internal`。

**为什么没顺手公开**：路径规范化规则（`ResourceKeys.ForPath`）、权限声明、返回格式
都只被 CLI 一个消费者用过。等第二个宿主出现再定契约，比现在猜更稳。

### 缺口 2：不传审批器太容易（值得讨论）

```csharp
var dispatcher = new ToolDispatcher();   // 编译通过，无任何警告
```

**这行代码在生产环境是危险的**——所有工具调用直接执行，不询问任何人。

源码注释写了"生产宿主必须显式提供"，但**编译器不会提醒你**。

可能的改进方向（未定，先记录）：

- 加一个必须显式传 `approver` 的重载，让"无审批"变成需要刻意选择
- 或提供一个 `ToolDispatcher.ForTesting()`，把危险路径的名字写明

**现在没改**，因为改构造签名是破坏性变更（ApiCompat 会报 CP0002），
需要先确认没有其他更合适的形状。

### 缺口 3：上下文压缩要自己配（不算缺口，但是坑）

`LoopAgentRuntime` 的 `limits` / `compactor` / `summarize` 三个参数都不传时，
**不压缩**。长会话会一直涨到 provider 拒绝为止。

需要压缩时三个一起传：

```csharp
var runtime = new LoopAgentRuntime(client, dispatcher, store,
    limits: new ContextLimits { ContextWindowTokens = 128_000, MaxOutputTokens = 4_096 },
    compactor: new SlidingWindowCompactor(preservedHead: 2, preservedTail: 6),
    summarize: (transcript, ct) => MySummarizeAsync(client, transcript, ct));
```

**三个缺一个都不生效**，而且**不报错**——这是最容易踩的坑。本文写在这里就是为了避免它。

---

## 八、这份文档的边界

**已验证的**：本文所有代码**实际编译并运行过**（`samples/_embed_probe` 探针工程），
不是"看起来对"。

编译时抓到两处写错，已修正——记在这里，因为它们都是"照抄会失败"的类型：

| 我原本写的 | 实际要求 |
|---|---|
| `new ChatProviderConfig { BaseUrl = ... }` | `Name` 是 `required`，不写编译失败 |
| `new LoopAgentRuntime(client, dispatcher, store)` | 第三个参数是 `IStoreAccessor`，要 `new FixedStoreAccessor(store)` |

**这两个错误说明"作者凭记忆写代码示例"不可靠。** 后续改本文时请同样跑一遍探针。

**没验证的**：

- 只在一个进程内跑过，没有在 ASP.NET Core 的请求/后台服务里实际接过
- 并发多会话（同一进程内多个 runtime 实例共享库）没测
- `IChatClient` 换成非 Dougong 的实现（例如 `OpenAIClient`）没试过，
  理论上可以——`LoopAgentRuntime` 只依赖 `IChatClient` 抽象

**如果你在嵌入时卡住**：那不是你的问题，是本文的缺口。按
[getting-started.md §8](getting-started.md) 的方式报出来。
