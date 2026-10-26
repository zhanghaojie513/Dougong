# 实施状态板

> 执行入口：技术契约见 [architecture.md](architecture.md)，总体决策与验收见 [design.md](design.md)。完成设计、完成实现和通过验收分别记录；新会话从本文件进入。

## 当前状态（2026-10-24）

- M0 已关闭：三轮审核问题全部修复，13 项独立探测通过，验收项逐条勾选并附证据。公共 API 未做兼容承诺（当时写作 experimental，实际从未标记；2026-10-25 已改为 ApiCompat 方案，见 m3-plan.md §五）。
- 实际工程为 6 个产品项目 + 1 个测试项目 + 5 个样例工程 + 1 个对比探针工程。Agent 已有契约与最小 dispatcher；Ai/Mcp/Acp 仍为空工程；CLI 仍为占位。Telemetry 未建。
- 已修复本轮审核问题：排空超时后拒绝再激活与卸载；`UnloadAsync` 在停用后重新核验失败标志与最终状态；`LoadAsync` 取消/失败回滚残留条目；激活、取消、服务排空、事件排空与清理各自有独立预算；`Context.DisposeAsync` 走内核协调器，不能绕过隔离；`ResolveAll` 返回全部提供方；清理顺序按实际依赖边计算；普通退订立即从事件总线移除并可回收；事件先排空再清理；状态提交在锁内原子完成，新增登记/移除增量与共享诊断 DTO。
- .NET SDK 10.0.402 可用；完整解决方案 Release 构建通过（0 警告、0 错误），156 项内核行为测试全部通过（0 跳过）。
- 首个提交 `5c1bbe5` 及后续切片均已推送至 [GitHub 仓库](https://github.com/zhanghaojie513/Dougong)。
- [前沿调查](vision/frontier-roadmap.md)与[未来迭代路线](vision/iteration-roadmap.md)已建立；后者面向 M3 之后，不作为当前 M0 已完成的证据。
- M0 期间的实现缺陷、测试缺陷与判断错误已整理到[开发历程素材](journey/m0-dev-journey.md)，M1 的同类素材见 [m1-dev-journey.md](journey/m1-dev-journey.md)；这两份文件不是验收依据。
- 本文只回答"做到了哪一步"。**要达成什么**见 [goal.md](goal.md)，**什么算做完**见
  [quality-gates.md](quality-gates.md)，**下一步做什么**见 [plan.md](plan.md)。
  勾选任一项前，先确认它过了 [quality-gates.md](quality-gates.md) 的六道门。

## 仓库可见性（2026-10-24 起私有）

- 仓库已转为 **private**。转私有时 0 star / 0 fork / 0 watch，无外部依赖受影响。
- **重新开源的触发条件（已定，不靠记忆）**：首个 `Dougong.*` 包发布到 nuget.org 时。备选触发：M3 插件 API 稳定化启动。两者取先到者；触发后执行 `gh repo edit zhanghaojie513/Dougong --visibility public --accept-visibility-change-consequences`。
- 公开资产已就绪：双语 README（中英切换）、docs 归类索引、CI 门禁、release 已标 pre-release。重新开源即 launch，无需额外准备。
- 私有期间注意事项：Actions 按分钟计费（Windows runner 2 倍率，单次约 2 计费分钟，月度免费额度充足）；README 徽章与仓库链接对外不可见属预期，无需修复；提交纪律不放松——CI 门禁与失败路径测试照常，将来公开的历史就是门面。

## 评审修订清单

| 顺序 | 设计修订 | 依据 | 实现/验收 |
|---|---|---|---|
| 1 | 已补必需/可选需求、Blocked、生命周期事务、租约排空、清理与跨 ALC 契约 | architecture.md §3 | 已实现静态生命周期、需求图、服务激活事务、调用租约、事件队列、状态快照与 ALC 装卸 |
| 2 | 已补 schema、复合/未知权限、审批绑定、资源锁与目标包依赖图 | architecture.md §2、§4.3–4.4 | 未开始；csproj 待同步 |
| 3 | 已撤回必须自研循环的预设，定义 MAF/自研同用例比较门槛 | design.md §2.1 | 未运行，最终实现未选定 |
| 4 | 已补事件顺序/背压/错误/排空及 Skill 来源/外发/撤销/最小评测 | architecture.md §3.4、skills-evolution.md | 未开始 |
| 5 | 已同步 README、理念、总体方案和本状态板 | 本轮六份文档 | 文档一致性检查，不代表运行验收 |

## 开工前置

- [x] SDK 固定：`global.json` 锁 `10.0.402` + `rollForward: latestFeature`，
      并指定 `Microsoft.Testing.Platform` 运行器。CI 用同一版本。
- [x] NuGet 版本与引用图核对：`Directory.Packages.props`（CPM）4 个包版本统一管理；
      引用图 Kernel 零依赖、Agent→Kernel、Ai/Mcp/Acp→Agent、Cli→全部。
      由 `ArchitectureBoundaryTests` 断言，越界即失败。
- [x] restore/build/test 全通过；恒真测试已移除；pack 产出 6 个 nupkg。
      **XML 文档待补**（`GenerateDocumentationFile` 全未开启，见「已知未完成」）——
      其余两项（包自述文件、完整内核反例集）已完成：每个包带仓库 README，
      内核 178 项测试含 18 项对抗探针。
- [x] 仓库已首次提交并推送；工程元数据已填实际 URL（`Directory.Build.props:12-13`）。
      提交仅在用户明确要求时执行。

## M0 内核任务与验收

> 2026-10-24：以下勾选依据是**实测**而非文档承诺。每项附证据位置；独立探测工程位于
> 仓库外 `%TEMP%/dougong-m0-final`，13 项全部 PASS。M0 据此关闭。

- [x] 契约与组合根：静态/动态注册、期望启用状态、激活代次、必需能力和作用域租约。
  - 证据：`KernelM0AcceptanceTests`、`KernelServiceTransactionTests`、`KernelServiceLeaseTests`；
    探测 `lifecycle-concurrent`、`resolveall-all`。
- [x] 生命周期协调器：静态/动态注册的激活暂存/提交/回滚及一致切面；插件回调不持内部锁。
  - 证据：`KernelDependencyTests.Cleanup_can_query_kernel_during_stop_rollback_and_disposal`、
    `KernelIsolationTests`、`KernelRecoveryRegressionTests`；探测 `isolation-rejects-reuse`。
- [x] 需求图：必需缺失/恢复、可选空集合后新增、多提供方选择/冲突和依赖环诊断。
  - 证据：`KernelDependencyGraphTests`、`KernelProviderSelectionTests`、`KernelProviderPriorityTests`、
    `KernelManifestImmutabilityTests`（清单为只读快照，外部改动不能改变依赖判定）。
- [x] 停用排空：拒绝新调用、等待在途、逆序幂等清理、错误聚合与忽略取消诊断；禁止重叠代次。
  - 证据：`KernelServiceLeaseTests`、`KernelReview2RegressionTests`（清理超时停止后续清理、
    取消回调预算）、`KernelR3DependencyRegressionTests`（按实际依赖边排序）；
    探测 `cleanup-consumer-before-used-provider`、`stale-reference-dead`。
- [x] 事件通知：内核版本顺序、有界队列、异常隔离、快照重同步、退订后在途排空。
  - 证据：`KernelEventBusTests`、`EventBusLifecycleRegressionTests`、`KernelStateCommitTests`；
    探测 `event-unsubscribe-releases`、`event-self-stop-rejected`。
- [x] 动态加载：共享契约白名单、完整支持范围验证、私有依赖解析、清理外部引用和重新加载。
  - 证据：`KernelDynamicLoadingTests`、`KernelContractVersionTests`、`KernelCrossAlcAcceptanceTests`、
    `KernelSharedContractTests`、`KernelSharedContractMissingTests`、`KernelUnloadReferenceTests`；
    探测 `unload-idempotent-rejects`、`missing-shared-contract-rollback`。
- [x] 示例与测试：真实服务/事件、失败激活、并发启停、清理和服务事务回滚、慢观察者、
  动态加载及多轮调用后 WeakReference 卸载。
  - 证据：`KernelSnapshotIsolationTests`（快照不钉住 ALC）、`KernelCrossAlcAcceptanceTests`
    （真实共享契约的服务与事件双向）、`KernelUnloadReferenceTests`。
- [x] architecture.md §3.7 与 §3.4 全部用例通过。
  - 证据：上述测试与 13 项独立探测；探测 `state-revision-monotonic`、
    `state-stream-ends-on-dispose`、`state-registered-delta`。

### 关闭前最后一轮修复（2026-10-24）

- `PluginManifest` 曾持有调用方传入集合的引用：外部 `Clear()` 可绕过必需依赖使消费者直接
  `Active`；激活后注入需求又会让已激活插件变 `Blocked`。已改为构造时复制为只读快照。
  测试 `KernelManifestImmutabilityTests`；探测 `manifest-mutation-before-register`（修复前
  `Active`，修复后 `Blocked`）与 `manifest-mutation-after-active`。
- 移除只读死字段 `PluginEntry.DrainTimedOut`（隔离实际由 `IsolationReason` 承载）与未使用的
  `UnloadTask`；行为不变，156 项测试全绿。

## M1 入口与执行契约

- [x] 用同一假模型/工具测试 design.md §2.1 两个候选；记录版本、复现命令和选择理由。
  - 证据：[m1-candidate-comparison.md](m1-candidate-comparison.md)。两候选在同一 10 项反例集上
    均 10/10；最终选**候选 B（自研循环）**，依据是候选 A 存在可复现的事件顺序错误、
    终止原因错误、每场景多一轮模型调用，且关闭旁路依赖带 `ExperimentalAttribute` 的
    `EnableInvocableFunctionBypassing`。
  - **真实 provider 已验证**：六个模型各 5/5 通过——`mimo-v2.6-flash`、`MiniMax-M3.1-Flash`、
    `deepseek-v4.1-flash`（Waygate）、`GLM-5.3`、`GLM-5.3-Flash`（BigModel）走 OpenAI 协议，
    `gpt-6.1-sol`（Routin）走 Anthropic 协议；`deepseek-flash` 的三种协议（chat-completions、
    Anthropic 兼容层、Responses）各 5/5。三个适配器实现同一 `IChatClient`，
    被比较的始终是循环。Routin **Plan** 端点因订阅未激活 403，非代码问题。
  - 真实调用暴露三个假模型测不出的缺陷：循环未把工具声明传给模型（模型输出 XML 伪调用）、
    空 `choices` 帧越界、参数分片语义（应按 index 拼接原文而非按键合并字典）。
  - Waygate 另外暴露两种"空字符串当缺失"的协议变体：后续帧用空字符串覆盖 `id`/`name`
    （原 `??` 只挡 null），以及用空字符串表示未完成的 `finish_reason`（导致每个 delta 误下发）。
    已修复为"只接受非空值"。
- [x] 唯一 dispatcher：schema/参数规范化、审批身份绑定、执行前校验、调用去重、资源冲突和取消终态。
  - 已实现（`src/Dougong.Agent/ToolDispatcher.cs`）：最小 JSON schema 校验（顶层 type 与 required）、
    调用 ID 去重、权限描述采集、七种明确终态、旁路检测用的调用记录、冻结工具声明的对外投影。
  - **工具声明通道已落地**：`Dougong.Agent.ToolDeclarations` 通过 `ChatOptions` 附加属性
    （键 `dougong.toolDeclarations`）传递冻结描述符，运行时与连接器解耦；
    附 6 项契约自检（`-- contract`）。
  - 已实现审批与资源锁（`IToolApprover.cs`、`ToolResourceGate.cs`）：
    审批绑定 session/turn/invocation/工具版本/规范化参数摘要/工作区/权限范围/有效期；
    执行前进锁复核，参数、权限或并发计划任一变化即拒绝；等待审批期间不持锁；
    两档并发（Serial / WorkspaceExclusive）。
  - 两档并发**共用同一把工作区执行权**（Serial 额外先取全局串行权）。这是故障验收抓出来的
    真实缺陷：原先两级各用一把独立信号量，串行工具能与同工作区的独占工具重叠执行，
    等于取消没有真正生效。反证已确认——改回独立双锁后，`uncooperative-cancel-still-holds-lock`
    立刻失败。
  - 9 项自检（`-- approval`）全部通过，并用禁用复核的反证确认测试有效
    （权限升级、并发变化、过期三项立刻失败）。
  - 已实现文件版本复核（`WorkspacePathGuard.cs`）：工具通过
    `DescribeFileDependencies` 声明依赖文件；审批时捕获长度+时间+内容哈希，
    执行前进锁后重新捕获并比对，文件被改动/新建即拒绝。
  - 已实现工作区路径守卫：绝对路径与 `../` 越界、大小写不敏感前缀、
    符号链接/重解析点逐段解析（限深度 8）。
  - 8 项自检（`-- fileversion`）全部通过，并用禁用比对的反证确认测试有效。
  - **完整 JSON Schema 校验已实现**（`ToolSchemaValidator`）：类型（含 integer 与 number
    的区分）、嵌套 required、enum/const、数值边界、字符串长度与 pattern、数组 items 与
    uniqueItems、additionalProperties:false。8 项验收（`-- schema`）。
    关键立场：**遇到不支持的 schema 关键字必须报错**，不能静默跳过——
    静默跳过等于向使用者宣称"已校验"而实际没有。
  - **未实现**：资源级读写键。
  - **明确保留的竞态**：指纹计算与真正打开文件之间仍可能被替换，
    当前只能降低概率，不宣称消除。
- [x] 显式存储提交、单写者租约、半条尾记录处理、结果未知恢复、分支一致切面及预算/压缩失败协议。
  - 已实现（`src/Dougong.Agent/IAgentStore.cs`、`JsonlAgentStore.cs`）：提交屏障四个落点
    （用户输入→模型响应→工具开始→工具终态）、单写者 `FileShare.None` 租约、
    尾部半条记录隔离、序号缺口检测、`OutcomeUnknown` 对账。
  - 6 项屏障自检（`-- store`）全部通过；已反证测试有效（移除工具开始屏障立刻报
    "提交失败后仍执行了工具 1 次"）。
  - **审批决定与运行终态已落盘**（原缺口：`StoreRecordKind.ApprovalDecision` 与
    `RunFinished` 的使用点均为 0）：`ToolDispatcher` 新增可选 `IAgentStore`，
    审批后立即提交记录，绑定 invocationId / 工具身份与版本 / 参数摘要 / 工作区 /
    决定时刻；提交失败阻止执行。
  - 新增 `ApprovalLog` + `ApprovalSnapshot`：`IsUnaccounted` 让"有调用但无审批记录"
    可表达，不与"被拒绝"混淆——这正是 `OutcomeUnknown` 对账最需要的信息。
  - `RunFinished` 收口到 `FinishAsync` 单一出口：终止点有七处，逐处插入容易漏；
    提交失败不改变已发生的事实，只并入诊断。
  - 7 项自检（`-- approval-log`，含两个反证）。真实端点日志确认六段记录齐全。
  - **会话 resume / branch / list 已实现**（`SessionRecovery.cs`、`SessionBranch.cs`）：
    重放已提交 JSONL 重建历史并还原工具交互配对；分支 = 从分叉点复制新文件，源会话不变。
  - 9 项会话自检（`-- session`）覆盖：恢复出的历史能**真的续跑一轮**、未知结果阻塞续跑、
    分叉截断、源会话不被改动、分叉点不存在时报错、序号范围与缺口、旧双重编码格式仍可读、
    分支点悬空时向后补齐。
  - 三个反证确认检查有效：历史退化成扁平文本 → 续跑检查失败；分支忽略分叉点 → 截断检查失败；
    模型响应不记参数 → 恢复检查失败。
  - **修掉两个真实缺陷**：
    1. 日志 Payload 被双重编码（`JsonSerializer.Serialize(string)` 把 JSON 包成字符串），
       恢复必须先解一层才能读。已改为按结构存，同时保留读旧会话的兼容。
    2. 在工具交互中间分叉会留下悬空的 `tool_calls`，provider 直接拒掉续跑
       （实测 400 "must be followed by tool messages"）。已改为向后补齐到配对完成处。
  - 真实端点验证：读文件 → 退出 → `--resume` 后模型准确复述文件内容；
    在原本失败的 #2 处分叉，自动补齐到 #4 并续跑成功。
  - **未实现**：压缩失败协议、断电级持久化（当前只声明 `SurvivesProcessCrash`，
    不声明 `SurvivesPowerLoss`）。
- [x] 存储失败、工具副作用后未提交、同文件写冲突、跨会话 shell、工具替换、流式参数不全等故障测试。
  - 6 项故障验收（`-- failure`，`spike/Dougong.Agent.Comparison/FailureAcceptanceChecks.cs`）：
    同文件并发编辑被串行化、读写竞争在复核阶段被拦下、取消不合作仍持锁且如实报"已执行"、
    并发派发的执行次数等于报告成功次数（单一执行入口）、提交失败发生在副作用之前、
    崩溃窗口报 `OutcomeUnknown` 而非重放。重复 5 轮全绿。
  - 这一套立刻抓到一个真实缺陷（两档并发不互斥），见上一节。
  - **跨会话 shell 已实现**（`src/Dougong.Agent/ShellTool.cs`）：11 项验收（`-- shell`）覆盖
    退出码由进程报告、stdout/stderr 分离、args 不经 shell 解释、工作目录固定为工作区、
    宿主密钥不进入子进程、声明 `WorkspaceExclusive` 与 `Execute`、取消与超时都结束进程树、
    输出有界且标记截断、stdin 关闭不挂起、被工作区锁阻塞。
  - shell 的两个反证确认检查有效：去掉 `Environment.Clear()` 立刻报密钥泄漏；
    去掉结束进程树的调用，取消与超时两项同时失败。
  - **shell 抓到一个真实安全缺陷**：`ProcessStartInfo.Environment` 初始就包含父进程的
    全部变量，只往里加白名单项并不等于"只传这些"——`DOUGONG_PROVIDER_KEY` 会一起被继承，
    模型可以执行一条命令把它读走。已改为先 `Clear()` 再填白名单。
  - **不宣称隔离**：命令可以触达进程权限内的一切；本类只保证经过唯一 dispatcher、
    取到工作区独占锁、结果如实上报。CLI 默认不启用 shell，需 `DOUGONG_SHELL=1` 显式开启。
  - **未覆盖**：工具替换（`Register` 句柄够用但未做替换语义测试）、
    流式参数不全（已在 `-- contract` 的畸形 JSON/分片中覆盖，未单列）。
- [x] 上下文压缩：预算计算、保留工具配对、摘要失败不覆盖原历史、检查点可续跑。
  - 已实现（`src/Dougong.Agent/IContextCompactor.cs`、`ContextCompaction.cs`）：
    三阶段检查点（Select → Summarize → Retry → Completed/Failed）、滑动窗口选区间、
    工具调用与其结果的配对保护、失败保留原历史、取消不算失败。
  - 9 项自检（`-- compaction`）全部通过，并用"失败也覆盖历史"的反证确认测试有效。
  - **已接入生产路径**（原缺口：压缩能力只存在于测试里，循环从未调用它）：
    `LoopAgentRuntime` 新增可选 `ContextLimits` + `IContextCompactor` + `summarize`，
    每轮调用模型前检查一次。压缩只处理进入循环前的基线历史——循环内追加的
    assistant/tool 交互不参与，它们必须成对保留。
  - 四种降级路径都保留原历史并继续运行，各自发出带诊断的 `ContextCompacted` 事件：
    摘要失败、未配摘要器、预算不可行、应用失败。中断一个能跑的任务比让它被
    provider 拒绝更糟。新增 `AgentEvent.ContextCompacted` 让宿主如实展示有损压缩。
  - 9 项接入验收（`-- wiring`）：无预算时保持旧行为、压缩后请求变短、事件可观察、
    **工具配对不被拆散**、摘要失败保留历史、无摘要器不假装压缩。
    反证：移除接入点后 4 项立刻失败。
  - 真实端点验证（DeepSeek）：12 轮长会话，估算 9 418 → 3 445 tokens，19 次调用 0 失败。
  - **未实现**：分支式压缩、按真实 token 计数校准（`UsageRecord.InputTokens` 已可对照，
    但换算因子尚未回填为默认值，仍按 2 字符/token 保守估算）。
  - **token 估算已按实测校准**（原为固定 2 字符/token）：2026-10-24 对
    DeepSeek `deepseek-flash` 实测，线性拟合 `tokens = 30 + chars/8`。
    纯 ASCII 长文本约 8 字符/token，中文约 1.2 字符/token——固定 2 会让
    ASCII 高估 4 倍（过早压缩）、中文略低估。改为分段模型：ASCII 取保守值 4、
    CJK 取 1，并计入每条消息与工具调用的固定开销。7 项验收（`-- token`）。
  - **仍然保留的近似**：系数是有意保守的（低估 token 会让会话失败在高估之前），
    不同 provider 的实际分词器不同，不宣称精确。
- [x] 真实编码任务与 CLI 构建/打包通过；模型/工具代码实际接入后再记录已验证能力。
  - 真实编码任务已通过（六个 provider，含写文件与内容校验）。
  - **Ai 层落地**（`src/Dougong.Ai`）：三种线协议各自独立实现（`OpenAiChatCompletionsFormat`、
    `OpenAiResponsesFormat`、`AnthropicMessagesFormat`），共用 `HttpClientChatClient` 传输；
    `ChatClientFactory` 按具名配置建连接器，密钥只从环境变量读。
    CLI 与真实 provider 对比都已切到这一套，spike 里的一次性适配器与 CLI 自带连接器已删除。
  - 10 项线格式自检（`-- wire`）覆盖三种协议的请求形状、流解码与**多轮回填**：
    Anthropic 的 `tool_result` block、/responses 的 `function_call_output` 条目此前从未被构造过。
  - 三协议真实端点复验（DeepSeek `deepseek-flash`，各 5/5）：chat-completions、Anthropic 兼容层、/responses。
  - CLI 已接入（`src/Dougong.Cli`）：REPL + 控制台审批 + 工作区内 read_file/write_file
    + JSONL 会话 + 完整提交屏障 + 三种协议可选。已用 DeepSeek 端到端验证：模型请求调用 →
    控制台审批 → 执行读取 → 真实内容回填 → 模型给出正确结论。
  - **本轮修掉的四个真实缺陷**：
    1. `finish_reason` 被从 `delta` 里读，实际是 `choices[0]` 的兄弟字段——主路径从未命中，
       一直靠流结束兜底掩盖（反证：改回后 `-- wire` 立刻失败）。
    2. 工具结果消息只放 `FunctionResultContent`，而 `ChatMessage.Text` 由 `Contents` 派生，
       于是三种协议拿到的工具结果都是空字符串，模型回答"没有返回任何东西"（反证已确认）。
    3. 文本与 `tool_calls` 被拆成两条相邻 assistant 消息，思考模式 provider 因此拒掉整轮（实测 400）。
    4. 审批被拒绝后同样触发第 3 条：拒绝也必须留下可解释的历史。
  - 会话命令：`--list` 列出会话（记录数、序号范围、缺口）、`--resume <会话>` 恢复、
    `--branch <会话> [--at N]` 分叉；会话可用完整路径、标识或序号（1 = 最新）指定。
  - shell 工具已接入（`DOUGONG_SHELL=1` 启用）。已用 DeepSeek 端到端验证：
    模型请求执行命令 → 控制台审批显示具体命令与 `Execute` 权限 → 执行 → 退出码与真实输出回填。
  - **用量记账已实现**（`src/Dougong.Ai/UsageLedger.cs`）：`UsageRecordingChatClient` 作为装饰器
    包住任意 `IChatClient`，记录每次调用的输入/输出/总量、耗时与成败；`UsageLedger` 按会话与
    自然日聚合。11 项自检（`-- usage`）覆盖流式/非流式、失败与取消留痕、会话隔离、按日汇总、
    并发追加、文本不被用量污染、未报告与零可区分。
  - **修掉一个真实缺陷**：usage 原先以自定义附加属性（`["usage"]`）传出，
    而 M.E.AI 的聚合与中间件只认标准 `UsageContent`，账目会静默丢失。已改为标准类型；
    反证确认——把发送端改回附加属性，`client-emits-usage-content` 立刻失败。
  - 真实端点验证：DeepSeek `deepseek-flash` 报回输入 352 / 输出 3 tokens，`/usage` 正确显示。
  - **不猜单价**：本层只汇总 token 与调用次数，金额依赖 provider 价目表，由宿主换算；
    `UnreportedCalls` 与可空的 `TotalTokens` 用于区分"零消耗"与"不知道"。
  - **未实现**：OTel 指标导出（属 M2，届时把 `UsageLedger` 的汇总接到
    `OpenTelemetry` 1.20.1 的 meter 上）、同目录多会话并行（当前一次只开一个会话）。

## 桌面恢复与重连（设计已修订，未实现）

契约依据：[architecture.md §6](architecture.md#6-宿主与进程模型)；总体方案：[design.md §7](design.md#7-宿主与进程模型与-dougong-desktop-的协同)。Desktop 源码由其独立仓库交付，本仓库维护 SDK 依赖契约，不把桌面设计当作当前 SDK 功能。

- [x] 固定 AgentHost 直接内嵌 SDK，CLI 为另一宿主；内部命名管道与标准 ACP stdio 分开。
- [x] 定义存活重连/崩溃恢复、审批连接失效、PTY 所有权与退出策略，以及显式提交/暂存流式边界。
- [x] **SDK 侧**：提交/恢复、命令去重、未知结果和租约实现通过 M1 验证。
      证据：`-- store` 8 项、`-- session` 9 项、`-- approval-log` 7 项、
      内核 178 项（含 18 对抗探针）。
- [ ] **Desktop 侧**（不在本仓库，由 Dougong Desktop 承担）：
      启动/认证、附着与会话状态同步、有界增量队列；会话语义优先采用 ACP 标准方法。
- [ ] **Desktop 侧**：审批断线/重发/过期和重复答复不得重复执行。
      （SDK 侧已提供依据：审批绑定 8 维度 + 执行前复核 + `IsCurrentRegistration`，
      但"断线重发不重复执行"属宿主行为，须由 Desktop 验证。）
- [ ] **Desktop 侧**：终端 ID、有界输出缓冲与输入去重；VT 屏幕状态恢复不纳入首版验收。
- [ ] §6.7 **SDK 侧三行已满足**（未知结果可见、不自动重放、存储失败不放行后续调用）；
      其余四行依赖 Desktop 与 NativeAOT 打包验证，**不得据此宣称"支持恢复与重连"**。

## 已知未完成（2026-10-26 汇总）

台账里散落的未勾选项集中在这里，分三类。**判断哪个是欠账、哪个是边界，看"为什么"列。**

| 项 | 类型 | 为什么没做 |
|---|---|---|
| ~~Skill L1 记忆层~~ | **已完成（2026-10-26）** | 验收 2–6 已实现并逐项反证（`-- skill-l1` 7 项 + `-- memory-cli` 4 项 + 10 条反证） |
| Skill L1 验收 1（留出任务对照） | **已完成（2026-10-26）** | 6 家 provider × 2 种协议共 14 个组合实跑：有记忆 70–100%，无记忆 0–10%，**提升 60%–90%，中位数 80%，14/14 超过预先登记的 50% 阈值**。装置自检 `-- l1-harness` 6 项 + 7 条反证。适用范围仅限被测模型与这 10 个任务，见 [skill-l1-acceptance-1.md](skill-l1-acceptance-1.md) |
| 上下文卫生（工具结果裁剪、压缩保留计划） | **已完成（2026-10-26）** | `-- context-hygiene` 10 项 + `-- compaction` 新增 5 项；反证 8 条全部变红。裁剪只作用于模型上下文，日志与会话恢复仍用完整原文。见 [context-hygiene.md](context-hygiene.md) |
| 记忆取代关系（`SupersedesArtifactId`） | **决定不做** | L1 只有 3 个白名单键，冲突场景不存在；且是公开类型 + 持久化格式双重改动。等 L2 条目数上去再评估，理由见 [context-hygiene.md](context-hygiene.md) §四 |
| 判据 1 真人验证 | **部分**（M3 出口） | 2026-10-26 有真人走到 §6.3 卡住并报出 `CS0246`——判据 1 要的正是这种证据。已据此把 §6.2 改成"整段复制"并点明三个类型、§6.3 补 `CS0246` 分类对照表，按新文档原样重建验证通过。**但「从零走完全程、全程无需提问」仍未达成**：§7–§8 无真人验证，且只覆盖了一个读者 |
| ~~XML 文档生成~~ | **已完成（2026-10-25）** | 5 个 SDK 包共 592 个成员产出 XML；开启后暴露并修掉 34 处注释语法错误（见「进度日志」） |
| 断电级持久化（G7） | 边界 | 已调 `Flush(flushToDisk: true)`，缺掉电测试条件 |
| 工具幂等键（G10 余项） | 边界 | 需远端工具配合识别重复请求 |
| Desktop 侧 4 项（§6.7） | 边界 | 不在本仓库，由 Dougong Desktop 承担 |
| 性能基线 5 项未覆盖 | 边界 | 刻意划定范围，各自写明何时该补 |

**三类判据**：
- **欠账**：计划里承诺过、能力已具备、只是没做——该做
- **边界**：有明确的外部依赖或验证条件——不该假装能做
- **设计限制**：做了会得到半成品或错误承诺——不该做

## M2/M3 后续验收入口

- [x] M2 ACP/MCP 按版本/能力联调，未知权限、schema 变化、审批取消和日志出口均覆盖。
      （逐条核实：`McpAgentTool` 权限恒 `IsKnown=false`、版本为 schema 的 SHA-256、
      `-- acp` 含取消用例、`stdout-carries-only-protocol-frames` 守卫日志出口。）
  - **MCP 工具桥接已实现**（`src/Dougong.Mcp/McpAgentTool.cs`，ModelContextProtocol 2.3.0）：
    远端工具包装为 `IAgentTool`，经过同一个 dispatcher。8 项验收（`-- mcp`）用**真实的
    MCP client + server 内存配对**，不是 mock。
  - 三条硬约束各有验收项：远端 annotations 仅作提示、**不得**翻译成已知权限；
    schema 变化必须改变工具版本（否则旧审批继续有效）；远端 `IsError` 必须是失败终态。
  - 连不上 server 不阻止启动，但必须报错——静默跳过会让用户以为工具已就绪。
  - **修掉一个契约不一致的真实缺陷**：`ToolDeclarations` 把 `Name` 发给模型，
    而 dispatcher 只按 `Id` 查找。M1 里所有工具 `Id == Name` 所以从未暴露；
    MCP 工具 ID 带 server 前缀（`demo/echo`）立刻暴露为"工具已注册却被判未注册"。
    已让 dispatcher 同时按 Id 与 Name 路由，并有反证。
  - CLI 接入：`DOUGONG_MCP_SERVERS="name=command args;..."`，默认不连。
    子进程不继承宿主环境（与 shell 工具同理）。真实 stdio server 端到端验证通过。
  - **MCP 变化检测已实现**（`src/Dougong.Mcp/McpToolRegistry.cs`）：
    订阅 `notifications/tools/list_changed`，远端工具集合或 schema 变化后全量刷新注册。
    4 项验收（`-- mcp-reload`）用真实 server 主动发通知触发：工具刷新、schema 变更
    使版本变（旧审批失效）、移除后不再被派发、多 server 刷新互不影响。
    反证：去掉订阅后 4 项全部失败。
  - **刷新是「先全部注销、再全部注册」**，存在极短的工具不可用窗口。接受这个窗口的
    理由：此时正确行为是「工具暂时不在列表里」，而不是「工具在列表里但指向旧 schema」
    ——后者会让模型按旧 schema 生成参数、远端正按新 schema 解释，即「批准了 A、执行了 B」。
  - **修掉一个真实缺陷**：模型可见的工具名最初用 `server/tool` 形式，真实 provider
    直接 400 拒绝（只接受 `^[a-zA-Z0-9_-]+$`）。改用 `server__tool`。
    这条只有真实端点能发现——假模型不校验工具名字符集。
  - **工具名必须跨 server 唯一**：两个 server 各自暴露 `alpha` 时，若模型可见名称相同，
    模型无法区分，dispatcher 的 Name 别名也只能指向一个。因此 Id 与 Name 都带 server 前缀。
  - **ACP Agent 端已实现**（`src/Dougong.Acp`）：按行分帧（无 Content-Length）、
    JSON-RPC 2.0 双向 id、`initialize` 版本协商、`session/new`、`session/prompt`、
    `session/cancel`、5 种 `session/update` 翻译、`AcpApprover` 权限请求。
    20 项验收（`-- acp`）用真实流配对驱动，含三个硬约束：**stdout 只含协议帧**、
    所有 update 先于 prompt 响应、未实现的 session 方法显式报错而非静默成功。
  - **修掉一个真缺陷**：`session/prompt` 会跑完整轮，原先在同一个读循环里 await，
    导致后续 `session/cancel` 通知读不到，取消永远无效。改为长请求登记后立即继续读取。
    反证：改回阻塞等待，取消检查立刻失败。
  - 真实进程级验证：外部客户端驱动 `dougong --acp`，initialize → session/new →
    session/prompt 拿到 4 条 update、文本正确、`end_turn`。
  - **审批已接入协议**（原缺口：`AcpApprover` 未接到 dispatcher，ACP 模式下需审批的调用
    一律拒绝）：CLI 的 `--acp` 现在用 `AcpApprover` 构造 dispatcher，审批变成向客户端发
    `session/request_permission` 并等待其选择。13 项验收（含三个审批用例：
    批准后执行、拒绝后不执行、无回复降级为拒绝）。
  - **修掉一个真缺陷**：host 收到权限请求的**响应**时直接忽略，从未交给等待方——
    审批永远挂着，而症状看起来像"客户端不配合"。根因是读循环的 Response 分支
    只写了日志。反证：改回忽略，两项审批检查立刻失败。
  - **修掉一处装配缺陷**：原先 runtime 与 dispatcher 由两个工厂分别构造，
    导致审批器挂在另一个实例上、工具执行根本不经过它。改为单一工厂产出一对。
  - 真实进程级验证：外部客户端驱动 `dougong --acp`，1 次权限请求 → 客户端批准 →
    工具执行 → 模型读到文件真实内容 → `end_turn`。
  - **会话恢复已实现**：`session/load`（重放整段历史再响应）、`session/resume`
    （不重放但接回历史）、`session/list`（列出已提交会话）、`session/close`
    （先取消再释放）。`loadSession` 与 `sessionCapabilities` 相应声明为支持——
    声明了就必须真能用，否则客户端会按能力走恢复路径然后失败。
  - 恢复历史来自 JSONL 真相源（`SessionRecovery`），不是客户端提供。有未知结果的
    会话**拒绝恢复**并指出具体调用，不假装历史完整。
  - **修掉一个真实缺陷（只有真实进程验证才暴露）**：纯文本轮次（无工具调用）
    从不提交 `ModelResponse`，于是所有"只是聊天"的会话恢复后只剩用户消息，
    模型不记得自己说过什么。19 项单元验收没抓到，因为它们都是手工写完整 JSONL
    再测恢复，从未让运行时自己跑一轮。已补回归用例并有反证。
  - 真实进程验证：进程 A 建立会话并告知暗号 → 进程 B `session/list` → `session/load`
    （重放 2 条 update）→ 追问暗号，模型准确答出 `BLUE-42`。
  - **两个入口能力已对齐**（原缺口：ACP 模式只注册 read_file/write_file，
    MCP 工具、shell、压缩、用量记账全都没接，而 REPL 全都有——M2 的成果在
    ACP 路径上等于没生效）：抽出 `SessionComposition`（能力集合）与
    `AcpComposition`（ACP 装配），REPL 与 ACP 走同一条装配路径。
  - 5 项一致性验收（`-- parity`）：shell 启用时可见 / 未启用时不可见 /
    上下文窗口配置后触发压缩 / 记录用量 / 两个入口都有文件工具。
    反证：退回旧装配后 3 项独立失败（shell 不可见、无压缩、无用量）。
  - **压缩断言修正过两次**，两次都是假通过：第一版用 `lastCount < 12`
    （轮次不多就永远通过）；第二版用"末轮小于峰值"（压缩后又会增长，
    末轮可能等于峰值）。正确判据是"条数序列里出现过明显回落"。
  - **修掉一个真缺陷**：压缩发生在运行时内部的 baseline 上，但 ACP 会话的
    `_history` 不知道，每轮仍传全量——压缩效果在会话层面不持久。
    已让 `ContextCompacted` 事件同步裁剪会话历史（按条数裁中间，
    保留头部与尾部，避免裁掉工具配对）。
  - **修掉一处资源泄漏**：MCP 注册器与客户端建立了但从不释放，
    `session/close` 现在按"先取消、再释放"的顺序释放它们。
  - 真实进程验证：`DOUGONG_SHELL=1` 下通过 ACP 驱动，模型成功调用 shell 工具
    并读到真实 stdout。
  - **未实现**：`session/delete`、`session/set_mode`（显式 -32601）、
    `fs/*`、`terminal`、`plan`。
  - **指标导出已实现**（`src/Dougong.Ai/DougongTelemetry.cs`）：基于 BCL 的
    `System.Diagnostics.Metrics.Meter`，不直接引 `OpenTelemetry` 包——SDK 只发布到
    标准出口，谁来采集由宿主决定。5 项验收（`-- telemetry`）用 `MeterListener`
    验证真实调用后确实产生计量。
  - 计量：`calls`（按 result 标签分成功/失败）、`duration`、`input_tokens`、
    `output_tokens`、`unreported_calls`。**未报告用量单独计数而不是记成 0**——
    后端看到"0 tokens"会读成"这次调用免费"，真相却是"不知道花了多少"。
  - 标签只有 provider / model / protocol / result，**刻意不含会话 ID 或调用 ID**：
    高基数维度会让后端时间序列数量爆炸。会话级归属属于日志，不属于指标。
  - 真实端点验证：DeepSeek 一次调用产出
    `calls=1, duration=2171ms, input_tokens=35, output_tokens=13`，与账本一致。
  - **MCP resources/prompts 已实现**（`McpResourceCatalog`）：列出资源与模板、
    读取资源、列出与展开 Prompt。三个设计要点：
    ① 资源与 Prompt **不走 dispatcher**——它们没有副作用，混进工具管线只会让审批变成噪音；
    ② 读取失败返回可恢复结果而非抛错，调用方转述原因而不是让会话崩掉；
    ③ 超大内容截断必须显式标记（`Truncated`），静默截断会让模型以为看到了全部。
  - 资源/Prompt 的 `list_changed` 通知会让目录版本号递增，宿主据此判断缓存是否过期
    （不主动重拉，避免无事发生时也打远端）。
  - **MCP HTTP transport 已实现**：配置支持三种形态，靠值前缀区分且向后兼容——
    `name=/path/server args`（stdio）、`name=http://host/mcp`、`name=https://host/mcp`。
    用 Streamable HTTP（SSE 已废弃，不提供开关）。
  - **ACP 客户端能力方法已显式拒绝**：`fs/*` 与 `terminal/*` 返回 -32601 并说明
    "Dougong 有意不声明——读写统一走 dispatcher、命令执行统一走 shell 工具"。
    笼统的"不受支持"会让对端误以为是实现缺陷，而不是能力协商的结果。
  - **ACP elicitation 已实现**（`src/Dougong.Acp/AcpElicitationTool.cs`，工具名
    `ask_user`）：模型可主动向用户提出 1–4 个选择题，每题 2–4 个带说明的选项，
    走 `elicitation/create` 的 `form` 模式。四条设计结论：
    ① **是工具不是宿主特权**——经过 dispatcher，审计/并发/终态都是现成的，
    与"唯一执行入口"一致；同类实现（Claude Code `AskUserQuestion`、Codex
    `request_user_input`、TKCode `ask_user`）都是这个形状。
    ② **取工作区独占**：避免问答面板与审批面板同时弹出。
    ③ **不走审批**：`DescribePermission` 报已知 `None`。但 dispatcher 对每次调用
    都会请求审批（正确的保守默认），因此宿主套了一层 `PermissionPolicyApprover`，
    只在「已知 + 无访问 + 未声明路径/命令/域名」三条同时成立时跳过。
    未知权限（MCP 工具全部如此）与声明了范围的工具照旧逐次审批。
    ④ **四种结果不混淆**：`Answered` / `Declined` / `Cancelled` / `Unavailable`，
    另有 `InvalidRequest`。客户端报错、传输异常、本轮取消都**不能**报成"用户拒绝"
    ——这是 Cursor 的已知事故，模型会据此误判自己已被否决。`Cancelled` 之下还用
    诊断文本区分"用户关闭了对话框"与"本轮被取消、用户根本没看到"。
  - 形状不合法（如只有 1 个选项）时**不发请求**，直接返回 `InvalidRequest`
    并提示模型"只有一条合理路径就直接说明并继续，不要编造凑数选项"。
  - 能力声明只加 `form`，**不加 `url`**——URL 模式要求客户端在安全上下文打开链接
    并对 Punycode 域名告警，我们无法验证，声明了就是承诺做不到的事。
    入站方向的 `elicitation/create` 以 −32602 拒绝并说明"方向反了"。
  - 验收 10 项（`-- elicitation`）+ 1 项真实进程（`-- elicitation-process`），
    5 条反证逐条命中：把 `cancel` 改报 `Declined`、放行单选项、多声明 `url`、
    去掉 `IsKnown` 守卫、去掉工具注册——最后一次是**真实 `dougong --acp` 进程**
    （provider 用本地 HTTP stub，不读任何密钥）驱动完整一轮问答。
  - **未实现**：MCP 断线重连（当前断连即失败，不自动重试——
    远端操作是否已发生未知，重试会重复副作用）；ACP 的 `plan`
    （**缺数据源**：`IAgentRuntime` 没有 plan 事件，而 ACP 的 plan 是每次全量替换，
    靠工具序列去猜"看起来像计划的"会把推测伪装成计划，属于 P3 反面）；
    OTel exporter（宿主自行接 meter provider）。
  - **协议内字段名差异无开关**（G12/G13）：`reasoning_content` 在运行时与 format 里
    共七处硬编码，输出上限在 Anthropic format 硬编码 4096、OpenAI 两个 format 从不发送；
    `ChatOptions` 只被读了工具声明。接一家把思考放 `reasoning` 的 provider 会
    **静默丢失思考内容**（不报错，只是下一轮上下文少东西），这是这批缺口里唯一
    "出错但看不出来"的。判据见 `plan.md` G12/G13。
- [x] **M2 的 Skill L1 记忆层已实现**（`design.md:236` 把"Skill 最简 L1 候选/启用/撤销
      与最小评测"列为 M2 范围）。
      证据：`-- skill-l1` 7 项（白名单、注入不可提权、撤销下一轮生效且不打断当前轮、
      未审批/篡改/过期/已撤销不加载、中断重放不重复、可观察可禁用、注入上限）
      + `-- memory-cli` 4 项（真实进程里的默认关闭与可观察性）
      + `scripts/skill-l1-counterproofs.sh` 10 条反证，逐条确认"改坏实现 → 该检查变红"。
      **验收 1（留出任务对照）已用真实模型跑完**：6 家 provider × 2 种协议共 14 个组合，
      有记忆组遵循率 70–100%、无记忆组 0–10%，**提升 60%–90%（中位数 80%），
      14/14 超过预先登记的 50% 阈值**。装置自检 `-- l1-harness` 6 项 + 7 条反证。
      结论适用范围仅限这 6 家模型、这 10 个任务、这两种偏好，**不外推**。
      详见 [skill-l1-acceptance-1.md](skill-l1-acceptance-1.md)，含 v1 基线与两批分开记录的理由。
- [x] M3 API/行为兼容、作者模板和卸载测试稳定化。
      证据：`-- kernel-wiring` 10 + `-- kernel-path` 3 + `-- kernel-process` 5 +
      `-- replaceability` 4 + `-- bare-hold`（12 处全说明）+ `-- workspace-lock` 8 +
      `-- acp-plan` 6 + `-- elicitation` 10；ApiCompat + CHANGELOG + 基线脚本；
      20 轮装卸 4 项；作者指南（经自我审查）+ 两个可运行样例；性能基线三场景。
      **判据 1（外部作者只看文档能写插件）仍为"部分"**——需真人验证。
      完整 Skill 进化和评测优化按验证结果后续建设（见下方"已知未完成"）。

## 依赖版本

2026-10-24 对照 NuGet 最新稳定版核对。原则：不锁定过时版本，也不追预览版。

| 包 | 版本 | 位置 | 说明 |
|---|---|---|---|
| `Microsoft.Extensions.AI.Abstractions` | 10.11.0 | Agent、Ai、spike | 已是当前最新稳定版 |
| `Microsoft.Agents.AI` | 1.26.0 | 仅 spike（候选 A 对比基线） | 最新稳定版；结论是不能依赖，见 m1-candidate-comparison.md |
| `xunit.v3` | 4.0.2 | 内核测试 | v2 主线已由 v3 接替；M0 测试全部迁移通过 |
| `ModelContextProtocol` | 2.3.0 | M2 计划 | 尚未引入 |
| `OpenTelemetry` | 1.20.1 | M2 计划 | 尚未引入 |

- 测试栈从 xUnit v2 + VSTest adapter 迁到 **xUnit v3 + Microsoft.Testing.Platform**：
  `global.json` 固定 SDK 并启用 MTP 运行器，`Microsoft.NET.Test.Sdk` 与
  `xunit.runner.visualstudio` 已不再需要，删除后 156 项测试仍全绿。
- 迁移暴露一个**测试自身的缺陷**：`Publish_does_not_run_handler_on_publisher_thread`
  在线程池线程上发布再比较线程 ID。事件泵也跑在线程池上，线程池可能复用发布线程，
  于是这个断言在 xUnit v3 的并行调度下稳定失败（VSTest 的时序恰好掩盖了它）。
  已改为在专用非线程池线程上发布；反证确认原写法 5 轮中 2 轮失败。
- xUnit v3 的 `xUnit1051`（建议统一传 `TestContext.Current.CancellationToken`）在
  `.editorconfig` 中降级为建议：内核测试大量 await 的是引擎内部的取消路径，
  统一塞入测试级令牌会把"取消是否被正确传播"与"测试是否超时"混在一起。

## 与 goal.md 的对应（2026-10-25 修正）

四条性质当前状态（详细判定见 [goal.md](goal.md) §4、§6）：

| 性质 | 状态 | 依据 |
|---|---|---|
| P1 可组合 | **达成（四种可替换能力已验证）** | **默认即经内核**（REPL 与 ACP），`DOUGONG_KERNEL=0` 可退回对照。存储/审批器/dispatcher/运行时都不再被编排者裸持有。可替换性用真实现验证（`InMemoryAgentStore` 对齐 JSONL 语义）。`-- kernel-wiring` 9 + `-- kernel-path` 3 + `-- kernel-process` 4 + `-- bare-hold` 12 处全说明；共 7 条反证。剩余：第三方插件生态属 M3 |
| P2 完整生命周期 | **内核内达成，产品路径未验证** | 177 项测试 + 20 项对抗探针，全部针对内核自身；无一项验证"插件在真实会话中被停用/替换后会话仍可继续"。断电级持久化未声明 |
| P3 故障可解释 | 基本达成 | 四个提交屏障 + 审批记录 + 未知结果对账；缺工具幂等键 |
| P4 真实可用 | **基本达成** | 3 协议 + 真实编码任务 + 压缩 + 恢复 + MCP/ACP 都验证过；协议内方言差异已走 `WireCompat`（G12/G13 已修）。剩余：更多真实 provider 的方言实测 |

**2026-10-25 的修正说明**：此前本表把 P1 记为"部分"，依据写的是"插件生态与可替换性验收未做（M3）"。
这个描述掩盖了真实问题——不是生态未成型，而是**内核与运行时之间没有接线**。
`design.md:113` 定义的"一切皆插件"是「CLI REPL、JSONL 存储、审批器、内置工具、MCP 连接与
Agent 运行实现均由内核承载，组合根解析 `IAgentRuntime`」，这条在代码里不存在。
因此 P1 应从"部分"改为"未达成"，并按 `plan.md` 作为下一步首要事项。

## 约定

- 当前 API **未做兼容承诺**（`0.x`），但变更由 ApiCompat 自动检出并记入 CHANGELOG（M3 切片 3.2 实施）。此前写作"全部 experimental"的那条从未执行，2026-10-25 修订，见 m3-plan.md §五。编译契约详细定义以 architecture.md 为准，design.md 保持摘要和决策，避免复制多份接口后漂移。
- 实现与其行为测试一起交付；未运行、失败、未覆盖的检查必须如实记录，文档勾选不替代测试结果。
- 原工期和桌面周数是未验证估算，按 PoC 和单人产能重估，不当作并行人力或交付承诺。
- M1 验收后再用 CLI 辅助后续开发；自用不能替代故障与独立任务验证。

## 进度日志

- 2026-10-22：建立设计文档与仓库骨架、定名 Dougong；当时“设计定稿”的结论已被 2026-10-23 技术评审修正。
- 2026-10-23：完成桌面内嵌宿主、IPC、恢复/重连、审批断线与 PTY 设计修订。
- 2026-10-23：按顺序修订其余内核/工具/依赖/MAF/事件/Skill 问题，并同步 README、理念和状态。
- 2026-10-23：完成 Kernel 生命周期首批实现与回归测试，修复清理重入、失败重试和激活/停用竞态；首个提交 `5c1bbe5` 创建并推送。
- 2026-10-23：完成服务激活事务首个切片：激活代次暂存、成功原子发布、失败/停用回滚、旧上下文隔离；Release 构建与 25 项内核测试通过。
- 2026-10-23：完成服务调用租约切片：代次绑定引用、原子在途计数、停用批次封口/取消/排空、旧引用失效与销毁排空；Release 构建与 32 项内核测试通过。
- 2026-10-23：补逆拓扑清理顺序与排空超时诊断：批次按激活序逆序清理，超时标记 Failed 并跳过清理；Release 构建与 34 项内核测试通过。
- 2026-10-23：补需求图能力：可选服务需求、插件级依赖阻塞与激活顺序、缺失依赖诊断、注册期依赖环拒绝（含完整路径）；Release 构建与 43 项内核测试通过。
- 2026-10-23：补事件总线有界异步投递：每订阅独立队列、发布不执行插件代码、异常隔离、溢出断开并诊断、停用/销毁排空；Release 构建与 50 项内核测试通过。
- 2026-10-23：补原子状态快照与修订号增量订阅：快照/订阅同临界区建立、修订号单调递增、溢出发出重同步信号并结束序列；Release 构建与 55 项内核测试通过。
- 2026-10-23：补同键多提供方显式选择：`ServiceRequirement.ProviderId` 在需求判断与运行时解析两侧生效，未指定时仍按歧义阻塞；Release 构建与 62 项内核测试通过。
- 2026-10-23：补动态加载与卸载：collectible ALC、宿主共享契约、`LoadAsync`/`UnloadAsync`、样例外挂插件、多轮装卸后的程序集回收验证；Release 构建与 69 项内核测试通过。
- 2026-10-23：补契约版本范围校验：`ContractVersion` 声明、宿主版本表、加载期兼容检查与拒载，样例拆为独立程序集；Release 构建与 79 项内核测试通过。
- 2026-10-23：收敛裸 `Services.Resolve`：仅激活回调窗口可用，窗口外抛错并指向 `Resolver`；Release 构建与 84 项内核测试通过。
- 2026-10-23：M0 验收收尾：`dotnet pack` 产出 6 个 nupkg；补可选服务后新增、跨 ALC 类型同一性、多轮真实调用后卸载的验收测试；Release 构建与 90 项内核测试通过。
- 2026-10-23：补齐 M0 最后两项：提供方注册优先级自动选择（并列仍歧义）与状态增量分区标记/游标续订；Release 构建与 100 项内核测试通过。
- 2026-10-23：M0 独立审核发现阻断问题（排空超时后可重激活、失败卸载、预算缺失、ResolveAll 收缩、清理顺序、事件引用残留、状态乱序）。按 R1–R6 修复：隔离代次与卸载事务、分阶段预算、集合解析与实际依赖清理、事件封口/排空/引用释放、原子状态提交与登记/移除增量、真实跨 ALC 共享契约验收；Release 构建与 128 项内核测试通过。
- 2026-10-23：第二轮独立复审发现 6 项实现与契约不符：激活窗口可从无关异步流借用、清理超时后仍清理提供方、`DeactivateAsync` 取消回调无预算、cleanup 令牌不可取消、观察者诊断仍持有插件异常、内核销毁后状态流不结束，另加历史上限缺少校验。已全部修复并补回归测试；复审探测 6 项均返回期望值，Release 构建与 140 项内核测试通过。
- 2026-10-23：处理外部引用治理：`PluginHandle`/`PluginSummary` 不再携带 `PluginManifest`，只保留 Id/Version/State；完整清单改由 `GetPluginManifestAsync` 显式取得。实证保留快照不再阻止动态插件 ALC 回收，显式持有清单仍会（属调用方主动选择）。Release 构建与 143 项内核测试通过。
- 2026-10-23：最终复审发现共享契约登记是进程级静态状态且不可撤销，漏配时插件会先登记、激活失败、留下不可卸载的隔离代次。改为 `DougongKernel.RegisterSharedContract`（按内核实例隔离、返回句柄可撤销）；装配解析失败时 `LoadAsync` 回滚条目并抛出提示登记契约的可操作诊断。Release 构建与 146 项内核测试通过。
- 2026-10-24：M0 关闭复审：`PluginManifest` 改为只读快照（外部改动不再能绕过依赖判定），移除死字段；13 项独立探测全部通过，M0 验收项据此勾选。
- 2026-10-24：M1 CLI：REPL 宿主接入循环/屏障/审批/JSONL；端到端真实调用通过，
  期间修掉工具结果回填缺 tool_call_id 与 DeepSeek reasoning_content 回传两个协议缺陷。
- 2026-10-24：M1 上下文压缩：三阶段检查点 + 滑动窗口，保护工具配对，
  摘要失败不覆盖原历史；9 项自检通过（含反证）。
- 2026-10-24：M1 文件版本复核：工具声明文件依赖，审批时捕获、执行前比对；
  路径守卫拦下绝对/相对越界与重解析点逃逸。8 项自检通过（含反证）。
- 2026-10-24：M1 审批与资源锁：审批凭据绑定八项维度并做执行前复核，
  9 项自检通过（含反证）；等待审批期间不持锁，避免慢审批阻塞工作区。
- 2026-10-24：M1 提交屏障：`IAgentStore` 契约与 JSONL 实现，四个提交落点串起
  "先提交意图、再产生副作用"；6 项自检通过，并用移除屏障的反证确认测试有效。
- 2026-10-24：M2 剩余协议面：MCP resources/prompts（可恢复失败、截断标记）、
  HTTP transport（三形态配置）、ACP 客户端能力方法显式拒绝。10 项验收含四个反证。
  如实记录两项未做：MCP 断线重连（重试会重复副作用）、
  ACP plan/elicitation（运行时没有数据源，做了是空壳）。
  （后项已于 2026-10-25 补齐 elicitation，见下。）

- 2026-10-25：补四种能力的替换实测（`-- replaceability` 4 项）。
  此前只有存储有"换一种实现、不改内核、行为仍通过"的验收；`IChatClient`、
  `IToolApprover`、`IToolDispatcher`、`IAgentRuntime` 虽然都是接口，
  却**没有一条测试证明"换了还成立"**——"接口存在"和"可替换被证明"是两件事。
  每种都用两份可观察行为不同的实现跑同一套断言。
  过程中发现自己的检查有缺陷：`dispatcher-is-replaceable` 初版把替代实现传给
  `LoopAgentRuntime` 的裸 `IToolDispatcher` 重载，被内部包成 `FixedDispatcherAccessor`，
  于是"替换"实际发生在包装层之外，反证无法命中。改用 `IDispatcherAccessor` 重载后
  反证才生效。这类缺陷只有靠反证才能发现。
  另有一条期望写错（实现 B 两轮都有文本，累计是 "workdone" 不是 "done"）。
  178 项测试 + 32 套件全绿。

- 2026-10-25：让 `StoreDurability` 真正被消费（G7 的声明部分）。
  核实发现它在**产品代码里零消费**：只有定义处赋值和一处测试包装器转发，
  宿主从不读、从不展示。声明没人看就等于没声明。
  补上：CLI 启动横幅输出 `durability=<tag>（<描述>）`；三档描述互不相同且
  明确"不保证断电"；仅内存档额外警告"退出后无法恢复"；
  ACP 存储打开失败时说明"本次会话不会被保存"而不是只说"改用无持久化"。
  两处验收：真实进程断言横幅出现等级标记（用 ASCII tag，
  因控制台编码在不同平台不一致）、单元级断言三档描述可区分。
  各配一条反证。G7 的断电安全部分仍是设计限制——需要掉电测试条件。
  178 项测试 + 31 套件全绿。

- 2026-10-25：处理剩余四项缺口（G5/G7/G10/G11），M2 协议面闭合。
  **G5 资源级读写键**：`ToolExecutionPlan` 增加 `Reads`/`Writes`。此前
  `read_file` 声明 Serial，两个读也要排队——真实损失。实现读写锁要注意三点：
  ① 不能用 `ReaderWriterLockSlim`（线程亲和，租约跨 await）；② 写者必须显式等
  读者清零（只占 WriteGate 不够）；③ 多资源按键排序加锁防死锁。
  `write_file` 从 WorkspaceExclusive 收窄为 Serial+资源键——只与同一文件互斥，
  不再阻塞整个工作区。探针 8 项，其中一条验证**真实工具**声明了键
  （防"能力实现了但没人用"）。
  **G10**：真问题不是"缺幂等键"，而是**未知结果不带参数**——恢复时宿主连
  "当时要做什么"都不知道。已修：`ToolCallStarted` 记参数原文，
  `UnknownOutcome.ArgumentsJson` 透出。验收走**真实运行时**（让终态提交失败造窗口），
  而不是手工写记录——后者测的是"我能不能写对 JSON"。
  **G11 ACP plan**：数据源是**工具**不是推测。新增 `todo_write`，
  模型显式声明计划 → 运行时投影为 `PlanUpdated` → ACP 翻译为全量替换条目。
  6 项验收含"全量替换"「未知 status 词不被改写」「工具失败不发光计划」。
  **G7**：澄清为设计限制——代码已 `Flush(flushToDisk: true)`，
  缺的是掉电测试条件；不用"调用了 flush"冒充"已验证"。
  178 项测试 + 31 套件全绿。

- 2026-10-25：清掉 `Dougong.Agent.csproj` 里多余的 Kernel 工程引用。
  它从 M0 建仓（`5c1bbe5`）起就存在，而 Agent 层源码从没用过 Kernel 的任何类型。
  害处不只是冗余：它让"Agent 可脱离内核单独工作"这句话看起来不成立，
  也让"L1 对 L0 零引用"需要额外解释。
  移除后全量构建与 178 项测试通过，`deps.json` 也不再含 Kernel。
  新增 `Agent_layer_does_not_depend_on_kernel` 守卫：同时查工程图与已编译程序集的
  依赖（只查 csproj 不够，传递引用同样会破坏独立性）。
  反证：把引用加回去，测试精确报出"Dougong.Agent 不得引用 Dougong.Kernel"。

- 2026-10-25：修 G9（跨会话工作区互斥）。
  发现方式：核实缺口登记时发现 G9 不只是"等桌面宿主"——它是**现在就不成立**的
  承诺。`architecture.md` §4.4 要求同一工作区的串行/独占工具互斥，而
  `ToolResourceGate` 的锁表是实例字段，每个会话（CLI 一次、ACP 每 session 一次）
  各 new 一个。同一工作区的两个会话各持一把互不相干的锁，互斥失效。
  这在 ACP 多会话下是必然触发的，不是理论问题。
  修法：锁表改为进程级共享（静态表 + 规范化工作区键），并加引用计数回收——
  否则长期运行的宿主会随工作区数量无界增长（反证：无回收时 20 轮后从 3 涨到 23）。
  `Delete` 侧不 Dispose 共享信号量：仍可能有其他实例在用同一个对象。
  新增 `-- workspace-lock` 三项 + 两条反证。
  177 项测试 + 32 套件全绿。

- 2026-10-25：修 G12/G13（协议内方言差异）。
  **G12 的成因是跨层契约用了 provider 字段名**：Agent 层与 format 里共七处
  直接读写 `reasoning_content`，接一家用 `reasoning` 的 provider 就会**静默丢弃
  思考内容**——不报错，只是下一轮上下文少东西；若对方要求回传则退化为 400
  且错误信息不含字段名。
  修法：Agent 层改用中性键 `AgentPropertyKeys.Reasoning`，只表达"这段是思考内容"
  这个语义；翻译交给连接器，由 `WireCompat.ReasoningField` 决定线上字段名。
  **G13**：`WireCompat.MaxTokensField`（null=不发，保持已验证端点行为）+
  `ChatOptions.MaxOutputTokens`；Anthropic format 从硬编码 4096 改为可配置。
  `-- wire` 新增三条：方言端到端往返（解码与编码必须用同一方言）、
  方言隔离（防"两个字段都读"的假实现）、输出上限三种配置。
  原特征化探针按设计失败并提示"缺陷已修"，改为验证修复本身——
  这个过程记录在代码注释里。
  反证：编码阶段写死旧字段名时，方言往返检查精确失败。
  177 项测试 + 31 套件全绿。

- 2026-10-25：**默认转正**。内核接线成为默认路径（`DOUGONG_KERNEL=0` 退回直连，
  保留为对照手段）。新增两条必须单独立的反向检查：
  `default-path-uses-kernel`（不设任何环境变量时默认走内核——原先
  `acp-path-uses-kernel` 显式设了 `DOUGONG_KERNEL=1`，默认值改没改它都通过，
  测不出默认行为）与 `opt-out-restores-direct-path`（开关必须真能退回）。
  反证：把默认改回"需显式启用"后，`default-path-uses-kernel` 精确失败。
  P1 由"部分"改为"达成（首项可替换能力）"。

- 2026-10-25：补两条此前未实现的验收 + 收口 G15。
  ① **验收 3（内核路径下审批仍生效）**：新增 `-- kernel-path` 三项。
  此前 `-- kernel-wiring` 里一个审批相关断言都没有——如果 `LeasedApprover`
  吞掉异常或把"取不到审批器"误判为批准，内核路径下审批会静默失效而测试全绿。
  反证：让 `LeasedApprover` 不调用真实审批器，两项断言精确失败。
  ② **验收 7（停用运行时后旧引用不能静默跑完）**：同套件。
  首版断言写错了——只看是否抛异常，而 `KernelAgentRuntime` 会把异常转成
  `RunFinished(Failed)` 事件（与"流永不抛异常"取向一致），所以永远"通过"。
  改为断言终态后暴露真实行为：`Failed` + `ObjectDisposedException` 诊断。
  ③ **G15 收口**：新增 `IApproverAccessor` / `IDispatcherAccessor`，
  `ToolDispatcher._approver` 与 `LoopAgentRuntime.dispatcher` 字段消失；
  `LoopAgentRuntime` 从主构造函数改为显式构造函数以承载两种注入方式。
  内核路径传 `KernelApproverAccessor`/`KernelDispatcherAccessor`（每次走租约），
  直连路径传 `Fixed*`（显式选择不参与替换）。96 处旧调用点不受影响。
  `-- bare-hold` 白名单从 14 条降到 12 条，两条缺口条目已消失。
  177 项测试 + 31 套件全绿。

- 2026-10-25：收口"编排者裸持有存储"。新增 `IStoreAccessor`（Agent 层契约）：
  `ToolDispatcher` 与 `LoopAgentRuntime` 不再接受 `IAgentStore` 实例，
  改为接受"每次提交自己取实例"的访问点；内核路径传 `KernelStoreAccessor`
  （走租约，替换后自动取新实现），直连路径传 `FixedStoreAccessor`（显式固定）。
  保留旧构造签名（内部包一层），96 处调用点不受影响。
  新增通用探针 `-- bare-hold`：反射扫描全部产品程序集，列出把可替换接口
  存成实例字段的地方，**默认判违规**，合法持有必须在白名单登记理由。
  首次运行扫出 45 条噪声（编译器生成的 async 状态机），排除后得到 8 条真实持有：
  修掉 `ToolDispatcher._store` / `LoopAgentRuntime.store`，其余 6 条逐条说明。
  反证：删掉白名单一条即精确失败。
  如实记录未收口项：审批器与 dispatcher 仍被编排者持有（G15）。

- 2026-10-25：按 P1 分析补齐三处缺口：
  ① **存储不再被宿主裸持有**——`SessionComposition` 的 `_store` 字段删除，
  改为方法参数；dispatcher/运行时插件在激活期内经内核解析 store。
  新增结构探针 `host-does-not-cache-store-instance`（反射查实例字段）。
  ② **ACP 入口接入内核**——此前 `--acp` 完全不经过内核，是"只在一个入口验证"的盲区。
  新增 `acp-path-uses-kernel`：真起 `--acp` 进程、走 initialize + session/new，
  断言 stderr 出现 kernel=enabled（元数据检查分不清两个入口，只有跨进程能）。
  ③ **可替换性验收升级为真实现**——原 `RecordingStore` 只有一个计数器，
  验的是"服务键能换提供方"，不是"换一种存储产品还能跑"。新增
  `InMemoryAgentStore`，对齐 JSONL 的序号分配（忽略调用方传入值）、
  提交失败不留半条、释放后拒绝；验收改为断言这些真实语义。
  三处各配一条反证。177 项测试 + 29 套件全绿。

- 2026-10-25：内核接线（G14）：新增 `KernelAgentHost`/`KernelAgentRuntime`，
  把存储、审批器、dispatcher、运行时作为插件注册进内核，宿主通过
  `IServiceReference.InvokeAsync` 使用它们；租约粒度取"一回合一次"。
  `DOUGONG_KERNEL=1` 启用，默认路径不变。新增两个套件 8 项验收 + 3 条反证。
  过程中发现三个真实缺陷：① `LeasedDispatcher.Tools` 抛异常会让主循环
  发不出工具声明（NotSupported），改为激活期内取冻结描述符快照；
  ② "放弃枚举释放租约"的初版断言是假绿——只测了停用够快，没测运行时循环真的被取消；
  ③ 收紧后连抓到两处测试自身缺陷（async iterator 里 yield 之后注册的取消回调永不执行；
  `Task.WhenAny(...) == task` 比较的是 Task<Task> 引用，永远为 false）。

- 2026-10-26：**判据 1 的第三步"配置模型跑真实任务"整节缺失，已补**。
  核对判据原文（"安装 → 配置模型 → 跑一个真实任务 → 写一个工具插件并装上"）
  发现本文档从头到尾没教"配置模型"这件事：§5 用假模型、§6 写插件，
  读者走完全程也不知道怎么真正用起来。**判据 1 有四步，文档只覆盖了三步。**
  已补 getting-started §5.5：三个环境变量、`--doctor` 先验证连通性、
  启动后怎么提问、会用到工具的任务、审批屏每一行的含义、`/exit` 与常用命令。
  写这一节时被实测纠正两处：审批提示实际是"批准？[y/N]"而非我想当然的措辞；
  更要紧的是**管道喂 `y` 不可靠**——审批提示与 REPL 提示争抢同一行输入，
  实测审批被拒（fail-closed 正确生效）。已在文档写明自动化要用
  `DOUGONG_AUTO_APPROVE=1` 并标注它关掉人工确认。
  实测（DeepSeek 真实 API）：纯问答通过；用工具任务走完
  "请求 → 审批 → 执行 → 回填 → 基于真实文件内容回答"；自动批准跑通完整链路。

- 2026-10-26：**getting-started.md 的三轮走查记录**（从该文档 §8 移到这里）。
  §8 原本写了大量"作者怎么验证的"细节，那是维护者记录，放在读者文档里读起来
  莫名其妙——读者关心"怎么用"，不关心"作者怎么测的"。已移出，§8 只留一句结论
  （验证到什么程度、哪里可能不准）。
  三轮走查的实际发现：
  ① 独立 agent 严格按文档走：§0–§5 与 §6.4 全部复现。修掉两处内容错误——
     套数写 34 实际 36、§2 漏了 `MSB3101` 的处理办法。
  ② 真人走到 §6.3 报 `CS0246: 找不到 SystemClock`。两个原因：读的是旧副本
     （`SystemClock` 当轮才补）、以及 §6.2 只写"复制 §1.2 那段 C#"而那段有
     三个类型，读者只复制"主要"的那个必然失败。已改成"整段复制"并点出三个类型，
     §6.3 补 `CS0246` 分类对照表。
  ③ 同一次走查暴露 §6.4 原来做不到"验证你自己的插件"——它只教读者跑
     `kernel-wiring`，而那加载的是内核自己的样例插件。补 `-- load-plugin <dll>`，
     走生产加载路径，验证卸载，失败时列出三类常见配置错误。
     对应检查 `-- load-plugin-checks` 会**从指南里提取代码块**生成插件再加载，
     这样"指南代码抄下来不能用"会被自动发现。

- 2026-10-26：**兼容策略成文 + ApiCompat 覆盖补齐**。
  此前只有 Kernel 与 Agent 配了 `EnablePackageValidation`，**Ai / Mcp / Acp 三个包
  共 37 个公开类型完全没有保护**——破坏性改动不会被发现。现在五个 SDK 包全部纳入，
  基线在 `artifacts/baseline/`；Cli 明确排除（它是工具不是库，公开类型不是契约）。
  `docs/compat-policy.md`：承诺什么/不承诺什么、哪些包属于契约面、
  破坏性变更分三档（直接破坏 / 行为破坏 / 不算破坏）、弃用流程（`[Obsolete]` 必须写
  替代方案与预期移除版本，至少跨一个次版本）、实验性边界（本项目不用
  `[Experimental]`，理由是它产生 error 而非 warning）、版本号规则、以及四条已知不完善。
  验收：`-- apicompat` 4 项 + 反证 2 条全部变红。其中 `breaking-change-is-detected`
  **真的改代码再改回来**（给 `ChatClientFactory.Create` 加必填参数 → 断言 `CP0002` 出现），
  不是只检查配置属性存在——那属于"检查配置而不是检查行为"。
  期间修掉自己的两处错误：跨行字符串在 CRLF 文件上匹配不上（本项一度"永远跳过"）、
  注释里写 `
` 被当成真实换行导致编译失败。

- 2026-10-26：**I0 补齐：`--report` 可复现信息包**（doctor/日志/故障排查三块之后的第四块）。
  把排查需要的事实收成一份可整段粘贴的文本：版本与 commit（取自程序集
  `InformationalVersion`，不是硬编码——顺带发现 `Program.cs` 里版本号重复硬编码了两处）、
  平台、provider 配置形态、最近会话元信息、其它开关。
  **硬约束：绝不含密钥与用户内容。** 这份文本的用途就是被贴进公开 issue，
  泄露撤不回来。密钥只报「是否设置 / 长度 / 是否含首尾空白」——连前缀都不给，
  因为前几位会缩小猜测空间而它对诊断没价值；会话只给文件名/大小/记录数，不读内容。
  同样排在 provider 配置检查之前，缺配置时也能生成。
  验收：`-- report` 6 项 + 反证 6 条全部变红（含「泄露密钥」「泄露会话内容」两条）。

- 2026-10-26：**I0 稳定化起步：启动自检 + 分级日志 + 故障排查文档**。
  ① `--doctor`：逐项检查运行时、provider 配置（含连通性）、工作区可写、
  会话目录可写、L1 记忆开关，每项失败都给「下一步」。退出码 0/1 表示能否启动。
  **关键设计**：它排在 provider 配置检查**之前**——否则缺配置时程序先退出 2，
  自检永远跑不到，而那正是最需要它的时候。这条有专门的反证守着。
  密钥只报「已设置(长度)」，绝不打印；连通性失败只算警告（离线也要能用其它功能）。
  ② `DOUGONG_LOG` 四级日志（error/warn/info/debug）。**未设置时行为与之前完全一致**
  ——默认不刷任何新输出，这条也有反证。`info` 给出排查最先要问的事实，
  `debug` 给每轮细节。一律写 stderr，不打印密钥与用户输入原文。
  无法识别的值明确警告而非静默忽略。
  ③ `docs/troubleshooting.md`：每一条都来自实际遇到的问题（MSB3101、内存不足、
  401/403/429、连接重置、插件类型不匹配、清理同步阻塞），并注明「这是缺陷还是环境问题」。
  验收：`-- doctor` 9 项 + 反证 8 条全部变红。其中一条反证最初没命中——
  阈值改成 `<` 时「恰好等于阈值」那档仍放行，改用 `>=` 才打到。

- 2026-10-26：**上下文卫生两项**（对照外部长期记忆实践审视后的结果）。
  ① `ToolResultLimits` + `AgentEvent.ToolResultTruncated`：工具结果进入**模型上下文**时
  按 32K 字符裁剪（保头 24K + 尾 6K + 显式标记）。此前一次 `read_file` 读大文件
  就能挤爆窗口。**位置是关键**：只在拼装模型历史处裁，日志与会话恢复保持完整原文
  ——在 dispatcher 出口或写日志前裁会造成数据丢失且无法还原。
  ② `SlidingWindowCompactor.TrimToExcludeLatestPlan`：最近一次 `todo_write` 计划
  不再被压进摘要区间。只改压缩器，不动 runtime、不碰 `IMemoryInjector` 语义
  ——比"每轮重新注入计划"的破坏性小得多。
  验收：`-- context-hygiene` 10 项（其中 3 项走真实运行时 + 真实 dispatcher，
  只测 `Clamp` 本身测不出"裁剪没接上"）+ `-- compaction` 新增 5 项；
  **反证 8 条全部如期变红**，含"把裁剪从主路径摘掉"这条。
  期间修掉三处自己的假检查/假反证：`store-keeps-full-content` 原本手工构造
  `StoreRecord`（测的是 JsonlAgentStore 不裁剪，而它本来就不裁剪）、
  `mut01` 用 `if (true)` 触发 CS0162 变构建失败、两条反证的预期检查项写错。
  **复查时又读出一个真实缺口**：模型历史有**两条**写入路径
  （`AppendToolExchange` 与 `BuildHistory`→`ToChatMessage`），第一版只裁了第一条。
  恢复含大工具结果的会话时模型收到 64 011 字符（上限 32 000），
  三条累积到 192 033。已修并补两条检查 + 一条反证。
  这个缺口不是测出来的，是读代码找出来的——"改一处就以为覆盖了"本身就是缺陷来源。
  ③ 记忆取代关系**决定不做**并写明理由（L1 只有 3 个白名单键，冲突不存在；
  且是公开类型 + 持久化格式双重改动）。见 [context-hygiene.md](context-hygiene.md)。

- 2026-10-26：**Skill L1 记忆层落地**（M2 唯一未闭合的范围项）。
  新增 `MemoryArtifact`/`MemoryStore`/`StoreMemoryInjector`/`MemoryPrompt`/`MemoryDiagnostics`，
  白名单硬编码（`language`/`output.format`/`naming.convention`），审批绑定内容 SHA-256，
  产物与启用严格分离，跨工作区默认不传播。运行时经 `Memory`/`MemoryWorkspace`
  两个 `init` 属性接入（不改构造签名）；CLI 新增 `/memory` 与 `DOUGONG_MEMORY`（默认关闭）。
  验收 2–6：`-- skill-l1` 7 项 + `-- memory-cli` 4 项（真实进程）。反证 10 条全部如期变红
  （`scripts/skill-l1-counterproofs.sh`）。
  期间修掉三个真缺陷：① 存储把 payload 二次编码，读端取不到 `kind`，所有记录读不出来
  ——而"全部拒绝加载"的断言在"一条也读不出来"时照样通过，属假通过，已补正向对照；
  ② 读取按"最高版本胜"选记录，版本号未递增的撤销被静默忽略，改为"最后写入胜"
  且 `PutAsync` 拒绝版本倒退；③ `/memory` 把过期项显示为"生效"（展示层自写判定漏了过期），
  改为与注入器共用 `MemoryDiagnostics`。
  **未做验收 1（留出任务对比）**：需真实模型 + 独立评测集 + 预先记录的指标，
  因此只能说"记忆管道安全且可撤销"，不能说"记忆改善了任务表现"。

- 2026-10-25：ACP elicitation：`ask_user` 工具化实现，form 模式 1–4 题 × 2–4 选项。
  11 项验收（10 项进程内 + 1 项真实 `dougong --acp` 进程），5 条反证逐条命中。
  期间发现一个真缺陷：dispatcher 会对每次调用请求审批，导致用户先被问
  "允许提问吗"再看到问题本身；引入 `PermissionPolicyApprover` 限定跳过范围
  （已知 + 无访问 + 无路径/命令/域名），并用三种"不该跳过"的工具反证边界。
  如实记录未做：ACP `plan`（缺数据源，靠工具序列猜会把推测伪装成计划）。

- 2026-10-24：入口能力对齐：抽出 SessionComposition/AcpComposition，
  REPL 与 ACP 走同一装配。5 项一致性验收（含反证 3 项）。
  修掉压缩在会话层不持久、MCP 资源不释放两个真缺陷。
  期间两次修正自己的假断言（压缩判据），并记录用户规则「不做假通过的测试」。

- 2026-10-24：M2.4 会话恢复：load/resume/list/close 实现，20 项验收（含四个反证）。
  真实进程验证暴露一个真缺陷：纯文本轮次从不提交 ModelResponse，
  所有纯聊天会话恢复后只剩用户消息。已修并补回归。
  同时记录用户规则「不做假通过的测试」，并据此审出一处假断言：
  审批用例的 expectExecuted 形参从未被使用。

- 2026-10-24：M2.4 补完审批接线：AcpApprover 接入 CLI 的 dispatcher，审批走
  session/request_permission。13 项验收（新增批准/拒绝/无回复三种情形）。
  修掉两个真缺陷：host 收到响应后直接忽略（审批永远挂着）、
  runtime 与 dispatcher 由两个工厂分别构造（审批器挂在不使用的实例上）。

- 2026-10-24：M2.4 ACP Agent 端：按行分帧、版本协商、session/new / prompt / cancel、
  5 种 update 翻译、权限请求适配；10 项验收（含 stdout 纯净与事件顺序）。
  修掉一个真缺陷：session/prompt 在读循环里阻塞，导致 cancel 通知读不到。
  CLI 加 --acp 模式，真实进程级验证通过。

- 2026-10-24：M2.3 指标导出：DougongTelemetry 基于 BCL Meter，5 项验收
  （未报告与零可区分、失败带 result 标签、无高基数标签）。真实端点确认产出。
  另做修复复检（-- recheck 4 项），抓到 3 个上轮没覆盖的缺陷：
  审批窗口内工具替换后旧审批仍放行（真缺陷，已修）、联合类型 schema 被静默跳过、
  布尔形态 exclusiveMinimum 直接抛异常。

- 2026-10-24：清掉 G4/G6/G8 三项遗留（触发条件均由 M2 满足）：
  G4 完整 schema 校验（不支持关键字必须报错）、G6 工具替换后旧审批失效（4 项）、
  G8 token 估算按实测校准（ASCII 8 字符/token、CJK 1.2，改分段模型）。
  另新增 scripts/all-checks.sh —— 固化「反证后必须单独重建 spike」这条纪律，
  该坑已重复四次。

- 2026-10-24：M2.2 MCP 变化检测：McpToolRegistry 订阅 list_changed 并全量刷新，
  4 项验收用真实 server 主动通知触发（含反证 4/4）。
  修掉一个只有真实端点能发现的缺陷：工具名用 server/tool 形式被 provider 400 拒绝
  （只接受 ^[a-zA-Z0-9_-]+$），改用 server__tool。

- 2026-10-24：M2 起步：MCP 工具桥接（ModelContextProtocol 2.3.0）。
  远端工具包装为 IAgentTool 走同一 dispatcher；annotations 不作授权、schema 变化改版本、
  IsError 记失败。8 项验收用真实 client/server 内存配对（含三个反证）。
  修掉一个契约不一致缺陷：模型只看得见工具 Name，而 dispatcher 只按 Id 查找
  ——M1 里 Id==Name 从未暴露，MCP 的 server 前缀立刻触发了它。

- 2026-10-24：M1 G2/G3 修复：审批决定与运行终态从未落盘（两处枚举值使用点均为 0）。
  dispatcher 新增可选 store，审批后提交绑定参数摘要的记录，提交失败阻止执行；
  新增 ApprovalLog/ApprovalSnapshot 让「有调用但无审批记录」可表达；
  RunFinished 收口到单一出口。7 项自检（含两个反证），真实日志六段记录齐全。

- 2026-10-24：M1 G1 修复：上下文压缩从未接入循环（能力只在测试里）。探针先确认缺口
  （10 200 tokens 完整历史全量送进模型），再接入 `ContextLimits` + 压缩器 + 摘要器；
  循环内交互不参与压缩以保证工具配对完整；四种降级路径都保留原历史并发出诊断事件。
  9 项接入验收（含反证），真实端点 12 轮会话 9 418 → 3 445 tokens。
  同时新增 [plan.md](plan.md) 作为排期文档，并登记 10 项缺口台账。

- 2026-10-24：内核对抗性审查：6 个维度共 20 项攻击探针（`KernelAdversarialTests`、
  `KernelDrainProbeTests`）。发现并修复 1 个真实缺陷：同步阻塞的激活回调会独占
  评估循环，使 `ActivationTimeout` 完全无效，其它插件的生命周期变更无限排队。
  修法是回调改为 `Task.Run` 启动；修复前先实测验证了 `Task.Run + WaitAsync` 的
  超时有效性与 `AsyncLocal` 的跨线程传递语义，并有反证。其余 15 类攻击未击穿
  不变量。详见 [kernel-adversarial-review.md](kernel-adversarial-review.md)。

- 2026-10-24：修掉一个 M0 遗留的真实内核缺陷：插件装配回落到宿主进程中
  未登记的共享契约时，加载会静默成功。根因是 `PluginLoadContext.Load` 在插件目录
  解析不到时返回 null，运行时于是回落到默认 ALC——只要同名程序集碰巧已在进程里，
  「登记是加载前置条件」就被绕过。表现为同一份插件在干净进程里失败、在跑过相关代码的
  进程里成功，CI 与本地结果不一致。修法是记录这类回落并在 `LoadAsync` 中拒绝，
  同时排除共享框架程序集（它们回落是唯一正确行为）。附回归测试 + 反证。

- 2026-10-24：CI 修复：测试命令从 VSTest 的 `--logger` 改为 MTP 的 `--output Detailed`
  （前者在 MTP 下表现为 Zero tests ran + 握手失败）；共享契约测试改为用独立可回收 ALC
  制造污染条件，不再把契约永久留在进程里。

- 2026-10-24：M1 用量记账：`UsageRecordingChatClient` 装饰器 + `UsageLedger` 会话/按日聚合，
  11 项自检（含两个反证）。修掉一个真实缺陷：usage 走自定义附加属性时 M.E.AI 的聚合
  与中间件看不见，账目会静默丢失；改用标准 `UsageContent`。真实端点验证 352/3 tokens。

- 2026-10-24：M1 shell 工具：`ShellTool` + `ShellEnvironment` 落地，11 项验收（含两个反证）。
  修掉一个真实安全缺陷：`ProcessStartInfo.Environment` 初始含父进程全部变量，
  只加白名单不等于只传白名单，宿主密钥会被子进程继承。CLI 侧默认不启用，需显式开启。

- 2026-10-24：依赖版本与测试栈核对：对照 NuGet 最新稳定版，AI 抽象 10.11.0 与 MAF 1.26.0
  已是最新；测试栈从 xUnit v2 + VSTest 迁到 xUnit v3 4.0.2 + Microsoft.Testing.Platform
  （删除 `Microsoft.NET.Test.Sdk` 与 `xunit.runner.visualstudio`）。迁移暴露并修复一个
  测试自身的并发缺陷（线程池复用导致线程 ID 断言不稳定，反证 5 轮中 2 轮失败）。

- 2026-10-24：M1 会话恢复与分支：`SessionRecovery`/`SessionBranch` 落地，
  CLI 支持 `--list`/`--resume`/`--branch --at N`；9 项会话自检（含三个反证）。
  期间修掉两个真实缺陷：日志 Payload 双重编码、在工具交互中间分叉留下悬空 tool_calls
  （实测 400）。真实端点验证过恢复后续跑能准确复述上一轮工具读到内容。

- 2026-10-24：M1 Ai 层：三种线协议拆成可独立校验的实现，共用 HTTP 传输与 `ChatClientFactory`；
  一次性适配器与 CLI 自带连接器删除，真实 provider 证据落在交付代码上。
  10 项线格式自检（`-- wire`）首次覆盖多轮回填；三协议真实端点各 5/5。
  期间修掉四个真实缺陷：`finish_reason` 读错字段（一直被兜底掩盖）、工具结果 Text 为空、
  文本与 tool_calls 拆成两条消息导致 400、审批拒绝后同样触发。四项均有反证或真实复现。

- 2026-10-24：M1 故障验收：补 6 项故障用例（同文件并发编辑、读写竞争、取消不合作、单一执行入口、
  提交失败先于副作用、崩溃窗口报未知结果），重复 5 轮全绿。该套件当场抓到一个真实缺陷——
  `Serial` 与 `WorkspaceExclusive` 原本各用一把独立信号量，串行工具能与同工作区独占工具重叠执行；
  已改为共用同一把工作区执行权，并用改回双锁的反证确认检查有效。156 项内核测试与全部自检套件通过。
- 2026-10-24：M1 真实 provider 验证：用 MiMo 与 MiniMax 各跑 5 项真实调用（含真实编码任务），全部通过；期间修复三个仅真实协议暴露的缺陷（工具声明未传给模型、空 choices 越界、参数分片语义）。
- 2026-10-24：M1 首批：落地 `IAgentRuntime`/`IToolDispatcher`/`IAgentTool` 契约与最小 dispatcher；用 10 项确定性反例对比 MAF 包装与自研循环，均 10/10，选择自研循环（详见 m1-candidate-comparison.md）。真实 provider 对比待验证。
- 2026-10-23：补漏配路径的进程内回归：新增 `Dougong.Sample.Isolated`（测试项目刻意不引用）与对应插件，未登记时装配解析必然失败，因此能在 CI 中真实覆盖拒绝与回滚；测试先断言该前提，误引用会显式失败而非静默假通过。同时修正 `LoadAsync` 在类型发现阶段抛 `ReflectionTypeLoadException` 时未给出可操作诊断的问题。Release 构建与 148 项内核测试通过。
