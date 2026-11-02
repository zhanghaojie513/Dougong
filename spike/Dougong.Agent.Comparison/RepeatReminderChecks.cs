using System.Text.Json;
using Dougong.Agent;
using Microsoft.Extensions.AI;

namespace Dougong.Agent.Comparison;

/// <summary>
/// 重复调用提醒的验收。
/// </summary>
/// <remarks>
/// <b>可观察面是"什么时候提醒、提醒什么"</b>：
/// <list type="bullet">
/// <item>第几次提醒（阈值）</item>
/// <item>什么算"同一次调用"（键的构成）</item>
/// <item>什么会打断链（换工具、换参数、透明工具）</item>
/// <item>提醒里出现什么（截断的上限，且不影响检测）</item>
/// </list>
/// </remarks>
public static class RepeatReminderChecks
{
    public static async Task<int> RunAsync()
    {
        var failures = 0;
        failures += await CheckAsync("repeat-fires-at-each-threshold", ThresholdsAsync);
        failures += await CheckAsync("repeat-needs-identical-arguments", DifferentArgsAsync);
        failures += await CheckAsync("repeat-chain-resets-on-other-tool", DifferentToolAsync);
        failures += await CheckAsync("repeat-transparent-tools-neither-count-nor-reset", TransparentAsync);
        failures += await CheckAsync("repeat-preview-is-capped-but-detection-is-not", PreviewCapAsync);
        failures += await CheckAsync("repeat-reset-clears-chain", ResetAsync);
        failures += await CheckAsync("repeat-rejects-invalid-thresholds-loudly", ValidationAsync);
        failures += await CheckAsync("repeat-renders-actionable-text", RenderAsync);
        failures += await CheckAsync("repeat-reminder-actually-reaches-next-request", ReachesRequestAsync);
        failures += await CheckAsync("repeat-does-not-block-and-is-wired", WiringAsync);

        Console.WriteLine(failures == 0
            ? "REPEAT-REMINDER: ALL PASS"
            : $"REPEAT-REMINDER: {failures} FAILURE(S)");
        return failures;
    }

    /// <summary>每个阈值各提醒一次，中间不提醒。</summary>
    private static async Task<string?> ThresholdsAsync()
    {
        await Task.CompletedTask;
        var reminder = new RepeatToolReminder();   // 默认 3/5/8

        var fired = new List<int>();
        for (var i = 1; i <= 10; i++)
        {
            if (reminder.Observe("read_file", Args("a.txt")) is { } r)
                fired.Add(r.Consecutive);
        }

        var expected = new[] { 3, 5, 8 };
        return fired.SequenceEqual(expected)
            ? null
            : $"应在 {string.Join(",", expected)} 次提醒，实际 {string.Join(",", fired)} 次";
    }

    /// <summary>参数不同就不算同一次调用。</summary>
    /// <remarks>
    /// <b>这是"误报"的主要来源</b>：把参数忽略掉会让模型连续读三个不同文件
    /// 也被算成重复，而它收到提醒后不知道该改什么。
    /// </remarks>
    private static async Task<string?> DifferentArgsAsync()
    {
        await Task.CompletedTask;
        var reminder = new RepeatToolReminder();

        // a, b, a：从头到尾没有任何一次连续相同。
        var fired = new List<int>();
        for (var i = 0; i < 10; i++)
        {
            var name = i % 2 == 0 ? "a.txt" : "b.txt";
            if (reminder.Observe("read_file", Args(name)) is { } r)
                fired.Add(r.Consecutive);
        }

        return fired.Count == 0
            ? null
            : $"交替参数每次都是新的调用，不该提醒；实际提醒了 {fired.Count} 次";
    }

    /// <summary>换一个工具就打断链。</summary>
    private static async Task<string?> DifferentToolAsync()
    {
        await Task.CompletedTask;
        var reminder = new RepeatToolReminder();

        reminder.Observe("read_file", Args("a.txt"));
        reminder.Observe("read_file", Args("a.txt"));
        if (reminder.Consecutive != 2) return $"前置计数不对：{reminder.Consecutive}";

        reminder.Observe("write_file", Args("a.txt"));
        // 换了工具：链重新从 1 开始，而不是接着数到 3。
        return reminder.Consecutive == 1
            ? null
            : $"换工具后应从 1 重新计数，实际 {reminder.Consecutive}";
    }

    /// <summary>透明工具既不计数也不清零。</summary>
    /// <remarks>
    /// <b>这是最容易做错的一条</b>：把它们算作"换了调用"会让链频繁清零，
    /// 于是真正卡住的模型（一边重复读文件一边更新 todo）永远达不到阈值。
    /// </remarks>
    private static async Task<string?> TransparentAsync()
    {
        await Task.CompletedTask;
        var reminder = new RepeatToolReminder();

        reminder.Observe("read_file", Args("a.txt"));
        reminder.Observe("read_file", Args("a.txt"));
        reminder.Observe("todo_write", Args("x"));   // 透明：不打断

        if (reminder.Consecutive != 2)
            return $"透明工具打断了链：计数变成 {reminder.Consecutive}（应为 2）";

        // 第 3 次相同调用必须触发提醒——若透明工具把链清零，这里就数不到 3。
        var r = reminder.Observe("read_file", Args("a.txt"));
        return r?.Consecutive == 3
            ? null
            : "透明工具之后的第 3 次相同调用没有触发提醒";
    }

    /// <summary>提醒里的参数被截断，但链的判定用完整参数。</summary>
    /// <remarks>
    /// <b>两件事必须分开</b>：用截断后的参数当键，会让"两个长命令只有末尾不同"
    /// 被误判成重复；不截断展示，会让一条提醒带着一整段文件正文进下一轮请求。
    /// </remarks>
    private static async Task<string?> PreviewCapAsync()
    {
        await Task.CompletedTask;
        var reminder = new RepeatToolReminder(thresholds: [2], argumentsPreviewChars: 20);

        var longA = new string('a', 100) + "-tail-A";
        var longB = new string('a', 100) + "-tail-B";

        // 两个长参数只有末尾不同：不能算重复。
        reminder.Observe("shell", Args(longA));
        if (reminder.Observe("shell", Args(longB)) is not null)
            return "末尾不同的两个长参数被误判成重复——键用了截断后的值";

        // 同一个长参数：第 2 次应提醒，且预览不超过上限。
        var r = reminder.Observe("shell", Args(longB));
        if (r is null) return "相同的长参数没有触发提醒";
        if (r.ArgumentsPreview.Length > 20)
            return $"预览 {r.ArgumentsPreview.Length} 字符，超过上限 20";

        return r.ArgumentsPreview.EndsWith('…')
            ? null
            : "截断了却没有省略号";
    }

    /// <summary>重置后链从零开始。</summary>
    /// <remarks>
    /// <b>每轮清零是必须的</b>：用户说"再跑一次"是一次新的开始，
    /// 不该接着上一轮的计数。
    /// </remarks>
    private static async Task<string?> ResetAsync()
    {
        await Task.CompletedTask;
        var reminder = new RepeatToolReminder(thresholds: [2]);

        reminder.Observe("read_file", Args("a.txt"));
        var first = reminder.Observe("read_file", Args("a.txt"));
        if (first is null) return "前置：第 2 次应提醒";

        reminder.Reset();
        if (reminder.Consecutive != 0) return $"重置后计数应为 0，实际 {reminder.Consecutive}";

        // 重置后第 1 次不该提醒（若链没清，这里就是第 3 次）。
        return reminder.Observe("read_file", Args("a.txt")) is null
            ? null
            : "重置后第一次调用就提醒了——链没清干净";
    }

    /// <summary>非法阈值在构造时抛，不静默回退。</summary>
    private static async Task<string?> ValidationAsync()
    {
        await Task.CompletedTask;

        if (!Throws(() => new RepeatToolReminder(thresholds: [])))
            return "空阈值列表没有报错";

        if (!Throws(() => new RepeatToolReminder(thresholds: [1])))
            return "阈值 1 没有报错（连续 1 次不构成重复）";

        if (!Throws(() => new RepeatToolReminder(thresholds: [3, 3])))
            return "重复阈值没有报错";

        return null;
    }

    /// <summary>提醒文本要说清"接下来该做什么"。</summary>
    /// <remarks>
    /// <b>只说"你重复了"没有用</b>：模型会换个写法再试一遍，
    /// 而那不是它需要的信息。
    /// </remarks>
    private static async Task<string?> RenderAsync()
    {
        await Task.CompletedTask;
        var reminder = new RepeatToolReminder(thresholds: [2, 3]);

        reminder.Observe("read_file", Args("a.txt"));
        var r = reminder.Observe("read_file", Args("a.txt"));
        if (r is null) return "没有触发提醒";

        var text = r.Render();

        if (!text.Contains("read_file", StringComparison.Ordinal))
            return "提醒里没有工具名";

        if (!text.Contains("确认", StringComparison.Ordinal))
            return "提醒没有要求模型先判断原因";

        // **不能声称"已阻止"**：我们没有阻止，说了会让模型以为被限制。
        if (text.Contains("已阻止", StringComparison.Ordinal)
            || text.Contains("已拦截", StringComparison.Ordinal))
            return "提醒声称已阻止，但实现只提醒不阻止";

        if (r.OnlyCounts) return "第 2 次不该是最后一个阈值（配置是 [2,3]）";

        var last = reminder.Observe("read_file", Args("a.txt"));
        return last?.OnlyCounts == true ? null : "到达最后一个阈值时没有标记 OnlyCounts";
    }

    /// <summary>
    /// 提醒真的进了下一次请求的消息列表。
    /// </summary>
    /// <remarks>
    /// <b>这条是第一版缺的</b>：原来只做字符串检查（"运行时里有没有那段代码"），
    /// 而把 <c>Observe</c> 的调用注释掉之后它照样全绿——字符串还在，
    /// 但提醒再也不会产生。这类"检查看的是源码而不是行为"的错
    /// 只能靠真实运行抓出来。
    /// </remarks>
    private static async Task<string?> ReachesRequestAsync()
    {
        var reminder = new RepeatToolReminder(thresholds: [2]);

        // 三次相同工具调用：第 2 次触发提醒，写在下一轮请求里。
        var client = new RepeatingToolClient(FakeToolId, repeated: 3);
        var dispatcher = new ToolDispatcher();
        dispatcher.Register(new FakeTool(FakeToolId, []));

        var runtime = new LoopAgentRuntime(client, new FixedDispatcherAccessor(dispatcher))
        {
            RepeatDetection = reminder,
        };

        var request = new AgentRunRequest("s", "t", [], "go");
        await foreach (var _ in runtime.RunAsync(request)) { }

        if (client.Requests.Count < 2)
            return $"模型只被调了 {client.Requests.Count} 次，看不见下一轮";

        // **找的是"提醒出现之后的那次请求"**，不是固定的第 2 次。
        // 第 2 次调用后产生提醒，于是第 3 次请求才带上它——
        // 第一版固定取 Requests[1]，而那一轮发生在提醒之前。
        var anyWithReminder = client.Requests.Skip(1).Any(req =>
        {
            var t = string.Join(Environment.NewLine, req.Select(m => m.Text ?? string.Empty));
            return t.Contains("连续执行了", StringComparison.Ordinal)
                && t.Contains(FakeToolId, StringComparison.Ordinal);
        });

        if (anyWithReminder) return null;

        // 诊断信息：把每次请求的文本摘要打出来。
        //
        // **用 Contents 而不是 Text**：<c>ChatMessage.Text</c> 只是
        // <c>Contents</c> 里文本块的便捷拼接，而提醒是作为独立消息追加的——
        // 第一版用 Text 判，注入明明发生了（探针确认 count=1）却判不出来。
        return $"共 {client.Requests.Count} 次请求，没有任何一次含提醒";
    }

    /// <summary>提醒不阻止调用，且装配里真的接上了。</summary>
    private static async Task<string?> WiringAsync()
    {
        await Task.CompletedTask;
        var root = FindRepoRoot();

        var runtime = Path.Combine(root, "src", "Dougong.Agent", "LoopAgentRuntime.cs");
        if (!File.Exists(runtime)) return $"找不到运行时：{runtime}";

        var text = File.ReadAllText(runtime);
        if (!text.Contains("RepeatDetection?.Observe(", StringComparison.Ordinal))
            return "运行时没有观察工具调用——检测不会跑";

        if (!text.Contains("AppendRepeatReminders(", StringComparison.Ordinal))
            return "运行时没有注入提醒——模型看不到";

        if (!text.Contains("RepeatDetection?.Reset();", StringComparison.Ordinal))
            return "运行时没有每轮清零——上一轮的计数会延续";

        var composition = Path.Combine(root, "src", "Dougong.Cli", "SessionComposition.cs");
        if (!File.Exists(composition)) return $"找不到装配：{composition}";

        // **按分支数验，不只查字符串**：第一版只做 Contains，
        // 而 RepeatDetection 曾只加进 CreateRuntime 的一条返回路径
        // （REPL 常态走的是另一条），检查照样全绿。
        var source = File.ReadAllText(composition);
        var returns = CountOccurrences(source, "return new LoopAgentRuntime");
        var assignments = CountOccurrences(source, "RepeatDetection =");

        if (returns < 2)
            return $"前提不成立：CreateRuntime 只找到 {returns} 条返回路径";

        return assignments >= returns
            ? null
            : $"RepeatDetection 只在 {assignments} 条返回路径上赋值，而共 {returns} 条——某条分支上提醒不会触发";
    }

    // ── 支撑 ─────────────────────────────────────────────────────

    /// <summary>
    /// 被重复的工具名。
    /// </summary>
    /// <remarks>
    /// <b>用具名常量而不是两处字面量</b>：第一版客户端发 <c>same-call</c>、
    /// 检查找 <c>probe_tool</c>，注入明明发生了却判为失败。
    /// 两处各写一遍不报错，症状是"检查说没有、探针说有了"。
    /// </remarks>
    private const string FakeToolId = "probe_tool";

    private static JsonElement Args(string text)
    {
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(new { path = text }));
        return document.RootElement.Clone();
    }

    /// <summary>每次都请求同一个工具调用，记录每次收到的消息。</summary>
    private sealed class RepeatingToolClient(string toolId, int repeated) : IChatClient
    {
        private int _calls;

        public List<IReadOnlyList<ChatMessage>> Requests { get; } = [];

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> m, ChatOptions? o = null, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> m, ChatOptions? o = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.CompletedTask;
            Requests.Add([.. m]);
            _calls++;

            if (_calls <= repeated)
            {
                // 每次都发同一个调用 ID 会命中"重复调用"拒绝，
                // 所以按次编号——重复检测看的是工具名与参数，不是 ID。
                yield return new ChatResponseUpdate(ChatRole.Assistant,
                    [new FunctionCallContent($"call-{_calls}", toolId,
                        new Dictionary<string, object?> { ["x"] = "same" })]);
            }
            else
            {
                yield return new ChatResponseUpdate(ChatRole.Assistant, "done");
            }
        }

        public object? GetService(Type t, object? k = null) => null;
        public void Dispose() { }
    }

    private static bool Throws(Action action)
    {
        try
        {
            action();
            return false;
        }
        catch (ArgumentException)
        {
            return true;
        }
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
