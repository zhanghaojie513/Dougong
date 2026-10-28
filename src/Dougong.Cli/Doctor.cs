using Dougong.Ai;
using Microsoft.Extensions.AI;

using Dougong.Cli.Terminal;

namespace Dougong.Cli;

/// <summary>
/// 启动自检：一条命令判断"为什么跑不起来"，并给出下一步。
/// </summary>
/// <remarks>
/// 存在的理由来自一次真实的新人走查：构建警告、内存不足、目录权限、provider 配置
/// 这些东西出错时，报错文本本身不会告诉你"这是环境问题还是项目问题"。
/// 判断成本很高，而结论往往只有一句话。
/// <para>
/// <b>设计约束</b>：
/// <list type="bullet">
/// <item>每一项都必须给出**下一步做什么**，只说"失败"等于把问题换个地方显示</item>
/// <item>区分"错误"与"警告"：缺 provider 配置是错误（跑不起来），
/// 缺 shell 工具是警告（能跑，只是功能少）</item>
/// <item>**不打印密钥**——只报告"设置了/未设置"与长度</item>
/// <item>没有网络时也要能跑完：provider 连通性检查失败不算致命</item>
/// </list>
/// </para>
/// </remarks>
public static class Doctor
{
    /// <summary>单项检查的结果等级。</summary>
    private enum Level
    {
        Pass,
        Warn,
        Fail,
    }

    private sealed record CheckResult(Level Level, string Name, string Detail, string? Next = null);

    /// <summary>运行全部检查，返回退出码：有失败项返回 1，否则 0。</summary>
    public static async Task<int> RunAsync(CancellationToken cancellationToken = default)
    {
        var results = new List<CheckResult>
        {
            CheckRuntime(),
            CheckProviderConfig(),
            CheckWorkspace(),
            CheckSessionDirectory(),
            CheckMemorySwitch(),
        };

        // 连通性检查放最后：它可能耗时，且前面有硬失败时它的价值下降。
        results.Add(await CheckProviderReachableAsync(cancellationToken).ConfigureAwait(false));

        Print(results);
        return results.Any(r => r.Level is Level.Fail) ? 1 : 0;
    }

    private static CheckResult CheckRuntime()
    {
        var version = Environment.Version;
        var framework = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription;

        // 低于 .NET 10 会因运行时版本不匹配而加载失败，先于其它检查报告。
        if (version.Major < 10)
        {
            return new(Level.Fail, "运行时",
                $"{framework}（{version}）",
                "本项目面向 .NET 10。请安装 .NET 10 SDK，并确认没有任何 global.json 把版本钉在更低的版本。");
        }

        return new(Level.Pass, "运行时", $"{framework}（{version}）");
    }

    private static CheckResult CheckProviderConfig()
    {
        var baseUrl = Environment.GetEnvironmentVariable("DOUGONG_PROVIDER_BASE");
        var model = Environment.GetEnvironmentVariable("DOUGONG_PROVIDER_MODEL");
        var key = Environment.GetEnvironmentVariable("DOUGONG_PROVIDER_KEY");
        var protocol = Environment.GetEnvironmentVariable("DOUGONG_PROVIDER_PROTOCOL") ?? "openai";

        var missing = new List<string>();
        if (string.IsNullOrWhiteSpace(baseUrl)) missing.Add("DOUGONG_PROVIDER_BASE");
        if (string.IsNullOrWhiteSpace(model)) missing.Add("DOUGONG_PROVIDER_MODEL");
        if (string.IsNullOrWhiteSpace(key)) missing.Add("DOUGONG_PROVIDER_KEY");

        if (missing.Count > 0)
        {
            return new(Level.Fail, "provider 配置",
                $"缺少 {string.Join(" / ", missing)}",
                "设置这三个环境变量后再运行。示例：\n"
                + "      DOUGONG_PROVIDER_BASE=https://api.example.com/v1\n"
                + "      DOUGONG_PROVIDER_MODEL=<模型名>\n"
                + "      DOUGONG_PROVIDER_KEY=<密钥>\n"
                + "  协议默认 chat-completions；Anthropic 兼容端点设 DOUGONG_PROVIDER_PROTOCOL=anthropic。");
        }

        // 只报告长度，绝不打印值。
        var detail = $"base={baseUrl}  model={model}  protocol={protocol}  key=已设置({key!.Length} 字符)";

        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri)
            || (uri.Scheme != "http" && uri.Scheme != "https"))
        {
            return new(Level.Fail, "provider 配置",
                $"base 不是合法的 http(s) 地址：{baseUrl}", "检查 DOUGONG_PROVIDER_BASE，它应当形如 https://host/v1。");
        }

        if (protocol is not ("openai" or "anthropic" or "responses"))
        {
            return new(Level.Fail, "provider 配置",
                $"未知协议 '{protocol}'", "DOUGONG_PROVIDER_PROTOCOL 只接受 openai / anthropic / responses。");
        }

        return new(Level.Pass, "provider 配置", detail);
    }

    private static CheckResult CheckWorkspace()
    {
        var workspace = Environment.GetEnvironmentVariable("DOUGONG_WORKSPACE")
            ?? Directory.GetCurrentDirectory();

        if (!Directory.Exists(workspace))
        {
            return new(Level.Fail, "工作区",
                $"目录不存在：{workspace}", "检查 DOUGONG_WORKSPACE；不设时使用当前目录。");
        }

        try
        {
            // 真的写一个文件再删掉：只查权限标志会漏掉 ACL 与只读挂载。
            var probe = Path.Combine(workspace, $".dougong-write-probe-{Guid.NewGuid():N}");
            File.WriteAllText(probe, "probe");
            File.Delete(probe);
            return new(Level.Pass, "工作区", $"{workspace}（可写）");
        }
        catch (Exception ex)
        {
            return new(Level.Fail, "工作区",
                $"不可写：{workspace}（{ex.GetType().Name}）",
                "写文件工具需要工作区可写。检查目录权限，或把 DOUGONG_WORKSPACE 指向可写目录。");
        }
    }

    private static CheckResult CheckSessionDirectory()
    {
        var sessionDir = Environment.GetEnvironmentVariable("DOUGONG_SESSION_DIR")
            ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".dougong", "sessions");

        try
        {
            Directory.CreateDirectory(sessionDir);
            var probe = Path.Combine(sessionDir, $".probe-{Guid.NewGuid():N}");
            File.WriteAllText(probe, "probe");
            File.Delete(probe);
            return new(Level.Pass, "会话目录", $"{sessionDir}（可写）");
        }
        catch (Exception ex)
        {
            return new(Level.Fail, "会话目录",
                $"无法创建或写入：{sessionDir}（{ex.GetType().Name}）",
                "会话日志是提交屏障的落点，不可写会导致任何操作都无法继续。"
                + "检查权限，或用 DOUGONG_SESSION_DIR 指定另一个目录。");
        }
    }

    private static CheckResult CheckMemorySwitch()
    {
        var value = Environment.GetEnvironmentVariable("DOUGONG_MEMORY");

        if (value is null or "" or "0" or "false")
        {
            return new(Level.Pass, "L1 记忆",
                "未启用（默认）", "需要时设 DOUGONG_MEMORY=1。");
        }

        // 启用了记忆，就要能解析出目录——否则 CLI 会在启动时才失败。
        var dir = SessionComposition.MemoryDirectoryFromEnvironment(
            Environment.GetEnvironmentVariable("DOUGONG_SESSION_DIR")
                ?? Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    ".dougong", "sessions"));

        if (string.IsNullOrWhiteSpace(dir))
        {
            return new(Level.Fail, "L1 记忆",
                $"DOUGONG_MEMORY='{value}' 但没有解析出目录",
                "设 DOUGONG_MEMORY=1 使用默认目录，或直接给一个目录路径。");
        }

        try
        {
            Directory.CreateDirectory(dir);
            return new(Level.Warn, "L1 记忆",
                $"已启用，目录 {dir}",
                "记忆内容会进入后续每次 prompt，可能发给远端模型。确认这是你要的；"
                + "用 /memory 查看当前生效内容。");
        }
        catch (Exception ex)
        {
            return new(Level.Fail, "L1 记忆",
                $"记忆目录不可用：{dir}（{ex.GetType().Name}）",
                "改用 DOUGONG_MEMORY=<一个可写目录>，或设 DOUGONG_MEMORY=0 关闭。");
        }
    }

    /// <summary>
    /// 用最小请求验证 provider 真的能用。
    /// </summary>
    /// <remarks>
    /// 只发一次极短的请求。**失败不算致命**：离线环境、临时网络问题都会让它失败，
    /// 而 CLI 本身仍可启动（只是无法对话）。所以这里报 Warn 而不是 Fail——
    /// 报 Fail 会让离线用户以为安装坏了。
    /// </remarks>
    private static async Task<CheckResult> CheckProviderReachableAsync(CancellationToken cancellationToken)
    {
        var config = ChatClientFactory.FromEnvironment();
        var key = Environment.GetEnvironmentVariable("DOUGONG_PROVIDER_KEY");

        if (config is null || string.IsNullOrWhiteSpace(key))
        {
            return new(Level.Warn, "provider 连通性", "跳过（配置不完整）");
        }

        // 网络检查必须有超时：没有超时的"连通性检查"会在断网时挂住，
        // 而挂住比报错更糟——用户不知道是在等还是已经失败。
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));

        try
        {
            using var client = ChatClientFactory.Create(config, key);
            var response = await client.GetResponseAsync(
                "ping", new ChatOptions { MaxOutputTokens = 1 }, timeout.Token)
                .ConfigureAwait(false);

            _ = response;
            return new(Level.Pass, "provider 连通性", "可用（已发送一次最小请求）");
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            return new(Level.Warn, "provider 连通性",
                "20 秒内没有响应",
                "可能是网络不通或端点很慢。确认 DOUGONG_PROVIDER_BASE 可达；"
                + "离线环境下这条警告可以忽略。");
        }
        catch (Exception ex)
        {
            return new(Level.Warn, "provider 连通性",
                $"{ex.GetType().Name}: {Trim(ex.Message)}",
                ConnectivityHint(config, ex));
        }
    }

    /// <summary>
    /// 按错误码给出**针对性**的下一步，而不是一句笼统的"常见原因"。
    /// </summary>
    /// <remarks>
    /// 修的是一个真实缺口（2026-10-28，一位读者实测）：
    /// 他用 <c>https://api.minimaxi.com/anthropic</c> 配 <c>openai</c> 协议，
    /// 拿到 404。当时的提示只说"密钥无效、模型名不对、端点路径错"——
    /// **漏掉了最可能的那个原因：地址形状与协议不匹配**。
    /// 把两者配错的后果是拼出 <c>.../anthropic/chat/completions</c> 这种
    /// 不存在的路径，而提示里完全没提这一层。
    /// </remarks>
    private static string ConnectivityHint(ChatProviderConfig config, Exception error)
    {
        var message = error.Message;
        var baseUrl = config.BaseUrl ?? string.Empty;

        // 地址里有 /anthropic 但协议不是 anthropic，或反过来——这是 404 的头号原因。
        var pathSaysAnthropic = baseUrl.Contains("/anthropic", StringComparison.OrdinalIgnoreCase);
        var protocolIsAnthropic = config.Protocol is ChatProtocol.AnthropicMessages;

        if (pathSaysAnthropic != protocolIsAnthropic)
        {
            return $"{baseUrl} {(pathSaysAnthropic ? "带" : "不带")} /anthropic，"
                + $"而协议是 {config.Protocol}。**这两者必须配套**："
                + (pathSaysAnthropic
                    ? "地址带 /anthropic 时应设 DOUGONG_PROVIDER_PROTOCOL=anthropic。"
                    : "地址不带 /anthropic 时应使用默认的 openai 协议，"
                      + "删掉 DOUGONG_PROVIDER_PROTOCOL 或设为 openai。")
                + "改完重跑 --doctor。";
        }

        if (message.Contains("404", StringComparison.Ordinal))
        {
            return "地址路径对不上。检查 BASE 该不该带 /v1（不同服务商不一样），"
                + "以及协议是否与该端点的形状匹配。见 getting-started.md §6.3 的排查顺序。";
        }

        if (message.Contains("401", StringComparison.Ordinal))
            return "密钥不对或已失效。重新复制一次，注意别带前后空格"
                + "（cmd 的 set 加引号会把引号也存进去）。";

        if (message.Contains("402", StringComparison.Ordinal))
            return "账户余额不足，与配置无关。充值或换一家 provider。";

        if (message.Contains("403", StringComparison.Ordinal))
            return "该密钥没有这个模型的权限。换模型名，或确认订阅状态。";

        return "CLI 仍可启动，但对话会失败。常见原因：密钥无效、模型名不对、端点路径错。"
            + "确认后可用一次真实对话验证。";
    }

    private static string Trim(string text)
    {
        var single = text.ReplaceLineEndings(" ").Trim();
        return single.Length <= 160 ? single : single[..160] + "…";
    }

    private static void Print(IReadOnlyList<CheckResult> results)
    {
        Output.Line("dougong 启动自检");
        Output.Line();

        foreach (var r in results)
        {
            var mark = r.Level switch
            {
                Level.Pass => "通过",
                Level.Warn => "警告",
                _ => "失败",
            };

            Output.Line($"  [{mark}] {r.Name}：{r.Detail}");

            if (r.Next is { Length: > 0 })
                Output.Line($"         下一步：{r.Next}");
        }

        Output.Line();

        var failed = results.Count(r => r.Level is Level.Fail);
        var warned = results.Count(r => r.Level is Level.Warn);

        if (failed > 0)
        {
            Output.Error($"结论：{failed} 项失败，无法正常启动。按上面的「下一步」逐项处理后重跑本品。");
        }
        else if (warned > 0)
        {
            Output.Line($"结论：可以启动，但有 {warned} 项警告。");
        }
        else
        {
            Output.Line("结论：全部通过。可以直接运行 dougong 开始对话。");
        }
    }
}
