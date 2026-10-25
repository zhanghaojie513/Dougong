using System.Text;
using System.Text.Json;
using Dougong.Agent;
using Microsoft.Extensions.AI;

namespace Dougong.Agent.Comparison;

/// <summary>
/// 验证 L1 效果测量装置本身是否有效——**不测量记忆效果**。
/// </summary>
/// <remarks>
/// 这一套存在的原因：验收 1 需要真实模型与费用，因此平时跑不了。
/// 但"跑不了"不等于"装置没被验证过"。如果判据写错（例如把英文回答判成中文），
/// 那么将来用真实模型跑出的数字就是错的，而且没人能发现。
/// <para>
/// 所以这里用一个**行为完全确定的假模型**，验证三件事：
/// <list type="number">
/// <item>判据能区分遵循与不遵循（正例返回遵循，反例返回未遵循）</item>
/// <item>注入链路真的把偏好送到了模型面前（假模型记录它收到的 system 消息）</item>
/// <item>无记忆组确实收不到偏好（否则对照实验无效）</item>
/// </list>
/// </para>
/// <para>
/// <b>明确不说明的事</b>：本套全绿只证明"尺子是准的"，不证明"记忆有用"。
/// 后者必须等真实模型跑 <c>-- l1-effect</c>。
/// </para>
/// </remarks>
public static class L1HarnessChecks
{
    public static async Task<int> RunAsync()
    {
        var failures = 0;
        failures += Check("criteria-accept-following", CriteriaAcceptFollowing);
        failures += Check("criteria-reject-non-following", CriteriaRejectNonFollowing);
        failures += Check("criteria-tolerate-alternative-shapes", CriteriaTolerateAlternatives);
        failures += await CheckAsync("memory-reaches-the-model", MemoryReachesModelAsync);
        failures += await CheckAsync("no-memory-group-sees-nothing", NoMemoryGroupAsync);
        failures += await CheckAsync("prompt-does-not-leak-preference", PromptIsNeutralAsync);

        Console.WriteLine(failures == 0
            ? "L1-HARNESS: ALL PASS（尺子准；记忆效果仍未测）"
            : $"L1-HARNESS: {failures} FAILURE(S)");
        return failures;
    }

    /// <summary>正例：明显遵循的回答必须被判为遵循。</summary>
    private static string? CriteriaAcceptFollowing()
    {
        var language = HeldOutTasks.All.First(t => t.Name == "explain-http-cache");
        if (!language.IsFollowed("HTTP 缓存通过 Cache-Control 头控制，浏览器按 max-age 决定是否复用。"))
            return "中文回答未判为遵循 language=中文";

        var bullets = HeldOutTasks.All.First(t => t.Name == "list-review-tips");
        if (!bullets.IsFollowed("- 先看意图\n- 再看实现\n- 最后看测试"))
            return "列表回答未判为遵循 output.format=bullets";

        var naming = HeldOutTasks.All.First(t => t.Name == "name-config-fields");
        if (!naming.IsFollowed("建议字段：max_retry_count、request_timeout_ms、enable_cache。"))
            return "snake_case 回答未判为遵循";

        // 混合文本：多数是 snake_case、偶尔一个 camelCase，应算遵循（响应了偏好但不完美）。
        if (!naming.IsFollowed("字段：max_retry_count、request_timeout_ms、enableCache。"))
            return "以 snake_case 为主但含个别 camelCase 的回答被判为未遵循：会低估效果";

        return null;
    }

    /// <summary>
    /// 反例：明显不遵循的回答必须被判为未遵循。
    /// </summary>
    /// <remarks>
    /// 这一条比正例更重要：只有正例时，一个"永远返回 true"的判据也能全绿。
    /// </remarks>
    private static string? CriteriaRejectNonFollowing()
    {
        var language = HeldOutTasks.All.First(t => t.Name == "explain-http-cache");
        if (language.IsFollowed("HTTP caching uses Cache-Control headers to decide reuse."))
            return "英文回答被误判为遵循 language=中文";

        var bullets = HeldOutTasks.All.First(t => t.Name == "list-review-tips");
        if (bullets.IsFollowed("第一，先看意图。第二，再看实现。第三，最后看测试。"))
            return "段落式回答被误判为遵循 output.format=bullets";

        var naming = HeldOutTasks.All.First(t => t.Name == "name-config-fields");
        if (naming.IsFollowed("建议字段：maxRetryCount、requestTimeoutMs、enableCache。"))
            return "camelCase 回答被误判为遵循 naming.convention=snake_case";

        // 以 camelCase 为主、混入一个 snake_case：仍应判为未遵循。
        // 没有这一条，"snake 多于 camel" 那一半就是死代码——它永远不会起作用。
        if (naming.IsFollowed("建议字段：maxRetryCount、requestTimeoutMs、enable_cache。"))
            return "以 camelCase 为主（仅一个 snake_case）被判为遵循：会高估效果";

        return null;
    }

    /// <summary>同一偏好的其它合理写法也要接受，避免"表达方式不同"被当成"未遵循"。</summary>
    private static string? CriteriaTolerateAlternatives()
    {
        var bullets = HeldOutTasks.All.First(t => t.Name == "list-review-tips");
        foreach (var variant in new[]
                 {
                     "* 先看意图\n* 再看实现",
                     "• 先看意图\n• 再看实现",
                     "1. 先看意图\n2. 再看实现",
                 })
        {
            if (!bullets.IsFollowed(variant))
                return $"另一种列表写法被拒：{variant.ReplaceLineEndings(" / ")}";
        }

        // 必须仍然拒绝非列表：不能为了容忍写法而放宽到"什么都算"。
        if (bullets.IsFollowed("先说意图，再说实现。"))
            return "容忍写法时放宽过头：段落被判成列表";

        return null;
    }

    /// <summary>
    /// 有记忆组：模型必须真的收到偏好段落，且收到的是这条任务的偏好。
    /// </summary>
    private static async Task<string?> MemoryReachesModelAsync()
    {
        var dir = TempDir();
        try
        {
            await using var store = await MemoryStore.OpenAsync(Path.Combine(dir, "m.jsonl"));
            var task = HeldOutTasks.All.First(t => t.Name == "explain-http-cache");

            await store.PutAsync(new MemoryArtifact(
                "eval-" + task.Name, 1,
                MemoryArtifact.ComputeHash(task.PreferenceKey, task.PreferenceValue),
                task.PreferenceKey, task.PreferenceValue,
                MemoryScope.Workspace, dir, "held-out-seed", DateTimeOffset.UtcNow,
                MemoryArtifactState.Enabled));

            var client = new RecordingClient("HTTP 缓存通过 Cache-Control 头控制。");
            var runtime = new LoopAgentRuntime(client, new ToolDispatcher())
            {
                Memory = new StoreMemoryInjector(store),
                MemoryWorkspace = dir,
            };

            await foreach (var _ in runtime.RunAsync(
                new AgentRunRequest("eval", task.Name, [], task.Prompt))) { }

            var systems = client.SystemMessages;
            if (systems.Count == 0) return "模型没有收到任何 system 消息";
            if (!systems.Any(s => s.Contains("language: 中文", StringComparison.Ordinal)))
                return "模型收到的 system 消息里没有这条偏好";

            return null;
        }
        finally { Cleanup(dir); }
    }

    /// <summary>无记忆组：模型不得看到任何偏好。这一半不成立，整组对照就是假的。</summary>
    private static async Task<string?> NoMemoryGroupAsync()
    {
        var dir = TempDir();
        try
        {
            await using var store = await MemoryStore.OpenAsync(Path.Combine(dir, "m.jsonl"));
            var task = HeldOutTasks.All.First(t => t.Name == "explain-http-cache");

            // 即使存储里**有**这条偏好，只要不传注入器，模型就不该看到。
            await store.PutAsync(new MemoryArtifact(
                "eval-" + task.Name, 1,
                MemoryArtifact.ComputeHash(task.PreferenceKey, task.PreferenceValue),
                task.PreferenceKey, task.PreferenceValue,
                MemoryScope.Workspace, dir, "held-out-seed", DateTimeOffset.UtcNow,
                MemoryArtifactState.Enabled));

            var client = new RecordingClient("done");
            var runtime = new LoopAgentRuntime(client, new ToolDispatcher())
            {
                Memory = null,
                MemoryWorkspace = dir,
            };

            await foreach (var _ in runtime.RunAsync(
                new AgentRunRequest("eval", task.Name, [], task.Prompt))) { }

            if (client.SystemMessages.Any(s => s.Contains("language: 中文", StringComparison.Ordinal)))
                return "未配置注入器时模型仍看到了偏好：对照组不成立";

            return null;
        }
        finally { Cleanup(dir); }
    }

    /// <summary>
    /// 任务提示里不得出现偏好线索。
    /// </summary>
    /// <remarks>
    /// 这是整个实验最容易出错的地方：提示里一旦写了"用中文回答"，
    /// 两组都会照做，提升幅度变成 0，而我们会误判为"记忆没用"。
    /// </remarks>
    private static async Task<string?> PromptIsNeutralAsync()
    {
        await Task.CompletedTask;

        foreach (var task in HeldOutTasks.All)
        {
            var leak = task.PreferenceKey switch
            {
                "language" => task.Prompt.Contains(task.PreferenceValue, StringComparison.Ordinal)
                    || task.Prompt.Contains("中文", StringComparison.Ordinal)
                    || task.Prompt.Contains("English", StringComparison.OrdinalIgnoreCase),
                "output.format" => task.Prompt.Contains("列表", StringComparison.Ordinal)
                    || task.Prompt.Contains("bullet", StringComparison.OrdinalIgnoreCase)
                    || task.Prompt.Contains("要点", StringComparison.Ordinal),
                "naming.convention" => task.Prompt.Contains("snake", StringComparison.OrdinalIgnoreCase)
                    || task.Prompt.Contains("camel", StringComparison.OrdinalIgnoreCase)
                    || task.Prompt.Contains("下划线", StringComparison.Ordinal),
                _ => false,
            };

            if (leak) return $"任务 '{task.Name}' 的提示里泄漏了偏好线索：{task.Prompt}";
        }

        return null;
    }

    // ── 支撑 ─────────────────────────────────────────────────────

    /// <summary>记录收到的全部 system 消息，并返回固定答复。</summary>
    private sealed class RecordingClient(string reply) : IChatClient
    {
        public List<string> SystemMessages { get; } = [];

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation]
            CancellationToken cancellationToken = default)
        {
            SystemMessages.AddRange(messages
                .Where(m => m.Role == ChatRole.System)
                .Select(m => m.Text));

            await Task.CompletedTask;
            yield return new ChatResponseUpdate(ChatRole.Assistant, reply);
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private static string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "dougong-l1h-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void Cleanup(string dir)
    {
        try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
    }

    private static int Check(string name, Func<string?> action)
    {
        try
        {
            var failure = action();
            if (failure is null) { Console.WriteLine($"PASS {name}"); return 0; }
            Console.WriteLine($"FAIL {name}: {failure}");
            return 1;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"FAIL {name}: {ex.GetType().Name}: {ex.Message}");
            return 1;
        }
    }

    private static async Task<int> CheckAsync(string name, Func<Task<string?>> action)
    {
        try
        {
            var failure = await action();
            if (failure is null) { Console.WriteLine($"PASS {name}"); return 0; }
            Console.WriteLine($"FAIL {name}: {failure}");
            return 1;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"FAIL {name}: {ex.GetType().Name}: {ex.Message}");
            return 1;
        }
    }
}
