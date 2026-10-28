# M3 + I0 开发历程素材

> 用途：记录 M3（生态与稳定）与 I0（稳定化与发布）期间真实发生的问题与转折，
> 供后续梳理成开发历程文章。不是验收文档，也不是对外承诺。
> 验收状态见 [status.md](../status.md)。
> 记录原则：只写实际发生的；区分「实现缺陷 / 测试缺陷 / 判断错误 / 文档缺陷」四类。
>
> 上一篇：[m2-dev-journey.md](m2-dev-journey.md)。

---

## 一、这一阶段的主题变了

M0–M2 的问题是「东西没做对」：租约没生效、协议字段错、压缩没接入循环。
**M3 之后的问题全部换了一种：东西是对的，但别人用不起来。**

具体表现：

- 代码全绿，但一位读者照文档走，5 次卡住
- 功能完整，但参数拼错会静默进对话
- 测试 45 套全过，但 `dotnet tool install` 装不上
- 桌面图标做出来了，但深色屏幕上立体感消失

**这一阶段的缺陷几乎都不在代码逻辑里，在"接口"上**——文档、命令行、CLI 行为、
包元数据。这类缺陷的共同特点是：**自己测不出来，必须有人（或另一台机器）真的用一次。**

---

## 二、实现缺陷

### 2.1 `dotnet publish -p:PublishSingleFile=true` 根本编译不出来

**现象**：发布单文件时报错

```
error IL3000: 'Assembly.Location' always returns an empty string for
              assemblies embedded in a single-file app.
```

**根因**：`PluginLoadContext.IsUnderDotnetRoot` 用 `typeof(object).Assembly.Location`
推共享框架目录。单文件下该属性恒为空，`TreatWarningsAsErrors` 把 IL3000 提升为 error。

**修法**：把"可能为空"显式写进逻辑，并在两处 `#pragma warning disable IL3000` 附上理由——
那里的用途正是观察它是否为空，分析器无法表达这个意图。

**两件没做到的事，必须记下来**：

1. 我一开始断言这会让运行期把框架程序集误判成插件依赖。**查证后是错的**：
   原代码 `Path.GetDirectoryName("")` 已返回 null 并跳过，行为与修后等价。
   真正的缺陷只有编译失败这一条。
2. 我两次尝试用"未登记契约必须加载失败"反证这个修复，**两次都没变红**——
   那个场景在装配解析阶段就失败，走不到 `IsFrameworkAssembly`。
   最后用"让 `UnloadAsync` 提前返回"做反证，才得到 `卸载后剩余插件: 1` → FAIL。

### 2.2 11 个脚本以 CRLF 入库，Linux 上一行都跑不了

**现象**（WSL2 Ubuntu 实测）：

```
scripts/all-checks.sh: line 7: set: pipefail: invalid option name
```

**根因**：11 个 `scripts/*.sh` 是 CRLF 结尾，Linux 下 `set -euo pipefail` 被读成
`set -euo pipefail\r`。仓库此前没有 `.gitattributes`。

**为什么 CI 抓不到**：`actions/checkout` 默认把工作区转成 LF，ubuntu runner 上一切正常。
**踩到的是从 Windows 检出的真实副本**——而这正是 I0 退出条件说的"干净环境"。

### 2.3 未知命令行参数静默进入对话

**现象**：敲 `dougong --docter`（把 `--doctor` 拼错），程序**正常启动交互对话**并创建会话文件。

```
dougong 0.1.0-alpha.1  模型=deepseek-v4.1-flash ...
会话=C:\Users\haiha\.dougong\sessions\20261027-184301.jsonl
```

**根因**：`Program.cs` 里所有参数都用 `args.Contains(...)` 判断，未知参数被静默忽略，
直接落到"读 provider 配置 → 启动对话"。

**修法**：新增 `FindUnknownArgument`，放在 provider 检查**之前**（缺配置 + 参数拼错时，
报的是参数错，那才是根因）。

### 2.4 `--doctor` 的 404 提示漏掉了最可能的原因

**现象**（外部读者走查发现）：配 provider 时用
`https://api.minimaxi.com/anthropic` 配 `openai` 协议，拿到 404。
而 `--doctor` 当时只说：

> 常见原因：密钥无效、模型名不对、端点路径错。

**漏掉了最可能的那一条**：地址带 `/anthropic` 就该配 `anthropic` 协议，
不匹配时会拼出 `.../anthropic/chat/completions` 这种不存在的路径。

**修法**：`ConnectivityHint(config, error)` 按地址形状 + 错误码给针对性建议。

### 2.5 LICENSE 结构破坏导致 GitHub 识别不出许可证

**现象**：GitHub API 返回 `"license": {"spdx_id": "NOASSERTION"}`——不是 MIT。

**根因**：我把"Third-party notices"段插在了 MIT 正文**之前**，破坏了许可证的标准结构。

**修法**：MIT 正文保持完整连续，第三方声明用 `---` 分隔附在末尾。

---

## 三、判断错误（这一阶段最多）

### 3.1 "调用点多"被当成"需要抽象"

我曾写过：*"40+ 次 `new ToolDispatcher` 调用说明每个使用者都要自己想办法装配"*。

查了调用分布之后发现：**98 次里 60 次是零参数**，产品代码里真正的构造**只有 1 处**。

> **我只数了总数，没数差异度。**

先看分布再看总量，才是有依据的判断。

### 3.2 "规则长度上限"套错了位置

看到外部建议"全局规则压在 200 行以内"，就想给 Dougong 加一个 system prompt 长度检查。

**但 Dougong 没有 system prompt 概念**——那是宿主的自由。
硬加只能变成"检查配置而不是检查行为"。

真正的对应物是 `ContextLimits.OverheadTokens`（工具声明的预留量），
那个默认值 512 从未被实测校准过。实测：4 个工具 188 token、10 个 453、30 个 1404。

### 3.3 把"实现上的方便"当成"设计要求"（出现三次）

| 初版判断 | 错在哪 |
|---|---|
| "显示真实步骤比 Hermes 的拟人化更好" | 把调试需求当成了用户需求 |
| "只有等宽 kaomoji 才能做动画帧" | 真实约束是"槽位固定"，不是"每帧等宽" |
| "不引入 Spectre，自己写 80 行 ANSI" | 把"不想加依赖"当成了设计约束。实测 Spectre 在重定向时自动降级，45 套件不受影响 |

### 3.4 编了一个"别人也这么做"的理由

我说"Hermes 也把 warn 让位给橙色"来支撑自己的配色决定。

**查了源码，它没有这回事**——它从一开始就把品牌色和警告色定成两个相邻色。

> **先查再写理由。没读过的地方不要替对方总结。**

### 3.5 Logo 设计连续四轮走错方向

| 轮次 | 方向 | 结果 |
|---|---|---|
| 1 | 画斗拱（斜昂） | 概念太字面，16px 糊 |
| 2 | 层叠出跳 | 单色/反白有效，但通用 |
| 3 | D 字标 | **撞 ReScript**（红底白粗字母 + 缺口） |
| 4 | 咬合/节拍/托举 | 像 H、像条形图、像 E |

**共同错误：一直在猜形状，而不是在解决一个问题。**

用户点破的关键：*"我们这个毕竟是 agent，ai coding，其他这类产品 dsh、codex、claude code、zcode 都没这样做过"*。

查证后确认：**四家都是"产品名/母公司标识的直接图形化"，没有一个用隐喻式 logo。**
我做的是"斗拱项目 → 画斗拱"，正是 skill 红旗清单第一条的 clip-art literalism。

第五轮改成字标/抽象几何才对。

---

## 四、测试缺陷

### 4.1 反证失败时先怀疑反证（这一阶段连中三次）

| 次数 | 反证怎么失效的 |
|---|---|
| 1 | 改在附近而非被断言处 |
| 2 | 破坏条件在目标情形下为假 |
| 3 | 对错了检查项 |

**没有一次是"缺陷不存在"。**

### 4.2 `--no-build` 用旧 DLL（复发两次）

- 反证时恢复源码后测出与破坏时几乎相同的数字，差点得出"优化无效"的结论
- 删掉 `bin/Release` 重建后才是真实数据

已写进 `performance-baseline.md` §九：**跑 perf 前必须先清理重建**。

### 4.3 "全部拒绝"类断言缺正向对照

L1 存储的 `CommitAsync` 二次编码导致所有记录都读不出来时，**"全部拒绝加载"的断言照样通过**。

现在所有"全部 X"类断言都配正向对照。

---

## 五、文档缺陷（这一阶段的重点）

### 5.1 一次真人走查找出 5 个文档缺口

读者**严格按 `getting-started.md` 逐步执行**，5 次提问全部由文档缺口引起：

| 卡在哪 | 缺口 |
|---|---|
| §6.5 位置不明 | 章节顺序不合理 |
| 文档未提示先同步 | 读者用了旧副本 |
| §5.5 缺失 | 章节编号跳跃 |
| cmd 里 `export` | 平台写法未区分 |
| shell 差异未说明 | 同上 |

五处全修，并补了 6 项检查把每处钉住。

**第二次走查（2026-10-28）只发现 1 个问题**——`--doctor` 的 404 提示缺项。
对比说明第一次的修正确实生效了。

### 5.2 "填 None 就不触发审批"——同一句话写错两次

正文修过一次，示例注释里还留着同样的话。
实测确认：`Access=None + IsKnown=true` 在默认 dispatcher 下审批器**仍被调用 1 次**。

现在有一条 `guide-does-not-claim-none-skips-approval` **双向**检查：
既不许出现无条件断言，也要求说明正确做法（只查前者，删掉整段说明也能假通过）。

### 5.3 台账类文档自己会把人带偏

`plan.md` 停在 2026-10-24，把 M2/M3 记为"未开始"，而它们早已关闭。
`skill-l1-plan.md` 写着验收 1"装置就绪，未运行"，而它 10-26 就跑完了。

**台账必须与原文档对齐，否则"防跑偏"的导航文档自己就会误导。**

---

## 六、I0 阶段特有的问题

### 6.1 退出条件被误读成"发版完成"

`plan.md` 一度写着"I0 只剩 ACP Registry 上架"。但对 `iteration-roadmap.md`
的原文后发现：**I0 的退出条件是"一位未参与开发的人在干净 Windows 与 Linux
环境各完成一次全流程"，从没要求上架 Registry。**

把"分发渠道"当成了"退出条件"——这是记账错误。

### 6.2 上架 Registry 的真实阻碍（查证纠正）

原记录写"被 `0.x` 不承诺兼容卡住"。读了 Registry 的 `AUTHENTICATION.md` 与
`agent.schema.json` 后，真实阻碍是三条：

| # | 阻碍 | 状态 |
|---|---|---|
| 1 | 仓库是私有的 | 已解决（开源） |
| 2 | **只接受 Agent Auth / Terminal Auth**，Dougong 只有环境变量认证 | 未解决，是功能缺口 |
| 3 | distribution 只允许 binary/npx/uvx，没有 `dotnet tool` | 可绕（走 binary） |

第 2 条是关键：它与"密钥只从环境变量读"的纪律冲突，**需要先做设计决策**。

### 6.3 安装命令必须带 `--version`

```
dotnet tool install --global Dougong.Cli
→ 在 NuGet 源中找不到 dougong.cli
```

加 `--version 0.1.0-alpha.1` 立刻成功。

**根因是 nuget.org 两套索引不同步**：registration 索引已有该版本（`listed: true`），
搜索索引 `totalHits` 仍为 0。不带版本号时 NuGet 必须查搜索索引找"最新版"。

现在有 `docs-pin-tool-version-on-install` 检查盯着三个用户可见入口。

---

## 七、这一阶段的收获

**一、缺陷的种类变了。** M0–M2 是逻辑错误，M3–I0 是"正确但用不起来"。
后者的检出方式完全不同——**必须真的用一次**，读代码看不出来。

**二、反证比断言重要。** 这一阶段有 8 次反证失败，每次都不是"缺陷不存在"，
而是反证本身写错了。**先怀疑反证，再怀疑缺陷。**

**三、诚实记录的价值在事后。** 上面 §3.1–3.5 那些判断错误，
写下来之后才发现它们有共同模式：**把某个方便或恐惧当成了约束，然后找理由支撑它。**

**四、用户的质疑多次纠正了方向。** "其他产品都没这样做过"这一句，
直接推翻了我四轮的 logo 方向。**外部视角是这种"自己觉得对"的错误的唯一解药。**

---

## 相关文档

- M0： [m0-dev-journey.md](m0-dev-journey.md)
- M1： [m1-dev-journey.md](m1-dev-journey.md)
- M2： [m2-dev-journey.md](m2-dev-journey.md)
- 品牌设计对比与决策：`assets/brand/README.md`
- CLI 方案：Obsidian vault `projects/dougong/CLI-终端呈现方案.md`
