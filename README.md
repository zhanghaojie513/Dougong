# Dougong（斗拱）

<p align="center">
  <img src="assets/logo.png" width="96" alt="Dougong">
</p>

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
  <a href="https://github.com/zhanghaojie513/Dougong/releases"><img src="https://img.shields.io/badge/version-0.1.11--alpha.1-orange?style=flat-square" alt="Version 0.1.11-alpha.1"></a>
  <a href="LICENSE"><img src="https://img.shields.io/badge/license-MIT-1f883d?style=flat-square" alt="MIT License"></a>
</p>

> [!WARNING]
> **早期开发中，暂不建议依赖。** M0–M3 已完成，四条完成判据均已达成，但版本仍是 `0.x`，
> **不承诺向后兼容**：公开 API 的每一次变更都会被 ApiCompat 自动检出并记入
> [CHANGELOG](CHANGELOG.md)，规则见[兼容策略](docs/compat-policy.md)。
> 适合阅读、试用与写插件；生产依赖请等 `1.0`。

> **Dougong** 借鉴 DeepSeek Harness（dsh）的 “Everything is a Plugin” 思想，为 .NET 生态构建独立的 Agent Harness SDK。项目借鉴架构思想，不移植代码，也不是 DeepSeek 官方项目。

斗拱是中国木构中真实存在的结构：独立构件通过连接与组合，共同承担结构作用。Dougong 的纹章
**云拱 · 承智之枢** 取意于此——层叠的檐承接复杂意图与模型能力，中央的合构之核由插件通过
明确接口组成整体，两侧回转云纹代表发出行动再接回外界反馈，下方基座让行动留下可核实的结果。

核心理念是 **承智成事**：把分散的能力，在清晰的连接中组织为行动。

> 「云拱」与「承智之枢」是为本产品创作的当代命名与寓意，并非古代已有器物的名称；
> 斗拱与榫卯本身属于真实的木构传统。基座的寓意不等于绝对安全、永不失败或进程级隔离。

## 当前状态

目标框架 `net10.0`，版本 `0.1.11-alpha.1`，**已发布到 nuget.org**（2026-10-30）。
四条设计承诺（可组合、完整生命周期、故障可解释、真实可用）均已达成，
见 [设计承诺](docs/goal.md)。

**规模**：7 个产品工程、1 个内核测试工程（**201 项测试**）、9 个样例与探针工程、
**45 个确定性自检套件**（不需网络与 API Key）。

**已验证的关键能力**：

- **内核**：生命周期状态机、代次绑定的服务租约、依赖图、有界异步事件、
  collectible ALC 动态装卸、五类失败诊断（阶段 + 建议）
- **Agent**：唯一工具执行入口 `ToolDispatcher`（schema 校验 → 审批 → 资源锁 → 执行）、
  JSONL 提交屏障、会话恢复与分支、上下文压缩、工具结果长度限制（只裁模型上下文，
  日志保持完整）、计划保护（压缩时不摘要当前计划）；
  **推理内容单独成流**（`ReasoningDelta`，与正文分开），
  以及阶段推导 `AgentPhaseTracker`（宿主不必自己从事件猜"现在该显示什么"）
- **Hosting**：内核接线（插件注册、租约适配、服务解析）。CLI 与第二个宿主
  （桌面端）共用这一层，避免各写一遍——那类重复的出错方式是静默的（注册顺序、
  accessor 与裸实例、异常清理），不会当场报错
- **Ai**：三种线协议连接器（chat-completions / responses / anthropic）、
  方言差异收口在 `WireCompat`、用量记账
- **Mcp**：远端工具桥接与变化检测（annotations 不作授权依据）
- **Acp**：Agent 端（按行分帧 JSON-RPC）、审批、`ask_user` 与 `plan`
- **Cli**：参考宿主，REPL 与 `--acp` 两种模式；`--doctor` 启动自检、
  `--report` 诊断信息包（不含密钥与对话内容）、`DOUGONG_LOG` 分级日志；
  终端按能力分三档（富/窄/纯文本），后两档只在真终端启用底部活动行与状态栏，
  被重定向时输出与无此特性时逐字节相同

**L1 偏好记忆层**（`design.md:236` 的 M2 范围项）已实现并验证：
白名单硬编码、审批绑定内容哈希、可撤销、跨工作区不传播。
效果对照在 6 家 provider × 2 种协议共 14 个组合上实测：
**偏好遵循率提升 60%–90%（中位数 80%）**——该结论仅适用于被测模型与任务集，
完整实验记录属于内部材料。

**尚未做**：`Dougong.Telemetry`（OTel 导出）、断电级持久化验证、
ACP Registry 上架（仓库已开源，剩认证方式一条：Registry 只接受 OAuth 或终端交互认证，
而本项目只有环境变量认证）。

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
│ Dougong.Hosting                                       │
│ 内核接线 · 租约适配 · 服务解析（CLI/Desktop 共用）     │
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
| `Dougong.Hosting` | 宿主装配：内核接线、租约适配、服务解析。CLI 与 Desktop 共用 | **完成**（ApiCompat 保护） |
| `Dougong.Cli` | 参考宿主：REPL + `--acp` + `--doctor` + `--report` | **完成**（工具，非库，不在 ApiCompat 范围） |
| `Dougong.Telemetry` | OpenTelemetry 桥接 | 未开始（指标已用 BCL `Meter` 暴露，缺 exporter） |

## 安装

**命令走工具**（不需要 clone 仓库）：

```bash
dotnet tool install --global Dougong.Cli --version 0.1.11-alpha.1
dougong --doctor      # 先确认环境，它会逐项检查并给出下一步
dougong               # 开始对话（需要配置模型）
```

**装的是包 `Dougong.Cli`，命令是 `dougong`。** 两个名字不一样是刻意的：
包名跟 `Dougong.*` 命名约定，命令名短。

**升级**（注意 `--prerelease`）：

```bash
dotnet tool update --global Dougong.Cli --prerelease
dougong --version     # 确认升到了哪个版本
```

> **升级必须带 `--prerelease`**，不带会报「找不到 dougong.cli」——那个报错是
> 误导，包其实在。原因是所有版本都带 `-alpha.N` 预发布标签，而
> `dotnet tool update` 默认只在**稳定版**里挑；稳定版列表为空，于是说找不到。
> 详见 [从零开始 §0.2](docs/getting-started.md)。

> `0.1.11-alpha.1` 的命令功能是完整的（`--doctor`、`--report`、`--help`、REPL、
> `--acp` 都实测过，行为由 46 个自检套件钉住），但**终端呈现与提示文案仍在打磨**，
> 后续版本会变。

**作为库用**（在 .NET 工程里引用）：

```bash
dotnet add package Dougong.Agent     # 运行能力：循环、工具派发、会话、压缩
dotnet add package Dougong.Hosting   # 接内核：插件注册、租约适配（CLI/Desktop 都该用）
dotnet add package Dougong.Ai        # 模型连接（三种线协议）
dotnet add package Dougong.Mcp       # MCP 工具桥接
dotnet add package Dougong.Acp       # ACP Agent 端
dotnet add package Dougong.Kernel    # 插件内核（通常由 Hosting 带入）
```

20 行的最小嵌入示例见 [嵌入指南](docs/embedding-guide.md)。

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
# 全部确定性自检套件（当前 46 套，不联网、不需要 API Key）
# 脚本会先单独重建 spike——它不在 solution 里，全量构建不会重建它
bash scripts/all-checks.sh
```

### 打包与发布

```bash
dotnet pack Dougong.slnx -c Release --output ./artifacts/packages
```

要发布到 nuget.org 用 `bash scripts/publish-nuget.sh`（先 `--dry-run` 看会推什么）。
它按依赖顺序推、失败即停、不读不打印 API key（只从 `NUGET_API_KEY` 环境变量取）。

> **兼容性**：项目处于 `0.x`，**不做向后兼容承诺**，但每条公开 API 变更都会被
> ApiCompat 自动检出并记入 [CHANGELOG](CHANGELOG.md)。规则见
> [兼容策略](docs/compat-policy.md)。

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

### 配置模型

三条路，选一条：

**① 交互式选择**（最省事，会写回配置文件）：

```bash
dougong model
```

四步走，每步都有默认值，回车即接受：

1. **选 provider** —— 19 家内置预设与配置里已有的项一起列出，每行显示主机名与密钥状态
2. **确认端点** —— 显示默认 `base_url`，方括号内回车接受；换端点直接输入
3. **确认密钥** —— 说清需要哪个环境变量、当前有没有、怎么设；未设置时可选择先跳过
4. **选模型** —— 先从端点拉 `/v1/models` 列出可选项，拉不到再手填

写回 `~/.dougong/config.toml` 时，**已有注释和空行原样保留**，只改需要改的值。
选到配置里还没有的 provider 时会新建那一段（端点取自预设）。

非交互形式适合脚本：

```bash
dougong model --list                                            # 只看当前配置
dougong model --set deepseek --model deepseek-chat              # 直接设置并设为默认
```

> **密钥仍只从环境变量读**，向导不会把它写进配置文件——命令行键入的密钥会进
> shell 历史，而进程也没法替父 shell 设环境变量。

**② 手写配置文件**（可配多个 provider）：`~/.dougong/config.toml`

```toml
default_provider = "deepseek"

[providers.deepseek]
base_url = "https://api.deepseek.com"
wire_api  = "openai"            # openai（默认）/ responses / anthropic
model     = "deepseek-chat"
key_env   = "DEEPSEEK_API_KEY"  # 变量名，不是密钥本身

[providers.local]
base_url = "http://127.0.0.1:1234/v1"
model    = "qwen3"
```

```bash
export DEEPSEEK_API_KEY='sk-...'
dougong
```

**③ 三个环境变量**（优先级最高，适合临时试一个端点）：

```bash
export DOUGONG_PROVIDER_BASE=https://api.deepseek.com
export DOUGONG_PROVIDER_MODEL=deepseek-chat
export DOUGONG_PROVIDER_KEY=sk-...
```

**密钥只从环境变量读，任何文件都不保存它。** 配置文件里只有变量名
（`key_env`）；写成密钥值会被硬拒并提示改写。因此配置文件可以进 git、进备份。

**内置 19 家预设**，每条只有端点、线协议与密钥变量名，**没有模型名**
——同一端点的可用模型会变，写死会让你拿到 404：

| 分类 | provider |
|---|---|
| 国内 | `deepseek` `glm`（智谱）`kimi` `kimi-intl`（月之暗面）`mimo`（小米）`minimax` `minimax-cn` `stepfun`（阶跃）`qwen`（百炼）`doubao`（豆包） |
| 国际 | `openai` `anthropic` `gemini` `xai` `mistral` `groq` |
| 聚合 / 本地 | `openrouter` · `ollama` `lmstudio`（本地，无需密钥） |

会话内用 `/model` 列出全部 provider 并切换（切换只在本次会话有效）：

```text
> /model
  ▶ deepseek         deepseek-chat   OpenAiChatCompletions  DEEPSEEK_API_KEY 已设置
    local            qwen3           OpenAiChatCompletions  无需密钥
    openai           未配置模型        OpenAiChatCompletions  OPENAI_API_KEY 未设置
> /model local
```

## 文档

> 完整索引（含开发台账与复盘）见 [docs/README.md](docs/README.md)。

**上手就看这几份**

| 文档 | 什么时候读 |
|---|---|
| [从零开始](docs/getting-started.md) | 第一次动手。每条命令写明"应该看到什么"，给没写过 .NET 的人 |
| [插件作者指南](docs/plugin-author-guide.md) | 写内核插件或工具插件时；含五个易犯错误与失败定位 |
| [嵌入指南](docs/embedding-guide.md) | 把 Dougong 装进自己的 .NET 服务（20 行最小可用） |
| [故障排查](docs/troubleshooting.md) | 出问题时；区分"是缺陷还是环境问题" |

**要理解设计**

| 文档 | 什么时候读 |
|---|---|
| [架构](docs/architecture.md) | 分层、内核契约、状态机、动态加载、宿主边界。多处代码注释引用其章节号 |
| [总体设计](docs/design.md) | 为什么这样设计：生态位、里程碑、关键取舍 |
| [最终目的](docs/goal.md) | 四条不可让渡的性质与反目标；判断"要不要做"时看它 |
| [兼容策略](docs/compat-policy.md) | 改公开 API 前必读：承诺什么、破坏性变更三档、弃用流程 |

**现状与规划**

| 文档 | 内容 |
|---|---|
| [性能基线](docs/performance-baseline.md) | 实测数字与环境，只记录不承诺 |
| [故障排查](docs/troubleshooting.md) | 构建、provider、插件加载的实际案例 |

> 内部工作记录（状态台账、排期、里程碑计划、开发历程）不在此仓库——
> 它们是维护过程的中间产物，随发现调整。完整文档索引见 [docs/README.md](docs/README.md)。

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
I0  稳定化与发布                      ✅ 2026-10-28 关闭（已发布到 nuget.org）
```

M3 的四条完成判据——外部作者只看文档能写出插件、API 变更被自动发现、
多轮装卸不泄漏、失败可定位——均已达成。

I0 的退出条件是"一位未参与开发的人在干净 Windows 与 Linux 环境各完成一次
安装→配置→真实任务→写插件"，两端都有实测记录。

**下一步由真实需求决定，不照路线图硬推。** I1「可嵌入运行时」的判据
已经满足：桌面端作为独立进程的宿主已在用这套 SDK，它撞上的第一个缺口
直接变成了 API 的一部分。

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

项目尚处于 API 快速演进期。提交 Issue 或 Pull Request 前，建议先阅读 [架构文档](docs/architecture.md) 与 [设计承诺](docs/goal.md)，并为行为变化补充确定性的失败路径测试。

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
- [pi.net](https://github.com/AIDotNet/pi.net) — .NET 上如何切"库与应用"的参考（`CodingAgent` 与 `Cli` 分层）；
  样例由 CI 编译保活的做法也源自它。**只读了它的 README 与目录结构做架构对照，没有复制任何代码**，
  对比结论见 [host-layering-decision.md](docs/host-layering-decision.md)

以上项目均为**思想与设计参考**，Dougong 不含它们的代码。
原始实现有独立的事实依据：每条借鉴都记在 `docs/` 对应文档里（谁、哪一条、为什么采纳或不采纳），
不采纳的也写明了理由——例如 [kernel-vs-cordis.md](docs/kernel-vs-cordis.md) 记录了
"对等的 .NET dsh"这个定位为何不成立。

## License

MIT — 见 [LICENSE](LICENSE)。© 2026 Haojie Zhang。

Dougong 是独立实现，不是任何项目的移植或分支。"致谢与参考"里列出的项目
只读了文档与目录结构做架构对照，**没有复制源码**，因此这里不保留任何上游版权。
每条借鉴都在 `docs/` 里记了出处（谁、哪一条、为什么采纳或不采纳），
不采纳的同样写了理由。
