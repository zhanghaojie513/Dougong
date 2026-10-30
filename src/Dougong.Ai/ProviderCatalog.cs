namespace Dougong.Ai;

/// <summary>
/// 一组具名 provider，以及"当前用哪个"。
/// </summary>
/// <remarks>
/// <b>这个类型只解决「可定义多个」，不解决「运行时可切换」</b>——后者由宿主
/// 拿 <see cref="Providers"/> 自己实现选择 UI。分清楚这两件事很重要：
/// 有集合没有切换入口时，用户至少能通过配置决定用哪个；
/// 而只有切换入口没有集合时，切换就无从谈起。
/// <para>
/// <b>为什么预设表不是「provider 矩阵」</b>：<c>goal.md</c> §3 把逐家对齐协议方言
/// 列为反目标，理由是那是个维护成本黑洞。而这里的预设每条只有三个字段
/// （端点、线协议、密钥变量名），<b>不含任何厂商特例代码</b>——
/// 协议差异早就收敛在 <see cref="WireCompat"/> 与三种 wire format 里。
/// 两者的维护面差一个数量级，不是同一件事。
/// </para>
/// </remarks>
public sealed class ProviderCatalog
{
    /// <summary>
    /// 内置预设：端点、线协议与模型能力表，不含任何厂商特例逻辑。
    /// </summary>
    /// <remarks>
    /// <b>数据来自 models.dev，由 <c>scripts/sync-provider-presets.py</c> 生成</b>
    /// （<see cref="ProviderPresets"/>）。不手写这张表：42 家以上的端点、协议、
    /// 上下文窗口、输出上限、推理档位都是会变的事实，手写必然漂移，
    /// 而漂移的表现是"配置看起来正常，只是连到了错的地方"或"上下文永远不触发压缩"。
    /// <para>
    /// <b>为什么预设里没有密钥变量名</b>：各家的环境变量名不像端点那样有统一来源，
    /// 猜一个会给出一个"设了也没用"的提示。所以预设只带端点与模型能力，
    /// 密钥变量名由用户配置或 <see cref="ChatClientFactory"/> 的环境变量路径给。
    /// </para>
    /// <para>
    /// <b>仍然不是 provider 矩阵</b>：每条只是一行数据，没有厂商特例代码。
    /// 协议差异收敛在三种 wire format 与 <see cref="WireCompat"/>。判据见 <c>goal.md</c> §3。
    /// </para>
    /// </remarks>
    private static readonly Dictionary<string, ProviderPreset> BuiltInPresets =
        ProviderPresets.All.ToDictionary(
            entry => entry.Key,
            entry => new ProviderPreset(
                BaseUrl: entry.Value.BaseUrl,
                Protocol: entry.Value.Protocol,
                ApiKeyEnvironmentVariable: entry.Value.ApiKeyEnvironmentVariable),
            StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<string, ChatProviderConfig> _providers;

    private ProviderCatalog(
        Dictionary<string, ChatProviderConfig> providers,
        string selectedName,
        string? sourcePath)
    {
        _providers = providers;
        SelectedName = selectedName;
        SourcePath = sourcePath;
    }

    /// <summary>当前选中的 provider 名。</summary>
    public string SelectedName { get; }

    /// <summary>配置来源文件；来自环境变量回退时为 null。</summary>
    public string? SourcePath { get; }

    /// <summary>全部 provider，按名字排序。</summary>
    public IReadOnlyList<ChatProviderConfig> Providers =>
        _providers.Values.OrderBy(p => p.Name, StringComparer.Ordinal).ToArray();

    /// <summary>当前选中的 provider。</summary>
    /// <exception cref="InvalidOperationException">选中的名字不在集合里。</exception>
    public ChatProviderConfig Selected =>
        _providers.TryGetValue(SelectedName, out var config)
            ? config
            : throw new InvalidOperationException(
                $"选中的 provider '{SelectedName}' 不存在；已配置的有：{string.Join(", ", _providers.Keys)}。");

    /// <summary>用一组配置建立目录。</summary>
    /// <param name="providers">provider 列表；名字必须唯一。</param>
    /// <param name="selectedName">当前选中的名字；null 表示取第一个。</param>
    /// <param name="sourcePath">配置来源，仅用于诊断展示。</param>
    public static ProviderCatalog Create(
        IEnumerable<ChatProviderConfig> providers,
        string? selectedName = null,
        string? sourcePath = null)
    {
        ArgumentNullException.ThrowIfNull(providers);

        var map = new Dictionary<string, ChatProviderConfig>(StringComparer.OrdinalIgnoreCase);
        foreach (var provider in providers)
        {
            // 重名必须报错而不是后者覆盖前者：配置文件里写重复段是笔误，
            // 静默覆盖会让用户改的那个"没生效"且毫无线索。
            if (!map.TryAdd(provider.Name, provider))
            {
                throw new ArgumentException(
                    $"provider 名重复：'{provider.Name}'。每个 provider 必须有唯一的名字。",
                    nameof(providers));
            }
        }

        if (map.Count == 0)
            throw new ArgumentException("至少要有一个 provider。", nameof(providers));

        var selected = selectedName is null or ""
            ? map.Keys.First()
            : selectedName;

        return new ProviderCatalog(map, selected, sourcePath);
    }

    /// <summary>
    /// 在预设之上应用一组覆盖：同名整体替换，新名直接新增。
    /// </summary>
    /// <remarks>
    /// <b>整体替换而不是逐字段合并</b>：逐字段合并会造出"我明知这个预设的端点不对，
    /// 但改不动它"的困境——用户写了 base_url 却没写 protocol，就会拿到预设的
    /// 协议配自己的端点，这类组合通常不成立。让用户写一个完整的条目更清楚。
    /// </remarks>
    public static ProviderCatalog WithOverrides(
        IEnumerable<ChatProviderConfig> overrides, string? selectedName, string? sourcePath)
    {
        ArgumentNullException.ThrowIfNull(overrides);

        var merged = new Dictionary<string, ChatProviderConfig>(StringComparer.OrdinalIgnoreCase);

        foreach (var preset in BuiltInPresets)
        {
            merged[preset.Key] = new ChatProviderConfig
            {
                Name = preset.Key,
                BaseUrl = preset.Value.BaseUrl,
                // 预设没有模型名：端点不会告诉我们要用哪个模型，
                // 猜一个会变成"配好了却 404"。空字符串在 Create 时由使用方校验。
                Model = string.Empty,
                Protocol = preset.Value.Protocol,
                ApiKeyEnvironmentVariable = preset.Value.ApiKeyEnvironmentVariable,
            };
        }

        foreach (var provider in overrides)
            merged[provider.Name] = provider;

        var selected = selectedName is null or "" ? merged.Keys.First() : selectedName;
        return new ProviderCatalog(merged, selected, sourcePath);
    }

    /// <summary>内置预设的名字与端点，供诊断与文档使用。</summary>
    public static IReadOnlyDictionary<string, ProviderPreset> Presets => BuiltInPresets;

    /// <summary>
    /// 用一组环境变量得到的配置覆盖当前选中项，其余 provider 保持不变。
    /// </summary>
    /// <remarks>
    /// <b>为什么是"覆盖选中项"而不是"另起一个目录"</b>：环境变量表达的是
    /// "这次改用这个端点跑"，不是"我只有这一个 provider"。若另起目录，
    /// 配置文件里那些 provider 会从 <c>/model</c> 列表里凭空消失，
    /// 用户会以为配置没被读到。
    /// <para>
    /// 环境变量那条用 <c>DOUGONG_PROVIDER_NAME</c> 命名（默认 <c>default</c>）。
    /// 若它与配置文件的某个条目同名，就替换那一条；否则新增一条并选中它。
    /// </para>
    /// </remarks>
    public ProviderCatalog WithSelectedOverride(ChatProviderConfig overrideProvider)
    {
        ArgumentNullException.ThrowIfNull(overrideProvider);

        var merged = new Dictionary<string, ChatProviderConfig>(_providers, StringComparer.OrdinalIgnoreCase)
        {
            [overrideProvider.Name] = overrideProvider,
        };

        return new ProviderCatalog(
            merged, overrideProvider.Name, $"环境变量（覆盖 {SourcePath ?? "无配置文件"}）");
    }

    /// <summary>
    /// 切换选中的 provider，返回新目录。
    /// </summary>
    /// <remarks>
    /// 返回新实例而不是改字段：目录是不可变的一份"当前配置快照"，
    /// 就地修改会让持有旧引用的调用方看到不一致的状态。
    /// </remarks>
    /// <exception cref="ArgumentException">名字不在目录里。</exception>
    public ProviderCatalog WithSelected(string name)
    {
        if (!_providers.ContainsKey(name))
        {
            throw new ArgumentException(
                $"没有名为 '{name}' 的 provider；可选：{string.Join(", ", _providers.Keys)}。",
                nameof(name));
        }

        return new ProviderCatalog(_providers, name, SourcePath);
    }

    /// <summary>按名字查一个 provider；不存在时返回 null。</summary>
    public ChatProviderConfig? Find(string name) =>
        _providers.TryGetValue(name, out var config) ? config : null;

    /// <summary>
    /// 查一个模型的能力数据（上下文窗口、输出上限、推理档位）。
    /// </summary>
    /// <remarks>
    /// <b>宿主需要它才能不问用户已知的事实</b>：选完模型后上下文窗口不该让人手填。
    /// 填错的代价不对称——填大则压缩永不触发（撞上 provider 上限才失败），
    /// 填小则过早压缩（历史被无谓地摘掉），两种都不报错。
    /// <para>
    /// 查不到时返回 null，调用方应当如实说"未知"，不要编一个默认值——
    /// 一个看起来精确的错数字比"未知"更糟。
    /// </para>
    /// </remarks>
    /// <param name="providerName">provider 名。</param>
    /// <param name="modelId">模型标识。</param>
    public static ModelCapabilities? FindModelCapabilities(string providerName, string modelId) =>
        ProviderPresets.FindModel(providerName, modelId);

    /// <summary>全部内置预设的模型能力表，供宿主渲染模型选择器。</summary>
    public static IReadOnlyDictionary<string, ProviderPresetData> AllPresets =>
        ProviderPresets.All;

    /// <summary>内置预设的一条。</summary>
    /// <param name="BaseUrl">API 基地址。</param>
    /// <param name="Protocol">线协议。</param>
    /// <param name="ApiKeyEnvironmentVariable">密钥环境变量名——**是名字，不是值**。</param>
    public sealed record ProviderPreset(
        string BaseUrl,
        ChatProtocol Protocol,
        string? ApiKeyEnvironmentVariable);
}
