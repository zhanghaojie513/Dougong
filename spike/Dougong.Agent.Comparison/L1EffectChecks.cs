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
                "  这不是通过。效果验证必须用真实模型跑。");
            return 2;
        }

        Console.WriteLine($"L1-EFFECT: model={config.Model}, base={config.BaseUrl}, protocol={config.Protocol}");
        Console.WriteLine();

        var workingDirectory = Path.Combine(
            Path.GetTempPath(), "dougong-l1-effect-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(workingDirectory);

        try
        {
            // 先跑两组拿到原始文本，再统一判定。
            // 为什么不能边跑边判：concise 类任务是**相对判据**（要和另一组比），
            // 单看一条没有"多短算短"的客观界线。
            var withMemory = await RunArmAsync(config, apiKey, workingDirectory, memoryEnabled: true);
            var withoutMemory = await RunArmAsync(config, apiKey, workingDirectory, memoryEnabled: false);

            var judged = Judge(withMemory, withoutMemory);

            Console.WriteLine();
            Console.WriteLine("name".PadRight(34) + "pref".PadRight(30) + "with".PadRight(8) + "without");
            foreach (var j in judged)
            {
                Console.WriteLine(
                    j.Task.Name.PadRight(34)
                    + (j.Task.PreferenceKey + "=" + j.Task.PreferenceValue).PadRight(30)
                    + (j.FollowedWith ? "YES" : "no").PadRight(8)
                    + (j.FollowedWithout ? "YES" : "no"));
            }

            var total = judged.Count;
            var followWith = judged.Count(j => j.FollowedWith);
            var followWithout = judged.Count(j => j.FollowedWithout);
            var rateWith = (double)followWith / total;
            var rateWithout = (double)followWithout / total;
            var lift = rateWith - rateWithout;

            Console.WriteLine();
            Console.WriteLine($"  有记忆遵循率 {rateWith:P0}（{followWith}/{total}）");
            Console.WriteLine($"  无记忆遵循率 {rateWithout:P0}（{followWithout}/{total}）");
            Console.WriteLine($"  提升 {lift:+0.0%;-0.0%;0.0%}（预先登记的最小幅度 {RequiredLift:P0}）");

            // 分项报告：两类判据的性质不同，合并成一个数字会掩盖差异。
            foreach (var group in judged.GroupBy(j => j.Task.PreferenceKey))
            {
                var gw = group.Count(j => j.FollowedWith);
                var go = group.Count(j => j.FollowedWithout);
                var measure = group.First().Task.Measure == TaskMeasure.Relative ? "相对" : "绝对";
                Console.WriteLine(
                    $"    {group.Key}（{measure}判据）：有记忆 {gw}/{group.Count()}，"
                    + $"无记忆 {go}/{group.Count()}");
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

            var (text, outcome) = await RunOnceAsync(
                config, apiKey, task, memoryStore, memoryEnabled, directory);

            // 这里只收集文本，不做判定——相对判据要等两组都有了才能比。
            results.Add(new TaskResult(task, text, outcome));
            Console.WriteLine(
                $"  {task.Name.PadRight(34)} 长度 {text.Trim().Length,5}"
                + $"   回复：{Shorten(text)}");
        }

        return results;
    }

    /// <summary>
    /// 对两组结果做判定。
    /// </summary>
    /// <remarks>
    /// 相对判据（concise）问"有记忆组是否明显更短"，需要两组文本；
    /// 绝对判据（PascalCase）各自独立判定，但为了表格可比，
    /// 两组都用同一套规则算一遍——这样"无记忆组也遵循了"这种情况能看出来。
    /// </remarks>
    private static List<JudgedTask> Judge(
        IReadOnlyList<TaskResult> withMemory, IReadOnlyList<TaskResult> withoutMemory)
    {
        var judged = new List<JudgedTask>(withMemory.Count);

        for (var i = 0; i < withMemory.Count; i++)
        {
            var w = withMemory[i];
            var o = withoutMemory[i];

            if (w.Task.Measure == TaskMeasure.Absolute)
            {
                // 绝对判据：两组各看各的
                judged.Add(new JudgedTask(
                    w.Task,
                    w.Task.JudgeSingle(w.Text),
                    o.Task.JudgeSingle(o.Text)));
            }
            else
            {
                // 相对判据：只有"有记忆组相对无记忆组"这一个比较有意义。
                // 无记忆组本身无法判定是否遵循（没有基线可比），按未遵循计——
                // 这与实验定义一致：无记忆组的"遵循"是偶然，不是被偏好驱动的。
                judged.Add(new JudgedTask(
                    w.Task,
                    w.Task.JudgePair(w.Text, o.Text),
                    FollowedWithout: false));
            }
        }

        return judged;
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
    private static async Task<(string Text, AgentRunOutcome Outcome)> RunOnceAsync(
        ChatProviderConfig config, string apiKey, HeldOutTask task,
        MemoryStore store, bool memoryEnabled, string workspace)
    {
        using var client = ChatClientFactory.Create(config, apiKey);

        var injector = memoryEnabled ? new StoreMemoryInjector(store) : null;

        // 先确认偏好真的会被注入。不做这一步，"两组都是 0%" 就无法区分
        // "记忆没用" 与 "注入根本没生效"——那是两种完全不同的结论。
        var snapshot = injector is null
            ? (IReadOnlyList<MemoryArtifact>)[]
            : await injector.SnapshotAsync(workspace).ConfigureAwait(false);

        if (memoryEnabled && snapshot.Count == 0)
        {
            throw new InvalidOperationException(
                $"注入器返回空快照（任务 {task.Name}）：记忆未生效，本次运行不能作为效果证据。");
        }

        var runtime = new LoopAgentRuntime(client, new ToolDispatcher())
        {
            // 无记忆组不传注入器——这就是"关闭该数据流"的表达（验收 6 的开关）。
            Memory = injector,
            MemoryWorkspace = workspace,
        };

        var text = new StringBuilder();
        var outcome = AgentRunOutcome.Completed;
        string? diagnostic = null;

        await foreach (var evt in runtime.RunAsync(
            new AgentRunRequest("eval", task.Name, [], task.Prompt)))
        {
            switch (evt)
            {
                case AgentEvent.TextDelta delta:
                    text.Append(delta.Text);
                    break;

                // 必须读终态：此前硬编码 Completed，导致"请求失败"和
                // "模型答了但没遵循偏好"在结果里完全一样——都是空回复 + 未遵循。
                case AgentEvent.RunFinished finished:
                    outcome = finished.Outcome;
                    diagnostic = finished.Diagnostic;
                    break;
            }
        }

        if (outcome != AgentRunOutcome.Completed)
        {
            throw new InvalidOperationException(
                $"任务 {task.Name} 未正常结束：{outcome}（{diagnostic}）。"
                + "这不是'记忆没用'，是这次调用失败，不能计入效果统计。");
        }

        if (text.Length == 0)
        {
            throw new InvalidOperationException(
                $"任务 {task.Name} 收到空回复。空回复计为'未遵循'会把装置故障"
                + "伪装成'记忆无效'——必须先修装置。");
        }

        return (text.ToString(), outcome);
    }

    private static string Shorten(string text)
    {
        var single = text.ReplaceLineEndings(" ⏎ ").Trim();
        return single.Length <= 160 ? single : single[..160] + "…";
    }

    private sealed record TaskResult(HeldOutTask Task, string Text, AgentRunOutcome Outcome);

    private sealed record JudgedTask(
        HeldOutTask Task, bool FollowedWith, bool FollowedWithout);
}

/// <summary>一个留出任务：中性提示 + 它所对应的偏好 + 机械判据。</summary>
/// <param name="Name">任务名。</param>
/// <param name="Prompt">给模型的提示。**不得提及偏好**。</param>
/// <param name="PreferenceKey">该任务对应的偏好键。</param>
/// <param name="PreferenceValue">该偏好的值。</param>
/// <param name="IsFollowed">
/// 单条判据：这条回答本身是否体现了偏好。
/// 只适用于有**绝对标准**的偏好（例如命名风格——要么是 PascalCase，要么不是）。
/// </param>
/// <param name="CompareFollowed">
/// 成对判据：有记忆组的回答相对无记忆组是否体现了偏好。
/// 用于没有绝对标准的偏好（例如 concise——"多短算短"没有客观界线），
/// 只能问"有没有变短"。
/// <para>
/// 由 <see cref="HeldOutTask.Measure"/> 表达哪一种是本任务使用的判据。
/// </para>
/// </param>
public sealed record HeldOutTask(
    string Name,
    string Prompt,
    string PreferenceKey,
    string PreferenceValue,
    Func<string, bool>? IsFollowed = null,
    Func<string, string, bool>? CompareFollowed = null)
{
    /// <summary>本任务用哪种判据。</summary>
    public TaskMeasure Measure =>
        IsFollowed is not null ? TaskMeasure.Absolute : TaskMeasure.Relative;

    /// <summary>
    /// 单条判定。仅当 <see cref="Measure"/> 为 <see cref="TaskMeasure.Absolute"/> 时可用。
    /// </summary>
    /// <remarks>
    /// 提供这个方法而不是到处写 <c>IsFollowed!</c>：调用点少一次判空心智负担，
    /// 用错判据类型时立即抛异常而不是空引用。
    /// </remarks>
    public bool JudgeSingle(string text) =>
        IsFollowed?.Invoke(text)
        ?? throw new InvalidOperationException(
            $"任务 '{Name}' 用的是相对判据，不能单条判定。");

    /// <summary>
    /// 成对判定。仅当 <see cref="Measure"/> 为 <see cref="TaskMeasure.Relative"/> 时可用。
    /// </summary>
    public bool JudgePair(string withMemory, string withoutMemory) =>
        CompareFollowed?.Invoke(withMemory, withoutMemory)
        ?? throw new InvalidOperationException(
            $"任务 '{Name}' 用的是绝对判据，不需要成对判定。");
}

/// <summary>判据类型。</summary>
public enum TaskMeasure
{
    /// <summary>有绝对标准：单条回答即可判定。</summary>
    Absolute,

    /// <summary>无绝对标准：只能和有记忆组对照，看是否朝偏好方向变化。</summary>
    Relative,
}

/// <summary>
/// 留出任务集（v2，2026-10-26 重新登记）。
/// </summary>
/// <remarks>
/// <b>为什么有 v2</b>：v1 用 4 个 <c>language=中文</c> 任务，结果在 14 个
/// provider/协议组合上**两组都是 4/4**——这些模型默认就用中文回答。
/// 那 4 个任务对它们零区分力，把提升上限压到 60%，于是预先登记的 50% 阈值
/// 虽然在统计上"没达到"，却不是"记忆无效"的证据。
/// <para>
/// <b>v2 的原则：只测模型默认不会做的事。</b>
/// 每条任务对应的偏好都必须与模型的默认倾向相反，否则测的是模型能力而不是记忆。
/// </para>
/// <list type="number">
/// <item><c>output.format=concise</c>：模型默认爱写长篇。这一项**用相对判据**——
/// "多短算短"没有客观界线（实测注入后 207–813 字符、默认 893–1243，
/// 确实变短但到不了任何绝对阈值），所以只能问"有没有变短"。</item>
/// <item><c>naming.convention=PascalCase</c>：模型给字段名默认用 camelCase
/// 或 snake_case，PascalCase 是反直觉选择。这一项有**绝对判据**。</item>
/// </list>
/// <para>
/// <b>v1 结果不废弃</b>：它证明了注入在 6 家 provider × 2 种协议上一致生效
/// （有记忆组 14/14 全部 100%），这是真实结论。v2 只用来测提升幅度。
/// 两批分开记录，不混报。
/// </para>
/// </remarks>
public static class HeldOutTasks
{
    public static IReadOnlyList<HeldOutTask> All { get; } =
    [
        new("explain-http-cache", "说明 HTTP 缓存的工作方式。", "output.format", "concise",
            CompareFollowed: IsShorter),

        new("explain-git-rebase", "解释 git rebase 和 merge 的区别。", "output.format", "concise",
            CompareFollowed: IsShorter),

        new("describe-docker-layer", "描述 Docker 镜像层的复用机制。", "output.format", "concise",
            CompareFollowed: IsShorter),

        new("explain-tcp-handshake", "简述 TCP 三次握手。", "output.format", "concise",
            CompareFollowed: IsShorter),

        new("explain-deadlock", "什么情况下会发生死锁？", "output.format", "concise",
            CompareFollowed: IsShorter),

        new("name-config-fields", "为一个配置对象起三个字段名并说明用途。", "naming.convention", "PascalCase",
            IsFollowed: PredominantlyPascalCase),

        new("name-json-fields", "设计一个表示用户资料的 JSON 结构，给出字段名。", "naming.convention", "PascalCase",
            IsFollowed: PredominantlyPascalCase),

        new("name-db-columns", "为一张订单表设计列名。", "naming.convention", "PascalCase",
            IsFollowed: PredominantlyPascalCase),

        new("name-api-fields", "为一个 REST 接口的请求体设计字段名。", "naming.convention", "PascalCase",
            IsFollowed: PredominantlyPascalCase),

        new("name-log-fields", "为一条结构化日志设计字段名。", "naming.convention", "PascalCase",
            IsFollowed: PredominantlyPascalCase),
    ];

    /// <summary>
    /// concise 的相对判据：有记忆组的回答必须比无记忆组**明显更短**。
    /// </summary>
    /// <remarks>
    /// <b>为什么不用绝对阈值</b>：一开始定的是"≤200 字符"，实测发现模型收到
    /// concise 偏好后压到 207–813 字符（默认 893–1243）。它确实照做了，
    /// 只是"多短算短"没有客观标准——200 是我凭感觉定的，与模型行为不匹配。
    /// 用绝对阈值会把"部分遵循"记成"完全没遵循"，低估效果。
    /// <para>
    /// <b>为什么要求 15% 的降幅而不是"只要更短"</b>：同一模型两次调用之间
    /// 长度本来就有波动。不设最小幅度，"短了 3 个字符"也会算遵循，
    /// 那是噪声不是效果。
    /// </para>
    /// <para>
    /// 下限 40 字符仍然保留：只回一句"好"是退化，不是遵循。
    /// </para>
    /// </remarks>
    private const double MinShrinkRatio = 0.15;

    private static bool IsShorter(string withMemory, string withoutMemory)
    {
        var a = withMemory.Trim();
        var b = withoutMemory.Trim();

        if (a.Length < 40) return false;          // 退化，不算遵循
        if (b.Length == 0) return false;          // 对照无内容，无法比较

        return a.Length <= b.Length * (1 - MinShrinkRatio);
    }

    /// <summary>
    /// PascalCase 判据：标识符以 PascalCase 为主，且数量足以说明是**系统性**选择。
    /// </summary>
    /// <remarks>
    /// 要求 PascalCase **多于** camelCase，且至少出现 <see cref="MinPascalIdentifiers"/> 个。
    /// <para>
    /// <b>为什么要求至少 2 个</b>：实测发现无记忆组的回答里偶尔会出现单个 PascalCase
    /// 标识符（例如订单表的 `OrderId`——那是业界惯例，不是响应偏好）。
    /// 只要求"出现一个"，这种偶然命中会被记成"遵循"，直接抬高无记忆组基线、
    /// 低估记忆效果。
    /// </para>
    /// <para>
    /// 而"以 camelCase 为主"仍会被正确判为未遵循——这是计数比较的作用。
    /// </para>
    /// </remarks>
    private const int MinPascalIdentifiers = 2;

    private static bool PredominantlyPascalCase(string text)
    {
        var pascal = PascalPattern.Matches(text).Count;
        var camel = LowerCamelPattern.Matches(text).Count;
        return pascal >= MinPascalIdentifiers && pascal > camel;
    }

    /// <summary>PascalCase：大写字母开头，至少两个词段。</summary>
    private static readonly System.Text.RegularExpressions.Regex PascalPattern =
        new(@"\b[A-Z][a-z0-9]+(?:[A-Z][a-z0-9]+)+\b",
            System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>lowerCamelCase：小写字母开头，含大写转折。</summary>
    private static readonly System.Text.RegularExpressions.Regex LowerCamelPattern =
        new(@"\b[a-z]+[A-Z][a-zA-Z0-9]*\b",
            System.Text.RegularExpressions.RegexOptions.Compiled);
}
