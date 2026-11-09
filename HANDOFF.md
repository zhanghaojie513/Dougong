# 交接：底部三行版式已落地（活动行 / 输入行 / 状态栏）

> **给下一个会话的起点。** 只写"现在在哪、差什么、怎么接"。
> 设计理由看代码注释，不要去翻旧对话。
>
> 最后更新：2026-11-06，`main` + 未提交改动（本轮尚未 commit）。

---

## 一、三十秒接上

```bash
cd /Users/zhanghaojie/项目/my/dougong
git status                 # 5 个文件有改动，见第二节
bash scripts/all-checks.sh # 见第四节"已知红项"
```

### 必须先做的决定

1. **本轮改动还没提交**，也**没升版本号**。`version-not-already-published`
   现在是红的，因为 `0.1.18-alpha.1` 已发布到 nuget.org——这是预期行为。
   **发版前要先问用户**（用户 2026-11-02 明确要求：没有指示时不动版本号与 API 基线）。
2. 本轮**没有改任何 SDK 公开 API**（只动了 `Dougong.Cli` 内部与检查），
   所以 ApiCompat 不需要更新基线。

---

## 二、本轮做了什么

用户 2026-11-05 报的问题：**忙时活动行跑到了输入行下面**（附两张实机截图）。

### 根因（不是排版算错）

底部块当时**待在正文滚动区里**。正文一长，整块被推走；想让块留在原地
就只能按绝对行号重画，而正文又从那些行写起——于是"谁后画谁赢"，
次序完全由时序决定。

### 修法

**用 DECSTBM 把正文限制在 `1..height-3` 内滚动**，底部三行物理上碰不到：

```
1 .. height-3   正文滚动区
height-2        活动行（仅忙时有内容，空闲留空）
height-1        输入行（`> ` 常驻）
height          状态栏（模型 · 上下文）
```

改动文件：

| 文件 | 改了什么 |
|---|---|
| `src/Dougong.Cli/Terminal/LiveRegion.cs` | **整类重写**。三槽位 + 滚动区进出 + `CommitPrompt` |
| `src/Dougong.Cli/Terminal/StatusLine.cs` | 删掉 `RenderSingleLine`，拆成独立的 `RenderActivity` / `RenderStatus` |
| `src/Dougong.Cli/Terminal/ActivityReporter.cs` | `Render()` 分别喂两个槽位；流式只隐活动行、状态栏常驻 |
| `src/Dougong.Cli/Program.cs` | `ReadLine` 之后调 `liveRegion.CommitPrompt(input)` |
| `src/Dougong.Cli/Terminal/Banner.cs` | `BottomRows` 2 → 3 |
| `spike/Dougong.Agent.Comparison/TerminalChecks.cs` | VT 模拟器加 DECSTBM + 区域内滚动 + ONLCR；改写 10 条旧断言；新增 2 条 |

### 关键取舍（改代码前先读）

1. **块高恒为 3 行，空闲不塌成 2 行。** 改 DECSTBM 会把光标打回屏幕原点，
   高度一变就得每轮重设。空闲时活动行留空，滚动区整个会话只设一次。
2. **相对模式（Narrow / 高度未知）退回单行。** 曾试图在两档也画两行，
   真机在 60×24 上暴露：光标贴底时第二行被终端裁掉，活动行整条消失。
   三行版式是富终端能力，不是所有档都有。
3. **只用一个 DECSC 槽。** 静止时光标恒在正文区；`BeginPrompt` 存一次、
   `CommitPrompt` 用它回去。不要读光标坐标（要走 DSR，管道里拿不到回应）。
4. **`BeginPrompt` 只存一次光标。** 用户连按空回车会再次进入，
   存第二次就把"正文位置"换成"输入行位置"了。

---

## 三、验证怎么做的（复现方式）

### 1. 检查套件

```bash
dotnet run --project spike/Dougong.Agent.Comparison -c Release --no-build -- terminal
# TERMINAL: ALL PASS（共 3 条新增 + 10 条改写）
```

新增两条是这次改造的验收点：

- `activity-line-sits-above-input-line`——**用户报的那个缺陷本身**。
  跑完整流程（提两次问 + 正文 + 忙起来），按屏幕行号比较。
- `bottom-block-rows-are-stable`——空闲时活动行槽位必须为空、状态栏仍在。
- `turn-elapsed-is-shown-exactly-once`——本轮耗时恰好显示一处：忙时活动行、
  闲时状态栏。**走真实 Reporter，不调纯函数**（原因见下面的反向验证表）。

**三条反向验证都做过**（改坏 → 确认变红 → 还原）：

| 破坏方式 | 变红的检查 |
|---|---|
| 活动行与输入行对调 | 4 条（含用户缺陷那条） |
| 去掉 DECSTBM | `anchored-region-survives-body-scroll` |
| 三槽位合并回一行（复现 P4 版式） | 4 条 |
| 忙时状态栏也报耗时 | `turn-elapsed-is-shown-exactly-once` |

**第四条的教训值得记住**：第一版那条检查只调了两个渲染函数，
于是"状态栏永远带耗时"改坏后它仍然全绿——它验的是纯函数，
而缺陷在 `ActivityReporter` 的接线处。现在改成经 Reporter 渲染、
再从区域捕获真实的两行。**反证失败过一轮才修对，别以为写了检查就算数。**

### 2. 真机（PTY 回放）

`/tmp/dgprobe/pty-probe.py` + `/tmp/dgprobe/replay.py`（**临时文件，未入库**；
要复现就重写，思路见下）：

- 用不可用 provider（`http://127.0.0.1:9`）触发 `Retrying`
- PTY 里跑真实 CLI，喂一句 `你好`，录下字节流
- 自己写 VT 回放，得到"用户实际看到的屏幕"，再断言行序

**结果：24×60、30×80、40×120、16×50、12×40 全部 PASS**，
边界高度 29–33 也全过。

真机抓到的屏幕（40×120）：

```
37|  | (´･_･`) 重试中… · 7s        ← 活动行
38|>                               ← 输入行
39|  probe-model                   ← 状态栏
```

### 3. VT 模拟器踩到的坑（**别再踩**）

- **ONLCR**：真实 tty 把 `\n` 转成 `\r\n`，模拟器早先只下移不归零，
  于是不带 `\r` 的正文每行右移一段。已在模拟器里补上。
- **`Text()` 会 TrimEnd**：`> ` 到屏幕上只剩 `>`，所以断言提示符要用
  `StartsWith('>')`，不能用 `Contains("> ")`。
- **`> ` 在转录里也出现**：按文本找输入行会命中正文第一行。
  输入行必须按**槽位行号**判定。

---

## 四、已知红项（都不是本轮引入的）

| 套件 | 失败项 | 状态 |
|---|---|---|
| `shell` / `persistent-shell` | `*-reports-current-directory` | **macOS 既有**。`/var` 是 `/private/var` 的符号链接，断言比的是未规范化路径。已在未改动的 HEAD 上复现过一次。 |
| `tool-package-checks` | `version-not-already-published` | **预期**。`0.1.18-alpha.1` 已发布；发版前升号即可。 |

其余 68 套全绿。

---

## 五、这个仓库的几条硬约束（会踩的坑）

1. **Plain 档必须逐字不变。** 46 个自检套件跑在 `TerminalTier.Plain`，
   断言改造前的确切文本。新呈现逻辑一律限定在 `Rich`/`Narrow`。
2. **高度判据要成对钉。** `Banner` 的高度常量是**实测值**。本轮把
   `BottomRows` 从 2 改成 3，四档判据自动跟着加一——改版式后要重新实测。
3. **不要并行跑 `all-checks.sh`。** `apicompat` 套会临时改
   `src/Dougong.Ai/ChatClientFactory.cs`。
4. **还原文件的验收标准**是 `git diff HEAD -- <file>` 空输出，不是 `git status` 干净。
5. **Spectre 会按 80 列兜底折行。** 自绘每一行都要按 `DisplayWidth` 截断，
   且**留最后一列不写**（写满会触发终端自动折行，后续定位全错）。
6. **`.gitattributes` 要求 LF。** 编辑工具写出 CRLF 后要转回 LF，
   否则 `bash scripts/*.sh` 会报 `invalid option name`。

---

## 六、macOS 平台差异

| 项 | 说明 |
|---|---|
| `/var` 符号链接 | 临时目录断言要规范化，否则 `shell` 套会红（见第四节） |
| 终端尺寸 | `Console.WindowHeight` 在 iTerm2 / Terminal.app / tmux 都能读 |
| 盲文字形 | U+2800–U+28FF，macOS 自带字体支持；显示宽度实测已确认 |
| 换行 | 比对文本时注意 `Environment.NewLine`（macOS 是 `\n`） |
| 脚本换行 | 仓库要求 LF；Windows 检出会带 CRLF，跑前先规范化 |

---

## 七、C5 / C8：宿主契约已补（2026-11-09）

桌面端反馈把 C5、C6 从 ⬜ 改成 ⏳。核实结果：

| 项 | 桌面端标 | 实际 | 结论 |
|---|---|---|---|
| C5 权限预设 | ⏳ | `PermissionPresets` 在 `src/Dougong.Agent/PermissionPresets.cs`，12 条检查钉着，CLI 在用 | **⏳ 成立**。只缺契约，已补 |
| C6 AgentPreset | ⏳ | **全仓库零实现**（引擎里也没有） | **应退回 ⬜**。这是等能力，不是等契约 |
| C8 子 agent | ⏳ | `SubagentRegistry` 在引擎，装配在 CLI 应用层 | **⏳ 成立**。已补 |

### 补了什么

- `src/Dougong.HostProtocol/Permission.cs`（新）：权限预设的 2 条命令 + 1 个事件
- `src/Dougong.HostProtocol/Subagent.cs`（新）：子 agent 的 3 条命令 + 2 个事件
- `src/Dougong.Hosting/ProtocolMapping.cs`（新）：**引擎类型 ↔ 协议形状的映射**
- `src/Dougong.Agent/Subagent.cs`：补 `StartedAt` / `ParentTurnId` / `ToolRounds` /
  `CurrentStatus` / `IsRunning` / `Complete()`
- 检查：`host-protocol` 加 4 条，新增 `protocol-mapping` 套件 7 条

### 为什么映射要单独一层（关键设计）

`HostProtocol` 是**零依赖**包，刻意不引用 `Dougong.Agent`。所以：

- 契约里不能直接用引擎类型 → 协议定义自己的形状
- "引擎枚举 ↔ 协议枚举"这条知识**不能留在各宿主**里 → 放 `Hosting`（L2，能同时看到两者）
- 放错地方的后果是静默的：枚举加值时两边都编译通过，只是那个值在界面上显示成错的

### 协议服务端（2026-11-09 补上）

`Dougong.Hosting/HostProtocolServer.cs`。**不绑定任何宿主**，三条约束照
`Dougong.Acp` 的先例：

| 维度 | 做法 |
|---|---|
| 传输 | 裸 `Stream`（stdio / socket / 进程内管道都行） |
| 实现 | `IHostSessionProvider` 委托，服务端只认接口 |
| 依赖 | 只认 Agent + HostProtocol，**不引用 Cli / Acp** |

已实现 5 条命令（C5 两条 + C8 三条）。其余一律回
`CommandFailureReason.Unsupported`，并在 `Message` 里列出当前支持范围。

**这是本轮最重要的决定**：宁可"明确说不支持"，也不要"实现了但走不通"。
静默不响应是最坏的——客户端只能等到超时，用户永远看不到原因。

契约侧顺带补了两处（都是补服务端时才暴露的）：
- `ServerEvent.RequestId`——命令与响应此前无法配对
- `CommandFailedEvent`——失败此前没有统一的表达方式

`IHostSessionProvider` 的两个方法都返回可空：**能力缺失回 `Conflict`，
不假装成功**。宿主可以只支持其中一部分（例如纯 CLI 场景不要子 agent）。

### 推送：子 agent 完成会主动通知

桌面端问过"事件驱动还是轮询"。答案是**事件驱动**：

```
SubagentHandle.Changed      ← 单个句柄结束（只触发一次）
        ↓ 注册表自动转发
SubagentRegistry.Completed  ← 任一子 agent 结束
        ↓ 服务端订阅（首次见到注册表时）
subagent/updated            ← 推给客户端，不带 RequestId
```

**为什么现在才有**：契约里一直写着 `subagent/updated`，但服务端从未发出过
一条——引擎根本没有可订阅的通知源，`SubagentRegistry` 只有查询方法。
这是"契约先于实现"的典型盲区。

两处容易写错、都已被检查钉住：
- **只能触发一次**（`_completionNotified`）：重复推会让客户端在回调里取结果时
  第二次取不到（已被第一次取走）
- **推送不带 `RequestId`**（`PushAsync` 强制）：带了会被误认为某条命令的响应，
  真正的状态更新反而被丢掉

### 权限广播：默认关，可选开（2026-11-09）

桌面端确认**单窗口，不需要权限推送**；但 Dock 浮动窗已在其技术栈里，
要求**预留一条可选事件**。所以做成显式订阅而不是默认广播：

```
permission/watch { enabled: true }   ← 订阅（幂等）
permission/watch { enabled: false }  ← 退订
        ↓ 之后 switch 成功时
permission/listed（不带 RequestId）  ← 广播给订阅者
```

**默认不广播的理由**：单窗口自己发起切换、自己收响应就够了，
再推一条是多余的一帧。这也保证"单窗口不付这份成本"。

### 子 agent：Started 也推

桌面端说"相比权限，更急的是子 agent 的异步事件"。所以补成
**Started + Completed 成对**——只推完成的话，界面要等子 agent 跑完
才知道它存在过，而那时它已经开始花额度了。中间的工具轮数变化不推。

### C6 待设计

`AgentPreset` 连引擎里都没有。要动它得先回答"AgentPreset 是什么"：
是模型+提示词+工具集的打包？和现有的 `/permission`、模型切换是什么关系？
**这是设计讨论，不是接线**，所以没有动。

---

## 八、复核（2026-11-09）抓到的东西

对两轮实现做了逐段复核，**五个真缺陷**，都已修 + 有检查钉住：

| # | 缺陷 | 后果 | 钉住它的检查 |
|---|---|---|---|
| 1 | 锚定模式下 `Clear()` 不清屏（它只调相对模式的 `ErasePaintedRows`，那个方法在该模式下直接 return） | 退出后底部三行留在终端上，用户回 shell 还能看到「重试中…」 | `anchored-region-clears-on-dispose`（新） |
| 2 | `Subscribe` 的登记与 `+=` 分在两处（check-then-act） | 两个并发命令各自订阅一次，同一次完成推两条 | 代码注释 + 锁内完成 |
| 3 | `_completionNotified` 是非原子 check-then-set | 并发 `Complete` 可能推送两次 | 改用 `Interlocked.Exchange` |
| 4 | 服务端 `DisposeAsync` 无超时等待写锁 | 对端不读时程序退不出去，栈上只看到一句 `WaitAsync` | 2 秒上限 |
| 5 | `MethodOf` 是手写映射表，与 `[JsonDerivedType]` 是同一知识的两个副本 | 新增命令时回报成类名，客户端认不出 | 改为 `ClientCommand.MethodOf` 从特性派生 |

另外把 `Check` 的取样时机修正了一处：`RenderThroughReporter` 在 `using var region`
作用域内取快照，而 Dispose 现在会清屏——那会让检查读到空屏（**假红**）。

### 真机复核

`/tmp/dgprobe2/pty-verify.py`（临时，未入库）在真实 PTY 里跑 CLI，验证两件事：

1. **版式**：40×120 下活动行 / 输入行 / 状态栏三行就位（`RESULT: PASS`）
2. **退出清理**：`/exit` 之后字节流里是 `清 38/39/40 行 → ESC[r`，顺序正确

### ⚠️ 发现但**没有**修的既有问题

**重试进行中 `/exit` 会卡住**：`composition.Dispose()` →
`kernelHost.DisposeAsync()` 要排空正在跑的回合，而那一轮在退避重试
（最长 10 秒）。表现为"按了退出但没反应"。这不是本轮引入的
（`/exit` 一直走这条清理路径），但确实会让人以为程序挂了。
要改的话得先定"退出时该不该强杀在跑的回合"——**那是行为决策，不是 bug 修复**。

---

## 九、还没做的

1. **提交本轮改动**（用户没要求，等指示）。
2. **升版本号**（需先问用户）。
3. **窗口尺寸变化后没有重设滚动区**：`LiveRegion` 只在创建时算一次高度。
   用户中途改窗口大小，滚动区会停在旧值。已知限制，未处理。
4. **`docs/porting-differences.md` 与本轮相关条目未更新**（如需要）。
