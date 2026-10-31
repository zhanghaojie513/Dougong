using System.Text.Json;
using Dougong.Agent;
using Microsoft.Extensions.AI;

namespace Dougong.Agent.Comparison;

/// <summary>
/// 系统提示词组装的验证。
/// </summary>
/// <remarks>
/// <b>重点不是"拼出了文字"，是三条容易被忽略的边界</b>：
/// <list type="bullet">
/// <item>没有注册某个工具时，对应纪律**不能出现**——否则模型会去调一个不存在的工具</item>
/// <item>同名分节要替换，不能并列——两段矛盾规则比没有规则更糟</item>
/// <item>总长要截断并**说出来**——静默截断让模型以为自己看全了</item>
/// <item>拼出来的提示必须**真的进到请求里**，不能只进 token 统计</item>
/// </list>
/// </remarks>
public static class PromptChecks
{
    public static async Task<int> RunAsync()
    {
        var failures = 0;
        failures += await CheckAsync("prompt-includes-workspace", WorkspaceAsync);
        failures += await CheckAsync("prompt-omits-discipline-for-missing-tools", MissingToolsAsync);
        failures += await CheckAsync("prompt-includes-edit-discipline-when-registered", EditDisciplineAsync);
        failures += await CheckAsync("prompt-same-name-replaces-not-duplicates", ReplaceDuplicateAsync);
        failures += await CheckAsync("prompt-truncates-and-says-so", TruncationAsync);
        failures += await CheckAsync("prompt-empty-when-no-sections-apply", EmptyAsync);
        failures += await CheckAsync("prompt-is-actually-sent", SentAsync);

        Console.WriteLine(failures == 0
            ? "PROMPT: ALL PASS"
            : $"PROMPT: {failures} FAILURE(S)");
        return failures;
    }

    /// <summary>工作区路径必须出现在提示里——否则模型不知道相对路径的根。</summary>
    private static async Task<string?> WorkspaceAsync()
    {
        var builder = new SystemPromptBuilder().Add(new WorkspaceSection());
        var prompt = await builder.BuildAsync(Context("/tmp/ws", []));

        if (prompt is null) return "没有产出任何提示";
        return prompt.Contains("/tmp/ws", StringComparison.Ordinal)
            ? null
            : $"提示里没有工作区路径：{prompt}";
    }

    /// <summary>
    /// 没有注册 glob/grep 时，不能告诉模型"先用 glob 定位"。
    /// </summary>
    /// <remarks>
    /// 这是本节最重要的一条：给了一条走不通的路，模型会先尝试、失败、
    /// 再猜，比不给提示更浪费。所以纪律必须由实际工具集决定。
    /// </remarks>
    private static async Task<string?> MissingToolsAsync()
    {
        var builder = new SystemPromptBuilder().Add(new FileToolPolicySection());

        // 只有 read_file：不该出现 glob / grep / edit_file。
        var prompt = await builder.BuildAsync(Context("/ws", ["read_file"]));

        if (prompt is null) return "只有 read_file 时提示为空——读文件纪律没给出";
        if (prompt.Contains("glob", StringComparison.Ordinal))
            return $"没注册 glob 却提到它：{prompt}";
        if (prompt.Contains("edit_file", StringComparison.Ordinal))
            return $"没注册 edit_file 却提到它：{prompt}";

        return null;
    }

    /// <summary>注册了 edit_file 就要给出"不要改用 write_file 重写全文"这条纪律。</summary>
    private static async Task<string?> EditDisciplineAsync()
    {
        var builder = new SystemPromptBuilder().Add(new FileToolPolicySection());
        var prompt = await builder.BuildAsync(
            Context("/ws", ["read_file", "write_file", "edit_file", "glob", "grep"]));

        if (prompt is null) return "注册了全部文件工具却没有任何提示";
        if (!prompt.Contains("edit_file", StringComparison.Ordinal))
            return $"注册了 edit_file 却没给它的纪律：{prompt}";

        return null;
    }

    /// <summary>同名分节替换而不是并列。</summary>
    private static async Task<string?> ReplaceDuplicateAsync()
    {
        var builder = new SystemPromptBuilder()
            .Add(new ConstantSection("dup", 100, "第一版"))
            .Add(new ConstantSection("dup", 100, "第二版"));

        var prompt = await builder.BuildAsync(Context("/ws", []));

        if (prompt is null) return "同名分节替换后没有内容";
        if (prompt.Contains("第一版", StringComparison.Ordinal))
            return $"旧分节没有被替换：{prompt}";
        return prompt.Contains("第二版", StringComparison.Ordinal)
            ? null
            : $"新分节不在提示里：{prompt}";
    }

    /// <summary>
    /// 超长要截断，且必须留下"还有没给出的部分"的说明。
    /// </summary>
    /// <remarks>
    /// 静默截断最坏：模型以为自己看全了规则，按不完整的纪律行动，
    /// 而人完全不知道它少看了什么。
    /// </remarks>
    private static async Task<string?> TruncationAsync()
    {
        var huge = new string('x', SystemPromptBuilder.MaxTotalChars);
        var builder = new SystemPromptBuilder()
            .Add(new ConstantSection("a", 100, huge))
            .Add(new ConstantSection("b", 200, "后面这一节应当被截掉"));

        var prompt = await builder.BuildAsync(Context("/ws", []));

        if (prompt is null) return "超长时没有产出提示";
        if (prompt.Length > SystemPromptBuilder.MaxTotalChars + 200)
            return $"没有截断，长度 {prompt.Length}";
        if (!prompt.Contains("超出长度上限", StringComparison.Ordinal))
            return "截断了但没说——模型会以为自己看全了";

        return null;
    }

    /// <summary>所有分节都不适用时返回 null，而不是空字符串。</summary>
    /// <remarks>
    /// 空字符串会让调用方以为"有系统提示"，然后发出一条空的 system 消息。
    /// 有些 provider 对空 system 消息会报错。
    /// </remarks>
    private static async Task<string?> EmptyAsync()
    {
        var builder = new SystemPromptBuilder()
            .Add(new ConstantSection("nothing", 100, "   "));

        var prompt = await builder.BuildAsync(Context("/ws", []));
        return prompt is null ? null : $"应当返回 null，实际是：'{prompt}'";
    }

    /// <summary>
    /// 系统提示必须真的出现在发给模型的消息列表里。
    /// </summary>
    /// <remarks>
    /// <b>这条钉的是一个真实缺陷</b>：此前 <c>SystemPrompt</c> 只被拿去算 token，
    /// 从未进入请求。于是上下文报告说"系统提示占 X token"，
    /// 而模型一个字都没收到——报告与真实请求不一致，
    /// 所有"该告诉模型什么"的分节也就都落不了地。
    /// <para>
    /// 验法是抓连接器实际收到的消息，而不是读运行时字段——
    /// 字段有值不等于发出去了，这个区别正是缺陷所在。
    /// </para>
    /// </remarks>
    private static async Task<string?> SentAsync()
    {
        var client = new CapturingChatClient();
        var runtime = new LoopAgentRuntime(client, new EmptyDispatcher())
        {
            PromptBuilder = new SystemPromptBuilder().Add(new WorkspaceSection()),
            MemoryWorkspace = "/sent-check",
        };

        await foreach (var _ in runtime.RunAsync(
            new AgentRunRequest("s1", "t1", [], "hello", MaxToolRounds: 1),
            CancellationToken.None))
        {
            // 只关心连接器收到了什么。
        }

        var messages = client.Captured;
        if (messages is null) return "连接器没有被调用";

        var system = messages.Where(m => m.Role == ChatRole.System).ToList();
        if (system.Count == 0) return "请求里没有 system 消息——系统提示没有发出去";
        if (!system[0].Text.Contains("/sent-check", StringComparison.Ordinal))
            return $"system 消息里没有工作区分节：{system[0].Text}";

        // 系统消息必须排在最前：放在历史之后会被当成某一轮的内容。
        if (messages[0].Role != ChatRole.System)
            return "system 消息不在最前";

        return null;
    }

    // ── 支撑 ─────────────────────────────────────────────────────

    private static SystemPromptContext Context(string workspace, string[] tools) =>
        new(workspace, tools);

    /// <summary>内容固定的分节，用于验证组装规则本身。</summary>
    private sealed class ConstantSection(string name, int order, string text) : ISystemPromptSection
    {
        public string Name => name;

        public int Order => order;

        public ValueTask<string?> BuildAsync(
            SystemPromptContext context, CancellationToken cancellationToken) =>
            ValueTask.FromResult<string?>(text);
    }

    /// <summary>记录收到的消息，供"提示是否真的发出去"检查。</summary>
    private sealed class CapturingChatClient : IChatClient
    {
        public IReadOnlyList<ChatMessage>? Captured { get; private set; }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            GetStreamingResponseAsync(messages, options, cancellationToken)
                .ToChatResponseAsync();

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation]
            CancellationToken cancellationToken = default)
        {
            Captured = [.. messages];
            await Task.Yield();
            yield return new ChatResponseUpdate(ChatRole.Assistant, "ok");
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }

    /// <summary>没有任何工具的空 dispatcher。</summary>
    /// <remarks>
    /// 用真实接口实现而不是假的扩展方法：<see cref="LoopAgentRuntime"/> 读的是
    /// <see cref="IToolDispatcher.Tools"/>，一个只实现"看起来够了"的假类型
    /// 会让检查在接口变化时仍然通过。
    /// </remarks>
    private sealed class EmptyDispatcher : IToolDispatcher
    {
        public IReadOnlyList<ToolDescriptor> Tools => [];

        public IDisposable Register(IAgentTool tool) =>
            throw new NotSupportedException("本检查不注册工具。");

        public ValueTask<ToolDispatchResult> DispatchAsync(
            ToolInvocation invocation, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("本检查不派发工具。");
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
