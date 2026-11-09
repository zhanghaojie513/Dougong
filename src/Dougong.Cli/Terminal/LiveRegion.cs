namespace Dougong.Cli.Terminal;

/// <summary>
/// 屏幕底部的固定三行块：**活动行 / 输入行 / 状态栏**。**只在富终端下启用**。
/// </summary>
/// <remarks>
/// <b>版式（自下而上，锚定到屏幕最后一行）</b>：
/// <code>
///   1 .. height-3   正文滚动区
///   height-2        活动行（仅忙时有内容，空闲留空）
///   height-1        输入行（`&gt; ` + 用户输入）
///   height          状态栏（模型 · 上下文 · 耗时）
/// </code>
///
/// <para>
/// <b>为什么必须引入滚动区（DECSTBM）</b>：用户 2026-11-05 报"活动行跑到了
/// 输入行下面"。根因不是排版算错，而是底部块**待在正文滚动区里**——
/// 正文一长，整块被推走；想让块留在原地就只能每次按绝对行号重画，
/// 而正文又恰恰从那几行写起，于是"谁后画谁赢"，顺序完全由时序决定。
/// 把正文限制在 <c>1..height-3</c> 内滚动之后，底部三行**物理上不可能被正文碰到**，
/// 版式变成结构保证而不是时序巧合。
/// </para>
///
/// <para>
/// <b>块高为什么恒为 3 行</b>：忙时 3 行、闲时 2 行会让滚动区高度随忙碌状态变化，
/// 而改 DECSTBM 会把光标打回屏幕原点（DECSTBM 的副作用），每轮都要重新定位。
/// 空闲时把活动行留空、块高不变，换来的是"滚动区在整个会话内只设置一次"。
/// </para>
///
/// <para>
/// <b>光标位置只用一个 DECSC 槽</b>：块画在绝对行上，正文光标则被
/// <c>ESC 7</c>/<c>ESC 8</c> 保存与还原。约定：<b>静止时光标恒在正文区</b>；
/// 只有 `BeginPrompt` 到 `CommitPrompt` 之间它才停在输入行，此时 DECSC 槽里
/// 存的就是正文位置，提交时用 `ESC 8` 回去即可——不需要读取光标坐标
/// （读坐标要走 DSR，在管道里拿不到回应）。
/// </para>
///
/// <para>
/// <b>重定向时一律不启用</b>，即使调用方传了 <c>force: true</c>：那里写 ANSI
/// 既没有意义，也会污染 46 个套件的断言。
/// </para>
/// </remarks>
public sealed class LiveRegion : IDisposable
{
    /// <summary>保存光标位置（DECSC）。</summary>
    /// <remarks>
    /// <b>必须是 <c>ESC 7</c>，不能用 <c>ESC[s</c></b>。<c>ESC[s</c> 是 SCO 的
    /// 私有序列，pyte 等实现只"认识"却不保存位置，真实终端里也有变异。
    /// 用 pyte 实测过：<c>ESC[s</c> … <c>ESC[u</c> 之后光标停在错误位置，
    /// <c>ESC 7</c> … <c>ESC 8</c> 才正确回到原处（2026-10-29）。
    /// </remarks>
    private const string SaveCursor = "\u001b7";

    /// <summary>恢复光标位置（DECRC）。</summary>
    private const string RestoreCursor = "\u001b8";

    /// <summary>清整行。</summary>
    private const string ClearLine = "\u001b[2K";

    /// <summary>把滚动区恢复成整屏（DECSTBM 无参 = 全屏）。</summary>
    private const string ResetScrollRegion = "\u001b[r";

    /// <summary>移到指定行、第 1 列（CUP）。<c>ESC[{row};1H</c>。</summary>
    private static string CursorTo(int row) => $"\u001b[{row};1H";

    /// <summary>设置正文滚动区：正文只在该区间内滚动。</summary>
    /// <remarks>
    /// <b>参数顺序是"上界;下界"，1-based。</b>上界恒为 1——xterm 只在上界为 1 时
    /// 才把滚出区的行写进 scrollback，上界取别值会让历史凭空消失。
    /// </remarks>
    private static string ScrollRegion(int bottom) => $"\u001b[1;{bottom}r";

    /// <summary>底部块占的行数：活动 + 输入 + 状态。</summary>
    private const int BlockRows = 3;

    /// <summary>启用锚定所需的最小终端高度（块 3 行 + 至少 2 行正文）。</summary>
    private const int MinimumAnchoredHeight = BlockRows + 2;

    private readonly bool _enabled;
    private readonly int _width;

    /// <summary>
    /// 锚定到的屏幕行（1-based）；0 表示**不锚定**，仍画在光标下方。
    /// </summary>
    /// <remarks>
    /// <b>两个模式必须分开实现，不能"尽量锚定"</b>：锚定要读终端高度、
    /// 且要改终端的滚动区。而"画在光标下方"是这个类一直在用的方案，
    /// 已经被 46 个套件和真机验过。只在 <see cref="TerminalCapability.CanAnchorBottom"/>
    /// 且高度够放下块时用锚定。
    /// </remarks>
    private readonly int _anchorBottom;

    private readonly object _sync = new();

    /// <summary>活动行内容；null 表示该行留空。</summary>
    private string? _activity;

    /// <summary>状态栏内容；null 表示该行留空。</summary>
    private string? _status;

    /// <summary>输入行内容（含提示符）。null 表示该行留空。</summary>
    private string? _promptLine;

    /// <summary>是否停在输入行等用户输入。</summary>
    /// <remarks>
    /// <b>这个状态下不许画块</b>：画块要把光标挪到绝对行，而输入行上
    /// 用户可能已经打了几个字、光标列只有终端自己知道。所以输入期间
    /// 只允许"提交"这一种动作，它先用 <c>ESC 8</c> 把光标交还正文。
    /// </remarks>
    private bool _atPrompt;

    /// <summary>是否正在重画（防止 Repaint 递归）。</summary>
    private bool _painting;

    /// <summary>滚动区是否已设置（退出时必须复原）。</summary>
    private bool _scrollRegionSet;

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

    /// <summary>是否采用底部锚定（供诊断与检查断言）。</summary>
    public bool IsAnchored => _enabled && !_disposed && _anchorBottom > 0;

    /// <summary>活动行所在屏幕行（1-based）；未锚定时为 0。</summary>
    public int ActivityRow => IsAnchored ? _anchorBottom - 2 : 0;

    /// <summary>输入行所在屏幕行（1-based）；未锚定时为 0。</summary>
    public int PromptRow => IsAnchored ? _anchorBottom - 1 : 0;

    /// <summary>状态栏所在屏幕行（1-based）；未锚定时为 0。</summary>
    public int StatusRow => IsAnchored ? _anchorBottom : 0;

    /// <summary>正文滚动区的下界（1-based）；未锚定时为 0。</summary>
    public int BodyBottom => IsAnchored ? _anchorBottom - BlockRows : 0;

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

        // 底部锚定只在 Rich、高度可读、且放得下三行块时启用。
        // 其余情况维持"画在光标下方"——那条路已被验过。
        var anchor = enabled
            && capability.CanAnchorBottom
            && capability.Height >= MinimumAnchoredHeight
                ? capability.Height
                : 0;

        var region = new LiveRegion(enabled, capability.Width, anchor);
        region.EnterScrollRegion();
        return region;
    }

    /// <summary>
    /// 设定正文滚动区，让底部三行不被正文滚动波及。
    /// </summary>
    /// <remarks>
    /// <b>必须 save/restore 光标</b>：DECSTBM 的副作用是把光标移到屏幕原点，
    /// 不还原的话下一行正文会从第 1 行开始写，直接盖掉启动面板。
    /// <para>
    /// 只在创建时设一次。终端尺寸变化后本类不知道（读高度要真终端），
    /// 这与会话中改窗口大小的场景一起列为已知限制。
    /// </para>
    /// </remarks>
    private void EnterScrollRegion()
    {
        if (!_enabled || _disposed || _anchorBottom <= 0) return;

        try
        {
            Output.Control(SaveCursor + ScrollRegion(_anchorBottom - BlockRows) + RestoreCursor);
            _scrollRegionSet = true;
        }
        catch (Exception)
        {
            // 终端拒绝控制序列：静默降级为不锚定，绝不半途而废地留在锚定模式。
            _disposed = true;
            _scrollRegionSet = false;
        }
    }

    /// <summary>设置活动行；null 或空串表示隐藏。</summary>
    /// <remarks>
    /// <b>活动行在输入行**上方**</b>（用户 2026-11-05 的版式要求）：
    /// 它属于"正在发生的事"，用户视线从正文落到输入行之前先看到它；
    /// 状态栏是常驻参考信息，贴最底。
    /// </remarks>
    public void SetActivity(string? activity)
    {
        var value = string.IsNullOrEmpty(activity) ? null : activity;

        lock (_sync)
        {
            if (string.Equals(_activity, value, StringComparison.Ordinal)) return;
            _activity = value;
            Repaint();
        }
    }

    /// <summary>设置状态栏；null 或空串表示隐藏。</summary>
    public void SetStatus(string? status)
    {
        var value = string.IsNullOrEmpty(status) ? null : status;

        lock (_sync)
        {
            if (string.Equals(_status, value, StringComparison.Ordinal)) return;
            _status = value;
            Repaint();
        }
    }

    /// <summary>
    /// 进入"等待输入"状态：把输入行画出来，光标停在它末尾。
    /// </summary>
    /// <remarks>
    /// <b>返回 false 表示"没接管"</b>，调用方照旧用 <see cref="Output.Text"/> 写提示符。
    /// 那种情况下提示符是正文的一部分，终端自己回声用户输入，不需要
    /// <see cref="CommitPrompt"/>。
    ///
    /// <para>
    /// <b>提示符必须由本类画</b>：既然输入行归底部块，它就属于块的坐标系；
    /// 让调用方在正文里写，等于把两种坐标系混在一起——区域一重画就把它擦了。
    /// </para>
    ///
    /// <para>
    /// <b>这里开的 DECSC 槽一直用到 <see cref="CommitPrompt"/>。</b>
    /// 它存的是正文位置，输入期间任何写正文的调用都要先 <c>ESC 8</c> 回去。
    /// </para>
    /// </remarks>
    public bool BeginPrompt(string prompt)
    {
        ArgumentNullException.ThrowIfNull(prompt);

        if (!_enabled || _disposed || _anchorBottom <= 0) return false;

        lock (_sync)
        {
            try
            {
                var builder = new System.Text.StringBuilder();

                // 保存正文位置 → 画块 → 停到输入行。
                // 注意这里**不**在末尾 ESC 8：光标要留在输入行等用户打字，
                // 而槽里留着正文位置供 CommitPrompt 回去。
                //
                // **只存一次**：用户连按回车（空输入）会再次进到这里，
                // 那时光标已经在输入行上——再存一次就把"正文位置"换成了
                // "输入行位置"，提交时空格正文会糊在底部块里。
                if (!_atPrompt) builder.Append(SaveCursor);

                builder.Append(RenderBlockRows());
                builder.Append(CursorTo(_anchorBottom - 1));
                builder.Append(ClearLine).Append(Truncate(prompt, _width));

                Output.Control(builder.ToString());

                _promptLine = prompt;
                _atPrompt = true;
                return true;
            }
            catch (Exception)
            {
                _disposed = true;
                return false;
            }
        }
    }

    /// <summary>
    /// 提交本轮输入：把用户输入的整行写进正文区，并把输入行清空。
    /// </summary>
    /// <remarks>
    /// <b>为什么由本类写这一行，而不是靠终端回声</b>：输入行不在正文区里，
    /// 终端的回声只会留在那一行上，回车换行后就被块覆盖。
    /// 要让它进入转录（用户看得见自己问过什么），必须在提交时
    /// 把它**重新写进正文区**——这也是 Hermes 的行为。
    ///
    /// <para>
    /// <b>没有接管输入行时（非锚定 / Plain）本方法什么都不做</b>：
    /// 那种模式下提示符在正文流里，终端回声已经把它写进去了，
    /// 再写一遍就会出现两行一模一样的问题。
    /// </para>
    /// </remarks>
    /// <param name="input">用户输入的原文（不含提示符）。</param>
    public void CommitPrompt(string input)
    {
        ArgumentNullException.ThrowIfNull(input);

        if (!_enabled || _disposed || _anchorBottom <= 0) return;

        lock (_sync)
        {
            try
            {
                // 输入行要**常驻显示提示符**：提交后立刻清成空白的话，
                // 整轮处理期间（可能几十秒）底部会少一行，看起来像界面塌了。
                var prompt = _promptLine ?? "> ";

                // 回到正文位置——那是上一轮输出结束的地方。从这里写提交行，
                // 它会紧接正文，并在写满滚动区时触发滚动；
                // 滚动只作用于 1..bodyBottom，底部三行纹丝不动。
                //
                // `_atPrompt` 为 false 说明正文已经先写过（正常路径不会发生，
                // 因为 ReadLine 期间没有别的输出），此时光标就在正文末尾，
                // 不能再 ESC 8——那个槽位已经过期了。
                var prefix = _atPrompt ? RestoreCursor : string.Empty;

                Output.Control(
                    prefix
                    + Truncate($"> {input}", _width)
                    + Environment.NewLine);

                _atPrompt = false;

                // 提示符保留、用户打的字已经进了正文，这里只把它擦掉。
                _promptLine = prompt;
                Repaint();
            }
            catch (Exception)
            {
                _disposed = true;
            }
        }
    }

    /// <summary>
    /// 在区域"让位"的状态下执行一次输出。**这是正文唯一的写入方式。**
    /// </summary>
    /// <remarks>
    /// <b>为什么还要进锁</b>：刷新循环在另一个线程上跑，正文写入与重画
    /// 必须是同一段临界区，否则块会画到"正文刚写一半"的中间位置。
    ///
    /// <para>
    /// <b>锚定模式下这里不擦除、也不重画</b>：正文被滚动区限制在
    /// <c>1..bodyBottom</c>，碰不到底部三行，擦画纯属多余——
    /// 而且每次擦画都会让屏幕闪一下。相对模式仍按原样擦画。
    /// </para>
    ///
    /// <para>
    /// <b>停在输入行时必须先回正文区</b>：此时光标在输入行上，
    /// 直接写会把正文糊在输入行（用户正在看的地方）。
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
            if (_anchorBottom > 0)
            {
                // 锚定：正文只受滚动区约束，块在区外，不需要擦画。
                if (_atPrompt)
                {
                    Output.Control(RestoreCursor);
                    _atPrompt = false;
                    _promptLine = null;
                }

                write();
                return;
            }

            // 相对模式（Narrow / 高度未知）：块画在光标下方，写之前让位。
            ErasePaintedRows();
            write();
        }
    }

    /// <summary>
    /// 擦掉底部区域并**丢弃内容**。轮次结束、退出前都必须调用。
    /// </summary>
    /// <remarks>
    /// <b>必须同时清空内容</b>，不能只擦屏幕：只擦屏幕的话，后续任何一次
    /// 写入都会把那份陈旧内容重新画上去——2026-10-29 真机上看到的就是这个：
    /// 一轮失败结束后汇总行都打完了，状态栏却还留在最下面。
    ///
    /// <para>
    /// <b>锚定模式下必须逐行清三行</b>：第一版这里只调
    /// <see cref="ErasePaintedRows"/>，而后者在锚定模式下直接返回
    /// （它只服务相对模式）。于是退出时底部三行原样留在屏幕上——
    /// 用户回到 shell 之后还能看到「重试中…」。
    /// 探针实测：Dispose 只发出了 <c>ESC[r</c>，一个字都没清。
    /// </para>
    /// </remarks>
    public void Clear()
    {
        lock (_sync)
        {
            _activity = null;
            _status = null;
            _promptLine = null;
            _atPrompt = false;

            if (_anchorBottom > 0)
                ClearBlockRows();
            else
                ErasePaintedRows();
        }
    }

    /// <summary>清掉锚定块占的三行（含输入行）。</summary>
    /// <remarks>
    /// <b>光标不还原</b>：调用方都是"不要了"的场景（Clear / Dispose），
    /// 保存的正文位置此后不再有效——还原反而会把光标送回一个即将被
    /// 滚动区复原打乱的位置。
    /// </remarks>
    private void ClearBlockRows()
    {
        if (!_enabled || _disposed) return;

        try
        {
            var builder = new System.Text.StringBuilder();
            for (var row = _anchorBottom - BlockRows + 1; row <= _anchorBottom; row++)
                builder.Append(CursorTo(row)).Append(ClearLine);

            Output.Control(builder.ToString());
        }
        catch (Exception)
        {
            // 终端已关闭：无处可写，忽略。
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;

            Clear();

            // **必须复原滚动区**：不然退出后 shell 的滚动也被限制在
            // 上半屏，输出会把提示符顶出屏幕——用户会以为是终端坏了。
            if (_scrollRegionSet)
            {
                try { Output.Control(ResetScrollRegion); }
                catch (Exception) { /* 终端已关闭：无处可写，忽略。 */ }

                _scrollRegionSet = false;
            }

            _disposed = true;
        }
    }

    /// <summary>把底部块重画一遍。</summary>
    /// <remarks>
    /// <b>输入期间一律不画</b>：画块要把光标挪到绝对行，而输入行上
    /// 用户可能已经打了字、光标列只有终端自己知道；块画完无法把光标
    /// 精确放回那个位置（DECSC 槽里存的是正文位置，不能借用）。
    /// 输入期间状态栏的秒数不刷新是可以接受的——那时用户在打字，
    /// 而且这段文字在提交时会一起重画。
    /// </remarks>
    private void Repaint()
    {
        if (!_enabled || _disposed || _painting) return;
        if (_anchorBottom > 0 && _atPrompt) return;

        _painting = true;
        try
        {
            if (_anchorBottom > 0)
            {
                // 锚定：块画在固定行上，正文光标由 DECSC 原样归还。
                Output.Control(SaveCursor + RenderBlockRows() + RestoreCursor);
                return;
            }

            // 相对模式：块画在光标下方（原方案），行数随内容变化。
            ErasePaintedRows();

            var builder = new System.Text.StringBuilder(SaveCursor);
            var count = 0;
            foreach (var line in RelativeLines())
            {
                builder.Append("\u001b[1E").Append(ClearLine).Append(Truncate(line, _width));
                count++;
            }

            if (count > 0)
            {
                builder.Append(RestoreCursor);
                Output.Control(builder.ToString());
            }

            _relativeRows = count;
        }
        catch (Exception)
        {
            // 终端拒绝控制序列：静默降级，此后不再尝试。
            _disposed = true;
            _relativeRows = 0;
        }
        finally
        {
            _painting = false;
        }
    }

    /// <summary>相对模式画过的行数。</summary>
    private int _relativeRows;

    /// <summary>
    /// 相对模式（Narrow / 高度未知）是否可画两行。
    /// </summary>
    /// <remarks>
    /// <b>相对模式下只有一行可用</b>：块画在光标下方，而光标可能就贴在屏幕
    /// 最后一行。第一版把活动行与状态栏两行都往"下方"画，于是在贴底时
    /// 第二行被终端裁掉——真机复现过一次：60 列 24 行终端上活动行整条消失，
    /// 只剩状态栏。那一档本来就窄，两行挤不下也是实情。
    ///
    /// <para>
    /// 所以相对模式退回**单行合并**：忙时显示活动行，闲时显示状态栏。
    /// 三行版式是富终端的能力，不是所有档都要有。
    /// </para>
    /// </remarks>
    private bool RelativeSingleRow => _anchorBottom <= 0;

    /// <summary>相对模式要画的那一行（隐藏的不出力）。</summary>
    private IEnumerable<string> RelativeLines()
    {
        if (RelativeSingleRow)
        {
            // 忙时活动行优先（"正在发生什么"比"环境"重要）；闲时退回状态栏。
            var line = !string.IsNullOrEmpty(_activity) ? _activity : _status;
            if (!string.IsNullOrEmpty(line)) yield return line;
            yield break;
        }

        if (_activity is { Length: > 0 } activity) yield return activity;
        if (_status is { Length: > 0 } status) yield return status;
    }

    /// <summary>生成底部三行的绘制序列（不含光标保存/还原）。</summary>
    /// <remarks>
    /// <b>三行都画，包括留空的</b>：只画有内容的那行，上一次的内容会留在
    /// 屏幕上——活动行从"重试中"变回隐藏时就会留个鬼影。
    /// 空行也要清一次，清行的代价远小于重影。
    /// </remarks>
    private string RenderBlockRows()
    {
        if (_anchorBottom <= 0) return string.Empty;

        var builder = new System.Text.StringBuilder();

        AppendRow(builder, _anchorBottom - 2, _activity);
        AppendRow(builder, _anchorBottom - 1, _promptLine);
        AppendRow(builder, _anchorBottom, _status);

        return builder.ToString();
    }

    private void AppendRow(System.Text.StringBuilder builder, int row, string? content)
    {
        builder.Append(CursorTo(row)).Append(ClearLine);
        if (!string.IsNullOrEmpty(content)) builder.Append(Truncate(content, _width));
    }

    /// <summary>擦掉相对模式下画过的行（锚定模式下块由重画覆盖，无需擦）。</summary>
    private void ErasePaintedRows()
    {
        if (!_enabled || _relativeRows == 0 || _anchorBottom > 0) return;

        try
        {
            var builder = new System.Text.StringBuilder(SaveCursor);
            for (var i = 0; i < _relativeRows; i++)
                builder.Append("\u001b[1E").Append(ClearLine);

            builder.Append(RestoreCursor);
            Output.Control(builder.ToString());
        }
        catch (Exception)
        {
            _disposed = true;
        }

        _relativeRows = 0;
    }

    /// <summary>按显示宽度截断，避免折行破坏区域布局。</summary>
    /// <remarks>
    /// <b>上限是 <c>width - 1</c>，不是 <c>width</c></b>：写满最右列会让终端
    /// 自动折行，光标跳到下一行，底部块的绝对行定位就全乱了。
    /// 真机上表现为活动行只剩 spinner、文字消失（2026-10-29 截图）。
    /// <para>
    /// <b>按 Rune 迭代</b>：按 <c>char</c> 迭代时，宽度刚好卡在代理对中间
    /// 会 Append 一个孤立高代理，输出里出现 <c>�</c>。
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
