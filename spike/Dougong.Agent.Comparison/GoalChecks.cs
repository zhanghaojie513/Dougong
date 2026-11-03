using System.Text.Json;
using Dougong.Agent;

namespace Dougong.Agent.Comparison;

/// <summary>
/// 会话目标的验收。
/// </summary>
/// <remarks>
/// <b>可观察面是"模型能不能据此判断还差多少"</b>：
/// <list type="bullet">
/// <item>一个会话只有一个进行中的目标（两个就没法判断这轮在推进哪个）</item>
/// <item>修订号冲突要回传权威值（否则调用方只能再读一次，而那次可能又过期）</item>
/// <item>blocked 有轮次门槛（否则模型第一次失败就宣布做不了）</item>
/// <item>离开 blocked 时原因要清掉（否则界面一直显示已不成立的阻塞理由）</item>
/// </list>
/// </remarks>
public static class GoalChecks
{
    public static async Task<int> RunAsync()
    {
        var failures = 0;
        failures += await CheckAsync("goal-create-then-read-roundtrips", RoundtripAsync);
        failures += await CheckAsync("goal-rejects-second-active-goal", SingleActiveAsync);
        failures += await CheckAsync("goal-update-requires-matching-revision", RevisionAsync);
        failures += await CheckAsync("goal-noop-does-not-bump-revision", NoOpAsync);
        failures += await CheckAsync("goal-blocked-requires-rounds-and-reason", BlockedGateAsync);
        failures += await CheckAsync("goal-leaving-blocked-clears-reason", ClearsReasonAsync);
        failures += await CheckAsync("goal-rounds-only-count-while-active", RoundCountingAsync);
        failures += await CheckAsync("goal-tool-reports-conflict-with-current", ToolConflictAsync);
        failures += await CheckAsync("goal-clear-then-create-again", ClearAsync);
        failures += await CheckAsync("goal-is-wired-into-cli", WiringAsync);

        Console.WriteLine(failures == 0
            ? "GOAL: ALL PASS"
            : $"GOAL: {failures} FAILURE(S)");
        return failures;
    }

    private static async Task<string?> RoundtripAsync()
    {
        await Task.CompletedTask;
        var goal = new SessionGoal();

        var created = goal.Create("把 OrderService 的测试覆盖到 80%");

        if (created.Phase is not GoalPhase.Active) return $"新建目标应是 active，实际 {created.Phase}";
        if (created.Revision != 1) return $"初始修订号应为 1，实际 {created.Revision}";
        if (created.RoundsStarted != 0) return "初始轮次应为 0";

        var current = goal.Current;
        return current is not null
            && current.Objective == "把 OrderService 的测试覆盖到 80%"
                ? null
                : "读回的判据不对";
    }

    /// <summary>一个会话只能有一个进行中的目标。</summary>
    /// <remarks>
    /// <b>两个目标时模型无法判断这一轮在推进哪个</b>，而它会随便挑一个——
    /// 那比没有目标更糟，因为它以为自己在推进正确的那件事。
    /// </remarks>
    private static async Task<string?> SingleActiveAsync()
    {
        await Task.CompletedTask;
        var goal = new SessionGoal();
        goal.Create("第一个");

        try
        {
            goal.Create("第二个");
            return "允许了第二个进行中的目标";
        }
        catch (InvalidOperationException)
        {
            // 预期。
        }

        // 完成之后可以再建。
        var first = goal.Current!;
        goal.Update(new GoalUpdateRequest(first.Id, first.Revision, GoalAction.Complete));

        try
        {
            goal.Create("第二个");
            return null;
        }
        catch (InvalidOperationException ex)
        {
            return $"上一个已完成，仍拒绝新建：{ex.Message}";
        }
    }

    /// <summary>修订号不符即拒绝，并回传当前值。</summary>
    private static async Task<string?> RevisionAsync()
    {
        await Task.CompletedTask;
        var goal = new SessionGoal();
        var created = goal.Create("判据");

        var stale = goal.Update(new GoalUpdateRequest(created.Id, 999, GoalAction.Pause));
        if (stale is not GoalUpdateResult.VersionConflict conflict)
            return $"陈旧修订号应冲突，实际 {stale.GetType().Name}";

        if (conflict.Current.Revision != created.Revision)
            return "冲突时回传的不是当前值";

        var ok = goal.Update(new GoalUpdateRequest(created.Id, created.Revision, GoalAction.Pause));
        if (ok is not GoalUpdateResult.Succeeded s) return "正确修订号应成功";
        if (s.Goal.Revision != created.Revision + 1) return "实质修改应递增修订号";
        return s.Goal.Phase is GoalPhase.Paused ? null : $"阶段没变：{s.Goal.Phase}";
    }

    /// <summary>no-op 不递增修订号。</summary>
    /// <remarks>
    /// <b>递增会制造假的新版本</b>：别的调用方手里的修订号因此失效，
    /// 而它们看到的"冲突"其实什么都没变。
    /// </remarks>
    private static async Task<string?> NoOpAsync()
    {
        await Task.CompletedTask;
        var goal = new SessionGoal();
        var created = goal.Create("判据");

        // 已经 active，再 resume 一次。
        var resumed = goal.Update(new GoalUpdateRequest(created.Id, created.Revision, GoalAction.Resume));
        if (resumed is not GoalUpdateResult.Succeeded r) return "no-op 被拒绝了";

        if (r.Changed) return "no-op 却报告 changed=true";
        return r.Goal.Revision == created.Revision
            ? null
            : $"no-op 递增了修订号：{created.Revision} → {r.Goal.Revision}";
    }

    /// <summary>blocked 要跑满轮次且必须给原因。</summary>
    /// <remarks>
    /// <b>门槛的理由</b>：模型很容易在第一次失败后就宣布"做不了"，
    /// 而那通常只是尝试得不够。dsh 的默认也是至少三轮。
    /// </remarks>
    private static async Task<string?> BlockedGateAsync()
    {
        await Task.CompletedTask;
        var goal = new SessionGoal();
        var created = goal.Create("判据", maxGoalRounds: 8);

        // 只跑 1 轮就想 blocked。
        goal.BeginRound();
        var early = goal.Update(new GoalUpdateRequest(
            created.Id, goal.Current!.Revision, GoalAction.Blocked,
            ReasonCode: "test-fails", ReasonMessage: "测试不过"));

        if (early is not GoalUpdateResult.TooEarlyToBlock t)
            return $"轮次不足时应拒绝 blocked，实际 {early.GetType().Name}";

        if (t.MinimumRounds != SessionGoal.MinRoundsBeforeBlocked)
            return $"门槛应报 {SessionGoal.MinRoundsBeforeBlocked}，实际 {t.MinimumRounds}";

        // 跑满之后，但没给原因。
        goal.BeginRound();
        goal.BeginRound();
        var noReason = goal.Update(new GoalUpdateRequest(
            goal.Current!.Id, goal.Current!.Revision, GoalAction.Blocked));
        if (noReason is not GoalUpdateResult.ReasonRequired)
            return $"缺原因时应拒绝，实际 {noReason.GetType().Name}";

        // 给原因后成功。
        var ok = goal.Update(new GoalUpdateRequest(
            goal.Current!.Id, goal.Current!.Revision, GoalAction.Blocked,
            ReasonCode: "test-fails", ReasonMessage: "三个测试稳定失败"));

        return ok is GoalUpdateResult.Succeeded s
            && s.Goal.Phase is GoalPhase.Blocked
            && s.Goal.BlockedReason?.Code == "test-fails"
                ? null
                : "给了合法原因仍未进入 blocked";
    }

    /// <summary>离开 blocked 时清掉原因。</summary>
    /// <remarks>
    /// <b>留着会让界面一直显示已经不成立的阻塞理由</b>，
    /// 而用户会以为还没解决。
    /// </remarks>
    private static async Task<string?> ClearsReasonAsync()
    {
        await Task.CompletedTask;
        var goal = new SessionGoal();
        var created = goal.Create("判据");
        for (var i = 0; i < 3; i++) goal.BeginRound();

        var blocked = goal.Update(new GoalUpdateRequest(
            created.Id, goal.Current!.Revision, GoalAction.Blocked,
            ReasonCode: "test-fails", ReasonMessage: "测试不过"));

        if (blocked is not GoalUpdateResult.Succeeded b) return "前置：进 blocked 失败";
        if (b.Goal.BlockedReason is null) return "前置：blocked 没有原因";

        var resumed = goal.Update(new GoalUpdateRequest(
            goal.Current!.Id, goal.Current!.Revision, GoalAction.Resume));

        if (resumed is not GoalUpdateResult.Succeeded r) return "resume 失败";

        return r.Goal.BlockedReason is null
            ? null
            : $"离开 blocked 后仍留着原因：{r.Goal.BlockedReason.Code}";
    }

    /// <summary>暂停/阻塞时不计数轮次。</summary>
    private static async Task<string?> RoundCountingAsync()
    {
        await Task.CompletedTask;
        var goal = new SessionGoal();
        var created = goal.Create("判据");

        if (goal.BeginRound() != 1) return "第一轮应为 1";

        goal.Update(new GoalUpdateRequest(created.Id, goal.Current!.Revision, GoalAction.Pause));

        var paused = goal.BeginRound();
        if (paused is not null) return $"暂停时仍记了轮次：{paused}";

        if (goal.Current!.RoundsStarted != 1) return "暂停时轮次被改动了";
        return null;
    }

    /// <summary>工具层：冲突时回传当前值。</summary>
    /// <remarks>
    /// <b>回传权威值</b>让调用方能直接协调，不必再读一次——
    /// 而那次读可能又落在下一次修改之后。
    /// </remarks>
    private static async Task<string?> ToolConflictAsync()
    {
        var state = new SessionGoal();
        var created = state.Create("判据");

        var update = new UpdateGoalTool(state);
        var result = await update.ExecuteAsync(
            Invocation(UpdateGoalTool.ToolId, new
            {
                goal_id = created.Id,
                revision = 999,
                action = "pause",
            }),
            CancellationToken.None);

        var ok = result.Content.TryGetProperty("ok", out var okElement)
            && okElement.ValueKind is JsonValueKind.True;
        if (ok) return "冲突却报告成功";

        if (!result.Content.TryGetProperty("goal", out var goalElement)
            || goalElement.ValueKind is JsonValueKind.Null)
            return "冲突时没有回传当前目标";

        var revision = goalElement.GetProperty("revision").GetInt32();
        return revision == created.Revision
            ? null
            : $"回传的修订号不对：{revision}";
    }

    /// <summary>清除之后可以重新创建。</summary>
    private static async Task<string?> ClearAsync()
    {
        await Task.CompletedTask;
        var goal = new SessionGoal();
        var created = goal.Create("判据");

        var cleared = goal.Update(new GoalUpdateRequest(
            created.Id, created.Revision, GoalAction.Clear));

        if (cleared is not GoalUpdateResult.Cleared) return "清除失败";
        if (goal.Current is not null) return "清除后仍有当前目标";

        try
        {
            goal.Create("新判据");
            return null;
        }
        catch (InvalidOperationException)
        {
            return "清除后无法创建新目标";
        }
    }

    /// <summary>装配里接了三个工具与轮次记账。</summary>
    private static async Task<string?> WiringAsync()
    {
        await Task.CompletedTask;
        var root = FindRepoRoot();

        var composition = Path.Combine(root, "src", "Dougong.Cli", "SessionComposition.cs");
        if (!File.Exists(composition)) return $"找不到 {composition}";

        var text = File.ReadAllText(composition);

        foreach (var tool in new[] { "CreateGoalTool", "GetGoalTool", "UpdateGoalTool" })
            if (!text.Contains($"new {tool}(", StringComparison.Ordinal))
                return $"装配里没有注册 {tool}";

        // 轮次要真的被记：否则 blocked 门槛永远达不到、上限也不生效。
        var runtime = Path.Combine(root, "src", "Dougong.Agent", "LoopAgentRuntime.cs");
        if (!File.Exists(runtime)) return $"找不到 {runtime}";

        var runtimeText = File.ReadAllText(runtime);
        if (!runtimeText.Contains("Goal?.BeginRound();", StringComparison.Ordinal))
            return "运行时没有记轮次——blocked 门槛与轮次上限都不会生效";

        return text.Contains("Goal = _goal", StringComparison.Ordinal)
            ? null
            : "装配没有把目标传给运行时";
    }

    // ── 支撑 ─────────────────────────────────────────────────────

    private static ToolInvocation Invocation(string toolId, object arguments)
    {
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(arguments));
        return new ToolInvocation("inv-g", toolId, document.RootElement.Clone());
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Dougong.slnx")))
                return directory.FullName;
            directory = directory.Parent;
        }

        return Directory.GetCurrentDirectory();
    }

    private static async Task<int> CheckAsync(string name, Func<Task<string?>> action)
    {
        try
        {
            var failure = await action().ConfigureAwait(false);
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
}
