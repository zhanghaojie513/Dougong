using System.Text.Json;
using Dougong.Agent;
using Microsoft.Extensions.AI;

namespace Dougong.Agent.Comparison;

/// <summary>
/// M1 故障验收（docs/architecture.md §4.4）：并发编辑、读写竞争、取消不合作、
/// 单一执行入口。这些是"M1 验收包含"清单里尚未被其他套件覆盖的部分。
/// </summary>
public static class FailureAcceptanceChecks
{
    public static async Task<int> RunAsync()
    {
        var failures = 0;
        failures += await CheckAsync("same-file-concurrent-edit-is-serialised", ConcurrentEditAsync);
        failures += await CheckAsync("read-write-race-sees-consistent-violation", ReadWriteRaceAsync);
        failures += await CheckAsync("uncooperative-cancel-still-holds-lock", UncooperativeCancelAsync);
        failures += await CheckAsync("single-execution-entry-under-concurrency", SingleEntryAsync);
        failures += await CheckAsync("commit-failure-stops-before-side-effect", CommitBeforeSideEffectAsync);
        failures += await CheckAsync("crash-window-reports-unknown-not-replay", CrashWindowAsync);
        failures += await CheckAsync("runtime-persists-arguments-with-call-start", RuntimeArgumentsAsync);

        Console.WriteLine(failures == 0
            ? "FAILURE-ACCEPTANCE: ALL PASS"
            : $"FAILURE-ACCEPTANCE: {failures} FAILURE(S)");
        return failures;
    }

    // 同文件并发编辑：工作区独占必须把两次写串行化，不能交织。
    private static async Task<string?> ConcurrentEditAsync()
    {
        var root = NewRoot();
        try
        {
            var target = Path.Combine(root, "shared.txt");
            await File.WriteAllTextAsync(target, "start");

            using var gate = new ToolResourceGate();
            var order = new List<string>();
            var firstEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            var tool = new SlowWriteTool(target, order, firstEntered, releaseFirst, blockFirst: true);
            var dispatcher = new ToolDispatcher(
                gate: gate, workspace: root, pathGuard: new WorkspacePathGuard(root));
            dispatcher.Register(tool);

            var first = dispatcher.DispatchAsync(Invocation("e1", tool)).AsTask();
            await firstEntered.Task;

            var second = dispatcher.DispatchAsync(Invocation("e2", tool)).AsTask();
            var secondBlocked = await Task.WhenAny(second, Task.Delay(300)) != second;

            releaseFirst.TrySetResult();
            await Task.WhenAll(first, second);

            if (!secondBlocked) return "同文件并发编辑未串行化";
            lock (order)
            {
                // 必须是一段完整结束后另一段才开始，不能出现 a-start,b-start 交错。
                if (order.Count < 4) return $"执行记录不完整：{string.Join(",", order)}";
                if (order[1].Contains("start") && order[1].StartsWith("b"))
                    return $"执行交织：{string.Join(",", order)}";
            }

            return null;
        }
        finally { TryDelete(root); }
    }

    // 读写竞争：读工具看到的是写入前的版本，审批后文件变化必须被拦。
    private static async Task<string?> ReadWriteRaceAsync()
    {
        var root = NewRoot();
        try
        {
            var target = Path.Combine(root, "data.txt");
            await File.WriteAllTextAsync(target, "v1");

            var readTool = new FileTool("read_file", target, ToolAccess.Read);
            var writeTool = new FileTool("write_file", target, ToolAccess.Write);

            // 读工具在审批期间文件被改写：复核必须拒绝。
            var dispatcher = new ToolDispatcher(
                new Approver(async () =>
                {
                    await File.WriteAllTextAsync(target, "v2-changed-during-approval");
                    return ToolApprovalDecision.Allow();
                }),
                workspace: root,
                pathGuard: new WorkspacePathGuard(root));
            dispatcher.Register(readTool);
            dispatcher.Register(writeTool);

            var result = await dispatcher.DispatchAsync(Invocation("r1", readTool));
            if (result.Outcome != ToolCallOutcome.RejectedNotApproved)
                return $"读写竞争未被拦下，结果 {result.Outcome}";
            return readTool.ExecutionCount == 0 ? null : "竞争下仍然执行了读取";
        }
        finally { TryDelete(root); }
    }

    // 取消不合作：工具忽略取消并继续持有工作区锁，此时第二个写必须仍被阻塞。
    private static async Task<string?> UncooperativeCancelAsync()
    {
        var root = NewRoot();
        try
        {
            using var gate = new ToolResourceGate();
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var tool = new UncooperativeTool(entered, release);
            var other = new FileTool("other_write", Path.Combine(root, "x.txt"), ToolAccess.Write);

            var dispatcher = new ToolDispatcher(
                gate: gate, workspace: root, pathGuard: new WorkspacePathGuard(root));
            dispatcher.Register(tool);
            dispatcher.Register(other);

            using var cts = new CancellationTokenSource();
            var running = dispatcher.DispatchAsync(Invocation("u1", tool), cts.Token).AsTask();
            await entered.Task;

            cts.Cancel();
            await Task.Delay(100);   // 给取消传播一点时间

            var second = dispatcher.DispatchAsync(Invocation("u2", other)).AsTask();
            var secondBlocked = await Task.WhenAny(second, Task.Delay(300)) != second;

            release.TrySetResult();
            var firstResult = await running.WaitAsync(TimeSpan.FromSeconds(3));
            await second.WaitAsync(TimeSpan.FromSeconds(3));

            if (!secondBlocked)
                return "不合作工具取消后提前释放了锁，可能出现重叠执行";

            // 文档 §4.4：已执行者不得伪装已撤销。工具忽略取消并跑完，终态必须是"已执行"，
            // 并把"取消被忽略、副作用可能已发生"作为诊断暴露给宿主。
            if (firstResult.Outcome != ToolCallOutcome.Succeeded)
                return $"不合作工具跑完后应报已执行，实际 {firstResult.Outcome}";
            return firstResult.Diagnostic?.Contains("未响应取消", StringComparison.Ordinal) == true
                ? null
                : $"缺少取消被忽略的诊断，实际 {firstResult.Diagnostic}";
        }
        finally { TryDelete(root); }
    }

    // 单一执行入口：并发派发下，工具执行次数必须等于 dispatcher 记录的派发次数。
    private static async Task<string?> SingleEntryAsync()
    {
        var root = NewRoot();
        try
        {
            var tool = new CountingTool();
            var dispatcher = new ToolDispatcher(
                workspace: root, pathGuard: new WorkspacePathGuard(root));
            dispatcher.Register(tool);

            var tasks = Enumerable.Range(0, 24)
                .Select(i => dispatcher.DispatchAsync(Invocation($"c{i}", tool)).AsTask())
                .ToArray();
            await Task.WhenAll(tasks);

            var succeeded = tasks.Count(t => t.Result.Outcome == ToolCallOutcome.Succeeded);
            if (tool.ExecutionCount != succeeded)
                return $"执行 {tool.ExecutionCount} 次但报告成功 {succeeded} 次";
            return succeeded == 24 ? null : $"应全部成功，实际 {succeeded}/24";
        }
        finally { TryDelete(root); }
    }

    // 提交失败必须发生在副作用之前。
    private static async Task<string?> CommitBeforeSideEffectAsync()
    {
        var root = NewRoot();
        try
        {
            var target = Path.Combine(root, "must-not-exist.txt");
            var tool = new WriteOnceTool(target);
            var store = new FailingStore(StoreRecordKind.ToolCallStarted);
            var dispatcher = new ToolDispatcher(
                workspace: root, pathGuard: new WorkspacePathGuard(root));
            dispatcher.Register(tool);
            var runtime = new LoopAgentRuntime(
                new OneToolCallClient("c1", "write_file",
                    """{"path":"must-not-exist.txt","content":"x"}"""),
                dispatcher, new FixedStoreAccessor(store));

            var events = new List<AgentEvent>();
            await foreach (var e in runtime.RunAsync(new AgentRunRequest("s", "t", [], "write it")))
                events.Add(e);

            if (File.Exists(target)) return "提交失败后仍产生了副作用";
            return null;
        }
        finally { TryDelete(root); }
    }

    // 崩溃窗口：有开始无终态 → 恢复必须报 unknown，不重放。
    private static async Task<string?> CrashWindowAsync()
    {
        var root = NewRoot();
        var sessionPath = Path.Combine(root, "session.jsonl");
        try
        {
            await using (var store = await JsonlAgentStore.OpenAsync(sessionPath, "crash"))
            {
                await store.CommitAsync(Record(StoreRecordKind.UserInput, "do it"));
                await store.CommitAsync(Record(StoreRecordKind.ToolCallStarted,
                    JsonSerializer.Serialize(new { id = "c1", tool = "write_file" })));
                // 模拟崩溃：不写终态。
            }

            await using var reopened = await JsonlAgentStore.OpenAsync(sessionPath, "crash");
            var recovered = await SessionRecovery.ReplayAsync(reopened);

            if (!recovered.RequiresReconciliation) return "崩溃窗口未报出对账需求";
            if (recovered.Unknown.Single().InvocationId != "c1") return "未知结果 ID 不符";
            return null;
        }
        finally { TryDelete(root); }
    }

    /// <summary>
    /// 真实运行时提交"调用开始"时必须带参数（G10）。
    /// </summary>
    /// <remarks>
    /// 为什么要走真实运行时而不是手工写记录：手工写的记录当然带参数——
    /// 那测的是"我能不能写对 JSON"，不是"运行时会不会写参数"。
    /// 上一版的 G10 验收就是这个毛病，反证时没能命中。
    /// </remarks>
    private static async Task<string?> RuntimeArgumentsAsync()
    {
        var root = NewRoot();
        try
        {
            // 用内存存储：文件版有单写者租约，重放前必须先关闭，
            // 而本用例要在同一个存储上跑完再重放，用文件反而绕。
            var recording = new InMemoryAgentStore("s-args");

            // 让"终态"提交失败，而"开始"已落盘——这才是真实的崩溃窗口。
            // 仅让工具抛异常不行：那会被 dispatcher 记成失败终态并正常提交，
            // 不产生"未知"。
            var store = new FailingStore(StoreRecordKind.ToolCallCompleted, recording);
            try
            {
                var dispatcher = new ToolDispatcher(
                    workspace: root, pathGuard: new WorkspacePathGuard(root));
                dispatcher.Register(new OkTool());

                var runtime = new LoopAgentRuntime(
                    new ScriptedChatClient(
                        new ScriptedTurn(
                            new ScriptedUpdate(
                                FunctionCallId: "c7",
                                FunctionName: "boom",
                                ArgumentsJson: """{"path":"target.txt","content":"payload-42"}"""))),
                    dispatcher,
                    new FixedStoreAccessor(store));

                try
                {
                    await foreach (var _ in runtime.RunAsync(
                        new AgentRunRequest("s-args", "t1", [], "go")))
                    {
                    }
                }
                catch (Exception)
                {
                    // 工具崩溃经由 dispatcher 变成失败终态，正常不应抛到这里；
                    // 真抛了也不影响本检查要看的"开始记录"。
                }
            }
            finally
            {
                await store.DisposeAsync();
            }

            // 用同一个内存存储重放：文件版有单写者租约，重开前必须先关闭。
            var recovered = await SessionRecovery.ReplayAsync(recording);

            if (recovered.Unknown.Count == 0)
                return "崩溃窗口未报出未知结果";

            var unknown = recovered.Unknown[0];
            if (unknown.ArgumentsJson is null)
                return "运行时提交的调用开始记录不含参数：恢复后宿主无法核实副作用";

            if (!unknown.ArgumentsJson.Contains("payload-42", StringComparison.Ordinal))
                return $"参数内容不符：{unknown.ArgumentsJson}";

            return null;
        }
        finally { TryDelete(root); }
    }

    /// <summary>正常执行的工具：真正制造"未知"的是终态提交失败，不是它。</summary>
    private sealed class OkTool : IAgentTool
    {
        public ToolDescriptor Descriptor { get; } = new(
            "boom", "boom", "1.0.0", "正常工具",
            JsonDocument.Parse(
                """{"type":"object","properties":{"path":{"type":"string"},"content":{"type":"string"}}}""")
                .RootElement.Clone());

        public ToolPermission DescribePermission(JsonElement arguments) =>
            new(ToolAccess.Write, IsKnown: true, Paths: [], Commands: [], Domains: []);

        public ToolExecutionPlan DescribeExecution(JsonElement arguments) =>
            new(ToolConcurrency.Serial) { Writes = ["target.txt"] };

        public ValueTask<AgentToolResult> ExecuteAsync(
            ToolInvocation invocation, CancellationToken cancellationToken)
        {
            using var document = JsonDocument.Parse("""{"ok":true}""");
            return ValueTask.FromResult(new AgentToolResult(document.RootElement.Clone()));
        }
    }

    /// <summary>执行即抛异常的工具：保留用于其它场景。</summary>
    private sealed class CrashingTool : IAgentTool
    {
        public ToolDescriptor Descriptor { get; } = new(
            "boom", "boom", "1.0.0", "崩溃工具",
            JsonDocument.Parse(
                """{"type":"object","properties":{"path":{"type":"string"},"content":{"type":"string"}}}""")
                .RootElement.Clone());

        public ToolPermission DescribePermission(JsonElement arguments) =>
            new(ToolAccess.Write, IsKnown: true, Paths: [], Commands: [], Domains: []);

        public ToolExecutionPlan DescribeExecution(JsonElement arguments) =>
            new(ToolConcurrency.Serial) { Writes = ["target.txt"] };

        public ValueTask<AgentToolResult> ExecuteAsync(
            ToolInvocation invocation, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("模拟工具崩溃");
    }

    private static StoreRecord Record(StoreRecordKind kind, string payload)
    {
        // 已经是 JSON 的按结构存，纯文本按字符串存——与运行时写入一致。
        JsonElement element;
        try { element = JsonDocument.Parse(payload).RootElement.Clone(); }
        catch (JsonException) { element = JsonSerializer.SerializeToElement(payload); }
        return new StoreRecord(0, kind, element);
    }

    private static ToolInvocation Invocation(string id, IAgentTool tool)
    {
        using var document = JsonDocument.Parse("""{"path":"shared.txt","content":"v"}""");
        return new ToolInvocation(id, tool.Descriptor.Id, document.RootElement.Clone());
    }

    private static string NewRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "dg-fa-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(root);
        return root;
    }

    private static void TryDelete(string root)
    {
        try { Directory.Delete(root, recursive: true); } catch { }
    }

    private static async Task<int> CheckAsync(string name, Func<Task<string?>> action)
    {
        try
        {
            var failure = await action();
            if (failure is null)
            {
                Console.WriteLine($"PASS {name}");
                return 0;
            }

            Console.WriteLine($"FAIL {name}: {failure}");
            return 1;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"FAIL {name}: {ex.GetType().Name}: {ex.Message}");
            return 1;
        }
    }

    private sealed class Approver(Func<Task<ToolApprovalDecision>> decide) : IToolApprover
    {
        public async ValueTask<ToolApprovalDecision> RequestApprovalAsync(
            ToolApprovalRequest request, CancellationToken cancellationToken = default) =>
            await decide().ConfigureAwait(false);
    }

    private sealed class SlowWriteTool(
        string target, List<string> order,
        TaskCompletionSource entered, TaskCompletionSource release, bool blockFirst) : IAgentTool
    {
        private int _calls;

        public ToolDescriptor Descriptor { get; } = new(
            "write_file", "write_file", "1.0.0", "slow write",
            JsonDocument.Parse("""{"type":"object"}""").RootElement.Clone());

        public ToolPermission DescribePermission(JsonElement a) =>
            new(ToolAccess.Write, true, [target], [], []);

        public ToolExecutionPlan DescribeExecution(JsonElement a) =>
            new(ToolConcurrency.WorkspaceExclusive);

        public IReadOnlyList<string> DescribeFileDependencies(JsonElement a) => [target];

        public async ValueTask<AgentToolResult> ExecuteAsync(
            ToolInvocation invocation, CancellationToken cancellationToken)
        {
            var n = ++_calls;
            var label = n == 1 ? "a" : "b";
            lock (order) order.Add($"{label}-start");

            if (n == 1 && blockFirst)
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(cancellationToken);
            }

            lock (order) order.Add($"{label}-end");
            using var document = JsonDocument.Parse("""{"ok":true}""");
            return new AgentToolResult(document.RootElement.Clone());
        }
    }

    private sealed class UncooperativeTool(
        TaskCompletionSource entered, TaskCompletionSource release) : IAgentTool
    {
        public ToolDescriptor Descriptor { get; } = new(
            "stubborn", "stubborn", "1.0.0", "ignores cancellation",
            JsonDocument.Parse("""{"type":"object"}""").RootElement.Clone());

        public ToolPermission DescribePermission(JsonElement a) =>
            new(ToolAccess.Execute, true, [], [], []);

        // 故意声明工作区独占：取消后仍持锁，第二个调用必须被挡。
        public ToolExecutionPlan DescribeExecution(JsonElement a) =>
            new(ToolConcurrency.WorkspaceExclusive);

        public async ValueTask<AgentToolResult> ExecuteAsync(
            ToolInvocation invocation, CancellationToken cancellationToken)
        {
            entered.TrySetResult();
            // 忽略 cancellationToken：模拟不合作工具。
            await release.Task;
            using var document = JsonDocument.Parse("""{"ok":true}""");
            return new AgentToolResult(document.RootElement.Clone());
        }
    }

    private sealed class CountingTool : IAgentTool
    {
        private int _count;
        public int ExecutionCount => Volatile.Read(ref _count);

        public ToolDescriptor Descriptor { get; } = new(
            "count", "count", "1.0.0", "count",
            JsonDocument.Parse("""{"type":"object"}""").RootElement.Clone());

        public ToolPermission DescribePermission(JsonElement a) =>
            new(ToolAccess.None, true, [], [], []);
        public ToolExecutionPlan DescribeExecution(JsonElement a) => ToolExecutionPlan.Serial;

        public ValueTask<AgentToolResult> ExecuteAsync(
            ToolInvocation invocation, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _count);
            using var document = JsonDocument.Parse("""{"ok":true}""");
            return ValueTask.FromResult(new AgentToolResult(document.RootElement.Clone()));
        }
    }

    private sealed class WriteOnceTool(string target) : IAgentTool
    {
        public ToolDescriptor Descriptor { get; } = new(
            "write_file", "write_file", "1.0.0", "write",
            JsonDocument.Parse("""{"type":"object"}""").RootElement.Clone());

        public ToolPermission DescribePermission(JsonElement a) =>
            new(ToolAccess.Write, true, [target], [], []);
        public ToolExecutionPlan DescribeExecution(JsonElement a) => ToolExecutionPlan.Serial;

        public ValueTask<AgentToolResult> ExecuteAsync(
            ToolInvocation invocation, CancellationToken cancellationToken)
        {
            File.WriteAllText(target, "side-effect");
            using var document = JsonDocument.Parse("""{"ok":true}""");
            return ValueTask.FromResult(new AgentToolResult(document.RootElement.Clone()));
        }
    }

    private sealed class FileTool(string id, string path, ToolAccess access) : IAgentTool
    {
        public int ExecutionCount { get; private set; }

        public ToolDescriptor Descriptor { get; } = new(
            id, id, "1.0.0", "file",
            JsonDocument.Parse("""{"type":"object"}""").RootElement.Clone());

        public ToolPermission DescribePermission(JsonElement a) =>
            new(access, true, [path], [], []);
        public ToolExecutionPlan DescribeExecution(JsonElement a) => ToolExecutionPlan.Serial;
        public IReadOnlyList<string> DescribeFileDependencies(JsonElement a) => [path];

        public ValueTask<AgentToolResult> ExecuteAsync(
            ToolInvocation invocation, CancellationToken cancellationToken)
        {
            ExecutionCount++;
            using var document = JsonDocument.Parse("""{"ok":true}""");
            return ValueTask.FromResult(new AgentToolResult(document.RootElement.Clone()));
        }
    }

    /// <summary>
    /// 让指定类型的记录提交失败，其余照常写入底层存储。
    /// </summary>
    /// <remarks>
    /// 必须**真正写入**其余记录：否则"开始"也没落盘，恢复时看不到任何窗口，
    /// 造不出"有开始、无终态"的场景。
    /// </remarks>
    private sealed class FailingStore(StoreRecordKind failOn, IAgentStore? inner = null) : IAgentStore
    {
        public string SessionId => inner?.SessionId ?? "failing";
        public StoreDurability Durability => inner?.Durability ?? StoreDurability.ProcessMemory;

        public ValueTask CommitAsync(StoreRecord record, CancellationToken cancellationToken = default)
        {
            if (record.Kind == failOn)
                throw new StoreCommitException(record.Kind.ToString(), "注入失败");

            return inner is null
                ? ValueTask.CompletedTask
                : inner.CommitAsync(record, cancellationToken);
        }

        public async IAsyncEnumerable<StoreRecord> ReadCommittedAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class OneToolCallClient(string id, string tool, string args) : IChatClient
    {
        private int _calls;

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> m, ChatOptions? o = null, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> m, ChatOptions? o = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            _calls++;
            if (_calls == 1)
            {
                using var document = JsonDocument.Parse(args);
                var dict = document.RootElement.EnumerateObject()
                    .ToDictionary(p => p.Name, p => (object?)p.Value.Clone());
                yield return new ChatResponseUpdate(ChatRole.Assistant,
                    [new FunctionCallContent(id, tool, dict)]);
            }
            else
            {
                yield return new ChatResponseUpdate(ChatRole.Assistant, "done");
            }

            await Task.CompletedTask;
        }

        public object? GetService(Type t, object? k = null) => null;
        public void Dispose() { }
    }
}
