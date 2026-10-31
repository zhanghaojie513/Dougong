using Dougong.Ai;

namespace Dougong.Agent.Comparison;

/// <summary>
/// 本机凭据文件的验收。
/// </summary>
/// <remarks>
/// <b>可观察面有三个</b>：
/// <list type="number">
/// <item><b>能存能读</b>，且覆盖写入不破坏其他条目。</item>
/// <item><b>密钥不落进配置文件</b>——这是项目的安全红线，
/// 配置文件里只有变量名。</item>
/// <item><b>优先级是"存储优先"</b>，与 pi / dsh / hermes 一致。
/// 搞反了会让"用户填好的密钥"被残留的环境变量悄悄盖掉。</item>
/// </list>
/// </remarks>
public static class CredentialChecks
{
    public static Task<int> RunAsync()
    {
        var failures = 0;
        failures += Check("credential-roundtrips", RoundTrips);
        failures += Check("credential-write-preserves-other-entries", PreservesOthers);
        failures += Check("credential-delete-removes-only-target", DeleteIsTargeted);
        failures += Check("credential-keeps-values-with-special-chars", SpecialChars);
        failures += Check("credential-rejects-secret-looking-name", RejectsSecretName);
        failures += Check("credential-file-is-not-the-config-file", SeparateFromConfig);
        failures += Check("credential-list-names-never-leaks-values", ListHasNoValues);

        Console.WriteLine(failures == 0
            ? "CREDENTIAL: ALL PASS"
            : $"CREDENTIAL: {failures} FAILURE(S)");
        return Task.FromResult(failures);
    }

    /// <summary>写进去能读出来。</summary>
    private static string? RoundTrips()
    {
        using var file = new TempCredentialFile();

        CredentialFile.Write("DEEPSEEK_API_KEY", "sk-abc123", file.FilePath);

        var read = CredentialFile.TryRead("DEEPSEEK_API_KEY", file.FilePath);
        return read == "sk-abc123" ? null : $"读回的值不对：'{read}'";
    }

    /// <summary>覆盖一个变量不能碰其他变量。</summary>
    /// <remarks>
    /// <b>这条防的是"重写整个文件"的实现错误</b>：如果写的时候没保留
    /// 其他行，用户配了多家 provider 时，改 A 的密钥会把 B 的冲掉。
    /// </remarks>
    private static string? PreservesOthers()
    {
        using var file = new TempCredentialFile();

        CredentialFile.Write("OPENAI_API_KEY", "sk-openai", file.FilePath);
        CredentialFile.Write("DEEPSEEK_API_KEY", "sk-deepseek", file.FilePath);
        CredentialFile.Write("OPENAI_API_KEY", "sk-openai-new", file.FilePath);

        var openai = CredentialFile.TryRead("OPENAI_API_KEY", file.FilePath);
        var deepseek = CredentialFile.TryRead("DEEPSEEK_API_KEY", file.FilePath);

        if (openai != "sk-openai-new") return $"覆盖没生效：{openai}";
        return deepseek == "sk-deepseek" ? null : $"另一个变量被冲掉了：{deepseek}";
    }

    /// <summary>删除只影响目标变量。</summary>
    private static string? DeleteIsTargeted()
    {
        using var file = new TempCredentialFile();

        CredentialFile.Write("A_KEY", "a", file.FilePath);
        CredentialFile.Write("B_KEY", "b", file.FilePath);

        if (!CredentialFile.TryDelete("A_KEY", file.FilePath)) return "删除返回了 false";
        if (CredentialFile.TryRead("A_KEY", file.FilePath) is not null) return "A_KEY 还在";

        return CredentialFile.TryRead("B_KEY", file.FilePath) == "b" ? null : "B_KEY 被误删";
    }

    /// <summary>
    /// 值里有 <c>=</c>、<c>#</c>、空格时原样保留。
    /// </summary>
    /// <remarks>
    /// <b>密钥可能含这些字符</b>：base64 编码的密钥常带 <c>=</c> 补齐，
    /// 某些服务用 <c>#</c>。做转义解析只会引入 bug，所以约定"取等号后全部内容"。
    /// </remarks>
    private static string? SpecialChars()
    {
        using var file = new TempCredentialFile();

        const string weird = "abc=def#ghi jkl";
        CredentialFile.Write("WEIRD_KEY", weird, file.FilePath);

        var read = CredentialFile.TryRead("WEIRD_KEY", file.FilePath);
        return read == weird ? null : $"特殊字符被破坏：'{read}'";
    }

    /// <summary>把密钥当变量名传进来要拒绝。</summary>
    /// <remarks>
    /// 用户/调用方可能把 <c>sk-xxx</c> 填进名字位置，那会写成
    /// <c>sk-xxx=sk-xxx</c>，两份都留在文件里。
    /// </remarks>
    private static string? RejectsSecretName()
    {
        using var file = new TempCredentialFile();

        try
        {
            CredentialFile.Write("sk-abc123", "sk-abc123", file.FilePath);
            return "把密钥当变量名传进去竟然成功了";
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    /// <summary>
    /// 凭据文件与配置文件是两个文件。
    /// </summary>
    /// <remarks>
    /// <b>这是安全红线的可执行形式</b>：<c>config.toml</c> 里只写
    /// <c>key</c>（条目名），值必须在另一个文件。若两者合一，
    /// 配置文件就不能安全地分享/提交了。
    /// </remarks>
    private static string? SeparateFromConfig()
    {
        var credentials = Path.GetFullPath(CredentialFile.DefaultPath);
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var config = Path.GetFullPath(Path.Combine(home, ".dougong", "config.toml"));

        if (string.Equals(credentials, config, StringComparison.OrdinalIgnoreCase))
            return "凭据文件与配置文件是同一个文件——密钥会混进可分享的配置";

        return credentials.EndsWith(CredentialFile.FileName, StringComparison.Ordinal)
            ? null
            : $"凭据文件路径异常：{credentials}";
    }

    /// <summary>列名字不能带出值。</summary>
    /// <remarks>
    /// 诊断输出会显示这个列表。它一旦带值，密钥就会出现在日志、
    /// issue 附件、截图里——而那正是 pi 的 <c>auth print-api-key</c>
    /// 那种"为了调试方便"接口带来的风险。
    /// </remarks>
    private static string? ListHasNoValues()
    {
        using var file = new TempCredentialFile();

        CredentialFile.Write("SECRET_ONE", "sk-super-secret-value", file.FilePath);

        var names = CredentialFile.ListNames(file.FilePath);
        foreach (var name in names)
        {
            if (name.Contains("super-secret", StringComparison.Ordinal))
                return $"名字列表里带出了值：{name}";
        }

        return names.Contains("SECRET_ONE", StringComparer.Ordinal)
            ? null
            : "名字列表里没有 SECRET_ONE";
    }

    // ── 支撑 ─────────────────────────────────────────────────────

    /// <summary>一次性的凭据文件，退出时删除。</summary>
    private sealed class TempCredentialFile : IDisposable
    {
        public TempCredentialFile()
        {
            var directory = Path.Combine(
                Path.GetTempPath(), "dougong-cred-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            FilePath = Path.Combine(directory, "credentials");
        }

        public string FilePath { get; }

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
