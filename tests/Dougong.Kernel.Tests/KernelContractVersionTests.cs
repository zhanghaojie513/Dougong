using Dougong.Kernel;

namespace Dougong.Kernel.Tests;

public sealed class KernelContractVersionTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private static string ContractsAssemblyPath =>
        Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "..",
            "samples", "Dougong.Sample.Contracts", "bin", Configuration,
            "net11.0", "Dougong.Sample.Contracts.dll"));

    private static string Configuration =>
#if DEBUG
        "Debug";
#else
        "Release";
#endif

    [Fact]
    public void Host_contract_version_is_exposed()
    {
        Assert.True(DougongKernel.ContractVersions.TryGetValue("Dougong.Kernel", out var version));
        Assert.NotNull(version);
    }

    /// <summary>
    /// **兼容范围按宿主的主/次版本判定。**
    /// </summary>
    /// <remarks>
    /// <b>这里原先把具体区间写死在 InlineData 里</b>（"0.1"→true、"0.2"→false），
    /// 于是版本号从 0.1.x 走到 0.2.0 时测试立刻红——**红的不是代码错了，
    /// 是测试把"当前版本"当成了期望值的一部分**。
    ///
    /// <para>
    /// 现在从宿主版本推导：同主次版和更低的都兼容，更高的都不兼容。
    /// 这样升版本不会误报，而判据本身（只看主次版，不看修订号与预发布标签）
    /// 仍然被钉住。
    /// </para>
    /// </remarks>
    [Fact]
    public void Contract_range_is_checked_against_host_major_and_minor()
    {
        var host = DougongKernel.ContractVersions["Dougong.Kernel"];

        // 同主次版：兼容。预发布标签（-alpha.1）不影响判定。
        var sameMinor = $"{host.Major}.{host.Minor}";
        Assert.True(
            new ContractVersion("Dougong.Kernel", sameMinor).IsSatisfiedBy(host),
            $"宿主 {host} 应当兼容范围 {sameMinor}");

        // 更低的次版本：兼容（次版本只增不减）。
        if (host.Minor > 0)
        {
            var lower = $"{host.Major}.{host.Minor - 1}";
            Assert.True(
                new ContractVersion("Dougong.Kernel", lower).IsSatisfiedBy(host),
                $"宿主 {host} 应当兼容更低的范围 {lower}");
        }

        // 更高的次版本：不兼容。
        var higher = $"{host.Major}.{host.Minor + 1}";
        Assert.False(
            new ContractVersion("Dougong.Kernel", higher).IsSatisfiedBy(host),
            $"宿主 {host} 不该兼容更高的范围 {higher}");

        // 更高的主版本：不兼容。
        var nextMajor = $"{host.Major + 1}.0";
        Assert.False(
            new ContractVersion("Dougong.Kernel", nextMajor).IsSatisfiedBy(host),
            $"宿主 {host} 不该兼容更高的主版本 {nextMajor}");
    }

    [Fact]
    public void Invalid_contract_range_is_rejected_at_manifest_construction()
    {
        Assert.Throws<FormatException>(() => PluginManifest.CreateWithContracts(
            "test.bad", "1.0.0", [new ContractVersion("Dougong.Kernel", "not-a-version")]));
    }

    [Fact]
    public async Task Compatible_contract_plugin_loads()
    {
        await using var kernel = new DougongKernel();
        Assert.True(File.Exists(ContractsAssemblyPath), $"契约样例未构建：{ContractsAssemblyPath}");

        var handle = await kernel.LoadAsync(
            ContractsAssemblyPath, "Dougong.Sample.Contracts.CompatibleContractPlugin")
            .AsTask().WaitAsync(Timeout);

        Assert.Equal(PluginState.Active, handle.State);
    }

    [Theory]
    [InlineData("Dougong.Sample.Contracts.IncompatibleContractPlugin", "999.0")]
    [InlineData("Dougong.Sample.Contracts.UnknownContractPlugin", "Dougong.NotProvided")]
    public async Task Incompatible_contract_plugin_is_rejected(string typeName, string messagePart)
    {
        await using var kernel = new DougongKernel();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await kernel.LoadAsync(ContractsAssemblyPath, typeName).AsTask().WaitAsync(Timeout));

        Assert.Contains(messagePart, error.Message);
        Assert.Empty(await kernel.GetPluginsAsync());
    }

    [Fact]
    public async Task Manifest_without_contract_declarations_is_not_restricted()
    {
        await using var kernel = new DougongKernel();
        Assert.True(File.Exists(SampleAssemblyPath), $"样例插件未构建：{SampleAssemblyPath}");

        var handle = await kernel.LoadAsync(SampleAssemblyPath, "Dougong.Sample.Plugin.SamplePlugin")
            .AsTask().WaitAsync(Timeout);

        Assert.Equal(PluginState.Active, handle.State);
    }

    private static string SampleAssemblyPath =>
        Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "..",
            "samples", "Dougong.Sample.Plugin", "bin", Configuration,
            "net11.0", "Dougong.Sample.Plugin.dll"));
}
