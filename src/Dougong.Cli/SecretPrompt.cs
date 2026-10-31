namespace Dougong.Cli;

/// <summary>
/// 掩码输入密钥。
/// </summary>
/// <remarks>
/// <b>为什么需要它</b>：让用户 <c>export XXX_API_KEY=...</c> 的门槛太高——
/// 得知道在哪设、每次开终端重设、Windows 上还要区分 PowerShell 与 Git Bash。
/// 四家参考项目全都提供交互输入，只是位置不同：
/// <list type="bullet">
/// <item>hermes：<c>hermes setup</c> 向导，<c>masked_secret_prompt</c> 逐字符掩码</item>
/// <item>dsh：GUI 设置面板的 <c>type="password"</c> 输入框</item>
/// <item>pi / pi.net：会话内 <c>/login</c></item>
/// </list>
///
/// <para>
/// <b>pi 的做法有个真实缺陷可以参考</b>：它的登录对话框把密钥交给普通输入框，
/// 而那个输入框没有掩码模式——密钥明文显示在屏幕上，而且留在滚动历史里。
/// 这里必须掩码，不能省这一步。
/// </para>
///
/// <para>
/// <b>非交互环境退回可见输入</b>：管道、重定向、CI 里没有真实的键盘事件，
/// 掩码无从谈起。那时读一行即可——把密钥写进管道本来就不是"防窥"场景。
/// </para>
/// </remarks>
public static class SecretPrompt
{
    /// <summary>
    /// 提示用户输入密钥，输入过程显示掩码。返回 null 表示用户放弃（Ctrl+C 或空输入）。
    /// </summary>
    /// <param name="prompt">提示文本，如 <c>DEEPSEEK_API_KEY</c>。</param>
    public static string? Ask(string prompt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);

        // 重定向时无法逐键读取：Console.ReadKey 会抛 InvalidOperationException。
        if (Console.IsInputRedirected || Console.IsOutputRedirected)
        {
            Console.Error.Write($"{prompt}: ");
            var redirected = Console.ReadLine();
            return string.IsNullOrWhiteSpace(redirected) ? null : redirected.Trim();
        }

        Console.Error.Write($"{prompt}（输入不回显，回车确认）: ");

        var builder = new System.Text.StringBuilder();
        try
        {
            while (true)
            {
                var key = Console.ReadKey(intercept: true);

                if (key.Key == ConsoleKey.Enter) break;

                // Ctrl+C 在 intercept 模式下不会自动终止进程，要自己处理。
                if (key.Key == ConsoleKey.C
                    && (key.Modifiers & ConsoleModifiers.Control) != 0)
                {
                    Console.Error.WriteLine();
                    return null;
                }

                if (key.Key == ConsoleKey.Backspace)
                {
                    if (builder.Length == 0) continue;
                    builder.Length--;
                    // 擦掉屏幕上的一个掩码字符：退格 + 空格 + 退格。
                    Console.Error.Write("\b \b");
                    continue;
                }

                // 跳过功能键与控制字符（方向键、F1-F12 等会带转义序列）。
                if (char.IsControl(key.KeyChar)) continue;

                builder.Append(key.KeyChar);
                Console.Error.Write('*');
            }
        }
        catch (Exception)
        {
            // 终端不支持逐键读取（某些伪终端、IDE 内置终端）。
            // 已经提示过用户，退回可见输入总比让命令失败好。
            Console.Error.WriteLine();
            Console.Error.Write($"{prompt}: ");
            var fallback = Console.ReadLine();
            return string.IsNullOrWhiteSpace(fallback) ? null : fallback.Trim();
        }
        finally
        {
            Console.Error.WriteLine();
        }

        return builder.Length == 0 ? null : builder.ToString();
    }

    /// <summary>
    /// 把密钥显示成可安全粘贴的形式：前 4 后 4，中间固定长度。
    /// </summary>
    /// <remarks>
    /// <b>不按真实长度留点</b>：那样会泄漏长度信息，而长度对某些提供商是可用的指纹。
    /// 固定 8 个星号就够了——用户只需要确认"是这一把"。
    /// </remarks>
    public static string Mask(string? secret)
    {
        if (string.IsNullOrEmpty(secret)) return "(未设置)";

        // 太短的不露任何前缀：露 4 位等于露一半。
        if (secret.Length < 12) return new string('*', 8);

        return secret[..4] + new string('*', 8) + secret[^4..];
    }
}
