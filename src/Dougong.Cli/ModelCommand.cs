using Dougong.Ai;
using Dougong.Cli.Terminal;

namespace Dougong.Cli;

/// <summary>
/// <c>dougong model</c>：交互式选择 provider 与模型，并写回配置文件。
/// </summary>
/// <remarks>
/// <b>为什么是独立子命令而不是 REPL 里的 <c>/model</c></b>：两者改的东西不同。
/// <c>/model</c> 只换本次会话的连接器，退出即失效——那是"临时试一个端点"。
/// 这个命令改的是**配置文件**，是持久决定。把它们合成一个入口，
/// 用户就分不清"我刚才改的到底存没存"。
/// <para>
/// <b>为什么不自动探测可用模型</b>：那要调 <c>/v1/models</c>，需要密钥、
/// 需要网络，而且各家的返回形状并不统一。配模型是低频动作，一次真实报错
/// （404 model not found）比一个可能过时的列表更有用。所以这里让人填，
/// 但把"当前值"和"怎么填"讲清楚。
/// </para>
/// </remarks>
public static class ModelCommand
{
    /// <summary>执行命令，返回进程退出码。</summary>
    /// <param name="arguments">命令参数（不含 "model" 本身）。</param>
    public static int Run(string[] arguments)
    {
        // 非交互式形式：dougong model --set <provider> --model <name>
        // 既给脚本用，也给"我知道自己要用什么"的人用。
        string? setProvider = null;
        string? setModel = null;
        var makeDefault = false;
        var list = false;

        for (var i = 0; i < arguments.Length; i++)
        {
            switch (arguments[i])
            {
                case "--set" when i + 1 < arguments.Length:
                    setProvider = arguments[++i];
                    break;
                case "--model" when i + 1 < arguments.Length:
                    setModel = arguments[++i];
                    break;
                case "--default":
                    makeDefault = true;
                    break;
                case "--list" or "-l":
                    list = true;
                    break;
                case "--help" or "-h":
                    PrintHelp();
                    return 0;
                default:
                    Console.Error.WriteLine($"未知参数：{arguments[i]}");
                    Console.Error.WriteLine("用法：dougong model [--set <provider> [--model <名字>] [--default]] [--list]");
                    return 2;
            }
        }

        var path = ProviderConfigFile.DefaultPath;
        var loadError = ProviderConfigFile.TryLoad(path, out var catalog);

        if (loadError is not null)
        {
            Console.Error.WriteLine($"配置有问题：{loadError}");
            Console.Error.WriteLine("先修好再改。运行 dougong --doctor 查看完整诊断。");
            return 2;
        }

        if (list || setProvider is not null)
            return RunNonInteractive(path, catalog, setProvider, setModel, makeDefault);

        return RunInteractive(path, catalog);
    }

    private static int RunNonInteractive(
        string path, ProviderCatalog? catalog, string? providerName, string? model, bool makeDefault)
    {
        if (providerName is null)
        {
            PrintProviderTable(catalog);
            return 0;
        }

        // 换 provider 但没给模型时，沿用该 provider 已有的模型名。
        // 不给的话就变成"换过去但用不了"，而那要到下次对话才发现。
        var existing = catalog?.Find(providerName);
        var target = model ?? existing?.Model;

        if (string.IsNullOrWhiteSpace(target))
        {
            Console.Error.WriteLine(
                $"provider '{providerName}' 还没有模型名。请用 --model 指定。");
            return 2;
        }

        var result = ProviderConfigWriter.UpdateProvider(
            path, providerName, target, makeDefault || catalog is null);

        if (!result.Succeeded)
        {
            Console.Error.WriteLine(result.Error);
            return 1;
        }

        Output.Line($"已写入 {path}");
        Output.Line($"  provider: {providerName}");
        Output.Line($"  model   : {target}");
        if (makeDefault || catalog is null)
            Output.Line("  （已设为 default_provider）");
        if (result.BackupPath is not null)
            Output.Line($"  备份    : {result.BackupPath}");
        Output.Line();
        Output.Line("下一步：dougong --doctor 确认配置可用。");

        return 0;
    }

    /// <summary>交互式：挑 provider，再填模型，然后写回。</summary>
    private static int RunInteractive(string path, ProviderCatalog? catalog)
    {
        Output.Line();
        Output.Line($"  配置文件：{path}");
        if (catalog is null)
        {
            Output.Line("  当前还没有任何 provider——下面会帮你建一份。");
        }
        else
        {
            Output.Line($"  当前选中：{catalog.SelectedName}"
                + $"（模型 {(string.IsNullOrWhiteSpace(catalog.Selected.Model) ? "未配置" : catalog.Selected.Model)}）");
        }
        Output.Line();

        var current = catalog?.SelectedName;
        var chosen = PromptProvider(catalog, current);
        if (chosen is null)
        {
            Output.Line("未改动。");
            return 0;
        }

        // ── 端点：显示默认值，允许覆盖 ────────────────────────────
        // 照 Hermes 的 Base URL 提示：方括号里是默认值，回车即接受。
        // 不显示直接问模型名的话，用户不知道这个 provider 会连到哪里。
        var existing = catalog?.Find(chosen);
        var defaultBase = existing?.BaseUrl
            ?? (ProviderCatalog.Presets.TryGetValue(chosen, out var p) ? p.BaseUrl : null);

        var baseUrl = PromptBaseUrl(chosen, defaultBase);
        if (baseUrl is null)
        {
            Output.Line("未改动。");
            return 0;
        }

        // ── 密钥：流程内采集，不再只提示 export ─────────────────────
        // Hermes 的关键一步：配模型时顺手把密钥处理掉。
        // 只提示"你还需要 export"等于把活留给用户，而他刚打开这个界面
        // 就是因为不知道要配什么。
        var keyEnv = existing?.ApiKeyName
            ?? (ProviderCatalog.Presets.TryGetValue(chosen, out var kp) ? kp.ApiKeyName : null);

        var (keyOk, keyNote) = PromptApiKey(chosen, keyEnv);
        if (!keyOk)
        {
            Output.Line("未改动。");
            return 0;
        }

        // ── 模型：先试拉取，拉不到再手填 ───────────────────────────
        var model = PromptModelWithDiscovery(baseUrl, keyEnv, existing?.Model);
        if (model is null)
        {
            Output.Line("未改动。");
            return 0;
        }

        // 三层回退：配置里显式写 > 内置表查到 > 默认起点。
        // 前两层是事实，第三层是**估计**——所以界面上必须说清是哪一种。
        // 一个看起来精确的错数字比"未知"更糟，而"估计值"是可以接受的：
        // 用户看到"估计"就知道要自己确认。
        var capabilities = ProviderCatalog.FindModelCapabilities(chosen, model);

        var contextWindow = capabilities?.ContextWindow
            ?? ProviderCatalog.FallbackContextWindow;
        var maxOutput = capabilities?.MaxOutputTokens is > 0
            ? capabilities.MaxOutputTokens
            : ProviderCatalog.FallbackMaxOutputTokens;

        // 选中的 provider 就设成默认：这个命令的意图是"以后用这个"。
        var result = ProviderConfigWriter.UpdateProvider(
            path, chosen, model, setAsDefault: true, baseUrl: baseUrl,
            contextWindow: contextWindow,
            maxOutputTokens: maxOutput);

        if (!result.Succeeded)
        {
            Console.Error.WriteLine(result.Error);
            return 1;
        }

        Output.Line();
        Output.Line($"已写入 {path}");
        Output.Line($"  provider: {chosen}（已设为 default_provider）");
        Output.Line($"  base_url: {baseUrl}");
        Output.Line($"  model   : {model}");
        if (capabilities is not null)
        {
            Output.Line($"  上下文  : {contextWindow:N0} tokens"
                + $"（输出上限 {maxOutput:N0}）"
                + "  ← 取自内置能力表");
        }
        else
        {
            // 必须说清这是估计：用户看到"估计"就知道要自己确认，
            // 而看到一个精确数字会以为它是查出来的。
            Output.Line($"  上下文  : {contextWindow:N0} tokens"
                + $"（输出上限 {maxOutput:N0}）"
                + "  ⚠ 内置表未收录该模型，这是估计值，请按实际型号确认");
        }

        if (keyEnv is { Length: > 0 }) Output.Line($"  key_env : {keyEnv}");
        if (result.BackupPath is not null)
            Output.Line($"  备份    : {result.BackupPath}");

        if (keyNote is not null)
        {
            Output.Line();
            Output.Line($"⚠ {keyNote}");
        }

        Output.Line();
        Output.Line("下一步：dougong --doctor 确认配置可用。");
        return 0;
    }

    /// <summary>从 URL 取主机名（含端口），失败时原样返回。</summary>
    /// <remarks>
    /// 列表里只放主机名：完整 URL 太长会挤掉状态列，而用户辨认 provider
    /// 靠的正是主机名（<c>api.deepseek.com</c> 一眼认得出）。
    /// </remarks>
    private static string TryHost(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return "(无)";
        return Uri.TryCreate(url, UriKind.Absolute, out var uri)
            ? uri.Host + (uri.IsDefaultPort ? string.Empty : $":{uri.Port}")
            : url;
    }

    /// <summary>提示端点，方括号里是默认值，回车接受。返回 null 表示取消。</summary>
    /// <remarks>
    /// 照 Hermes 的做法先显示默认端点：不说端点就直接问模型名，
    /// 用户不知道这个 provider 会连到哪里，而端点填错是最难自己发现的错——
    /// 表现出来只是 404 或连接超时。
    /// </remarks>
    private static string? PromptBaseUrl(string provider, string? defaultBase)
    {
        Output.Line();
        if (string.IsNullOrWhiteSpace(defaultBase))
        {
            Output.Text($"  base_url（{provider} 的 API 地址，例如 https://api.example.com/v1）: ");
            var raw = Console.ReadLine();
            if (raw is null) return null;
            raw = raw.Trim();
            return raw.Length == 0 ? null : raw;
        }

        Output.Text($"  base_url [{defaultBase}]: ");
        var input = Console.ReadLine();
        if (input is null) return null;
        input = input.Trim();
        return input.Length == 0 ? defaultBase : input;
    }

    /// <summary>
    /// 采集密钥。返回 (是否继续, 事后提示)。
    /// </summary>
    /// <remarks>
    /// <b>红线不变：密钥绝不写进配置文件。</b>它写进本机凭据文件
    /// （<see cref="CredentialFile"/>），那个文件与 config.toml 是两回事——
    /// 前者从不进版本库，后者可以安全分享。
    /// <para>
    /// <b>为什么就在命令里收，而不是让用户自己 export</b>：
    /// 参考的四个项目（pi / pi.net / dsh / hermes）全都提供交互输入，
    /// 没有一个要求用户 export。那层门槛是 CLI 时代的习惯：
    /// 得知道在哪设、每次开新终端重设、Windows 上还要分清 PowerShell 与 Git Bash。
    /// 本命令收下后直接存盘，用户填一次就够。
    /// </para>
    /// <para>
    /// <b>输入必须掩码</b>：pi 的登录框把密钥交给普通输入框，明文显示在屏幕上、
    /// 留在滚动历史里——那是可参照的教训。见 <see cref="SecretPrompt"/>。
    /// </para>
    /// </remarks>
    private static (bool Proceed, string? Note) PromptApiKey(string provider, string? keyName)
    {
        Output.Line();

        if (keyName is null or "")
        {
            Output.Line($"  {provider} 无需密钥。");
            return (true, null);
        }

        // **先看凭据——已配好就直接过，不再问一句**。
        //
        // 用户重跑 dougong model 通常是想改 provider 或模型，不是改密钥。
        // 每次都追加一个"要换吗"的提示会让交互步骤随状态变化，
        // 而那既烦人、也让"输入第 N 步是什么"变得不可预测。
        // 想换密钥就再单独跑一次（或在下面"没有凭据"分支里输入）。
        if (CredentialFile.TryRead(keyName) is { Length: > 0 } stored)
        {
            Output.Line($"  密钥：已保存（{SecretPrompt.Mask(stored)}）✓  想换请删掉后重跑");
            return (true, null);
        }


        // 没有存储：直接问用户输入。
        //
        // **这是本轮改动的核心**。原来这里打印
        //     export XXX_API_KEY='<你的密钥>'
        // 就算完事——那要求用户知道在哪设、每次开新终端重设、
        // Windows 上还要分清 PowerShell 与 Git Bash。四个参考项目
        // （pi / pi.net / dsh / hermes）全都提供交互输入，没有一个
        // 让用户自己去 export。
        Output.Line($"  密钥：还没有。直接粘贴进来即可，会保存到本机。");
        Output.Line($"  （存放位置：{CredentialFile.DefaultPath}，仅当前用户可读）");
        Output.Line();

        var entered = SecretPrompt.Ask(keyName);
        if (entered is { Length: > 0 })
        {
            CredentialFile.Write(keyName, entered);
            Output.Line($"  已保存（{SecretPrompt.Mask(entered)}）。以后不用再输入。");
            return (true, null);
        }

        // 用户没输入：允许先写配置、稍后补。
        Output.Line($"  已跳过。之后补密钥只需重跑：");
        Output.Line($"      dougong model");

        return (true, $"还差密钥：重跑 dougong model 输入即可，否则对话会失败。");
    }

    /// <summary>
    /// 选模型：先尝试从端点拉取可用列表，拉不到再让人手填。
    /// </summary>
    /// <remarks>
    /// <b>为什么先试拉取</b>：Hermes 这么做，理由站得住——手打模型名最容易写错
    /// （<c>deepseek-chat</c> 打成 <c>deepseek-chart</c>），而错误要到第一次对话
    /// 才以 404 的形式出现，那时看不出是拼写问题。
    /// <para>
    /// <b>拉取失败不算错误</b>：很多端点不实现 <c>/v1/models</c>，或需要额外权限。
    /// 失败就退回手填，并说清"没拉到，请手填"，而不是让整个流程失败。
    /// </para>
    /// </remarks>
    private static string? PromptModelWithDiscovery(string baseUrl, string? keyEnv, string? existing)
    {
        Output.Line();
        Output.Line("  正在查询端点可用模型…");

        var models = ModelDiscovery.TryListModels(baseUrl, keyEnv);

        if (models.Count == 0)
        {
            Output.Line("  没查到模型列表（端点可能不支持 /v1/models），请手填。");
            return PromptModelText(existing);
        }

        Output.Line($"  查到 {models.Count} 个模型：");
        Output.Line();

        var shown = models.Take(40).ToList();
        for (var i = 0; i < shown.Count; i++)
        {
            var marker = string.Equals(shown[i], existing, StringComparison.Ordinal) ? "▶" : " ";
            Output.Line($"  {marker} {i + 1,2}. {shown[i]}");
        }
        if (models.Count > shown.Count)
            Output.Line($"     …另有 {models.Count - shown.Count} 个，输入名字可直接指定");

        Output.Line();
        Output.Line("   0. 手动输入模型名");
        Output.Line();

        while (true)
        {
            Output.Text($"选择 [1-{shown.Count}]、输入模型名，或回车取消: ");
            var input = Console.ReadLine();
            if (input is null) return null;
            input = input.Trim();
            if (input.Length == 0) return null;
            if (input == "0") return PromptModelText(existing);

            if (int.TryParse(input, out var index) && index >= 1 && index <= shown.Count)
                return shown[index - 1];

            // 直接敲模型名也接受：列表可能很长，数序号不如打字快。
            var byName = models.FirstOrDefault(m =>
                string.Equals(m, input, StringComparison.OrdinalIgnoreCase));
            if (byName is not null) return byName;

            // 不在列表里不等于错——列出来的可能不全（分页、过滤）。
            // 但必须让用户确认，否则拼错一个字母就成了一个"合法"的坏配置。
            Output.Line($"  '{input}' 不在查到的列表里。用它？[y/N]: ");
            Output.Text("  ");
            var confirm = Console.ReadLine()?.Trim().ToLowerInvariant();
            if (confirm is "y" or "yes") return input;
        }
    }

    /// <summary>纯手填模型名。</summary>
    private static string? PromptModelText(string? existing)
    {
        if (string.IsNullOrWhiteSpace(existing))
            Output.Text("  模型名: ");
        else
            Output.Text($"  模型名（当前 {existing}，回车保持不变）: ");

        var input = Console.ReadLine();
        if (input is null) return null;

        input = input.Trim();
        if (input.Length == 0) return string.IsNullOrWhiteSpace(existing) ? null : existing;

        return input;
    }

    /// <summary>列出可选 provider，读一行选择。返回 null 表示取消。</summary>
    private static string? PromptProvider(ProviderCatalog? catalog, string? current)
    {
        // 候选是"预设 + 配置里已有的"，去重后按名字排序。
        var names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in ProviderCatalog.Presets.Keys) names.Add(name);
        if (catalog is not null)
            foreach (var provider in catalog.Providers) names.Add(provider.Name);

        var items = names.ToList();
        Output.Line("  选择 provider：");
        Output.Line();

        for (var i = 0; i < items.Count; i++)
        {
            var name = items[i];
            var configured = catalog?.Find(name);

            // 端点优先显示配置里的（用户改过就以配置为准），否则显示预设。
            var baseUrl = configured?.BaseUrl
                ?? (ProviderCatalog.Presets.TryGetValue(name, out var preset) ? preset.BaseUrl : null);

            // **只显示主机名**：完整 URL 太长会挤掉状态列，而用户辨认 provider
            // 靠的正是主机名（api.deepseek.com 一眼认得出）。
            var host = TryHost(baseUrl);

            // 密钥状态：已设置 / 未设置 / 无需密钥。
            var keyEnv = configured?.ApiKeyName
                ?? (ProviderCatalog.Presets.TryGetValue(name, out var kp) ? kp.ApiKeyName : null);
            var keyState = keyEnv switch
            {
                null or "" => "无需密钥",
                // 存储优先（与解析顺序一致）。两处都算"已设置"，
                // 且**不显示来源**：环境变量名是实现细节，对用户没有意义。
                { } n when CredentialFile.TryRead(n) is { Length: > 0 } => "密钥✓",
                _ => "密钥未设",
            };

            var marker = string.Equals(name, current, StringComparison.OrdinalIgnoreCase) ? "▶" : " ";
            Output.Line($"  {marker} {i + 1,2}. {name,-12} {host,-34} {keyState}");
        }
        Output.Line();
        Output.Line("   0. 取消");
        Output.Line("      （输入名字也可以，例如 glm）");

        while (true)
        {
            Output.Text($"选择 [1-{items.Count}]，回车取消: ");
            var input = Console.ReadLine();
            if (input is null) return null;
            input = input.Trim();
            if (input.Length == 0 || input == "0") return null;

            if (int.TryParse(input, out var index) && index >= 1 && index <= items.Count)
                return items[index - 1];

            // 直接敲名字也接受：列表长的时候比数序号快。
            var byName = items.FirstOrDefault(n =>
                string.Equals(n, input, StringComparison.OrdinalIgnoreCase));
            if (byName is not null) return byName;
            Output.Line($"  没有 {input} 这一项。");
        }
    }

    /// <summary>提示输入模型名。返回 null 表示取消。</summary>
    private static string? PromptModel(string provider, string? existing)
    {
        Output.Line();
        if (string.IsNullOrWhiteSpace(existing))
            Output.Text($"  模型名（例如 deepseek-chat）: ");
        else
            Output.Text($"  模型名（当前 {existing}，回车保持不变）: ");

        var input = Console.ReadLine();
        if (input is null) return null;

        input = input.Trim();
        if (input.Length == 0) return string.IsNullOrWhiteSpace(existing) ? null : existing;

        return input;
    }

    private static void PrintProviderTable(ProviderCatalog? catalog)
    {
        Output.Line($"  配置文件：{ProviderConfigFile.DefaultPath}");
        Output.Line();

        if (catalog is null)
        {
            Output.Line("  还没有任何 provider。");
            Output.Line("  直接跑 dougong model（不带 --list）会引导你建一份。");
            return;
        }

        foreach (var provider in catalog.Providers)
        {
            var active = string.Equals(
                provider.Name, catalog.SelectedName, StringComparison.OrdinalIgnoreCase);
            var model = string.IsNullOrWhiteSpace(provider.Model) ? "未配置模型" : provider.Model;
            var keyState = provider.ApiKeyName switch
            {
                null or "" => "无需密钥",
                { } n when CredentialFile.TryRead(n) is { Length: > 0 } => "密钥已保存",
                _ => "密钥未设置",
            };

            Output.Line($"  {(active ? "▶" : " ")} {provider.Name,-16} {model,-22} {keyState}");
        }

        Output.Line();
        Output.Line("  改模型：dougong model      或  dougong model --set <名字> --model <模型>");
    }

    private static void PrintHelp()
    {
        Output.Line("dougong model —— 选择 provider 与默认模型，写回配置文件");
        Output.Line();
        Output.Line("  dougong model                          交互式选择（会写回配置文件）");
        Output.Line("  dougong model --list                   只看当前配置");
        Output.Line("  dougong model --set deepseek --model deepseek-chat");
        Output.Line("                                         直接设置，并设为默认");
        Output.Line();
        Output.Line("与 REPL 里的 /model 的区别：");
        Output.Line("  /model        只换本次会话，退出即失效");
        Output.Line("  dougong model 改配置文件，是持久决定");
    }
}
