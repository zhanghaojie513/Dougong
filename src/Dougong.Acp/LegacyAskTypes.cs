namespace Dougong.Acp;

// 兼容别名：真实定义已移到 Dougong.Agent（提问语义与传输无关，
// 同一个 ask_user 也要服务本地 REPL）。
//
// **为什么不直接删**：这些类型原先公开定义在 Dougong.Acp 下，删掉会让
// 已引用 Dougong.Acp.AskOutcome 的下游代码编译失败（ApiCompat 报 CP0001）。
// 本项目 0.x 不做向后兼容承诺，但**变更必须可见**——保留别名比静默破坏好。
//
// 内部代码一律用 Dougong.Agent 的类型；这里只做对外命名兼容。

/// <summary>
/// 兼容别名：请改用 <see cref="Dougong.Agent.AskOutcome"/>。
/// </summary>
[Obsolete("改用 Dougong.Agent.AskOutcome——提问语义不属于 ACP。")]
public enum AskOutcome
{
    /// <inheritdoc cref="Dougong.Agent.AskOutcome.Answered"/>
    Answered = Dougong.Agent.AskOutcome.Answered,

    /// <inheritdoc cref="Dougong.Agent.AskOutcome.Declined"/>
    Declined = Dougong.Agent.AskOutcome.Declined,

    /// <inheritdoc cref="Dougong.Agent.AskOutcome.Cancelled"/>
    Cancelled = Dougong.Agent.AskOutcome.Cancelled,

    /// <inheritdoc cref="Dougong.Agent.AskOutcome.Unavailable"/>
    Unavailable = Dougong.Agent.AskOutcome.Unavailable,

    /// <inheritdoc cref="Dougong.Agent.AskOutcome.InvalidRequest"/>
    InvalidRequest = Dougong.Agent.AskOutcome.InvalidRequest,
}

/// <summary>
/// 兼容别名：请改用 <see cref="Dougong.Agent.AskResult"/>。
/// </summary>
/// <remarks>
/// 记录类型的构造签名必须与基线一致（枚举是 <see cref="AskOutcome"/> 而非
/// Agent 的那个）——否则 ApiCompat 仍报构造器缺失。
/// </remarks>
[Obsolete("改用 Dougong.Agent.AskResult——提问语义不属于 ACP。")]
public sealed record AskResult(
    AskOutcome Outcome, string? Diagnostic, IReadOnlyList<AskAnswer> Answers)
{
    /// <summary>答案的键值视图。</summary>
    public IReadOnlyDictionary<string, string> AsDictionary() =>
        Answers.ToDictionary(a => a.Question, a => a.Answer, StringComparer.Ordinal);

    /// <summary>转成 Agent 层的等价结果。</summary>
    public Dougong.Agent.AskResult ToAgentResult() =>
        new((Dougong.Agent.AskOutcome)Outcome, Diagnostic,
            [.. Answers.Select(a => new Dougong.Agent.AskAnswer(a.Question, a.Answer))]);
}

/// <summary>
/// 兼容别名：请改用 <see cref="Dougong.Agent.AskAnswer"/>。
/// </summary>
[Obsolete("改用 Dougong.Agent.AskAnswer——提问语义不属于 ACP。")]
public sealed record AskAnswer(string Question, string Answer);
