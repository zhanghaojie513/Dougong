using Tomlyn;
using Tomlyn.Model;

namespace Dougong.Ai;

/// <summary>
/// 从 <c>config.toml</c> 的 <c>[preferences]</c> 段读用户偏好。
/// </summary>
/// <remarks>
/// <b>为什么不单独一个文件</b>：用户已经有一个 <c>config.toml</c>，
/// 再加一个"偏好.toml"会让他不知道该改哪个。参考项目也都是一个文件：
/// hermes 的 <c>config.yaml</c> 同时装 provider、显示、压缩、审批设置。
///
/// <para>
/// <b>段名 <c>[preferences]</c> 而不是顶层散字段</b>：与
/// <c>[providers.&lt;name&gt;]</c> 的层级风格一致，且给未来留了空间
/// （以后加 <c>[ui]</c>、<c>[tools]</c> 不会与现有键冲突）。
/// </para>
///
/// <para>
/// <b>读失败一律回落到默认值，不抛</b>：偏好是增强不是必需品。
/// 一个写坏的 <c>shell = "yes please"</c> 不该让整个会话起不来——
/// 那时用户看到的会是"程序坏了"，而不是"你的配置有一处小错"。
/// 真正的语法错误由 <see cref="ProviderConfigFile"/> 那条路径负责报出。
/// </para>
/// </remarks>
public static class UserPreferencesFile
{
    /// <summary>读取偏好；文件不存在或该段缺失时返回全默认值。</summary>
    /// <param name="error">解析失败时的一句话说明；成功时 null。</param>
    /// <param name="path">配置路径；null 用默认路径。</param>
    public static UserPreferences TryLoad(out string? error, string? path = null)
    {
        error = null;
        path ??= ProviderConfigFile.DefaultPath;

        if (!File.Exists(path)) return new UserPreferences();

        TomlTable model;
        try
        {
            model = TomlSerializer.Deserialize<TomlTable>(File.ReadAllText(path)) ?? [];
        }
        catch (Exception ex)
        {
            // 语法错误交给 ProviderConfigFile 报；这里只回落到默认。
            error = ex.Message;
            return new UserPreferences();
        }

        if (!model.TryGetValue("preferences", out var section)
            || section is not TomlTable table)
        {
            return new UserPreferences();
        }

        return new UserPreferences
        {
            Shell = ReadBool(table, "shell"),
            Memory = ReadBool(table, "memory"),
            ShowReasoning = ReadBool(table, "show_reasoning"),
            MemoryDirectory = ReadString(table, "memory_dir"),
        };
    }

    /// <summary>把偏好写进配置文件，保留文件里其他内容。</summary>
    /// <remarks>
    /// <b>必须保留其他段</b>：用户可能配了十几个 provider。整体重写会把它们冲掉——
    /// 那是"改一个开关，丢掉全部 provider"级别的事故。
    /// <para>
    /// <b>写法是追加或替换 <c>[preferences]</c> 段</b>，在文本层面做：
    /// 用 Tomlyn 的语法树改表头容易把格式弄乱（此前踩过），
    /// 而这一段是扁平的、只有三五个布尔键，文本处理更可控。
    /// </para>
    /// </remarks>
    public static void Save(UserPreferences preferences, string? path = null)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        path ??= ProviderConfigFile.DefaultPath;

        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        var existing = File.Exists(path) ? File.ReadAllText(path) : string.Empty;
        var withoutSection = RemoveSection(existing, "preferences");

        var builder = new System.Text.StringBuilder(withoutSection);
        if (builder.Length > 0 && !builder.ToString().EndsWith('\n')) builder.Append('\n');
        if (builder.Length > 0) builder.Append('\n');

        builder.Append("[preferences]\n");
        builder.Append(preferences.Shell ? "shell = true\n" : "shell = false\n");
        builder.Append(preferences.Memory ? "memory = true\n" : "memory = false\n");
        builder.Append(preferences.ShowReasoning ? "show_reasoning = true\n" : "show_reasoning = false\n");

        // 路径只在设了时才写：写个空值进去会让用户以为它有意义。
        if (!string.IsNullOrWhiteSpace(preferences.MemoryDirectory))
        {
            builder.Append("memory_dir = \"").Append(preferences.MemoryDirectory).Append("\"\n");
        }

        File.WriteAllText(path, builder.ToString());
    }

    /// <summary>删掉一个 <c>[name]</c> 段（到下一个表头或文件尾）。</summary>
    /// <remarks>
    /// 只认**顶格**的表头：<c>[preferences]</c> 与 <c>[providers.x]</c> 都是顶格，
    /// 而数组表 <c>[[x]]</c> 与嵌套缩进不受影响。
    /// </remarks>
    private static string RemoveSection(string text, string name)
    {
        if (text.Length == 0) return text;

        var lines = text.Replace("\r\n", "\n").Split('\n');
        var kept = new List<string>(lines.Length);
        var skipping = false;

        foreach (var line in lines)
        {
            var trimmed = line.TrimStart();

            if (trimmed.StartsWith('[') && !trimmed.StartsWith("[["))
            {
                skipping = trimmed.StartsWith($"[{name}]", StringComparison.Ordinal);
            }
            else if (trimmed.StartsWith("[[", StringComparison.Ordinal))
            {
                skipping = false;
            }

            if (!skipping) kept.Add(line);
        }

        // 去掉尾部多余空行，避免每次保存都累积。
        while (kept.Count > 0 && kept[^1].Length == 0) kept.RemoveAt(kept.Count - 1);
        return kept.Count == 0 ? string.Empty : string.Join('\n', kept) + '\n';
    }

    /// <summary>读字符串；缺失、空串或类型不符返回 null。</summary>
    /// <remarks>
    /// 空串按"未设置"处理：用户写了 <c>memory_dir = ""</c> 与不写这个键，
    /// 语义上是同一件事，区别对待只会多一条要维护的分支。
    /// </remarks>
    private static string? ReadString(TomlTable table, string key) =>
        table.TryGetValue(key, out var value)
        && value is string text
        && !string.IsNullOrWhiteSpace(text)
            ? text
            : null;

    /// <summary>读布尔；缺失或类型不符一律 false。</summary>
    /// <remarks>
    /// 类型不符时**不报错也不猜**（`"yes"` 不当成 true）：猜错会让用户
    /// 以为开了而实际没开，而那种问题只能靠试才发现。
    /// </remarks>
    private static bool ReadBool(TomlTable table, string key) =>
        table.TryGetValue(key, out var value) && value is true;
}
