# Dougong 文档索引

> 这里只放**对外文档**：用 SDK、写插件、嵌入到自己的程序、排查故障时会读的内容。
>
> 内部工作记录（状态台账、排期、里程碑计划、开发历程）不在此仓库——
> 它们是维护过程中的中间产物，随发现调整，不适合作为对外承诺。

## 按读者分组

### 我想先用起来

| 文档 | 内容 |
|---|---|
| [getting-started.md](getting-started.md) | 从零开始：装 SDK 到写出第一个插件。每条命令都写明"应该看到什么"。不需要先懂 .NET |
| [plugin-author-guide.md](plugin-author-guide.md) | 插件作者指南：从工程文件到最小插件，含五个易犯错与失败定位 |
| [troubleshooting.md](troubleshooting.md) | 故障排查对照表：构建、provider、插件加载的实际案例与判断依据 |

### 我要把它嵌进自己的程序

| 文档 | 内容 |
|---|---|
| [embedding-guide.md](embedding-guide.md) | 嵌入指南：20 行最小可用 + 五个零件的职责 + 事件流怎么用（含跨进程宿主的实践） |
| [compat-policy.md](compat-policy.md) | 兼容策略：承诺什么、哪些包受保护、破坏性变更三档、弃用流程 |

### 我要理解它为什么这样设计

| 文档 | 内容 |
|---|---|
| [architecture.md](architecture.md) | 系统长什么样、怎么运转：分层、内核契约、状态机、动态加载、宿主边界。多处代码注释引用其章节号 |
| [design.md](design.md) | 为什么这样设计：生态位、里程碑、MAF 复用决策 |
| [goal.md](goal.md) | **四条不可让渡的性质与反目标**。决定"要不要做某件事"时的判据 |
| [host-layering-decision.md](host-layering-decision.md) | 宿主分层：什么进 SDK、什么留在宿主，以及为什么 |
| [desktop-capability-feedback.md](desktop-capability-feedback.md) | 桌面端提的能力需求，SDK 逐条核对后的回应：哪些成立、哪些理由要修正、哪些明确不做 |
| [kernel-vs-cordis.md](kernel-vs-cordis.md) | 与 dsh/Cordis 内核的一手对比：租约/排空/超时/变化传播逐项对照，以及已定的取舍 |

### 选型与参照依据

这些文档记录"当时为什么选这条而不是那条"，代码注释直接引用它们。

| 文档 | 内容 |
|---|---|
| [m1-candidate-comparison.md](m1-candidate-comparison.md) | MAF 包装 vs 自研循环的同用例对比与选择理由。**代码注释引用最密的一份** |
| [acp-reference-survey.md](acp-reference-survey.md) | ACP 协议参照调查：实际做法、三个真实坑、plan 数据源的取舍 |
| [performance-baseline.md](performance-baseline.md) | 性能基线：大会话/长会话/并发会话的实测数字与环境。只记录不承诺 |

### 品牌

| 文档 | 内容 |
|---|---|
| [brand](../assets/brand/README.md) | 标志规范与素材清单（在 `assets/brand/`） |
| [brand-archive](brand-archive/README.md) | 已废弃的标志方案，仅供追溯 |

## 阅读路径

```text
想先用起来     getting-started.md → plugin-author-guide.md → troubleshooting.md
嵌进自己的程序 embedding-guide.md → compat-policy.md
理解设计       architecture.md（按需查章节）→ design.md → goal.md
评估可不可以信 goal.md（四条性质）→ compat-policy.md → performance-baseline.md
```

## 这份索引的边界

- **不列内部工作记录**。想做贡献请先看 [architecture.md](architecture.md) 与
  [design.md](design.md)。
- **`architecture.md` 与 `design.md` 是长文档**，适合按章节查，不适合通读。
- 引用了本文档之外的内部材料时，那些材料不在本仓库。
