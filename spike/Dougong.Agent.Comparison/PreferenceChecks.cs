using Dougong.Ai;

namespace Dougong.Agent.Comparison;

/// <summary>
/// 用户偏好与安全开关的验收。
/// </summary>
/// <remarks>
/// <b>可观察面有三个</b>：
/// <list type="number">
/// <item>偏好能从配置文件读出来、写回去，且**不破坏其他段**。</item>
/// <item>环境变量是临时覆盖，优先级高于配置。</item>
/// <item><b>安全开关在进程内冻结</b>——运行期改配置文件不能影响当前进程。
/// 这是防自举的核心，也是本套最重要的一条。</item>
/// </list>
/// </remarks>
public static class PreferenceChecks
{
    public static Task<int> RunAsync()
    {
        var failures = 0;
        failures += Check("preferences-roundtrip", RoundTrips);
        failures += Check("preferences-save-preserves-providers", PreservesProviders);
        failures += Check("preferences-default-to-off", DefaultsAreOff);
        failures += Check("preferences-ignore-wrong-typed-values", IgnoresWrongTypes);
        failures += Check("env-overrides-config", EnvOverridesConfig);
        failures += Check("security-switch-freezes-in-process", FrozenInProcess);
        failures += Check("memory-dir-comes-from-config-not-env", MemoryDirFromConfig);
        failures += Check("memory-default-is-home-relative", MemoryDefaultIsHomeRelative);
        failures += Check("home-describe-never-leaks-username", DescribeIsSymbolic);

        Console.WriteLine(failures == 0
            ? "PREFERENCE: ALL PASS"
            : $"PREFERENCE: {failures} FAILURE(S)");
        return Task.FromResult(failures);
    }

    /// <summary>三个偏好能写能读。</summary>
    private static string? RoundTrips()
    {
        using var file = new TempConfig();

        UserPreferencesFile.Save(
            new UserPreferences { Shell = true, Memory = true, ShowReasoning = false },
            file.FilePath);

        var loaded = UserPreferencesFile.TryLoad(out var error, file.FilePath);
        if (error is not null) return $"读取失败：{error}";

        if (!loaded.Shell) return "shell 没读回来";
        if (!loaded.Memory) return "memory 没读回来";
        if (loaded.ShowReasoning) return "show_reasoning 应为 false";
        return null;
    }

    /// <summary>
    /// 保存偏好不能冲掉 provider 段。
    /// </summary>
    /// <remarks>
    /// <b>这是最容易写错的一处</b>：整体重写文件会让用户配的 provider 全丢。
    /// 那是"改一个开关，丢掉全部配置"级别的事故。
    /// </remarks>
    private static string? PreservesProviders()
    {
        using var file = new TempConfig();
        File.WriteAllText(file.FilePath, """
            default_provider = "a"

            [providers.a]
            base_url = "https://a.example.com"
            model = "m1"
            key = "A_KEY"

            [providers.b]
            base_url = "https://b.example.com"
            model = "m2"
            key = "B_KEY"
            """);

        UserPreferencesFile.Save(new UserPreferences { Shell = true }, file.FilePath);

        var text = File.ReadAllText(file.FilePath);
        if (!text.Contains("[providers.a]", StringComparison.Ordinal)) return "providers.a 丢了";
        if (!text.Contains("[providers.b]", StringComparison.Ordinal)) return "providers.b 丢了";
        if (!text.Contains("https://b.example.com", StringComparison.Ordinal)) return "b 的 base_url 丢了";
        if (!text.Contains("default_provider", StringComparison.Ordinal)) return "default_provider 丢了";

        // 而且保存后的文件仍能解析出这两个 provider。
        //
        // **不能断言总数**：TryLoad 会把内置预设一起合并（22 家），
        // 所以总数是 22 + 2。断言总数等于 2 是错的，而且以后内置表一变就红。
        var parseError = ProviderConfigFile.TryLoad(file.FilePath, out var catalog);
        if (parseError is not null) return $"保存后解析失败：{parseError}";
        if (catalog is null) return "保存后没有解析出目录";

        var names = catalog.Providers.Select(p => p.Name).ToList();
        if (!names.Contains("a", StringComparer.OrdinalIgnoreCase))
            return $"provider a 丢了：{string.Join(",", names.Take(5))}";
        if (!names.Contains("b", StringComparer.OrdinalIgnoreCase))
            return "provider b 丢了";

        return null;
    }

    /// <summary>没有配置时三个开关都是关。</summary>
    /// <remarks>
    /// <b>默认关是刻意的</b>：shell 能执行任意命令，而我们的命令策略
    /// 只是模式匹配不是沙箱。让用户显式选择，而不是默认给。
    /// </remarks>
    private static string? DefaultsAreOff()
    {
        using var file = new TempConfig();   // 不创建文件

        var loaded = UserPreferencesFile.TryLoad(out _, file.FilePath);
        if (loaded.Shell) return "shell 默认应为 false";
        if (loaded.Memory) return "memory 默认应为 false";
        if (loaded.ShowReasoning) return "show_reasoning 默认应为 false";
        return null;
    }

    /// <summary>类型不对的值当未设置，不猜。</summary>
    /// <remarks>
    /// 把 <c>shell = "yes"</c> 当 true 会让用户以为开了而实际没开，
    /// 而那只能靠试才发现。
    /// </remarks>
    private static string? IgnoresWrongTypes()
    {
        using var file = new TempConfig();
        File.WriteAllText(file.FilePath, """
            [preferences]
            shell = "yes please"
            memory = 1
            show_reasoning = "true"
            """);

        var loaded = UserPreferencesFile.TryLoad(out _, file.FilePath);
        if (loaded.Shell) return "字符串值被当成了 true";
        if (loaded.Memory) return "数字 1 被当成了 true";
        if (loaded.ShowReasoning) return "字符串 \"true\" 被当成了 true";
        return null;
    }

    /// <summary>环境变量压过配置文件。</summary>
    /// <remarks>
    /// 顺序与密钥相反（密钥是存储优先）。理由：开关有"临时"语义——
    /// "我这次想关掉"，而密钥没有。
    /// </remarks>
    private static string? EnvOverridesConfig()
    {
        using var file = new TempConfig();
        UserPreferencesFile.Save(new UserPreferences { Shell = false }, file.FilePath);

        var saved = Environment.GetEnvironmentVariable("DOUGONG_SHELL");
        try
        {
            Environment.SetEnvironmentVariable("DOUGONG_SHELL", "1");
            SecuritySwitches.ResetForTesting();

            // 指到同一个文件：配置说 false，环境变量说 1。
            var savedPath = Environment.GetEnvironmentVariable("DOUGONG_HOME");
            Environment.SetEnvironmentVariable("DOUGONG_HOME", file.HomeDirectory);
            try
            {
                SecuritySwitches.ResetForTesting();
                return SecuritySwitches.Current.Shell
                    ? null
                    : "环境变量 DOUGONG_SHELL=1 没有压过配置里的 shell = false";
            }
            finally
            {
                Environment.SetEnvironmentVariable("DOUGONG_HOME", savedPath);
                SecuritySwitches.ResetForTesting();
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("DOUGONG_SHELL", saved);
            SecuritySwitches.ResetForTesting();
        }
    }

    /// <summary>
    /// **安全开关在进程内冻结：改配置文件不影响当前进程。**
    /// </summary>
    /// <remarks>
    /// <b>这条是本套最重要的，钉的是自举漏洞</b>：
    /// 一旦 shell 开启，模型能用
    /// <c>echo 'shell = true' &gt;&gt; ~/.dougong/config.toml</c> 给自己永久授权。
    /// 冻结让这个写入要等用户主动重启才生效——而重启是用户的行为，不是模型的。
    /// <para>
    /// 参考 hermes 的做法：<c>tools/approval.py</c> 在导入时读定
    /// <c>HERMES_YOLO_MODE</c>，注释说明"每次读环境变量会让进程内的任何技能
    /// 改掉它并绕过全部审批（prompt-injection escalation path）"。
    /// </para>
    /// <para>
    /// 验法：取一次冻结值 → 改配置文件 → 再取，必须还是旧值。
    /// </para>
    /// </remarks>
    private static string? FrozenInProcess()
    {
        using var file = new TempConfig();
        UserPreferencesFile.Save(new UserPreferences { Shell = true }, file.FilePath);

        var savedPath = Environment.GetEnvironmentVariable("DOUGONG_HOME");
        var savedShell = Environment.GetEnvironmentVariable("DOUGONG_SHELL");
        try
        {
            Environment.SetEnvironmentVariable("DOUGONG_HOME", file.HomeDirectory);
            Environment.SetEnvironmentVariable("DOUGONG_SHELL", null);
            SecuritySwitches.ResetForTesting();

            // 第一次读取：冻结为 true。
            if (!SecuritySwitches.Current.Shell) return "初始值应为 true";

            // 模拟"模型把配置改成了 false"。
            UserPreferencesFile.Save(new UserPreferences { Shell = false }, file.FilePath);

            // 不重置的情况下必须仍是 true——这正是冻结的意义。
            if (!SecuritySwitches.Current.Shell)
            {
                return "运行期改配置文件影响了当前进程——自举漏洞："
                    + "模型可以用 shell 改配置给自己永久授权";
            }

            return null;
        }
        finally
        {
            Environment.SetEnvironmentVariable("DOUGONG_HOME", savedPath);
            Environment.SetEnvironmentVariable("DOUGONG_SHELL", savedShell);
            SecuritySwitches.ResetForTesting();
        }
    }

    /// <summary>
    /// 记忆目录从配置读，不从环境变量读。
    /// </summary>
    /// <remarks>
    /// <b>这条钉的是"单一来源"</b>：曾经 <c>DOUGONG_MEMORY</c> 可以是一个路径，
    /// 于是"记忆开关"有两套读法（布尔看环境变量、路径看同一变量的另一种形态），
    /// 而 <c>shell</c> / <c>show_reasoning</c> 只有一套。
    /// <para>
    /// 现在路径只来自 <c>[preferences] memory_dir</c>；
    /// 环境变量只覆盖布尔。这条检查确认配置里的路径真的被读到。
    /// </para>
    /// </remarks>
    private static string? MemoryDirFromConfig()
    {
        using var file = new TempConfig();
        UserPreferencesFile.Save(
            new UserPreferences { Memory = true, MemoryDirectory = "/custom/memory" },
            file.FilePath);

        var loaded = UserPreferencesFile.TryLoad(out _, file.FilePath);
        if (!loaded.Memory) return "memory 没读回来";
        if (loaded.MemoryDirectory != "/custom/memory")
            return $"memory_dir 没读回来：{loaded.MemoryDirectory}";

        // 保存后文件里应当有这一行，且再次解析仍然正确（引号转义没写坏）。
        var text = File.ReadAllText(file.FilePath);
        if (!text.Contains("memory_dir", StringComparison.Ordinal))
            return "保存的文件里没有 memory_dir";

        var again = UserPreferencesFile.TryLoad(out var error, file.FilePath);
        return error is null && again.MemoryDirectory == "/custom/memory"
            ? null
            : $"二次解析不一致：{error ?? again.MemoryDirectory}";
    }

    /// <summary>
    /// 默认记忆位置由 home 决定，与传入的会话目录无关。
    /// </summary>
    /// <remarks>
    /// <b>这条钉的是一个真实的不一致</b>：默认位置曾经从
    /// <c>Path.Combine(sessionDirectory, "..", "memory")</c> 推出，
    /// 而两个入口传的 <c>sessionDirectory</c> 不同——
    /// REPL 传 <c>&lt;home&gt;/sessions</c>、ACP 传 <c>&lt;workspace&gt;/.dougong</c>。
    /// 于是同一功能在两个入口落在不同位置（<c>&lt;home&gt;/memory</c> vs
    /// <c>&lt;workspace&gt;/memory</c>），而两边都不报错。
    /// <para>
    /// 验法：用两个完全不同的 sessionDirectory 取值调用，结果必须相同。
    /// </para>
    /// </remarks>
    private static string? MemoryDefaultIsHomeRelative()
    {
        var savedHome = Environment.GetEnvironmentVariable("DOUGONG_HOME");
        var savedMemory = Environment.GetEnvironmentVariable("DOUGONG_MEMORY");
        try
        {
            var isolated = Path.Combine(
                Path.GetTempPath(), "dougong-mem-" + Guid.NewGuid().ToString("N"));
            Environment.SetEnvironmentVariable("DOUGONG_HOME", isolated);
            Environment.SetEnvironmentVariable("DOUGONG_MEMORY", "1");
            SecuritySwitches.ResetForTesting();

            var fromRepl = Dougong.Cli.SessionComposition.ResolveMemoryDirectory(
                Path.Combine(isolated, "sessions"));
            var fromAcp = Dougong.Cli.SessionComposition.ResolveMemoryDirectory(
                Path.Combine("/some/project", ".dougong"));

            if (fromRepl is null) return "启用记忆后仍返回 null";
            if (!string.Equals(fromRepl, fromAcp, StringComparison.Ordinal))
            {
                return $"两个入口的记忆位置不一致：REPL={fromRepl} ACP={fromAcp}";
            }

            // 必须落在 home 下，而不是某个项目目录里。
            if (!fromRepl.StartsWith(isolated, StringComparison.OrdinalIgnoreCase))
                return $"记忆位置不在 home 下：{fromRepl}";

            return null;
        }
        finally
        {
            Environment.SetEnvironmentVariable("DOUGONG_HOME", savedHome);
            Environment.SetEnvironmentVariable("DOUGONG_MEMORY", savedMemory);
            SecuritySwitches.ResetForTesting();
        }
    }

    /// <summary>
    /// 给用户看的路径必须符号化，且保留相对部分。
    /// </summary>
    /// <remarks>
    /// <b>两个要点</b>：一是绝不输出 <c>C:/Users/用户名</c> 这类绝对路径——
    /// 用户把诊断输出贴到 issue 时往往想不到要打码；二是**必须保留
    /// <c>config.toml</c> 这类相对部分**，否则读者会以为配置文件就是
    /// home 目录本身。
    /// </remarks>
    private static string? DescribeIsSymbolic()
    {
        var savedHome = Environment.GetEnvironmentVariable("DOUGONG_HOME");
        try
        {
            // 默认位置：应显示 ~/.dougong，不含用户名。
            Environment.SetEnvironmentVariable("DOUGONG_HOME", null);
            SecuritySwitches.ResetForTesting();

            var byDefault = DougongHome.Describe(DougongHome.ConfigPath);
            if (byDefault.Contains("Users", StringComparison.OrdinalIgnoreCase))
                return $"默认位置泄漏了绝对用户路径：{byDefault}";
            if (!byDefault.StartsWith("~/", StringComparison.Ordinal))
                return $"默认位置没有符号化：{byDefault}";
            if (!byDefault.EndsWith("config.toml", StringComparison.Ordinal))
                return $"默认位置丢了相对部分：{byDefault}";

            // 被覆盖：应显示 $DOUGONG_HOME/xxx。
            var isolated = Path.Combine(
                Path.GetTempPath(), "dougong-desc-" + Guid.NewGuid().ToString("N"));
            Environment.SetEnvironmentVariable("DOUGONG_HOME", isolated);
            SecuritySwitches.ResetForTesting();

            var overridden = DougongHome.Describe(DougongHome.ConfigPath);
            if (overridden.Contains(isolated, StringComparison.OrdinalIgnoreCase))
                return $"覆盖后仍显示绝对路径：{overridden}";
            if (!overridden.StartsWith("$DOUGONG_HOME/", StringComparison.Ordinal))
                return $"覆盖后没有符号化：{overridden}";
            if (!overridden.EndsWith("config.toml", StringComparison.Ordinal))
                return $"覆盖后丢了相对部分：{overridden}";

            return null;
        }
        finally
        {
            Environment.SetEnvironmentVariable("DOUGONG_HOME", savedHome);
            SecuritySwitches.ResetForTesting();
        }
    }

    // ── 支撑 ─────────────────────────────────────────────────────

    /// <summary>一次性配置文件。</summary>
    private sealed class TempConfig : IDisposable
    {
        public TempConfig()
        {
            var directory = Path.Combine(
                Path.GetTempPath(), "dougong-pref-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            FilePath = Path.Combine(directory, "config.toml");
        }

        public string FilePath { get; }

        /// <summary>包含该配置的目录，供 <c>DOUGONG_HOME</c> 使用。</summary>
        public string HomeDirectory => Path.GetDirectoryName(FilePath) ?? FilePath;

        public void Dispose()
        {
            try
            {
                var directory = Path.GetDirectoryName(FilePath);
                if (directory is not null && Directory.Exists(directory))
                    Directory.Delete(directory, recursive: true);
            }
            catch
            {
                // 清理失败不影响结论。
            }
        }
    }

    private static int Check(string name, Func<string?> action)
    {
        try
        {
            var failure = action();
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
