namespace Dougong.Cli.Terminal;

/// <summary>
/// 比例的文本格式化。**输出必须与平台无关。**
/// </summary>
/// <remarks>
/// <b>为什么不直接用 <c>:P0</c></b>：它依赖当前 culture。Linux 的默认 culture
/// 会在数字与百分号之间插入空格（<c>75 %</c>），Windows 不会（<c>75%</c>）。
/// 同一个 CLI 在两个平台上输出不同格式是缺陷——而它只在 CI 上暴露，
/// 本地怎么都复现不出来。2026-10-31 就因为这个让 doctor 套件在 ubuntu 上红了，
/// 而本地一路绿。
/// <para>
/// 这里自己乘 100 并四舍五入，用 <see cref="InvariantCulture"/> 输出，结果确定。
/// </para>
/// </remarks>
public static class PercentText
{
    /// <summary>把 0..1 的比例格式化成 <c>75%</c>（无空格）。</summary>
    public static string Format(double ratio) =>
        Math.Round(ratio * 100, MidpointRounding.AwayFromZero)
            .ToString(System.Globalization.CultureInfo.InvariantCulture) + "%";
}
