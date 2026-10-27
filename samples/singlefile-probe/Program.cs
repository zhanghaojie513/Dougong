using System.Reflection;
using Dougong.Kernel;

// 单文件发布下的内核探针。
//
// 存在的理由：`dotnet publish -p:PublishSingleFile=true` 原先**编译失败**——
// PluginLoadContext 用 `typeof(object).Assembly.Location` 推共享框架目录，
// 而单文件下该属性恒为空（程序集被内嵌进 exe，本来就没有独立路径），
// 分析器报 IL3000，本仓库 TreatWarningsAsErrors 把它提升为 error。
//
// 修好之后需要证据说明"单文件里内核真的能用"，而不只是"能编译"。
// 常规套件都跑在非单文件环境，证明不了这一点，所以单独一个探针。
//
// 它验证三件事：
//   1. 确实运行在单文件模式（Location 全为空）——否则结论不适用
//   2. 真实的插件能在单文件宿主里加载到 Active
//   3. 卸载后插件程序集可回收（没有残留强引用）
//
// 判据用 WeakReference 而不是 AppDomain.GetAssemblies()：.NET Core 上
// 后者在 ALC 卸载后仍可能列出旧程序集，拿它断言会把正常行为报成泄漏
// （本探针第一版踩过这个坑）。内核自己的 KernelRepeatedCycleTests 也用 WeakReference。

var pluginDll = args.Length > 0 ? args[0] : null;
if (string.IsNullOrWhiteSpace(pluginDll))
{
    Console.WriteLine("用法：SingleFileProbe <插件 dll 的路径>");
    return 2;
}

pluginDll = Path.GetFullPath(pluginDll);
if (!File.Exists(pluginDll))
{
    Console.WriteLine($"SINGLEFILE-PROBE: 找不到插件 {pluginDll}");
    return 2;
}

// 先证明自己确实在单文件模式里跑。这一条不成立时直接判失败，
// 不能让"在非单文件环境跑出来的 PASS"冒充本探针的结论。
//
// IL3000 在这里是刻意的：探针**就是要观察 Location 为空**，
// 并据此确认自己在单文件模式里。分析器无法表达这个意图。
#pragma warning disable IL3000
var corelib = typeof(object).Assembly.Location;
var self = Assembly.GetEntryAssembly()?.Location;
#pragma warning restore IL3000

var inSingleFile = string.IsNullOrEmpty(corelib) && string.IsNullOrEmpty(self);

Console.WriteLine($"  object.Location    : [{corelib}]");
Console.WriteLine($"  entry.Location     : [{self}]");
Console.WriteLine($"  单文件模式         : {(inSingleFile ? "是" : "否")}");
Console.WriteLine($"  BaseDirectory      : [{AppContext.BaseDirectory}]");

if (!inSingleFile)
{
    Console.WriteLine("SINGLEFILE-PROBE: FAIL 未运行在单文件模式，结论不适用");
    return 1;
}

await using var kernel = new DougongKernel();

var failures = new List<string>();
using var subscription = kernel.SubscribeStateChanges(change =>
{
    if (change.To is PluginState.Failed or PluginState.Blocked)
    {
        failures.Add($"插件 {change.PluginId} 进入 {change.To}" +
                     (change.Diagnostic is { } d ? $"：{d.Code} {d.Message}" : string.Empty));
    }
});

var handle = await kernel.LoadAsync(pluginDll);
Console.WriteLine($"  加载结果           : {handle.State}");

if (handle.State is not PluginState.Active)
{
    Console.WriteLine("SINGLEFILE-PROBE: FAIL 插件未激活");
    foreach (var f in failures) Console.WriteLine($"  {f}");
    return 1;
}

// 只持弱引用：强引用会让程序集永远回收不掉，探针自己就成了泄漏源。
var pluginName = Path.GetFileNameWithoutExtension(pluginDll);
var pluginRef = new WeakReference(AppDomain.CurrentDomain.GetAssemblies()
    .FirstOrDefault(a => a.GetName().Name == pluginName));

await kernel.UnloadAsync(handle.Id);
var remaining = (await kernel.GetPluginsAsync()).Count;
Console.WriteLine($"  卸载后剩余插件     : {remaining}");

if (remaining != 0)
{
    Console.WriteLine("SINGLEFILE-PROBE: FAIL 卸载后插件仍在列表中");
    return 1;
}

var reclaimed = await CollectUntilAsync(() => !pluginRef.IsAlive);
Console.WriteLine($"  插件程序集可回收   : {(reclaimed ? "是" : "否")}");

if (!reclaimed)
{
    Console.WriteLine("SINGLEFILE-PROBE: FAIL 卸载后插件程序集仍被强引用持有");
    return 1;
}

if (failures.Count > 0)
{
    Console.WriteLine("SINGLEFILE-PROBE: FAIL 出现失败状态");
    foreach (var f in failures) Console.WriteLine($"  {f}");
    return 1;
}

Console.WriteLine("SINGLEFILE-PROBE: ALL PASS");
return 0;

static async Task<bool> CollectUntilAsync(Func<bool> condition)
{
    // GC 是协作式的：太早断言会把正常延迟当成泄漏。
    // 逐轮加大等待，总计约 2 秒，与内核测试的做法一致。
    for (var i = 0; i < 20 && !condition(); i++)
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        await Task.Delay(100);
    }

    return condition();
}
