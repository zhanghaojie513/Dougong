# Changelog

本文件记录**公开 API** 与可观察行为的变化。

格式参考 [Keep a Changelog](https://keepachangelog.com/zh-CN/1.1.0/)，
版本号遵循 [语义化版本](https://semver.org/lang/zh-CN/)。

## 关于兼容性

本项目处于 `0.x`，**不做向后兼容承诺**。但所有公开 API 的变更都会被检出并记录：

- `Dougong.Kernel` 与 `Dougong.Agent` 启用了 `EnablePackageValidation`，
  任何公开 API 的增删改会在 `dotnet pack` 时报 `CP****` 错误（见 `docs/m3-plan.md` §五）
- 删改公开 API 前先标 `[Obsolete]`，至少跨一个次版本
- 每条变更记入下面的对应版本

**这里只写"对使用者有影响"的变化**，不写内部重构。

---

## [Unreleased]

### Added

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
  见 [skill-l1-acceptance-1.md](docs/skill-l1-acceptance-1.md)。
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
