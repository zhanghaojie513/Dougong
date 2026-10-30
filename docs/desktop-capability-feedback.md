# 桌面端能力需求反馈：SDK 侧回应

> **这份文档回应什么**：桌面端提交了一份「需要 SDK 提供的能力，按紧迫度排列」，
> 分三部分：真正缺失、已解决、不该进 SDK。
> 这里逐条核对源码后给出结论——**哪些判断成立、哪些需要修正理由、哪些我们已经决定不做**。
>
> 核对日期 2026-10-30，对应 SDK `0.1.10-alpha.1`。

---

## 一、真正缺失的三条

### 1. 当前上下文占用 token 数 —— **成立，且比原判断更严重**

**桌面端的描述是准确的**：

> `UsageLedger` 只在调用后累计消耗；`TokenBudget.Estimate` 只能拿到消息列表后估算，
> 而且工具声明和系统提示都算不准。估算值会与实际发送内容差很远，不拿它冒充。

核对源码后确认两点，其中第二点比桌面端说的更严重：

**① `UsageLedger` 确实与此事无关。**

它记的是 `InputTokens` / `OutputTokens` 的**调用累计量**——三轮对话后那个数可能是
实际上下文的三倍。拿它当占用率会一路顶到 100%。

**② SDK 自己也没有准确值，只有"够不够"的判定。**

压缩判据用的是：

```
effective = contextWindow - RequestOutputReserve - OverheadTokens
```

其中 `OverheadTokens` **是个固定常数 512**（`TokenBudget.DefaultOverheadTokens`），
用来笼统地覆盖"估算误差 + 工具声明 + 系统指令"。

这个 512 是**设计上的保守预留，不是实际开销的测量**。工具装到十几个时，
真实占用远超 512，而这个常数不会变。所以：

> **SDK 内部同样不知道"现在真实占了多少"，它只知道"够不够用"。**

这一点必须说清楚：不是"SDK 有准确值但没暴露"，而是**双方都没有准确值**。
所以这不是"补一个查询 API"，而是"要不要提供带明确误差说明的估算"。

**建议的做法**（待定，需要你确认要不要做）：

发一个每轮一次的事件，三个数同源：

```csharp
public sealed record ContextUsageReported(
    int EstimatedUsedTokens,   // Estimate(history) + OverheadTokens
    int BudgetTokens,          // EffectiveInput（窗口 − 请求预留 − 预留）
    int ContextWindowTokens,   // 模型原始窗口
    bool CompactionEnabled) : AgentEvent;
```

**为什么用事件而不是查询方法**：`Estimate` 是 O(n) 的字符统计，UI 帧率下反复调不可接受；
而让宿主自己缓存就退回到"宿主自己管状态"，那正是要避免的。

**为什么不叫它"真实占用"**：它就是估算。名字和文档都必须带这个限定，
否则下一个人会拿它去对账。

**在定这个之前，桌面端标注「未实现」是正确选择。** 用一个偏低的估算冒充占用率，
比不显示更糟——用户会以为还有余量。

---

### 2. 行级 diff —— **缺的能力成立，但理由需要修正**

**结论对，方向错。**

桌面端写：

> `FileVersionSet.Diff` 只给"文件长度变化：X（11 → 3）"这类变更描述，没有内容差异。
> 这是我的实现不够到位：应该用 `FileVersionSet`，而不是猜"哪些工具碰过路径"。

前半句**准确**——`Diff()` 返回的确实是一句话描述。后半句**不成立**。

**`FileVersionSet` 的设计目的不是给人看变更，是审批闸门。**

它判断的是"批准时看到的文件，执行时还是不是同一个"。看它的报错文案就明白：

```csharp
return $"文件从审批时存在变为不可见：{path}";
return $"出现了审批时未声明的文件：{path}";
return $"文件内容变化：{path}";
```

这些是**安全判定语句**。而且它的数据里**根本没有文件内容**：

```csharp
public sealed record FileVersion(
    string Path, long Length, DateTimeOffset LastWriteUtc, string ContentHash);
```

只有长度、时间与 SHA-256。**没有内容，做不了 diff**——这不是实现不够，
是这个类型从设计起就不承担这个职责。硬要它做，等于把审批闸门和 UI 视图耦合到一起。

**所以「应该用 FileVersionSet」这句要改。** 正确的拆法是：

| 需求 | 用哪个 | 状态 |
|---|---|---|
| 判断哪些文件被改动过 | `FileVersionSet`（比较 `ContentHash`） | **已有** |
| 展示改动前后内容 | 需要新的快照能力 | **没有** |

桌面端后半句「而不是猜哪些工具碰过路径」是对的——`FileVersionSet` 确实该用在这个用途上。

**真正缺的是什么**：文件改动前后的**内容快照**。两种可能的形态：

- **工具自己报告改动**：`WriteFileTool` 执行后把 `before`/`after` 内容附在结果里
- **执行前后各存一份**：dispatcher 在调用工具前后捕获目标文件内容

第一种更省，但要求每个写类工具都配合；第二种对工具透明，代价是每次写都要多读一次原文件。

**这一条需要设计，不是补个方法就行。** 而且它涉及"工具结果里放什么"这个已经冻结的契约，
改动面比第 1 条大。

---

### 3. NuGet 下载解包 —— **成立，且 SDK 已明确不做**

桌面端判断准确。SDK 里的原话（`DougongKernel.Loading.cs:71`）：

> **为什么不做 NuGet 下载**：下载解包是分发策略，不是内核能力。

这与 `goal.md` 的反目标一致，也与 dsh 的边界一致——它的插件安装属于产品层，
`packages/` 里没有下载器。

**建议桌面端自建**：拿 `nuget.org` 的 v3 flat container 拉 `.nupkg`（本质是 zip），
解到临时目录，再调 `LoadFromDirectoryAsync`。SDK 提供的那一步已经够了。

如果将来有第二个消费者（比如 CLI 也要装插件），再考虑抽一个 `Dougong.PackageFeed`
之类的包——**按"第三个消费者出现才抽包"的既有判据**，现在抽是死代码。

---

## 二、已解决的五条 —— 核对无误

| 能力 | 桌面端结论 | 核对 |
|---|---|---|
| `ReasoningDelta` 实时推理 | 0.1.1 起已有 | ✅ 确实在 `AgentEvent` 里 |
| 工具两段耗时 | `ApprovalElapsed` / `ExecutionElapsed` | ✅ 在 `ToolDispatchResult` 上 |
| 模型能力表 | `ProviderCatalog.FindModelCapabilities` | ✅ 公开，22 家 662 个模型 |
| 上下文三层回退 | `FallbackContextWindow = 262144` | ✅ 公开常量 |
| 计划工具 | SDK 自带 `TodoWriteTool` | ✅ 在 `SessionComposition.CreateDispatcher` 注册 |
| 插件重复加载幂等 | `PluginLoadStatus.AlreadyLoaded` | ✅ |

**一个补充提醒**：模型能力表在 0.1.10 里**语义有变**。
`ModelCapabilities.MaxOutputTokens` 现在是"模型输出容量上限"，可以是 1048576 与窗口同大，
**不再适合直接用作压缩预算的请求预留**。

桌面端现在用它做 `ReadMaxOutput` → `ClampMaxOutput(窗口一半)`。
如果已经在跑，暂时不出错；但升级到 0.1.10 后应该改成：

```csharp
// 请求预留用固定值，与模型容量无关
new ContextLimits
{
    ContextWindowTokens = contextWindowTokens,
    RequestOutputReserve = 4096,        // ← 不再是 MaxOutputTokens
}
```

`ClampMaxOutput` 也随之不再需要——容量不参与预算，就没有"夹"的理由。

---

## 三、不该进 SDK 的三条 —— 同意，理由补充

| 项 | 桌面端理由 | 补充 |
|---|---|---|
| PTY 终端 | 独立进程 + 独立契约 | 同意。它还要求**进程生命周期管理**，那是内核已有的职责但不是它的能力边界；塞进来会让内核同时管插件与终端进程 |
| 右栏文件树 | 是桌面端自己的 IPC | 同意。SDK 不定义 UI 数据形状 |
| uiPlugin 三段埋点 | 桌面端自埋 | 同意。SDK 只需知道记录形状——`TelemetryChecks` 已经钉住事件字段名 |

---

## 四、汇总

| # | 条目 | 结论 |
|---|---|---|
| 1 | 上下文占用 token | **成立**。但要说清：**SDK 也没有准确值**，只有带 512 粗略预留的估算。要不要暴露估算值待定 |
| 2 | 行级 diff | **缺 diff 成立**；“应该用 FileVersionSet”**不成立**——它是审批闸门，不存内容。真正缺的是内容快照，需要设计 |
| 3 | NuGet 下载 | **成立**，SDK 明确不做。桌面端自建，拿 flat container + `LoadFromDirectoryAsync` |
| 4-9 | 已解决五项 | 核对无误 |
| 10-12 | 不该进 SDK | 同意 |

**优先级建议**：第 1 条最值得做，但它需要先定义清楚"暴露的是估算而非实测"。
第 2 条改动面更大（涉及工具结果契约），建议单独立项。
第 3 条不需要 SDK 动。
