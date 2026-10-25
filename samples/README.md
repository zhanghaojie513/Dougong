# samples

每个里程碑一个可运行样例。样例是插件作者文档的活参照：API 变更时先改样例，再改文档。

| 样例 | 里程碑 | 内容 |
|---|---|---|
| `Dougong.Sample.Plugin/` | M0 | 单插件服务注册与清理，用于动态加载和回收验证 |
| `Dougong.Sample.Contracts/` | M0 | 契约版本、失败清理、共享契约插件 |
| `Dougong.Sample.Shared/` | M0 | 宿主与插件共同引用的共享契约程序集（服务接口与事件 DTO） |
| `Dougong.Sample.Isolated/` + `IsolatedPlugin/` | M0 | 覆盖"漏配共享契约"的失败路径 |
| `m1-minimal-agent/` | M1 | **可运行**：headless 跑通完整工具循环（模型请求工具 → 审批 → 执行 → 回填 → 答复），**不需要 API Key** |
| `m2-acp-mcp/` | M2 | **可运行**：MCP server 工具桥接 + ACP 协议驱动一轮对话 |

两个可运行样例各有明确的验证目标：

- **`m1-minimal-agent`** 演示接缝的价值——模型是可替换的 `IChatClient`，
  因此用脚本化假模型就能跑完整个循环，不花 API 费用。它同时做**旁路检测**：
  断言工具执行确实经过 `ToolDispatcher`（`DispatchedInvocations` 里有记录），
  而不是被某个框架偷偷自动调用。
- **`m2-acp-mcp`** 演示两条链路串起来：远端 MCP 工具经 `McpToolRegistry` 变成
  `IAgentTool`，与本地工具走同一个 dispatcher、同样要审批；整轮对话由 ACP
  JSON-RPC 驱动。**注意它断言 `权限已知=False`**——远端工具的 annotations
  只是提示，不是授权依据。

两个样例都**不用真实 provider**，因此验证的是接线而非协议形状。
协议形状必须用真实端点验证（M1 的 13 个缺陷里 11 个只有真端点才能暴露）。

动态插件样例使用 `EnableDynamicLoading`，并把 `Dougong.Kernel` 标记为不复制到输出，
以便加载路径真实走 collectible AssemblyLoadContext。

共享契约样例用于验证跨 ALC 类型同一性：宿主调用 `DougongKernel.RegisterSharedContract`
登记契约程序集（按内核实例隔离、返回句柄可撤销），插件程序集则不复制该契约。
漏配登记时，加载会因装配解析失败而回滚，并给出提示登记契约的诊断。

`Dougong.Sample.Isolated` / `Dougong.Sample.IsolatedPlugin` 专用于覆盖漏配路径：
测试项目刻意不引用该契约，默认 AssemblyLoadContext 中没有它，因此未登记时装配解析必然失败。
回归测试会先断言这个前提，避免将来被误引用后静默失效。
