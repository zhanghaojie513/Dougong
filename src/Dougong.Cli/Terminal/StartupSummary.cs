namespace Dougong.Cli.Terminal;

/// <summary>
/// 启动面板要显示的一组事实。**全部来自运行时实际装配结果**，不含宣传文案。
/// </summary>
/// <remarks>
/// <b>为什么单独建一个类型而不是把参数一个个传进 <see cref="Banner"/></b>：
/// 面板要显示的东西横跨五个来源（工具清单、MCP 规格与连接结果、技能目录、
/// 会话路径、模型配置）。逐项传参会让 <c>Banner.Print</c> 的签名长到
/// 没人愿意读，而新增一项就要改一处调用点。
///
/// <para>
/// <b>这里只装"已经确定的事实"</b>：工具来自 dispatcher 的冻结描述符、
/// MCP 来自配置与连接结果、技能来自 <c>SkillCatalog</c>。
/// 任何一项拿不到就如实记"不可用"——<b>不做推测性填充</b>，
/// 因为面板假报一个数字比不显示更糟：用户会按它去排查不存在的问题。
/// </para>
/// </remarks>
/// <param name="Version">版本号。</param>
/// <param name="Model">模型名。</param>
/// <param name="Provider">供应商名。</param>
/// <param name="Workspace">工作区路径。</param>
/// <param name="SessionId">会话标识。</param>
/// <param name="Tools">已注册工具的名字，已排序。</param>
/// <param name="McpServers">MCP server 与其连接状态。</param>
/// <param name="SkillCount">已发现技能总数。</param>
/// <param name="SkillSources">按来源分组的技能名（来源标签 → 名字列表）。</param>
public sealed record StartupSummary(
    string Version,
    string Model,
    string Provider,
    string Workspace,
    string SessionId,
    IReadOnlyList<string> Tools,
    IReadOnlyList<McpServerStatus> McpServers,
    int SkillCount,
    IReadOnlyList<SkillSourceGroup> SkillSources)
{
    /// <summary>
    /// 是否拿得到工具清单。false 表示 <see cref="Tools"/> 为空是
    /// **"读不到"而不是"没有"**。
    /// </summary>
    /// <remarks>
    /// <b>两者必须分开报</b>：dispatcher 拿不到（接线异常）时若显示
    /// "（无已注册工具）"，用户会去查工具注册配置——而问题在装配。
    /// 面板的第一职责是**不说错话**。
    ///
    /// <para>
    /// 用 <c>init</c> 属性而不是构造参数：加参数要改所有构造点，
    /// 而默认值 <c>true</c> 对"正常路径"是对的。
    /// </para>
    /// </remarks>
    public bool ToolsKnown { get; init; } = true;
}

/// <summary>
/// 一组同来源的技能。
/// </summary>
/// <param name="Source">来源标签（如 <c>project-agents</c>）。</param>
/// <param name="Names">该来源下的技能名，已排序。</param>
/// <remarks>
/// <b>分组粒度是"我们的三个来源"，不是 Hermes 的 category</b>：
/// 我们的 <c>SkillDiscovery</c> 只按目录来源分（project-dsh / project-agents /
/// user-dsh），没有 category 这一层。**不为了像 Hermes 而造一层假分类**——
/// 那会让用户以为技能文件里可以写 category，而实际不会生效。
/// </remarks>
public sealed record SkillSourceGroup(string Source, IReadOnlyList<string> Names);

/// <summary>
/// 一个 MCP server 的启动状态。
/// </summary>
/// <param name="Name">配置里的名字。</param>
/// <param name="Transport">传输方式：<c>stdio</c> 或 <c>http</c>。</param>
/// <param name="Connected">是否连通。</param>
/// <param name="ToolCount">该 server 贡献的工具数；未连通时为 0。</param>
/// <param name="Error">未连通时的简短原因；连通时为空。</param>
/// <remarks>
/// <b>如实区分"配置了"与"连上了"</b>：Hermes 截图里写的是
/// <c>(stdio) — configured</c>，那只是"配置里有一条"。我们同时报连通状态，
/// 因为"配了但连不上"是用户最需要立刻知道的事——而只写 configured
/// 会让人以为它在工作。
/// </remarks>
public sealed record McpServerStatus(
    string Name,
    string Transport,
    bool Connected,
    int ToolCount,
    string? Error)
{
    /// <summary>供面板显示的一行状态文字。</summary>
    public string Describe() => Connected
        ? ToolCount > 0 ? $"connected · {ToolCount} tools" : "connected · no tools"
        : Error is { Length: > 0 } ? $"failed · {Error}" : "configured";
}
