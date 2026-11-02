using System.Text;
using System.Text.Json;

namespace Dougong.Agent;

/// <summary>
/// 从会话日志推导"这一轮改了哪些文件"。
/// </summary>
/// <remarks>
/// <b>解决什么问题</b>：模型说自己"改好了"，用户想知道到底动了哪些文件——
/// 尤其是在它连着改了三四个文件之后。dsh 的 <c>deliverables/workspace-changes</c>
/// 就在补这一环。
///
/// <para>
/// <b>照 dsh 的意图做，但判据不同</b>：dsh 用 <c>ctx.workspaceChanges</c>
/// 在每轮开始/结束各做一次文件指纹快照，再求差。我们**直接从会话日志推导**：
/// 写工具的调用参数里就有 <c>path</c>，而那已经落盘了。
/// </para>
///
/// <para>
/// <b>为什么不用指纹快照</b>：
/// <list type="number">
/// <item><b>代价</b>：每轮前后各扫一次工作区。大仓库里这不是免费操作，
/// 而"看一眼改了哪些文件"不该比一次工具调用还贵。</item>
/// <item><b>信息更少</b>：快照只能告诉你"这个文件变了"，日志能告诉你
/// "哪一轮、哪个工具、改了几次"。</item>
/// <item><b>我们已经有日志</b>：dsh 那样做是因为它的日志只记"轮次"不记文件。</item>
/// </list>
/// </para>
///
/// <para>
/// <b>已知的盲区，必须承认</b>：
/// <list type="bullet">
/// <item><b>shell 改了文件不会被发现</b>——命令是自由文本，看不出它动了什么。
/// 这是真实的漏报，不是实现简化。</item>
/// <item><b>工具外的改动不会被发现</b>：用户在编辑器里改了，日志里没有记录。</item>
/// <item><b>只看"成功"的调用</b>：失败的写没有改变文件，报出来是噪音。</item>
/// </list>
/// 前两条正是 dsh 用快照的理由。我们的取舍是"便宜且够用"，
/// 而代价要在文档里写清，不能让用户以为这是完整的变更清单。
/// </para>
/// </remarks>
public static class WorkspaceChanges
{
    /// <summary>写类工具的参数名。</summary>
    /// <remarks>
    /// <b>用工具名而不是"有没有 path 参数"</b>：<c>read_file</c> 也有 path，
    /// 按参数名判断会把所有读操作都算成改动。
    /// </remarks>
    private static readonly HashSet<string> WriteTools = new(StringComparer.Ordinal)
    {
        "write_file",
        "edit_file",
    };

    /// <summary>
    /// 从一个会话文件里读出每轮改动的文件。
    /// </summary>
    /// <param name="sessionPath">会话文件路径。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <remarks>
    /// <b>按"用户输入"切轮</b>：日志里没有显式的轮次编号，而每一轮都从
    /// 一条 <see cref="StoreRecordKind.UserInput"/> 开始。用工具调用去数轮次
    /// 会把"一轮里调了三次工具"算成三轮。
    /// </remarks>
    public static async Task<IReadOnlyList<TurnChanges>> ReadAsync(
        string sessionPath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionPath);

        var turns = new List<TurnChanges>();
        var current = new List<string>();
        string? currentPrompt = null;
        var turnIndex = 0;

        try
        {
            await using var stream = new FileStream(
                sessionPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite,
                bufferSize: 4096, useAsync: true);
            using var reader = new StreamReader(
                stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false);

            while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
            {
                // 统一读取判据：格式行跳过、坏行停止。
                switch (SessionRecordReader.Read(line, out var record))
                {
                    case SessionLineKind.End:
                    case SessionLineKind.Stop:
                        goto finish;
                    case SessionLineKind.Skip:
                        continue;
                }

                if (record is null) continue;

                if (record.Kind is nameof(StoreRecordKind.UserInput))
                {
                    // 上一轮收尾。
                    if (currentPrompt is not null)
                    {
                        turns.Add(new TurnChanges(++turnIndex, currentPrompt, [.. current]));
                        current.Clear();
                    }

                    currentPrompt = ReadUserInput(record.Payload);
                    continue;
                }

                if (record.Kind is not nameof(StoreRecordKind.ToolCallStarted)) continue;

                if (ExtractWrittenPath(record.Payload) is { } path)
                    current.Add(path);
            }

        finish:
            // 最后一轮也要收：否则"刚改完就退出"的那次改动永远看不到。
            if (currentPrompt is not null)
                turns.Add(new TurnChanges(++turnIndex, currentPrompt, [.. current]));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 读不到就返回已解析的部分，不抛——这是展示用的增强。
            return turns;
        }

        return turns;
    }

    /// <summary>
    /// 从 <c>ToolCallStarted</c> 的载荷里取出被写的文件路径。
    /// </summary>
    /// <remarks>
    /// <b>只认写工具</b>：见 <see cref="WriteTools"/> 的注释。
    /// <para>
    /// <b>失败的工具调用也会记录在这里</b>：<c>ToolCallStarted</c> 是"即将执行"，
    /// 此时还不知道成败。要排除失败的需要看配对的 <c>ToolCallCompleted</c>，
    /// 那需要跨记录关联。当前选择是"可能多报"，因为漏报比多报更危险——
    /// 用户看到多一个文件会去核对，看不到漏掉的文件则完全不知情。
    /// </para>
    /// </remarks>
    private static string? ExtractWrittenPath(JsonElement payload)
    {
        if (payload.ValueKind is not JsonValueKind.Object) return null;

        var tool = payload.TryGetProperty("tool", out var t) ? t.GetString() : null;
        if (tool is null || !WriteTools.Contains(tool)) return null;

        // arguments 是一个 JSON 字符串（模型给的原始参数）。
        if (!payload.TryGetProperty("arguments", out var args)) return null;

        var raw = args.ValueKind is JsonValueKind.String ? args.GetString() : args.GetRawText();
        if (string.IsNullOrWhiteSpace(raw)) return null;

        try
        {
            using var document = JsonDocument.Parse(raw);
            return document.RootElement.TryGetProperty("path", out var path)
                && path.ValueKind is JsonValueKind.String
                    ? path.GetString()
                    : null;
        }
        catch (JsonException)
        {
            // 参数不是合法 JSON：跳过。这是模型的输入问题，
            // 由 dispatcher 的 schema 校验负责报错，这里不重复。
            return null;
        }
    }

    /// <summary>读用户输入；两种载荷形态都认。</summary>
    private static string ReadUserInput(JsonElement payload) =>
        payload.ValueKind is JsonValueKind.String
            ? payload.GetString() ?? string.Empty
            : payload.TryGetProperty("text", out var text)
                ? text.GetString() ?? string.Empty
                : string.Empty;
}

/// <summary>一轮的改动摘要。</summary>
/// <param name="Turn">第几轮（从 1 开始，按用户输入切分）。</param>
/// <param name="Prompt">这一轮的用户输入（可能被截断，仅供识别）。</param>
/// <param name="Paths">这一轮写过的文件路径；可能有重复。</param>
public sealed record TurnChanges(
    int Turn,
    string Prompt,
    IReadOnlyList<string> Paths)
{
    /// <summary>去重后的文件列表，保持首次出现顺序。</summary>
    /// <remarks>
    /// <b>去重但保留顺序</b>：同一个文件被改两次只报一次，而顺序反映
    /// "先动哪个后动哪个"——那对理解改动意图有用。
    /// </remarks>
    public IReadOnlyList<string> DistinctPaths =>
        [.. Paths.Distinct(StringComparer.Ordinal)];
}
