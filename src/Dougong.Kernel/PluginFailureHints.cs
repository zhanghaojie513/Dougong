namespace Dougong.Kernel;

/// <summary>
/// 每个失败阶段的"下一步该做什么"。
/// </summary>
/// <remarks>
/// 集中在一处而不是散落在各抛出点：**同一种失败应当给同一条建议**，
/// 否则同类问题在不同路径上会给出不一致的指引。
/// <para>
/// 文本面向使用者：说的是"你能查什么"，不是"代码在哪"。
/// 每条都应当是**可执行的下一步**，不是对现象的重述。
/// </para>
/// </remarks>
public static class PluginFailureHints
{
    /// <summary>取某个阶段的标准建议；<see cref="PluginFailureStage.Unknown"/> 返回 null。</summary>
    public static string? For(PluginFailureStage stage) => stage switch
    {
        PluginFailureStage.Load =>
            "检查插件的 EnableDynamicLoading 是否开启、依赖程序集是否随包发布、"
            + "以及跨 ALC 的契约程序集是否已用 RegisterSharedContract 登记。",

        PluginFailureStage.Activate =>
            "查看插件 ActivateAsync 抛出的异常；若涉及跨程序集类型，"
            + "按 Load 阶段的建议检查共享契约登记。激活失败不会保留半提交的服务。",

        PluginFailureStage.Drain =>
            "插件未在排空预算内停止，通常意味着它忽略了取消令牌或启动了未等待的后台工作。"
            + "该代次已隔离，需要重启宿主；修复插件后重新加载会使用新代次。",

        PluginFailureStage.Cleanup =>
            "清理回调抛出异常或超时。该代次已隔离、不能再激活，需要重启宿主。"
            + "检查 RegisterForCleanup 登记的回调是否可能永久阻塞。",

        PluginFailureStage.Unload =>
            "只有动态加载的插件可卸载。若已卸载但程序集未回收，"
            + "检查是否仍有外部引用持有插件类型、委托或异常对象。",

        _ => null,
    };
}
