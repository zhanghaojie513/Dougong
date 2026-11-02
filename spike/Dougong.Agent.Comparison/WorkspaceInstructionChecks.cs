using Dougong.Agent;

namespace Dougong.Agent.Comparison;

/// <summary>
/// 工作区指令（<c>AGENTS.md</c>）加载的验收。
/// </summary>
/// <remarks>
/// <b>可观察面是"模型收到的那段提示"</b>，不是"文件被读了"：
/// <list type="bullet">
/// <item>有文件时内容出现在提示里，且带来源路径</item>
/// <item>没有文件时**不产出空节**——空的"项目约定"标题会让模型以为项目没写约定</item>
/// <item><c>CLAUDE.md</c> 与 <c>AGENTS.md</c> 内容相同时只出现一次</item>
/// <item>全局与项目内容相同时，项目那份胜出（更贴近当前工作）</item>
/// <item>超预算时截断而非丢弃，且**不切坏 UTF-8**</item>
/// </list>
/// </remarks>
public static class WorkspaceInstructionChecks
{
    public static async Task<int> RunAsync()
    {
        var failures = 0;
        failures += await CheckAsync("no-instructions-means-no-section", NoSectionAsync);
        failures += await CheckAsync("project-instructions-are-loaded", ProjectLoadedAsync);
        failures += await CheckAsync("duplicate-file-names-render-once", DuplicateAsync);
        failures += await CheckAsync("global-and-project-both-included", BothChainsAsync);
        failures += await CheckAsync("project-wins-when-identical", ProjectWinsAsync);
        failures += await CheckAsync("global-header-dropped-when-fully-deduped", GlobalHeaderDroppedAsync);
        failures += await CheckAsync("over-budget-is-truncated-not-dropped", TruncatedAsync);
        failures += await CheckAsync("truncation-keeps-utf8-intact", Utf8SafeAsync);
        failures += await CheckAsync("instructions-do-not-starve-tool-policy", ToolPolicySurvivesAsync);

        Console.WriteLine(failures == 0
            ? "WS-INSTRUCTIONS: ALL PASS"
            : $"WS-INSTRUCTIONS: {failures} FAILURE(S)");
        return failures;
    }

    /// <summary>没有指令文件时不产出这一节。</summary>
    /// <remarks>
    /// <b>空的"## 项目约定"标题比没有更糟</b>：模型会以为项目写了约定但内容丢了，
    /// 于是去猜或者问用户。
    /// </remarks>
    private static async Task<string?> NoSectionAsync()
    {
        using var tree = new TempTree();
        var prompt = await BuildAsync(tree.Workspace, tree.Global, []);

        if (prompt is null) return null;   // 整段都没有：正确

        // **两个标题都要查**。只查「项目约定」的话，一个只产出空
        // 「用户全局约定」标题的实现会通过——那同样是本类要避免的症状。
        if (prompt.Contains("项目约定", StringComparison.Ordinal))
            return "没有指令文件却出现了空的「项目约定」节";
        if (prompt.Contains("用户全局约定", StringComparison.Ordinal))
            return "没有指令文件却出现了空的「用户全局约定」节";

        return null;
    }

    /// <summary>项目级的 AGENTS.md 要进提示。</summary>
    private static async Task<string?> ProjectLoadedAsync()
    {
        using var tree = new TempTree();
        tree.Write("ws/AGENTS.md", "构建命令是 dotnet build");

        var prompt = await BuildAsync(tree.Workspace, tree.Global, []);
        if (prompt is null) return "有 AGENTS.md 却没有产出提示";

        if (!prompt.Contains("dotnet build", StringComparison.Ordinal))
            return $"内容没进提示：{prompt}";

        // 来源路径必须给出，否则模型不知道规则出自哪。
        return prompt.Contains("AGENTS.md", StringComparison.Ordinal)
            ? null
            : "提示里没有说明内容来自哪个文件";
    }

    /// <summary>
    /// <c>CLAUDE.md</c> 与 <c>AGENTS.md</c> 内容相同时只渲染一次。
    /// </summary>
    /// <remarks>
    /// 实际项目里这两个文件常互为副本（一个给 Claude Code，一个给其他工具）。
    /// 不去重会让同一段约定占两倍预算，而且模型可能以为有两套不同的规则。
    /// </remarks>
    private static async Task<string?> DuplicateAsync()
    {
        using var tree = new TempTree();
        const string content = "统一用 4 空格缩进";
        tree.Write("ws/AGENTS.md", content);
        tree.Write("ws/CLAUDE.md", content);

        var prompt = await BuildAsync(tree.Workspace, tree.Global, []);
        if (prompt is null) return "内容没进提示";

        var count = CountOccurrences(prompt, content);
        return count == 1 ? null : $"相同内容出现了 {count} 次，应只出现一次";
    }

    /// <summary>全局与项目两份不同内容都要出现。</summary>
    private static async Task<string?> BothChainsAsync()
    {
        using var tree = new TempTree();
        tree.Write("global/AGENTS.md", "全局规则：中文注释");
        tree.Write("ws/AGENTS.md", "项目规则：用 tabs");

        var prompt = await BuildAsync(tree.Workspace, tree.Global, []);
        if (prompt is null) return "两条链都没进提示";

        if (!prompt.Contains("全局规则", StringComparison.Ordinal))
            return $"全局链缺失：{prompt}";
        if (!prompt.Contains("项目规则", StringComparison.Ordinal))
            return "项目链缺失";

        // 顺序：全局在前，项目在后（宽泛到具体）。
        var globalIndex = prompt.IndexOf("全局规则", StringComparison.Ordinal);
        var projectIndex = prompt.IndexOf("项目规则", StringComparison.Ordinal);
        return globalIndex < projectIndex ? null : "项目约定出现在了全局约定之前";
    }

    /// <summary>两份内容相同时，项目那份胜出，只显示一次。</summary>
    /// <remarks>
    /// 项目级的更贴近当前工作。两份都显示会让模型以为有两条独立规则，
    /// 而实际是同一条——它可能因此重复遵守或困惑。
    /// </remarks>
    private static async Task<string?> ProjectWinsAsync()
    {
        using var tree = new TempTree();
        const string content = "同一条规则";
        tree.Write("global/AGENTS.md", content);
        tree.Write("ws/AGENTS.md", content);

        var prompt = await BuildAsync(tree.Workspace, tree.Global, []);
        if (prompt is null) return "内容没进提示";

        var count = CountOccurrences(prompt, content);
        if (count != 1) return $"相同内容出现了 {count} 次，应只出现一次";

        // **还要确认标题也没留下**。只数内容次数的话，"先写标题再逐条跳过"
        // 的实现会通过——它确实只渲染一次内容，但留下一个空的「用户全局约定」节。
        // 而空标题正是本类要避免的症状：模型会以为全局约定写了但内容丢了。
        return prompt.Contains("## 用户全局约定", StringComparison.Ordinal)
            ? "全局内容全被去重，却留下了空的「用户全局约定」标题"
            : null;
    }

    /// <summary>
    /// 全局内容被项目完全覆盖时，连「用户全局约定」标题都不该出现。
    /// </summary>
    /// <remarks>
    /// <b>与 <c>project-wins-when-identical</c> 的区别：那条只看内容出现几次，
    /// 这条专看标题</b>。两者一起才能钉住"先算去重集、再决定写不写标题"。
    /// <para>
    /// 这里项目侧有两个文件，覆盖"去重后项目仍有内容"的情形——
    /// 那种情况下最容易把标题留下（因为项目节确实要写，位置就在下面）。
    /// </para>
    /// </remarks>
    private static async Task<string?> GlobalHeaderDroppedAsync()
    {
        using var tree = new TempTree();
        tree.Write("global/AGENTS.md", "同一条");
        tree.Write("ws/AGENTS.md", "同一条");
        tree.Write("ws/CLAUDE.md", "另一条");

        var prompt = await BuildAsync(tree.Workspace, tree.Global, []);
        if (prompt is null) return "内容没进提示";

        if (prompt.Contains("## 用户全局约定", StringComparison.Ordinal))
            return $"全局被完全覆盖却仍写了「用户全局约定」标题：{prompt}";

        // 项目侧两条都还在。
        if (!prompt.Contains("同一条", StringComparison.Ordinal)
            || !prompt.Contains("另一条", StringComparison.Ordinal))
            return $"项目内容缺失：{prompt}";

        return null;
    }

    /// <summary>超预算时截断而非整段丢弃。</summary>
    /// <remarks>
    /// <b>丢弃会让"我写了约定但没生效"成为一个无法自查的故障</b>——
    /// 用户看到文件在、程序也读了，却没有任何效果。截断至少保留前半部分，
    /// 并明确写出被截断。
    /// </remarks>
    private static async Task<string?> TruncatedAsync()
    {
        using var tree = new TempTree();
        var huge = string.Concat(Enumerable.Repeat("这是一条很长的约定。", 1000));
        tree.Write("ws/AGENTS.md", huge);

        var section = new WorkspaceInstructionsSection(tree.Global);
        var text = await section.BuildAsync(new SystemPromptContext(tree.Workspace, []));

        if (text is null) return "超预算时整段丢弃了";

        var bytes = System.Text.Encoding.UTF8.GetByteCount(text);
        if (bytes > WorkspaceInstructionsSection.MaxBytesPerChain + 1024)
            return $"没有真正截断，产出 {bytes} 字节";

        return text.Contains("超出预算", StringComparison.Ordinal)
            ? null
            : "截断了但没说——用户会以为约定完整生效了";
    }

    /// <summary>截断不能把 UTF-8 多字节序列切断。</summary>
    /// <remarks>
    /// <b>这是中文场景的真实风险</b>：按字节截断时如果切在一个汉字中间，
    /// 剩下半个序列，解码成 <c>U+FFFD</c>。那会污染提示，而且看起来像文件坏了。
    /// </remarks>
    private static async Task<string?> Utf8SafeAsync()
    {
        using var tree = new TempTree();
        // 纯汉字：每字 3 字节，必然在某个字节中间越界。
        tree.Write("ws/AGENTS.md", new string('规', 3000));

        var section = new WorkspaceInstructionsSection(tree.Global);
        var text = await section.BuildAsync(new SystemPromptContext(tree.Workspace, []));

        if (text is null) return "超预算时整段丢弃了";
        return text.Contains('\uFFFD')
            ? "截断切坏了 UTF-8，产出里有替换字符"
            : null;
    }

    /// <summary>
    /// 指令预算不能把工具纪律挤出提示。
    /// </summary>
    /// <remarks>
    /// <b>这条钉的是两个预算打架</b>：第一次实现的链预算是 16 KiB，
    /// 而系统提示总预算只有 8000 字符——结果整个指令节被总预算截掉，
    /// 症状是"文件明明有内容，提示里一个字都没有"。
    /// <para>
    /// <b>必须用 ASCII 内容来试</b>，这一点很容易写错：链预算按 **UTF-8 字节**算，
    /// 总预算按 **字符**算。<b>汉字是 3 字节 1 字符</b>，所以用中文填满 4 KiB
    /// 只得到 1365 个字符——离 8000 的字符上限还远，于是链预算设成 4 KiB 还是
    /// 16 KiB 都能通过，这条检查就空转了。ASCII 是 1 字节 1 字符，
    /// 两个单位重合，链预算才会真的顶到总预算。
    /// </para>
    /// <para>
    /// 现在两者对齐：约定最多 4 KiB，留给工具纪律足够空间。这条检查确认
    /// 装满约定后，工作区与工具纪律仍在。
    /// </para>
    /// </remarks>
    private static async Task<string?> ToolPolicySurvivesAsync()
    {
        using var tree = new TempTree();
        // **必须写到真的撑满链预算**，否则这条检查什么也没钉住：
        // 早先版本写 1400 字（约 4200 字节），无论链预算是 4 KiB 还是 16 KiB
        // 都不会被截断，于是把预算改回 16 KiB 它照样绿。
        // 现在写一个远超任何合理预算的文件，让它**必然**被截到 MaxBytesPerChain。
        // 用 ASCII 而不是汉字：见方法 remarks，汉字下两个预算的单位不重合。
        tree.Write("ws/AGENTS.md", new string('a', 40_000));

        var builder = new SystemPromptBuilder()
            .Add(new WorkspaceSection())
            .Add(new WorkspaceInstructionsSection(tree.Global))
            .Add(new FileToolPolicySection());

        var prompt = await builder.BuildAsync(
            new SystemPromptContext(tree.Workspace, ["read_file", "write_file", "edit_file"]));

        if (prompt is null) return "什么都没产出";

        // 先确认确实撑满了：没撑满的话下面几条断言都是空过。
        var sectionOnly = await new WorkspaceInstructionsSection(tree.Global)
            .BuildAsync(new SystemPromptContext(tree.Workspace, []));
        var sectionBytes = System.Text.Encoding.UTF8.GetByteCount(sectionOnly ?? string.Empty);
        if (sectionBytes < WorkspaceInstructionsSection.MaxBytesPerChain)
            return $"约定没撑满链预算（{sectionBytes} < {WorkspaceInstructionsSection.MaxBytesPerChain}），这条检查无效";

        if (!prompt.Contains("当前工作区", StringComparison.Ordinal))
            return "工作区分节被挤掉了";
        if (!prompt.Contains("edit_file", StringComparison.Ordinal))
            return "工具纪律被挤掉了——约定占了太多预算";
        if (prompt.Contains("超出长度上限", StringComparison.Ordinal))
            return "触发了总预算截断，说明两个预算仍然打架";

        return null;
    }

    // ── 支撑 ─────────────────────────────────────────────────────

    private static async Task<string?> BuildAsync(
        string workspace, string globalDirectory, string[] toolIds)
    {
        var builder = new SystemPromptBuilder()
            .Add(new WorkspaceSection())
            .Add(new WorkspaceInstructionsSection(globalDirectory));

        return await builder.BuildAsync(new SystemPromptContext(workspace, toolIds));
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

    /// <summary>临时目录树：<c>ws/</c> 与 <c>global/</c>。</summary>
    private sealed class TempTree : IDisposable
    {
        public TempTree()
        {
            Root = Path.Combine(
                Path.GetTempPath(), "dougong-ws-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Workspace);
            Directory.CreateDirectory(Global);
        }

        public string Root { get; }

        public string Workspace => Path.Combine(Root, "ws");

        public string Global => Path.Combine(Root, "global");

        public void Write(string relative, string content)
        {
            var full = Path.Combine(Root, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, content);
        }

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
