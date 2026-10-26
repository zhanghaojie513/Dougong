# Dougong 文档索引

> 新会话从这里进入。先读 [goal.md](goal.md) 知道要达成什么，再按 §入门顺序 往下读。

## 归类规则

```text
根目录   = 描述当前系统：契约、决策、状态。改动会约束实现，代码注释直接引用这些路径。
vision/  = 方向与理念：前瞻调查、远期路线、设计哲学。是思考材料，不是已实现能力的承诺。
journey/ = 开发记录：各里程碑真实发生的问题与判断。复盘素材，不是验收依据；按里程碑命名（m0、m1…）。
```

判断标准：**如果一份文档的内容变了，代码要不要跟着改？** 要，就放根目录；不要，按性质进 `vision/`（面向未来）或 `journey/`（记录过去）。

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

| 文档 | 内容 | 谁在读 |
|---|---|---|
| [goal.md](goal.md) | **最终目的**：四条不可让渡的性质、反目标、可执行的达成判定 | 所有人；决定"要不要做"时先读 |
| [quality-gates.md](quality-gates.md) | **完成定义**：六道门、红线、按规模裁剪 | 每一片交付自检；发布与关闭里程碑 |
| [architecture.md](architecture.md) | 系统长什么样、怎么运转：分层、内核契约、状态机、动态加载、宿主边界 | 实现者；多处代码注释引用其章节号 |
| [design.md](design.md) | 为什么这样设计：生态位、里程碑、MAF 复用决策、桌面端双轨计划 | 决策与评审 |
| [plan.md](plan.md) | 下一步做什么：里程碑顺序、缺口台账、判定标准 | 决定优先级时读 |
| [status.md](status.md) | 现在到哪了：已实现、测试基线、未完成验收、恢复入口 | 动手前必读；只认实测证据 |
| [acp-reference-survey.md](acp-reference-survey.md) | M2.4 前置调查：TKCode（ACP Client）与 pi.net 的实际做法与可借鉴/不可借鉴项 | M2.4 实现者 |
| [g14-kernel-wiring-plan.md](g14-kernel-wiring-plan.md) | G14 接线方案：租约粒度取舍（一回合一次）、目标装配、7 项验收与反证方式 | G14 实现者 |
| [getting-started.md](getting-started.md) | 从零开始：装 SDK 到写出第一个插件，每条命令都写明"应该看到什么"。给没写过 .NET 的人 | 第一次动手时读 |
| [plugin-author-guide.md](plugin-author-guide.md) | 插件作者指南：从工程文件到最小插件，含五个易犯错与失败定位 | 写插件时读 |
| [skill-l1-plan.md](skill-l1-plan.md) | Skill L1 记忆层：实现范围、验收 2–6 的证据、验收 1 为何未做 | 看记忆层边界时读 |
| [skill-l1-acceptance-1.md](skill-l1-acceptance-1.md) | 验收 1：记忆效果对照的装置、判据、预先登记阈值与结果解释规则 | 跑或解读记忆效果时读 |
| [context-hygiene.md](context-hygiene.md) | 上下文卫生两项改动（工具结果裁剪、压缩保留计划）与"记忆取代关系为何不做"的评估 | 改上下文/压缩逻辑时读 |
| [troubleshooting.md](troubleshooting.md) | 故障排查对照表：构建、provider、插件加载的实际案例与判断依据 | 出问题时读 |
| [compat-policy.md](compat-policy.md) | 兼容策略：承诺什么、哪些包受保护、破坏性变更三档、弃用流程、实验性边界 | 改公开 API 时读 |
| [performance-baseline.md](performance-baseline.md) | 性能基线：大会话/长会话/并发会话的实测数字与环境。只记录不承诺 | 做性能对比时读 |
| [m3-plan.md](m3-plan.md) | M3 计划：四条完成判据、七个切片顺序、`Experimental` 决策点、M2 遗留的三条待办登记 | 启动 M3 前读 |
| [kernel-vs-cordis.md](kernel-vs-cordis.md) | 与 dsh/Cordis 内核的一手对比：租约/排空/超时/变化传播逐项对照，已定取舍（不做热更新），以及"对等的 .NET dsh"这个定位为何不成立 | 决定内核方向与对外表述时读 |
| [m1-candidate-comparison.md](m1-candidate-comparison.md) | M1 候选对比：MAF 包装 vs 自研循环的同用例对比与选择理由 | M1 实现者；Agent 代码注释引用。M1 关闭后移入 `journey/` |

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
实现某特性   architecture.md 对应章节 → design.md 相关联动 → 测试基线（status.md）
评估方向     vision/frontier-roadmap.md → vision/iteration-roadmap.md
复盘教训     journey/m0-dev-journey.md（实现同类能力前先看）
```

> `vision/` 与 `journey/` 的内容不随实现状态自动同步；引用其中结论时先对照 [status.md](status.md) 核实是否仍然成立。
