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

## 一、进度总览

| 能力域 | ✅ | 🟡 | ⬜ | ➖ | 小计 | 完成度 |
|---|---|---|---|---|---|---|
| 1. Agent 循环 | 3 | 1 | 3 | 1 | 8 | 50% |
| 2. LLM 与用量 | 4 | 2 | 3 | 0 | 9 | 56% |
| 3. 会话与持久化 | 2 | 3 | 7 | 1 | 13 | 29% |
| 4. 工具 | 2 | 4 | 11 | 3 | 20 | 22% |
| 5. 审批与提问 | 2 | 1 | 1 | 0 | 4 | 62% |
| 6. 沙箱与进程 | 0 | 2 | 3 | 0 | 5 | 20% |
| 7. 文件系统 | 3 | 1 | 2 | 0 | 6 | 58% |
| 8. 技能/目标/计划/子agent | 0 | 1 | 3 | 0 | 4 | 12% |
| 9. 任务与调度 | 0 | 1 | 4 | 0 | 5 | 10% |
| 10. 网络与外部 | 2 | 1 | 2 | 1 | 6 | 50% |
| 11. 配置与凭据 | 2 | 3 | 2 | 0 | 7 | 50% |
| **合计** | **20** | **20** | **41** | **6** | **87** | **37%** |

完成度算法：`(✅ + 🟡×0.5) / (✅+🟡+⬜)`，不含 `➖`。
（`(20 + 10) / 81 = 37%`）

**统计口径**：这些数字是从下面第二部分的表格逐行数出来的，
不是手填的。改了一行状态就要同步改这里——两处对不上说明漏改。

**代码规模参考**：Agent 7.9k 行 / Ai 4.2k / Kernel 3.5k / Acp 1.8k / Mcp 0.7k / Hosting 0.6k。

---

## 二、逐项清单

### 1. Agent 循环

| 状态 | 能力 | dsh | 我们 | 备注 |
|---|---|---|---|---|
| ✅ | 循环与事件词汇 | `dsh-agent`, `dsh-agent-loop` | `LoopAgentRuntime`, `AgentEvent` | |
| ✅ | 默认模型选择 | `dsh-agent-default-model` | `ProviderCatalog`, `default_provider` | |
| ✅ | 上下文压缩接入 | `dsh-compaction-basic` | `SlidingWindowCompactor` + 摘要 | |
| 🟡 | 系统提示组装 | `dsh-system-prompt` | `LoopAgentRuntime.SystemPrompt` | **单值，无分节注册表**；且 CLI 主对话根本没设置它 |
| ⬜ | 工作区指令加载 | `dsh-agent-instructions` | — | 不读 `AGENTS.md` / `CLAUDE.md` |
| ⬜ | 部署人格段落 | `dsh-persona` | — | |
| ⬜ | 时间上下文 | `dsh-time-context` | — | 模型不知道"现在几点" |
| ➖ | 工具呈现形态（PTC/原生） | `dsh-agent-tool-presentation` | — | 我们没有 PTC |

### 2. LLM 与用量

| 状态 | 能力 | dsh | 我们 | 备注 |
|---|---|---|---|---|
| ✅ | provider 中立接口 | `dsh-llm` | `IChatClient` (M.E.AI) | |
| ✅ | 三种线协议 | — | `OpenAiChatCompletions` / `Responses` / `Anthropic` | dsh 在 `dsh-llm-deepseek` 里 |
| ✅ | token 测量 | `dsh-token-meter` | `TokenBudget` + `ContextBreakdown` + `UsageAnchor` | 2026-10-30 补齐 |
| ✅ | 用量记账 | — | `UsageLedger` + `UsageReported` | 比 dsh 独立 |
| 🟡 | 多 provider 数据 | `dsh-llm-pi-ai`（42 家） | `ProviderPresets`（22 家 / 662 模型） | 数据生成，但要靠协议推断才知用哪种 |
| 🟡 | 推理强度翻译 | — | `ThinkingParameters` + `WireCompat.Thinking` | **翻译层完整，但无法从配置触发**——TOML 里没有对应键 |
| ⬜ | 请求重试策略 | `dsh-llm-retry` | — | 失败直接上抛 |
| ⬜ | 图片卸载 | `dsh-compaction-image-offload` | — | 不支持图片输入 |
| ⬜ | 会话统计（轮数/墙钟） | `dsh-session-stats` | — | CLI 自己算 |

### 3. 会话与持久化

| 状态 | 能力 | dsh | 我们 | 备注 |
|---|---|---|---|---|
| 🟡 | 事件溯源会话 | `dsh-session` | `JsonlAgentStore` | append-only JSONL，**无投影层** |
| ✅ | 持久化 seam | `dsh-session-persistence` | `IAgentStore` | |
| ✅ | JSONL 后端 | `dsh-session-persistence-jsonl` | `JsonlAgentStore` | |
| 🟡 | 会话恢复与分叉 | — | `SessionRecovery`, `SessionBranch` | 有分叉，但无"消息树"概念 |
| 🟡 | 提交屏障 | `dsh-session-checkpoint-policy` | 提交屏障（四处落点） | 有屏障，**无显式检查点策略** |
| ➖ | 格式版本与迁移 | `dsh-session-format*` | — | **明确不做**，见 `compat-policy.md` §7.1 |
| ⬜ | 会话状态投影 | `dsh-session-projection*` | — | 宿主各自从日志重算 |
| ⬜ | 会话内容搜索 | `dsh-session-query-sqlite` | `SessionBranch.List` 只列文件 | |
| ⬜ | 会话标题生成 | `dsh-session-title*` | — | |
| ⬜ | 轮次大纲 | `dsh-session-turn-outline` | — | |
| ⬜ | 跨会话引用 | `dsh-session-reference` | — | |
| ⬜ | 日志导出 | `dsh-session-log-export` | — | |
| ⬜ | 会话遥测 | `dsh-session-telemetry*` | `DougongTelemetry`（仅 Meter） | 无 OTel exporter |

### 4. 工具

| 状态 | 能力 | dsh | 我们 | 备注 |
|---|---|---|---|---|
| ✅ | 注册与执行管线 | `dsh-tools` | `ToolDispatcher` | |
| ✅ | 计划工具 | `dsh-tool-todo` | `TodoWriteTool` | |
| 🟡 | 读文件 | `dsh-tool-fs` | `ReadFileTool`（已进 SDK） | 返回 version |
| 🟡 | 写文件 | `dsh-tool-fs` | `WriteFileTool`（已进 SDK） | 原子写 + 版本守卫 |
| ⬜ | **glob / grep** | `dsh-tool-fs-search` | — | **P0**，模型不能查代码 |
| ⬜ | **按内容替换编辑** | `dsh-tool-str-replace-editor` | — | **P0**，改一行要重写全文 |
| 🟡 | shell | `dsh-tool-bash` | `ShellTool` | 能跑，但**无沙箱**（`cd /` 可绕过）、无持久 shell、无后台任务 |
| ⬜ | PowerShell | `dsh-tool-pwsh*` | — | |
| ⬜ | **工具超时** | `dsh-tool-call-timeout-policy` | — | **P0**，工具卡住挂住整轮 |
| 🟡 | 向人类提问 | `dsh-tool-ask-user` | `AcpElicitationTool` | 仅 ACP，REPL 无等价物 |
| ⬜ | 技能加载 | `dsh-tool-skill` | — | |
| ⬜ | 子 agent 委派 | `dsh-tool-subagent*` | — | |
| ⬜ | 后台任务控制 | `dsh-tool-jobs` | — | |
| ⬜ | web 搜索/抓取 | `dsh-tool-web` | — | |
| ⬜ | 文件交付声明 | `dsh-tool-present` | — | |
| ⬜ | 目标工具 | `dsh-tool-goal` | — | |
| ⬜ | 重复调用提醒 | `dsh-repeat-tool-reminder` | — | |
| ➖ | 工作流脚本 | `dsh-tool-workflow` | — | |
| ➖ | 插件运行时探查 | `dsh-tool-cordis` | — | |
| ➖ | Ralph 循环 | `dsh-tool-ralph` | — | |

### 5. 审批与提问

| 状态 | 能力 | dsh | 我们 | 备注 |
|---|---|---|---|---|
| ✅ | 一次性审批 fail-closed | `dsh-user-approval` | `IToolApprover` + `PermissionPolicyApprover` | |
| ✅ | 审批记录持久化 | （拼在 user-approval 里） | `ApprovalLog` | 比 dsh 更显式 |
| 🟡 | 向人类提问 seam | `dsh-user-questions` | `AcpElicitationTool` | 仅 ACP |
| ⬜ | 权限预设 | `dsh-permission-presets` | — | 沙箱 + 审批没打包成一个选项 |

### 6. 沙箱与进程

| 状态 | 能力 | dsh | 我们 | 备注 |
|---|---|---|---|---|
| 🟡 | 路径守卫 | `dsh-sandbox-policy` | `WorkspacePathGuard` | **只管路径拼装**，`cd /` 能绕过 |
| 🟡 | 子进程执行 | `dsh-subprocess` | `ShellTool` | 无进程组、无升级杀 |
| ⬜ | **进程沙箱** | `dsh-sandbox*` | — | **P0** |
| ⬜ | 持久 PTY | `dsh-terminal*` | — | 桌面端要的 PTY |
| ⬜ | Win32 Job Object | `dsh-win32-process` | — | |

### 7. 文件系统

| 状态 | 能力 | dsh | 我们 | 备注 |
|---|---|---|---|---|
| ✅ | 文件能力 seam | `dsh-fs` | `IFileSystem` + `LocalFileSystem` | 2026-10-31 补齐，四个方法 |
| ✅ | **原子写** | `dsh-atomic-write` | `LocalFileSystem` 内实现 | **P0 已解决** |
| ✅ | **版本守卫写** | `dsh-fs-observation-policy` | `FileWriteExpectation` | **P0 已解决** |
| 🟡 | 读后写观察态 | `dsh-fs-observation-policy` | — | 有守卫但**不强制**先读后写 |
| ⬜ | 沙箱内文件操作 | `dsh-fs-sandbox` | — | 依赖 §6 沙箱 |
| ⬜ | `@file` 引用 | `dsh-file-reference*` | — | |

### 8. 技能 / 目标 / 计划 / 子 agent

| 状态 | 能力 | dsh | 我们 | 备注 |
|---|---|---|---|---|
| 🟡 | 计划展示 | `dsh-plan-mode` | `TodoWriteTool` + `PlanTool` | 只有展示，**无"带审阅的退出"** |
| ⬜ | 技能提供者注册 | `dsh-skill*` | — | L1 记忆是另一回事 |
| ⬜ | 会话内目标 | `dsh-goal*` | — | |
| ⬜ | 子 agent | `dsh-subagent*` | — | |

### 9. 任务与调度

| 状态 | 能力 | dsh | 我们 | 备注 |
|---|---|---|---|---|
| 🟡 | 工具结果限制 | `dsh-output-retention` | `ToolResultLimits` | 只有总量截断 |
| ⬜ | 后台任务 | `dsh-jobs*` | — | |
| ⬜ | 持久提醒 | `dsh-schedule` | — | |
| ⬜ | 大结果落盘可取回 | `dsh-spill*` | — | 截断后取不回来 |
| ⬜ | 拦截钩子 | `dsh-hook-protocol` | — | |

### 10. 网络与外部

| 状态 | 能力 | dsh | 我们 | 备注 |
|---|---|---|---|---|
| ✅ | MCP 客户端 | `dsh-mcp-client` | `McpToolRegistry` | |
| 🟡 | MCP 资源 | `dsh-mcp-resources` | `McpResourceCatalog` | 有目录，读写覆盖不全 |
| ✅ | ACP 服务端 | `dsh-acp` | `AcpAgentHost` | |
| ⬜ | web 搜索/抓取 | `dsh-web*` | — | |
| ⬜ | 出站代理 | `dsh-http-proxy` | — | 企业环境跑不起来 |
| ➖ | Webhook | `dsh-webhook*` | — | 不是 harness 能力 |

### 11. 配置与凭据

| 状态 | 能力 | dsh | 我们 | 备注 |
|---|---|---|---|---|
| ✅ | 凭据引用（不存值） | `dsh-credentials*` | `ProviderConfigFile.key_env` | 语义一致 |
| ✅ | 配置加载与校验 | — | `ProviderConfigFile` + `ProviderConfigWriter` | 比 dsh 显式（原子写配置） |
| 🟡 | 配置来源层级 | `dsh-launch-environment` | 配置 > 环境变量 > 默认 | **只到三层，不记录"每个值由哪层给"** |
| 🟡 | 命令注册 | `dsh-commands` | `HandleCommand`（硬编码 switch） | 不可扩展 |
| 🟡 | 路径助手 | `dsh-home-paths` | 分散在各处 | |
| ⬜ | 超时原语 | `dsh-timeout` | 分散实现 | 无统一 `TimeoutReason` 分类 |
| ⬜ | 内�� | `dsh-schedule` | — | |

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
