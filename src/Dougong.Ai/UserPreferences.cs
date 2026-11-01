namespace Dougong.Ai;

/// <summary>
/// 用户偏好设置：与密钥、路径这类"运行环境"分开的持久选择。
/// </summary>
/// <remarks>
/// <b>为什么要与 provider 配置分开</b>：provider 段描述"连哪里、用哪个模型"，
/// 是**部署事实**；这里的字段描述"用户希望 agent 怎么工作"，是**偏好**。
/// 混在一起时，改 provider 会牵动偏好，反之亦然。
///
/// <para>
/// <b>参考项目全部把偏好放配置文件</b>：
/// <list type="bullet">
/// <item>dsh：profile 的 <c>cordis.patch.yml</c></item>
/// <item>hermes：<c>~/.hermes/config.yaml</c> 的 dotted key，如 <c>display.show_reasoning</c></item>
/// <item>pi：<c>~/.pi/agent/settings.json</c> 的 <c>defaultTools</c>、<c>hideThinkingBlock</c></item>
/// </list>
/// 没有一个要求用户 <c>export</c> 来保持一个持久偏好。
/// </para>
///
/// <para>
/// <b>但安全相关的开关要冻结</b>：hermes 的 <c>tools/approval.py</c> 在模块导入时
/// 就读定 <c>HERMES_YOLO_MODE</c>，注释写的是
/// "reading os.environ per call would let any skill running in the process set this
/// and bypass every approval check (prompt-injection escalation path)"。
/// 见 <see cref="SecuritySwitches"/>。
/// </para>
/// </remarks>
public sealed record UserPreferences
{
    /// <summary>
    /// 是否允许 <c>shell</c> 工具执行命令。
    /// </summary>
    /// <remarks>
    /// <b>默认 false</b>：开启意味着模型能执行任意命令，而我们的命令策略只是
    /// 模式匹配、不是沙箱（见 <c>dsh-capability-gap.md</c> 阶段 3）。
    /// 让用户显式选择，而不是默认给。
    /// </remarks>
    public bool Shell { get; init; }

    /// <summary>是否注入 L1 记忆。</summary>
    /// <remarks>
    /// 默认 false：启用意味着历史偏好会进入后续每一次 prompt，
    /// 并可能发给远端模型。这条数据流必须显式开启。
    /// </remarks>
    public bool Memory { get; init; }

    /// <summary>
    /// 记忆文件放哪；null 表示默认位置（会话目录的同级 <c>memory</c>）。
    /// </summary>
    /// <remarks>
    /// <b>为什么路径也进配置而不留在环境变量</b>：它曾经是
    /// <c>DOUGONG_MEMORY</c> 的第三种取值（除了 <c>0/1</c> 之外的任意字符串都当路径）。
    /// 那让这个开关有了**两条读取路径**——布尔看环境变量、路径看同一个变量的另一种形态，
    /// 而 <c>shell</c> 与 <c>show_reasoning</c> 只有一条。同一类配置两种读法，
    /// 迟早会得出不同结论；本项目已经在 provider 连通性与压缩配置上各踩过一次。
    /// <para>
    /// 放进配置后，<c>DOUGONG_MEMORY</c> 只保留布尔语义（临时覆盖），
    /// 路径这类"持久选择"只有一处权威来源。
    /// </para>
    /// <para>
    /// <b>为什么需要这个字段（对照 hermes）</b>：hermes 的 memory 段有 10 个字段，
    /// 一个路径都没有——因为它有 <c>HERMES_HOME</c> 统管全部状态，
    /// 用户想换位置就整体搬 home。**我们没有那个抽象**：
    /// <c>.dougong</c> 硬编码在 7 个文件、19 处，配置/凭据/会话/记忆各走各的路径变量。
    /// 在补上统一的 home 之前，<c>memory_dir</c> 是唯一能"把记忆放到别处"的入口
    /// （外置盘、同步目录），所以它不是可选项。
    /// </para>
    /// <para>
    /// 将来若加了统一的 home 变量，这个字段可以退役——届时"搬 home"覆盖了它的用途。
    /// </para>
    /// </remarks>
    public string? MemoryDirectory { get; init; }

    /// <summary>是否显示模型的推理过程。</summary>
    /// <remarks>
    /// 默认 false：推理常带未定稿的自我纠错，当结论显示会误导。
    /// 需要观察模型思路时打开。
    /// </remarks>
    public bool ShowReasoning { get; init; }
}

/// <summary>
/// 安全开关的冻结值。
/// </summary>
/// <remarks>
/// <b>为什么单独一类并"冻结"</b>：这些开关决定 agent 能做什么，而配置文件
/// 本身对模型可写——一旦 <c>shell</c> 已开，模型就能用
/// <c>echo 'shell = true' &gt;&gt; ~/.dougong/config.toml</c> 把自己永久解锁。
/// 那是**自举漏洞**：一次授权变成永久授权，而用户不知道。
///
/// <para>
/// <b>两家的处理方式</b>：
/// <list type="bullet">
/// <item>hermes：<c>tools/approval.py</c> 在导入时冻结；另有 denylist 让这类变量
/// 无法经 <c>hermes config set</c> 写进 <c>.env</c></item>
/// <item>dsh：最激进的档位（auto）<b>不进持久 schema</b>，
/// 只存在于会话级，"disappears when the effect is disposed"</item>
/// </list>
/// </para>
///
/// <para>
/// <b>我们的做法</b>：值在**进程启动时读一次**并不可变。配置文件仍然写
/// <c>shell = true</c>（用户可以持久化自己的选择），但**本次运行期间不会被重新读**。
/// 于是模型改了文件也影响不到当前进程，下次启动才会生效——而那是用户
/// 主动重启，不是模型的行为。
/// </para>
///
/// <para>
/// <b>为什么不做成"完全不可持久化"</b>：那样每次都要回答，而对本地单人使用
/// 是纯负担。冻结已经堵住了自举路径：模型需要"用户重启"才能生效。
/// </para>
/// </remarks>
public sealed class SecuritySwitches
{
    /// <summary>进程级冻结快照。第一次访问时读定，此后不变。</summary>
    /// <remarks>
    /// <b>可变是为了测试</b>：本仓库的检查套件在同一进程里用
    /// <c>Environment.SetEnvironmentVariable</c> 临时改值，而 <see cref="Lazy{T}"/>
    /// 一旦求值就不再看环境。没有重置口，那些用例只能全部改成子进程。
    /// <para>
    /// 重置口是 <c>internal</c> 的，宿主改不到它——**自举防护不能被外部重置**。
    /// </para>
    /// </remarks>
    private static Lazy<SecuritySwitches> _frozen = new(() => Load());

    /// <summary>本进程的冻结值。</summary>
    public static SecuritySwitches Current => _frozen.Value;

    /// <summary>丢弃冻结值，下次访问重新读。</summary>
    /// <remarks>
    /// <b>只给检查套件用</b>。产品代码从不调用它：运行期重新读取正是
    /// 自举漏洞的来源（模型改配置 → 下次读取生效）。
    /// </remarks>
    internal static void ResetForTesting() => _frozen = new(() => Load());

    private SecuritySwitches(
        bool shell, bool memory, bool showReasoning, string? memoryDirectory, string source)
    {
        Shell = shell;
        Memory = memory;
        ShowReasoning = showReasoning;
        MemoryDirectory = memoryDirectory;
        Source = source;
    }

    /// <summary>本进程是否允许 shell 工具。</summary>
    public bool Shell { get; }

    /// <summary>本进程是否启用记忆。</summary>
    public bool Memory { get; }

    /// <summary>本进程是否显示推理过程。</summary>
    public bool ShowReasoning { get; }

    /// <summary>
    /// 记忆目录；null 表示用默认位置。
    /// </summary>
    /// <remarks>
    /// <b>它不受环境变量影响</b>：路径是持久选择，没有"这次临时换个目录"的语义。
    /// 环境变量只覆盖布尔开关。
    /// </remarks>
    public string? MemoryDirectory { get; }

    /// <summary>这些值从哪来：<c>env</c> / <c>config</c> / <c>default</c>。供诊断显示。</summary>
    public string Source { get; }

    /// <summary>
    /// 按"环境变量 &gt; 配置文件 &gt; 默认"读一次。
    /// </summary>
    /// <remarks>
    /// <b>环境变量为什么优先</b>：它是**临时**覆盖（CI、一次性会话），
    /// 而配置文件是持久选择。让临时的赢，符合"我这次想关掉"的直觉。
    /// 这与密钥的顺序相反（密钥是存储优先）——因为密钥没有"临时"的语义，
    /// 而开关有。
    /// </remarks>
    private static SecuritySwitches Load()
    {
        var preferences = UserPreferencesFile.TryLoad(out _);
        var source = "config";

        bool Read(string envName, bool configured)
        {
            var raw = Environment.GetEnvironmentVariable(envName);
            if (string.IsNullOrWhiteSpace(raw)) return configured;

            source = "env";
            return raw is "1" or "true" or "TRUE" or "True";
        }

        var shell = Read("DOUGONG_SHELL", preferences.Shell);
        var memory = Read("DOUGONG_MEMORY", preferences.Memory);
        var reasoning = Read("DOUGONG_SHOW_REASONING", preferences.ShowReasoning);

        if (Environment.GetEnvironmentVariable("DOUGONG_SHELL") is null
            && Environment.GetEnvironmentVariable("DOUGONG_MEMORY") is null
            && Environment.GetEnvironmentVariable("DOUGONG_SHOW_REASONING") is null)
        {
            source = "config";
        }

        return new SecuritySwitches(shell, memory, reasoning, preferences.MemoryDirectory, source);
    }

    /// <summary>仅供测试：把一个固定快照当作"已冻结"的值。</summary>
    /// <remarks>
    /// <b>为什么需要它</b>：<see cref="Lazy{T}"/> 一旦求值就不再变化，
    /// 而检查套件里需要不同取值。没有这个口，每个用例都得开子进程。
    /// </remarks>
    internal static SecuritySwitches ForTesting(
        bool shell = false, bool memory = false, bool showReasoning = false,
        string? memoryDirectory = null) =>
        new(shell, memory, showReasoning, memoryDirectory, "test");
}
