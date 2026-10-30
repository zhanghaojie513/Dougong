using Microsoft.Extensions.AI;

namespace Dougong.Ai;

/// <summary>
/// 可以在运行中换掉底层连接器的 <see cref="IChatClient"/>。
/// </summary>
/// <remarks>
/// <b>为什么需要它</b>：会话运行时会持有连接器实例。要支持“会话中换模型”，
/// 有两条路——重建运行时，或换掉连接器。重建运行时的代价不止是新建对象：
/// 内核接线路径下它还持有租约、MCP 连接与排空状态，重建等于把整个会话拆一遍。
/// 而换连接器只需一次赋值。
/// <para>
/// <b>它不改任何调用语义</b>：所有请求原样转给当前连接器，与
/// <see cref="UsageRecordingChatClient"/> 一样是纯装饰。区别只在于委托目标可变。
/// </para>
/// <para>
/// <b>并发安全</b>：读取用 volatile 引用，切换是整体替换——不会有半个旧半个新的状态。
/// 切换时已经在飞行的请求仍走旧连接器，这是正确的：那次请求的参数是按旧模型生成的。
/// </para>
/// </remarks>
public sealed class SwappableChatClient(IChatClient inner) : DelegatingChatClient(inner)
{
    private volatile IChatClient _current = inner
        ?? throw new ArgumentNullException(nameof(inner));

    /// <summary>当前底层连接器。</summary>
    public IChatClient Current => _current;

    /// <summary>换掉底层连接器。返回旧的那一个，供调用方决定是否释放。</summary>
    public IChatClient Swap(IChatClient next)
    {
        ArgumentNullException.ThrowIfNull(next);
        var previous = _current;
        _current = next;
        return previous;
    }

    public override Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default) =>
        _current.GetResponseAsync(messages, options, cancellationToken);

    public override IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default) =>
        _current.GetStreamingResponseAsync(messages, options, cancellationToken);

    public override object? GetService(Type serviceType, object? serviceKey = null) =>
        _current.GetService(serviceType, serviceKey);
}
