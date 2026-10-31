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
| 工具卡住不会挂住整轮 | `timeout`：`hanging-tool-times-out`、`timeout-is-not-reported-as-cancellation`、`timeout-counts-as-executed`、`per-tool-timeout-overrides-default`、`outer-cancel-still-wins`、`new-outcome-does-not-shift-existing-values` | 把 `Executed` 里的 `or TimedOut` 删掉 → `timeout-counts-as-executed` 失败；把 `catch when (!cancellationToken.IsCancellationRequested)` 的守卫去掉 → `outer-cancel-still-wins` 失败 |
| shell 拦常见越界且承认不是沙箱 | `shell-policy`：`destructive-commands-are-rejected`、`credential-reads-are-rejected`、`cd-outside-workspace-is-rejected`、`rejection-message-admits-it-is-not-a-sandbox`、`read-only-classification-rejects-redirection`、`shell-tool-reports-rejection-as-result` | 把 `deny(escape)` 改成 `allow(escape)` → `cd-outside-workspace-is-rejected` 失败；把拒绝消息改成只写"命令被拒绝" → `rejection-message-admits-it-is-not-a-sandbox` 失败 |
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
| **为什么先这样做** | OS 级隔离每平台一套实现、各自需要验证环境；本机只有 win-x64，无法验证另外两个平台。而**绝大多数越界不是攻击，是误操作**——模型写 `cd / && ls` 或 `rm -rf /tmp/*`，这些模式识别就能拦。真隔离作为 P1 单独一轮。 |
| **方向** | 拦得住常见误操作，**拦不住有意绕过**（`find / -name x`、`cat $(...)`、脚本内 cd）。所以拒绝消息里必须写明"这是策略过滤而非沙箱"——宣称已沙箱而只是模式匹配，会让用户放松审查，比不做更危险。 |
| **前提 / 何时重考虑** | 前提是"使用者知道它不是沙箱"这一条能靠文档与拒绝消息传达。若要对外宣称"可安全让 AI 跑任意命令"，必须先有 OS 级隔离。 |
| **验证** | `shell-policy` 9 项，其中 `rejection-message-admits-it-is-not-a-sandbox` 专门钉住"承认边界"这一点。 |
| **源码位置** | `src/Dougong.Agent/ShellCommandPolicy.cs` |

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
