using System.Text.Json;

namespace Dougong.Agent;

/// <summary>
/// 资源键的构造规则：规范化路径，避免同一文件被当成两个资源。
/// </summary>
/// <remarks>
/// <b>为什么必须规范化</b>：<c>./a.txt</c> 与 <c>a.txt</c> 是同一个文件。
/// 若给出两个不同的键，该互斥的就没互斥——两个写并发落到同一个文件上，
/// 结果是后写的那份覆盖前一份，而两边都以为成功了。
/// <para>
/// 框架不替工具猜：规范化是工具的责任，因为只有工具知道哪个参数是路径。
/// </para>
/// </remarks>
public static class FileResourceKeys
{
    /// <summary>把参数里的 <c>path</c> 变成资源键。</summary>
    public static string ForPath(JsonElement arguments)
    {
        if (!arguments.TryGetProperty("path", out var path)
            || path.GetString() is not { Length: > 0 } value)
        {
            return string.Empty;
        }

        try
        {
            return "file:" + Path.GetFullPath(value);
        }
        catch (Exception)
        {
            // 无法规范化时退化为原样：宁可少互斥，也不要静默合并两个不同路径。
            return "file:" + value;
        }
    }
}

/// <summary>读取工作区内文件。</summary>
/// <remarks>
/// <b>这个工具住在 SDK 而不是某个宿主里</b>：CLI 与桌面端都需要它，
/// 各写一份必然分叉（路径校验、编码、大小限制）。此前它定义在
/// <c>Dougong.Cli</c>，桌面端只能重写一遍——那正是能力不一致的成因。
/// </remarks>
/// <param name="files">文件能力来源。工具本身不做 IO，只组织参数与结果。</param>
public sealed class ReadFileTool(IFileSystem files) : IAgentTool
{
    private readonly IFileSystem _files = files
        ?? throw new ArgumentNullException(nameof(files));

    public ToolDescriptor Descriptor { get; } = new(
        "read_file", "read_file", "1.0.0", "读取工作区内的文本文件",
        JsonDocument.Parse(
            """{"type":"object","properties":{"path":{"type":"string"}},"required":["path"]}""")
            .RootElement.Clone());

    public ToolPermission DescribePermission(JsonElement arguments) =>
        new(ToolAccess.Read, true,
            [arguments.TryGetProperty("path", out var p) ? p.GetString() ?? "" : ""], [], []);

    /// <summary>只读工具声明资源键：多个读可并行，与写同一文件的调用互斥。</summary>
    public ToolExecutionPlan DescribeExecution(JsonElement arguments) =>
        new(ToolConcurrency.Serial)
        {
            Reads = [FileResourceKeys.ForPath(arguments)],
        };

    public IReadOnlyList<string> DescribeFileDependencies(JsonElement arguments) =>
        [ToolArguments.PathForPermission(arguments)];

    public async ValueTask<AgentToolResult> ExecuteAsync(
        ToolInvocation invocation, CancellationToken cancellationToken)
    {
        var path = ToolArguments.RequiredString(invocation.Arguments, "path");
        var content = await _files.ReadTextAsync(path, cancellationToken).ConfigureAwait(false);

        // 返回**版本**：模型此后要改这个文件时，可以带上它做守卫。
        // 不带版本时也能写，但那会覆盖别人期间的改动。
        var stat = await _files.StatAsync(path, cancellationToken).ConfigureAwait(false);

        using var document = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            path,
            content,
            version = stat?.Version,
        }));
        return new AgentToolResult(document.RootElement.Clone());
    }
}

/// <summary>写入工作区内文件。</summary>
/// <remarks>
/// <b>返回写之前的内容</b>（<see cref="FileWriteOutcome.Before"/>）：
/// 宿主做"改动前后对照"需要它，而写完之后再读一次读到的是新内容，
/// 届时不返回就永远拿不到了。
/// </remarks>
public sealed class WriteFileTool(IFileSystem files) : IAgentTool
{
    private readonly IFileSystem _files = files
        ?? throw new ArgumentNullException(nameof(files));

    public ToolDescriptor Descriptor { get; } = new(
        "write_file", "write_file", "1.0.0", "写入工作区内的文本文件",
        JsonDocument.Parse(
            """
            {"type":"object","properties":{
              "path":{"type":"string"},
              "content":{"type":"string"},
              "expected_version":{"type":"string","description":"可选。带上次读到的版本，文件在此期间被改过则拒绝写入。"}
            },"required":["path","content"]}
            """).RootElement.Clone());

    public ToolPermission DescribePermission(JsonElement arguments) =>
        new(ToolAccess.Write, true,
            [arguments.TryGetProperty("path", out var p) ? p.GetString() ?? "" : ""], [], []);

    /// <summary>写工具与同一路径的读写互斥，但不阻塞其他文件。</summary>
    public ToolExecutionPlan DescribeExecution(JsonElement arguments) =>
        new(ToolConcurrency.Serial)
        {
            Writes = [FileResourceKeys.ForPath(arguments)],
        };

    public IReadOnlyList<string> DescribeFileDependencies(JsonElement arguments) =>
        [ToolArguments.PathForPermission(arguments)];

    public async ValueTask<AgentToolResult> ExecuteAsync(
        ToolInvocation invocation, CancellationToken cancellationToken)
    {
        var path = ToolArguments.RequiredString(invocation.Arguments, "path");
        var content = ToolArguments.RequiredString(invocation.Arguments, "content");

        // 期望版本是可选的。**给了就必须遵守**；没给则不检查，
        // 因为"整文件重写"本身就是一个明确意图（模型说我要这个内容）。
        var expected = invocation.Arguments.TryGetProperty("expected_version", out var version)
            && version.GetString() is { Length: > 0 } value
                ? new FileWriteExpectation.MustMatchVersion(value)
                : null;

        try
        {
            var outcome = await _files
                .WriteTextAsync(path, content, expected, cancellationToken)
                .ConfigureAwait(false);

            using var document = JsonDocument.Parse(JsonSerializer.Serialize(new
            {
                path,
                bytes = content.Length,
                operation = outcome.Operation.ToString().ToLowerInvariant(),
                version = outcome.Version,
                // 只有替换时才有前一份内容；新建时为 null 表示"这个文件之前不存在"。
                previous_content = outcome.Before,
            }));
            return new AgentToolResult(document.RootElement.Clone());
        }
        catch (FileVersionConflictException conflict)
        {
            // **返回失败结果而不是抛异常**：这是模型可以自我纠正的常规情况
            // （重读文件再来），不是环境故障。抛异常会让整轮以 Failed 结束，
            // 而模型其实只需要知道"文件变了"。
            using var document = JsonDocument.Parse(JsonSerializer.Serialize(new
            {
                path,
                error = "version_conflict",
                detail = conflict.Message,
            }));
            return new AgentToolResult(document.RootElement.Clone(), Details: null);
        }
    }
}
