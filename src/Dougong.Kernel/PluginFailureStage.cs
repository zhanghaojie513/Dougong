namespace Dougong.Kernel;

/// <summary>
/// 插件失败发生的阶段。用于把"失败了"细化成"在哪一步失败"。
/// </summary>
/// <remarks>
/// **为什么需要它**：`PluginDiagnostic` 原本只有异常类型名与消息。
/// 对于"插件加载失败"，宿主能看到的是一个 `AggregateException` 或
/// `TargetInvocationException`——既不知道是哪一步，也不知道下一步该做什么。
/// <para>
/// 阶段划分与内核的生命周期步骤一一对应，因此看到 Stage 就能定位到代码位置。
/// 这不是分类学的完备性问题，而是"宿主拿去能立刻做什么"的问题：
/// 每个阶段都有不同的补救方式（见 `PluginDiagnostic.Hint`）。
/// </para>
/// </remarks>
public enum PluginFailureStage
{
    /// <summary>无法归类。观察者/事件处理器异常等不属于插件生命周期的失败用它。</summary>
    Unknown,

    /// <summary>加载：程序集解析、契约版本校验、入口类型查找。</summary>
    Load,

    /// <summary>激活：<c>ActivateAsync</c> 执行或服务注册提交。</summary>
    Activate,

    /// <summary>排空：取消后等待在途调用归零。</summary>
    Drain,

    /// <summary>清理：执行 <c>RegisterForCleanup</c> 登记的回调。</summary>
    Cleanup,

    /// <summary>卸载：ALC 卸载或卸前复核。</summary>
    Unload,
}
