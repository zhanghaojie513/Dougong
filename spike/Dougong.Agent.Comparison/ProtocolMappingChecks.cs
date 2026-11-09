using Dougong.Agent;
using Dougong.HostProtocol;
using Dougong.Hosting;

namespace Dougong.Agent.Comparison;

/// <summary>
/// 协议映射层的验收：引擎类型 → 宿主契约形状。
/// </summary>
/// <remarks>
/// <b>为什么这一层值得单独一套检查</b>：它存在的唯一理由就是"这条知识不该
/// 在每个宿主里重写一遍"。而重写错的形态是**静默的**——
/// 引擎枚举加一个值时，映射两侧都编译通过，只是那个值在界面上显示成
/// 别的东西。所以这里既验当前映射对不对，也验"新增枚举值会不会被漏掉"。
/// </remarks>
public static class ProtocolMappingChecks
{
    public static Task<int> RunAsync()
    {
        var failures = 0;
        failures += Check("permission-state-carries-every-option", PermissionCarriesOptions);
        failures += Check("permission-current-name-is-never-empty", PermissionCurrentName);
        failures += Check("permission-custom-is-not-selectable", PermissionCustomNotSelectable);
        failures += Check("subagent-status-mapping-is-exhaustive", SubagentStatusExhaustive);
        failures += Check("subagent-completed-without-result-is-not-running", SubagentCompletedNotRunning);
        failures += Check("subagent-carries-start-time", SubagentCarriesStartTime);
        failures += Check("protocol-enum-covers-engine-enum", ProtocolEnumCoversEngine);

        Console.WriteLine(failures == 0
            ? "PROTOCOL-MAPPING: ALL PASS"
            : $"PROTOCOL-MAPPING: {failures} FAILURE(S)");
        return Task.FromResult(failures);
    }

    /// <summary>预设状态必须把引擎的每个选项都带上，一个不漏。</summary>
    /// <remarks>
    /// <b>漏一个的表现</b>：界面少了那个档位，用户以为只有两个选择。
    /// 而这种"少一项"在列表渲染里完全正常，不会报错。
    /// </remarks>
    private static string? PermissionCarriesOptions()
    {
        var presets = PermissionPresets.CreateDefault(ApprovalPolicy.AskOnRisk);
        var state = presets.ToProtocol();

        var expected = presets.Options().Count;
        return state.Options.Count == expected
            ? null
            : $"引擎给了 {expected} 个选项，映射产出 {state.Options.Count} 个";
    }

    /// <summary>当前档的显示名不能是空串——状态栏会显示一片空白。</summary>
    private static string? PermissionCurrentName()
    {
        var presets = PermissionPresets.CreateDefault(ApprovalPolicy.AskOnRisk);
        var state = presets.ToProtocol();

        if (string.IsNullOrWhiteSpace(state.CurrentName))
            return "当前档的显示名是空的";

        if (state.CurrentKey != "interactive")
            return $"按 AskOnRisk 初始化时当前键应是 interactive，实际 {state.CurrentKey}";

        return null;
    }

    /// <summary>
    /// 派生的 <c>custom</c> 在映射之后仍然必须是不可选的。
    /// </summary>
    /// <remarks>
    /// <b>这条防的是"映射时把 Selectable 写死成 true"</b>：引擎正确地把
    /// custom 标成不可选，映射一层若图省事全部置 true，界面就会让用户
    /// 点一个不存在的目标。映射层存在的意义就是搬运正确性，不能在这里丢。
    /// </remarks>
    private static string? PermissionCustomNotSelectable()
    {
        // 构造"当前策略不匹配任何具名预设"的情形：interactive 用 AskAlways，
        // 而预设表里没有写 AskAlways 的档 → CurrentKey 变成 custom。
        var presets = PermissionPresets.CreateDefault(ApprovalPolicy.AskAlways);
        var state = presets.ToProtocol();

        if (state.CurrentKey != PermissionPresets.CustomKey)
            return $"预期当前键是 custom，实际 {state.CurrentKey}";

        var custom = state.Options.FirstOrDefault(
            o => string.Equals(o.Key, PermissionPresets.CustomKey, StringComparison.Ordinal));

        if (custom is null) return "custom 档没有被带进选项——界面无法显示'当前是自定义'";

        return custom.Selectable
            ? "custom 被映射成可选——用户点下去会得到'未知预设'"
            : null;
    }

    /// <summary>
    /// 引擎的每个 <see cref="SubagentStatus"/> 都必须能映射，且不等同于 Running。
    /// </summary>
    /// <remarks>
    /// <b>为什么不能只测已知的三个值</b>：那样新增第四个值时检查仍然全绿
    /// ——它验的是当下，不是"穷尽"。所以这里遍历枚举的全部值，
    /// 并断言映射不会把它落到 <c>Running</c>（那是最危险的错映射：
    /// 一个已经结束的子 agent 显示成还在跑）。
    /// </remarks>
    private static string? SubagentStatusExhaustive()
    {
        foreach (var status in Enum.GetValues<SubagentStatus>())
        {
            var result = new SubagentResult("sub-1", status, "text", null, 1);
            var outcome = result.ToProtocol();

            // **按名字对齐，不按序号**：两个枚举的成员顺序是各自演化的
            // （引擎的 Running 排在末尾是为了不移动已有序号，协议的
            // Running 排在最前是为了读起来顺）。序号相等是个错误的判据。
            if (!string.Equals(outcome.State.ToString(), status.ToString(), StringComparison.Ordinal))
                return $"引擎状态 {status} 映射成了 {outcome.State}，与预期不一致";

            if (!Enum.IsDefined(outcome.State))
                return $"映射产出未定义的协议状态：{outcome.State}";
        }

        return null;
    }

    /// <summary>
    /// 已完成但结果还没被取走的子 agent，状态不能报 Running。
    /// </summary>
    /// <remarks>
    /// <b>这是最容易写错的一处</b>：句柄的 <c>IsCompleted</c> 与 <c>Result</c>
    /// 是分开维护的（跑完但结果未取时仍在列表里）。只看 <c>IsCompleted</c>
    /// 会把"跑完了、等你去取"报成"还在跑"，用户就一直等一个已经好的任务。
    /// <para>
    /// 这里验的是引擎的 <see cref="SubagentHandle.CurrentStatus"/> 本身——
    /// 映射层已经改成只搬运它，所以那条判断只该有这一份。
    /// </para>
    /// </remarks>
    private static string? SubagentCompletedNotRunning()
    {
        using var handle = CreateHandle("sub-x", "测试");

        // 1. 刚开始：Running。
        if (handle.CurrentStatus != SubagentStatus.Running)
            return $"刚开始的子 agent 应报 Running，实际 {handle.CurrentStatus}";

        // 2. 有结果了：按结果的状态报，而不是 Running。
        handle.Complete(new SubagentResult(
            "sub-x", SubagentStatus.Completed, "结论", null, 2));

        if (handle.CurrentStatus == SubagentStatus.Running)
            return "已有结果的子 agent 仍报 Running——用户会一直等一个已经好的任务";

        if (handle.ToProtocol().State != SubagentRunState.Completed)
            return $"映射没跟着状态走：{handle.ToProtocol().State}";

        return null;
    }

    /// <summary>协议形状必须带上启动时刻——否则界面算不出"跑了多久"。</summary>
    private static string? SubagentCarriesStartTime()
    {
        using var handle = CreateHandle("sub-t", "测试");
        var info = handle.ToProtocol();

        return info.StartedAt == default
            ? "StartedAt 是默认值——界面无法显示已运行时长"
            : null;
    }

    /// <summary>
    /// 协议的 <see cref="SubagentRunState"/> 必须覆盖引擎的每个终态。
    /// </summary>
    /// <remarks>
    /// <b>两个枚举的方向性</b>：协议可以比引擎多一个值（<c>Running</c> 就是
    /// 协议独有的——引擎用"有没有 Result"表达它）。但引擎有、协议没有
    /// 是硬错误：映射无处可去。这里断言的是后者。
    /// </remarks>
    private static string? ProtocolEnumCoversEngine()
    {
        var protocolNames = Enum.GetNames<SubagentRunState>().ToHashSet(StringComparer.Ordinal);

        foreach (var engineName in Enum.GetNames<SubagentStatus>())
        {
            if (!protocolNames.Contains(engineName))
                return $"引擎状态 {engineName} 在协议枚举里没有对应名字";
        }

        return null;
    }

    /// <summary>
    /// 造一个句柄。
    /// </summary>
    /// <remarks>
    /// <b>构造函数是 internal，所以这个检查必须能访问到引擎内部</b>：
    /// 尖峰工程通过 <c>InternalsVisibleTo</c> 拿到了访问权（见 Cli 的 csproj）。
    /// 用反射兜底会让"构造函数改签名"这类改动在检查里静默失败。
    /// </remarks>
    private static SubagentHandle CreateHandle(string id, string label)
    {
        var ctor = typeof(SubagentHandle).GetConstructors(
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
            .FirstOrDefault(c => c.GetParameters().Length == 2)
            ?? throw new InvalidOperationException(
                "SubagentHandle 的 (id, label) 构造函数不见了——映射检查需要它。检查若用反射"
                + "静默兜底，构造函数改签名时这里会变成假通过。");

        return (SubagentHandle)ctor.Invoke([id, label]);
    }

    private static int Check(string name, Func<string?> action)
    {
        try
        {
            var failure = action();
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
