using System.Globalization;
using Tomlyn;
using Tomlyn.Model;

namespace Dougong.Ai;

/// <summary>
/// 从 TOML 读出 provider 目录。
/// </summary>
/// <remarks>
/// <b>红线：这个类读到的任何东西里都不允许出现密钥值。</b>
/// 配置里只写变量名（<c>key_env</c>），值由 <see cref="ChatClientFactory"/>
/// 在建立连接器时从环境变量取。这样配置文件可以进 git、进备份、进日志。
/// <para>
/// <b>为什么是 TOML</b>：与 Codex 的 <c>config.toml</c> 一致，用户不必学两套格式；
/// 且 TOML 的表结构天然表达 <c>[providers.&lt;name&gt;]</c> 这种具名条目。
/// </para>
/// <para>
/// <b>为什么解析失败返回诊断而不是抛</b>：<c>--doctor</c> 要把配置错误报成
/// 一条可看的失败项。装配期该硬失败时由调用方把诊断变成异常——
/// 判据共享、控制流不同，与 <c>ContextBudgetValidation</c> 同一分工。
/// </para>
/// </remarks>
public static class ProviderConfigFile
{
    /// <summary>
    /// 默认配置路径：<c>~/.dougong/config.toml</c>，可用
    /// <c>DOUGONG_PROVIDER_CONFIG</c> 整体覆盖。
    /// </summary>
    /// <remarks>
    /// <b>为什么要有覆盖开关</b>：<c>Environment.GetFolderPath(UserProfile)</c>
    /// 走的是操作系统的 Known Folder API，**不读 <c>USERPROFILE</c> 环境变量**。
    /// 于是"改 HOME 指向另一个目录来测试"在 Windows 上根本不生效——
    /// 实测过一次，自检读的仍是真实用户目录。没有这个开关就只能改真配置才能验证。
    /// <para>
    /// 它也让"一个项目一套 provider"成为可能：把路径指到工作区内的文件即可。
    /// </para>
    /// </remarks>
    public static string DefaultPath
    {
        get
        {
            var explicitPath = Environment.GetEnvironmentVariable("DOUGONG_PROVIDER_CONFIG");
            if (!string.IsNullOrWhiteSpace(explicitPath)) return explicitPath;

            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".dougong", "config.toml");
        }
    }

    /// <summary>provider 段允许出现的键。</summary>
    private static readonly string[] AllowedProviderKeys =
        ["base_url", "wire_api", "key_env", "model", "messages_path"];

    /// <summary>解析选项。未知键由我们自己报，所以不禁用它们的读取。</summary>
    private static readonly TomlSerializerOptions SerializerOptions = new();

    /// <summary>
    /// 读取并解析配置。
    /// </summary>
    /// <param name="path">配置文件路径；null 表示 <see cref="DefaultPath"/>。</param>
    /// <param name="catalog">成功时的目录；失败或文件不存在时为 null。</param>
    /// <returns>失败原因；成功或文件不存在时为 null。</returns>
    public static string? TryLoad(string? path, out ProviderCatalog? catalog)
    {
        catalog = null;
        var target = path ?? DefaultPath;

        // 文件不存在不是错误：没有任何配置时由环境变量回退接管。
        if (!File.Exists(target)) return null;

        string text;
        try
        {
            text = File.ReadAllText(target);
        }
        catch (Exception ex)
        {
            return $"配置文件读不了：{target}（{ex.GetType().Name}）";
        }

        return TryParse(text, target, out catalog);
    }

    /// <summary>
    /// 解析一段 TOML 文本。
    /// </summary>
    /// <param name="text">TOML 内容。</param>
    /// <param name="sourcePath">来源路径，仅用于诊断。</param>
    /// <param name="catalog">成功时的目录；失败时为 null。</param>
    /// <returns>失败原因；成功时为 null。</returns>
    public static string? TryParse(string text, string sourcePath, out ProviderCatalog? catalog)
    {
        catalog = null;

        // Deserialize 而不是"先解析语法树再遍历"：Tomlyn 2.10 的公开入口就是
        // 这一组 STJ 风格的 Serialize/Deserialize，语法树那套（Toml.Parse）
        // 在 2.x 已经没有了。嵌套表仍然读成 TomlTable，所以逐键校验照做。
        TomlTable root;
        try
        {
            root = TomlSerializer.Deserialize<TomlTable>(text, SerializerOptions)
                ?? throw new TomlException("配置顶层为空。");
        }
        catch (TomlException ex)
        {
            // 解析错误必须带出位置：只说"不是合法 TOML"用户无从修改。
            return $"配置不是合法 TOML：{ex.Message}";
        }
        catch (Exception ex)
        {
            return $"配置解析失败（{ex.GetType().Name}）：{ex.Message}";
        }

        // 顶层只有 default_provider 与 providers 两个键。未知键必须报，
        // 否则用户写错字段名会以为生效了——静默忽略最难查。
        foreach (var key in root.Keys)
        {
            if (key is not ("default_provider" or "providers"))
                return $"配置里有未知的顶层键 '{key}'；只支持 default_provider 与 providers。";
        }

        if (!root.TryGetValue("providers", out var providersValue)
            || providersValue is not TomlTable providersTable)
        {
            return "配置缺少 [providers.<名字>] 段。";
        }

        if (providersTable.Count == 0)
            return "配置里一个 provider 都没有。";

        var list = new List<ChatProviderConfig>();
        foreach (var (name, value) in providersTable)
        {
            if (value is not TomlTable table)
                return $"provider '{name}' 不是一个表（应为 [providers.{name}]）。";

            var error = ParseProvider(name, table, out var config);
            if (error is not null) return error;
            list.Add(config!);
        }

        var selected = root.TryGetValue("default_provider", out var sel) ? sel as string : null;

        try
        {
            // WithOverrides 会与内置预设合并：同名整体替换，新名新增。
            catalog = ProviderCatalog.WithOverrides(list, selected, sourcePath);

            // 立刻验证选中项存在。配了 default_provider 却不指向任何条目，
            // 到第一次调用才炸的话，报错完全看不出是配置问题。
            _ = catalog.Selected;
        }
        catch (Exception ex)
        {
            catalog = null;
            return ex.Message;
        }

        return null;
    }

    /// <summary>解析单个 provider 段。</summary>
    private static string? ParseProvider(
        string name, TomlTable table, out ChatProviderConfig? config)
    {
        config = null;

        foreach (var key in table.Keys)
        {
            if (!AllowedProviderKeys.Contains(key, StringComparer.Ordinal))
            {
                return $"provider '{name}' 有未知的键 '{key}'；"
                    + $"支持 {string.Join(" / ", AllowedProviderKeys)}。";
            }
        }

        var baseUrl = ReadString(table, "base_url");
        if (string.IsNullOrWhiteSpace(baseUrl))
            return $"provider '{name}' 缺少 base_url。";

        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return $"provider '{name}' 的 base_url 不是合法的 http(s) 地址：{baseUrl}";
        }

        var wireApi = ReadString(table, "wire_api");
        var protocol = wireApi?.ToLowerInvariant() switch
        {
            null or "" or "openai" or "chat" => ChatProtocol.OpenAiChatCompletions,
            "responses" => ChatProtocol.OpenAiResponses,
            "anthropic" or "messages" => ChatProtocol.AnthropicMessages,
            _ => (ChatProtocol?)null,
        };

        if (protocol is null)
        {
            return $"provider '{name}' 的 wire_api='{wireApi}' 无法识别；"
                + "支持 openai / responses / anthropic。";
        }

        var keyEnv = ReadString(table, "key_env");

        // key_env 必须是**变量名**而不是密钥值。这是安全红线，所以做成硬校验，
        // 而不是在文档里请求用户注意——把 sk-xxx 直接写进 key_env 是最容易犯的错，
        // 后果是密钥进 git，而那时已经太晚。
        if (keyEnv is not null && LooksLikeSecret(keyEnv))
        {
            return $"provider '{name}' 的 key_env='{keyEnv}' 看起来是一个密钥值而不是变量名。"
                + "配置里只能写变量名（例如 DEEPSEEK_API_KEY），密钥值放进环境变量。";
        }

        config = new ChatProviderConfig
        {
            Name = name,
            BaseUrl = baseUrl,
            // 预设没有模型名（同一端点的可用模型会变），空着比猜一个好。
            Model = ReadString(table, "model") ?? string.Empty,
            Protocol = protocol.Value,
            ApiKeyEnvironmentVariable = keyEnv,
            MessagesPath = ReadString(table, "messages_path"),
        };

        return null;
    }

    private static string? ReadString(TomlTable table, string key) =>
        table.TryGetValue(key, out var value)
            ? value as string ?? Convert.ToString(value, CultureInfo.InvariantCulture)
            : null;

    /// <summary>
    /// 判断一个值像不像密钥本身（而不是环境变量名）。
    /// </summary>
    /// <remarks>
    /// 判据刻意保守：环境变量名在 POSIX 上就是 <c>[A-Za-z_][A-Za-z0-9_]*</c>。
    /// 任何不符合这个形状的值都当作"可能是密钥"拒掉——宁可误拒一个奇怪的
    /// 变量名，也不放过一次把密钥写进配置文件的错误。
    /// </remarks>
    private static bool LooksLikeSecret(string value)
    {
        if (value.Length == 0) return false;

        var first = value[0];
        if (!(char.IsAsciiLetter(first) || first == '_')) return true;

        return value.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '_');
    }
}
