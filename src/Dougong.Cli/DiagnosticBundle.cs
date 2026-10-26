using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Dougong.Ai;

namespace Dougong.Cli;

/// <summary>
/// 可复现信息包：把排查需要的事实收集成一份可粘贴的文本。
/// </summary>
/// <remarks>
/// 解决的问题：用户报"跑不起来"时，最有用的信息（版本、平台、配置形态、最近失败）
/// 散落各处且需要人工整理，而人整理时常常漏掉关键项、又常常顺手贴进密钥。
/// <para>
/// <b>硬约束：产出里绝不能有密钥或用户内容。</b>
/// 这份文本的用途就是被贴到 issue 里，一旦带密钥，泄露就会随截图与日志扩散。
/// 所有敏感值只以"是否设置 / 长度 / 形态"的形式出现。这条有专门的反证守着。
/// </para>
/// <para>
/// 与 <see cref="Doctor"/> 的分工：doctor 面向"我现在哪里不对"，逐项检查并给建议；
/// 本命令面向"我要把情况发给别人"，输出确定性的事实清单，不含建议、不发起网络请求。
/// </para>
/// </remarks>
public static class DiagnosticBundle
{
    /// <summary>生成信息包文本。</summary>
    /// <param name="cancellationToken">取消令牌。</param>
    public static async Task<string> BuildAsync(CancellationToken cancellationToken = default)
    {
        var writer = new StringBuilder();

        writer.AppendLine("=== Dougong 诊断信息包 ===");
        writer.AppendLine("（可直接粘贴到 issue。已确认不含密钥与用户内容。）");
        writer.AppendLine();

        WriteProduct(writer);
        WritePlatform(writer);
        WriteConfiguration(writer);
        await WriteRecentSessionsAsync(writer, cancellationToken).ConfigureAwait(false);
        WriteEnvironmentShape(writer);

        writer.AppendLine();
        writer.AppendLine("=== 结束 ===");
        return writer.ToString();
    }

    private static void WriteProduct(StringBuilder writer)
    {
        var asm = typeof(DiagnosticBundle).Assembly;
        var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? asm.GetName().Version?.ToString()
            ?? "未知";

        writer.AppendLine("[产品]");

        // InformationalVersion 形如 1.2.3+<sha>。拆开打印，避免版本行里混一个长 sha
        // 影响阅读；两者都保留，因为报问题时"哪个 commit"和"哪个版本"是两件事。
        var plus = info.IndexOf('+');
        if (plus > 0 && plus + 1 < info.Length)
        {
            writer.AppendLine($"  版本    : {info[..plus]}");
            writer.AppendLine($"  commit  : {info[(plus + 1)..]}");
        }
        else
        {
            writer.AppendLine($"  版本    : {info}");
        }

        writer.AppendLine($"  内核接线: {(EnvIsFalse("DOUGONG_KERNEL") ? "禁用（DOUGONG_KERNEL=0）" : "启用")}");
        writer.AppendLine();
    }

    private static void WritePlatform(StringBuilder writer)
    {
        writer.AppendLine("[平台]");
        writer.AppendLine($"  运行时  : {RuntimeInformation.FrameworkDescription}");
        writer.AppendLine($"  RID     : {RuntimeInformation.RuntimeIdentifier}");
        writer.AppendLine($"  系统    : {RuntimeInformation.OSDescription}");
        writer.AppendLine($"  架构    : {RuntimeInformation.ProcessArchitecture}");
        writer.AppendLine($"  控制台  : 代码页 {Console.OutputEncoding.WebName}");
        writer.AppendLine();
    }

    private static void WriteConfiguration(StringBuilder writer)
    {
        writer.AppendLine("[provider 配置]");

        var baseUrl = Environment.GetEnvironmentVariable("DOUGONG_PROVIDER_BASE");
        var model = Environment.GetEnvironmentVariable("DOUGONG_PROVIDER_MODEL");
        var key = Environment.GetEnvironmentVariable("DOUGONG_PROVIDER_KEY");
        var protocol = Environment.GetEnvironmentVariable("DOUGONG_PROVIDER_PROTOCOL");

        // base 与 model 不是秘密，原样给出——否则信息包没有诊断价值。
        writer.AppendLine($"  base    : {Or(baseUrl, "(未设置)")}");
        writer.AppendLine($"  model   : {Or(model, "(未设置)")}");
        writer.AppendLine($"  protocol: {Or(protocol, "openai（默认）")}");

        // 密钥只给形态：自检输出会被贴到公开场合。
        writer.AppendLine($"  key     : {DescribeSecret(key)}");
        writer.AppendLine();
    }

    private static void WriteEnvironmentShape(StringBuilder writer)
    {
        writer.AppendLine("[其它开关]");
        writer.AppendLine($"  DOUGONG_LOG     : {Or(Environment.GetEnvironmentVariable("DOUGONG_LOG"), "warn（默认）")}");
        writer.AppendLine($"  DOUGONG_MEMORY  : {DescribeToggle(Environment.GetEnvironmentVariable("DOUGONG_MEMORY"))}");
        writer.AppendLine($"  DOUGONG_SHELL   : {DescribeToggle(Environment.GetEnvironmentVariable("DOUGONG_SHELL"))}");

        var window = Environment.GetEnvironmentVariable("DOUGONG_CONTEXT_WINDOW");
        writer.AppendLine($"  上下文窗口      : {Or(window, "未设置（不压缩）")}");

        var mcp = Environment.GetEnvironmentVariable("DOUGONG_MCP_SERVERS");
        writer.AppendLine($"  MCP server 数量 : {CountMcpServers(mcp)}");
        writer.AppendLine();
    }

    /// <summary>
    /// 列出最近几个会话文件的**元信息**。
    /// </summary>
    /// <remarks>
    /// 只给文件名、大小、记录数——**不读内容**。
    /// 会话内容包含用户输入的原文，那不是诊断信息，是隐私。
    /// </remarks>
    private static async Task WriteRecentSessionsAsync(
        StringBuilder writer, CancellationToken cancellationToken)
    {
        var dir = Environment.GetEnvironmentVariable("DOUGONG_SESSION_DIR")
            ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".dougong", "sessions");

        writer.AppendLine("[最近会话（只看元信息，不读内容）]");
        writer.AppendLine($"  目录: {dir}");

        if (!Directory.Exists(dir))
        {
            writer.AppendLine("  状态: 目录不存在");
            writer.AppendLine();
            return;
        }

        try
        {
            var files = new DirectoryInfo(dir)
                .GetFiles("*.jsonl")
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .Take(5)
                .ToList();

            if (files.Count == 0)
            {
                writer.AppendLine("  状态: 没有会话文件");
                writer.AppendLine();
                return;
            }

            writer.AppendLine("  文件 / 大小 / 记录数 / 最后修改(UTC)");
            foreach (var f in files)
            {
                var lines = await CountLinesAsync(f.FullName, cancellationToken).ConfigureAwait(false);
                writer.AppendLine(
                    $"    {f.Name}  {f.Length} B  {lines} 条  {f.LastWriteTimeUtc:yyyy-MM-dd HH:mm:ss}");
            }
        }
        catch (Exception ex)
        {
            writer.AppendLine($"  状态: 读取失败（{ex.GetType().Name}）");
        }

        writer.AppendLine();
    }

    private static async Task<int> CountLinesAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            var count = 0;
            using var reader = new StreamReader(path, Encoding.UTF8);
            while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is not null)
                count++;
            return count;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return -1;
        }
    }

    /// <summary>
    /// 描述一个密钥：只说是否存在与长度，绝不给值。
    /// </summary>
    /// <remarks>
    /// 连"前缀"也不给。很多 provider 的密钥形如 `sk-...`，
    /// 给出前几位会缩小猜测空间，而它对诊断毫无价值——
    /// 真正有用的是"到底设没设"和"长度像不像被截断"。
    /// </remarks>
    private static string DescribeSecret(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "(未设置)";

        var trimmed = value.Trim();
        if (trimmed.Length != value.Length)
        {
            return $"已设置，{value.Length} 字符（**含首尾空白，可能是复制粘贴带入的**）";
        }

        if (trimmed.Length < 16)
        {
            return $"已设置，{trimmed.Length} 字符（偏短，确认没有截断）";
        }

        return $"已设置，{trimmed.Length} 字符";
    }

    private static string DescribeToggle(string? value) => value switch
    {
        null or "" or "0" or "false" => "关闭（默认）",
        "1" or "true" => "启用",
        _ => $"启用（值='{value}'）",
    };

    /// <summary>只数条目，不打印内容——server 地址可能含内网主机名。</summary>
    private static int CountMcpServers(string? spec)
    {
        if (string.IsNullOrWhiteSpace(spec)) return 0;

        return spec.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Length;
    }

    private static bool EnvIsFalse(string name) =>
        Environment.GetEnvironmentVariable(name) is "0" or "false";

    private static string Or(string? value, string fallback) =>
        string.IsNullOrWhiteSpace(value) ? fallback : value;
}
