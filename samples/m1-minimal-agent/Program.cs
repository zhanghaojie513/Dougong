using System.Text;
using System.Text.Json;
using Dougong.Agent;
using Microsoft.Extensions.AI;

namespace Dougong.Sample.MinimalAgent;

/// <summary>
/// M1 样例：不经真实 provider，跑通"模型请求工具 → 审批 → 执行 → 结果回填 → 最终答复"。
/// </summary>
/// <remarks>
/// **不需要 API Key**，因为模型是可替换的接缝（<see cref="IChatClient"/>）——
/// 这是本样例要演示的第一件事。
/// <para>
/// 它演示的第二件事是：**旁路检测**。所有工具执行都经过
/// <see cref="ToolDispatcher"/>，样例末尾会断言"工具确实只被这一个入口执行过"，
/// 而不是被某个框架偷偷自动调用。
/// </para>
/// <para>
/// 它**不**演示真实 provider 的协议形状——那必须用真端点验证。
/// 假模型只能验控制流，这一点在 M1 的 13 个缺陷里被反复证明。
/// </para>
/// </remarks>
public static class Program
{
    public static async Task<int> Main()
    {
        var workspace = Path.Combine(Path.GetTempPath(), "dougong-m1-sample-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(workspace);
        File.WriteAllText(Path.Combine(workspace, "greeting.txt"), "hello from the sample workspace");

        try
        {
            var executed = new List<string>();

            // ① 工具集：一个只读工具。注册进 dispatcher 后，模型才可能看到它。
            var dispatcher = new ToolDispatcher(
                new RecordingApprover(),
                new ToolResourceGate(isolated: true),
                "sample-session", "turn-1", workspace,
                TimeSpan.FromMinutes(5),
                new WorkspacePathGuard(workspace));

            dispatcher.Register(new ReadTextFileTool(workspace, executed));

            // ② 模型：脚本化的假实现。第一轮请求读文件，第二轮给最终答复。
            var runtime = new LoopAgentRuntime(
                new ScriptedClient(),
                dispatcher);

            // ③ 跑一轮。
            var answer = new StringBuilder();
            await foreach (var evt in runtime.RunAsync(
                new AgentRunRequest("sample-session", "turn-1", [], "读一下 greeting.txt")))
            {
                switch (evt)
                {
                    case AgentEvent.TextDelta delta:
                        Console.Write(delta.Text);
                        answer.Append(delta.Text);
                        break;

                    case AgentEvent.ToolCallRequested call:
                        Console.WriteLine($"\n  → 请求工具 {call.ToolId} {call.Arguments}");
                        break;

                    case AgentEvent.ToolCallCompleted completed:
                        Console.WriteLine($"  ← 终态 {completed.Result.Outcome}");
                        break;

                    case AgentEvent.RunFinished finished:
                        Console.WriteLine($"\n[运行结束：{finished.Outcome}]");
                        if (finished.Diagnostic is { } d) Console.WriteLine($"  {d}");
                        break;
                }
            }

            // ④ 断言：工具确实被执行过，且只经由 dispatcher。
            if (executed.Count != 1)
            {
                Console.Error.WriteLine($"期望工具执行 1 次，实际 {executed.Count} 次。");
                return 1;
            }

            if (!dispatcher.DispatchedInvocations.Contains("call-1"))
            {
                // 这条防的是"旁路"：如果某个框架直接调了工具的 ExecuteAsync，
                // dispatcher 里就不会有这条记录。
                Console.Error.WriteLine("调用没有经过 dispatcher：存在绕过执行的路径。");
                return 1;
            }

            if (!answer.ToString().Contains("hello", StringComparison.OrdinalIgnoreCase))
            {
                Console.Error.WriteLine("最终答复里没有看到文件内容：结果回填失败。");
                return 1;
            }

            Console.WriteLine("\n样例通过：工具经 dispatcher 执行，结果已回填给模型。");
            return 0;
        }
        finally
        {
            try { Directory.Delete(workspace, recursive: true); } catch (IOException) { }
        }
    }

    /// <summary>只读文本文件；记录每次执行，供样例断言。</summary>
    private sealed class ReadTextFileTool(string workspace, List<string> executed) : IAgentTool
    {
        public ToolDescriptor Descriptor { get; } = new(
            "read_text", "read_text", "1.0.0", "读取工作区内的文本文件。",
            JsonDocument.Parse("""
                {
                  "type": "object",
                  "properties": { "path": { "type": "string" } },
                  "required": ["path"],
                  "additionalProperties": false
                }
                """).RootElement.Clone());

        /// <summary>只读且已知：声明 Read 而非 Unknown，审批器就能给出有意义的提示。</summary>
        public ToolPermission DescribePermission(JsonElement arguments) =>
            new(ToolAccess.Read, IsKnown: true,
                Paths: [arguments.GetProperty("path").GetString() ?? ""],
                Commands: [], Domains: []);

        /// <summary>声明资源键：与写同一文件的操作互斥，与读其他文件的操作并行。</summary>
        public ToolExecutionPlan DescribeExecution(JsonElement arguments) =>
            new(ToolConcurrency.Serial)
            {
                Reads = ["file:" + Path.GetFullPath(
                    Path.Combine(workspace, arguments.GetProperty("path").GetString() ?? ""))],
            };

        /// <summary>声明文件依赖：审批时捕获版本，执行前比对，防止"批准 A 执行 B"。</summary>
        public IReadOnlyList<string> DescribeFileDependencies(JsonElement arguments) =>
            [arguments.GetProperty("path").GetString() ?? ""];

        public ValueTask<AgentToolResult> ExecuteAsync(
            ToolInvocation invocation, CancellationToken cancellationToken)
        {
            var relative = invocation.Arguments.GetProperty("path").GetString() ?? "";
            executed.Add(relative);

            var full = Path.Combine(workspace, relative);
            var content = File.Exists(full) ? File.ReadAllText(full) : "(文件不存在)";

            using var document = JsonDocument.Parse(JsonSerializer.Serialize(new { content }));
            return ValueTask.FromResult(new AgentToolResult(document.RootElement.Clone()));
        }
    }

    /// <summary>记录审批调用，便于观察"是否真的问了"。</summary>
    private sealed class RecordingApprover : IToolApprover
    {
        public ValueTask<ToolApprovalDecision> RequestApprovalAsync(
            ToolApprovalRequest request, CancellationToken cancellationToken = default)
        {
            Console.WriteLine($"  [审批] {request.Tool.Name} 权限={request.Permission.Access}");
            return ValueTask.FromResult(ToolApprovalDecision.Allow("样例：自动批准"));
        }
    }

    /// <summary>
    /// 脚本化的假模型：按顺序产出两轮响应。
    /// </summary>
    /// <remarks>
    /// 真实 provider 的协议形状（工具声明放哪、参数怎么分片、终结信号在哪）
    /// **无法**用这个类验证——那必须用真端点。它只验证控制流。
    /// </remarks>
    private sealed class ScriptedClient : IChatClient
    {
        private int _turn;

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("样例只走流式路径。");

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation]
            CancellationToken cancellationToken = default)
        {
            _turn++;

            if (_turn == 1)
            {
                await Task.CompletedTask;
                yield return new ChatResponseUpdate(ChatRole.Assistant, [new TextContent("我来读文件。")]);
                yield return new ChatResponseUpdate(ChatRole.Assistant,
                [
                    new FunctionCallContent("call-1", "read_text",
                        new Dictionary<string, object?> { ["path"] = "greeting.txt" }),
                ]);
                yield break;
            }

            // 第二轮：从历史里找工具结果，确认它真的被回填了。
            var toolText = messages
                .Where(m => m.Role == ChatRole.Tool)
                .SelectMany(m => m.Contents.OfType<FunctionResultContent>())
                .Select(r => r.Result?.ToString() ?? "")
                .FirstOrDefault() ?? "(没有工具结果)";

            await Task.Yield();
            yield return new ChatResponseUpdate(ChatRole.Assistant, "文件内容：" + toolText);
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }
}
