using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Dougong.Mcp;

/// <summary>
/// 一个 MCP server 的资源与 Prompt 目录。
/// </summary>
/// <remarks>
/// 为什么不把它们也做成 <c>IAgentTool</c>：工具是**模型主动调用并产生副作用**的东西，
/// 走 dispatcher 的审批与资源协调。资源是**读取**，Prompt 是**模板**——
/// 它们没有副作用，也不需要审批，强行塞进工具管线会让审批变成噪音。
/// <para>
/// 但读资源仍是一次远端调用，可能失败、可能返回大块内容，
/// 所以迁移到模型上下文时必须经过大小限制与错误处理，不能直接塞。
/// </para>
/// </remarks>
public sealed class McpResourceCatalog
{
    /// <summary>单次读取允许的最大字符数；超出截断并标记。</summary>
    public const int MaxResourceChars = 256 * 1024;

    private readonly McpClient _client;
    private readonly string _serverName;

    public McpResourceCatalog(McpClient client, string serverName)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentException.ThrowIfNullOrWhiteSpace(serverName);
        _client = client;
        _serverName = serverName;
    }

    public string ServerName => _serverName;

    /// <summary>列出远端资源。</summary>
    public async Task<IReadOnlyList<McpResourceEntry>> ListResourcesAsync(
        CancellationToken cancellationToken = default)
    {
        try
        {
            var resources = await _client.ListResourcesAsync(
                cancellationToken: cancellationToken).ConfigureAwait(false);

            return [.. resources.Select(r => new McpResourceEntry(
                _serverName, r.Uri, r.Name, r.Description, r.MimeType))];
        }
        catch (Exception)
        {
            // 远端不支持 resources（能力未声明或报错）：返回空目录而不是抛错。
            // 调用方据此显示"该 server 没有资源"。
            return [];
        }
    }

    /// <summary>列出远端资源模板（带参数化的 URI）。</summary>
    public async Task<IReadOnlyList<McpResourceTemplateEntry>> ListTemplatesAsync(
        CancellationToken cancellationToken = default)
    {
        try
        {
            var templates = await _client.ListResourceTemplatesAsync(
                cancellationToken: cancellationToken).ConfigureAwait(false);

            return [.. templates.Select(t => new McpResourceTemplateEntry(
                _serverName, t.UriTemplate, t.Name, t.Description, t.MimeType))];
        }
        catch (Exception)
        {
            return [];
        }
    }

    /// <summary>
    /// 读取一个资源的内容。
    /// </summary>
    /// <remarks>
    /// 返回结构化结果而不是抛错：读取失败是**可预期**的（资源被删、权限不足、网络问题），
    /// 调用方需要把失败原因如实转述给用户，而不是让整个会话崩掉。
    /// </remarks>
    public async Task<McpResourceReadResult> ReadAsync(
        string uri, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(uri);

        try
        {
            var result = await _client.ReadResourceAsync(
                uri, cancellationToken: cancellationToken).ConfigureAwait(false);

            var parts = new List<string>();
            var truncated = false;
            var total = 0;

            foreach (var content in result.Contents)
            {
                var text = content switch
                {
                    TextResourceContents textContent => textContent.Text ?? string.Empty,
                    BlobResourceContents blob => $"[binary {blob.MimeType ?? "unknown"}]",
                    _ => string.Empty,
                };

                if (string.IsNullOrEmpty(text)) continue;

                if (total + text.Length > MaxResourceChars)
                {
                    // 截断必须显式标记：静默截断会让模型以为看到了全部内容。
                    parts.Add(text[..Math.Max(0, MaxResourceChars - total)]);
                    truncated = true;
                    break;
                }

                parts.Add(text);
                total += text.Length;
            }

            return new McpResourceReadResult(uri, string.Join("\n", parts), truncated, null);
        }
        catch (Exception ex)
        {
            return new McpResourceReadResult(uri, null, false, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>列出远端 Prompt 模板。</summary>
    public async Task<IReadOnlyList<McpPromptEntry>> ListPromptsAsync(
        CancellationToken cancellationToken = default)
    {
        try
        {
            var prompts = await _client.ListPromptsAsync(
                cancellationToken: cancellationToken).ConfigureAwait(false);

            return [.. prompts.Select(p => new McpPromptEntry(
                _serverName, p.Name, p.Description,
                // 参数 schema 从远端 Prompt 的 arguments 读取：
                // 只保留名称与是否必需，不暴露远端内部结构。
                p.ProtocolPrompt.Arguments?.Select(a =>
                    new McpPromptArgument(a.Name, a.Description, a.Required ?? false)).ToList() ?? []))];
        }
        catch (Exception)
        {
            return [];
        }
    }

    /// <summary>
    /// 取一个 Prompt 的展开结果（通常是一组预填消息）。
    /// </summary>
    /// <remarks>
    /// Prompt 是模板，取回后由宿主决定怎么用（通常塞进对话开头）。
    /// 这里只负责取回并折叠成文本，不替宿主决定消息角色。
    /// </remarks>
    public async Task<McpPromptResult> GetPromptAsync(
        string name, IReadOnlyDictionary<string, string>? arguments = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        try
        {
            var converted = arguments is null
                ? null
                : new Dictionary<string, object?>(
                    arguments.Select(pair => new KeyValuePair<string, object?>(pair.Key, pair.Value)),
                    StringComparer.Ordinal);

            var result = await _client.GetPromptAsync(
                name, converted, cancellationToken: cancellationToken).ConfigureAwait(false);

            var parts = new List<string>();
            foreach (var message in result.Messages)
            {
                var text = message.Content switch
                {
                    TextContentBlock block => block.Text ?? string.Empty,
                    EmbeddedResourceBlock embedded => embedded.Resource is TextResourceContents r
                        ? r.Text ?? string.Empty
                        : string.Empty,
                    _ => string.Empty,
                };

                if (text.Length > 0) parts.Add($"[{message.Role}] {text}");
            }

            return new McpPromptResult(name, parts, null);
        }
        catch (Exception ex)
        {
            return new McpPromptResult(name, [], $"{ex.GetType().Name}: {ex.Message}");
        }
    }
}

/// <summary>资源目录中的一项。</summary>
public sealed record McpResourceEntry(
    string ServerName, string Uri, string Name, string? Description, string? MimeType);

/// <summary>资源模板中的一项（URI 带参数占位）。</summary>
public sealed record McpResourceTemplateEntry(
    string ServerName, string UriTemplate, string Name, string? Description, string? MimeType);

/// <summary>Prompt 模板中的一项。</summary>
public sealed record McpPromptEntry(
    string ServerName, string Name, string? Description, IReadOnlyList<McpPromptArgument> Arguments);

/// <summary>Prompt 的一个参数。</summary>
public sealed record McpPromptArgument(string Name, string? Description, bool Required);

/// <summary>资源读取结果。失败时 <see cref="Text"/> 为 null，原因在 <see cref="Failure"/>。</summary>
public sealed record McpResourceReadResult(
    string Uri, string? Text, bool Truncated, string? Failure)
{
    public bool Succeeded => Failure is null;

    /// <summary>转成模型可读的一段文本；失败时如实说明失败，不伪造内容。</summary>
    public JsonNode ToModelContent() => new JsonObject
    {
        ["uri"] = Uri,
        ["text"] = Text,
        ["truncated"] = Truncated,
        ["error"] = Failure,
    };
}

/// <summary>Prompt 取回结果。</summary>
public sealed record McpPromptResult(
    string Name, IReadOnlyList<string> Messages, string? Failure)
{
    public bool Succeeded => Failure is null;

    /// <summary>折叠成一段文本，保持角色标注。</summary>
    public JsonNode ToModelContent() => new JsonObject
    {
        ["name"] = Name,
        ["text"] = string.Join("\n", Messages),
        ["error"] = Failure,
    };
}
