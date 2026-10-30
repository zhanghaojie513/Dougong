# dsh 能力对照进度清单

> **这份清单的用途**：统计我们对 dsh 能力的覆盖进度。
>
> 与 [dsh-capability-gap.md](dsh-capability-gap.md) 的分工：那份讲**为什么**缺、
> 怎么排优先级；这份只讲**现在是什么状态**，可逐行勾选、可数。
>
> **状态口径**（每项只取一个，按"做到哪一步"从严判定）：
>
> | 标记 | 含义 |
> |---|---|
> | `✅` | 有等价实现，且语义对齐 |
> | `🟡` | 有实现但不完整（明确写出差什么） |
> | `⬜` | 没有实现 |
> | `➖` | 明确不做（附理由，见 gap 文档） |
>
> **判定要严**：有类名但没接进装配，算 `⬜`；接进装配但没有验证，算 `🟡`。
> 上一轮就吃过这个亏——`SystemPrompt` 属性存在但永远没人赋值。

最后更新：2026-10-31，SDK `0.1.10-alpha.1`（未发布）

---

## 零、实现方式的分类

标记状态之外，每一项还标了**怎么实现的**。这两件事独立：
同样是 ✅，"用了成熟库" 与 "自己写" 的维护成本差一个量级。

| 标记 | 实现方式 | 含义 |
|---|---|---|
| `[库]` | 复用成熟库 | 直接用第三方或 BCL，我们不维护算法 |
| `[M.E.AI]` | 站在 Microsoft.Extensions.AI 上 | 抽象是它的，我们只做适配 |
| `[自有]` | 自己实现 | 核心逻辑由我们写与维护 |
| `[数据]` | 数据生成，无运行时逻辑 | 表/常量，无行为代码 |
| `[—]` | 不适用 | 没有实现（⬜ / ➖） |

**为什么标这个**：`[自有]` 的项在升级、排错时都要自己扛；
`[库]` 出问题能查上游文档。同样是"有"，成本不同——
不标出来，排期时会低估 `[自有]` 那一堆。

---

## 一、进度总览

| 能力域 | ✅ | 🟡 | ⬜ | ➖ | 小计 | 完成度 |
|---|---|---|---|---|---|---|
| 1. Agent 循环 | 4 | 1 | 3 | 1 | 9 | 56% |
| 2. LLM 与用量 | 4 | 2 | 3 | 0 | 9 | 56% |
| 3. 会话与持久化 | 2 | 3 | 8 | 1 | 14 | 27% |
| 4. 工具 | 3 | 4 | 11 | 4 | 22 | 29% |
| 5. 审批与提问 | 2 | 1 | 1 | 0 | 4 | 62% |
| 6. 沙箱与进程 | 0 | 2 | 3 | 0 | 5 | 20% |
| 7. 文件系统 | 3 | 0 | 3 | 0 | 6 | 50% |
| 8. 技能/目标/计划/子agent | 0 | 1 | 3 | 0 | 4 | 12% |
| 9. 任务与调度 | 0 | 1 | 5 | 0 | 6 | 8% |
| 10. 网络与外部 | 2 | 1 | 2 | 1 | 6 | 50% |
| 11. 配置与凭据 | 2 | 3 | 1 | 0 | 6 | 58% |
| **合计** | **22** | **19** | **43** | **7** | **91** | **38%** |

完成度算法：`(✅ + 🟡×0.5) / (✅+🟡+⬜)`，不含 `➖`。
（`(22 + 9.5) / 84 = 38%`）

**统计口径**：这些数字是从下面第二部分的表格逐行数出来的，
不是手填的。改了一行状态就要同步改这里——两处对不上说明漏改。

### 已实现项的实现方式分布

| 实现方式 | 项数 | 占已实现 |
|---|---|---|
| `[自有]` | 35 | 84% |
| `[库]` | 5 | 12% |
| `[M.E.AI]` | 1 | 2% |
| `[数据]` | 1 | 2% |

（"已实现"含 ✅ 与 🟡，共 42 项。）

**这个分布值得注意**：**84% 是自己写的**。

好在这些自研项都是"小而确定"的——事件词汇、schema 校验、资源闸门、
token 估算、压缩选区。它们的共同点是**逻辑短、可确定性验证**，
`agent` 与 `compaction` 套件逐条钉住了行为。

真正需要外部依赖的地方都用了库：TOML（Tomlyn）、HTTP（`HttpClient`）、
MCP（官方 SDK）、JSON（`System.Text.Json`）。

**将来补缺口时优先找库**：dsh 的很多能力背后有成熟实现
（ripgrep、SQLite FTS5、bwrap），自己写会比"用库"多出持续维护成本。

**代码规模参考**：Agent 7.9k 行 / Ai 4.2k / Kernel 3.5k / Acp 1.8k / Mcp 0.7k / Hosting 0.6k。

---

## 二、逐项清单

### 1. Agent 循环

| 状态 | 能力 | 实现方式 | dsh | 我们 | 备注 |
|---|---|---|---|---|---|
| ✅ | 循环与事件词汇 | `[自有]` | `dsh-agent`, `dsh-agent-loop` | `LoopAgentRuntime`, `AgentEvent` | 循环控制流自己写；事件词汇是 record 层级 |
| ✅ | 默认模型选择 | `[自有]` | `dsh-agent-default-model` | `ProviderCatalog`, `default_provider` | |
| ✅ | 压缩 seam | `[自有]` | `dsh-compaction` | `IContextCompactor` / `ICompactionTask` | 接口自己定；`dsh` 的 seam 在 `ctx.compaction` |
| ✅ | 上下文压缩实现 | `[自有]` | `dsh-compaction-basic` | `SlidingWindowCompactor` + 摘要 | 选区算法自研；摘要复用同一个 `IChatClient` |
| 🟡 | 系统提示组装 | `[自有]` | `dsh-system-prompt` | `LoopAgentRuntime.SystemPrompt` | **单值，无分节注册表**；且 CLI 主对话根本没设置它 |
| ⬜ | 工作区指令加载 | `[—]` | `dsh-agent-instructions` | — | 不读 `AGENTS.md` / `CLAUDE.md` |
| ⬜ | 部署人格段落 | `[—]` | `dsh-persona` | — | |
| ⬜ | 时间上下文 | `[—]` | `dsh-time-context` | — | 模型不知道"现在几点" |
| ➖ | 工具呈现形态（PTC/原生） | `[—]` | `dsh-agent-tool-presentation` | — | 我们没有 PTC |

### 2. LLM 与用量

| 状态 | 能力 | 实现方式 | dsh | 我们 | 备注 |
|---|---|---|---|---|---|
| ✅ | provider 中立接口 | `[M.E.AI]` | `dsh-llm` | `IChatClient` | 抽象复用，我们只实现连接器 |
| ✅ | 三种线协议 | `[自有]` | —（在 `dsh-llm-deepseek`） | `OpenAiChatCompletions` / `Responses` / `Anthropic` | **手写请求构造与流式解码**，用 `System.Text.Json` |
| ✅ | token 测量 | `[自有]` | `dsh-token-meter` | `TokenBudget` + `ContextBreakdown` + `UsageAnchor` | 字符启发式 + 真实用量锚点；2026-10-30 补齐 |
| ✅ | 用量记账 | `[自有]` | — | `UsageLedger` + `UsageReported` | 装饰器拦 `IChatClient`，比 dsh 独立 |
| 🟡 | 多 provider 数据 | `[数据]` | `dsh-llm-pi-ai`（42 家） | `ProviderPresets`（22 家 / 662 模型） | 脚本从 models.dev 生成，运行时零依赖 |
| 🟡 | 推理强度翻译 | `[自有]` | — | `ThinkingParameters` + `WireCompat.Thinking` | **翻译层完整，但无法从配置触发**——TOML 里没有对应键 |
| ⬜ | 请求重试策略 | `[—]` | `dsh-llm-retry` | — | 失败直接上抛 |
| ⬜ | 图片卸载 | `[—]` | `dsh-compaction-image-offload` | — | 不支持图片输入 |
| ⬜ | 会话统计（轮数/墙钟） | `[—]` | `dsh-session-stats` | — | CLI 自己算 |

### 3. 会话与持久化

| 状态 | 能力 | 实现方式 | dsh | 我们 | 备注 |
|---|---|---|---|---|---|
| 🟡 | 事件溯源会话 | `[自有]` | `dsh-session` | `JsonlAgentStore` | append-only JSONL，**无投影层** |
| ✅ | 持久化 seam | `[自有]` | `dsh-session-persistence` | `IAgentStore` | 接口自己定 |
| ✅ | JSONL 后端 | `[自有]` | `dsh-session-persistence-jsonl` | `JsonlAgentStore` | 手写 `System.Text.Json` 行读写 |
| 🟡 | 会话恢复与分叉 | `[自有]` | — | `SessionRecovery`, `SessionBranch` | 有分叉，但无"消息树"概念 |
| 🟡 | 提交屏障 | `[自有]` | `dsh-session-checkpoint-policy` | 提交屏障（四处落点） | 有屏障，**无显式检查点策略** |
| ➖ | 格式版本与迁移 | `[—]` | `dsh-session-format*` | — | **明确不做**，见 `compat-policy.md` §7.1 |
| ⬜ | 会话状态投影 | `[—]` | `dsh-session-projection*` | — | 宿主各自从日志重算 |
| ⬜ | 会话查询 seam | `[—]` | `dsh-session-query` | `SessionBranch.List` 只列文件 | 无查询抽象，只有"列文件" |
| ⬜ | 会话内容搜索 | `[—]` | `dsh-session-query-sqlite` | — | dsh 用 SQLite FTS5 |
| ⬜ | 会话标题生成 | `[—]` | `dsh-session-title*` | — | |
| ⬜ | 轮次大纲 | `[—]` | `dsh-session-turn-outline` | — | |
| ⬜ | 跨会话引用 | `[—]` | `dsh-session-reference` | — | |
| ⬜ | 日志导出 | `[—]` | `dsh-session-log-export` | — | |
| ⬜ | 会话遥测 | `[库]` | `dsh-session-telemetry*` | `DougongTelemetry`（仅 Meter） | 用 BCL `Meter`；无 OTel exporter |

### 4. 工具

| 状态 | 能力 | 实现方式 | dsh | 我们 | 备注 |
|---|---|---|---|---|---|
| ✅ | 注册与执行管线 | `[自有]` | `dsh-tools` | `ToolDispatcher` | schema 校验、资源闸门、并发计划全是自研 |
| ✅ | 计划工具 | `[自有]` | `dsh-tool-todo` | `TodoWriteTool` | |
| 🟡 | 读文件 | `[自有]` | `dsh-tool-fs` | `ReadFileTool`（已进 SDK） | 走 `IFileSystem` seam，返回 version |
| 🟡 | 写文件 | `[自有]` | `dsh-tool-fs` | `WriteFileTool`（已进 SDK） | 走 seam；原子写 + 版本守卫 |
| ⬜ | **glob / grep** | `[—]` | `dsh-tool-fs-search` | — | **P0**。dsh 打包 ripgrep；我们计划用 BCL |
| ⬜ | **按内容替换编辑** | `[—]` | `dsh-tool-str-replace-editor` | — | **P0**，改一行要重写全文 |
| ✅ | shell 执行 seam | `[自有]` | `dsh-shell` | `IShellExecutor` + `LocalShellExecutor` | 2026-10-31 补齐；换沙箱只换实现，工具不改 |
| 🟡 | shell | `[自有]` | `dsh-tool-bash` | `ShellTool` | 用 `System.Diagnostics.Process`；**无沙箱**（`cd /` 可绕过）、无持久、无后台 |
| ⬜ | PowerShell | `[—]` | `dsh-tool-pwsh*` | — | |
| ⬜ | **工具超时** | `[—]` | `dsh-tool-call-timeout-policy` | — | **P0**，工具卡住挂住整轮 |
| 🟡 | 向人类提问 | `[自有]` | `dsh-tool-ask-user` | `AcpElicitationTool` | ACP elicitation 协议；REPL 无等价物 |
| ⬜ | 技能加载 | `[—]` | `dsh-tool-skill` | — | |
| ⬜ | 子 agent 委派 | `[—]` | `dsh-tool-subagent*` | — | |
| ⬜ | 后台任务控制 | `[—]` | `dsh-tool-jobs` | — | |
| ⬜ | web 搜索/抓取 | `[—]` | `dsh-tool-web` | — | |
| ⬜ | 文件交付声明 | `[—]` | `dsh-tool-present` | — | |
| ⬜ | 目标工具 | `[—]` | `dsh-tool-goal` | — | |
| ⬜ | 重复调用提醒 | `[—]` | `dsh-repeat-tool-reminder` | — | |
| ➖ | 工作流引擎 | `[—]` | `dsh-workflow` | — | `goal.md` 把可视化编排列为反目标；脚本式也没做 |
| ➖ | 工作流脚本工具 | `[—]` | `dsh-tool-workflow` | — | |
| ➖ | 插件运行时探查 | `[—]` | `dsh-tool-cordis` | — | |
| ➖ | Ralph 循环 | `[—]` | `dsh-tool-ralph` | — | |

### 5. 审批与提问

| 状态 | 能力 | 实现方式 | dsh | 我们 | 备注 |
|---|---|---|---|---|---|
| ✅ | 一次性审批 fail-closed | `[自有]` | `dsh-user-approval` | `IToolApprover` + `PermissionPolicyApprover` | 无批准即拒绝 |
| ✅ | 审批记录持久化 | `[自有]` | （拼在 user-approval 里） | `ApprovalLog` | 比 dsh 更显式 |
| 🟡 | 向人类提问 seam | `[自有]` | `dsh-user-questions` | `AcpElicitationTool` | 走 ACP 协议；仅 ACP |
| ⬜ | 权限预设 | `[—]` | `dsh-permission-presets` | — | 沙箱 + 审批没打包成一个选项 |

### 6. 沙箱与进程

| 状态 | 能力 | 实现方式 | dsh | 我们 | 备注 |
|---|---|---|---|---|---|
| 🟡 | 路径守卫 | `[自有]` | `dsh-sandbox-policy` | `WorkspacePathGuard` | 纯路径规范化；**`cd /` 能绕过** |
| 🟡 | 子进程执行 | `[库]` | `dsh-subprocess` | `ShellTool` | 用 `System.Diagnostics.Process`；无进程组、无升级杀 |
| ⬜ | **进程沙箱** | `[—]` | `dsh-sandbox*` | — | **P0**。dsh 有 bwrap/landlock/Seatbelt/Windows ACL 四套 |
| ⬜ | 持久 PTY | `[—]` | `dsh-terminal*` | — | 桌面端要的 PTY |
| ⬜ | Win32 Job Object | `[—]` | `dsh-win32-process` | — | |

### 7. 文件系统

| 状态 | 能力 | 实现方式 | dsh | 我们 | 备注 |
|---|---|---|---|---|---|
| ✅ | 文件能力 seam | `[自有]` | `dsh-fs` | `IFileSystem` + `LocalFileSystem` | 四方法；2026-10-31 补齐 |
| ✅ | **原子写** | `[库]` | `dsh-atomic-write` | `LocalFileSystem` 内实现 | 同目录临时 + `File.Move`（BCL） |
| ✅ | **版本守卫写** | `[自有]` | `dsh-fs-observation-policy` | `FileWriteExpectation` | 内容 SHA-256 作版本，不用 mtime |
| ⬜ | 读后写观察态 | `[—]` | `dsh-fs-observation-policy` | — | 版本守卫已能防空写，但**不强制**先读后写这条流程 |
| ⬜ | 沙箱内文件操作 | `[—]` | `dsh-fs-sandbox` | — | 依赖 §6 沙箱 |
| ⬜ | `@file` 引用 | `[—]` | `dsh-file-reference*` | — | |

### 8. 技能 / 目标 / 计划 / 子 agent

| 状态 | 能力 | 实现方式 | dsh | 我们 | 备注 |
|---|---|---|---|---|---|
| 🟡 | 计划展示 | `[自有]` | `dsh-plan-mode` | `TodoWriteTool` + `PlanTool` | 模型主动调 `todo_write`；**无"带审阅的退出"** |
| ⬜ | 技能提供者注册 | `[—]` | `dsh-skill*` | — | L1 记忆是另一回事 |
| ⬜ | 会话内目标 | `[—]` | `dsh-goal*` | — | |
| ⬜ | 子 agent | `[—]` | `dsh-subagent*` | — | |

### 9. 任务与调度

| 状态 | 能力 | 实现方式 | dsh | 我们 | 备注 |
|---|---|---|---|---|---|
| 🟡 | 工具结果限制 | `[自有]` | `dsh-output-retention` | `ToolResultLimits` | 只有总量截断 |
| ⬜ | 工具结果分区裁剪 | `[—]` | `dsh-compaction-tool-result-pruner` | — | dsh 做头/中/尾三段保留且可回放；我们只会截尾 |
| ⬜ | 后台任务 | `[—]` | `dsh-jobs*` | — | |
| ⬜ | 持久提醒 | `[—]` | `dsh-schedule` | — | |
| ⬜ | 大结果落盘可取回 | `[—]` | `dsh-spill*` | — | 截断后取不回来 |
| ⬜ | 拦截钩子 | `[—]` | `dsh-hook-protocol` | — | |

### 10. 网络与外部

| 状态 | 能力 | 实现方式 | dsh | 我们 | 备注 |
|---|---|---|---|---|---|
| ✅ | MCP 客户端 | `[库]` | `dsh-mcp-client` | `McpToolRegistry` | 用官方 `ModelContextProtocol` SDK，我们做桥接 |
| 🟡 | MCP 资源 | `[库]` | `dsh-mcp-resources` | `McpResourceCatalog` | 有目录，读写覆盖不全 |
| ✅ | ACP 服务端 | `[自有]` | `dsh-acp` | `AcpAgentHost` | **手写协议实现**（C# 无官方 SDK） |
| ⬜ | web 搜索/抓取 | `[—]` | `dsh-web*` | — | |
| ⬜ | 出站代理 | `[—]` | `dsh-http-proxy` | — | 企业环境跑不起来 |
| ➖ | Webhook | `[—]` | `dsh-webhook*` | — | 不是 harness 能力 |

### 11. 配置与凭据

| 状态 | 能力 | 实现方式 | dsh | 我们 | 备注 |
|---|---|---|---|---|---|
| ✅ | 凭据引用（不存值） | `[自有]` | `dsh-credentials*` | `ProviderConfigFile.key_env` | 只存变量名；语义与 dsh 一致 |
| ✅ | 配置加载与校验 | `[库]` | — | `ProviderConfigFile` + `ProviderConfigWriter` | TOML 用 **Tomlyn**；写入走语法树保留注释 |
| 🟡 | 配置来源层级 | `[自有]` | `dsh-launch-environment` | 配置 > 环境变量 > 默认 | **只到三层，不记录"每个值由哪层给"** |
| 🟡 | 命令注册 | `[自有]` | `dsh-commands` | `HandleCommand`（硬编码 switch） | 不可扩展 |
| 🟡 | 路径助手 | `[自有]` | `dsh-home-paths` | 分散在各处 | 无统一入口 |
| ⬜ | 超时原语 | `[—]` | `dsh-timeout` | 分散实现 | 无统一 `TimeoutReason` 分类 |

---

## 三、P0 进度

| # | 项 | 状态 | 完成日期 |
|---|---|---|---|
| 0 | 文件能力归位到 SDK | ✅ | 2026-10-31 |
| 5 | 原子写 | ✅ | 2026-10-31（随 #0） |
| 6 | 会话格式版本 | ➖ | 决策：不做（`compat-policy.md` §7.1） |
| 1 | glob / grep | ⬜ | 下一步 |
| 2 | 按内容替换编辑 | ⬜ | 下一步 |
| 3 | shell 沙箱 | ⬜ | 独立一轮 |
| 4 | 工具超时 | ⬜ | |

**P0 完成度：2/5（另有 1 项明确不做）**

---

## 四、这份清单怎么维护

**只在状态变化时改**，不按时间刷。改的时候同时改两处：
- 下面对应那一行
- §一 的总览计数

**判定从严**，三个具体规则：

1. **有类名不算**。类存在但没接进 `SessionComposition` 的装配，算 `⬜`。
   上轮实例：`SystemPrompt` 属性在，但 CLI 从不赋值，那一栏永远是 0。
2. **接进装配但没验证，算 🟡**。验证指有确定性套件钉住行为，
   不是"我试过一次能跑"。
3. **`🟡` 必须在"备注"列写清差什么**。写不出差什么，说明没核过，
   应该回去核而不是标 `🟡`。

**➖ 必须能在 gap 文档里找到理由**。找不到就说明是漏了，不是不做。
