# G14 接线方案：让内核真正承载运行能力

> **状态**：**已实现（2026-10-25 落地，现为默认路径）**。
> 本方案里的租约粒度取舍（B：按回合分租约）已落地为 `KernelAgentHost` /
> `KernelAgentRuntime`；内核接线默认启用，`DOUGONG_KERNEL=0` 可退回直连做对照。
> 验收：`-- kernel-wiring` 10 项 + `-- kernel-path` 3 项 + `-- kernel-process` 5 项
> + `-- replaceability` 4 项 + `-- bare-hold`（12 处持有全部说明）。
> **原文保留**——它记录了"为什么选 B 而不是 A/C"的完整论证，改接线时仍要读。
> **依据**：[plan.md](plan.md) §三 2.6、[kernel-vs-cordis.md](kernel-vs-cordis.md)、
> `goal.md` P1 判定第 1 条。
> **这份文档要解决的具体问题**：`DougongKernel` 在产品路径上零引用——
> `Dougong.Agent` 与 `Dougong.Cli` 源码都不用它，CLI 直接 `new ToolDispatcher` /
> `new LoopAgentRuntime`。内核的租约、代次绑定、排空、隔离在真实会话中从未生效。

---

## 一、必须先定的取舍：租约粒度

这是接线的唯一硬约束，不先定就写不下去。

`IServiceReference<T>.InvokeAsync` 的契约原文（`IResolver.cs`）：

> 绑定提供方激活代次的引用。每次调用取得短期租约，停用后不能再调用。
> **委托必须等待全部工作结束**，不得将实例保存到调用外或启动未等待的后台工作。

而一轮对话是长时间流式的：可能几十秒、多个工具调用、用户中途取消。

### 三个候选

| 粒度 | 做法 | 停用等待 | 问题 |
|---|---|---|---|
| **A. 整轮一租约** | 整个 `RunAsync` 包在一次 `InvokeAsync` 里 | 等当前轮跑完，或 `DrainTimeout`(30s) 后隔离 | 流式输出中停用插件，用户要等几十秒 |
| **B. 按回合/工具分租约** | 每次模型调用、每次工具执行各取一次租约 | 最多等一次模型调用或一次工具执行 | 实现复杂；轮次之间释放意味着"停用"可能落在两回合之间 |
| **C. 会话级租约** | 整个会话（多轮）持租约 | 等整个会话结束 | 停用等于关会话，最差 |

### 决定：B，按回合分租约

理由：

1. **它对应真实的可观察行为**。选项 A 的"停用要等 30 秒"是不可接受的：用户点停用插件，
   界面卡住半分钟，只能靠 `DrainTimeout` 强杀成隔离态。选项 B 下最坏只等一次模型调用
   （通常 1–5 秒）或一次工具执行。
2. **`architecture.md:278` 对工具调用已经是短租约**：
   "取得目标激活代次的短期租约后调用 `DescribePermission`/`DescribeExecution`……
   执行前重新取得同一代次的调用租约"。B 只是把同一原则推广到运行入口，不是新发明。
3. **它让"停用"有确定语义**：轮次边界是可观察点（`RunStarted`/`RunFinished` 之间）。
   停用请求到达时，若正处在一个回合中间，就等这个回合结束；若在两回合之间，立即生效。

**明确接受的代价**：轮次之间释放租约，意味着停用可能恰好落在两回合之间——
此时消费者被排空，下一轮会因提供方缺失进入 `Blocked`。这是正确的行为（依赖真的没了），
不是漏洞。要在验收里明确断言这一点，而不是假装它不会发生。

**不引入的东西**（`kernel-vs-cordis.md` §六）：不做自动重载，不用 epoch 字符串。

---

## 二、现在的装配长什么样

```text
Program.cs / AcpComposition
   └─ SessionComposition.FromEnvironment(...)        ← 读环境变量
        ├─ CreateDispatcher()                        ← new ToolDispatcher(approver, gate, ...)
        │     └─ Register(read_file / write_file / shell)
        └─ CreateRuntime(dispatcher, ledger)         ← new LoopAgentRuntime(...)
```

问题：`CreateDispatcher` 和 `CreateRuntime` 各自 `new`，内核完全不参与。

---

## 三、目标装配

```text
KernelAgentHost（新增，Dougong.Cli 或新的 Dougong.Hosting）
   ├─ DougongKernel
   ├─ RegisterAsync(StorePlugin)      → 提供 IAgentStore
   ├─ RegisterAsync(ApproverPlugin)   → 提供 IToolApprover
   ├─ RegisterAsync(RuntimePlugin)    → 提供 IAgentRuntime（依赖 IAgentStore）
   └─ RegisterAsync(ToolPlugin)       → 提供 IToolDispatcher（依赖 IToolApprover）
        ↓
   Resolver.Resolve<IAgentRuntime>()  → IServiceReference<IAgentRuntime>
   Resolver.Resolve<IAgentStore>()    → IServiceReference<IAgentStore>
```

宿主侧只通过 `InvokeAsync` 使用这些服务，每次调用是一回合。

### 分层约束

`ArchitectureBoundaryTests` 规定 `Dougong.Kernel`(L0) ← `Dougong.Agent`(L1) ←
`{Ai,Mcp,Acp}`(L2) ← `Cli`(L3)。新代码需要同时引用 Kernel 与 Agent，
**只有 L3 的 `Dougong.Cli` 满足**（它已同时引用两者）。

因此：**接线代码放 `Dougong.Cli`，不新增工程、不改分层表**。
如果将来桌面宿主也要用，再抽 `Dougong.Hosting`（L3，与 Cli 同层或更低），
但那要同时改 `Layers` 表——不在本切片。

---

## 四、实现步骤（每步都要能独立验证）

### 步骤 0：先让探针失败

新增 `-- kernel-wiring` 套件，第一条断言：

> `Dougong.Cli` 的编译产物中，存在对 `DougongKernel.RegisterAsync` 的引用。

断言方式**扫描编译产物**（`Assembly.GetReferencedAssemblies` + 从 IL/元数据找成员引用），
不读源码文本——后者会被注释和字符串误判，且在重构后容易失效。

现在必然失败：CLI 引用了 Kernel 程序集（csproj）但没有任何成员引用。

### 步骤 1：`KernelAgentHost` 骨架 + 存储插件

先只接一个能力：`IAgentStore`。选它的理由是最干净——不涉及审批时序，
`IAgentStore` 只有 `CommitAsync` / `ReadCommittedAsync` 两个方法，失败语义明确。

```csharp
internal sealed class StorePlugin(IAgentStore store) : IPlugin
{
    public PluginManifest Manifest { get; } = PluginManifest.Create(
        "dougong.store", "1.0.0");

    public ValueTask ActivateAsync(PluginContext context, CancellationToken ct)
    {
        context.Services.Register<IAgentStore>(store);
        // 注册即登记清理：不依赖插件作者记得调用 RegisterForCleanup。
        context.RegisterForCleanup(_ => store.DisposeAsync());
        return ValueTask.CompletedTask;
    }
}
```

验证点：`Resolve<IAgentStore>()` 非 null；`InvokeAsync(s => s.CommitAsync(...))` 成功。

### 步骤 2：审批器与 dispatcher 插件

`ToolDispatcher` 依赖审批器，因此 dispatcher 插件必须**解析**审批器，不能自己 `new`——
否则审批器又成了裸引用。这里要在插件激活时用 `context.Resolver.Resolve<IToolApprover>()`
拿到 `IServiceReference`，并把它适配成 `IToolApprover`（因为 dispatcher 要的是裸接口）。

**这是本方案最需要小心的一处**：适配器内部每次 `RequestApprovalAsync` 都走
`IServiceReference.InvokeAsync`，这样审批器的代次被尊重；但适配器本身会被 dispatcher
长期持有——它持有的是**引用**而不是实例，符合约束。

### 步骤 3：运行时插件

`LoopAgentRuntime` 依赖 `IChatClient` 与 `IToolDispatcher`。前者由宿主提供
（模型连接不属于插件组合的必然后果，先作为构造参数传给插件工厂），后者从解析器取。

**租约粒度的落点在这里**：`IAgentRuntime` 的 `RunAsync` 返回 `IAsyncEnumerable`，
宿主不能把整个枚举包在一次 `InvokeAsync` 里（那就是选项 A）。做法是提供
`KernelAgentRuntime` 适配器：它的 `RunAsync` 逐回合取租约——

```csharp
public async IAsyncEnumerable<AgentEvent> RunAsync(AgentRunRequest request, ...)
{
    // 每一回合 = 一次模型调用 + 它请求的工具；回合边界由 RunStarted/RunFinished 界定。
    await foreach (var evt in InvokeOnceAsync(request, ct))
        yield return evt;
}
```

**注意**：`InvokeAsync` 的委托签名是 `Func<T, CancellationToken, ValueTask<TResult>>`，
不适合直接包 `IAsyncEnumerable`。因此需要一个 `Channel<AgentEvent>` 中转：
后台任务在 `InvokeAsync` 里跑一个回合，事件写入 channel，宿主从 channel 读。
这引入一个真实风险——**如果宿主中途放弃枚举，后台回合必须被取消**，
否则租约永远不会释放、排空永远等不到。验收必须覆盖这一条。

### 步骤 4：CLI 切换 + 开关回退

`SessionComposition` 增加 `CreateFromKernelAsync(...)`，`Program.cs` 用环境变量
`DOUGONG_KERNEL` 切换。**已完成（2026-10-25）**：验收全部通过后默认已转为内核路径，
`DOUGONG_KERNEL=0` 退回直连作为对照手段。

理由：接线是结构改动，一次性切默认值会让 27 个套件同时受影响，失败时无法归因。

---

## 五、验收标准（对应 goal.md P1）

| # | 断言 | 反证方式 |
|---|---|---|
| 1 | CLI 编译产物中存在内核成员引用 | 删掉 `RegisterAsync` 调用后探针失败 |
| 2 | `Resolve<IAgentStore>()` 返回非 null，且 `InvokeAsync` 能提交记录 | 去掉插件注册后失败 |
| 3 | `Resolve<IToolDispatcher>()` 解析出的 dispatcher 走同一条审批路径 | 绕过解析直接 `new` 时，审批调用次数为 0 |
| 4 | **换一种 `IAgentStore` 实现（内存版），不改内核代码，行为测试仍通过** | ← 这是 P1 判定第 2 条，最关键 |
| 5 | 停用存储插件后，旧引用调用抛 `ObjectDisposedException` | 跳过排空直接清理时，旧引用仍可用 |
| 6 | 放弃枚举时后台回合被取消，租约释放（`WhenDrained` 在预算内完成） | 去掉取消传播后，停用会等到 `DrainTimeout` 超时隔离 |
| 7 | 停用落在两回合之间时，下一回合进入 `Blocked` 而非静默用旧实现 | 不做代次校验时，会继续用旧实例 |

**第 4 条是判定 P1 真正达成的唯一标准。** 前三条只证明"接上了"，
第 4 条证明"接上之后是可替换的"。

---

## 六、明确不做

- **不做插件热更新**（`architecture.md` §3.3 已定为契约）。
- **不新增工程**，接线代码放 `Dougong.Cli`。
- ~~不改默认路径~~ → 已于 2026-10-25 转正（全部验收通过后）。
- **不把 `IChatClient` 也做成插件**。它是宿主的模型连接配置，
  不是"运行能力"；强行插件化会让 `DOUGONG_PROVIDER_*` 那套配置无处安放。
  这一条留作后续判断，先记录为"有意不做"。
- **不动 spike 里既有的 27 个套件**，除新增 `-- kernel-wiring`。

---

## 七、风险

| 风险 | 应对 |
|---|---|
| `Channel` 中转导致事件乱序 | 单生产者单消费者，`Channel` 保序；验收断言事件顺序与旧路径一致 |
| 放弃枚举时租约泄漏 | 步骤 3 明确要求取消传播，验收第 6 条覆盖 |
| 插件激活顺序依赖 | 用 `PluginManifest.RequiredServices` 声明，让内核排序，不靠手工顺序 |
| 双路径并存导致"测试绿但产品走旧路" | 验收第 1、2 条都断言**编译产物**而非源码，且第 4 条在两条路径上各跑一遍 |
