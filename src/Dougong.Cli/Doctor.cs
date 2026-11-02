using Dougong.Ai;
using Dougong.Agent;
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
            CheckInstructions(),
            CheckSessionDirectory(),
            CheckMemorySwitch(),
            CheckCompactionConfig(),
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
        // 走与 REPL/ACP 同一个解析路径，而不是各读一遍环境变量。
        // 分开读的话，自检说"配置没问题"而实际启动失败是必然会发生的事。
        var resolveError = ChatClientFactory.TryResolve(null, out var catalog, out _);
        if (resolveError is not null)
        {
            return new(Level.Fail, "provider 配置", resolveError,
                "修好配置文件后重跑 --doctor。未知键、非法地址、把密钥值写进 key_env、"
                + "上下文装不下输出，都会报在这里。");
        }

        if (catalog is null)
        {
            return new(Level.Fail, "provider 配置",
                "缺少 provider 配置：没有任何可用的 provider",
                "二选一：\n"
                + $"    · 写配置文件 {ProviderConfigFile.DefaultPath}（推荐），形如：\n"
                + "        default_provider = \"deepseek\"\n"
                + "        [providers.deepseek]\n"
                + "        base_url = \"https://api.deepseek.com\"\n"
                + "        model = \"deepseek-chat\"\n"
                + "        key = \"DEEPSEEK_API_KEY\"\n"
                + "    · 或者只是想临时试试某个端点：设 DOUGONG_PROVIDER_BASE / MODEL / KEY");
        }

        var selected = catalog.Selected;

        if (string.IsNullOrWhiteSpace(selected.Model))
        {
            return new(Level.Fail, "provider 配置",
                $"provider '{selected.Name}' 没有配置 model",
                $"在配置文件里给 [providers.{selected.Name}] 加 model = \"<模型名>\"，"
                + "或设 DOUGONG_PROVIDER_MODEL。");
        }

        if (!Uri.TryCreate(selected.BaseUrl, UriKind.Absolute, out var uri)
            || (uri.Scheme != "http" && uri.Scheme != "https"))
        {
            return new(Level.Fail, "provider 配置",
                $"base 不是合法的 http(s) 地址：{selected.BaseUrl}",
                "检查该 provider 的 base_url，它应当形如 https://host/v1。");
        }

        // 密钥状态：只说有没有找到，**绝不打印值**。
        //
        // **用 ResolveApiKey 判断，不自己查凭据文件**：那是唯一真源。
        // 自建一份判断会漏掉 DOUGONG_PROVIDER_* 那条通道——
        // 症状是"环境变量明明设了、doctor 却说没配"，而实际能正常对话。
        var hasKey = !string.IsNullOrWhiteSpace(ChatClientFactory.ResolveApiKey(selected));

        if (selected.ApiKeyName is { Length: > 0 } && !hasKey)
        {
            return new(Level.Fail, "provider 配置",
                $"provider '{selected.Name}' 需要密钥，但还没配",
                "重跑 dougong model 会提示你粘贴密钥，之后自动保存，不用每次设置。");
        }

        var others = catalog.Providers.Count - 1;
        var keyState = selected.ApiKeyName switch
        {
            null or "" => hasKey ? "已设置" : "无需密钥",
            _ => hasKey ? "已设置" : "未设置",
        };

        var detail = $"当前 '{selected.Name}'：model={selected.Model}  "
            + $"protocol={selected.Protocol}  base={selected.BaseUrl}  key={keyState}"
            + (others > 0 ? $"；另有 {others} 个可选（/model 查看）" : string.Empty)
            + $"；来源={catalog.SourcePath}";

        return new(Level.Pass, "provider 配置", detail);
    }

    /// <summary>
    /// 工作区检查：目录存在即可通过；不可写只是警告。
    /// </summary>
    /// <remarks>
    /// <b>不可写不该判为失败</b>。实测过：在 <c>C:\WINDOWS\system32</c> 下启动，
    /// 程序完全正常——能对话、能读文件、状态全部写到 <c>~/.dougong</c>。
    /// 只有写文件工具会因路径守卫拒绝，那是**功能受限**，不是"无法正常启动"。
    /// <para>
    /// 参考项目 hermes 更彻底：它**完全不检查**启动目录的可写性。
    /// 原因也一样——状态写在 <c>HERMES_HOME</c>，启动目录不可写不构成障碍。
    /// 全部源码里没有一处 <c>os.access(cwd, W_OK)</c>。
    /// </para>
    /// <para>
    /// 判为失败的实际后果是：用户明明能用，doctor 却说"无法正常启动"，
    /// 于是他开始怀疑一个完全正常的配置。那比不检查更糟。
    /// </para>
    /// </remarks>
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
            // 警告而不是失败：能启动、能对话，只是写文件工具用不了。
            return new(Level.Warn, "工作区",
                $"只读：{workspace}（{ex.GetType().Name}）——可以对话，但写文件会被拒",
                BuildWorkspaceHint(workspace));
        }
    }

    /// <summary>
    /// 工作区不可写时给一句能直接照做的话。
    /// </summary>
    /// <remarks>
    /// <b>先说"还能做什么"，再说"要改什么"</b>：用户此时确实能对话，
    /// 一上来只讲限制会让他以为程序坏了。
    /// <para>
    /// <b>在系统目录下启动的最常见原因是管理员 PowerShell</b>：
    /// 它的默认目录就是 <c>C:\WINDOWS\system32</c>，用户往往没注意到。
    /// 指出这一点比让他去查目录权限有用得多。
    /// </para>
    /// </remarks>
    private static string BuildWorkspaceHint(string workspace)
    {
        var normalized = workspace.TrimEnd(
            Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        var isSystemDirectory =
            normalized.Contains(@"\Windows\system32", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals(@"C:\Windows", StringComparison.OrdinalIgnoreCase)
            || normalized == "/" || normalized == "/usr" || normalized == "/etc";

        const string canStill =
            "对话、读文件、会话记录都正常（它们不在这个目录里）。";

        if (isSystemDirectory)
        {
            return canStill
                + $"\n    这里看起来是系统目录——用管理员身份开 PowerShell 时默认就在 system32。"
                + "\n    要让它能改文件：先 cd 到你的项目目录，例如 cd C:\\work\\myProject\\dougong";
        }

        return canStill
            + "\n    要让它能改文件：先 cd 到你的项目目录，或设 DOUGONG_WORKSPACE=<可写目录>";
    }

    /// <summary>
    /// 检查工作区与全局目录下的指令文件是否**可读**。
    /// </summary>
    /// <remarks>
    /// <b>这条检查补的是一个静默缺口</b>：<see cref="WorkspaceInstructionsSection"/>
    /// 读不到文件时只能跳过——<c>ISystemPromptSection</c> 只有"产出或不产出"两种结果，
    /// 没有诊断通道。于是权限错误会表现为"我写了 AGENTS.md 但约定没生效"，
    /// 而用户没有任何地方能看到原因。这里是那个缺口唯一的出口。
    /// <para>
    /// <b>只查可读，不查内容</b>：内容为空、太长、写得不好都不是 doctor 该判的事。
    /// </para>
    /// <para>
    /// <b>有问题时是警告不是失败</b>：约定文件是增强，不是启动前提。
    /// </para>
    /// </remarks>
    private static CheckResult CheckInstructions()
    {
        var workspace = Environment.GetEnvironmentVariable("DOUGONG_WORKSPACE")
            ?? Directory.GetCurrentDirectory();

        var found = new List<string>();
        var unreadable = new List<string>();

        // 全局链优先：它出问题时症状与项目链一样，但原因完全不同
        // （一个是 ~/.dougong 权限，一个是项目目录权限）。
        var places = new List<string> { DougongHome.Root };

        // **两个目录相同时只查一次**：否则同一个文件会被列成
        // "全局 AGENTS.md、项目 AGENTS.md" 两条，而实际只加载了一次——
        // 诊断信息与实现脱节比不诊断更糟，用户会去找第二个文件。
        if (!SamePath(workspace, DougongHome.Root)) places.Add(workspace);

        foreach (var directory in places)
        {
            foreach (var name in WorkspaceInstructionsSection.FileNames)
            {
                var path = Path.Combine(directory, name);
                if (!File.Exists(path)) continue;

                try
                {
                    // 真读一次：只判 File.Exists 查不出 ACL 与独占锁。
                    using var stream = File.OpenRead(path);
                    found.Add(Describe(path, directory));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    unreadable.Add($"{Describe(path, directory)}（{ex.GetType().Name}）");
                }
            }
        }

        if (unreadable.Count > 0)
        {
            return new(Level.Warn, "指令文件",
                $"找到但读不到：{string.Join("；", unreadable)}",
                "这些文件的内容不会进入提示词——你会看到「我写了约定但没生效」。"
                + "检查文件权限，或确认没有其他程序独占它。");
        }

        return found.Count > 0
            ? new(Level.Pass, "指令文件", $"已加载：{string.Join("、", found)}")
            : new(Level.Pass, "指令文件", "无（不产出「约定」节）");
    }

    /// <summary>两个路径是否指向同一目录。</summary>
    /// <remarks>
    /// 用 <see cref="Path.GetFullPath(string)"/> 规范化后比较：<c>~/.dougong</c>
    /// 与环境变量可能一个带尾斜杠、一个不带。规范化失败时按"不同"处理——
    /// 宁可多查一次，也不要在路径奇怪时漏掉项目目录。
    /// </remarks>
    private static bool SamePath(string a, string b)
    {
        try
        {
            return string.Equals(
                Path.GetFullPath(a).TrimEnd(Path.DirectorySeparatorChar),
                Path.GetFullPath(b).TrimEnd(Path.DirectorySeparatorChar),
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    /// <summary>把路径显示成不泄漏完整用户目录的形态。</summary>
    private static string Describe(string path, string directory)
    {
        var prefix = string.Equals(directory, DougongHome.Root, StringComparison.Ordinal)
            ? "全局"
            : "项目";
        return $"{prefix} {Path.GetFileName(path)}";
    }

    private static CheckResult CheckSessionDirectory()
    {        var sessionDir = Dougong.Ai.DougongHome.SessionsDirectory;

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
                + "检查权限，或设 DOUGONG_HOME 指向另一个位置。");
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
        var dir = SessionComposition.ResolveMemoryDirectory(
            Dougong.Ai.DougongHome.SessionsDirectory);

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
    /// 上下文压缩配置是否自洽。
    /// </summary>
    /// <remarks>
    /// <b>为什么必须有这一项</b>：窗口填得比输出预算还小时，
    /// 真正抛异常发生在每轮调模型之前的压缩检查里，用户视角是"对话崩了"，
    /// 而报错完全看不出是配置问题。实测踩到过：桌面端为此自己写了一遍校验，
    /// 并在源码里硬编码了一份预留量——宿主各写一遍就会漂移。
    /// <para>
    /// 判据统一走 <see cref="ContextBudgetValidation.Check"/>，
    /// 与桌面端装配期用的是同一个方法，不是两份实现。
    /// </para>
    /// </remarks>
    private static CheckResult CheckCompactionConfig()
    {
        const string name = "上下文压缩";

        // **先看配置文件，再看环境变量**——与启动路径一致。
        //
        // 只读环境变量会得出"未启用"，而启动时读的是配置文件（显示 1048576）。
        // 同一个配置在两处报出不同结论，用户只能怀疑其中一个是错的。
        // 这与 provider 连通性那条是同一类缺陷：自检自建了一套读取逻辑。
        var fromConfig = ChatClientFactory.TryResolve(null, out var configured, out _) is null
            ? configured?.Selected.ContextWindowTokens ?? 0
            : 0;

        var raw = Environment.GetEnvironmentVariable("DOUGONG_CONTEXT_WINDOW");

        // 设了但不是正整数：必须报出来，不能当成"未配置"。
        // 静默回退到"不压缩"会让用户以为配置生效了。
        if (!string.IsNullOrWhiteSpace(raw)
            && (!int.TryParse(raw, out var parsed) || parsed <= 0))
        {
            return new(Level.Fail, name,
                $"DOUGONG_CONTEXT_WINDOW='{raw}' 不是正整数",
                "填模型的实际上下文窗口，例如 1000000；不想压缩就删掉这个变量。");
        }

        var window = int.TryParse(raw, out var value) && value > 0 ? value : fromConfig;

        // 比例配置无论窗口开没开都要校验：写错了就该报，而不是等开了窗口才炸。
        if (!SessionComposition.TryReadCompactionRatios(
                out var triggerRatio, out var retainRatio, out var ratioError))
        {
            return new(Level.Fail, name, $"压缩比例配置无效：{ratioError}",
                "DOUGONG_COMPACT_TRIGGER_RATIO 与 DOUGONG_COMPACT_RETAIN_RATIO 都要在 (0, 1)，"
                + "且保留 < 触发；删掉变量即用默认值。");
        }

        var verdict = ContextBudgetValidation.Check(window, SessionComposition.EffectiveRequestOutputReserve());

        if (!verdict.IsUsable)
        {
            return new(Level.Fail, name, verdict.Detail,
                verdict.Hint ?? "调整 DOUGONG_CONTEXT_WINDOW。");
        }

        // 未启用压缩是合法状态，但不是"通过"——用户可能以为自己开了。
        if (!verdict.IsEnabled)
        {
            return new(Level.Pass, name, verdict.Detail, verdict.Hint);
        }

        // 策略必须报出来：默认值不是"没有配置"，而是一个具体选择
        // （触发 75% / 保留 16%）。不说就等于让用户以为自己没被策略支配。
        //
        // 去掉 Detail 末尾的句号再接分隔号：它自带 "。"，直接拼会得到 "。；"。
        var detail = verdict.Detail.TrimEnd('。');
        return new(Level.Pass, name,
            $"{detail}；策略 触发 {PercentText.Format(triggerRatio)} / 保留 {PercentText.Format(retainRatio)}");
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
        // **必须走与启动完全相同的解析路径**（TryResolve → 配置文件 + 环境变量覆盖）。
        //
        // 这里此前只读 `DOUGONG_PROVIDER_*` 环境变量，于是用配置文件配好的用户
        // 永远看到"跳过（配置不完整）"——而实际能正常对话。
        // 那种"自检说没配、实际能用"的报错比不检查更糟：它教用户怀疑一个正常的配置。
        var resolveError = ChatClientFactory.TryResolve(null, out var catalog, out _);
        if (resolveError is not null || catalog is null)
        {
            return new(Level.Warn, "provider 连通性", "跳过（没有可用的 provider 配置）");
        }

        var config = catalog.Selected;
        var key = ChatClientFactory.ResolveApiKey(config);

        if (!string.IsNullOrWhiteSpace(config.ApiKeyName) && string.IsNullOrWhiteSpace(key))
        {
            return new(Level.Warn, "provider 连通性", "跳过（还没有密钥）",
                "重跑 dougong model 输入密钥。");
        }

        // 网络检查必须有超时：没有超时的"连通性检查"会在断网时挂住，
        // 而挂住比报错更糟——用户不知道是在等还是已经失败。
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));

        try
        {
            using var client = ChatClientFactory.Create(config, key, OutboundHttp.Create(Log.Warning));
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
