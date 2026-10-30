using System.Text.Json.Nodes;

namespace Dougong.Ai;

/// <summary>
/// 把统一档位翻译成各家请求体里的思考参数。
/// </summary>
/// <remarks>
/// <b>为什么翻译必须在协议层做</b>：推理强度不是"一个字段名"的问题，参数结构各家不同。
/// 桌面端手里只有 <c>IChatClient</c>，看不到 JSON 请求体；让它自己拼，
/// 就会出现"某家不认这个字段、静默忽略、用户以为生效了"。
/// <para>
/// <b>规则来源</b>：形态取自 pi（earendil-works/pi）的 <c>api/openai-completions.ts</c>
/// 与 <c>api/anthropic-messages.ts</c>，那些是它在生产里跑出来的写法。
/// 每条都在下面注明依据；<b>没有依据的一律不做</b>。
/// </para>
/// <para>
/// <b>本实现的验证状态</b>：结构对，字符串对，但**没有真实密钥逐家打通过**。
/// 所以默认值是 <see cref="WireCompat.ThinkingFormat.None"/>——不发。
/// 用户配了某个档位但协议是 None 时，明确告知不生效，而不是静默失败。
/// </para>
/// </remarks>
public static class ThinkingParameters
{
    /// <summary>
    /// 把思考参数写进请求体。没有配置或协议为 None 时什么都不做。
    /// </summary>
    /// <param name="body">请求体。</param>
    /// <param name="compat">端点方言配置。</param>
    /// <param name="effort">统一档位；null 表示"不启用思考"。</param>
    /// <param name="offLevel">"关闭思考"对应的档位词（通常 "off" 或 "none"）。</param>
    public static void Apply(
        JsonObject body, WireCompat compat, string? effort, string offLevel = "off")
    {
        ArgumentNullException.ThrowIfNull(body);
        ArgumentNullException.ThrowIfNull(compat);

        if (compat.Thinking == WireCompat.ThinkingFormat.None) return;

        // 映射：**先查表，查不到才透传**。表里显式写了 null 表示不支持该档位，
        // 此时不发——而不是退化成"发一个相近的"。静默降级会让用户以为生效了。
        var mapped = Resolve(compat, effort);

        switch (compat.Thinking)
        {
            case WireCompat.ThinkingFormat.ReasoningEffort:
                // OpenAI 官方：只发一个字符串字段。
                if (mapped is not null) body["reasoning_effort"] = mapped;
                break;

            case WireCompat.ThinkingFormat.DeepSeek:
                // DeepSeek / Kimi：thinking 开关 + 可选 reasoning_effort。
                // pi 的写法：有档位 → {type:"enabled"}；无档位且模型允许关闭 → {type:"disabled"}。
                body["thinking"] = mapped is not null
                    ? new JsonObject { ["type"] = "enabled" }
                    : new JsonObject { ["type"] = "disabled" };
                if (mapped is not null) body["reasoning_effort"] = mapped;
                break;

            case WireCompat.ThinkingFormat.Zai:
                // 智谱 GLM：带 clear_thinking 的 thinking 对象。
                // pi 的写法里 clear_thinking 固定 false——它要保留上一轮的思考内容，
                // 否则多轮对话里模型会"忘记自己刚想过什么"。
                body["thinking"] = mapped is not null
                    ? new JsonObject { ["type"] = "enabled", ["clear_thinking"] = false }
                    : new JsonObject { ["type"] = "disabled" };
                if (mapped is not null) body["reasoning_effort"] = mapped;
                break;

            case WireCompat.ThinkingFormat.Qwen:
                // 通义千问：布尔开关 enable_thinking，另可带 reasoning_effort。
                body["enable_thinking"] = mapped is not null;
                if (mapped is not null) body["reasoning_effort"] = mapped;
                break;

            case WireCompat.ThinkingFormat.OpenRouter:
                // OpenRouter：嵌套的 reasoning 对象，它负责向下游 provider 归一。
                body["reasoning"] = new JsonObject
                {
                    ["effort"] = mapped ?? Resolve(compat, offLevel) ?? "none",
                };
                break;

            default:
                throw new ArgumentOutOfRangeException(
                    nameof(compat), compat.Thinking, "未知的思考格式。");
        }
    }

    /// <summary>
    /// 解析一个档位到实际词。返回 null 表示**该档位不支持**（不是"用默认"）。
    /// </summary>
    /// <remarks>
    /// 查表与透传的区别很关键：表里显式给 null 是"这家不支持这个档位"，
    /// 而表里没有这个键是"没配映射，按同名透传"。混起来会让
    /// "不支持"和"没配置"变成同一件事，而它们的正确行为不同。
    /// </remarks>
    private static string? Resolve(WireCompat compat, string? level)
    {
        if (level is null) return null;

        if (compat.ThinkingLevelMap is { } map)
        {
            return map.TryGetValue(level, out var value) ? value : level;
        }

        return level;
    }

    /// <summary>
    /// Anthropic Messages 协议的思考参数。结构与 OpenAI 兼容族完全不同。
    /// </summary>
    /// <remarks>
    /// <b>两种形态，不能混用</b>（依据 pi 的 <c>api/anthropic-messages.ts</c>）：
    /// <list type="bullet">
    /// <item>老模型：<c>thinking: { type: "enabled", budget_tokens: N }</c>，
    /// 必须给绝对 token 预算，没有档位概念。</item>
    /// <item>自适应模型：<c>thinking: { type: "adaptive" }</c> +
    /// <c>output_config: { effort }</c>。</item>
    /// </list>
    /// 把档位词当预算发过去会 400；把预算当档位发过去也会 400。
    /// </remarks>
    public static void ApplyAnthropic(
        JsonObject body, string? effort, bool adaptive, int budgetTokens = 1024)
    {
        ArgumentNullException.ThrowIfNull(body);

        if (effort is null)
        {
            // 不启用思考：整块不发。Anthropic 没有 "disabled" 这个值——
            // 不发 thinking 字段就是不思考，发 {type:"disabled"} 反而是非法值。
            return;
        }

        if (adaptive)
        {
            body["thinking"] = new JsonObject { ["type"] = "adaptive" };
            body["output_config"] = new JsonObject { ["effort"] = effort };
            return;
        }

        // 预算制：档位词转不了预算，用调用方给的固定预算。
        // **不把档位名当预算发**——那会是一个字符串发给整数字段，直接 400。
        body["thinking"] = new JsonObject
        {
            ["type"] = "enabled",
            ["budget_tokens"] = budgetTokens,
        };
    }
}
