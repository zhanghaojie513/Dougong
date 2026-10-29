using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dougong.Agent;
using Dougong.Ai;
using Microsoft.Extensions.AI;

namespace Dougong.Agent.Comparison;

/// <summary>
/// 三种线协议的确定性校验：请求体形状、流解码、多轮回填。
/// </summary>
/// <remarks>
/// 为什么需要这一套：真实 provider 只验证过单轮调用。多轮次回填
/// （assistant 携带调用 → 工具结果回填 → 下一轮请求）是循环正确性的核心，
/// 而三种协议的形状完全不同：Anthropic 要求 <c>tool_result</c> block，
/// /responses 要求 <c>function_call_output</c> 条目。这些结构一旦写错，
/// 只有在第二次模型调用时才会被 provider 拒绝。
/// <para>
/// 这里用录制的帧与假传输做断言，不依赖网络，也不依赖 provider 当天行为。
/// </para>
/// </remarks>
public static class WireFormatChecks
{
    public static async Task<int> RunAsync()
    {
        var failures = 0;
        failures += await CheckAsync("chat-completions-declares-tools", ChatDeclaresToolsAsync);
        failures += await CheckAsync("chat-completions-roundtrip-tool-result", ChatRoundtripAsync);
        failures += await CheckAsync("anthropic-tool-result-is-user-block", AnthropicToolResultAsync);
        failures += await CheckAsync("anthropic-terminal-only-on-message-delta", AnthropicTerminalAsync);
        failures += await CheckAsync("responses-function-call-output-entry", ResponsesToolOutputAsync);
        failures += Check("responses-failed-raises", ResponsesFailed);
        failures += Check("fragmented-arguments-are-concatenated", Fragmented);
        failures += Check("empty-choices-frame-is-ignored", EmptyChoices);
        failures += Check("reasoning-content-is-echoed", Reasoning);
        failures += Check("other-reasoning-field-names-are-dropped", OtherReasoningFieldsDropped);
        failures += Check("dialect-switch-round-trips-to-wire", DialectRoundTrip);
        failures += Check("max-output-tokens-is-configurable", MaxTokensConfigurable);
        failures += await CheckAsync("runtime-history-carries-tool-result-text", RuntimeToolResultTextAsync);
        failures += await CheckAsync("reasoning-produces-live-event", ReasoningProducesEventAsync);
        failures += await CheckAsync("no-reasoning-yields-no-event", NoReasoningNoEventAsync);

        Console.WriteLine(failures == 0
            ? "WIRE: ALL PASS"
            : $"WIRE: {failures} FAILURE(S)");
        return failures;
    }

    private static ToolDescriptor Descriptor(string id, string schema = """{"type":"object","required":["path"]}""")
        => new(id, id, "1.0.0", "tool " + id,
            JsonDocument.Parse(schema).RootElement.Clone());

    private static ChatOptions OptionsWith(params ToolDescriptor[] tools)
        => new ChatOptions().Attach(tools);

    // chat-completions：工具必须出现在 tools[].function.parameters。
    private static async Task<string?> ChatDeclaresToolsAsync()
    {
        var format = new OpenAiChatCompletionsFormat();
        var body = format.BuildBody("m", [new ChatMessage(ChatRole.User, "hi")],
            OptionsWith(Descriptor("read")), stream: true);

        var tools = body["tools"] as JsonArray;
        if (tools is null || tools.Count != 1) return "工具声明缺失";
        var fn = tools[0]!["function"];
        if (fn?["name"]?.GetValue<string>() != "read") return "工具名不符";
        if (fn["parameters"]?["required"] is not JsonArray required
            || required[0]?.GetValue<string>() != "path") return "parameters schema 未透传";
        await Task.CompletedTask;
        return null;
    }

    // chat-completions：工具结果必须是 role=tool 且带 tool_call_id。
    private static async Task<string?> ChatRoundtripAsync()
    {
        var format = new OpenAiChatCompletionsFormat();
        var messages = new List<ChatMessage>
        {
            new(ChatRole.User, "read it"),
            new(ChatRole.Assistant,
                [new FunctionCallContent("c1", "read", new Dictionary<string, object?> { ["path"] = "a.txt" })])
            {
                AdditionalProperties = new AdditionalPropertiesDictionary
                {
                    // Agent 层用中性键；连接器翻译成方言字段名（G12）。
                    [AgentPropertyKeys.Reasoning] = "thinking",
                },
            },
            new(ChatRole.Tool, """{"ok":true}""")
            {
                AdditionalProperties = new AdditionalPropertiesDictionary { ["tool_call_id"] = "c1" },
            },
        };

        var array = format.BuildBody("m", messages, null, true)["messages"] as JsonArray;
        if (array is null || array.Count != 3) return "消息数不符";

        var assistant = array[1]!;
        if (assistant["tool_calls"] is not JsonArray calls || calls.Count != 1)
            return "assistant 未序列化 tool_calls";
        if (calls[0]!["id"]?.GetValue<string>() != "c1") return "tool_calls.id 不符";
        // 线上字段名由 WireCompat 决定，默认 reasoning_content。
        if (assistant["reasoning_content"]?.GetValue<string>() != "thinking")
            return "线上未回传 reasoning_content（默认方言）";

        var tool = array[2]!;
        if (tool["role"]?.GetValue<string>() != "tool") return "工具结果角色不是 tool";
        if (tool["tool_call_id"]?.GetValue<string>() != "c1") return "工具结果缺 tool_call_id";
        await Task.CompletedTask;
        return null;
    }

    // Anthropic：工具结果必须是 user 消息里的 tool_result block，不能是独立 tool 角色。
    private static async Task<string?> AnthropicToolResultAsync()
    {
        var format = new AnthropicMessagesFormat();
        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, "be brief"),
            new(ChatRole.User, "read it"),
            new(ChatRole.Assistant,
                [new FunctionCallContent("c1", "read", new Dictionary<string, object?> { ["path"] = "a.txt" })]),
            new(ChatRole.Tool, """{"ok":true}""")
            {
                AdditionalProperties = new AdditionalPropertiesDictionary { ["tool_call_id"] = "c1" },
            },
        };

        var body = format.BuildBody("m", messages, OptionsWith(Descriptor("read")), true);

        if (body["system"]?.GetValue<string>() != "be brief") return "system 未独立成字段";

        var array = body["messages"] as JsonArray;
        if (array is null) return "messages 缺失";
        // user + assistant + 合并后的 tool_result user 消息
        if (array.Count != 3) return $"消息数不符：{array.Count}";

        foreach (var item in array)
        {
            if (item?["role"]?.GetValue<string>() == "tool")
                return "不得出现独立 tool 角色";
        }

        var assistantBlocks = array[1]!["content"] as JsonArray;
        if (assistantBlocks is null) return "assistant content 不是 block 数组";
        if (assistantBlocks[0]?["type"]?.GetValue<string>() != "tool_use") return "assistant 未用 tool_use block";

        var toolResult = array[2]!;
        if (toolResult["role"]?.GetValue<string>() != "user") return "tool_result 外层角色必须是 user";
        var resultBlocks = toolResult["content"] as JsonArray;
        if (resultBlocks is null || resultBlocks.Count != 1) return "tool_result block 数不符";
        if (resultBlocks[0]?["type"]?.GetValue<string>() != "tool_result") return "不是 tool_result block";
        if (resultBlocks[0]!["tool_use_id"]?.GetValue<string>() != "c1") return "tool_use_id 不符";

        // 相邻的第二个工具结果必须并入同一条 user 消息，而不是新开一条。
        var twoResults = new List<ChatMessage>(messages)
        {
            new(ChatRole.Tool, "second")
            {
                AdditionalProperties = new AdditionalPropertiesDictionary { ["tool_call_id"] = "c2" },
            },
        };

        var mergedBody = format.BuildBody("m", twoResults, null, true);
        var merged = mergedBody["messages"] as JsonArray;
        if (merged is null || merged.Count != 3) return "相邻工具结果未合并且同一条 user 消息";
        if ((merged[2]!["content"] as JsonArray)?.Count != 2) return "合并后 block 数应为 2";

        // 工具声明必须是 input_schema，不是 function.parameters。
        var tools = body["tools"] as JsonArray;
        if (tools is null || tools.Count != 1) return "工具声明缺失";
        if (tools[0]!["input_schema"] is null) return "工具声明缺 input_schema";
        if (tools[0]!["function"] is not null) return "工具声明不得套 function 层";

        await Task.CompletedTask;
        return null;
    }

    // Anthropic：只有 message_delta.stop_reason 才是终结；content_block_delta 不是。
    private static async Task<string?> AnthropicTerminalAsync()
    {
        var decoder = new AnthropicMessagesFormat().CreateDecoder();
        var updates = new List<ChatResponseUpdate>();

        decoder.Decode("""{"type":"message_start","message":{"usage":{"input_tokens":7}}}""", updates);
        if (decoder.IsTerminal) return "message_start 不应终结";

        decoder.Decode("""{"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"hi"}}""", updates);
        if (decoder.IsTerminal) return "content_block_delta 不应终结";
        if (updates.Count != 1 || updates[0].Text != "hi") return "文本增量未透传";

        decoder.Decode("""{"type":"message_delta","delta":{"stop_reason":"end_turn"}}""", updates);
        if (!decoder.IsTerminal) return "message_delta.stop_reason 未标记终结";

        if (decoder.Usage?.InputTokenCount != 7) return "usage 未读取";
        await Task.CompletedTask;
        return null;
    }

    // /responses：工具结果必须是 function_call_output 条目，assistant 调用是 function_call 条目。
    private static async Task<string?> ResponsesToolOutputAsync()
    {
        var format = new OpenAiResponsesFormat();
        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, "be brief"),
            new(ChatRole.User, "read it"),
            new(ChatRole.Assistant,
                [new FunctionCallContent("call_1", "read", new Dictionary<string, object?> { ["path"] = "a.txt" })]),
            new(ChatRole.Tool, """{"ok":true}""")
            {
                AdditionalProperties = new AdditionalPropertiesDictionary { ["tool_call_id"] = "call_1" },
            },
        };

        var body = format.BuildBody("m", messages, OptionsWith(Descriptor("read")), true);

        if (body["instructions"]?.GetValue<string>() != "be brief") return "system 未走 instructions";
        if (body["messages"] is not null) return "/responses 不得出现 messages 字段";

        var input = body["input"] as JsonArray;
        if (input is null) return "input 缺失";

        var call = input.FirstOrDefault(i => i?["type"]?.GetValue<string>() == "function_call");
        if (call is null) return "assistant 调用未序列化为 function_call 条目";
        if (call["call_id"]?.GetValue<string>() != "call_1") return "function_call.call_id 不符";
        if (call["arguments"] is not JsonValue argsValue
            || !argsValue.ToJsonString().Contains("a.txt", StringComparison.Ordinal))
            return "function_call.arguments 未包含参数";

        var output = input.FirstOrDefault(i => i?["type"]?.GetValue<string>() == "function_call_output");
        if (output is null) return "工具结果未序列化为 function_call_output";
        if (output["call_id"]?.GetValue<string>() != "call_1") return "function_call_output.call_id 不符";

        var tools = body["tools"] as JsonArray;
        if (tools is null || tools[0]?["parameters"] is null) return "工具声明缺 parameters";
        if (tools[0]!["function"] is not null) return "/responses 工具声明不得套 function 层";

        await Task.CompletedTask;
        return null;
    }

    // /responses：response.failed 必须抛错，不能当正常结束。
    private static string? ResponsesFailed()
    {
        var decoder = new OpenAiResponsesFormat().CreateDecoder();
        var updates = new List<ChatResponseUpdate>();
        try
        {
            decoder.Decode(
                """{"type":"response.failed","response":{"error":{"message":"boom"}}}""", updates);
            return "response.failed 未抛错";
        }
        catch (InvalidOperationException ex)
        {
            return ex.Message.Contains("boom", StringComparison.Ordinal) ? null : "错误信息未透传";
        }
    }

    // 参数分片必须按 index 拼接原文，不能按键合并字典。
    private static string? Fragmented()
    {
        var decoder = new OpenAiChatCompletionsFormat().CreateDecoder();
        var updates = new List<ChatResponseUpdate>();

        decoder.Decode("""{"choices":[{"delta":{"tool_calls":[{"index":0,"id":"c1","function":{"name":"write","arguments":"{\"path\":\"a.txt\","}}]}}]}""", updates);
        decoder.Decode("""{"choices":[{"delta":{"tool_calls":[{"index":0,"function":{"arguments":"\"lines\":[\"one\",\"two\"]}"}}]}}]}""", updates);
        if (updates.Count != 0) return "未终结就下发了调用";

        decoder.Decode("""{"choices":[{"delta":{},"finish_reason":"tool_calls"}]}""", updates);
        if (!decoder.IsTerminal) return "finish_reason 未标记终结";

        var flushed = decoder.Flush();
        // 终结时未自动下发，由 Flush 负责；两种路径都必须给出完整参数。
        if (flushed.Count != 1) return $"应恰好下发 1 个调用，实际 {flushed.Count}";
        var call = flushed[0].Contents.OfType<FunctionCallContent>().Single();
        if (call.CallId != "c1" || call.Name != "write") return "调用身份不符";
        if (call.Arguments is null) return "参数未解析";
        if (!call.Arguments.TryGetValue("lines", out var lines) || lines is null)
            return "嵌套数组参数丢失（按键合并会拍平结构）";

        return null;
    }

    // 只发用量的帧没有 choices：必须跳过而不是越界。
    private static string? EmptyChoices()
    {
        var decoder = new OpenAiChatCompletionsFormat().CreateDecoder();
        var updates = new List<ChatResponseUpdate>();
        try
        {
            decoder.Decode("""{"usage":{"prompt_tokens":3,"completion_tokens":1}}""", updates);
            decoder.Decode("""{"choices":[]}""", updates);
        }
        catch (Exception ex)
        {
            return $"空 choices 帧抛错：{ex.GetType().Name}";
        }

        if (updates.Count != 0) return "空帧不应产生更新";
        if (decoder.Usage?.InputTokenCount != 3) return "usage 未读取";
        return null;
    }

    // 思考模式：reasoning_content 必须随 assistant 更新带出，否则下一轮被拒。
    private static string? Reasoning()
    {
        var decoder = new OpenAiChatCompletionsFormat().CreateDecoder();
        var updates = new List<ChatResponseUpdate>();

        decoder.Decode("""{"choices":[{"delta":{"reasoning_content":"think "}}]}""", updates);
        decoder.Decode("""{"choices":[{"delta":{"reasoning_content":"more"}}]}""", updates);
        decoder.Decode("""{"choices":[{"delta":{"tool_calls":[{"index":0,"id":"c1","function":{"name":"read","arguments":"{\"path\":\"a\"}"}}]}}]}""", updates);
        decoder.Decode("""{"choices":[{"delta":{},"finish_reason":"tool_calls"}]}""", updates);

        var flushed = decoder.Flush();
        if (flushed.Count != 1) return $"应下发 1 个更新，实际 {flushed.Count}";
        // Agent 层用中性键（AgentPropertyKeys.Reasoning），连接器负责翻译成方言字段名。
        // 这里断言的是中性键：跨层契约不该随 provider 变。
        var props = flushed[0].AdditionalProperties;
        if (props is null || !props.TryGetValue(AgentPropertyKeys.Reasoning, out var value))
            return $"{AgentPropertyKeys.Reasoning} 未随调用带出";
        return value?.ToString() == "think more" ? null : $"思考内容不符：{value}";
    }

    // G12 已修：思考字段名由 WireCompat 决定，不再是硬编码。
    //
    // 这条检查的演化值得留存：它最初是**特征化探针**——断言"缺陷确实存在且确实静默"
    // （换一个字段名，思考内容会被无声丢弃）。G12 修好后它按设计失败并提示
    // "缺陷已修，请更新文档"。现在改为验证修复本身：两种字段名都能正确工作。
    private static string? OtherReasoningFieldsDropped()
    {
        // ① 默认方言：reasoning_content
        var classic = DecodeReasoning(WireCompat.Default);
        if (classic != "think more")
            return $"默认方言（reasoning_content）下思考内容为 '{classic}'，应能读到";

        // ② 覆盖方言：reasoning
        var alternate = DecodeReasoning(
            WireCompat.Default with { ReasoningField = "reasoning" });
        if (alternate != "think more")
            return $"覆盖为 'reasoning' 后思考内容为 '{alternate}'，说明方言开关没生效";

        // ③ 反方向：方言设成 reasoning 时，reasoning_content 应被忽略。
        // 这条防的是"两个字段都读"的假实现——那会让两种方言互相污染。
        var strict = DecodeReasoning(
            WireCompat.Default with { ReasoningField = "reasoning" }, field: "reasoning_content");
        if (strict is not null && strict.Length > 0)
            return $"方言设为 'reasoning' 时仍读了 reasoning_content（得到 '{strict}'）：开关未真正隔离";

        return null;
    }

    /// <param name="field">响应里实际使用的字段名；null 表示与方言一致。</param>
    private static string DecodeReasoning(WireCompat compat, string? field = null)
    {
        var name = field ?? compat.ReasoningField;
        var decoder = new OpenAiChatCompletionsFormat(compat).CreateDecoder();
        var updates = new List<ChatResponseUpdate>();

        // 用字符串拼接而不是内插原始字符串：多了一层大括号，内插语法要额外 $，
        // 这里没必要跟编译器较劲。
        decoder.Decode(
            "{\"choices\":[{\"delta\":{\"" + name + "\":\"think \"}}]}", updates);
        decoder.Decode(
            "{\"choices\":[{\"delta\":{\"" + name + "\":\"more\"}}]}", updates);
        decoder.Decode(
            """{"choices":[{"delta":{"tool_calls":[{"index":0,"id":"c1","function":{"name":"read","arguments":"{}"}}]}}]}""",
            updates);
        decoder.Decode("""{"choices":[{"delta":{},"finish_reason":"tool_calls"}]}""", updates);

        var flushed = decoder.Flush();
        if (flushed.Count != 1) return string.Empty;

        var props = flushed[0].AdditionalProperties;
        return props is not null && props.TryGetValue(AgentPropertyKeys.Reasoning, out var value)
            ? value?.ToString() ?? string.Empty
            : string.Empty;
    }

    /// <summary>
    /// 方言开关必须端到端生效：读进来、写回去都用同一方言。
    /// </summary>
    /// <remarks>
    /// 只测解码器不够——G12 的本质是"解码和编码用了不同字段名"时静默丢失。
    /// 这条真的走一遍：用 <c>reasoning</c> 方言解出思考内容，再把它作为历史
    /// 构造下一轮请求，断言请求体里用的是同一方言的字段名。
    /// </remarks>
    private static string? DialectRoundTrip()
    {
        var compat = WireCompat.Default with { ReasoningField = "reasoning" };
        var format = new OpenAiChatCompletionsFormat(compat);

        // 解码：provider 用 reasoning 字段给出思考内容。
        var decoder = format.CreateDecoder();
        var updates = new List<ChatResponseUpdate>();
        decoder.Decode("{\"choices\":[{\"delta\":{\"reasoning\":\"思考中\"}}]}", updates);
        decoder.Decode(
            """{"choices":[{"delta":{"tool_calls":[{"index":0,"id":"c1","function":{"name":"read","arguments":"{}"}}]}}]}""",
            updates);
        decoder.Decode("""{"choices":[{"delta":{},"finish_reason":"tool_calls"}]}""", updates);

        var flushed = decoder.Flush();
        if (flushed.Count != 1) return $"应下发 1 个更新，实际 {flushed.Count}";

        var props = flushed[0].AdditionalProperties;
        if (props is null || !props.TryGetValue(AgentPropertyKeys.Reasoning, out var value))
            return "解码阶段未产出中性键";

        // 编码：把思考内容放回历史，构造下一轮请求体。
        var history = new List<ChatMessage>
        {
            new(ChatRole.User, "read it"),
            new(ChatRole.Assistant,
                [new FunctionCallContent("c1", "read", new Dictionary<string, object?> { ["path"] = "a.txt" })])
            {
                AdditionalProperties = new AdditionalPropertiesDictionary
                {
                    [AgentPropertyKeys.Reasoning] = value!.ToString(),
                },
            },
            new(ChatRole.Tool, "ok")
            {
                AdditionalProperties = new AdditionalPropertiesDictionary { ["tool_call_id"] = "c1" },
            },
        };

        var array = format.BuildBody("m", history, null, true)["messages"] as JsonArray;
        var assistant = array?[1];
        if (assistant?["reasoning"]?.GetValue<string>() != "思考中")
            return $"编码阶段未使用方言字段 'reasoning'：{assistant?.ToJsonString()}";
        if (assistant["reasoning_content"] is not null)
            return "编码阶段同时写了 reasoning_content：方言未隔离";

        return null;
    }

    /// <summary>输出上限必须可配置，字段名也由方言决定（G13）。</summary>
    private static string? MaxTokensConfigurable()
    {
        var messages = new List<ChatMessage> { new(ChatRole.User, "hi") };

        // ① 默认不发送该字段：保持已验证端点的行为。
        var plain = new OpenAiChatCompletionsFormat()
            .BuildBody("m", messages, new ChatOptions { MaxOutputTokens = 1234 }, true);
        if (plain["max_tokens"] is not null || plain["max_completion_tokens"] is not null)
            return "默认方言不应发送输出上限字段（会改变已验证端点的行为）";

        // ② 显式配置 max_tokens。
        var classic = new OpenAiChatCompletionsFormat(
                WireCompat.Default with { MaxTokensField = "max_tokens" })
            .BuildBody("m", messages, new ChatOptions { MaxOutputTokens = 1234 }, true);
        if (classic["max_tokens"]?.GetValue<int>() != 1234)
            return $"max_tokens 未按配置发送：{classic.ToJsonString()}";

        // ③ 显式配置 max_completion_tokens。
        var modern = new OpenAiChatCompletionsFormat(
                WireCompat.Default with { MaxTokensField = "max_completion_tokens" })
            .BuildBody("m", messages, new ChatOptions { MaxOutputTokens = 4321 }, true);
        if (modern["max_completion_tokens"]?.GetValue<int>() != 4321)
            return $"max_completion_tokens 未按配置发送：{modern.ToJsonString()}";

        return null;
    }

    // 工具结果回填后，协议层必须能读到内容。
    // 这条针对一个真实缺陷：ChatMessage.Text 由 Contents 派生，
    // 只放 FunctionResultContent 会让 Text 变空，三种协议都拿到空字符串，
    // 模型因此说"工具没有返回任何东西"。
    private static async Task<string?> RuntimeToolResultTextAsync()
    {
        var tool = new EchoTool();
        var dispatcher = new ToolDispatcher();
        dispatcher.Register(tool);
        var client = new ScriptedChatClient(
            new ScriptedTurn(
                new ScriptedUpdate(FunctionCallId: "c1", FunctionName: "echo",
                    ArgumentsJson: """{"text":"payload-42"}""")),
            new ScriptedTurn(new ScriptedUpdate(Text: "done")));

        var runtime = new LoopAgentRuntime(client, dispatcher);
        await foreach (var _ in runtime.RunAsync(new AgentRunRequest("s", "t", [], "go"))) { }

        // 第二次模型调用看到的历史里，tool 消息必须带内容。
        if (client.ObservedHistory.Count < 2) return "模型只被调用了一次";
        var toolMessage = client.ObservedHistory[1].LastOrDefault(m => m.Role == ChatRole.Tool);
        if (toolMessage is null) return "历史里没有 tool 消息";
        if (toolMessage.Text is not { Length: > 0 } text) return "tool 消息 Text 为空";
        return text.Contains("payload-42", StringComparison.Ordinal)
            ? null
            : $"tool 消息内容不符：{text}";
    }

    /// <summary>
    /// 推理内容必须作为事件实时产出，而不只是存进历史。
    /// </summary>
    /// <remarks>
    /// <b>这条检查针对的是一个如实记录过的能力缺口</b>：<c>LoopAgentRuntime</c>
    /// 原先只把 <c>AgentPropertyKeys.Reasoning</c> 追加进 StringBuilder、写进历史，
    /// 从不构造事件。于是宿主拿不到实时推理流，UI 只能在会话恢复时补显示。
    /// <para>
    /// 插件补不了这个缺口：它只能订阅已是 <c>AgentEvent</c> 的东西，
    /// 而这份数据在运行时就已被吞掉，进程内根本没有可订阅的流。
    /// </para>
    /// </remarks>
    private static async Task<string?> ReasoningProducesEventAsync()
    {
        var client = new ScriptedChatClient(
            new ScriptedTurn(
                new ScriptedUpdate(Reasoning: "先想一下。" + "\n"),
                new ScriptedUpdate(Reasoning: "答案是 4。"),
                new ScriptedUpdate(Text: "4")));

        var runtime = new LoopAgentRuntime(client, new ToolDispatcher());

        var reasoning = new System.Text.StringBuilder();
        var texts = new System.Text.StringBuilder();
        var reasoningEvents = 0;

        await foreach (var evt in runtime.RunAsync(
            new AgentRunRequest("s", "t", [], "2+2?")))
        {
            switch (evt)
            {
                case AgentEvent.ReasoningDelta delta:
                    reasoning.Append(delta.Text);
                    reasoningEvents++;
                    break;

                case AgentEvent.TextDelta delta:
                    texts.Append(delta.Text);
                    break;
            }
        }

        if (reasoningEvents == 0)
            return "推理内容没有作为事件产出（只进了历史）——这正是要修的缺口";

        if (reasoning.ToString() != "先想一下。" + "\n" + "答案是 4。")
            return $"推理内容不符：[{reasoning}]";

        // 推理绝不能混进正文：否则 UI 会把"未定稿的自我纠错"当回答显示。
        if (texts.ToString() != "4")
            return $"正文被推理污染：[{texts}]";

        return null;
    }

    /// <summary>
    /// 没有推理内容时不得凭空产出 <c>ReasoningDelta</c>。
    /// </summary>
    /// <remarks>
    /// 与上一条互为对偶：只测"有推理时要产出"，会让"无条件产出空事件"同样通过，
    /// 而那会让宿主把每个普通模型都当成支持思考模式。
    /// </remarks>
    private static async Task<string?> NoReasoningNoEventAsync()
    {
        var client = new ScriptedChatClient(
            new ScriptedTurn(new ScriptedUpdate(Text: "4")));

        var runtime = new LoopAgentRuntime(client, new ToolDispatcher());

        var reasoningEvents = 0;
        await foreach (var evt in runtime.RunAsync(
            new AgentRunRequest("s", "t", [], "2+2?")))
        {
            if (evt is AgentEvent.ReasoningDelta) reasoningEvents++;
        }

        return reasoningEvents == 0
            ? null
            : $"模型没有推理内容，却产出了 {reasoningEvents} 个 ReasoningDelta";
    }

    private sealed class EchoTool : IAgentTool
    {
        public ToolDescriptor Descriptor { get; } = new(
            "echo", "echo", "1.0.0", "echo",
            JsonDocument.Parse("""{"type":"object","required":["text"]}""").RootElement.Clone());

        public ToolPermission DescribePermission(JsonElement a) =>
            new(ToolAccess.None, true, [], [], []);
        public ToolExecutionPlan DescribeExecution(JsonElement a) => ToolExecutionPlan.Serial;

        public ValueTask<AgentToolResult> ExecuteAsync(
            ToolInvocation invocation, CancellationToken cancellationToken)
        {
            var text = invocation.Arguments.TryGetProperty("text", out var value)
                ? value.GetString() ?? string.Empty : string.Empty;
            using var document = JsonDocument.Parse(
                JsonSerializer.Serialize(new { echoed = text }));
            return ValueTask.FromResult(new AgentToolResult(document.RootElement.Clone()));
        }
    }

    private static int Check(string name, Func<string?> action)
    {
        try
        {
            var failure = action();
            if (failure is null)
            {
                Console.WriteLine($"PASS {name}");
                return 0;
            }

            Console.WriteLine($"FAIL {name}: {failure}");
            return 1;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"FAIL {name}: {ex.GetType().Name}: {ex.Message}");
            return 1;
        }
    }

    private static async Task<int> CheckAsync(string name, Func<Task<string?>> action)
    {
        try
        {
            var failure = await action();
            if (failure is null)
            {
                Console.WriteLine($"PASS {name}");
                return 0;
            }

            Console.WriteLine($"FAIL {name}: {failure}");
            return 1;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"FAIL {name}: {ex.GetType().Name}: {ex.Message}");
            return 1;
        }
    }
}
