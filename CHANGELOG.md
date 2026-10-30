# Changelog

本文件记录**公开 API** 与可观察行为的变化。

格式参考 [Keep a Changelog](https://keepachangelog.com/zh-CN/1.1.0/)，
版本号遵循 [语义化版本](https://semver.org/lang/zh-CN/)。

## 关于兼容性

本项目处于 `0.x`，**不做向后兼容承诺**，但**每条公开 API 变更都会被自动检出并记录**。
完整规则见 [docs/compat-policy.md](docs/compat-policy.md)，要点：

- 五个 SDK 包（Kernel / Agent / Ai / Mcp / Acp）都启用了 `EnablePackageValidation`，
  公开 API 的增删改会在 `dotnet pack` 时报 `CP****` 错误
- 删改公开 API 前先标 `[Obsolete]` 并写清替代方案，至少跨一个次版本
- **行为变化**（默认值、语义、输出格式）ApiCompat 检不出，必须在这里单独记一行

**这里只写"对使用者有影响"的变化**，不写内部重构。

---

## [Unreleased]

## [0.1.10-alpha.1] — 2026-10-30

**修一个会让应用无法启动的数据缺陷，并把配置里的预算真正接通。**

### Fixed

- **预设表里 52 条模型的输出上限 ≥ 上下文窗口，会让应用起不来**。
  `kimi-k3` 是 1048576/1048576、`doubao` 是 256000/256000、`grok-4.5` 是 500000/500000。
  照抄后 `ctx - out - 512 <= 0`，`ContextLimits.Validate()` 抛异常，
  而用户看到的报错是"上下文窗口配置不可行"——完全看不出是预设数据的问题。

  **根因不是上游数据错，是我把字段语义理解错了**：models.dev 的
  `limit.output` 是"输出方向的**容量上限**"，不是"单次回复该配多少"。
  dsh 的措辞更准确：`contextWindow` 是 "Maximum **combined** request and
  response context"——输入输出共享那一个窗口。

  修法三层：
  1. **生成期收敛**（`SafeOutputLimit`）：输出不超过上下文的 25%，
     且保证 `ctx - out - 512 > 0`。不改 `context`——那是实打实的事实。
  2. **生成后自检**：任何越界组合直接拒绝写文件，避免以后被 `CORRECTIONS` 改坏。
  3. **解析期拦截**：用户手写的越界值在解析时报错，点名两个数字、算出差值、
     说清怎么改。此前是装配期抛异常，那时应用已经起不来了。

- **配置文件里的 `context_window` / `max_output_tokens` 不生效**。
  `dougong model` 把能力表里的值写进配置，首页与 `/status` 也显示它，
  但 CLI 装配只读环境变量，运行时用的是常量 4096——用户看到 1M 上下文，
  实际按另一个数算。更糟的是桌面端读配置、CLI 读常量，同一个文件在两边
  算出不同预算，而两边都不报错。

  现在取值优先级：**provider 配置 > 环境变量 > 默认值**。配置文件是用户的
  持久选择，不该被临时变量顶掉。

- **首页横幅读环境变量而不是配置**：`/status` 读装配后的真实值、横幅读环境变量，
  两处打架。现在统一。

### Added

- **`DOUGONG_MAX_OUTPUT_TOKENS`**：临时覆盖输出上限，不必改配置文件。

### Docs

- **`architecture.md` 新增 §0「参考项目：谁在哪方面权威」**。
  这一轮踩到的坑都源于只参考了一家：先只看 pi.net（二次移植快照），
  后来才有 models.dev 与上游 pi；做推理强度时只看 pi，没对照 dsh 的类型契约，
  结果把 `limit.output` 的语义理解错了。

  现在写清分工：**数据找 models.dev，行为找 pi，契约措辞找 dsh，
  交互找 hermes，C# 写法找 pi.net**。同一问题两家冲突时，先看谁给了解释。

### Added

- **`IShellExecutor`：命令执行的可替换 seam**，加 `LocalShellExecutor` 实现。

  **为什么需要它**：进程创建此前焊在 `ShellTool` 里（直接 `new Process`）。
  这带来两个后果：
  - 换沙箱执行要**改工具本身**——而沙箱是"怎么起进程"的问题，
    与"模型怎么调命令"无关，两者不该耦合。
  - 测试里无法替换，跑一次就要真起进程，跑不了"命令被拒"这类分支。

  与 dsh 的对应：它分的正是 `dsh-shell`（执行 seam）与 `dsh-tool-bash`（工具）。
  我们此前把两者合成一个类，而阶段 3 的沙箱必须从这一层进。

  接口只描述"起一个进程并等它结束"，**不含策略**：命令黑白名单、输出上限、
  超时语义都不在 seam 里。把策略塞进去会让"换实现"变成"换语义"，那不是替换。

  新增 `ShellTool(IShellExecutor, ...)` 重载；原 `ShellTool(string workspace, ...)`
  保留，内部构造 `LocalShellExecutor`。换沙箱只换这一个参数。

- **`filesystem` 套件加一项 `shell-executor-is-replaceable`**：用替身执行器
  验证工具确实走 seam，且参数作为独立 argv 项被引用（不拼进命令文本——
  那等于把 shell 语法交给模型）。反向对照确认：让工具忽略注入的执行器后，
  该条准确报红。

### 顺带修的一处测试缺陷

`shell-executor-is-replaceable` 第一版只断言单引号引用，在 Windows 上失败——
而失败原因与它要防的问题无关。改成接受两种平台的引用符：
**只断言一种会让用例在另一个平台上假红，那种红会把真正的回归埋掉。**

### Added

- **`IFileSystem`：文件能力的可替换 seam**，加 `LocalFileSystem` 实现。
  ```csharp
  ValueTask<string> ReadTextAsync(path, ct);
  ValueTask<FileStat?> StatAsync(path, ct);          // 版本是不透明值
  ValueTask<IReadOnlyList<FileEntry>> ListDirectoryAsync(path, ct);
  ValueTask<FileWriteOutcome> WriteTextAsync(path, content, expected?, ct);
  ```

  **为什么需要这一层**：沙箱要在写入前拦截；版本守卫要在同一处实现；
  原子写的"临时文件 + 改名"不该在每个工具里重写。工具直接调 `File.*` 时，
  拦截点只能是每个调用方——漏一个就是洞。

  **与 dsh 的 `dsh-fs` 的差别**：它有 `processPath`、`fileUrl`、跨执行世界的
  路径映射与 `watch`，那些服务于"沙箱进程里路径与宿主不同"和远程工作区。
  我们目前没有这两种形态，按 `goal.md`「不做没有消费者的架子」只放四个方法。

- **`FileWriteOutcome.Before`：写之前的内容**。这是 diff 的唯一原料——
  写完之后再读拿到的是新内容。桌面端的"改动前后对照"就是缺这个。

- **`FileWriteExpectation`：版本守卫**（`MustNotExist` / `MustMatchVersion`）。
  不符合时抛 `FileVersionConflictException`，**不静默覆盖**。
  防的是"模型读到第 100 行，期间用户手工改了文件，写回时把用户改动冲掉"。
  版本用**内容哈希**而不是 mtime：FAT 的 mtime 粒度是 2 秒，两次快速修改
  会得到相同 mtime，于是"内容变了但版本没变"。

- **`ReadFileTool` / `WriteFileTool` 搬到 SDK**（原在 `Dougong.Cli/FileTools.cs`）。
  桌面端此前只能自己重写一遍，两个宿主的路径校验、编码、大小限制必然分叉。
  现在两个入口共用同一实现，并由 `filesystem` 套件断言它们住在 `Dougong.Agent`。

  `read_file` 的返回增加 `version`，`write_file` 增加可选的 `expected_version`
  与 `previous_content`。不传 `expected_version` 时行为与之前一致（整文件重写）。

- **`write_file` 遇到版本冲突时返回失败结果而不是抛异常**：这是模型可以
  自我纠正的常规情况（重读文件再来），不是环境故障。抛异常会让整轮以
  Failed 结束，而模型其实只需要知道"文件变了"。

### Breaking

- **`ReadFileTool` / `WriteFileTool` 构造参数从 `string workspace` 改为
  `IFileSystem files`**，且类型从 `Dougong.Cli` 移到 `Dougong.Agent`
  （`internal` → `public`）。项目无外部调用者，直接改不留别名。

### Added

- **`ContextUsageReported`：上下文的估算构成**，每轮一次。
  拆成三项：`SystemPromptTokens` / `ToolDefinitionTokens` / `MessageTokens`，
  外加 `EstimatedTotalTokens` 与 `VerifiedInputTokens`。

  **为什么必须 SDK 提供**：三项里有两项宿主拿不到——系统提示词是 SDK 拼的；
  工具定义是 SDK 把 `IAgentTool` 序列化成 schema 的产物，宿主手里只有工具对象，
  自己算等于重做一遍序列化，必然与实际发出去的不一致。此前桌面端的右栏
  "上下文" tab 只能标注"未实现"，CLI 也只能自己扫一遍历史，漏掉这两项。

  **口径照 dsh 的 `contextBreakdown`**（`systemTokens` / `toolsTokens` /
  `messageTokens`），语义一致：**上下文的构成，不是提供方计费规模**。

- **`UsageReported`：本轮真实用量**（`InputTokens` / `OutputTokens` /
  `CacheReadTokens` / `CacheWriteTokens` / `TotalTokens`）。

  **为什么与上面拆成两个事件**：这个是 provider 回报的**实测值**，可以对账；
  上面那个是**估算**，不能对账。混成一个会让成本统计出错。
  `CacheWriteTokens` 拿不到时是 `null` 而不是 0——0 会被当成"确实没有缓存写入"。

- **`UsageAnchor`：用真实用量做估算基准**。
  有 provider 回报的输入 token 时，以它为基准、只估算其后新增的消息；
  没有基准时才纯字符估算。真实用量里已包含系统提示与工具 schema，
  比逐项估算准一个量级。这与 pi 的 `estimateContextTokens` 同一思路。

  **锚点会失效**，这是最容易写错的地方：压缩替换历史后必须作废 ——
  它测的是压缩前的旧历史，留着会算出"压缩后比压缩前还满"。

- **`ContextBreakdown`：三项拆分的计算**（公开，宿主可复用于自己的展示）。

- **`LoopAgentRuntime.SystemPrompt`**：系统提示词全文，供上面那项估算。
  不传就是 null，该项显示 0——**不编一个值**。

### 关于精度

`ContextUsageReported` 的数值**全部是估算**，不是实测；`UsageReported` 才是实测。

这一条必须写进文档和命名：`EstimatedTotalTokens`、`WindowRatio` 都带"估算"限定。
两个数用途不同（前者"装了多满"，后者"花了多少"），混用会让成本统计出错。

压缩后、下一次模型响应之前，`EstimatedTotalTokens` 返回 `null`——
那时唯一可信的用量是压缩**之前**的，给一个数只会误导。pi 同样返回 `null`
（`getContextUsage()` 注释：*context token count is unknown until the next LLM response*）。

### Changed

- **拆开"输出容量上限"与"单次请求预留"两个概念**。
  `ContextLimits.MaxOutputTokens` 改名为 `RequestOutputReserve`，
  `CompactionInput` 的第三个参数同步改名。

  **为什么必须拆**：一个字段承担了两个含义完全不同的数——
  - **模型输出容量上限**（models.dev 的 `limit.output`）：这个模型最多能吐多长，
    可以是 1048576，与上下文窗口同大（`kimi-k3` 就是 1048576/1048576，
    那是"输入输出共享这一个窗口"）。
  - **单次请求预留**：这一次调用准备让模型写多少，通常是几千 token。

  预算公式 `窗口 − 输出 − 预留` 用的是后者，而之前传的是前者。
  `kimi-k3` 因此算出 `1048576 − 1048576 − 512 = −512`，装配期抛异常、
  **应用无法启动**，用户看到的报错是"上下文窗口配置不可行"——
  完全看不出是我们把两个概念混了。

  与 dsh 的对应：它的 `reservedCompletionTokens` 就是"单次请求预留"，
  取自请求真正发的 `max_tokens`，不是模型容量。

- **预设表恢复上游真值**。此前为了让旧公式不崩，生成脚本把输出上限
  收敛到上下文的 25%（`kimi-k3` 被写成 262144，这是编的值）。
  概念拆开后不再需要，现在如实等于上游给的容量上限。

- **`DOUGONG_MAX_OUTPUT_TOKENS` 更名为 `DOUGONG_REQUEST_OUTPUT_RESERVE`**，
  语义随之明确：它调的是单次请求预留，不是模型容量。

- **解析期校验改判据**：从"窗口减去输出容量后为正"改成"窗口本身能容纳固定预留"。
  输出容量等于甚至大于窗口是**合法**的，此前会被误判为配置错。

**推理强度终于能发到端点上了。**

### Added

- **三种协议各自翻译推理强度**。此前能力表里有档位映射（662 个模型中带
  `reasoning_options` 的都生成了），但 `BuildBody` 不读它——用户设了档位
  **不会发出去**，静默无效果，正是我们一直在防的那类问题。

  现在按各家实际结构发：

  | 协议 / 家 | 请求体形状 |
  |---|---|
  | OpenAI 官方 | `reasoning_effort: "high"` |
  | DeepSeek / Kimi | `thinking: {type:"enabled"}` + `reasoning_effort` |
  | 智谱 GLM | `thinking: {type:"enabled", clear_thinking:false}` + `reasoning_effort` |
  | 通义千问 | `enable_thinking: true` + `reasoning_effort` |
  | OpenRouter | `reasoning: {effort: "high"}`（嵌套对象） |
  | Anthropic 预算制 | `thinking: {type:"enabled", budget_tokens: N}` |
  | Anthropic 自适应 | `thinking: {type:"adaptive"}` + `output_config:{effort}` |
  | Responses | `reasoning: {effort: "high"}` |

  **为什么必须按形状分而不是只换字段名**：同样一个 high，各家要的是
  扁平字段、开关对象、嵌套对象或绝对 token 预算——用一个字段名表达不了。
  把档位词当 `budget_tokens` 发会 400（字符串给整数字段）。

- **`AgentPropertyKeys.ReasoningEffort`**：中性键，宿主写进
  `ChatOptions.AdditionalProperties`，连接器翻译成具体 provider 参数。
  和 `Reasoning` 一样用中性名——`reasoning_effort` / `thinking.type` /
  `enable_thinking` 都是某一家或某族的写法，写进 Agent 层就把这层绑死了。

- **`WireCompat.Thinking` 枚举与 `ThinkingLevelMap`**：
  - 默认 `ThinkingFormat.None`（**不发**）。发一个对方不认的字段，
    轻则静默忽略（用户以为生效了），重则 400 且看不出是哪个参数。
  - `ThinkingLevelMap` 里显式 `null` 表示该档位不支持，此时不发——
    而不是退化成"发一个相近的"。静默降级会让用户以为选的那个档位生效了。

### 关于验证状态

**结构已验证，端点兼容未验证。** 这两件事必须分清：

- 结构错（比如把档位词当预算发）是**必然 400 的硬错误**，由
  `wire` 套件的 `reasoning-effort-shapes` 逐条钉住——覆盖 10 种形状，
  且做了反向对照（故意改坏 `clear_thinking` 的值，断言确实变红）。
- 但**没有真实密钥逐家打通**，所以不声明"已验证端点兼容"。
  规则形态取自 pi 的 `api/openai-completions.ts` 与 `api/anthropic-messages.ts`，
  那是它在生产里跑出来的写法，但对我们自己仍未实测。

### Fixed

- **`AnthropicMessagesFormat` 加 `adaptiveThinking` 参数时保留了单参数重载**。
  直接加可选参数会改变方法签名（ApiCompat `CP0002`），外部按单参数调用时
  绑定会变。加重载既扩展了能力又没破坏旧签名。

## [0.1.9-alpha.1] — 2026-10-30

**协议自动推断、上下文三层回退、收录 Gemini/Mistral。**

### Added

- **协议三级推断**：显式 `wire_api` > 端点形状 > 默认 chat-completions。
  - `https://api.minimax.io/anthropic` 这种路径末尾带 `/anthropic` 的端点
    现在能自动认出是 Anthropic Messages。此前它会被当成 chat-completions
    发出去，拿到一个含糊的 404——而用户完全看不出是自己少写了一行 `wire_api`。
  - 精确匹配主机名而不是子串：`api.anthropic.com.attacker.test` 与
    `proxy.test/api.anthropic.com/v1` 都能骗过 `Contains`。
  - **不推断 openai-responses**：只有 OpenAI 官方主机确定用它，而官方主机在预设里
    已经写死 `wire_api`。对未知端点赌 responses 会让绝大多数兼容服务直接 404。
- **上下文三层回退**：配置显式写 > 内置表查到 > `FallbackContextWindow`（262144）。
  没有第三层时的结果是"上下文未知 → 压缩永不启用"，而长会话一定撞上 provider
  上限，那时报的是 provider 的原始错误，看不出是配置缺失。
  **取中位数而不是最大值**：填大的代价更糟（压缩永不触发，直到撞真上限才失败），
  填小只是多压几次，有代价但可恢复。
  - 界面上**区分事实与估计**：查到的显示"取自内置能力表"，
    回退的显示"⚠ 内置表未收录该模型，这是估计值"。一个看起来精确的错数字比"未知"更糟。
- **收录 `google` / `mistral`**（22 家、662 个模型）。两家都有 OpenAI 兼容端点
  （实测：Gemini 的 `/v1beta/openai/chat/completions` 报"缺 Authorization"
  而非路径不存在；Mistral 的 `/v1/chat/completions` 报 401），
  只是 models.dev 的 `npm` 标的是它们的原生 SDK 包名。

### Changed

- **修正表改为带出处的两列**（`source` + `why`），收录两条上游修正：
  - `gpt-5-pro` 输出上限 128000：models.dev 报 272000，那是输入子限额的重复值。
  - `claude-opus-4-6` / `claude-sonnet-4-6` 上下文 1M：官方文档支持 1M，
    models.dev 报 200K。不修则压缩在 200K 就触发，白白浪费一半窗口。
  - **`source` 区分 `pi`（上游结论）与 `self`（自己实测）**，两者排查路径不同。
  - **`why` 必须写机理，不能只写"某某这么写"**：没有机理的修正无法判断
    上游改版后是否还成立，那才是真正该自己重测的。
  - 缺 `source` 或 `why` 时生成脚本直接报错退出，不静默跳过。

### 为什么不学 dsh 把多 provider 外包给 pi-ai

dsh 只内置官方一家，其余靠 `dsh-llm-pi-ai` 接 pi-ai 目录——它在 Node 生态里，
pi-ai 是同语言可选包。我们在 .NET 里，`Pi.Ai` 虽在 NuGet 上，但：
- 它有自己的 `Pi.Ai.Api` 抽象，与 `IChatClient` 不兼容；
- 它依赖自研的 `Pi.Telemetry`（`architecture.md` §2.3 明确不学其形状）；
- 引入它等于把 `Dougong.Ai` 三种协议实现（约 1500 行，已实测验证）作废。

**我们做的是"外包数据、不外包运行时"**：从 models.dev 生成编译期常量，零依赖。
缺的从来不是"42 家的适配代码"，是"42 家的端点与能力数据"。

## [0.1.8-alpha.1] — 2026-10-30

**预设表改为从 models.dev 生成，模型能力自动带出。**

### Added

- **`ProviderPresets.g.cs`：20 家 provider、605 个模型的能力表**，由
  `scripts/sync-provider-presets.py` 从 models.dev 生成。此前是我手写的 19 家、
  零模型数据，被指出"glm、kimi、mimo、minimax 呢"——现在这些都在，
  且每个模型带上下文窗口、输出上限、推理字段名、推理档位。
- **`dougong model` 选完模型直接带出上下文与输出上限**，写进配置文件的
  `context_window` / `max_output_tokens`。这是能力表真正的价值：
  用户不知道 MiniMax-M3 是 1M 上下文，让他手填等于把可查的事实变成一道考题；
  填错的代价不对称——填大则压缩永不触发（撞上 provider 上限才失败），
  填小则过早压缩（历史被无谓摘掉），两种都不报错。
- **`ProviderCatalog.FindModelCapabilities`** 与 **`AllPresets`**：宿主
  （桌面端 ModelPicker）可以直接拿能力表渲染模型选择器，不必自己维护。

### Changed

- **数据源从 pi.net 改为 models.dev**。核实过三件事：
  - pi 的 `catalog/` 是它的加工层，不是事实来源（它自己的 README 写着
    `sources/models-dev.ts` normalizes the models.dev catalog）。
  - 它的 `baseUrl` 有 6/25 家是变量而非字面量（`baseUrl: BASE_URL`），
    正则解析拿不全，且上游一改写法就失效。
  - 它的 `compat` 里是私有枚举（`thinkingFormat: "zai"`），语义只有它自己懂；
    照抄进我们的 `WireCompat` 是错的——我们的开关是**字段名**，不是标签。
  - models.dev 反而更全：233 家、`interleaved.field` 直接就是推理字段名、
    `reasoning_options` 直接就是档位、`env` 直接就是密钥变量名。

### 加工层的三层处理

- **机械映射**：`limit` / `api` / `env` / `interleaved` 原样搬运，零判断。
  源哈希记在生成文件头部，出问题能追到是哪天的数据。
- **实测修正**：脚本里有一个 `CORRECTIONS` 表，**目前为空**。空着是正确状态——
  models.dev 会错（pi 源码里记着 `gpt-5-pro` 的输出上限被报成 272000，
  实际 128000），但我们还没有一次真实调用能证明哪条错。填进去的每一条
  都必须是自己测出来的结论，否则就是拿别人的实测冒充自己的验证。
- **明确不抄**：`promptCache` / `maxRequestBytes` / `inputLimits` /
  `zaiToolStream`——那些服务于 pi 自己的缓存与图片卸载策略，
  我们的运行时没有对应机制，抄过来是死字段。

### Fixed

- **写进配置的 `context_window` 读不回来**。向导显示 1M 上下文、
  文件里也写了，但解析器不认识这两个键，直接报未知键——压缩因此永远不会启用，
  而界面上刚显示过它。这类"显示的与文件里的不一致"是最难自查的一类错误。

### Breaking

- **`ContextLimits.MaxOutputTokens` → `RequestOutputReserve`**、
  **`CompactionInput.MaxOutputTokens` → `RequestOutputReserve`**、
  **`ContextLimits` 三参数构造的第二个参数语义随之明确**。
  项目还没有外部调用者，所以直接改名不留弃用别名——留一个语义错误的名字
  在公开 API 上，下一个人还会踩同一个坑。
- **`ProviderConfigWriter.UpdateProvider` 增加 `contextWindow` / `maxOutputTokens` 参数**；
  **`ChatProviderConfig` 增加 `ContextWindowTokens` / `MaxOutputTokens`**；
  **`ProviderPresetData` / `ModelCapabilities` 为公开类型**。ApiCompat 已检出并更新基线。
- **`ProviderPreset.ApiKeyEnvironmentVariable` 重新有值**：此前因预设不带密钥变量名
  而为 null，现在从 models.dev 的 `env` 字段取，不再靠猜。

## [0.1.7-alpha.1] — 2026-10-30

**内置 provider 从 3 家扩到 19 家。**

### Added

- **预设表补齐国内可用厂商**。此前只有 `openai` / `anthropic` / `deepseek` 三家，
  而 `glm`、`kimi`、`mimo`、`minimax` 这些国内实际在用的根本不在列表里——
  列表的作用就是"选得到"，选不到等于没有。
  - 国内（10）：`deepseek` `glm` `kimi` `kimi-intl` `mimo` `minimax` `minimax-cn`
    `stepfun` `qwen` `doubao`
  - 国际（6）：`openai` `anthropic` `gemini` `xai` `mistral` `groq`
  - 聚合 / 本地（3）：`openrouter` · `ollama` `lmstudio`（本地，无需密钥）
  - **每条都实测核对过端点**：各端点返回 401/403/404 而非 DNS 失败，
    说明主机与路径存在。写了但连不上的条目比不写更糟——用户会以为是自己配错了。
  - `minimax` 只提供 **Anthropic Messages** 兼容端点（路径里就带 `/anthropic`），
    不是 chat-completions。协议写错会得到一个含糊的 404，很难自查，所以单独标注。
  - 本地服务（`ollama` / `lmstudio`）的 `key_env` 为 null，表示不鉴权。
- **列表行显示主机名与密钥状态**：`glm  api.z.ai  密钥✓`。此前只显示名字与模型，
  用户不知道会连到哪里、也不知道密钥缺不缺。

### Fixed

- **`presets-are-data-only` 补上覆盖度断言**：`glm` / `kimi` / `mimo` / `minimax`
  等必须存在。防止以后列表又被"精简"回三家。

## [0.1.6-alpha.1] — 2026-10-30

**修 `dougong model` 的两个实测 bug，并补齐 Hermes 式的完整流程。**

### Fixed

- **选中未配置的 provider 后直接退出**。列表把内置预设（`anthropic`、`openai`）
  与配置里已有的项一起列出，但选到"还没有 <code>[providers.x]</code> 段"的那个时，
  写回直接报错结束——用户看到的是"还没输入模型名就退出了"。
  现在会新建那一段（端点、`wire_api`、`key_env` 取自预设，模型来自用户输入）。
  **把没配过的项列进菜单却不让选，等于把选择权给了用户又收回去。**
- **用户改过的端点没写进文件**。向导显示的 `base_url` 是用户输入的，
  但新建配置时仍写预设默认值——显示的地址和文件里的不一致。
  这种错误极难自查：配置看起来正常，只是连到了错的地方。

### Added

- **交互流程补齐到 Hermes 那条链路**：选 provider → 确认端点 → 确认密钥 → 选模型。
  - **端点**：显示默认值，方括号内回车接受。不说端点直接问模型名，
    用户不知道会连到哪里，而端点填错是最难自己发现的错。
  - **密钥**：说清需要哪个变量、当前有没有、怎么设；未设置时可选择"先跳过并继续"。
    **不在流程里收密钥值**——命令行键入的密钥会进 shell 历史，而进程也没法
    替父 shell 设环境变量，收下来只能写配置文件，那正好破坏"密钥不落盘"这条红线。
  - **模型**：先尝试从端点拉 `/v1/models` 列出可选项，拉不到再手填。
    手打模型名最容易错（`deepseek-chat` → `deepseek-chart`），而错误要到
    第一次对话才以 404 出现，看不出是拼写问题。拉取失败不算错误（很多端点
    不实现该接口），退回手填即可；输入不在列表里的名字会二次确认，
    否则拼错一个字母就成了一个"合法"的坏配置。

### Breaking

- **`ProviderConfigWriter.UpdateProvider` 增加可选参数 `baseUrl`**。
  签名变化改变方法绑定，ApiCompat 会报 `CP0002`；README 里说明了这是
  交互式端点覆盖所需。旧的 4 参数调用在源码层面仍能编译。

## [0.1.5-alpha.1] — 2026-10-30

### Added

- **`dougong model`：交互式选择 provider 与模型，写回配置文件**。
  列可选 provider（含密钥是否已设置），选一项、填模型名，然后写回
  `~/.dougong/config.toml`。也支持非交互形式：
  `dougong model --list`、`dougong model --set <名字> --model <模型> [--default]`。
  - **与 REPL 里的 `/model` 是两个东西**：那个只换本次会话的连接器，退出即失效；
    这个改配置文件，是持久决定。合成一个入口用户就分不清"刚才改的存没存"。
  - **写回保留注释**：配置文件是手写的，注释往往记着"为什么这么配"。
    所以走语法树改节点而不是整体重新序列化——后者会把注释、空行、字段顺序
    全丢掉。有对照验证（`model-command-preserves-comments`）逐条断言注释与空行数不变。
  - **配置有语法错时拒绝写回**，一个字节都不改。原文件坏了还去重写，
    会把错误固化下来，用户再也分不清是自己写错还是工具改坏。
  - 新建配置时从内置预设取端点，用户只需选 provider、填模型名。
    缺密钥当场提示（`export XXX='...'`），不等下次对话 401 才发现。
  - **不自动探测可用模型**：那要调 `/v1/models`，需要密钥与网络，各家返回形状还不统一。
    配模型是低频动作，一次真实的 404 比一个可能过时的列表更有用。

## [0.1.4-alpha.1] — 2026-10-30

**只改文档，无代码变更。**

### Changed

- **补上升级路径，修掉一个会踩到的坑**。此前文档只写了安装命令
  （带 `--version`），没有写升级；而 `dotnet tool update` 不带
  `--prerelease` 会报「找不到 dougong.cli」——这个报错是误导，包其实在
  nuget.org 上。真实原因是所有版本都带 `-alpha.N` 预发布标签，
  `dotnet tool update` 默认只在**稳定版**里挑；稳定版列表为空，于是说找不到。
  现在 `getting-started.md`、`README.md`、`README.en.md` 都写清了这一点，
  并注明**不要中途改成 `preview`**（SemVer 里 `preview` > `alpha`，
  改了之后所有后续 `alpha` 都会被判成降级、被工具拒绝）。
- **`troubleshooting.md` 新增 §3.2「找不到 dougong.cli」**，
  并重写 §3.1：原文还写着「缺少三个环境变量」，那是多 provider 之前的报错，
  已过期。同时补了一张配置报错对照表（`key_env` 写成密钥值、未知键、
  非法地址、`default_provider` 指向不存在的段、预设未填模型）。
- **自检套件数从 45 更正为 46**（新增的 `provider` 套件）。

### 为什么升版本号而不是复用在 0.1.3-alpha.1

`0.1.3-alpha.1` 已经发布到 nuget.org，包内容与本仓库 `374e766` 的构建产物
**哈希完全一致**。版本号一经发布就不能重用——继续在它下面改并打包，
推送会被 nuget.org 拒绝。`version-not-already-published` 这条自检就是拦这个的，
本次回归里它确实亮了，是正确的拦截。

## [0.1.3-alpha.1] — 2026-10-30

本版做两件事：把三处「配置错要到运行时才炸」的静默失效改成装配期失败，
并把压缩预算的判据收敛到单一来源；同时引入**多 provider 配置**
（配置文件为主、环境变量覆盖），密钥仍然只进环境变量。

### Added

- **多 provider：`ProviderCatalog` + `ProviderConfigFile` + `config.toml`**。
  ```toml
  default_provider = "deepseek"

  [providers.deepseek]
  base_url = "https://api.deepseek.com"
  wire_api  = "openai"
  key_env   = "DEEPSEEK_API_KEY"   # 变量名，不是值
  model     = "deepseek-chat"
  ```
  - **密钥策略不变，但配置文件终于可以存在了**。此前「密钥只从环境变量读」
    被实现成「没有任何配置文件」——两件事被绑在一起。现在配置里只放
    `key_env`（变量名），密钥值仍只从环境变量取，于是配置文件可以进 git、
    进备份、进日志。`key_env` 若写成密钥值会被**硬拒**并提示改写变量名；
    配套验证用唯一标记串跑完整 `--doctor`，断言输出与配置文件都不含它。
  - **环境变量 > 配置文件**，覆盖粒度是整个 provider（不是单个字段）。
    覆盖时配置文件里的其它 provider 保留，`/model` 列表不会凭空少一批。
  - **`DOUGONG_PROVIDER_CONFIG`** 可整体改配置路径。`GetFolderPath(UserProfile)`
    走操作系统 Known Folder API 且**不读 `USERPROFILE`**，没有这个开关就无法
    在不碰真实用户目录的前提下验证配置。
  - 内置预设 `deepseek` / `openai` / `anthropic`：**只有** `base_url`、
    `wire_api`、`key_env` 三个字段，**没有模型名**（同一端点的可用模型会变，
    写死会让用户拿到 404）。边界判据见 `goal.md` §3「反目标的边界」——
    这是端点地址表，不是 provider 矩阵。

- **`/model`：会话内切换 provider**。列全部 provider（含密钥是否已设置，
  **绝不显示值**），`/model <名字>` 切换。切换换的是连接器
  （`SwappableChatClient`）而不是重建运行时——重建会把内核租约、MCP 连接
  与排空状态一起拆掉。切换前同时检查模型名与密钥，**两条一起报**，
  避免用户补一条试一轮。只在本次会话有效，退出后回到 `default_provider`。

- **`ProviderCatalog.ProviderPreset` / `ChatClientFactory.TryResolve`**：
  配置解析的公开入口，宿主（桌面端等）可直接复用，不必各写一遍。

- **`ContextBudgetValidation.Check`：压缩预算校验的可展示结论**。
  返回 `ContextBudgetVerdict(IsUsable, IsEnabled, Detail, Hint)`，不抛异常。
  - **存在的理由是两个宿主各写一遍会漂移**。实测到的活样本：桌面端为了
    「配置错要在启动时炸」自己写了 `Validate()`，并在源码里硬编码了
    `const int overhead = 512;`——SDK 改默认值时那个副本**不会报错**，
    只会静默算出不同的预算。
  - 与 `ContextLimits.Validate()` 的分工：那个用于装配期硬失败（抛），
    这个用于诊断展示（返回结论）。**判据共享、控制流不同**，不是两份实现。
  - 窗口为 0 时返回 `IsUsable: true, IsEnabled: false`——
    「没配窗口 = 不压缩」是合法状态，不是错误。

- **`SlidingWindowCompactor` 的四个默认值改成公开常量**
  （`DefaultTriggerRatio` / `DefaultRetainRatio` / `DefaultPreservedHead` /
  `DefaultPreservedTail`），并暴露实例的 `TriggerRatio` / `RetainRatio`。
  宿主展示"当前策略"时只能读这里；此前它们只存在于参数表字面量里，
  宿主想看只能自己抄一份，而抄出来的副本不会随 SDK 改。

- **`DOUGONG_COMPACT_TRIGGER_RATIO` / `DOUGONG_COMPACT_RETAIN_RATIO`**：
  压缩比例的覆盖入口。此前的两个比例**无调用点、无环境变量**，
  用户被隐式默认（0.75 / 0.16）支配，既看不到也改不了。
  `/status`、首页与 `--doctor` 现在都报出当前策略。

- **`TokenBudget.DefaultOverheadTokens`**：预留量的单一常量。
  此前 `CompactionInput` 的参数默认值与 `ContextLimits.OverheadTokens`
  各写了一个字面量 `512`，加上宿主抄的那一份共三处副本。

- **`--doctor` 新增「上下文压缩」检查**：窗口装不下输出时报失败并给出
  可操作的数字（「把窗口调到大于 4660」），窗口值非正整数时也报失败
  而不是静默当成「未配置」——静默回退到不压缩最危险，用户以为开了。

### Changed

- **`--doctor` 的「provider 配置」改走 `ChatClientFactory.TryResolve`**，
  与 REPL / ACP 同一路径。此前各处各读一遍环境变量，自检说「没问题」
  而实际启动失败是必然会发生的事。现在报出：当前 provider、模型、协议、
  端点、密钥变量是否已设置、还有几个可选、来源（配置文件或环境变量）。
- **`/status` 增加压缩策略一行**（触发 / 保留比例），仅在启用压缩时显示。
- **ACP 模式也接受配置文件**，与 REPL 同一解析路径——两个入口的 provider
  来源必须一致，否则会出现「REPL 能连、ACP 连不上」这类只在协议形态上
  暴露的怪事。

### Breaking

- **`DOUGONG_PROVIDER_*` 环境变量不再是唯一入口**，但仍是最高优先级；
  只设环境变量的现有用法**行为不变**。此条列出是因为文档与报错文案都变了：
  缺配置时的提示从「请设置三个环境变量」改为「写配置文件，或设环境变量」。
- **`Dougong.Ai` 新增依赖 `Tomlyn 2.10.1`**（内核仍零第三方依赖）。
  不手写解析器的理由：转义、多行字符串、数组、内联表都容易写出解析偏差，
  而偏差表现为「读到的值和文件里写的不一样」，排查成本远高于一个依赖。

- **`ContextLimits` 增加三参数构造函数，构造时即校验**。
  `new ContextLimits(4_000, 4_096)` 现在会在构造处抛
  `ArgumentOutOfRangeException`，而不是等第一次对话。
  - 带 `[SetsRequiredMembers]`：它让编译器**不再检查调用方**是否设置了
    required 成员。将来加第五个 required 成员却忘了在构造函数里赋值，
    编译器不会报错。这个代价已写进注释。
  - `with` 表达式与无参构造（对象初始化器写法）均不受影响，已实测。

- **`LoopAgentRuntime` 在装配时校验预算**（`limits?.Validate()`）。
  对象初始化器写法绕过了构造校验，这条路必须在连接运行时补上，
  否则配置错误仍会拖到第一轮对话才爆——那正是要修的场景。

### 为什么不改「不传 = 不压缩」的默认值

`LoopAgentRuntime` 的 `limits`/`compactor`/`summarize` 默认都是 `null`，
`null` 就是关闭压缩。曾考虑把默认改成「自动压缩」，但：

1. 这个语义**被测试钉住**——`CompactionWiringChecks.cs` 明确断言
   「未配置预算时必须保持旧行为：完整历史送入模型（向后兼容契约）」
2. SDK **猜不出模型窗口**。没有窗口就算不出阈值，猜一个必然错一半，
   而错的两个方向代价不对称（填小过早压缩，填大永不触发）

所以本版只硬化「配置不合法要早炸」，不动「没配置就不启用」。

### 为什么现在才让配置文件存在

此前 README 与 `getting-started.md` 都写着「任何文件都不会保存密钥」，
而实际实现是「**没有任何配置文件**」。这两条被绑在一起了，但它们是两件事：

- **密钥不能落盘** —— 这条不可让渡，且现在仍然成立
- **端点、模型、协议不能落盘** —— 这条从来没有理由

把端点信息也逼进环境变量，代价是多 provider 根本无从表达：
`FromEnvironment()` 只能返回一个配置。Codex 的 `env_key`、dsh 的
`CredentialRef`、Hermes 的 `key_env` 三家用的是同一个解法——
**配置里放变量名，值仍从环境取**。我们照做，于是那个"刻意"的理由
（配置文件会进 git、进备份、进日志）不再成立：进 git 的只有变量名。

这条边界的判据也顺带写进了 `goal.md` §3：端点地址表不是 provider 矩阵。

## [0.1.2-alpha.2] — 2026-10-30

**只改文档，无代码变更。**

### Changed

- `architecture.md` §2.3 加一条指向：将来若要公开宿主协议，抽的是
  「会话协议包」而不是「分帧包」。桌面端已有的 `DougongDesktop.Ipc`
  比 `Pi.Protocol` 更贴业务（有会话/条目/审批这些概念，不止是字节怎么切），
  其成熟度评估与抽包触发条件记在 Desktop 仓库的
  `docs/ipc-protocol-readiness.md`。

## [0.1.2-alpha.1] — 2026-10-30

> ⚠️ **本版含破坏性 API 变更**（见下方 Breaking）。

### Added

- **`UsageFailureKind.ContextOverflow`：溢出终于有独立分类**。
  上一版加了溢出恢复，但账目里没有对应类别——溢出的 400 落到状态码兜底
  分支的 `Unknown`，宿主看不出"这个错误值得重试"，只会当成永久失败。
  而它恰恰是唯一能靠压缩自动恢复的一类。
  - **判定必须排在状态码之前**：超限与参数不合法都返回 400，状态码分不开。
    若让状态码先判，溢出永远到不了文案匹配那一步，前面的识别和恢复全部白做
  - 判据双保险：专用异常类型 + 异常链上的响应体文案
    （第三方连接器只给 `HttpRequestException` 时靠文案认出）
  - 普通 400（参数错误）仍归 `Unknown`，不能因为放宽匹配把参数错误当成溢出
    ——那会白压一次历史，有损且不可撤销

- **上下文溢出恢复**。此前 provider 判定"上下文太长"时抛一个通用
  `HttpRequestException`，整轮失败——而这是最该自动恢复的一类失败。

  **识别**（`ContextOverflowDetector`，三层判据）：
  - 错误文案：覆盖 Anthropic / OpenAI / Bedrock / Groq / llama.cpp /
    MiniMax / Kimi / Mistral / Ollama / 通义 / 中文厂商等，
    来源是 pi.net 的 `Overflow.cs` 实际文案表，不是猜的
  - **裸措辞一律不匹配**（照 dsh 的原则）：`too many tokens`、
    `token limit exceeded`、`exceeds the limit of N` 这类宾语不明的写法
    被删掉或收紧为"必须提到 context"。dsh 的注释原话：
    *"'Exceeds' wording is safe only when its object is explicitly the model context."*
    代价是可能漏判某些简写报错；收益是**不会被无关错误触发压缩**——
    压缩有损且不可撤销。
  - **排除清单**：Bedrock 的 `ThrottlingException` 含 "too many tokens"，
    会被正向模式命中——不排除就会因一次限流**白压一次历史**（有损且不可撤销）
  - **静默溢出**：调用"成功"但 `usage.input` 已超过配置窗口，
    说明我们对窗口的认知是错的；不处理则下一轮必然失败
  - **截断溢出**：`stop_reason=length` + 输出 0 + 输入 ≥99% 窗口
    （小米 MiMo 一类）。判据里的 99% 是必要的，否则"模型主动不回答"
    也会被当成溢出

  **恢复**（`LoopAgentRuntime`）：
  - 强制压缩一次（`Plan(force: true)`）→ 验证历史**真的变小** → 重发同一请求
  - **必须验证有缩减**才重试：压完一样大就重发，只会得到同一个错误
  - **只试一次**：再溢出就失败，不无限循环
  - 耗尽时报**原始 provider 错误**，不报"压缩失败"——
    压缩是我们尝试的手段，超限才是原因

### Changed

- **尾部保留量改为「条数下限」与「窗口比例」的双重约束**，
  不再固定 6 条。原因是固定条数在两种极端下都不对：
  - 6 条短对话约 300 token——保住了但没意义
  - 6 条含工具输出的消息可能两万 token——保留太多，压完等于没压，
    很快又触发（**这是"长会话反复压缩"的一个可能原因**）
  现在取 `max(6 条, 有效预算 × 0.16)`，并按 dsh 的 `retainRatio` 取值。
  - **必须留至少 1 条可压**：否则比例算出的保留量会吃掉整个历史，
    小窗口用户从"压得太狠"变成"完全压不动"——那是倒退
  - **`retainRatio` 必须小于 `triggerRatio`**，构造时校验。
    否则压缩后仍越过阈值、每次调用都压一次，表现是会话越来越慢、
    账单越来越高，且没有任何错误提示

- `embedding-guide.md` 的示例窗口从 128K 改为 1M，并注明「填你所用模型的
  实际窗口，不要照抄」。原数字是写文档时随手填的，不是实测值。

> ⚠️ **本版含破坏性 API 变更**（`IContextCompactor.Plan` 增加参数）。
> 完善阶段尚未有外部使用者，直接改而不走弃用窗口
> （见 [compat-policy.md](docs/compat-policy.md) §6.1）。

### Breaking

- **`UsageFailureKind` 插入 `ContextOverflow`，后续成员的值顺移**。
  这是刻意的：该枚举只用于内存比较与指标标签（标签走 `ToString()`
  而非数字），不落盘、不跨进程，因此调整顺序没有兼容风险。
  若将来要序列化它，必须先固定所有值再发布。

- **`SlidingWindowCompactor` 构造函数增加 `retainRatio` 参数**（默认 0.16）。
  记录类的主构造签名因此改变，已编译的调用方需要重新编译。
  只传 `(preservedHead, preservedTail, triggerRatio)` 的源码级调用仍可编译。

- **`IContextCompactor.Plan` 增加 `bool force = false` 参数**。
  接口加参数会让**现有实现全部编译失败**（这是 ApiCompat 的 CP0006 抓到的）。
  自定义压缩器需要补这个参数；不需要强制压缩时忽略即可。
  - 为什么必须加：provider 判定溢出时，说明**窗口配置不可信**
    （填大了或实际上限更小）。沿用那个窗口算触发阈值会得出"不需要压"，
    恢复逻辑就永远压不了东西——实测踩到过：窗口配 200 万、历史 2 万，
    provider 报溢出，`Plan` 返回 null，恢复空转。

### Fixed

- `HttpClientChatClient` 的流式路径现在把 `StopReason` 带出来
  （`ChatStreamDecoder.StopReason`），供截断溢出判定使用。

- **流式调用中途停止时账目漏记**。成功记录原先写在 `while` 循环之后，
  而调用方 `break` 会让枚举器直接 Dispose、循环退出——那一行永远不执行，
  这次调用在账目里**完全消失**。漏记比记错更隐蔽：总数只是偏低，
  没有任何异常迹象。改用 `finally` + `recorded` 标志覆盖全部退出路径
  （正常结束、提前 break、抛异常），且不重复记账。

- 复检确认的另一处：**非托管 dll 不会中断整批扫描**（`BadImageFormatException`
  被归成 `Failed`，符合"读不出来"而非"不是插件"的语义）。没有缺陷，
  补了测试钉住。

## [0.1.1-alpha.3] — 2026-10-29

> **注**：本版的包内容包含两个在"release 提交"之后才完成的修复
> （`InvalidResponse` 接线、畸形响应报错）——包是从最终代码打的，
> 而 CHANGELOG 写成时那两个修复还没进。已核验：已发布的 alpha.3 里
> 确实有 `InvalidWireResponseException`。下不为例：发版提交应当就是
> 最终代码状态。

> ⚠️ **本版含破坏性 API 变更**（相对 `0.1.1-alpha.2`）。
> 完善阶段尚未有外部使用者，改动成本为零，因此直接改而不走弃用窗口
> （见 [compat-policy.md](docs/compat-policy.md) §6.1）。
> **基于旧版编译过的程序集需要重新编译**，源码级调用不用改。

### Breaking

- **`UsageRecord` 新增 `FailureKind` 参数**（末尾可选）。
  同样是"record 加参数 = 构造签名变化"，已编译的调用方需要重新编译。

- **`PluginManifest` 的手写构造函数新增 `display` 参数**（末尾可选）。
  这里要更正一个容易想错的地方：**手写构造函数 + 可选参数 ≠ 二进制兼容**。
  `PluginManifest` 虽是手写构造，但它是 record，构造签名变了之后
  已编译的调用方同样会 `MissingMethodException`。ApiCompat 报 `CP0002`。
  源码级调用不用改，重新编译即可。
  （`PluginSummary` 改用 `init` 属性，就是为了避开这个问题。）

### Added

- **`PluginLoadStatus.AlreadyLoaded`：已存在的插件不再混进 `Failed`**。
  重复加载同一插件时，原来只在 `Reason` 里写"插件 'x' 已登记"，
  UI 要靠字符串匹配才能区分——SDK 改一下措辞就失效。
  - 两种情况的用户动作相反：已存在不用做任何事（也许只是想刷新列表），
    真失败要用户排查。混在一个状态里只能靠猜。
  - 这与 `Ambiguous` 是同一条思路：状态按"用户该做什么"分，
    不按"内部发生了什么"分。
  - `AlreadyLoaded` 带上 `PluginId`，UI 用它关联列表里已有的那一行；
    `IsUserVisible` 返回 false。
  - **新增公开异常 `PluginAlreadyRegisteredException`**：判断依据是异常类型
    而不是消息文本，消息改了也不会静默失效。

- **LLM 调用失败现在有分类**：`UsageRecord.FailureKind`（`UsageFailureKind` 枚举）。
  此前只有 `Succeeded: true/false`，"今天失败率 30%"无法回答是 key 过期、
  还是 provider 挂了、还是我们代码出错——三种处理方式完全不同。
  - 类别：`Unauthorized`（401/403）、`QuotaExceeded`（402）、`RateLimited`（429）、
    `NotFound`（404）、`ServerError`（5xx）、`NetworkError`、`Timeout`、
    `Cancelled`、`InvalidResponse`、`Unknown`
  - **`Cancelled` 与 `Timeout` 必须分开**：两者都是 `OperationCanceledException`，
    区别只在"调用方令牌有没有触发"。混在一起会让用户按 Ctrl+C 污染故障率。
    `UsageFailureClassifier.CountsAsFault` 明确排除 `Cancelled`。
  - **判不出来的归 `Unknown`，不挑一个最像的凑数**：错的分类比没有分类更糟，
    会把排查引向错误方向。
  - `failure_kind` 作为低基数标签进入指标（11 个值）；
    具体错误消息是高基数的，绝不进标签。
  - **范围只含 LLM 调用**：工具审批拒绝、schema 校验失败发生在调用模型之外，
    不计 token，由 `ToolDispatchResult.Outcome` 表达。

- **`HttpClientChatClient` 把 HTTP 状态码放进 `HttpRequestException.StatusCode`**。
  此前只写进消息文本，要靠正则解析才能取到——上游改措辞就会静默退化成
  `Unknown`。这是修一个已有缺陷：结构化数据本来该有，只是没设。

- **`LoadFromDirectoryAsync`：目录级批量加载**。一次扫描目录下的
  `*.dll` 并逐个加载其中的插件，返回每个程序集一条 `PluginLoadResult`。
  真实插件包通常是 `Plugin.dll` + `Helpers.dll` + `Contracts.dll`，
  而 `LoadAsync` 一次只处理一个 dll、一个 `IPlugin` 类型，宿主无从知道
  "这个目录里有哪些插件要装"。
  - 只扫 `*.dll`，**不递归子目录**（子目录是依赖区）
  - 无 `IPlugin` 实现的 → `Skipped`（依赖库，不是失败）
  - 一个 dll 内有多个实现 → `Ambiguous` 并列出候选，**不擅自挑一个**
  - 单个失败不抛异常，整批结果照常返回——"9 成功 1 失败"要如实报成 9+1
  - **不做 NuGet 下载**：那是分发策略不是内核能力，塞进 SDK 会引入
    HTTP + 包解析依赖以及版本冲突、私有源、认证一整套问题

- **`PluginDisplayInfo`：插件的展示元信息**（`Title` / `Description` / `Author`，
  全部可选）。载体是 `PluginManifest` 而非目录旁的元数据文件——两个数据源
  会不一致，manifest 来自插件本身才是唯一权威。
  - 图标刻意不进 manifest：那是 UI 资源，SDK 一旦支持路径引用就要定义
    目录布局、体积上限、越界与符号链接校验，属于宿主的产品策略
  - 不做多语言：本项目还没有外部插件作者，先冻结语言协商与回退链
    正是 host-layering-decision §4.4 反对的"预先冻结格式"

- **`PluginSummary.Display`**（`init` 属性）。列表页因此能显示标题而不是只显示
  `sess-xxx` 风格的 Id。只带字符串值、不带 `PluginManifest`——
  manifest 里的 `ServiceRequirement.Type` 对动态插件而言是插件 ALC 的类型，
  长期保留会阻止程序集回收。

## [0.1.1-alpha.2] — 2026-10-29

> **这是 nuget.org 上的已发布版本，内容冻结。**
> 之后的改动进入 `0.1.1-alpha.3`——版本号不能重用，已发布的内容不可修改。

### Breaking

- **`PluginStateChange` 与 `ToolDispatchResult` 的构造函数新增参数**：
  - `PluginStateChange(..., PluginDiagnostic? Diagnostic = null, TimeSpan? Elapsed = null)`
  - `ToolDispatchResult(..., ToolPermission? Permission = null,
    TimeSpan? ApprovalElapsed = null, TimeSpan? ExecutionElapsed = null)`
  **源码级调用不用改**（新参数有默认值），但**已编译的程序集会 `MissingMethodException`**
  ——record 加参数会替换原构造函数与 `Deconstruct` 签名。
  这是 ApiCompat 报 `CP0002` 抓到的，不是我预判到的：我原以为"可选参数=兼容"。
  重新编译即可，无需改代码。

### Added

- **工具派发耗时分成两段**：`ToolDispatchResult.ApprovalElapsed`（等审批）与
  `ExecutionElapsed`（工具执行）。合成一个总耗时无法归因——前者取决于人多快确认，
  后者取决于工具本身多慢，优化方向相反。
  各段在"没有发生"时为 `null` 而不是 `0`：没配审批器与"审批瞬间通过"是两件事，
  用 0 冒充会让统计里混进假的"审批很快"。

- **插件激活耗时**：`PluginStateChange.Elapsed`，只填在 `Activating → Active` 上，
  其余迁移为 `null`。挂在状态迁移上而不是单独发事件，因为耗时天然属于某一次迁移；
  拆成两条消息会要求订阅方自己配对，并发下容易配错且没有迹象。

- CLI 工具行显示两段耗时（`审批 1.2s / 执行 45ms`）。毫秒级单独格式化，
  不复用秒级的 `FormatDuration`——那会把 45ms 显示成 `0s`。

### Fixed

- **畸形响应不再静默记成成功**。HTTP 200 之后如果响应体解析不出任何内容，
  `HttpClientChatClient` 原先静默结束、产出零个 update，账目记成成功——
  用户看到"成功但没有任何内容"，无从判断是模型没说话还是协议出了问题。
  这也让 `UsageFailureKind.InvalidResponse` 成了一个**死枚举**：
  枚举里有位置，但没有路径能触发它。
  - 现在分两种情况报错：一条 SSE 负载都没看到（网关错误页、代理改写、
    端点指向非流式接口）→ 抛 `InvalidWireResponseException`；
    看到了负载但解析不出内容（协议实现与 provider 输出不符）→ 同样抛
  - **有终结信号时允许零内容**：模型确实可以什么都不说，那不是缺陷
  - 新增公开异常 `InvalidWireResponseException`（继承 `InvalidOperationException`）：
    用类型而不是消息文本判断，消息改了也不会静默失效

- **`scripts/update-api-baseline.sh` 一直在复制最老的包**。它用
  `ls *.nupkg | head -1` 选文件，拿到的是字典序第一个（`0.1.0-alpha.1`），
  于是"更新基线"每次都在复制旧包，基线永远停在最早版本——
  ApiCompat 一直在拿最老的包比对，真实变更淹没在累积的旧差异里。
  改成从 `Directory.Build.props` 读当前版本，找不到就报错退出。
  实测：修复前六个基线包版本都是 `0.1.0-alpha.1`，修复后同步到当前版本。

## [0.1.1-alpha.1] — 2026-10-29

> **版本号为什么从 `0.1.0-alpha.2` 跳到 `0.1.1-alpha.1`**：本版包含**新增公开 API**
> （`AgentEvent.ReasoningDelta`、`AgentPhase`、`AgentPhaseTracker`）。
> 按 [compat-policy.md](docs/compat-policy.md) 的规则，新增功能属 minor 变更，
> 不留在 `0.1.0` 的 alpha 序列里。

### Fixed

- **CLI 终端渲染两处真机缺陷**（`0.1.0-alpha.2` 存在，需升级修复）：
  - **`ESC[s`/`ESC[u` 存不住光标位置**。底部活动区靠"保存光标 → 往下画 →
    恢复光标"避免覆盖正文，而这两个序列是 SCO 的私有扩展，真实终端与
    pyte 都不保证生效。用 pyte 实测：`ESC[s … ESC[u` 之后光标停在错误位置，
    `ESC 7 … ESC 8`（DECSC/DECRC）才正确。光标没回去就是屏幕上两行字叠在一起。
  - **补白写满最后一列触发自动折行**。活动行被补白到正好等于终端宽度，
    写满最右列时终端自动换行、光标跳到下一行，之后所有定位偏移——
    表现为活动行只剩一个 spinner、文字跑到别处。现在宽度上限改为 `width - 1`。
  - 另修 `Clear()` 只擦屏幕不清内容（导致陈旧状态栏在回合结束后被重画）、
    `stderr` 绕过底部区域保护（`[结束：Failed]` 直接打在活动行上）。

### Added

- **新增 `AgentEvent.ReasoningDelta`：推理内容现在有实时事件**。
  此前 `LoopAgentRuntime` 只把 `AgentPropertyKeys.Reasoning` 追加进历史，
  **从不构造事件**——宿主拿不到实时推理流，UI 只能在会话恢复时补显示。
  这是一处如实记录过的能力缺口，且**插件补不了**：插件只能订阅已是
  `AgentEvent` 的东西，而这份数据在运行时就已被吞掉。
  - 与 `TextDelta` 分离：推理不是回答，且常带未定稿的自我纠错
  - 两条通道并存：事件给 UI，历史保证会话恢复后推理仍完整
  - 是否出现取决于 provider；有对偶检查钉住"没有推理时不得产出空事件"
  - ACP 侧投递为 `agent_thought_chunk`（协议标准消息类型，此前一直是空的）
  - CLI 默认不显示（Plain 档须逐字一致），`DOUGONG_SHOW_REASONING=1` 打开

- **新增公开类型 `AgentPhase` 与 `AgentPhaseTracker`（Agent 层）**。
  阶段推导从 CLI 提升为 SDK 契约：自己做宿主的用户不该各写一套。
  - `AgentPhase` 七个值，其中 `Reasoning` 与 `Thinking` 分开——
    "什么都还没来"与"模型确实在动"，长时间无输出时感受完全不同
  - `AgentPhaseTracker` 提供事件 + 计时的状态推导（含去抖与失败停留），
    纯逻辑、无 UI 依赖、不加锁（并发策略交给调用方）
  - **不调 `Tick` 的后果不是"不刷新"，而是阶段永远停在最后一个事件上**
  - CLI 的 `ActivityPhase` 改为它的别名，`ActivityState` 内部委托，
    不再保留第二份实现

## [0.1.0-alpha.2] — 2026-10-29

### Added

- **品牌标志全面替换为"合构"黑白质感版**。旧方案（阶梯环：玄黑底 + 米白线）
  已整体归档到 `docs/brand-archive/stepped-ring/`。
  新素材包含主标志（纯黑/纯白）、横纵组合稿（字标已转路径，不依赖机器字体）、
  九个尺寸的应用图标与多帧 ICO。**质感只用于应用图标**，主标志仍是纯黑白。
  README 顶部图同步替换。
  新增 `-- brand` 自检套件 6 条：尺寸齐全、正方形且带 alpha、ICO 帧与文档一致、
  主标志文件存在、README 提到的路径真实存在、`assets/logo.png` 与当前图标逐字节相同。
  最后一条防的正是"换了素材但 README 顶部还是旧图"——那是最容易漏的一处。

- **新包 `Dougong.Hosting`**：把内核接线从 `Dougong.Cli` 抽出来，
  CLI 与 Desktop 共用。包含 `KernelAgentHost`（注册 4 个插件进内核、
  按依赖激活、异常时清理、把服务引用适配成接口、订阅失败诊断）与
  `KernelAgentRuntime`（把 `IAgentRuntime` 包成内核服务，租约粒度为一回合）。
  **它不是产品层**：不含工具集、提示词、`DOUGONG_*` 解析。
  抽出的理由是这段代码抄错会**静默出错**（注册顺序、accessor vs 裸实例、
  异常清理），而不是分层更好看——理由与边界见
  [host-layering-decision.md](docs/host-layering-decision.md) §4.5。
  ApiCompat 已覆盖（基线 `artifacts/baseline/Dougong.Hosting.nupkg`）。

- **底部活动行与状态栏**（CLI，真终端下）：等待模型响应时显示当前阶段、
  已耗时、模型名、上下文占用率与本轮耗时。**没有任何"等待模型"的 AgentEvent**，
  所以它由事件加计时混合驱动——指令发出瞬间亮起，不用等第一个字。
  - 输出被重定向时**完全不启用**（实测 `Console.SetCursorPosition` 抛
    `IOException`，且写控制序列会污染全部自检套件的断言）
  - 正文流式输出期间整块收起，不与正文抢光标
  - `DOUGONG_TUI=0` 可关闭
  - 不做全屏（`AlternateScreen`）：历史仍归终端自己的 scrollback 管理

- **`/status` 命令**：显示本次会话的完整配置——会话路径、工作区、shell 开关、
  压缩阈值、L1 记忆、MCP server 列表、终端档位、底部活动区是否生效。
  首页为保持简洁折叠掉的信息都在这里。所有值**读实际状态**而不是重算环境变量。

- **终端宽度按显示宽度计算**：中文在等宽终端占两格，此前按 `String.Length`
  算会让补白溢出，整行折行后重绘时上一帧擦不干净（状态栏越用越乱）。
  现在 CJK 与全角符号按 2 格计，活动行与状态栏在所有宽度下都不超出终端。

### Fixed

- **CLI 输出改为固定 UTF-8，不再跟随控制台代码页**。中文 Windows 默认代码页是
  936（GBK），此前 `--help`、`/status` 等输出里的汉字全是 GBK 字节：
  直接调用 `.exe` 时，任何按 UTF-8 读取的调用方（CI、脚本、父进程日志）
  拿到的都是乱码；而经 `dotnet run` 启动却正常，因为后者会设控制台编码。
  这种"换启动方式就变"的行为最难排查，现固定在 `Main` 开头设置，
  且验证过不影响 Plain 档的零 ANSI 序列保证。

- **未知命令行参数不再被静默忽略**。此前 `dougong --docter`（拼错 `--doctor`）
  会忽略该参数并**直接进入交互对话**，同时创建会话文件——用户以为在跑自检，
  实际开了个会话。现在报 `未知参数：--docter` 并退出 2，且这条检查排在
  provider 配置检查之前（缺配置 + 参数拼错时报的是参数错，那才是根因）。
  带值形式 `--resume x` 与 `--resume=x` 均不会被误判。

### Changed

- `Dougong.Cli` 不再自己定义 `KernelAgentHost` / `KernelAgentRuntime`，
  改为引用 `Dougong.Hosting`。**CLI 的命令行行为与开关完全不变。**
- 架构分层表新增一层：`Kernel(0) ← Agent(1) ← Hosting(2) ← {Ai,Mcp,Acp}(3) ← Cli(4)`。
  `Dougong.Hosting` **不得**引用 Ai/Mcp/Acp——同层互引被架构测试禁止。

- `Dougong.Cli`：`--help`（`-h`）。**排在 provider 配置检查之前**——
  刚装完工具的人第一件事就是敲它，那时还没配 provider 密钥；
  若先报"缺少 provider 配置"，用户连有哪些命令都不知道。
  帮助里列出全部命令、三个必需环境变量、可选开关与对话内命令。
- `dotnet tool` 安装链路：包 `Dougong.Cli`（命令名 `dougong`）已实测可装可卸，
  自带全部依赖（6 个 Dougong 程序集 + 12 个第三方），
  装完能在**项目目录之外**运行。
- `spike` 的 `-- load-plugin <dll>`：加载任意插件程序集并报告结果，
  弥补指南 §6 的断点——读者能编译出 dll，却没有办法确认内核认不认它。
  走的是生产路径 `DougongKernel.LoadAsync`，不是另写一套验证逻辑；
  会验证卸载（只会加载不会卸载的插件会泄漏），失败时列出最常见的三类配置错误。
- `Dougong.Cli`：`--report` 可复现信息包。输出产品版本与 commit（取自程序集
  `InformationalVersion`，非硬编码）、平台、provider 配置形态、最近会话的**元信息**
  （文件名/大小/记录数，不读内容）、其它开关状态。
  **硬约束：绝不含密钥与用户内容**——这份文本的用途就是被贴到 issue，
  一旦带密钥就撤不回来。密钥只报"是否设置 / 长度 / 是否含首尾空白"。
  同样排在 provider 配置检查之前，缺配置时也能生成。
- `Dougong.Cli`：`--doctor` 启动自检。逐项检查运行时、provider 配置（含连通性）、
  工作区可写、会话目录可写、L1 记忆开关，每项失败都给出**下一步做什么**；
  退出码 0 = 可启动，1 = 有失败项。**它排在 provider 配置检查之前**——
  否则缺配置时会先退出，自检永远跑不到，而那正是最需要它的时候。
  密钥只报告"已设置(长度)"，绝不打印值。
- `Dougong.Cli`：`DOUGONG_LOG` 分级日志（`error`/`warn`/`info`/`debug`）。
  **未设置时行为与加它之前完全一致**；`info` 输出启动事实（会话、工作区、
  存储等级、内核接线、压缩、MCP 数量），`debug` 额外输出每轮细节。
  一律写 stderr（ACP 模式下 stdout 归协议所有）；不打印密钥与用户输入原文。
  无法识别的值会明确警告而非静默忽略。
- `docs/troubleshooting.md`：故障排查对照表，每一条都来自实际遇到的问题，
  并注明"这是缺陷还是环境问题"。
- `Dougong.Agent`：`ToolResultLimits` 与 `AgentEvent.ToolResultTruncated`。
  工具结果进入**模型上下文**时按 32K 字符上限裁剪（保留头 24K + 尾 6K，中间带显式标记）。
  **会话日志与会话恢复仍使用完整原文**——裁剪是上下文卫生，不是数据丢失。
  此前一次 `read_file` 读大文件或 MCP 返回大 JSON 就能挤爆窗口，是长任务崩溃的直接原因。
  **行为变化**：模型现在看到的是裁剪后的内容，超限时会收到 `ToolResultTruncated` 事件。
- `Dougong.Agent`：`SlidingWindowCompactor` 不再把最近一次 `todo_write` 计划压进摘要区间。
  计划被摘要掉后模型失去"我在做什么"的显式记录，是长任务目标漂移的典型机制。
  只改压缩器，不改模型输入结构，也不碰 `IMemoryInjector` 的语义。
- `Dougong.Agent`：L1 偏好记忆层（`MemoryArtifact`、`MemoryStore`、`StoreMemoryInjector`、
  `MemoryPrompt`、`MemoryDiagnostics`）。产物与启用严格分离，白名单硬编码
  （`PreferenceKeys`：`language` / `output.format` / `naming.convention`），
  审批绑定内容 SHA-256，跨工作区默认不传播。
  **这是 `design.md:236` 列出但此前从未实现的切片**，也是 M2 唯一的范围欠账。
  验证范围要说清：验收 2–6（安全性质）已实现并逐项反证；
  **验收 1（记忆是否改善任务表现）需要真实模型 + 独立评测集 + 预先记录的指标，
  本切片不做**——所以只能说"管道安全且可撤销"，不能说"记忆有效"。
- `Dougong.Agent`：`LoopAgentRuntime.Memory` / `MemoryWorkspace`（`init` 属性）
  与 `IMemoryInjector`。为 null 即关闭该数据流，不产生任何偏好注入。
  用 `init` 属性而非构造参数，避免破坏已有构造函数签名。
- `Dougong.Agent`：`MemoryStore.PutAsync` 拒绝版本倒退。
  允许倒退等于允许"用旧快照覆盖撤销"，而旧快照最常见的来源是中断后重放。
- `Dougong.Agent`：`MemoryStore.ReadArtifactsAsync` 改为"最后写入胜"而非"最高版本胜"。
  按版本选胜会让版本号未递增的撤销被**静默忽略**，而调用方以为成功。
- `Dougong.Cli`：`/memory` 命令，列出**全部**产物（含未生效）并说明每条不生效的原因。
  原因文本与注入器共用 `MemoryDiagnostics`，避免展示与实际注入漂移。
- `Dougong.Cli`：`DOUGONG_MEMORY` 开关（默认关闭）。启用即意味着偏好内容进入
  后续每次 prompt、可能发给远端模型——按 `skills-evolution.md §4` 必须显式开启。
- 验收 1（记忆效果对照）的测量装置与实测结果：`spike` 的 `-- l1-effect`
  （10 个留出任务、有/无记忆两组、机械化判据、预先登记的最小提升 50%）
  与 `-- l1-harness`（装置自检 6 项，不需要真实模型）。
  缺凭证时 `-- l1-effect` 明确报"未运行"并返回非零，绝不算通过。
  **实测：6 家 provider × 2 种协议共 14 个组合，提升 60%–90%（中位数 80%），
  14/14 超过 50% 阈值。** 适用范围仅限被测模型与这 10 个任务。
  实验记录属于内部材料，不在本仓库。
- `Dougong.Agent`：`ToolDescriptor.MayHaveIrreversibleEffects`（默认 false）
  与 `UnknownOutcome.MayHaveIrreversibleEffects`（默认 true）+ `RecoveredSession.Retryable`。
  恢复时据此区分「只读工具崩溃，可安全重试」与「可能已产生副作用，必须人工核实」。
  **此前所有未知结果一律拒绝恢复**——一次只读工具崩溃会让整个会话报废。
  用 `init` 属性而非构造参数，避免破坏已有调用的构造函数签名（ApiCompat 在 CI 上
  精确报出了这一点，随后按 CP0002 修正）。
- `Dougong.Kernel`：`PluginDiagnostic` 增加 `Stage` 与 `Hint`，
  用于把失败定位到阶段并给出下一步建议（M3 切片 3.4）
- `Dougong.Agent`：`TodoWriteTool` 与 `PlanTool`，模型可显式维护计划；
  运行时投影为 `AgentEvent.PlanUpdated`（M2）
- `Dougong.Agent`：`AgentPropertyKeys.Reasoning` 中性键；`Dougong.Ai.WireCompat`
  承载协议内方言差异（`ReasoningField` / `MaxTokensField`）
- `Dougong.Agent`：`ToolExecutionPlan.Reads` / `Writes` 资源级读写键
- `Dougong.Agent`：`IStoreAccessor` / `IApproverAccessor` / `IDispatcherAccessor`，
  以及 `Fixed*` 三个显式固定实现
- `Dougong.Agent`：`UnknownOutcome.ArgumentsJson`，未知结果对账时可看到调用参数
- `Dougong.Cli`：启动横幅展示存储的 `Durability` 等级

### Changed

- `Dougong.Agent`：`ToolDispatcher` 与 `LoopAgentRuntime` 不再接受裸 `IAgentStore` /
  `IToolApprover` / `IToolDispatcher` 长期持有——前者保留以兼容旧调用点（内部包装为
  `Fixed*`），新代码应使用 `*Accessor` 重载
- `Dougong.Agent`：`ToolResourceGate` 的锁表改为进程级共享（跨会话互斥此前不成立）

### Fixed

- 跨会话工作区互斥失效：锁表是实例字段，同一工作区的两个会话各持一把锁
- 生成 `ToolCallStarted` 记录时未写入调用参数，导致恢复时无法对账

---

## [0.1.0-alpha.1] — 2026-10-24

首个可运行版本。M0（内核）、M1（执行契约）、M2（协议与集成）。

### Added

- `Dougong.Kernel`：插件生命周期（7 状态）、服务注册表、代次绑定调用租约、
  事件总线、collectible ALC 动态装卸、共享契约登记。零第三方依赖。
- `Dougong.Agent`：`LoopAgentRuntime`（自研循环）、`ToolDispatcher`（唯一执行入口，
  schema 校验 + 审批 + 资源锁 + 执行前复核）、`JsonlAgentStore`（提交屏障 + 单写者租约）、
  会话恢复与分支、上下文压缩、5 个内置工具
- `Dougong.Ai`：三种线协议（OpenAI chat-completions / responses / Anthropic messages），
  用量账本与 BCL `Meter` 指标
- `Dougong.Mcp`：工具桥接、list_changed 热更新、resources/prompts、stdio 与 HTTP 传输
- `Dougong.Acp`：Agent 端 stdio 宿主、会话方法全set、`request_permission`、
  `elicitation/create`（`ask_user`）
- `Dougong.Cli`：REPL 与 `--acp` 两个入口，共用同一装配

### Known limits

- 不做插件热更新：替换插件需重启宿主（.NET ALC 是协作式卸载，热重载成本远高于 JS）
- 存储只声明 `SurvivesProcessCrash`，不声明断电安全（需要掉电测试条件）
- 工具幂等键未实现（需要远端工具配合）

---

## 维护方式

基线包放在 `artifacts/baseline/`，与 `artifacts/packages/` 分开：

| 目录 | 用途 |
|---|---|
| `artifacts/baseline/` | ApiCompat 比对的基准，**只在发版时更新** |
| `artifacts/packages/` | 当前构建产物，每次 pack 覆盖 |

更新基线的方法（发版时执行）：

```bash
# 1. 从当前 HEAD 打包
dotnet pack src/Dougong.Kernel src/Dougong.Agent -c Release

# 2. 复制为基线
cp src/Dougong.Kernel/bin/Release/Dougong.Kernel.*.nupkg artifacts/baseline/Dougong.Kernel.nupkg
cp src/Dougong.Agent/bin/Release/Dougong.Agent.*.nupkg artifacts/baseline/Dougong.Agent.nupkg
```

**为什么基线要单独放**：如果直接比对 `artifacts/packages/`，那么"上一次 pack 的产物"
会成为基线，任何改动都不会被检出——基线会自己跟着代码走，等于没有基线。
