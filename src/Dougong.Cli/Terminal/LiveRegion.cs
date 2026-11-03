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

    /// <summary>移到指定行、第 1 列（CUP）。<c>ESC[{row};1H</c>。</summary>
    /// <remarks>
    /// <b>底部锚定用的定位原语</b>。行号是绝对行（1-based），
    /// 与 <see cref="NextLine"/> 的相对下移互为替代：
    /// 前者让区域固定贴底，后者让区域跟着光标走。
    /// </remarks>
    private static string CursorTo(int row) => $"\u001b[{row};1H";

    private readonly bool _enabled;
    private readonly int _width;

    /// <summary>
    /// 锚定到的屏幕行（1-based）；0 表示**不锚定**，仍画在光标下方。
    /// </summary>
    /// <remarks>
    /// <b>两个模式必须分开实现，不能"尽量锚定"</b>：
    /// 锚定要读终端高度、且与滚动的相互作用很微妙（正文写满最后一行时
    /// 终端整体上滚，锚点跟着移）。而"画在光标下方"是这个类一直在用的方案，
    /// 已经被 46 个套件和真机验过。
    /// <para>
    /// 所以只在 <see cref="TerminalCapability.CanAnchorBottom"/> 时用锚定，
    /// 其余情况维持原路径——两条路径的切换点是纯数据，可以确定性覆盖。
    /// </para>
    /// </remarks>
    private readonly int _anchorBottom;

    /// <summary>已画区域占的屏幕行范围（1-based，锚定模式下有效）。</summary>
    private int _paintedFirstRow;

    /// <summary>提示符行是否由本类接管（1 = 是）。</summary>
    /// <remarks>
    /// <b>接管后 Erase 不能碰最后一行</b>：那是提示符的位置，
    /// 擦掉就等于把用户正在看的光标行清了。
    /// </remarks>
    private int _promptRows;
    private readonly object _sync = new();
    private readonly List<string> _lines = [];

    private int _paintedRows;
    private bool _disposed;

    private LiveRegion(bool enabled, int width, int anchorBottom)
    {
        _enabled = enabled;
        _width = width;
        _anchorBottom = anchorBottom;
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

        // 底部锚定只在 Rich 且高度可读时启用。
        // 降级路径（Narrow / 高度未知）保持"画在光标下方"——那条路已被验过。
        var anchor = enabled && capability.CanAnchorBottom ? capability.Height : 0;

        return new LiveRegion(enabled, capability.Width, anchor);
    }

    /// <summary>是否采用底部锚定（供诊断与检查断言）。</summary>
    public bool IsAnchored => _enabled && !_disposed && _anchorBottom > 0;

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

    /// <summary>
    /// 进入"等待输入"状态：把状态行锚到底部，提示符留在它下面一行。
    /// </summary>
    /// <remarks>
    /// <b>为什么提示符要由这里管</b>：Hermes 的状态行在提示符**上方**，
    /// 而我们的提示符原本是正文的一部分——那样状态行只能画在它下面，
    /// 方向正好相反（见方案 §2.3）。
    ///
    /// <para>
    /// 锚定模式下：状态行定位到 <c>height-1</c> 行，提示符落在 <c>height</c> 行。
    /// 非锚定模式下本方法是**空操作**，提示符照旧由调用方用
    /// <c>Output.Text</c> 写——那条路已被 46 个套件与真机验过，不动它。
    /// </para>
    ///
    /// <para>
    /// <b>Plain 档完全无副作用</b>：它连 <c>_enabled</c> 都是 false，
    /// 所以什么都不做，输出与改造前逐字一致。
    /// </para>
    /// </remarks>
    public bool BeginPrompt(string prompt)
    {
        ArgumentNullException.ThrowIfNull(prompt);

        // **返回 false 表示"没接管"，调用方照旧用 Output.Text 写提示符。**
        //
        // 第一版这里只挪光标、然后让调用方 Output.Text 写提示符——
        // 结果是提示符被 RunExclusive 里的 Erase 擦掉了（真机看到 `>` 消失）。
        // 根因是职责没划清：既然提示符归底部区域，就必须**由区域自己画**，
        // 而不是"区域挪好位子、正文再写上去"。
        if (!_enabled || _disposed || _anchorBottom <= 0) return false;

        lock (_sync)
        {
            try
            {
                // **不 SaveCursor**：本方法的目的是把光标**留在**提示符末尾
                // 等用户输入。存了之后不恢复，会污染 DECSC 栈——后续
                // Repaint/Erase 里的 RestoreCursor 会跳到这个位置，
                // 表现为提示符在下一轮输入前消失（真机实测）。
                var builder = new System.Text.StringBuilder();

                // 状态行占 height-1（可能多行，往上排），提示符占 height。
                var statusRows = Math.Min(_lines.Count, Math.Max(0, _anchorBottom - 1));
                var firstRow = Math.Max(1, (_anchorBottom - 1) - statusRows + 1);

                for (var i = 0; i < statusRows; i++)
                {
                    builder.Append(CursorTo(firstRow + i));
                    builder.Append(ClearLine).Append(Truncate(_lines[i], _width));
                }

                // 提示符行：清干净再写，光标停在提示符之后等输入。
                builder.Append(CursorTo(_anchorBottom));
                builder.Append(ClearLine).Append(Truncate(prompt, _width));

                Output.Control(builder.ToString());

                _promptRows = 1;
                _paintedFirstRow = statusRows > 0 ? firstRow : 0;
                _paintedRows = statusRows;
                return true;
            }
            catch (Exception)
            {
                _disposed = true;
                return false;
            }
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
            ErasePrompt();
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
            ErasePrompt();
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

            if (_anchorBottom > 0)
            {
                // **锚定路径**：从底部往上排，但要给提示符留最后一行。
                //
                // 第一版让状态行贴着 `height` 行，于是它与提示符抢同一行
                // （检查 `prompt-goes-below-status-line-when-anchored` 抓到）。
                // 正确布局（照 Hermes）：状态行占 `height-1`，提示符占 `height`。
                //
                // 也就是说可用高度是 `height - 1`（留一行给提示符）。
                var usable = Math.Max(1, _anchorBottom - 1);
                var firstRow = Math.Max(1, usable - _lines.Count + 1);
                builder.Append(SaveCursor);

                for (var i = 0; i < _lines.Count; i++)
                {
                    builder.Append(CursorTo(firstRow + i));
                    builder.Append(ClearLine).Append(Truncate(_lines[i], _width));
                }

                builder.Append(RestoreCursor);
                _paintedFirstRow = firstRow;
            }
            else
            {
                // **相对路径**（原方案）：从光标往下写 N 行再回来。
                builder.Append(SaveCursor);

                for (var i = 0; i < _lines.Count; i++)
                {
                    builder.Append(NextLine);
                    builder.Append(ClearLine).Append(Truncate(_lines[i], _width));
                }

                builder.Append(RestoreCursor);
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

            if (_anchorBottom > 0 && _paintedFirstRow > 0)
            {
                // 锚定模式：按记录下的绝对行号逐行清，不依赖光标在哪。
                //
                // **只清状态行，不动提示符行**：`_paintedRows` 只统计状态行，
                // 提示符那一行由 `_promptRows` 单独记账且永不在这里清除。
                // 第一版没分开，真机上表现为提示符 `>` 消失。
                for (var i = 0; i < _paintedRows; i++)
                {
                    builder.Append(CursorTo(_paintedFirstRow + i));
                    builder.Append(ClearLine);
                }
            }
            else
            {
                // 相对模式：区域在光标下方，往下数。
                for (var i = 0; i < _paintedRows; i++)
                {
                    builder.Append(NextLine);
                    builder.Append(ClearLine);
                }
            }

            builder.Append(RestoreCursor);
            Output.Control(builder.ToString());
        }
        catch (Exception)
        {
            _disposed = true;
        }

        _paintedRows = 0;
        _paintedFirstRow = 0;
        // 不重置 _promptRows：提示符那一行仍然由本类占着。
        // 它随 Clear()/Dispose() 一起清。
    }

    /// <summary>清掉由本类接管的提示符行。</summary>
    /// <remarks>
    /// <b>只在真正"不要了"的时候调</b>（<see cref="Clear"/>、<see cref="Dispose"/>）。
    /// 普通的 <see cref="Erase"/> 不能碰它——那是用户正在看的光标行。
    /// 第一版没区分，真机上表现为提示符 `>` 在下一轮输入前就消失了。
    /// </remarks>
    private void ErasePrompt()
    {
        if (!_enabled || _promptRows == 0 || _anchorBottom <= 0) return;

        try
        {
            var builder = new System.Text.StringBuilder();
            builder.Append(CursorTo(_anchorBottom));
            builder.Append(ClearLine);
            Output.Control(builder.ToString());
        }
        catch (Exception)
        {
            _disposed = true;
        }

        _promptRows = 0;
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
    /// <para>
    /// <b>按 Rune 迭代</b>：按 <c>char</c> 迭代时，宽度刚好卡在代理对中间
    /// 会 Append 一个孤立高代理，输出里出现 <c>�</c>。宽度判定统一走
    /// <see cref="DisplayWidth"/>，不在这里另抄一份区间表。
    /// </para>
    /// </remarks>
    private static string Truncate(string text, int width)
    {
        if (width <= 1) return text;

        var limit = width - 1;
        var used = 0;
        var builder = new System.Text.StringBuilder(text.Length);

        foreach (var rune in text.EnumerateRunes())
        {
            var w = DisplayWidth.Of(rune);
            if (used + w > limit) break;
            builder.Append(rune.ToString());
            used += w;
        }

        return builder.ToString();
    }
}
