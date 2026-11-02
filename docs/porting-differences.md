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
| 后台作业游标与环形缓冲守恒 | `jobs`：`job-start-returns-without-waiting`、`job-output-respects-cursor`、`job-output-reports-discarded-bytes`、`job-caps-concurrency-per-owner`、`job-limit-is-per-owner-not-global`、`job-unknown-id-is-not-silent`、`job-registry-dispose-kills-children`、`job-tool-declares-execute-permission`、`job-stop-kills-grandchildren-on-posix`、`job-finished-output-survives-until-read` | `OmittedBytes` 恒 0 → `job-output-reports-discarded-bytes` 报"总输出 7200 字节却报 omitted=0"；并发上限改全局 → `job-limit-is-per-owner-not-global` 失败 |
| 持久 shell 的状态与输出干净度 | `persistent-shell`：`shell-keeps-cwd-across-commands`、`shell-exit-code-is-this-command`、`shell-failure-does-not-break-session`、`shell-output-has-no-prompt-noise`、`shell-reports-current-directory`、`shell-times-out-instead-of-hanging`、`shell-dispose-terminates-process`、`shell-reuses-one-process`、`shell-recovers-after-timeout`、`shell-output-has-no-leading-blank` | 每条命令重建进程 → 3 项同时失败（cd 失效、目录报错、变量丢失） |
| 计划审阅挡住执行且批准后可继续 | `plan-mode`：`plan-approval-continues-same-turn`、`plan-approval-exits-plan-mode`、`plan-rejection-keeps-plan-mode`、`plan-cancel-is-distinct-from-reject`、`plan-requires-markdown-title`、`plan-rejects-oversized-document`、`plan-mode-blocks-write-tools`、`plan-mode-allows-read-tools`、`plan-mode-off-executes-normally`、`plan-section-only-when-active`、`plan-intent-carries-approve-label`、`exit-plan-mode-rejected-outside-plan-mode`、`plan-mode-whitelist-matches-real-tools` | 批准后不 `Disable` → `plan-approval-exits-plan-mode` 报"后续写操作会被误拦"；取消报成 `PlanRejected` → `plan-cancel-is-distinct-from-reject` 失败 |
| 权限预设真的改变"要不要问"，且不越权放行 | `permission-preset`：`preset-interactive-asks-on-risk-only`、`preset-unattended-never-asks`、`preset-switch-changes-observable-behavior`、`preset-does-not-override-inner-rejection`、`preset-ask-always-asks-even-for-inert`、`preset-unknown-key-throws-loudly`、`preset-custom-key-is-derived-not-selectable`、`preset-table-validates-at-construction`、`preset-descriptions-admit-no-sandbox`、`preset-is-wired-into-cli` | 预设改成永远转内层 → 3 项红（`preset-unattended-never-asks` 报"仍问了 2 次"、`preset-switch-changes-observable-behavior` 报"切到无人值守后仍问了"）；预设一律放行 → 4 项红（含 `preset-does-not-override-inner-rejection` 报"内层拒绝被预设放行了"）；未知键静默无操作 → `preset-unknown-key-throws-loudly` 报"没有抛异常"；`custom` 标记为可选 → `preset-custom-key-is-derived-not-selectable` 失败；删掉"不含沙箱"描述 → `preset-descriptions-admit-no-sandbox` 失败。**注意**：第一版用 `if (true)` 破坏触发 CS0162、编译不过、跑旧 DLL 假绿——改用 `Environment.TickCount` 才有效 |
| 重复调用按连续相同计数、透明工具不打断、提醒真的到达 | `repeat-reminder`：`repeat-fires-at-each-threshold`、`repeat-needs-identical-arguments`、`repeat-chain-resets-on-other-tool`、`repeat-transparent-tools-neither-count-nor-reset`、`repeat-preview-is-capped-but-detection-is-not`、`repeat-reset-clears-chain`、`repeat-rejects-invalid-thresholds-loudly`、`repeat-renders-actionable-text`、`repeat-reminder-actually-reaches-next-request`、`repeat-does-not-block-and-is-wired` | 键改成不含参数 → 2 项红（`repeat-needs-identical-arguments` 报"交替参数被算成重复"、预览截断那条报"长参数被误判"）；透明工具按普通工具处理 → `repeat-transparent-tools-neither-count-nor-reset` 报"计数变成 1（应为 2）"；删掉阈值校验 → `repeat-rejects-invalid-thresholds-loudly` 报"空阈值列表没有报错"；`Reset` 不清链 → `repeat-reset-clears-chain` 报"重置后计数应为 0，实际 2"；把 `Observe` 改成永不成立 → `repeat-reminder-actually-reaches-next-request` 报"共 4 次请求，没有任何一次含提醒"（第一版只做字符串检查时这组破坏全绿，是补了行为检查才抓到的） |
| 拦截点真的阻止执行且不让规则失败变成放行 | `intercept`：`intercept-approve-runs-tool`、`intercept-deny-blocks-execution`、`intercept-deny-skips-approval`、`intercept-strictest-wins`、`intercept-stops-at-first-deny`、`intercept-ask-without-approver-is-denied`、`intercept-ask-reaches-approver-with-reason`、`intercept-throwing-interceptor-denies`、`intercept-rejection-is-not-executed`、`intercept-no-interceptors-is-distinguishable` | Deny 改成"只记不拦" → 4 项同时红（含 `intercept-deny-blocks-execution` 报"实际 Succeeded"与 `intercept-deny-skips-approval` 报"仍然问了审批 1 次"）；Ask 无审批器改放行 → 2 项红（fail-open）；Ask 理由丢掉 → `intercept-ask-reaches-approver-with-reason` 报"审批请求里没有拦截理由"；不短路 → `intercept-stops-at-first-deny` 报"仍调用了后续拦截器"；抛异常改放行 → `intercept-throwing-interceptor-denies` 报"实际 Succeeded"；把拦截拒绝算进 `Executed` → `intercept-rejection-is-not-executed` 报"被算成了已执行" |
| 技能按需加载且可见性开关两处都生效 | `skills`：`skill-discovers-bundle-and-flat-forms`、`skill-ignores-non-kebab-directories`、`skill-project-wins-over-user-and-records-shadow`、`skill-description-is-truncated-with-ellipsis`、`skill-missing-description-still-listed`、`skill-frontmatter-is-stripped-from-body`、`skill-load-rereads-the-file`、`skill-disable-model-invocation-hides-and-blocks`、`skill-tool-rejects-bad-and-unknown-names`、`skill-catalog-text-escapes-and-omits-paths`、`skill-is-wired-into-cli-composition` | 去掉两处可见性过滤 → `skill-disable-model-invocation-hides-and-blocks` 报"不可调用的技能出现在模型目录里"；只去掉工具侧 → 报"工具仍然加载了不可调用的技能——开关等于没做"（证明两处都要查）；同名改后到覆盖 → `skill-project-wins-over-user-and-records-shadow` 报"项目级没有优先"；加载加缓存 → `skill-load-rereads-the-file` 报"有缓存，用户改了文件不生效"；不转义 → `skill-catalog-text-escapes-and-omits-paths` 报"描述没有被转义"；不筛名字 → `skill-ignores-non-kebab-directories` 报 `Bad_Name` 进了目录 |
| 换目录只动相对路径起点，不动权限边界 | `working-directory`：`cwd-relative-paths-follow-current-directory`、`cwd-changes-once-than-rejects-missing-directory`、`cwd-project-boundary-is-not-moved`、`cwd-change-survives-new-store-instance`、`cwd-disappeared-directory-falls-back-to-project`、`cwd-tool-reads-when-cd-omitted`、`cwd-tool-rejects-nonexistent-directory`、`cwd-tool-does-not-persist-when-write-fails`、`cwd-existing-shell-keeps-its-own-directory`、`cwd-context-line-mentions-both-when-outside`、`cwd-is-wired-into-cli-composition` | 删掉工具里的 `_files?.UseDirectory` → `cwd-relative-paths-follow-current-directory` 报"工具没让文件系统跟着换根"（第一版检查自己调 `UseDirectory`，删了仍绿，已修）；让 `Project` 跟着 cd 变 → `cwd-project-boundary-is-not-moved` 报"守卫的根不是原始项目"（第一版用上级目录验，对缺陷不敏感，已修）；去掉退回原始项目 → `cwd-disappeared-directory-falls-back-to-project` 报"没有标记为回退"；落盘失败不回滚 → `cwd-tool-does-not-persist-when-write-fails` 报"目录没回退"；装配里删掉工具注册 → `cwd-is-wired-into-cli-composition` 报"没有注册"；两处 `Directory = _directory` 都去掉 → 报"运行时没有拿到目录" |
| 消息反馈不进会话日志、按版本守卫 | `feedback`：`feedback-put-then-list-roundtrips`、`feedback-put-requires-observed-version`、`feedback-conflict-returns-authoritative-item`、`feedback-noop-keeps-version-and-appends-nothing`、`feedback-delete-is-idempotent-and-tombstoned`、`feedback-delete-respects-version`、`feedback-rejects-blank-and-oversized-note`、`feedback-file-is-append-only-never-rewritten`、`feedback-never-touches-session-log`、`feedback-sidecar-is-not-a-session-file`、`feedback-tail-corruption-does-not-hide-earlier-items`、`feedback-middle-corruption-is-an-error`、`feedback-target-must-be-model-response` | 去掉 `ifVersion` 比较 → 2 项失败（陈旧版本被当成成功覆盖）；删除改成重写文件抹行 → `feedback-delete-is-idempotent-and-tombstoned` 报"删除没留下记录"；后缀改成 `.jsonl` → `feedback-sidecar-is-not-a-session-file` 报"反馈文件被列成了会话"（`1_session.jsonl.jsonl`）；去掉 no-op 短路 → 报"无变化的写入不该换版本"；备注上限按字符算 → `feedback-rejects-blank-and-oversized-note` 报"超长备注被接受"；中间坏行改成静默跳过 → `feedback-middle-corruption-is-an-error` 失败 |
| 文件补全只给路径且排序确定 | `file-references`：`refs-returns-relative-slash-paths`、`refs-match-is-substring-and-case-insensitive`、`refs-order-is-stable`、`refs-prefers-shallow-paths`、`refs-skips-noise-directories`、`refs-reports-total-and-truncation`、`refs-does-not-read-file-contents`、`refs-empty-query-lists-all` | 去掉 `Sort` → `refs-order-is-stable` 报"zz.cs 在 aa.cs 之前"；不排除噪声目录 → `refs-skips-noise-directories` 报"候选里出现噪声目录"；总数报成返回数 → `refs-reports-total-and-truncation` 报"总数应为 6，实际 2" |
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

### D-19 作业活在进程内，进程树结束未完全验证

| | |
|---|---|
| **哪不一样** | dsh 的 `dsh-jobs-local` 是进程内注册表 + 环形缓冲 + 游标；我们把同样的契约实现在 `JobRegistry`，但**不持久化**，且进程树结束只有代码保证、没有检查钉住。 |
| **为什么与 `IShellExecutor` 并列而非继承** | 那个接口的类型注释明确只描述"起一个进程并等它结束"。作业的形态是"起进程、立刻返回、之后多次读"——硬合成一个接口会让"换沙箱实现"变成"换语义"，那不是替换。 |
| **四个工具而不是一个带 mode** | 模型靠工具名与描述选择。`job(mode=read)` 会让它频繁选错 mode，而每次选错浪费一整轮。dsh 的 `dsh-tool-jobs` 也是动作分工具。 |
| **并发上限按 owner 且拒绝而非排队** | 排队会让模型以为已启动，而它可能几分钟后才真正开始——那时它已经基于错误假设继续做事。拒绝让它立刻知道要等或要停。 |
| **环形缓冲必须报丢弃字节** | 只报"还剩多少"会让模型以为那就是全部输出。检查用守恒判据：`total == 保留 + 丢弃`，同时证明两边统计一致。 |
| **进程树结束：诚实降级** | 代码里显式传 `Kill(entireProcessTree: true)`，但**没有能在 Windows 上稳定验证它的检查**。试过两条路：`cmd /c ping` 不产生独立子进程；`start /b` 产生的是**有意分离**的进程（kill tree 本就不该管）。两种形态数进程的结果一样，无法区分 true 与 false。所以检查改名为 `job-stop-reaches-finished-state`（只验"停了"），另加只在 POSIX 跑的 `job-stop-kills-grandchildren-on-posix`（用心跳文件观察子进程是否还活着）。**把"已验证"写成结论就是假声称**，这一栏如实记录。 |
| **不做持久化** | 作业活在进程内，进程退出就没了。理由：把「进程内句柄」落盘再恢复是不可能的——重启后那些子进程已经不存在。这点写进工具描述，因为模型会合理假设"我启动的东西还在"。 |
| **验证** | `jobs` 套 12 项；2 组负控见 §三。其中进程树那条在 Windows 上跳过、在 Linux/CI 上真跑。 |
| **源码位置** | `src/Dougong.Agent/JobRegistry.cs`、`src/Dougong.Agent/JobTools.cs` |

### D-20 持久 shell 用管道，不做 PTY

| | |
|---|---|
| **哪不一样** | dsh 的 `dsh-terminal-bash` 是真 PTY（ConPTY / forkpty），带窗口大小、信号、作业控制。我们用普通管道，只保证"状态跨命令保留"。 |
| **为什么模型不需要 PTY** | 模型不渲染。它要的是"先 cd 再执行"这类状态保留，不需要光标、ANSI 屏幕控制、IME。PTY 是给**终端 UI** 的——桌面端那个标签页要跑 vim、top，那是另一件事。 |
| **因此两项分开** | 序号 21 记的是"持久 shell / PTY"两件事。**shell 部分已完成**（`PersistentShell` + `shell_session` 工具）；PTY 仍未做，等桌面端 P8f。把两者算成一项会让"已完成"看起来比实际多。 |
| **管道模式的真实代价** | 没有作业控制：`vim`/`top`/`less` 会挂住或行为异常，`Ctrl+C` 语义无法传递，部分工具会因无 TTY 切到非交互模式（通常好，但不总是）。这些写进工具描述，否则模型会拿它跑交互式命令然后卡在超时上。 |
| **怎么判断命令结束** | 管道模式没有提示符可解析。做法是**分两行发**：第一行命令，第二行 `echo <哨兵>:$?`（Windows 用 `!errorlevel!` + `/V:ON`）。顺序不能反，也不能合成一行。 |
| **踩过的三个坑** | ① `prompt $` 写在 `_stdin` 赋值之前 → 从未生效，提示符混进输出；② Drain 线程在发送之后才启动 → 哨兵永远等不到，实测首条命令耗时 3049ms（修好后 75ms）；③ `WaitForBarrier` 截断后又多了一次 `Clear` → 把哨兵之后的输出也丢了。 |
| **超时必须重启 shell（复核修的）** | 第一版超时后不清残留，于是**后续每条命令都返回同一条超时消息**——会话看起来"坏了"，用户唯一能做的重启整个程序。现在超时即 `Restart()`，诊断里说明 cd/export 状态已丢。代价是有，但比"永久不可用"好得多。 |
| **一处没能验证的断言** | 实现用 `!errorlevel!` 并开 `/V:ON`，但**检查抓不到它与 `%errorlevel%` 的差别**：实测两者分两行发时行为相同（`%errorlevel%` 在每行新输入时解析，正好取到上一行）。差别只在同一行用 `&` 串联时出现。所以这个决定是对的（防将来改回单行串联），但**检查不证明它必要**——如实记录，不算已验证。 |
| **验证** | `persistent-shell` 套 10 项；2 组负控见 §三。 |
| **源码位置** | `src/Dougong.Agent/PersistentShell.cs`、`src/Dougong.Agent/PersistentShellTool.cs` |

### D-21 计划模式照 dsh 实现：专用工具 + 阻塞审阅

| | |
|---|---|
| **哪不一样（第一版错在哪）** | 第一版复用了 `todo_write` 提交条目列表，并以 `PlanAwaitingReview` **结束本轮**。读过 dsh 源码后发现三处实质性偏离——`todo_write` 是持续维护的进度清单，而计划是**一次性提交的文档**；条目列表装不下"为什么这么做"；结束本轮让用户批准后还要重新起轮。 |
| **dsh 的做法** | `exit_plan_mode` 工具，参数 `{ plan: "<完整 Markdown>" }`；工具**内部阻塞**等审阅；批准后返回 `"Plan approved"`，模型**在同一轮继续**。计划地址是 `dsh-resource://plan/<session>/<callId>`，可存进侧边栏并重新打开。 |
| **为什么专用工具而不是复用 todo_write** | `todo_write` 可调多次、语义是"更新进度"；`exit_plan_mode` 是一次性动作"提交文档、请求退出"。复用会让模型分不清该在什么时候停。 |
| **为什么是完整 Markdown 而不是条目列表** | 计划要说明**为什么这么做、有什么风险**，条目列表装不下。dsh 的 e2e 明确断言审阅卡片 `getByRole('list').count()` 为 0——只显示标题 + "查看完整计划"。 |
| **为什么阻塞而不是结束本轮** | 结束本轮后用户批准，模型要重新起轮；阻塞让批准直接变成工具结果，模型不必猜"用户那句'开始吧'是不是批准"。判据是"模型被调了第二次"，不只是结局。 |
| **走 AskUser 通道带 intent** | dsh 用 `userQuestions.ask` + `intent: { kind: 'plan-review', approve: 'Approve' }`，且明确要求"plan-review 请求**不能**落到通用问题流"。我们扩展 `AskedQuestion` 加 `Detail`（完整文档）与 `Intent`（类别 + 批准选项），并把传输抽成 `IAskUserHost`——ACP 与 REPL 各一份。 |
| **`intent` 用数据不用类型** | 客户端的槽位要能按 intent 分发；"多态问题类型"会让宿主必须为每种类型实现渲染器。 |
| **批准选项用文本不用序号** | 序号在不同渲染顺序下不稳定，而选项文本是用户看到并点的那个。 |
| **标题必须存在** | 客户端用 `# 标题` 显示"这份计划关于什么"。缺标题时拒收，且**不发起审阅**（不打扰用户）。 |
| **取消 ≠ 拒绝** | 取消意味着用户没看到或被打断；拒绝意味着方案被否决。前者该重试，后者该改方案。分别报 `PlanCancelled` / `PlanRejected`。 |
| **计划模式下拦非只读工具** | 白名单（`PlanModePolicy.AllowedTools`）而不是黑名单：黑名单让新增工具默认放行，那是最危险的方向。 |
| **验证** | `plan-mode` 套 13 项；2 组负控见 §三。 |
| **源码位置** | `src/Dougong.Agent/PlanMode.cs`、`src/Dougong.Cli/ConsoleElicitationTool.cs`（`ConsoleAskUserHost`） |

### D-22 P2 复核修掉的三个真缺陷

复核 P2 三项实现时用探针找到的问题，记录在此避免重犯。

| 项 | 缺陷 | 现在由哪条检查钉住 |
|---|---|---|
| 计划模式 | **非计划模式下调用 `exit_plan_mode` 仍弹审阅卡片**，批准后返回 `PlanApproved`——模型以为自己在按计划执行，而它从没进入过计划模式 | `exit-plan-mode-rejected-outside-plan-mode` |
| 计划模式白名单 | **`ask_user` 漏了**。它零副作用，而计划阶段最需要澄清；不放行会让模型只能猜需求 | `plan-mode-whitelist-matches-real-tools` |
| 后台作业 | **已结束但未读的作业被清掉**。`PruneFinished` 用"游标 >= 总写出"判断"读完"，而没有输出的作业两者都是 0，于是刚结束就被删——`Read` 返回 NotFound，用户永远看不到它 | `job-finished-output-survives-until-read` |
| 持久 shell | **超时后永久不可用**。残留输出污染后续每条命令，全都返回同一条超时消息，用户唯一能做的重启整个程序 | `shell-recovers-after-timeout` |
| 持久 shell | 超时标志恒为 `false`，模型无法区分"超时"与"普通失败" | 同上（断言 `TimedOut`） |
| 持久 shell | 输出以空行开头：启动屏障的 CRLF 被只按 LF 截断 | `shell-output-has-no-leading-blank` |

**两条经验**：

1. **"状态标志"的写入点要数清**。作业那条缺陷的根因是 `EverRead` 由 `Snapshot` 设置，而 `Start` 的返回也调 `Snapshot`——**作业刚启动就被标成"读过"**。修法是把标记改成 `Snapshot` 的参数，只让真正的读取传 `true`。
2. **失败路径要验"之后还能用"**。持久 shell 超时那条在原检查里只验了"超时能返回"，没验"超时后还能执行"——而后者才是用户真正会遇到的场景。

### D-23 文件引用补全只给路径，不建索引

| | |
|---|---|
| **哪不一样** | dsh 的 `file-reference-local` 有 `maxEntries`（索引上限），说明它建了索引。我们每次遍历，不建。 |
| **契约一致的部分** | dsh 原文："path-only completion candidates within an Agent cwd; providers own namespace access and ranking **without reading file contents**"。三条我们都照做：只返回相对路径、不越工作区、排序由实现负责。 |
| **"不读内容"是硬要求** | 补全是"你想引用哪个文件"，不是"这个文件里有什么"。读内容会让一次按键触发几十次磁盘 IO。检查用一个"读到就记录"的 `IFileSystem` 装饰器验证——`refs-does-not-read-file-contents`。 |
| **为什么不建索引** | 工作区通常几千个文件，一次遍历毫秒级；而索引要解决失效（文件增删改后何时重建），那是持续成本。等真出现"补全卡顿"的反馈再做。但**必须有 `maxEntries`**：仓库根目录无界遍历会扫进十万级目录。 |
| **排序规则** | 深度 → 长度 → 字典序。意图：浅层文件更常用；同深度时短的更贴近；字典序兜底保证确定性（否则用户按 Tab 会发现候选在跳）。 |
| **检查曾空转过一次** | 第一版 `refs-order-is-stable` 只验"两次查询结果相同"，而**这台机器上文件系统的枚举顺序恰好有序**——去掉 `Sort` 它照样通过。改成注入"按倒序返回目录项"的装饰器 + 直接断言排序规则后，负控才真的会红。这是"检查本身也要被验证"的又一例。 |
| **验证** | `file-references` 套 8 项；3 组负控见 §三。 |
| **源码位置** | `src/Dougong.Agent/FileReferenceIndex.cs` |

### D-24 消息反馈存在会话旁的伴生文件，不写进权威日志

| | |
|---|---|
| **哪不一样** | dsh 把消息评价写进权威 Session 日志（`feedback/message-put` / `feedback/message-delete`），由投影派生当前值。我们写进会话文件旁的 `<session>.feedback`，会话日志一个字节不动。 |
| **为什么不跟：加记录类型会让旧程序读不了整个会话** | `JsonlAgentStore.ReadCommittedAsync` 用 `Enum.Parse<StoreRecordKind>(parsed.Kind)` 还原记录。遇到新名字时它**抛 `ArgumentException`**，不是跳过那一条——**整个会话打不开**。而与 D-15（标题不写进日志）相比，这里的后果更严重：标题只是显示不出来，这里是历史全部读不了。 |
| **dsh 为什么可以** | 它有成套的事件词汇、投影层、按 Session 划分的变更队列和 `persistence-catalog` 的版本清单来支撑"日志里多一种事件"。我们没有投影层（#24 仍未做），而为一个评价功能先补一整套投影不划算。归档笔记里 dsh 自己也曾评估过 sidecar 方案，只在拿到那套支撑后才改为写日志——顺序不能反。 |
| **存哪** | `<session>.jsonl` → `<session>.jsonl.feedback`（单文件，与会话文件同目录、同名）。**后缀刻意不是 `.jsonl`**：会话目录按 `*.jsonl` 枚举（`SessionBranch.List`、`DiagnosticBundle`），后缀相同会让评价文件被列成一个会话，打开时报"格式版本无效"。这条有检查钉住（`feedback-sidecar-is-not-a-session-file`）。 |
| **指谁：按日志序号，不按宿主条目 id** | 目标用 `StoreRecord.Sequence`——全仓库唯一跨进程稳定的标识。宿主的 `ConversationItem.Id` 不能用：桌面端它是 `item-<guid>`，每次进程启动重新生成，冷启动后对不上任何历史消息。这也让本能力与具体宿主解耦：SDK 只说"第几条记录"，谁渲染、用什么 id 是宿主自己的事。 |
| **并发语义照 dsh** | `ifVersion` 严格比较，no-op 也算冲突（否则陈旧的"我以为我是第一次改"会静默成功）；冲突回传权威条目，调用方不必再读一次；删除幂等、留墓碑；备注按 UTF-8 字节上限（默认 8192），空白串拒绝而不是当成"没有"。 |
| **同样 append-only** | 与主日志同一理由：修改要留痕。删除是墓碑，早期评分留在文件里——"用户改过主意"这件事因此可见，而原地覆盖会让它彻底消失。尾部坏行只截断并标记 `Truncated`；**中间坏行抛异常**（append-only 文件的坏行只可能在末尾，中间出现说明文件被外部改过）。 |
| **单写者，跨进程不保证** | 与 `JsonlAgentStore` 同口径：同一会话由调用方保证只有一个写入者。跨进程靠文件锁是不够的——两个进程都读到旧版本、都认为自己是第一次改，版本校验就失去意义。 |
| **不做 UI、不做投影** | SDK 不定义按钮、弹窗或 toast，也不把评价推给模型（结构上保证：本能力的类型不被 `LoopAgentRuntime` 读取）。宿主自己读、自己渲染。 |
| **验证** | `feedback` 套 13 项；6 组负控见 §三。其中 `feedback-never-touches-session-log` 专门守住"绝不写进日志"这条核心约束。 |
| **源码位置** | `src/Dougong.Agent/MessageFeedback.cs`、`src/Dougong.Agent/JsonlMessageFeedbackStore.cs` |

### D-25 换工作目录不动原始项目边界，变更存伴生文件

| | |
|---|---|
| **哪不一样** | dsh 把目录变更作为日志事件 `working-directory/change`（`{cwd}`）；我们存会话旁的 `<session>.jsonl.cwd`，理由与 D-24 相同——加记录类型会让旧程序的 `Enum.Parse<StoreRecordKind>` 抛异常。 |
| **三条语义照 dsh，缺一条就会出错** | ①**原始项目 vs 有效目录分开**：守卫的根永远是会话头里的项目，只有相对路径的起点跟着变。若把守卫的根也换掉，模型在子目录里 `cd ..` 就出了项目，而那不会报错。②**已有进程保持自己的目录**：持久 shell 与后台作业不跟着换——它们里面可能已经 `cd` 过。③**目录变更要能重算**：从伴生文件恢复，否则重开会话后模型按错的目录拼路径。 |
| **`working_directory` 工具的形态** | 一个可选参数 `cd`：不传 = 读当前目录，传了 = 换过去。照 dsh 的 `tool-working-directory`。用可选参数而不是两个工具——读与写是同一事实的两种动作，拆开会让模型在"只想确认一下"时多一次决策。 |
| **模型必须知道自己在哪** | 目录作为 user 角色的一行读数每轮注入（与时间上下文同一形态，不进系统提示）。**没有最小间隔**：目录只在被显式切换时才变，而它一变就必须立刻让模型知道——一条过时的目录读数比没有更糟。dsh 对此的要求是"必需的用户上下文快照，即使可选运行时上下文被禁用"。 |
| **换目录不等于换权限** | 换成项目外的目录是允许的（"去那里看东西"是真实需求），但写权限边界不动。工具结果里明说这一点，否则模型会以为换目录等于能写任何地方。 |
| **恢复时目录已消失** | 退回原始项目并记录原因，而不是报错。一个指向已删目录的会话会让每次相对路径操作都失败，而用户看不出原因。原始项目也不存在时才如实失败。 |
| **落盘失败要回退内存状态** | 先落盘、再换工具根。顺序反了的话，落盘失败时模型看到"成功"、重开会话后目录又回去了——期间读写的路径全错而没有任何提示。 |
| **检查曾两次空转（本次复现）** | ①`cwd-relative-paths-follow-current-directory` 第一版自己调 `UseDirectory`，于是删掉工具里那句之后仍全绿——它验的是测试自己的代码；改成经工具换目录才红。②`cwd-project-boundary-is-not-moved` 第一版用"项目的上级目录"验越界，而根换成 `sub` 之后那条路径同样被拒——对缺陷不敏感；改为直接断言 `guard.Root` 是原始项目才红。**两次都是靠负控发现的**，说明"破坏后仍绿"必须当成检查本身的结论来查。 |
| **验证** | `working-directory` 套 11 项；5 组负控见 §三。 |
| **源码位置** | `src/Dougong.Agent/WorkingDirectory.cs`、`src/Dougong.Agent/WorkingDirectoryTool.cs` |

### D-26 技能是"发现 + 按需加载"，不做分层注册表与热失效

| | |
|---|---|
| **哪不一样** | dsh 的 `ctx.skills` 是一个**分层注册表**：注册按调用上下文的 scope 归档、读时合并 global 与当前 scope 链、按 rank/provider 顺序/local 顺序决同名、用 Chokidar 盯目录变化并作废缓存。我们是 `SkillDiscovery` 一个具体类：每次调用重扫四个固定根、同名按 rank 先到先得。 |
| **为什么要分层的那套** | 因为 dsh 允许**插件**在运行时往注册表里加技能，而插件可以挂在 agent preset 的 scope 下——于是"这个会话能看到哪些技能"取决于它的装配链。我们还没有 agent preset，也没有插件注册技能的入口。**先补注册表等于为一个不存在的扩展点写基础设施**。 |
| **四个根照 rank 保留** | 100 `project-dsh`（`<项目>/.dougong/skills`）、200 `project-agents`（`<项目>/.agents/skills`）、400 `user-dsh`（`<dougong home>/skills`）、500 `user-agents`（`<agents home>/skills`）。**没有 300 `custom` 与 600 `bundled`**：前者要有配置键，后者要有打包产物，两者都还没出现。 |
| **影子链必须报出来** | 同名时低 rank 赢，但被遮住的那条要记下来（`ShadowedBy`）。不记的话，用户会问"我用户目录里那个为什么没生效"，而系统里没有任何地方能回答。dsh 的分层合并天然没有这个问题（它按层解释），我们没有层，就必须显式记。 |
| **只给名字与描述** | 模型目录里只有 `name` 与 `description`，**没有正文、没有绝对路径**——dsh 的原话是 "never the body or absolute file path"。给路径会把用户名带进对话并随日志外发；给正文会让一次目录注入变成几百 KB。 |
| **每轮重算目录，不做增量替换** | dsh 会对比会话日志里最近一条目录消息、只在变化时产出替换，且压缩之后要重新建立。我们每轮重算：扫描是个位数目录、每次只读 4 KiB 文件头。代价是列表不变时每轮多一条几十字节的消息——比维护"上次发的是哪一版"（还要处理压缩把那条消息压掉的情形）便宜。 |
| **可见性两处都要查** | `disable-model-invocation: true` 既过滤目录，也拦加载。**只做前者等于没做**：模型可以凭记忆拼出那个名字。负控专门验了这条——只修目录侧时检查会红在"工具仍然加载了不可调用的技能"。 |
| **缓存与热失效都不做** | 完整体每次重读（dsh 也明确说不缓存完整体），因为技能正文是用户正在编辑的文件，缓存会让"我改了但它还是照旧的来"。watcher 不做：它要处理"目录还不存在""在被删的瞬间"这些边界，而换来的只是省掉一次毫秒级扫描。 |
| **修掉的两个真缺陷** | ①空 frontmatter（`---
---`）时 `end == afterStart`，切片是逆序区间，直接抛 `ArgumentOutOfRangeException`——症状是"这个技能读不了"，看起来像文件损坏。②检查把平铺技能写进了 `<name>/<name>.md`，那个路径两种形态都不符合，检查在报一个不存在的缺陷。 |
| **验证** | `skills` 套 11 项；6 组负控见 §三。 |
| **源码位置** | `src/Dougong.Agent/SkillDiscovery.cs`、`src/Dougong.Agent/SkillTool.cs` |

### D-27 拦截钩子做类型化拦截点，不做 Claude Code / Codex 的 hooks.json 桥接

| | |
|---|---|
| **哪不一样** | dsh 的"拦截钩子"本体是**类型化拦截点**（`agent/pre-step`、`tools/pre-execute`、`tools/post-execute`、`agent/turn-stopping` 等），所谓 hooks 只是**普通 Cordis 插件订阅这些点**。它另外还有两个**桥接**包（`dsh-hooks-claude-code`、`dsh-hooks-codex`），把外部 `hooks.json` 里的 shell 命令翻译到那些点上。我们做的是前者，不做后者。 |
| **为什么不做桥接** | 桥接的存在理由是"用户带着既有的 CC/Codex 配置到来，希望它们原样运行"。我们还没有这样的用户，而桥接要付的代价不小：方言 payload 两种形状、匹配器的字面量/正则两种解释、`CLAUDE_PROJECT_DIR` 与 `${CLAUDE_PLUGIN_ROOT}` 替换、两类事件集不同（CC 七个 / Codex 五个）、退出码到决策的映射。**为一个没人用的兼容层写这些，是给未来留债**。 |
| **只做 `tools/pre-execute` 这一个点** | dsh 的完整拦截面还有 `agent/pre-step`、`tools/post-execute`、`agent/turn-stopping`、`subagent/start|end`。我们只做执行前的工具拦截，因为**它有明确的现有需求**（"不许删 migrations/""不许 git push"），而其余几个点要先有相应的接缝（pre-step 要能拦回合、turn-stopping 要能引导下一步）。dsh 的归档笔记里也写了 `continue:false`（硬停止）需要"拦截点尚无硬停止原语"，同样是先有接缝才有桥接。 |
| **三态而不是布尔** | `Approve` / `Ask` / `Deny`，因为"没人看"与"看过了、允许"在排查时是第一个要区分的问题。dsh 的合并优先级同样是 `deny > ask > allow`。 |
| **Ask 没有审批器时 fail-closed** | 与 `PermissionPolicyApprover` 同一判据。反过来做（没有审批器就放行）会让"装一个拦截器"变成一次**静默提权**——用户以为加了规则更安全，实际是更宽松。 |
| **在审批之前跑，且被拒时不产生审批项** | 让用户为一件注定被拒的事点两次头是净损失，而且会训练他们"闭眼批准"，那比不做审批更糟。 |
| **到第一个 Deny 就停** | 后续拦截器不会改变结局，而它们可能有代价（读文件、起进程）。dsh 的桥接刻意**串行**跑同一点的钩子（而非并发），理由同样是顺序确定性；我们的短路在此之上再加一层成本考虑。 |
| **拦截器抛异常按拒绝** | 放行等于让"规则坏了"变成"什么都允许"。 |
| **新增 `RejectedByInterceptor` 终态，与 `RejectedNotApproved` 分开** | 那个是"人没批准"，这个是"规则不允许"。合并会让用户以为"我批准了为什么还不执行"，而处置完全不同（前者去问人，后者改规则或改命令）。追加在枚举末尾——插在中间会让其后所有成员的数值 +1，而它们被序列化进日志。 |
| **不做入参重写（`updatedInput`）** | dsh 自己也把它列为推迟项：pre-execution 参数被 tool/call 审计、assistant/message 历史与工具展示共同读取，"诚实的重写是一个设计单元，而非一个字段"。我们照同样判断，不留半成品字段。 |
| **API 形态用可写属性而不是新构造参数** | `ToolDispatcher.Interceptors` 与 `ToolApprovalRequest.InterceptionReason` 都是 `get; set/init;`。加构造参数会改变公开签名，ApiCompat 报 CP0002，而本次改动没有"先标 Obsolete 再删"的余地。这与 `ToolDescriptor.MayHaveIrreversibleEffects` 的既有处理方式一致。 |
| **验证** | `intercept` 套 10 项；6 组负控见 §三。 |
| **源码位置** | `src/Dougong.Agent/IToolInterceptor.cs`、`src/Dougong.Agent/ToolDispatcher.cs` |

### D-28 重复调用只提醒不阻止；透明工具既不计数也不清零

| | |
|---|---|
| **哪不一样** | 行为主体一致：dsh 的 `dsh-repeat-tool-reminder` 也是**只提醒不阻止**——它的归档笔记专门写过"该插件向模型添加提醒，并不阻止工具调用，也不执行防护决策"。差别在配置面：它有 `include`/`exclude`/`argumentsPreviewChars` 三个配置项，我们只有阈值与预览上限两个参数，透明工具写死成固定集合。 |
| **为什么只提醒不阻止** | 确实需要重试的场景（等编译产物、轮询状态）会因阻止变成硬失败。而"卡在循环里"与"有意重试"的区别，模型比我们更清楚——给它一条提示比替它做决定合适。 |
| **必须按"连续"计数** | 中间换一个别的调用就清零。累计计数会把"隔很久又跑了一次"也算成重复，而那种情况没有循环风险。dsh 的原话是 "Consecutive-repeat counts"。 |
| **透明工具是这条能力最容易被做错的地方** | `todo_write` / `working_directory` / `skill` **既不计数也不清零**。若把它们当作"换了调用"，链就会频繁清零，而真正卡住的模型恰好会一边重复读文件一边更新 todo——于是永远达不到阈值。检查专门验了这条：透明工具之后的第 3 次相同调用必须仍然触发提醒。 |
| **判定用完整参数，展示用截断参数** | 这一点是**两件事**：链的键必须是完整规范化参数，否则"两个长命令只有末尾不同"会被误判成重复；提醒里引用的参数要截断（默认 500 字），否则一条提醒带着整段文件正文进下一轮请求。dsh 的注释把这条写得很清楚："the cap bounds the reminder, never the detection"。检查同时验了两侧。 |
| **阈值在构造期校验** | 空列表、小于 2、重复值都抛。静默回退会让配置看起来生效了，而实际跑的是默认值。dsh 同样在 load 期报错。 |
| **每轮清零** | 用户说"再跑一次"是一次新的开始，不该接着上一轮的计数。`RunAsync` 入口调 `Reset()`。 |
| **提醒攒到下一次请求再发** | 工具调用发生在一次请求之后，那时没有"当前请求"可以附加消息。留到下一轮是唯一时机，也够用——模型下一次决定要不要重复时就会看到它。发过即清空：同一条提醒重复出现在后续每轮会挤占预算。 |
| **修掉的一个真检查缺陷** | 第一版只有一条**字符串检查**（"运行时里有没有那段代码"）。把 `Observe` 的调用改成永不成立之后它照样全绿——字符串还在，提醒再也不会产生。补了一条真实运行的检查（构造 runtime、跑三次相同调用、断言提醒出现在后续请求的消息里），并验证了它在本缺陷存在时会红。这是本项目反复出现的同一类问题：**检查看源码而不是看行为**。 |
| **验证** | `repeat-reminder` 套 10 项；5 组负控见 §三。 |
| **源码位置** | `src/Dougong.Agent/RepeatToolReminder.cs`、`src/Dougong.Agent/LoopAgentRuntime.cs` |

### D-29 权限预设只打包审批一维，键名避开 dsh 的沙箱语义

| | |
|---|---|
| **哪不一样** | dsh 的预设是**两个开关的打包**：沙箱模式（`sandbox/mode`）与审批策略（`approval/policy`）。**我们没有沙箱**（项目明确不做 OS 级隔离，`dsh-capability-gap.md` 阶段 3），所以预设只打包审批策略这一维，两档：`interactive`（写操作与命令问、纯内存工具跳过）与 `unattended`（从不问）。 |
| **为什么键名不用 dsh 的** | dsh 的 `workspace-write` / `danger-full-access` 都在描述**沙箱范围**。我们没有那个东西——用这两个名字会让人以为文件真的被限在某个范围内，或者以为最宽松那档"什么限制都没有"（实际路径守卫与拦截器都还在）。**名字是承诺**。 |
| **描述必须承认这一点** | 两个预设的描述里分别写了"不含沙箱隔离"与"规则拦截仍然生效"。有一条检查专门盯这个：把这两句删掉它就红。理由是这个收窄是**安全方向上会误导人**的——用户会以为自己获得了文件隔离。 |
| **`custom` 是派生值，不是可选目标** | 照 dsh：当前策略不匹配任何具名预设时（宿主直接改了策略），当前值显示 `custom`，但它不出现在可选列表里。它是保留键，配置里不能用。 |
| **`Never` 档不妨碍拦截器** | 拦截器在审批**之前**跑，所以"不问人"不等于"什么都能做"。这条写在预设描述里，否则用户会以为无人值守档把规则也关了。 |
| **预设不覆盖内层的拒绝** | 它决定"问不问"，不决定"允不允许"。吞掉内层拒绝会让"选一个预设"变成"放宽权限"，而用户不会预期这件事。检查专门验了：内层拒绝时预设必须照旧返回拒绝。 |
| **默认档按入口能不能问人来定** | 控制台与 ACP 都有人答 → `AskOnRisk`；`AutoApproveApprover` 环境下没人答 → `Never`。默认值在装配里定，不让每个入口自己记着传——那种写法已经让 `PermissionPolicyApprover` 出现过一次"某个入口漏接"。 |
| **装饰顺序：预设在外层** | `PermissionPolicyApprover(new PresetApprover(...))`。反过来的话 `PermissionPolicyApprover` 会先放行，预设那一档永远看不到有风险的调用。焊接检查专门盯着这个顺序。 |
| **不持久化选择** | 重开会话回到默认档。那是安全的缺省——不会因为上次选了"无人值守"而这次悄悄不问了。dsh 会把选择记成 `permission/preset` 日志事件；我们不做（那要加记录类型，与 D-24 同一约束）。代价是"多对一时分不清用户选的是哪个"——`CurrentKey` 取声明在前的那个，是近似。 |
| **两次负控踩到同一个坑** | 第一次用 `if (true) return ...` 破坏，触发 **CS0162**（不可达代码），编译失败 → 跑的是旧 DLL → 检查假绿。改用 `if (Environment.TickCount >= 0)` 才得出有效结论。这与 HANDOFF §四之2 记的是同一个陷阱，**第二次又踩到**：破坏代码必须能编译。 |
| **验证** | `permission-preset` 套 10 项；5 组负控见 §三。 |
| **源码位置** | `src/Dougong.Agent/PermissionPresets.cs`、`src/Dougong.Cli/SessionComposition.cs`、`src/Dougong.Cli/Program.cs`（`/permission`） |

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
