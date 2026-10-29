using Dougong.Kernel;

namespace Dougong.Kernel.Tests;

/// <summary>
/// 目录级批量加载（<c>LoadFromDirectoryAsync</c>）的验收。
/// </summary>
/// <remarks>
/// 这些行为是宿主插件管理 UI 直接依赖的，每一条都必须能被观察到：
/// <list type="bullet">
/// <item>"依赖库"要能被识别为跳过，而不是失败——否则一个带三个依赖的插件
/// 会在 UI 上显示成"1 个成功 3 个失败"</item>
/// <item>"多个候选"要与"加载失败"分开——前者的用户动作是选择，后者是排查</item>
/// <item>单个失败不能中断整批——9 成功 1 失败必须如实报告成 9+1</item>
/// </list>
/// </remarks>
public sealed class KernelDirectoryLoadingTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    private static string Flavor =>
#if DEBUG
        "Debug";
#else
        "Release";
#endif

    private static string SamplePluginPath => Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "..", "..",
        "samples", "Dougong.Sample.Plugin", "bin", Flavor, "net10.0",
        "Dougong.Sample.Plugin.dll"));

    private static string TwoPluginsPath => Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "..", "..",
        "samples", "Dougong.Sample.TwoPlugins", "bin", Flavor, "net10.0",
        "Dougong.Sample.TwoPlugins.dll"));

    /// <summary>
    /// 重复加载同一插件必须报"已存在"，不能混进 Failed。
    /// </summary>
    /// <remarks>
    /// 两种情况的用户动作相反：**已存在不用做任何事**（也许只是想刷新列表），
    /// 而真失败要用户去排查。混在 Failed 里，UI 只能靠字符串匹配 reason
    /// 来区分——SDK 一改措辞就失效。
    /// </remarks>
    [Fact]
    public async Task Duplicate_load_reports_already_loaded()
    {
        using var dir = new PluginDirectory();
        dir.CopyIn(SamplePluginPath, "Dougong.Sample.Plugin.dll");

        await using var kernel = new DougongKernel();

        var first = await kernel.LoadFromDirectoryAsync(dir.Path).AsTask().WaitAsync(Timeout);
        Assert.Equal(PluginLoadStatus.Loaded, Assert.Single(first).Status);

        // 再扫一次：同一个插件已在内核里。
        var second = await kernel.LoadFromDirectoryAsync(dir.Path).AsTask().WaitAsync(Timeout);

        var entry = Assert.Single(second);
        Assert.Equal(PluginLoadStatus.AlreadyLoaded, entry.Status);

        // 已存在时也要给出 PluginId：UI 需要它来关联到列表里已有的那一行。
        Assert.NotNull(entry.PluginId);
        Assert.Equal("dougong.sample.plugin", entry.PluginId!.Value.Value);

        // 不该被当成用户要处理的失败。
        Assert.False(entry.IsUserVisible);

        // 内核里仍然只有一个。
        var summaries = await kernel.GetPluginsAsync().AsTask().WaitAsync(Timeout);
        Assert.Single(summaries);
    }

    /// <summary>扫到的插件全部被装上。</summary>
    [Fact]
    public async Task Loads_every_plugin_in_directory()
    {
        using var dir = new PluginDirectory();
        dir.CopyIn(SamplePluginPath, "Dougong.Sample.Plugin.dll");

        await using var kernel = new DougongKernel();
        var results = await kernel.LoadFromDirectoryAsync(dir.Path).AsTask().WaitAsync(Timeout);

        var loaded = results.Where(r => r.Status == PluginLoadStatus.Loaded).ToArray();
        Assert.Single(loaded);
        Assert.Equal("dougong.sample.plugin", loaded[0].PluginId!.Value.Value);
        Assert.NotNull(loaded[0].AssemblyPath);
    }

    /// <summary>
    /// 一个 dll 里有多个 IPlugin 时报 Ambiguous，并列出候选——**不擅自挑一个**。
    /// </summary>
    /// <remarks>
    /// 挑错的后果是装了用户没想要的东西，而用户看不到"还有别的"，
    /// 比直接报错更糟。所以这里同时断言"没装任何一个"。
    /// </remarks>
    [Fact]
    public async Task Multiple_plugins_in_one_assembly_are_ambiguous()
    {
        using var dir = new PluginDirectory();
        dir.CopyIn(TwoPluginsPath, "Dougong.Sample.TwoPlugins.dll");

        await using var kernel = new DougongKernel();
        var results = await kernel.LoadFromDirectoryAsync(dir.Path).AsTask().WaitAsync(Timeout);

        var ambiguous = Assert.Single(results);
        Assert.Equal(PluginLoadStatus.Ambiguous, ambiguous.Status);
        Assert.Null(ambiguous.PluginId);
        Assert.NotNull(ambiguous.Reason);
        Assert.Contains("typeName", ambiguous.Reason);

        // 两个候选都要列出来，宿主要用它做选择界面。
        Assert.NotNull(ambiguous.AmbiguousTypeNames);
        Assert.Equal(2, ambiguous.AmbiguousTypeNames!.Count);
        Assert.Contains(ambiguous.AmbiguousTypeNames, n => n.Contains("FirstPlugin"));
        Assert.Contains(ambiguous.AmbiguousTypeNames, n => n.Contains("SecondPlugin"));

        // 关键：一个都没装。事先装了再报错会在内核里留下用户没要的插件。
        var summaries = await kernel.GetPluginsAsync().AsTask().WaitAsync(Timeout);
        Assert.Empty(summaries);
    }

    /// <summary>
    /// 非插件 dll 标为 Skipped，且**不让整批失败**。
    /// </summary>
    /// <remarks>
    /// 真实插件包自带 Contracts、Helpers 等依赖库。它们被扫到是正常的，
    /// 归进"失败"会让 UI 显示成一片红。
    /// </remarks>
    [Fact]
    public async Task Non_plugin_assembly_is_skipped_not_failed()
    {
        using var dir = new PluginDirectory();
        dir.CopyIn(SamplePluginPath, "Dougong.Sample.Plugin.dll");

        // 拷一个没有 IPlugin 实现的程序集进来（内核自身就是）。
        var kernelAssembly = typeof(DougongKernel).Assembly.Location;
        dir.CopyIn(kernelAssembly, "Dougong.Kernel.dll");

        await using var kernel = new DougongKernel();
        var results = await kernel.LoadFromDirectoryAsync(dir.Path).AsTask().WaitAsync(Timeout);

        Assert.Equal(2, results.Count);

        var skipped = Assert.Single(results, r => r.Status == PluginLoadStatus.Skipped);
        Assert.Contains("无 IPlugin", skipped.Reason);
        Assert.False(skipped.IsUserVisible);

        Assert.Single(results, r => r.Status == PluginLoadStatus.Loaded);
    }

    /// <summary>
    /// 部分失败不抛异常，整批结果照常返回。
    /// </summary>
    /// <remarks>
    /// "9 成功 1 失败"必须如实报成 9+1，而不是显示成"装插件失败"。
    /// 抛异常会让宿主丢掉前面已经装好的那些。
    /// </remarks>
    [Fact]
    public async Task Partial_failure_does_not_abort_batch()
    {
        using var dir = new PluginDirectory();
        dir.CopyIn(SamplePluginPath, "Dougong.Sample.Plugin.dll");
        dir.CopyIn(TwoPluginsPath, "Dougong.Sample.TwoPlugins.dll");

        await using var kernel = new DougongKernel();

        // 不抛异常是这条的核心断言——按目录加载时个别失败是常态。
        var results = await kernel.LoadFromDirectoryAsync(dir.Path).AsTask().WaitAsync(Timeout);

        Assert.Equal(2, results.Count);
        Assert.Single(results, r => r.Status == PluginLoadStatus.Loaded);
        Assert.Single(results, r => r.Status == PluginLoadStatus.Ambiguous);

        // 成功那个确实进了内核。
        var summaries = await kernel.GetPluginsAsync().AsTask().WaitAsync(Timeout);
        Assert.Single(summaries);
    }

    /// <summary>
    /// 不递归子目录。子目录是依赖区。
    /// </summary>
    /// <remarks>
    /// 递归会让依赖目录里的 dll 也被当成插件候选，
    /// 而其中一个含多个 IPlugin 时整批会多出莫名其妙的 Ambiguous 条目。
    /// </remarks>
    [Fact]
    public async Task Does_not_recurse_into_subdirectories()
    {
        using var dir = new PluginDirectory();
        Directory.CreateDirectory(Path.Combine(dir.Path, "deps"));
        File.Copy(SamplePluginPath,
            Path.Combine(dir.Path, "deps", "Dougong.Sample.Plugin.dll"));

        await using var kernel = new DougongKernel();
        var results = await kernel.LoadFromDirectoryAsync(dir.Path).AsTask().WaitAsync(Timeout);

        Assert.Empty(results);
    }

    /// <summary>空目录返回空列表，不抛。</summary>
    [Fact]
    public async Task Empty_directory_returns_empty_list()
    {
        using var dir = new PluginDirectory();

        await using var kernel = new DougongKernel();
        var results = await kernel.LoadFromDirectoryAsync(dir.Path).AsTask().WaitAsync(Timeout);

        Assert.Empty(results);
    }

    /// <summary>目录不存在时报错——静默返回空列表会让宿主以为"这里没有插件"。</summary>
    [Fact]
    public async Task Missing_directory_throws()
    {
        await using var kernel = new DougongKernel();

        await Assert.ThrowsAsync<DirectoryNotFoundException>(
            async () => await kernel.LoadFromDirectoryAsync(
                Path.Combine(Path.GetTempPath(), "no-such-dir-" + Guid.NewGuid().ToString("N")))
                .AsTask().WaitAsync(Timeout));
    }

    /// <summary>
    /// 加载完成后 <see cref="PluginSummary"/> 带展示信息。
    /// </summary>
    /// <remarks>
    /// 展示信息从 manifest 来。<see cref="PluginSummary"/> 只带值、不带 manifest，
    /// 否则 <c>ServiceRequirement.Type</c> 会长期持有插件 ALC 的类型，
    /// 阻止程序集回收。
    /// </remarks>
    [Fact]
    public async Task Summary_carries_display_info()
    {
        await using var kernel = new DougongKernel();
        var handle = await kernel.RegisterAsync(() => new TitledPlugin())
            .AsTask().WaitAsync(Timeout);

        var summaries = await kernel.GetPluginsAsync().AsTask().WaitAsync(Timeout);
        var summary = Assert.Single(summaries);

        Assert.Equal(handle.Id, summary.Id);
        Assert.NotNull(summary.Display);
        Assert.Equal("有标题的插件", summary.Display!.Title);
        Assert.Equal("用来验证展示字段", summary.Display.Description);
        Assert.Equal("测试", summary.Display.Author);
    }

    /// <summary>没声明展示信息时为 null，不是空对象。</summary>
    /// <remarks>
    /// 空对象会让宿主无法区分"作者没填"与"填了空串"——
    /// 前者该回退到 Id，后者是作者写错了。
    /// </remarks>
    [Fact]
    public async Task Summary_display_is_null_when_not_declared()
    {
        await using var kernel = new DougongKernel();
        await kernel.RegisterAsync(() => new UntitledPlugin()).AsTask().WaitAsync(Timeout);

        var summaries = await kernel.GetPluginsAsync().AsTask().WaitAsync(Timeout);
        Assert.Null(Assert.Single(summaries).Display);
    }

    /// <summary>声明了展示信息的插件。</summary>
    private sealed class TitledPlugin : IPlugin
    {
        public PluginId Id { get; } = PluginId.Parse("test.titled");
        public PluginManifest Manifest { get; } = new(
            PluginId.Parse("test.titled"), "1.0.0",
            display: new PluginDisplayInfo
            {
                Title = "有标题的插件",
                Description = "用来验证展示字段",
                Author = "测试",
            });

        public ValueTask ActivateAsync(PluginContext context, CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;
    }

    /// <summary>不声明展示信息的插件。</summary>
    private sealed class UntitledPlugin : IPlugin
    {
        public PluginId Id { get; } = PluginId.Parse("test.untitled");
        public PluginManifest Manifest { get; } = PluginManifest.Create("test.untitled", "1.0.0");

        public ValueTask ActivateAsync(PluginContext context, CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;
    }

    /// <summary>
    /// 临时插件目录。用固定前缀，避免每次运行留下不同名字的垃圾目录。
    /// </summary>
    private sealed class PluginDirectory : IDisposable
    {
        public PluginDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "dougong-dirload-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        /// <summary>把程序集拷进来并改成目标文件名。</summary>
        /// <remarks>
        /// 必须改名：同一目录里两个同名 dll 会互相覆盖，
        /// 而这里恰恰要测"多个不同插件在同一目录"。
        /// </remarks>
        public void CopyIn(string source, string fileName)
        {
            if (!File.Exists(source))
                throw new FileNotFoundException($"测试固件缺失：{source}（先构建 samples）", source);

            File.Copy(source, System.IO.Path.Combine(Path, fileName), overwrite: true);
        }

        public void Dispose()
        {
            // Windows 上 ALC 可能仍持有文件句柄，删不掉不是失败。
            // 目录名带随机后缀，不会互相污染。
            try { Directory.Delete(Path, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
