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
            ? "L1-HARNESS: ALL PASS（本套只验尺子；效果实测见 -- l1-effect）"
            : $"L1-HARNESS: {failures} FAILURE(S)");
        return failures;
    }

    /// <summary>正例：明显遵循的回答必须被判为遵循。</summary>
    /// <remarks>
    /// v2 任务集只用两类偏好：concise（**相对判据**）与 PascalCase（绝对判据）。
    /// v1 的 language/bullets 判据已移除——它们在 14 个真实组合上零区分力，
    /// 留着会让人误以为那些维度被测过。
    /// </remarks>
    private static string? CriteriaAcceptFollowing()
    {
        var concise = HeldOutTasks.All.First(t => t.Name == "explain-http-cache");

        var baseline = Repeat("默认回答很长。", 70);     // 约 420 字符
        var shortAnswer = Repeat("简短回答。", 20);      // 约 100 字符

        if (!concise.JudgePair(shortAnswer, baseline))
            return "明显更短的回答未判为遵循 concise（相对判据）";

        var pascal = HeldOutTasks.All.First(t => t.Name == "name-config-fields");
        if (!pascal.JudgeSingle("字段：MaxRetryCount、RequestTimeoutMs、EnableCache。"))
            return "PascalCase 回答未判为遵循";

        // 混合：多数 PascalCase、个别 camelCase，应算遵循（不完美但响应了偏好）
        if (!pascal.JudgeSingle("字段：MaxRetryCount、RequestTimeoutMs、enableCache。"))
            return "以 PascalCase 为主但含个别 camelCase 被判为未遵循：会低估效果";

        return null;
    }

    /// <summary>
    /// 反例：明显不遵循的回答必须被判为未遵循。
    /// </summary>
    /// <remarks>
    /// 没有反例时，一个"永远返回 true"的判据也能全绿。
    /// </remarks>
    private static string? CriteriaRejectNonFollowing()
    {
        var concise = HeldOutTasks.All.First(t => t.Name == "explain-http-cache");

        var baseline = Repeat("默认回答很长。", 70);

        // 长度几乎没变 → 不算遵循
        if (concise.JudgePair(baseline, baseline))
            return "长度未变被误判为遵循 concise";

        // 只短了一点点（< 15%）→ 那是噪声，不算遵循
        var slightlyShorter = Repeat("默认回答很长。", 66);
        if (concise.JudgePair(slightlyShorter, baseline))
            return "只短 5% 被误判为遵循：把噪声当成了效果";

        // 只回一句"好" → 退化，不算遵循
        if (concise.JudgePair("好的。", baseline))
            return "空话被误判为遵循 concise：应排除没有实际回答的情况";

        var pascal = HeldOutTasks.All.First(t => t.Name == "name-config-fields");
        if (pascal.JudgeSingle("字段：maxRetryCount、requestTimeoutMs、enableCache。"))
            return "camelCase 回答被误判为遵循 PascalCase";

        if (pascal.JudgeSingle("字段：max_retry_count、request_timeout_ms、enable_cache。"))
            return "snake_case 回答被误判为遵循 PascalCase";

        // 只出现一个 PascalCase 标识符：那是业界惯例的偶然命中（例如 OrderId），
        // 不是系统性响应偏好。实测中这一条真的造成过假遵循，故必须拒。
        if (pascal.JudgeSingle("建议主键用 OrderId，其它字段按项目约定。"))
            return "只含一个 PascalCase 标识符被判为遵循：偶然命中不算响应偏好";

        // PascalCase 有 2 个但 camelCase 更多：仍应判未遵循。
        // 没有这一条，"pascal 必须多于 camel" 那一半永远不会被测到——
        // 上面几条反例的 PascalCase 计数都是 0，光靠 >= 2 就已经拒绝了。
        // 注意样本里不能出现 "PascalCase" 这个词本身：它自己就是一个
        // PascalCase 标识符，会把计数抬高从而掩盖被判定的行为。
        if (pascal.JudgeSingle(
                "混合写法：OrderId、CustomerId 用大写驼峰，但 user_name、orderTotal、"
                + "itemCount、shipAddress、trackingNumber 都是别的风格。"))
            return "大写驼峰少于小写驼峰时被判为遵循：比较那一半失效";

        return null;
    }

    /// <summary>成对判据的边界行为。</summary>
    private static string? CriteriaTolerateAlternatives()
    {
        var concise = HeldOutTasks.All.First(t => t.Name == "explain-http-cache");

        var baseline = Repeat("默认回答很长。", 70);      // 420

        // 刚好超过 15% 降幅（420*0.85=357）应算遵循
        if (!concise.JudgePair(Repeat("默认回答很长。", 58), baseline))   // 348
            return "降幅 17% 被判为未遵循：门槛过严";

        // 刚好不到 15%（420*0.9=378）应算未遵循
        if (concise.JudgePair(Repeat("默认回答很长。", 62), baseline))    // 372
            return "降幅 11% 被判为遵循：门槛失效";

        // 对照组为空 → 无法比较，必须判未遵循而不是崩或误判
        if (concise.JudgePair(Repeat("短。", 30), ""))
            return "对照为空时被判为遵循：应判无法比较";

        return null;
    }

    /// <summary>构造指定重复次数的文本，用于控制长度。</summary>
    private static string Repeat(string unit, int times) =>
        string.Concat(Enumerable.Repeat(unit, times));

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

            // 从任务自己的偏好构造期望文本，而不是硬编码某个键值——
            // 硬编码在任务集换成 v2 之后会静默失效（检查照样通过，但什么都没验证）。
            var expected = $"{task.PreferenceKey}: {task.PreferenceValue}";
            if (!systems.Any(s => s.Contains(expected, StringComparison.Ordinal)))
                return $"模型收到的 system 消息里没有 '{expected}'";

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

            var expected = $"{task.PreferenceKey}: {task.PreferenceValue}";
            if (client.SystemMessages.Any(s => s.Contains(expected, StringComparison.Ordinal)))
                return $"未配置注入器时模型仍看到了偏好 '{expected}'：对照组不成立";

            return null;
        }
        finally { Cleanup(dir); }
    }

    /// <summary>
    /// 任务提示里不得出现偏好线索。
    /// </summary>
    /// <remarks>
    /// 这是整个实验最容易出错的地方：提示里一旦写了"用中文回答"或"简短一点"，
    /// 两组都会照做，提升幅度变成 0，而我们会误判为"记忆没用"。
    /// <para>
    /// v2 的两类偏好各有一组禁用词：concise 不能提"简短/简要/一句话"，
    /// PascalCase 不能提"大写/命名风格/驼峰"。
    /// </para>
    /// </remarks>
    private static async Task<string?> PromptIsNeutralAsync()
    {
        await Task.CompletedTask;

        // 这些词一旦出现在提示里，就等于直接告诉了模型偏好
        string[] forbidden =
        [
            "简短", "简要", "简洁", "一句话", "简短地", "concise", "brief",
            "大写", "PascalCase", "camelCase", "snake_case", "驼峰", "下划线", "命名风格",
            "列表", "要点", "bullet",
            "中文", "English", "中文回答",
        ];

        foreach (var task in HeldOutTasks.All)
        {
            foreach (var word in forbidden)
            {
                if (task.Prompt.Contains(word, StringComparison.OrdinalIgnoreCase))
                    return $"任务 '{task.Name}' 的提示里泄漏了偏好线索 '{word}'：{task.Prompt}";
            }

            // 偏好值本身当然也不能出现在提示里
            if (task.Prompt.Contains(task.PreferenceValue, StringComparison.OrdinalIgnoreCase))
                return $"任务 '{task.Name}' 的提示里直接含偏好值：{task.Prompt}";
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
