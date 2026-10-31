using Microsoft.Extensions.AI;

namespace Dougong.Ai;

/// <summary>
/// 一个具名 provider 配置：端点、模型、密钥来源与线协议。
/// </summary>
/// <remarks>
/// 密钥不放在这里，只放"从哪里取密钥"。配置对象可能被记录、序列化或打印，
/// 明文密钥一旦进入它就是事故；M1 已发生过一次误把密钥打到终端的事。
/// </remarks>
public sealed record ChatProviderConfig
{
    /// <summary>配置名，用于在多个 provider 之间选择。</summary>
    public required string Name { get; init; }

    /// <summary>API 基地址，例如 <c>https://api.deepseek.com</c>。</summary>
    public required string BaseUrl { get; init; }

    /// <summary>模型标识。</summary>
    public required string Model { get; init; }

    /// <summary>线协议。同一 provider 可能同时支持多种，必须显式指定。</summary>
    public ChatProtocol Protocol { get; init; } = ChatProtocol.OpenAiChatCompletions;

    /// <summary>
    /// 凭据条目名（如 <c>DEEPSEEK_API_KEY</c>），值存在本机凭据文件里。
    /// 为空表示该端点无需鉴权。
    /// </summary>
    /// <remarks>
    /// <b>名字带 <c>Name</c> 而不是 <c>Env</c></b>：它曾经是环境变量名，
    /// 但现在指向 <see cref="CredentialFile"/> 里的条目。环境变量这条通道
    /// 已经删掉——它随终端会话消失，会让"配好的密钥"莫名失效。
    /// <para>
    /// 需要"不改文件地试一个端点"时用 <see cref="ApiKey"/>（内存值），
    /// 那条路径来自 <c>DOUGONG_PROVIDER_*</c>，与凭据文件互不影响。
    /// </para>
    /// </remarks>
    public string? ApiKeyName { get; init; }

    /// <summary>
    /// 直接内联的密钥，**仅存在于内存，绝不写进任何文件**。
    /// </summary>
    /// <remarks>
    /// <b>为什么需要它</b>：<c>DOUGONG_PROVIDER_*</c> 那条路是"临时试一个端点"，
    /// 它的整套配置都来自环境变量，密钥自然也该在那里——让它去凭据文件找
    /// 一个叫 <c>DOUGONG_PROVIDER_KEY</c> 的条目既绕又容易出错。
    /// <para>
    /// <b>它不参与序列化</b>：<see cref="ProviderConfigWriter"/> 只写
    /// <see cref="ApiKeyName"/>，从不写这个字段。CI 里设环境变量即可，
    /// 不需要造一个假的凭据文件。
    /// </para>
    /// </remarks>
    public string? ApiKey { get; init; }

    /// <summary>
    /// 同一协议内部的方言差异（字段名、可选参数）。默认值对应已实测通过的端点。
    /// </summary>
    /// <remarks>
    /// 接一家"兼容 OpenAI"但字段名不同的端点时，改这里而不是改连接器代码——
    /// 否则每接一家就要动一次协议实现（G12/G13 的成因）。
    /// </remarks>
    public WireCompat Compat { get; init; } = WireCompat.Default;

    /// <summary>
    /// 上下文窗口（token）。null 表示未配置——**不是 0**：
    /// 0 在上下文压缩里意味着"不启用"，与"还不知道"是两件事。
    /// </summary>
    public int? ContextWindowTokens { get; init; }

    /// <summary>
    /// 单次回复的输出上限。null 表示未配置，沿用端点默认。
    /// </summary>
    public int? MaxOutputTokens { get; init; }

    /// <summary>
    /// Anthropic 兼容层的端点路径覆盖。多数是 <c>/v1/messages</c>；
    /// DeepSeek 挂在 <c>/anthropic/v1/messages</c>。仅对 <see cref="ChatProtocol.AnthropicMessages"/> 有效。
    /// </summary>
    public string? MessagesPath { get; init; }
}

/// <summary>
/// 按具名配置建立 <see cref="IChatClient"/>。
/// </summary>
/// <remarks>
/// 存在的理由只有一个：让"用哪个 provider"成为配置，而不是散落在宿主里的一堆 if。
/// 连接器本身不含重试与路由，这里也不加——那会让"失败必须可见"变模糊。
/// </remarks>
public static class ChatClientFactory
{
    /// <summary>
    /// 按配置建立连接器；密钥按"环境变量 > 本机凭据文件"的顺序读取。
    /// </summary>
    /// <remarks>
    /// <b>为什么环境变量优先</b>：CI 与一次性会话用它覆盖，比改文件方便；
    /// 而且这与本项目的既有行为一致，老用法不会因为加了凭据文件而改变。
    /// <para>
    /// <b>凭据文件是给常规用户的</b>：让他们填一次，之后自动读。
    /// 格式与权限见 <see cref="CredentialFile"/>。
    /// </para>
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// 需要密钥但两处都没找到。
    /// </exception>
    public static IChatClient Create(ChatProviderConfig config, HttpClient? http = null)
    {
        ArgumentNullException.ThrowIfNull(config);

        var apiKey = ResolveApiKey(config);

        // **需要密钥却取不到要在这里明确失败**。
        // 不失败的话，请求会带着空 Authorization 发出去，用户看到的是一句
        // provider 侧的 401——那完全看不出"你还没配密钥"，
        // 而这是最容易自己解决的一类问题。
        if (string.IsNullOrWhiteSpace(apiKey)
            && !string.IsNullOrWhiteSpace(config.ApiKeyName))
        {
            throw new InvalidOperationException(
                $"provider '{config.Name}' 需要密钥 '{config.ApiKeyName}'，但未找到。\n"
                + "两种设置方式：\n"
                + $"  · 在本机凭据文件里保存（推荐，一次即可）：dougong model\n"
                + $"  · 或设环境变量：export {config.ApiKeyName}='<你的密钥>'");
        }

        return Create(config, apiKey, http);
    }

    /// <summary>
    /// 从本机凭据文件取密钥；没有返回空串。
    /// </summary>
    /// <remarks>
    /// <b>只有一处来源，与 dsh / hermes 一致。</b>
    /// <list type="bullet">
    /// <item>dsh：<c>~/.dsh/.credentials.yaml</c> 是主存储</item>
    /// <item>hermes：<c>~/.hermes/.env</c> 文件本身就是主存储</item>
    /// </list>
    ///
    /// <para>
    /// <b>为什么删掉了环境变量回退</b>：它不是一个存储——随终端会话消失，
    /// 要求用户每次开新窗口重设。保留它只会制造两种失败：
    /// 一是残留的旧值悄悄盖过用户填好的密钥，二是"我明明配过"却因为换了终端
    /// 而失效。两种都极难自查。密钥只有一处权威来源，问题就只有一个方向。
    /// </para>
    ///
    /// <para>
    /// <b><c>key_env</c> 字段仍然保留</b>：它是凭据文件里的**条目名**
    /// （如 <c>DEEPSEEK_API_KEY</c>），不是环境变量名。名字带 env 是历史遗留，
    /// 语义已经改成"凭据条目的键"。
    /// </para>
    ///
    /// <para>
    /// 需要临时试另一个端点时用 <see cref="FromEnvironment"/> 那条独立路径——
    /// 它用 <c>DOUGONG_PROVIDER_*</c> 配整套 provider，与这里的凭据文件互不影响。
    /// </para>
    /// </remarks>
    public static string ResolveApiKey(ChatProviderConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        // 内存值优先：它来自 DOUGONG_PROVIDER_* 那条独立路径，
        // 本来就该压过凭据文件（"这次换一个端点跑"）。
        if (!string.IsNullOrWhiteSpace(config.ApiKey)) return config.ApiKey;

        if (string.IsNullOrWhiteSpace(config.ApiKeyName))
            return string.Empty;

        return CredentialFile.TryRead(config.ApiKeyName) ?? string.Empty;
    }

    /// <summary>
    /// 用显式传入的密钥建立连接器。仅用于宿主自己已有密钥通道的场景
    /// （例如从凭据管理器读取），不要用于把密钥写进配置文件。
    /// </summary>
    public static IChatClient Create(
        ChatProviderConfig config, string apiKey, HttpClient? http = null)
    {
        ArgumentNullException.ThrowIfNull(config);

        IChatWireFormat format = config.Protocol switch
        {
            ChatProtocol.OpenAiChatCompletions => new OpenAiChatCompletionsFormat(config.Compat),
            ChatProtocol.OpenAiResponses => new OpenAiResponsesFormat(),
            ChatProtocol.AnthropicMessages => new AnthropicMessagesFormat(
                config.MessagesPath ?? "/v1/messages"),
            _ => throw new ArgumentOutOfRangeException(
                nameof(config), config.Protocol, "未知的线协议。"),
        };

        return new HttpClientChatClient(config.BaseUrl, apiKey, config.Model, format, http);
    }

    /// <summary>
    /// 从环境变量读一份配置：<c>DOUGONG_PROVIDER_BASE</c>、<c>DOUGONG_PROVIDER_KEY</c>、
    /// <c>DOUGONG_PROVIDER_MODEL</c>、<c>DOUGONG_PROVIDER_PROTOCOL</c>、<c>DOUGONG_ANTHROPIC_PATH</c>。
    /// </summary>
    public static ChatProviderConfig? FromEnvironment()
    {
        var baseUrl = Environment.GetEnvironmentVariable("DOUGONG_PROVIDER_BASE");
        var model = Environment.GetEnvironmentVariable("DOUGONG_PROVIDER_MODEL");
        if (string.IsNullOrWhiteSpace(baseUrl) || string.IsNullOrWhiteSpace(model)) return null;

        var protocol = (Environment.GetEnvironmentVariable("DOUGONG_PROVIDER_PROTOCOL") ?? "openai")
            .ToLowerInvariant() switch
            {
                "anthropic" => ChatProtocol.AnthropicMessages,
                "responses" => ChatProtocol.OpenAiResponses,
                _ => ChatProtocol.OpenAiChatCompletions,
            };

        return new ChatProviderConfig
        {
            Name = Environment.GetEnvironmentVariable("DOUGONG_PROVIDER_NAME") ?? "default",
            BaseUrl = baseUrl,
            Model = model,
            Protocol = protocol,

            // **这条路径不用凭据文件**：值就在 DOUGONG_PROVIDER_KEY 里。
            // 放进内存字段而不是让 ApiKeyName 指向一个不存在的条目。
            ApiKeyName = null,
            ApiKey = Environment.GetEnvironmentVariable("DOUGONG_PROVIDER_KEY"),
            MessagesPath = Environment.GetEnvironmentVariable("DOUGONG_ANTHROPIC_PATH"),
        };
    }

    /// <summary>
    /// 按「配置文件 → 环境变量覆盖」解析出本次要用的 provider。
    /// </summary>
    /// <remarks>
    /// <b>优先级：环境变量 &gt; 配置文件</b>。理由是排错场景——
    /// 用户改了配置却不生效时，最常见的原因是他 shell 里还有一个旧的 export。
    /// 让环境变量赢，就能用"临时 export 一个值"来验证配置本身对不对；
    /// 反过来则必须先清掉环境变量才能试配置，多一步且不直观。
    /// <para>
    /// <b>环境变量的覆盖粒度是整个 provider，不是单个字段</b>：它本来就是
    /// "这次换一个端点跑"的意思。逐字段覆盖会造出配置文件的协议 + 环境变量的端点
    /// 这类通常不成立的组合。
    /// </para>
    /// <para>
    /// 全部来源都没有时返回 null——调用方据此报"未配置"，而不是拿空端点去发请求。
    /// </para>
    /// </remarks>
    /// <param name="configPath">配置文件路径；null 用 <see cref="ProviderConfigFile.DefaultPath"/>。</param>
    /// <param name="catalog">解析出的目录；无任何配置时为 null。</param>
    /// <param name="error">配置文件有错时的诊断；成功时为 null。</param>
    public static string? TryResolve(
        string? configPath, out ProviderCatalog? catalog, out string? error)
    {
        catalog = null;
        error = ProviderConfigFile.TryLoad(configPath, out var fromFile);
        if (error is not null) return error;

        // 环境变量优先：覆盖**选中项**，但不丢弃配置文件里的其它 provider。
        // 否则 /model 的列表会凭空少一批，用户会以为配置没被读到。
        if (FromEnvironment() is { } fromEnv)
        {
            catalog = fromFile is null
                ? ProviderCatalog.Create([fromEnv], fromEnv.Name, sourcePath: "环境变量")
                : fromFile.WithSelectedOverride(fromEnv);
            return null;
        }

        catalog = fromFile;
        return null;
    }
}
