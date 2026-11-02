namespace Dougong.Ai;

/// <summary>
/// 用户数据根目录的唯一解析入口。
/// </summary>
/// <remarks>
/// <b>为什么要这个类</b>：<c>.dougong</c> 此前硬编码在 7 个文件、19 处，
/// 配置 / 凭据 / 会话 / 记忆各写各的路径。后果已经实测到两次：
/// <list type="bullet">
/// <item>新增 <c>DOUGONG_PROVIDER_CONFIG</c> 后，检查套件忘了隔离，
/// 读到了开发者机器上真实的 <c>~/.dougong/config.toml</c>，
/// 于是"缺配置应报失败"的断言在本机配好 provider 之后假失败</item>
/// <item>新增 <c>DOUGONG_CREDENTIALS</c> 后同样忘了隔离，
/// 检查写进了真实凭据文件</item>
/// </list>
/// 两次都是同一个模式：**多一个路径变量，就多一处会忘的地方**。
///
/// <para>
/// <b>照 dsh 的做法</b>（<c>@deepseek-ai/dsh-home-paths</c>）：
/// <list type="number">
/// <item><b>单一入口</b>：<c>dshHomePath(...)</c> 是唯一拼路径的方式，
/// 12 个包都用它，无处硬编码</item>
/// <item><b>空值当未设置</b>：原文 "An empty or whitespace-only <c>$DSH_HOME</c>
/// is treated as unset, so a blank override never resolves the home to the
/// current working directory"</item>
/// <item><b>显示时符号化</b>：对外展示 <c>~/.dougong</c> 或 <c>$DOUGONG_HOME</c>，
/// 绝不输出绝对机器路径——那会泄漏用户名</item>
/// </list>
/// </para>
///
/// <para>
/// <b>与 dsh 的差别</b>：它还有第二个根 <c>~/.agents</c>（跨工具共享的
/// agent 配置）。我们暂时没有那个需求，只做单根；将来要加时，
/// 这个类是多开一个 <c>SharedPath(...)</c> 还是加第二个解析器，届时有依据再说。
/// </para>
/// </remarks>
public static class DougongHome
{
    /// <summary>默认目录名。</summary>
    public const string DirectoryName = ".dougong";

    /// <summary>覆盖根目录的环境变量名。</summary>
    public const string EnvironmentVariable = "DOUGONG_HOME";

    /// <summary>
    /// 解析出的根目录绝对路径。
    /// </summary>
    /// <remarks>
    /// <b>每次调用都重新读环境变量</b>，不做静态缓存：检查套件需要在
    /// 同一进程里把 home 指到不同临时目录，缓存会让那些用例互相污染。
    /// 代价是一次环境变量读取，可以忽略。
    /// <para>
    /// <b>空串与纯空白当未设置</b>：否则 <c>DOUGONG_HOME=""</c> 会解析成
    /// 当前工作目录——那是个极难查的故障（程序突然把数据写进项目目录）。
    /// </para>
    /// </remarks>
    public static string Root
    {
        get
        {
            var overridden = Environment.GetEnvironmentVariable(EnvironmentVariable);
            if (!string.IsNullOrWhiteSpace(overridden))
                return Path.GetFullPath(overridden);

            var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return Path.Combine(profile, DirectoryName);
        }
    }

    /// <summary>把路径段拼到根目录下。</summary>
    /// <remarks>
    /// <b>这是唯一该被调用的方法</b>。直接用 <c>Path.Combine(Root, "x")</c> 也行，
    /// 但那样每处都要记得先取 <see cref="Root"/>；集中在一处更容易看出
    /// "哪些东西存在这个根下面"。
    /// </remarks>
    public static string Resolve(params string[] segments) =>
        segments.Length == 0
            ? Root
            : Path.Combine([Root, .. segments]);

    /// <summary>配置文件：<c>config.toml</c>。</summary>
    public static string ConfigPath => Resolve("config.toml");

    /// <summary>凭据文件：<c>credentials</c>。</summary>
    public static string CredentialPath => Resolve("credentials");

    /// <summary>会话目录：<c>sessions</c>。</summary>
    public static string SessionsDirectory => Resolve("sessions");

    /// <summary>
    /// 用户级技能目录（<c>~/.dougong/skills</c>）。
    /// </summary>
    /// <remarks>
    /// <b>与 <see cref="SessionsDirectory"/> 同级</b>：技能是用户自己的配置，
    /// 不属于某个会话或某个工作区。项目级技能另有位置（工作区下的
    /// <c>.dougong/skills</c>），两者的优先级见 <c>SkillDiscovery</c>。
    /// </remarks>
    public static string SkillsDirectory => Resolve("skills");

    /// <summary>记忆目录：<c>memory</c>。</summary>
    public static string MemoryDirectory => Resolve("memory");

    /// <summary>
    /// 给用户看的符号化路径。
    /// </summary>
    /// <remarks>
    /// <b>绝不返回绝对机器路径</b>：诊断输出、错误信息、文档示例里出现
    /// <c>C:\Users\张三\.dougong</c> 会泄漏用户名，而用户把日志贴到 issue 里
    /// 时往往想不到要打码。默认位置显示 <c>~/.dougong</c>，
    /// 被覆盖时显示 <c>$DOUGONG_HOME</c>。
    /// </remarks>
    public static string Describe(string? absolutePath = null)
    {
        var target = Path.GetFullPath(absolutePath ?? Root);
        var root = Path.GetFullPath(Root);

        // 相对根的剩余部分。**必须保留它**：只说 `$DOUGONG_HOME` 会让读者
        // 以为配置文件就是那个目录本身，而实际是 `<home>/config.toml`。
        var relative = target.Length > root.Length
            && target.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                ? target[(root.Length + 1)..].Replace('\\', '/')
                : null;

        var isOverridden = !string.IsNullOrWhiteSpace(
            Environment.GetEnvironmentVariable(EnvironmentVariable));

        if (isOverridden)
        {
            return relative is { Length: > 0 }
                ? $"${EnvironmentVariable}/{relative}"
                : $"${EnvironmentVariable}";
        }

        // 默认位置：把绝对路径换回 ~ 开头。
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (target.StartsWith(profile, StringComparison.OrdinalIgnoreCase))
            return "~" + target[profile.Length..].Replace('\\', '/');

        return target;
    }
}
