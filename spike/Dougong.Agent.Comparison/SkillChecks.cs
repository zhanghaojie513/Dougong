using System.Text.Json;
using Dougong.Agent;

namespace Dougong.Agent.Comparison;

/// <summary>
/// 技能提供者的验收。
/// </summary>
/// <remarks>
/// <b>可观察面分三层，每一层都能单独坏掉</b>：
/// <list type="number">
/// <item><b>发现</b>：目录里有哪些技能。判据是名字、描述与来源优先级。</item>
/// <item><b>加载</b>：正文真的读回来了，且 frontmatter 被剥掉。</item>
/// <item><b>可见性</b>：<c>disable-model-invocation</c> 的技能不该出现在
/// 模型目录里，也不该能被工具加载。**只做前者等于没做**——模型可以凭记忆拼名字。</item>
/// </list>
/// </remarks>
public static class SkillChecks
{
    public static async Task<int> RunAsync()
    {
        var failures = 0;
        failures += await CheckAsync("skill-discovers-bundle-and-flat-forms", DiscoverFormsAsync);
        failures += await CheckAsync("skill-ignores-non-kebab-directories", IgnoresBadNamesAsync);
        failures += await CheckAsync("skill-project-wins-over-user-and-records-shadow", PriorityAsync);
        failures += await CheckAsync("skill-description-is-truncated-with-ellipsis", TruncationAsync);
        failures += await CheckAsync("skill-missing-description-still-listed", MissingDescriptionAsync);
        failures += await CheckAsync("skill-frontmatter-is-stripped-from-body", FrontmatterAsync);
        failures += await CheckAsync("skill-load-rereads-the-file", RereadsAsync);
        failures += await CheckAsync("skill-disable-model-invocation-hides-and-blocks", VisibilityAsync);
        failures += await CheckAsync("skill-tool-rejects-bad-and-unknown-names", ToolErrorsAsync);
        failures += await CheckAsync("skill-catalog-text-escapes-and-omits-paths", CatalogTextAsync);
        failures += await CheckAsync("skill-is-wired-into-cli-composition", WiringAsync);

        Console.WriteLine(failures == 0
            ? "SKILLS: ALL PASS"
            : $"SKILLS: {failures} FAILURE(S)");
        return failures;
    }

    /// <summary>目录包与平铺文件都能发现。</summary>
    private static async Task<string?> DiscoverFormsAsync()
    {
        await Task.CompletedTask;
        using var env = new Env();
        env.WriteSkill("bundle-skill", "SKILL.md", "---\ndescription: 目录包形态\n---\n正文");
        // **平铺形态是根目录下的 <name>.md，不在同名子目录里**。
        // 第一版写进了 skills/flat-skill/flat-skill.md——那个路径两种形态都不符合，
        // 检查在报一个不存在的缺陷。
        env.WriteFlatSkill("flat-skill", "---\ndescription: 平铺形态\n---\n正文");

        var names = env.Discover().Skills.Select(s => s.Name).OrderBy(n => n).ToArray();
        if (!names.Contains("bundle-skill")) return "没有发现目录包形态";
        if (!names.Contains("flat-skill")) return "没有发现平铺形态";
        return names.Length == 2 ? null : $"应发现 2 个，实际 {names.Length}：{string.Join(",", names)}";
    }

    /// <summary>非 kebab-case 的目录/文件被跳过。</summary>
    /// <remarks>
    /// <b>这是"目录里别的东西"与"技能"的分界线</b>：不筛的话，
    /// 一个顺手放进来的 <c>README.md</c> 会变成技能，而模型会尝试加载它。
    /// </remarks>
    private static async Task<string?> IgnoresBadNamesAsync()
    {
        await Task.CompletedTask;
        using var env = new Env();
        env.WriteSkill("good-skill", "SKILL.md", "---\ndescription: 合法\n---\n正文");
        env.WriteSkill("README", "README.md", "---\ndescription: 大写\n---\n正文");
        env.WriteSkill("Bad_Name", "SKILL.md", "---\ndescription: 下划线\n---\n正文");
        env.WriteSkill("trailing-", "trailing-.md", "---\ndescription: 尾连字符\n---\n正文");

        var names = env.Discover().Skills.Select(s => s.Name).ToArray();
        return names.Length == 1 && names[0] == "good-skill"
            ? null
            : $"只应发现 good-skill，实际：{string.Join(",", names)}";
    }

    /// <summary>同名时项目级（rank 小）赢，且影子链被记下。</summary>
    private static async Task<string?> PriorityAsync()
    {
        await Task.CompletedTask;
        using var env = new Env();
        env.WriteSkillAt(env.ProjectSkills, "dup-skill", "SKILL.md", "---\ndescription: 项目版\n---\n正文");
        env.WriteSkillAt(env.UserSkills, "dup-skill", "SKILL.md", "---\ndescription: 用户版\n---\n正文");

        var catalog = env.Discover();
        if (catalog.Skills.Count != 1) return $"应合并为 1 个，实际 {catalog.Skills.Count}";

        var skill = catalog.Skills[0];
        if (!skill.Description.Contains("项目版", StringComparison.Ordinal))
            return $"项目级没有优先：描述是 '{skill.Description}'";

        // **影子链必须报出来**：用户会问"我用户目录里那个为什么没生效"。
        return skill.ShadowedBy.Count == 1
            ? null
            : $"没有记录被遮住的同名技能（{skill.ShadowedBy.Count} 条）";
    }

    /// <summary>长描述被截断，且总长度不超过上限。</summary>
    /// <remarks>
    /// <b>截断要给省略号留位</b>：前一阶段在会话标题上就是这里错的——
    /// 上限 + 一个省略号 = 超出一个字符，而那个数字是被声明的列宽。
    /// </remarks>
    private static async Task<string?> TruncationAsync()
    {
        await Task.CompletedTask;
        using var env = new Env(descriptionMaxLength: 10);
        env.WriteSkill(
            "long-desc", "SKILL.md",
            "---\ndescription: " + new string('x', 50) + "\n---\n正文");

        var skill = env.Discover().Skills.Single();
        if (skill.Description.Length > 10)
            return $"描述 {skill.Description.Length} 字符，超过上限 10";

        return skill.Description.EndsWith('…')
            ? null
            : "截断了却没有省略号——用户看不出还有下文";
    }

    /// <summary>没有 description 的技能仍然列出。</summary>
    /// <remarks>
    /// <b>丢掉它比列出来更糟</b>：用户写了 SKILL.md 却看不到，
    /// 会以为技能机制没生效。
    /// </remarks>
    private static async Task<string?> MissingDescriptionAsync()
    {
        await Task.CompletedTask;
        using var env = new Env();
        env.WriteSkill("no-desc", "SKILL.md", "---\n---\n正文");

        var catalog = env.Discover();
        if (catalog.Skills.Count != 1) return "没有描述的技能被整个丢掉了";
        return string.IsNullOrWhiteSpace(catalog.Skills[0].Description)
            ? "描述为空，模型无法判断它做什么"
            : null;
    }

    /// <summary>加载时剥掉 frontmatter，正文原样返回。</summary>
    private static async Task<string?> FrontmatterAsync()
    {
        using var env = new Env();
        env.WriteSkill(
            "body-skill", "SKILL.md",
            "---\nname: body-skill\ndescription: 有正文\n---\n# 步骤\n\n1. 做这个\n2. 做那个\n");

        var definition = await env.Discovery.LoadAsync("body-skill");
        if (definition is null) return "加载失败";

        if (definition.Content.Contains("description:", StringComparison.Ordinal))
            return "正文里残留了 frontmatter";

        return definition.Content.Contains("1. 做这个", StringComparison.Ordinal)
            ? null
            : $"正文没有读回来（实际 '{definition.Content.Trim()}'）";
    }

    /// <summary>每次加载都重读文件。</summary>
    /// <remarks>
    /// <b>缓存会让"我改了但它还是照旧的来"</b>。dsh 明确说不缓存完整体。
    /// </remarks>
    private static async Task<string?> RereadsAsync()
    {
        using var env = new Env();
        env.WriteSkill("live-skill", "SKILL.md", "---\ndescription: d\n---\n旧内容");

        var first = await env.Discovery.LoadAsync("live-skill");
        if (first is null || !first.Content.Contains("旧内容", StringComparison.Ordinal))
            return "第一次加载不对";

        // 改文件后再加载。
        env.WriteSkill("live-skill", "SKILL.md", "---\ndescription: d\n---\n新内容");

        var second = await env.Discovery.LoadAsync("live-skill");
        return second is not null && second.Content.Contains("新内容", StringComparison.Ordinal)
            ? null
            : "第二次加载拿到的是旧内容——有缓存，用户改了文件不生效";
    }

    /// <summary>
    /// <c>disable-model-invocation: true</c> 既不出现在目录里，也不能被工具加载。
    /// </summary>
    /// <remarks>
    /// <b>只做目录过滤是不够的</b>：那是"看不见但能用"。
    /// 这条检查两处都要验——目录里没有、加载也拒绝。
    /// </remarks>
    private static async Task<string?> VisibilityAsync()
    {
        using var env = new Env();
        env.WriteSkill(
            "user-only", "SKILL.md",
            "---\ndescription: 只给用户\ndisable-model-invocation: true\n---\n正文");
        env.WriteSkill("model-ok", "SKILL.md", "---\ndescription: 模型可用\n---\n正文");

        var catalog = env.Discover();
        var hidden = catalog.Skills.FirstOrDefault(s => s.Name == "user-only");
        if (hidden is null) return "技能根本没被发现（应被发现但标记为不可调用）";
        if (hidden.ModelInvocable) return "disable-model-invocation 没有生效";

        // 目录文本里不该出现它。
        var text = SkillCatalogText.Render(catalog);
        if (text.Contains("user-only", StringComparison.Ordinal))
            return "不可调用的技能出现在模型目录里";

        if (!text.Contains("model-ok", StringComparison.Ordinal))
            return "可调用的技能没有出现在目录里";

        // 工具加载也必须拒绝。
        var tool = new SkillTool(env.Discovery);
        var result = await tool.ExecuteAsync(
            Invocation("user-only"), CancellationToken.None);

        var ok = result.Content.TryGetProperty("ok", out var okElement)
            && okElement.ValueKind is JsonValueKind.True;
        return ok ? "工具仍然加载了不可调用的技能——开关等于没做" : null;
    }

    /// <summary>工具拒绝非法名字与不存在的技能。</summary>
    private static async Task<string?> ToolErrorsAsync()
    {
        using var env = new Env();
        env.WriteSkill("real-skill", "SKILL.md", "---\ndescription: 真的\n---\n正文");
        var tool = new SkillTool(env.Discovery);

        var bad = await tool.ExecuteAsync(Invocation("Not Kebab"), CancellationToken.None);
        if (IsOk(bad)) return "非法名字被接受了";

        var missing = await tool.ExecuteAsync(Invocation("no-such-skill"), CancellationToken.None);
        if (IsOk(missing)) return "不存在的技能被接受了";

        // **错误消息不该暴露"存在但不可见"这一事实**：所以只说"可能不存在、
        // 名字写错或对模型不可调用"。
        var error = missing.Content.TryGetProperty("error", out var e)
            && e.ValueKind is JsonValueKind.String
                ? e.GetString() ?? string.Empty
                : string.Empty;
        if (error.Length == 0) return "失败结果没有原因";

        var good = await tool.ExecuteAsync(Invocation("real-skill"), CancellationToken.None);
        return IsOk(good) ? null : "合法技能加载失败";
    }

    /// <summary>目录文本转义且不含绝对路径。</summary>
    /// <remarks>
    /// <b>不带绝对路径</b>：那是 dsh 契约原话（"never the body or absolute file path"），
    /// 而绝对路径会把用户名带进对话、随日志外发。
    /// </remarks>
    private static async Task<string?> CatalogTextAsync()
    {
        await Task.CompletedTask;
        using var env = new Env();
        env.WriteSkill(
            "escape-skill", "SKILL.md",
            "---\ndescription: 有 <b> 与 & 的说明\n---\n正文");

        var text = SkillCatalogText.Render(env.Discover());

        if (text.Contains("<b>", StringComparison.Ordinal))
            return "描述没有被转义——会把标记打乱";

        if (text.Contains(env.Root, StringComparison.OrdinalIgnoreCase))
            return "目录文本里出现了绝对路径";

        // **同名冲突时也不能泄漏路径**。第一版这条检查只造了一个技能，
        // 影子链恒为空，于是"修 ShadowedBy 渲染"时把 root.Path 写了进去
        // 也照样绿——那正好破坏了同类里"不给绝对路径"的承诺。
        env.WriteSkillAt(env.UserSkills, "escape-skill", "SKILL.md",
            "---\ndescription: 用户版\n---\n正文");

        var withShadow = SkillCatalogText.Render(env.Discover());
        if (withShadow.Contains(env.Root, StringComparison.OrdinalIgnoreCase))
            return "影子链里出现了绝对路径——会把用户名带进对话并随日志外发";

        if (!withShadow.Contains("user-dsh", StringComparison.Ordinal))
            return "影子链没有报出来源——用户仍无法知道「我那个为什么没生效」";

        return text.Contains("&lt;b&gt;", StringComparison.Ordinal)
            ? null
            : "转义结果不是预期形态";
    }

    /// <summary>装配里真的注册了工具、注入了目录、且用的是原始项目。</summary>
    private static async Task<string?> WiringAsync()
    {
        await Task.CompletedTask;
        var root = FindRepoRoot();
        var file = Path.Combine(root, "src", "Dougong.Cli", "SessionComposition.cs");
        if (!File.Exists(file)) return $"找不到装配文件：{file}";

        var text = File.ReadAllText(file);

        if (!text.Contains("new SkillTool(", StringComparison.Ordinal))
            return "装配里没有注册 skill 工具";

        // **按分支数验，不只查字符串**：只查"文件里有没有这一句"在
        // 属性只加进一条返回路径时照样通过，而那正是本能力的真实缺陷形态。
        if (CheckAssignedOnEveryBranch(text, "Skills") is { } branchFailure)
            return branchFailure;

        // **技能按原始项目扫**：跟着有效目录换会让列表随 cd 变化，
        // 而模型已经加载过的正文不会变，那是不一致。
        return text.Contains("Path.Combine(workspace, \".dougong\", \"skills\")", StringComparison.Ordinal)
            ? null
            : "技能根不是按原始项目构造的";
    }

    // ── 支撑 ─────────────────────────────────────────────────────

    private static bool IsOk(AgentToolResult result) =>
        result.Content.TryGetProperty("ok", out var ok) && ok.ValueKind is JsonValueKind.True;

    private static ToolInvocation Invocation(string name)
    {
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(new { name }));
        return new ToolInvocation("inv-1", SkillTool.ToolId, document.RootElement.Clone());
    }


    /// <summary>
    /// 断言某个属性在 <c>CreateRuntime</c> 的<b>每一条</b>返回路径上都赋了值。
    /// </summary>
    /// <remarks>
    /// <b>为什么要按分支数，而不是"文件里有没有这个字符串"</b>：
    /// <c>CreateRuntime</c> 有两条返回（无压缩 / 有压缩），而第一版检查只做
    /// <c>text.Contains("Skills = _skills")</c>。属性只加在其中一条分支时，
    /// 字符串依然存在，检查照样通过——实际效果是"REPL 常态下功能不生效"。
    /// 这正是本项目反复出现的失败模式，所以判定改成"赋值次数 ≥ 返回次数"。
    /// </remarks>
    private static string? CheckAssignedOnEveryBranch(string source, string property)
    {
        // **只数 CreateRuntime 方法体内部**。全文件数会把子 agent 的运行时构造
        // （CreateSubagentRuntime）也算进来——那不是本检查的对象，
        // 而多算一处就要求属性在那里也赋值，于是检查变成假的红。
        var body = MethodBody(source, "LoopAgentRuntime CreateRuntime(");
        if (body is null) return "找不到 CreateRuntime 方法体——检查失效";

        var returns = CountOccurrences(body, "return new LoopAgentRuntime");
        var assignments = CountOccurrences(body, property + " =");

        if (returns < 2)
            return $"前提不成立：CreateRuntime 只找到 {returns} 条返回路径（预期至少 2 条）";

        return assignments >= returns
            ? null
            : $"{property} 只在 {assignments} 条返回路径上赋值，而 CreateRuntime 有 {returns} 条——"
                + "某条分支上功能不生效";
    }

    /// <summary>截取一个方法的正文（从签名后的第一个 { 到配对的 }）。</summary>
    private static string? MethodBody(string source, string signature)
    {
        var start = source.IndexOf(signature, StringComparison.Ordinal);
        if (start < 0) return null;

        var open = source.IndexOf('{', start);
        if (open < 0) return null;

        var depth = 0;
        for (var i = open; i < source.Length; i++)
        {
            if (source[i] == '{') depth++;
            else if (source[i] == '}')
            {
                depth--;
                if (depth == 0) return source[open..(i + 1)];
            }
        }

        return null;
    }

    private static int CountOccurrences(string text, string needle)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }

        return count;
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Dougong.slnx")))
                return directory.FullName;
            directory = directory.Parent;
        }

        return Directory.GetCurrentDirectory();
    }

    private sealed class Env : IDisposable
    {
        public Env(int descriptionMaxLength = SkillDiscovery.DefaultDescriptionMaxLength)
        {
            Root = Path.Combine(Path.GetTempPath(), $"dougong-skill-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Root);

            ProjectSkills = Path.Combine(Root, "project", "skills");
            UserSkills = Path.Combine(Root, "user", "skills");

            Discovery = new SkillDiscovery(
                [
                    new SkillRoot(100, "project-dsh", ProjectSkills),
                    new SkillRoot(400, "user-dsh", UserSkills),
                ],
                descriptionMaxLength);
        }

        public string Root { get; }

        public string ProjectSkills { get; }

        public string UserSkills { get; }

        public SkillDiscovery Discovery { get; }

        /// <summary>在项目级根里写一个技能。</summary>
        public void WriteSkill(string name, string fileName, string content) =>
            WriteSkillAt(ProjectSkills, name, fileName, content);

        /// <summary>在项目级根下写一个平铺技能 <c>&lt;name&gt;.md</c>。</summary>
        public void WriteFlatSkill(string name, string content)
        {
            Directory.CreateDirectory(ProjectSkills);
            File.WriteAllText(Path.Combine(ProjectSkills, name + ".md"), content);
        }

        public void WriteSkillAt(string root, string name, string fileName, string content)
        {
            var directory = Path.Combine(root, name);
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, fileName), content);
        }

        public SkillCatalog Discover() => Discovery.Discover();

        public void Dispose()
        {
            try { Directory.Delete(Root, recursive: true); }
            catch { /* 清理失败不影响结论 */ }
        }
    }

    private static async Task<int> CheckAsync(string name, Func<Task<string?>> action)
    {
        try
        {
            var failure = await action().ConfigureAwait(false);
            if (failure is null)
            {
                Console.WriteLine($"PASS {name}");
                return 0;
            }

            Console.WriteLine($"FAIL {name}: {failure}");
            return 1;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"FAIL {name}: {ex.GetType().Name}: {ex.Message}");
            return 1;
        }
    }
}
