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

最后更新：2026-11-02，SDK `0.1.16-alpha.1`（已发布）

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
| 1. Agent 循环 | 7 | 0 | 1 | 1 | 9 | 88% |
| 2. LLM 与用量 | 5 | 2 | 2 | 0 | 9 | 67% |
| 3. 会话与持久化 | 7 | 5 | 5 | 0 | 17 | 56% |
| 4. 工具 | 10 | 3 | 6 | 4 | 23 | 54% |
| 5. 审批与提问 | 3 | 0 | 1 | 0 | 4 | 75% |
| 6. 沙箱与进程 | 0 | 3 | 3 | 0 | 6 | 25% |
| 7. 文件系统 | 4 | 0 | 2 | 0 | 6 | 67% |
| 8. 技能 / 目标 / 计划 / 子 agent | 2 | 0 | 2 | 0 | 4 | 50% |
| 9. 任务与调度 | 3 | 1 | 2 | 0 | 6 | 58% |
| 10. 网络与外部 | 2 | 2 | 1 | 1 | 6 | 60% |
| 11. 配置与凭据 | 3 | 2 | 1 | 0 | 6 | 67% |
| **合计** | **46** | **18** | **26** | **6** | **96** | **61%** |

完成度算法：`(✅ + 🟡×0.5) / (✅+🟡+⬜)`，不含 `➖`。
（`(46 + 9) / 90 = 61%`）

**2026-11-02 P1 九项落地后，✅ 从 29 变成 38**（工作区指令加载、时间上下文、
请求重试策略、大结果落盘可取回、会话内容搜索、会话标题生成、REPL 提问、Web 抓取、交付物清单），⬜ 从 45 降到 35，
🟡 从 16 变成 18（会话查询缺 seam、web 只做了抓取、`present` 工具没有），总数仍是 96。

**2026-11-02 重新逐包核对 dsh 后，总数从 91 变成 96。** 增加的五项是此前
完全漏掉的：LSP 代码导航、会话内切换工作目录、每轮改动文件清单、
消息级反馈、远端执行（SSH）。同时 ✅ 从 27 变成 29（读写工具的状态其实
早已完整，只是没更新）。

**统计口径**：这些数字是从下面第二部分的表格逐行数出来的，
不是手填的。改了一行状态就要同步改这里——两处对不上说明漏改。

### 已实现项的实现方式分布

| 实现方式 | 项数 | 占已实现 |
|---|---|---|
| `[自有]` | 57 | 90% |
| `[库]` | 5 | 8% |
| `[M.E.AI]` | 1 | 2% |
| `[数据]` | 1 | 2% |

（"已实现"含 ✅ 与 🟡，共 63 项。）

**这个分布值得注意**：**89% 是自己写的**。

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
| ✅ | 系统提示组装 | `[自有]` | `dsh-system-prompt` | `SystemPromptBuilder` + `ISystemPromptSection` | 分节注册表；纪律按实际工具集取舍；2026-10-31 顺带修掉"提示只进统计、从未发出"的缺陷 |
| ✅ | 工作区指令加载 | `[自有]` | `dsh-agent-instructions` | `WorkspaceInstructionsSection` | 两条链（全局+项目）；`AGENTS.md`/`CLAUDE.md` 都认；内容去重；超预算截断不切坏 UTF-8；2026-11-02 |
| ⬜ | 部署人格段落 | `[—]` | `dsh-persona` | — | |
| ✅ | 时间上下文 | `[自有]` | `dsh-time-context` | `TimeContext` | user 角色注入；带偏移与 IANA 名；报间隔；默认最小间隔 10 分钟；2026-11-02 |
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
| ✅ | 请求重试策略 | `[自有]` | `dsh-llm-retry` | `RetryPolicy` + `RetryClassifier` + `RetryScheduled` | 只重试瞬时类（408/429/5xx/超时/网络）；已交付增量不重试；退避+抖动；`Retry-After` 优先但有上限；2026-11-02 |
| ⬜ | 图片卸载 | `[—]` | `dsh-compaction-image-offload` | — | 不支持图片输入 |
| ⬜ | 会话统计（轮数/墙钟） | `[—]` | `dsh-session-stats` | — | CLI 自己算 |

### 3. 会话与持久化

| 状态 | 能力 | 实现方式 | dsh | 我们 | 备注 |
|---|---|---|---|---|---|
| 🟡 | 事件溯源会话 | `[自有]` | `dsh-session` | `JsonlAgentStore` + `SessionRecordReader` | append-only JSONL，**无投影层**；读取逻辑已收敛到单一入口 |
| ✅ | 持久化 seam | `[自有]` | `dsh-session-persistence` | `IAgentStore` | 接口自己定 |
| ✅ | JSONL 后端 | `[自有]` | `dsh-session-persistence-jsonl` | `JsonlAgentStore` | 手写 `System.Text.Json` 行读写 |
| 🟡 | 会话恢复与分叉 | `[自有]` | — | `SessionRecovery`, `SessionBranch` | 有分叉，但无"消息树"概念 |
| 🟡 | 提交屏障 | `[自有]` | `dsh-session-checkpoint-policy` | 提交屏障（四处落点） | 有屏障，**无显式检查点策略** |
| 🟡 | 格式版本 | `[自有]` | `dsh-session-format*` | `SessionFormat` + `SessionFormatException` | **有版本号，只用于拒绝不做迁移**：读到更高版本明确报错而非静默读错；迁移链不做（见 `compat-policy.md` §7.1） |
| ⬜ | 会话状态投影 | `[—]` | `dsh-session-projection*` | — | 宿主各自从日志重算 |
| 🟡 | 会话查询 seam | `[自有]` | `dsh-session-query` | `SessionBranch.List` + `SessionSearch` | 有精确读与文本过滤，**但没有 seam 接口**——`SessionSearch` 是静态类，宿主换不了后端 |
| ✅ | 会话内容搜索 | `[自有]` | `dsh-session-query-sqlite` | `SessionSearch` + `--search` | 扫 JSONL 而非 SQLite FTS5（会话是 append-only、几十个文件、毫秒级）；只搜用户输入与模型回复，工具参数/结果不参与；带会话标识与序号可 `--resume`；2026-11-02 |
| ✅ | 会话标题生成 | `[自有]` | `dsh-session-title` | `SessionTitle` | 只做 dsh 契约的**确定性兜底**部分（不调模型）；从首条用户输入推导，20 字符含省略号；**不写入会话日志**（加记录类型会让旧程序读不了新会话）；2026-11-02 |
| ⬜ | 轮次大纲 | `[—]` | `dsh-session-turn-outline` | — | |
| ⬜ | 跨会话引用 | `[—]` | `dsh-session-reference` | — | |
| ⬜ | 日志导出 | `[—]` | `dsh-session-log-export` | — | |
| ⬜ | 会话遥测 | `[库]` | `dsh-session-telemetry*` | `DougongTelemetry`（仅 Meter） | 用 BCL `Meter`；无 OTel exporter |
| ✅ | 会话内切换工作目录 | `[自有]` | `dsh-session/working-directory` | `WorkingDirectory` + `working_directory` 工具 | 原始项目 vs 有效目录分开：守卫的根不动，相对路径跟有效目录；已有持久 shell/作业保持自己目录；目录变更存 `<session>.jsonl.cwd`，重开可恢复；2026-11-03 |
| ✅ | 每轮改动文件清单 | `[自有]` | `dsh-deliverables/workspace-changes` | `WorkspaceChanges` + `--changes` | 从会话日志的写工具参数推导，不做文件指纹快照；**shell 与手动改动不在清单里**（真实盲区，界面上明说）；2026-11-02 |
| ✅ | 消息级反馈 | `[自有]` | `dsh-feedback/message-feedback` | `JsonlMessageFeedbackStore` + `MessageFeedback` | 正负 + 七类分类 + 备注；**不进会话日志、不进模型**，存会话旁的 `<session>.jsonl.feedback`；按 `RecordSequence` 定位（宿主的条目 id 重启即变）；版本守卫 + 墓碑；2026-11-02 |

### 4. 工具

| 状态 | 能力 | 实现方式 | dsh | 我们 | 备注 |
|---|---|---|---|---|---|
| ✅ | 注册与执行管线 | `[自有]` | `dsh-tools` | `ToolDispatcher` | schema 校验、资源闸门、并发计划全是自研 |
| ✅ | 计划工具 | `[自有]` | `dsh-tool-todo` | `TodoWriteTool` | |
| ✅ | 读文件 | `[自有]` | `dsh-tool-fs` | `ReadFileTool`（已进 SDK） | 走 `IFileSystem` seam，返回 version |
| ✅ | 写文件 | `[自有]` | `dsh-tool-fs` | `WriteFileTool`（已进 SDK） | 走 seam；原子写 + 版本守卫 |
| ✅ | glob / grep | `[自有]` | `dsh-tool-fs-search` | `GlobTool` / `GrepTool` | 走 `IFileSystem` seam，不打包 ripgrep；限深、跳噪声目录、截断如实报告 |
| ✅ | 按内容替换编辑 | `[自有]` | `dsh-tool-str-replace-editor` | `EditFileTool` | **要求唯一匹配**，多次出现即拒绝；走版本守卫写 |
| ✅ | shell 执行 seam | `[自有]` | `dsh-shell` | `IShellExecutor` + `LocalShellExecutor` | 2026-10-31 补齐；换沙箱只换实现，工具不改 |
| 🟡 | shell | `[自有]` | `dsh-tool-bash` | `ShellTool` + `ShellCommandPolicy` | 有**命令策略层**（危险命令/凭据读取/`cd` 越界）；**不是沙箱**，有意绕过仍可行；无持久、无后台 |
| ⬜ | PowerShell | `[—]` | `dsh-tool-pwsh*` | — | |
| ⬜ | LSP 代码导航 | `[—]` | `dsh-lsp` / `-stdio` / `tool-lsp` | — | 定义/引用/实现/悬停。**是优化不是缺口**：编辑闭环不依赖它，只在"重构找全部引用"与"重载/同名方法"上有增量。见 D-10 |
| ✅ | 工具超时 | `[自有]` | `dsh-tool-call-timeout-policy` | `ToolExecutionPlan.Timeout` + `ToolDispatcher.DefaultToolTimeout` | 逐工具上限优先；`TimedOut` 与"用户取消"分开；超时算已执行 |
| ✅ | 向人类提问 | `[自有]` | `dsh-tool-ask-user` | `AskUser`（语义）+ `AcpElicitationTool` / `ConsoleElicitationTool`（传输） | 语义层下沉到 Agent，两个宿主共用；四态结果分开（答/拒/取消/不可用）；2026-11-02 |
| ⬜ | 技能加载 | `[—]` | `dsh-tool-skill` | — | |
| ⬜ | 子 agent 委派 | `[—]` | `dsh-tool-subagent*` | — | |
| ✅ | 后台任务控制 | `[自有]` | `dsh-tool-jobs` | `JobStartTool` / `JobOutputTool` / `JobStopTool` / `JobListTool` | 四个名字各自说清动作，不用 mode 参数（模型会选错）；2026-11-02 |
| 🟡 | web 抓取 | `[自有]` | `dsh-web-fetch-http` | `WebFetchTool` | 只做抓取，不执行 JS；默认关闭（`DOUGONG_WEB_FETCH=1`）；报 `ToolAccess.Network` + 目标域名；2026-11-02 |
| 🟡 | `present` 工具 | `[自有]` | `dsh-tool-present` | — | 改动清单有了（`--changes`），但**没有 `present` 工具**让模型主动声明交付物 |
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
| ✅ | 向人类提问 seam | `[自有]` | `dsh-user-questions` | `AskUser` + 两个传输实现 | ACP 与 REPL 都有；校验与结果语义共用一份 |
| ⬜ | 权限预设 | `[—]` | `dsh-permission-presets` | — | 沙箱 + 审批没打包成一个选项 |

### 6. 沙箱与进程

| 状态 | 能力 | 实现方式 | dsh | 我们 | 备注 |
|---|---|---|---|---|---|
| 🟡 | 路径守卫 | `[自有]` | `dsh-sandbox-policy` | `WorkspacePathGuard` + `ShellCommandPolicy` | 路径规范化 + `cd` 越界拦截；**故意绕过仍可行**（`find /`、脚本内 cd） |
| 🟡 | 子进程执行 | `[库]` | `dsh-subprocess` | `ShellTool` | 用 `System.Diagnostics.Process`；无进程组、无升级杀 |
| ⬜ | 进程沙箱 | `[—]` | `dsh-sandbox*` | — | 已降为 P1：先做策略层（2026-10-31）。OS 级隔离需 bwrap/landlock/Seatbelt/ACL 四套各自实现与验证环境 |
| 🟡 | 持久 shell | `[自有]` | `dsh-tool-terminal` | `PersistentShell` + `shell_session` 工具 | **持久 shell，不是 PTY**：普通管道，跨平台一致；状态跨调用保留。**无作业控制**（交互式程序会挂住、无 Ctrl+C）。PTY 仍待做（桌面端终端） |
| ⬜ | Win32 Job Object | `[—]` | `dsh-win32-process` | — | |
| ⬜ | 远端执行（SSH） | `[—]` | `dsh-ssh*` | — | seam 形态允许（`IShellExecutor` / `IFileSystem`），无实现 |

### 7. 文件系统

| 状态 | 能力 | 实现方式 | dsh | 我们 | 备注 |
|---|---|---|---|---|---|
| ✅ | 文件能力 seam | `[自有]` | `dsh-fs` | `IFileSystem` + `LocalFileSystem` | 四方法；2026-10-31 补齐 |
| ✅ | **原子写** | `[库]` | `dsh-atomic-write` | `LocalFileSystem` 内实现 | 同目录临时 + `File.Move`（BCL） |
| ✅ | **版本守卫写** | `[自有]` | `dsh-fs-observation-policy` | `FileWriteExpectation` | 内容 SHA-256 作版本，不用 mtime |
| ⬜ | 读后写观察态 | `[—]` | `dsh-fs-observation-policy` | — | 版本守卫已能防空写，但**不强制**先读后写这条流程 |
| ⬜ | 沙箱内文件操作 | `[—]` | `dsh-fs-sandbox` | — | 依赖 §6 沙箱 |
| ✅ | `@file` 引用 | `[自有]` | `dsh-file-reference` / `-local` | `FileReferenceIndex` | 只返回相对路径、**不读内容**（dsh 契约原话）；按深度/长度/字典序稳定排序；不越工作区、跳噪声目录；2026-11-02 |

### 8. 技能 / 目标 / 计划 / 子 agent

| 状态 | 能力 | 实现方式 | dsh | 我们 | 备注 |
|---|---|---|---|---|---|
| ✅ | 计划模式 | `[自有]` | `dsh-plan-mode` | `PlanMode` + `ExitPlanModeTool` + `/plan` | **照 dsh 做**：`exit_plan_mode` 工具**阻塞**等审阅，批准后同一轮继续；`plan` 是完整 Markdown 文档（须 `# 标题`）；走 `AskUser` 通道带 `intent.kind=plan-review`；2026-11-02 |
| ✅ | 技能提供者注册 | `[自有]` | `dsh-skill*` | `SkillDiscovery` + `SkillTool` | 两种形态（`<name>/SKILL.md` 与 `<name>.md`）；四个根按 rank 合并（项目级压用户级）且记影子链；只注入名字与描述、按需加载正文；`disable-model-invocation` 在目录与加载**两处**都生效；2026-11-03 |
| ⬜ | 会话内目标 | `[—]` | `dsh-goal*` | — | |
| ⬜ | 子 agent | `[—]` | `dsh-subagent*` | — | |

### 9. 任务与调度

| 状态 | 能力 | 实现方式 | dsh | 我们 | 备注 |
|---|---|---|---|---|---|
| 🟡 | 工具结果限制 | `[自有]` | `dsh-output-retention` | `ToolResultLimits` | 保头尾 + 中间落盘可取回；仍非 dsh 的分区裁剪 |
| ⬜ | 工具结果分区裁剪 | `[—]` | `dsh-compaction-tool-result-pruner` | — | dsh 做头/中/尾三段保留且可回放；我们只会截尾 |
| ✅ | 后台任务 | `[自有]` | `dsh-jobs` / `dsh-jobs-local` / `dsh-tool-jobs` | `JobRegistry` + 四个工具 | 启动立即返回；游标只进不退；环形缓冲 256 KiB 且如实报丢弃；按 owner 限并发 10；2026-11-02 |
| ⬜ | 持久提醒 | `[—]` | `dsh-schedule` | — | |
| ✅ | 大结果落盘可取回 | `[自有]` | `dsh-spill` / `spill-local` / `spill-policy` | `SpillStore` + `Clamp(…, spillPath)` | 落盘到工作区 `.dougong/spill`；文件名由内容哈希决定（同内容不重复落盘）；标记给相对路径，模型用已有 `read_file` 取回；`.dougong` 已排除出 glob/grep；2026-11-02 |
| ✅ | 拦截钩子 | `[自有]` | `dsh-hook-protocol` | `IToolInterceptor` + `ToolInterception` | 类型化拦截点：三态决策（Approve/Ask/Deny）、多拦截器最严者胜并短路、审批前执行；Ask 无审批器时 fail-closed；抛异常按拒绝；**不做 CC/Codex hooks.json 桥接**（见 D-27）；2026-11-03 |

### 10. 网络与外部

| 状态 | 能力 | 实现方式 | dsh | 我们 | 备注 |
|---|---|---|---|---|---|
| ✅ | MCP 客户端 | `[库]` | `dsh-mcp-client` | `McpToolRegistry` | 用官方 `ModelContextProtocol` SDK，我们做桥接 |
| 🟡 | MCP 资源 | `[库]` | `dsh-mcp-resources` | `McpResourceCatalog` | 有目录，读写覆盖不全 |
| ✅ | ACP 服务端 | `[自有]` | `dsh-acp` | `AcpAgentHost` | **手写协议实现**（C# 无官方 SDK） |
| 🟡 | web 抓取 | `[自有]` | `dsh-web` / `dsh-web-fetch-http` | `WebFetchTool` | 抓取有；**搜索没有**（需第三方 provider 与 key）；2026-11-02 |
| ⬜ | 出站代理 | `[—]` | `dsh-http-proxy` | — | 企业环境跑不起来 |
| ➖ | Webhook | `[—]` | `dsh-webhook*` | — | 不是 harness 能力 |

### 11. 配置与凭据

| 状态 | 能力 | 实现方式 | dsh | 我们 | 备注 |
|---|---|---|---|---|---|
| ✅ | 凭据引用（不存值） | `[自有]` | `dsh-credentials*` | `ProviderConfigFile.key` + `CredentialFile` | 配置只存条目名，值在 `~/.dougong/credentials`；环境变量回退已删 |
| ✅ | 配置加载与校验 | `[库]` | — | `ProviderConfigFile` + `ProviderConfigWriter` | TOML 用 **Tomlyn**；写入走语法树保留注释 |
| 🟡 | 配置来源层级 | `[自有]` | `dsh-launch-environment` | 偏好走冻结快照并记录来源；provider 走配置/环境变量 | **provider 类仍不记录来源**；偏好类已具备 |
| 🟡 | 命令注册 | `[自有]` | `dsh-commands` | `HandleCommand`（硬编码 switch） | 不可扩展 |
| ✅ | 路径助手 | `[自有]` | `dsh-home-paths` | `DougongHome` | 单一入口 + `DOUGONG_HOME`；`Describe()` 符号化不泄漏用户名 |
| ⬜ | 超时原语 | `[—]` | `dsh-timeout` | 分散实现 | 无统一 `TimeoutReason` 分类 |

---

## 三、P0 进度

| # | 项 | 状态 | 完成日期 |
|---|---|---|---|
| 0 | 文件能力归位到 SDK | ✅ | 2026-10-31 |
| 5 | 原子写 | ✅ | 2026-10-31（随 #0） |
| 6 | 会话格式版本 | 🟡 | 2026-11-02：加版本号用于拒绝；迁移链不做 |
| 1 | glob / grep | ✅ | 2026-10-31 |
| 2 | 按内容替换编辑 | ✅ | 2026-10-31 |
| 3 | shell 沙箱 | ➖ | 决策：只做策略层，OS 级隔离不做（`dsh-capability-gap.md` 阶段 3） |
| 4 | 工具超时 | ✅ | 2026-10-31 |

**P0 完成度：5/5，但有一处折价**：

- `#3 shell 安全` 的最终形态就是**策略层**（`ShellCommandPolicy` 的模式匹配）。
  实测 8 个真实误操作里 6 个被放行，**决定不再补**：没有内核隔离时，
  把判据从"匹配文本"改成"解析路径"只是把军备竞赛推进一步，没有收敛点；
  而只补一半比不补更危险——用户会以为文件受保护。
  OS 级隔离也不做（dsh 自己说它不是安全边界、建议用 VM）。
  完整理由与"使用者的正确预期"见 `dsh-capability-gap.md` 阶段 3。
- `#6 会话格式版本` **部分做了**：加了 `#dougong-session-format:1` 版本行，
  读到更高版本明确报错（`SessionFormatException`，消息写明"不是文件损坏"），
  而不是静默当成空会话。**迁移链仍不做**——那需要为每个历史版本维护冻结解码器，
  与 `compat-policy.md` §7.1 的主判断一致。
  加它的理由不是"要实现兼容"，是消除一个静默错误：此前格式不兼容会让
  旧程序把文件当成"在第一条就断了"，然后当空会话继续写。

**另一个未验项**：P0 这批工具（glob / grep / edit / 超时 / 命令策略）
全部只经过确定性套件，**没有一次真实 provider 的端到端会话**。
按本文的判定规则，行为检查已足够（每项都有负控），但"实际好不好用"
尚未确认。

---

## 三之二、P1 进度

| # | 项 | 状态 | 完成日期 | 检查套件 |
|---|---|---|---|---|
| 8 | 工作区指令加载（`AGENTS.md`） | ✅ | 2026-11-02 | `ws-instructions`（9 项） |
| 9 | 时间上下文 | ✅ | 2026-11-02 | `time-context`（6 项） |
| 10 | 大结果落盘可取回（spill） | ✅ | 2026-11-02 | `spill`（8 项） |
| 11 | 请求重试策略 | ✅ | 2026-11-02 | `retry`（9 项） |
| 13 | 会话内容搜索 | ✅ | 2026-11-02 | `session-search`（8 项） |
| 12 | 会话标题生成 | ✅ | 2026-11-02 | `session-title`（9 项） |
| 7 | 排队中的 `ask_user`（REPL 侧） | ✅ | 2026-11-02 | `ask-user`（11 项） |
| 14 | Web 抓取 | ✅ | 2026-11-02 | `web-fetch`（9 项） |
| 15 | LSP 代码导航 | ⬜ | — | — |
| 16 | 交付物声明（每轮改了哪些文件） | ✅ | 2026-11-02 | `workspace-changes`（8 项） |

**P1 完成度：9/10。**

### 九项各自的"差什么"（按判定规则 4）

| 项 | 差什么 |
|---|---|
| 工作区指令 | **不做热重载**。每次构建提示时重读文件，改了要等下一轮生效。dsh 在文件操作后重新发现嵌套文件、在会话恢复时对账，那需要把"读了哪些文件"记进会话状态。另外**读失败是静默的**——`ISystemPromptSection` 只有"产出或不产出"，没有诊断通道；补偿手段是 `dougong --doctor` 的`指令文件`检查。 |
| 时间上下文 | **只报服务端时区**。dsh 还报浏览器时区（Web 客户端上下文）；我们没有浏览器客户端，单一来源就是全部。这在桌面端接 WebSocket 后需要重看。 |
| spill | **不拆 seam**。dsh 是 `spill` + `spill-local` + `spill-policy` 三件套；我们是一个 `SpillStore` 具体类。**不自动清理**：`.dougong/spill` 只由用户或宿主整个删。 |
| 重试 | **只做 dsh 的 normal 模式**。`always`（无限重试）不做——在 CLI 里它等于"卡住不动"，与直接失败无区别却多烧额度。**已交付增量后不重试**，这是硬约束而非可配置项。 |
| 会话搜索 | **无 seam 接口**。`SessionSearch` 是静态类，宿主换不了后端（进度表里「会话查询 seam」因此标 🟡）。**不建索引**：扫 JSONL 即可，会话多到扫不动时才需要。 |
| 交付物声明 | **只有"每轮改了哪些文件"，没有 `present` 工具**。dsh 的 `dsh-tool-present` 让模型主动把成果列给用户，那需要它判断"什么算交付物"——而我们的清单是机械推导的。**盲区**：shell 命令与手动改动看不到。 |
| Web 抓取 | **只做抓取，不做搜索**——搜索要第三方 provider（dsh 有 exa / perplexity / deepseek 三个），每个都要 key 与计费。**不执行 JavaScript**：脚本渲染的页面拿到空壳，工具会如实说明。**只接受 http/https**（但这不是安全边界，`HttpClient` 本身就拒绝其它协议；守卫的价值是给出能照做的错误消息）。 |
| 提问 | **REPL 版是逐题顺序问答**，不是 dsh 的多问题同屏表单。多选接受逗号分隔的序号，但不支持"边问边改上一个答案"。ACP 侧仍是完整的表单形态。 |
| 会话标题 | **只做确定性兜底**，没有 dsh 的可选异步 LLM 提供者——标题好不好看不如"能认出来"重要，而 LLM 标题要求会话能调模型，恰好在密钥过期时就失效了。**不写入日志**（见下）。 |

### 九项共同的遗留代价

**都没有真实 provider 的端到端验证**，与 P0 的情况相同。其中三项
（时间上下文、重试、spill）的可观察面依赖真实 provider 行为：

- **重试**：只在模拟的 429/5xx 上验过。真实 provider 的限流窗口长度未接触。
  `Retry-After` 的**传递链路已接通并验证**（`retry-reads-provider-retry-after-from-wire`
  用真实 HTTP 服务跑通秒数与无法解析两种形态），但真实 provider 是否下发这个头
  尚未见到实例。
- **时间上下文**：时区转换用 `TimeZoneInfo.Local`，跨时区部署（容器设 `TZ`）
  未实测。
- **spill**：`read_file` 的路径守卫对 `.dougong/spill` 下文件的读取
  已用真实工具验证过，但**模型真的会不会去读**没有证据。

### 复核时修掉的缺陷（记录在此，避免下次重犯）

九项实现在落地后各有一轮复核、最后又做了一轮终检，共修掉 19 个真缺陷：

| 项 | 缺陷 | 现在由哪条检查钉住 |
|---|---|---|
| 工作区指令 | 全局内容全被去重时留下**空的「用户全局约定」标题** | `project-wins-when-identical`、`global-header-dropped-when-fully-deduped` |
| 工作区指令 | 注释声称"记进诊断"，而分节没有诊断通道 | 改为如实说明，并补 `--doctor` 的 `指令文件` 检查 |
| 工作区指令 | `instructions-do-not-starve-tool-policy` **空转**：用汉字填文件时两个预算单位不重合，链预算设 16 KiB 也绿 | 改用 ASCII 填充 + 断言"确实撑满链预算" |
| 时间上下文 | `Render` 里的负间隔分支是**死代码**（`TryRender` 已钳零） | 已删；`time-clock-rollback-clamps-to-zero` |
| 重试 | **HTTP 408** 被判为不可重试（带状态码 → 走不到超时分支，又不在 429/5xx 白名单） | `retry-classifier-maps-status-codes` |
| 重试 | `Retry-After` 的**传递链路断了**：策略层会用它、分类器能读它，但连接器从没往里写——两段各自正确，中间没接 | `retry-reads-provider-retry-after-from-wire` |
| spill | 文件名带时间戳，**同一内容跨秒落盘两次**，而注释写着"不会反复落盘" | `identical-content-reuses-one-file` |
| 会话标题 | 截断上限没给省略号留位，20 字 + "…" = **21 字符**，超出声明的列宽 | `title-never-exceeds-max-chars` |
| 会话标题 | 修完上一条后，**"恰好 20 字的完整输入"被误加省略号**——用字符数判截断区分不了"刚好写满"与"写满后被截断" | `title-exact-fit-has-no-ellipsis` |
| 提问 | 重构时**丢了"不要用凑数选项填充"这句引导**，模型会编假选项塞进去 | `elicitation` 套的 `invalid-shape-sends-nothing` |
| 提问 | 多问题中途 EOF 时诊断只说"输入已结束"，**模型以为一个答案都没拿到会全部重问** | `ask-partial-answers-survive-eof` |
| 会话标题 | **读不到的会话与"还没说话"共用同一个占位**，用户会把打不开的会话当空会话跳过 | `title-unreadable-is-distinct-from-empty` |
| 指令文件 | `--doctor` 在两个目录相同时**把同一个文件列两次**（"全局 AGENTS.md、项目 AGENTS.md"），而实际只加载一次 | `doctor-instructions-lists-each-file-once` |
| Web 抓取 | 删 `<script>` 的正则加了 `NonBacktracking`，而该模式用了反向引用——**真实抓取时抛 `NotSupportedException` 崩掉整轮** | `fetch-drops-script-content`（负控时脚本内容漏进正文） |
| 跨项：spill × glob | 噪声目录名 `".dougong"` 在 `GlobTool` 里是字面量，与 `SpillStore.DirectoryName` 各写一遍——改一处漏一处不报错，spill 文件会进搜索结果 | `spill-root-name-is-derived-not-duplicated` |
| 跨项：CLI × 文档 | `--search` 与 `--changes` 实现完、检查全绿，但 **`--help` 里没有**——用户只能靠读源码发现 | `every-cli-flag-appears-in-help` |
| 终检：检查基础设施 | 检查程序集引用的 `Dougong.Cli.dll` 副本可能比 Release 旧，此时**检查在测旧代码**，该红的不会红 | `comparison-uses-release-cli-assembly` |
| 终检：诊断归因 | `retry` 的失败信息写死"连接器没写进 Data"，而链路有两段——读取端出问题时误导排查方向 | 改为列出两段 |
| 终检：新检查自身误报 | `comparison-uses-release-cli-assembly` 第一版把时间比较方向写反，在**正常构建上就误报** | 改成"只会因明显陈旧而红"，并双向验证 |

这 19 个按成因分类：

- **2 个跨项耦合**：两处独立字面量（spill × glob）、功能与文档脱节（CLI × help）
- **4 个注释/文档与实现不符**：诊断通道、空转说明、去重承诺、死代码注释
  （web 抓取又添一例：把 http 守卫写成了安全机制）
- **1 个两段代码都对、中间没接**：`Retry-After` 链路
- **3 个诊断信息与实现脱节**：doctor 重复列出、EOF 不提已答、失败归因写死单段
- **其余是边界处理缺失或运行期才暴露的崩溃**

两类最值得记：

**"声称比实现写得好"是系统性风险。** 注释与文档总是比代码好看，而检查往往
按那些错误假设来写，于是两者一起绿。这不是偶发失误，是需要专门对抗的倾向。

**"检查本身可能失效"是真问题。** 终检发现检查程序集引用的 CLI 副本比 Release
旧，此时检查在测旧代码——该红的不会红。加上本会话反复出现的"破坏代码编译不过
导致跑旧 DLL"，说明**验证手段自身也需要被验证**。

---

## 三之三、P2 进度

**完成 3 项 + 1 项部分。** 这一节的价值不在状态，而在**排序依据**——
P2 有 15 项，按什么顺序做决定了投入产出。

| # | 项 | 状态 | 检查套件 | 有谁在等 | 规模 |
|---|---|---|---|---|---|
| 20 | 后台任务 | ✅ | `jobs`（12 项） | 无明确宿主；模型跑长构建时阻塞对话 | 中 |
| 19 | 计划模式 | ✅ | `plan-mode`（13 项） | 桌面端已有计划展示，缺审阅退出 | 小 |
| 21 | 持久 shell / PTY | 🟡 | `persistent-shell`（10 项） | 桌面端 P8f 的终端标签页（只差真 PTY） | **shell 已完成**；PTY 仍需 ConPTY/forkpty 两套 |
| 22 | 技能提供者 | ✅ | `skills`（11 项） | 无 | 中 |
| 23 | 拦截钩子 | ✅ | `intercept`（10 项） | 无 | 中 |
| 28 | 文件引用 `@file` | ✅ | `file-references`（8 项） | 桌面端输入区 | 小 |
| 29 | 会话内切换工作目录 | ✅ | `working-directory`（11 项） | 无 | 小 |
| 30 | 消息级反馈 | ✅ | `feedback`（13 项） | 桌面端 | 小 |
| 24 | 会话投影 / 轮次大纲 | ⬜ | — | 宿主各自从日志重算 | 中 |
| 26 | 重复工具调用提醒 | ⬜ | — | 无 | 小 |
| 27 | 权限预设 | ⬜ | — | 无 | 小 |
| 25 | 出站代理 | ⬜ | — | 企业环境 | 小 |
| 17 | 子 agent 委派 | ⬜ | — | 无 | 大 |
| 18 | 会话内目标 | ⬜ | — | 无 | 中 |
| 31 | 远端执行（SSH） | ⬜ | — | 无 | 大 |

### 已完成三项的"差什么"

| 项 | 差什么 |
|---|---|
| 后台任务 | **不持久化**：作业活在进程内，程序退出就没了（把进程内句柄落盘再恢复是不可能的）。**进程树结束只在 POSIX 验证**：Windows 上构造不出可靠的"父活子活"场景，检查跳过，靠代码保证。 |
| 计划模式 | **工具集不按模式裁剪**：dsh 在计划模式下不提供写工具，我们的工具集装配期固定，所以靠运行时拦。后果是模型能"看见"被拦的工具，可能反复试。 |
| 文件引用 | **不建索引**：一次遍历是毫秒级，而索引要解决失效（文件增删改后何时重建）。有 `maxEntries` 上限，到顶即停并标记截断。 |
| 持久 shell | **不是 PTY**：无作业控制，交互式程序（vim/top/less）会挂住、无 Ctrl+C 语义。**超时会重启 shell**，代价是丢掉 cd/export 状态。 |

### 排序依据：有没有真实的等待方

P1 的经验：**有明确宿主在等的项，优先级高于"看起来该有"的项**。
理由是前者有可观察的验收点（"桌面端那个标签页能不能用了"），
后者只能靠自评。

**唯一有硬等待方的是 #21 PTY**：桌面端 P8f 的终端标签页已确认
`TerminalView`（Ghostty 内核）能工作，只差 PTY 进程 + 双向字节流。
这是打包条件，不是"想要"。

**#20 后台任务是第二优先**，但不是因为有人等——是因为它是 #21 的底座。
两者共同的底层是"子进程生命周期管理"：启动、读写管道、超时、
退出码、被杀后的清理。先做后台任务能把这层抽出来，PTY 复用它。
反过来做会让 PTY 里塞一份进程管理、后台任务里再塞一份。

### 为什么其余项都可以等

- **小项（#19/#28/#29/#30/#26/#27/#25）**：每项都是几十到一两百行，
  但都需要**有人真的提出来**。它们各自解决的是特定交互形态的细节，
  猜错方向比不做更浪费——P1 里 `--list` 被 provider 检查拦住这类问题，
  是真实使用才暴露的。
- **大项（#17 子 agent / #31 SSH）**：规模接近子系统，而当前没有场景。
  子 agent 的价值在"并行探索互不干扰"，SSH 的价值在"目标机器与开发机不同"——
  两者都还没有真实诉求。
- **#24 会话投影**：宿主各自从日志重算，目前算得动。等宿主抱怨慢再做，
  那时才知道瓶颈在哪个字段。

### 与 P1 的一个差别

P1 的目标是"基本闭环能不能用"，所以可以按能力清单扫过去。
P2 的目标是"特定场景好不好用"，**没有真实场景就没有验收标准**——
这是为什么这一节按"有谁在等"排序，而不是照 dsh 的包编号。

---

## 四、这份清单怎么维护

**只在状态变化时改**，不按时间刷。改的时候同时改两处：
- 下面对应那一行
- §一 的总览计数

**判定从严**，四个具体规则：

1. **有类名不算**。类存在但没接进 `SessionComposition` 的装配，算 `⬜`。
   上轮实例：`SystemPrompt` 属性在，但 CLI 从不赋值，那一栏永远是 0。
2. **接进装配但没验证，算 🟡**。验证指有确定性套件钉住行为，
   不是"我试过一次能跑"。
3. **验证了但只验"能跑"不算 ✅**。要回答移植的三问
   （对外暴露出什么 / 改内部实现哪一项会变而编译仍过 / 怎么破坏它检查才红），
   见 `porting-differences.md` §一"验收怎么问"。
   **这不是一份题单**：每项能力的可观察面不同，答案不同。
   2026-10-31 的教训：P0 工具全都"接进装配、编译通过、检查全绿"，
   按这三问一查仍有四个缺陷，且**分别落在 Agent 循环、工具层、协议层、终端层**——
   预设一份维度清单不会恰好覆盖它们。
4. **`🟡` 必须在"备注"列写清差什么**。写不出差什么，说明没核过，
   应该回去核而不是标 `🟡`。

**✅ 必须有证据**。每个 ✅ 都要在 `porting-differences.md` §三 指向一条
能跑起来的检查，并记录负控怎么做的。`tool-package-checks` 的三条检查
（`every-claim-has-evidence`、`deviation-records-cite-real-locations`、
`evidence-suites-exist-and-are-registered`）会拦住没有证据的声称。

**➖ 必须能在 gap 文档里找到理由**。找不到就说明是漏了，不是不做。
