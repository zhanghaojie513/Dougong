using System.Text.Json;

namespace Dougong.Agent;

/// <summary>
/// 审批策略装饰器：权限为"已知且无访问"的调用不再询问审批，其余照旧交给内层审批器。
/// </summary>
/// <remarks>
/// 为什么需要它：dispatcher 会对<b>每一次</b>调用请求审批，这是正确的保守默认——
/// 它不能自己判断"这个工具应该没事"。但某些工具确实没有可审批的内容
/// （例如 <c>ask_user</c> 只向用户提问）：
/// 让用户先批准"允许提问"再看到问题本身，是把一次交互拆成两次。
/// <para>
/// 触发条件被刻意收得很窄，三条同时满足才跳过：
/// <list type="number">
/// <item><see cref="ToolPermission.IsKnown"/> 为 true——未知权限永不跳过；</item>
/// <item><see cref="ToolPermission.Access"/> 恰为 <see cref="ToolAccess.None"/>；</item>
/// <item>没有声明任何路径、命令或域名——声明了具体范围就说明它有可审批的内容。</item>
/// </list>
/// 因此 MCP 工具（一律 <c>IsKnown=false</c>）与文件/shell 工具都不受影响。
/// </para>
/// <para>
/// 这是宿主显式选择的策略，不是内核默认：<see cref="ToolDispatcher"/> 不启用它时
/// 行为完全不变。是否该信任某个工具的 <c>None</c> 声明由宿主决定——
/// 可信进程内插件可以直接调用 OS API，这项策略约束的是交互次数，不是安全性。
/// </para>
/// </remarks>
public sealed class PermissionPolicyApprover(IToolApprover inner) : IToolApprover
{
    private readonly IToolApprover _inner = inner
        ?? throw new ArgumentNullException(nameof(inner));

    /// <summary>被跳过的调用数，供诊断与验收断言。</summary>
    public int Skipped { get; private set; }

    public ValueTask<ToolApprovalDecision> RequestApprovalAsync(
        ToolApprovalRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (IsInert(request.Permission))
        {
            Skipped++;
            return ValueTask.FromResult(
                ToolApprovalDecision.Allow("该工具声明为已知无访问，不产生审批项。"));
        }

        return _inner.RequestApprovalAsync(request, cancellationToken);
    }

    private static bool IsInert(ToolPermission permission) =>
        permission.IsKnown
        && permission.Access == ToolAccess.None
        && permission.Paths.Count == 0
        && permission.Commands.Count == 0
        && permission.Domains.Count == 0;
}
