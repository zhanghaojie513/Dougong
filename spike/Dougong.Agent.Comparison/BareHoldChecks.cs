using System.Reflection;
using Dougong.Agent;

namespace Dougong.Agent.Comparison;

/// <summary>
/// 通用探针：找出"把可替换实现存成实例字段"的地方。
/// </summary>
/// <remarks>
/// 为什么需要通用版：租约机制保护的是**通过它访问**的路径，不是实例本身。
/// 只要某个接口签名接受裸接口，就必然有人存下来，绕过租约。
/// <para>
/// 实例：`SessionComposition._store` 修掉后，缺陷转移到了
/// `ToolDispatcher._store`——而当时的探针只查 `SessionComposition`，
/// 于是"修好了"只是看起来修好了。**单点探针会把人引向错的地方。**
/// </para>
/// <para>
/// <b>判据</b>：一个字段算"违规持有"当且仅当同时满足
/// ① 类型是可替换的运行能力接口（见 <see cref="Replaceable"/>）；
/// ② 它所在的对象**不是**该能力的编排者（编排者自己就是生命周期的一部分，
/// 例如 ACP 会话持有本次会话的 runtime 是它的职责，不是缓存）。
/// 合法持有必须在 <see cref="AllowedHolds"/> 里显式登记并写明理由——
/// **默认判违规**，这样新增的持有点会被立刻发现，而不是悄悄积累。
/// </para>
/// </remarks>
public static class BareHoldChecks
{
    /// <summary>可替换的运行能力。它们都可能被插件替换，因此实例不该被长期缓存。</summary>
    private static readonly Type[] Replaceable =
    [
        typeof(IAgentStore),
        typeof(IToolApprover),
        typeof(IToolDispatcher),
        typeof(IAgentRuntime),
    ];

    /// <summary>
    /// 已审阅的合法持有：`类型名.字段名` → 理由。
    /// </summary>
    /// <remarks>
    /// 每一条都是"这个对象本身就是该能力的生命周期所有者"，不是缓存。
    /// 新增条目必须先回答：替换这个能力之后，该字段会指向旧实例吗？
    /// 会 → 违规，不要加进白名单。
    /// </remarks>
    private static readonly Dictionary<string, string> AllowedHolds = new(StringComparer.Ordinal)
    {
        ["AcpSession._runtime"] =
            "会话对象：一个 AcpSession 就是这一次会话的生命周期，runtime 由工厂为该会话新建，"
            + "会话关闭即释放。不是跨会话缓存，替换发生在会话边界。",

        ["McpToolRegistry._dispatcher"] =
            "注册器：它的唯一职责是把 MCP 工具注册/注销到某个 dispatcher，"
            + "随会话创建与释放，与 dispatcher 同生命周期。",

        ["PermissionPolicyApprover._inner"] =
            "装饰器：策略包装是透明的，它转发给内层；内层审批器本身由宿主或内核提供，"
            + "装饰器不延长其生命周期。",

        ["SessionComposition._approver"] =
            "装配器：它不参与运行，只在构造 dispatcher/runtime 时把审批器交出去；"
            + "内核路径下该字段未被使用（改用了内核解析的适配器）。",

        ["FixedDispatcherAccessor._dispatcher"] =
            "显式不参与替换：调用方主动选择固定 dispatcher（测试、单机场景）。",

        ["FixedApproverAccessor._approver"] =
            "显式不参与替换：调用方主动选择固定审批器（测试、单机 CLI）。"
            + "与'接口收裸实例、悄悄存下来'不同，这个选择写在类型名里。",

        ["FixedStoreAccessor._store"] =
            "显式不参与替换：调用方主动选择固定实例（测试、无插件替换的单机场景）。"
            + "与'接口收裸实例、悄悄存下来'不同，这个选择是可见的、写在类型名里的。",

        ["RegistrationHandle.<owner>P"] =
            "注册句柄：它持有的是 dispatcher 的**身份**用于注销，不是调用能力；"
            + "生命周期与注册本身一致（句柄释放即注销）。",

        ["AcpSessionRuntime.<Runtime>k__BackingField"] =
            "会话结果记录：工厂产出后立即交给 AcpSession，不跨会话复用。",

        ["AcpSessionRuntime.<Dispatcher>k__BackingField"] =
            "会话结果记录：同上，与 Runtime 成对产出，保证两者出自同一次装配。",

        ["StorePlugin.<store>P"] =
            "提供方自身：这个插件**就是**存储的实现持有者，它注册的就是这个实例。"
            + "要求它不持有实例等于要求它是空实现。",

        ["ApproverPlugin.<approver>P"] =
            "提供方自身：同 StorePlugin，它是审批器的提供方。",
    };

    public static int Run()
    {
        var failures = 0;
        var findings = Scan();

        foreach (var finding in findings)
        {
            var key = $"{finding.Type}.{finding.Field}";

            if (AllowedHolds.TryGetValue(key, out var reason))
            {
                // 已审阅：仍然打印，但明确标为"已说明"，避免看的人以为漏了。
                Console.WriteLine($"PASS {key}（已审阅：{Truncate(reason, 60)}）");
                continue;
            }

            Console.WriteLine(
                $"FAIL {key}: {finding.InterfaceName} 被长期缓存为实例字段，绕过租约；"
                + $"替换该能力后旧实例仍会被使用。要么改走 IServiceReference，"
                + $"要么在 BareHoldChecks.AllowedHolds 登记理由");
            failures++;
        }

        // 白名单条目若已失效（字段被删），也要提醒：否则白名单会越长越没有约束力。
        foreach (var key in AllowedHolds.Keys)
        {
            if (findings.Any(f => $"{f.Type}.{f.Field}" == key)) continue;
            Console.WriteLine($"WARN {key}：白名单条目已不存在，请删除以免白名单失效");
        }

        Console.WriteLine(failures == 0
            ? $"BARE-HOLD: ALL PASS（{findings.Count} 处持有，全部已说明）"
            : $"BARE-HOLD: {failures} UNEXPLAINED HOLD(S)");
        return failures;
    }

    private static List<Finding> Scan()
    {
        var findings = new List<Finding>();

        foreach (var assemblyName in new[]
        {
            "Dougong.Agent", "Dougong.Ai", "Dougong.Mcp", "Dougong.Acp", "Dougong.Cli",
        })
        {
            var assembly = Load(assemblyName);
            if (assembly is null) continue;

            foreach (var type in assembly.GetTypes())
            {
                // 跳过编译器生成的 async/iterator 状态机与闭包类。
                // 它们的字段是 await 点的局部变量，生命周期只有一次调用，
                // 不是"长期缓存"。不排除的话，45 条噪声会淹没真实违规。
                if (IsCompilerGenerated(type)) continue;
                if (type.IsAbstract && type.IsSealed) continue;   // static 类没有实例字段

                foreach (var field in type.GetFields(
                    BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public
                    | BindingFlags.DeclaredOnly))
                {
                    var match = Replaceable.FirstOrDefault(r => r.IsAssignableFrom(field.FieldType));
                    if (match is null) continue;
                    findings.Add(new Finding(type.Name, field.Name, match.Name));
                }
            }
        }

        return findings;
    }

    /// <summary>
    /// 按名取程序集；已加载则复用，否则按引用加载。
    /// </summary>
    /// <remarks>
    /// 只查已加载程序集不够：本套件启动时不一定会碰到全部产品程序集，
    /// 于是扫描会静默返回 0 处持有——探针"通过"但什么也没查。
    /// 这类失效最难发现，因为它长得和"确实没有违规"一模一样。
    /// </remarks>
    /// <summary>
    /// 是否为编译器生成的类型（async 状态机、iterator、闭包显示类）。
    /// </summary>
    /// <remarks>
    /// 判据取两条：类型名以 <c>&lt;</c> 开头（C# 编译器对合成类型的命名约定），
    /// 或带 <see cref="CompilerGeneratedAttribute"/>。
    /// </remarks>
    private static bool IsCompilerGenerated(Type type) =>
        type.Name.StartsWith('<')
        || type.IsDefined(typeof(System.Runtime.CompilerServices.CompilerGeneratedAttribute), false);

    private static Assembly? Load(string name)
    {
        var loaded = AppDomain.CurrentDomain.GetAssemblies()
            .FirstOrDefault(a => a.GetName().Name == name);
        if (loaded is not null) return loaded;

        try { return Assembly.Load(name); }
        catch (FileNotFoundException) { return null; }
    }

    private static string Truncate(string text, int max) =>
        text.Length <= max ? text : text[..max] + "…";

    private sealed record Finding(string Type, string Field, string InterfaceName);
}
