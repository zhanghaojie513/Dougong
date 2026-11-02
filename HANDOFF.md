# 交接：P2 进行中

> **给下一个会话的起点。** 这份文件只写"读哪些、现在在哪、下一步做什么"，
> 不重复三份文档已有的内容——那是它们的职责。
>
> 最后更新：2026-11-03，`main` @ `7477503` + #22 技能提供者（未提交）

---

## 一、三十秒接上

```bash
cd C:/work/myProject/dougong
git pull
bash scripts/all-checks.sh        # 70 个检查套件，预期全绿
```

**唯一允许的失败**：`tool-package-checks` 的 `version-not-already-published`。
版本号 `0.1.16-alpha.1` 已发布到 nuget.org，而代码有变化——它在提醒
"发版前必须升号"，不是"现在必须升"。**没有明确指示时不要动版本号与
API 基线**（用户 2026-11-02 明确要求过）。

---

## 二、按顺序读这四份

| 文档 | 读什么 |
|---|---|
| `docs/dsh-capability-progress.md` | **§三之三 P2 进度**：15 项的状态、检查套件、排序依据、"差什么" |
| `docs/porting-differences.md` | **§四 D-19 ~ D-23**（本阶段新增的五条偏离）；**§三 证据表**里各能力的负控做法 |
| `docs/dsh-capability-gap.md` | **P2 清单**：还剩哪些、为什么按那个顺序 |
| `CHANGELOG.md` | **`[未发布]` 段**：本阶段对使用者可见的变化 |

读这三份就够，不需要翻 git log——但若想确认某次改动为什么那么做，
`git log --oneline -10` 的提交信息都写了理由。

---

## 三、当前状态

**P2 完成 6 项 + 1 项部分**（P1 已全部完成，只剩 LSP 不做）。

| 项 | 状态 | 套件 |
|---|---|---|
| 后台任务 | ✅ | `jobs`（12 项） |
| 计划模式 | ✅ | `plan-mode`（13 项） |
| 文件引用 `@file` | ✅ | `file-references`（8 项） |
| 消息级反馈 | ✅ | `feedback`（13 项） |
| 会话内换目录 | ✅ | `working-directory`（11 项） |
| 技能提供者 | ✅ | `skills`（11 项） |
| 持久 shell | 🟡 | `persistent-shell`（10 项）—— shell 有，PTY 未做 |

**剩余 8 项**（按"有没有真实的等待方"排）：

1. **#23 拦截钩子**（扩展性，中）——**要先建类型化拦截点**；dsh 的 hooks.json
   桥接只是兼容层，不是本体
2. 其余：#24 会话投影、#26 重复调用提醒、#27 权限预设、#25 出站代理、
   #17 子 agent、#18 目标、#31 SSH
3. **真 PTY**（#21 的另一半）——桌面端 P8f 唯一卡点，但要 ConPTY/forkpty 两套

**#30 已落地，但桌面端要改一处才能用上**：SDK 按会话日志的
`RecordSequence` 定位被评价的消息，而桌面端现在把**投影条目的
随机 id**（`item-<guid>`，重启即变）当作反馈键、且只写 stderr。
它需要在投影时把条目与日志序号关联起来，并改用 `JsonlMessageFeedbackStore`。

---

## 四、做下一项之前必须知道的四件事

### 1. 先调研 dsh 源码，别看配置猜

dsh 的 `docs/config-catalog.md` **只暴露配置**，实现语义在 `apps/web/tests/*.e2e.ts`
与对应包的 `src/` 里。计划模式第一版就是因为只看配置而做错了四处
（用 `todo_write` 而非 `exit_plan_mode`、条目列表而非 Markdown、结束本轮而非
阻塞、自造状态机而非走提问通道）。**读 e2e 测试的断言**——那里有真实行为。

### 2. 检查必须做负控，且要在"缺陷存在时真的红"

本阶段有三次检查空转：

- `refs-order-is-stable` 只验"两次结果相同"，而这台机器上文件系统顺序恰好有序
- `job-stop-ends-process-tree` 名字说验进程树，实际只验父进程退出
- `persistent-shell` 超时检查只验"能返回"，没验"超时后还能用"

**做法**：破坏代码 → 重建 → 跑检查 → 确认红 → 恢复。
破坏必须**能编译**（`if (false)` 会触发 CS0162，而 `TreatWarningsAsErrors`
让它变成编译错误 → 跑的是旧 DLL → 检查假绿）。

### 3. 检查程序集可能比源码旧

`terminal` 套的 `comparison-uses-release-cli-assembly` 专盯这个：
spike 里的 `Dougong.Cli.dll` 副本可能比 Release 产物旧，那时检查在测旧代码。
**改完源码先 `dotnet build src/...` 再 build spike**，否则负控结论不作数。

### 4. 文档与注释里的"我们做了 X"要能对上代码

本阶段修掉的缺陷里，三分之一是"注释声称的行为不存在"。
写下断言后**立刻用探针构造那个反例跑一遍**。

---

## 五、工作流约定

- **版本号与 API 基线只在用户明确说更新时更新**。`scripts/update-api-baseline.sh`
  是发版脚本，不是每次提交都跑。
- **提交粒度**：一项能力一个提交，信息里写清"为什么这么选"，不只是"做了什么"。
- **文档同步**：改完能力后回改三份文档（progress / gap / porting-differences）
  + `CHANGELOG.md` 的 `[未发布]` 段。有这个检查盯着：
  `capability-progress-counts-are-consistent`（计数自洽）、
  `every-claim-has-evidence`（每个 ✅ 要指向真实检查）。
- **推送上不去**：`github.com:443` 常连不上，重试即可；`gh` 与 api 是通的。

---

## 六、下一个会话的第一句话

建议这么说：

> 接着做 P2。先读 `HANDOFF.md` 与 `docs/dsh-capability-progress.md` 的
> §三之三，然后按"有谁在等"的顺序继续。下一项是 #30 消息级反馈。

若要换项、或想先做真 PTY，直接说——排序依据在 progress 文档里，
换项时把理由写进 `dsh-capability-gap.md`。
