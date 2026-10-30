using System.Text;
using Tomlyn.Model;
using Tomlyn.Parsing;
using Tomlyn.Syntax;

namespace Dougong.Ai;

/// <summary>
/// 把选择写回 <c>config.toml</c>。
/// </summary>
/// <remarks>
/// <b>为什么不用序列化器整体重写</b>：那会把用户的注释、空行、字段顺序
/// 全部丢掉。配置文件是用户手写的，注释往往记着"为什么这么配"；
/// 一条配置命令顺手抹掉它们，比不改更糟——用户会不敢再跑这个命令。
/// 所以这里改的是**语法树节点**（保留全部 trivia），再整体输出。
/// <para>
/// <b>为什么不用正则替换</b>：正则匹配 `<c>model = "..."</c>` 会误伤注释里的
/// 同样文本、也分不清它属于哪个 <c>[providers.x]</c> 段。语法树能按表定位。
/// </para>
/// </remarks>
public static class ProviderConfigWriter
{
    /// <summary>一次写回的结果。</summary>
    /// <param name="Succeeded">是否成功。</param>
    /// <param name="Error">失败原因；成功时为 null。</param>
    /// <param name="BackupPath">改动前留下的备份路径；无原文件时为 null。</param>
    public sealed record WriteResult(bool Succeeded, string? Error, string? BackupPath);

    /// <summary>
    /// 设置某个 provider 的字段，并可选地设置 <c>default_provider</c>。
    /// </summary>
    /// <param name="path">配置文件路径。</param>
    /// <param name="providerName">要改的 provider 名。</param>
    /// <param name="model">新的模型名；null 表示不改。</param>
    /// <param name="setAsDefault">是否把 <c>default_provider</c> 指到它。</param>
    public static WriteResult UpdateProvider(
        string path, string providerName, string? model, bool setAsDefault)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(providerName);

        // 文件不存在时新建：首次配置就是这个路径，让用户手写一整份 TOML
        // 是不必要的门槛。
        if (!File.Exists(path))
            return Create(path, providerName, model, setAsDefault);

        string text;
        try
        {
            text = File.ReadAllText(path);
        }
        catch (Exception ex)
        {
            return new WriteResult(false, $"读取配置失败：{ex.GetType().Name}：{ex.Message}", null);
        }

        var document = SyntaxParser.Parse(text, path, false);
        if (document.HasErrors)
        {
            var first = document.Diagnostics.FirstOrDefault();
            // 原文件有语法错时不改。这时候任何写回都会把错误固化下来，
            // 用户更难看出是自己写错了还是工具改坏了。
            return new WriteResult(false,
                $"配置有语法错误，未改动：{first?.Message ?? "未知错误"}", null);
        }

        var providerTable = FindTable(document, providerName);
        if (providerTable is null)
        {
            // 不自动新建段：往用户文件里凭空插入一张表，位置和字段都得猜，
            // 猜出来的东西用户下次看文件时会困惑"这不是我写的"。
            // 交互式流程会走 Create 分支新建整个文件；这里只报清楚可选项。
            var existing = document.Tables is null
                ? []
                : document.Tables.OfType<TableSyntax>()
                    .Select(t => t.Name?.ToString()?.Trim())
                    .Where(n => n is not null && n.StartsWith("providers.", StringComparison.Ordinal))
                    .Select(n => n!["providers.".Length..])
                    .ToArray();

            return new WriteResult(false,
                $"配置里没有 [providers.{providerName}] 段，未改动。"
                + (existing.Length > 0
                    ? $"现有：{string.Join(", ", existing)}。"
                    : "现有：无。")
                + "要新增请手工加这一段，或用交互式 dougong model。", null);
        }

        var changed = false;

        if (model is not null)
        {
            var key = FindKey(providerTable, "model");
            if (key is null)
            {
                // 段里没有 model：追加到最后一条键值之后，沿用它的缩进。
                providerTable.Items.Add(NewKeyValue("model", model));
                changed = true;
            }
            else if (!string.Equals(ReadString(key), model, StringComparison.Ordinal))
            {
                key.Value = new StringValueSyntax(model);
                changed = true;
            }
        }

        if (setAsDefault)
        {
            var root = FindRootKey(document, "default_provider");
            if (root is null)
            {
                // 没有就放在文件最前面：TOML 里顶层键必须在任何 [table] 之前，
                // 否则会被解析成上一张表里的键。
                document.KeyValues.Add(NewKeyValue("default_provider", providerName));
                changed = true;
            }
            else if (!string.Equals(ReadString(root), providerName, StringComparison.Ordinal))
            {
                root.Value = new StringValueSyntax(providerName);
                changed = true;
            }
        }

        if (!changed)
            return new WriteResult(true, null, null);

        var backup = Backup(path, text);

        try
        {
            WriteAtomic(path, document.ToString() ?? text);
        }
        catch (Exception ex)
        {
            return new WriteResult(false, $"写回失败：{ex.GetType().Name}：{ex.Message}", backup);
        }

        return new WriteResult(true, null, backup);
    }

    /// <summary>新建一份最小配置。</summary>
    private static WriteResult Create(
        string path, string providerName, string? model, bool setAsDefault)
    {
        // 新文件只写用户要的那个 provider。预设里有的端点信息从 ProviderCatalog 取，
        // 免得用户为了"先跑起来"还要去查一遍 base_url。
        if (!ProviderCatalog.Presets.TryGetValue(providerName, out var preset))
        {
            return new WriteResult(false,
                $"没有名为 '{providerName}' 的内置预设，无法自动创建配置。"
                + $"可用的预设：{string.Join(", ", ProviderCatalog.Presets.Keys)}。"
                + "其他端点请手工写配置文件。", null);
        }

        if (model is null)
        {
            return new WriteResult(false,
                $"新建配置需要同时给出模型名（--model）。", null);
        }

        var text = new StringBuilder();
        // 新建文件时这个 provider 就是唯一的一个，直接设为默认——
        // 否则文件建好了却没有 default_provider，启动时还得再猜一次。
        text.AppendLine($"default_provider = \"{providerName}\"");
        text.AppendLine();
        text.AppendLine($"[providers.{providerName}]");
        text.AppendLine($"base_url = \"{preset.BaseUrl}\"");
        text.AppendLine($"wire_api = \"{WireApiName(preset.Protocol)}\"");
        text.AppendLine($"model = \"{model}\"");
        text.AppendLine($"key_env = \"{preset.ApiKeyEnvironmentVariable}\"");

        try
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            WriteAtomic(path, text.ToString());
        }
        catch (Exception ex)
        {
            return new WriteResult(false, $"创建配置失败：{ex.GetType().Name}：{ex.Message}", null);
        }

        return new WriteResult(true, null, null);
    }

    private static string WireApiName(ChatProtocol protocol) => protocol switch
    {
        ChatProtocol.OpenAiResponses => "responses",
        ChatProtocol.AnthropicMessages => "anthropic",
        _ => "openai",
    };

    /// <summary>写临时文件再替换，避免中途失败留下半个文件。</summary>
    private static void WriteAtomic(string path, string content)
    {
        var temp = path + ".tmp";
        // 统一 LF：CRLF 会让 round-trip 后的 diff 整片变红。
        File.WriteAllText(temp, content.Replace("\r\n", "\n").Replace("\n", Environment.NewLine),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        File.Move(temp, path, overwrite: true);
    }

    private static string? Backup(string path, string original)
    {
        try
        {
            var backup = path + ".bak";
            File.WriteAllText(backup, original);
            return backup;
        }
        catch
        {
            // 备份失败不阻断写回：配置是纯文本，用户 git 里通常也有。
            return null;
        }
    }

    private static string? ReadString(KeyValueSyntax key) =>
        key.Value is StringValueSyntax s ? s.Value : key.Value?.ToString()?.Trim('"');

    private static KeyValueSyntax NewKeyValue(string key, string value) =>
        new()
        {
            Key = new KeySyntax(key),
            // 等号与换行必须显式给：省掉会生成 `key"value"` 这种缺分隔符的文本，
            // 它连着两个字面量但语法不成立。**实测踩过**。
            EqualToken = SyntaxFactory.Token(TokenKind.Equal),
            Value = new StringValueSyntax(value),
            EndOfLineToken = SyntaxFactory.NewLine(),
        };

    /// <summary>在整份文档里找 <c>[providers.&lt;name&gt;]</c> 这张表。</summary>
    private static TableSyntax? FindTable(DocumentSyntax document, string providerName)
    {
        if (document.Tables is null) return null;

        foreach (var table in document.Tables.OfType<TableSyntax>())
        {
            var name = table.Name?.ToString()?.Trim();
            if (name is null) continue;

            // 只要两级及以上精确匹配 providers.<name>。
            // 用 EndsWith 会误配 [foo.providers.deepseek] 这种嵌套表。
            var parts = name.Split('.');
            if (parts.Length == 2
                && parts[0].Trim() == "providers"
                && parts[1].Trim() == providerName)
            {
                return table;
            }
        }

        return null;
    }

    private static KeyValueSyntax? FindKey(TableSyntax table, string keyName) =>
        table.Items?.FirstOrDefault(kv =>
            kv.Key?.ToString()?.Trim() == keyName);

    /// <summary>在顶层键值里找（不在任何表内的那些）。</summary>
    private static KeyValueSyntax? FindRootKey(DocumentSyntax document, string keyName) =>
        document.KeyValues?.FirstOrDefault(kv =>
            kv.Key?.ToString()?.Trim() == keyName);
}
