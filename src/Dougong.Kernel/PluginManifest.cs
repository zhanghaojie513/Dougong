namespace Dougong.Kernel;

/// <summary>
/// 一条服务需求。它属于消费方，可以指向当前尚不存在的提供方：
/// 内核保存未满足的需求，用于后续重新评估。<see cref="Optional"/> 为 true 时，
/// 缺失不会阻止激活，但提供方后续出现时仍会触发重新评估。
/// <see cref="ProviderId"/> 非空时表示同键多提供方中只接受该插件，
/// 用于显式消解歧义；此时不再按唯一性判断。
/// </summary>
public sealed record ServiceRequirement(
    Type ServiceType,
    string? Name = null,
    bool Optional = false,
    PluginId? ProviderId = null)
{
    /// <summary>该需求是否排斥除 <paramref name="owner"/> 之外的提供方。</summary>
    public bool Accepts(PluginId owner) => ProviderId is not { } required || required == owner;
}

/// <summary>
/// 插件对某个共享契约的版本支持范围。M0 只支持「主版本相同且不低于最低次版本」的
/// <c>major.minor</c> 形式，不做完整 SemVer 区间解析。
/// </summary>
public sealed record ContractVersion(string ContractName, string Range)
{
    /// <summary>宿主契约版本是否落在声明范围内。</summary>
    public bool IsSatisfiedBy(Version hostVersion)
    {
        var (major, minor) = Parse();
        return hostVersion.Major == major && hostVersion.Minor >= minor;
    }

    internal (int Major, int Minor) Parse()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(Range);
        var parts = Range.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2
            || !int.TryParse(parts[0], out var major)
            || !int.TryParse(parts[1], out var minor))
        {
            throw new FormatException(
                $"契约版本范围 '{Range}' 无效；M0 仅支持 'major.minor' 形式。");
        }

        return (major, minor);
    }
}

/// <summary>
/// 插件清单。版本约束与程序集兼容检查属于加载层，这里只表达运行时所需的最小信息。
/// <see cref="DependsOn"/> 是插件级依赖：被依赖插件必须已激活，本插件才能激活。
/// </summary>
public sealed record PluginManifest
{
    public PluginManifest(
        PluginId id,
        string version,
        IReadOnlyList<ServiceRequirement>? requiredServices = null,
        IReadOnlyList<PluginId>? dependsOn = null,
        IReadOnlyList<ContractVersion>? contractVersions = null,
        PluginDisplayInfo? display = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        Id = id;
        Version = version;
        // 复制为只读快照：调用方后续改动原集合不能悄悄改变内核的依赖判定。
        RequiredServices = Copy(requiredServices);
        DependsOn = Copy(dependsOn);
        ContractVersions = Copy(contractVersions);
        Display = display;
        foreach (var contract in ContractVersions) contract.Parse();
    }

    private static IReadOnlyList<T> Copy<T>(IReadOnlyList<T>? source) =>
        source is null ? Array.AsReadOnly(Array.Empty<T>()) : Array.AsReadOnly(source.ToArray());

    public PluginId Id { get; }

    public string Version { get; }

    /// <summary>包含必需与可选服务需求；用 <see cref="ServiceRequirement.Optional"/> 区分。</summary>
    public IReadOnlyList<ServiceRequirement> RequiredServices { get; }

    /// <summary>必须已处于 <see cref="PluginState.Active"/> 的插件。</summary>
    public IReadOnlyList<PluginId> DependsOn { get; }

    /// <summary>插件声明支持的宿主契约版本范围；为空表示不做版本校验。</summary>
    public IReadOnlyList<ContractVersion> ContractVersions { get; }

    /// <summary>
    /// 展示元信息。为 null 时宿主应回退到 <see cref="PluginId.Value"/>。
    /// </summary>
    /// <remarks>
    /// <b>为什么放在 manifest 而不是插件目录旁的元数据文件</b>：两个数据源
    /// 会不一致——目录里写着 Title、程序集里是另一个，宿主无法判断以谁为准。
    /// manifest 来自插件本身，是唯一权威。
    /// <para>
    /// <b>图标刻意不在其中</b>：图标是 UI 资源，SDK 一旦支持路径引用就要定义
    /// 目录布局、体积上限、越界与符号链接校验规则——那是宿主的产品策略。
    /// 宿主自己约定插件目录旁边放什么文件更干净。
    /// </para>
    /// </remarks>
    public PluginDisplayInfo? Display { get; }

    public static PluginManifest Create(string id, string version, params ServiceRequirement[] requiredServices) =>
        new(PluginId.Parse(id), version, requiredServices);

    public static PluginManifest CreateWithDependencies(
        string id,
        string version,
        IReadOnlyList<ServiceRequirement> requiredServices,
        IReadOnlyList<PluginId> dependsOn) =>
        new(PluginId.Parse(id), version, requiredServices, dependsOn);

    public static PluginManifest CreateWithContracts(
        string id,
        string version,
        IReadOnlyList<ContractVersion> contractVersions) =>
        new(PluginId.Parse(id), version, contractVersions: contractVersions);
}
