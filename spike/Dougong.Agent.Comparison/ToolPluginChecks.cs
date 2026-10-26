using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Dougong.Agent;

namespace Dougong.Agent.Comparison;

/// <summary>
/// 验证"按指南 §2.5 写一个工具插件"这条路真的走得通。
/// </summary>
/// <remarks>
/// 判据 1 要的是"写一个**工具**插件并装上"，反例明确点名
/// "需要看源码才知道 `DescribePermission` 该怎么填"。
/// 所以这里的验证必须覆盖那个反例：教程给的填法要能被 dispatcher 真的接受。
/// <para>
/// 做法：**从指南里提取代码块**编译成真实程序集，再用反射加载、
/// 注册进真实 <see cref="ToolDispatcher"/>、派发一次、检查结果。
/// 不复写一份代码——复写会出现"测试通过但指南里是错的"，
/// 这种问题本项目已经发生过（§1.2 缺 SystemClock）。
/// </para>
/// </remarks>
public static class ToolPluginChecks
{
    public static async Task<int> RunAsync()
    {
        var failures = 0;
        failures += await CheckAsync("guide-tool-code-compiles", CompilesAsync);
        failures += await CheckAsync("guide-tool-code-is-dispatchable", DispatchableAsync);
        failures += await CheckAsync("default-dispatcher-asks-approval", DefaultAsksAsync);
        failures += await CheckAsync("policy-approver-skips-inert-tools", PolicySkipsInertAsync);
        failures += await CheckAsync("guide-explains-reads-writes-semantics", DocExplainsAsync);

        Console.WriteLine(failures == 0
            ? "TOOL-PLUGIN: ALL PASS"
            : $"TOOL-PLUGIN: {failures} FAILURE(S)");

        // 必须清理：探针目录在 samples/ 下（csproj 的相对引用需要这个层级），
        // 不删就会在读者的工作区里留一堆 _tool-probe-<pid> 垃圾。
        // 上一版漏了这一句，实测跑了三次就留下三个目录。
        CleanupProbe();

        return failures;
    }

    /// <summary>§2.5.1 的代码必须能编译。</summary>
    private static async Task<string?> CompilesAsync()
    {
        var (ok, detail) = await BuildGuideToolAsync();
        return ok ? null : $"指南里的工具代码编译失败：{detail}";
    }

    /// <summary>
    /// 编译出来的工具必须能被 dispatcher 真正派发。
    /// </summary>
    /// <remarks>
    /// 只测"能编译"是不够的：一个 `DescribePermission` 填错、
    /// 或者 `ExecuteAsync` 返回非法 JSON 的工具，照样能编译。
    /// 判据 1 要的是"装上"，即真的能被调用。
    /// </remarks>
    private static async Task<string?> DispatchableAsync()
    {
        var (ok, detail) = await BuildGuideToolAsync();
        if (!ok) return $"前置构建失败：{detail}";

        try
        {
            var type = LoadToolType();
            if (type is null) return "找不到指南里的工具类型";

            var tool = (IAgentTool)Activator.CreateInstance(type)!;
            var dispatcher = new ToolDispatcher();
            dispatcher.Register(tool);

            using var args = JsonDocument.Parse("""{"text":"hello world"}""");
            var invocation = new ToolInvocation("c1", tool.Descriptor.Id, args.RootElement.Clone());
            var result = await dispatcher.DispatchAsync(invocation);

            if (result.Outcome != ToolCallOutcome.Succeeded)
                return $"派发结果不是成功：{result.Outcome}（{result.Diagnostic}）";

            if (result.Result is null) return "成功但没有结果内容";

            // 指南写的是统计字符数，输入 "hello world" 是 11
            var content = result.Result.Content;
            if (!content.TryGetProperty("characters", out var n))
                return $"结果里没有 characters 字段：{content.GetRawText()}";
            if (n.GetInt32() != 11)
                return $"统计结果不对：期望 11，实际 {n.GetInt32()}";

            return null;
        }
        catch (Exception ex)
        {
            return $"{ex.GetType().Name}: {ex.Message}";
        }
    }

    /// <summary>
    /// 默认 dispatcher 对**每一次**调用都请求审批，包括声明为无访问的工具。
    /// </summary>
    /// <remarks>
    /// 这是保守的正确默认。指南最初把它写错成"填 `None` 就不触发审批"——
    /// 那条错误正是本检查发现的：审批器被调用了 1 次。
    /// <para>
    /// 保留这条断言的价值在于钉住**默认行为**：将来若有人"优化"成静默跳过，
    /// 这条会变红，逼他显式讨论。
    /// </para>
    /// </remarks>
    private static async Task<string?> DefaultAsksAsync()
    {
        var (ok, detail) = await BuildGuideToolAsync();
        if (!ok) return $"前置构建失败：{detail}";

        try
        {
            var type = LoadToolType();
            if (type is null) return "找不到指南里的工具类型";

            var tool = (IAgentTool)Activator.CreateInstance(type)!;
            var approver = new CountingApprover();
            var dispatcher = new ToolDispatcher(approver);
            dispatcher.Register(tool);

            using var args = JsonDocument.Parse("""{"text":"abc"}""");
            await dispatcher.DispatchAsync(
                new ToolInvocation("c1", tool.Descriptor.Id, args.RootElement.Clone()));

            // 这条断言的方向是"必须问"。若变成 0，说明默认行为被改了。
            return approver.Calls > 0
                ? null
                : "默认 dispatcher 没有请求审批：保守默认被改动了";
        }
        catch (Exception ex)
        {
            return $"{ex.GetType().Name}: {ex.Message}";
        }
    }

    /// <summary>
    /// 包了 `PermissionPolicyApprover` 之后，声明为无访问的工具才跳过审批。
    /// </summary>
    /// <remarks>
    /// 这是指南 §2.5.2 现在的说法：跳过需要宿主配合，不是工具填个字段就生效。
    /// 两个方向都要测——跳过了无辜的工具会丢掉安全提示，该跳过的不跳过
    /// 则会让用户被无意义的确认淹没。
    /// </remarks>
    private static async Task<string?> PolicySkipsInertAsync()
    {
        var (ok, detail) = await BuildGuideToolAsync();
        if (!ok) return $"前置构建失败：{detail}";

        try
        {
            var type = LoadToolType();
            if (type is null) return "找不到指南里的工具类型";

            var tool = (IAgentTool)Activator.CreateInstance(type)!;
            var inner = new CountingApprover();
            var policy = new PermissionPolicyApprover(inner);
            var dispatcher = new ToolDispatcher(policy);
            dispatcher.Register(tool);

            using var args = JsonDocument.Parse("""{"text":"abc"}""");
            var result = await dispatcher.DispatchAsync(
                new ToolInvocation("c1", tool.Descriptor.Id, args.RootElement.Clone()));

            if (inner.Calls > 0)
                return $"包了 PermissionPolicyApprover 仍然询问了 {inner.Calls} 次";

            if (policy.Skipped == 0)
                return "没有记录跳过次数：无法判断是真的跳过还是根本没走审批";

            if (result.Outcome != ToolCallOutcome.Succeeded)
                return $"派发没有成功：{result.Outcome}";

            return null;
        }
        catch (Exception ex)
        {
            return $"{ex.GetType().Name}: {ex.Message}";
        }
    }

    /// <summary>
    /// 指南必须解释 `Reads`/`Writes` 的语义——判据 1 的反例点名了这一项。
    /// </summary>
    private static Task<string?> DocExplainsAsync()
    {
        var guide = File.ReadAllText(
            Path.Combine(FindRepoRoot(), "docs", "plugin-author-guide.md"));

        var required = new (string Text, string Why)[]
        {
            ("Reads", "没提 Reads 怎么填"),
            ("Writes", "没提 Writes 怎么填"),
            ("读-读不冲突", "没说明读-读可以并行"),
            ("GetFullPath", "没说明键必须规范化"),
            ("WorkspaceExclusive", "没说明说不清资源时该用什么"),
        };

        foreach (var (text, why) in required)
        {
            if (!guide.Contains(text, StringComparison.Ordinal))
                return Task.FromResult<string?>(why);
        }

        return Task.FromResult<string?>(null);
    }

    // ── 构建与加载指南里的代码 ───────────────────────────────────

    /// <summary>
    /// 探针工程目录。
    /// </summary>
    /// <remarks>
    /// **固定名字，不带进程 ID**。原因是 Windows 上 <c>Assembly.LoadFrom</c>
    /// 会锁住 Probe.dll，进程退出前删不掉；若名字带 PID，每次运行都留一个新目录，
    /// 累积成一片垃圾（实测跑了三次就有三个）。
    /// <para>
    /// 固定名字后重复运行只是覆盖同一个目录，不会累积。
    /// 代价是不能并行跑同一套检查——本项目不并行跑套件，可以接受。
    /// </para>
    /// </remarks>
    private static readonly string ProbeDir =
        Path.Combine(FindRepoRoot(), "samples", "_tool-probe");

    private static string? _builtDll;
    private static string? _buildError;

    /// <summary>把指南 §2.5.1 的代码块提取出来编译，返回是否成功。</summary>
    private static async Task<(bool Ok, string Detail)> BuildGuideToolAsync()
    {
        if (_builtDll is not null) return (true, string.Empty);
        if (_buildError is not null) return (false, _buildError);

        try
        {
            var root = FindRepoRoot();
            var guide = await File.ReadAllTextAsync(
                Path.Combine(root, "docs", "plugin-author-guide.md")).ConfigureAwait(false);

            var section = guide.IndexOf("### 2.5.1", StringComparison.Ordinal);
            if (section < 0) { _buildError = "找不到 §2.5.1"; return (false, _buildError); }

            var fence = guide.IndexOf("```csharp", section, StringComparison.Ordinal);
            var bodyStart = guide.IndexOf('\n', fence) + 1;
            var close = guide.IndexOf("```", bodyStart, StringComparison.Ordinal);
            if (fence < 0 || close < 0) { _buildError = "找不到代码块"; return (false, _buildError); }

            var code = guide[bodyStart..close];

            Directory.CreateDirectory(ProbeDir);
            await File.WriteAllTextAsync(
                Path.Combine(ProbeDir, "GuideTool.cs"), code).ConfigureAwait(false);

            await File.WriteAllTextAsync(Path.Combine(ProbeDir, "Probe.csproj"), $"""
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                    <ImplicitUsings>enable</ImplicitUsings>
                    <Nullable>enable</Nullable>
                    <IsPackable>false</IsPackable>
                  </PropertyGroup>
                  <ItemGroup>
                    <ProjectReference Include="{Path.Combine(root, "src", "Dougong.Agent", "Dougong.Agent.csproj")}">
                      <Private>false</Private>
                      <ExcludeAssets>runtime</ExcludeAssets>
                    </ProjectReference>
                  </ItemGroup>
                </Project>
                """).ConfigureAwait(false);

            var build = await RunProcessAsync("dotnet",
                ["build", ProbeDir, "-c", "Release", "-v", "q", "--nologo"]);

            if (build.ExitCode != 0)
            {
                _buildError = Trim(build.Output);
                return (false, _buildError);
            }

            var dll = Path.Combine(ProbeDir, "bin", "Release", "net10.0", "Probe.dll");
            if (!File.Exists(dll))
            {
                _buildError = $"找不到产物 {dll}";
                return (false, _buildError);
            }

            _builtDll = dll;
            return (true, string.Empty);
        }
        catch (Exception ex)
        {
            _buildError = $"{ex.GetType().Name}: {ex.Message}";
            return (false, _buildError);
        }
    }

    /// <summary>
    /// 删除探针目录。
    /// </summary>
    /// <remarks>
    /// Windows 上会**删不掉**：<c>Assembly.LoadFrom</c> 把 Probe.dll 锁住了，
    /// 进程不退出就释放不了。所以这里删两次——能删的删掉，
    /// 删不掉的部分留给下次运行时清（启动时先扫一遍旧目录）。
    /// <para>
    /// 静默失败是对的：清理不该让检查变红。但**必须**在下次运行时补删，
    /// 否则跑 N 次就留 N 个目录，污染读者的工作区。
    /// </para>
    /// </remarks>
    private static void CleanupProbe()
    {
        TryDelete(ProbeDir);
    }

    private static void TryDelete(string dir)
    {
        try
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    /// <summary>
    /// 从编译产物里找出实现 <see cref="IAgentTool"/> 的类型。
    /// </summary>
    /// <remarks>
    /// 用反射而不是硬编码类型名：指南里的类名可能改。
    /// 只要它实现 IAgentTool 且能无参构造，就是我们要找的。
    /// </remarks>
    private static Type? LoadToolType()
    {
        if (_builtDll is null) return null;

        var assembly = System.Reflection.Assembly.LoadFrom(_builtDll);
        return assembly.GetTypes().FirstOrDefault(t =>
            typeof(IAgentTool).IsAssignableFrom(t)
            && !t.IsAbstract
            && t.GetConstructor(Type.EmptyTypes) is not null);
    }

    private sealed class CountingApprover : IToolApprover
    {
        public int Calls { get; private set; }

        public ValueTask<ToolApprovalDecision> RequestApprovalAsync(
            ToolApprovalRequest request, CancellationToken cancellationToken = default)
        {
            Calls++;
            return ValueTask.FromResult(ToolApprovalDecision.Allow("auto"));
        }
    }

    // ── 进程驱动 ─────────────────────────────────────────────────

    private static async Task<(int ExitCode, string Output)> RunProcessAsync(
        string file, string[] args)
    {
        var startInfo = new ProcessStartInfo(file)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in args) startInfo.ArgumentList.Add(a);
        startInfo.Environment["DOTNET_CLI_UI_LANGUAGE"] = "en";

        using var process = new Process { StartInfo = startInfo };
        process.Start();

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();

        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            return (-1, "超时");
        }

        return (process.ExitCode, await stdoutTask + await stderrTask);
    }

    private static string FindRepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "Dougong.slnx")))
        {
            dir = Path.GetDirectoryName(dir);
        }
        return dir ?? throw new InvalidOperationException("找不到仓库根目录（Dougong.slnx）");
    }

    private static string Trim(string text)
    {
        var single = text.ReplaceLineEndings(" ").Trim();
        return single.Length <= 400 ? single : single[..400] + "…";
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
