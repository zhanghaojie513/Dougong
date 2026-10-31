using System.Text;

namespace Dougong.Cli.Terminal;

/// <summary>
/// 终端显示宽度的唯一实现。
/// </summary>
/// <remarks>
/// <b>为什么必须只有一份</b>：此前有三份手抄的区间表——
/// <c>StatusLine.IsWide</c>、<c>LiveRegion.WidthOf</c>、
/// 以及 <c>TerminalChecks</c> 里的 <c>IsWideChar</c>。
/// 前两者算的是被测行为，后者算的是断言口径；三者只要有一处不同，
/// "补白有没有补满整行"这类检查就会永远对不上，然后被改成迁就实现了事。
/// <para>
/// <b>测试不再调用这里</b>，理由是循环验证：用被测实现验证被测实现，
/// 任何错误都会被两边同时犯，"一致"就永远成立。检查保留独立实现，
/// 但用**硬编码的期望宽度**钉住正确值（见 <c>width-golden-values</c>）——
/// 那才是外部基准，两边一起错时它会失败。
/// </para>
/// </remarks>
public static class DisplayWidth
{
    /// <summary>
    /// 一个 <see cref="Rune"/> 在等宽终端里占几格。
    /// </summary>
    /// <remarks>
    /// <b>必须按 Rune 而不是按 char 判定</b>：<c>😀</c> 在 UTF-16 里是两个
    /// <c>char</c>（代理对）。逐 <c>char</c> 判定时它们落在任何宽字符区间之外，
    /// 各算 1 格，合计 2 格——对单 emoji 碰巧等于真实宽度，
    /// 于是这个错误**看不出是错误**。
    /// <para>
    /// 真正暴露它的是组合序列：<c>👨‍👩‍👧‍👦</c> 是 11 个 <c>char</c>，
    /// 按旧算法算 11 格，而终端显示 2 格。用它拼状态行会让补白差 9 格，
    /// 表现为状态行尾部空出一截，或者被错误截断。
    /// </para>
    /// <para>
    /// <b>本实现按码点近似，不处理字素簇</b>：ZWJ 连字、变体选择符（<c>❤️</c>）、
    /// 国旗对（区域指示符）都按多码点累加，比真实显示宽。
    /// 这是**有意保留的偏差**：完整字形簇分割需要 ICU 或自带表，
    /// 而那与"状态行别错位"的收益不成比例。宁可偏宽（少显示几个字），
    /// 不可偏窄——偏窄会写满最右列触发折行，那才是真正的布局崩坏。
    /// </para>
    /// </remarks>
    public static int Of(Rune rune)
    {
        var value = rune.Value;

        // 控制字符不占位（除零宽连接符等特殊格式符，它们也不占位）。
        if (Rune.IsControl(rune)) return 0;

        // 零宽字符：组合附加符、ZWJ、变体选择符、变音标记。
        // 按 0 计而不是 1，否则 "é" 这类组合写法会被算成 2 格。
        if (value is >= 0x0300 and <= 0x036F) return 0;   // 组合变音符号
        if (value is >= 0x200B and <= 0x200F) return 0;   // 零宽空格与方向标记
        if (value is >= 0xFE00 and <= 0xFE0F) return 0;   // 变体选择符
        if (value == 0x200D) return 0;                    // ZWJ
        if (value is >= 0xE0100 and <= 0xE01EF) return 0; // 变体选择符补充

        return IsWide(value) ? 2 : 1;
    }

    /// <summary>该码点是否落在东亚宽字符区。</summary>
    /// <remarks>
    /// 区间按 Unicode East Asian Width 的 Wide / Fullwidth 两类取并。
    /// 含 <b>U+1F300–U+1F9FF</b>（emoji）与 <b>U+2600–U+27BF</b>（杂项符号）：
    /// 后者在多数终端里按 2 格渲染，不列进去会让 <c>✓</c>、<c>→</c> 之类
    /// 在部分终端上错位一格。
    /// </remarks>
    private static bool IsWide(int value) => value switch
    {
        >= 0x1100 and <= 0x115F => true,   // 谚文字母
        >= 0x2E80 and <= 0x303E => true,   // CJK 部首与标点
        >= 0x3041 and <= 0x33FF => true,   // 假名、CJK 兼容
        >= 0x3400 and <= 0x4DBF => true,   // CJK 扩展 A
        >= 0x4E00 and <= 0x9FFF => true,   // CJK 统一表意
        >= 0xA000 and <= 0xA4CF => true,   // 彝文
        >= 0xAC00 and <= 0xD7A3 => true,   // 谚文音节
        >= 0xF900 and <= 0xFAFF => true,   // CJK 兼容表意
        >= 0xFE30 and <= 0xFE6F => true,   // CJK 竖排标点
        >= 0xFF00 and <= 0xFF60 => true,   // 全角 ASCII
        >= 0xFFE0 and <= 0xFFE6 => true,   // 全角符号
        >= 0x1F300 and <= 0x1F9FF => true, // emoji 主体
        >= 0x1F000 and <= 0x1F2FF => true, // 麻将、扑克、围合字
        >= 0x2600 and <= 0x27BF => true,   // 杂项符号与装饰符
        _ => false,
    };

    /// <summary>一段文本的显示宽度，跳过 ANSI 转义序列。</summary>
    public static int Of(string text)
    {
        var width = 0;
        var inEscape = false;

        // 按 Rune 枚举：代理对作为一个码点处理，emoji 才不会各算一格。
        foreach (var rune in text.EnumerateRunes())
        {
            if (inEscape)
            {
                if (rune.Value == 'm') inEscape = false;
                continue;
            }

            if (rune.Value == '\u001b') { inEscape = true; continue; }

            width += Of(rune);
        }

        return width;
    }
}
