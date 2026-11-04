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
        // TryRender 的 height 是"字标可用高度"：这里模拟"字标占满终端"，
        // 所以直接传终端高度。
        var rows = BigText.TryRender(width, height, reservedRows: 0);
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
            plainLines: ["（Plain 档原文）"],
            summary: SampleSummary());

        return 0;
    }

    /// <summary>
    /// 一份固定的样例事实。**不用真实数据**：预览要能反映"内容很多时"的排版
    /// （技能 20 几个、MCP 3 台），而本机实际可能一个都没有。
    /// </summary>
    private static StartupSummary SampleSummary() => new(
        Version: Dougong.Cli.ProductInfo.Version,
        Model: "qfmodel",
        Provider: "Nous Research",
        Workspace: "C:/work/myProject/dougong",
        SessionId: "81bcbb68",
        Tools:
        [
            "ask_user", "edit_file", "glob", "get_goal", "grep", "job_output",
            "job_start", "job_stop", "list_files", "read_file", "search_sessions",
            "skill", "subagent", "subagent_list", "subagent_result",
            "todo_write", "web_fetch", "working_directory", "write_file",
        ],
        McpServers:
        [
            new McpServerStatus("ekko-studio-api", "stdio", true, 12, null),
            new McpServerStatus("ekko-studio-browser", "stdio", true, 5, null),
            new McpServerStatus("ekko-studio-devices", "stdio", false, 0, "IOException"),
        ],
        SkillCount: 27,
        SkillSources:
        [
            new SkillSourceGroup("project-agents",
                ["doc-qa-factory", "csv-pipeline", "image-ocr", "pdf", "cover-maker"]),
            new SkillSourceGroup("project-dsh",
                ["brand-review", "release-notes"]),
            new SkillSourceGroup("user-dsh",
                ["excel-processor", "skill-creator", "logo-design", "xlsx", "web-scraper",
                 "frontend-skill", "pdf", "docx", "template-creator", "impeccable",
                 "archify", "product-design-audit", "product-design-prototype",
                 "product-design-get-context", "product-design-ideate"]),
        ]);
}
