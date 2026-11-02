using System.Text;
using System.Text.Json;

namespace Dougong.Agent;

/// <summary>
/// 检测同一调用被连续重复，并生成一条提醒。
/// </summary>
/// <remarks>
/// <b>解决什么问题</b>：模型卡住时最常见的样子是**把同一个工具调用重复几遍**——
/// 同一个文件读三次、同一条命令跑三遍。它自己看不出这是循环，
/// 而每多跑一次就多一轮延迟与一次计费。
///
/// <para>
/// <b>照 dsh 的 <c>repeat-tool-reminder</c>，关键行为一致</b>：
/// <list type="bullet">
/// <item><b>只提醒，不阻止</b>。dsh 的归档笔记专门写过这一点：
/// 它"向模型添加提醒，并不阻止工具调用，也不执行防护决策"。
/// 阻止会让"确实需要重试"的场景（等编译产物、轮询状态）变成硬失败</item>
/// <item><b>按"连续"计数</b>：中间换一个别的调用就清零。累计计数会把
/// "隔很久又跑了一次"也算成重复</item>
/// <item><b>阈值是多个</b>（默认 3/5/8）：只提醒一次会淹没在后面的轮次里；
/// 每次都提醒则会挤占预算。多次提醒是这两者的折中</item>
/// <item><b><c>exclude</c> 是"透明"的</b>：既不计数也不清零。这不是细节——
/// 把 <c>todo_write</c> 这类工具算作"换了调用"会让链频繁清零，
/// 于是真正的重复永远达不到阈值</item>
/// <item><b>判定用完整参数，展示用截断参数</b>：链的键必须是完整的规范化参数
/// （否则两个参数不同、只是开头相同的调用会被误判为重复），
/// 而提醒里引用的参数要截断（一个 <c>write</c> 的正文跟一轮请求进上下文是纯负担）</item>
/// </list>
/// </para>
///
/// <para>
/// <b>不做 include/exclude 的配置项</b>：我们只有一个默认策略。
/// 等真的出现"某个工具不该被算进去"的具体案例再加——空配置键比没有更难维护。
/// 因此这里把"透明工具"写成一个固定集合，理由逐条写在
/// <see cref="TransparentTools"/> 上。
/// </para>
/// </remarks>
public sealed class RepeatToolReminder
{
    /// <summary>
    /// 默认阈值：第 3、5、8 次连续重复各提醒一次。
    /// </summary>
    /// <remarks>
    /// <b>为什么不是"每次"</b>：连续重复 10 次时发 10 条提醒，会把真正有用的
    /// 上下文挤掉，而模型的注意力已经被"我在循环"这件事占满了。
    /// 三个递增的间隔让它在第 3、5、8 次各有一次机会注意到。
    /// </remarks>
    public static readonly IReadOnlyList<int> DefaultThresholds = [3, 5, 8];

    /// <summary>
    /// 提醒里引用参数的字符上限（默认 500，与 dsh 一致）。
    /// </summary>
    /// <remarks>
    /// <b>只影响展示，不影响检测</b>：链的键永远是完整参数。
    /// 反过来做（用截断后的参数当键）会把"两个长命令只有末尾不同"误判成重复。
    /// </remarks>
    public const int DefaultArgumentsPreviewChars = 500;

    /// <summary>
    /// 对链透明的工具：既不计数、也不清零。
    /// </summary>
    /// <remarks>
    /// <b>为什么是这几个</b>：它们是模型在循环里**顺手做的小动作**，
    /// 不是"换了任务"。<c>todo_write</c> 每轮更新进度、<c>working_directory</c>
    /// 只是看一眼目录——把它们算作"换了调用"会让链清零，
    /// 而真正卡住的模型恰好会一边重复读文件一边更新 todo。
    /// <para>
    /// <b>不把 <c>job_output</c> 放进来</b>：轮询作业输出是**有意的重复**，
    /// 但它重复的是真实调用，应该被看到——而我们只提醒不阻止，
    /// 所以提醒它"你在重复"是对的，不是误报。
    /// </para>
    /// </remarks>
    private static readonly HashSet<string> TransparentTools = new(StringComparer.Ordinal)
    {
        "todo_write",
        "working_directory",
        "skill",
    };

    private readonly int[] _thresholds;
    private readonly int _previewChars;

    // 链的当前尾巴：只有"与上一条完全相同"的调用才延续它。
    private string? _lastKey;
    private int _consecutive;

    /// <summary>构造。</summary>
    /// <param name="thresholds">
    /// 触发提醒的连续次数。<b>必须在加载期校验</b>（与 dsh 一致）：
    /// 空列表、非整数、小于 2、重复值都抛——静默回退会让配置看起来生效了。
    /// </param>
    /// <param name="argumentsPreviewChars">提醒里引用参数的上限。</param>
    public RepeatToolReminder(
        IReadOnlyList<int>? thresholds = null,
        int argumentsPreviewChars = DefaultArgumentsPreviewChars)
    {
        var values = thresholds ?? DefaultThresholds;
        ValidateThresholds(values);

        _thresholds = [.. values.Distinct().Order()];
        _previewChars = argumentsPreviewChars > 0
            ? argumentsPreviewChars
            : DefaultArgumentsPreviewChars;
    }

    /// <summary>当前连续重复次数，供诊断。</summary>
    public int Consecutive => _consecutive;

    /// <summary>
    /// 清零链。
    /// </summary>
    /// <remarks>
    /// <b>每轮开始时调用</b>：用户说"再跑一次"是一次新的开始，
    /// 不该接着上一轮的计数。不重置的话，上一轮末尾的两次相同调用
    /// 会让这一轮的第一次调用就接近阈值。
    /// </remarks>
    public void Reset()
    {
        _lastKey = null;
        _consecutive = 0;
    }

    /// <summary>
    /// 记录一次调用，返回该发出的提醒；不需要提醒时返回 null。
    /// </summary>
    /// <param name="toolId">工具 ID。</param>
    /// <param name="arguments">规范化后的参数（完整，未截断）。</param>
    public RepeatReminder? Observe(string toolId, JsonElement arguments)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(toolId);

        if (TransparentTools.Contains(toolId)) return null;

        var key = BuildKey(toolId, arguments);

        if (string.Equals(key, _lastKey, StringComparison.Ordinal))
        {
            _consecutive++;
        }
        else
        {
            _lastKey = key;
            _consecutive = 1;
        }

        return _thresholds.Contains(_consecutive)
            ? new RepeatReminder(
                toolId,
                _consecutive,
                BuildPreview(toolId, arguments),
                OnlyCounts: _consecutive == _thresholds[^1])
            : null;
    }

    /// <summary>
    /// 键：工具 + 完整规范化参数。
    /// </summary>
    /// <remarks>
    /// <b>用原文而不是解析后的结构</b>：两个语义相同、空格不同的 JSON 会被算成
    /// 两次不同的调用。这是刻意的保守——把"看起来一样"判成重复会让模型
    /// 收到一条错误的提醒，而漏报只是少一次提示。参数在进入这里之前
    /// 已经过 <c>ArgumentCanonicalizer</c> 的规范化，所以主要差异
    /// （键序、空白）已经被消掉了。
    /// </remarks>
    private static string BuildKey(string toolId, JsonElement arguments)
    {
        var raw = arguments.ValueKind is JsonValueKind.Undefined ? "{}" : arguments.GetRawText();
        return toolId + "\n" + raw;
    }

    private string BuildPreview(string toolId, JsonElement arguments)
    {
        var raw = arguments.ValueKind is JsonValueKind.Undefined ? "{}" : arguments.GetRawText();
        if (raw.Length <= _previewChars) return raw;

        // 截断要给省略号留位：上限 + 省略号 = 超出一个字符，
        // 而那个数字会被写进提醒文案。
        return _previewChars <= 1 ? "…" : raw[..(_previewChars - 1)] + "…";
    }

    /// <summary>阈值校验：配置错要在构造时炸，不能静默回退。</summary>
    private static void ValidateThresholds(IReadOnlyList<int> thresholds)
    {
        if (thresholds.Count == 0)
            throw new ArgumentException("阈值列表不能为空。", nameof(thresholds));

        foreach (var value in thresholds)
        {
            if (value < 2)
                throw new ArgumentException(
                    $"阈值必须 ≥ 2（连续 1 次不构成重复），实际 {value}。", nameof(thresholds));
        }

        if (thresholds.Distinct().Count() != thresholds.Count)
            throw new ArgumentException("阈值列表不能有重复值。", nameof(thresholds));
    }
}

/// <summary>一次重复调用的提醒。</summary>
/// <param name="ToolId">被重复的工具。</param>
/// <param name="Consecutive">当前连续第几次。</param>
/// <param name="ArgumentsPreview">参数预览（已按上限截断）。</param>
/// <param name="OnlyCounts">
/// 是否为最后一个阈值——到了这里，再提醒只会变成噪音。
/// </param>
public sealed record RepeatReminder(
    string ToolId,
    int Consecutive,
    string ArgumentsPreview,
    bool OnlyCounts)
{
    /// <summary>渲染成给模型的一段文本。</summary>
    /// <remarks>
    /// <b>必须说清"接下来该做什么"</b>：只说"你重复了 3 次"会让模型
    /// 换一种写法再试一遍——那不是它需要的信息。要它停下来判断：
    /// 是参数错了、还是环境没变、还是任务已经完成。
    /// <para>
    /// <b>不说"我已阻止"</b>：我们没有阻止，也不该让模型以为被限制了。
    /// dsh 同样只提醒。
    /// </para>
    /// </remarks>
    public string Render()
    {
        var sb = new StringBuilder();
        sb.Append("注意：你已经连续执行了 ").Append(Consecutive).Append(" 次相同的调用——")
            .Append(ToolId).Append(' ').Append(ArgumentsPreview).Append('。');

        sb.Append(OnlyCounts
            ? "这已经是最后一次提醒。"
            : "再重复不会产生新的结果。");

        sb.Append(
            "\n在继续之前，先确认一件事：是参数写错了、环境没有变化，还是这一步其实已经完成？"
            + "如果同一个调用已经给出同样的结果，换一种做法或直接报告阻塞。");

        return sb.ToString();
    }
}
