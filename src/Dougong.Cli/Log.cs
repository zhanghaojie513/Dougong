namespace Dougong.Cli;

/// <summary>
/// 分级日志：受 <c>DOUGONG_LOG</c> 控制的诊断输出。
/// </summary>
/// <remarks>
/// <b>默认行为必须与没有它时完全一致。</b> 不设 <c>DOUGONG_LOG</c> 时，
/// <see cref="Information"/> 与 <see cref="Debug"/> 一律不输出，只有
/// <see cref="Warning"/> 与 <see cref="Error"/> 写到 stderr——
/// 也就是说所有现有的错误输出照旧，只是多了一条可开启的细节通道。
/// <para>
/// <b>为什么不用成熟的日志框架</b>：这个 CLI 是参考宿主，不是产品。
/// 加一个依赖会同时带来配置体系、格式选择、sink 管理三件事，
/// 而实际需求只是"出问题时能看到更多"。
/// </para>
/// <para>
/// <b>级别</b>（<c>DOUGONG_LOG</c> 的取值，从少到多）：
/// <c>error</c> &lt; <c>warn</c> &lt; <c>info</c> &lt; <c>debug</c>。
/// 未设置时等价于 <c>warn</c>。
/// </para>
/// <para>
/// <b>绝不打日志的内容</b>：密钥、完整请求体、用户输入的原文。
/// 调试级别只额外给出结构信息（长度、数量、耗时、标识符）。
/// </para>
/// </remarks>
public static class Log
{
    /// <summary>日志级别。</summary>
    public enum Level
    {
        /// <summary>只输出错误。</summary>
        Error = 0,

        /// <summary>输出错误与警告。未设置 <c>DOUGONG_LOG</c> 时的默认值。</summary>
        Warn = 1,

        /// <summary>额外输出流程节点（启动、装配、每轮开始/结束）。</summary>
        Info = 2,

        /// <summary>额外输出细节（工具派发、压缩决策、状态变更）。</summary>
        Debug = 3,
    }

    private static readonly Level Threshold = ParseThreshold();

    /// <summary>当前阈值，供自检与测试观察。</summary>
    public static Level CurrentThreshold => Threshold;

    /// <summary>调试级别是否开启。</summary>
    public static bool IsDebugEnabled => Threshold >= Level.Debug;

    /// <summary>信息级别是否开启。</summary>
    public static bool IsInformationEnabled => Threshold >= Level.Info;

    /// <summary>写一条调试日志。</summary>
    /// <param name="message">内容。**不得包含密钥或用户原文**。</param>
    public static void Debug(string message) => Write(Level.Debug, message);

    /// <summary>写一条信息日志。</summary>
    /// <param name="message">内容。</param>
    public static void Information(string message) => Write(Level.Info, message);

    /// <summary>写一条警告。默认级别下可见。</summary>
    /// <param name="message">内容。</param>
    public static void Warning(string message) => Write(Level.Warn, message);

    /// <summary>写一条错误。任何级别下都可见。</summary>
    /// <param name="message">内容。</param>
    public static void Error(string message) => Write(Level.Error, message);

    private static void Write(Level level, string message)
    {
        if (level > Threshold) return;

        var tag = level switch
        {
            Level.Error => "error",
            Level.Warn => "warn",
            Level.Info => "info",
            _ => "debug",
        };

        // 一律走 stderr：stdout 在 ACP 模式下归协议所有，
        // 日志混进去会破坏 JSON-RPC 流。
        Console.Error.WriteLine($"[{tag}] {message}");
    }

    private static Level ParseThreshold()
    {
        var raw = Environment.GetEnvironmentVariable("DOUGONG_LOG");
        if (string.IsNullOrWhiteSpace(raw)) return Level.Warn;

        return raw.Trim().ToLowerInvariant() switch
        {
            "error" => Level.Error,
            "warn" or "warning" => Level.Warn,
            "info" => Level.Info,
            "debug" or "verbose" or "trace" => Level.Debug,
            // 认不出的值退回默认，并明确告知——静默忽略会让人以为已经生效。
            _ => Unknown(raw),
        };
    }

    private static Level Unknown(string raw)
    {
        Console.Error.WriteLine(
            $"[warn] 无法识别的 DOUGONG_LOG='{raw}'，按默认级别 warn 运行。"
            + "可选值：error / warn / info / debug。");
        return Level.Warn;
    }
}
