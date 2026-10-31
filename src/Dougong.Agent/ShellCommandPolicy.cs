using System.Text.RegularExpressions;

namespace Dougong.Agent;

/// <summary>
/// 一次命令的策略判定结果。
/// </summary>
/// <param name="Allowed">是否放行。</param>
/// <param name="Reason">拒绝原因；放行时为 null。</param>
public sealed record CommandPolicyVerdict(bool Allowed, string? Reason)
{
    /// <summary>放行。</summary>
    public static CommandPolicyVerdict Allow { get; } = new(true, null);

    /// <summary>拒绝并说明原因。</summary>
    public static CommandPolicyVerdict Deny(string reason) => new(false, reason);
}

/// <summary>
/// shell 命令的策略闸门。
/// </summary>
/// <remarks>
/// <b>这不是沙箱，必须说清楚</b>：它做的是**模式匹配**——认出明显越界的写法并拒绝。
/// 它挡不住有意绕过的命令，例如用 <c>find / -name x</c>、
/// <c>for f in /*; do cat $f; done</c>、或任意脚本。
///
/// <para>
/// <b>为什么还是值得做</b>：绝大多数越界**不是攻击，是误操作**。
/// 模型想"看看项目结构"时写 <c>ls /</c>，想"清理一下"时写
/// <c>rm -rf /tmp/*</c>——这些都能被模式识别拦住，而它们才是真实发生的损失。
/// 真正的恶意绕过需要 OS 级隔离，见文档里的沙箱条目。
/// </para>
///
/// <para>
/// <b>诚实边界写在拒绝消息里</b>：拒绝时说明"这是策略而非沙箱"，
/// 让宿主与用户都知道防护到哪为止。宣称"已沙箱"而只是模式匹配，
/// 比不做更危险——它会让人放松警惕。
/// </para>
///
/// <para>
/// <b>白名单优先于黑名单</b>：黑名单永远漏（新的破坏命令、别名、
/// 编码变体）。这里两条都用了，但白名单是主判据：
/// 只有显式列出的只读命令可以无提示执行。
/// </para>
/// </remarks>
public sealed class ShellCommandPolicy
{
    /// <summary>不需要审批即可执行的只读命令前缀。</summary>
    /// <remarks>
    /// <b>为什么这些命令值得单独列</b>：它们是 agent 最容易反复调用的
    /// （<c>ls</c>、<c>cat</c>、<c>git status</c>）。每次都弹审批会让用户
    /// 条件反射地点"允许"，而那正是审批失效的原因——疲劳比拒绝更危险。
    /// <para>
    /// 只收**不修改文件系统、不发网络请求**的命令。任何带写能力的
    /// （<c>git commit</c>、<c>npm install</c>）都不在此列。
    /// </para>
    /// </remarks>
    private static readonly string[] SafeReadOnlyPrefixes =
    [
        "ls", "dir", "cat", "type", "head", "tail", "wc", "file", "stat",
        "pwd", "echo", "which", "where", "whoami", "date",
        "git status", "git log", "git diff", "git show", "git branch",
        "git remote", "git describe", "git rev-parse",
        "dotnet --info", "dotnet --version", "dotnet build --help",
        "node --version", "npm --version", "python --version",
    ];

    /// <summary>
    /// 明确危险的模式：命中即拒绝。
    /// </summary>
    /// <remarks>
    /// 每条都注明它防的是什么具体事故，不是"看起来像危险命令"。
    /// 写不出具体事故的规则不该加——它会误杀正常用法而收益不明。
    /// </remarks>
    private static readonly (Regex Pattern, string Why)[] Dangerous =
    [
        // 磁盘级破坏：一次误操作可以毁掉整台机器。
        (new Regex(@"\brm\s+(-[a-zA-Z]*[rf][a-zA-Z]*\s+)+(/|~|\$HOME|\*)",
            RegexOptions.CultureInvariant), "递归删除根目录、家目录或通配全部内容"),

        // Windows 等价物。
        (new Regex(@"\b(rd|rmdir)\s+/s\s+/q\s+[A-Za-z]:\\?\s*$",
            RegexOptions.CultureInvariant | RegexOptions.IgnoreCase), "递归删除整个盘"),

        // 权限/所有权变更：模型几乎从不该做这个，误做后果严重。
        (new Regex(@"\bchmod\s+(-R\s+)?777\b", RegexOptions.CultureInvariant),
            "把权限放开到 777"),

        // 覆写系统文件与设备。
        (new Regex(@"\b(dd|mkfs|format)\b", RegexOptions.CultureInvariant), "磁盘直写或格式化"),
        (new Regex(@">\s*/dev/(sd|nvme|hd)", RegexOptions.CultureInvariant), "直接覆写块设备"),

        // 管道执行远端脚本：无法审查内容，等于把控制权交出去。
        (new Regex(@"\b(curl|wget)\b[^|;]*\|\s*(ba|z|k)?sh\b",
            RegexOptions.CultureInvariant), "把网络内容直接管进 shell 执行"),

        // 反向 shell 的常见形态。
        (new Regex(@"\b(nc|ncat|netcat)\b[^|;]*-[a-z]*e\b", RegexOptions.CultureInvariant),
            "用 netcat 开反向 shell"),

        // 关机/重启：中断用户工作且可能丢未保存数据。
        (new Regex(@"\b(shutdown|reboot|halt|poweroff)\b",
            RegexOptions.CultureInvariant | RegexOptions.IgnoreCase), "关机或重启"),

        // fork 炸弹的经典写法（正则形态）。
        (new Regex(@":\s*\(\s*\)\s*\{.*\}\s*;\s*:", RegexOptions.CultureInvariant),
            "fork 炸弹"),
    ];

    /// <summary>
    /// 读取工作区之外路径的模式。
    /// </summary>
    /// <remarks>
    /// <b>这一组是"信息泄露"而不是"破坏"</b>：<c>cat ~/.ssh/id_rsa</c>
    /// 不改任何东西，但把密钥读出来发给模型。它是本项目最需要防的一类，
    /// 因为密钥值的约束是"绝不进模型上下文"。
    /// </remarks>
    private static readonly (Regex Pattern, string Why)[] SensitiveReads =
    [
        (new Regex(@"(/etc/(passwd|shadow|sudoers)|/root/|/proc/\d+/environ)",
            RegexOptions.CultureInvariant), "读取系统账户或进程环境"),
        (new Regex(@"\.ssh/(id_|authorized_keys|known_hosts)",
            RegexOptions.CultureInvariant | RegexOptions.IgnoreCase), "读取 SSH 密钥"),
        (new Regex(@"\.(aws|azure|kube|docker)/(credentials|config)",
            RegexOptions.CultureInvariant | RegexOptions.IgnoreCase), "读取云凭据"),
        (new Regex(@"\.(npmrc|pypirc|netrc|git-credentials)\b",
            RegexOptions.CultureInvariant | RegexOptions.IgnoreCase), "读取包管理器或 git 凭据"),
        (new Regex(@"\bDOUGONG_[A-Z_]*(KEY|TOKEN|SECRET)\b", RegexOptions.CultureInvariant),
            "读取本项目的密钥类环境变量"),

        // **`.env` 是最常见的一处漏网**：它通常装着该项目的全部 API key，
        // 而且几乎总在工作区根目录——正是模型最可能随手 `cat` 的文件。
        // 实测确认过：不列这一条时 `cat .env` 会被放行。
        //
        // 覆盖多个变体是因为命名不统一：`.env`、`.env.local`、
        // `.env.production` 都是同一种东西。
        (new Regex(@"\.env(\.[\w-]+)?\b", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase),
            "读取 .env 环境变量文件（通常含全部 API key）"),

        // `.git/config` 的 remote url 里可能带 token（`https://user:token@host/`），
        // 而 credential helper 配置也在里面。
        (new Regex(@"\.git[/\\]config\b", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase),
            "读取 .git/config（remote url 可能含 token）"),
    ];

    /// <summary>
    /// 判定一条命令。
    /// </summary>
    /// <param name="command">要执行的命令文本。</param>
    /// <param name="workspace">
    /// 工作区绝对路径；<b>null 表示调用方不知道根在哪</b>。
    /// 那时任何 <c>cd</c> 都会被拒绝——无法判定才是不放行的理由，
    /// 而不是拿一个推测的根去放行。
    /// </param>
    /// <param name="cwd">当前目录（上一轮 <c>cd</c> 之后的位置）；null 表示工作区根。</param>
    public CommandPolicyVerdict Evaluate(string command, string? workspace, string? cwd = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command);

        // 危险模式优先于一切：即使是"看起来只读"的命令，命中危险模式也拒绝。
        // 顺序很重要——若先判白名单，`cat /dev/sda` 会因为前缀是 cat 而放行。
        foreach (var (pattern, why) in Dangerous)
        {
            if (pattern.IsMatch(command))
                return CommandPolicyVerdict.Deny(
                    $"命令被拒绝：{why}。\n"
                    + "这是**策略过滤而非沙箱**——它识别具体写法，不能阻止等价的其他写法。");
        }

        foreach (var (pattern, why) in SensitiveReads)
        {
            if (pattern.IsMatch(command))
                return CommandPolicyVerdict.Deny(
                    $"命令被拒绝：{why}。密钥不得进入模型上下文。\n"
                    + "这是策略过滤规则的直接后果（见 goal.md 的凭据约束）。");
        }

        // 工作区逃逸检查：`cd /` 这类。走路径守卫，与文件工具同一判据——
        // 两处判据必须一致，否则会出现"读文件被拒、shell 里能读"的漏洞。
        var escape = FindDirectoryEscape(command, workspace, cwd);
        if (escape is not null) return CommandPolicyVerdict.Deny(escape);

        return CommandPolicyVerdict.Allow;
    }

    /// <summary>
    /// 判断命令里的 <c>cd</c> 是否会把工作目录带出工作区。
    /// </summary>
    /// <remarks>
    /// <b>只查 <c>cd</c>，不查每一个路径参数</b>：检查所有参数会误杀
    /// <c>git log --stat</c> 这类正常用法，而且 CP 编码差别巨大（正则难以可靠识别）。
    /// <c>cd</c> 是**持续生效**的那一类——它改变后续所有命令的基准目录，
    /// 而其他命令的越界是单次的，单次的至少还能被上面那些模式拦住。
    /// <para>
    /// <b>能绕过的写法不少</b>：<c>pushd</c>、<c>cd $(echo /)</c>、
    /// 脚本内 cd。这条只挡最常见的直写形式。文档里如实记录。
    /// </para>
    /// </remarks>
    private static string? FindDirectoryEscape(string command, string? workspace, string? cwd)
    {
        // 依次累积：一行里可能有多个 cd。
        var current = string.IsNullOrWhiteSpace(cwd) ? workspace : cwd;

        foreach (var segment in SplitOnSequentialOperators(command))
        {
            var match = Regex.Match(
                segment,
                @"(?:^|[\s;&|(])(cd|chdir|pushd)\s+(""[^""]*""|'[^']*'|\S+)",
                RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

            if (!match.Success) continue;

            var target = match.Groups[2].Value.Trim('"', '\'');

            // 目标是变量、命令替换、或波浪号展开：无法静态判断，按越界处理。
            // 宁可拒绝一次正常调用，也不放过可能越界的一次——
            // 拒绝可以让模型改用明确路径，误放行却是静默的。
            //
            // **`~` 必须在这里**：shell 会把它展开成家目录，而 Path.GetFullPath
            // 不会——后者把它当普通字符，于是 `cd ~` 会被算成"工作区内的 ~ 目录"
            // 而放行。实测在 Linux 上是真实逃逸。
            if (target.Contains('$', StringComparison.Ordinal)
                || target.Contains('`', StringComparison.Ordinal)
                || target.Contains('(', StringComparison.Ordinal)
                || target.StartsWith('~'))
            {
                return $"命令被拒绝：cd 的目标 '{target}' 含变量、命令替换或家目录展开，无法静态判定是否越界。"
                    + "\n请用工作区内的字面路径。这是策略过滤而非沙箱。";
            }

            string absolute;
            try
            {
                // 工作区未知时无法解析相对路径：直接按"无法判定"拒绝。
                // 不拿当前目录冒充——那会让守卫看起来在工作，实际拦错地方。
                if (current is null)
                {
                    return "命令被拒绝：执行方未声明工作区，无法判定 cd 后的目录是否越界。"
                        + "\n这是策略过滤而非沙箱。请让宿主在装配时传入工作区。";
                }

                absolute = Path.IsPathRooted(target)
                    ? Path.GetFullPath(target)
                    : Path.GetFullPath(Path.Combine(current, target));
            }
            catch (Exception)
            {
                return $"命令被拒绝：cd 的目标 '{target}' 无法解析为路径。";
            }

            if (!IsUnder(absolute, workspace!))
            {
                return $"命令被拒绝：cd 到 '{target}' 会离开工作区 {workspace}。"
                    + "\n这是策略过滤而非沙箱——它挡常见误操作，不挡有意绕过。";
            }

            current = absolute;
        }

        return null;
    }

    /// <summary>按顺序分隔符切段，保留 <c>cd a &amp;&amp; cd b</c> 的先后语义。</summary>
    private static IEnumerable<string> SplitOnSequentialOperators(string command) =>
        Regex.Split(command, @"(?:&&|\|\||;|\n)")
            .Where(part => !string.IsNullOrWhiteSpace(part));

    private static bool IsUnder(string absolute, string workspace)
    {
        var normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(absolute));
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(workspace));

        return string.Equals(normalized, root, StringComparison.OrdinalIgnoreCase)
            || normalized.StartsWith(
                root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>该命令是否属于可以免审批执行的只读类型。</summary>
    /// <remarks>
    /// <b>判据是"没有任何能改变行为或泄露内容的构造"</b>，不是"前缀匹配成功"。
    /// 带重定向、管道、命令替换、变量展开的命令全部不算只读——
    /// 否则白名单会变成任意写入或任意读取的通道。
    /// </remarks>
    public static bool IsSafeReadOnly(string command)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command);
        var trimmed = command.TrimStart();

        // 任何能改变数据流或注入内容的构造都排除。
        //
        // **$ 和 ( 都要排除**：`cat $(cat /etc/passwd)` 会先执行内层命令，
        // 内层读的是凭据。只查反引号会漏掉 $() 这个更常见的写法。
        string[] unsafeMarkers = [">", "|", "`", "$", "(", ")", "<", "&", ";", "\n"];
        foreach (var marker in unsafeMarkers)
        {
            if (trimmed.Contains(marker, StringComparison.Ordinal))
                return false;
        }

        return SafeReadOnlyPrefixes.Any(prefix =>
            trimmed.Equals(prefix, StringComparison.OrdinalIgnoreCase)
            || (trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                && trimmed.Length > prefix.Length
                && char.IsWhiteSpace(trimmed[prefix.Length])));
    }
}
