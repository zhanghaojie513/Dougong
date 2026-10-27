using System.Text;
using System.Text.Json;
using Dougong.Agent;
using Dougong.Ai;
using Dougong.Acp;
using Dougong.Mcp;
using Microsoft.Extensions.AI;

namespace Dougong.Cli;

/// <summary>
/// headless 参考宿主：把 Agent 契约、循环、提交屏障、审批与压缩接到一个可运行的 REPL。
/// </summary>
/// <remarks>
/// 环境变量：
/// <list type="bullet">
/// <item><c>DOUGONG_PROVIDER_BASE</c>、<c>DOUGONG_PROVIDER_KEY</c>、<c>DOUGONG_PROVIDER_MODEL</c> 必填</item>
/// <item><c>DOUGONG_PROVIDER_PROTOCOL</c> 可选：<c>openai</c>（默认）/ <c>anthropic</c> / <c>responses</c></item>
/// <item><c>DOUGONG_ANTHROPIC_PATH</c> 可选：Anthropic 兼容层路径，默认 <c>/v1/messages</c></item>
/// <item><c>DOUGONG_WORKSPACE</c> 可选，默认当前目录</item>
/// <item><c>DOUGONG_SESSION_DIR</c> 可选，默认 ~/.dougong/sessions</item>
/// <item><c>DOUGONG_SHELL</c> 可选：设为 <c>1</c> 时启用 <c>shell</c> 工具（默认关闭）</item>
/// <item><c>DOUGONG_CONTEXT_WINDOW</c> 可选：模型上下文窗口大小；设置后启用历史压缩（默认关闭）</item>
/// <item><c>DOUGONG_MCP_SERVERS</c> 可选：MCP server 列表（默认不连）。三种形态：
/// <c>name=/path/server args</c>（stdio）、<c>name=http://host/mcp</c>、
/// <c>name=https://host/mcp</c>（Streamable HTTP）</item>
/// <item><c>DOUGONG_MEMORY</c> 可选：启用 L1 偏好记忆。<c>1</c>/<c>true</c> 用默认目录，
/// 或直接给目录路径。**默认关闭**——启用意味着历史偏好会进入后续每次 prompt，
/// 可能发给远端模型，因此必须显式开启。开启后 <c>/memory</c> 可查看全部产物</item>
/// </list>
/// 会话参数：
/// <list type="bullet">
/// <item><c>--list</c> 列出会话（文件、记录数、序号范围、序号缺口）</item>
/// <item><c>--resume &lt;会话&gt;</c> 恢复会话；接受完整路径、会话标识或序号（1 = 最新）</item>
/// <item><c>--branch &lt;会话&gt; [--at N]</c> 从第 N 条已提交记录分叉（缺省为最后一条）</item>
/// </list>
/// 恢复或分支时若存在结果未知的调用，命令会拒绝继续：不能把"没有结果"当成"没有执行"。
/// 密钥只从环境变量读取，不写入文件、不打印。
/// </remarks>
public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        // ACP 模式：作为 Agent 端被外部客户端驱动，stdout 归协议所有。
        if (args.Contains("--acp"))
            return await RunAcpAgentAsync();

        if (args.Contains("--version") || args.Contains("-v"))
        {
            Console.WriteLine("dougong 0.1.0-alpha.1");
            return 0;
        }

        // --help 必须排在 provider 配置检查之前。
        // 一个刚装完工具的人第一件事就是敲 --help——那时他还没配 provider，
        // 若在这里先报"缺少 provider 配置"，他连有哪些命令都不知道。
        if (args.Contains("--help") || args.Contains("-h"))
        {
            PrintHelp();
            return 0;
        }

        // 自检必须在读取 provider 配置之前：它的用途正是诊断"配置不全导致跑不起来"，
        // 如果放在后面，缺配置时会先退出 2，自检永远跑不到。
        if (args.Contains("--doctor"))
            return await Doctor.RunAsync();

        // 信息包也排在 provider 配置检查之前：它要能用于"配置不完整"的场景。
        if (args.Contains("--report"))
        {
            Console.WriteLine(await DiagnosticBundle.BuildAsync());
            return 0;
        }

        var config = ChatClientFactory.FromEnvironment();
        if (config is null)
        {
            Console.Error.WriteLine(
                "缺少 provider 配置。请设置 DOUGONG_PROVIDER_BASE / KEY / MODEL 环境变量。");
            return 2;
        }

        if (Environment.GetEnvironmentVariable("DOUGONG_PROVIDER_KEY") is not { Length: > 0 })
        {
            Console.Error.WriteLine("缺少 DOUGONG_PROVIDER_KEY 环境变量。");
            return 2;
        }

        var workspace = Environment.GetEnvironmentVariable("DOUGONG_WORKSPACE") ?? Directory.GetCurrentDirectory();
        var sessionDir = Environment.GetEnvironmentVariable("DOUGONG_SESSION_DIR")
            ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".dougong", "sessions");

        // --list / --resume / --branch 都是"先决定用哪个会话，再开 REPL"。
        if (args.Contains("--list"))
        {
            PrintSessionList(sessionDir);
            return 0;
        }

        var sessionId = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss");
        var sessionPath = Path.Combine(sessionDir, $"{sessionId}.jsonl");
        IReadOnlyList<AgentMessage> history = [];

        if (FindArg(args, "--resume") is { } resumeArg)
        {
            var resolved = ResolveSessionPath(sessionDir, resumeArg);
            if (resolved is null)
            {
                Console.Error.WriteLine($"找不到会话：{resumeArg}");
                return 3;
            }

            var recovered = await ReplayAsync(resolved);
            if (recovered is null) return 3;

            // 需要人工对账时必须停下：不能把"没有结果"当成"没有执行"。
            if (recovered.DescribeBlockingReason() is { } blocking)
            {
                Console.Error.WriteLine($"拒绝恢复会话：{blocking}");
                return 4;
            }

            sessionPath = resolved;
            sessionId = Path.GetFileNameWithoutExtension(resolved);
            history = recovered.HistoryForResume;

            Console.WriteLine($"已恢复会话：{sessionId}（{history.Count} 条历史）");
        }

        if (FindArg(args, "--branch") is { } branchArg)
        {
            var resolved = ResolveSessionPath(sessionDir, branchArg);
            if (resolved is null)
            {
                Console.Error.WriteLine($"找不到会话：{branchArg}");
                return 3;
            }

            var recovered = await ReplayAsync(resolved);
            if (recovered is null) return 3;
            if (recovered.DescribeBlockingReason() is { } blocking)
            {
                Console.Error.WriteLine($"拒绝分支：{blocking}");
                return 4;
            }

            // 分支点默认取最后一条已提交记录；也可用 --at 指定。
            long? at = long.TryParse(FindArg(args, "--at"), out var parsedAt) ? parsedAt : null;
            var branchId = $"{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..4]}";
            var branchPath = Path.Combine(sessionDir, $"{branchId}.jsonl");

            BranchResult result;
            try
            {
                result = await SessionBranch.CreateAsync(resolved, branchPath, branchId, at);
            }
            catch (InvalidOperationException ex)
            {
                Console.Error.WriteLine($"分支失败：{ex.Message}");
                return 4;
            }

            Console.WriteLine($"已从 #{result.LastSequence} 分支：{branchId}（{result.RecordCount} 条）");
            sessionPath = result.Path;
            sessionId = result.SessionId;

            var forked = await ReplayAsync(result.Path);
            history = forked?.HistoryForResume ?? [];
        }

        var shellEnabled = Environment.GetEnvironmentVariable("DOUGONG_SHELL") is "1" or "true";

        // 上下文预算：按 DOUGONG_CONTEXT_WINDOW 配置；未配置时保持旧行为（不压缩）。
        var contextWindow = int.TryParse(
            Environment.GetEnvironmentVariable("DOUGONG_CONTEXT_WINDOW"), out var window) && window > 0
            ? window
            : 0;

        Console.WriteLine($"dougong 0.1.0-alpha.1  模型={config.Model}  协议={config.Protocol}");
        Console.WriteLine($"工作区={workspace}");
        Console.WriteLine($"会话={sessionPath}");
        Console.WriteLine($"shell 工具={(shellEnabled ? "已启用" : "未启用（DOUGONG_SHELL=1 开启）")}");
        Console.WriteLine(contextWindow > 0
            ? $"上下文压缩=已启用（窗口 {contextWindow} tokens）"
            : "上下文压缩=未启用（DOUGONG_CONTEXT_WINDOW 开启）");

        await using var store = await JsonlAgentStore.OpenAsync(sessionPath, sessionId)
            .ConfigureAwait(false);

        // 如实展示持久化等级：这是 IAgentStore 契约的一部分，
        // 此前声明了却没有任何消费者——用户看不到自己在什么保证之下。
        Console.WriteLine(
            $"durability={DurabilityText.Tag(store.Durability)}（{DurabilityText.Describe(store.Durability)}）");
        if (DurabilityText.NeedsWarning(store.Durability))
        {
            Console.WriteLine("  ⚠ 该存储不保留会话：退出后无法恢复。");
        }
        // 用量记账：装饰器由共用装配包在连接器外面，不改变任何调用语义。
        // 账目随会话保存，`/usage` 可查；这里不猜单价，只汇总 token 与调用次数。
        var ledger = new UsageLedger();
        // 与 ACP 模式共用同一套装配：能力集合必须一致。
        // 自动化验收可用 DOUGONG_AUTO_APPROVE=1 免除逐次询问；默认仍走控制台。
        IToolApprover approver = AutoApproveApprover.IsEnabled
            ? new AutoApproveApprover()
            : new ConsoleApprover();
        var composition = await SessionComposition.CreateAsync(
            sessionId, workspace, ChatClientFactory.Create(config), approver,
            SessionComposition.MemoryDirectoryFromEnvironment(sessionDir));

        // 内核接线（G14）：默认启用——运行能力由内核解析，宿主不直接持有
        // dispatcher/runtime 实例。DOUGONG_KERNEL=0 可退回直连路径，
        // 保留给"怀疑内核接线本身有问题"时的对照手段。
        var useKernel = Environment.GetEnvironmentVariable("DOUGONG_KERNEL") is not "0" and not "false";

        // 这些是"排查时最先要问"的事实，放在 info 级：
        // 默认不打扰，出问题时 DOUGONG_LOG=info 就能看到。
        Log.Information($"日志级别={Log.CurrentThreshold}（DOUGONG_LOG 控制）");
        Log.Information($"会话={sessionId} 工作区={workspace}");
        Log.Information($"存储={store.Durability} 记忆={(composition.Memory is null ? "关闭" : "启用")}");
        Log.Information($"内核接线={(useKernel ? "启用" : "禁用")} "
            + $"压缩={(composition.ContextWindow > 0 ? composition.ContextWindow.ToString() : "关闭")} "
            + $"shell={(composition.ShellEnabled ? "启用" : "关闭")} "
            + $"MCP={composition.McpServers.Count} 个");

        IAgentRuntime runtime;
        IReadOnlyList<McpToolRegistry> mcpRegistries = [];
        IReadOnlyList<ModelContextProtocol.Client.McpClient> mcpClients = [];
        KernelAgentHost? kernelHost = null;

        try
        {
            if (useKernel)
            {
                kernelHost = await KernelAgentHost.CreateAsync(
                    store,
                    async (resolvedApprover, resolvedStore, ct) =>
                    {
                        // dispatcher 在插件激活期内构造：MCP 工具注册属于它的构造，
                        // 不能被推到激活之后（届时服务快照已提交）。
                        var built = composition.CreateDispatcher(resolvedApprover, resolvedStore);
                        var (registries, clients) = await composition
                            .ConnectMcpAsync(built, Console.Out, ct)
                            .ConfigureAwait(false);
                        mcpRegistries = registries;
                        mcpClients = clients;
                        return built;
                    },
                    (resolvedDispatcher, resolvedStore) =>
                        composition.CreateRuntime(resolvedDispatcher, resolvedStore, ledger),
                    approver).ConfigureAwait(false);

                runtime = kernelHost.ResolveRuntime();
                Console.WriteLine("kernel=enabled（运行能力由内核解析，每次回合取租约）");
            }
            else
            {
                Console.WriteLine("kernel=disabled（DOUGONG_KERNEL=0，直连路径）");
                var dispatcher = composition.CreateDispatcher(store);
                var (registries, clients) = await composition
                    .ConnectMcpAsync(dispatcher, Console.Out)
                    .ConfigureAwait(false);
                mcpRegistries = registries;
                mcpClients = clients;
                runtime = composition.CreateRuntime(dispatcher, store, ledger);
            }
        }
        catch (Exception ex) when (useKernel)
        {
            Console.Error.WriteLine($"内核接线失败：{ex.GetType().Name}: {ex.Message}");
            return 3;
        }

        if (composition.McpServers.Count == 0)
            Console.WriteLine("MCP：未配置（DOUGONG_MCP_SERVERS 开启）");

        var messages = new List<AgentMessage>(history);
        var turn = 0;

        while (true)
        {
            Console.Write("> ");
            var input = Console.ReadLine();
            if (input is null) break;                       // EOF
            if (string.IsNullOrWhiteSpace(input)) continue;

            if (input.StartsWith('/'))
            {
                if (!HandleCommand(input, messages, sessionPath, ledger, sessionId, composition))
                {
                    foreach (var registry in mcpRegistries) registry.Dispose();
                    foreach (var mcpClient in mcpClients) await mcpClient.DisposeAsync();
                    // 内核宿主必须显式释放：它持有租约与排空状态，
                    // 不释放会让内核对"停用"的判定停在半路。
                    if (kernelHost is not null) await kernelHost.DisposeAsync();
                    return 0;
                }
                continue;
            }

            turn++;
            // 只记长度不记内容：用户输入原文可能含敏感信息。
            Log.Debug($"turn-{turn} 开始，历史 {messages.Count} 条，输入 {input.Length} 字符");
            var request = new AgentRunRequest(sessionId, $"turn-{turn}", messages, input);
            var assistantText = new StringBuilder();

            try
            {
                await foreach (var evt in runtime.RunAsync(request))
                {
                    switch (evt)
                    {
                        case AgentEvent.TextDelta delta:
                            assistantText.Append(delta.Text);
                            Console.Write(delta.Text);
                            break;

                        case AgentEvent.ToolCallRequested call:
                            Console.WriteLine();
                            Console.WriteLine($"  → 调用 {call.ToolId} {Truncate(call.Arguments.GetRawText())}");
                            break;

                        case AgentEvent.ToolCallCompleted done:
                            Console.WriteLine($"  ← {done.Result.Outcome}" +
                                (done.Result.Diagnostic is { } d ? $"（{d}）" : string.Empty));
                            break;

                        case AgentEvent.ContextCompacted compacted:
                            Console.WriteLine();
                            Console.WriteLine(compacted.Applied
                                ? $"  ⇄ 已压缩 {compacted.RemovedMessages} 条历史"
                                  + $"（估算 {compacted.EstimatedTokensBefore} → {compacted.EstimatedTokensAfter} tokens）"
                                : $"  ⇄ 未压缩：{compacted.Diagnostic}");
                            break;

                        case AgentEvent.RunFinished finished:
                            Console.WriteLine();
                            if (finished.Outcome != AgentRunOutcome.Completed)
                                Console.Error.WriteLine($"  [结束：{finished.Outcome}] {finished.Diagnostic}");
                            break;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                Console.WriteLine();
                Console.Error.WriteLine("  [已取消]");
            }

            if (assistantText.Length > 0)
                messages.Add(new AgentMessage(AgentRole.Assistant, assistantText.ToString()));
            messages.Add(new AgentMessage(AgentRole.User, input));
            Console.WriteLine();
        }

        return 0;
    }

    private static bool HandleCommand(
        string input, List<AgentMessage> history, string sessionPath,
        UsageLedger ledger, string sessionId, SessionComposition composition)
    {
        switch (input.Trim().ToLowerInvariant())
        {
            case "/exit":
            case "/quit":
                return false;

            case "/help":
                Console.WriteLine("  /exit     退出");
                Console.WriteLine("  /history  查看内存中的历史条数（会话日志以 JSONL 为准）");
                Console.WriteLine("  /where    显示当前会话文件");
                Console.WriteLine("  /usage    显示本会话的 token 用量与调用次数");
                Console.WriteLine("  /memory   显示 L1 偏好记忆（含未启用的候选）");
                return true;

            case "/history":
                Console.WriteLine($"  内存历史 {history.Count} 条；持久化记录见会话 {sessionId}。");
                return true;

            case "/where":
                Console.WriteLine($"  {sessionPath}");
                return true;

            case "/memory":
                PrintMemory(composition);
                return true;

            case "/usage":
                PrintUsage(ledger, sessionId);
                return true;

            default:
                Console.WriteLine($"  未知命令：{input}");
                return true;
        }
    }

    /// <summary>
    /// 列出记忆产物。
    /// </summary>
    /// <remarks>
    /// 列出**全部**状态而不只是启用中的：候选为什么没生效（未审批？篡改？过期？）
    /// 是用户最需要看到的信息，只显示启用项等于把诊断信息藏起来。
    /// 每条都带来源、版本、作用域——验收 6 要求"用户能查看当前启用内容、
    /// 来源、版本及作用域"。
    /// </remarks>
    private static void PrintMemory(SessionComposition composition)
    {
        if (composition.MemoryStore is null)
        {
            Console.WriteLine("  记忆未启用。设置 DOUGONG_MEMORY=1 开启。");
            Console.WriteLine("  开启后，历史任务中的偏好会被注入后续对话——"
                + "这是外发数据流，因此默认关闭。");
            return;
        }

        // 同步等待：这是用户主动输入的命令，不是后台路径。
        var all = composition.MemoryStore.ReadArtifactsAsync().GetAwaiter().GetResult();
        if (all.Count == 0)
        {
            Console.WriteLine("  记忆为空。");
            return;
        }

        Console.WriteLine($"  记忆产物 {all.Count} 条（未生效项会说明原因）：");
        Console.WriteLine();

        var now = DateTimeOffset.UtcNow;

        foreach (var artifact in all.Values
            .OrderByDescending(a => MemoryDiagnostics.WhyInactive(a, composition.MemoryWorkspace, now) is null)
            .ThenBy(a => a.Key, StringComparer.Ordinal))
        {
            // 判定与原因都取自与注入器同一处，展示不会与实际注入漂移。
            var reason = MemoryDiagnostics.WhyInactive(
                artifact, composition.MemoryWorkspace, now);

            Console.WriteLine($"  [{(reason is null ? "生效" : "未生效")}] {artifact.Key} = {artifact.Value}");
            Console.WriteLine($"      状态 {artifact.State} · 版本 {artifact.Version}"
                + $" · 作用域 {artifact.Scope}"
                + (string.IsNullOrEmpty(artifact.Workspace) ? "" : $"({artifact.Workspace})"));
            Console.WriteLine($"      来源任务 {artifact.SourceTaskId}"
                + (artifact.ExpiresAt is { } expires ? $" · 有效至 {expires:yyyy-MM-dd HH:mm}" : ""));

            if (reason is not null) Console.WriteLine($"      原因：{reason}");
        }
    }

    /// <summary>说明一条产物为什么不生效。顺序与注入器的校验顺序一致。</summary>
    private static string ExplainInactive(MemoryArtifact artifact)
    {
        if (artifact.State is not MemoryArtifactState.Enabled)
            return artifact.State switch
            {
                MemoryArtifactState.Candidate => "尚为候选，未审批启用",
                MemoryArtifactState.Approved => "已审批但未启用",
                MemoryArtifactState.Revoked => "已撤销",
                MemoryArtifactState.Expired => "已过期",
                _ => artifact.State.ToString(),
            };

        if (!artifact.IsIntact) return "内容与审批时的哈希不符（可能被改动）";
        if (artifact.ExpiresAt is { } expires && expires <= DateTimeOffset.UtcNow)
            return $"已过期（{expires:yyyy-MM-dd HH:mm}）";
        if (!PreferenceKeys.IsAllowed(artifact.Key))
            return "键不在白名单内";
        if (!PreferenceKeys.IsValid(artifact.Key, artifact.Value))
            return "取值不符合该键的约束";

        return "作用域不匹配当前工作区";
    }

    /// <summary>
    /// 打印用量汇总。未报告的调用单独列出：
    /// 把"不知道"显示成 0 会让人以为不花钱。
    /// </summary>
    /// <summary>
    /// 打印用法。
    /// </summary>
    /// <remarks>
    /// 这是安装后第一个会看的输出，所以要说清"最小可跑起来的一条路"，
    /// 而不是罗列所有参数。
    /// <para>
    /// **必须在 provider 配置检查之前处理**：刚装完工具的人还没配 key，
    /// 那时若先报"缺少 provider 配置"，他连有哪些命令都不知道。
    /// </para>
    /// </remarks>
    private static void PrintHelp()
    {
        Console.WriteLine("dougong 0.1.0-alpha.1 —— .NET Agent Harness 参考宿主");
        Console.WriteLine();
        Console.WriteLine("用法：");
        Console.WriteLine("  dougong                      启动对话（需要 provider 配置）");
        Console.WriteLine("  dougong --doctor             启动自检：逐项检查环境并给出下一步");
        Console.WriteLine("  dougong --report             输出诊断信息包（不含密钥与对话内容）");
        Console.WriteLine("  dougong --version            显示版本");
        Console.WriteLine("  dougong --help               显示本帮助");
        Console.WriteLine("  dougong --acp                以 ACP Agent 模式运行（stdout 归协议）");
        Console.WriteLine("  dougong --list               列出会话");
        Console.WriteLine("  dougong --resume <会话>      恢复会话");
        Console.WriteLine("  dougong --branch <会话>      从某条记录分叉");
        Console.WriteLine();
        Console.WriteLine("第一次用：先跑 dougong --doctor 确认环境。");
        Console.WriteLine();
        Console.WriteLine("需要三个环境变量（密钥只从环境变量读，不写入任何文件）：");
        Console.WriteLine("  DOUGONG_PROVIDER_BASE    服务地址，如 https://api.deepseek.com");
        Console.WriteLine("  DOUGONG_PROVIDER_MODEL   模型名");
        Console.WriteLine("  DOUGONG_PROVIDER_KEY     密钥");
        Console.WriteLine();
        Console.WriteLine("可选：");
        Console.WriteLine("  DOUGONG_PROVIDER_PROTOCOL  openai（默认）/ anthropic / responses");
        Console.WriteLine("  DOUGONG_WORKSPACE          工作区目录，默认当前目录");
        Console.WriteLine("  DOUGONG_SESSION_DIR        会话目录，默认 ~/.dougong/sessions");
        Console.WriteLine("  DOUGONG_SHELL=1            启用 shell 工具（默认关闭）");
        Console.WriteLine("  DOUGONG_MEMORY=1           启用 L1 偏好记忆（默认关闭）");
        Console.WriteLine("  DOUGONG_LOG=debug          分级日志：error/warn/info/debug");
        Console.WriteLine();
        Console.WriteLine("对话中可用：/help  /exit  /usage  /where  /memory");
        Console.WriteLine();
        Console.WriteLine("文档：https://github.com/zhanghaojie513/Dougong");
    }

    private static void PrintUsage(UsageLedger ledger, string sessionId)
    {
        var summary = ledger.Summarize(sessionId);
        if (summary.Calls == 0)
        {
            Console.WriteLine("  本会话还没有模型调用。");
            return;
        }

        Console.WriteLine($"  调用      : {summary.Calls} 次（失败 {summary.FailedCalls}）");
        Console.WriteLine($"  输入      : {summary.InputTokens} tokens");
        Console.WriteLine($"  输出      : {summary.OutputTokens} tokens");

        if (summary.TotalTokens is { } total)
            Console.WriteLine($"  合计      : {total} tokens");
        else
            Console.WriteLine("  合计      : 未知（provider 没有报告用量）");

        if (summary.UnreportedCalls > 0)
        {
            Console.WriteLine(
                $"  ⚠ 未报告  : {summary.UnreportedCalls} 次调用没有用量数据，"
                + $"覆盖率 {summary.ReportedRatio:P0}");
        }

        Console.WriteLine($"  累计耗时  : {summary.TotalElapsed.TotalSeconds:0.0}s");
        Console.WriteLine("  成本      : 未计算（依赖 provider 价目表，本层不猜单价）");
    }

    /// <summary>读取会话列表。只显示文件与序号范围，不解析完整历史。</summary>
    private static void PrintSessionList(string sessionDir)
    {
        var sessions = SessionBranch.List(sessionDir);
        if (sessions.Count == 0)
        {
            Console.WriteLine($"目录下没有会话：{sessionDir}");
            return;
        }

        Console.WriteLine($"{"会话",-28} {"记录",6} {"序号范围",14}  {"最后写入",-20} 备注");
        foreach (var session in sessions)
        {
            var note = session.HasSequenceGap ? "序号缺口（不可续跑）" : string.Empty;
            Console.WriteLine(
                $"{session.SessionId,-28} {session.RecordCount,6} " +
                $"{session.FirstSequence + ".." + session.LastSequence,14}  " +
                $"{session.LastWriteUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss}  {note}");
        }
    }

    /// <summary>把命令行参数解析成会话文件路径；接受完整路径或会话标识。</summary>
    private static string? ResolveSessionPath(string sessionDir, string argument)
    {
        if (File.Exists(argument)) return Path.GetFullPath(argument);

        var candidate = Path.Combine(sessionDir, argument + ".jsonl");
        if (File.Exists(candidate)) return candidate;

        // 也允许直接传最近会话的序号（1 = 最新），省去复制长文件名。
        if (int.TryParse(argument, out var index) && index >= 1)
        {
            var sessions = SessionBranch.List(sessionDir);
            if (index <= sessions.Count) return sessions[index - 1].Path;
        }

        return null;
    }

    /// <summary>重放会话；失败时打印原因并返回 null。</summary>
    private static async Task<RecoveredSession?> ReplayAsync(string path)
    {
        try
        {
            await using var store = await JsonlAgentStore.OpenAsync(
                path, Path.GetFileNameWithoutExtension(path)).ConfigureAwait(false);
            return await SessionRecovery.ReplayAsync(store).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or StoreCommitException)
        {
            Console.Error.WriteLine($"无法读取会话 {path}：{ex.Message}");
            return null;
        }
    }

    private static string? FindArg(string[] args, string name)
    {
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i].StartsWith(name + "=", StringComparison.Ordinal))
                return args[i][(name.Length + 1)..];
            if (args[i] == name && i + 1 < args.Length) return args[i + 1];
        }

        return null;
    }

    /// <summary>
    /// 以 ACP Agent 端运行：从 stdin 读协议帧，向 stdout 写协议帧。
    /// </summary>
    /// <remarks>
    /// **日志必须走 stderr**：stdout 归协议所有，混入任何非协议内容都会破坏连接。
    /// 因此这里的 Console.WriteLine 一律写成 Console.Error.WriteLine。
    /// </remarks>
    private static async Task<int> RunAcpAgentAsync()
    {
        var config = ChatClientFactory.FromEnvironment();
        if (config is null)
        {
            Console.Error.WriteLine(
                "ACP 模式需要 DOUGONG_PROVIDER_BASE / KEY / MODEL 环境变量。");
            return 2;
        }

        Console.Error.WriteLine($"[acp] dougong 作为 ACP Agent 启动，模型={config.Model}");

        using var client = ChatClientFactory.Create(config);

        // host 与工厂互相依赖：工厂里要创建审批器，而审批器要能向 host 发请求。
        // 用延迟绑定打破这个循环，而不是引入可变字段。
        AcpAgentHost? host = null;
        var sessionResources =
            new List<(IReadOnlyList<McpToolRegistry> Registries,
                IReadOnlyList<ModelContextProtocol.Client.McpClient> Clients)>();

        host = new AcpAgentHost(
            Console.OpenStandardInput(),
            Console.OpenStandardOutput(),
            async context =>
            {
                var sessionDir = Environment.GetEnvironmentVariable("DOUGONG_SESSION_DIR")
                    ?? Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                        ".dougong", "sessions");

                // 恢复时复用原路径：换新文件会表现为"恢复了但历史没接上"。
                var storePath = context.StorePath
                    ?? Path.Combine(sessionDir, $"{context.SessionId}.jsonl");

                // 审批走协议：客户端弹窗选择，而不是控制台交互。
                IToolApprover approver = host is null
                    ? new DenyAllApprover("ACP host 尚未就绪，按拒绝处理。")
                    : new AcpApprover(() => host!, context.SessionId);

                // 装配集中在 AcpComposition，测试直接复用同一路径。
                return await AcpComposition.ComposeAsync(
                    context, client, approver, storePath, Console.Error,
                    // ask_user 工具需要向客户端发请求；host 在装配之后才存在，
                    // 因此用延迟取用。
                    hostAccessor: () => host!);
            },
            storeResolver: sessionId =>
            {
                var sessionDir = Environment.GetEnvironmentVariable("DOUGONG_SESSION_DIR")
                    ?? Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                        ".dougong", "sessions");
                var candidate = Path.Combine(sessionDir, $"{sessionId}.jsonl");
                return File.Exists(candidate) ? candidate : null;
            },
            listRoots:
            [
                Environment.GetEnvironmentVariable("DOUGONG_SESSION_DIR")
                    ?? Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                        ".dougong", "sessions")
            ]);

        await host.RunAsync();
        return 0;
    }

    /// <summary>
    /// 兜底审批器：在 ACP host 尚未就绪等异常装配状态下**默认拒绝**。
    /// </summary>
    /// <remarks>
    /// 存在的意义是"绝不默认放行"。静默批准会让一个装配错误变成安全问题。
    /// </remarks>
    private sealed class DenyAllApprover(string reason) : IToolApprover
    {
        public ValueTask<ToolApprovalDecision> RequestApprovalAsync(
            ToolApprovalRequest request, CancellationToken cancellationToken = default)
        {
            Console.Error.WriteLine($"[acp] 拒绝工具 '{request.Tool.Name}'：{reason}");
            return ValueTask.FromResult(ToolApprovalDecision.Deny(reason));
        }
    }

    private static IAgentStore? TryOpenSessionStoreAt(string path, string sessionId)
    {
        try
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(path));
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            return JsonlAgentStore.OpenAsync(path, sessionId).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            // 降级必须说清代价：不是"改用另一个选项"，而是"这次会话不会被保存"。
            Console.Error.WriteLine(
                "[acp] 会话存储打开失败，本次会话**不会持久化**（进程退出即丢失）："
                + ex.Message);
            return null;
        }
    }

    private static IAgentStore? TryOpenSessionStore(string directory, string sessionId)
    {
        try
        {
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, $"{sessionId}.jsonl");
            return JsonlAgentStore.OpenAsync(path, sessionId).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            // 降级必须说清代价：不是"改用另一个选项"，而是"这次会话不会被保存"。
            Console.Error.WriteLine(
                "[acp] 会话存储打开失败，本次会话**不会持久化**（进程退出即丢失）："
                + ex.Message);
            return null;
        }
    }

    /// <summary>
    /// ACP 模式下的兜底审批器：真正的权限请求由 AcpApprover 发出。
    /// </summary>
    /// <remarks>
    /// 这里的实现是为了让 dispatcher 在没有协议审批通道时**默认拒绝**，
    /// 而不是默认放行。ACP 首版尚未把 AcpApprover 接到 dispatcher 上
    /// （它需要 host 实例，装配顺序见 plan.md），因此当前行为是拒绝所有需审批的调用，
    /// 并在 stderr 说明原因——这比静默放行安全。
    /// </remarks>
    private sealed class StdinApprover : IToolApprover
    {
        public ValueTask<ToolApprovalDecision> RequestApprovalAsync(
            ToolApprovalRequest request, CancellationToken cancellationToken = default)
        {
            Console.Error.WriteLine(
                $"[acp] 拒绝工具调用 '{request.Tool.Name}'："
                + "ACP 首版尚未把权限请求接到客户端（见 plan.md M2.4 剩余项）。");
            return ValueTask.FromResult(
                ToolApprovalDecision.Deny("ACP 首版尚未实现客户端权限请求，按拒绝处理。"));
        }
    }

    private static string Truncate(string text) =>
        text.Length <= 120 ? text : text[..120] + "…";



    private static string ResolvePath(string workspace, string path) =>
        Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(workspace, path));
}
