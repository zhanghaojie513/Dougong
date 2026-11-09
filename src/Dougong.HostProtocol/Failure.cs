namespace Dougong.HostProtocol;

// ─────────────────────────────────────────────────────────────
// 命令失败：一条命令没能被处理的统一表达
// ─────────────────────────────────────────────────────────────

/// <summary>
/// 一条命令没能被处理。
/// </summary>
/// <remarks>
/// <b>为什么必须有它，而不是"失败时什么都不发"</b>：不发的话，客户端只能靠超时
/// 判断失败——用户看到界面转圈直到超时，而真正的原因（"这个功能还没实现"）
/// 永远不会显示出来。这是"看起来能用、实际不生效"的典型形态。
///
/// <para>
/// <b>它不是异常，是正常响应</b>：协议是消息流，没有"抛异常"这个动作。
/// 所以失败也是事件，带 <see cref="ServerEvent.RequestId"/> 与原来的命令配对。
/// </para>
///
/// <para>
/// <b>为什么要区分 <see cref="CommandFailureReason.Unsupported"/> 与其它原因</b>：
/// "这个版本还没实现"和"这次执行出错"对客户端的含义完全不同——
/// 前者应该隐藏该功能或置灰，后者应该显示错误并允许重试。
/// 合成一个字符串会逼客户端去匹配文案，那是脆弱的。
/// </para>
/// </remarks>
public sealed record CommandFailedEvent : ServerEvent
{
    /// <summary>失败原因的分类。</summary>
    public required CommandFailureReason Reason { get; init; }

    /// <summary>
    /// 给人看的一句话原因。
    /// </summary>
    /// <remarks>
    /// <b>必须写清"怎么办"，不能只说"失败了"</b>：例如
    /// "这个版本不支持 turn/cancel，请升级服务端"比 "unsupported" 有用得多。
    /// </remarks>
    public required string Message { get; init; }

    /// <summary>
    /// 原始命令的判别器（例如 <c>turn/cancel</c>）。
    /// </summary>
    /// <remarks>
    /// <b>客户端据它决定隐藏哪个入口</b>。只靠 <see cref="ServerEvent.RequestId"/>
    /// 也能配对上，但客户端那时可能已经忘了自己发过什么
    /// （尤其在它并发发了多条命令时）。
    ///
    /// <para>
    /// <b>序列化成 <c>failedMethod</c> 而不是 <c>method</c></b>：
    /// 多态判别器已经占了 <c>method</c> 这个属性名，同名会直接抛
    /// <see cref="InvalidOperationException"/>（"conflicts with an existing
    /// metadata property name"）。这个冲突在类型编译时看不出来，
    /// 只在第一次序列化时才炸——所以检查必须真的走一遍序列化。
    /// </para>
    /// </remarks>
    [System.Text.Json.Serialization.JsonPropertyName("failedMethod")]
    public string? Method { get; init; }
}

/// <summary>命令失败的原因分类。</summary>
public enum CommandFailureReason
{
    /// <summary>
    /// 这个服务端版本不支持该命令。
    /// </summary>
    /// <remarks>
    /// <b>正确反应是隐藏或置灰对应入口，不是重试</b>。重试永远不会成功。
    /// </remarks>
    Unsupported,

    /// <summary>参数不对（缺字段、引用了不存在的 id）。</summary>
    Invalid,

    /// <summary>服务端当前状态不允许（例如还没打开会话就发回合）。</summary>
    Conflict,

    /// <summary>执行过程中出错。可以重试，但要先显示原因。</summary>
    Failed,
}
