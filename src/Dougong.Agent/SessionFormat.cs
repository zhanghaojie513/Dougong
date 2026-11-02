namespace Dougong.Agent;

/// <summary>
/// 会话文件格式版本。
/// </summary>
/// <remarks>
/// <b>为什么只加版本号、不做迁移链</b>：
/// <para>
/// <c>compat-policy.md</c> §7.1 已明确 <c>0.x</c> 不承诺会话兼容，理由是
/// 每个历史版本都要维护一份冻结解码器，而本项目没有外部解码者。
/// 那条判断不变——**这里加的不是兼容能力，是拒绝能力**。
/// </para>
/// <para>
/// <b>不加会漏掉什么</b>：此前 <c>ScanForLastSequenceAsync</c> 遇到解析失败
/// 就 <c>break</c>，并把原因归为"尾部半条记录"。而**格式不兼容**看起来完全一样：
/// 旧程序读到新格式，每一行都解析失败，于是它认为文件在第一条就断了，
/// 接着把它当成一个空会话继续写入——**用户的历史看起来凭空消失**，
/// 而没有任何错误提示。那是本章最难查的一类故障。
/// </para>
/// <para>
/// <b>加了之后</b>：格式不兼容会在打开时明确失败
/// （<see cref="SessionFormatException"/>），而不是静默变成空会话。
/// 这不能救回数据，但能让人知道**数据还在、只是读不了**——两者的处置完全不同。
/// </para>
/// <para>
/// <b>向后兼容的做法</b>：没有版本行的旧文件按 v1 处理。它们本来就是 v1，
/// 而这个判断不需要解码器——只是"缺省值"。
/// </para>
/// </remarks>
public static class SessionFormat
{
    /// <summary>当前程序写入的格式版本。</summary>
    /// <remarks>
    /// <b>改这个数字的判据</b>：记录结构发生**不兼容**变化时加一——字段删除、
    /// 字段语义改变、<see cref="StoreRecordKind"/> 的序列化名变化。
    /// 只加新字段不需要加一（读取方忽略不认识的字段）。
    /// </remarks>
    public const int Current = 1;

    /// <summary>版本行在文件里的前缀。</summary>
    /// <remarks>
    /// <b>刻意不是 JSON</b>：扫描逻辑按行解析 <c>PersistedRecord</c>，
    /// 若版本行也是那种结构，旧程序会把它当成一条正常记录读进去。
    /// 用一个它必然解析失败、但失败得**可识别**的形态，才能区分
    /// "这是版本行"与"这是坏记录"。
    /// </remarks>
    public const string Prefix = "#dougong-session-format:";
}

/// <summary>
/// 会话文件格式与当前程序不兼容。
/// </summary>
/// <remarks>
/// <b>它不是"文件损坏"</b>：损坏是数据坏了，不兼容是数据好着、只是这一版读不懂。
/// 分开的理由是处置完全不同——损坏只能重开或还原备份；
/// 不兼容应当升级程序，或用新版打开。
/// </remarks>
public sealed class SessionFormatException(int fileVersion, string path, string? detail = null)
    : Exception(
        detail
        ?? (
            $"会话文件格式版本为 {fileVersion}，当前程序支持 {SessionFormat.Current}。"
            + $"\n文件：{path}"
            + "\n这**不是文件损坏**——数据还在，只是这一版读不懂。"
            + "\n升级到能读该版本的 dougong，或改用新会话。"))
{
    /// <summary>文件声明的版本。</summary>
    public int FileVersion { get; } = fileVersion;

    /// <summary>会话文件路径。</summary>
    public string Path { get; } = path;
}
