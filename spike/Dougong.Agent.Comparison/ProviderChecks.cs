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

            // 变量名必须像变量名。预设里出现密钥值是最严重的配置事故。
            var keyName = preset.ApiKeyEnvironmentVariable;
            if (keyName.Length == 0
                || !(char.IsAsciiLetter(keyName[0]) || keyName[0] == '_')
                || keyName.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '_'))
            {
                return Task.FromResult<string?>(
                    $"预设 '{name}' 的密钥变量名不像变量名：'{keyName}'");
            }
        }

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
