using Dougong.Agent;

namespace Dougong.Cli;

/// <summary>
/// 把存储的持久化自述翻译成用户能看懂的表述。
/// </summary>
/// <remarks>
/// 为什么需要它：`StoreDurability` 此前在**产品代码里没有任何消费者**——
/// 实现声明了等级，却没人读、没人展示。声明没人看就等于没声明，
/// 而 `goal.md` P3 要求"必须如实转述，不能把进程崩溃存活说成断电安全"。
/// <para>
/// 表述原则：**不模糊**。每个等级都要说清"能扛什么、不能扛什么"，
/// 而不是只报一个词。用户需要知道自己的会话在什么情况下会丢。
/// </para>
/// </remarks>
public static class DurabilityText
{
    /// <summary>
    /// 机器可读的等级标记，与人类可读描述一起输出。
    /// </summary>
    /// <remarks>
    /// 为什么要有它：验收需要断言"等级确实被展示了"，而控制台编码在不同
    /// 平台上不一致（本项目在 GBK 控制台下踩过中文乱码导致匹配失败）。
    /// ASCII 标记让检查不依赖编码，人类可读描述照常给用户看。
    /// </remarks>
    public static string Tag(StoreDurability durability) => durability switch
    {
        StoreDurability.ProcessMemory => "process-memory",
        StoreDurability.SurvivesProcessCrash => "process-crash",
        StoreDurability.SurvivesPowerLoss => "power-loss",
        _ => "unknown",
    };

    /// <summary>单行描述，用于启动横幅。</summary>
    public static string Describe(StoreDurability durability) => durability switch
    {
        StoreDurability.ProcessMemory =>
            "仅内存（进程退出即丢失）",

        StoreDurability.SurvivesProcessCrash =>
            "进程崩溃可恢复（不保证断电安全）",

        StoreDurability.SurvivesPowerLoss =>
            "断电安全（实现已提供持久化证明）",

        _ => $"未知等级（{(int)durability}）—按最低保证对待",
    };

    /// <summary>
    /// 是否需要提醒用户"这里有风险"。仅内存实现应当被显式警告，
    /// 否则用户会以为自己在用持久化会话。
    /// </summary>
    public static bool NeedsWarning(StoreDurability durability) =>
        durability is StoreDurability.ProcessMemory;
}
