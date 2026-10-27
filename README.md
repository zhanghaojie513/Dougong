# Dougong（斗拱）

<p align="center">
  <strong>Everything is a Plugin · .NET Agent Harness SDK</strong>
</p>

<p align="center">
  <span>简体中文</span> · <a href="README.en.md">English</a>
</p>

<p align="center">
  用可组合的插件内核，承托 Agent 运行时、工具、协议与宿主。
</p>

<p align="center">
  <a href="https://github.com/zhanghaojie513/Dougong"><img src="https://img.shields.io/badge/repository-Dougong-2f6f5e?style=flat-square" alt="Repository"></a>
  <a href="https://github.com/zhanghaojie513/Dougong/actions/workflows/build.yml"><img src="https://img.shields.io/github/actions/workflow/status/zhanghaojie513/Dougong/build.yml?style=flat-square&label=build" alt="Build"></a>
  <a href="https://dotnet.microsoft.com/"><img src="https://img.shields.io/badge/.NET-10.0-512bd4?style=flat-square" alt=".NET 10"></a>
  <a href="https://github.com/zhanghaojie513/Dougong/releases"><img src="https://img.shields.io/badge/version-0.1.0--alpha.1-orange?style=flat-square" alt="Version 0.1.0-alpha.1"></a>
  <a href="LICENSE"><img src="https://img.shields.io/badge/license-MIT-1f883d?style=flat-square" alt="MIT License"></a>
</p>

> [!WARNING]
> **早期开发中，暂不建议依赖。** M0–M3 已完成，四条完成判据均已达成，但版本仍是 `0.x`，
> **不承诺向后兼容**：公开 API 的每一次变更都会被 ApiCompat 自动检出并记入
> [CHANGELOG](CHANGELOG.md)，规则见[兼容策略](docs/compat-policy.md)。
> 适合阅读、试用与写插件；生产依赖请等 `1.0`。

> **Dougong** 借鉴 DeepSeek Harness（dsh）的 “Everything is a Plugin” 思想，为 .NET 生态构建独立的 Agent Harness SDK。项目借鉴架构思想，不移植代码，也不是 DeepSeek 官方项目。

斗拱是中国木构建筑中由标准化构件逐层装配、共同承托屋檐的结构。Dougong 采用同样的隐喻：内核负责承托和协调，插件负责提供能力；组件可以组合、替换、停用和重新加载。

## 当前状态

目标框架 `net10.0`，版本 `0.1.0-alpha.1`。**M0–M3 已全部完成**，四条 M3 完成判据均达成
（依据见 [实施状态](docs/status.md) 与 [M3 计划](docs/m3-plan.md) §九）。

**规模**：6 个产品工程、1 个内核测试工程（**187 项测试**）、7 个样例工程、
**41 个确定性自检套件**（不需网络与 API Key）。

**已验证的关键能力**：

- **内核**：生命周期状态机、代次绑定的服务租约、依赖图、有界异步事件、
  collectible ALC 动态装卸、五类失败诊断（阶段 + 建议）
- **Agent**：唯一工具执行入口 `ToolDispatcher`（schema 校验 → 审批 → 资源锁 → 执行）、
  JSONL 提交屏障、会话恢复与分支、上下文压缩、工具结果长度限制（只裁模型上下文，
  日志保持完整）、计划保护（压缩时不摘要当前计划）
- **Ai**：三种线协议连接器（chat-completions / responses / anthropic）、
  方言差异收口在 `WireCompat`、用量记账
- **Mcp**：远端工具桥接与变化检测（annotations 不作授权依据）
- **Acp**：Agent 端（按行分帧 JSON-RPC）、审批、`ask_user` 与 `plan`
- **Cli**：参考宿主，REPL 与 `--acp` 两种模式；`--doctor` 启动自检、
  `--report` 诊断信息包（不含密钥与对话内容）、`DOUGONG_LOG` 分级日志

**L1 偏好记忆层**（`design.md:236` 的 M2 范围项）已实现并验证：
白名单硬编码、审批绑定内容哈希、可撤销、跨工作区不传播。
效果对照在 6 家 provider × 2 种协议共 14 个组合上实测：
**偏好遵循率提升 60%–90%（中位数 80%）**，详见
[验收 1 报告](docs/skill-l1-acceptance-1.md)——该结论仅适用于被测模型与任务集。

**尚未做**：`Dougong.Telemetry`（OTel 导出）、断电级持久化验证、
ACP Registry 上架（需先开源 + 实现 Agent/Terminal Auth，见[计划](docs/plan.md) §六）。
完整清单按"有前置条件 / 已判定不做 / 外部边界 / 刻意划范围"四类分，见[实施状态](docs/status.md)。

## 为什么是 Dougong

Dougong 关注的是 Agent 的**承托层**，而不是某一个模型或某一种 UI：

- 插件拥有清晰的生命周期和作用域
- 服务通过代次绑定和调用租约进行协作
- 依赖缺失、提供方替换和停用都进入统一协调器
- 动态插件使用可回收的 AssemblyLoadContext
- Agent、MCP、ACP、Skill 和宿主都可以作为插件组合
- 模型供应商通过 `Microsoft.Extensions.AI` 接入，不把 provider 矩阵写进内核

## 核心架构

```text
┌──────────────────────────────────────────────────────┐
│                    Host / Desktop                    │
│              CLI · Avalonia · ACP Client             │
└──────────────────────────┬───────────────────────────┘
                           │
┌──────────────────────────▼───────────────────────────┐
│ Dougong.Agent                                         │
│ Agent loop · 会话 · 工具管线 · 审批 · 上下文压缩      │
├──────────────────────────────────────────────────────┤
│ Dougong.Ai · Dougong.Mcp · Dougong.Acp                │
│ 模型路由 · MCP 桥接 · ACP 适配                         │
├──────────────────────────────────────────────────────┤
│ Dougong.Kernel                                        │
│ 生命周期 · 服务注册 · 依赖图 · 事件 · 动态加载         │
└──────────────────────────────────────────────────────┘
```

### `Dougong.Kernel` 的设计重点

| 能力 | 说明 |
|---|---|
| 生命周期 | `Registered`、`Activating`、`Active`、`Deactivating`、`Stopped`、`Blocked`、`Failed` 状态与统一协调器 |
| 服务协作 | `(Type, Name?)` 服务键、可选需求、显式 `ProviderId`、优先级选择和激活事务 |
| 调用租约 | `IResolver` 返回代次绑定的服务引用，调用结束后释放在途计数 |
| 停用排空 | 关闭新调用、取消插件寿命、等待在途调用与已接受事件，再执行清理 |
| 事件总线 | 类型化订阅、独立有界队列、异步处理、退订即释放引用和溢出诊断 |
| 状态视图 | 锁内原子提交的修订号增量、快照、游标续订、登记/移除投影和重同步信号 |
| 动态加载 | collectible `AssemblyLoadContext`、显式共享契约、版本检查和协作式卸载 |
| 外部引用治理 | 快照与句柄只含共享数据，完整清单显式获取 |

> 当前实现仍是可信进程内插件模型。ALC 提供依赖隔离和协作式卸载，不提供操作系统级权限沙箱或故障隔离。

## 包结构

| 包 | 当前定位 | 状态 |
|---|---|---|
| `Dougong.Kernel` | 生命周期、服务、事件、依赖图、动态加载、失败诊断 | **完成**（ApiCompat 保护） |
| `Dougong.Agent` | Agent 运行契约、循环、工具派发、会话、审批、上下文压缩、L1 记忆 | **完成**（ApiCompat 保护） |
| `Dougong.Ai` | 三种线协议连接器、`ChatClientFactory`、用量记账 | **完成**（ApiCompat 保护） |
| `Dougong.Mcp` | MCP 工具桥接与变化检测、可注入传输 | **完成**（ApiCompat 保护） |
| `Dougong.Acp` | ACP Agent 端（按行分帧 JSON-RPC over stdio）、审批、`ask_user`、`plan` | **完成**（ApiCompat 保护） |
| `Dougong.Cli` | 参考宿主：REPL + `--acp` + `--doctor` + `--report` | **完成**（工具，非库，不在 ApiCompat 范围） |
| `Dougong.Telemetry` | OpenTelemetry 桥接 | 未开始（指标已用 BCL `Meter` 暴露，缺 exporter） |

## 快速开始

**完全没写过 .NET 或没写过代码？** 先看 [从零开始：第一次动手](docs/getting-started.md)
——那里每条命令都写全，并说明"应该看到什么"。

### 环境要求

- .NET SDK `10.0.402`
- Windows、Linux 或 macOS
- Git

### 构建和测试

```bash
git clone https://github.com/zhanghaojie513/Dougong.git dougong
cd dougong

dotnet restore Dougong.slnx
dotnet build Dougong.slnx -c Release
dotnet test Dougong.slnx -c Release --no-build
```

测试栈为 xUnit v3 4.0.2 + Microsoft.Testing.Platform；`global.json` 已指定 SDK 与 MTP
运行器，因此 `dotnet test` 走新体验，不需要 VSTest adapter。

```bash
# 全部确定性自检套件（当前 41 套，不联网、不需要 API Key）
# 脚本会先单独重建 spike——它不在 solution 里，全量构建不会重建它
bash scripts/all-checks.sh
```

### 打包

```bash
dotnet pack Dougong.slnx -c Release --output ./artifacts/packages
```

当前 API 仍处于实验阶段。使用 `Dougong.Kernel` 前，请先阅读状态板和架构文档，并以实际 API 与测试结果为准。

## 写第一个插件

先分清两类插件——它们是不同的东西：

| 类型 | 接口 | 谁看得见 | 用来做什么 |
|---|---|---|---|
| **内核插件** | `IPlugin` | 只有宿主 | 给宿主提供能力（存储、模型连接、审批器…） |
| **工具插件** | `IAgentTool` | **模型能调用** | 让模型多会一件事（查天气、查库、算汇率…） |

大多数人的需求是后者。

**完全没写过 .NET**：从[从零开始](docs/getting-started.md) 开始。它从"装什么软件"
讲起，每条命令都写明"应该看到什么"，§7 讲内核插件、§7.5 讲工具插件，
最后用 `--load-plugin` / `--load-tool` 让宿主真的加载你的 dll。

**已经会 .NET**：直接看[插件作者指南](docs/plugin-author-guide.md)。§1 是内核插件，
§2.5 是工具插件（含 `DescribePermission` 六种填法与 `Reads`/`Writes` 的冲突规则）。

**想把它嵌进自己的服务**（不是写插件，是当库用）：看[嵌入指南](docs/embedding-guide.md)，
最小可用只要 20 行。

可运行的例子在 [samples/](samples/)——其中 `m1-minimal-agent` 与 `m2-acp-mcp`
**不需要 API Key** 就能跑完整个循环。

### 先确认环境没问题

**从源码运行**（刚 clone 下来，没装 dotnet tool）：

```bash
dotnet run --project src/Dougong.Cli -c Release -- --doctor
```

**已装成 dotnet tool**：

```bash
dougong --doctor
```

逐项检查运行时、provider 配置（含一次真实连通性请求）、工作区、会话目录，
每项失败都给出**下一步做什么**。出问题时先跑它，比逐条猜快得多。

同一套差异适用于其它 CLI 命令：源码场景一律是
`dotnet run --project src/Dougong.Cli -c Release -- <参数>`。

## 文档

> 全部文档的归类与阅读路径见 [docs/README.md](docs/README.md)。根目录是描述当前系统的契约与状态；`docs/vision/` 是方向与理念，不是已实现能力的承诺。

| 文档 | 内容 |
|---|---|
| [架构](docs/architecture.md) | 分层结构、内核契约、状态机、动态加载和宿主边界 |
| [总体设计](docs/design.md) | 生态位、里程碑、MAF 与自研循环的比较门槛 |
| [实施状态](docs/status.md) | 已实现内容、测试基线、未完成验收和恢复入口 |
| [M1 候选对比](docs/m1-candidate-comparison.md) | MAF 包装与自研循环的同用例对比、版本与选择理由 |
| [M0 开发历程](docs/journey/m0-dev-journey.md) | M0 期间真实发生的问题与判断错误，供开发记录梳理 |
| [最终目的](docs/goal.md) | 四条不可让渡的性质、反目标与可执行的达成判定 |
| [完成定义](docs/quality-gates.md) | 六道质量门、红线、按规模裁剪 |
| [开发计划](docs/plan.md) | 里程碑顺序、缺口台账；防跑偏用 |
| [ACP 参照调查](docs/acp-reference-survey.md) | TKCode 与 pi.net 的实际做法，M2.4 可借鉴/不可借鉴项 |
| [M1 开发历程](docs/journey/m1-dev-journey.md) | M1 期间的实现缺陷、测试缺陷与判断错误；含真实 provider 才暴露的协议问题 |
| [设计理念](docs/vision/philosophy.md) | 斗拱隐喻、设计原则和明确不做的事情 |
| [前沿调查](docs/vision/frontier-roadmap.md) | Agent Harness、协议、隔离和记忆评测的公开进展 |
| [未来路线](docs/vision/iteration-roadmap.md) | M3 之后的演进方向和退出条件 |
| [Skill 演进](docs/vision/skills-evolution.md) | 复盘、沉淀、审批和加载的 Skill 进化设想 |
| [从零开始](docs/getting-started.md) | 装环境 → 构建 → 跑样例 → 接真模型 → 写插件；每条命令写明"应该看到什么" |
| [插件作者指南](docs/plugin-author-guide.md) | 内核插件与工具插件、生命周期约束、五个易犯错误、失败定位 |
| [兼容策略](docs/compat-policy.md) | 承诺什么、哪些包受保护、破坏性变更三档、弃用流程 |
| [故障排查](docs/troubleshooting.md) | 构建/provider/插件加载的实际案例，注明"是缺陷还是环境问题" |
| [L1 记忆方案](docs/skill-l1-plan.md) | 记忆层的实现范围与验收边界 |
| [L1 验收 1 报告](docs/skill-l1-acceptance-1.md) | 14 个 provider×协议组合的效果对照：判据、阈值与结果解释规则 |
| [上下文卫生](docs/context-hygiene.md) | 工具结果裁剪、压缩保留计划，以及"记忆取代关系为何不做" |
| [M3 计划](docs/m3-plan.md) | 四条完成判据、七个切片、执行结果 |
| [性能基线](docs/performance-baseline.md) | 大会话/长会话/并发会话的实测数字与环境，只记录不承诺 |
| [与 dsh/Cordis 对比](docs/kernel-vs-cordis.md) | 租约/排空/超时/变化传播逐项对照，以及已定的取舍 |
| [嵌入指南](docs/embedding-guide.md) | 把 Dougong 装进自己的 .NET 服务：20 行最小可用、五个零件的职责、三个真实缺口 |
| [宿主分层决定](docs/host-layering-decision.md) | dsh 与 pi.net 怎么切库与应用；学分层、不学 `CodingAgent` 式产品层 |
| [内核对抗审查](docs/kernel-adversarial-review.md) | 20 项攻击探针与发现 |

## 路线图

```text
M0  插件内核与生命周期反例验证        ✅ 2026-10-24 关闭
 │
 ▼
M1  Agent 运行实现比较与最小可用 Agent  ✅ 关闭（选定自研循环）
 │
 ▼
M2  ACP · MCP · 遥测 · 受限记忆        ✅ 关闭（指标用 BCL Meter；OTel exporter 未做）
 │
 ▼
M3  插件 API 稳定化与生态工具          ✅ 四条判据全部达成
 │
 ▼
I0  稳定化与发布                      ← 当前阶段
```

M3 的四条完成判据——外部作者只看文档能写出插件、API 变更被自动发现、
多轮装卸不泄漏、失败可定位——均已达成，证据见 [M3 计划](docs/m3-plan.md) §九。

当前进入 **I0 稳定化与发布**：把已有能力变成别人能装、能诊断、能扩展的产品。
后续迭代（I1 可嵌入运行时、I3 知识资产化等）的排序与退出条件见
[未来路线](docs/vision/iteration-roadmap.md)。

## 设计边界

Dougong 当前**不是**：

- dsh 的移植或兼容运行时
- 进程内安全沙箱
- LLM provider 矩阵
- 桌面 UI 或 IDE
- 多 Agent 编排框架
- 已完成的 AI 编程助手

配套桌面端将作为独立的 Dougong Desktop 宿主项目开发；本仓库负责 SDK、内核和参考 CLI。

## 贡献

项目尚处于 API 快速演进期。提交 Issue 或 Pull Request 前，建议先阅读 [实施状态](docs/status.md) 和 [架构文档](docs/architecture.md)，并为行为变化补充确定性的失败路径测试。

### 命名约定

项目名一律写作 **Dougong**，不使用 DouGong、dou gong 等变体：

- NuGet 包与命名空间：`Dougong.*`（如 `Dougong.Kernel`）
- CLI 命令与 dotnet tool：小写 `dougong`
- 仓库、目录、URL 中的项目名：`Dougong`

## 致谢与参考

Dougong 是独立开源项目，以下项目提供了公开思想或技术参考：

- [deepseek-harness](https://github.com/deepseek-ai/deepseek-harness) — “Everything is a Plugin” 架构参照
- [Microsoft Agent Framework](https://learn.microsoft.com/en-us/agent-framework/) — Agent 运行能力参考
- [Microsoft.Extensions.AI](https://learn.microsoft.com/en-us/dotnet/ai/microsoft-extensions-ai) — 模型抽象与连接器基础
- [MCP C# SDK](https://github.com/modelcontextprotocol/csharp-sdk) — MCP 协议基础
- [Hermes Agent](https://github.com/NousResearch/hermes-agent) — Skill 自动进化设想的参考
- [TKCode](https://github.com/AtomUICommunity/TKCode) — 权限自描述、并发分级和写盘屏障的设计参考

## License

[MIT](LICENSE) © 2026 Haojie Zhang（张浩洁）
