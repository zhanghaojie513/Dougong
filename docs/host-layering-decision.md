# 宿主分层：dsh 与 pi.net 的做法，以及我们不学的部分

> **这份文档的作用**：回答"CLI 和 Desktop 该怎么与 SDK 分层"这个反复出现的问题，
> 并记录一条已定的取舍：**不抽 `CodingAgent` 式的产品层**。
>
> **为什么单独成文**：`kernel-vs-cordis.md` 比较的是**内核**（租约、排空、超时）；
> 本文比较的是**应用分层**（库与可执行怎么切）。两者是不同的问题，混在一起会互相稀释。
>
> **一手证据来源**（2026-10-27 通过 GitHub API 与仓库页面核实）：
> - `deepseek-ai/deepseek-harness`：`packages/` 52 个、`apps/` 4 个，
>   读了三份 README（`packages/sdk`、`packages/boot`、`apps/cli`）
> - `AIDotNet/pi.net`：`src/` 17 个工程（13 库 + 4 可执行），读了主 README

---

## 一、两个项目怎么切的

### 1.1 dsh：`packages/` 是库，`apps/` 是应用

```
deepseek-harness/
├── packages/          52 个，全部是库
│   ├── core / session / tools / llm / mcp / acp / sandbox / skill / …
│   ├── boot/          app-boot、cmdline、hmr、plugin-manager
│   ├── host/          web GUI 后端：webserver、directory-picker…
│   └── sdk/           JSON-RPC 协议 + client + server
└── apps/              4 个，全部是应用
    ├── cli/           ← 只有 9 个文件、约 40KB
    ├── desktop/       Electron
    ├── desktop-host/
    └── web/
```

`apps/cli/README.md` 的第一句：

> The `dsh` command is the sole supported Node application launcher: profiles are
> ordered stacks of plugin-bundle patch layers under the user's own overrides.
> **SDK and ACP are profiles, not separate public bins.**

它的核心机制是 **profile**：

```bash
dsh --profile <name>            普通对话
dsh --profile headless "job"    一次性任务
dsh --profile acp               ACP 服务
dsh --profile sdk               SDK 服务
dsh web                         Web 界面
```

**CLI 不实现任何功能。** 它做三件事：解析命令文法（`args.ts`）、按 profile 加载插件层
（`profile-boot.ts`）、清晰的启动失败报告（`startup-diagnostics.ts`）。
所有能力在 `packages/` 里，由 profile 组合。

### 1.2 pi.net：`src/` 下库与可执行混放，但用一条线切开

`src/` 有 17 个工程。README 明确：**13 个是库，4 个是可执行**。

| 类别 | 工程 |
|---|---|
| 库（打包发布） | Telemetry、Protocol、Ai、Agent、Mcp、Chord、Server、Client、Codemode、Tui、Durable、Env、**CodingAgent** |
| 可执行（不打包） | **Cli**、Vacation、DurableHost、Evals |

README 对组合方式的表述：

> 每个库只做"最小但完整的一件事"，**上层由你自己组合**。

关键是 **`Pi.CodingAgent` 与 `Pi.Cli` 是两层**：

- `Pi.CodingAgent`（**打包**）：交互式编码 agent、**工具**、.NET 扩展、RPC
- `Pi.Cli`（**不打包**）：可执行入口，处理 `--help`、`-p`、`--mode json`

而且 **Vacation**（研究/规划 host）与 **DurableHost**（持久会话 host）
都是"built on the coding-agent runtime"——**三个可执行宿主共用 `CodingAgent` 那一层**。

---

## 二、两家其实在说同一件事

| | dsh | pi.net |
|---|---|---|
| 装配 + 工具放哪 | `packages/`（库） | `Pi.CodingAgent`（库，已打包） |
| 参数解析 + UI 放哪 | `apps/cli`（薄壳） | `Pi.Cli`（可执行，不打包） |
| 多宿主共用 | ✅ CLI / Desktop / Web / SDK 同一套 packages | ✅ Cli / Vacation / DurableHost 共用 CodingAgent |
| CLI 代码量 | 约 40KB | 未核实（GitHub 限流，没有查） |

**共同结论：装配和工具放进库，参数解析和 UI 留在可执行里。**

## 三、我们现在的状态正好相反

| | dsh | pi.net | **Dougong** |
|---|---|---|---|
| 库 | 52 个 | 13 个 | **5 个** |
| 可执行 | 4 个 | 4 个 | **1 个** |
| 装配在哪 | `packages/boot` | `Pi.CodingAgent` | **`SessionComposition.cs`（`internal`）** |
| 内置工具在哪 | `packages/fs`、`shell` | `Pi.CodingAgent` | **`FileTools.cs`（`internal`）** |
| 启动诊断在哪 | `packages/host` + `startup-diagnostics` | 未核实 | **`Doctor.cs`（`internal`）** |
| 多宿主共用 | ✅ | ✅ | ❌ 只有 CLI 一个宿主 |

**依赖方向是对的**（CLI → SDK 单向，SDK 从不引用 CLI），缺的是中间那一层。

---

## 四、学什么，不学什么

### 4.1 学：把"中立零件 + 装配方式"放进库

任何宿主都会需要、且不涉及产品决策的东西：

| 内容 | 现状 | 该去哪 |
|---|---|---|
| dispatcher / runtime / store 的接法 | `SessionComposition`（`internal`） | SDK |
| 内核接线、租约、提交屏障 | `KernelAgentHost`（`internal`） | SDK |
| `read_file` / `write_file` | `FileTools`（`internal`） | SDK |
| 模型连接、MCP 连接 | `SessionComposition` 的一部分 | SDK |

**理由**：CLI 和 Desktop 各装一遍，就会出现"两套装配、两种行为"——
这正是 `goal.md` P1（可组合）要避免的。

### 4.2 不学：抽一个 `CodingAgent` 式的产品层

pi.net 的 `Pi.CodingAgent` 不只是一个装配器，它是**"一个具体的编码 agent 产品"**：
包含工具集、提示策略、会话行为的整体。dsh 的 profile 体系同理——它决定了
"启动后是什么形态的 agent"。

**我们不抽这一层**，理由来自 `goal.md` §三 的反目标：

> **做插件市场 / 桌面 UI / IDE 集成** —— 由宿主项目承担（如 Dougong Desktop）；
> **本仓库只交付 SDK + 参考 CLI**

以及 `design.md:31`：

> **SDK 优先，宿主示范**。交付物是 NuGet 包族 + 一个 headless CLI 参考宿主。

"编码 agent 该怎么工作"是**宿主的决定**，不是 SDK 的决定。写进 SDK 会有三个后果：

1. Desktop 想做一个"审阅代码"的 agent 时，要么被 CLI 的编码 agent 绑住，要么绕开它（等于白抽）
2. 这些产品决策会进入 ApiCompat 契约，改一次就是一次破坏性变更
3. 反目标里明写的"本仓库只交付 SDK + 参考 CLI"被推翻

### 4.3 为什么两家可以这么做，我们不行

**因为它们的"产品"和"SDK"是同一件事。**

- pi.net 的目标是"把 Pi 移植到 .NET"，而 Pi 本身就是一个具体的编码 agent
- dsh 的 profile 体系服务于它自己的多种形态（CLI / Desktop / Web / SDK）

**我们的目标不是"做一个编码 agent"，是"做一个能让别人做编码 agent 的 harness"。**
这两者的合理抽象层次不同。

一个佐证：我们自己的 `design.md:323` 早就写对了——

> **宿主职责固定**：SDK 仓库交付 NuGet 包和 CLI 参考宿主；Desktop 仓库交付直接内嵌
> SDK 的 AgentHost 与 PtyHost……**两种宿主复用 SDK 的组合入口**。

注意"组合入口"四个字：说的是**怎么把零件组合起来**，不是"一个完整的 agent 产品"。

---

## 五、边界表：该共用 vs 该各自实现

| 该共用（进 SDK） | 该各自实现（留宿主） |
|---|---|
| dispatcher / runtime / store 怎么接 | 用哪些工具、工具的提示词怎么写 |
| 内核接线、租约、提交屏障 | 会话怎么开场、上下文怎么组织 |
| 模型连接、MCP 连接 | 命令行体验、UI 布局 |
| `read_file` / `write_file` 等中立基础工具 | "编码 agent"这一整套产品决策 |
| 诊断的事实采集（版本、平台、配置形态） | 诊断的呈现方式（CLI 打文本，Desktop 画界面） |

左列是 P1「可组合」要的东西；右列是宿主的自由。

---

## 六、这个判断的边界

**没有核实到的**（如实列出，不猜）：

1. **`Pi.Cli` 的实际代码量** —— GitHub API 限流，没查到。若它其实很厚，
   说明 pi.net 的"薄壳"程度不如 dsh，我们的对照结论会弱一些
2. **`Pi.Tui` 是否被多个宿主共用** —— README 没写。若只被 `Pi.Cli` 用，
   那它其实是 CLI 的一部分，只是放在了库里
3. **`Pi.CodingAgent` 内部"工具 / 提示 / 会话策略"的界限有多清晰** —— 需要读源码。
   若它内部其实是中立装配 + 可配置策略，那结论要修正

**没验证的推论**：

- "Desktop 会需要和 CLI 不同的产品行为"——这是`design.md:323`的设计意图，
  但 Desktop 尚未实现，没有实证

---

## 七、一句话结论

**学它们的分层（零件与装配进库、壳留应用），不学它们的产品层（`CodingAgent` / profile）。**

前者的依据是 P1「可组合」与"CLI 与 Desktop 要共用组合入口"；
后者的排除依据是反目标"本仓库只交付 SDK + 参考 CLI"。
