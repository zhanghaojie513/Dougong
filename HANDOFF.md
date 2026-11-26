# 交接：SDK 侧当前状态

> **给下一个会话的起点。** 只写"现在在哪、差什么、怎么接"。
> 设计理由看代码注释，不要去翻旧对话。
>
> 最后更新：2026-11-26，`main` @ `cbc499f`，工作区干净、与远端同步。

---

## 一、三十秒接上

```bash
cd /Users/zhanghaojie/项目/my/dougong
git status                 # 应当干净
dotnet --version           # 应当 11.0.100
bash scripts/all-checks.sh # 见第四节"已知红项"
```

**当前版本：`0.2.0-alpha.1`，目标框架 `net11.0`，已发到 nuget.org。**

### 必须先做的决定

动版本号或 API 基线之前**先问用户**（用户 2026-11-02 明确要求：
没有指示时不动版本号与 API 基线）。`version-not-already-published`
转红是提醒，不是故障。

---

## 二、最近三个版本做了什么

| 版本 | 内容 |
|---|---|
| `0.2.0-alpha.1` | **TFM 从 `net10.0` 升到 `net11.0`**（与桌面端同批升）。 |
| `0.1.25-alpha.1` | 删掉能力清单里的假缺口（"子进程无进程组"是错的，见第七节）。 |
| `0.1.24-alpha.1` | 接通推理强度档位：配置键 `reasoning_effort` 现在真的生效。 |

### TFM 升级的要点

- `.NET 11` 于 2026-11-10 发布，**是 STS（支持到 2028-05）**；
  `net10` 是 LTS（到 2028-11）。这是用更长的支持期换了更新的运行时。
- **使用者需要 .NET 11 运行时**。CLI 是 `dotnet tool`，装完运行就靠它；
  SDK 包使用方要把项目目标改到 `net11.0` 才拿得到 `lib/net11.0/` 的程序集。
- `global.json` 固定 `11.0.100`。
- **升级时改了 31 个文件、53 处 `net10.0` 硬编码**——分布在 tests、spike、
  scripts、docs 里。这类改动**改漏不会编译失败**，只在运行时找不到 DLL
  或发版验证时报错。改 TFM 时务必全仓 grep 一遍，并用
  **清空 `bin`/`obj` 后重建**来验证（旧产物会让漏改的路径"假通过"）。

---

## 三、C5 / C8：宿主契约与服务端（已完成）

桌面端当初把 C5（权限预设）、C6（AgentPreset）从 ⬜ 改成 ⏳。核实结果：

| 项 | 桌面端标 | 实际 | 结论 |
|---|---|---|---|
| C5 权限预设 | ⏳ | `PermissionPresets` 在引擎，CLI 在用 | **⏳ 成立**，契约已补 |
| C6 AgentPreset | ⏳ | **全仓库零实现** | **应退回 ⬜**（等能力，不是等契约） |
| C8 子 agent | ⏳ | `SubagentRegistry` 在引擎，装配在 CLI 应用层 | **⏳ 成立**，契约已补 |

### 补了什么

- `src/Dougong.HostProtocol/Permission.cs`：2 条命令 + 1 个事件
- `src/Dougong.HostProtocol/Subagent.cs`：3 条命令 + 2 个事件
- `src/Dougong.HostProtocol/Failure.cs`：`CommandFailedEvent` + 四种失败原因
- `src/Dougong.Hosting/ProtocolMapping.cs`：**引擎类型 ↔ 协议形状的映射**
- `src/Dougong.Hosting/HostProtocolServer.cs`：参考服务端

### ⚠️ 两个约束，答错就是假控件

**1. `HostProtocolServer` 不提供扩展点，别指望继承。**
类 `sealed`，分发点是私有 switch。即使去掉 `sealed` 也没用——分发逻辑
不认识宿主的命令，那些一律落到 `Unsupported`。
宿主有自己的协议命令时，**保留自己的服务端循环**，只复用契约类型与
`ProtocolMapping`。（桌面端实测：744 行 IpcServer 承载 `fs/*`、`models/*`，
用不上本类。）

**2. `IHostSessionProvider` 两个方法必须返回"引擎真正在用的那一份"。**

```csharp
// C5：presets 只 new 一次，既包进审批链，也交给 provider
var presets = PermissionPresets.CreateDefault(ApprovalPolicy.AskOnRisk);
var approver = new PermissionPolicyApprover(new PresetApprover(inner, presets));
// → dispatcher 用 approver；provider.Permissions() 返回 presets

// C8：subagents 只 new 一次，既给三个工具，也给 provider
var subagents = new SubagentRegistry(maxDepth: 3, maxConcurrent: 4);
// → SubagentTool/ListTool/ResultTool 都用它；provider.Subagents() 返回它
```

接错的表现：界面能切、服务端回成功，**但引擎行为不变**；
或者 `subagent/list` 永远为空而额度在烧。
两条检查钉着这件事：`permission-switch-affects-real-approval`、
`subagent-list-sees-real-registry`（反证都验过）。

### 事件驱动，不是轮询

```
subagent：Started 与 Completed 都推 subagent/updated，用 State 区分
permission：默认不广播；多界面场景（Dock 浮动窗）发 permission/watch 订阅
```

推送**不带 `RequestId`**（带了会被误认为某条命令的响应），`PushAsync` 强制检查。

### 传输：stdio

桌面端已定 stdio，并决定**自己做 serve 循环**——`dougong serve` 子命令
在本仓库**不存在**（`HostProtocolServer` 类有，CLI 入口没做）。
不要开 TCP localhost：协议无鉴权，同机任意进程都能发命令。

---

## 四、已知红项

| 套件 | 失败项 | 状态 |
|---|---|---|
| `shell` / `persistent-shell` | `*-reports-current-directory` | **macOS 既有**。`/var` 是 `/private/var` 的符号链接，断言比的是未规范化路径。在未改动的 HEAD 上也能复现。 |
| `tool-package-checks` | `version-not-already-published` | **预期**。`0.2.0-alpha.1` 已发布；下次改动前升号即可。 |

其余 78 套全绿（共 82 套）。

### ⚠️ CI 目前是停摆的

`.github/workflows/build.yml` 会在 push 到 `main` 时跑，但 GitHub Actions
预算被设成 $0（页面显示 `Usage blocked`），所以 **run 会立刻失败**。
那不是代码问题。**后果：Windows 与 Linux 两个平台完全没被验证过**——
TFM 升到 net11 之后尤其值得留意。

---

## 五、这个仓库的几条硬约束

1. **Plain 档必须逐字不变。** 自检套件跑在 `TerminalTier.Plain`（输出被重定向时
   一律降这一档），断言的是改造前的确切文本。新呈现逻辑一律限定在
   `Rich`/`Narrow`，判据是 `Output.Capability.Tier`。
2. **高度判据要成对钉。** `Banner` 的各档高度常量是**实测值**，
   改版式后必须重新实测并更新。
3. **不要并行跑 `all-checks.sh`。** `apicompat` 套会临时改
   `src/Dougong.Ai/ChatClientFactory.cs`。
4. **还原文件的验收标准**是 `git diff HEAD -- <file>` 空输出，不是 `git status` 干净。
5. **Spectre 会按 80 列兜底折行。** 自绘每一行都要按 `DisplayWidth` 截断，
   且**留最后一列不写**（写满会触发终端自动折行，后续定位全错）。
6. **`.gitattributes` 要求 LF。** 编辑工具写出 CRLF 后要转回 LF，
   否则 `bash scripts/*.sh` 会报 `invalid option name`。
7. **改 TFM / 改版本号后要全仓 grep。** 见第二节末尾。

---

## 六、macOS 与网络环境

| 项 | 说明 |
|---|---|
| `/var` 符号链接 | 临时目录断言要规范化，否则 `shell` 套会红 |
| 终端尺寸 | `Console.WindowHeight` 在 iTerm2 / Terminal.app / tmux 都能读 |
| 换行 | 比对文本时注意 `Environment.NewLine`（macOS 是 `\n`） |
| 脚本换行 | 仓库要求 LF；Windows 检出会带 CRLF，跑前先规范化 |
| GitHub 443 不稳 | 推送常报 `HTTP2 framing layer` 或超时，重试即可（`nuget.org` 通常正常） |

---

## 七、教训：三次"做了才发现不必做"

这几条都是踩过的，写下来免得重复：

**1. 子进程"无进程组"是个假缺口。**
能力清单曾写着"`ShellTool` 无进程组、无升级杀"，我据此实现了
perl `setpgrp` + 按组杀，写完也在 macOS 验过。然后做决定性实验：

```
父 sh 存活，子进程用 perl setpgrp 另建进程组 → Kill(entireProcessTree) → 残留 0
```

**`.NET` 的 `Kill(entireProcessTree)` 按父子关系遍历，能跨进程组。**
按组杀是重复劳动，实现已全部撤销。
**误判原因**：我拿 `(sleep 50 &) ; exit 0` 测出残留，但那个场景里命令
自己先退出了——而真实执行器只在 `WaitForExitAsync` 抛异常时调 `KillTree`，
那时父进程必然还活着。**把"理论漏洞"当成了"实际漏洞"。**

**2. 契约注释里的承诺要实测。**
"不认识的判别器会忽略"曾经是假的（实际抛 `JsonException`），
"穷尽 switch 编译期暴露"也是假的（`_ => throw` 让编译器认为已穷尽，
加枚举值零警告）。前者补了 `TryParse`，后者改了措辞。

**3. 检查本身会坏。**
`verify-release.sh` 缺 `curl -L`，把已上架的包报成"未找到"，
`--wait` 白等 10 分钟。**这类问题比被测对象坏了更误导**——
会让人以为推送失败，然后去重推一个已占用的版本号（不可恢复）。

**共同点：这三个都不是"写错代码"，是"没验证就相信"。**

---

## 八、还没做的

1. **CI 预算**（$0，见第四节）。要不要开、开多少，由用户定。
2. **Windows / Linux 平台上 net11 未验证**（因 CI 停摆）。
3. **窗口尺寸变化后没有重设滚动区**：`LiveRegion` 只在创建时算一次高度。
   用户中途改窗口大小，滚动区会停在旧值。已知限制，未处理。
4. **能力清单里剩下的 ⬜/🟡 都等具体场景**——见
   `docs/dsh-capability-progress.md` 表头的说明。没有真实需求时不要按清单硬推。
