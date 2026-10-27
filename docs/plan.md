# 开发计划（防跑偏用）

> **这是什么**：唯一一份排期性文档。它回答"下一步做什么、为什么是这个顺序"。
> **这不是什么**：不是目标（见 [goal.md](goal.md)）、不是完成定义（见 [quality-gates.md](quality-gates.md)）、
> 不是设计（见 [architecture.md](architecture.md) / [design.md](design.md)）、
> 不是台账（见 [status.md](status.md)）。
>
> **文档闭环**：
>
> ```text
> goal.md          为什么做、什么算达成   ← 判定标准
>   ↓ 每项工作都要对应一条性质
> plan.md          按什么顺序做、还差什么 ← 本文
>   ↓ 每片交付都要过门
> quality-gates.md 六道门与红线
>   ↓ 结果如实记录
> status.md        实际做到哪一步（附证据）
>   ↓ 过程中的判断错误
> journey/         复盘素材，反哺门的加严
> ```
>
> **维护规则**：每完成一项更新本文标记；发现新缺口先登记 §5，并标注它影响
> [goal.md](goal.md) 的哪条性质。本文与 status.md 冲突时，以实测证据为准并回来修正本文。

## 零、本文每项的书写要求

每一项必须写清三件事，缺一不可：

1. **对应哪条性质**（P1 可组合 / P2 生命周期 / P3 故障可解释 / P4 真实可用）
2. **失败证据是什么**（门 1 要求；写不出说明还没想清问题）
3. **从哪个入口触达**（门 3 要求；写不出说明会重演 G1）

答不上第 1 条的，先问"它推进了什么"；答不上第 3 条的，不要开始。

---

## 一、当前真实位置（2026-10-27 核准）

| 里程碑 | 状态 | 证据 |
|---|---|---|
| M0 内核 | **已关闭** | 三轮审核问题全部修复，13 项独立探测通过 |
| M1 入口与执行契约 | **已关闭** | 截止 M1 的缺口全部修完（含 G1/G2/G3/G4/G6/G8） |
| M2 协议与集成 | **已关闭** | MCP / ACP / 指标 / 受限记忆（L1 记忆含效果对照）全部完成 |
| M3 生态与稳定 | **已关闭** | 四条判据全部达成，见 [m3-plan.md](m3-plan.md) §九 |
| **I0 稳定化与发布** | **进行中（退出条件两个平台都已验证）** | 诊断/日志/故障排查/兼容策略/安装实测/嵌入指南/单文件发布/Linux CI/Linux 实机全流程已完成；剩 ACP Registry 上架（被 `0.x` 不承诺兼容卡住）与配置迁移（已判定不做） |

**这份表在 2026-10-27 更新**：此前停在 2026-10-24，把 M2/M3 记为"未开始"，
而它们早已关闭。台账类的表格必须与原文档（[status.md](status.md)）对齐，
否则这份"防跑偏"的导航文档自己就会把人带偏。

**核准方式**：不采信文档描述，逐个 grep 代码确认。M1 清单里勾选的项都验证过对应实现存在；
未勾选的项验证过确实缺失。下面 §2 的两个缺口是这次核准发现的，其中第 1 项
status.md 的记录不够准确（写了"未实现真实模型摘要"，但没写"未接入循环"）。

---

## 二、M1 剩余缺口（**全部已修完**，保留作复盘）

> **2026-10-27 更新**：本节列的 G1 / G2 / G3 / G8 全部已修复并通过验收。
> 内容保留不删——它记录了"能力只在测试里"这类缺陷当时长什么样、怎么发现的，
> 是 [quality-gates.md](quality-gates.md) 门 4 的真实案例来源。
> 当前缺口状态以 [§五 缺口登记簿](#五缺口登记簿) 为准。

### G1. 上下文压缩从未接入生产路径（阻断级）— **已修复**

**事实**：`IContextCompactor`、`SlidingWindowCompactor`、`SummarizingCompactionTask`、
`CompactionApply` 全部实现并有 9 项自检通过。但：

- `LoopAgentRuntime` 的构造函数只有 `(client, dispatcher, store?, ...)`，**没有压缩器参数**
- CLI 的 `messages` 列表只增不减（`Program.cs:167` 起）
- 全仓库除测试外，没有任何代码调用 `Plan()` / `AdvanceAsync()` / `TryApply()`

**后果**：长会话把完整历史一路送进 `history`（`LoopAgentRuntime.cs:41-43`），
直到撞上 provider 的上下文上限才失败。压缩能力只存在于测试里。

**为什么是阻断级**：M1 的验收目标是"能真实跑编码任务"。短任务能过，长任务必挂，
而失败形式是 provider 报上下文超限——使用者会以为是模型限制，不是产品缺陷。

**修法方向**：
1. `LoopAgentRuntime` 接受可选 `IContextCompactor` + 预算参数，在每轮构造 `history` 前
   检查并压缩
2. 用真实 `UsageDetails.InputTokenCount`（已由 `UsageRecordingChatClient` 采集）校准
   那个"2 字符/token"的粗略估算——这是唯一能把估算变成实测的通道
3. 压缩失败必须保留原历史并继续（不覆盖、不中断），预算仍不足时明确暂停

**验收**：探针先证明"长会话确实会撞上限"，再验证压缩后不撞；压缩失败的降级路径有反证。

**完成情况（2026-10-24）**：
- 探针先确认缺口：10 200 tokens 的完整历史全部送进模型，构造参数里既无预算也无压缩器
- 接入：`LoopAgentRuntime` 新增可选 `ContextLimits` + `IContextCompactor` + `summarize`；
  每轮调用模型前检查一次；压缩只处理进入循环前的基线历史，循环内追加的交互不参与
  （它们必须成对保留）
- 降级：摘要失败、无摘要器、预算不可行、应用失败——四种情形都保留原历史并继续运行，
  并各自发出带诊断的 `ContextCompacted` 事件
- 真实端点验证（DeepSeek）：12 轮长会话，估算从 9 418 压缩到 3 445 tokens，19 次调用 0 失败
- 反证：移除接入点后 4 项检查立刻失败
- **G8（token 估算非真实计数）同时缓解**：`UsageRecord.InputTokens` 已可对照校准，
  但换算因子尚未回填为默认值，仍按 2 字符/token 保守估算

### G2. 审批决定没有持久化（中）— **已修复**

**事实**：`StoreRecordKind.ApprovalDecision` 枚举值存在，但全仓库使用点 **0 处**。
`RunFinished` 同样 0 处。

**后果**：恢复会话时无法回答"这个调用当初经过审批了吗"。`OutcomeUnknown` 对账
只知调用了、不知授权情况，对账信息缺一半。

**修法方向**：`ToolDispatcher` 取得审批决定后提交一条 `ApprovalDecision` 记录；
`RunFinished` 在每次运行终止时提交。两者都要在既有四个提交屏障的框架内。

**验收**：崩溃恢复后能列出"哪些调用有审批记录、哪些没有"。

**完成情况（2026-10-24）**：
- `ToolDispatcher` 新增可选 `IAgentStore`；审批后立即提交 `ApprovalDecision`，
  记录绑定 invocationId / 工具身份与版本 / 参数摘要 / 工作区 / 决定时刻
- 提交失败阻止执行并返回 `Failed`：否则"已批准"未被持久化而副作用已发生
- 新增 `ApprovalLog` + `ApprovalSnapshot`：`IsUnaccounted` 让"有调用但无审批记录"
  可表达，不与"被拒绝"混淆
- **G3 一并修复**：`RunFinished` 收口到 `FinishAsync` 单一出口（原先七处 `yield break`
  逐处插入容易漏），提交失败不改变已发生的事实，只并入诊断
- 7 项自检（`-- approval-log`，含两个反证）；真实端点日志确认六段记录齐全：
  UserInput → ModelResponse → ToolCallStarted → ApprovalDecision → ToolCallCompleted → RunFinished

---

## 三、后续里程碑

### M2 协议与集成（G1/G2 之后）

按价值排序，不按文档章节顺序：

| 顺序 | 事项 | 为什么是这个位置 |
|---|---|---|
| 2.1 | **MCP 工具桥接**（P4，兼 P1）— **已完成** | 见 `status.md` M2 段；`-- mcp` 8 项 |
| 2.2 | MCP 变化检测（P3）— **已完成** | `-- mcp-reload` 4 项；含反证 4/4 |
| 2.3 | 指标导出（P1/P4）— **已完成** | `-- telemetry` 5 项；BCL Meter，不直接依赖 OTel 包 |
| 2.4 | ACP Agent 端 stdio（P1/P4）— **已完成（含审批、恢复、入口对齐）** | `-- acp` 20 项 + `-- parity` 5 项 + `-- surface` 11 项 + 真实进程验证 |
| 2.5 | ACP elicitation（P4）— **已完成** | `-- elicitation` 10 项 + `-- elicitation-process` 1 项；5 条反证 |

**2.6 内核接线（P1）— 已完成（默认启用）**

> **2026-10-27 更新**：已完成。[g14-kernel-wiring-plan.md](g14-kernel-wiring-plan.md)
> 里的租约粒度取舍（按回合分租约）落地为 `KernelAgentHost` / `KernelAgentRuntime`；
> 内核接线现在是**默认路径**，`DOUGONG_KERNEL=0` 可退回直连做对照。
> 验收：`-- kernel-wiring` 10 项 + `-- kernel-path` 3 项 + `-- kernel-process` 5 项
> + `-- replaceability` 4 项 + `-- bare-hold`（12 处持有全部说明）。
> 以下原文保留，说明当时的问题长什么样。

这是 2026-10-25 实测发现的结构问题，不是新功能：**`DougongKernel` 在产品路径上零引用**。
`Dougong.Agent` 只在注释里提到内核；`Dougong.Cli` 的 csproj 引用了内核但源码不用——
装配是 `new ToolDispatcher(...)` / `new LoopAgentRuntime(...)` / `new AcpAgentHost(...)`。
因此 `design.md:113` 定义的"一切皆插件"（运行时、存储、审批器均由内核承载、组合根解析
`IAgentRuntime`）在代码里不存在，内核的调用租约与排空语义在真实会话中从未生效。

**为什么它必须排在 M3 之前**：M3 是"API 稳定 + 第三方插件作者"，而第三方插件要挂载的
正是内核的服务注册面。接线不先做，M3 稳定下来的 API 是一套没人用的 API。

**开工前必须先定的一条**（否则接线会卡住）：`IServiceReference.InvokeAsync` 的契约要求
"委托必须等待全部工作结束"。一轮对话是长时间流式的，若整个 `RunAsync` 包在一次
`InvokeAsync` 里，停用就要等当前轮跑完（或等满 `DrainTimeout` 默认 30s 后隔离）。
两个选项：
- **整轮一租约**：实现简单，代价是"流式输出中停用插件"要等这一轮结束；
- **按回合/工具分租约**：`architecture.md:278` 对工具调用已经是短租约
  （"取得租约后 `DescribePermission`/`DescribeExecution`，执行前重新取得同一代次的调用租约"），
  把它推广到运行入口，则停用只需等一次模型调用或一次工具执行。

倾向后者，但要在方案里写清"哪个粒度对应哪条可观察行为"，不能只说"更快"。

**已定取舍（2026-10-25）**：不做插件热更新，见 `architecture.md` §3.3 与
[kernel-vs-cordis.md](kernel-vs-cordis.md) §六。接线时不引入 epoch 式自动重载；
提供方被替换后由宿主重新激活消费者。同时保留从 Cordis 学到的两条：
注册应成为唯一可逆入口（服务注册与事件订阅自动登记清理），以及服务级健康检查作为候选。

**对标依据**：[kernel-vs-cordis.md](kernel-vs-cordis.md) 用一手源码（Cordis core v4.0.0-rc.12）
与 dsh 架构文档对比，结论是——在停用安全性上我们多四样（在途计数、调用期拒绝、排空超时、
分阶段预算），在变化传播上 Cordis 强（消费者自动重载），并把"未知副作用"与"扩展点密度"
两处差距如实记录。租约机制本身无可比性：Cordis 没有租约抽象。


**2.5 为什么排在 2.4 之后**：它需要 2.4 的双向请求通道（Agent 主动向客户端发请求并
等响应）已经可靠。也正是它比 `plan` 先做的原因——`elicitation` 缺的是**通道**（已有），
`plan` 缺的是**数据源**（运行时没有计划状态）。

**elicitation 的三条硬约束**（来自同类实现的事故记录，不可简化）：
- 客户端报错/传输失败/本轮取消，**不得**报成"用户拒绝"
- 形状不合法时不发请求，不浪费用户一次点击
- 只声明 `form`，不声明 `url`（URL 模式的安全要求我们无法验证）

**MCP 优先于 ACP 的理由**：MCP 扩展"能做什么"，ACP 扩展"怎么接入"。后者依赖前者有内容可暴露。

**MCP 的硬约束**（来自架构 §5.2，不可简化）：
- server 的 `annotations` 仅作提示，**不是**可信授权依据
- 未知工具默认不能自动批准，不以整个 server 的低风险默认值覆盖破坏性工具
- 连接断开或取消后不假定远端操作已撤销，缺结果调用按 `OutcomeUnknown` 处理

### M3 生态与稳定

**计划已成文**：[m3-plan.md](m3-plan.md)。**已关闭（2026-10-27）**，四条判据全部达成，
出口记录见其 §九。

一句话目标：一位没参与开发的人，能照着文档写出一个可信插件、跑起来、
并知道它什么时候会失败。M3 不加功能，只把已有能力封成别人不会用错的形状。

决策已完成：**不补 `Experimental` 标记**，改做实 ApiCompat + CHANGELOG + 弃用窗口。
原 `design.md:249` 那条"公共 API 在 M3 前保持 experimental"从未执行（实测 0 处），
已改为诚实表述。理由见 m3-plan.md §五——该标记产生编译错误而非警告，
与"外部作者照文档能写出插件"直接冲突。

M3 之前不启动 `vision/iteration-roadmap.md` 里的 I1–I6。

---

## 四、贯穿全程的纪律

这些不是"某阶段的任务"，是每一片都要遵守的：

1. **先证伪再动手**。改之前先写一个能失败的探针。没有失败证据的改动，写完也不知道
   是否解决了问题。G1 尤其如此——"压缩接进去了"和"长会话不再失败"是两件事。
2. **反证必须重建后再跑**。本项目已因此浪费过至少四次排查：`spike` 不在 solution 里，
   全量构建不重建它，`--no-build` 跑的是旧 DLL。
3. **测试全绿不等于验收**。M0/M1 各有实例：假模型全绿但 4 个协议缺陷；
   兜底逻辑掩盖主路径死亡。
4. **真实 provider 是协议层的唯一裁判**。假模型只能验证控制流。
5. **文档与实测不符时改文档**。G1 的记录不准确就是教训。
6. **不宣称未验证的能力**。`status.md` 每一项必须附证据位置。
7. **破坏性 API 变更先给方案**。评审不等于修改授权。

---

## 五、缺口登记簿

发现即登记，不要在对话里丢失。修完标记并注明提交哈希。

| ID | 缺口 | 性质 | 严重度 | 状态 | 证据 / 触发条件 |
|---|---|---|---|---|---|
| G1 | 上下文压缩未接入循环 | P4 | 阻断 | **已修** | `-- wiring` 9 项；真实端点 9418→3445 tokens |
| G2 | 审批决定未持久化 | P3 | 中 | **已修** | `-- approval-log` 7 项；真实日志含 ApprovalDecision |
| G3 | `RunFinished` 未持久化 | P3 | 低 | **已修** | 统一收口 `FinishAsync` |
| G4 | 完整 JSON Schema 校验 | P2 | 中 | **已修** | `-- schema` 8 项；触发条件已在 M2.1 满足（schema 来自不可信远端） |
| G5 | 资源级读写键 | P2 | 低 | **已修** | `ToolExecutionPlan.Reads`/`Writes` + 读写锁：读-读并行、读-写与写-写互斥、不同资源互不阻塞。`read_file`/`write_file` 已声明真实键（规范化路径）。`-- workspace-lock` 8 项，反证确认写工具退回旧语义时精确失败 |
| G6 | 工具替换语义未测 | P3 | 低 | **已修** | `-- tool-replacement` 4 项；反证确认版本不随 schema 变化时检查失败 |
| G16 | `ConnectMcpAsync` 传输构造不可注入 | P1 | 低 | **已修** | `McpTransportKind` 显式表达形态（不再靠"哪个字段非空"推断）；`ConnectMcpAsync` 接受可选 `transportFactory`，第三方扩展传输**不必改本方法**。`-- surface` 三条：Kind 正确、工厂返回值真被使用（用带特征串的失败传输验证）、无工厂时走内置构造 |
| G7 | 断电级持久化 | P3 | 低 | **设计限制（声明已落地）** | ① 能力：每次提交调用 `Flush(flushToDisk: true)`（BCL 文档语义：清空所有中间文件缓冲）。② **声明已有消费者**（2026-10-25 修）：此前 `StoreDurability` 在产品代码里零消费——声明了却没人读、没人展示，等于没声明。现在 CLI 启动横幅展示等级、`DurabilityText` 三档表述互不相同且明确"不保证断电"，ACP 降级时说明"本次会话不会被保存"。③ 仍不宣称断电安全：需要掉电测试，本项目没有条件。**这是设计限制不是缺陷** |
| G8 | token 估算非真实计数 | P4 | 中 | **已修** | `-- token` 7 项；实测校准（ASCII 8 字符/token，CJK 1.2），改用分段模型 |
| G9 | 同目录多会话并行 | P1 | 低 | **已修** | 锁表改为**进程级共享** + 引用计数回收：此前每个会话各 new 一个 gate，同工作区的两个会话各持一把互不相干的锁，跨会话互斥不成立。`-- workspace-lock` 3 项（共享互斥/跨工作区不阻塞/锁表可回收），反证确认无回收时表从 3 涨到 23 |
| G10 | 工具幂等键 | P3 | 低 | **已修（可解部分）** | 原登记说"缺幂等键"，但真问题更基础：未知结果无法区分「可安全重试」与「必须人工核实」，于是全部拒绝恢复——一次只读工具崩溃让整个会话报废。已修：`ToolDescriptor.MayHaveIrreversibleEffects`（默认 false）+ 恢复时据此分流出 `RecoveredSession.Retryable`。`-- store` 两条验收（只读不阻塞 / 有副作用阻塞）+ 反证。**幂等键本身仍缺**：需要远端工具配合识别重复请求，触发条件不变 |
| G11 | ACP `plan` 无数据源 | P4 | 低 | **已修** | 数据源是**工具**不是推测：`todo_write` 让模型显式声明计划，运行时投影为 `PlanUpdated`，ACP 翻译为全量替换的 plan 条目。`-- acp-plan` 6 项，反证确认去掉投影后两条精确失败 |
| G12 | 思考字段名硬编码 | P4/P3 | 中 | **已修** | `AgentPropertyKeys.Reasoning`（中性键）+ `WireCompat.ReasoningField`（方言开关）。Agent 层不再出现任何 provider 字段名；连接器负责翻译。`-- wire` 三条：默认方言、覆盖方言、方言隔离（防"两个字段都读"）。反证：编码写死旧字段名时精确失败 |
| G13 | 输出上限不可配置 | P4 | 低 | **已修** | `WireCompat.MaxTokensField`（null=不发，保持已验证端点行为）+ `ChatOptions.MaxOutputTokens`。Anthropic format 改为 `options?.MaxOutputTokens ?? 4096`。`-- wire` 的 `max-output-tokens-is-configurable` 三条 |
| G14 | **内核未接入产品路径** | P1 | **阻断** | **已修（默认启用）** | `-- kernel-wiring` 10 项 + `-- kernel-path` 3 项 + `-- kernel-process` 5 项 + `-- replaceability` 4 项 + `-- bare-hold`；9 条反证。`DOUGONG_KERNEL=0` 可退回直连对照 |
| G15 | 编排者持有可替换实现（审批器/dispatcher） | P3/P1 | 中 | **已修** | `IApproverAccessor` / `IDispatcherAccessor`：`ToolDispatcher` 与 `LoopAgentRuntime` 不再持有实现实例。内核路径传 `KernelApproverAccessor`/`KernelDispatcherAccessor`（走租约），直连路径传 `Fixed*`（显式固定）。`-- bare-hold` 12 处持有全部有说明，两条已知缺口条目已从白名单消失 |

**G12/G13 为什么不是"自研 provider 矩阵"**：`goal.md` §3 把 provider 矩阵列为反目标，
但那条针对的是"重造 LLM 抽象与 42 家厂商目录"。G12/G13 是**同一协议内部的字段名差异**，
量级与已有的 `MessagesPath` 相同（那个字段的存在理由就是"DeepSeek 的 Anthropic 兼容层
挂在 `/anthropic/v1/messages`"）。修法因此也是同形的：`ChatProviderConfig` 加一两个
带默认值的字段，默认保持现有行为，不预设没见过的开关。
**现在不做的理由**：没有具体 provider 要接时，加字段就是死代码——与 §4.4 拒绝
"预先冻结资源键格式"是同一条判据。

**G14（阻断）的完整记录**——这是 2026-10-25 实测发现的，此前被文档描述成了别的问题：

| 项 | 实测事实 |
|---|---|
| 证据 | `grep -rn "Dougong.Kernel\|DougongKernel\|IPlugin\|PluginContext\|IResolver" src/Dougong.Agent/ src/Dougong.Cli/ --include=*.cs` → **零命中**（Agent 层仅在 `IToolApprover.cs` 注释里出现 "Kernel" 一词） |
| 引用 | `Dougong.Cli.csproj` 引用了 `Dougong.Kernel` 工程，但源码不用；`Dougong.Agent.csproj` 也引用，同样不用 |
| 真实装配 | `SessionComposition.cs:91` `new ToolDispatcher(...)`、`:123`/`:125` `new LoopAgentRuntime(...)`、`Program.cs:424` `new AcpAgentHost(...)` |
| 后果 | 内核的 `IServiceReference` 租约、代次绑定、排空、`Blocked` 重评估、ALC 装卸，在**真实会话中一次都没发生过**。177 项内核测试验证的是机制本身正确 |
| 与文档的冲突 | `design.md:113` 写明"Agent 运行实现由内核承载，组合根解析 `IAgentRuntime`"；`architecture.md:208` 写明"运行入口解析及工具调用均取得作用域租约，宿主不得永久缓存可替换实现"。两条均未实现 |
| 完成判据 | `goal.md` P1 第一条：存在至少一种非内核提供的能力，能在**不修改内核代码**的前提下替换为另一种实现，且行为测试仍通过。目前连解析路径都不存在 |

**为什么这条被漏掉这么久**：每次做协议或运行时切片，改动都落在 `Dougong.Agent` 这条线上，
内核另有一条完整且自洽的测试线。两条线各自全绿，没有任何检查项问过"内核被用上了吗"。
门 3（生产路径可达性）本应拦住它——`-- reachability` 检查的是能力是否出现在生产类型的公开面上，
而内核的公开面确实存在，只是没有调用者。**这是门 3 的一个盲区，已记入 `quality-gates.md`。**

**"触发条件"取代"什么时候修"**：其余缺口都不阻断当前目标，等真实需求出现再引入，
不预先冻结格式——与 architecture.md §4.4 的立场一致。写下触发条件是为了将来
能判定"现在该做了"，而不是靠感觉。

**已修的 G4/G6/G8 说明**（2026-10-24）：三者的触发条件都已由 M2 满足——
M2.1 接入 MCP 让 schema 成为不可信输入（G4），
M2.2 的热更新让"批准了 A、执行了 B"成为现实风险（G6），
真实 usage 已采集到足够样本可校准估算（G8）。
**这验证了"触发条件"这种写法的价值**：不是拖延，而是有判据地决定何时动手。

**剩余缺口状态（2026-10-25）**：15 项中 14 项已修或部分修复，
**唯一剩余的 G7 是设计限制而非缺陷**——代码已调用 `Flush(flushToDisk: true)`，
缺的是掉电测试条件；不宣称未验证的能力（见其行内说明）。
G10 的"幂等键"部分仍需远端工具配合，但它原本最要命的缺口（对账时不知道参数）
已修。
**当前没有一项"会在正常使用中静默出错"的缺口。**

---

## 六、下一个具体动作

**M0–M3 全部关闭。当前在 I0 稳定化与发布。**

### 已完成（I0）

| 项 | 证据 |
|---|---|
| 启动自检 `--doctor` | `-- doctor` 9 项 + 反证 8 条 |
| 分级日志 `DOUGONG_LOG` | 4 级；不设时行为与加它之前完全一致（有反证） |
| 可复现信息包 `--report` | `-- report` 6 项；不含密钥与对话内容（有反证） |
| 故障排查文档 | [troubleshooting.md](troubleshooting.md) |
| 兼容策略 | [compat-policy.md](compat-policy.md)；ApiCompat 覆盖 5 个包 |
| `dotnet tool` 安装 | 真装实测：依赖自带、项目外可运行、卸载干净；`--help` 已补 |
| 嵌入指南 | [embedding-guide.md](embedding-guide.md)；代码由 `samples/embedding-probe` 保活 |
| 性能基线 | [performance-baseline.md](performance-baseline.md)，只记录不承诺 |
| **单文件发布** | `samples/singlefile-probe` 探针；`all-checks.sh` 每次发布并验证"单文件里能加载/卸载插件"。**过程中修掉一个真实缺陷**，见下 |
| **Linux CI** | `build.yml` 改为 `windows-latest` + `ubuntu-latest` matrix，两个平台跑同一套 `all-checks.sh` |
| **Linux 实机全流程** | WSL2 Ubuntu 24.04 上按 [getting-started.md](getting-started.md) 走完全程：构建 → 187 测试 → 43 套件 + 单文件探针 → `--doctor` → **真实 provider 对话成功** → 建插件/编译/加载/卸载。见下 |

### Linux 验证时发现的跨平台缺陷（2026-10-27）

**11 个 `scripts/*.sh` 以 CRLF 行结束符入库，且仓库没有 `.gitattributes`。**

Linux 下 `set -euo pipefail` 被读成 `set -euo pipefail\r`，报：

```
scripts/all-checks.sh: line 7: set: pipefail: invalid option name
```

整个自检脚本在 Linux 上一行都跑不了。

**为什么 CI 没抓到**：`actions/checkout` 默认会把工作区转成 LF，
所以 ubuntu runner 上一切正常。**真实用户从 Windows 检出的副本才踩得到**——
而这正是 I0 退出条件所说的"干净环境"。

**修法**：新增 `.gitattributes`，强制 `*.sh`/`*.py` 为 LF（`*.cmd`/`*.ps1` 为 CRLF），
并把 11 个脚本转成 LF。

**顺带确认的一件好事**：文档里插件工程用 `..\..\src\...` 这种反斜杠路径，
在 Linux 上**照样能用**——MSBuild 内部会把 `\` 当分隔符解析。实测编译通过，
所以文档不需要为不同平台写两套路径。

### 单文件发布时发现的内核缺陷（2026-10-27）

`dotnet publish -p:PublishSingleFile=true` **根本无法编译**：

```
error IL3000: 'Assembly.Location' always returns an empty string for
              assemblies embedded in a single-file app.
```

`PluginLoadContext.IsUnderDotnetRoot` 用 `typeof(object).Assembly.Location` 推共享框架目录。
单文件下该属性恒为空，`Path.GetDirectoryName(null)` 返回 null。
`TreatWarningsAsErrors` 把 IL3000 提升为 error，因此**发布直接失败**。

**修法**：把"可能为空"显式写进逻辑（空则不能由此判定），并在两处加 `#pragma warning disable IL3000`
附上理由——这里的用途正是观察它是否为空，分析器无法表达这个意图。

**如实记录两件没做到的事**：

1. 我一开始以为这会让运行期出错（框架程序集被误判成插件依赖）。**实际不是**：
   原代码 `Path.GetDirectoryName("")` 已返回 null 并跳过，行为与修后等价。
   修后的代码只是把这一点写明。**真正的问题只有编译失败这一条。**
2. 我两次尝试用"未登记契约必须加载失败"来反证这个修复，**两次都没能变红**——
   那个场景在装配解析阶段就失败了，走不到 `IsFrameworkAssembly`。
   最后改成验证"单文件里能加载→激活→卸载→程序集可回收"，并用"让 UnloadAsync 提前返回"
   做反证，得到 `卸载后剩余插件: 1` → FAIL。**这才是有证据的那一条。**

### 剩下的一项：ACP Registry 上架（2026-10-27 查证：三个硬阻碍）

上一版这里写的是"被 `0.x` 不承诺兼容卡住"。**查了 Registry 的实际规则后，
这个说法是错的**——版本号根本不是主要障碍。真实情况是三条，每条都要先解决：

| # | 阻碍 | 证据 | 能否绕过 |
|---|---|---|---|
| 1 | **仓库是私有的** | `gh repo view` → `isPrivate: true`；`https://github.com/zhanghaojie513/Dougong` 返回 404。而 `agent.json` 的 `repository` 是**必填字段**，PR 也是公开提交 | 不能。要么先开源，要么不上架 |
| 2 | **认证方式不符合** | Registry 的 `AUTHENTICATION.md` 明确：**只接受 Agent Auth（OAuth）或 Terminal Auth**，并写明"AUTH spec 还定义了 Environment Variable Auth，但本 registry 目前只支持 Agent Auth 与 Terminal Auth"。Dougong 只有环境变量认证（`DOUGONG_PROVIDER_KEY`），`AcpAgentHost.cs:303` 的 `authMethods` 是空数组 | 不能。需要真的实现 OAuth 或终端交互认证 |
| 3 | **分发形态不在白名单** | `agent.schema.json` 的 `distribution` 只允许 `binary` / `npx` / `uvx`，`additionalProperties: false`。**没有 `dotnet tool`** | 可以。走 `binary`：`dotnet publish` 出自包含单文件的 tar.gz/zip，附 `cmd` 与 `args`。这条路今天刚被打通（见上节单文件发布） |

**阻碍 1 还有个连带问题**：仓库私有期间就算 PR 被接受，Registry 的 CI 也拉不到
`repository` 地址、客户端也装不了 `binary` 的下载链接。所以**必须开源在前**。

#### 所以正确的顺序是

1. **先开源**。`status.md` 里已定的触发条件是"首个包发到 nuget.org"或"M3 插件 API 稳定化"，
   两者取先到者——**M3 已经关闭，这条其实已满足**。这是一个可以现在做的决定。
2. **再实现认证**。这是真正的工作量，不是配置。ACP 的 `authenticate` 流程 + 一个
   OAuth 或终端交互实现。要注意：Dougong 是"带自己的 provider key"的 harness，
   让每个用户走 OAuth 登录某个厂商，与它"用户自带 key"的定位有冲突——
   更合适的是 **Terminal Auth**（引导用户在终端里填 key 并落盘到受控位置），
   但那要先设计"密钥存哪、怎么保护"，与现在"只从环境变量读"的纪律相冲突。
   **这一条需要先有设计，不能顺手做。**
3. **最后打 binary 包**。三个平台的单文件产物 + sha256 + `agent.json` + 16×16
   `currentColor` 图标，提 PR。

**结论**：这不是"待做"，也不是单纯"缺前置条件"——第 2 条是一个**需要先做设计决策**
的功能（认证与密钥存储），与 [compat-policy.md](compat-policy.md) 和现有
"密钥只从环境变量读"的纪律直接相关。**在解决第 2 条之前，I0 不应以"上架"为关闭条件。**

已把第 1、2 条登记为 I0 之后的输入，见下节。

### 遗留问题总表（2026-10-27 清点）

按"该不该做"分三类。**每一条都注明了触发条件，不是凭感觉挂在这里。**

#### A. 有明确前置条件，条件未满足（4 项）

| 项 | 卡在哪 | 触发条件 |
|---|---|---|
| **仓库重新开源** | 私有仓库，`gh repo view` 确认 `isPrivate: true` | `status.md` 已定：首个包发到 nuget.org **或** M3 插件 API 稳定化。**M3 已关闭，这条其实已满足**——是个可以随时执行的决定，只需你点头 |
| **ACP Registry 上架** | 必须先开源（阻碍 1）+ 必须实现 Agent/Terminal Auth（阻碍 2） | 上面两条解决后即可，binary 打包已打通 |
| **ACP 认证实现** | 与"密钥只从环境变量读"的纪律冲突，需先设计密钥存储 | 等上架成为真实需求时。**这是功能，不是配置** |
| **断电级持久化（G7）** | 已调 `Flush(flushToDisk: true)`，缺掉电测试条件 | 有可控断电环境时。现在如实声明"不保证断电"，不假装 |

#### B. 已判定不做，理由充分（3 项）

| 项 | 理由 |
|---|---|
| 记忆取代关系（`SupersedesArtifactId`） | L1 只有 3 个白名单键，冲突场景不存在；等 L2 条目数上去再评估 |
| 配置迁移与回滚 | 没有可迁移的旧数据（只有环境变量、JSONL 未标版本、外部用户 0）；见 compat-policy §7.1 |
| 抽 `DougongHost` 装配器 / `CodingAgent` 产品层 | 98 次 `new ToolDispatcher` 里产品代码只有 1 处真实调用；见 host-layering-decision §4.4 |

#### C. 外部边界，不在本仓库（3 项）

| 项 | 归属 |
|---|---|
| Desktop 侧 4 项（§6.7 恢复重连） | Dougong Desktop 仓库 |
| `Dougong.Telemetry`（OTel 导出） | 有真实可观测性需求时；BCL `Meter` 已就位 |
| 工具幂等键（G10 余项） | 需远端工具配合识别重复请求 |

#### D. 刻意划定范围（5 项）

性能基线只测了三个场景（大会话 / 长会话 / 并发会话），其余 5 项各自写明了何时该补，
见 [performance-baseline.md](performance-baseline.md)。**这不是漏做，是定了边界。**

---

### 再往后：I1 可嵌入运行时

`iteration-roadmap.md` 的 I1：从"一个 CLI"变成"能被 .NET 服务嵌入的库"。

**但先别急着开。** [embedding-guide.md](embedding-guide.md) 已经把"怎么嵌"写清了
（20 行）。真要推进 I1，该等第一个真实的嵌入需求出现——那时才知道
"可嵌入运行时"缺的是什么，而不是照着路线图造。

这与 [host-layering-decision.md](host-layering-decision.md) §4.4 的结论一致：
**先有第二个消费者，再抽抽象。**

---

## 已完成动作（留档）

**G1**，分三步，每步都要有失败证据：

1. **写探针证明缺口**：构造一段足够长的历史，验证 `LoopAgentRuntime` 把全部历史送进
   `history`、没有任何裁剪。这一步可能发现比"没接压缩"更具体的问题
   （例如历史里有失效的 `tool_call_id` 配对）。
2. **接入压缩**：`LoopAgentRuntime` 增加可选的 `IContextCompactor` 与预算参数；
   在每轮构造 `history` 前 `Plan()`，需要时推进 `SummarizingCompactionTask`，
   `CompactionApply.TryApply()` 成功才替换。
3. **用真实 usage 校准**：把 `UsageRecord.InputTokens` 与估算值对照，
   得出实际比例；换算因子作为可配置参数，默认值保守。

**不做**：不重构 `LoopAgentRuntime` 的既有提交屏障顺序；不改 `IAgentRuntime` 的公开签名
（压缩器走可选构造参数）；不为压缩引入新的存储记录类型（摘要替换是内存态，
原日志保留）。
