# TKCode 与 pi.net 的做法对比（M2.4 前置调查）

> **这份文档的作用**：在动手实现 `Dougong.Acp` 之前，先核实两个参照项目的实际做法，
> 避免按"我以为它们是这样"来实现。这是 [quality-gates.md](quality-gates.md) 门 2
> （外部契约对齐）的要求。
>
> **核实方式**：全部结论来自直接读取对方仓库的源码与文档（GitHub API），
> 不是凭印象或二手转述。核实日期 2026-10-24。

---

## 一、最重要的一个差异：方向相反

| | TKCode | Dougong |
|---|---|---|
| 角色 | ACP **Client** | ACP **Agent** |
| 谁启动谁 | 它启动 Agent 子进程 | 被别人（桌面/编辑器）启动 |
| stdout | 从 Agent 读协议帧 | 向 Client 写协议帧 |
| 权限流 | 它是被请求方，弹窗给用户 | 它是请求方，发 `session/request_permission` |

**这个差异决定了不能照搬**：TKCode 的大量实现细节是在解决"如何可靠地驱动一个不听话的子进程"，
而我们要解决的是"如何作为一个被驱动的进程正确响应"。两者共用的是**协议形状**，
不共用**控制流**。

值得直接借鉴的是：协议字段的准确含义、真实 Agent 的怪癖清单、时序陷阱。
不能借鉴的是：进程管理、审批 UI、重连策略。

---

## 二、TKCode 的做法（ACP Client，10 430 KB，C#/Avalonia/NativeAOT）

### 2.1 它做对且值得学的四件事

**（1）把协议规格核实成一份带出处标注的文档**

`docs/protocols/acp-v1.md` 每条结论都标来源：`[Schema]`（一手 schema.json）、
`[Doc:xxx]`（官方文档）、`[zeron]`（另一个实现的交叉核对）、`[本机]`（自己 grep 安装包）、
`[待核实]`（未确认，实现时必须容错）。

**这一点最值得学**。它把"我知道"和"我猜的"分开标注了，后者明确要求容错。
我们 M1 的四个协议缺陷全部来自"我以为它是这样"——如果当时也做这种标注，
`finish_reason` 那条会被标成 `[待核实]`，就不会被兜底逻辑掩盖那么久。

**（2）分帧：按行，不是 Content-Length**

ACP 的 stdio 分帧是 **JSON 一行一条，`\n` 分隔，没有 `Content-Length` 头**。
TKCode 特意注明："这与 TKCode 内部 UI↔Host 的 `JsonRpcConnection`（Content-Length 分帧）
不同，必须使用按行分帧模式"。

对我们直接有用：`Dougong.Acp` 必须用按行分帧，不能因为"MCP 也是 JSON-RPC"就套用
MCP 的分帧方式。

**（3）单行没有长度上限**

图片、diff、`rawOutput` 都可能让一行达到数 MB。TKCode 用 `PipeReader` 可增长缓冲，
并设 64 MiB 保护上限，超过判定连接损坏。

这条对我们同样成立：不能用固定大小缓冲读 stdin。

**（4）顺序保证：所有入站消息在同一读循环里严格按到达顺序处理**

这是全文档最有价值的一条。原因：`session/prompt` 的响应标志轮次结束，
而它之前的 `session/update` 必须先被应用。如果响应走"挂起表 → 另一个线程完成 Task"，
而通知走另一条队列，就会出现"轮次已结束，但最后几个增量还没到"。

TKCode 注明 zeron 为此修了两次。**这是设计层面的陷阱，不是实现细节**——
我们的 `IAgentRuntime` 用 `IAsyncEnumerable<AgentEvent>` 输出事件，翻译到
`session/update` 时天然有序；但"响应先于事件"的竞态仍可能出现，因为
`session/prompt` 的响应要等整个事件流跑完才发。这一点我们的结构反而更安全。

### 2.2 它对能力的取舍（直接影响我们的实现）

TKCode 声明 `fs.*` 和 `terminal` 能力为 **false**，理由写得很清楚：

- `fs.readTextFile/writeTextFile` 的意义是让 Agent 读到编辑器**未保存的缓冲区**；
  它不是编辑器，磁盘就是唯一真相。声明后就要自己实现路径边界检查、权限和并发写入，
  等于给 Agent 开一个绕过其自身权限系统的后门。
- `terminal` 声明后 Agent 会把命令交给 Client 执行，Client 就要负责沙箱、输出缓冲、
  超时和进程树清理——本该是 Agent 的职责。不声明时 Agent 用自己的 shell 工具，
  照样走 `session/request_permission` 审批。

**对我们的启示**：Dougong 作为 ACP **Agent**，如果 Client 声明了这些能力，
我们可以选择用它们（例如让客户端的文件系统工具参与我们的工具管线），但
`architecture.md §5.2` 的立场是"annotations 仅作提示，不是可信授权依据"。
同理：**Client 声明了能力，不代表我们可以把权限判断外包给它**。
首版不实现 `fs/*` 与 `terminal` 的 Agent 侧调用，工具仍走自己的 dispatcher。

### 2.3 审批映射（对我们反向适用）

TKCode 是被请求方：Agent 发 `session/request_permission`，它弹窗。

我们的方向相反：**我们要发这个请求**，等 Client 回复。可直接借鉴的规则：

- `outcome` 是判别式：`{"outcome":"selected","optionId":"..."}` 或 `{"outcome":"cancelled"}`
- `optionId` 必须**原样回传 Agent 自己提供的某个值**——但我们作为请求方，
  是**提供**选项的一方，所以要点变成：给 Client 的 `options` 要覆盖
  `allow_once` / `allow_always` / `reject_once` 三类，且每种最多一个
- 非规范形状（同一 kind 出现多次、未知 kind）Client 可能退化为"选项列表"模式
- **没有回复时必须按拒绝处理**：TKCode 在超时/断连/关窗时一律拒绝，
  我们作为请求方同样要有超时并视为拒绝，不能无限等

这条与我们 dispatcher 的 `IToolApprover` 完全对得上：
`session/request_permission` 就是 `IToolApprover` 的一种实现（`AcpApprover`），
`ToolApprovalDecision.Allow/Deny` 直接映射到 `selected(allow_once)` / `selected(reject_once)`。

### 2.4 真实 Agent 的怪癖清单（TKCode 实测，我们可复用）

| 现象 | 影响 |
|---|---|
| 启动时往 stdout 打印横幅/警告 | 解析失败的行必须跳过而非断连，连续大量才判损坏 |
| 只发一个已终态的 `tool_call`，不再发更新 | 事件翻译不能假设"一定有 in_progress" |
| `tool_call_update` 引用从未出现过的 `toolCallId` | 当作新调用创建 |
| `tool_call_update.content` 是**整体替换**不是追加 | 误按追加处理会拼出错误内容 |
| `title` 在参数到达前是占位文本（如 `Read File`） | 只用于显示，不能当参数解析 |
| 同一 kind 出现多次的权限选项（Codex 发两个 `allow_always`） | 不能让 Client 侧的映射假设"每种一个" |
| 未认证时 `session/new` 报 `-32000` | 需要认证流程；我们作为 Agent 侧则是**要能发** `-32000` |

### 2.5 它没有的东西

- **没有 ACP Agent 侧实现**：全部代码在 `src/TKCode.Runtimes/Acp/`，是 Client。
- 没有协议版本协商的 Agent 侧（它只校验 `protocolVersion == 1`，不等就断连）。

---

## 三、pi.net 的做法（Agent harness 移植，17 532 KB）

### 3.1 它没有 ACP

`src/Pi.Protocol/` 是**自研的 CBOR 协议**（`CborEncoder`/`CborDecoder`/`Framing`），
用于它自己的 session worker 通信，不是 ACP。

`src/Pi.Mcp/Protocol/` 是 MCP 实现（`JsonRpc`、`Content`、`Types`）。

因此 **M2.4 无法从 pi.net 借鉴 ACP 实现**。这一点必须如实记录，
避免"pi.net 也做 ACP"的错误印象。

### 3.2 它的 agent loop 结构（已在 M1 对比过）

`src/Pi.Agent/AgentLoop.cs` 的注释写得很清楚：外层（follow-up 续跑）× 内层
（工具调用 / steering 消息）。这与我方 `LoopAgentRuntime` 的多轮结构一致，
M1 已做过候选对比，结论是自研循环。

### 3.3 它值得学的一件工程习惯

`design.md` 里提到过：**样例由 CI 编译保活**（学 pi.net）。这条我们已经在做
（`samples/` 在 solution 里，CI 构建全部）。

---

## 四、对 M2.4 的具体结论

### 4.1 必须自己写，不能移植

- 协议编解码：按行分帧、JSON-RPC 2.0、双向 id 空间独立
- Agent 侧的消息处理：`initialize` 协商、`session/new`、`session/prompt`、
  `session/cancel` 的接收与响应
- 事件翻译：`IAgentRuntime` 的 `AgentEvent` → `session/update` 通知
- 审批反向流：`IToolApprover` → `session/request_permission` 请求

### 4.2 直接采用的既有结论

| 结论 | 来源 | 对应我们的设计 |
|---|---|---|
| stdio 按行分帧，无 Content-Length | TKCode §2.1 | 必须 |
| 单行无上限，需可增长缓冲 + 保护上限 | TKCode §2.3 | 必须 |
| 无法解析的行跳过，连续大量才判损坏 | TKCode §2.3 | 必须 |
| 请求 id 可为 int 或 string，回复原样回传 | TKCode §2.3 | 必须 |
| 双向 id 空间独立 | TKCode §2.3 | 必须 |
| 入站消息严格按到达顺序处理 | TKCode §2.4 | 必须 |
| `protocolVersion` 不等于 1 时断连而非降级 | TKCode §3.4 | 采用 |
| 未回复的权限请求视为拒绝 | TKCode §8.3 | 必须 |
| `session/update` 有 14 种稳定变体 | TKCode §7 | 首版只实现 4-5 种，其余忽略 |
| 提问是**工具**，不是宿主特权 | TKCode `AskUserTool.cs` + `docs/design/ask-user.md`；pi.net 的 `question.ts` / `questionnaire.ts` | 见 §4.5 |
| 提问取工作区独占，避免问答与审批同时弹 | TKCode `docs/design/ask-user.md`（明确写出 `Concurrency = Exclusive` 并接受等待代价） | 见 §4.5 |
| 提问不走审批（`NoAccess`） | TKCode 同上 | 见 §4.5 |
| 传输失败**不得**报成"用户跳过" | TKCode 文档记录的 Cursor 事故 | 见 §4.5 |
| form 模式 schema 是扁平对象，属性名即字段标识 | ACP schema（`requestedSchema`） | 属性名用 `header` |
| plan 是**全量替换**的条目列表 `{content, priority, status}` | ACP schema | 未实现，理由见 §4.6 |

### 4.5 elicitation（已实现）

调研三个同类实现后，形状高度一致，都是「1–4 个问题、每题 2–4 个选项、每个选项带说明」：

| 项目 | 暴露方式 | 备注 |
|---|---|---|
| Claude Code | `AskUserQuestion` 工具 | 最常被模仿的形状 |
| Codex | `request_user_input` 工具 | 同上 |
| TKCode | `ask_user` 工具 + `question`/`questionnaire` 扩展 | 唯一写了设计文档说明取舍的 |
| pi.net | 仅扩展（`question.ts`、`questionnaire.ts`） | 没有做成工具 |

我们采用**工具**形态（`ask_user`），理由有三：

1. 工具天然经过 `IToolDispatcher`——审计、并发协调、终态记录都是现成的。
   做成宿主特权等于在 dispatcher 之外再开一条执行路径，与"唯一执行入口"冲突。
2. 模型能自己决定何时问，宿主不必猜测意图。
3. 工具形态让"提问不可用"有地方表达（返回结果），不需要额外的错误通道。

三条从 TKCode 直接采纳、并落进代码与验收的结论：

- **取工作区独占**（`AcpElicitationTool.DescribeExecution` → `WorkspaceExclusive`）。
  代价是等待用户回答期间占住工作区锁，但这段时间的"工作"本来就是停下来等用户；
  收益是避免问答面板与审批面板同时弹出。
- **不走审批**。`DescribePermission` 报 `ToolPermission(None, IsKnown: true, …)`。
  但 dispatcher 会对每次调用都请求审批（这是正确的保守默认），
  因此宿主显式套一层 `PermissionPolicyApprover`：仅当
  **已知 + 无访问 + 未声明任何路径/命令/域名** 三者同时成立时才跳过询问。
  未知权限（MCP 工具全部如此）与声明了范围的工具照旧逐次审批。
- **四种结果必须分开**（`AskOutcome`）：`Answered` / `Declined` / `Cancelled` /
  `Unavailable`，另有 `InvalidRequest`。**客户端报错、传输异常、本轮取消都不能
  伪装成"用户拒绝"**——这是 Cursor 踩过的坑，模型会据此误判自己已被否决。
  `Cancelled` 之下还用诊断文本区分"用户关闭了对话框"与"本轮被取消、用户根本没看到"。

另有一条本地约束：形状不合法（例如只有 1 个选项）时**不发请求**，
直接返回 `InvalidRequest` 并提示模型"如果只有一条合理路径，直接说明并继续，
不要编造凑数选项"。这类问题问出来只会浪费用户一次点击。

能力声明只加 `form`，**不加 `url`**：URL 模式要求客户端在安全上下文打开链接、
对 Punycode 域名告警，我们无法验证客户端是否做到，声明了就是在承诺做不到的事。
入站方向的 `elicitation/create` 以 −32602 拒绝并说明"方向反了"，
而不是笼统报"不支持"——后者会让对端以为是能力问题。

### 4.6 plan：为什么还没做

`plan` 的数据源是协议里最麻烦的一处。ACP 的 plan 是
`{entries:[{content, priority, status}]}`，且**每次都是全量替换**。
要做对，运行时必须持续维护"当前计划"，而 `IAgentRuntime` 目前只产出
`RunStarted` / `TextDelta` / `ToolCallRequested` / `ToolCallFinished` /
`ContextCompacted` / `RunFinished`，没有 plan 事件。

两个可选做法都不够好：

- 由运行时从工具调用序列**猜**计划：这会把"看起来像计划"的东西报给用户，
  用户无法分辨哪些是真计划、哪些是推测，属于 P3"可解释失败"的反面。
- 加一个让模型自己维护 TODO 的工具：这是独立的一片功能（自己的 schema、存储、
  恢复语义），塞进 M2 会让它变成半成品。

因此**不做**，并在 `status.md` 记为已知缺口，而不是"实现了但走不通"。


### 4.3 首版范围（按门 3："从哪个入口触达"）

**只做能被外部 ACP 客户端驱动跑通一轮对话的最小闭环**：

- `initialize`（协商 v1，声明能力）
- `session/new` → 创建会话（绑定一个 `LoopAgentRuntime` + JSONL store）
- `session/prompt` → 驱动一轮运行，翻译事件为 `session/update`
- `session/cancel` → linked CancellationToken
- `session/update`：`agent_message_chunk`、`agent_thought_chunk`、`tool_call`、
  `tool_call_update`、`usage_update`
- `session/request_permission`：由 `AcpApprover` 实现 `IToolApprover`
- `session/load` / `session/resume` / `session/list` / `session/close`（M2.4 补齐）
- `elicitation/create`（Agent → Client）：由 `ask_user` 工具发起，见 §4.5

**不做**：`fs/*`、`terminal`、`plan`、`available_commands`。
声明不支持，而不是"实现了但走不通"。（`elicitation` 已按 §4.5 实现。）

### 4.4 硬约束（写进验收）

1. **stdout 只能有协议帧**。日志必须走 stderr。这是最容易踩的坑：
   任何 `Console.WriteLine` 都会污染协议。首版要有一个检查项专门验证这点。
2. **响应先于事件**的竞态：`session/prompt` 的响应必须在所有 `session/update`
   之后发出，否则客户端会丢最后几条增量。
3. **权限请求超时视为拒绝**，不能无限等。
4. **进程退出时所有挂起请求以错误结束**，不能静默吞掉。

---

## 五、这份文档的局限

- 只读了 TKCode 的协议文档与目录结构，**没有读它的 ACP 实现源码**
  （`AcpRuntime.cs`、`AcpSession.cs`）。如果要借鉴具体控制流，需要再读。
- pi.net 只读了目录与 README，确认了"没有 ACP"这一结论所需的证据。
- 两个项目的 ACP schema 版本可能不同；我们的实现应以**一手 schema** 为准，
  这两份材料只作为"真实实现会遇到什么问题"的参考。

**下一步**：按 §4.3 的范围实现，先写一个能被真实 ACP 客户端驱动的检查项，
再补全消息处理。动手前先确认 `Dougong.Acp` 引入哪些依赖——按现有分层，
它只能引用 `Dougong.Agent`（L1），不能引用 `Dougong.Ai`（同为 L2）。
