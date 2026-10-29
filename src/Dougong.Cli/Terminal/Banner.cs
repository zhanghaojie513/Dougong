namespace Dougong.Cli.Terminal;

/// <summary>
/// 启动首页。三档，宽度不够就降级——学 Codex 的
/// <c>full art → compact → text → hidden</c> 序列。
/// </summary>
/// <remarks>
/// <b>为什么大字要能降级</b>：42 列的大字在 80 列终端好看，
/// 在 50 列终端会折行变成乱码。Codex 的做法是"视口不够就不画"，
/// 我们的做法是"逐级退到纯文本"——因为纯文本这一档还要给测试和管道用。
/// <para>
/// <b>Plain 档的输出必须与改造前逐字一致</b>。44 个套件断言的是那些确切文本，
/// 所以这里不是"精简"，是"保持原样"。
/// </para>
/// </remarks>
public static class Banner
{
    /// <summary>大字所需的最小宽度（含左右各一格边距）。</summary>
    private const int BannerMinWidth = 46;

    /// <summary>大字字形的行数与宽度，改动字形时同步。</summary>
    private static readonly string[] BigText =
    [
        "████   ███  ██ ██  ████  ███  ████   ████",
        "██ ██ ██ ██ ██ ██ ██ ██ ██ ██ ██ ██ ██ ██",
        "██ ██ ██ ██ ██ ██ ██ ██ ██ ██ ██ ██ ██ ██",
        "██ ██ ██ ██ ██ ██  ████ ██ ██ ██ ██  ████",
        "██ ██ ██ ██ ██ ██    ██ ██ ██ ██ ██    ██",
        "████   ███   ████  ███   ███  ██ ██  ███ ",
    ];

    /// <summary>
    /// 打印首页。
    /// </summary>
    /// <param name="version">版本号，例如 <c>0.1.0-alpha.1</c>。</param>
    /// <param name="model">模型名。</param>
    /// <param name="protocol">协议名。</param>
    /// <param name="workspace">工作区路径。</param>
    /// <param name="plainLines">
    /// Plain 档要输出的原文行。**由调用方传入改造前的确切文本**，
    /// 这样"兼容"这件事不依赖本类去猜。
    /// </param>
    public static void Print(
        string version,
        string model,
        string protocol,
        string workspace,
        IReadOnlyList<string> plainLines)
    {
        ArgumentNullException.ThrowIfNull(plainLines);

        var capability = Output.Capability;

        switch (capability.Tier)
        {
            case TerminalTier.Rich when capability.Width >= BannerMinWidth:
                PrintRich(version, model, protocol, workspace);
                break;

            case TerminalTier.Rich:
            case TerminalTier.Narrow:
                PrintCompact(version, model, protocol, workspace);
                break;

            default:
                // Plain：逐字输出调用方给的原文本。一个字符都不动。
                foreach (var line in plainLines) Output.Line(line);
                break;
        }
    }

    /// <summary>A 档：大字 + 三行元信息。</summary>
    private static void PrintRich(string version, string model, string protocol, string workspace)
    {
        Output.Line();
        foreach (var row in BigText) Output.Accent(row);
        Output.Line();
        Output.Line($"  {version}   {model} · {protocol}");
        Output.Muted($"  {workspace}");
        Output.Line();
        Output.Muted("  输入 /help 看命令，/exit 退出");
        Output.Line();
    }

    /// <summary>B 档：单行标题，不带大字。</summary>
    private static void PrintCompact(string version, string model, string protocol, string workspace)
    {
        Output.Line();
        Output.Accent($"  dougong {version}");
        Output.Line($"  {model} · {protocol}");
        Output.Muted($"  {workspace}");
        Output.Line();
    }
}
