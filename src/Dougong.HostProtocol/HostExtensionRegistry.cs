using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace Dougong.HostProtocol;

/// <summary>
/// 宿主扩展的多态类型登记表。
/// </summary>
/// <remarks>
/// <b>存在的理由</b>：<c>[JsonDerivedType]</c> 必须挂在基类上，而基类在本程序集里。
/// 宿主想给 <see cref="ClientCommand"/> 加自己的命令（<c>fs/list</c>）
/// 时改不了它。没有这个登记表，宿主只有两条路：放弃加消息，
/// 或自建一套 <see cref="System.Text.Json.JsonSerializerOptions"/>——
/// 而后者正是"两侧配置不一致"这类问题的入口。
///
/// <para>
/// <b>为什么不用 <c>params JsonDerivedType[]</c></b>：那个签名不带基类信息，
/// SDK 只能反射猜 <c>DerivedType.BaseType</c>。而"猜"在这里是危险的：
/// 猜错的表现是静默的——类型挂到了错误的多态根上，序列化时它不走、
/// 反序列化时它不被识别，而两端都不报错。基类必须由调用方显式给出。
/// </para>
///
/// <para>
/// <b>登记必须在首次序列化之前</b>：<see cref="System.Text.Json.JsonSerializerOptions"/>
/// 一旦被使用就冻结，之后改 <see cref="System.Text.Json.JsonSerializerOptions.TypeInfoResolver"/>
/// 会抛 <see cref="InvalidOperationException"/>。这里把那条异常换成
/// 说人话的版本，见 <see cref="HostProtocolContract.RegisterDerivedType{TBase}"/>。
/// </para>
///
/// <para>
/// <b>扩展口是逃生舱，不是常规通道</b>：宿主自定义的消息只有那个宿主认识，
/// 别的客户端读到会忽略。若某个扩展类型变得通用（两个以上宿主都需要），
/// 它应当被收进本程序集的契约里，而不是各自注册一遍。
/// </para>
/// </remarks>
internal static class HostExtensionRegistry
{
    private static readonly List<(Type Base, JsonDerivedType Derived)> Entries = [];

    /// <summary>已登记的扩展，供自检与诊断。</summary>
    internal static IReadOnlyList<(Type Base, JsonDerivedType Derived)> Registered
    {
        get
        {
            lock (Entries) return [.. Entries];
        }
    }

    /// <summary>追加一条登记；同一 (基类, 派生类型) 只保留最后一次。</summary>
    internal static void Add(Type baseType, JsonDerivedType derived)
    {
        lock (Entries)
        {
            // 同一派生类型重复登记时以最后一次为准：宿主可能在重建
            // 自己的配置（热重载、测试里重复初始化），保留旧判别器
            // 会让两条规则同时存在，而 JSON 只认第一个匹配的。
            Entries.RemoveAll(entry =>
                entry.Base == baseType
                && entry.Derived.DerivedType == derived.DerivedType);

            Entries.Add((baseType, derived));
        }
    }

    /// <summary>
    /// 把已登记的类型挂到对应多态根上。
    /// </summary>
    /// <remarks>
    /// <b>追加而不是替换 <c>PolymorphismOptions</c></b>：基类上的
    /// <c>[JsonDerivedType]</c> 特性已经建立了这个对象，替换会丢掉 SDK 自己的登记。
    /// 只有宿主是第一个给这个根加类型时（<c>PolymorphismOptions</c> 为 null）
    /// 才需要新建它，而那时要从类型上读出判别器属性名，不能硬编码——
    /// 命令用 <c>method</c>、条目用 <c>kind</c>。
    /// </remarks>
    internal static void Apply(JsonTypeInfo typeInfo)
    {
        var matches = Registered
            .Where(entry => entry.Base == typeInfo.Type)
            .ToList();

        if (matches.Count == 0) return;

        // 已存在就复用：特性登记在里面，替换会把 SDK 自己的类型丢掉。
        var options = typeInfo.PolymorphismOptions ?? CreateEmpty(typeInfo.Type);

        foreach (var (_, derived) in matches)
        {
            var already = options.DerivedTypes.Any(existing =>
                existing.DerivedType == derived.DerivedType);
            if (!already) options.DerivedTypes.Add(derived);
        }

        typeInfo.PolymorphismOptions = options;
    }

    /// <summary>为还没有多态配置的类型造一个，判别器属性名从特性上取。</summary>
    private static JsonPolymorphismOptions CreateEmpty(Type type)
    {
        // 从特性的命名里读出判别器属性名。读不到时退回 null，
        // 由 System.Text.Json 用自己的默认规则——比硬编码一个错的名字安全。
        var attribute = type
            .GetCustomAttributes(typeof(JsonPolymorphicAttribute), inherit: false)
            .OfType<JsonPolymorphicAttribute>()
            .FirstOrDefault();

        return new JsonPolymorphismOptions
        {
            TypeDiscriminatorPropertyName = attribute?.TypeDiscriminatorPropertyName,
        };
    }
}
