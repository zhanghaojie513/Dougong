using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dougong.Agent;
using Dougong.HostProtocol;

namespace Dougong.Hosting;

/// <summary>
/// 宿主协议的参考服务端：从输入流读 <see cref="ClientCommand"/>，
/// 向输出流写 <see cref="ServerEvent"/>。**按行分帧的 JSON**。
/// </summary>
/// <remarks>
/// <b>⚠️ 定位：最小参考实现，不是给宿主复用的框架。不要继承、不要指望扩展点。</b>
///
/// <para>
/// <b>它封闭是有意的，而且封闭在两层</b>：类本身 <c>sealed</c>，
/// 分发点 <see cref="ExecuteAsync"/> 是私有的 <c>switch</c>。
/// 即使去掉 <c>sealed</c> 也没用——分发逻辑不认识宿主的命令，
/// 那些命令会一律落到 <see cref="CommandFailureReason.Unsupported"/>。
/// </para>
///
/// <para>
/// <b>宿主有自己的协议命令时，正确做法是保留自己的服务端循环</b>：
/// 只复用本包的两样东西——<c>Dougong.HostProtocol</c> 的契约类型，
/// 以及 <see cref="ProtocolMapping"/> 的映射。本类的价值是
/// "一个能跑通 5 条命令的最小完整例子"，照着抄即可。
/// </para>
///
/// <para>
/// <b>为什么不提供扩展点</b>：加一个命令注册点，就要同时回答
/// "宿主命令怎么和内置命令共享状态（会话表、事件序号、订阅）"——
/// 那是设计问题，不是一个 <c>delegate</c> 能解决的。而当前只有一个宿主
/// 提出该需求，现在抽等于为单个场景设计框架。（2026-11-10 桌面端实测后
/// 确认：他们有 744 行的 IpcServer，承载 <c>fs/*</c>、<c>models/*</c> 等
/// 自家命令，用不上本类。）
/// </para>
///
/// <para>
/// <b>它不绑定任何具体宿主</b>：传输是裸 <see cref="Stream"/>（stdio、socket、
/// 进程内管道都能喂）；会话能力由 <see cref="IHostSessionProvider"/> 注入；
/// 本文件只依赖 Agent（引擎接口）与 HostProtocol（消息形状），不碰 Cli / Acp。
/// </para>
///
/// <para>
/// <b>只实现能跑通的能力，其余明确回 <see cref="CommandFailureReason.Unsupported"/></b>：
/// 照 <c>AcpAgentHost</c> 的判据——"声明不支持"远好于"实现了但走不通"。
/// 静默不响应是最坏的，客户端只能等到超时，用户永远看不到原因。
/// </para>
///
/// <para>
/// <b>stdout 归协议</b>：任何诊断必须走构造参数里传入的日志出口
/// （默认 stderr）。混进协议流会直接破坏分帧——这是这类服务端最容易犯的错。
/// </para>
/// </remarks>
public sealed class HostProtocolServer : IAsyncDisposable
{
    /// <summary>单行消息上限。超过即判定连接损坏，不继续累积内存。</summary>
    public const int MaxLineBytes = 64 * 1024 * 1024;

    /// <summary>连续多少行无法解析后判定连接损坏。</summary>
    public const int MaxConsecutiveUnparsableLines = 100;

    private readonly Stream _input;
    private readonly Stream _output;
    private readonly IHostSessionProvider _provider;
    private readonly TextWriter _log;

    /// <summary>
    /// 写输出时的互斥。
    /// </summary>
    /// <remarks>
    /// <b>必须有</b>：命令响应由主循环写，而推送事件（子 agent 更新、审批请求）
    /// 可能来自后台任务。两条写路径交错会把两帧拼成一行，
    /// 对端解析直接失败——而症状看起来像"随机丢消息"。
    /// </remarks>
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    private readonly CancellationTokenSource _lifetime = new();

    /// <summary>
    /// 已订阅的注册表 → 会话标识。**登记过就不再重复订阅**。
    /// </summary>
    /// <remarks>
    /// <b>为什么要记这个表</b>：<see cref="IHostSessionProvider.Subagents"/> 是按会话
    /// 查询的，服务端不知道总共有哪些会话。第一次见到某个注册表时订阅它，
    /// 之后再见到就跳过——否则同一个注册表会被订阅 N 次，
    /// 一次子 agent 完成推出 N 条重复事件。
    ///
    /// <para>
    /// <b>用引用相等而不是会话 id 去重</b>：同一个 id 在不同时刻可能指向不同的
    /// 注册表（会话重建），而同一个注册表也可能被多个 id 引用。
    /// 去重的目标对象是"注册表"，就该按它本身比。
    /// </para>
    /// </remarks>
    private readonly Dictionary<SubagentRegistry, string> _subscribed = new(ReferenceEqualityComparer.Instance);

    /// <summary>
    /// 订阅了权限变更广播的作用域（会话 id；空串表示全局）。
    /// </summary>
    /// <remarks>
    /// <b>默认是空的</b>：单窗口客户端不该收到这类广播。
    /// 只有显式发过 <c>permission/watch</c> 的作用域才进来。
    /// </remarks>
    private readonly HashSet<string> _permissionWatchers = new(StringComparer.Ordinal);

    /// <summary>事件序号。单连接内严格单调。</summary>
    private long _sequence;

    /// <param name="input">协议输入流。</param>
    /// <param name="output">协议输出流。<b>只能有协议帧。</b></param>
    /// <param name="provider">会话能力提供方。</param>
    /// <param name="log">诊断出口。**必须是 stderr**。</param>
    public HostProtocolServer(
        Stream input,
        Stream output,
        IHostSessionProvider provider,
        TextWriter? log = null)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(provider);

        _input = input;
        _output = output;
        _provider = provider;
        _log = log ?? Console.Error;
    }

    /// <summary>
    /// 逐条处理命令直到输入结束或取消。
    /// </summary>
    /// <remarks>
    /// <b>串行处理，不并发</b>：协议里命令之间有隐含顺序（先 <c>session/open</c>
    /// 才能 <c>turn/start</c>）。并发处理会让"还没建的会话"被同时引用，
    /// 而错误只有在特定时序下才出现。
    ///
    /// <para>
    /// <b>单条命令出错不能杀死循环</b>：把异常转成
    /// <see cref="CommandFailedEvent"/> 回给客户端，连接继续可用。
    /// 连接级致命错误（分帧损坏、连续无法解析）才终止。
    /// </para>
    /// </remarks>
    /// <param name="cancellationToken">取消令牌。</param>
    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, _lifetime.Token);

        var consecutiveUnparsable = 0;

        try
        {
            await foreach (var node in ReadLinesAsync(linked.Token).ConfigureAwait(false))
            {
                if (node is null)
                {
                    consecutiveUnparsable++;
                    if (consecutiveUnparsable >= MaxConsecutiveUnparsableLines)
                    {
                        _log.WriteLine(
                            $"[protocol] 连续 {consecutiveUnparsable} 行无法解析，判定连接损坏。");
                        return;
                    }

                    continue;
                }

                consecutiveUnparsable = 0;

                await DispatchAsync(node, linked.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested)
        {
            // 正常退出路径。
        }
    }

    /// <summary>
    /// 处理一条命令。
    /// </summary>
    /// <remarks>
    /// <b>先取 <c>RequestId</c> 再反序列化</b>：反序列化可能因为缺字段失败，
    /// 而那正是最需要回一条失败事件的时刻——没有 <c>RequestId</c> 就配对不上，
    /// 客户端只能超时。所以失败路径必须能拿到它。
    /// </remarks>
    private async Task DispatchAsync(JsonNode node, CancellationToken cancellationToken)
    {
        // 先尽力取 RequestId：失败响应靠它与命令配对。
        var requestId = TryReadString(node, "RequestId") ?? TryReadString(node, "requestId");
        var method = TryReadString(node, "method");

        ClientCommand? command;
        try
        {
            command = node.Deserialize<ClientCommand>(HostProtocolContract.Json);
        }
        catch (JsonException ex)
        {
            await SendAsync(new CommandFailedEvent
            {
                Sequence = NextSequence(),
                RequestId = requestId,
                Reason = CommandFailureReason.Invalid,
                Message = $"命令无法解析：{ex.Message}",
                Method = method,
            }, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (command is null)
        {
            await SendAsync(new CommandFailedEvent
            {
                Sequence = NextSequence(),
                RequestId = requestId,
                Reason = CommandFailureReason.Invalid,
                Message = "命令为空或不认识它的判别器。",
                Method = method,
            }, cancellationToken).ConfigureAwait(false);
            return;
        }

        try
        {
            await ExecuteAsync(command, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // 单条命令失败不终止连接：把原因回给客户端，它自己能决定怎么办。
            _log.WriteLine($"[protocol] 命令 {command.GetType().Name} 失败：{ex}");

            await SendAsync(new CommandFailedEvent
            {
                Sequence = NextSequence(),
                RequestId = command.RequestId,
                Reason = CommandFailureReason.Failed,
                Message = $"{ex.GetType().Name}: {ex.Message}",
                Method = MethodOf(command),
            }, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// 真正执行一条命令。
    /// </summary>
    /// <remarks>
    /// <b>首版的分工很明确</b>：C5（权限预设）与 C8（子 agent）完整实现，
    /// 其余一律 <see cref="CommandFailureReason.Unsupported"/>。
    /// 每多实现一条，都在 <c>host-protocol-server</c> 套里加一条端到端检查。
    /// </remarks>
    private async Task ExecuteAsync(ClientCommand command, CancellationToken cancellationToken)
    {
        switch (command)
        {
            // ── C5 权限预设 ──────────────────────────────
            case ListPermissionPresetsCommand list:
                await RespondPermission(list.RequestId, list.SessionId, cancellationToken)
                    .ConfigureAwait(false);
                return;

            case SwitchPermissionPresetCommand switchCmd:
                await SwitchPermissionAsync(switchCmd, cancellationToken).ConfigureAwait(false);
                return;

            // 可选广播：单窗口不需要，Dock 浮动窗那种多界面场景才订阅。
            case WatchPermissionPresetsCommand watch:
                await WatchPermissionAsync(watch, cancellationToken).ConfigureAwait(false);
                return;

            // ── C8 子 agent ──────────────────────────────
            case ListSubagentsCommand listSub:
                await RespondSubagentsAsync(
                    listSub.RequestId, listSub.SessionId, take: null, cancellationToken)
                    .ConfigureAwait(false);
                return;

            case TakeSubagentResultCommand take:
                await RespondSubagentsAsync(
                    take.RequestId, take.SessionId, take, cancellationToken)
                    .ConfigureAwait(false);
                return;

            case CancelSubagentCommand cancel:
                await CancelSubagentAsync(cancel, cancellationToken).ConfigureAwait(false);
                return;

            // ── 其余：明确不支持 ─────────────────────────
            default:
                await SendAsync(new CommandFailedEvent
                {
                    Sequence = NextSequence(),
                    RequestId = command.RequestId,
                    Reason = CommandFailureReason.Unsupported,
                    Message = $"这个服务端版本不支持 {MethodOf(command)}。"
                        + "当前支持：permission/presets、permission/switch、"
                        + "subagent/list、subagent/result、subagent/cancel。",
                    Method = MethodOf(command),
                }, cancellationToken).ConfigureAwait(false);
                return;
        }
    }

    /// <summary>回应权限预设查询。</summary>
    private async Task RespondPermission(
        string requestId, string? sessionId, CancellationToken cancellationToken)
    {
        var presets = _provider.Permissions(sessionId);
        if (presets is null)
        {
            await SendAsync(new CommandFailedEvent
            {
                Sequence = NextSequence(),
                RequestId = requestId,
                Reason = CommandFailureReason.Conflict,
                Message = "还没有可用的权限预设（会话未建立或提供方未启用该能力）。",
                Method = "permission/presets",
            }, cancellationToken).ConfigureAwait(false);
            return;
        }

        await SendAsync(new PermissionPresetListedEvent
        {
            Sequence = NextSequence(),
            RequestId = requestId,
            SessionId = sessionId,
            State = presets.ToProtocol(),
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 订阅/退订权限预设变更广播。
    /// </summary>
    /// <remarks>
    /// <b>默认不广播</b>（见 <see cref="WatchPermissionPresetsCommand"/> 的注释）：
    /// 单窗口自己发起切换、自己收响应就够了。这条命令只服务多界面场景。
    ///
    /// <para>
    /// 幂等：重复订阅不报错、也不会产生重复广播。
    /// </para>
    /// </remarks>
    private async Task WatchPermissionAsync(
        WatchPermissionPresetsCommand command, CancellationToken cancellationToken)
    {
        // 会话键：null 表示全局默认。用同一个键去重，避免"全局"与"某会话"
        // 混在一起——两个作用域的广播不该互相干扰。
        var key = command.SessionId ?? string.Empty;

        lock (_permissionWatchers)
        {
            if (command.Enabled) _permissionWatchers.Add(key);
            else _permissionWatchers.Remove(key);
        }

        // **回当前状态**，让调用方立刻有东西可显示；之后才靠广播更新。
        // 只回一个"订阅成功"会让客户端多一次往返才能渲染选择器。
        var presets = _provider.Permissions(command.SessionId);
        if (presets is null)
        {
            await SendAsync(new CommandFailedEvent
            {
                Sequence = NextSequence(),
                RequestId = command.RequestId,
                Reason = CommandFailureReason.Conflict,
                Message = "还没有可用的权限预设。",
                Method = command.Method,
            }, cancellationToken).ConfigureAwait(false);
            return;
        }

        await SendAsync(new PermissionPresetListedEvent
        {
            Sequence = NextSequence(),
            RequestId = command.RequestId,
            SessionId = command.SessionId,
            State = presets.ToProtocol(),
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 如果该作用域有订阅者，广播一次权限变更。**没有订阅者时一个字节都不写。**
    /// </summary>
    private async Task BroadcastPermissionAsync(
        string? sessionId, PermissionPresetState state, CancellationToken cancellationToken)
    {
        var watched = false;
        lock (_permissionWatchers)
        {
            watched = _permissionWatchers.Contains(sessionId ?? string.Empty);
        }

        if (!watched) return;

        await PushAsync(new PermissionPresetListedEvent
        {
            Sequence = NextSequence(),
            SessionId = sessionId,
            State = state,
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>切换权限预设。未知键回 <see cref="CommandFailureReason.Invalid"/>。</summary>
    /// <remarks>
    /// <b>切换失败必须回失败事件，不能静默</b>：用户以为收紧了而实际没变，
    /// 在安全方向上是更坏的结果。
    /// </remarks>
    private async Task SwitchPermissionAsync(
        SwitchPermissionPresetCommand command, CancellationToken cancellationToken)
    {
        var presets = _provider.Permissions(command.SessionId);
        if (presets is null)
        {
            await SendAsync(new CommandFailedEvent
            {
                Sequence = NextSequence(),
                RequestId = command.RequestId,
                Reason = CommandFailureReason.Conflict,
                Message = "还没有可用的权限预设。",
                Method = "permission/switch",
            }, cancellationToken).ConfigureAwait(false);
            return;
        }

        try
        {
            await presets.SwitchAsync(command.Key, cancellationToken).ConfigureAwait(false);
        }
        catch (ArgumentException ex)
        {
            await SendAsync(new CommandFailedEvent
            {
                Sequence = NextSequence(),
                RequestId = command.RequestId,
                Reason = CommandFailureReason.Invalid,
                Message = ex.Message,
                Method = "permission/switch",
            }, cancellationToken).ConfigureAwait(false);
            return;
        }

        // 切换成功后回完整状态，客户端不必再发一次查询。
        var state = presets.ToProtocol();

        await SendAsync(new PermissionPresetListedEvent
        {
            Sequence = NextSequence(),
            RequestId = command.RequestId,
            SessionId = command.SessionId,
            State = state,
        }, cancellationToken).ConfigureAwait(false);

        // **再广播给其它界面**（只有订阅过的才收）。发起方已经收到上面那条
        // 带 RequestId 的响应；广播那条**不带** RequestId——
        // 它是对状态的更新，不是对某条命令的回答。
        await BroadcastPermissionAsync(command.SessionId, state, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// 订阅某个注册表的"子 agent 结束"通知。同一个注册表只订阅一次。
    /// </summary>
    /// <remarks>
    /// <b>为什么不在这里也遍历现有句柄</b>：<see cref="SubagentRegistry.Completed"/>
    /// 已经覆盖了"注册之后结束"的情形，而现有句柄若已经结束，客户端
    /// 刚才那次 <c>subagent/list</c> 已经看到了它的状态——
    /// 补推一条只会让客户端收到一个它已经知道的旧状态。
    /// </remarks>
    private void Subscribe(SubagentRegistry registry, string sessionId)
    {
        // **登记与订阅必须在同一把锁内。**
        // 分成两步（锁内登记、锁外 +=）时，两个并发命令——例如客户端同时发
        // subagent/list 与 subagent/cancel——会都通过 ContainsKey 检查，
        // 然后各自挂一次订阅：同一次完成推两条，而客户端按"收到即刷新"处理。
        lock (_subscribed)
        {
            if (_subscribed.ContainsKey(registry)) return;

            _subscribed[registry] = sessionId;

            // **两条都要挂**。只挂 Completed 的话，界面要等到子 agent
            // 跑完才知道它存在过——而那时它已经开始花额度了。
            //
            // 事件里的状态自己会说明是"刚开始"还是"结束了"
            // （`SubagentInfo.State`），客户端用一个处理器就能分清，
            // 不需要两个不同的消息类型。
            registry.Started += handle => _ = PushSubagentUpdateAsync(handle, sessionId);
            registry.Completed += handle => _ = PushSubagentUpdateAsync(handle, sessionId);
        }
    }

    /// <summary>
    /// 把子 agent 的状态变化推给客户端。
    /// </summary>
    /// <remarks>
    /// <b>这个方法在子 agent 的收尾线程上被调用，绝不能同步等待</b>：
    /// 写输出要拿锁、可能要等对端读，而子 agent 的收尾线程被卡住会让
    /// <c>Task.Run</c> 那条任务迟迟不结束——调用方以为它还在跑。
    /// 所以调用点用 <c>_ =</c> 丢弃任务，这里自己兜住所有异常。
    ///
    /// <para>
    /// <b>异常必须吞掉</b>：推送失败（对端已断开）不该反过来把
    /// 子 agent 的收尾搞坏。连接断了由读循环发现并退出。
    /// </para>
    /// </remarks>
    private async Task PushSubagentUpdateAsync(SubagentHandle handle, string sessionId)
    {
        try
        {
            await PushAsync(new SubagentUpdatedEvent
            {
                Sequence = NextSequence(),
                SessionId = sessionId,
                Agent = handle.ToProtocol(),
            }, _lifetime.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // 对端断开、服务端已释放：都正常。记一行诊断即可。
            _log.WriteLine($"[protocol] 推送子 agent 更新失败：{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>回子 agent 列表；<paramref name="take"/> 非 null 时附带取结果。</summary>
    private async Task RespondSubagentsAsync(
        string requestId, string sessionId,
        TakeSubagentResultCommand? take,
        CancellationToken cancellationToken)
    {
        var registry = _provider.Subagents(sessionId);
        if (registry is null)
        {
            await SendAsync(new CommandFailedEvent
            {
                Sequence = NextSequence(),
                RequestId = requestId,
                Reason = CommandFailureReason.Conflict,
                Message = "这个会话没有子 agent 注册表（提供方未启用该能力）。",
                Method = take is null ? "subagent/list" : "subagent/result",
            }, cancellationToken).ConfigureAwait(false);
            return;
        }

        // **每见到一个注册表就订阅它。** 客户端第一次问子 agent 之后，
        // 后续的子 agent 结束会主动推上来——不必再轮询。
        Subscribe(registry, sessionId);

        SubagentOutcome? outcome = null;
        string? error = null;

        if (take is not null)
        {
            var handle = registry.Find(take.SubagentId);

            if (handle is null)
            {
                // **必须报错，不能回空**：回空会让客户端以为"结果还没好"而
                // 一直轮询，而它永远不会好了——结果要么已被取走，要么 id 写错。
                // 这两种情况都只有报错能让用户看出问题。
                error = $"没有 id 为 '{take.SubagentId}' 的子 agent"
                    + "（结果只回一次：取走之后它就不在列表里了）。";
            }
            else if (handle.Result is { } result)
            {
                // **取走即消失**：与引擎的语义一致，不能让协议假装可以反复查。
                outcome = result.ToProtocol();
                await registry.UnregisterAsync(take.SubagentId, cancellationToken)
                    .ConfigureAwait(false);
                handle.Dispose();
            }
            // 还在跑：outcome 保持 null，列表里仍带着它（状态为 Running）。
        }

        var agents = registry.Active()
            .Where(h => take is null
                || !string.Equals(h.Id, take.SubagentId, StringComparison.Ordinal)
                || h.Result is null)   // 刚被取走的不再出现在列表里
            .Select(h => h.ToProtocol())
            .ToList();

        await SendAsync(new SubagentsListedEvent
        {
            Sequence = NextSequence(),
            RequestId = requestId,
            SessionId = sessionId,
            Agents = agents,
            Outcome = outcome,
            Error = error,
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>停止一个子 agent。**只是请求**——它会跑完当前那次工具调用。</summary>
    /// <remarks>
    /// <b>响应里要如实说"只是请求"</b>：点下去立刻显示"已停止"是撒谎，
    /// 它可能还要跑几秒。这里回列表，客户端据状态变化判断什么时候真停了。
    /// </remarks>
    private async Task CancelSubagentAsync(
        CancelSubagentCommand command, CancellationToken cancellationToken)
    {
        var registry = _provider.Subagents(command.SessionId);
        if (registry is null)
        {
            await SendAsync(new CommandFailedEvent
            {
                Sequence = NextSequence(),
                RequestId = command.RequestId,
                Reason = CommandFailureReason.Conflict,
                Message = "这个会话没有子 agent 注册表。",
                Method = "subagent/cancel",
            }, cancellationToken).ConfigureAwait(false);
            return;
        }

        var handle = registry.Find(command.SubagentId);
        if (handle is null)
        {
            await SendAsync(new CommandFailedEvent
            {
                Sequence = NextSequence(),
                RequestId = command.RequestId,
                Reason = CommandFailureReason.Invalid,
                Message = $"没有 id 为 '{command.SubagentId}' 的子 agent。",
                Method = "subagent/cancel",
            }, cancellationToken).ConfigureAwait(false);
            return;
        }

        // 取消之后子 agent 还会跑完当前那次工具调用才真正结束。
        // **必须订阅**，否则用户点了停止却收不到"真的停了"那一刻的通知
        // ——只能靠自己轮询，而那时他已经以为停下来了。
        Subscribe(registry, command.SessionId);

        handle.Cancel();

        // 回当前列表：状态此刻可能仍是 Running（取消是请求，不是立即生效）。
        await RespondSubagentsAsync(
            command.RequestId, command.SessionId, take: null, cancellationToken)
            .ConfigureAwait(false);
    }

    // ── 帧读写 ────────────────────────────────────────────────

    /// <summary>写一条事件。</summary>
    /// <remarks>
    /// <b>紧凑 JSON + LF</b>：<c>WriteIndented</c> 会插入裸换行，直接破坏按行分帧。
    /// System.Text.Json 会把字符串内的换行转义成 <c>\n</c>，所以紧凑输出天然安全。
    /// </remarks>
    public async Task SendAsync(ServerEvent evt, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(evt);

        var json = JsonSerializer.Serialize(evt, HostProtocolContract.Json);

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var bytes = Encoding.UTF8.GetBytes(json + "\n");
            await _output.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
            await _output.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>
    /// 直接送一条推送事件（不回应任何命令）。
    /// </summary>
    /// <remarks>
    /// 供宿主在后台把子 agent 状态变化推上来。**必须不带 <c>RequestId</c>**：
    /// 带了会让客户端误以为它是某条命令的响应。
    /// </remarks>
    public Task PushAsync(ServerEvent evt, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(evt);

        if (evt.RequestId is not null)
            throw new ArgumentException(
                "推送事件不能带 RequestId——那会让客户端误以为它是某条命令的响应。",
                nameof(evt));

        return SendAsync(evt, cancellationToken);
    }

    /// <summary>按行读并解析 JSON。无法解析的行返回 null（由调用方计数）。</summary>
    private async IAsyncEnumerable<JsonNode?> ReadLinesAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(
            _input, Encoding.UTF8, detectEncodingFromByteOrderMarks: false,
            bufferSize: 8192, leaveOpen: true);

        while (true)
        {
            string? line;
            try
            {
                line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                yield break;
            }

            if (line is null) yield break;

            // 对端可能用 CRLF。
            if (line.EndsWith('\r')) line = line[..^1];

            if (string.IsNullOrWhiteSpace(line)) continue;

            if (line.Length > MaxLineBytes)
            {
                _log.WriteLine($"[protocol] 单行超过 {MaxLineBytes} 字节，判定连接损坏。");
                yield break;
            }

            JsonNode? node;
            try
            {
                node = JsonNode.Parse(line);
            }
            catch (JsonException)
            {
                node = null;
            }

            yield return node;
        }
    }

    private long NextSequence() => Interlocked.Increment(ref _sequence);

    /// <summary>
    /// 取命令的判别器字符串，供失败事件回报。
    /// </summary>
    /// <remarks>
    /// <b>委托给契约的 <see cref="ClientCommand.MethodOf"/></b>，不在这里
    /// 手写一张映射表：手写表与 <c>[JsonDerivedType]</c> 必然漂移，
    /// 而漂移的表现是"新增的命令在失败事件里回报成类名"，客户端认不出。
    /// </remarks>
    private static string MethodOf(ClientCommand command) =>
        ClientCommand.MethodOf(command.GetType());

    /// <summary>从 JSON 节点里尽力取一个字符串字段（大小写两种写法都试）。</summary>
    private static string? TryReadString(JsonNode node, string property)
    {
        if (node is not JsonObject obj) return null;

        if (obj.TryGetPropertyValue(property, out var value) && value is JsonValue v)
            return v.TryGetValue<string>(out var text) ? text : null;

        return null;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        // **用 Interlocked 而不是先判后置**：两个线程同时 Dispose 时，
        // 后者会走到已释放的 SemaphoreSlim 上抛 ObjectDisposedException。
        if (Interlocked.Exchange(ref _disposedFlag, 1) != 0) return;

        _lifetime.Cancel();

        // **等写入完成要有上限**：若某次写卡在流上（对端不读、管道缓冲满），
        // 无超时的等待会让 Dispose 永远挂住——表现为"程序退不出去"，
        // 而栈上只看到一句 WaitAsync，看不出是对端不肯读。
        // 超时后直接继续：释放资源比"保证没有在途写"更重要，
        // 在途写的异常由它自己的 catch 兜住。
        try
        {
            if (await _writeLock.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false))
                _writeLock.Release();
        }
        catch (ObjectDisposedException)
        {
            // 已经释放过，正常。
        }

        _writeLock.Dispose();
        _lifetime.Dispose();
    }

    /// <summary>是否已经释放。</summary>
    private bool _disposed => Volatile.Read(ref _disposedFlag) != 0;

    /// <summary>释放标志。用 int 是为了 <see cref="Interlocked"/>。</summary>
    private int _disposedFlag;
}

/// <summary>
/// 服务端握有的会话能力。**服务端只认这个接口，不认任何具体宿主。**
/// </summary>
/// <remarks>
/// <b>为什么是接口而不是具体类型</b>：这是"不绑定"的落点。桌面端、CLI、
/// 第三方各自实现它——服务端不知道背后是哪个宿主的会话装配。
///
/// <para>
/// <b>为什么三个能力分开给，而不是一个大接口</b>：宿主可能只想支持其中一部分
/// （例如纯 CLI 场景不要子 agent）。返回 <c>null</c> 表示"这个能力没有"，
/// 服务端据此回 <see cref="CommandFailureReason.Conflict"/> 而不是假装成功。
/// </para>
/// </remarks>
public interface IHostSessionProvider
{
    /// <summary>
    /// 取某个会话的权限预设；null 表示没有这个能力。
    /// </summary>
    /// <remarks>
    /// <b>⚠️ 必须返回"引擎审批真正在读的那一份"，否则 C5 是假控件。</b>
    /// 服务端只是把 <c>permission/switch</c> 转成
    /// <see cref="PermissionPresets.SwitchAsync"/>——它改的是**这个实例**。
    /// 若引擎的审批链没有用同一个实例（见
    /// <see cref="PresetApprover"/> 的装饰方式），界面会切成功、
    /// 服务端也回成功，而工具该问的还是照问。
    ///
    /// <para>
    /// 正确接法：宿主建**一个** <see cref="PermissionPresets"/>，
    /// 既包进 <c>new PresetApprover(innerApprover, presets)</c> 交给
    /// dispatcher，也从这里返回。
    /// </para>
    /// </remarks>
    /// <param name="sessionId">会话标识；null 表示全局默认。</param>
    PermissionPresets? Permissions(string? sessionId);

    /// <summary>
    /// 取某个会话的子 agent 注册表；null 表示没有这个能力。
    /// </summary>
    /// <remarks>
    /// <b>⚠️ 必须是构造 <c>SubagentTool</c> / <c>SubagentListTool</c> /
    /// <c>SubagentResultTool</c> 时传进去的同一个实例。</b>
    /// 引擎**不自己创建注册表**——它只是把宿主给的那份转交给工具。
    /// 若这里返回另一个 <c>new SubagentRegistry()</c>，
    /// <c>subagent/list</c> 会永远返回空列表，而引擎实际在跑子 agent：
    /// "界面显示零个、额度却在烧"，这是最难排查的一致性问题。
    /// </remarks>
    /// <param name="sessionId">会话标识。</param>
    SubagentRegistry? Subagents(string sessionId);
}
