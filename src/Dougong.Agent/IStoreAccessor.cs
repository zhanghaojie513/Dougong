using System.Text.Json;

namespace Dougong.Agent;

/// <summary>
/// 对存储的**每次调用都重新获取**的访问点。
/// </summary>
/// <remarks>
/// 为什么要有这一层（而不是直接持有 <see cref="IAgentStore"/>）：
/// <para>
/// 租约机制（<c>IServiceReference.InvokeAsync</c>）保护的是"通过它访问"的路径，
/// 不是实例本身。只要某个类型把 <see cref="IAgentStore"/> 存成字段，
/// 替换存储之后旧实例仍会被使用——那条路径上租约完全失效。
/// </para>
/// <para>
/// 这个接口把"取用"这个动作变成契约的一部分：实现方必须在每次调用时自己决定
/// 去哪里拿当前实例（内核路径下走租约，直连路径下返回固定实例）。
/// 于是"长期持有实现实例"在类型层面就不可表达。
/// </para>
/// <para>
/// <b>为什么不直接用内核的 <c>IServiceReference</c></b>：那会让 <c>Dougong.Agent</c>
/// 依赖 <c>Dougong.Kernel</c>，破坏分层（Agent 要能脱离内核单独工作，
/// spike 里的多数套件正是这样用的）。契约留在 L1，实现对接到 L0。
/// </para>
/// </remarks>
public interface IStoreAccessor
{
    /// <summary>
    /// 在当前有效的存储上执行一次提交。
    /// </summary>
    /// <remarks>
    /// 实现必须自己解决"拿哪个实例"：替换发生后应当用新实例，
    /// 而不是继续用创建时看到的那个。
    /// </remarks>
    ValueTask CommitAsync(StoreRecord record, CancellationToken cancellationToken = default);
}

/// <summary>把固定实例包装成访问点：用于没有插件替换的场景（测试、单机 CLI）。</summary>
/// <remarks>
/// 这个实现**确实**长期持有实例——但它是显式的：调用方明确选择了"不参与替换"。
/// 与"接口收裸实例、悄悄存下来"不同，这里的选择是可见的、可被审查的。
/// </remarks>
public sealed class FixedStoreAccessor(IAgentStore store) : IStoreAccessor
{
    private readonly IAgentStore _store = store
        ?? throw new ArgumentNullException(nameof(store));

    public ValueTask CommitAsync(StoreRecord record, CancellationToken cancellationToken = default) =>
        _store.CommitAsync(record, cancellationToken);
}
