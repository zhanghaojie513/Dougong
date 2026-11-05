# 交接：终端布局待修 —— 活动行必须在输入行上方

> **给下一个会话（macOS）的起点。** 只写"现在在哪、差什么、怎么接"。
> 设计理由看代码注释，不要去翻旧对话。
>
> 最后更新：2026-11-05，`main` @ `b10ebd3`，工作区干净、与远端同步。

---

## 一、三十秒接上

```bash
git clone https://github.com/zhanghaojie513/Dougong.git   # 或 git pull
cd Dougong
bash scripts/all-checks.sh        # 71 个套件，预期全绿
```

**`version-not-already-published` 现在会转红**：`0.1.18-alpha.1` 已经发布到
nuget.org 了，所以这条检查会在代码改动后提醒"发版前要升号"。
**那是预期行为，不是缺陷。**

### 一个必须先做的决定

如果为了修本轮问题升版本（例如 `0.1.19-alpha.1`），**先问用户**。
用户 2026-11-02 明确要求过"没有明确指示时不要动版本号与 API 基线"。
另外这个项目**不做向后兼容承诺**（0.x），但每条公开 API 变更都会被
ApiCompat 检出，改 SDK 公开签名要在 `CHANGELOG.md` 里记一句。

---

## 二、当前问题（用户 2026-11-05 报，附两张实机截图）

### 现象

用户发一句话后，屏幕底部是：

```
> 你好                                     ← 输入行
  ↻ 请求失败（NetworkError），0.5 秒后重试（1/5）
  ↻ 请求失败（NetworkError），0.9 秒后重试（2/5）
  ↻ 请求失败（NetworkError），2.1 秒后重试（3/5）
∿ (¬_¬) 重试中… │ mimo-v2.6-flash │ 3.3K/1.0M [░░░░] 0% │ 14s   ← 活动行
```

**活动行跑到了输入行下面。** 用户的期望（也是 Hermes 的实际版式）：

```
∿ (¬_¬) 重试中… │ mimo-v2.6-flash │ 3.3K/1.0M [░░░░] 0% │ 14s   ← 活动行（输入行上方）
> 你好                                     ← 输入行
──────────────────────────────────────────
 ⚙ model · ctx · ...                       ← 状态栏（贴最底）
```

### 根因

我在 P4 阶段**误读**了 Hermes 的版式：我以为它"忙闲共用一行、只有一行"，
于是把「活动行」和「状态栏」合并成了一行，并且把它排到了最底。

实际是**三行、两个独立槽位**：

| 位置 | 内容 | 何时出现 |
|---|---|---|
| 活动行 | spinner + 面孔 + 阶段文案 + 耗时 | **只在忙时** |
| 输入行 | `❯ ` + 用户输入 | 常驻 |
| 状态栏 | 模型 · 上下文 · 成本 | 常驻 |

对照源码：

- `src/Dougong.Cli/Terminal/StatusLine.cs` 的 `RenderSingleLine`
  ——现在忙闲共用，需要拆回两个方法。
  **注意**：同文件里已经有 `RenderActivity`（活动行）和 `RenderStatus`（状态栏），
  但**生产代码（`ActivityReporter`）只调 `RenderSingleLine`**，那两个方法
  目前只有检查在用。改的时候要判断是复用它俩、还是重写——先读它们的注释，
  它们是为"两行版式"写的，`RenderActivity` 还会自己补白到整行宽。
- `src/Dougong.Cli/Terminal/ActivityReporter.cs` 的 `Render()`
  ——它调 `RenderSingleLine` 并 `SetLines([line])`，只产出一行
- `src/Dougong.Cli/Terminal/LiveRegion.cs` 的 `BeginPrompt`
  ——第 194–205 行按"状态行贴底、提示符紧贴其上方"计算行号。
  三行版式下要变成：**活动行 → 输入行 → 状态栏（贴底）**

### 具体要改的地方

1. **`ActivityReporter.Render()`**：忙时输出两行 `[活动行, 状态栏]`，
   闲时输出一行 `[状态栏]`。`SetLines` 已经支持多行。
2. **`LiveRegion.BeginPrompt`**：提示符行要落在"状态行块的上方"，
   而状态行块的高度随忙闲变化。当前实现已按 `_lines.Count` 动态算
   （第 203–205 行），**这部分逻辑本身是对的**——
   问题在于调用时序：`BeginPrompt` 在 `Render` 之前跑，
   而轮次进行中 `Render` 由泵线程每 100ms 调一次。
   忙时 `Repaint` 会把两行内容画在 `_anchorBottom` 之上，
   而提示符行是 `BeginPrompt` 那一刻算的——**两者会打架**。
   这里需要设计：要么让 `Repaint` 也负责提示符行，
   要么让 `BeginPrompt` 之外的路径不去碰提示符那一行。
3. **`TerminalChecks` 里三条检查写的是合并版式**，要跟着改：
   - `status-is-single-line-when-busy`（`SingleLineBusy`）
   - `status-persists-when-idle`（`StatusPersistsIdle`）
   - `prompt-sits-above-status-bar-when-anchored`（`PromptAboveStatusBar`）

---

## 三、这个仓库的几条硬约束（会踩的坑）

这些是前几轮真机踩出来的，**改代码前先读一遍**。

### 1. Plain 档必须逐字不变

46 个自检套件跑在 `TerminalTier.Plain`，断言的是改造前的确切文本。
任何新的呈现逻辑都必须限定在 `Rich`/`Narrow`，判据是
`Output.Capability.Tier`。`Output` 是**唯一出口**，不要直接 `Console.Write`。

### 2. 高度判据要成对钉

`Banner` 的各档高度常量是**实测值**，不是推算的：
`FullMinHeight = 26 + 2`、`Stacked = 32 + 2`、`Compact = 29 + 2`、`Ascii = 19 + 2`。
改动版式（加空行、改形象行数）后必须重新实测并更新，
否则 `startup-never-exceeds-terminal-height` 会红——**那是提醒不是噪音**。

同理，改判据时**两个方向都要有检查**：`n` 该升级、`n-1` 该降级。
只验典型值会漏掉边界（这个坑我踩过两次，见 memory）。

### 3. 不要并行跑 `scripts/all-checks.sh`

`apicompat` 套会**临时修改 `src/Dougong.Ai/ChatClientFactory.cs`**，
靠 `finally` 还原。并行跑两个全量会让它被打断、留下脏文件。
（已有自愈逻辑，但自愈本身不该被当成常态。）

### 4. 还原文件的验收标准

不是 `git status` 干净，是 **`git diff HEAD -- <file>` 空输出**。
只看 status 会把脏内容提交进去（我犯过，`5d7ff99` 里多了一个无用参数）。

### 5. Spectre 会按 80 列兜底折行

`Output.CreateConsole` 已经把 `Profile.Width` 设成真实宽度。
**自绘的每一行都要按显示宽度截断**（用 `DisplayWidth`，中文占 2 格），
否则窄终端上会被 Spectre 重排、行数翻倍。`Banner.Clip` 是现成的做法。

---

## 四、macOS 上要注意的平台差异

| 项 | 说明 |
|---|---|
| 终端 | `Console.WindowHeight` 在 iTerm2 / Terminal.app 都能读；tmux 下也能读。**先真机确认**再依赖它（`dougong` 的 `/status` 会显示档位与高度） |
| 盲文字形 | 云拱形象用 U+2800–U+28FF。**macOS 自带字体支持**，但显示宽度可能被判成 2 格——实测确认；若不齐，`DisplayWidth.IsWide` 需要补区间 |
| 框线 | `─│┌┐└┘` 半角，macOS 无问题 |
| 字形表 | `BigText` 用 `█`（U+2588），全平台一致 |
| 颜色 | `#FFB347` → `#C0392B` 渐变依赖 24-bit 色。iTerm2 / Terminal.app 都支持；tmux 里可能需要 `terminal-features` 配置 |
| 换行 | 检查里比对文本时注意 `Environment.NewLine`（macOS 是 `\n`），已有几处用 `Split('\n')` 比较好 |

**跑检查时的平台差异**：`persistent-shell` 套在 Windows 上偶发失败
（`shell-output-has-no-leading-blank`），macOS 上应该更稳（POSIX 分支）。
若在 macOS 上红了，那可能是真问题，别照着 Windows 的结论忽略它。

---

## 五、验证方式

改完之后，除了 `all-checks.sh` 全绿，还要：

1. **独立探针**：写一个只引用 `Dougong.Cli` 的小项目，
   不经过 `TerminalChecks`，直接调 `Banner.Print` / `LiveRegion` 数行数。
   前几轮的经验是它抓到了检查覆盖不到的缺陷。
   **探针必须反向验一次**：故意破坏修复，它要变红。
2. **真机**：`dotnet run --project src/Dougong.Cli -- ` 跑起来，
   发一句会失败的话（或断网）触发重试，看活动行是否在输入行上方。
3. **重试路径要专门测**：用户这次是在 `RetryScheduled` 状态下看到问题的。
   该状态下 `Phase = Retrying`、`ShouldDisplay = true`，会走忙的分支。

---

## 六、相关文档

| 文档 | 读什么 |
|---|---|
| `C:\myObsidian\...\tui-fullscreen-hermes-parity-plan.md` | §2.3 底部区域结构（**注意：§2.3 的"状态行在输入行上方"这句我先前判过，把它划掉了；但用户 2026-11-05 的截图说明"活动行在输入行上方"是对的——两者别混）** |
| `docs/porting-differences.md` | D-34（字标暖色例外）、D-35（面板清单来源） |
| `CHANGELOG.md` | `0.1.18-alpha.1` 段：本版对使用者可见的全部变化 |

---

## 七、当前进度一句话

**已发布 `0.1.18-alpha.1`。启动页（字标 + 面板）与状态栏均已落地并真机验过；
唯一未解决的是「忙时活动行跑到了输入行下方」——这是 P4 阶段对 Hermes 版式的
误读，需要把底部从「两行（提示符 + 合并行）」改回「三行（活动 + 输入 + 状态）」。
**
