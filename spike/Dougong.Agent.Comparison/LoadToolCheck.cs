using System.Reflection;
using System.Text.Json;
using Dougong.Agent;

namespace Dougong.Agent.Comparison;

/// <summary>
/// 加载任意工具程序集、注册进 dispatcher、真实派发一次——验证"我的工具装上了"。
/// </summary>
/// <remarks>
/// 与 <see cref="LoadPluginCheck"/> 的分工：
/// 那个验证**内核插件**（`IPlugin`，模型看不见）；
/// 这个验证**工具插件**（`IAgentTool`，模型能调用）。
/// 判据 1 要求的是后者。
/// <para>
/// 派发走真实 <see cref="ToolDispatcher"/>：schema 校验、权限采集、资源协调
/// 全都跑一遍。只做 `Register` 不派发，测不出 schema 是否合法、
/// `DescribePermission` 是否会抛异常——那才是真正容易出错的地方。
/// </para>
/// </remarks>
public static class LoadToolCheck
{
    public static async Task<int> RunAsync(string? assemblyPath)
    {
        if (string.IsNullOrWhiteSpace(assemblyPath))
        {
            Console.WriteLine("用法：-- load-tool <工具 dll 的路径>");
            Console.WriteLine();
            Console.WriteLine("  例如：dotnet run --project spike/Dougong.Agent.Comparison \\");
            Console.WriteLine("          -- load-tool samples/my-first-tool/bin/Release/net10.0/MyFirstTool.dll");
            Console.WriteLine();
            Console.WriteLine("  只应加载你自己写的、可信的工具。");
            return 2;
        }

        var full = Path.GetFullPath(assemblyPath);
        if (!File.Exists(full))
        {
            Console.WriteLine($"找不到文件：{full}");
            Console.WriteLine("先编译：dotnet build <你的工具目录> -c Release");
            return 2;
        }

        Console.WriteLine($"加载：{full}");
        Console.WriteLine();

        Type? toolType;
        try
        {
            var assembly = Assembly.LoadFrom(full);
            toolType = assembly.GetTypes().FirstOrDefault(t =>
                typeof(IAgentTool).IsAssignableFrom(t)
                && !t.IsAbstract
                && t.GetConstructor(Type.EmptyTypes) is not null);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"读取程序集失败：{ex.GetType().Name}: {ex.Message}");
            return 1;
        }

        if (toolType is null)
        {
            Console.WriteLine("这个程序集里没有可用的工具类型。");
            Console.WriteLine();
            Console.WriteLine("需要满足三条：");
            Console.WriteLine("  - 实现 Dougong.Agent.IAgentTool");
            Console.WriteLine("  - 不是抽象类");
            Console.WriteLine("  - 有一个无参构造函数");
            Console.WriteLine();
            Console.WriteLine("常见原因：csproj 引的是 Dougong.Kernel 而不是 Dougong.Agent——");
            Console.WriteLine("IAgentTool 在 Agent 层，只引 Kernel 就找不到这个接口。");
            return 1;
        }

        IAgentTool tool;
        try
        {
            tool = (IAgentTool)Activator.CreateInstance(toolType)!;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"创建工具实例失败：{ex.GetType().Name}: {ex.Message}");
            Console.WriteLine("检查无参构造函数是否抛异常。");
            return 1;
        }

        Console.WriteLine($"工具：{tool.Descriptor.Name}（版本 {tool.Descriptor.Version}）");

        // 用一个空对象试一次权限与调度描述——这两处在参数不完整时最容易抛异常。
        using var emptyArgs = JsonDocument.Parse("{}");
        var sample = emptyArgs.RootElement.Clone();

        ToolPermission permission;
        ToolExecutionPlan plan;
        try
        {
            permission = tool.DescribePermission(sample);
            plan = tool.DescribeExecution(sample);
        }
        catch (Exception ex)
        {
            Console.WriteLine();
            Console.WriteLine($"DescribePermission / DescribeExecution 抛异常：{ex.GetType().Name}: {ex.Message}");
            Console.WriteLine("这两个方法在参数不全时也必须能返回——模型可能漏字段。");
            return 1;
        }

        Console.WriteLine($"权限：{Describe(permission)}");
        Console.WriteLine($"调度：{plan.Concurrency}"
            + (plan.HasResourceKeys
                ? $"（读 {plan.Reads.Count} 个键、写 {plan.Writes.Count} 个键）"
                : "（未声明资源键）"));

        // 真实派发一次。用最小合法参数：从 schema 的 required 里取第一个字符串字段。
        var arguments = BuildMinimalArguments(tool.Descriptor.InputSchema);
        Console.WriteLine($"调用：{arguments}");

        // 审批器：记录是否会询问。默认 dispatcher 对每次调用都问，
        // 这里如实打印出来——指南 §2.5.2 讲的就是这个行为。
        var approver = new CountingApprover();
        var dispatcher = new ToolDispatcher(approver);
        dispatcher.Register(tool);

        ToolDispatchResult result;
        try
        {
            using var doc = JsonDocument.Parse(arguments);
            result = await dispatcher.DispatchAsync(
                new ToolInvocation("probe-1", tool.Descriptor.Id, doc.RootElement.Clone()));
        }
        catch (Exception ex)
        {
            Console.WriteLine();
            Console.WriteLine($"派发抛异常：{ex.GetType().Name}: {ex.Message}");
            Console.WriteLine("dispatcher 不应抛未归类异常——请把这条报出来。");
            return 1;
        }

        if (approver.Calls > 0)
        {
            Console.WriteLine($"审批：请求了 {approver.Calls} 次（默认行为；宿主可包 "
                + "PermissionPolicyApprover 让无访问工具跳过）");
        }

        if (result.Outcome != ToolCallOutcome.Succeeded)
        {
            Console.WriteLine($"结果：{result.Outcome}");
            if (result.Diagnostic is { Length: > 0 } d)
                Console.WriteLine($"诊断：{d}");
            Console.WriteLine();
            Console.WriteLine("失败。最常见的原因：");
            Console.WriteLine("  - InputSchema 不合法（required 里的字段必须也在 properties 里）");
            Console.WriteLine("  - 参数没通过 schema 校验");
            return 1;
        }

        Console.WriteLine($"结果：{result.Result?.Content.GetRawText() ?? "(空)"}");
        Console.WriteLine();
        Console.WriteLine("成功：宿主认这个工具，而且能被派发。");
        return 0;
    }

    /// <summary>
    /// 从 schema 的 required 字段构造一份最小参数。
    /// </summary>
    /// <remarks>
    /// 只填字符串字段为占位值：目的是让调用通过 schema 校验，
    /// 不是构造有意义的输入。填不出来就退回空对象，让 dispatcher 如实报错。
    /// </remarks>
    private static string BuildMinimalArguments(JsonElement schema)
    {
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);

        if (schema.ValueKind is JsonValueKind.Object
            && schema.TryGetProperty("properties", out var props)
            && props.ValueKind is JsonValueKind.Object)
        {
            var required = schema.TryGetProperty("required", out var req)
                && req.ValueKind is JsonValueKind.Array
                    ? req.EnumerateArray().Select(x => x.GetString()).ToHashSet(StringComparer.Ordinal)
                    : null;

            foreach (var prop in props.EnumerateObject())
            {
                // 只填 required 里的字段：可选字段留空更接近真实的"最小调用"。
                if (required is not null && !required.Contains(prop.Name)) continue;

                result[prop.Name] = prop.Value.TryGetProperty("type", out var t)
                    && t.GetString() == "string"
                        ? "hello world"
                        : null;
            }
        }

        return JsonSerializer.Serialize(result);
    }

    private static string Describe(ToolPermission permission)
    {
        if (!permission.IsKnown) return "未知（dispatcher 会警告并仍然询问）";
        if (permission.Access == ToolAccess.None) return "已知、无访问 —— 这类工具由宿主决定是否跳过审批";

        var parts = new List<string> { permission.Access.ToString() };
        if (permission.Paths.Count > 0) parts.Add($"路径 {string.Join(", ", permission.Paths)}");
        if (permission.Commands.Count > 0) parts.Add($"命令 {string.Join(", ", permission.Commands)}");
        if (permission.Domains.Count > 0) parts.Add($"域名 {string.Join(", ", permission.Domains)}");
        return $"已知、{string.Join("、", parts)}";
    }

    private sealed class CountingApprover : IToolApprover
    {
        public int Calls { get; private set; }

        public ValueTask<ToolApprovalDecision> RequestApprovalAsync(
            ToolApprovalRequest request, CancellationToken cancellationToken = default)
        {
            Calls++;
            return ValueTask.FromResult(ToolApprovalDecision.Allow("probe"));
        }
    }
}
