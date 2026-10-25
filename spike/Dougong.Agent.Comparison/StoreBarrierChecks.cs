using System.Text.Json;
using Dougong.Agent;
using Microsoft.Extensions.AI;

namespace Dougong.Agent.Comparison;

/// <summary>
/// 提交屏障的确定性验证：用会失败的存储证明"提交失败即停止执行"，
/// 而不是靠观察日志猜测。
/// </summary>
public static class StoreBarrierChecks
{
    public static async Task<int> RunAsync()
    {
        var failures = 0;
        failures += await CheckAsync("input-commit-failure-stops-run", InputFailureStopsRunAsync);
        failures += await CheckAsync("tool-start-failure-prevents-execution", ToolStartFailurePreventsExecutionAsync);
        failures += await CheckAsync("committed-session-recovers-history", CommittedSessionRecoversAsync);
        failures += await CheckAsync("unknown-outcome-on-missing-completion", UnknownOutcomeAsync);
        failures += await CheckAsync("unknown-outcome-carries-arguments", UnknownArgumentsAsync);
        failures += await CheckAsync("read-only-unknown-does-not-block-recovery", ReadOnlyUnknownAsync);
        failures += await CheckAsync("side-effecting-unknown-blocks-recovery", SideEffectUnknownAsync);
        failures += await CheckAsync("single-writer-lease", SingleWriterLeaseAsync);
        failures += await CheckAsync("truncated-tail-is-isolated", TruncatedTailAsync);

        Console.WriteLine(failures == 0
            ? "STORE: ALL PASS"
            : $"STORE: {failures} FAILURE(S)");
        return failures;
    }

    // 1. 用户输入提交失败 → 不得调用模型、不得执行工具。
    private static async Task<string?> InputFailureStopsRunAsync()
    {
        var store = new FailingStore(failOn: StoreRecordKind.UserInput);
        var client = new CountingClient();
        var tool = new FakeTool("read_file", ["path"]);
        var dispatcher = new ToolDispatcher();
        dispatcher.Register(tool);
        var runtime = new LoopAgentRuntime(client, dispatcher, new FixedStoreAccessor(store));

        var events = await CollectAsync(runtime, "Read /tmp/a.txt using the read_file tool.");

        if (client.CallCount != 0) return $"提交失败后仍调用了模型 {client.CallCount} 次";
        if (tool.ExecutionCount != 0) return $"提交失败后仍执行了工具 {tool.ExecutionCount} 次";
        if (events.OfType<AgentEvent.RunFinished>().LastOrDefault()?.Outcome != AgentRunOutcome.Failed)
            return "未以 Failed 结束";
        return null;
    }

    // 2. 工具开始记录提交失败 → 工具不得执行（副作用屏障）。
    private static async Task<string?> ToolStartFailurePreventsExecutionAsync()
    {
        var store = new FailingStore(failOn: StoreRecordKind.ToolCallStarted);
        var client = new SingleCallClient();
        var tool = new FakeTool("read_file", ["path"]);
        var dispatcher = new ToolDispatcher();
        dispatcher.Register(tool);
        var runtime = new LoopAgentRuntime(client, dispatcher, new FixedStoreAccessor(store));

        var events = await CollectAsync(runtime, "Read /tmp/a.txt using the read_file tool.");

        if (tool.ExecutionCount != 0)
            return $"工具开始记录提交失败后仍执行了工具 {tool.ExecutionCount} 次";
        if (events.OfType<AgentEvent.RunFinished>().LastOrDefault()?.Outcome != AgentRunOutcome.Failed)
            return "未以 Failed 结束";
        return null;
    }

    // 3. 正常提交的会话可回放出一致历史。
    private static async Task<string?> CommittedSessionRecoversAsync()
    {
        var path = Path.Combine(Path.GetTempPath(), $"dougong-store-{Guid.NewGuid():N}.jsonl");
        try
        {
            await using (var store = await JsonlAgentStore.OpenAsync(path, "s1"))
            {
                var client = new SingleCallClient();
                var tool = new FakeTool("read_file", ["path"]);
                var dispatcher = new ToolDispatcher();
                dispatcher.Register(tool);
                var runtime = new LoopAgentRuntime(client, dispatcher, new FixedStoreAccessor(store));
                await CollectAsync(runtime, "Read /tmp/a.txt using the read_file tool.");
            }

            await using var reopened = await JsonlAgentStore.OpenAsync(path, "s1");
            var recovered = await SessionRecovery.ReplayAsync(reopened);

            if (recovered.History.Count == 0) return "恢复后没有历史";
            if (recovered.Unknown.Count > 0) return $"正常会话出现未知结果 {recovered.Unknown.Count} 条";
            if (recovered.HasSequenceGap) return "正常会话出现序号缺口";
            return null;
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }

    // 4. 有开始无终态 → 必须报 OutcomeUnknown，且不自动重放。
    private static async Task<string?> UnknownOutcomeAsync()
    {
        var path = Path.Combine(Path.GetTempPath(), $"dougong-unknown-{Guid.NewGuid():N}.jsonl");
        try
        {
            await using (var store = await JsonlAgentStore.OpenAsync(path, "s2"))
            {
                await CommitAsync(store, StoreRecordKind.UserInput, "do it");
                await CommitAsync(store, StoreRecordKind.ToolCallStarted,
                    JsonSerializer.Serialize(new { id = "c1", tool = "write_file" }));
                // 故意不提交终态，模拟崩溃窗口。
            }

            await using var reopened = await JsonlAgentStore.OpenAsync(path, "s2");
            var recovered = await SessionRecovery.ReplayAsync(reopened);

            if (!recovered.RequiresReconciliation) return "没有报出需要人工对账";
            var unknown = recovered.Unknown.SingleOrDefault();
            if (unknown is null) return "未知结果条目缺失";
            if (unknown.InvocationId != "c1") return $"调用 ID 不符：{unknown.InvocationId}";
            return null;
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }

    // 5. 单写者租约：第二个打开必须失败。
    private static async Task<string?> SingleWriterLeaseAsync()
    {
        var path = Path.Combine(Path.GetTempPath(), $"dougong-lease-{Guid.NewGuid():N}.jsonl");
        try
        {
            await using var first = await JsonlAgentStore.OpenAsync(path, "s3");
            var error = await RecordAsync(async () =>
            {
                await using var second = await JsonlAgentStore.OpenAsync(path, "s3");
            });
            if (error is null) return "第二个写入者被允许打开，单写者租约失效";
            return null;
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }

    // 6. 尾部半条记录必须被隔离，不能污染已提交部分。
    private static async Task<string?> TruncatedTailAsync()
    {
        var path = Path.Combine(Path.GetTempPath(), $"dougong-tail-{Guid.NewGuid():N}.jsonl");
        try
        {
            await using (var store = await JsonlAgentStore.OpenAsync(path, "s4"))
            {
                await CommitAsync(store, StoreRecordKind.UserInput, "first");
                await CommitAsync(store, StoreRecordKind.UserInput, "second");
            }

            // 追加一条被截断的记录（模拟写入中途崩溃）。
            await File.AppendAllTextAsync(path, "{\"Sequence\":3,\"Kind\":\"UserInp");

            await using var reopened = await JsonlAgentStore.OpenAsync(path, "s4");
            var recovered = await SessionRecovery.ReplayAsync(reopened);

            if (recovered.History.Count != 2)
                return $"期望恢复 2 条已提交记录，实际 {recovered.History.Count}";
            return null;
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }

    private static async Task CommitAsync(IAgentStore store, StoreRecordKind kind, string payload)
    {
        // 已经是 JSON 的按结构存，纯文本按字符串存——与运行时写入一致。
        JsonElement element;
        try { element = JsonDocument.Parse(payload).RootElement.Clone(); }
        catch (JsonException) { element = JsonSerializer.SerializeToElement(payload); }
        await store.CommitAsync(new StoreRecord(0, kind, element));
    }

    private static async Task<IReadOnlyList<AgentEvent>> CollectAsync(
        IAgentRuntime runtime, string input)
    {
        var events = new List<AgentEvent>();
        await foreach (var e in runtime.RunAsync(new AgentRunRequest("t", "turn", [], input)))
            events.Add(e);
        return events;
    }

    private static async Task<Exception?> RecordAsync(Func<Task> action)
    {
        try { await action(); return null; }
        catch (Exception ex) { return ex; }
    }

    /// <summary>
    /// 对账出的未知结果必须带**参数原文**（G10 的一部分）。
    /// </summary>
    /// <remarks>
    /// 只有 invocation id 和工具名时，宿主唯一能做的就是问用户"你还记得那个写操作吗"——
    /// 那不叫对账。这条断言参数真的入库、真的能读回来。
    /// </remarks>
    private static async Task<string?> UnknownArgumentsAsync()
    {
        var path = Path.Combine(Path.GetTempPath(), $"dougong-args-{Guid.NewGuid():N}.jsonl");
        try
        {
            var argumentsJson = JsonSerializer.Serialize(new
            {
                path = "a.txt",
                lines = new[] { "x" },
            });

            var started = new
            {
                id = "c9",
                tool = "write_file",
                arguments = argumentsJson,
            };

            // 手工写两条记录：调用开始（带参数）+ 没有终态。
            await using (var store = await JsonlAgentStore.OpenAsync(path, "s-args"))
            {
                await store.CommitAsync(new StoreRecord(
                    0, StoreRecordKind.UserInput, Json("""{"text":"写文件"}""")));

                await store.CommitAsync(new StoreRecord(
                    0, StoreRecordKind.ToolCallStarted,
                    Json(JsonSerializer.Serialize(started))));
            }

            await using var reopened = await JsonlAgentStore.OpenAsync(path, "s-args");
            var recovered = await SessionRecovery.ReplayAsync(reopened);

            if (recovered.Unknown.Count != 1)
                return $"应有 1 条未知结果，实际 {recovered.Unknown.Count}";

            var unknown = recovered.Unknown[0];
            if (unknown.ArgumentsJson is null)
                return "未知结果没有带参数原文：宿主无法核实副作用";

            // 参数必须保持原结构，不能被折叠成字符串。
            if (!unknown.ArgumentsJson.Contains("path", StringComparison.Ordinal))
                return $"参数内容不符：{unknown.ArgumentsJson}";
            if (!unknown.ArgumentsJson.Contains("[", StringComparison.Ordinal))
                return $"数组参数被折叠：{unknown.ArgumentsJson}";

            return null;
        }
        finally
        {
            try { File.Delete(path); } catch (IOException) { }
        }
    }

    /// <summary>
    /// 只读工具崩溃**不该**让会话无法恢复。
    /// </summary>
    /// <remarks>
    /// 此前所有未知结果都被一律拒绝恢复——一次只读工具崩溃会让整个会话报废。
    /// 现在按工具声明区分：无副作用的可以继续。
    /// </remarks>
    private static async Task<string?> ReadOnlyUnknownAsync()
    {
        var path = Path.Combine(Path.GetTempPath(), $"dougong-ro-{Guid.NewGuid():N}.jsonl");
        try
        {
            var started = new
            {
                id = "c1",
                tool = "read_file",
                arguments = """{"path":"a.txt"}""",
                mayHaveIrreversibleEffects = false,   // 只读工具显式声明
            };

            await using (var store = await JsonlAgentStore.OpenAsync(path, "s-ro"))
            {
                await store.CommitAsync(new StoreRecord(
                    0, StoreRecordKind.ToolCallStarted, Json(JsonSerializer.Serialize(started))));
            }

            await using var reopened = await JsonlAgentStore.OpenAsync(path, "s-ro");
            var recovered = await SessionRecovery.ReplayAsync(reopened);

            if (recovered.Unknown.Count != 1) return $"应有 1 条未知结果，实际 {recovered.Unknown.Count}";
            if (recovered.RequiresReconciliation)
                return "只读工具的未知结果不应阻塞恢复";
            if (recovered.DescribeBlockingReason() is { } reason)
                return $"只读工具不该产出阻塞原因：{reason}";
            if (recovered.Retryable.Count != 1)
                return $"应被列为可重试，实际 {recovered.Retryable.Count} 条";

            return null;
        }
        finally
        {
            try { File.Delete(path); } catch (IOException) { }
        }
    }

    /// <summary>可能有不可撤销副作用的工具崩溃，必须阻塞并要求人工核实。</summary>
    private static async Task<string?> SideEffectUnknownAsync()
    {
        var path = Path.Combine(Path.GetTempPath(), $"dougong-rw-{Guid.NewGuid():N}.jsonl");
        try
        {
            var started = new
            {
                id = "c2",
                tool = "write_file",
                arguments = """{"path":"a.txt"}""",
                mayHaveIrreversibleEffects = true,
            };

            await using (var store = await JsonlAgentStore.OpenAsync(path, "s-rw"))
            {
                await store.CommitAsync(new StoreRecord(
                    0, StoreRecordKind.ToolCallStarted, Json(JsonSerializer.Serialize(started))));
            }

            await using var reopened = await JsonlAgentStore.OpenAsync(path, "s-rw");
            var recovered = await SessionRecovery.ReplayAsync(reopened);

            if (!recovered.RequiresReconciliation)
                return "有副作用的工具崩溃必须要求人工核实";
            if (recovered.DescribeBlockingReason() is not { } reason)
                return "必须产出阻塞原因";
            if (!reason.Contains("对账", StringComparison.Ordinal))
                return $"阻塞原因未说明要对账：{reason}";
            if (recovered.Retryable.Count != 0)
                return "有副作用的未知结果不该被列为可重试";

            return null;
        }
        finally
        {
            try { File.Delete(path); } catch (IOException) { }
        }
    }

    private static System.Text.Json.JsonElement Json(string raw)
    {
        using var document = System.Text.Json.JsonDocument.Parse(raw);
        return document.RootElement.Clone();
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

    /// <summary>在指定记录类型上必然失败的存储，用于证明屏障真的拦截执行。</summary>
    private sealed class FailingStore(StoreRecordKind failOn) : IAgentStore
    {
        public string SessionId => "failing";
        public StoreDurability Durability => StoreDurability.ProcessMemory;

        public ValueTask CommitAsync(StoreRecord record, CancellationToken cancellationToken = default)
        {
            if (record.Kind == failOn)
                throw new StoreCommitException(record.Kind.ToString(), "注入的提交失败");
            return ValueTask.CompletedTask;
        }

        public async IAsyncEnumerable<StoreRecord> ReadCommittedAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>永远返回纯文本，用于观察"提交失败时模型是否被调用"。</summary>
    private sealed class CountingClient : IChatClient
    {
        public int CallCount { get; private set; }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "text")));

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            CallCount++;
            yield return new ChatResponseUpdate(ChatRole.Assistant, "text");
            await Task.CompletedTask;
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    /// <summary>第一次返回工具调用，之后返回文本。</summary>
    private sealed class SingleCallClient : IChatClient
    {
        private int _calls;

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            _calls++;
            if (_calls == 1)
            {
                yield return new ChatResponseUpdate(ChatRole.Assistant,
                    [new FunctionCallContent("c1", "read_file",
                        new Dictionary<string, object?> { ["path"] = "/tmp/a.txt" })]);
            }
            else
            {
                yield return new ChatResponseUpdate(ChatRole.Assistant, "done");
            }

            await Task.CompletedTask;
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
