using System.Text.Json;

namespace Dougong.Agent;

/// <summary>
/// 工具执行前的拦截决策。
/// </summary>
/// <remarks>
/// <b>三态而不是布尔</b>：<see cref="Approve"/> 与"没有拦截器"在语义上不同——
/// 前者是"看过了、允许"，后者是"没人看"。合成一个布尔会让日志分不清
/// "策略放行"与"根本没有策略"，而排查时这正是第一个要问的。
///
/// <para>
/// <b>没有 <c>Rewrite</c>（改写入参）</b>：dsh 的 CC/Codex 桥接也把它列为
/// 推迟项，理由是"pre-execution 参数被 tool/call 审计、assistant/message 历史
/// 和工具展示共同读取，诚实的重写是一个设计单元，而非一个字段"。
/// 我们照同样的判断：先不做，也不留一个半成品字段。
/// </para>
/// </remarks>
public enum InterceptionDecision
{
    /// <summary>允许执行。</summary>
    Approve,

    /// <summary>
    /// 需要人确认。
    /// </summary>
    /// <remarks>
    /// <b>它不是"拒绝"</b>：它把决定交给审批通道。没有审批器时**按拒绝处理**——
    /// 这是 fail-closed，与 <c>PermissionPolicyApprover</c> 同一判据。
    /// 反过来做（没有审批器就放行）会让"装一个拦截器"变成一次静默的提权。
    /// </remarks>
    Ask,

    /// <summary>拒绝执行。</summary>
    Deny,
}

/// <summary>一次拦截的结论。</summary>
/// <param name="Decision">决策。</param>
/// <param name="Reason">
/// 给模型与用户看的原因。<b>拒绝时必须写</b>：只说"被拦截了"会让模型反复重试
/// 同一个调用，而它不知道该怎么改。
/// </param>
public sealed record InterceptionResult(
    InterceptionDecision Decision,
    string? Reason = null)
{
    /// <summary>允许。</summary>
    public static InterceptionResult Approve() => new(InterceptionDecision.Approve);

    /// <summary>拒绝。</summary>
    /// <param name="reason">必须说明"为什么"与"该怎么做"。</param>
    public static InterceptionResult Deny(string reason) =>
        new(InterceptionDecision.Deny, reason);

    /// <summary>交给审批通道。</summary>
    /// <param name="reason">给用户看的说明。</param>
    public static InterceptionResult Ask(string reason) =>
        new(InterceptionDecision.Ask, reason);
}

/// <summary>拦截器能看到的一次调用。</summary>
/// <param name="SessionId">所属会话。</param>
/// <param name="TurnId">所属回合。</param>
/// <param name="InvocationId">调用 ID。</param>
/// <param name="ToolId">工具 ID。</param>
/// <param name="Arguments">已通过 schema 校验的参数。</param>
/// <param name="Permission">工具自述的权限范围。</param>
/// <param name="Workspace">原始项目根。</param>
public sealed record ToolInterceptionContext(
    string SessionId,
    string TurnId,
    string InvocationId,
    string ToolId,
    JsonElement Arguments,
    ToolPermission Permission,
    string Workspace);

/// <summary>
/// 工具执行前的拦截接缝。
/// </summary>
/// <remarks>
/// <b>解决什么问题</b>：审批器回答的是"这个人允不允许"，
/// 而宿主还需要回答"这个项目的规则允不允许"——不许删 <c>migrations/</c>、
/// 不许 <c>git push</c>、不许读 <c>.env</c>。这类判断不该混进审批器：
/// 审批器是**人**的通道，规则是**配置**。
///
/// <para>
/// <b>与审批的分工要说死</b>：
/// <list type="bullet">
/// <item>拦截器在审批**之前**跑。被 <see cref="InterceptionDecision.Deny"/> 的调用
/// 不会产生审批项——让用户为一件注定被拒的事点两次头是纯粹的浪费。</item>
/// <item><see cref="InterceptionDecision.Ask"/> 把决定**交给**审批器（若已配置）。
/// 没有审批器时按拒绝处理。</item>
/// <item>拦截器**不持有**最终执行权：批准之后仍要过分派器的进锁复核，
/// 否则"拦截时通过、执行时规则变了"就没人管。</item>
/// </list>
/// </para>
///
/// <para>
/// <b>多个拦截器的合并规则是"最严者胜"</b>（<c>Deny &gt; Ask &gt; Approve</c>），
/// 且**依次调用到第一个 Deny 为止**——一个拒绝之后继续问别的拦截器没有意义，
/// 而它们的副作用（读文件、起进程）不为一个已定的结局再付一次。
/// </para>
///
/// <para>
/// <b>抛异常按拒绝处理</b>：拦截器失败时放行，等于让"规则坏了"变成"什么都允许"。
/// 这与审批器同一判据。
/// </para>
/// </remarks>
public interface IToolInterceptor
{
    /// <summary>拦截器名字，进诊断与日志。</summary>
    string Name { get; }

    /// <summary>
    /// 判断这次调用可否执行。
    /// </summary>
    /// <remarks>
    /// <b>不要在这里做慢操作</b>：它在每个工具调用前同步等待。
    /// 需要外部查询的实现应自己缓存。
    /// </remarks>
    /// <param name="context">调用上下文。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    ValueTask<InterceptionResult> InterceptAsync(
        ToolInterceptionContext context, CancellationToken cancellationToken = default);
}

/// <summary>多个拦截器的合并。</summary>
/// <remarks>
/// <b>合并放在这里而不是各调用点</b>：优先级顺序是契约的一部分，
/// 写两遍就会出现"分派器按 Deny 优先、别的入口按最后一个赢"。
/// </remarks>
public static class ToolInterception
{
    /// <summary>
    /// 依次询问拦截器，返回最严的结论。
    /// </summary>
    /// <remarks>
    /// <b>到第一个 Deny 就停</b>：后续拦截器不会改变结局，
    /// 而它们可能有代价（读文件、起进程）。
    /// <para>
    /// <b>没有拦截器时返回 null 而不是 Approve</b>：调用方要能区分
    /// "没人看"与"看过了、允许"。
    /// </para>
    /// </remarks>
    public static async ValueTask<InterceptionResult?> EvaluateAsync(
        IReadOnlyList<IToolInterceptor> interceptors,
        ToolInterceptionContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(interceptors);
        ArgumentNullException.ThrowIfNull(context);

        if (interceptors.Count == 0) return null;

        InterceptionResult? strictest = null;

        foreach (var interceptor in interceptors)
        {
            InterceptionResult result;
            try
            {
                result = await interceptor.InterceptAsync(context, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // **失败按拒绝处理**：放行等于让"规则坏了"变成"什么都允许"。
                return InterceptionResult.Deny(
                    $"拦截器 '{interceptor.Name}' 执行失败，按拒绝处理：{ex.Message}");
            }

            if (result.Decision is InterceptionDecision.Deny)
                return result;

            if (result.Decision is InterceptionDecision.Ask)
                strictest = result;
        }

        return strictest ?? InterceptionResult.Approve();
    }
}
