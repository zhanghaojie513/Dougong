using System.Text.Json;
using Dougong.Agent;

namespace Dougong.Agent.Comparison;

/// <summary>
/// 权限预设的验收。
/// </summary>
/// <remarks>
/// <b>可观察面是"某个调用会不会去问人"</b>，不是"当前策略是什么"：
/// 用户切了预设却看不到询问次数变化，那这个功能就是空的。
/// </remarks>
public static class PermissionPresetChecks
{
    public static async Task<int> RunAsync()
    {
        var failures = 0;
        failures += await CheckAsync("preset-interactive-asks-on-risk-only", InteractiveSkipsInertAsync);
        failures += await CheckAsync("preset-unattended-never-asks", UnattendedNeverAsksAsync);
        failures += await CheckAsync("preset-switch-changes-observable-behavior", SwitchChangesBehaviorAsync);
        failures += await CheckAsync("preset-does-not-override-inner-rejection", InnerRejectionSurvivesAsync);
        failures += await CheckAsync("preset-unattended-still-refuses-unknown-permission", UnattendedUnknownAsync);
        failures += await CheckAsync("preset-ask-always-asks-even-for-inert", AskAlwaysAsksAsync);
        failures += await CheckAsync("preset-unknown-key-throws-loudly", UnknownKeyThrowsAsync);
        failures += await CheckAsync("preset-custom-key-is-derived-not-selectable", CustomKeyAsync);
        failures += await CheckAsync("preset-table-validates-at-construction", ValidationAsync);
        failures += await CheckAsync("preset-descriptions-admit-no-sandbox", DescriptionsAsync);
        failures += await CheckAsync("preset-is-wired-into-cli", WiringAsync);

        Console.WriteLine(failures == 0
            ? "PERMISSION-PRESET: ALL PASS"
            : $"PERMISSION-PRESET: {failures} FAILURE(S)");
        return failures;
    }

    /// <summary>交互式：纯内存工具跳过，有风险的照问。</summary>
    private static async Task<string?> InteractiveSkipsInertAsync()
    {
        var inner = new CountingApprover();
        var presets = PermissionPresets.CreateDefault(ApprovalPolicy.AskOnRisk);
        var approver = new PresetApprover(inner, presets);

        // 纯内存工具：不打扰。
        await approver.RequestApprovalAsync(Request(ToolAccess.None, known: true));
        if (inner.Requests != 0) return "纯内存工具仍被问了审批";

        // 读文件：有风险，要问。
        await approver.RequestApprovalAsync(Request(ToolAccess.Read, known: true, paths: ["a.txt"]));
        if (inner.Requests != 1) return $"读文件应问 1 次，实际 {inner.Requests}";

        return approver.Skipped == 1 ? null : $"跳过计数应为 1，实际 {approver.Skipped}";
    }

    /// <summary>无人值守：一次都不问。</summary>
    /// <remarks>
    /// <b>这是它存在的理由</b>：批量脚本与 CI 里没人能回答，
    /// 而"问不到就拒绝"会让它们全部失败。
    /// </remarks>
    private static async Task<string?> UnattendedNeverAsksAsync()
    {
        var inner = new CountingApprover();
        var presets = PermissionPresets.CreateDefault(ApprovalPolicy.Never);
        var approver = new PresetApprover(inner, presets);

        await approver.RequestApprovalAsync(Request(ToolAccess.Read, known: true, paths: ["a.txt"]));
        await approver.RequestApprovalAsync(Request(ToolAccess.Execute, known: true, commands: ["make"]));

        if (inner.Requests != 0) return $"无人值守仍问了 {inner.Requests} 次";

        // **仍然记数**：诊断要知道"这条命令被谁放过了"。
        return approver.Skipped == 2 ? null : $"跳过计数应为 2，实际 {approver.Skipped}";
    }

    /// <summary>切换预设会改变可观察行为。</summary>
    /// <remarks>
    /// <b>只验 Current 的值不算验证</b>：那是"改了个字段"，
    /// 而用户要的是"下次不再问我"。这条真的跑两次询问并比对次数。
    /// </remarks>
    private static async Task<string?> SwitchChangesBehaviorAsync()
    {
        var inner = new CountingApprover();
        var presets = PermissionPresets.CreateDefault(ApprovalPolicy.AskOnRisk);
        var approver = new PresetApprover(inner, presets);

        await approver.RequestApprovalAsync(Request(ToolAccess.Read, known: true, paths: ["a.txt"]));
        var before = inner.Requests;
        if (before != 1) return $"切换前应问 1 次，实际 {before}";

        await presets.SwitchAsync("unattended");

        await approver.RequestApprovalAsync(Request(ToolAccess.Read, known: true, paths: ["a.txt"]));
        return inner.Requests == before
            ? null
            : $"切到无人值守后仍问了（{inner.Requests - before} 次）——预设没有生效";
    }

    /// <summary>预设不改变内层审批器的结论。</summary>
    /// <remarks>
    /// <b>预设管"问不问"，不管"允不允许"</b>。若它把内层的拒绝吞掉，
    /// "选一个预设"就变成了"放宽权限"——而用户不会预期这件事。
    /// </remarks>
    private static async Task<string?> InnerRejectionSurvivesAsync()
    {
        var inner = new DenyingApprover();
        var presets = PermissionPresets.CreateDefault(ApprovalPolicy.AskOnRisk);
        var approver = new PresetApprover(inner, presets);

        var decision = await approver.RequestApprovalAsync(
            Request(ToolAccess.Read, known: true, paths: ["a.txt"]));

        if (decision.Approved) return "内层拒绝被预设放行了";
        return inner.Requests == 1 ? null : "内层没有被调用";
    }

    /// <summary>
    /// 无人值守档也要拒绝"未知权限"的工具。
    /// </summary>
    /// <remarks>
    /// <b>第一版这条缺失，而缺陷正落在这里</b>：<c>Never</c> 分支无条件返回
    /// <c>Allow</c>、从不调用内层。MCP 工具一律 <c>IsKnown = false</c>，
    /// 于是选中无人值守后它们全部被放行——比专门的 <c>AutoApproveApprover</c>
    /// 还宽松（那个仍然拒绝未知权限）。
    ///
    /// <para>
    /// 用户选"无人值守"是想少按几次确认，不是想取消
    /// "不知道自己会碰什么就别动手"这条底线。
    /// </para>
    /// </remarks>
    private static async Task<string?> UnattendedUnknownAsync()
    {
        var inner = new DenyingApprover();
        var presets = PermissionPresets.CreateDefault(ApprovalPolicy.Never);
        var approver = new PresetApprover(inner, presets);

        var decision = await approver.RequestApprovalAsync(
            Request(ToolAccess.Read, known: false, paths: ["a.txt"]));

        if (decision.Approved)
            return "无人值守档把未知权限的工具也放行了——那比自动批准更宽松";

        return inner.Requests == 1
            ? null
            : $"未知权限应当交给内层判断，实际内层被调用了 {inner.Requests} 次";
    }

    /// <summary>AskAlways 连纯内存工具也问。</summary>
    private static async Task<string?> AskAlwaysAsksAsync()
    {
        var inner = new CountingApprover();
        var presets = PermissionPresets.CreateDefault(ApprovalPolicy.AskAlways);
        var approver = new PresetApprover(inner, presets);

        await approver.RequestApprovalAsync(Request(ToolAccess.None, known: true));

        if (inner.Requests != 1) return $"每次询问档应问 1 次，实际 {inner.Requests}";
        return approver.Skipped == 0 ? null : "每次询问档不该跳过任何调用";
    }

    /// <summary>未知预设键抛异常，不静默无操作。</summary>
    /// <remarks>
    /// <b>静默无操作在安全方向上更坏</b>：用户以为切到了更严的档，
    /// 而策略没变。
    /// </remarks>
    private static async Task<string?> UnknownKeyThrowsAsync()
    {
        var presets = PermissionPresets.CreateDefault(ApprovalPolicy.AskOnRisk);

        try
        {
            await presets.SwitchAsync("no-such-preset");
            return "未知键没有抛异常";
        }
        catch (ArgumentException ex)
        {
            // 错误消息要列出可选项，否则用户只能猜。
            return ex.Message.Contains("interactive", StringComparison.Ordinal)
                ? null
                : "错误消息没有列出可选项";
        }
    }

    /// <summary><c>custom</c> 是派生值：能显示、不能选。</summary>
    /// <remarks>
    /// <b>它是 dsh 的语义</b>：当当前策略不匹配任何预设时，
    /// 界面要能显示"自定义"，但用户不能主动切到它——那不是一档策略，
    /// 而是一个观察结果。
    /// </remarks>
    private static async Task<string?> CustomKeyAsync()
    {
        await Task.CompletedTask;

        // 构造一个当前值不匹配任何预设的表。
        var presets = new PermissionPresets(
            [new PermissionPreset("a", null, null, ApprovalPolicy.AskOnRisk)],
            ApprovalPolicy.AskAlways);

        if (presets.CurrentKey != PermissionPresets.CustomKey)
            return $"不匹配时应显示 custom，实际 {presets.CurrentKey}";

        var options = presets.Options();
        var custom = options.FirstOrDefault(o => o.Value == PermissionPresets.CustomKey);
        if (custom is null) return "custom 没有出现在选项里（界面无法显示当前值）";
        if (custom.Selectable) return "custom 被标记为可选——它不该能被切换";

        // 其余项必须可选。
        return options.Where(o => o.Value != PermissionPresets.CustomKey).All(o => o.Selectable)
            ? null
            : "具名预设被标记为不可选";
    }

    /// <summary>非法预设表在构造期抛。</summary>
    private static async Task<string?> ValidationAsync()
    {
        await Task.CompletedTask;

        if (!Throws(() => new PermissionPresets([], ApprovalPolicy.AskOnRisk)))
            return "空预设表没有报错";

        if (!Throws(() => new PermissionPresets(
                [
                    new PermissionPreset("dup", null, null, ApprovalPolicy.AskOnRisk),
                    new PermissionPreset("dup", null, null, ApprovalPolicy.Never),
                ],
                ApprovalPolicy.AskOnRisk)))
            return "重复键没有报错";

        // **保留键**：用它会让"派生值"与"真的有个预设叫这个"混在一起。
        return Throws(() => new PermissionPresets(
                [new PermissionPreset(PermissionPresets.CustomKey, null, null, ApprovalPolicy.Never)],
                ApprovalPolicy.AskOnRisk))
            ? null
            : "用保留键 custom 当预设名没有报错";
    }

    /// <summary>预设描述必须承认不含沙箱。</summary>
    /// <remarks>
    /// <b>这是本能力最容易误导用户的一处</b>：dsh 的预设打包"沙箱模式 + 审批策略"，
    /// 我们没有沙箱。若描述不提这一点，用户会以为选了某档就获得了文件隔离，
    /// 或者以为最宽松的档"什么限制都没了"（实际路径守卫仍在）。
    /// <para>
    /// <b>键名也有同样的风险</b>：不能用 dsh 的 <c>workspace-write</c> /
    /// <c>danger-full-access</c>——那两个名字都在描述沙箱范围。
    /// </para>
    /// </remarks>
    private static async Task<string?> DescriptionsAsync()
    {
        await Task.CompletedTask;

        foreach (var preset in PermissionPresets.DefaultTable)
        {
            if (preset.Description is not { Length: > 0 })
                return $"预设 {preset.Key} 没有描述";

            if (preset.Key is "workspace-write" or "danger-full-access")
                return $"预设键 {preset.Key} 沿用了 dsh 的沙箱语义名，会让人以为有沙箱";
        }

        // 至少有一个预设的描述里明说"不含沙箱"或"规则拦截仍然生效"这类限定。
        var admits = PermissionPresets.DefaultTable.Any(p =>
            p.Description!.Contains("沙箱", StringComparison.Ordinal)
            || p.Description.Contains("规则拦截", StringComparison.Ordinal));

        return admits ? null : "没有任何预设说明「不含沙箱」——用户会误以为有隔离";
    }

    /// <summary>装配里接了预设，且顺序正确。</summary>
    private static async Task<string?> WiringAsync()
    {
        await Task.CompletedTask;
        var root = FindRepoRoot();
        var file = Path.Combine(root, "src", "Dougong.Cli", "SessionComposition.cs");
        if (!File.Exists(file)) return $"找不到装配：{file}";

        var text = File.ReadAllText(file);

        if (!text.Contains("new PresetApprover(approver, Permissions)", StringComparison.Ordinal))
            return "装配里没有用预设装饰审批器——切换预设不会生效";

        // **顺序**：预设必须在外层（先决定问不问），否则它看不到有风险的调用。
        var presetIndex = text.IndexOf("new PermissionPolicyApprover(new PresetApprover(", StringComparison.Ordinal);
        if (presetIndex < 0)
            return "PresetApprover 没有包在 PermissionPolicyApprover 内层——顺序反了";

        return text.Contains("public PermissionPresets Permissions", StringComparison.Ordinal)
            ? null
            : "装配没有暴露 PermissionPresets，/permission 命令无法工作";
    }

    // ── 支撑 ─────────────────────────────────────────────────────

    private static ToolApprovalRequest Request(
        ToolAccess access, bool known, string[]? paths = null, string[]? commands = null)
    {
        using var document = JsonDocument.Parse("{}");
        var descriptor = new ToolDescriptor(
            "t", "t", "1.0.0", "d", document.RootElement.Clone());

        return new ToolApprovalRequest(
            "s", "turn", "inv", descriptor, document.RootElement.Clone(),
            new ToolPermission(access, known, paths ?? [], commands ?? [], []),
            ToolExecutionPlan.Serial, ".", new ApprovalBinding(
                "s", "turn", "inv", "t", "1.0.0", "digest", ".", "Read", DateTimeOffset.MaxValue));
    }

    private static bool Throws(Action action)
    {
        try
        {
            action();
            return false;
        }
        catch (ArgumentException)
        {
            return true;
        }
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

    private sealed class CountingApprover : IToolApprover
    {
        public int Requests { get; private set; }

        public ValueTask<ToolApprovalDecision> RequestApprovalAsync(
            ToolApprovalRequest request, CancellationToken cancellationToken = default)
        {
            Requests++;
            return ValueTask.FromResult(ToolApprovalDecision.Allow("ok"));
        }
    }

    private sealed class DenyingApprover : IToolApprover
    {
        public int Requests { get; private set; }

        public ValueTask<ToolApprovalDecision> RequestApprovalAsync(
            ToolApprovalRequest request, CancellationToken cancellationToken = default)
        {
            Requests++;
            return ValueTask.FromResult(ToolApprovalDecision.Deny("不允许"));
        }
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
