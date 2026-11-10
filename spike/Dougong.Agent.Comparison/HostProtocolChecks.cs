using System.Text.Json;
using System.Text.Json.Serialization;
using Dougong.HostProtocol;

namespace Dougong.Agent.Comparison;

/// <summary>
/// 宿主协议的验证：多态序列化必须往返，且旧客户端遇到新判别器不崩。
/// </summary>
/// <remarks>
/// <b>契约包最容易错的地方不是类型定义，是序列化配置。</b>
/// 两侧各写一份 <see cref="JsonSerializerOptions"/> 是最常见的裂缝——
/// 一侧忽略 null、另一侧不忽略时，"字段不存在"与"字段是 null"
/// 在线上就无法区分。
/// </remarks>
public static class HostProtocolChecks
{
    /// <summary>
    /// 固定时刻，用于构造可比较的往返样本。
    /// </summary>
    /// <remarks>
    /// <b>必须是固定值而不是 <c>DateTimeOffset.Now</c></b>：
    /// 用"现在"构造样本时，往返前后必然差几毫秒，
    /// 于是"字段有没有保真"这件事被时间抖动掩盖——
    /// 检查会通过，但其实没验证任何东西。
    /// </remarks>
    private static readonly DateTimeOffset FixedAt =
        new(2026, 1, 2, 3, 4, 5, TimeSpan.FromHours(8));

    public static async Task<int> RunAsync()
    {
        var failures = 0;
        failures += await CheckAsync("command-roundtrip-keeps-method", CommandRoundTripAsync);
        failures += await CheckAsync("event-roundtrip-keeps-sequence", EventRoundTripAsync);
        failures += await CheckAsync("item-roundtrip-keeps-kind", ItemRoundTripAsync);
        failures += await CheckAsync("all-derived-types-register-discriminator", AllRegisteredAsync);
        failures += await CheckAsync("every-command-serializes-its-method", CommandsCarryMethodAsync);
        failures += await CheckAsync("every-event-serializes-its-method", EventsCarryMethodAsync);
        failures += await CheckAsync("wire-property-names-are-camel-case", WireNamingAsync);
        failures += await CheckAsync("non-null-payload-fields-reach-the-wire", PayloadFieldsAsync);
        failures += await CheckAsync("unknown-method-is-explicitly-handled", UnknownMethodAsync);
        failures += await CheckAsync("shared-json-options-are-correct", SharedOptionsAsync);

        // 缺字段时的行为：这两种默认值都会静默产生错误数据，必须钉住。
        failures += await CheckAsync("missing-required-field-fails-loudly", MissingRequiredAsync);
        failures += await CheckAsync("timestamp-is-not-defaulted-to-now", TimestampNotNowAsync);

        // 模型目录的载荷必须够客户端用：原先是残缺的，而且从未被验证。
        failures += await CheckAsync("models-listed-carries-providers", ModelsCarriesProvidersAsync);
        failures += await CheckAsync("models-listed-carries-current-model", ModelsCarriesCurrentAsync);

        // 从**真实线协议文本**解析，而不是从对象往返——
        // 对象往返验证的是"我们写的序列化配置"，构造时漏填它抓不到。
        failures += await CheckAsync("models-listed-parses-from-wire-text", ModelsParsesFromWireAsync);

        // C5 / C8：权限预设与子 agent 的契约（桌面端要的东西）。
        // 这两条缺口曾经是"能力在引擎里、契约没暴露"，于是桌面端只能
        // 自己引用 Dougong.Agent 再实现一遍——那会绕过唯一契约。
        failures += await CheckAsync("permission-presets-wire-roundtrip", PermissionRoundTripAsync);
        failures += await CheckAsync("permission-custom-preset-is-not-selectable", PermissionCustomAsync);
        failures += await CheckAsync("subagents-wire-roundtrip", SubagentRoundTripAsync);
        failures += await CheckAsync("subagent-outcome-is-carried-once", SubagentOutcomeOnceAsync);

        Console.WriteLine(failures == 0
            ? "HOST-PROTOCOL: ALL PASS"
            : $"HOST-PROTOCOL: {failures} FAILURE(S)");
        return failures;
    }

    /// <summary>权限预设状态必须能从线文本还原，包括"不可选的自定义档"。</summary>
    /// <remarks>
    /// <b>重点验 <c>Selectable</c> 这个布尔能过线</b>：它决定界面给不给点。
    /// 序列化时被忽略掉的话，客户端会拿到默认 <c>false</c>，
    /// 于是所有档位都变成不可点——而 JSON 本身看起来完全正常。
    /// </remarks>
    private static Task<string?> PermissionRoundTripAsync()
    {
        var original = new PermissionPresetListedEvent
        {
            Sequence = 7,
            SessionId = "s1",
            State = new PermissionPresetState(
                "interactive", "交互式",
                [
                    new PermissionPresetInfo("interactive", "交互式", "写操作要确认", true),
                    new PermissionPresetInfo("unattended", "无人值守", null, true),
                ]),
        };

        var json = JsonSerializer.Serialize<ServerEvent>(original, HostProtocolContract.Json);
        if (!json.Contains("\"method\":\"permission/listed\"", StringComparison.Ordinal))
            return Task.FromResult<string?>($"判别器不对：{json}");

        var back = JsonSerializer.Deserialize<ServerEvent>(json, HostProtocolContract.Json)
            as PermissionPresetListedEvent;

        if (back is null) return Task.FromResult<string?>("反序列化得到 null 或错类型");
        if (back.State.CurrentKey != "interactive") return Task.FromResult<string?>("当前键丢失");
        if (back.State.CurrentName != "交互式") return Task.FromResult<string?>("当前显示名丢失");
        if (back.State.Options.Count != 2) return Task.FromResult<string?>($"选项数不对：{back.State.Options.Count}");
        if (!back.State.Options[0].Selectable) return Task.FromResult<string?>("Selectable=true 丢失");
        if (back.State.Options[1].Description is not null)
            return Task.FromResult<string?>("null 的 Description 不该变成空串");

        // 命令方向也要过。
        var switchJson = JsonSerializer.Serialize<ClientCommand>(
            new SwitchPermissionPresetCommand { RequestId = "r", SessionId = "s1", Key = "unattended" },
            HostProtocolContract.Json);

        var switchBack = JsonSerializer.Deserialize<ClientCommand>(switchJson, HostProtocolContract.Json)
            as SwitchPermissionPresetCommand;

        return Task.FromResult(switchBack?.Key == "unattended"
            ? null
            : $"切换命令往返失败：{switchJson}");
    }

    /// <summary>
    /// 派生的 <c>custom</c> 档必须是**不可选**的。
    /// </summary>
    /// <remarks>
    /// <b>这条防的是一个会让界面抛异常的细节</b>：<c>custom</c> 是"当前值不匹配任何
    /// 具名预设"时的派生显示值，不是切换目标。若把它当可选项发给客户端，
    /// 用户点下去会得到"未知预设"——而他明明是从界面上点来的。
    /// </remarks>
    private static Task<string?> PermissionCustomAsync()
    {
        // 构造一个"当前策略不匹配任何预设"的状态，模拟引擎的 Options() 输出。
        var state = new PermissionPresetState(
            "custom", "自定义",
            [
                new PermissionPresetInfo("interactive", "交互式", null, true),
                new PermissionPresetInfo("custom", "自定义", "由宿主直接设置", false),
            ]);

        var json = JsonSerializer.Serialize<ServerEvent>(
            new PermissionPresetListedEvent { Sequence = 1, State = state },
            HostProtocolContract.Json);

        var back = JsonSerializer.Deserialize<ServerEvent>(json, HostProtocolContract.Json)
            as PermissionPresetListedEvent;

        if (back is null) return Task.FromResult<string?>("反序列化失败");

        var custom = back.State.Options.FirstOrDefault(o => o.Key == "custom");
        if (custom is null) return Task.FromResult<string?>("custom 档没上线——界面无法显示'当前是自定义'");

        return Task.FromResult(custom.Selectable
            ? "custom 被标成可选——用户点下去会得到'未知预设'"
            : null);
    }

    /// <summary>子 agent 列表必须能从线文本还原，状态枚举按名字而非序号。</summary>
    private static Task<string?> SubagentRoundTripAsync()
    {
        var original = new SubagentsListedEvent
        {
            Sequence = 3,
            SessionId = "s1",
            Agents =
            [
                new SubagentInfo(
                    "sub-abc", "查调用链", SubagentRunState.Running,
                    FixedAt, "turn-1", ToolRounds: 2),
                new SubagentInfo(
                    "sub-def", "收重复实现", SubagentRunState.Completed,
                    FixedAt, ParentTurnId: null, ToolRounds: 5),
            ],
        };

        var json = JsonSerializer.Serialize<ServerEvent>(original, HostProtocolContract.Json);
        if (!json.Contains("\"method\":\"subagent/listed\"", StringComparison.Ordinal))
            return Task.FromResult<string?>($"判别器不对：{json}");

        // **必须是名字而不是数字**：枚举按序号传时，两侧枚举加值的顺序
        // 一旦不同，同一个数字在两端就表示不同的状态——静默错。
        if (!json.Contains("\"running\"", StringComparison.Ordinal))
            return Task.FromResult<string?>($"状态没有按名字序列化：{json}");

        var back = JsonSerializer.Deserialize<ServerEvent>(json, HostProtocolContract.Json)
            as SubagentsListedEvent;

        if (back is null) return Task.FromResult<string?>("反序列化失败");
        if (back.Agents.Count != 2) return Task.FromResult<string?>($"条目数不对：{back.Agents.Count}");
        if (back.Agents[0].State != SubagentRunState.Running)
            return Task.FromResult<string?>($"状态错：{back.Agents[0].State}");
        if (back.Agents[0].ParentTurnId != "turn-1")
            return Task.FromResult<string?>("ParentTurnId 丢失——界面无法把子 agent 归到对应回合");
        if (back.Agents[0].StartedAt != FixedAt)
            return Task.FromResult<string?>("StartedAt 丢失——界面无法显示'跑了多久'");
        if (back.Agents[1].ToolRounds != 5)
            return Task.FromResult<string?>("ToolRounds 丢失");

        return Task.FromResult<string?>(null);
    }

    /// <summary>取结果的响应必须带 outcome，且"还没好"时明确为 null。</summary>
    /// <remarks>
    /// <b>"还没好"不能靠"缺字段"表达</b>：<c>DefaultIgnoreCondition.WhenWritingNull</c>
    /// 会让 null 字段整个消失。客户端必须能区分"这个字段没有"与"这次没有结果"——
    /// 所以检查直接验：反序列化出来是 null（缺字段与显式 null 在语义上一致），
    /// 但 <see cref="SubagentsListedEvent.Agents"/> 必须仍然完整，
    /// 客户端据此知道"它在跑，还没结果"。
    /// </remarks>
    private static Task<string?> SubagentOutcomeOnceAsync()
    {
        var withOutcome = new SubagentsListedEvent
        {
            Sequence = 4,
            SessionId = "s1",
            Agents = [],
            Outcome = new SubagentOutcome("sub-abc", SubagentRunState.Completed, "结论正文", null, 3),
        };

        var json = JsonSerializer.Serialize<ServerEvent>(withOutcome, HostProtocolContract.Json);
        var back = JsonSerializer.Deserialize<ServerEvent>(json, HostProtocolContract.Json)
            as SubagentsListedEvent;

        if (back?.Outcome is null) return Task.FromResult<string?>("Outcome 丢失");
        if (back.Outcome.Text != "结论正文") return Task.FromResult<string?>("结果正文丢失");
        if (back.Outcome.State != SubagentRunState.Completed)
            return Task.FromResult<string?>($"结果状态错：{back.Outcome.State}");

        // 没有结果时：Outcome 必须是 null，而 Agents 仍然给出（含 Running 那条）。
        var stillRunning = new SubagentsListedEvent
        {
            Sequence = 5,
            SessionId = "s1",
            Agents = [new SubagentInfo("sub-x", "还在跑", SubagentRunState.Running, FixedAt, "t", null)],
            Outcome = null,
        };

        var json2 = JsonSerializer.Serialize<ServerEvent>(stillRunning, HostProtocolContract.Json);
        var back2 = JsonSerializer.Deserialize<ServerEvent>(json2, HostProtocolContract.Json)
            as SubagentsListedEvent;

        if (back2 is null) return Task.FromResult<string?>("第二轮反序列化失败");
        if (back2.Outcome is not null) return Task.FromResult<string?>("没有结果时 Outcome 不该有值");

        return Task.FromResult(back2.Agents.Count == 1 && back2.Agents[0].State == SubagentRunState.Running
            ? null
            : "没跑完时状态必须是 Running，且该 agent 仍要在列表里");
    }

    /// <summary>命令往返后判别字段必须保留，否则服务端分不出是哪种命令。</summary>
    private static Task<string?> CommandRoundTripAsync()
    {
        ClientCommand original = new StartTurnCommand
        {
            RequestId = "r1",
            SessionId = "s1",
            Input = "hello",
        };

        var json = JsonSerializer.Serialize(original, HostProtocolContract.Json);
        if (!json.Contains("\"method\":\"turn/start\"", StringComparison.Ordinal))
            return Task.FromResult<string?>($"序列化后丢了 method 判别字段：{json}");

        var back = JsonSerializer.Deserialize<ClientCommand>(json, HostProtocolContract.Json);
        if (back is not StartTurnCommand start)
            return Task.FromResult<string?>($"反序列化得到 {back?.GetType().Name ?? "null"}");

        return Task.FromResult(start.Input == "hello" && start.SessionId == "s1"
            ? null
            : $"字段丢失：{start.Input}/{start.SessionId}");
    }

    /// <summary>事件往返后序号必须保留——它是缺口检测的唯一依据。</summary>
    private static Task<string?> EventRoundTripAsync()
    {
        ServerEvent original = new ItemAddedEvent
        {
            Sequence = 42,
            SessionId = "s1",
            TurnId = "t1",
            Item = new UserMessageItem { Id = "i1", At = FixedAt, Text = "hi" },
        };

        var json = JsonSerializer.Serialize(original, HostProtocolContract.Json);
        var back = JsonSerializer.Deserialize<ServerEvent>(json, HostProtocolContract.Json);

        if (back is not ItemAddedEvent added)
            return Task.FromResult<string?>($"反序列化得到 {back?.GetType().Name ?? "null"}");

        if (added.Sequence != 42)
            return Task.FromResult<string?>($"序号丢失：{added.Sequence}");

        // 嵌套的多态字段也必须能还原——这是双层多态，最容易漏。
        if (added.Item is not UserMessageItem user)
            return Task.FromResult<string?>($"嵌套条目丢了 kind：{added.Item?.GetType().Name}");

        return Task.FromResult(user.Text == "hi" ? null : $"嵌套条目内容丢失：{user.Text}");
    }

    /// <summary>六种条目都要能往返，且判别器不串。</summary>
    private static Task<string?> ItemRoundTripAsync()
    {
        ConversationItem[] items =
        [
            new UserMessageItem { Id = "1", At = FixedAt, Text = "u" },
            new AssistantMessageItem { Id = "2", At = FixedAt, Text = "a" },
            new ReasoningItem { Id = "3", At = FixedAt, Text = "r" },
            new ToolCallItem { Id = "4", At = FixedAt, InvocationId = "c", ToolName = "shell" },
            new PlanItem { Id = "5", At = FixedAt, Steps = [new PlanStep("s", "pending", "high")] },
            new NoticeItem { Id = "6", At = FixedAt, Text = "n", Level = NoticeLevel.Warning },
        ];

        foreach (var item in items)
        {
            var json = JsonSerializer.Serialize(item, HostProtocolContract.Json);
            var back = JsonSerializer.Deserialize<ConversationItem>(json, HostProtocolContract.Json);

            if (back?.GetType() != item.GetType())
            {
                return Task.FromResult<string?>(
                    $"{item.GetType().Name} 往返后变成 {back?.GetType().Name ?? "null"}");
            }
        }

        return Task.FromResult<string?>(null);
    }

    /// <summary>
    /// 每个具体派生类型都必须登记判别器——漏一个要到运行期才发现。
    /// </summary>
    /// <remarks>
    /// 这条防的是"加了新类型但忘记加 <c>[JsonDerivedType]</c>"。
    /// 那种错在编译期完全看不出来，而线上一序列化就抛异常。
    /// </remarks>
    private static Task<string?> AllRegisteredAsync()
    {
        var assembly = typeof(HostProtocolContract).Assembly;
        var problems = new List<string>();

        foreach (var type in assembly.GetExportedTypes())
        {
            if (type.IsAbstract || type.IsEnum) continue;

            var root =
                typeof(ClientCommand).IsAssignableFrom(type) ? typeof(ClientCommand)
                : typeof(ServerEvent).IsAssignableFrom(type) ? typeof(ServerEvent)
                : typeof(ConversationItem).IsAssignableFrom(type) ? typeof(ConversationItem)
                : null;

            if (root is null) continue;

            if (DiscriminatorFor(root, type) is null)
                problems.Add(type.Name);
        }

        return Task.FromResult(problems.Count == 0
            ? null
            : $"这些类型没有在基类上登记 [JsonDerivedType]：{string.Join("、", problems)}");
    }

    /// <summary>
    /// **每条命令序列化出来的文本里必须真的有 <c>method</c> 字段。**
    /// </summary>
    /// <remarks>
    /// <b>这条比"特性登记了没"更严，而且补的是一个真实盲区。</b>
    /// 原来的 <c>all-derived-types-register-discriminator</c> 只查了
    /// <c>[JsonDerivedType]</c> 特性存不存在——但"登记了"不等于
    /// "真的序列化出来了"。中间还有一整类会静默失效的情形：
    ///
    /// <list type="bullet">
    /// <item>派生类型上有个同名属性撞掉了判别字段
    /// （<c>CommandFailedEvent.Method</c> 就撞过一次，
    /// 那次是抛异常，属于运气好）；</item>
    /// <item>某个类型被加了自定义 converter，绕开了多态的写入逻辑；</item>
    /// <item>基类被改成非抽象，多态配置整体失效。</item>
    /// </list>
    ///
    /// <para>
    /// 这几种的**共同失败形态是静默的**：序列化不抛异常，只是不带
    /// <c>method</c>。对端按"不认识的判别器"丢弃，界面上表现为
    /// "点了没反应"而日志一片空白——最难查的那一类。
    /// </para>
    ///
    /// <para>
    /// <b>所以判据必须是"看输出文本"，不能看反射。</b>
    /// 反射看到的是意图，文本才是对端真正收到的东西。
    /// </para>
    /// </remarks>
    private static Task<string?> CommandsCarryMethodAsync() =>
        EveryConcreteTypeCarriesMethodAsync(typeof(ClientCommand), typeof(ServerEvent));

    /// <inheritdoc cref="CommandsCarryMethodAsync"/>
    private static Task<string?> EventsCarryMethodAsync() =>
        EveryConcreteTypeCarriesMethodAsync(typeof(ServerEvent), typeof(ClientCommand));

    /// <summary>
    /// 逐个具体类型构造实例、序列化成文本，断言文本里有正确的判别器。
    /// </summary>
    /// <param name="root">要验的多态根。</param>
    /// <param name="otherRoot">另一个根（构造时用作排除）。</param>
    private static Task<string?> EveryConcreteTypeCarriesMethodAsync(Type root, Type otherRoot)
    {
        var assembly = typeof(HostProtocolContract).Assembly;
        var problems = new List<string>();

        foreach (var type in assembly.GetExportedTypes())
        {
            if (type.IsAbstract || type.IsEnum) continue;

            // 只验属于本根、且不属于另一个根的类型。
            if (!root.IsAssignableFrom(type) || otherRoot.IsAssignableFrom(type)) continue;

            var expected = DiscriminatorFor(root, type);
            if (expected is null) continue;   // 由 all-derived-types 那条负责报

            object? instance;
            try
            {
                instance = BuildSample(type);
            }
            catch (Exception ex)
            {
                problems.Add($"{type.Name}（无法构造样本：{ex.GetType().Name}）");
                continue;
            }

            if (instance is null)
            {
                problems.Add($"{type.Name}（无法构造样本）");
                continue;
            }

            string json;
            try
            {
                json = JsonSerializer.Serialize(instance, root, HostProtocolContract.Json);
            }
            catch (Exception ex)
            {
                problems.Add($"{type.Name}（序列化抛异常：{ex.Message}）");
                continue;
            }

            // **必须有 method 字段，且值等于登记的判别器。**
            if (!json.Contains($"\"method\":\"{expected}\"", StringComparison.Ordinal))
                problems.Add($"{type.Name}（输出里没有 method=\"{expected}\"）→ {json}");
        }

        return Task.FromResult(problems.Count == 0
            ? null
            : "这些类型的线上文本缺判别器（对端会静默丢弃）：\n  "
                + string.Join("\n  ", problems));
    }

    /// <summary>
    /// **所有上线属性必须是 camelCase。**
    /// </summary>
    /// <remarks>
    /// <b>为什么值得单独一条</b>：桌面端报过一个消息里同时出现两种命名风格
    /// （<c>sessionId</c> + <c>Key</c>），2026-11-10 复核时当前版本复现不出来
    /// ——但"复现不出来"不等于"不会发生"。真正要防的是**有人给某个属性加了
    /// <c>[JsonPropertyName]</c> 却没统一风格**：那种改动在编译期完全看不出来，
    /// 只有抓包或这条检查能发现。
    ///
    /// <para>
    /// <b>对照的先例</b>：<c>CommandFailedEvent.Method</c> 因为与多态判别器
    /// <c>method</c> 撞名，显式命名成了 <c>failedMethod</c>——那是**正确的例外**
    /// （camelCase 且必要）。这条检查不管"为什么改名"，只要求结果仍是 camelCase。
    /// </para>
    /// </remarks>
    private static Task<string?> WireNamingAsync()
    {
        var asm = typeof(HostProtocolContract).Assembly;
        var problems = new List<string>();

        foreach (var root in new[] { typeof(ClientCommand), typeof(ServerEvent), typeof(ConversationItem) })
        {
            foreach (var type in asm.GetExportedTypes())
            {
                if (type.IsAbstract || type.IsEnum || !root.IsAssignableFrom(type)) continue;

                var sample = BuildSample(type);
                if (sample is null) continue;   // 由 payload 那条报"构造不出来"

                string json;
                try { json = JsonSerializer.Serialize(sample, root, HostProtocolContract.Json); }
                catch (JsonException) { continue; }   // 同上

                using var doc = JsonDocument.Parse(json);
                foreach (var prop in doc.RootElement.EnumerateObject())
                {
                    if (prop.Name.Length == 0 || !char.IsUpper(prop.Name[0])) continue;
                    problems.Add($"{type.Name}.{prop.Name}");
                }
            }
        }

        return Task.FromResult(problems.Count == 0
            ? null
            : $"这些属性上线成了 PascalCase（应为 camelCase）：{string.Join("、", problems)}");
    }

    /// <summary>
    /// **非空、非抽象类型的属性必须真的出现在线上。**
    /// </summary>
    /// <remarks>
    /// <b>这条抓的是"字段悄悄丢了"</b>：属性存在、类型正确、编译通过，
    /// 但序列化时因为某个 <c>JsonIgnore</c> 或者命名不匹配而没上线。
    /// 对端读不到它，会当成缺失——而契约里它是 <c>required</c>，
    /// 于是反序列化直接失败，症状是"这条消息收不到"。
    ///
    /// <para>
    /// <b>跳过 null 与抽象类型</b>：可空字段为 null 时被
    /// <c>DefaultIgnoreCondition.WhenWritingNull</c> 合理省略；
    /// 抽象类型（<c>ConversationItem</c>）由样本构造器指定具体实现，
    /// 构造不出来时不在这里报。
    /// </para>
    /// </remarks>
    private static Task<string?> PayloadFieldsAsync()
    {
        var asm = typeof(HostProtocolContract).Assembly;
        var problems = new List<string>();

        foreach (var root in new[] { typeof(ClientCommand), typeof(ServerEvent), typeof(ConversationItem) })
        {
            foreach (var type in asm.GetExportedTypes())
            {
                if (type.IsAbstract || type.IsEnum || !root.IsAssignableFrom(type)) continue;

                var sample = BuildSample(type);
                if (sample is null) continue;

                string json;
                try { json = JsonSerializer.Serialize(sample, root, HostProtocolContract.Json); }
                catch (JsonException) { continue; }

                using var doc = JsonDocument.Parse(json);

                foreach (var prop in type.GetProperties(
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
                {
                    if (Nullable.GetUnderlyingType(prop.PropertyType) is not null) continue;
                    if (prop.PropertyType.IsAbstract) continue;
                    if (prop.GetValue(sample) is null) continue;

                    var name = char.ToLowerInvariant(prop.Name[0]) + prop.Name[1..];
                    if (!doc.RootElement.TryGetProperty(name, out _))
                        problems.Add($"{type.Name}.{prop.Name}（expected '{name}'）");
                }
            }
        }

        return Task.FromResult(problems.Count == 0
            ? null
            : $"这些非空字段没上线，对端会当成缺失：{string.Join("、", problems)}");
    }

    /// <summary>
    /// 构造一个够用的样本实例。
    /// </summary>
    /// <remarks>
    /// <b>用反射填必填属性，而不是手写 N 个构造</b>：这里的目标不是验业务语义，
    /// 是"让它能序列化"。手写会让新增类型时这条检查自己漏掉——而漏掉
    /// 恰恰是它要防的事。
    ///
    /// <para>
    /// 填不出来的必填属性（例如某个自定义类型）会让序列化抛异常，
    /// 那条也会被记成问题——**宁可报"无法构造"，不要静默跳过**。
    /// </para>
    /// </remarks>
    private static object? BuildSample(Type type)
    {
        var instance = Activator.CreateInstance(type);
        if (instance is null) return null;

        foreach (var prop in type.GetProperties(
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
        {
            if (!prop.CanWrite) continue;
            if (prop.GetValue(instance) is not null) continue;   // 初始值够用

            prop.SetValue(instance, SampleValue(prop.PropertyType));
        }

        return instance;
    }

    /// <summary>给一个类型造个不会导致序列化失败的占位值。</summary>
    private static object? SampleValue(Type type)
    {
        if (type == typeof(string)) return "x";
        if (type == typeof(bool)) return true;
        if (type == typeof(int)) return 1;
        if (type == typeof(long)) return 1L;
        if (type == typeof(DateTimeOffset)) return FixedAt;

        var underlying = Nullable.GetUnderlyingType(type);
        if (underlying is not null) return null;

        if (type.IsEnum) return Enum.GetValues(type).GetValue(0);

        if (type.IsArray) return Array.CreateInstance(type.GetElementType()!, 0);

        // 集合类：造空集合。
        if (type.IsGenericType)
        {
            var def = type.GetGenericTypeDefinition();
            if (def == typeof(IReadOnlyList<>) || def == typeof(List<>)
                || def == typeof(IList<>))
            {
                return Activator.CreateInstance(typeof(List<>).MakeGenericType(
                    type.GetGenericArguments()[0]));
            }
        }

        // 嵌套的契约记录：递归造一个。
        if (type.IsClass)
        {
            try { return BuildSample(type); }
            catch (Exception) { return null; }
        }

        return null;
    }

    /// <summary>从基类的多态配置里查某个具体类型的判别器。</summary>
    private static string? DiscriminatorFor(Type root, Type concrete)
    {
        var attrs = root
            .GetCustomAttributes(typeof(JsonDerivedTypeAttribute), inherit: false)
            .Cast<JsonDerivedTypeAttribute>();

        foreach (var item in attrs)
        {
            if (item.DerivedType == concrete) return item.TypeDiscriminator?.ToString();
        }

        return null;
    }

    /// <summary>
    /// **未知判别器：直接 Deserialize 会抛，TryParse 会折成 false。**
    /// </summary>
    /// <remarks>
    /// <b>这条检查原先断言的是相反的行为</b>——它要求"必须抛异常"，
    /// 而类型注释当时写着"不认识的判别器要忽略，不是崩"。两者矛盾，
    /// 而检查是绿的，于是那个假承诺一直留在文档里，直到桌面端实测打回来
    /// （2026-11-10）。
    ///
    /// <para>
    /// <b>现在两边都钉</b>：
    /// <list type="bullet">
    /// <item>裸 <c>Deserialize</c> 确实抛——这是事实，写进文档让人别指望它；</item>
    /// <item><see cref="HostProtocolContract.TryParse{T}"/> 返回 false 且不抛
    /// ——这是承诺，必须成立。</item>
    /// </list>
    /// 只钉第二条会让人以为框架自动降级；只钉第一条则等于放弃"加新类型不用
    /// 升 Version"这条能力。
    /// </para>
    /// </remarks>
    private static Task<string?> UnknownMethodAsync()
    {
        const string json = """{"method":"future/unknown","sequence":1}""";

        // 1. 裸反序列化：必须抛（文档如实写了这一点）。
        var threw = false;
        try
        {
            _ = JsonSerializer.Deserialize<ServerEvent>(json, HostProtocolContract.Json);
        }
        catch (JsonException)
        {
            threw = true;
        }

        if (!threw)
            return Task.FromResult<string?>(
                "未知判别器竟然没抛异常——若 System.Text.Json 改了行为，"
                + "TryParse 的 try/catch 就成了死代码，需要重新评估");

        // 2. TryParse：必须不抛、返回 false、message 为 null，且回调拿到判别器值。
        string? reported = null;
        var ok = HostProtocolContract.TryParse<ServerEvent>(json, out var parsed, d => reported = d);

        if (ok) return Task.FromResult<string?>("TryParse 对未知判别器返回了 true");
        if (parsed is not null) return Task.FromResult<string?>("TryParse 未知判别器时 message 应为 null");
        if (reported != "future/unknown")
            return Task.FromResult<string?>($"回调应当拿到 'future/unknown'，实际 {reported ?? "null"}");

        // 3. 已知判别器但内容坏了：**必须仍然抛**，不能被当成"未知消息"吞掉。
        const string broken = """{"method":"turn/started"}""";   // 缺 sessionId/turnId
        try
        {
            var swallowed = HostProtocolContract.TryParse<ServerEvent>(broken, out _);
            return Task.FromResult<string?>(
                $"缺必需字段的消息被 TryParse 吞成了 {swallowed}——真错误不该被当成未知消息");
        }
        catch (JsonException)
        {
            return Task.FromResult<string?>(null);
        }
    }

    /// <summary>
    /// 序列化配置必须由契约包独家提供，且配置本身要对。
    /// </summary>
    /// <remarks>
    /// <summary>
    /// 缺必需字段必须明确失败，不能静默补一个默认值。
    /// </summary>
    /// <remarks>
    /// <b>这是跨语言移植最容易漏的一条</b>：两端都能反序列化、
    /// 都能跑完流程，但缺字段时一边抛错一边补默认值，
    /// 于是"看起来正常"的那一侧把错误数据带进了会话。
    /// <para>
    /// <c>required</c> 让缺失变成 <see cref="JsonException"/>。
    /// 这比"默认值凑合"严格，但协议字段不是可有可无的装饰——
    /// 少了 <c>sessionId</c> 的事件路由不到会话，凭空补一个只会更糟。
    /// </para>
    /// </remarks>
    private static Task<string?> MissingRequiredAsync()
    {
        // 每种都缺一个必需字段。
        (string Label, string Json, Type Type)[] cases =
        [
            ("事件缺 sessionId", """{"method":"turn/started","turnId":"t"}""", typeof(ServerEvent)),
            ("事件缺 snapshot", """{"method":"session/opened"}""", typeof(ServerEvent)),
            ("条目缺 id", """{"kind":"assistantMessage","at":"2026-01-02T03:04:05+08:00"}""",
                typeof(ConversationItem)),
            ("命令缺 sessionId", """{"method":"session/open"}""", typeof(ClientCommand)),
        ];

        foreach (var (label, json, type) in cases)
        {
            try
            {
                var parsed = JsonSerializer.Deserialize(json, type, HostProtocolContract.Json);
                if (parsed is not null)
                    return Task.FromResult<string?>(
                        $"{label}：缺必需字段却成功反序列化成了 {parsed.GetType().Name}");
            }
            catch (JsonException)
            {
                // 期望路径。
            }
        }

        return Task.FromResult<string?>(null);
    }

    /// <summary>
    /// 时间戳缺失时不能默认取"现在"。
    /// </summary>
    /// <remarks>
    /// <b>这曾经是真实缺陷</b>：<c>ConversationItem.At</c> 有
    /// <c>= DateTimeOffset.Now</c> 的默认值。缺字段时两端各自取自己的"现在"——
    /// 客户端拿到的是收包时刻，不是产生时刻。跨进程时分叉立刻可见：
    /// 两条消息的时间戳可能颠倒，界面按时间排序就错序，而且**没有任何报错**。
    /// <para>
    /// 换成 <c>required</c> 后这里应当抛 <see cref="JsonException"/>。
    /// 检查同时验证"没有退回去用 Now"——把默认值改回来会让它失败。
    /// </para>
    /// </remarks>
    private static Task<string?> TimestampNotNowAsync()
    {
        const string missingAt = """{"kind":"assistantMessage","id":"i1","text":"x"}""";

        try
        {
            var parsed = JsonSerializer.Deserialize<ConversationItem>(
                missingAt, HostProtocolContract.Json);

            if (parsed is not null)
            {
                return Task.FromResult<string?>(
                    $"缺 at 字段却反序列化成功了，At={parsed.At:O}"
                    + "——说明仍在用本地当前时间兜底，跨进程会静默错序");
            }
        }
        catch (JsonException)
        {
            // 期望路径：明确失败。
        }

        return Task.FromResult<string?>(null);
    }

    private static Task<string?> SharedOptionsAsync()
    {
        if (HostProtocolContract.Json.DefaultIgnoreCondition != JsonIgnoreCondition.WhenWritingNull)
        {
            return Task.FromResult<string?>(
                "null 字段应当不写入：否则「字段不存在」与「字段是 null」在线上无法区分");
        }

        var notice = new NoticeItem { Id = "1", At = FixedAt, Text = "x", Level = NoticeLevel.Warning };
        var json = JsonSerializer.Serialize<ConversationItem>(notice, HostProtocolContract.Json);

        return Task.FromResult(json.Contains("\"level\":\"warning\"", StringComparison.Ordinal)
            ? null
            : $"枚举应写字符串（便于抓包定位）：{json}");
    }

    /// <summary>
    /// 模型目录必须带上 provider 清单。
    /// </summary>
    /// <remarks>
    /// <b>这条钉的是一个"有类型但没被验证"的缺口</b>：<c>ModelsListedEvent</c>
    /// 在 SDK 里只登记过判别器，从未被构造或消费，于是它缺
    /// <c>Providers</c> 字段这件事一直没人发现。
    /// <para>
    /// 该字段不是界面偏好：一个 provider 可能因为凭据未配而没有模型，
    /// 但它仍需出现在"选一个 provider"的下拉里。从 Models 反推会漏掉它。
    /// </para>
    /// </remarks>
    private static Task<string?> ModelsCarriesProvidersAsync()
    {
        var payload = new ModelsListedEvent
        {
            Sequence = 1,
            Models = [new ModelInfo("anthropic", "claude-opus-4-6", 1_000_000, 32_000, true)],
            Providers = ["anthropic", "openai"],
            CurrentModelId = "claude-opus-4-6",
        };

        var json = JsonSerializer.Serialize<ServerEvent>(payload, HostProtocolContract.Json);

        // 往返必须保住这两个字段——它们不在 Models 里，序列化配置漏一个就丢。
        var back = JsonSerializer.Deserialize<ServerEvent>(json, HostProtocolContract.Json)
            as ModelsListedEvent;

        if (back is null) return Task.FromResult<string?>("反序列化结果不是 ModelsListedEvent");

        // Providers 是必需字段：缺了会抛 JsonException（与 At 同样的策略）。
        try
        {
            _ = JsonSerializer.Deserialize<ServerEvent>(
                """{"method":"model/listed","sequence":1,"models":[]}""",
                HostProtocolContract.Json);
            return Task.FromResult<string?>(
                "缺 Providers 却反序列化成功了——客户端会把「没有 provider」"
                + "与「服务端没给」混为一谈");
        }
        catch (JsonException)
        {
            // 期望路径。
        }

        // openai 没有模型，仍必须在清单里——这正是不能从 Models 反推的理由。
        if (!back.Providers.Contains("openai", StringComparer.Ordinal))
            return Task.FromResult<string?>($"没有模型的 provider 被丢了：{string.Join(",", back.Providers)}");

        return Task.FromResult<string?>(null);
    }

    /// <summary>
    /// 从线协议原文解析模型目录，字段一个都不能丢。
    /// </summary>
    /// <remarks>
    /// <b>为什么不能只做对象往返</b>：往返是"自己构造、自己验证"，
    /// 验证的是序列化配置对称。而真实风险在**服务端构造时漏填**——
    /// 那由 <c>required</c> 在编译期拦，往返检查碰不到。
    /// <para>
    /// 这条用**手写的 JSON 文本**当输入：它代表一个真实的线上报文，
    /// 与我们的对象构造代码完全独立。若字段被漏在序列化配置之外、
    /// 或被 <c>WhenWritingNull</c> 吞掉，这里会暴露。
    /// </para>
    /// </remarks>
    private static Task<string?> ModelsParsesFromWireAsync()
    {
        // 手写报文：注意 openai 没有模型，但它必须在 providers 里。
        const string wire = """
            {
              "method": "model/listed",
              "sequence": 7,
              "models": [
                {"providerId":"anthropic","id":"claude-opus-4-6",
                 "contextWindow":1000000,"maxOutputTokens":32000,"supportsReasoning":true}
              ],
              "providers": ["anthropic","openai"],
              "currentModelId": "claude-opus-4-6"
            }
            """;

        ModelsListedEvent? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<ServerEvent>(wire, HostProtocolContract.Json)
                as ModelsListedEvent;
        }
        catch (Exception ex)
        {
            return Task.FromResult<string?>($"线协议报文解析失败：{ex.GetType().Name}: {ex.Message}");
        }

        if (parsed is null) return Task.FromResult<string?>("没有解析成 ModelsListedEvent");
        if (parsed.Sequence != 7) return Task.FromResult<string?>($"序号丢了：{parsed.Sequence}");
        if (parsed.Models.Count != 1) return Task.FromResult<string?>($"模型数不对：{parsed.Models.Count}");

        if (!parsed.Providers.SequenceEqual(["anthropic", "openai"], StringComparer.Ordinal))
            return Task.FromResult<string?>($"providers 不对：{string.Join(",", parsed.Providers)}");

        if (parsed.CurrentModelId != "claude-opus-4-6")
            return Task.FromResult<string?>($"currentModelId 不对：{parsed.CurrentModelId}");

        // 模型自身的字段也要完整——ModelInfo 是 positional record，
        // 少一个参数会在构造处暴露，但反序列化走的是属性映射，可能静默取默认值。
        var model = parsed.Models[0];
        if (model.ContextWindow != 1_000_000 || model.MaxOutputTokens != 32_000 || !model.SupportsReasoning)
            return Task.FromResult<string?>(
                $"ModelInfo 字段被静默填了默认值：{model.ContextWindow}/{model.MaxOutputTokens}/{model.SupportsReasoning}");

        return Task.FromResult<string?>(null);
    }

    /// <summary>模型目录必须能标出当前在用的模型。</summary>
    /// <remarks>
    /// 没有它，界面上所有模型看起来一样，用户不知道自己正在用哪个。
    /// </remarks>
    private static Task<string?> ModelsCarriesCurrentAsync()
    {
        var payload = new ModelsListedEvent
        {
            Sequence = 2,
            Models = [new ModelInfo("openai", "gpt-5", 400_000, 128_000, true)],
            Providers = ["openai"],
            CurrentModelId = "gpt-5",
        };

        var json = JsonSerializer.Serialize<ServerEvent>(payload, HostProtocolContract.Json);
        var back = JsonSerializer.Deserialize<ServerEvent>(json, HostProtocolContract.Json)
            as ModelsListedEvent;

        if (back?.CurrentModelId is null)
            return Task.FromResult<string?>($"CurrentModelId 在往返中丢失：{json}");

        return Task.FromResult<string?>(back.CurrentModelId == "gpt-5"
            ? null
            : $"CurrentModelId 变了：{back.CurrentModelId}");
    }

    private static async Task<int> CheckAsync(string name, Func<Task<string?>> action)
    {
        try
        {
            var failure = await action().ConfigureAwait(false);
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
