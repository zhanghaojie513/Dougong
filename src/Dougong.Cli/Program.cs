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
        // 用量记账：装饰器由共用装配包在连接器外面，不改变任何调用语义。
        // 账目随会话保存，`/usage` 可查；这里不猜单价，只汇总 token 与调用次数。
        var ledger = new UsageLedger();
        // 与 ACP 模式共用同一套装配：能力集合必须一致。
        var composition = SessionComposition.FromEnvironment(
            sessionId, workspace, ChatClientFactory.Create(config), store, new ConsoleApprover());

        var dispatcher = composition.CreateDispatcher();

        var (mcpRegistries, mcpClients) = await composition
            .ConnectMcpAsync(dispatcher, Console.Out)
            .ConfigureAwait(false);

        if (composition.McpServers.Count == 0)
            Console.WriteLine("MCP：未配置（DOUGONG_MCP_SERVERS 开启）");

        var runtime = composition.CreateRuntime(dispatcher, ledger);
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
                if (!HandleCommand(input, store, messages, sessionPath, ledger, sessionId))
                {
                    foreach (var registry in mcpRegistries) registry.Dispose();
                    foreach (var mcpClient in mcpClients) await mcpClient.DisposeAsync();
                    return 0;
                }
                continue;
            }

            turn++;
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
        string input, IAgentStore store, List<AgentMessage> history, string sessionPath,
        UsageLedger ledger, string sessionId)
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
                return true;

            case "/history":
                Console.WriteLine($"  内存历史 {history.Count} 条；持久化记录见会话 {store.SessionId}。");
                return true;

            case "/where":
                Console.WriteLine($"  {sessionPath}");
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
    /// 打印用量汇总。未报告的调用单独列出：
    /// 把"不知道"显示成 0 会让人以为不花钱。
    /// </summary>
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
                    context, client, approver, storePath, Console.Error);
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
            Console.Error.WriteLine($"[acp] 会话存储打开失败，改用无持久化：{ex.Message}");
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
            Console.Error.WriteLine($"[acp] 会话存储打开失败，改用无持久化：{ex.Message}");
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
