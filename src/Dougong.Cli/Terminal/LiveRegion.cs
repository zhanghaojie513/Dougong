namespace Dougong.Cli.Terminal;

/// <summary>
/// 屏幕底部的固定区域（活动行 + 状态栏）。**只在富终端下启用**。
/// </summary>
/// <remarks>
/// <b>为什么不写在事件循环里</b>：重绘要处理并发（计时器回调与事件循环同时跑）
/// 和光标状态。混在业务逻辑里最容易出竞态。这里的边界很硬：事件循环只调
/// <see cref="SetActivity"/> / <see cref="SetStatus"/> 更新内容，什么时候刷、
/// 怎么刷全部封装在本类内部。
/// <para>
/// <b>与正文输出的协作</b>：底部区域画在光标的"下方"，一旦有正文写出来，
/// 它就会跑到区域下面去，区域立刻错位。所以 <see cref="Output"/> 在写正文前
/// 调 <see cref="RunExclusive"/> 把"擦掉、写入、重画"做成一整段。
/// 这对调用方是透明的，且 Plain 档下它只是直接执行写入。
/// </para>
/// <para>
/// <b>重定向时一律不启用</b>，即使调用方传了 <c>force: true</c>：那里写 ANSI
/// 既没有意义，也会污染 45 个套件的断言。
/// </para>
/// <para>
/// <b>流式输出期间不重画</b>：每个 delta 都擦一次画一次会把终端刷爆，
/// 而且正文自己滚起来就已经是"活着"的证据。流式结束后由状态机切回思考中，
/// 区域自然恢复。
/// </para>
/// </remarks>
public sealed class LiveRegion : IDisposable
{
    private readonly bool _enabled;
    private readonly int _width;
    private readonly object _sync = new();
    private readonly List<string> _lines = [];

    private int _paintedRows;
    private bool _disposed;

    private LiveRegion(bool enabled, int width)
    {
        _enabled = enabled;
        _width = width;
    }

    /// <summary>是否真的在重绘（不可用时为 false）。</summary>
    public bool IsActive => _enabled && !_disposed;

    /// <summary>可用宽度。</summary>
    public int Width => _width;

    /// <summary>
    /// 按终端能力创建。**Plain 档返回不活动的实例**，调用方不必判空。
    /// </summary>
    /// <param name="capability">终端能力。</param>
    /// <param name="force">true 强制启用、false 强制禁用、null 按能力自动判断。</param>
    public static LiveRegion Create(TerminalCapability capability, bool? force = null)
    {
        var enabled = force ?? capability.SupportsDecoration;

        // 重定向时无条件关闭：这里写 ANSI 只会污染输出。
        if (capability.IsRedirected) enabled = false;

        return new LiveRegion(enabled, capability.Width);
    }

    /// <summary>设置要显示的行。空集合表示隐藏。</summary>
    public void SetLines(IReadOnlyList<string> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);

        lock (_sync)
        {
            _lines.Clear();
            _lines.AddRange(lines);
            Repaint();
        }
    }

    /// <summary>只显示活动行；null 表示隐藏。</summary>
    public void SetActivity(string? activity) =>
        SetLines(activity is { Length: > 0 } ? [activity] : []);

    /// <summary>
    /// 在区域"让位"的状态下执行一次输出。**这是唯一的正确写入方式。**
    /// </summary>
    /// <remarks>
    /// <b>为什么写动作必须进锁</b>：刷新循环在另一个线程上跑，完全可能在
    /// "已擦除、正文刚写一半"的间隙把区域画回来，插进正文中间。
    /// 擦、写、画做成一个原子段才真正排除这种撕裂。
    /// </remarks>
    public void RunExclusive(Action write)
    {
        ArgumentNullException.ThrowIfNull(write);

        if (!_enabled || _disposed)
        {
            write();
            return;
        }

        lock (_sync)
        {
            Erase();
            write();
            Repaint();
        }
    }

    /// <summary>擦掉底部区域。退出前必须调用。</summary>
    public void Clear()
    {
        lock (_sync) Erase();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            Erase();
            _disposed = true;
        }
    }

    /// <summary>把区域重画一遍。</summary>
    /// <remarks>
    /// <b>必须先换行再画</b>：正文写完后光标停在正文末尾（流式输出不带换行），
    /// 直接在原地画会把那一行当成自己的地盘，下一次重画就用 <c>ESC[2K</c>
    /// 把正文整行抹了。实测踩到过——VT 回放里"正文第二行"直接消失。
    /// 先 <c>\n</c> 换到下一行，区域就永远独占自己的行。
    /// <para>
    /// 每次全画，不做差量：差量要记住"终端里现在真的显示着什么"，
    /// 而这个前提会被滚动和外部写入打破。
    /// </para>
    /// </remarks>
    private void Repaint()
    {
        if (!_enabled || _disposed) return;

        // 行数或内容变了，先把旧区域擦干净——否则缩行时会留下鬼影。
        Erase();

        if (_lines.Count == 0) return;

        try
        {
            var builder = new System.Text.StringBuilder("\n");

            for (var i = 0; i < _lines.Count; i++)
            {
                if (i > 0) builder.Append('\n');
                builder.Append("\u001b[2K").Append(Truncate(_lines[i], _width));
            }

            Output.Control(builder.ToString());
            _paintedRows = _lines.Count;
        }
        catch (Exception)
        {
            // 终端拒绝控制序列：静默降级，此后不再尝试。
            _disposed = true;
            _paintedRows = 0;
        }
    }

    /// <summary>
    /// 擦掉已画的区域，把光标留在区域的上一行（也就是正文末尾那一行）。
    /// </summary>
    /// <remarks>
    /// 与 <see cref="Repaint"/> 的开头 <c>\n</c> 对应：擦完退回一行，
    /// 下次正文写入接着写，下次重画再换行。两者成对，才不会累积空行。
    /// </remarks>
    private void Erase()
    {
        if (!_enabled || _paintedRows == 0) return;

        try
        {
            var builder = new System.Text.StringBuilder();

            // 光标此刻在区域末行。逐行上移清空，最后再退到区域上一行。
            for (var i = 0; i < _paintedRows; i++)
            {
                builder.Append("\r\u001b[2K");
                builder.Append("\u001b[1A");
            }

            builder.Append('\r');

            Output.Control(builder.ToString());
        }
        catch (Exception)
        {
            _disposed = true;
        }

        _paintedRows = 0;
    }

    /// <summary>按显示宽度截断，避免折行破坏区域布局。</summary>
    private static string Truncate(string text, int width)
    {
        if (width <= 0) return text;

        var used = 0;
        var builder = new System.Text.StringBuilder(text.Length);

        foreach (var c in text)
        {
            var w = WidthOf(c);
            if (used + w > width) break;
            builder.Append(c);
            used += w;
        }

        return builder.ToString();
    }

    /// <summary>CJK 占两格——与 <see cref="StatusLine"/> 的判定保持一致。</summary>
    private static int WidthOf(char c) => c is
        >= '\u1100' and <= '\u115F' or
        >= '\u2E80' and <= '\u303E' or
        >= '\u3041' and <= '\u33FF' or
        >= '\u3400' and <= '\u4DBF' or
        >= '\u4E00' and <= '\u9FFF' or
        >= '\uA000' and <= '\uA4CF' or
        >= '\uAC00' and <= '\uD7A3' or
        >= '\uF900' and <= '\uFAFF' or
        >= '\uFE30' and <= '\uFE6F' or
        >= '\uFF00' and <= '\uFF60' or
        >= '\uFFE0' and <= '\uFFE6'
        ? 2 : 1;
}
