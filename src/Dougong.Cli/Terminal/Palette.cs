namespace Dougong.Cli.Terminal;

/// <summary>
/// 语义色。**颜色在这里是信息编码，不是装饰**——用户扫一眼就知道哪行是错误。
/// </summary>
/// <remarks>
/// <b>为什么 accent 用暗黄而不是品牌正红</b>：
/// 终端惯例里红色 = 错误。品牌红如果同时做 accent，用户看到红字会先以为出错。
/// 所以错开：accent 走黄系，红色只留给 error。
/// <para>
/// <b>黄与橙必须能区分</b>：accent（黄）与 warn（橙）在色环上相邻，
/// 单靠色相不够——色弱用户区分能力弱。它们还靠**图标**（无 / ⚠ / ✗）
/// 与**出现位置**（静态 / 动态）区分，颜色只是第三通道。
/// </para>
/// <para>
/// <b>Plain 档下所有颜色被忽略</b>。这里只定义"是什么色"，
/// "要不要上色"由 <see cref="Output"/> 按终端档次决定。
/// </para>
/// </remarks>
public static class Palette
{
    /// <summary>主色：版本号、模型名、框线。低饱和的暗黄，不与 warn 抢注意力。</summary>
    public const string Accent = "#C9A227";

    /// <summary>次要：路径、耗时、分隔线。</summary>
    public const string Muted = "#8A8A8A";

    /// <summary>成功：✓ 工具完成、检查通过。</summary>
    public const string Ok = "#4CAF50";

    /// <summary>警告：⚠ 降级、跳过。比 accent 更亮更橙，可区分。</summary>
    public const string Warn = "#FF8C00";

    /// <summary>失败：✗ 工具失败、检查未通过。</summary>
    public const string Error = "#EF5350";

    /// <summary>提示符与用户输入的回显。</summary>
    public const string Prompt = "#C9A227";
}
