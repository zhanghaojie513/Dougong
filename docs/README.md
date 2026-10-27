# Dougong 文档索引

> 新会话从这里进入。先读 [goal.md](goal.md) 知道要达成什么，再按 §入门顺序 往下读。

## 归类规则

```text
根目录   = 描述当前系统：契约、决策、状态。改动会约束实现，代码注释直接引用这些路径。
vision/  = 方向与理念：前瞻调查、远期路线、设计哲学。是思考材料，不是已实现能力的承诺。
journey/ = 开发记录：各里程碑真实发生的问题与判断。复盘素材，不是验收依据；按里程碑命名（m0、m1…）。
```

判断标准：**如果一份文档的内容变了，代码要不要跟着改？** 要，就放根目录；不要，按性质进 `vision/`（面向未来）或 `journey/`（记录过去）。

两条由此推出的例外，都已实际发生过：

- **已关闭的计划留在根目录**。`m3-plan.md`、`skill-l1-*.md`、`g14-*` 都已结题，
  但都有活跃引用：`m1-candidate-comparison.md`（55 处）与 `acp-reference-survey.md`
  （18 处）被 `src/` 代码注释直接引用；`m3-plan`（6 处）、`skill-l1-acceptance-1`
  （5 处）被 `README.md`/`CHANGELOG.md`/`scripts/` 引用；`g14` 被 `architecture.md §5`
  当作"租约粒度为什么是一回合一次"的依据引用。挪走会让这些引用指向 404，
  代价大于收益。它们的作用从"待办"变成"决策依据"，索引里按这个说法列。
- **`journey/` 按里程碑命名，不按主题**。它记录的是"当时怎么想的、错在哪"，
  一旦按主题重组，就退化成第二份设计文档。

## 入门顺序（新会话按此读）

```text
1. goal.md            为什么做、什么算达成      ← 判定标准，先读这个
2. status.md          实际做到哪一步            ← 现在在哪
3. plan.md            下一步做什么              ← 接着做什么
4. quality-gates.md   每一片必须过哪些门        ← 怎么算做完
5. architecture.md    怎么设计（按需深入）      ← 怎么做
```

前四份合计约 700 行，读完就能判断"现在该不该动手、动手要满足什么"。
其余文档按需查阅。

## 当前系统（根目录）

按"读它是为了做什么决定"分组。状态栏写的是**今天**的状态：标「已关闭」的是历史记录，
读它是因为代码注释会引用、或复盘时需要，不是待办。

### 判定与台账

| 文档 | 内容 | 谁在读 |
|---|---|---|
| [goal.md](goal.md) | **最终目的**：四条不可让渡的性质、反目标、可执行的达成判定 | 所有人；决定"要不要做"时先读 |
| [quality-gates.md](quality-gates.md) | **完成定义**：六道门、红线、按规模裁剪 | 每一片交付自检；发布与关闭里程碑 |
| [plan.md](plan.md) | 下一步做什么：里程碑顺序、缺口台账、判定标准 | 决定优先级时读 |
| [status.md](status.md) | 现在到哪了：已实现、测试基线、未完成验收、恢复入口 | 动手前必读；只认实测证据 |

### 契约与设计

| 文档 | 内容 | 谁在读 |
|---|---|---|
| [architecture.md](architecture.md) | 系统长什么样、怎么运转：分层、内核契约、状态机、动态加载、宿主边界 | 实现者；多处代码注释引用其章节号 |
| [design.md](design.md) | 为什么这样设计：生态位、里程碑、MAF 复用决策、桌面端双轨计划 | 决策与评审 |
| [compat-policy.md](compat-policy.md) | 兼容策略：承诺什么、哪些包受保护、破坏性变更三档、弃用流程、实验性边界 | 改公开 API 时读 |

### 上手与运维

| 文档 | 内容 | 谁在读 |
|---|---|---|
| [getting-started.md](getting-started.md) | 从零开始：装 SDK 到写出第一个插件，每条命令都写明"应该看到什么"。给没写过 .NET 的人 | 第一次动手时读 |
| [plugin-author-guide.md](plugin-author-guide.md) | 插件作者指南：从工程文件到最小插件，含五个易犯错与失败定位 | 写插件时读 |
| [embedding-guide.md](embedding-guide.md) | 嵌入指南：把 Dougong 装进自己的 .NET 服务（20 行最小可用 + 五个零件的职责 + 三个真实缺口） | 嵌入到自己的程序时读 |
| [troubleshooting.md](troubleshooting.md) | 故障排查对照表：构建、provider、插件加载的实际案例与判断依据 | 出问题时读 |
| [performance-baseline.md](performance-baseline.md) | 性能基线：大会话/长会话/并发会话的实测数字与环境。只记录不承诺 | 做性能对比时读 |

### 决策依据（为什么是现在这个形状）

| 文档 | 内容 | 谁在读 |
|---|---|---|
| [kernel-vs-cordis.md](kernel-vs-cordis.md) | 与 dsh/Cordis 内核的一手对比：租约/排空/超时/变化传播逐项对照，已定取舍（不做热更新），以及"对等的 .NET dsh"这个定位为何不成立 | 决定内核方向与对外表述时读 |
| [host-layering-decision.md](host-layering-decision.md) | 宿主分层：dsh 与 pi.net 怎么切库与应用，我们学什么、不学什么（不抽 CodingAgent 式产品层） | 决定 CLI/Desktop/SDK 边界时读 |
| [m1-candidate-comparison.md](m1-candidate-comparison.md) | M1 候选对比：MAF 包装 vs 自研循环的同用例对比与选择理由 | **代码注释引用最密的一份**（`src/` 与 `scripts/` 下 53 处，`LoopAgentRuntime`/`ToolDispatcher`/`MafAgentRuntime` 等） |
| [acp-reference-survey.md](acp-reference-survey.md) | ACP 协议参照调查：TKCode 与 pi.net 的实际做法、三个真实坑、plan 数据源的取舍 | 代码注释引用 17 处（`AcpFraming`/`AcpElicitationTool`/`AcpChecks`）；改 ACP 容错时读 |
| [g14-kernel-wiring-plan.md](g14-kernel-wiring-plan.md) | G14 接线方案：租约粒度取舍（一回合一次）、目标装配、7 项验收与反证方式 | **已关闭（2026-10-25）**，现为默认路径；`architecture.md §5` 引用它。仅在 M2 复盘时读 |
| [kernel-adversarial-review.md](kernel-adversarial-review.md) | 内核 6 维对抗审查：确认 1 个真实缺陷（同步阻塞绕过激活预算）、20 项探针与已坚守的不变量 | 改内核生命周期/租约前读；也是"哪些不变量已被攻过"的清单。`status.md` 与 `journey/m1` 引用 |

### 已完成切片（记录结论，不是待办）

| 文档 | 内容 | 谁在读 |
|---|---|---|
| [m3-plan.md](m3-plan.md) | M3：四条完成判据、七个切片、`Experimental` 决策点。**已关闭**，出口记录见 §九 | 想知道"为什么不用 Experimental"时读 §五；判据证据在 §九 |
| [skill-l1-plan.md](skill-l1-plan.md) | L1 记忆层：实现范围、验收 1–6 的证据与三个真实缺陷。**已关闭** | 看记忆层边界、L2/L3/L4 为何不做时读 |
| [skill-l1-acceptance-1.md](skill-l1-acceptance-1.md) | 验收 1：记忆效果对照的装置、判据、预先登记阈值与结果解释规则。**已完成**，14/14 组合达标 | 跑或解读记忆效果、引用"提升 60–90%"这个数字时读 |
| [context-hygiene.md](context-hygiene.md) | 上下文卫生两项改动（工具结果裁剪、压缩保留计划）与"记忆取代关系为何不做"的评估 | 改上下文/压缩逻辑时读 |

## 方向与理念（vision/）

| 文档 | 内容 |
|---|---|
| [philosophy.md](vision/philosophy.md) | 斗拱隐喻、设计原则、明确不做的事情 |
| [frontier-roadmap.md](vision/frontier-roadmap.md) | Agent Harness、协议、隔离、记忆评测的外部公开进展调查 |
| [iteration-roadmap.md](vision/iteration-roadmap.md) | M3 之后的内部迭代排序与退出条件 |
| [skills-evolution.md](vision/skills-evolution.md) | Skill 复盘、沉淀、审批与加载的进化设想 |

## 开发记录（journey/）

| 文档 | 内容 |
|---|---|
| [m0-dev-journey.md](journey/m0-dev-journey.md) | M0 期间真实发生的问题与判断错误，供开发记录梳理 |
| [m1-dev-journey.md](journey/m1-dev-journey.md) | M1 期间 15 个真实缺陷、5 个测试缺陷与 4 类判断错误；核心教训是「假模型全绿不等于协议正确」 |
| [m2-dev-journey.md](journey/m2-dev-journey.md) | M2 期间 11 个实现缺陷、7 个测试缺陷与 5 类判断错误；核心教训是「177 项测试全绿而核心组件从未被使用」 |

## 阅读路径

```text
第一次接触   status.md → architecture.md §1–3 → design.md §1–2
想动手写插件 getting-started.md → plugin-author-guide.md → troubleshooting.md
嵌入到别处   embedding-guide.md → compat-policy.md
实现某特性   architecture.md 对应章节 → design.md 相关联动 → 测试基线（status.md）
评估方向     vision/frontier-roadmap.md → vision/iteration-roadmap.md
复盘教训     journey/m0-dev-journey.md（实现同类能力前先看）
```

> `vision/` 与 `journey/` 的内容不随实现状态自动同步；引用其中结论时先对照 [status.md](status.md) 核实是否仍然成立。
>
> 根目录里标「已关闭」的几份（m3-plan、skill-l1-*、g14）**不移动**：`src/`、`scripts/`、
> `spike/`、`README.md` 与 `CHANGELOG.md` 里有 80 多处路径引用会失效。
> 它们的作用已从"待办"变成"决策依据"。
