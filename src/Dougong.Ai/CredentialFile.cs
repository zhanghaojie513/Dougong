using System.Text;

namespace Dougong.Ai;

/// <summary>
/// 本机凭据文件：把密钥存在一个仅当前用户可读的文件里。
/// </summary>
/// <remarks>
/// <b>这是照 hermes / pi / dsh 的做法，不是 OS 凭据管理器</b>。四家实测结论：
/// <list type="bullet">
/// <item>pi / pi.net：<c>~/.pi/agent/auth.json</c>，明文，0600</item>
/// <item>dsh：<c>~/.dsh/.credentials.yaml</c>，明文，0600 + 目录 0700</item>
/// <item>hermes：<c>~/.hermes/.env</c>，明文，0600</item>
/// </list>
/// <b>没有一家用系统钥匙串存 API key。</b> dsh 的 README 明确写着
/// "an OS-keychain provider is deferred … none is shipped"。
///
/// <para>
/// <b>为什么不用钥匙串</b>：Linux 上它依赖 D-Bus 与会话密钥环，
/// 无头容器和 CI 里通常没有；三平台三套 API，收益只是"防同 UID 进程"——
/// 而 0600 已经挡住了其他用户。四家都算过这笔账，结论一致。
/// </para>
///
/// <para>
/// <b>Windows 上 0600 是空操作</b>：NTFS 不看 POSIX 位。四家都一样
/// （pi.net 代码里直接 <c>if (OperatingSystem.IsWindows()) return;</c>）。
/// 这是**已知缺口**，不假装解决——文件仍受用户配置文件目录的默认 ACL 保护。
/// </para>
///
/// <para>
/// <b>与 <c>config.toml</c> 的分工</b>：配置里只写变量名（<c>key_env</c>），
/// 值放这里。两者分开是为了让配置文件可以安全地分享/提交，
/// 而凭据文件从不进版本库。
/// </para>
/// </remarks>
public static class CredentialFile
{
    /// <summary>凭据文件名。</summary>
    public const string FileName = "credentials";

    /// <summary>
    /// 默认凭据文件路径：<c>~/.dougong/credentials</c>。
    /// </summary>
    /// <remarks>
    /// 放在 <c>config.toml</c> 同目录，用户找得到；名字不带扩展名是刻意的——
    /// 它不是一个用户该编辑的文件。
    /// <para>
    /// <b>可用 <c>DOUGONG_CREDENTIALS</c> 整体覆盖</b>，与
    /// <c>DOUGONG_PROVIDER_CONFIG</c> 覆盖配置路径同一做法。
    /// 存在的理由是**测试隔离**：没有它，跑一次 CLI 会读写用户真实的
    /// 凭据文件——那是既污染环境又可能泄漏的一类事故。
    /// </para>
    /// </remarks>
    public static string DefaultPath
    {
        get
        {
            var overridden = Environment.GetEnvironmentVariable("DOUGONG_CREDENTIALS");
            if (!string.IsNullOrWhiteSpace(overridden)) return overridden;

            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return Path.Combine(home, ".dougong", FileName);
        }
    }

    /// <summary>
    /// 读一个变量的值；不存在返回 null。
    /// </summary>
    /// <remarks>
    /// 格式是极简的 <c>KEY=value</c> 行（dotenv 风格），不是 TOML/JSON：
    /// 这个文件只由本程序读写，用最简单的格式最不容易在解析上出错。
    /// <para>
    /// <b>不做引号解析</b>：值取等号后的全部内容，原样返回。
    /// 密钥里可能含 <c>=</c>、<c>#</c>、空格，做转义解析只会引入 bug。
    /// </para>
    /// </remarks>
    public static string? TryRead(string name, string? path = null)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        path ??= DefaultPath;

        if (!File.Exists(path)) return null;

        foreach (var line in File.ReadAllLines(path))
        {
            var trimmed = line.TrimStart();
            if (trimmed.Length == 0 || trimmed[0] == '#') continue;

            var separator = trimmed.IndexOf('=');
            if (separator <= 0) continue;

            if (!string.Equals(trimmed[..separator].Trim(), name, StringComparison.Ordinal))
                continue;

            var value = trimmed[(separator + 1)..];
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }

        return null;
    }

    /// <summary>
    /// 写入或覆盖一个变量。
    /// </summary>
    /// <remarks>
    /// <b>先写临时文件再改名</b>：直接改原文件时若中断，会把已有凭据弄坏——
    /// 那意味着用户明明配过，下次启动却要重配。
    /// <para>
    /// <b>权限尽力而为</b>：POSIX 上设 0600，Windows 上没有等价物（见类型注释）。
    /// 设权限失败不阻断写入——拒写会让功能完全不可用，而文件已经在用户自己的
    /// 配置目录里。
    /// </para>
    /// </remarks>
    public static void Write(string name, string value, string? path = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(value);
        path ??= DefaultPath;

        RejectSecretLookingName(name);

        // 读现有条目，替换同名、保留其他。
        var lines = File.Exists(path)
            ? File.ReadAllLines(path).ToList()
            : [];

        var replaced = false;
        for (var i = 0; i < lines.Count; i++)
        {
            var trimmed = lines[i].TrimStart();
            var separator = trimmed.IndexOf('=');
            if (separator <= 0) continue;
            if (!string.Equals(trimmed[..separator].Trim(), name, StringComparison.Ordinal))
                continue;

            lines[i] = $"{name}={value}";
            replaced = true;
            break;
        }

        if (!replaced) lines.Add($"{name}={value}");

        WriteAtomically(path, lines);
    }

    /// <summary>删除一个变量；不存在算成功。</summary>
    public static bool TryDelete(string name, string? path = null)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        path ??= DefaultPath;

        if (!File.Exists(path)) return false;

        var lines = File.ReadAllLines(path).ToList();
        var before = lines.Count;

        lines.RemoveAll(line =>
        {
            var trimmed = line.TrimStart();
            var separator = trimmed.IndexOf('=');
            return separator > 0
                && string.Equals(trimmed[..separator].Trim(), name, StringComparison.Ordinal);
        });

        if (lines.Count == before) return false;

        WriteAtomically(path, lines);
        return true;
    }

    /// <summary>该文件里有哪些变量名（不含值）。供诊断显示。</summary>
    public static IReadOnlyList<string> ListNames(string? path = null)
    {
        path ??= DefaultPath;
        if (!File.Exists(path)) return [];

        var names = new List<string>();
        foreach (var line in File.ReadAllLines(path))
        {
            var trimmed = line.TrimStart();
            if (trimmed.Length == 0 || trimmed[0] == '#') continue;

            var separator = trimmed.IndexOf('=');
            if (separator <= 0) continue;

            names.Add(trimmed[..separator].Trim());
        }

        return names;
    }

    /// <summary>
    /// 原子写：临时文件 + 改名。
    /// </summary>
    /// <remarks>
    /// 临时文件放在**同目录**：跨目录改名不是原子的（不同卷时变成复制+删除），
    /// 而我们需要"要么旧内容、要么新内容"这个保证。
    /// </remarks>
    private static void WriteAtomically(string path, IReadOnlyList<string> lines)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N")[..8];
        var content = new StringBuilder();
        foreach (var line in lines) content.Append(line).Append('\n');

        File.WriteAllText(temporary, content.ToString());

        // 权限尽力而为：POSIX 上传给目标文件，Windows 上这段是空操作。
        TryRestrictPermissions(temporary);

        File.Move(temporary, path, overwrite: true);
        TryRestrictPermissions(path);
    }

    /// <summary>把文件收紧到仅当前用户可读写。</summary>
    /// <remarks>
    /// <b>失败不抛出</b>：容器、网络盘、某些挂载点上 chmod 会失败，
    /// 而那时拒写整个凭据功能比"权限稍宽"更糟。
    /// </remarks>
    private static void TryRestrictPermissions(string path)
    {
        if (OperatingSystem.IsWindows()) return;

        try
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch (Exception)
        {
            // 见注释：尽力而为。
        }
    }

    /// <summary>
    /// 拒绝把密钥本身当成变量名传进来。
    /// </summary>
    /// <remarks>
    /// 防的是"用户/调用方把 <c>sk-xxx</c> 填进变量名位置"这类错误——
    /// 那会把值写成 <c>sk-xxx=sk-xxx</c>，两份都留在文件里。
    /// 与 <c>ProviderConfigFile</c> 对 <c>key_env</c> 的校验是同一类防护。
    /// </remarks>
    private static void RejectSecretLookingName(string name)
    {
        if (name.Contains('=', StringComparison.Ordinal)
            || name.StartsWith("sk-", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                $"'{name}' 看起来不是变量名。这里要的是变量名（如 DEEPSEEK_API_KEY），"
                + "值请放在第二个参数。", nameof(name));
        }
    }
}
