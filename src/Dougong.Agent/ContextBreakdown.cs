namespace Dougong.Agent;

/// <summary>
/// 估算一次请求的上下文构成：系统提示词 / 工具定义 / 对话消息。
/// </summary>
/// <remarks>
/// <b>这是估算，不是实测</b>。三项都用字符统计的启发式规则
/// （<see cref="TokenBudget.Estimate(string)"/>），与真正发出去的 token 数
/// 必然有偏差——系统提示与工具 schema 里常有大量结构化符号，
/// 实际分词比线性估算更省。
///
/// <para>
/// <b>为什么由 SDK 算而不是宿主</b>：三项里有两项宿主拿不到。
/// 系统提示词是 SDK 拼的；工具定义是 SDK 把 <see cref="IAgentTool"/> 序列化成
/// schema 的产物，宿主手里只有工具对象。让宿主自己算等于重做一遍序列化，
/// 而那必然与实际发出请求里的不一致。
/// </para>
///
/// <para>
/// <b>分解口径照 dsh 的 <c>contextBreakdown</c></b>：它把上下文拆成
/// <c>systemTokens</c> / <c>toolsTokens</c> / <c>messageTokens</c> 三项，
/// 并明确注释这是"上下文的构成，而非提供方计费规模"。
/// 这里的口径与之一致：最后一条系统提示、本次请求的工具 schema、其余历史。
/// </para>
/// </remarks>
public static class ContextBreakdown
{
    /// <summary>一次请求的上下文构成估算。</summary>
    /// <param name="SystemPromptTokens">最后一个非空系统提示词的估算。</param>
    /// <param name="ToolDefinitionTokens">工具 schema 的估算（按请求里的形态）。</param>
    /// <param name="MessageTokens">其余可见历史的估算。</param>
    public readonly record struct Estimate(
        int SystemPromptTokens,
        int ToolDefinitionTokens,
        int MessageTokens)
    {
        /// <summary>三项之和。</summary>
        public int TotalTokens => SystemPromptTokens + ToolDefinitionTokens + MessageTokens;
    }

    /// <summary>
    /// 按"将要发出的一次请求"估算构成。
    /// </summary>
    /// <param name="systemPrompt">系统提示词全文；null 或空表示这一轮没有。</param>
    /// <param name="tools">本次请求声明的工具。</param>
    /// <param name="history">将要发送的历史（已含本轮用户输入）。</param>
    public static Estimate Measure(
        string? systemPrompt,
        IReadOnlyList<ToolDescriptor> tools,
        IReadOnlyList<AgentMessage> history)
    {
        // 系统提示：只有最后一个算 systemTokens。历史上被取代的系统提示
        // 归到 messageTokens（与 dsh 的"including superseded system prompts"一致），
        // 否则一次提示变更会让"系统提示词"那栏显示成一个根本不存在的合并长度。
        var systemTokens = string.IsNullOrEmpty(systemPrompt)
            ? 0
            : TokenBudget.Estimate(systemPrompt);

        // 工具定义：按真正要发的 schema 形态估，不是按工具对象的字段。
        // 用 JsonSerializer 序列化一遍是最接近实际请求的做法——
        // 声明里包含 name / description / 参数 schema，缺一项都会低估。
        var toolsTokens = tools.Count == 0 ? 0 : EstimateTools(tools);

        var messageTokens = 0;
        foreach (var message in history)
            messageTokens += TokenBudget.Estimate(RenderMessageForEstimate(message));

        return new Estimate(systemTokens, toolsTokens, messageTokens);
    }

    /// <summary>估算工具 schema 的 token 数。</summary>
    /// <remarks>
    /// 把声明序列化成 JSON 再估字符数。比按字段逐项估算准：
    /// 工具的参数 schema 往往很深，逐项乘系数会累积误差。
    /// </remarks>
    private static int EstimateTools(IReadOnlyList<ToolDescriptor> tools)
    {
        var nodes = ToolDeclarations.ToOpenAiToolsArray(tools);
        return TokenBudget.Estimate(nodes.ToJsonString());
    }

    /// <summary>
    /// 把一条历史消息折叠成用于估算的文本。
    /// </summary>
    /// <remarks>
    /// <b>必须包含工具调用与结果</b>：只估 <c>Content</c> 会漏掉
    /// <c>tool_calls</c> 的参数 JSON 与工具返回，而工具返回往往是上下文里
    /// 最大的一块（一次读文件可能几万字符）。
    /// </remarks>
    private static string RenderMessageForEstimate(AgentMessage message)
    {
        if (message.ToolCalls.Count == 0) return message.Content;

        var builder = new System.Text.StringBuilder(message.Content);
        foreach (var call in message.ToolCalls)
        {
            builder.Append(' ').Append(call.ToolId).Append(' ')
                   .Append(call.Arguments.GetRawText());
        }

        return builder.ToString();
    }
}
