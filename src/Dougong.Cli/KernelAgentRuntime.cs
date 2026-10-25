using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Dougong.Agent;
using Dougong.Kernel;

namespace Dougong.Cli;

/// <summary>
/// 把一个具体的 <see cref="IAgentRuntime"/> 适配成内核服务，使宿主必须通过
/// <c>IServiceReference.InvokeAsync</c> 使用它，而不是长期缓存实例。
/// </summary>
/// <remarks>
/// 存在的理由（G14）：内核的租约、代次绑定、排空此前从未在真实会话中生效，
/// 因为 CLI 直接 <c>new LoopAgentRuntime(...)</c> 并存下来。
/// <para>
/// <b>租约粒度</b>：一次 <see cref="RunAsync"/> 调用 = 一个回合，取一次租约。
/// 不选"整轮"是因为一轮对话可能跑几十秒，停用要等满 <c>DrainTimeout</c> 才能隔离；
/// 不选"会话级"理由相同且更差。这里与 <c>architecture.md</c> §4.4 对工具调用的
/// 短租约原则一致。
/// </para>
/// <para>
/// <b>Channel 中转</b>：<c>InvokeAsync</c> 的委托是
/// <c>Func&lt;T, CancellationToken, ValueTask&lt;TResult&gt;&gt;</c>，装不下
/// <see cref="IAsyncEnumerable{T}"/>。因此后台任务在租约内跑一个回合，事件写入 channel，
/// 调用方从 channel 读。
/// <b>由此引入的真实风险</b>：调用方若中途放弃枚举，后台回合必须被取消，
/// 否则租约永不释放、排空永远等不到。代码里用
/// <c>try/finally + enumerator.DisposeAsync</c> 保证这一点，并在
/// <c>-- kernel-wiring</c> 的 <c>abandoned-enumeration-releases-lease</c> 里验收。
/// </para>
/// </remarks>
public sealed class KernelAgentRuntime : IAgentRuntime
{
    private readonly IServiceReference<IAgentRuntime> _reference;

    public KernelAgentRuntime(IServiceReference<IAgentRuntime> reference)
    {
        ArgumentNullException.ThrowIfNull(reference);
        _reference = reference;
    }

    public async IAsyncEnumerable<AgentEvent> RunAsync(
        AgentRunRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        // 单生产者单消费者，保序；容量小是因为消费者几乎总是跟得上。
        var channel = Channel.CreateBounded<AgentEvent>(new BoundedChannelOptions(64)
        {
            SingleReader = true,
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.Wait,
        });

        // 回合内的取消源：调用方放弃枚举时用它通知后台停手。
        using var turn = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        var pump = Task.Run(async () =>
        {
            try
            {
                // 整个回合在一次 InvokeAsync 内：提供方停用会等这一回合结束。
                await _reference.InvokeAsync(async (runtime, token) =>
                {
                    using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                        token, turn.Token);


                    await foreach (var evt in runtime.RunAsync(request, linked.Token)
                        .ConfigureAwait(false))
                    {
                        await channel.Writer.WriteAsync(evt, linked.Token).ConfigureAwait(false);
                    }

                    return true;
                // 必须传 turn.Token 而不是 cancellationToken：
                // 后者是调用方的令牌，调用方主动 break 时它并不会被取消，
                // 于是 turn.Cancel() 影响不到 InvokeAsync 内部的 linked token，
                // 运行时的循环永远收不到取消，租约也永不释放。
                }, turn.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (turn.IsCancellationRequested)
            {
                // 调用方放弃枚举：不是错误，但必须让读取端知道流已结束。
            }
            catch (Exception ex)
            {
                // 异常要变成事件交给调用方，而不是留在这个后台任务里丢失。
                // 与"流永不抛异常"的取向一致：错误是数据。
                await channel.Writer
                    .WriteAsync(new AgentEvent.RunFinished(
                        AgentRunOutcome.Failed, $"运行时调用失败：{ex.GetType().Name}: {ex.Message}"))
                    .ConfigureAwait(false);
            }
            finally
            {
                channel.Writer.TryComplete();
            }
        }, CancellationToken.None);

        try
        {
            await foreach (var evt in channel.Reader.ReadAllAsync(cancellationToken)
                .ConfigureAwait(false))
            {
                yield return evt;
            }
        }
        finally
        {
            // 取消有两个来源：这里的显式 Cancel，以及 `using turn` 退出时的 Dispose。
            // 前者让取消在 await 之前发生，读起来更直白；后者是兜底。
            // 真正必需的是**等 pump 结束**——不等的话租约不释放，下面的检查会失败。
            turn.Cancel();
            try { await pump.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }
    }
}
