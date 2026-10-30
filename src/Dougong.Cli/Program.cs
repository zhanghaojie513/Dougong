using System.Text;
using System.Text.Json;
using Dougong.Agent;
using Dougong.Hosting;
using Dougong.Ai;
using Dougong.Acp;
using Dougong.Cli.Terminal;
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
/// <item><c>DOUGONG_TUI</c> 可选：设为 <c>0</c> 关闭底部活动行。真终端里默认开启，
/// 输出被重定向时无条件关闭（那里写控制序列只会污染输出）</item>
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
        // 输出一律用 UTF-8，不跟随控制台代码页。
        //
        // **这不是可选项**：中文 Windows 的默认代码页是 936（GBK），
        // 输出里每个汉字都变成 GBK 字节；任何按 UTF-8 读取的调用方
        // （CI、检查脚本、把输出接进日志的父进程）都会得到一堆 `?`。
        // 实测过一次：`--help` 直接调用 .exe 时中文全乱，经 `dotnet run`
        // 启动却正常——后者会设控制台编码，前者不会。这种"换个启动方式就变"
        // 的缺陷最难查，所以不能指望调用方去设。
        //
        // 必须在任何输出之前执行。
        try
        {
            Console.OutputEncoding = Encoding.UTF8;
        }
        catch (Exception)
        {
            // 有的宿主拒绝设编码。保持原样：乱码好过因为设不了而崩掉。
        }

        // ACP 模式：作为 Agent 端被外部客户端驱动，stdout 归协议所有。
        if (args.Contains("--acp"))
            return await RunAcpAgentAsync();

        // 子命令 `dougong model`：配置模型并写回配置文件。
        //
        // **必须排在 --help 与未知参数检查之前**：
        //  · 排后 → `dougong model --help` 会打出主帮助，用户看不到子命令的用法
        //  · 排后 → `model` 会被当成未知参数拒掉（它不是以 - 开头的开关）
        //  · 排后 → 没有任何 provider 时无法运行，而这个命令正是用来
        //    从零创建配置的，那是最需要它的场景
        if (args.Length > 0 && args[0] == "model")
            return ModelCommand.Run(args[1..]);

        if (args.Contains("--version") || args.Contains("-v"))
        {
            Output.Line($"dougong {ProductInfo.Version}");
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
            Output.Line(await DiagnosticBundle.BuildAsync());
            return 0;
        }

        // 未知参数必须在读 provider 配置**之前**报出来。
        //
        // 修的是一个真实的静默失败：此前所有参数都用 `args.Contains(...)` 判断，
        // 拼错的参数（例如把 `--doctor` 写成 `--docter`）被静默忽略，
        // 程序直接进入交互对话——用户以为在跑自检，实际开了个会话并写了日志文件。
        // 已实测复现：`dougong --docter` 会正常启动对话并创建会话文件。
        //
        // 报错放在这里而不是更早：--help / --version / --doctor / --report
        // 都不需要 provider 配置，它们的判断在上面，不受影响。
        // 放在这里也意味着**缺配置 + 参数拼错**时报的是参数错——那才是根因。
        if (FindUnknownArgument(args) is { } unknown)
        {
            Console.Error.WriteLine($"未知参数：{unknown}");
            Console.Error.WriteLine();
            Console.Error.WriteLine("可用参数：--help --version --doctor --report --acp --list");
            Console.Error.WriteLine("          --resume <会话>  --branch <会话>");
            Console.Error.WriteLine();
            Console.Error.WriteLine("若想看完整说明：dougong --help");
            return 2;
        }

        // provider 来源：配置文件为主，环境变量整体覆盖（见 ChatClientFactory.TryResolve）。
        // 配置文件写错时必须报出**具体哪一行/哪个键**，不能静默回退到环境变量——
        // 那样用户改了配置却不生效，而屏幕上什么都看不出来。
        var resolveError = ChatClientFactory.TryResolve(null, out var catalog, out _);
        if (resolveError is not null)
        {
            Console.Error.WriteLine($"provider 配置有问题：{resolveError}");
            Console.Error.WriteLine("运行 dougong --doctor 查看完整诊断。");
            return 2;
        }

        if (catalog is null)
        {
            Console.Error.WriteLine(
                "没有可用的 provider。二选一：");
            Console.Error.WriteLine(
                $"  · 写配置文件 {ProviderConfigFile.DefaultPath}（推荐，见 --help）");
            Console.Error.WriteLine(
                "  · 或设 DOUGONG_PROVIDER_BASE / MODEL / KEY 环境变量");
            return 2;
        }

        var config = catalog.Selected;
        if (string.IsNullOrWhiteSpace(config.Model))
        {
            Console.Error.WriteLine(
                $"provider '{config.Name}' 没有配置 model。");
            Console.Error.WriteLine(
                $"在配置文件里给 [providers.{config.Name}] 加一行 model = \"<模型名>\"，"
                + "或设 DOUGONG_PROVIDER_MODEL。");
            return 2;
        }

        // 密钥：有 key_env 就必须能在环境里取到。取不到时报"缺哪个变量"，
        // 而不是笼统说配置不全——用户需要知道该 export 什么。
        if (config.ApiKeyEnvironmentVariable is { Length: > 0 } keyName
            && Environment.GetEnvironmentVariable(keyName) is not { Length: > 0 })
        {
            Console.Error.WriteLine(
                $"provider '{config.Name}' 需要环境变量 {keyName}，但它没有设置。");
            Console.Error.WriteLine($"  export {keyName}='<你的密钥>'");
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

            Output.Line($"已恢复会话：{sessionId}（{history.Count} 条历史）");
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

            Output.Line($"已从 #{result.LastSequence} 分支：{branchId}（{result.RecordCount} 条）");
            sessionPath = result.Path;
            sessionId = result.SessionId;

            var forked = await ReplayAsync(result.Path);
            history = forked?.HistoryForResume ?? [];
        }

        var shellEnabled = Environment.GetEnvironmentVariable("DOUGONG_SHELL") is "1" or "true";

        // 上下文预算：**配置里的优先，其次环境变量**。
        // 此前只看环境变量，于是 `dougong model` 写进配置的 context_window
        // 在首页上显示为"未启用"——而 /status 读的是装配后的真实值，
        // 两处打架。用户会以为自己配的没生效。
        var contextWindow = config.ContextWindowTokens is { } configuredWindow and > 0
            ? configuredWindow
            : int.TryParse(
                Environment.GetEnvironmentVariable("DOUGONG_CONTEXT_WINDOW"), out var window)
                && window > 0
                ? window
                : 0;

        // 首页也报出压缩策略：默认值（75% / 16%）是一个具体选择，不是"没配置"。
        // 非法配置这里不报错——装配期会硬失败，--doctor 也会单独列出，不重复打扰。
        var policy = SessionComposition.TryReadCompactionRatios(
                out var triggerRatio, out var retainRatio, out _)
            ? $"；触发 {triggerRatio:P0} / 保留 {retainRatio:P0}"
            : string.Empty;

        // 首页三档：Rich 出大字，Narrow 出单行，Plain 输出与改造前逐字一致的原文。
        // 原文由这里传入而不是 Banner 自己拼——"兼容"这件事不能靠猜。
        Banner.Print(
            version: ProductInfo.Version,
            model: config.Model,
            protocol: config.Protocol.ToString(),
            workspace: workspace,
            plainLines:
            [
                $"dougong {ProductInfo.Version}  模型={config.Model}  协议={config.Protocol}",
                $"工作区={workspace}",
                $"会话={sessionPath}",
                $"shell 工具={(shellEnabled ? "已启用" : "未启用（DOUGONG_SHELL=1 开启）")}",
                contextWindow > 0
                    ? $"上下文压缩=已启用（窗口 {contextWindow} tokens{policy}）"
                    : "上下文压缩=未启用（用 dougong model 选模型，或设 DOUGONG_CONTEXT_WINDOW）",
            ]);

        await using var store = await JsonlAgentStore.OpenAsync(sessionPath, sessionId)
            .ConfigureAwait(false);

        // 如实展示持久化等级：这是 IAgentStore 契约的一部分，
        // 此前声明了却没有任何消费者——用户看不到自己在什么保证之下。
        Output.Line(
            $"durability={DurabilityText.Tag(store.Durability)}（{DurabilityText.Describe(store.Durability)}）");
        if (DurabilityText.NeedsWarning(store.Durability))
        {
            Output.Line("  ⚠ 该存储不保留会话：退出后无法恢复。");
        }
        // 用量记账：装饰器由共用装配包在连接器外面，不改变任何调用语义。
        // 账目随会话保存，`/usage` 可查；这里不猜单价，只汇总 token 与调用次数。
        var ledger = new UsageLedger();
        // 与 ACP 模式共用同一套装配：能力集合必须一致。
        // 自动化验收可用 DOUGONG_AUTO_APPROVE=1 免除逐次询问；默认仍走控制台。
        IToolApprover approver = AutoApproveApprover.IsEnabled
            ? new AutoApproveApprover()
            : new ConsoleApprover();
        // 会话中换模型的载体：换连接器而不是重建运行时。
        // 重建会把内核租约、MCP 连接与排空状态一起拆掉，代价远大于一次赋值。
        // **只建一个连接器**：可切换客户端包住它，装配拿到的也是它，
        // 于是"换模型"对摘要器等所有下游同时生效，不会出现半新半旧。
        var swappable = new SwappableChatClient(ChatClientFactory.Create(config));
        var models = catalog;
        var activeProvider = config.Name;

        // 上下文与输出上限来自已解析的 provider 配置，不是环境变量。
        // 配置里写了就传下去——否则用户在 `dougong model` 里看到
        // "上下文 1,048,576 tokens"、文件里也写了，运行时用的却是别处的数。
        var composition = await SessionComposition.CreateAsync(
            sessionId, workspace, swappable, approver,
            SessionComposition.MemoryDirectoryFromEnvironment(sessionDir),
            contextWindow: config.ContextWindowTokens,
            // **不传 config.MaxOutputTokens**：那是模型的输出容量上限，
            // 不是单次请求预留。传进去会在 kimi-k3 这类
            // （1048576/1048576）模型上算出负预算。
            requestOutputReserve: null);
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
                Output.Line("kernel=enabled（运行能力由内核解析，每次回合取租约）");
            }
            else
            {
                Output.Line("kernel=disabled（DOUGONG_KERNEL=0，直连路径）");
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
            Output.Line("MCP：未配置（DOUGONG_MCP_SERVERS 开启）");

        var messages = new List<AgentMessage>(history);
        var turn = 0;

        // 底部固定区域（活动行 + 状态栏）。三个条件同时满足才启用：
        // 富终端、没被 DOUGONG_TUI=0 关掉、输出没被重定向。
        // 重定向那一层由 LiveRegion.Create 内部兜住，这里只表达用户意图。
        var tuiRequested = Environment.GetEnvironmentVariable("DOUGONG_TUI") is not ("0" or "false");

        // 推理内容默认不显示：Plain 档必须与改造前逐字一致，
        // 而推理里常带未定稿的自我纠错，当正文显示会误导。
        var showReasoning = Environment.GetEnvironmentVariable("DOUGONG_SHOW_REASONING") is "1" or "true";

        using var liveRegion = LiveRegion.Create(
            Output.Capability, force: tuiRequested ? null : false);

        // 上下文占用在一轮开始时算一次，泵线程只读这个缓存的整数。
        //
        // **不能让泵线程直接枚举 messages**：那个 List 由主线程在轮末追加，
        // List<T> 在并发枚举下会抛 InvalidOperationException，
        // 而它抛出在后台线程上——表现为整个进程莫名退出，栈里看不到业务代码。
        // 一轮之内历史不会变（本轮输入在轮末才追加），所以缓存是准确的。
        var contextTokens = -1;   // 主线程写、泵线程读；int 读写本身原子

        await using var activity = new ActivityReporter(
            liveRegion,
            config.Model,
            composition.ContextWindow,
            () => contextTokens >= 0 ? contextTokens : null);

        // 让所有正文输出自动与底部区域协作：写之前擦、写之后画。
        Output.LiveRegion = liveRegion;

        while (true)
        {
            Output.Text("> ");
            var input = Console.ReadLine();
            if (input is null) break;                       // EOF
            if (string.IsNullOrWhiteSpace(input)) continue;

            if (input.StartsWith('/'))
            {
                if (!HandleCommand(
                        input, messages, sessionPath, ledger, sessionId, composition,
                        swappable, models, ref activeProvider))
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
            var reasoningText = new StringBuilder();
            var turnStartedAt = System.Diagnostics.Stopwatch.StartNew();

            // 工具行是否延后到本轮流结束再输出。
            //
            // **Plain 档必须为 false**：那一档的输出要与改造前逐字一致，
            // 包括工具行插在流式文本中间的这个位置。改成延后输出会让 diff 变红。
            //
            // Rich/Narrow 档为 true：工具调用不再打断正文，整轮结束后统一列出。
            var deferToolLines = Output.Capability.SupportsDecoration;
            var deferredTools = new List<(string ToolId, ToolDispatchResult Result)>();

            try
            {
                // 必须在拉取第一个事件之前启动：等待模型是整轮最长的静默段，
                // 而这段时间一个事件都不会来。放在循环里就永远不会按时亮起。
                // 轮初沿用上一轮的值；SDK 的 ContextUsageReported 到达后覆盖。
                activity.StartTurn();

                await foreach (var evt in runtime.RunAsync(request))
                {
                    activity.OnEvent(evt);

                    switch (evt)
                    {
                        case AgentEvent.ContextUsageReported usage:
                            // SDK 每轮报一次**估算**的上下文构成。用它而不是自己算：
                            // 自算的漏了系统提示与工具 schema，而这两项的开销
                            // 随工具数量变化，CLI 这边拿不到。
                            // 总量为 null 表示压缩后还没有可信基准——那时不显示百分比。
                            //
                            // 也别用 UsageLedger 的会话累计值：那是每次调用的
                            // 输入+输出累加，三轮对话后可能是实际上下文的三倍，
                            // 进度条会一路顶到 100%。一个看起来精确的假数字，
                            // 比不显示更糟。
                            contextTokens = usage.EstimatedTotalTokens ?? -1;
                            break;

                        case AgentEvent.TextDelta delta:
                            assistantText.Append(delta.Text);
                            Output.Text(delta.Text);
                            break;

                        case AgentEvent.ReasoningDelta reasoningDelta:
                            // **默认不显示**，两个理由：
                            //   1. Plain 档必须与改造前逐字一致（45 个套件钉着），
                            //      多写一段推理内容就破了这条底线
                            //   2. 推理里常带未定稿的自我纠错，当正文显示会误导
                            // 想看的人用 DOUGONG_SHOW_REASONING=1 打开。
                            reasoningText.Append(reasoningDelta.Text);
                            if (showReasoning)
                                Output.Muted(reasoningDelta.Text);
                            break;

                        case AgentEvent.ToolCallRequested call:
                            if (deferToolLines)
                            {
                                // 延后档：正文不被打断，这里只记录。
                                continue;
                            }

                            Output.Line();
                            Output.Line($"  → 调用 {call.ToolId} {Truncate(call.Arguments.GetRawText())}");
                            break;

                        case AgentEvent.ToolCallCompleted done:
                            if (deferToolLines)
                            {
                                deferredTools.Add((done.Result.ToolId, done.Result));
                                continue;
                            }

                            Output.Line($"  ← {done.Result.Outcome}" +
                                (done.Result.Diagnostic is { } d ? $"（{d}）" : string.Empty));
                            break;

                        case AgentEvent.ContextCompacted compacted:
                            Output.Line();
                            Output.Line(compacted.Applied
                                ? $"  ⇄ 已压缩 {compacted.RemovedMessages} 条历史"
                                  + $"（估算 {compacted.EstimatedTokensBefore} → {compacted.EstimatedTokensAfter} tokens）"
                                : $"  ⇄ 未压缩：{compacted.Diagnostic}");
                            break;

                        case AgentEvent.RunFinished finished:
                            Output.Line();
                            if (finished.Outcome != AgentRunOutcome.Completed)
                                Output.Diagnostics($"  [结束：{finished.Outcome}] {finished.Diagnostic}");
                            break;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                Output.Line();
                Output.Diagnostics("  [已取消]");
            }

            // 先收起底部区域再打汇总：区域还在时写正文，光标位置会被它挤乱。
            await activity.FinishTurnAsync();

            // 延后档：本轮流结束后统一列出工具行 + 一行汇总。
            if (deferToolLines)
            {
                Output.PrintToolSummary(deferredTools, turnStartedAt.Elapsed);
            }

            if (assistantText.Length > 0)
                messages.Add(new AgentMessage(AgentRole.Assistant, assistantText.ToString()));
            messages.Add(new AgentMessage(AgentRole.User, input));
            Output.Line();
        }

        return 0;
    }

    private static bool HandleCommand(
        string input, List<AgentMessage> history, string sessionPath,
        UsageLedger ledger, string sessionId, SessionComposition composition,
        SwappableChatClient swappable, ProviderCatalog models, ref string activeProvider)
    {
        var trimmed = input.Trim();

        // /model 带参数，不能先 ToLowerInvariant 整串再匹配——
        // provider 名是用户起的，大小写有意义（虽然查找时忽略），
        // 但整串小写会让回显的名字与用户写的不一致。
        if (trimmed.StartsWith("/model", StringComparison.OrdinalIgnoreCase))
        {
            HandleModelCommand(trimmed, swappable, models, ref activeProvider);
            return true;
        }

        switch (trimmed.ToLowerInvariant())
        {
            case "/exit":
            case "/quit":
                return false;

            case "/help":
                Output.Line("  /status   显示本次会话的完整配置（路径、开关、内核、MCP）");
                Output.Line("  /exit     退出");
                Output.Line("  /history  查看内存中的历史条数（会话日志以 JSONL 为准）");
                Output.Line("  /where    显示当前会话文件");
                Output.Line("  /usage    显示本会话的 token 用量与调用次数");
                Output.Line("  /memory   显示 L1 偏好记忆（含未启用的候选）");
                Output.Line("  /model    列出全部 provider（/model <名字> 切换）");
                return true;

            case "/history":
                Output.Line($"  内存历史 {history.Count} 条；持久化记录见会话 {sessionId}。");
                return true;

            case "/where":
                Output.Line($"  {sessionPath}");
                return true;

            case "/memory":
                PrintMemory(composition);
                return true;

            case "/usage":
                PrintUsage(ledger, sessionId);
                return true;

            case "/status":
                PrintStatus(sessionPath, sessionId, composition);
                return true;

            default:
                Output.Line($"  未知命令：{input}");
                return true;
        }
    }

    /// <summary>
    /// <c>/model</c>：列出或切换 provider。
    /// </summary>
    /// <remarks>
    /// <b>为什么只列出不删除</b>：这里改的是运行中的选择，不改配置文件。
    /// 会话结束就回到配置里的 <c>default_provider</c>——用户临时试一个端点，
    /// 不该被永久记住。真要改默认值就去改配置，那是有意识的动作。
    /// <para>
    /// <b>密钥状态只报"有没有找到"，绝不打印值</b>。这条与
    /// <c>--doctor</c> 的密钥检查同一口径，见 architecture.md §5.1。
    /// </para>
    /// </remarks>
    private static void HandleModelCommand(
        string input, SwappableChatClient swappable, ProviderCatalog models, ref string activeProvider)
    {
        var argument = input.Length > "/model".Length
            ? input["/model".Length..].Trim()
            : string.Empty;

        if (argument.Length == 0)
        {
            PrintModelList(models, activeProvider);
            return;
        }

        // `/model <名字>`：切换。名字来自用户配置，大小写不敏感查找。
        var target = models.Find(argument);
        if (target is null)
        {
            Output.Line($"  没有名为 '{argument}' 的 provider。");
            PrintModelList(models, activeProvider);
            return;
        }

        // 换之前必须先确认新 provider 能真的用：缺模型名或密钥时直接换过去，
        // 会让下一次对话以一个看不出原因的 401/404 失败。
        // **两项都要查**，不能查到一个就返回：只报模型不报密钥的话，
        // 用户补上 model 再切一次，才被告知还缺密钥——两轮才能配好。
        var problems = new List<string>();

        if (string.IsNullOrWhiteSpace(target.Model))
        {
            problems.Add(
                $"没有配置 model（在配置文件的 [providers.{target.Name}] 段里加 "
                + "model = \"<模型名>\"）");
        }

        if (target.ApiKeyEnvironmentVariable is { Length: > 0 } keyName
            && Environment.GetEnvironmentVariable(keyName) is not { Length: > 0 })
        {
            problems.Add($"需要环境变量 {keyName}，但它没有设置（export {keyName}='<你的密钥>'）");
        }

        if (problems.Count > 0)
        {
            Output.Line($"  provider '{target.Name}' 不能切换：");
            foreach (var problem in problems) Output.Line($"    · {problem}");
            return;
        }

        var next = ChatClientFactory.Create(target);
        var previous = swappable.Swap(next);
        activeProvider = target.Name;

        // 释放旧连接器：它是 HttpClientChatClient，持有自己的 HttpClient。
        // 不释放会随切换次数累积。
        // IChatClient 本身没有实现 IAsyncDisposable，所以要按类型判断——
        // 直接 .DisposeAsync() 编译不过，而强转后不判空会在第三方连接器上炸。
        if (!ReferenceEquals(previous, next))
        {
            if (previous is IAsyncDisposable asyncDisposable)
                _ = asyncDisposable.DisposeAsync();
            else if (previous is IDisposable disposable)
                disposable.Dispose();
        }

        Output.Line($"  已切到 provider '{target.Name}'：{target.Model}"
            + $"（{target.Protocol}，{target.BaseUrl}）");
        Output.Line("  本次会话有效；退出后回到配置里的 default_provider。");
    }

    /// <summary>列出全部 provider 与当前选中项。</summary>
    private static void PrintModelList(ProviderCatalog models, string activeProvider)
    {
        Output.Line($"  provider 列表（来源：{models.SourcePath ?? "内置预设"}）：");
        Output.Line();

        foreach (var provider in models.Providers)
        {
            var active = string.Equals(provider.Name, activeProvider, StringComparison.OrdinalIgnoreCase);
            var marker = active ? "▶" : " ";

            // 模型名可能为空（预设没有模型名），如实显示"未配置"而不是留白——
            // 留白看不出是"没配"还是"显示被截断"。
            var model = string.IsNullOrWhiteSpace(provider.Model) ? "未配置模型" : provider.Model;

            // 密钥状态：只说有没有，不说是什么。
            var keyState = provider.ApiKeyEnvironmentVariable switch
            {
                null or "" => "无需密钥",
                { } name when Environment.GetEnvironmentVariable(name) is { Length: > 0 }
                    => $"{name} 已设置",
                { } name => $"{name} 未设置",
            };

            Output.Line($"  {marker} {provider.Name,-16} {model,-24} {provider.Protocol,-14} {keyState}");
        }

        Output.Line();
        Output.Line("  切换：/model <名字>");
        Output.Line($"  配置文件：{ProviderConfigFile.DefaultPath}");
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
            Output.Line("  记忆未启用。设置 DOUGONG_MEMORY=1 开启。");
            Output.Line("  开启后，历史任务中的偏好会被注入后续对话——"
                + "这是外发数据流，因此默认关闭。");
            return;
        }

        // 同步等待：这是用户主动输入的命令，不是后台路径。
        var all = composition.MemoryStore.ReadArtifactsAsync().GetAwaiter().GetResult();
        if (all.Count == 0)
        {
            Output.Line("  记忆为空。");
            return;
        }

        Output.Line($"  记忆产物 {all.Count} 条（未生效项会说明原因）：");
        Output.Line();

        var now = DateTimeOffset.UtcNow;

        foreach (var artifact in all.Values
            .OrderByDescending(a => MemoryDiagnostics.WhyInactive(a, composition.MemoryWorkspace, now) is null)
            .ThenBy(a => a.Key, StringComparer.Ordinal))
        {
            // 判定与原因都取自与注入器同一处，展示不会与实际注入漂移。
            var reason = MemoryDiagnostics.WhyInactive(
                artifact, composition.MemoryWorkspace, now);

            Output.Line($"  [{(reason is null ? "生效" : "未生效")}] {artifact.Key} = {artifact.Value}");
            Output.Line($"      状态 {artifact.State} · 版本 {artifact.Version}"
                + $" · 作用域 {artifact.Scope}"
                + (string.IsNullOrEmpty(artifact.Workspace) ? "" : $"({artifact.Workspace})"));
            Output.Line($"      来源任务 {artifact.SourceTaskId}"
                + (artifact.ExpiresAt is { } expires ? $" · 有效至 {expires:yyyy-MM-dd HH:mm}" : ""));

            if (reason is not null) Output.Line($"      原因：{reason}");
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
        Output.Line($"dougong {ProductInfo.Version} —— .NET Agent Harness 参考宿主");
        Output.Line();
        Output.Line("用法：");
        Output.Line("  dougong                      启动对话（需要 provider 配置）");
        Output.Line("  dougong model                选择 provider 与模型，写回配置文件");
        Output.Line("  dougong --doctor             启动自检：逐项检查环境并给出下一步");
        Output.Line("  dougong --report             输出诊断信息包（不含密钥与对话内容）");
        Output.Line("  dougong --version            显示版本");
        Output.Line("  dougong --help               显示本帮助");
        Output.Line("  dougong --acp                以 ACP Agent 模式运行（stdout 归协议）");
        Output.Line("  dougong --list               列出会话");
        Output.Line("  dougong --resume <会话>      恢复会话");
        Output.Line("  dougong --branch <会话>      从某条记录分叉");
        Output.Line();
        Output.Line("第一次用：先跑 dougong --doctor 确认环境。");
        Output.Line();
        Output.Line($"provider 配置（推荐写 {ProviderConfigFile.DefaultPath}）：");
        Output.Line("  default_provider = \"deepseek\"");
        Output.Line("  [providers.deepseek]");
        Output.Line("  base_url = \"https://api.deepseek.com\"");
        Output.Line("  model    = \"deepseek-chat\"");
        Output.Line("  key_env  = \"DEEPSEEK_API_KEY\"   # 变量名，不是密钥本身");
        Output.Line();
        Output.Line("  密钥只从环境变量读：export DEEPSEEK_API_KEY='...'");
        Output.Line("  环境变量优先于配置文件。会话内 /model 可切换 provider。");
        Output.Line();
        Output.Line("也可全用环境变量（优先级最高）：");
        Output.Line("  DOUGONG_PROVIDER_BASE    服务地址，如 https://api.deepseek.com");
        Output.Line("  DOUGONG_PROVIDER_MODEL   模型名");
        Output.Line("  DOUGONG_PROVIDER_KEY     密钥");
        Output.Line();
        Output.Line("可选：");
        Output.Line("  DOUGONG_PROVIDER_CONFIG    配置文件路径，默认 ~/.dougong/config.toml");
        Output.Line("  DOUGONG_PROVIDER_PROTOCOL  openai（默认）/ anthropic / responses");
        Output.Line("  DOUGONG_WORKSPACE          工作区目录，默认当前目录");
        Output.Line("  DOUGONG_SESSION_DIR        会话目录，默认 ~/.dougong/sessions");
        Output.Line("  DOUGONG_SHELL=1            启用 shell 工具（默认关闭）");
        Output.Line("  DOUGONG_MEMORY=1           启用 L1 偏好记忆（默认关闭）");
        Output.Line("  DOUGONG_CONTEXT_WINDOW     上下文窗口，设置后启用历史压缩");
        Output.Line("  DOUGONG_COMPACT_TRIGGER_RATIO  压缩触发比例（默认 0.75）");
        Output.Line("  DOUGONG_COMPACT_RETAIN_RATIO   压缩后保留比例（默认 0.16，须小于触发比例）");
        Output.Line("  DOUGONG_TUI=0              关闭底部活动行（默认在真终端里开启）");
        Output.Line("  DOUGONG_LOG=debug          分级日志：error/warn/info/debug");
        Output.Line();
        Output.Line("对话中可用：/help  /status  /exit  /usage  /where  /memory  /model");
        Output.Line();
        Output.Line("文档：https://github.com/zhanghaojie513/Dougong");
    }

    /// <summary>
    /// <c>/status</c>：本次会话的完整配置。
    /// </summary>
    /// <remarks>
    /// <b>为什么要有它</b>：首页为了不啰嗦，把大量信息折叠掉了（会话路径、
    /// 记忆开关、MCP 数量、终端档位）。用户在出问题时需要一处能看到**全部**
    /// 生效配置的地方，而不必回头翻启动那一屏。
    /// <para>
    /// 所有值都**读实际状态**，不重算环境变量：写"按环境变量应该是 X"
    /// 会和真实行为脱节，那正是这类命令最容易造出来的假信息。
    /// </para>
    /// </remarks>
    private static void PrintStatus(string sessionPath, string sessionId, SessionComposition composition)
    {
        Output.Line($"  会话      : {sessionId}");
        Output.Line($"  文件      : {sessionPath}");
        Output.Line($"  工作区    : {composition.MemoryWorkspace}");
        Output.Line($"  shell 工具: {(composition.ShellEnabled ? "已启用" : "未启用")}");
        Output.Line($"  上下文压缩: {(composition.ContextWindow > 0
            ? $"{composition.ContextWindow} tokens"
            : "未启用")}");
        if (composition.ContextWindow > 0)
        {
            // 策略必须可见：两个比例直接决定"会话能跑多长"和"多久花一次摘要调用"，
            // 此前它们只存在于构造参数的默认值里，用户既看不到也改不了。
            Output.Line($"    策略    : 触发 {composition.TriggerRatio:P0}、"
                + $"保留 {composition.RetainRatio:P0}（DOUGONG_COMPACT_*_RATIO 可调）");
        }
        Output.Line($"  L1 记忆   : {(composition.Memory is null ? "未启用" : "已启用")}");
        Output.Line($"  MCP server: {composition.McpServers.Count} 个");

        foreach (var server in composition.McpServers)
            Output.Line($"    · {server.Name}");

        var capability = Output.Capability;
        Output.Line($"  终端      : {capability.Tier}（宽 {capability.Width}，"
            + $"{(capability.IsRedirected ? "已重定向" : "交互")}）");

        var tui = Environment.GetEnvironmentVariable("DOUGONG_TUI") is "0" or "false";
        Output.Line($"  底部活动区: {(tui ? "已关闭（DOUGONG_TUI=0）" : capability.SupportsDecoration
            ? "启用" : "不适用（当前档位）")}");
    }

    private static void PrintUsage(UsageLedger ledger, string sessionId)
    {
        var summary = ledger.Summarize(sessionId);
        if (summary.Calls == 0)
        {
            Output.Line("  本会话还没有模型调用。");
            return;
        }

        Output.Line($"  调用      : {summary.Calls} 次（失败 {summary.FailedCalls}）");
        Output.Line($"  输入      : {summary.InputTokens} tokens");
        Output.Line($"  输出      : {summary.OutputTokens} tokens");

        if (summary.TotalTokens is { } total)
            Output.Line($"  合计      : {total} tokens");
        else
            Output.Line("  合计      : 未知（provider 没有报告用量）");

        if (summary.UnreportedCalls > 0)
        {
            Output.Line(
                $"  ⚠ 未报告  : {summary.UnreportedCalls} 次调用没有用量数据，"
                + $"覆盖率 {summary.ReportedRatio:P0}");
        }

        Output.Line($"  累计耗时  : {summary.TotalElapsed.TotalSeconds:0.0}s");
        Output.Line("  成本      : 未计算（依赖 provider 价目表，本层不猜单价）");
    }

    /// <summary>读取会话列表。只显示文件与序号范围，不解析完整历史。</summary>
    private static void PrintSessionList(string sessionDir)
    {
        var sessions = SessionBranch.List(sessionDir);
        if (sessions.Count == 0)
        {
            Output.Line($"目录下没有会话：{sessionDir}");
            return;
        }

        Output.Line($"{"会话",-28} {"记录",6} {"序号范围",14}  {"最后写入",-20} 备注");
        foreach (var session in sessions)
        {
            var note = session.HasSequenceGap ? "序号缺口（不可续跑）" : string.Empty;
            Output.Line(
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
    /// 找出无法识别的命令行参数；全部认识时返回 null。
    /// </summary>
    /// <remarks>
    /// 存在的理由是一个实测到的静默失败：参数曾全部用 <c>args.Contains</c> 判断，
    /// 拼错的参数被忽略，程序直接进入交互对话——用户敲 <c>--docter</c> 想跑自检，
    /// 实际开了个会话并写了日志文件。
    /// <para>
    /// <b>宁可漏报也不要误报</b>：这个方法的唯一职责是抓"明显不认识"的参数。
    /// 它必须认识两种带值形式（<c>--resume x</c> 与 <c>--resume=x</c>），
    /// 否则合法调用会被判成未知参数——那比原来的静默忽略更糟。
    /// 判据因此写得很窄：只有以 <c>-</c> 开头、且不在白名单里的才算未知；
    /// 不带前缀的裸值是某个开关的取值，交给上面的 <see cref="FindArg"/> 处理。
    /// </para>
    /// </remarks>
    private static string? FindUnknownArgument(string[] args)
    {
        // 无值开关。
        string[] switches =
        [
            "--acp", "--version", "-v", "--help", "-h", "--doctor", "--report", "--list",
        ];

        // 带值开关：后面那个参数是它的值，不是独立参数。
        string[] valued = ["--resume", "--branch"];

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];

            // 带值开关的 `--name=value` 形式：整体合法。
            if (valued.Any(v => arg.StartsWith(v + "=", StringComparison.Ordinal)))
                continue;

            if (Array.IndexOf(valued, arg) >= 0)
            {
                // 跳过它的值。值缺失也不在这里报错——由使用该参数的调用方判断，
                // 那里能给出更具体的提示（例如"--resume 需要一个会话文件"）。
                i++;
                continue;
            }

            if (Array.IndexOf(switches, arg) >= 0) continue;

            // 不是开关（不以 - 开头）却走到这里：它是前面某个带值参数的值。
            // 上面已经跳过成对的值，剩下的是多余的位置参数。
            if (!arg.StartsWith('-')) return arg;

            return arg;
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
        // 与 REPL 入口走同一个解析路径：两个入口的 provider 来源必须一致，
        // 否则会出现"REPL 能连、ACP 连不上"这类只有协议形态不同才暴露的怪事。
        var acpResolveError = ChatClientFactory.TryResolve(null, out var acpCatalog, out _);
        if (acpResolveError is not null)
        {
            Console.Error.WriteLine($"[acp] provider 配置有问题：{acpResolveError}");
            return 2;
        }

        var config = acpCatalog?.Selected;
        if (config is null || string.IsNullOrWhiteSpace(config.Model))
        {
            Console.Error.WriteLine(
                "[acp] 没有可用的 provider：请写 ~/.dougong/config.toml，"
                + "或设 DOUGONG_PROVIDER_BASE / MODEL / KEY 环境变量。");
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

    private static string Truncate(string text) =>
        text.Length <= 120 ? text : text[..120] + "…";



    private static string ResolvePath(string workspace, string path) =>
        Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(workspace, path));
}
