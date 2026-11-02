# 差异台账

> 这份文件的唯一目的：**让"我们声称支持"这件事有据可查**。
> 每条声称必须指向一条**能跑起来的检查**，或指向一处**写明了理由的偏离**。
> 两者都没有的项，不许标 ✅。

---

## 一、为什么要有这份台账

跨语言复刻（dsh 是 TypeScript、我们是 C#）最大的失败模式不是"没实现"，
而是**实现了却不一样**——两边都能跑、都编译过、都"看起来对"。

把"编译通过"当验收标准，会漏掉整整一类问题：**可观察行为上的差异**。
编译器只检查类型，不检查顺序、不检查文案、不检查默认值、不检查渲染宽度。

所以验收标准必须是**可观察行为**，而不是源码形状。
这份台账是那个标准的落地形式：**把每条声称绑到证据上**。

### 验收怎么问

不是背一张题单。固定题单会被当成考点——照着勾就能"通过"，
而题单之外的破坏无人发现。正确的做法是**对每项能力问三个问题**：

1. **它对外暴露出什么？** 事件序列？文本输出？返回值？终端渲染？
   协议报文？磁盘上的文件？
2. **改一行内部实现，上面哪一项会变而编译仍然通过？**
   那就是该钉的地方。**答不上来，说明这项能力没有可观察面**——
   要么它不重要，要么它还没真正接出去。
3. **怎么破坏它才能让检查变红？** 说不出来，检查就还没被证明在验证任何东西。

第 2 问是关键：**它随能力不同而不同**。搜索工具的可观察面是
"该拒绝时拒绝"，压缩器的是"何时触发、压掉多少"，
协议的是"缺字段怎么办"，终端的是"占几格、在哪断行"。
把维度固定成五条，等于用别人的能力面套自己的实现。

> 一句话：**问法固定，答案不固定。**

---

## 二、台账怎么用

### 加一项能力时

1. 先在 `docs/dsh-capability-progress.md` 的状态列写 `⬜`。
2. 实现它。
3. **用上面三问确定该钉什么**，写完可执行的检查，让它跑绿。
4. **做负控**：故意破坏实现，确认检查会失败，然后恢复。
5. 才把状态改成 `✅`，并在本文件 §三 补一行：声称 → 证据 → 负控方式。
6. 若与 dsh 行为不同，**必须**在 §四 记一条偏离，写明理由与验证方式。

**跳步的后果是可见的**：`tool-package-checks` 的
`every-claim-has-evidence` 会逐行检查 §三——
证据列必须指向具体检查，负控列必须写清怎么证明它会失败。
空泛的"有意为之"和"已验证"都过不去。

**它不检查"验了哪几个维度"**：那是能力决定的，写死就成了题单。

### 偏离怎么写

不是"我们不一样"就够。必须回答四个问题：

- **哪一点不一样**（具体到参数值或行为）
- **为什么不跟**（是基数不同？是它依赖 Node 生态？是我们的安全方向更严？）
- **偏离方向对我们意味着什么**（更早压缩？更容易失败？更保守？）
- **将来什么条件下该重新考虑**

只写"有意为之"等于没写——下次有人看到差异还是会当成 bug 报上来。

### 偏离怎么失效

**偏离会过期**。dsh 改了参数、我们的基数变了、某个前提不再成立时，
旧的偏离记录会变成错误信息——比没有记录更糟，因为它看起来权威。

所以 §四 每条都必须写**依赖的前提**。改到相关代码时顺手核一遍前提。

---

## 三、声称与证据

> 只列 P0/P1 里**已实现且值得被质疑**的项。全量清单在
> `docs/dsh-capability-progress.md`，那儿的计数由 `capability-progress-counts-are-consistent` 守。

| 声称 | 证据（套件 + 检查名） | 负控怎么做的 |
|---|---|---|
| 事件顺序与语义稳定 | `event-order`：`runs-starts-first-and-completes-last`、`every-requested-has-exactly-one-completed`、`text-precedes-its-tool-call`、`context-usage-precedes-request`、`usage-reported-after-stream-not-before`、`failure-still-ends-with-terminal-event` | 删掉流式 `TextDelta` 的 yield → `text-precedes-its-tool-call` 失败 |
| 缺参数时四个文件工具口径一致 | `search`：`missing-arg-message-is-uniform` | `ToolArguments` 改回抛 `KeyNotFoundException` → 检查报出英文 BCL 消息 |
| 编辑只在唯一匹配时发生 | `search`：`edit-rejects-multiple-matches`、`edit-rejects-missing-text`、`edit-guards-against-concurrent-write` | 守卫改成 `secondIndex >= 0 && firstIndex < -1` → 只该项失败 |
| 搜索默认不钻进噪声目录、截断如实报告 | `search`：`glob-skips-noise-directories`、`glob-results-are-sorted`、`grep-rejects-invalid-regex` | 去掉 `IsNoiseDirectory` 判据 → `glob-skips-noise-directories` 失败 |
| 会话恢复后分叉点不悬空 | `session`：`branch-truncates-at-fork-point`、`branch-avoids-dangling-tool-call`、`resume-rebuilds-tool-exchange`、`resume-blocks-on-unknown-outcome` | 让 `SessionBranch` 在截断点保留半条工具交互（只留 assistant 的 tool_calls、丢掉 tool 结果）→ `branch-avoids-dangling-tool-call` 失败 |
| 模型目录载荷完整 | `host-protocol`：`models-listed-carries-providers`、`models-listed-carries-current-model`、`models-listed-parses-from-wire-text` | 给 `Providers` 加 `[JsonIgnore]` → 三条全失败（含线协议解析）；去掉 `required` → `models-listed-carries-providers` 失败 |
| 协议缺字段必须失败、时间戳不默认取 Now | `host-protocol`：`missing-required-field-fails-loudly`、`timestamp-is-not-defaulted-to-now` | 把 `At` 改回 `= DateTimeOffset.Now` → 检查报出实际补的假时间 |
| 终端宽度按码点算、截断不切代理对 | `terminal`：`width-golden-values`、`width-is-biased-wide-not-narrow`、`truncate-never-splits-surrogate-pair` | `Truncate` 改回按 `char` 遍历 → 报出孤立高代理 `U+D83D` |
| 系统提示真的进入请求 | `prompt`：`prompt-is-actually-sent`（抓连接器实收消息）、`prompt-omits-discipline-for-missing-tools` | `BuildRequestMessages` 恒返回原历史 → 该项失败 |
| 文件写入原子且带版本守卫 | `filesystem`、`fileversion` | 去掉 `MustMatchVersion` 校验 → `fileversion` 套的版本冲突检查失败；把原子写改成直接覆盖目标文件 → `filesystem` 套的中断写检查失败 |
| 宿主能扩展多态类型且不丢 SDK 登记 | `host-protocol-extension`：`host-type-roundtrips`、`extension-does-not-drop-sdk-registrations`、`registering-after-freeze-throws`、`registering-wrong-base-throws`、`registered-extensions-are-observable` | 把 `typeInfo.PolymorphismOptions ?? CreateEmpty(...)` 改成 `CreateEmpty(...)`（替换而非追加）→ `extension-does-not-drop-sdk-registrations` 报 `NotSupportedException`；删掉 `RegisterDerivedType` 里的冻结检查 → `registering-after-freeze-throws` 失败 |
| 用户偏好进配置、安全开关冻结 | `preference`：`preferences-roundtrip`、`preferences-save-preserves-providers`、`preferences-default-to-off`、`env-overrides-config`、`security-switch-freezes-in-process`、`memory-dir-comes-from-config-not-env` | 把 `Current` 改成每次 `Load()` → `security-switch-freezes-in-process` 报"自举漏洞"；保存时整体重写 → `preferences-save-preserves-providers` 报"providers.a 丢了" |
| 工具卡住不会挂住整轮 | `timeout`：`hanging-tool-times-out`、`timeout-is-not-reported-as-cancellation`、`timeout-counts-as-executed`、`per-tool-timeout-overrides-default`、`outer-cancel-still-wins`、`new-outcome-does-not-shift-existing-values` | 把 `Executed` 里的 `or TimedOut` 删掉 → `timeout-counts-as-executed` 失败；把 `catch when (!cancellationToken.IsCancellationRequested)` 的守卫去掉 → `outer-cancel-still-wins` 失败 |
| shell 拦常见越界且承认不是沙箱 | `shell-policy`：`destructive-commands-are-rejected`、`credential-reads-are-rejected`、`cd-outside-workspace-is-rejected`、`rejection-message-admits-it-is-not-a-sandbox`、`read-only-classification-rejects-redirection`、`shell-tool-reports-rejection-as-result` | 把 `deny(escape)` 改成 `allow(escape)` → `cd-outside-workspace-is-rejected` 失败；把拒绝消息改成只写"命令被拒绝" → `rejection-message-admits-it-is-not-a-sandbox` 失败 |
| 会话格式版本：写入、兼容旧文件、拒绝不兼容 | `session`：`session-writes-format-version`、`session-reads-old-files-without-version`、`session-rejects-newer-format`、`session-rejects-malformed-version`、`branch-writes-format-version` | 不写版本行 → `session-writes-format-version` 失败；不跳过版本行 → 8 项同时失败（四条读取路径全挂）；允许 `0` → `session-rejects-malformed-version` 失败；分支不写版本行 → `branch-writes-format-version` 失败 |
| 工作区指令真的进了提示且不挤掉工具纪律 | `ws-instructions`：`project-instructions-are-loaded`、`duplicate-file-names-render-once`、`project-wins-when-identical`、`global-header-dropped-when-fully-deduped`、`over-budget-is-truncated-not-dropped`、`truncation-keeps-utf8-intact`、`instructions-do-not-starve-tool-policy` | 把去重改成"先写标题再逐条跳过"→ `project-wins-when-identical` 报出空的「用户全局约定」标题；只数内容次数不查标题则漏过（这是第一版的真实缺陷）；链预算改回 16 KiB 并用 **ASCII** 填充 → `instructions-do-not-starve-tool-policy` 失败（用汉字填则两项预算单位不重合，检查空转） |
| 时间读数带时区且不刷屏 | `time-context`：`time-reading-has-timestamp-and-zone`、`time-first-reading-says-unavailable`、`time-respects-minimum-interval`、`time-reports-elapsed-compactly`、`time-clock-rollback-clamps-to-zero`、`time-offset-is-culture-independent` | 把 `_lastInjectedAt = now` 移到算间隔之前 → 第二条起全部报 `unavailable`；去掉 `ar-SA` 文化下的手工偏移拼装改用 `zzz` → `time-offset-is-culture-independent` 失败 |
| 重试只救瞬时故障、绝不重复交付 | `retry`：`retry-recovers-from-transient-failure`、`retry-emits-event-before-waiting`、`retry-skips-permanent-failures`、`retry-never-after-text-delivered`、`retry-stops-at-max-attempts`、`retry-disabled-by-default`、`retry-classifier-maps-status-codes`、`retry-backoff-grows-and-caps`、`retry-reads-provider-retry-after-from-wire` | 删掉连接器里 `failure.Data[...] = retryAfter` 的写入 → `retry-reads-provider-retry-after-from-wire` 报"没传到分类器"（此前两段各自正确、中间没接，所有检查都绿）；删掉 `producedAnything` 守卫 → `retry-never-after-text-delivered` 报 provider 被调 6 次（用户会看到 6 份重复内容）；分类器改成"一律 ServerError" → `retry-skips-permanent-failures` 报 401 被重试 6 次；去掉 `retryCount >= MaxRetries` → 报 101 次调用；把 `restartStream = true` 改成只 `break` → 三项同时失败（发事件但没真重发） |
| 截断的大结果能被模型取回 | `spill`：`big-result-is-spilled-and-marked`、`spilled-path-is-readable-by-read-file`、`small-result-is-not-spilled`、`spill-path-is-relative-not-absolute`、`spill-disabled-still-truncates-honestly`、`spill-files-are-hidden-from-glob`、`identical-content-reuses-one-file`、`spill-root-name-is-derived-not-duplicated` | 落盘但不把路径写进标记 → 三项失败；把文件名加回时间戳 → `identical-content-reuses-one-file` 报"同样的内容得到两个路径"；把落盘目录挪到工作区外 → `spilled-path-is-readable-by-read-file` 报 `DirectoryNotFoundException`（"标记里给了读不到的路径"）；去掉长度判断让小结果也落盘 → `small-result-is-not-spilled` 报磁盘上多出文件；把 `.dougong` 从噪声目录移除 → `spill-files-are-hidden-from-glob` 报 glob 扫到了 spill 文件；把判据改成无关的名字 `.dougong-typo` → 两条同时红（spill 文件出现在搜索结果里） |
| 会话内容搜索能定位到具体记录 | `session-search`：`search-finds-user-and-model-text`、`search-reads-legacy-string-payload`、`search-excludes-tool-content`、`search-is-case-insensitive`、`search-survives-corrupt-tail`、`search-snippet-contains-match`、`search-respects-limit-and-says-so`、`search-hit-carries-resume-locator` | 改成搜整个 JSON payload → `search-excludes-tool-content` 报"工具参数/结果被搜到了"（第一版用例里工具载荷没有 `text` 键，去掉类型过滤也能通过，补了一条带 `text` 键的工具记录后才真的能红）；不认裸字符串载荷 → 7 项同时失败；片段改成总取开头 → `search-snippet-contains-match` 报"片段里没有匹配词"；`Truncated` 恒为 false → `search-respects-limit-and-says-so` 报"被截断了却没标记" |
| 会话标题能认出会话且不超列宽 | `session-title`：`title-uses-first-user-input`、`title-truncates-with-ellipsis`、`title-exact-fit-has-no-ellipsis`、`title-flattens-multiline`、`title-empty-input-gives-placeholder`、`title-never-exceeds-max-chars`、`title-reads-from-session-file`、`title-is-derived-not-stored`、`title-unreadable-is-distinct-from-empty` | 改成取最后一条用户输入 → `title-uses-first-user-input` 报"取错了条目"；让截断判断恒成立 → 3 项失败（完整输入被加省略号）；把 `char.IsWhiteSpace` 改成只认空格 → `title-flattens-multiline` 报"没有压掉换行"；让推导时往文件追加内容 → `title-is-derived-not-stored` 报"推导标题时改动了会话文件" |
| 提问四态不混淆且不打扰用户 | `ask-user`：`ask-accepts-by-index`、`ask-accepts-by-label-text`、`ask-declines-on-skip`、`ask-eof-is-unavailable-not-declined`、`ask-cancel-is-distinct-from-decline`、`ask-invalid-shape-does-not-ask-user`、`ask-rejects-duplicate-headers`、`ask-multi-select-takes-several`、`ask-validation-shared-with-acp`、`ask-schema-uses-header-as-key`、`ask-partial-answers-survive-eof` | 把 EOF 报成 `Declined` → `ask-eof-is-unavailable-not-declined` 失败；让形状不合法也继续问 → `ask-invalid-shape-does-not-ask-user` 报"实际 Answered"（真的问了用户）；在 ACP 里加回自己的 `Validate` → `ask-validation-shared-with-acp` 报"校验会漂移"；去掉 header 重复检查 → `ask-rejects-duplicate-headers` 失败 |
| 抓取能取到正文且不误伤 | `web-fetch`：`fetch-extracts-text-from-html`、`fetch-drops-script-content`、`fetch-passes-non-html-through`、`fetch-explains-empty-body`、`fetch-rejects-non-http-scheme`、`fetch-reports-http-status`、`fetch-declares-network-and-host`、`fetch-permission-unknown-for-bad-url`、`fetch-caps-download-size` | 跳过 script/style 整段删除 → `fetch-drops-script-content` 报"脚本内容漏进了正文"（实测 `var s='JS_SECRET'` 出现在正文里），同时 `fetch-explains-empty-body` 也红；权限改报 `None` → `fetch-declares-network-and-host` 失败；去掉正文长度限制 → `fetch-caps-download-size` 报 5.2 MB |
| 改动清单按轮聚合且不假称完整 | `workspace-changes`：`changes-splits-by-user-turn`、`changes-excludes-read-tools`、`changes-dedupes-within-turn`、`changes-includes-write-and-edit`、`changes-omits-shell-commands`、`changes-keeps-final-turn`、`changes-tolerates-corrupt-tail`、`changes-survives-legacy-payload` | 去掉工具白名单（按有没有 path 判断）→ `changes-excludes-read-tools` 报"读操作被算成了改动"，`changes-omits-shell-commands` 也红；改成每见工具调用就切轮 → 6 项同时失败；不收尾最后一轮 → `changes-keeps-final-turn` 报"最后一轮丢了（0 轮）" |
| 每个 CLI 参数都能在帮助里找到 | `doctor`：`every-cli-flag-appears-in-help` | 从 `--help` 删掉 `--changes` → 报"没有出现在 --help 里"；白名单加 `--stats` 但不写进 help → 同样报错（检查直接引用参数解析用的清单，不另列一份） |
| 检查测的是当前代码而非陈旧副本 | `terminal`：`comparison-uses-release-cli-assembly` | 把检查目录里的 `Dougong.Cli.dll` 时间戳改成 2020 年 → 报"比 Release 旧……任何负控结论都不作数"；恢复后转绿 |
| 每个会被显示的阶段都有文案 | `terminal`：`every-phase-has-visible-label`、`binary-is-not-stale` | 新增 `Retrying` 时若只加枚举不加文案 → `every-phase-has-visible-label` 报"阶段 Retrying 会被显示却没有文案"；改源码不重建 Release → `binary-is-not-stale` 报"跑的是旧构建" |
| 压缩比例默认值与可配置性 | `compaction`、`wiring`、`doctor` | 把 `DefaultTriggerRatio` 改成 0.99 → `compaction` 套的触发点检查失败；给 `--doctor` 传入 `retain >= trigger` → `doctor` 套的非法规格检查失败 |

---

## 四、有意偏离

> 每条都必须回答：哪不一样 / 为什么不跟 / 方向意味着什么 / 什么条件下重考虑。

### D-1 压缩触发比例取 0.75，dsh 取 0.8

| | |
|---|---|
| **哪不一样** | 触发阈值 `DefaultTriggerRatio = 0.75`；dsh 的 `thresholdRatio` 默认 0.8 |
| **为什么不跟** | 基数不同。dsh 的 headroom 是 65 536 token、比例乘在 `W − O` 上；我们的预留是 512 token、比例乘在 `W − O − 预留` 上。基数小得多，同样的 0.8 会明显更晚触发。 |
| **方向** | 更早压缩。我们的 token 估算本身偏保守，早触发是安全方向——代价是多花摘要调用，收益是不撞 provider 上限。 |
| **前提 / 何时重考虑** | 前提是 token 估算是保守的（`TokenBudget` 按 ASCII 4、CJK 1 分档）。若将来接入精确分词器、估算不再保守，0.75 就该往上调。 |
| **验证** | `compaction` 套钉住触发点与保留量；改比值会失败。 |
| **源码位置** | `src/Dougong.Agent/ContextCompaction.cs` 的 `DefaultTriggerRatio` 注释 |

### D-2 压缩保留比例取 0.16，与 dsh 一致（记录以防误改）

| | |
|---|---|
| **哪不一样** | 无差异，与 dsh 的 `retainRatio` 相同 |
| **为什么记** | 曾被当成 bug 报过一次（"你说照 dsh，为什么触发比例不一样"）。**对 dsh 的是保留比例，不是全部参数**。 |
| **前提** | 按 1M 窗口设计：触发 0.75、保留 0.16，压缩后降到 0.16–0.2。按 128K 反推会得出偏小的值，而 128K 已不是典型窗口。 |
| **验证** | 同上 |

### D-3 上下文溢出识别删掉了两条宽松措辞

| | |
|---|---|
| **哪不一样** | 不认 `too many tokens`、`token limit exceeded` 这两种裸措辞；pi.net 认，dsh 不认。 |
| **为什么不跟 pi.net** | 这两句无宾语，可能是**限流**也可能是**输出上限**，不是上下文窗口。`too many tokens` 正是 Bedrock 限流错误里的词，只能靠排除清单挡，而排除清单只挡已知的。 |
| **方向** | 更容易漏识别（少报），不会误报。漏判的后果是走普通失败路径、用户看到原始错误；误判的后果是错误地压缩一遍历史并丢掉内容——后者更糟。 |
| **前提 / 何时重考虑** | 前提是没有真实 provider 只用这两种措辞表示上下文溢出。若出现实测反例，加回来并在排除清单里精确化。 |
| **源码位置** | `src/Dougong.Agent/ContextOverflowDetector.cs` 的模式表 |

### D-4 工具结果截断后不再是合法 JSON

| | |
|---|---|
| **哪不一样** | 超限裁剪会在文本中间插入标记，结构因此断开；不是"截断成合法 JSON"。 |
| **为什么不改** | 工具结果以**文本**进入模型历史，模型不解析它，所以"非法 JSON"不导致请求失败。改成结构化截断要按 JSON 语义裁剪，成本远高于收益。 |
| **方向** | 模型若尝试解析会失败。缓解：标记里写明"已截断"，让它知道在看残缺数据而不是数据本身有问题。 |
| **验证** | `token` 套的截断检查；`failure` 套覆盖超限路径。 |
| **源码位置** | `src/Dougong.Agent/ToolResultLimits.cs` |

### D-5 schema 校验对不支持的关键字必须报错

| | |
|---|---|
| **哪不一样** | 只支持工具参数实际用到的构造；遇到 `$ref`、`allOf`、条件 schema **报错**而不是跳过。 |
| **为什么不静默跳过** | M2 接入 MCP 后 schema 来自不可信远端。静默跳过等于向使用者宣称"已校验"而实际没有——那比明确失败更危险。 |
| **方向** | 更容易失败（拒绝带复杂 schema 的工具）。这是刻意的：失败可见优于静默放行。 |
| **验证** | `schema` 套。 |
| **源码位置** | `src/Dougong.Agent/ToolSchemaValidator.cs` |

### D-6 终端宽度宁可偏宽

| | |
|---|---|
| **哪不一样** | 按码点累加，不处理字形簇。`❤️`（U+2764+VS16）算 2 格（文本呈现时终端只占 1）；`👨‍👩‍👧‍👦` 算 8 格（终端显示 2）。 |
| **为什么不跟终端** | 完整字形簇分割需要 ICU 或自带表，与"状态行别错位"的收益不成比例。 |
| **方向** | 偏宽：少显示几个字。**偏窄会写满最右列触发折行，布局崩坏**——那才是真问题（2026-10-29 真机上表现为活动行文字消失）。 |
| **验证** | `terminal`：`width-is-biased-wide-not-narrow` 钉住方向，`width-golden-values` 钉住具体数值。 |
| **源码位置** | `src/Dougong.Cli/Terminal/DisplayWidth.cs` |

### D-7 不打包 ripgrep

| | |
|---|---|
| **哪不一样** | dsh 带约 30 MB 原生二进制；我们用 BCL 在 `IFileSystem` seam 上实现。 |
| **为什么不跟** | 沙箱实现将来可能根本没有本地文件系统，那时外挂二进制用不上。正确做法是让搜索建在 seam 上，性能不足时优化实现。 |
| **方向** | 大仓库上比 ripgrep 慢。缓解：限深 12 层、默认跳过噪声目录、扫描上限 400 文件，且超限如实报告。 |
| **前提 / 何时重考虑** | 若真实用户在大仓库上明确抱怨速度，再评估"seam 上加速"（索引、并行读）而不是外挂二进制。 |
| **验证** | `search` 套 10 项。 |
| **源码位置** | `src/Dougong.Agent/GlobTool.cs`、`GrepTool.cs` 的类型注释 |

---

### D-8 shell 只做命令策略，不做 OS 级沙箱

| | |
|---|---|
| **哪不一样** | dsh 有四套真隔离（bwrap / landlock / Seatbelt / Windows ACL）。我们只有 `ShellCommandPolicy` 的模式匹配：危险命令、凭据路径、`cd` 越界。 |
| **为什么先这样做** | 三条：① **dsh 自己说它不是安全边界**——它的 `SAFETY.md` 原文 "do not guarantee isolation or prevent damage"、"Prefer a disposable virtual machine"；做了三平台内核隔离的项目建议用户改用 VM。② **它解决的不是我们的主要风险**——dsh 沙箱边界只有文件效果（`docs/subsystems/sandbox.md`：「Network and process visibility are outside this vocabulary」），而我们的风险是误操作。③ **成本不成比例**——Linux 要 C 代码 + 原生产物（破坏零依赖），Windows 要 P/Invoke 受限令牌 + ACL，而 dsh 的 CI 都没跑 Windows 腿，我们更验不全。 |
| **方向** | 拦得住常见误操作，**拦不住有意绕过**（`find / -name x`、`cat $(...)`、脚本内 cd）。所以拒绝消息里必须写明"这是策略过滤而非沙箱"——宣称已沙箱而只是模式匹配，会让用户放松审查，比不做更危险。 |
| **现存缺口（已接受，不再是待办）** | 8 个真实误操作里 6 个被放行：`rm -rf ..\other-project`、`rm -rf C:\work\old`、`del /s /q C:\work\other`、`powershell -c Remove-Item`、`cp C:\Windows\...\hosts .`、`type C:\Windows\win.ini`。根因是判据用正则匹配**命令文本**。**决定不再补**——补到"解析路径"只是把军备竞赛推进一步，而没有内核隔离就永远没有收敛点；只补一半比不补更危险，因为用户会以为文件受保护。详见 `dsh-capability-gap.md` 阶段 3。 |
| **前提 / 何时重考虑** | 若要**对外宣称**"可安全让 AI 跑任意命令"，必须真隔离——而正确答案不是自己实现三平台，是把整个 agent 跑进容器（与 dsh 自己的建议一致）。 |
| **验证** | `shell-policy` 9 项，其中 `rejection-message-admits-it-is-not-a-sandbox` 专门钉住"承认边界"这一点。 |
| **源码位置** | `src/Dougong.Agent/ShellCommandPolicy.cs` |

### D-9 用户偏好进配置，安全开关在进程内冻结

| | |
|---|---|
| **哪不一样** | 我们把 `shell` / `memory` / `show_reasoning` 三个开关放进 `config.toml` 的 `[preferences]`，并在**进程启动时冻结**。|
| **参考了什么** | `memory` 进配置照 **hermes**（`memory.memory_enabled`）；偏好整体进配置是四家共同点（dsh 用 profile 的 `cordis.patch.yml`、hermes 用 `~/.hermes/config.yaml`、pi 用 `~/.pi/agent/settings.json`）。 |
| **`memory_dir` 是类推，不是照抄** | 四家**没有一个是记忆路径**配置：hermes 的 memory 段 10 个字段无一涉及路径，因为它有 `HERMES_HOME` 统管全部状态；pi 有 `sessionDir`（**会话**目录，`settings-schema.ts:401`）但那是另一个东西。我们加 `memory_dir` 是因为**当时没有统一的 home 抽象**——`.dougong` 硬编码在 7 个文件 19 处。

**2026-11-01 已加统一 home（`DOUGONG_HOME`），但这个字段保留**：两者的用途不同——
home 决定"所有状态放哪一整个根"，`memory_dir` 决定"记忆是否单独放一个位置"
（例如记忆要同步、其他状态不用）。四家里没有一家有后者，所以这仍是类推；
若将来发现没人用，删它比留着便宜。 |
| **为什么冻结** | 照 **hermes**：`tools/approval.py` 在导入时读定 `HERMES_YOLO_MODE`，注释写 "reading os.environ per call would let any skill running in the process set this and bypass every approval check (prompt-injection escalation path)"。我们的对应风险是**自举**：`shell` 一旦开启，模型能用 `echo 'shell = true' >> ~/.dougong/config.toml` 给自己永久授权。冻结让配置改动要等用户主动重启才生效。 |
| **为什么不做成"完全不持久"** | dsh 会把最激进的权限档位排除在持久 schema 外（"never enters `permission.defaultPreset`"），那是更彻底的做法规避自举。但我们面对的是本地单人使用，每次都要回答是纯负担；冻结已经切断自举路径（模型需要用户重启才能生效）。 |
| **优先级为何与密钥相反** | 开关是"环境变量 > 配置"，密钥是"凭据文件 > 环境变量"。理由：开关有"我这次想关掉"的临时语义，密钥没有——密钥不该有"临时"之说。 |
| **验证** | `preference` 套 9 项。其中 `security-switch-freezes-in-process` 专门钉自举防护，负控是把 `Current` 改成每次 `Load()`，失败信息会写"自举漏洞：模型可以用 shell 改配置给自己永久授权"。 |
| **源码位置** | `src/Dougong.Ai/UserPreferences.cs`、`src/Dougong.Ai/UserPreferencesFile.cs` |

### D-10 LSP 代码导航：是效率优化，不是能力缺口

| | |
|---|---|
| **哪不一样** | dsh 有 `lsp`（seam）+ `lsp-stdio`（传输）+ `tool-lsp`（模型可见），给 agent 定义/引用/实现/悬停四种语义导航。我们只有 `GlobTool` / `GrepTool` 的文本搜索。 |
| **先定性：它不是什么** | **它不是"没有它模型就不会改代码"**。我们的编辑闭环是 `glob/grep → read_file → edit_file（唯一匹配）→ 验证`，这条链路不依赖语义层。绝大多数改动是"在这个文件里把这个函数换成那样"，文本搜索完全够用。Claude Code、Cursor 的核心编辑循环同样是文本层的。 |
| **它真正值钱的地方很窄** | 只有两类任务 grep 会给错答案：① **重构**——改一个被多处引用的接口，要找出全部实现与调用点；② **重载/泛型/同名方法**——要区分"是哪一个 `Parse`"。其余场景（改实现、加功能、找唯一名字的定义）grep 够用。 |
| **连错也是可自愈的** | grep 在"找全部调用点"上会漏，但漏掉的后果是编译不过或运行报错，模型下一轮会自己发现并补。这与"语义层给错定位"不同——后者会让模型基于错误信息继续改，反而更难发现。 |
| **所以它是优化** | 做了：上述两类任务少几轮。不做：任务仍能完成，只是多花几轮。**优化要等真实的低效场景出现才值得做**，否则是在为想象中的瓶颈付成本。 |
| **为什么现在不做** | 只有一条实质理由：**没有真实的低效报告**。而它的规模不小——语言服务器进程管理、初始化握手、能力协商、版本协商、崩溃恢复、按文件类型路由到不同 server，接近一个子系统的三分之一。为"没被提出的痛点"付这个成本不划算。 |
| **需求侧的额外成本** | LSP 需要用户自己装 language server（`csharp-ls`、`typescript-language-server`、`pyright`…）。没装时的降级路径必须设计，否则是"文档说支持、实际不可用"——比不支持更坏的承诺。这是**我们需要承担的**成本，不是用户的收益。 |
| **先说清一个容易搞错的点** | 本项目的定位是"**用 .NET 写的、语言无关的 harness**"（`goal.md`）。**不是"C# 专用的 agent"**——用户可能在改 TypeScript、Python、Rust。 |
| **因此 Roslyn 不是替代方案** | 我最初写的理由是"C# 场景下 Roslyn 已在编译进程里，收益不如直接用"——**那条错了**：Roslyn 只解析 C#，而 LSP 是跨语言协议。真要做导航，LSP 才是正确形态；Roslyn 会把能力绑到单一语言，与定位冲突。 |
| **启动信号** | 出现**具体的**低效场景（不是"想要跳转"），例如"重构一个被 30 处引用的接口时 grep 明显不够"。届时照 dsh 做 **seam + 传输 + 工具三件套**，而不是把某个语言服务器硬编进来。 |
| **建议的第一步** | 若启动，**只接一个语言**（如 C# 走 `csharp-ls`）验证端到端，而不是一次铺开全语言——"没装 server"的降级路径只有真实环境才会暴露问题。关键是找不到 server 时必须**明确报"这个语言没有配置语言服务器"**，不能静默返回空：那会让模型以为"没有引用"，而实际是"查不了"。 |
| **验证** | 无检查（能力不存在）。若将来做，验收点是"重载方法跳到正确的那一个"——那需要真语言服务器，构造数据钉不住。 |

### D-11 指令文件按字节算预算，总提示按字符算

| | |
|---|---|
| **哪不一样** | dsh 的 `dsh-agent-instructions` 每条链 64 KiB，且没有总上限。我们每条链 **4 KiB（UTF-8 字节）**，而 `SystemPromptBuilder.MaxTotalChars` 是 **8000 字符**。 |
| **为什么不照抄 64 KiB** | 两个预算会打架，而大的那个不起作用：第一次实现取 16 KiB，结果整节被总预算截掉，症状是"文件明明有内容，提示里一个字都没有"。让链预算服从总预算才是唯一自洽的做法。 |
| **一个容易看错的点** | **两个预算单位不同**：链预算按字节，总预算按字符。ASCII 下 1 字节 = 1 字符，4 KiB 真的顶到 8000 的一半；中文下 4 KiB 只有约 1400 字符，离总预算还很远。所以**中文用户实得的约定篇幅约为英文用户的三分之一**。这不影响正确性（两者都不截断），但将来若按字符收紧这个常数，必须考虑中文场景会不会被无谓剪短。 |
| **我的检查第一版是空转的** | `instructions-do-not-starve-tool-policy` 原本写 1400 个汉字（约 4200 字节），无论链预算是 4 KiB 还是 16 KiB 都不会被截断——把预算改回 16 KiB 它照样绿。改用**远超上限的 ASCII** 填充后，负控才真的会红。 |
| **验证** | `ws-instructions` 套 8 项；负控见 §三。 |
| **源码位置** | `src/Dougong.Agent/WorkspaceInstructionsSection.cs` |

### D-12 重试只做 dsh 的 normal 模式，且不重试"已交付"

| | |
|---|---|
| **哪不一样** | dsh 的 `dsh-llm-retry` 有两种模式：`normal`（有限次）与 `always`（无限重试直到成功/取消/卸载）。我们**只做 normal**。 |
| **为什么不做 always** | 在 CLI 场景里它的实际效果是"卡住不动"：用户看不到任何进展，只能 Ctrl+C，而那与直接失败没有区别，却多烧了额度。dsh 的 always 模式服务于它自己的宿主形态（可观测的会话视图 + 明确的卸载语义），我们没有等价物。 |
| **更重要的约束** | **流一旦向宿主交付过任何增量（文本/推理），就不再重试。** 那些增量已经 `yield` 出去了，重发会让用户看到重复内容，而 `AgentEvent` 里没有"撤回"事件。dsh 的理由相同（raw stream 无法分离已发出的 chunk）。负控实测：去掉这个守卫后，一次失败会调用 provider 6 次，交付 6 份重复文本。 |
| **与 dsh 的一个结构差别** | dsh 把重试做成插件，通过 `agent/request-error` 瀑布钩子介入；我们的重试内建在 `LoopAgentRuntime` 里，因为它必须知道"这次尝试有没有交付过内容"——那是循环的局部状态，插件拿不到。 |
| **复核时修掉的两处** | ① **文件名带秒级时间戳**，导致同一内容跨秒落盘两次，而注释写着"不会反复落盘"——已去掉时间戳，让哈希成为唯一标识（`identical-content-reuses-one-file` 钉住）。② **HTTP 408 被判为不重试**：它带状态码所以走不到"取消类"分支，又不在 429/5xx 白名单里，于是一次纯等待型的失败被当成永久错误（`retry-classifier-maps-status-codes` 钉住）。 |
| **顺带修掉的缺陷** | 原实现把 `GetAsyncEnumerator` 放在 try 之外。而 `HttpClientChatClient` 是 async 迭代器，**状态码检查发生在第一次 `MoveNext` 里**——所以 429/503 是在"读流第一帧"抛的。旧写法下首次失败会直接冲出整个迭代器，宿主看到裸异常而不是 `RunFinished(Failed)`，连诊断都没有。 |
| **验证** | `retry` 套 8 项；4 组负控见 §三。 |
| **源码位置** | `src/Dougong.Agent/RetryPolicy.cs`、`src/Dougong.Agent/RetryClassifier.cs`、`src/Dougong.Agent/LoopAgentRuntime.cs` |

### D-13 spill 落在工作区内，且不拆三件套

| | |
|---|---|
| **哪不一样** | dsh 把 spill 拆成 seam（`ctx.spillStore`）+ `spill-local` 后端 + `spill-policy` 策略三个包。我们只有一个 `SpillStore` 具体类，策略（什么时候落盘）直接写在 `AppendToolExchangeAsync` 里。 |
| **为什么不拆** | 拆 seam 的价值是"能换后端/换策略"。我们只有一个宿主形态（本地 CLI），换后端的场景还不存在——先拆出三个类型，等于给一个不存在的需求写接口。将来真出现第二个后端时，把 `WriteAsync` 抽成接口即可，调用点只有一处。 |
| **关键约束：必须落在工作区内** | `read_file` 的路径守卫（`WorkspacePathGuard`）拒绝工作区外的绝对路径。落到外面的话模型拿到路径也读不了，而标记里写着"可以读"——**比不给路径更糟**。负控验证过：把目录挪到系统临时目录，`spilled-path-is-readable-by-read-file` 立刻报 `DirectoryNotFoundException`。 |
| **路径必须是相对的** | 绝对路径会带出用户名，进模型上下文与会话日志后无法跨机器使用；`read_file` 对绝对路径还要过一遍越界检查。所以标记里写 `.dougong/spill/<时间戳>-<工具>-<哈希>.txt`。 |
| **文件名带内容哈希** | 同一段内容重试时不重复落盘。时间戳保证不同会话的同类结果不互相覆盖。 |
| **顺带补的** | `.dougong` 加进 `GlobTool` 的噪声目录列表。否则"项目里有哪些文件"的答案会被自己的运行时产物污染，而模型会把 spill 副本当成源码。grep 复用同一判据，自动生效。 |
| **不做的** | 自动清理。这些文件很小、数量受工具调用次数限制，而"删掉模型可能还要读的文件"是更糟的错误。`.dougong/spill` 目录可整个删除，由用户或宿主决定。 |
| **验证** | `spill` 套 6 项；4 组负控见 §三。 |
| **源码位置** | `src/Dougong.Agent/SpillStore.cs`、`src/Dougong.Agent/ToolResultLimits.cs` |

### D-14 会话搜索扫 JSONL，不建 SQLite 索引

| | |
|---|---|
| **哪不一样** | dsh 的 `session-query-sqlite` 用 SQLite FTS5 建全文索引（排名、片段、游标世代）。我们直接扫 JSONL 文件，`SessionSearch` 是静态类。 |
| **为什么不建索引** | 会话是 append-only JSONL，目录里通常几十到几百个文件、每个几十 KB。全扫是毫秒级。建索引要维护一致性（新记录入库、删除、重建、游标失效），那层复杂度只有在"会话多到扫描扛不住"时才值得付——而我们离那个点还远。 |
| **只搜用户输入与模型回复** | 工具参数与结果不参与。工具结果里常有整文件内容，搜它会让"我们聊过什么"退化成"哪些工具碰过这个字符串"。`auth.cs` 这个例子最能说明问题：只应命中模型说的那句，不该命中 `read_file` 的参数。 |
| **两种载荷形态都要认** | `UserInput` 的载荷是裸字符串，`ModelResponse` 早期是裸字符串、现在是 `{text, calls}`。只认对象会静默漏掉整类会话，而症状恰好是"搜索说没找到"——很容易被当成"确实没有"。判据与 `SessionRecovery` 的读取侧一致。 |
| **顺带修的** | `--list` 原本在 provider 配置检查**之后**，于是密钥过期时查不了自己的会话列表——而那正是最需要查的时候（"上次是在哪个会话解决的"，然后去修 key）。`--list` 与 `--search` 一起提到检查之前，`--doctor`/`--report` 早已在那个位置，理由相同。 |
| **没做的** | seam 接口。`SessionSearch` 是静态类，宿主换不了后端——这一点记在进度表的 🟡 里（"会话查询 seam"），不假装已完整。 |
| **验证** | `session-search` 套 8 项；4 组负控见 §三。 |
| **源码位置** | `src/Dougong.Agent/SessionSearch.cs`、`src/Dougong.Cli/Program.cs` |

### D-15 会话标题从历史推导，不写入日志

| | |
|---|---|
| **哪不一样** | dsh 的 `session-title` 是 **log-backed**（标题写进会话日志，再折叠最新一条），并支持可选的异步 LLM 提供者。我们从首条用户输入**推导**标题，不写入任何东西，也不调模型。 |
| **为什么不能写进日志** | `JsonlAgentStore` 用 `Enum.Parse<StoreRecordKind>(kind)` 读记录类型。**旧程序遇到新增的 `SessionTitle` 记录会抛 `ArgumentException` 而不是跳过**——等于让所有旧版本读不了新会话。那需要真正的格式版本迁移，而本项目明确不做（`compat-policy.md` §7.1）。推导不产生任何新增记录，因此零格式风险。 |
| **为什么不做 LLM 标题** | 三件事：① **它要求会话能调模型**，而 `--list` 的典型用途恰恰是"密钥过期了，我想先找回上次那个会话"——给标题加这个前提等于把列表也废掉；② 列 200 个会话要发 200 次请求，或者建缓存，而缓存要解决失效（会话继续追加后标题该不该更新）；③ **确定性标题已经够用**：用户要的是"能认出来"，首条用户输入通常就是最好的线索，因为那是他自己写的话。 |
| **取首条而不是最新** | 会话开头定义主题，后面可能飘到别的话题。而"我为什么开这个会话"与"我记得它关于什么"往往是同一个东西。 |
| **上限含省略号** | `MaxChars = 20` 是**含省略号**的总长。列表列宽固定，正文写满 20 再追加"…"会变成 21 字符把后面那列挤掉。这条规则第一版写错过两次：先是不减一位（21 字符），改完又让"恰好 20 字的完整输入"被误加省略号——最终改成"先按上限规范化、根据是否消费完输入决定要不要省略号"两步走。 |
| **"读不到"与"无标题"必须分开** | 两者对用户的含义相反："无标题"是"这个会话还没说话"（可以忽略），"读不到"是"这里有个会话但我打不开"（可能正是要找的那个，值得去查权限）。合并成一个占位会让用户把打不开的会话当空会话跳过。 |
| **按显示宽度对齐** | 一个汉字占两格。用 `,-21` 那种字符补齐会让中文行比英文行宽近一倍，后面的列全被推歪。列输出用 `DisplayWidth.Of` 补空格。 |
| **验证** | `session-title` 套 8 项；4 组负控见 §三。 |
| **源码位置** | `src/Dougong.Agent/SessionTitle.cs`、`src/Dougong.Cli/Program.cs` |

### D-16 提问语义下沉到 Agent，传输各自实现

| | |
|---|---|
| **哪不一样** | 原先 `AskOutcome` / `AskResult` / 问题校验 / schema 构造全在 `Dougong.Acp` 里。现在语义层（`AskUser`）在 `Dougong.Agent`，ACP 与 REPL 各有一个工具类只做传输：`elicitation/create` 请求 vs 终端问答。 |
| **为什么必须下沉** | 同一个 `ask_user` 要服务两个宿主。语义绑在 ACP 上时，REPL 要么重写一遍校验（两份必然漂移，症状是"同一个问题在 ACP 里合法、在 REPL 里非法"），要么放弃。**校验规则属于模型的可观察行为**——它不该因客户端不同而不同。 |
| **REPL 版与 ACP 版的差别** | REPL 是**逐题顺序问答**，ACP 是完整表单。这不是省事：终端里同时展示 4 个问题并允许回头改，需要全屏 UI；而逐题问答在管道/重定向下也能工作。 |
| **四态结果** | 答了 / 明确拒绝 / 取消 / 不可用，必须分开。把 EOF 或取消报成"用户拒绝"会让模型以为方案被否决，而用户可能压根没看到问题（Cursor 踩过这个坑）。 |
| **不合法就不打扰** | 形状校验在**传输之前**做。检查方式是"读取器有没有被调用过"而不是"结果对不对"——`ask-invalid-shape-does-not-ask-user` 就是这么钉的。 |
| **诊断要引导而非只报错** | 选项少于 2 个时，诊断明确写"不要用凑数选项填充"。只报"至少 2 个"会让模型编一个假选项塞进去，那比拒绝更糟。（这条引导语在重构时丢过一次，被 `elicitation` 套件的 `invalid-shape-sends-nothing` 抓到。） |
| **公开 API 的迁移** | `Dougong.Acp.AskOutcome` / `AskResult` / `AskAnswer` / `MaxOptions` / `MaxQuestions` 保留为 `[Obsolete]` 别名，值转发到 Agent 层。直接删会让下游编译失败（ApiCompat CP0001/CP0002）——0.x 不做兼容承诺，但**变更必须可见**，不是静默破坏。 |
| **部分答案要留下** | 多问题中途 EOF 时，已答的答案留在结果里且诊断写明"已答 N 个"。只报"输入已结束"会让模型以为一个都没拿到，从而全部重问——用户刚答的那部分白费。 |
| **验证** | `ask-user` 套 11 项；5 组负控见 §三。另有既有的 `elicitation` / `elicitation-process` 套件继续覆盖 ACP 侧。 |
| **源码位置** | `src/Dougong.Agent/AskUser.cs`、`src/Dougong.Acp/AcpElicitationTool.cs`、`src/Dougong.Cli/ConsoleElicitationTool.cs` |

### D-17 只做抓取不做搜索；http/https 守卫不是安全边界

| | |
|---|---|
| **哪不一样** | dsh 的 `dsh-web` 是 seam，下有 `web-fetch-http` 与三个搜索后端（exa / perplexity / deepseek）。我们只有 `WebFetchTool`，没有搜索。 |
| **为什么先做抓取** | 抓取只需要 HTTP，不需要第三方账号与计费。而"没有搜索"可以用抓取 + 已知文档站弥补，反过来不成立——有搜索但不会抓取，拿到链接还是读不了正文。 |
| **默认关闭** | 它是**唯一会主动向外发请求的工具**。"我的 agent 会不会把内部 URL 发出去"该由用户决定，不该由 SDK 替他决定。`DOUGONG_WEB_FETCH=1` 打开。 |
| **不执行 JavaScript** | 这是文本抓取，不是浏览器。脚本渲染的页面拿到空壳，而**工具必须如实说明**——否则模型会断定页面真的没内容，然后放弃。 |
| **http/https 守卫不是安全边界** | <c>HttpClient</c> 本身对 <c>file:</c>/<c>ftp:</c> 抛 <c>NotSupportedException</c>（实测确认）。这道检查的增量价值是**给出能照做的错误消息**（"本地文件请用 read_file"），不是拦住本可能成功的越权。真实边界在内核与审批：抓取报 `ToolAccess.Network` + 目标域名。**第一版注释把守卫写成了安全机制，已更正**——把错误消息当防线会让后来的人误以为它在防什么。 |
| **权限声明要准确** | 报 `None` 会让"允许联网"的开关形同虚设；报空域名列表会让审批界面看起来像内网请求。URL 解析不了时报 `Network` 但标 `IsKnown=false`。 |
| **边读边限** | 下载上限 5 MB 在读的时候限，不是读完全量再判——后者会让一个畸形大响应先吃满内存。 |
| **正则不能用 NonBacktracking** | 删 `<script>...</script>` 的模式用了反向引用（``），而 `NonBacktracking` 明确不支持它：**一跑就抛 `NotSupportedException`**。改用显式超时防 ReDoS（输入是任意网页）。这个崩溃只在真实抓取时才暴露。 |
| **验证** | `web-fetch` 套 9 项；4 组负控见 §三。 |
| **源码位置** | `src/Dougong.Agent/WebFetchTool.cs`、`src/Dougong.Cli/SessionComposition.cs` |

### D-18 改动清单从日志推导，不做指纹快照

| | |
|---|---|
| **哪不一样** | dsh 的 `ctx.workspaceChanges` 在每轮开始/结束各做一次文件指纹快照再求差。我们直接**从会话日志的写工具参数推导**：`write_file` / `edit_file` 的 `path` 已经落盘了。 |
| **为什么不快照** | 三条：① **代价**——每轮前后各扫一次工作区，大仓库里"看一眼改了哪些文件"不该比一次工具调用还贵；② **信息更少**——快照只说"这个文件变了"，日志还能说"哪一轮、哪个工具、改了几次"；③ **日志已经在**——dsh 那样做是因为它的日志只记轮次不记文件。 |
| **盲区必须承认** | ① **shell 改了文件看不到**——命令是自由文本，看不出它动了什么；② **手动改动看不到**——用户在编辑器里改的，日志里没有；③ **失败的工具调用可能被算成改动**——`ToolCallStarted` 是"即将执行"，此时还不知道成败。前两条正是 dsh 用快照的理由，我们的取舍是"便宜且够用"，而这个代价在 CLI 输出里明说（"shell 命令与手动改动不在其中"）。 |
| **只认写工具白名单** | 按"有没有 `path` 参数"判断会把 `read_file` / `glob` / `grep` 全算成改动。实测负控确认：去掉白名单后清单里出现 `read.txt`、`**/*.cs`、`pattern`。 |
| **第三项取舍偏保守** | 失败的工具调用可能被多报。选择"可能多报"是因为**漏报比多报危险**：用户看到多一个文件会去核对，看不到漏掉的文件则完全不知情。 |
| **没有 `present` 工具** | dsh 的 `dsh-tool-present` 让模型主动声明"这是交付物"，需要它判断什么算交付物。我们的清单是机械推导的，不替模型做那个判断。这一项因此在进度表里是 🟡。 |
| **验证** | `workspace-changes` 套 8 项；3 组负控见 §三。 |
| **源码位置** | `src/Dougong.Agent/WorkspaceChanges.cs`、`src/Dougong.Cli/Program.cs` |

## 五、这份台账的边界

**它不保证正确，只保证"声称与证据对得上"。**

一条检查可能钉错了东西（断言了实现细节而非行为）。检查写错时台账会跟着错，
而它看起来依然权威——这与偏离过期的风险是同一个。

缓解手段只有两个，都需要人：

1. **负控必须真做**。检查跑绿不代表它在验证；故意破坏后仍绿说明它没在验证。
2. **写检查时问自己：这条断言的是行为，还是实现？**
   断言"这个字段存在"容易被重构打破，断言"缺字段时反序列化失败"才是行为。

---

## 六、维护

- **只随能力状态变化而改**，不按时间刷。
- 改 `dsh-capability-progress.md` 的状态时，**同一个提交**里改这里。
- §四 的偏离记录被推翻时，**删掉而不是标注"已废弃"**：
  一条过时的偏离记录会误导下一个人，而删除是明确的。
