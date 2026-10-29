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
/// 既没有意义，也会污染 44 个套件的断言。
/// </para>
/// <para>
/// <b>流式输出期间不重画</b>：每个 delta 都擦一次画一次会把终端刷爆，
/// 而且正文自己滚起来就已经是"活着"的证据。流式结束后由状态机切回思考中，
/// 区域自然恢复。
/// </para>
/// </remarks>
public sealed class LiveRegion : IDisposable
{
    /// <summary>保存光标位置（DECSC）。</summary>
    /// <remarks>
    /// <b>必须是 <c>ESC 7</c>，不能用 <c>ESC[s</c></b>。<c>ESC[s</c> 是 SCO 的
    /// 私有序列，pyte 等实现只“认识”却不保存位置，真实终端里也有变异。
    /// 用 pyte 实测过：<c>ESC[s</c> … <c>ESC[u</c> 之后光标停在错误位置，
    /// <c>ESC 7</c> … <c>ESC 8</c> 才正确回到原处（2026-10-29）。
    /// 光标没回去 ⇒ 区域画错位置 ⇒ 屏幕上是两行字叠在一起，
    /// 正是真机截图里看到的症状。
    /// </remarks>
    private const string SaveCursor = "\u001b7";

    /// <summary>恢复光标位置（DECRC）。</summary>
    private const string RestoreCursor = "\u001b8";

    /// <summary>清整行。</summary>
    private const string ClearLine = "\u001b[2K";

    /// <summary>移到下一行行首（CNL）。用 <c>\n</c> 会继承当前列，必须用这个。</summary>
    private const string NextLine = "\u001b[1E";

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
    /// <remarks>
    /// <b>内容没变就一个字节都不写</b>：刷新循环每 100ms 调一次，
    /// 而 spinner 之外的文字（模型名、耗时秒数）往往几秒才变一次。
    /// 每次都全量擦画会让终端持续收到写指令——视觉上是抖动，
    /// 远程终端（SSH）上则是实打实的带宽。
    /// </remarks>
    public void SetLines(IReadOnlyList<string> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);

        lock (_sync)
        {
            if (SameLines(lines)) return;

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
    /// <para>
    /// <b>写完之后不再重画</b>：区域由刷新循环在下一个 tick 重新画到
    /// 新的光标位置。这里如果立刻重画，用的还是写入前的位置基准，
    /// 而正文已经把光标推走了——画出来的区域会落在旧位置，
    /// 表现为残影或与正文叠字（真机实测过）。
    /// 少画一次最多让活动行空白 100ms，刷错了却会一直留在屏幕上。
    /// </para>
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
            // 不在这里 Repaint：见上面的理由。刷新循环会接手。
        }
    }

    /// <summary>
    /// 擦掉底部区域并**丢弃内容**。轮次结束、退出前都必须调用。
    /// </summary>
    /// <remarks>
    /// <b>必须同时清空 <c>_lines</c>，不能只擦屏幕。</b>
    /// 只擦屏幕的话，后续任何一次写入都会走 <see cref="RunExclusive"/> 的
    /// "擦、写、画"流程，把那份陈旧内容重新画到正文下方——
    /// 2026-10-29 真机上看到的就是这个：一轮失败结束后汇总行都打完了，
    /// 状态栏却还留在最下面。
    /// <para>
    /// 它与 <see cref="Erase"/> 的分工：<c>Erase</c> 是"暂时让位，还要再画"，
    /// <c>Clear</c> 是"不要了"。混用会让状态在"已清除"与"待重绘"之间摇摆。
    /// </para>
    /// </remarks>
    public void Clear()
    {
        lock (_sync)
        {
            Erase();
            _lines.Clear();
        }
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
    /// <b>每次全画，先滚出足够的空间再写。</b>
    /// 区域用 <c>SaveCursor</c> 记下正文末尾的位置，往下写 N 行，再恢复。
    /// 这样正文与区域互不覆盖，光标也回到正文末尾供后续续写。
    /// <para>
    /// <b>这里不再假设"区域在光标下方"</b>：正文写入会把光标往下推
    /// （<see cref="Output.Line"/> 带换行），于是区域落在光标上方。
    /// 早先的 <c>Erase</c> 按"区域在下方"上移 N 行去清，擦的是错的屏幕位置，
    /// 结果旧位置残留、新位置又画一遍——真机上表现为两行文字叠在一起
    /// （2026-10-29 用户截图）。
    /// </para>
    /// </remarks>
    private void Repaint()
    {
        if (!_enabled || _disposed) return;

        // 先把上一次画的内容清掉，否则缩行/移位都会留残留。
        Erase();

        if (_lines.Count == 0) return;

        try
        {
            var builder = new System.Text.StringBuilder();
            builder.Append(SaveCursor);

            for (var i = 0; i < _lines.Count; i++)
            {
                builder.Append(NextLine);
                builder.Append(ClearLine).Append(Truncate(_lines[i], _width));
            }

            builder.Append(RestoreCursor);
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
    /// 擦掉已画的区域，把光标恢复到擦除前的位置。
    /// </summary>
    /// <remarks>
    /// 区域始终在光标行的下方，所以从当前位置往下 <see cref="_paintedRows"/> 行
    /// 就是要清的范围。<b>必须 save/restore 光标</b>：擦完停在行首会让后续
    /// 不带换行的正文从列 0 开始写，覆盖掉用户刚输入的那一行。
    /// </remarks>
    private void Erase()
    {
        if (!_enabled || _paintedRows == 0) return;

        try
        {
            var builder = new System.Text.StringBuilder();
            builder.Append(SaveCursor);

            for (var i = 0; i < _paintedRows; i++)
            {
                builder.Append(NextLine);
                builder.Append(ClearLine);
            }

            builder.Append(RestoreCursor);
            Output.Control(builder.ToString());
        }
        catch (Exception)
        {
            _disposed = true;
        }

        _paintedRows = 0;
    }

    /// <summary>当前内容与将要显示的是否完全相同。</summary>
    private bool SameLines(IReadOnlyList<string> lines)
    {
        if (_lines.Count != lines.Count) return false;

        for (var i = 0; i < lines.Count; i++)
            if (!string.Equals(_lines[i], lines[i], StringComparison.Ordinal)) return false;

        return true;
    }

    /// <summary>按显示宽度截断，避免折行破坏区域布局。</summary>
    /// <remarks>
    /// <b>上限是 <c>width - 1</c>，不是 <c>width</c></b>：写满最右列会让终端
    /// 自动折行，光标跳到下一行，后续 <c>ESC[1E</c> 就多走一行、整块区域错位。
    /// 真机上表现为活动行只剩 spinner、文字消失（2026-10-29 截图）。
    /// </remarks>
    private static string Truncate(string text, int width)
    {
        if (width <= 1) return text;

        var limit = width;
        var used = 0;
        var builder = new System.Text.StringBuilder(text.Length);

        foreach (var c in text)
        {
            var w = WidthOf(c);
            if (used + w > limit) break;
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
