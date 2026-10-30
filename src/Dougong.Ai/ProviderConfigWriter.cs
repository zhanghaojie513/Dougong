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
    /// <param name="baseUrl">新的端点地址；null 表示不改。</param>
    public static WriteResult UpdateProvider(
        string path, string providerName, string? model, bool setAsDefault,
        string? baseUrl = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(providerName);

        // 文件不存在时新建：首次配置就是这个路径，让用户手写一整份 TOML
        // 是不必要的门槛。
        if (!File.Exists(path))
            return Create(path, providerName, model, setAsDefault, baseUrl);

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
            // 段不存在 = 用户选了一个还没写进配置的 provider（内置预设就是这种）。
            // **必须能新建**：列表里既然把它列出来了，选中后报"没有这一段"
            // 就是把选择权给了用户又收回去——实测被指出过，是主要问题。
            if (!ProviderCatalog.Presets.TryGetValue(providerName, out var preset))
            {
                var known = string.Join(", ", ProviderCatalog.Presets.Keys);
                return new WriteResult(false,
                    $"'{providerName}' 不是内置预设，无法自动确定端点。"
                    + $"内置：{known}。自定义端点请手工在配置文件里加一段 "
                    + $"[providers.{providerName}]，再跑这个命令改模型。", null);
            }

            if (string.IsNullOrWhiteSpace(model))
            {
                return new WriteResult(false,
                    $"新建 provider '{providerName}' 需要模型名。", null);
            }

            var appended = AppendProviderTableText(text, providerName, model, preset);

            // 拼接后必须重新 parse 校验：文本层拼接最怕引用/转义出错，
            // 而写出一个坏配置比不写更糟——用户下次启动会看到看不懂的解析错误。
            var check = SyntaxParser.Parse(appended, path, false);
            if (check.HasErrors)
            {
                var firstError = check.Diagnostics.FirstOrDefault();
                return new WriteResult(false,
                    $"生成的新配置无法解析，未写入：{firstError?.Message ?? "未知错误"}", null);
            }

            var backupForNew = Backup(path, text);
            try
            {
                var finalText = appended;
                if (setAsDefault)
                {
                    var withDefault = SyntaxParser.Parse(appended, path, false);
                    var rootKey = FindRootKey(withDefault, "default_provider");
                    if (rootKey is null)
                        withDefault.KeyValues.Add(NewKeyValue("default_provider", providerName));
                    else
                        rootKey.Value = new StringValueSyntax(providerName);
                    finalText = withDefault.ToString() ?? appended;
                }

                WriteAtomic(path, finalText);
            }
            catch (Exception ex)
            {
                return new WriteResult(false,
                    $"写回失败：{ex.GetType().Name}：{ex.Message}", backupForNew);
            }

            return new WriteResult(true, null, backupForNew);
        }


        var changed = false;

        // 端点覆盖：交互式流程允许用户改端点，写回时也要跟着改。
        // 端点填错是最难自己发现的错——表现出来只是 404 或连接超时。
        if (baseUrl is not null)
        {
            var baseKey = FindKey(providerTable, "base_url");
            if (baseKey is null)
            {
                providerTable.Items.Add(NewKeyValue("base_url", baseUrl));
                changed = true;
            }
            else if (!string.Equals(ReadString(baseKey), baseUrl, StringComparison.Ordinal))
            {
                baseKey.Value = new StringValueSyntax(baseUrl);
                changed = true;
            }
        }

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

    /// <summary>
    /// 往配置文本末尾追加一张 <c>[providers.&lt;name&gt;]</c> 表，返回新文本。
    /// </summary>
    /// <remarks>
    /// <b>为什么在文本层拼接而不是在语法树里造节点</b>：Tomlyn 的表节点
    /// 与其父文档强绑定（换父级会抛「already attached to another parent」，
    /// <c>Parent</c> 还是只读的），手工 <c>new TableSyntax</c> 又拿不到方括号——
    /// <c>Name</c> 的值本身就是 <c>[providers.x]</c>。实测两种造法都写出了
    /// 坏文本。拼接后**整体重新 parse 一次**做校验，坏文本会被当场拦下。
    /// <para>
    /// 追加在文件末尾：TOML 的表顺序不影响语义，而末尾是唯一不会切断
    /// 用户原有内容的位置。
    /// </para>
    /// </remarks>
    private static string AppendProviderTableText(
        string text, string providerName, string model, ProviderCatalog.ProviderPreset preset)
    {
        var newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";

        // 末尾没有换行时先补一个，否则表头会粘到上一行末尾。
        var builder = new StringBuilder(text);
        if (builder.Length > 0 && !text.EndsWith('\n'))
            builder.Append(newline);
        builder.Append(newline);

        builder.Append('[').Append("providers.").Append(providerName).Append(']').Append(newline);
        builder.Append("base_url = \"").Append(preset.BaseUrl).Append('"').Append(newline);
        builder.Append("wire_api = \"").Append(WireApiName(preset.Protocol)).Append('"').Append(newline);
        builder.Append("model = \"").Append(model).Append('"').Append(newline);
        builder.Append("key_env = \"").Append(preset.ApiKeyEnvironmentVariable).Append('"').Append(newline);

        return builder.ToString();
    }

    /// <summary>新建一份最小配置。</summary>
    private static WriteResult Create(
        string path, string providerName, string? model, bool setAsDefault, string? baseUrl)
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
        // 端点优先用调用方传的：交互式流程允许用户改端点，
        // 而忽略它会让向导显示的地址和写进文件的不一致——实测踩到过。
        text.AppendLine($"base_url = \"{baseUrl ?? preset.BaseUrl}\"");
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
