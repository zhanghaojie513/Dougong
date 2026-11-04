using Dougong.Cli.Terminal;

namespace Dougong.Agent.Comparison;

/// <summary>
/// 开发期预览：把字标与首页打到标准输出，供肉眼对版式。
/// **不是一个检查套件**——它不断言任何东西，只负责显示。
/// </summary>
/// <remarks>
/// <b>为什么需要它</b>：字标的观感只能看，不能断言。检查能钉住
/// "每行都是 5 列"、"宽度不超终端"，但"这一版看起来是不是一堵墙"
/// 只能人来看。这个入口让"看"从"改完再跑 dougong"降级成一条命令。
///
/// <para>
/// <b>它强制 Rich 档</b>：真实运行时本机很可能是 Plain 档（输出被重定向），
/// 那样什么都看不到。这里用 <see cref="Output.OverrideCapability"/> 造一个
/// 宽终端，只影响本进程。
/// </para>
/// </remarks>
internal static class PreviewBanner
{
    public static int Run(string[] args)
    {
        var width = 120;
        var height = 40;

        if (args.Length >= 1 && int.TryParse(args[0], out var w)) width = w;
        if (args.Length >= 2 && int.TryParse(args[1], out var h)) height = h;

        Output.OverrideCapability(
            new TerminalCapability(TerminalTier.Rich, width, IsRedirected: false, Height: height));

        Console.WriteLine($"── 字标（{width}×{height}）──────────────");
        var rows = BigText.TryRender(width, height);
        if (rows is null)
        {
            Console.WriteLine("（这个尺寸放不下字标，实际会退回云拱形象）");
        }
        else
        {
            for (var i = 0; i < rows.Count; i++)
                Output.Colored(rows[i], BigText.RowColor(i, rows.Count));
        }

        Console.WriteLine();
        Console.WriteLine("── 完整首页 ──────────────────────────────");
        Banner.Print(
            version: Dougong.Cli.ProductInfo.Version,
            model: "qfmodel",
            protocol: "Chat",
            workspace: "C:/work/myProject/dougong",
            plainLines: ["（Plain 档原文）"]);

        return 0;
    }
}
