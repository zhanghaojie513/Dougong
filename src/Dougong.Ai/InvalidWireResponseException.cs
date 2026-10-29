namespace Dougong.Ai;

/// <summary>
/// 响应体不符合协议，但 HTTP 状态码是成功的。
/// </summary>
/// <remarks>
/// <b>为什么需要专用类型</b>：<c>InvalidOperationException</c> 太泛——
/// 它也可能是我们自己的逻辑错误。用类型区分后，宿主与分类器都能确定
/// "这是协议层面的问题"，而不用去匹配消息文本。
/// <para>
/// <b>这个异常存在的意义是填补一个静默失败</b>：HTTP 200 之后如果响应体
/// 解析不出内容，早先的实现会静默结束、产出零个 update，账目记成成功。
/// 用户看到"成功但没有任何内容"，无从判断是模型没说话还是协议出了问题。
/// </para>
/// </remarks>
public sealed class InvalidWireResponseException : InvalidOperationException
{
    /// <summary>构造。</summary>
    /// <param name="message">说明哪里不符合协议。</param>
    public InvalidWireResponseException(string message) : base(message) { }
}
