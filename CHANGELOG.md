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

（暂无）

## [0.1.3-alpha.2] — 2026-10-30

> ⚠️ **本版含破坏性 API 变更**（`SlidingWindowCompactor` 构造函数增加参数）。

### Breaking

- **`SlidingWindowCompactor` 构造函数增加 `retainRatio` 参数**（默认 0.16）。
  记录类的主构造签名因此改变，已编译的调用方需要重新编译。
  只传 `(preservedHead, preservedTail, triggerRatio)` 的源码级调用仍可编译。

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

## [0.1.3-alpha.1] — 2026-10-30

> ⚠️ **本版含破坏性 API 变更**（`IContextCompactor.Plan` 增加参数）。
> 完善阶段尚未有外部使用者，直接改而不走弃用窗口
> （见 [compat-policy.md](docs/compat-policy.md) §6.1）。

### Added

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

### Breaking

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

## [0.1.2-alpha.1] — 2026-10-30

### Fixed

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
