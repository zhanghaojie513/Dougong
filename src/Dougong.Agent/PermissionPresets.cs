using System.Text.Json;

namespace Dougong.Agent;

/// <summary>
/// 审批策略：哪些工具在什么条件下需要人确认。
/// </summary>
/// <remarks>
/// <b>它只是"要不要问人"，不是"允不允许做"</b>：允许与否由审批者的答复决定，
/// 而这个策略决定要不要打扰他。把它叫做"权限"容易误解成能力边界——
/// 真正的边界是路径守卫（<see cref="WorkspacePathGuard"/>）与拦截器
/// （<see cref="IToolInterceptor"/>）。
/// </remarks>
public enum ApprovalPolicy
{
    /// <summary>
    /// 每次都问。
    /// </summary>
    /// <remarks>
    /// 最保守的一档。连只读调用也问——代价是交互次数多，
    /// 收益是"模型做的每件事都经过你"。
    /// </remarks>
    AskAlways,

    /// <summary>
    /// 只对有副作用或越界风险的调用问。
    /// </summary>
    /// <remarks>
    /// <b>默认档</b>：跳过 <see cref="ToolPermission.Access"/> 为 <c>None</c> 的纯内存工具
    /// （与 <see cref="PermissionPolicyApprover"/> 同一判据），其余照问。
    /// 注意它是**按"工具是否声明了访问"分的，不是按读写分的**：
    /// `read_file` 声明 <see cref="ToolAccess.Read"/> 且带路径，
    /// 所以它**照样会问**。这一档实际的效果是
    /// "纯内存工具（提问、计划、看目录）不打扰，其余都问"。
    /// </remarks>
    AskOnRisk,

    /// <summary>
    /// 从不问。
    /// </summary>
    /// <remarks>
    /// <b>危险档，但它有正当用途</b>：批量脚本、自动化验收、CI。
    /// 这些场景里没有人在看，问也问不出答案——而"问不到人就拒绝"
    /// 会让它们全部失败。
    /// <para>
    /// <b>它不妨碍拦截器</b>：拦截器在审批**之前**跑，
    /// 所以"不问人"不等于"什么都能做"。
    /// </para>
    /// </remarks>
    Never,
}

/// <summary>
/// 一个具名的权限预设。
/// </summary>
/// <param name="Key">稳定键，切换与持久化用它。</param>
/// <param name="Name">显示名；缺省用 <paramref name="Key"/>。</param>
/// <param name="Description">一句话说明，给选择器用。</param>
/// <param name="Policy">它写过的审批策略。</param>
public sealed record PermissionPreset(
    string Key,
    string? Name,
    string? Description,
    ApprovalPolicy Policy)
{
    /// <summary>显示名。</summary>
    public string DisplayName => Name ?? Key;
}

/// <summary>
/// 具名权限预设的注册表与当前选择。
/// </summary>
/// <remarks>
/// <b>解决什么问题</b>：审批策略只有三档，而用户面对的是"这个会话要多严格"。
/// 让用户记住 <c>AskOnRisk</c> 这种名字不现实；而不同场景要的档位不同
/// （日常编码 vs 无人值守跑批）。
///
/// <para>
/// <b>与 dsh 的差别必须说清</b>：dsh 的预设是**两个开关的打包**——
/// 沙箱模式（<c>sandbox/mode</c>）与审批策略（<c>approval/policy</c>）。
/// <b>我们没有沙箱</b>（项目明确不做 OS 级隔离，见 <c>dsh-capability-gap.md</c> 阶段 3），
/// 所以预设只打包审批策略这一维。
/// </para>
///
/// <para>
/// <b>这个收窄会误导人，所以要做两件事</b>：
/// <list type="number">
/// <item>预设的描述里明说"不含沙箱隔离"——不能让用户以为选了最宽松的档
/// 就"只是少点确认"，而实际上三条路径守卫都还在，反过来也不能让他以为
/// 选了最严的档就"文件被隔离了"。</item>
/// <item>键名不用 dsh 的 <c>workspace-write</c> / <c>danger-full-access</c>：
/// 那两个名字都在描述**沙箱范围**，而我们没有那个东西。
/// 用它们会让人以为文件真的被限在某个范围内。</item>
/// </list>
/// </para>
///
/// <para>
/// <b><c>custom</c> 是派生值不是可选目标</b>（照 dsh）：当当前策略不匹配任何
/// 具名预设时（宿主直接改了策略，或组合方式变了），当前值显示为
/// <see cref="CustomKey"/>。它<b>不出现在 <see cref="Available"/> 里</b>
/// （那就不是"可切换的目标"），但会以 `Selectable: false` 出现在
/// <see cref="Options"/> 里——界面需要它来显示"当前是自定义"。
/// </para>
/// </remarks>
public sealed class PermissionPresets
{
    /// <summary>当前值不匹配任何具名预设时的显示键。</summary>
    /// <remarks>
    /// <b>它是保留键</b>：配置里不能用它当预设名，否则"派生值"与"真的有个预设
    /// 叫这个"会混在一起。
    /// </remarks>
    public const string CustomKey = "custom";

    private readonly List<PermissionPreset> _presets;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private ApprovalPolicy _current;

    /// <summary>构造。</summary>
    /// <param name="presets">
    /// 预设表。<b>在构造期校验</b>（照 dsh 的"misconfiguration fails at plugin load"）：
    /// 空表、重复键、用保留键都抛。
    /// </param>
    /// <param name="current">初始策略。</param>
    public PermissionPresets(IEnumerable<PermissionPreset> presets, ApprovalPolicy current)
    {
        ArgumentNullException.ThrowIfNull(presets);

        _presets = [.. presets];
        Validate(_presets);
        _current = current;
    }

    /// <summary>
    /// 默认预设表。
    /// </summary>
    /// <remarks>
    /// <b>两个预设就够</b>：日常与无人值守。dsh 也是两个（外加保留的 custom/auto）。
    /// 加更多只会让选择器变成需要读文档才能选的东西。
    /// </remarks>
    public static IReadOnlyList<PermissionPreset> DefaultTable { get; } =
    [
        new("interactive", "交互式",
            "写操作与命令需要你确认；纯内存工具不打扰。不含沙箱隔离。",
            ApprovalPolicy.AskOnRisk),
        new("unattended", "无人值守",
            "已知权限不询问审批；未知权限仍交给入口审批器。用于批量脚本与 CI。",
            ApprovalPolicy.Never),
    ];

    /// <summary>按默认表构造。</summary>
    /// <param name="current">初始策略。</param>
    public static PermissionPresets CreateDefault(ApprovalPolicy current) =>
        new(DefaultTable, current);

    /// <summary>当前策略。</summary>
    public ApprovalPolicy Current => _current;

    /// <summary>可选预设，按声明顺序。</summary>
    public IReadOnlyList<PermissionPreset> Available => _presets;

    /// <summary>
    /// 当前值对应的预设键；不匹配任何预设时返回 <see cref="CustomKey"/>。
    /// </summary>
    /// <remarks>
    /// <b>多个预设写同一个策略时，取声明在前的那个</b>：这保留了"用户选了哪个"
    /// 在多对一情况下的一部分信息。dsh 为此额外记一条事件；
    /// 我们不记（见类注释的取舍），所以按声明顺序是不完全的近似——
    /// 这一点写进 D-29。
    /// </remarks>
    public string CurrentKey =>
        _presets.FirstOrDefault(p => p.Policy == _current)?.Key ?? CustomKey;

    /// <summary>
    /// 切到一个预设。
    /// </summary>
    /// <remarks>
    /// <b>未知键抛出而不是静默无操作</b>：静默无操作会让用户以为切换成功了，
    /// 而策略没有变——那在安全方向上是更坏的结果（他以为收紧了）。
    /// </remarks>
    /// <param name="key">预设键。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>切换后的策略。</returns>
    /// <exception cref="ArgumentException">键不存在。</exception>
    public async Task<ApprovalPolicy> SwitchAsync(
        string key, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var preset = _presets.FirstOrDefault(
                p => string.Equals(p.Key, key, StringComparison.Ordinal))
                ?? throw new ArgumentException(
                    $"未知的权限预设 '{key}'。可选：{string.Join(", ", _presets.Select(p => p.Key))}。",
                    nameof(key));

            _current = preset.Policy;
            return _current;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>渲染供选择器使用的选项列表。</summary>
    /// <remarks>
    /// <b>包含派生值 <c>custom</c> 的展示，但不含它的可选项</b>：
    /// 当前值是 custom 时，界面要能显示它，但不能让用户切到它。
    /// </remarks>
    public IReadOnlyList<PresetOption> Options()
    {
        var options = _presets
            .Select(p => new PresetOption(p.Key, p.DisplayName, p.Description, Selectable: true))
            .ToList();

        if (CurrentKey == CustomKey)
        {
            options.Add(new PresetOption(
                CustomKey, "自定义",
                "当前审批策略不匹配任何预设（由宿主直接设置）。",
                Selectable: false));
        }

        return options;
    }

    /// <summary>校验预设表。</summary>
    private static void Validate(IReadOnlyList<PermissionPreset> presets)
    {
        if (presets.Count == 0)
            throw new ArgumentException("预设表不能为空。", nameof(presets));

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var preset in presets)
        {
            if (string.IsNullOrWhiteSpace(preset.Key))
                throw new ArgumentException("预设键不能为空。", nameof(presets));

            if (string.Equals(preset.Key, CustomKey, StringComparison.Ordinal))
                throw new ArgumentException(
                    $"'{CustomKey}' 是保留键，不能当预设名——它表示派生值。", nameof(presets));

            if (!seen.Add(preset.Key))
                throw new ArgumentException($"预设键重复：'{preset.Key}'。", nameof(presets));
        }
    }
}

/// <summary>选择器里的一项。</summary>
/// <param name="Value">选项值。</param>
/// <param name="Name">显示名。</param>
/// <param name="Description">一句话说明。</param>
/// <param name="Selectable">
/// 是否可选。<c>custom</c> 为 false——它只能被显示，不能被选中。
/// </param>
public sealed record PresetOption(
    string Value,
    string Name,
    string? Description,
    bool Selectable);

/// <summary>
/// 把审批策略套在宿主自带的审批器外面。
/// </summary>
/// <remarks>
/// <b>为什么是装饰器而不是换一个审批器</b>：各入口（控制台 / ACP / 自动批准）
/// 的"怎么问人"完全不同，而"要不要问"应当是同一套判据。
/// 把判据抽到这里，三个入口不必各写一遍——那种写法已经让
/// <see cref="PermissionPolicyApprover"/> 出现过一次"某个入口漏接"的问题。
///
/// <para>
/// <b>它不改变批准结果</b>：只决定"跳过"还是"交给内层"。内层说拒绝就是拒绝——
/// 预设管的是交互次数，不是放行与否。
/// </para>
/// </remarks>
public sealed class PresetApprover(IToolApprover inner, PermissionPresets presets) : IToolApprover
{
    private readonly IToolApprover _inner = inner
        ?? throw new ArgumentNullException(nameof(inner));

    private readonly PermissionPresets _presets = presets
        ?? throw new ArgumentNullException(nameof(presets));

    /// <summary>被跳过的调用数，供诊断。</summary>
    public int Skipped { get; private set; }

    public ValueTask<ToolApprovalDecision> RequestApprovalAsync(
        ToolApprovalRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        return _presets.Current switch
        {
            // 从不问：直接放行。**仍然记数**——诊断需要知道"这条命令被谁放过了"。
            //
            // **但未知权限不在此列**：工具说"我不知道自己会碰什么"
            // （IsKnown=false，MCP 工具一律如此）时，本档**不自行放行**，
            // 而是把决定交回入口的审批器：控制台会问、AutoApprove 会拒。
            // "不问"是这一档的意图，"代替入口做决定"不是。
            // 第一版把 Never 无条件放行，结果比 AutoApproveApprover 还宽松：
            // 那个专门的自动批准器都仍然拒绝未知权限，而这个预设把 MCP 工具
            // 一并放过去了。用户选"无人值守"是想少按几次确认，不是想取消这条底线。
            ApprovalPolicy.Never when request.Permission.IsKnown =>
                Skip("无人值守预设：不询问审批。"),

            ApprovalPolicy.Never => _inner.RequestApprovalAsync(request, cancellationToken),

            // 只问有风险的：与 PermissionPolicyApprover 同一判据。
            ApprovalPolicy.AskOnRisk when IsInert(request.Permission) =>
                Skip("该工具声明为已知无访问，不产生审批项。"),

            _ => _inner.RequestApprovalAsync(request, cancellationToken),
        };

        ValueTask<ToolApprovalDecision> Skip(string reason)
        {
            Skipped++;
            return ValueTask.FromResult(ToolApprovalDecision.Allow(reason));
        }
    }

    /// <summary>纯内存工具的判据，与 <see cref="PermissionPolicyApprover"/> 一致。</summary>
    /// <remarks>
    /// <b>两处判据必须一致</b>：不一致时"交互式预设"与
    /// <see cref="PermissionPolicyApprover"/> 会对同一个工具给出不同结论，
    /// 而症状是"换了预设之后问的次数变了，但说不清为什么"。
    /// </remarks>
    private static bool IsInert(ToolPermission permission) =>
        permission.IsKnown
        && permission.Access == ToolAccess.None
        && permission.Paths.Count == 0
        && permission.Commands.Count == 0
        && permission.Domains.Count == 0;
}
