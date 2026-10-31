using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Dougong.HostProtocol;

namespace Dougong.Agent.Comparison;

/// <summary>
/// 宿主扩展多态类型的验收。
/// </summary>
/// <remarks>
/// <b>可观察面</b>：
/// <list type="number">
/// <item>宿主登记的类型能序列化出正确的判别器、也能反序列化回原类型。</item>
/// <item><b>原有的 SDK 类型不受影响</b>——追加不能覆盖 <c>[JsonDerivedType]</c> 特性上的登记。</item>
/// <item><b>冻结后登记要抛异常</b>，不能静默忽略。这是本项最要紧的一条：
/// 静默忽略会让宿主以为注册生效，然后对着"消息发不出去"排查半天。</item>
/// <item><b>基类不匹配要抛异常</b>——反射猜基类会静默挂错地方。</item>
/// </list>
/// </remarks>
public static class HostProtocolExtensionChecks
{
    /// <summary>宿主自定义的命令，模拟桌面端的 fs/list。</summary>
    private sealed record ListDirectoryCommand : ClientCommand
    {
        public required string Path { get; init; }
    }

    /// <summary>宿主自定义的事件。</summary>
    private sealed record DirectoryListedEvent : ServerEvent
    {
        public required string[] Entries { get; init; }
    }

    /// <summary>冻结后才尝试登记的类型，用于验证冻结检查。</summary>
    private sealed record LateCommand : ClientCommand
    {
        public required string Value { get; init; }
    }

    /// <summary>与 ClientCommand 无关的类型，用于验证基类检查。</summary>
    private sealed record NotACommand
    {
        public required string Value { get; init; }
    }

    public static Task<int> RunAsync()
    {
        var failures = 0;

        // **顺序有讲究**：参数校验与去重逻辑必须在触发序列化之前跑。
        // 一旦序列化发生，Json 冻结，RegisterDerivedType 只会抛"必须提前调用"，
        // 那时这几条测的就不是它们名字说的东西了。
        //
        // 这个顺序我是踩过之后才排对的：最初把 wrong-base / duplicate
        // 排在 roundtrip 之后，两者都报"必须在首次序列化之前调用"——
        // 看起来像功能坏了，实际是检查顺序错了。
        failures += Check("registering-wrong-base-throws", WrongBaseThrows);
        failures += Check("duplicate-registration-keeps-last", DuplicateKeepsLast);

        // 以下几条会触发序列化，此后 Json 冻结。
        failures += Check("host-type-roundtrips", HostTypeRoundTrips);
        failures += Check("extension-does-not-drop-sdk-registrations", SdkTypesSurvive);
        failures += Check("registered-extensions-are-observable", ExtensionsObservable);
        failures += Check("registering-after-freeze-throws", RegisterAfterFreezeThrows);

        Console.WriteLine(failures == 0
            ? "HOST-PROTOCOL-EXTENSION: ALL PASS"
            : $"HOST-PROTOCOL-EXTENSION: {failures} FAILURE(S)");
        return Task.FromResult(failures);
    }

    /// <summary>宿主类型必须能双向往返，判别器正确。</summary>
    /// <remarks>
    /// <b>这是本功能存在的理由</b>：没有它，宿主只能自建一份
    /// <see cref="JsonSerializerOptions"/>，而那会绕开"同一份配置"这条。
    /// </remarks>
    private static string? HostTypeRoundTrips()
    {
        EnsureRegistered();

        var original = new ListDirectoryCommand { RequestId = "r1", Path = "src/" };
        var json = JsonSerializer.Serialize<ClientCommand>(original, HostProtocolContract.Json);

        if (!json.Contains("\"method\":\"fs/list\"", StringComparison.Ordinal))
            return $"判别器不对：{json}";

        var back = JsonSerializer.Deserialize<ClientCommand>(json, HostProtocolContract.Json);
        if (back is not ListDirectoryCommand typed)
            return $"反序列化成了 {back?.GetType().Name ?? "null"}，不是宿主类型";

        return typed.Path == "src/" ? null : $"字段丢失：Path={typed.Path}";
    }

    /// <summary>
    /// 追加扩展不能丢掉 SDK 自己的 <c>[JsonDerivedType]</c> 登记。
    /// </summary>
    /// <remarks>
    /// <b>这是最容易写错的一处</b>：如果在修饰器里新建
    /// <c>JsonPolymorphismOptions</c> 去替换，特性上的登记会全部消失。
    /// 表现出来是"所有原有命令都发不出去"，而那与扩展功能毫无关系。
    /// 必须在已存在的对象上追加。
    /// </remarks>
    private static string? SdkTypesSurvive()
    {
        EnsureRegistered();

        // 挑几个不同多态根上的原有类型，逐一验证。
        var turn = new StartTurnCommand { RequestId = "r2", SessionId = "s", Input = "hi" };
        var turnJson = JsonSerializer.Serialize<ClientCommand>(turn, HostProtocolContract.Json);
        if (!turnJson.Contains("\"method\":\"turn/start\"", StringComparison.Ordinal))
            return $"SDK 的 StartTurnCommand 判别器丢了：{turnJson}";

        var evt = new TurnStartedEvent { Sequence = 1, SessionId = "s", TurnId = "t" };
        var evtJson = JsonSerializer.Serialize<ServerEvent>(evt, HostProtocolContract.Json);
        if (!evtJson.Contains("\"method\":\"turn/started\"", StringComparison.Ordinal))
            return $"SDK 的 TurnStartedEvent 判别器丢了：{evtJson}";

        var item = new NoticeItem { Id = "i", At = DateTimeOffset.UnixEpoch, Text = "x" };
        var itemJson = JsonSerializer.Serialize<ConversationItem>(item, HostProtocolContract.Json);
        if (!itemJson.Contains("\"kind\":\"notice\"", StringComparison.Ordinal))
            return $"SDK 的 NoticeItem 判别器丢了：{itemJson}";

        // 反序列化也要能走通。
        var back = JsonSerializer.Deserialize<ClientCommand>(turnJson, HostProtocolContract.Json);
        return back is StartTurnCommand ? null : $"SDK 类型反序列化失败：{back?.GetType().Name ?? "null"}";
    }

    /// <summary>
    /// 首次序列化之后登记必须抛异常，不能静默忽略。
    /// </summary>
    /// <remarks>
    /// <b>这条是本项最要紧的</b>。宿主的真实事故是：定义了配置、忘了用它，
    /// 编译过、检查过、运行时消息发不出去，而 dump 里看不出原因
    /// （模型目录一直是空的）。静默忽略会让同类事故再发生一次。
    /// <para>
    /// 验法：用另一个 <see cref="JsonSerializerOptions"/> 复现冻结后的状态。
    /// 直接对 <c>HostProtocolContract.Json</c> 做会污染其他检查，
    /// 所以这里测的是"冻结检查这条逻辑"，通过一个独立的 options 复现。
    /// </para>
    /// </remarks>
    private static string? RegisterAfterFreezeThrows()
    {
        // 这条必须测**真实的** RegisterDerivedType，不能另造一个 options 复现。
        //
        // 我第一版就是另造了一个 frozen 实例去测 TypeInfoResolver，
        // 结果删掉 RegisterDerivedType 里的冻结检查后检查依然全绿——
        // 它测的是 System.Text.Json 自己的行为，不是我们的防线。
        // 这正是"检查跑绿但没在验证"的典型。
        if (!HostProtocolContract.Json.IsReadOnly)
        {
            // 前提不成立说明前面的检查没有触发过序列化，顺序错了。
            return "本条要求 Json 已冻结（应由前面的序列化检查触发），当前未冻结";
        }

        try
        {
            HostProtocolContract.RegisterDerivedType<ClientCommand>(
                new JsonDerivedType(typeof(LateCommand), "fs/late"));

            return "冻结后登记没有抛异常——宿主会以为注册生效了，"
                + "然后对着「消息发不出去」排查半天，而根因是注册根本没进去";
        }
        catch (InvalidOperationException ex) when (
            ex.Message.Contains("首次序列化之前", StringComparison.Ordinal))
        {
            return null;
        }
    }

    /// <summary>基类不匹配时抛异常，而不是挂错地方。</summary>
    private static string? WrongBaseThrows()
    {
        // 这条要在冻结之前测——但它一旦执行就会登记进去，
        // 所以用一个"无论成功与否都不影响其他检查"的类型。
        try
        {
            HostProtocolContract.RegisterDerivedType<ClientCommand>(
                new JsonDerivedType(typeof(NotACommand), "not/a/command"));
            return "类型与基类无关却登记成功——反射猜基类会静默挂错地方";
        }
        catch (InvalidOperationException ex) when (
            ex.Message.Contains("派生类型", StringComparison.Ordinal))
        {
            return null;
        }
    }

    /// <summary>宿主能读到自己的登记，用于自检。</summary>
    /// <remarks>
    /// 这条直接针对"定义了但没用"那类事故：宿主可以断言
    /// "我注册的类型在里面"，而不是靠现象猜。
    /// </remarks>
    private static string? ExtensionsObservable()
    {
        EnsureRegistered();

        var registered = HostProtocolContract.RegisteredExtensions;
        if (registered.Count == 0)
            return "登记表为空——宿主无法自检";

        var hasCommand = registered.Any(entry =>
            entry.BaseType == typeof(ClientCommand)
            && entry.Derived.DerivedType == typeof(ListDirectoryCommand));

        if (!hasCommand)
            return "已登记的命令类型不在清单里";

        var hasEvent = registered.Any(entry =>
            entry.BaseType == typeof(ServerEvent)
            && entry.Derived.DerivedType == typeof(DirectoryListedEvent));

        return hasEvent ? null : "已登记的事件类型不在清单里";
    }

    /// <summary>同一类型重复登记时，以最后一次为准，不产生两条规则。</summary>
    /// <remarks>
    /// 宿主可能重建配置（热重载、测试里重复初始化）。两条规则并存时
    /// JSON 只认第一个匹配的，于是"改了判别器却没生效"会成为新的谜题。
    /// </remarks>
    private static string? DuplicateKeepsLast()
    {
        EnsureRegistered();

        // 同一类型第二次登记，换个判别器。
        HostProtocolContract.RegisterDerivedType<ServerEvent>(
            new JsonDerivedType(typeof(DirectoryListedEvent), "fs/listed/v2"));

        var count = HostProtocolContract.RegisteredExtensions.Count(entry =>
            entry.Derived.DerivedType == typeof(DirectoryListedEvent));
        if (count != 1)
            return $"同一类型登记了 {count} 次，应只保留一条";

        var json = JsonSerializer.Serialize<ServerEvent>(
            new DirectoryListedEvent { Sequence = 2, Entries = ["a"] },
            HostProtocolContract.Json);

        return json.Contains("\"method\":\"fs/listed/v2\"", StringComparison.Ordinal)
            ? null
            : $"重复登记没有以最后一次为准：{json}";
    }

    /// <summary>幂等地登记本套件需要的类型。</summary>
    private static void EnsureRegistered()
    {
        // 冻结后调用会抛，那时说明前面的检查已经触发过序列化——
        // 直接跳过，让可观察的那几条去报告问题。
        if (HostProtocolContract.Json.IsReadOnly) return;

        HostProtocolContract.RegisterDerivedType<ClientCommand>(
            new JsonDerivedType(typeof(ListDirectoryCommand), "fs/list"));

        HostProtocolContract.RegisterDerivedType<ServerEvent>(
            new JsonDerivedType(typeof(DirectoryListedEvent), "fs/listed"));
    }

    private static int Check(string name, Func<string?> action)
    {
        try
        {
            var failure = action();
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
