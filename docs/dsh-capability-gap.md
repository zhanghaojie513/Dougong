# dsh 能力全景与我们实现的对照

> **这份文档回答什么**：dsh 有哪些能力、我们实现了哪些、还缺哪些。
>
> **为什么需要它**：过去几轮开发里反复出现"桌面端/CLI 做到一半发现 SDK 缺东西"，
> 补一处又冒出另一处。根因是没有一张完整的能力底账——每次都靠撞墙才知道缺什么。
>
> **怎么用**：决定"下一步做什么"之前先看这里。缺的东西标了优先级，不是按
> dsh 有什么就抄什么——`goal.md` 的反目标仍然有效，只是那份反目标从没被
> 逐项套到 dsh 的能力面上过。
>
> 核对日期 2026-10-30，dsh 共 **236 个包**（含 `cordis*` 等基础设施），
> 本文件覆盖其中 117 个与 harness 能力直接相关的。

---

## 一、对照方法

**不是"dsh 有我们就必须有"。** 每个包按四类处置：

| 处置 | 含义 |
|---|---|
| **已有** | 我们有等价能力，语义对齐 |
| **缺** | 确实没有，影响实际使用 |
| **不该有** | 属于产品形态或它自己的实现选择，我们不做 |
| **不必有** | 它的形态依赖 Node/Cordis，我们架构不同但目标一致 |

**判定依据**：`goal.md` 的四条性质（可组合、失败可见、可验证、克制）与反目标。

---

## 二、Agent 循环与运行

| dsh | 能力 | 我们 | 差距 |
|---|---|---|---|
| `dsh-agent` | Agent 接口、注册表、事件词汇 | `IAgentRuntime` + `AgentEvent` | **已有** |
| `dsh-agent-loop` | 具体循环实现 | `LoopAgentRuntime` | **已有** |
| `dsh-agent-default-model` | 入口共用的默认模型选择 | `ProviderCatalog` + `default_provider` | **已有** |
| `dsh-agent-instructions` | AGENTS.md / CLAUDE.md 工作区指令加载 | **无** | **缺**：只认配置，不读工作区约定文件 |
| `dsh-agent-tool-presentation` | 工具以 PTC 或原生调用的形态呈现 | **无** | 不必有（我们没有 PTC） |
| `dsh-system-prompt` | 系统提示词组装注册表 | `LoopAgentRuntime.SystemPrompt`（单值） | **缺**：没有分节注册表 |
| `dsh-persona` | 部署级人格段落 | **无** | **缺** |
| `dsh-time-context` | 每步注入当前时间与耗时 | **无** | **缺**：模型不知道"现在几点" |
| `dsh-tmux-context` | 注入 tmux 面板位置 | **无** | 不必有 |

**缺的三项里，`dsh-time-context` 最值得补**：模型没有时间概念会导致
"三天前你说过"这类判断全错，而补它是几十行的事。

---

## 三、LLM 与用量

| dsh | 能力 | 我们 | 差距 |
|---|---|---|---|
| `dsh-llm` | provider 中立服务 | `IChatClient`（M.E.AI） | **已有** |
| `dsh-llm-retry` | provider 路由的请求重试 | **无** | **缺**：失败直接上抛 |
| `dsh-llm-pi-ai` | 42 家接入 | `ProviderCatalog` 数据 + 三种协议 | **部分**：数据有，协议只有三种 |
| `dsh-token-meter` | 回放感知的 token 测量 | `TokenBudget` + `ContextBreakdown` + `UsageAnchor` | **已有**（2026-10-30 补齐） |
| `dsh-session-stats` | 会话统计（轮数、墙钟时间） | **无** | **缺**：CLI 自己算 |
| `dsh-compaction` | 压缩 seam | `IContextCompactor` | **已有** |
| `dsh-compaction-basic` | 阈值策略 + LLM 摘要 | `SlidingWindowCompactor` + `SummarizingCompactionTask` | **已有** |
| `dsh-compaction-image-offload` | 超预算图片替换为占位符 | **无** | **缺**：我们没有图片处理 |
| `dsh-compaction-tool-result-pruner` | 工具结果头/中/尾裁剪 | `ToolResultLimits`（只截断） | **部分**：只有总量截断，没有分区裁剪 |

---

## 四、会话与持久化

| dsh | 能力 | 我们 | 差距 |
|---|---|---|---|
| `dsh-session` | 事件溯源会话存储 | `JsonlAgentStore` | **部分** |
| `dsh-session-persistence` | 持久化 seam | `IAgentStore` | **已有** |
| `dsh-session-persistence-jsonl` | JSONL 后端 | `JsonlAgentStore` | **已有** |
| `dsh-session-format*` | 格式版本与迁移链（v0→v1→v2） | **无** | **缺**：没有版本概念，格式变了旧会话就打不开 |
| `dsh-session-checkpoint-policy` | 模型请求与工具副作用前的语义检查点 | **部分**：有提交屏障，无显式检查点策略 | **缺** |
| `dsh-session-projection*` | 日志派生的会话状态投影 | **无** | **缺**：宿主各自从日志重算 |
| `dsh-session-query` / `-sqlite` | 会话查询（FTS5 全文搜索） | `SessionBranch.List`（列文件） | **缺**：不能搜内容 |
| `dsh-session-title*` | 会话标题生成（LLM） | **无** | **缺**：CLI 显示会话文件名 |
| `dsh-session-turn-outline` | 轮次大纲投影 | **无** | **缺** |
| `dsh-session-reference` | 跨会话快照引用 | **无** | **缺** |
| `dsh-session-log-export` | 会话日志导出 | **无** | **缺** |
| `dsh-session-telemetry*` | 会话遥测 | `DougongTelemetry`（Meter） | **部分** |
| `dsh-chunked-list` | 追加式分块列表 | **无** | 不必有（JSONL 够用） |

**这一块是缺口最大的区域**。`dsh-session-format` 那条尤其要紧：
我们没有任何版本概念，格式一变旧会话就打不开，而 `compat-policy.md` 里
"配置迁移与回滚"被列为暂不实现——这两个决定现在对不上了。

---

## 五、工具

| dsh | 能力 | 我们 | 差距 |
|---|---|---|---|
| `dsh-tools` | 工具注册与执行管线 | `ToolDispatcher` | **已有** |
| `dsh-tool-fs` | 读 / 写 / 编辑 | `ReadFileTool` / `WriteFileTool`**（在 CLI 项目，不在 SDK）** | **缺**：见下方"工具归属错位" |
| `dsh-tool-fs-search` | glob / grep（打包 ripgrep） | **无** | **缺**：模型不能搜代码 |
| `dsh-tool-str-replace-editor` | view / create / 字面替换 / 行插入 | **无** | **缺** |
| `dsh-tool-bash` / `-persistent` | shell，含后台任务与沙箱升级 | `ShellTool` | **部分**：无持久 shell、无后台 |
| `dsh-tool-pwsh` / `-persistent` | PowerShell | **无** | **缺**（Windows 上更重要） |
| `dsh-tool-todo` | `todo_write` | `TodoWriteTool` | **已有** |
| `dsh-tool-ask-user` | `ask_user_question` | `AcpElicitationTool`（仅 ACP） | **缺**：REPL 里没有等价物 |
| `dsh-tool-skill` | 技能加载工具 | **无** | **缺** |
| `dsh-tool-subagent` / `-control` | 子 agent 委派与控制 | **无** | **缺** |
| `dsh-tool-jobs` | 后台任务控制 | **无** | **缺** |
| `dsh-tool-web` | `web_search` / `web_fetch` | **无** | **缺** |
| `dsh-tool-goal` | 目标工具 | **无** | **缺** |
| `dsh-tool-present` | 显式文件交付声明 | **无** | **缺** |
| `dsh-tool-workflow` | 工作流脚本 | **无** | **缺**（但不是我们的目标） |
| `dsh-tool-cordis` | 插件运行时探查 | **无** | 不必有 |
| `dsh-tool-ralph` | Ralph 循环 | **无** | 不必有 |
| `dsh-tool-call-timeout-policy` | 逐工具超时 | **无** | **缺**：工具卡住会挂住整轮 |
| `dsh-repeat-tool-reminder` | 重复调用提醒 | **无** | **缺** |

**工具面缺口最大，也是桌面端最直接感受到的**：只有 read/write/shell/todo 四个，
没有搜索、没有编辑、没有 web。做一个编码 agent 连 grep 都没有。

### 工具归属错位（比缺工具更根本）

**`ReadFileTool` / `WriteFileTool` 定义在 `Dougong.Cli/FileTools.cs`，不是 SDK。**

这意味着：
- 桌面端要用文件工具，**只能自己重写一遍**——事实上它也确实重写了
- 两个宿主的文件读写行为会分叉（路径校验、编码、大小限制）
- 同一个"读写文件"能力，SDK 里没有位置

`dsh` 的对应做法是把工具放在 `dsh-tool-fs`，**运行在 `ctx.fs` seam 之上**：
工具只管协议形状，实际 IO 由可替换的 `dsh-fs-local` / `dsh-fs-sandbox` 提供。

**这一条不是"缺一个工具"，是"缺一层"。** 补工具之前应先决定文件能力放哪：
- 要么在 SDK 加 `Dougong.Agent.FileTools`（工具 + 内联 IO）
- 要么照 dsh 加 `IFileSystem` seam（工具 + 可替换 IO）

前者快，后者才能支持沙箱与远程工作区。考虑到第 3 项沙箱也在 P0，
建议直接做 seam——否则补完工具还要再改一遍。

---

## 六、批准、问题与权限

| dsh | 能力 | 我们 | 差距 |
|---|---|---|---|
| `dsh-user-approval` | 一次性审批，fail-closed，审批/请求瀑布 | `IToolApprover` + `PermissionPolicyApprover` + `ApprovalLog`（含记录持久化） | **已有** |
| `dsh-user-questions` | 向人类提问的 seam | `AcpElicitationTool`（仅 ACP） | **部分**：REPL 侧没有等价物 |
| `dsh-permission-presets` | 权限预设（沙箱 + 审批打包成一个选项） | **无** | **缺** |

**注**：dsh 没有独立的"审批持久化"包，审批记录拼在 `dsh-user-approval`
与 `dsh-client-ui-approval` 里。我们的 `ApprovalLog` 是独立实现，
比它更显式——这一项我们反而更清楚。

---

## 七、沙箱与进程

| dsh | 能力 | 我们 | 差距 |
|---|---|---|---|
| `dsh-sandbox` | 进程沙箱 seam | **无** | **缺** |
| `dsh-sandbox-local` | bwrap / landlock / Seatbelt / Windows ACL | **无** | **缺** |
| `dsh-sandbox-policy` | 逐调用沙箱策略 | `WorkspacePathGuard`（仅路径） | **部分**：只管路径，不管命令 |
| `dsh-bash-sandbox` / `pwsh-sandbox` | 沙箱内执行 | **无** | **缺** |
| `dsh-subprocess` | 进程组、有界输出、升级杀 | `ShellTool`（简单版） | **部分** |
| `dsh-win32-process` | Win32 Job Object | **无** | **缺** |
| `dsh-terminal` / `-bash` | 持久 PTY | **无** | **缺**（桌面端要的 PTY） |
| `dsh-shell` / `shell-env` | bash 执行 seam、受管环境变量 | `ShellTool` | **部分** |

**沙箱是最大的安全缺口**：`ShellTool` 目前只在工作区路径上做限制，
模型可以通过 `cd /` 或绝对路径绕过。`dsh` 有完整的 bwrap/ACL 实现。

---

## 八、文件系统

| dsh | 能力 | 我们 | 差距 |
|---|---|---|---|
| `dsh-fs` | 文件能力 seam（文本 IO + 版本守卫的原子变更） | **无 seam**：工具直接读文件 | **缺**：没有可替换的文件层 |
| `dsh-fs-local` | 本地实现 | 工具内直接用 `File.*` | **部分** |
| `dsh-fs-sandbox` | 沙箱内文件操作 | `WorkspacePathGuard` | **部分** |
| `dsh-fs-observation-policy` | 观察态、读后写、版本守卫 | **部分**：`FileVersionSet` 只在审批时用 | **缺**：工具执行时无版本校验 |
| `dsh-atomic-write` | 原子替换 | `WriteFileTool`（直接写） | **缺**：写一半崩溃会得到半个文件 |
| `dsh-file-reference` / `-local` | `@file` 引用与模糊索引 | **无** | **缺** |

---

## 九、技能、目标、计划、子 agent

| dsh | 能力 | 我们 | 差距 |
|---|---|---|---|
| `dsh-skill` / `-filesystem` | 技能提供者注册表 | **无** | **缺**（L1 记忆是另一回事） |
| `dsh-goal` / `-round-driver` | 会话内目标的持久状态与轮次驱动 | **无** | **缺** |
| `dsh-plan-mode` | 计划模式（带用户审阅的退出） | **部分**：`TodoWriteTool` 只是计划展示 | **缺** |
| `dsh-subagent` / `-fork` / `-spawn` | 子 agent seam 与两种后端 | **无** | **缺** |

---

## 十、任务、调度、工作流

| dsh | 能力 | 我们 | 差距 |
|---|---|---|---|
| `dsh-jobs` / `-local` | 后台任务注册表 | **无** | **缺** |
| `dsh-schedule` | 持久提醒 | **无** | **缺** |
| `dsh-workflow` / `-ptc` | 工作流引擎 | **无** | **缺**（`goal.md` 把可视化编排列为反目标，但脚本式不是） |
| `dsh-hook-protocol` / `-hooks-*` | 拦截钩子（兼容 Claude Code / Codex） | **无** | **缺** |
| `dsh-timeout` | 超时/截止原语 | 分散在各处 | **部分** |

---

## 十一、网络与外部

| dsh | 能力 | 我们 | 差距 |
|---|---|---|---|
| `dsh-web` | 搜索/抓取 seam | **无** | **缺** |
| `dsh-web-fetch-http` | HTTP 抓取 | **无** | **缺** |
| `dsh-web-search-deepseek` | 搜索 provider | **无** | **缺** |
| `dsh-mcp-client` | MCP 客户端 | `McpToolRegistry` | **已有** |
| `dsh-mcp-resources` | MCP 资源读写 | `McpResourceCatalog` | **部分** |
| `dsh-http-proxy` | 出站代理策略 | **无** | **缺**：企业环境跑不起来 |
| `dsh-webhook*` | Webhook 运行时 | **无** | 不必有 |

---

## 十二、配置、凭据、启动

| dsh | 能力 | 我们 | 差距 |
|---|---|---|---|
| `dsh-credentials` / `-local` | 凭据 seam（设置存引用） | `ProviderConfigFile` + `key_env` | **已有**（语义一致） |
| `dsh-launch-environment` | 记录"每个值由哪一层提供" | **无** | **缺**：配置来源只到"文件 or 环境变量" |
| `dsh-home-paths` | 路径助手 | 分散 | **部分** |
| `dsh-scope` | 作用域标签与过滤派发 | **无** | 不必有（内核有 ProviderId） |
| `dsh-commands` | 人的命令注册表 | `HandleCommand`（硬编码 switch） | **部分**：不可扩展 |
| `dsh-cmdline` | 不可变命令行交接 | `args` 直接解析 | **部分** |
| `dsh-output-retention` | 有界保留原语 | `ToolResultLimits` | **部分** |
| `dsh-spill*` | 溢出存储（大结果落盘 + 取回定位符） | **无** | **缺**：大结果只能截断，取不回来 |

---

## 十三、宿主与展示层

| dsh | 能力 | 我们 | 差距 |
|---|---|---|---|
| `dsh-acp` / `-acp-app` | ACP 服务端 | `AcpAgentHost` | **已有** |
| `dsh-sdk-*` | SDK 的 JSON-RPC 服务 | **无** | **缺**（我们的 SDK 是进程内库） |
| `dsh-*ui-*`（约 40 个） | Web 客户端 | 桌面端自建 | 不必有 |
| `dsh-host-*` | Web 宿主 | 无 | 不必有 |

**UI 层不列细项**：那是产品形态，不是 harness 能力。桌面端自己做。

---

## 十四、汇总：按优先级排序的缺失清单

### P0 — 挡住核心使用

| # | 缺什么 | 影响 | 对应 dsh |
|---|---|---|---|
| 0 | **文件工具不在 SDK 里** | 桌面端必须自己重写，两宿主行为必然分叉 | `dsh-fs` + `dsh-tool-fs` |
| 1 | **文件搜索（glob / grep）** | 模型不能查代码，做不了编码 agent | `dsh-tool-fs-search` |
| 2 | **文件编辑（按内容替换）** | 只能整文件重写，改一行要重写全文 | `dsh-tool-fs` / `dsh-tool-str-replace-editor` |
| 3 | **shell 沙箱** | 模型可用 `cd /` 绕过工作区限制 | `dsh-sandbox*` |
| 4 | **工具超时** | 工具卡住会挂住整轮，无恢复 | `dsh-tool-call-timeout-policy` |
| 5 | **原子写** | 写一半崩溃得到半个文件 | `dsh-atomic-write` |
| 6 | **会话格式版本** | 格式一变革旧会话打不开 | `dsh-session-format*` |

### P1 — 明显影响体验

| # | 缺什么 | 对应 dsh |
|---|---|---|
| 7 | 排队中的 `ask_user`（REPL 侧） | `dsh-user-questions` |
| 8 | 系统提示分节注册（`AGENTS.md` 加载） | `dsh-system-prompt` / `dsh-agent-instructions` |
| 9 | 时间上下文 | `dsh-time-context` |
| 10 | 大结果落盘可取回（spill） | `dsh-spill*` |
| 11 | 请求重试策略 | `dsh-llm-retry` |
| 12 | 会话标题生成 | `dsh-session-title*` |
| 13 | 会话内容搜索 | `dsh-session-query-sqlite` |
| 14 | Web 搜索/抓取 | `dsh-web*` |

### P2 — 有更好，但可以等

| # | 缺什么 | 对应 dsh |
|---|---|---|
| 15 | 子 agent 委派 | `dsh-subagent*` |
| 16 | 会话内目标（goal） | `dsh-goal*` |
| 17 | 计划模式（带审阅退出） | `dsh-plan-mode` |
| 18 | 后台任务 | `dsh-jobs*` |
| 19 | 持久 shell / PTY | `dsh-terminal*` |
| 20 | 技能提供者 | `dsh-skill*` |
| 21 | 拦截钩子 | `dsh-hook-protocol` |
| 22 | 会话投影 / 轮次大纲 | `dsh-session-projection*` |
| 23 | 出站代理 | `dsh-http-proxy` |
| 24 | 重复工具调用提醒 | `dsh-repeat-tool-reminder` |
| 25 | 权限预设 | `dsh-permission-presets` |
| 26 | 文件引用 `@file` | `dsh-file-reference*` |

### 明确不做

| 不做 | 理由 |
|---|---|
| PTC 运行时（`dsh-ptc-*`） | 那是"用 JS 脚本调工具"的形态，我们的工具就是 C# 对象 |
| 动态双半插件（`dsh-cordis-*`） | Node 特有的运行时挂载，我们的插件是 assembly |
| Typert / Remote / BFF（`dsh-typert-*`、`dsh-api-*`） | Web 客户端的协议栈，桌面端用自己的 IPC |
| 图像卸载（`dsh-compaction-image-offload`） | 我们还不支持图片输入 |
| Webhook（`dsh-webhook*`） | 不是 harness 能力 |
| 全部 UI 包（约 40 个） | 产品形态，桌面端自建 |

---

## 十五、从这份清单得到的两个结论

**① 缺口集中在"工具面"和"会话面"，不是"内核面"。**

内核（生命周期、服务、依赖、事件、动态加载）我们比 dsh 覆盖得更完整
（dsh 用 Cordis，我们用自己实现的 `DougongKernel`）。缺的全是**能力插件**——
这正是 `goal.md` P1 说"新能力应能独立发布而无需修改内核"的地方。

**好消息是：补这些不需要动内核。** 每个都是独立的工具类或服务实现。

**② `dsh-session-format` 那条与既有决策冲突，需要重新决定。**

`compat-policy.md` §"M1 交付清单"里写着「配置迁移与回滚：暂不实现」，
理由是"0.x 阶段没有外部用户"。但我们自己的会话文件已经有格式了，
一旦改动，用户手上的会话就打不开——这个风险 dsh 用版本化格式解决了。

这条要么补版本机制，要么在文档里明确"0.x 不做会话兼容"并接受旧会话作废。
**现在是含糊的，那是隐患。**

---

## 十六、推进顺序（详细）

顺序不是按"dsh 有就用"排的，按**依赖关系**排——前一项不做，后一项要返工。

---

### 阶段 1：文件能力归位到 SDK

**为什么排第一**：它是搜索、编辑、原子写三项的共同前提。
现在 `ReadFileTool`/`WriteFileTool` 在 `Dougong.Cli`，桌面端只能重写。
先把能力搬进 SDK，后面三项才有地方长。

**要做的事**

1. 决定形态：**加 `IFileSystem` seam 还是直接在 SDK 加工具类？**
   - 直接加工具类：快，但沙箱阶段要改一遍
   - 加 seam（照 dsh 的 `dsh-fs`）：慢一步，但沙箱、远程工作区都能接进来
   - **建议 seam**，因为阶段 3 的沙箱必然要拦 IO
2. `IFileSystem` 最小接口：读全文、读区间、写、是否存在、列目录、
   **带版本守卫的写**（`FileVersionSet` 已有，但只在审批时用）
3. 把 `ReadFileTool`/`WriteFileTool` 从 CLI 搬到 `Dougong.Agent`，
   改为走 seam
4. CLI 改回用 SDK 的（删掉自己那份）
5. **验收**：`EntryParityChecks` 扩展——两个入口的文件工具必须是同一个类型

**风险**：搬动会改公开 API（工具类从 CLI 移到 SDK）。项目无外部调用者，
直接移，不留别名。

---

### 阶段 2：文件搜索与编辑

**依赖阶段 1**（要 seam 才能实现"只搜工作区内"）。

1. **glob / grep**
   - 不打包 ripgrep（那是 30MB 二进制，且跨平台分发麻烦）
   - 用 .NET 自带的 `Directory.EnumerateFiles` + 正则，先实现正确版；
     性能不够时再考虑引入
   - **必须走 seam**：沙箱模式下搜索也要被限制在工作区内
   - 模型可见名称注意 `^[a-zA-Z0-9_-]+$`（`architecture.md` §5.2 已记录）

2. **按内容替换编辑**（`edit` 工具）
   - 参数：路径、`old_string`、`new_string`、是否要求唯一匹配
   - **必须要求唯一匹配**：不唯一的批量替换会一次改坏多处，
     而模型看不出改了什么
   - 走 seam 的**版本守卫写**：读到的是 A 版本、写回时已是 B 版本就失败

3. **验收**
   - 搜索：工作区外的匹配不得出现
   - 编辑：多处匹配时报错而不是改全部
   - 编辑：文件在读取后被外部修改时，写回必须失败

---

### 阶段 3：shell 沙箱

**单独一轮，独立于工具改动**——它要设计子进程限制，Windows/Linux 形态不同。

1. **先定义语义**，不是先写代码：
   - `read-only`：拒绝一切写
   - `workspace-write`：只能写工作区与临时目录
   - 拒绝时的行为：**fail-closed**（拒绝而不是降级放行）
2. **Windows**：受限令牌 + ACL 写白名单（`dsh-sandbox-windows-acl` 的做法）
3. **Linux/macOS**：bwrap / Seatbelt 不一定可用，
   **探测失败必须明确报错，不能静默降级成无沙箱**
4. 与 `WorkspacePathGuard` 的关系：那个只管路径拼装，
   管不住 `cd / && rm -rf`。两者是不同层次，不替代
5. **验收**：真实尝试 `cd /` 写文件必须被拒；探测不到沙箱时工具不可用而非放行

**这一项的判定标准是"拒绝得对不对"，不是"能不能跑"。**

---

### 阶段 4：工具超时与原子写

两项都小，一起做。

1. **工具超时**
   - 在 `ToolDispatcher` 里包一层：每个调用带 deadline
   - **需要先决定终态怎么表达**。现在 `ToolCallOutcome` 有八种取值
     （`Succeeded` / `Failed` / `RejectedInvalidArguments` /
     `RejectedDuplicateInvocation` / `RejectedUnknownTool` /
     `CancelledBeforeExecution` / `RejectedNotApproved` /
     `CancelledDuringExecution` / `OutcomeUnknown`），**没有 `TimedOut`**。
     两个方向：
     - 加 `TimedOut` —— 语义最清楚，但"超时可能已产生副作用"这个事实
       要不要像 `OutcomeUnknown` 那样要求宿主对账？
     - 复用 `CancelledDuringExecution` + `Diagnostic` 说明原因 —— 不改枚举，
       但宿主分不清"用户取消"与"自己挂住了"
     **建议加 `TimedOut`**，并在文档里说清它与 `OutcomeUnknown` 的区别：
     超时是"我们知道它还在跑、已经放弃等待"，未知是"我们不知道跑没跑"。
   - 关键：**超时后不能再等工具线程**。`ShellTool` 的子进程必须被杀，
     否则一个挂住的命令会让进程永远不退出
2. **原子写**
   - `File.WriteAllTextAsync` 改为"临时文件 + 改名"
   - 同目录临时文件（跨目录改名不是原子的）
   - 保留原文件权限
3. **验收**：超时后进程能退出；写到一半崩溃后目标文件仍是旧内容

---

### 阶段 5：会话格式与观测面（按需）

**这一阶段现在不必做。** 因为兼容立场已明确（`compat-policy.md` §7.1）：
0.x 不承诺会话兼容，格式变更后旧会话作废。

所以阶段 5 只在下面任一成立时才启动：

- 出现真实用户报告旧数据打不开
- 会话格式发生第二次不兼容变更
- 有第三方工具开始读我们的会话文件

到那时照 dsh 做：冻结每个历史版本的解码器 + 迁移链。

---

### 与 P1/P2 的关系

P1 里**系统提示分节注册**（`dsh-system-prompt`）值得提前到阶段 2 之后做，
因为阶段 2 加的编辑/搜索工具会往提示里加内容，现在 `SystemPrompt` 是个单值，
没法分节。**先有注册表再往里加，比加完再拆便宜。**

其余 P1/P2 项按实际需要触发，不预先排期——`goal.md` P4 反对
"宣称未验证的能力"，同理也反对"预先作好却没消费者的架子"。

---

### 一句话总结

**阶段 1→2 让"编码 agent"成立；阶段 3 让它安全；阶段 4 让它不挂住；
阶段 5 是决策触发，不是工作项。**
