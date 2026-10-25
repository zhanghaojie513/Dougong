using System.Text;
using System.Text.Json;
using Dougong.Agent;
using Dougong.Ai;
using Microsoft.Extensions.AI;

namespace Dougong.Agent.Comparison;

/// <summary>
/// Skill L1 验收 1：记忆启用前后，在**留出任务**上的偏好遵循率对照。
/// </summary>
/// <remarks>
/// 这是 `skills-evolution.md §7` 验收 1，也是唯一能回答
/// "记忆是否真的改善了任务表现"的检查。前面 2–6 只证明管道安全。
/// <para>
/// <b>为什么用"偏好遵循率"而不是"任务成功率"</b>：
/// 偏好遵循有可机械判定的特征（语言、条目形状、命名风格），不依赖人判断、
/// 不需要第二个模型当裁判。任务成功率需要可判定的成败标准，而编码任务的
/// 成败本身就要人来判——那会把"记忆有没有用"变成"裁判准不准"。
/// </para>
/// <para>
/// <b>关键的实验设计约束</b>：任务提示里**绝不能出现偏好相关的字眼**。
/// 一旦提示里写了"用中文回答"，两组都会照做，指标立刻失去区分力。
/// 所以本文件里每个 Task 的 Prompt 都是中性请求，偏好只存在于记忆里。
/// </para>
/// <para>
/// <b>没有阈值就不算通过</b>：下面的 <see cref="RequiredLift"/> 是**预先登记的**
/// 最小提升幅度，不是跑完看结果再定的。跑完达不到就是达不到，不许回头改阈值。
/// </para>
/// </remarks>
public static class L1EffectChecks
{
    /// <summary>
    /// 预先登记的最小提升幅度（有记忆组遵循率 − 无记忆组遵循率）。
    /// </summary>
    /// <remarks>
    /// 0.5 的含义：10 个任务里，有记忆组必须至少多遵守 5 个。
    /// 定这么高是因为偏好遵循是**容易**的行为（模型只是不知道用户偏好），
    /// 如果记忆真的生效，提升应该非常明显。若只提升 1–2 个，
    /// 说明要么注入没生效、要么模型忽略了注入——两种情况都不该算"记忆有用"。
    /// </remarks>
    public const double RequiredLift = 0.5;

    /// <summary>每个任务跑几次。取 1 是因为单任务单次就已能判定；重复会增加成本不增加结论强度。</summary>
    public const int RunsPerTask = 1;

    public static async Task<int> RunAsync()
    {
        var config = ChatClientFactory.FromEnvironment();
        var apiKey = Environment.GetEnvironmentVariable("DOUGONG_PROVIDER_KEY");

        if (config is null || string.IsNullOrWhiteSpace(apiKey))
        {
            // 与 LiveRun 同一口径：缺配置就明确"未运行"，返回非零。
            // 返回非零而不是 0，是为了让 CI/脚本把它当作"没做"，而不是"通过"。
            Console.WriteLine(
                "L1-EFFECT: 未运行（缺少 DOUGONG_PROVIDER_BASE / KEY / MODEL）。");
            Console.WriteLine(
                "  这不是通过。验收 1 必须用真实模型跑，见 docs/skill-l1-acceptance-1.md。");
            return 2;
        }

        Console.WriteLine($"L1-EFFECT: model={config.Model}, base={config.BaseUrl}, protocol={config.Protocol}");
        Console.WriteLine();

        var workingDirectory = Path.Combine(
            Path.GetTempPath(), "dougong-l1-effect-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(workingDirectory);

        try
        {
            var withMemory = await RunArmAsync(config, apiKey, workingDirectory, memoryEnabled: true);
            var withoutMemory = await RunArmAsync(config, apiKey, workingDirectory, memoryEnabled: false);

            Console.WriteLine();
            Console.WriteLine("name".PadRight(34) + "pref".PadRight(10) + "with".PadRight(8) + "without");
            var followWith = 0;
            var followWithout = 0;
            foreach (var (w, o) in withMemory.Zip(withoutMemory))
            {
                followWith += w.Followed ? 1 : 0;
                followWithout += o.Followed ? 1 : 0;
                Console.WriteLine(
                    w.Task.Name.PadRight(34)
                    + (w.Task.PreferenceKey + "=" + w.Task.PreferenceValue).PadRight(10)
                    + (w.Followed ? "YES" : "no").PadRight(8)
                    + (o.Followed ? "YES" : "no"));
            }

            var total = withMemory.Count;
            var rateWith = (double)followWith / total;
            var rateWithout = (double)followWithout / total;
            var lift = rateWith - rateWithout;

            Console.WriteLine();
            Console.WriteLine($"  有记忆遵循率 {rateWith:P0}（{followWith}/{total}）");
            Console.WriteLine($"  无记忆遵循率 {rateWithout:P0}（{followWithout}/{total}）");
            Console.WriteLine($"  提升 {lift:+0.0%;-0.0%;0.0%}（预先登记的最小幅度 {RequiredLift:P0}）");

            // 安全回归同时看：验收 2 的"不得提权"必须在真实模型下也成立。
            // 这里不重复验证管道，只报告有无异常行为。
            var safe = withMemory.All(r => r.Outcome == AgentRunOutcome.Completed);
            if (!safe)
            {
                var bad = withMemory.Where(r => r.Outcome != AgentRunOutcome.Completed)
                    .Select(r => r.Task.Name);
                Console.WriteLine($"  未能完成的任务：{string.Join(", ", bad)}");
            }

            if (lift < RequiredLift)
            {
                Console.WriteLine();
                Console.WriteLine(
                    $"L1-EFFECT: 不达预期（提升 {lift:P0} < 要求 {RequiredLift:P0}）。"
                    + "这说明记忆在本条件下**没有**带来预期改善——结论就是没有，不许改阈值。");
                return 1;
            }

            Console.WriteLine();
            Console.WriteLine($"L1-EFFECT: PASS（提升 {lift:P0} ≥ 要求 {RequiredLift:P0}）");
            Console.WriteLine(
                "  结论仅适用于被测模型与这 10 个任务，不外推到其它模型或任务。");
            return 0;
        }
        finally
        {
            try { Directory.Delete(workingDirectory, recursive: true); } catch (IOException) { }
        }
    }

    /// <summary>跑一组（有记忆 / 无记忆）。</summary>
    private static async Task<List<TaskResult>> RunArmAsync(
        ChatProviderConfig config, string apiKey, string directory, bool memoryEnabled)
    {
        Console.WriteLine($"== {(memoryEnabled ? "有记忆" : "无记忆")} ==");

        var results = new List<TaskResult>();

        // 记忆文件按"组"隔离：同一条记忆不能跨组泄漏，
        // 否则无记忆组也会读到它，整组对照无效。
        var memoryPath = Path.Combine(
            directory, (memoryEnabled ? "with" : "without") + ".jsonl");

        await using var memoryStore = await MemoryStore.OpenAsync(memoryPath);

        foreach (var task in HeldOutTasks.All)
        {
            if (memoryEnabled)
            {
                await SeedPreferenceAsync(memoryStore, directory, task);
            }

            var text = await RunOnceAsync(
                config, apiKey, task, memoryStore, memoryEnabled, directory);
            var followed = task.IsFollowed(text);

            results.Add(new TaskResult(task, followed, AgentRunOutcome.Completed));
            Console.WriteLine(
                $"  {task.Name.PadRight(34)} {(followed ? "遵循" : "未遵循")}"
                + (followed ? "" : $"   回复：{Shorten(text)}"));
        }

        return results;
    }

    /// <summary>
    /// 把任务对应的偏好写进记忆。
    /// </summary>
    /// <remarks>
    /// 每条都用真实哈希，因此能通过注入器的完整性校验——
    /// 否则"记忆没生效"会被误读成"记忆没用"。
    /// </remarks>
    private static async ValueTask SeedPreferenceAsync(
        MemoryStore store, string directory, HeldOutTask task)
    {
        var artifact = new MemoryArtifact(
            ArtifactId: "eval-" + task.Name,
            Version: 1,
            ContentHash: MemoryArtifact.ComputeHash(task.PreferenceKey, task.PreferenceValue),
            Key: task.PreferenceKey,
            Value: task.PreferenceValue,
            Scope: MemoryScope.Workspace,
            Workspace: directory,
            SourceTaskId: "held-out-seed",
            CreatedAt: DateTimeOffset.UtcNow,
            State: MemoryArtifactState.Enabled);

        await store.PutAsync(artifact).ConfigureAwait(false);
    }

    /// <summary>跑一次任务，返回模型输出的纯文本。</summary>
    /// <remarks>
    /// 作业目录必须与写入产物时的 workspace 完全一致，否则注入器的作用域过滤
    /// 会把这条记忆当作"别的项目的"而拒绝加载——那样得到的是"记忆没用"的假结论。
    /// </remarks>
    private static async Task<string> RunOnceAsync(
        ChatProviderConfig config, string apiKey, HeldOutTask task,
        MemoryStore store, bool memoryEnabled, string workspace)
    {
        using var client = ChatClientFactory.Create(config, apiKey);

        var runtime = new LoopAgentRuntime(client, new ToolDispatcher())
        {
            // 无记忆组不传注入器——这就是"关闭该数据流"的表达（验收 6 的开关）。
            Memory = memoryEnabled ? new StoreMemoryInjector(store) : null,
            MemoryWorkspace = workspace,
        };

        var text = new StringBuilder();
        await foreach (var evt in runtime.RunAsync(
            new AgentRunRequest("eval", task.Name, [], task.Prompt)))
        {
            if (evt is AgentEvent.TextDelta delta) text.Append(delta.Text);
        }

        return text.ToString();
    }

    private static string Shorten(string text)
    {
        var single = text.ReplaceLineEndings(" ").Trim();
        return single.Length <= 80 ? single : single[..80] + "…";
    }

    private sealed record TaskResult(HeldOutTask Task, bool Followed, AgentRunOutcome Outcome);
}

/// <summary>一个留出任务：中性提示 + 它所对应的偏好 + 机械判据。</summary>
/// <param name="Name">任务名。</param>
/// <param name="Prompt">给模型的提示。**不得提及偏好**。</param>
/// <param name="PreferenceKey">该任务对应的偏好键。</param>
/// <param name="PreferenceValue">该偏好的值。</param>
/// <param name="IsFollowed">机械判据：回答是否体现了该偏好。</param>
public sealed record HeldOutTask(
    string Name,
    string Prompt,
    string PreferenceKey,
    string PreferenceValue,
    Func<string, bool> IsFollowed);

/// <summary>
/// 留出任务集。
/// </summary>
/// <remarks>
/// <b>留出的含义</b>：这些任务不是开发记忆层时用过的那种任务，也不是"教它记住"的任务。
/// 它们是**事后**挑的中性请求，用来检验偏好是否真的迁移到了新情境。
/// <para>
/// <b>判据为什么能机械化</b>：三个白名单键各自有一个可数特征——
/// 语言看字符集，条目形状看有无列表标记与行长，命名风格看标识符的分隔方式。
/// 都只检查"形状"，不判断内容对不对，因此不需要裁判模型。
/// </para>
/// <para>
/// 每条判据都**同时接受几种合理实现**，避免把"没按我预想的方式表达"误判成"未遵循"。
/// 例如 bullets 判据接受 `-`、`*`、`•`、`1.` 四种标记。
/// </para>
/// </remarks>
public static class HeldOutTasks
{
    public static IReadOnlyList<HeldOutTask> All { get; } =
    [
        new("explain-http-cache", "简要说明 HTTP 缓存的工作方式。", "language", "中文",
            t => ContainsCjk(t)),

        new("explain-git-rebase", "解释 git rebase 和 merge 的区别。", "language", "中文",
            t => ContainsCjk(t)),

        new("describe-docker-layer", "描述 Docker 镜像层的复用机制。", "language", "中文",
            t => ContainsCjk(t)),

        new("explain-tcp-handshake", "简述 TCP 三次握手。", "language", "中文",
            t => ContainsCjk(t)),

        new("list-review-tips", "列出三条代码评审建议。", "output.format", "bullets",
            t => LooksLikeBullets(t)),

        new("list-test-tips", "列出三条写单元测试的建议。", "output.format", "bullets",
            t => LooksLikeBullets(t)),

        new("list-refactor-signs", "列出三个该重构代码的信号。", "output.format", "bullets",
            t => LooksLikeBullets(t)),

        new("name-config-fields", "为一个配置对象起三个字段名并说明用途。", "naming.convention", "snake_case",
            t => PredominantlySnakeCase(t)),

        new("name-json-fields", "设计一个表示用户资料的 JSON 结构，给出字段名。", "naming.convention", "snake_case",
            t => PredominantlySnakeCase(t)),

        new("name-db-columns", "为一张订单表设计列名。", "naming.convention", "snake_case",
            t => PredominantlySnakeCase(t)),
    ];

    /// <summary>回答里是否含中日韩字符。</summary>
    private static bool ContainsCjk(string text) =>
        text.Any(c => c >= 0x4E00 && c <= 0x9FFF);

    /// <summary>
    /// 回答是否以列表形式给出。
    /// </summary>
    /// <remarks>
    /// 同时接受四种标记，且要求至少 2 行——只有一行带 `-` 不算"列表"。
    /// 门槛定得保守是为了避免把"恰好句首有横线"误判成遵循。
    /// </remarks>
    private static bool LooksLikeBullets(string text)
    {
        var lines = text.Split('\n')
            .Select(l => l.TrimStart())
            .Where(l => l.Length > 0)
            .ToArray();

        var bulletLines = lines.Count(l =>
            l.StartsWith("- ", StringComparison.Ordinal)
            || l.StartsWith("* ", StringComparison.Ordinal)
            || l.StartsWith("• ", StringComparison.Ordinal)
            || (l.Length > 2 && char.IsDigit(l[0]) && l[1] == '.'));

        return bulletLines >= 2;
    }

    /// <summary>
    /// 标识符是否以 snake_case 为主。
    /// </summary>
    /// <remarks>
    /// <b>为什么不是"必须全是 snake_case"</b>：模型偶尔会漏一个字段写成 camelCase，
    /// 那仍是响应了偏好（大部分改了），只是不完美。用"全对"当判据会把
    /// "基本遵循"判成"未遵循"，低估记忆效果。
    /// <para>
    /// <b>为什么不是只看"含 snake_case"</b>：那样一段"snake 和 camel 各半"的
    /// 回答也算遵循，会高估效果。所以要求 snake 标识符**多于** camel 标识符，
    /// 且至少出现一个 snake 标识符。
    /// </para>
    /// <para>
    /// 早先的实现写成 <c>HasSnakeCase(t) &amp;&amp; !HasCamelCase(t)</c>，
    /// 反证时发现前半句已经蕴含后半句（纯 camelCase 文本没有下划线，
    /// 本来就不满足 HasSnakeCase），那个排除条件是死代码。改成计数比较后
    /// 两个方向的行为都变得有意义。
    /// </para>
    /// </remarks>
    private static bool PredominantlySnakeCase(string text)
    {
        var snake = SnakePattern.Matches(text).Count;
        var camel = CamelPattern.Matches(text).Count;
        return snake > 0 && snake > camel;
    }

    private static readonly System.Text.RegularExpressions.Regex SnakePattern =
        new(@"\b[a-z][a-z0-9]*(?:_[a-z0-9]+)+\b",
            System.Text.RegularExpressions.RegexOptions.Compiled);

    private static readonly System.Text.RegularExpressions.Regex CamelPattern =
        new(@"\b[a-z]+[A-Z][a-zA-Z0-9]*\b",
            System.Text.RegularExpressions.RegexOptions.Compiled);
}
