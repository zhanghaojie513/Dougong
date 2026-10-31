using System.Text.Json;
using System.Text.Json.Serialization;
using Dougong.HostProtocol;

namespace Dougong.Agent.Comparison;

/// <summary>
/// 宿主协议的验证：多态序列化必须往返，且旧客户端遇到新判别器不崩。
/// </summary>
/// <remarks>
/// <b>契约包最容易错的地方不是类型定义，是序列化配置。</b>
/// 两侧各写一份 <see cref="JsonSerializerOptions"/> 是最常见的裂缝——
/// 一侧忽略 null、另一侧不忽略时，"字段不存在"与"字段是 null"
/// 在线上就无法区分。
/// </remarks>
public static class HostProtocolChecks
{
    /// <summary>
    /// 固定时刻，用于构造可比较的往返样本。
    /// </summary>
    /// <remarks>
    /// <b>必须是固定值而不是 <c>DateTimeOffset.Now</c></b>：
    /// 用"现在"构造样本时，往返前后必然差几毫秒，
    /// 于是"字段有没有保真"这件事被时间抖动掩盖——
    /// 检查会通过，但其实没验证任何东西。
    /// </remarks>
    private static readonly DateTimeOffset FixedAt =
        new(2026, 1, 2, 3, 4, 5, TimeSpan.FromHours(8));

    public static async Task<int> RunAsync()
    {
        var failures = 0;
        failures += await CheckAsync("command-roundtrip-keeps-method", CommandRoundTripAsync);
        failures += await CheckAsync("event-roundtrip-keeps-sequence", EventRoundTripAsync);
        failures += await CheckAsync("item-roundtrip-keeps-kind", ItemRoundTripAsync);
        failures += await CheckAsync("all-derived-types-register-discriminator", AllRegisteredAsync);
        failures += await CheckAsync("unknown-method-is-explicitly-handled", UnknownMethodAsync);
        failures += await CheckAsync("shared-json-options-are-correct", SharedOptionsAsync);

        // 缺字段时的行为：这两种默认值都会静默产生错误数据，必须钉住。
        failures += await CheckAsync("missing-required-field-fails-loudly", MissingRequiredAsync);
        failures += await CheckAsync("timestamp-is-not-defaulted-to-now", TimestampNotNowAsync);

        // 模型目录的载荷必须够客户端用：原先是残缺的，而且从未被验证。
        failures += await CheckAsync("models-listed-carries-providers", ModelsCarriesProvidersAsync);
        failures += await CheckAsync("models-listed-carries-current-model", ModelsCarriesCurrentAsync);

        // 从**真实线协议文本**解析，而不是从对象往返——
        // 对象往返验证的是"我们写的序列化配置"，构造时漏填它抓不到。
        failures += await CheckAsync("models-listed-parses-from-wire-text", ModelsParsesFromWireAsync);

        Console.WriteLine(failures == 0
            ? "HOST-PROTOCOL: ALL PASS"
            : $"HOST-PROTOCOL: {failures} FAILURE(S)");
        return failures;
    }

    /// <summary>命令往返后判别字段必须保留，否则服务端分不出是哪种命令。</summary>
    private static Task<string?> CommandRoundTripAsync()
    {
        ClientCommand original = new StartTurnCommand
        {
            RequestId = "r1",
            SessionId = "s1",
            Input = "hello",
        };

        var json = JsonSerializer.Serialize(original, HostProtocolContract.Json);
        if (!json.Contains("\"method\":\"turn/start\"", StringComparison.Ordinal))
            return Task.FromResult<string?>($"序列化后丢了 method 判别字段：{json}");

        var back = JsonSerializer.Deserialize<ClientCommand>(json, HostProtocolContract.Json);
        if (back is not StartTurnCommand start)
            return Task.FromResult<string?>($"反序列化得到 {back?.GetType().Name ?? "null"}");

        return Task.FromResult(start.Input == "hello" && start.SessionId == "s1"
            ? null
            : $"字段丢失：{start.Input}/{start.SessionId}");
    }

    /// <summary>事件往返后序号必须保留——它是缺口检测的唯一依据。</summary>
    private static Task<string?> EventRoundTripAsync()
    {
        ServerEvent original = new ItemAddedEvent
        {
            Sequence = 42,
            SessionId = "s1",
            TurnId = "t1",
            Item = new UserMessageItem { Id = "i1", At = FixedAt, Text = "hi" },
        };

        var json = JsonSerializer.Serialize(original, HostProtocolContract.Json);
        var back = JsonSerializer.Deserialize<ServerEvent>(json, HostProtocolContract.Json);

        if (back is not ItemAddedEvent added)
            return Task.FromResult<string?>($"反序列化得到 {back?.GetType().Name ?? "null"}");

        if (added.Sequence != 42)
            return Task.FromResult<string?>($"序号丢失：{added.Sequence}");

        // 嵌套的多态字段也必须能还原——这是双层多态，最容易漏。
        if (added.Item is not UserMessageItem user)
            return Task.FromResult<string?>($"嵌套条目丢了 kind：{added.Item?.GetType().Name}");

        return Task.FromResult(user.Text == "hi" ? null : $"嵌套条目内容丢失：{user.Text}");
    }

    /// <summary>六种条目都要能往返，且判别器不串。</summary>
    private static Task<string?> ItemRoundTripAsync()
    {
        ConversationItem[] items =
        [
            new UserMessageItem { Id = "1", At = FixedAt, Text = "u" },
            new AssistantMessageItem { Id = "2", At = FixedAt, Text = "a" },
            new ReasoningItem { Id = "3", At = FixedAt, Text = "r" },
            new ToolCallItem { Id = "4", At = FixedAt, InvocationId = "c", ToolName = "shell" },
            new PlanItem { Id = "5", At = FixedAt, Steps = [new PlanStep("s", "pending", "high")] },
            new NoticeItem { Id = "6", At = FixedAt, Text = "n", Level = NoticeLevel.Warning },
        ];

        foreach (var item in items)
        {
            var json = JsonSerializer.Serialize(item, HostProtocolContract.Json);
            var back = JsonSerializer.Deserialize<ConversationItem>(json, HostProtocolContract.Json);

            if (back?.GetType() != item.GetType())
            {
                return Task.FromResult<string?>(
                    $"{item.GetType().Name} 往返后变成 {back?.GetType().Name ?? "null"}");
            }
        }

        return Task.FromResult<string?>(null);
    }

    /// <summary>
    /// 每个具体派生类型都必须登记判别器——漏一个要到运行期才发现。
    /// </summary>
    /// <remarks>
    /// 这条防的是"加了新类型但忘记加 <c>[JsonDerivedType]</c>"。
    /// 那种错在编译期完全看不出来，而线上一序列化就抛异常。
    /// </remarks>
    private static Task<string?> AllRegisteredAsync()
    {
        var assembly = typeof(HostProtocolContract).Assembly;
        var problems = new List<string>();

        foreach (var type in assembly.GetExportedTypes())
        {
            if (type.IsAbstract || type.IsEnum) continue;

            var root =
                typeof(ClientCommand).IsAssignableFrom(type) ? typeof(ClientCommand)
                : typeof(ServerEvent).IsAssignableFrom(type) ? typeof(ServerEvent)
                : typeof(ConversationItem).IsAssignableFrom(type) ? typeof(ConversationItem)
                : null;

            if (root is null) continue;

            if (DiscriminatorFor(root, type) is null)
                problems.Add(type.Name);
        }

        return Task.FromResult(problems.Count == 0
            ? null
            : $"这些类型没有在基类上登记 [JsonDerivedType]：{string.Join("、", problems)}");
    }

    /// <summary>从基类的多态配置里查某个具体类型的判别器。</summary>
    private static string? DiscriminatorFor(Type root, Type concrete)
    {
        var attrs = root
            .GetCustomAttributes(typeof(JsonDerivedTypeAttribute), inherit: false)
            .Cast<JsonDerivedTypeAttribute>();

        foreach (var item in attrs)
        {
            if (item.DerivedType == concrete) return item.TypeDiscriminator?.ToString();
        }

        return null;
    }

    /// <summary>
    /// 不认识的 method 必须**明确失败**，不能静默变成 null。
    /// </summary>
    /// <remarks>
    /// <b>为什么必须测这个</b>：旧客户端收到新事件是常态（新服务端 + 旧客户端）。
    /// 如果反序列化静默给出 null，客户端会当成"没有事件"——表现为界面卡住
    /// 而日志里什么都没有，这种问题最难查。
    /// </remarks>
    private static Task<string?> UnknownMethodAsync()
    {
        const string json = """{"method":"future/unknown","sequence":1}""";

        try
        {
            var result = JsonSerializer.Deserialize<ServerEvent>(json, HostProtocolContract.Json);
            return Task.FromResult<string?>(
                $"不认识的判别器没有抛异常，而是给出了 {result?.GetType().Name ?? "null"}");
        }
        catch (JsonException)
        {
            // 预期：明确失败。
            return Task.FromResult<string?>(null);
        }
    }

    /// <summary>
    /// 序列化配置必须由契约包独家提供，且配置本身要对。
    /// </summary>
    /// <remarks>
    /// <summary>
    /// 缺必需字段必须明确失败，不能静默补一个默认值。
    /// </summary>
    /// <remarks>
    /// <b>这是跨语言移植最容易漏的一条</b>：两端都能反序列化、
    /// 都能跑完流程，但缺字段时一边抛错一边补默认值，
    /// 于是"看起来正常"的那一侧把错误数据带进了会话。
    /// <para>
    /// <c>required</c> 让缺失变成 <see cref="JsonException"/>。
    /// 这比"默认值凑合"严格，但协议字段不是可有可无的装饰——
    /// 少了 <c>sessionId</c> 的事件路由不到会话，凭空补一个只会更糟。
    /// </para>
    /// </remarks>
    private static Task<string?> MissingRequiredAsync()
    {
        // 每种都缺一个必需字段。
        (string Label, string Json, Type Type)[] cases =
        [
            ("事件缺 sessionId", """{"method":"turn/started","turnId":"t"}""", typeof(ServerEvent)),
            ("事件缺 snapshot", """{"method":"session/opened"}""", typeof(ServerEvent)),
            ("条目缺 id", """{"kind":"assistantMessage","at":"2026-01-02T03:04:05+08:00"}""",
                typeof(ConversationItem)),
            ("命令缺 sessionId", """{"method":"session/open"}""", typeof(ClientCommand)),
        ];

        foreach (var (label, json, type) in cases)
        {
            try
            {
                var parsed = JsonSerializer.Deserialize(json, type, HostProtocolContract.Json);
                if (parsed is not null)
                    return Task.FromResult<string?>(
                        $"{label}：缺必需字段却成功反序列化成了 {parsed.GetType().Name}");
            }
            catch (JsonException)
            {
                // 期望路径。
            }
        }

        return Task.FromResult<string?>(null);
    }

    /// <summary>
    /// 时间戳缺失时不能默认取"现在"。
    /// </summary>
    /// <remarks>
    /// <b>这曾经是真实缺陷</b>：<c>ConversationItem.At</c> 有
    /// <c>= DateTimeOffset.Now</c> 的默认值。缺字段时两端各自取自己的"现在"——
    /// 客户端拿到的是收包时刻，不是产生时刻。跨进程时分叉立刻可见：
    /// 两条消息的时间戳可能颠倒，界面按时间排序就错序，而且**没有任何报错**。
    /// <para>
    /// 换成 <c>required</c> 后这里应当抛 <see cref="JsonException"/>。
    /// 检查同时验证"没有退回去用 Now"——把默认值改回来会让它失败。
    /// </para>
    /// </remarks>
    private static Task<string?> TimestampNotNowAsync()
    {
        const string missingAt = """{"kind":"assistantMessage","id":"i1","text":"x"}""";

        try
        {
            var parsed = JsonSerializer.Deserialize<ConversationItem>(
                missingAt, HostProtocolContract.Json);

            if (parsed is not null)
            {
                return Task.FromResult<string?>(
                    $"缺 at 字段却反序列化成功了，At={parsed.At:O}"
                    + "——说明仍在用本地当前时间兜底，跨进程会静默错序");
            }
        }
        catch (JsonException)
        {
            // 期望路径：明确失败。
        }

        return Task.FromResult<string?>(null);
    }

    private static Task<string?> SharedOptionsAsync()
    {
        if (HostProtocolContract.Json.DefaultIgnoreCondition != JsonIgnoreCondition.WhenWritingNull)
        {
            return Task.FromResult<string?>(
                "null 字段应当不写入：否则「字段不存在」与「字段是 null」在线上无法区分");
        }

        var notice = new NoticeItem { Id = "1", At = FixedAt, Text = "x", Level = NoticeLevel.Warning };
        var json = JsonSerializer.Serialize<ConversationItem>(notice, HostProtocolContract.Json);

        return Task.FromResult(json.Contains("\"level\":\"warning\"", StringComparison.Ordinal)
            ? null
            : $"枚举应写字符串（便于抓包定位）：{json}");
    }

    /// <summary>
    /// 模型目录必须带上 provider 清单。
    /// </summary>
    /// <remarks>
    /// <b>这条钉的是一个"有类型但没被验证"的缺口</b>：<c>ModelsListedEvent</c>
    /// 在 SDK 里只登记过判别器，从未被构造或消费，于是它缺
    /// <c>Providers</c> 字段这件事一直没人发现。
    /// <para>
    /// 该字段不是界面偏好：一个 provider 可能因为凭据未配而没有模型，
    /// 但它仍需出现在"选一个 provider"的下拉里。从 Models 反推会漏掉它。
    /// </para>
    /// </remarks>
    private static Task<string?> ModelsCarriesProvidersAsync()
    {
        var payload = new ModelsListedEvent
        {
            Sequence = 1,
            Models = [new ModelInfo("anthropic", "claude-opus-4-6", 1_000_000, 32_000, true)],
            Providers = ["anthropic", "openai"],
            CurrentModelId = "claude-opus-4-6",
        };

        var json = JsonSerializer.Serialize<ServerEvent>(payload, HostProtocolContract.Json);

        // 往返必须保住这两个字段——它们不在 Models 里，序列化配置漏一个就丢。
        var back = JsonSerializer.Deserialize<ServerEvent>(json, HostProtocolContract.Json)
            as ModelsListedEvent;

        if (back is null) return Task.FromResult<string?>("反序列化结果不是 ModelsListedEvent");

        // Providers 是必需字段：缺了会抛 JsonException（与 At 同样的策略）。
        try
        {
            _ = JsonSerializer.Deserialize<ServerEvent>(
                """{"method":"model/listed","sequence":1,"models":[]}""",
                HostProtocolContract.Json);
            return Task.FromResult<string?>(
                "缺 Providers 却反序列化成功了——客户端会把「没有 provider」"
                + "与「服务端没给」混为一谈");
        }
        catch (JsonException)
        {
            // 期望路径。
        }

        // openai 没有模型，仍必须在清单里——这正是不能从 Models 反推的理由。
        if (!back.Providers.Contains("openai", StringComparer.Ordinal))
            return Task.FromResult<string?>($"没有模型的 provider 被丢了：{string.Join(",", back.Providers)}");

        return Task.FromResult<string?>(null);
    }

    /// <summary>
    /// 从线协议原文解析模型目录，字段一个都不能丢。
    /// </summary>
    /// <remarks>
    /// <b>为什么不能只做对象往返</b>：往返是"自己构造、自己验证"，
    /// 验证的是序列化配置对称。而真实风险在**服务端构造时漏填**——
    /// 那由 <c>required</c> 在编译期拦，往返检查碰不到。
    /// <para>
    /// 这条用**手写的 JSON 文本**当输入：它代表一个真实的线上报文，
    /// 与我们的对象构造代码完全独立。若字段被漏在序列化配置之外、
    /// 或被 <c>WhenWritingNull</c> 吞掉，这里会暴露。
    /// </para>
    /// </remarks>
    private static Task<string?> ModelsParsesFromWireAsync()
    {
        // 手写报文：注意 openai 没有模型，但它必须在 providers 里。
        const string wire = """
            {
              "method": "model/listed",
              "sequence": 7,
              "models": [
                {"providerId":"anthropic","id":"claude-opus-4-6",
                 "contextWindow":1000000,"maxOutputTokens":32000,"supportsReasoning":true}
              ],
              "providers": ["anthropic","openai"],
              "currentModelId": "claude-opus-4-6"
            }
            """;

        ModelsListedEvent? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<ServerEvent>(wire, HostProtocolContract.Json)
                as ModelsListedEvent;
        }
        catch (Exception ex)
        {
            return Task.FromResult<string?>($"线协议报文解析失败：{ex.GetType().Name}: {ex.Message}");
        }

        if (parsed is null) return Task.FromResult<string?>("没有解析成 ModelsListedEvent");
        if (parsed.Sequence != 7) return Task.FromResult<string?>($"序号丢了：{parsed.Sequence}");
        if (parsed.Models.Count != 1) return Task.FromResult<string?>($"模型数不对：{parsed.Models.Count}");

        if (!parsed.Providers.SequenceEqual(["anthropic", "openai"], StringComparer.Ordinal))
            return Task.FromResult<string?>($"providers 不对：{string.Join(",", parsed.Providers)}");

        if (parsed.CurrentModelId != "claude-opus-4-6")
            return Task.FromResult<string?>($"currentModelId 不对：{parsed.CurrentModelId}");

        // 模型自身的字段也要完整——ModelInfo 是 positional record，
        // 少一个参数会在构造处暴露，但反序列化走的是属性映射，可能静默取默认值。
        var model = parsed.Models[0];
        if (model.ContextWindow != 1_000_000 || model.MaxOutputTokens != 32_000 || !model.SupportsReasoning)
            return Task.FromResult<string?>(
                $"ModelInfo 字段被静默填了默认值：{model.ContextWindow}/{model.MaxOutputTokens}/{model.SupportsReasoning}");

        return Task.FromResult<string?>(null);
    }

    /// <summary>模型目录必须能标出当前在用的模型。</summary>
    /// <remarks>
    /// 没有它，界面上所有模型看起来一样，用户不知道自己正在用哪个。
    /// </remarks>
    private static Task<string?> ModelsCarriesCurrentAsync()
    {
        var payload = new ModelsListedEvent
        {
            Sequence = 2,
            Models = [new ModelInfo("openai", "gpt-5", 400_000, 128_000, true)],
            Providers = ["openai"],
            CurrentModelId = "gpt-5",
        };

        var json = JsonSerializer.Serialize<ServerEvent>(payload, HostProtocolContract.Json);
        var back = JsonSerializer.Deserialize<ServerEvent>(json, HostProtocolContract.Json)
            as ModelsListedEvent;

        if (back?.CurrentModelId is null)
            return Task.FromResult<string?>($"CurrentModelId 在往返中丢失：{json}");

        return Task.FromResult<string?>(back.CurrentModelId == "gpt-5"
            ? null
            : $"CurrentModelId 变了：{back.CurrentModelId}");
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
