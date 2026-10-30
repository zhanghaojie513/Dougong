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
    public static async Task<int> RunAsync()
    {
        var failures = 0;
        failures += await CheckAsync("command-roundtrip-keeps-method", CommandRoundTripAsync);
        failures += await CheckAsync("event-roundtrip-keeps-sequence", EventRoundTripAsync);
        failures += await CheckAsync("item-roundtrip-keeps-kind", ItemRoundTripAsync);
        failures += await CheckAsync("all-derived-types-register-discriminator", AllRegisteredAsync);
        failures += await CheckAsync("unknown-method-is-explicitly-handled", UnknownMethodAsync);
        failures += await CheckAsync("shared-json-options-are-correct", SharedOptionsAsync);

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
            Item = new UserMessageItem { Id = "i1", Text = "hi" },
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
            new UserMessageItem { Id = "1", Text = "u" },
            new AssistantMessageItem { Id = "2", Text = "a" },
            new ReasoningItem { Id = "3", Text = "r" },
            new ToolCallItem { Id = "4", InvocationId = "c", ToolName = "shell" },
            new PlanItem { Id = "5", Steps = [new PlanStep("s", "pending", "high")] },
            new NoticeItem { Id = "6", Text = "n", Level = NoticeLevel.Warning },
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
    /// "宿主不得自己写一份"这条只能靠文档约束，测不了。但至少能断言配置本身：
    /// 枚举写字符串（抓包可读）、null 不写（省带宽且与"字段不存在"一致）。
    /// </remarks>
    private static Task<string?> SharedOptionsAsync()
    {
        if (HostProtocolContract.Json.DefaultIgnoreCondition != JsonIgnoreCondition.WhenWritingNull)
        {
            return Task.FromResult<string?>(
                "null 字段应当不写入：否则「字段不存在」与「字段是 null」在线上无法区分");
        }

        var notice = new NoticeItem { Id = "1", Text = "x", Level = NoticeLevel.Warning };
        var json = JsonSerializer.Serialize<ConversationItem>(notice, HostProtocolContract.Json);

        return Task.FromResult(json.Contains("\"level\":\"warning\"", StringComparison.Ordinal)
            ? null
            : $"枚举应写字符串（便于抓包定位）：{json}");
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
