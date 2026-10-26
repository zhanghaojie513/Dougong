using Dougong.Kernel;

namespace Dougong.Agent.Comparison;

/// <summary>
/// 加载任意插件程序集并报告结果——用来验证"我自己写的插件能装上"。
/// </summary>
/// <remarks>
/// 存在的理由：指南 §1.4 给了 `LoadAsync` 的代码片段，但要跑起来得自己建一个
/// 宿主工程。对一个刚照抄完 §1.1/§1.2 的读者来说，那一步是断的——
/// 他能编译出 dll，却没法确认内核认不认它。
/// <para>
/// 这个命令把那一步补上：给一个 dll 路径，它用与真实宿主相同的方式加载，
/// 打印状态与诊断，然后卸载。用的是生产代码路径（<see cref="DougongKernel.LoadAsync"/>），
/// 不是另写一套验证逻辑。
/// </para>
/// <para>
/// <b>只加载可信插件</b>：ALC 是可回收的隔离手段，不是安全沙箱。
/// 所以本命令明确要求你确认路径，不做通配扫描。
/// </para>
/// </remarks>
public static class LoadPluginCheck
{
    public static async Task<int> RunAsync(string? assemblyPath)
    {
        if (string.IsNullOrWhiteSpace(assemblyPath))
        {
            Console.WriteLine("用法：-- load-plugin <插件 dll 的路径>");
            Console.WriteLine();
            Console.WriteLine("  例如：dotnet run --project spike/Dougong.Agent.Comparison \\");
            Console.WriteLine("          -- load-plugin samples/my-first-plugin/bin/Release/net10.0/MyFirstPlugin.dll");
            Console.WriteLine();
            Console.WriteLine("  只应加载你自己写的、可信的插件。");
            return 2;
        }

        var full = Path.GetFullPath(assemblyPath);
        if (!File.Exists(full))
        {
            Console.WriteLine($"找不到文件：{full}");
            Console.WriteLine("先编译你的插件：dotnet build <你的插件目录> -c Release");
            return 2;
        }

        Console.WriteLine($"加载：{full}");
        Console.WriteLine();

        await using var kernel = new DougongKernel();

        // 诊断订阅：失败与阻塞都要能定位到阶段并给出建议。
        // 这正是指南 §5 讲的那套输出，让读者在这里就看到真实样子。
        using var subscription = kernel.SubscribeStateChanges(change =>
        {
            if (change.To is not (PluginState.Failed or PluginState.Blocked)) return;

            var stage = change.Diagnostic?.Stage is { } s and not PluginFailureStage.Unknown
                ? $" [{s}]"
                : string.Empty;

            Console.WriteLine($"  [kernel] 插件 '{change.PluginId}' 进入 {change.To}{stage}"
                + $"：{change.Reason ?? "(无原因)"}");

            if (change.Diagnostic is { } d)
            {
                Console.WriteLine($"    详情：{d.Code}: {d.Message}");
                if (d.Hint is { Length: > 0 } hint)
                    Console.WriteLine($"    建议：{hint}");
            }
        });

        PluginHandle handle;
        try
        {
            handle = await kernel.LoadAsync(full);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"加载抛出异常：{ex.GetType().Name}: {ex.Message}");
            Console.WriteLine();
            Console.WriteLine("常见原因（按出现频率）：");
            Console.WriteLine("  1. csproj 里 Dougong.Kernel 的引用没写 <Private>false</Private>");
            Console.WriteLine("     → 插件自带一份内核，类型同一性断裂，报「类型不实现 IPlugin」");
            Console.WriteLine("  2. 缺少 EnableDynamicLoading → 依赖程序集没被复制到输出目录");
            Console.WriteLine("  3. 插件目录里少了共享契约程序集（若插件提供宿主会用到的服务）");
            return 1;
        }

        Console.WriteLine($"状态：{handle.State}");

        if (handle.State is PluginState.Active)
        {
            Console.WriteLine();
            Console.WriteLine("成功：内核认这个插件，且激活完成。");

            // 卸载也要验证：只会加载不会卸载的插件，在多轮装卸时会泄漏。
            await kernel.UnloadAsync(handle.Id);
            var remaining = await kernel.GetPluginsAsync();
            var stillThere = remaining.Any(p => p.Id == handle.Id);
            Console.WriteLine(stillThere
                ? "卸载：**失败**——卸载后插件仍在列表里"
                : "卸载：成功");
        }
        else
        {
            Console.WriteLine();
            Console.WriteLine("失败：插件没能进入 Active。上面的诊断说明原因。");
        }

        return handle.State is PluginState.Active ? 0 : 1;
    }
}
