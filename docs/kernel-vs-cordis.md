# Dougong 内核 vs. dsh / Cordis 内核

> **这份文档的作用**：回答"我们的内核与 dsh 的差距有多大"，为内核接线决策
> 提供前置论证，并记录一条已定的取舍：**不做插件热更新**。
>
> **对标对象与版本**（一手证据，非二手描述）：
> - `deepseek-ai/deepseek-harness`，`master`，2026-10-23 推送；其 `docs/architecture.md` 明确
>   "powered by Cordis"。
> - `cordiverse/cordis`，`packages/core` **v4.0.0-rc.12**，commit `35fdc96`（2026-10-22）。
>   内核代码 `fiber.ts` 530 行、`reflect.ts` 313 行、`registry.ts` 214 行、`service.ts` 83 行。
>
> **这份文档的局限**：只读了 Cordis core 与 dsh 的架构/工具/持久化文档，未读 dsh 的
> 全部 52 个 package。凡是没读到源码的，下文标明"文档称"。

---

## 一、先看规模，避免自我美化

| | dsh | Dougong |
|---|---|---|
| 顶层 packages | 52（core / session / tools / llm / mcp / acp / sandbox / subagent / skill / plan / todo …） | 6 个工程 |
| 文档 | 543 篇 md，含中英双语与生成式子系统图谱 | 约 10 篇 |
| packages 下文件数 | 12 173 | 源码 12 051 行 |
| 定位 | 团队项目、developer preview、260k star | 单人起步项目 |

**Dougong 现在不是"对等的 .NET dsh"。** 它是"选对了架构方向、但在规模与成熟度上差两个数量级的
起步项目"。这个事实写在这里，是为了让后文的"优势"不被误读成整体领先。

---

## 二、两家的内核想解决的问题不同

这是理解一切差异的前提。

**Cordis 的核心问题是"依赖变化时，消费者怎么跟着重启"。**
它的 `_refresh()` 把依赖的每个服务 **uid** 拼成 epoch 字符串：

```typescript
_refresh() {
  let epoch = `${this._revision}`
  for (const name of Object.keys(this.inject)) {
    const impl = this._store[name]
    if (!impl) { epoch = INACTIVE; break }
    epoch += ':' + impl.uid
  }
  this._setEpoch(epoch)
}
```

epoch 变了就 `_unload()` → `_reload()`。**整个消费者插件重装一遍。** 这是 HMR 的基础。

**Dougong 的核心问题是"停用插件时，还在跑的调用怎么办"。**
`ServiceInvocationScope` 是一把按代次开的门：`TryEnter` / `Exit` / `Close`，
`Close()` 后新调用进不来，在途的跑完，`WhenDrained` 在计数归零时完成。

两个问题有交集，但不是同一个。所以下面的对比不是"谁更强"，而是"各自在哪一层发力"。

---

## 三、租约机制的精确差距

这是本文最需要说清的一节，因为提问时用的词是"内核租约机制"。

**结论先说：Cordis 没有租约（lease）这个抽象。**

核查方式与结果（`packages/core/src`）：
- `grep -rniE "inflight|in_flight|drain"` → **0 命中**。
- `grep -rniE "lease"` → **9 命中，但全部是 `release`**（`reflect.ts:59/230/233`、
  `fiber.ts:134/462/464/476/479/480`），即等待依赖方卸载时那个 `resolve` 回调，
  不是"每次调用取租约"的语义。**没有在途调用计数。**
- `grep -rniE "setTimeout|deadline|budget|AbortSignal"` → **0 命中**。即无任何超时预算。

Cordis 的等效物是 `fiber.ts:_unload()` 里的一段等待：

```typescript
// Dependents are notified when this fiber leaves ACTIVE, but may still
// access its services during their own teardown, so the effects of this
// fiber are only disposed after every dependent has unloaded.
while ([...this.provisions].some(impl => impl.dependents.size)) {
  await new Promise<void>((resolve) => this._release = resolve)
}
```

它等的是**依赖方 fiber 整体 unload 完成**，不是"某个调用返回"。

### 逐项对照

| 能力 | Cordis | Dougong | 差距性质 |
|---|---|---|---|
| 在途调用计数 | **无**。只有 `dependents`（依赖关系集合） | `ServiceInvocationScope._inFlight` | **我们是净增量** |
| 调用粒度隔离 | 无。引用是裸对象，停用后旧引用仍可用，靠消费者的 unload 兜住 | `InvokeAsync` 才接触实例；停用后抛 `ObjectDisposedException` | **我们是净增量** |
| 排空等待 | 有（`dependents.size` 循环） | 有（`WhenDrained`） | 等价 |
| 排空超时 | **无**（无界等待） | `DrainTimeout`（默认 30s）→ `Isolate` + 要求重启宿主 | **我们是净增量** |
| 分阶段预算 | **无** | 四个：`ActivationTimeout` / `CancellationTimeout` / `EventDrainTimeout` / `CleanupTimeout` | **我们是净增量** |
| 代次绑定 | 有（epoch 字符串，用于变化检测） | 有（`(provider, generation)` 元组，用于拒绝过期调用） | **用途不同**，见下 |
| 清理顺序 | 逆序（`disposables.splice(0).reverse()`） | 逆拓扑排序（消费者先于提供方） | **我们更精确**：能表达结构，不只是逆序 |
| 变化传播 | **有且强**（epoch 变化 → 消费者自动重装） | **无**（提供方变化 → 排空 → 未满足进 `Blocked`，消费者不自动重载） | **Cordis 是净增量**，见 §五 |
| 注册可逆性 | `ctx.effect()` 是唯一入口，返回的 disposable 自动挂到 fiber | `PluginContext.RegisterForCleanup` 显式登记 | **Cordis 更彻底**：插件无法忘记 |
| 服务健康检查 | `provide(name, value, check)`，`check` 返回 false 即视为不存在 | **无** | **Cordis 是净增量** |
| 生命周期状态数 | 6：`PENDING` / `LOADING` / `ACTIVE` / `FAILED` / `DISPOSED` / `UNLOADING` | 7：`Registered` / `Activating` / `Active` / `Deactivating` / `Stopped` / `Blocked` / `Failed`（`Failed` 下还有 `IsolationReason` 子态） | 我们状态更多，主要是把"依赖未满足"与"失败"分开 |
| 代码卸载 | 不需要（JS GC） | collectible ALC + 协作式卸载 | 平台差异，不算优劣 |

### 一句话量化

- **在"停用安全性"这一层，我们比 Cordis 多四样东西**：在途计数、调用期拒绝、排空超时、分阶段预算。
- **在"变化传播"这一层，Cordis 比我们多一样关键东西**：消费者自动重载。
- **共同的是**：排空等待、逆序/逆拓扑清理、代次绑定的基本思想。

所以差距**不在**"谁的租约更成熟"——Cordis 根本没有租约，无从比较。差距在
**"我们把资源边界做得更硬，它把变化传播做得更顺"**。

---

## 四、我们独有的两条（可辩护、可验收）

这一节只写能拿证据说话的优势。

### 4.1 未知副作用的第一类表达

dsh 在 `docs/subsystems/persistence.md:110` 写崩溃恢复：

> Repair is the reader's job: resume (agent-loop) reads the stored log through its write
> handle, computes `interruptedTurnClosers` — **missing tool errors**, any open `step/end`,
> and a synthetic `turn/end { reason: { kind: 'interrupted' } }` — and appends them…

关键：一个"调用已记录、结果没落盘"的工具，**dsh 补一条 tool error**。
`sessions.md:296` 佐证：`a tool/result may carry data.error only when its message has isError: true`。

也就是说，dsh 把"不知道副作用有没有发生"折叠成了"失败了"。

Dougong 的 `ToolCallOutcome.OutcomeUnknown` 是独立终态；`goal.md` P3 判定写明
「"不知道"必须能表达，不能与"没发生"混为一谈」；`-- store` 6 项测试守着恢复时不自动重放。

**这是真实差异。但要说清代价**：dsh 的选择有它的道理——长任务里更看重"会话能继续"，
补一条 error 最省事。我们单列未知，就得处理"未知之后怎么办"（目前要求宿主对账），复杂度更高。
**谁更好取决于你更怕哪种错：误报失败，还是重复副作用。**

### 4.2 生命周期全阶段超时预算

Cordis 的排空是无界等待（`while (dependents.size)` 没有超时）。
在 JS 生态这是可接受的工程取舍：插件基本自写自用，事件循环总会推进。

但 Dougong 的目标是第三方插件生态（M3），而 **.NET 的 ALC 卸载是协作式的**——
插件可能真的卡死，没有超时就是永久挂起。因此四个预算不是"做得更细"，是平台必需品。

---

## 五、我们的真实劣势

这一节同样只写能拿证据说话的。

### 5.1 变化传播自动化：我们没有

见 §二。Cordis 改一个插件，依赖它的全部自动重启；我们只把它们排空、未满足的进 `Blocked`。
**消费方不会被自动重载。**

### 5.2 扩展点密度：差一个量级

`dsh/architecture.md` 末尾"Where new behavior goes"表有 20+ 行，每行一个明确机制：
`ctx.llm` 加模型适配器、`ctx.tools` 加能力、`ctx.shell` 加执行后端、`ctx.fs` 加文件策略、
`ctx.sandbox` 限制子进程、`agent/*` 与 `tools/*` 事件拦截请求/工具/轮次、
`agent.inject()` 注入上下文、`SessionPersistence` 换存储后端……

它的工具管线是三段瀑布：`tools/pre-execute` → `tools/execute` → `tools/post-execute`，
外加 `finalizeContent` 与 `tools/result` 通知。

Dougong 只有 `IToolDispatcher` 一个执行入口和四个工具级回调。
"拦截模型请求""替换单条消息内容""policy 层改写工具结果"都没有对应位置。

### 5.3 会话日志的粒度

- dsh：`deriveMessages()` 从日志投影模型历史，隐含硬约束"**模型可见即已记录**"；
  `assistant/attempt` 保留失败的尝试但不进模型历史；有 v0→v1→v2 的格式迁移链，
  `session.vN.jsonl`，已提交的代次不可重命名/替换/删除。
- Dougong：append-only JSONL + 提交屏障，方向一致，但**没有格式版本迁移**，
  也没有"失败尝试留档"这一粒度。

---

## 六、已定取舍：不做插件热更新

**决定（2026-10-25）：明确不做插件热更新。把"插件替换后需要重启宿主"写进契约。**

理由：

1. **ALC 卸载在 .NET 里是协作式的，热重载天生比 JS 贵。** Cordis 的 `_unload()` 只需
   等 dependents 清完再 `_reload()`；.NET 要 `Unload()` collectible ALC、等 GC 回收、
   确保所有引用（工厂、服务类型键、事件/任务/诊断缓存、动态生成的委托）都已释放。
   我们在 M0 已经为"卸载后引用未释放导致程序集回收失败"付过一次代价（`KernelUnloadReferenceTests`）。
   把这件事做成"每次改插件都发生"，会得到一个又慢又不稳的机制。
2. **强行对标会得到半成品。** `goal.md` 的反目标清单里没有"热更新"，但有一条精神相通的原则：
   「用"代码量小"作为正确性的理由」不行——反过来，为了"功能对等"降低可靠性也不行。
3. **收益不明确。** 热更新的主要受益者是插件作者迭代期。M3 之前不存在第三方作者；
   M3 时若真需要，更合理的形态是"宿主重启很快"（会话持久化已经保证恢复），而不是进程内重载。

**这个决定排除了什么、保留了什么**：

| 场景 | 行为 |
|---|---|
| 提供方新增 | 需求重评估，`Blocked` 的消费者被唤醒（**已实现**） |
| 提供方移除 | 消费者逆拓扑排空 → 未满足进 `Blocked`（**已实现**） |
| 提供方被替换（同名新代次） | 消费者排空 → 未满足进 `Blocked`；**需要宿主重新激活消费者**（明确不自动） |
| 计划要实现的"接线"（2.6） | 不引入 epoch 字符串，不引入自动重装 |

**写进用户可见契约的表述**：更换可替换实现的插件后，宿主需要重新激活依赖它的插件；
Dougong 不提供进程内热重载。

**同时保留从 Cordis 学到的两条**（这两条不需要热更新也能生效）：

1. **注册应当是唯一的可逆入口**。现在 `RegisterForCleanup` 靠插件自觉调用；
   接线时应当让服务注册与事件订阅**自动**登记清理，减少"忘记登记导致泄漏"。
2. **服务级健康检查**（`provide(name, value, check)`）。这不是热更新，是一个很便宜的中间态：
   服务还在，但当前不可用。我们的 `ToolPermission.IsKnown` 只是工具自述，不是服务健康。
   记为候选，等出现真实需求再引入。

---

## 七、给接线方案（2.6）的直接输入

1. **保留**在途计数、四阶段预算、代次绑定、隔离——这些是 P3 的必需品，不因"对标"而删。
2. **不引入** epoch 字符串。我们的代次 + 拓扑排序能表达同样的事，且能看出结构
   （epoch 只是一个等式，依赖多了以后无法调试）。
3. **不实现**自动重载，按 §六 写进契约。
4. **必须补**的叫"调用方检查"（只看实现不看调用点，是常见的检查盲区）：
   断言产品程序集中真的存在对内核入口的调用点，避免 G14 重现。
5. 开工前仍需定"租约粒度"：整轮一租约 vs 按回合/工具分租约。
   §三 的对比不解决这个问题——Cordis 没有对应约束，无从参照。

---

## 八、定位表述（替代"对等的 .NET dsh"）

不追求功能对等，因为"对等"无法判定。可判定且可辩护的表述是：

> **Dougong 把"插件化 Agent Harness"在 .NET 上做到可判定正确：在 dsh 折叠掉的地方
> （未知副作用、生命周期超时）给出更严格的语义，在 dsh 强的地方（变化传播、扩展点密度）
> 明确承认差距并说明取舍。**

这个表述的每一句都有验收方式，也都保留了差距的可见性。
