namespace Dougong.Kernel;

/// <summary>
/// 共享诊断数据：只保留字符串与结构，不持有插件 Type、委托或原始异常对象，
/// 避免状态历史成为插件 ALC 的长期 GC 根。
/// </summary>
/// <param name="Code">异常类型名或固定标识，用于按类型筛选。</param>
/// <param name="Message">异常消息原文。</param>
/// <param name="Stage">
/// 失败发生的阶段。宿主据此决定补救方式——这是"能定位到组件"的关键
/// （`goal.md` P3：故障必须可解释）。
/// </param>
/// <param name="Hint">
/// 下一步该做什么。**面向使用者而非开发者**：写"检查 EnableDynamicLoading 与共享契约登记"，
/// 不写"看 Loading.cs 第 127 行"。为空表示该阶段没有可操作建议。
/// </param>
public sealed record PluginDiagnostic(
    string Code,
    string Message,
    PluginFailureStage Stage = PluginFailureStage.Unknown,
    string? Hint = null);

/// <summary>状态投影中的一条记录；用于从快照和增量重建插件集合。</summary>
public enum StateDeltaKind
{
    /// <summary>一次状态迁移。</summary>
    StateChange,

    /// <summary>插件已登记（此前不在投影中）。</summary>
    Registered,

    /// <summary>插件已从内核移除。</summary>
    Removed,
}
