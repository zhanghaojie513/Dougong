using System.Diagnostics;
using System.Text;
using Dougong.Ai;

namespace Dougong.Agent.Comparison;

/// <summary>
/// 多 provider 配置的验证。重点不是"能读 TOML"，而是三件事：
/// 密钥永不落盘、环境变量确实优先、错误配置必须指名道姓。
/// </summary>
/// <remarks>
/// <b>为什么"密钥不落盘"必须被验证而不是靠文档承诺</b>：本项目已经出现过
/// "能力只在文档里"的情况（G1）。密钥这条尤其不能只写进文档——
/// 一旦泄漏，用户不会有机会看到第二次提示。
/// 所以本套的核心手法是：把密钥设成一个唯一的标记串，跑完整流程，
/// 再断言输出与文件里**都不含**那个标记。
/// </remarks>
public static class ProviderChecks
{
    public static async Task<int> RunAsync()
    {
        var failures = 0;
        failures += await CheckAsync("config-file-is-parsed", ConfigFileIsParsed);
        failures += await CheckAsync("secret-value-in-key-env-is-rejected", SecretInKeyEnvRejected);
        failures += await CheckAsync("unknown-keys-are-named", UnknownKeysAreNamed);
        failures += await CheckAsync("env-vars-override-config", EnvOverridesConfig);
        failures += await CheckAsync("model-command-lists-and-switches", ModelCommandAsync);
        failures += await CheckAsync("secret-never-appears-in-doctor-output", SecretNeverPrintedAsync);
        failures += await CheckAsync("presets-are-data-only", PresetsAreDataOnly);
        failures += await CheckAsync("protocol-inference-three-levels", ProtocolInferenceAsync);
        failures += await CheckAsync("capabilities-and-fallback", CapabilitiesAndFallbackAsync);
        failures += await CheckAsync("model-command-preserves-comments", PreservesCommentsAsync);
        failures += await CheckAsync("model-command-creates-config", CreatesConfigAsync);
        failures += await CheckAsync("model-command-refuses-broken-config", RefusesBrokenConfigAsync);
        failures += await CheckAsync("model-command-adds-missing-provider", AddsMissingProviderAsync);
        failures += await CheckAsync("model-command-writes-chosen-base-url", WritesChosenBaseUrlAsync);

        Console.WriteLine(failures == 0
            ? "PROVIDER: ALL PASS"
            : $"PROVIDER: {failures} FAILURE(S)");
        return failures;
    }

    /// <summary>完整配置能被解析，且选中项、来源、字段都对。</summary>
    private static Task<string?> ConfigFileIsParsed()
    {
        const string toml = """
            default_provider = "second"

            [providers.first]
            base_url = "https://a.example.com/v1"
            wire_api = "anthropic"
            key_env = "FIRST_KEY"
            model = "m1"

            [providers.second]
            base_url = "https://b.example.com"
            model = "m2"
            """;

        var path = WriteTempConfig(toml);
        try
        {
            var error = ProviderConfigFile.TryLoad(path, out var catalog);
            if (error is not null) return Task.FromResult<string?>($"合法配置却报错：{error}");
            if (catalog is null) return Task.FromResult<string?>("合法配置没有解析出目录");

            if (catalog.SelectedName != "second")
                return Task.FromResult<string?>($"default_provider 未生效，选中了 {catalog.SelectedName}");

            // wire_api 缺省必须是 chat-completions：那是绝大多数兼容端点的形状。
            if (catalog.Selected.Protocol != ChatProtocol.OpenAiChatCompletions)
            {
                return Task.FromResult<string?>(
                    $"未指定 wire_api 时应为 chat-completions，实际 {catalog.Selected.Protocol}");
            }

            var first = catalog.Find("first");
            if (first is null) return Task.FromResult<string?>("provider 'first' 不见了");
            if (first.Protocol != ChatProtocol.AnthropicMessages)
                return Task.FromResult<string?>($"wire_api=anthropic 没生效，实际 {first.Protocol}");
            if (first.MessagesPath is not null)
                return Task.FromResult<string?>("没写 messages_path 却有了值");

            return Task.FromResult<string?>(null);
        }
        finally { File.Delete(path); }
    }

    /// <summary>
    /// 把密钥值写进 key_env 必须被拒。
    /// </summary>
    /// <remarks>
    /// <b>这是本套最重要的一条</b>：用户最容易犯的错就是把 sk-xxx 直接写进
    /// key_env。如果只把"应该写变量名"写进文档，那么配错的人在泄漏发生前
    /// 不会看到任何提示——而泄漏一旦发生（提交进 git），补救已经晚了。
    /// </remarks>
    private static Task<string?> SecretInKeyEnvRejected()
    {
        foreach (var secret in new[]
                 {
                     "sk-abc123def456",
                     "sk-proj-XXXXXXXXXXXXXXXX",
                     "abc/def",
                     "123456",
                 })
        {
            var toml = $"""
                [providers.x]
                base_url = "https://a.example.com"
                key_env = "{secret}"
                """;

            var error = ProviderConfigFile.TryParse(toml, "test.toml", out var catalog);
            if (error is null || catalog is not null)
                return Task.FromResult<string?>($"key_env='{secret}' 看起来是密钥值，却没被拒绝");

            if (!error.Contains("变量名", StringComparison.Ordinal))
            {
                return Task.FromResult<string?>(
                    $"拒绝了但没说清原因（应提示要写变量名）：{error}");
            }
        }

        // 反向对照：合法的变量名不能被误拒，否则这个校验会阻碍正常使用。
        foreach (var name in new[] { "DEEPSEEK_API_KEY", "_PRIVATE", "A1_B2" })
        {
            var toml = $"""
                [providers.x]
                base_url = "https://a.example.com"
                key_env = "{name}"
                """;

            var error = ProviderConfigFile.TryParse(toml, "test.toml", out _);
            if (error is not null)
                return Task.FromResult<string?>($"合法变量名 '{name}' 被误拒：{error}");
        }

        return Task.FromResult<string?>(null);
    }

    /// <summary>
    /// 未知的顶层键与 provider 键都必须被点名报出。
    /// </summary>
    /// <remarks>
    /// 静默忽略是最难查的一类问题：用户以为改了配置，实际那一行从未被读过。
    /// </remarks>
    private static Task<string?> UnknownKeysAreNamed()
    {
        var topLevel = ProviderConfigFile.TryParse(
            """
            bogus = 1
            [providers.x]
            base_url = "https://a.example.com"
            """, "t.toml", out _);

        if (topLevel is null || !topLevel.Contains("bogus", StringComparison.Ordinal))
            return Task.FromResult<string?>($"未知顶层键没被点名：{topLevel ?? "(未报错)"}");

        var providerKey = ProviderConfigFile.TryParse(
            """
            [providers.x]
            base_url = "https://a.example.com"
            key_envv = "TYPO"
            """, "t.toml", out _);

        if (providerKey is null || !providerKey.Contains("key_envv", StringComparison.Ordinal))
            return Task.FromResult<string?>($"provider 内的未知键没被点名：{providerKey ?? "(未报错)"}");

        return Task.FromResult<string?>(null);
    }

    /// <summary>
    /// 环境变量必须覆盖配置文件里的选中项。
    /// </summary>
    /// <remarks>
    /// 同时要验证**不丢失**配置文件里的其它 provider：若另起一个目录，
    /// <c>/model</c> 的列表会凭空少一批，用户会以为配置没被读到。
    /// </remarks>
    private static Task<string?> EnvOverridesConfig()
    {
        var toml = """
            default_provider = "fromfile"

            [providers.fromfile]
            base_url = "https://file.example.com"
            model = "file-model"
            key_env = "FILE_KEY"

            [providers.other]
            base_url = "https://other.example.com"
            model = "other-model"
            """;

        var path = WriteTempConfig(toml);
        var saved = CaptureProviderEnvironment();
        try
        {
            Environment.SetEnvironmentVariable("DOUGONG_PROVIDER_BASE", "https://env.example.com");
            Environment.SetEnvironmentVariable("DOUGONG_PROVIDER_MODEL", "env-model");

            var error = ChatClientFactory.TryResolve(path, out var catalog, out _);
            if (error is not null) return Task.FromResult<string?>($"解析失败：{error}");
            if (catalog is null) return Task.FromResult<string?>("没有解析出目录");

            if (catalog.Selected.Model != "env-model")
                return Task.FromResult<string?>($"环境变量未优先，选中模型是 {catalog.Selected.Model}");

            if (catalog.Selected.BaseUrl != "https://env.example.com")
                return Task.FromResult<string?>($"环境变量未优先，base 是 {catalog.Selected.BaseUrl}");

            // 配置文件里另外那些 provider 必须还在。
            if (catalog.Find("other") is null)
                return Task.FromResult<string?>("环境变量覆盖后，配置文件里的其它 provider 丢了");

            if (!(catalog.SourcePath ?? string.Empty).Contains("环境变量", StringComparison.Ordinal))
            {
                return Task.FromResult<string?>(
                    $"来源没标出被环境变量覆盖：{catalog.SourcePath}");
            }

            return Task.FromResult<string?>(null);
        }
        finally
        {
            RestoreProviderEnvironment(saved);
            File.Delete(path);
        }
    }

    /// <summary>
    /// <c>/model</c> 能列全部 provider，且切换后选中项跟着变。
    /// </summary>
    private static async Task<string?> ModelCommandAsync()
    {
        const string secret = "sk-MARKER-MODEL-CMD-9911";
        var toml = $"""
            default_provider = "alpha"

            [providers.alpha]
            base_url = "https://a.example.com/v1"
            model = "alpha-model"
            key_env = "ALPHA_KEY"

            [providers.beta]
            base_url = "https://b.example.com/v1"
            model = "beta-model"
            key_env = "BETA_KEY"

            [providers.gamma]
            base_url = "https://c.example.com/v1"
            key_env = "GAMMA_KEY"
            """;

        var path = WriteTempConfig(toml);

        // 斜杠命令走 stdin，不是命令行参数：当成 argv 传会被
        // "未知参数"检查拒掉（这是对的——/model 是会话内命令）。
        var result = await RunCliAsync(
            [],
            new Dictionary<string, string?>
            {
                ["DOUGONG_PROVIDER_CONFIG"] = path,
                ["ALPHA_KEY"] = secret,
                ["BETA_KEY"] = secret,
                // GAMMA_KEY 故意不设：切过去必须被拒并说明缺什么。
            },
            stdin: "/model\n/model beta\n/model\n/model gamma\n/exit\n");

        try
        {
            var combined = result.Stdout + result.Stderr;

            // 列表必须包含配置里的与内置预设的 provider。
            foreach (var name in new[] { "alpha", "beta", "gamma", "deepseek" })
            {
                if (!combined.Contains(name, StringComparison.Ordinal))
                    return $"provider 列表里少了 '{name}'。stdout={Trim(combined)}";
            }

            if (!combined.Contains("已切到 provider 'beta'", StringComparison.Ordinal))
                return $"没有切到 beta。stdout={Trim(combined)}";

            // 缺密钥的 provider 必须被拒，且报出缺哪个变量。
            // 这条用"需要环境变量 GAMMA_KEY"匹配——它是报错文案里唯一
            // 既指明变量名又说明原因的部分。
            if (!combined.Contains("需要环境变量 GAMMA_KEY", StringComparison.Ordinal))
                return $"缺密钥时没有报出缺哪个变量。stdout={Trim(combined)}";

            if (combined.Contains("已切到 provider 'gamma'", StringComparison.Ordinal))
                return "缺密钥的 provider 竟然切换成功了";

            // gamma 同时缺 model 与密钥，两条必须**一起**报出来：
            // 只报一条的话用户补完再切一次才知道还缺点什么。
            if (!combined.Contains("没有配置 model", StringComparison.Ordinal))
                return $"缺模型的 provider 没有报出缺 model。stdout={Trim(combined)}";

            // 密钥值一次都不能出现在输出里。
            if (combined.Contains(secret, StringComparison.Ordinal))
                return "密钥值出现在了 /model 的输出里";

            return null;
        }
        finally { File.Delete(path); }
    }

    /// <summary>
    /// 整个 <c>--doctor</c> 输出里绝不能出现密钥值。
    /// </summary>
    /// <remarks>
    /// <b>为什么单独一条</b>：<c>--doctor</c> 是用户出问题时第一个跑的命令，
    /// 也是贴到 issue 里最可能的那段输出。它若带出密钥，
    /// 泄漏就是必然的。这条用唯一标记串断言"完全不含"，比逐处审查可靠。
    /// </remarks>
    private static async Task<string?> SecretNeverPrintedAsync()
    {
        const string secret = "sk-MARKER-DOCTOR-LEAK-4477";
        var toml = """
            default_provider = "probe"

            [providers.probe]
            base_url = "https://probe.example.com/v1"
            model = "probe-model"
            key_env = "PROBE_KEY"
            """;

        var path = WriteTempConfig(toml);
        try
        {
            var result = await RunCliAsync(
                ["--doctor"],
                new Dictionary<string, string?>
                {
                    ["DOUGONG_PROVIDER_CONFIG"] = path,
                    ["PROBE_KEY"] = secret,
                });

            var combined = result.Stdout + result.Stderr;

            if (combined.Contains(secret, StringComparison.Ordinal))
                return "--doctor 的输出里出现了密钥值";

            // 正向对照：必须确实检查到了 provider 这一项，否则"没泄漏"
            // 可能只是因为压根没读配置。
            if (!combined.Contains("provider 配置", StringComparison.Ordinal))
                return $"自检没有检查 provider 配置。stdout={Trim(combined)}";

            if (!combined.Contains("PROBE_KEY 已设置", StringComparison.Ordinal))
                return $"自检没有报出密钥变量已设置（说明没走到这条路径）。stdout={Trim(combined)}";

            // 配置文件本身当然也不该被写入密钥——这里确认读之后文件没变。
            var after = await File.ReadAllTextAsync(path);
            if (after.Contains(secret, StringComparison.Ordinal))
                return "配置文件中出现了密钥值（说明有回写）";

            return null;
        }
        finally { File.Delete(path); }
    }

    /// <summary>
    /// 内置预设只能含端点、协议、变量名——不能有模型名或密钥值。
    /// </summary>
    /// <remarks>
    /// 这条守的是 <c>goal.md</c> §3 的反目标边界：预设是纯数据行，不是
    /// "provider 矩阵"。一旦有人往里加厂商特例字段，这条会先红。
    /// <para>
    /// 也顺带守住"预设不带模型名"：同一端点的可用模型会变，
    /// 写死会让用户拿到 404 且看不出原因。
    /// </para>
    /// </remarks>
    private static Task<string?> PresetsAreDataOnly()
    {
        foreach (var (name, preset) in ProviderCatalog.Presets)
        {
            if (string.IsNullOrWhiteSpace(preset.BaseUrl))
                return Task.FromResult<string?>($"预设 '{name}' 没有 base_url");

            if (!Uri.TryCreate(preset.BaseUrl, UriKind.Absolute, out var uri)
                || (uri.Scheme != "http" && uri.Scheme != "https"))
            {
                return Task.FromResult<string?>($"预设 '{name}' 的 base_url 不是合法地址：{preset.BaseUrl}");
            }

            // 密钥变量名可以为 null（本地服务无需鉴权），但给了就必须像变量名。
            // 预设里出现密钥**值**是最严重的配置事故。
            var keyName = preset.ApiKeyEnvironmentVariable;
            if (keyName is not null
                && (keyName.Length == 0
                    || !(char.IsAsciiLetter(keyName[0]) || keyName[0] == '_')
                    || keyName.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '_')))
            {
                return Task.FromResult<string?>(
                    $"预设 '{name}' 的密钥变量名不像变量名：'{keyName}'");
            }
        }

        // **覆盖度**：列表的作用是"选得到"。此前只有 openai/anthropic/deepseek 三家，
        // 被指出"glm、kimi、mimo、minimax 呢"——国内可用的大模型根本不在列表里。
        // 这条钉住那几家必须存在，防止以后又被精简掉。
        string[] mustExist =
            ["deepseek", "glm", "kimi", "mimo", "minimax", "qwen", "doubao", "stepfun"];

        var missing = mustExist
            .Where(n => !ProviderCatalog.Presets.ContainsKey(n))
            .ToArray();

        if (missing.Length > 0)
            return Task.FromResult<string?>($"预设缺少国内常用厂商：{string.Join(", ", missing)}");

        // 预设必须真的被应用（否则上面那圈是空转）。
        var catalog = ProviderCatalog.WithOverrides([], null, "test");
        foreach (var name in ProviderCatalog.Presets.Keys)
        {
            if (catalog.Find(name) is null)
                return Task.FromResult<string?>($"预设 '{name}' 没有被应用进目录");
        }

        // 预设的模型必须为空：不能替用户猜模型名。
        foreach (var provider in catalog.Providers)
        {
            if (ProviderCatalog.Presets.ContainsKey(provider.Name)
                && !string.IsNullOrWhiteSpace(provider.Model))
            {
                return Task.FromResult<string?>($"预设 '{provider.Name}' 带了模型名，应当留空");
            }
        }

        return Task.FromResult<string?>(null);
    }

    /// <summary>
    /// <c>dougong model</c> 改值时必须**完整保留注释、空行与字段顺序**。
    /// </summary>
    /// <remarks>
    /// <b>为什么这条是硬要求而不是"尽量"</b>：配置文件是用户手写的，注释往往记着
    /// "为什么这么配"。如果一条配置命令顺手抹掉它们，用户会不敢再跑这个命令——
    /// 而"改模型"恰恰是最常做的事。
    /// <para>
    /// <b>为什么不能只断言"值改对了"</b>：整体重新序列化也能把值改对，
    /// 同时把注释全丢掉。所以这里逐条比对注释、空行位置与未触碰的键。
    /// </para>
    /// </remarks>
    private static async Task<string?> PreservesCommentsAsync()
    {
        const string original = """
            # 顶部说明：这段注释必须活着
            default_provider = "alpha"

            [providers.alpha]
            base_url = "https://a.example.com/v1"   # 行尾注释也要保留
            # 换模型前先确认这个值
            model = "old-model"
            key_env = "ALPHA_KEY"

            [providers.beta]
            base_url = "https://b.example.com/v1"
            model = "beta-model"
            """;

        var path = WriteTempConfig(original);
        try
        {
            var result = await RunCliAsync(
                ["model", "--set", "alpha", "--model", "new-model"],
                new Dictionary<string, string?> { ["DOUGONG_PROVIDER_CONFIG"] = path });

            if (result.ExitCode != 0)
                return $"退出码 {result.ExitCode}，stderr={Trim(result.Stderr)}";

            var after = await File.ReadAllTextAsync(path);

            if (!after.Contains("new-model", StringComparison.Ordinal))
                return $"值没改成功：{Trim(after)}";

            if (after.Contains("old-model", StringComparison.Ordinal))
                return $"旧值没被替换：{Trim(after)}";

            // 逐一断言用户写下的三处注释都还在。
            foreach (var comment in new[]
                     {
                         "顶部说明：这段注释必须活着",
                         "行尾注释也要保留",
                         "换模型前先确认这个值",
                     })
            {
                if (!after.Contains(comment, StringComparison.Ordinal))
                    return $"注释被删掉了：「{comment}」。改动后={Trim(after)}";
            }

            // 空行数量不能变：整段重新序列化通常会把它们压掉。
            var blankBefore = original.Split('\n').Count(l => l.Trim().Length == 0);
            var blankAfter = after.Split('\n').Count(l => l.Trim().Length == 0);
            if (blankBefore != blankAfter)
                return $"空行数从 {blankBefore} 变成 {blankAfter}，格式被重排了";

            // 没碰的 provider 必须原样不动。
            if (!after.Contains("beta-model", StringComparison.Ordinal))
                return "未触碰的 provider 被改动了";

            // 改后的文件必须仍能解析，且解析出的值正确。
            var parseError = ProviderConfigFile.TryLoad(path, out var catalog);
            if (parseError is not null)
                return $"改完的配置解析失败：{parseError}";

            if (catalog!.Find("alpha")?.Model != "new-model")
                return $"解析出的 model 不是 new-model，而是 {catalog.Find("alpha")?.Model}";

            return null;
        }
        finally { File.Delete(path); }
    }

    /// <summary>
    /// 配置文件不存在时，<c>dougong model</c> 必须能建出一份可用的。
    /// </summary>
    /// <remarks>
    /// <b>为什么这条重要</b>：新用户的第一步就是它。要求他先手写一份 TOML
    /// 才能用上"配置模型"的命令，等于把这个命令的用途倒过来了。
    /// </remarks>
    private static async Task<string?> CreatesConfigAsync()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"dougong-new-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "config.toml");

        try
        {
            if (File.Exists(path)) File.Delete(path);

            // 交互式现在是四步：选 provider → 端点 → 密钥（跳过确认）→ 模型。
            // 选第 1 项（anthropic），端点回车接受默认，密钥那步回车接受跳过，
            // 最后手填模型名（端点无密钥查不到列表，会走手填分支）。
            var result = await RunCliAsync(
                ["model"],
                new Dictionary<string, string?> { ["DOUGONG_PROVIDER_CONFIG"] = path },
                stdin: "1\n\n\nmy-test-model\n");

            if (!File.Exists(path))
                return $"没有创建配置文件。stdout={Trim(result.Stdout)} stderr={Trim(result.Stderr)}";

            var parseError = ProviderConfigFile.TryLoad(path, out var catalog);
            if (parseError is not null) return $"新建的配置解析失败：{parseError}";
            if (catalog is null) return "新建的配置没有解析出目录";

            if (catalog.Selected.Model != "my-test-model")
                return $"模型名没写进去：{catalog.Selected.Model}";

            if (string.IsNullOrWhiteSpace(catalog.Selected.BaseUrl))
                return "新建的配置缺 base_url";

            // 缺密钥必须当场提示，不能等下次对话才 401。
            if (!result.Stdout.Contains("还差密钥", StringComparison.Ordinal))
                return $"没有提示缺少密钥。stdout={Trim(result.Stdout)}";

            return null;
        }
        finally
        {
            try { File.Delete(path); Directory.Delete(dir, recursive: true); } catch { /* 清理失败不影响结论 */ }
        }
    }

    /// <summary>
    /// 配置文件有语法错时必须拒绝改动，而不是"照着自己的理解重写一份"。
    /// </summary>
    /// <remarks>
    /// 原文件坏了还去写回，会把错误固化下来，用户再也分不清是自己写错的
    /// 还是工具改坏的。
    /// </remarks>
    private static async Task<string?> RefusesBrokenConfigAsync()
    {
        const string broken = "[providers.x\nbase_url =";
        var path = WriteTempConfig(broken);
        try
        {
            var result = await RunCliAsync(
                ["model", "--set", "x", "--model", "m"],
                new Dictionary<string, string?> { ["DOUGONG_PROVIDER_CONFIG"] = path });

            if (result.ExitCode == 0)
                return "配置有语法错却返回成功";

            // 文件必须一个字节都没变。
            var after = await File.ReadAllTextAsync(path);
            if (after != broken)
                return $"坏掉的配置被改写了：{Trim(after)}";

            var combined = result.Stdout + result.Stderr;
            if (!combined.Contains("TOML", StringComparison.Ordinal))
                return $"没说明是 TOML 语法问题：{Trim(combined)}";

            return null;
        }
        finally { File.Delete(path); }
    }

    /// <summary>
    /// 选中一个配置里还没有的 provider（内置预设）时必须**新建那一段**。
    /// </summary>
    /// <remarks>
    /// <b>这条是用户实际反馈的 bug</b>：列表把 <c>anthropic</c> 这类内置预设列出来了，
    /// 选中后却报"配置里没有 [providers.anthropic] 段，未改动"，然后直接结束。
    /// 表现出来就是"选完 provider 还没输入模型名就退出了"。
    /// <para>
    /// 把没配过的项列进菜单却不让选，等于把选择权给了用户又收回去。
    /// </para>
    /// </remarks>
    private static async Task<string?> AddsMissingProviderAsync()
    {
        const string original = """
            # 保留这句话
            default_provider = "mine"

            [providers.mine]
            base_url = "https://mine.example.com/v1"
            model = "mine-model"
            """;

        var path = WriteTempConfig(original);
        try
        {
            var result = await RunCliAsync(
                ["model", "--set", "anthropic", "--model", "claude-x"],
                new Dictionary<string, string?> { ["DOUGONG_PROVIDER_CONFIG"] = path });

            if (result.ExitCode != 0)
                return $"退出码 {result.ExitCode}：{Trim(result.Stdout + result.Stderr)}";

            var after = await File.ReadAllTextAsync(path);

            if (!after.Contains("[providers.anthropic]", StringComparison.Ordinal))
                return $"没有新建 [providers.anthropic] 段：{Trim(after)}";

            if (!after.Contains("claude-x", StringComparison.Ordinal))
                return $"模型名没写进去：{Trim(after)}";

            // 原有内容与注释必须完好。
            if (!after.Contains("保留这句话", StringComparison.Ordinal))
                return "原有注释被删掉了";

            if (!after.Contains("mine-model", StringComparison.Ordinal))
                return "原有 provider 被破坏了";

            // 新段必须能被真正解析出来（拼坏的表头会在这里暴露）。
            var parseError = ProviderConfigFile.TryLoad(path, out var catalog);
            if (parseError is not null)
                return $"新增段后配置无法解析：{parseError}";

            var added = catalog!.Find("anthropic");
            if (added is null) return "解析不出新增的 anthropic";
            if (added.Model != "claude-x") return $"新增段的模型是 {added.Model}";
            if (string.IsNullOrWhiteSpace(added.BaseUrl)) return "新增段缺 base_url";

            return null;
        }
        finally { File.Delete(path); }
    }

    /// <summary>
    /// 交互式流程里用户改过的端点必须**写进文件**，不能继续用预设默认值。
    /// </summary>
    /// <remarks>
    /// <b>实测踩到的 bug</b>：向导显示的 base_url 是用户输入的，
    /// 但新建配置时仍写预设里的默认端点——用户看到的和文件里的不一致。
    /// 这种错误极难自查：配置看起来正常，只是连到了错的地方。
    /// </remarks>
    private static async Task<string?> WritesChosenBaseUrlAsync()
    {
        using var server = new FakeModelsServer(Action: null);
        var path = Path.Combine(
            Path.GetTempPath(), $"dougong-base-{Guid.NewGuid():N}.toml");

        try
        {
            if (File.Exists(path)) File.Delete(path);

            // 选第 1 项（anthropic，预设端点是 api.anthropic.com），
            // 但把 base_url 改成假服务器；密钥那步回车接受跳过；
            // 然后选第 1 个发现的模型。
            var result = await RunCliAsync(
                ["model"],
                new Dictionary<string, string?>
                {
                    ["DOUGONG_PROVIDER_CONFIG"] = path,
                    // 预设要求 ANTHROPIC_API_KEY；设一个假的让它往下走。
                    ["ANTHROPIC_API_KEY"] = "sk-fake-for-test",
                },
                stdin: $"1\n{server.BaseUrl}\n1\n");

            if (!File.Exists(path))
                return $"没有创建配置。stdout={Trim(result.Stdout)}";

            var after = await File.ReadAllTextAsync(path);

            if (!after.Contains(server.BaseUrl, StringComparison.Ordinal))
            {
                return $"用户输入的端点没写进配置（仍是预设值）。文件={Trim(after)}";
            }

            return null;
        }
        finally { try { File.Delete(path); } catch { /* 清理失败不影响结论 */ } }
    }

    /// <summary>起一个只实现 <c>/models</c> 的本地端点，用于验证模型发现。</summary>
    private sealed class FakeModelsServer : IDisposable
    {
        private readonly System.Net.HttpListener _listener;
        private readonly Task _loop;

        public FakeModelsServer(Action? Action)
        {
            // 端口用 0 让系统分配，避免与服务固定端口的检查抢端口。
            var port = Random.Shared.Next(18100, 18999);
            BaseUrl = $"http://127.0.0.1:{port}/v1";

            _listener = new System.Net.HttpListener();
            _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            _listener.Start();

            _loop = Task.Run(async () =>
            {
                while (_listener.IsListening)
                {
                    System.Net.HttpListenerContext context;
                    try { context = await _listener.GetContextAsync(); }
                    catch { return; }

                    const string body =
                        """{"object":"list","data":[{"id":"model-a"},{"id":"model-b"}]}""";
                    var bytes = System.Text.Encoding.UTF8.GetBytes(body);
                    context.Response.ContentType = "application/json";
                    context.Response.ContentLength64 = bytes.Length;
                    await context.Response.OutputStream.WriteAsync(bytes);
                    context.Response.Close();
                }
            });
        }

        public string BaseUrl { get; }

        public void Dispose()
        {
            try { _listener.Stop(); _listener.Close(); } catch { /* 已关闭 */ }
            try { _loop.Wait(TimeSpan.FromSeconds(2)); } catch { /* 忽略 */ }
        }
    }

    /// <summary>
    /// 协议三级推断：显式 > 端点形状 > 默认。
    /// </summary>
    /// <remarks>
    /// <b>为什么必须测</b>：MiniMax 的端点是 <c>https://api.minimax.io/anthropic</c>，
    /// 路径末尾就是 <c>/anthropic</c>。不推断的话它会被当成 chat-completions 发出去，
    /// 拿到一个含糊的 404——而用户完全看不出是自己少写了一行 <c>wire_api</c>。
    /// <para>
    /// <b>精确匹配主机名而不是子串</b>：<c>api.anthropic.com.attacker.test</c> 和
    /// <c>proxy.test/api.anthropic.com/v1</c> 都能骗过 Contains 判断。
    /// 这里专门测一个伪造主机不会被误判。
    /// </para>
    /// </remarks>
    private static Task<string?> ProtocolInferenceAsync()
    {
        // 1) 路径末尾 /anthropic → AnthropicMessages
        var bySuffix = ProviderConfigFile.TryParse("""
            default_provider = "x"

            [providers.x]
            base_url = "https://api.minimax.io/anthropic"
            model = "m"
            """, "t.toml", out var suffixCatalog);

        if (bySuffix is not null) return Task.FromResult<string?>($"路径推断失败：{bySuffix}");
        if (suffixCatalog!.Selected.Protocol != ChatProtocol.AnthropicMessages)
        {
            return Task.FromResult<string?>(
                $"/anthropic 结尾应推断为 AnthropicMessages，实际 {suffixCatalog.Selected.Protocol}");
        }

        // 2) 显式 wire_api 必须覆盖推断（同一端点写 openai）
        var explicitApi = ProviderConfigFile.TryParse("""
            default_provider = "x"

            [providers.x]
            base_url = "https://api.minimax.io/anthropic"
            wire_api = "openai"
            model = "m"
            """, "t.toml", out var explicitCatalog);

        if (explicitApi is not null) return Task.FromResult<string?>($"显式协议解析失败：{explicitApi}");
        if (explicitCatalog!.Selected.Protocol != ChatProtocol.OpenAiChatCompletions)
        {
            return Task.FromResult<string?>(
                $"显式 wire_api 应覆盖推断，实际 {explicitCatalog.Selected.Protocol}");
        }

        // 3) 伪造主机不得被误判为 Anthropic（子串匹配会中招）
        var spoofed = ProviderConfigFile.TryParse("""
            default_provider = "x"

            [providers.x]
            base_url = "https://api.minimax.io/anthropic.attacker.test/v1"
            model = "m"
            """, "t.toml", out var spoofCatalog);

        if (spoofed is not null) return Task.FromResult<string?>($"伪造主机解析失败：{spoofed}");
        if (spoofCatalog!.Selected.Protocol == ChatProtocol.AnthropicMessages)
        {
            return Task.FromResult<string?>("伪造主机被误判为 AnthropicMessages——匹配方式过于宽松");
        }

        // 4) 普通端点落到 chat-completions
        var plain = ProviderConfigFile.TryParse("""
            default_provider = "x"

            [providers.x]
            base_url = "https://api.example.com/v1"
            model = "m"
            """, "t.toml", out var plainCatalog);

        if (plain is not null) return Task.FromResult<string?>($"普通端点解析失败：{plain}");
        if (plainCatalog!.Selected.Protocol != ChatProtocol.OpenAiChatCompletions)
        {
            return Task.FromResult<string?>(
                $"普通端点应默认为 chat-completions，实际 {plainCatalog.Selected.Protocol}");
        }

        return Task.FromResult<string?>(null);
    }

    /// <summary>
    /// 能力表与三层回退：查到的用事实，查不到的用可改的起点。
    /// </summary>
    /// <remarks>
    /// <b>为什么回退值是必要的</b>：没有它时的结果是"上下文未知 → 压缩永不启用"，
    /// 而长会话一定会撞上 provider 上限——那时报的是 provider 的原始错误，
    /// 完全看不出是配置缺失。
    /// <para>
    /// <b>回退值必须仍为正数</b>：0 在压缩语义里是"不启用"，
    /// 与"还不知道"是两件事，混用会让症状更难查。
    /// </para>
    /// </remarks>
    private static Task<string?> CapabilitiesAndFallbackAsync()
    {
        // 表里有的：必须给出真实值，且与回退值不同（否则测不出是否真查了表）。
        var known = ProviderCatalog.FindModelCapabilities("deepseek", "deepseek-v4-pro");
        if (known is null)
            return Task.FromResult<string?>("表里应有 deepseek-v4-pro，却查不到");

        if (known.ContextWindow <= 0)
            return Task.FromResult<string?>($"deepseek-v4-pro 的上下文窗口是 {known.ContextWindow}");

        if (known.ContextWindow == ProviderCatalog.FallbackContextWindow)
        {
            return Task.FromResult<string?>(
                "deepseek-v4-pro 的上下文窗口恰好等于回退值，这条测不出是否真的查了表");
        }

        // 表里没有的：调用方必须能用回退值，且回退值本身合法。
        var unknown = ProviderCatalog.FindModelCapabilities("deepseek", "no-such-model-xyz");
        if (unknown is not null)
            return Task.FromResult<string?>("不存在的模型竟然查到了能力数据");

        // 用局部变量而不是直接比常量：编译器会把它折叠成常量条件并报"不可达代码"，
        // 而这条断言的意义正是"以后有人把常量改坏了要被拦住"。
        var fallbackContext = ProviderCatalog.FallbackContextWindow;
        var fallbackOutput = ProviderCatalog.FallbackMaxOutputTokens;

        if (fallbackContext <= 0 || fallbackOutput <= 0)
        {
            return Task.FromResult<string?>("回退值必须为正数（0 在压缩语义里是「不启用」）");
        }

        // 输出上限不能大于上下文窗口，否则压缩预算会算成负数。
        if (fallbackOutput >= fallbackContext)
        {
            return Task.FromResult<string?>(
                "回退的输出上限不得大于上下文窗口，否则有效输入预算为负");
        }

        return Task.FromResult<string?>(null);
    }

    // ── 支撑 ─────────────────────────────────────────────────────
    private static string WriteTempConfig(string toml)
    {
        var path = Path.Combine(
            Path.GetTempPath(), $"dougong-provider-{Guid.NewGuid():N}.toml");
        File.WriteAllText(path, toml);
        return path;
    }

    private static readonly string[] ProviderEnvNames =
    [
        "DOUGONG_PROVIDER_BASE", "DOUGONG_PROVIDER_MODEL", "DOUGONG_PROVIDER_KEY",
        "DOUGONG_PROVIDER_PROTOCOL", "DOUGONG_PROVIDER_NAME", "DOUGONG_ANTHROPIC_PATH",
    ];

    private static Dictionary<string, string?> CaptureProviderEnvironment() =>
        ProviderEnvNames.ToDictionary(n => n, Environment.GetEnvironmentVariable);

    private static void RestoreProviderEnvironment(Dictionary<string, string?> saved)
    {
        foreach (var (name, value) in saved)
            Environment.SetEnvironmentVariable(name, value);
    }

    private sealed record CliResult(int ExitCode, string Stdout, string Stderr);

    /// <summary>
    /// 起真实 CLI 进程。环境先清空 provider 相关变量再注入，
    /// 否则跑测试的机器上已设的值会让断言假通过。
    /// </summary>
    /// <param name="stdin">写入标准输入的内容；null 表示不写（非交互模式）。</param>
    private static async Task<CliResult> RunCliAsync(
        string[] arguments, IReadOnlyDictionary<string, string?> environment,
        string? stdin = null)
    {
        var cliDll = Path.Combine(
            FindRepoRoot(), "src", "Dougong.Cli", "bin", "Release", "net10.0", "Dougong.Cli.dll");

        if (!File.Exists(cliDll))
            throw new InvalidOperationException($"找不到 CLI 产物：{cliDll}（先构建）");

        var startInfo = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        startInfo.ArgumentList.Add(cliDll);
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);

        foreach (var name in ProviderEnvNames.Concat(
                     new[] { "DOUGONG_PROVIDER_CONFIG", "DOUGONG_KERNEL", "DOUGONG_TUI" }))
        {
            startInfo.Environment.Remove(name);
        }

        foreach (var (key, value) in environment)
        {
            if (value is null) startInfo.Environment.Remove(key);
            else startInfo.Environment[key] = value;
        }

        using var process = new Process { StartInfo = startInfo };
        process.Start();

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();

        // 先写 stdin 再等退出：不写的话 REPL 会一直等输入，直到超时。
        if (stdin is not null)
        {
            await process.StandardInput.WriteAsync(stdin).ConfigureAwait(false);
        }
        process.StandardInput.Close();

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* 已退出 */ }
            return new CliResult(-1, await stdoutTask.ConfigureAwait(false), "超时");
        }

        return new CliResult(
            process.ExitCode,
            await stdoutTask.ConfigureAwait(false),
            await stderrTask.ConfigureAwait(false));
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Dougong.slnx")))
            dir = dir.Parent;

        return dir?.FullName
            ?? throw new InvalidOperationException("找不到仓库根目录（Dougong.slnx）");
    }

    private static string Trim(string text) =>
        text.Length <= 600 ? text : text[..600] + "…";

    private static async Task<int> CheckAsync(string name, Func<Task<string?>> action)
    {
        try
        {
            var failure = await action().ConfigureAwait(false);
            if (failure is null)
            {
                Console.WriteLine($"PASS {name}");
                return 0;
            }

            Console.WriteLine($"FAIL {name}: {failure}");
            return 1;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"FAIL {name}: {ex.GetType().Name}: {ex.Message}");
            return 1;
        }
    }
}
