using System.Text.Json;

namespace Dougong.Agent;

/// <summary>
/// 按内容替换：把文件里的 <c>old_string</c> 换成 <c>new_string</c>。
/// </summary>
/// <remarks>
/// <b>为什么必须有它</b>：只有 <c>write_file</c> 时，改一行要重写整个文件。
/// 那有三个代价：模型要输出全文（费 token）、容易在无关处改错、
/// 而且没法用版本守卫（全文重写没有"基于哪个版本"的概念）。
///
/// <para>
/// <b>必须要求唯一匹配</b>：<c>old_string</c> 出现多次时**报错，不猜**。
/// 批量替换会一次改坏多处，而模型看不出自己改了什么——它以为只改了一处。
/// 想改多处就分多次调用，每次都看到实际结果。
/// </para>
///
/// <para>
/// <b>走 <see cref="IFileSystem"/> 的版本守卫写</b>：读到的版本与写回时的
/// 版本不一致就失败。防的是"模型读到第 100 行，期间用户手工改了文件，
/// 写回时把用户的改动冲掉"——那种丢数据没有任何提示。
/// </para>
/// </remarks>
/// <param name="files">文件能力来源。</param>
public sealed class EditFileTool(IFileSystem files) : IAgentTool
{
    private readonly IFileSystem _files = files
        ?? throw new ArgumentNullException(nameof(files));

    public ToolDescriptor Descriptor { get; } = new(
        "edit_file", "edit_file", "1.0.0",
        "把文件里出现**唯一一次**的 old_string 替换成 new_string；出现多次或零次时拒绝并说明",
        JsonDocument.Parse(
            """
            {"type":"object","properties":{
              "path":{"type":"string"},
              "old_string":{"type":"string","description":"要被替换的原文，必须在文件里唯一出现"},
              "new_string":{"type":"string","description":"替换后的内容"}
            },"required":["path","old_string","new_string"]}
            """).RootElement.Clone());

    public ToolPermission DescribePermission(JsonElement arguments) =>
        new(ToolAccess.Write, true,
            [arguments.TryGetProperty("path", out var p) ? p.GetString() ?? "" : ""], [], []);

    public ToolExecutionPlan DescribeExecution(JsonElement arguments) =>
        new(ToolConcurrency.Serial)
        {
            Writes = [FileResourceKeys.ForPath(arguments)],
        };

    public IReadOnlyList<string> DescribeFileDependencies(JsonElement arguments) =>
        [arguments.GetProperty("path").GetString() ?? ""];

    public async ValueTask<AgentToolResult> ExecuteAsync(
        ToolInvocation invocation, CancellationToken cancellationToken)
    {
        var path = ToolArguments.RequiredString(invocation.Arguments, "path");

        // old_string 缺失与传空串是**同一个问题**：没有目标文本可定位。
        // 两者都给同一个失败码，而不是一个抛异常一个返回结果——
        // 那会让模型学不到"这种错误会怎样"。
        var oldText = ToolArguments.OptionalString(invocation.Arguments, "old_string") ?? string.Empty;
        // new_string 为空是合法的（删除这段文本），所以这里不能当缺失处理。
        var newText = ToolArguments.OptionalString(invocation.Arguments, "new_string") ?? string.Empty;

        if (oldText.Length == 0)
        {
            // 空串在任何文本里都"匹配无限次"，直接拒绝而不是走后面的计数。
            return Failure(path, "empty_old_string",
                "old_string 不能为空。要整文件重写请用 write_file。");
        }

        // 先取版本：**必须在读内容之前**。反过来的话，读到内容后、
        // 取版本前的那一瞬间文件被改，守卫就失效了。
        var stat = await _files.StatAsync(path, cancellationToken).ConfigureAwait(false);
        if (stat is null)
            return Failure(path, "not_found", $"文件不存在：{path}");

        if (stat.Length > 2 * 1024 * 1024)
        {
            return Failure(path, "too_large",
                $"文件 {stat.Length} 字节，超过 2 MiB 上限。大文件请用 write_file。");
        }

        var original = await _files.ReadTextAsync(path, cancellationToken).ConfigureAwait(false);

        // 统计出现次数。用 IndexOf 循环而不是 Regex：old_string 是字面量，
        // 正则化会引入转义问题，而"改一个普通字符串"不该要求用户懂正则。
        var firstIndex = original.IndexOf(oldText, StringComparison.Ordinal);
        if (firstIndex < 0)
        {
            return Failure(path, "not_found_in_file",
                $"在 {path} 里找不到 old_string。**先用 read_file 确认原文**——"
                + "包括缩进与换行，它们必须完全一致。");
        }

        // **判据必须与计数用同一套语义**。
        //
        // 原实现用 `IndexOf(oldText, firstIndex + 1)` 找第二次（允许重叠），
        // 而报数用 CountOccurrences（按 needle.Length 跳过，不允许重叠）。
        // 结果是 `"aa"` 在 `"aaa"` 里被拒，理由写成"出现 1 次"——
        // 一条自相矛盾的错误消息，模型只能猜哪里不对。
        //
        // 统一按**不重叠**：替换一次后指针前进 oldText.Length，
        // 重叠的那几处本来就替换不到，把它们算进"歧义"没有意义。
        var count = CountOccurrences(original, oldText);
        if (count > 1)
        {
            return Failure(path, "ambiguous",
                $"old_string 在 {path} 里出现 {count} 次，拒绝替换。"
                + "请把 old_string 写长一些，包含足够的上下文使它唯一；"
                + "或分多次调用，每次改一处。");
        }

        var updated = string.Concat(
            original.AsSpan(0, firstIndex), newText,
            original.AsSpan(firstIndex + oldText.Length));

        try
        {
            var outcome = await _files.WriteTextAsync(
                path, updated, new FileWriteExpectation.MustMatchVersion(stat.Version),
                cancellationToken).ConfigureAwait(false);

            using var document = JsonDocument.Parse(JsonSerializer.Serialize(new
            {
                path,
                operation = "edited",
                // 行号按原文算：改之后那个位置可能已经变了，用户要的是"改哪了"。
                line = LineOf(original, firstIndex),
                removed = oldText.Length,
                added = newText.Length,
                version = outcome.Version,
            }));
            return new AgentToolResult(document.RootElement.Clone());
        }
        catch (FileVersionConflictException conflict)
        {
            return Failure(path, "version_conflict", conflict.Message);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
            or DirectoryNotFoundException)
        {
            // **其他写盘失败也要返回失败结果，不是让它冒泡**。
            // 冒泡到 dispatcher 会变成 `Failed` 终态，而那意味着"工具坏了"；
            // 这里的真实情况是"这次写没成"——模型可以重试、换路径、
            // 或告诉用户权限不够。两者对模型是两条不同的路。
            return Failure(path, "write_failed", $"写入 {path} 失败：{ex.Message}");
        }
    }

    /// <summary>
    /// 返回失败结果。
    /// </summary>
    /// <remarks>
    /// <b>返回失败结果而不是抛异常</b>：这些都是模型可以自我纠正的常规情况
    /// （重读文件、写长一点、换个路径）。抛异常会让整轮以 Failed 结束，
    /// 而模型其实只需要知道"这次不行，原因是什么"。
    /// </remarks>
    private static AgentToolResult Failure(string path, string code, string detail)
    {
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            path,
            operation = "rejected",
            error = code,
            detail,
        }));
        return new AgentToolResult(document.RootElement.Clone());
    }

    /// <summary>数一个子串出现多少次。</summary>
    /// <remarks>
    /// 只用于错误消息里报数字。上限 100 足够——
    /// 报"出现 1000 次"和"出现 100 次"对用户的决策没有区别。
    /// </remarks>
    private static int CountOccurrences(string text, string needle)
    {
        var count = 0;
        var index = 0;
        while (count < 100)
        {
            index = text.IndexOf(needle, index, StringComparison.Ordinal);
            if (index < 0) break;
            count++;
            index += needle.Length;
        }

        return count;
    }

    /// <summary>算某个字符下标所在的行号（1 起）。</summary>
    private static int LineOf(string text, int index)
    {
        var line = 1;
        for (var i = 0; i < index && i < text.Length; i++)
            if (text[i] == '\n') line++;

        return line;
    }
}
