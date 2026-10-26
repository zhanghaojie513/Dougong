using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Dougong.Agent;

/// <summary>记忆产物的作用域。跨工作区默认不传播。</summary>
public enum MemoryScope
{
    /// <summary>只在该工作区生效。</summary>
    Workspace,

    /// <summary>用户级：明确声明后才可跨工作区共享。</summary>
    User,
}

/// <summary>
/// 记忆产物的状态。**候选与启用严格分离**：生成不等于启用。
/// </summary>
/// <remarks>
/// 状态迁移是单向的（除 Revoked 可由任何状态进入）：
/// <c>Candidate → Approved → Enabled → Revoked/Expired</c>。
/// <para>
/// 这个状态机存在的理由：L1 会持久影响后续行为，不是零副作用
/// （`skills-evolution.md §3`）。因此"生成"与"生效"必须是两个动作。
/// </para>
/// </remarks>
public enum MemoryArtifactState
{
    /// <summary>已生成，未审批。不加载。</summary>
    Candidate,

    /// <summary>已审批且内容未被改动，可加载。是否加载由启用状态决定。</summary>
    Approved,

    /// <summary>当前生效，会被注入到后续 turn。</summary>
    Enabled,

    /// <summary>已撤销。保留审计，不加载。</summary>
    Revoked,

    /// <summary>超过有效期。不加载。</summary>
    Expired,
}

/// <summary>
/// 一条 L1 偏好记忆。
/// </summary>
/// <param name="ArtifactId">稳定 ID；同一 Key 的多个版本共享它。</param>
/// <param name="Version">版本号，从 1 开始；内容变化必须递增。</param>
/// <param name="ContentHash">
/// 内容的 SHA-256。**审批绑定它**：审批后内容被改动则哈希不符，拒绝加载。
/// </param>
/// <param name="Key">白名单内的偏好键，例如 <c>language</c>。</param>
/// <param name="Value">取值。</param>
/// <param name="Scope">作用域。</param>
/// <param name="Workspace">所属工作区；<see cref="MemoryScope.User"/> 时可为空。</param>
/// <param name="SourceTaskId">来源任务 ID，用于追溯生成依据。</param>
/// <param name="CreatedAt">创建时间。</param>
/// <param name="State">当前状态。默认 <see cref="MemoryArtifactState.Candidate"/>。</param>
/// <param name="ExpiresAt">过期时间；null 表示不过期。</param>
/// <param name="ApprovedBy">审批标识；未审批为 null。</param>
/// <remarks>
/// <b>为什么不存自由文本</b>：自由文本偏好等于把任意指令注入 system prompt。
/// L1 的"低风险"完全来自 <see cref="PreferenceKeys"/> 的白名单约束——
/// 没有它，L1 与"让模型改写系统提示"没有区别。
/// </remarks>
public sealed record MemoryArtifact(
    string ArtifactId,
    int Version,
    string ContentHash,
    string Key,
    string Value,
    MemoryScope Scope,
    string Workspace,
    string SourceTaskId,
    DateTimeOffset CreatedAt,
    MemoryArtifactState State = MemoryArtifactState.Candidate,
    DateTimeOffset? ExpiresAt = null,
    string? ApprovedBy = null)
{
    /// <summary>计算内容哈希。任何内容变化都会改变它。</summary>
    /// <remarks>
    /// 把 Key 与 Value 一起哈希：只哈希 Value 时，改 Key 不会使审批失效，
    /// 而 Key 决定了这条偏好被当作什么使用。
    /// </remarks>
    public static string ComputeHash(string key, string value)
    {
        var bytes = Encoding.UTF8.GetBytes($"{key}\u0000{value}");
        return Convert.ToHexString(SHA256.HashData(bytes));
    }

    /// <summary>本条产物的哈希是否与当前内容一致（即审批后未被篡改）。</summary>
    public bool IsIntact => string.Equals(
        ContentHash, ComputeHash(Key, Value), StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// L1 允许的偏好键白名单。**硬编码，不可由产物扩展。**
/// </summary>
/// <remarks>
/// 白名单是 L1 低风险的全部依据。任何"先存下来以后再校验"的设计都会
/// 让未知内容进入 system prompt，等于取消 L1 与 L2/L3 的分界。
/// <para>
/// <b>明确排除</b>：任何与权限、审批、工具 allowlist、provider 外发策略相关的键。
/// 那是 L4 的范围，而 `skills-evolution.md §3` 明令"禁止由进化产物自动更改"。
/// </para>
/// </remarks>
public static class PreferenceKeys
{
    /// <summary>偏好键 → 允许的取值（null 表示任意字符串，但有格式约束）。</summary>
    private static readonly Dictionary<string, Func<string, bool>> Allowed =
        new(StringComparer.Ordinal)
        {
            ["language"] = v => v.Length is > 0 and <= 40,
            ["output.format"] = v => v is "concise" or "detailed" or "bullets",
            ["naming.convention"] = v => v is "camelCase" or "PascalCase" or "snake_case",
        };

    /// <summary>该键是否在白名单内。</summary>
    public static bool IsAllowed(string key) => Allowed.ContainsKey(key);

    /// <summary>该取值对该键是否合法。</summary>
    public static bool IsValid(string key, string value) =>
        Allowed.TryGetValue(key, out var check) && check(value);

    /// <summary>所有允许的键，供诊断与文档使用。</summary>
    public static IReadOnlyCollection<string> All => Allowed.Keys;
}
