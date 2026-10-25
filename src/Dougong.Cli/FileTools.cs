using System.Text.Json;
using Dougong.Agent;

namespace Dougong.Cli;

/// <summary>
/// 工作区内的最小文件工具集。
/// </summary>
/// <remarks>
/// 独立成文件而不是嵌在 <c>Program</c> 里：它们是**共用装配**的一部分，
/// REPL 与 ACP 两个入口都要用（见 <see cref="SessionComposition"/>）。
/// 嵌套在某个入口里会导致另一个入口拿不到——那正是能力不一致的成因。
/// </remarks>
internal static class FileToolSet
{
    /// <summary>把相对路径解析到工作区内；越界由 dispatcher 的路径守卫在更早阶段拦下。</summary>
    internal static string ResolvePath(string workspace, string relative)
    {
        var root = Path.GetFullPath(workspace);
        var combined = Path.GetFullPath(Path.Combine(root, relative));
        return combined;
    }
}

/// <summary>读取工作区内文件。</summary>
/// <summary>资源键的构造规则：规范化路径，避免同一文件被当成两个资源。</summary>
internal static class ResourceKeys
{
    /// <summary>
    /// 把参数里的路径变成资源键。
    /// </summary>
    /// <remarks>
    /// 必须规范化：`./a.txt` 与 `a.txt` 是同一个文件。若给出两个不同的键，
    /// 该互斥的就没互斥——这比不并发更糟。框架不替工具猜，规范化是工具的责任。
    /// </remarks>
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

internal sealed class ReadFileTool(string workspace) : IAgentTool
{
    public ToolDescriptor Descriptor { get; } = new(
        "read_file", "read_file", "1.0.0", "读取工作区内的文本文件",
        JsonDocument.Parse(
            """{"type":"object","properties":{"path":{"type":"string"}},"required":["path"]}""")
            .RootElement.Clone());

    public ToolPermission DescribePermission(JsonElement arguments) =>
        new(ToolAccess.Read, true,
            [arguments.TryGetProperty("path", out var p) ? p.GetString() ?? "" : ""], [], []);

    /// <summary>
    /// 只读工具声明资源键：多个读可并行，与写同一文件的调用互斥。
    /// </summary>
    /// <remarks>
    /// 此前一律 <see cref="ToolExecutionPlan.Serial"/>，导致两个读也要排队——
    /// 而它们不改任何东西（G5）。
    /// </remarks>
    public ToolExecutionPlan DescribeExecution(JsonElement arguments) =>
        new(ToolConcurrency.Serial)
        {
            Reads = [ResourceKeys.ForPath(arguments)],
        };

    public IReadOnlyList<string> DescribeFileDependencies(JsonElement arguments) =>
        [arguments.GetProperty("path").GetString() ?? ""];

    public async ValueTask<AgentToolResult> ExecuteAsync(
        ToolInvocation invocation, CancellationToken cancellationToken)
    {
        var path = FileToolSet.ResolvePath(
            workspace, invocation.Arguments.GetProperty("path").GetString()!);
        var content = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);

        using var document = JsonDocument.Parse(JsonSerializer.Serialize(new { path, content }));
        return new AgentToolResult(document.RootElement.Clone());
    }
}

/// <summary>写入工作区内文件；声明为工作区独占。</summary>
internal sealed class WriteFileTool(string workspace) : IAgentTool
{
    public ToolDescriptor Descriptor { get; } = new(
        "write_file", "write_file", "1.0.0", "写入工作区内的文本文件",
        JsonDocument.Parse(
            """{"type":"object","properties":{"path":{"type":"string"},"content":{"type":"string"}},"required":["path","content"]}""")
            .RootElement.Clone());

    public ToolPermission DescribePermission(JsonElement arguments) =>
        new(ToolAccess.Write, true,
            [arguments.TryGetProperty("path", out var p) ? p.GetString() ?? "" : ""], [], []);

    /// <summary>
    /// 写工具声明资源键：与读/写同一路径的调用互斥。
    /// </summary>
    /// <remarks>
    /// 仍取 <see cref="ToolConcurrency.WorkspaceExclusive"/>？不——那样任何写都会
    /// 阻塞整个工作区，包括对**其他文件**的读写。资源键的意义正是把这个粒度收窄。
    /// 因此写工具改为 Serial + 资源键：只与同一资源的调用互斥。
    /// </remarks>
    public ToolExecutionPlan DescribeExecution(JsonElement arguments) =>
        new(ToolConcurrency.Serial)
        {
            Writes = [ResourceKeys.ForPath(arguments)],
        };

    public IReadOnlyList<string> DescribeFileDependencies(JsonElement arguments) =>
        [arguments.GetProperty("path").GetString() ?? ""];

    public async ValueTask<AgentToolResult> ExecuteAsync(
        ToolInvocation invocation, CancellationToken cancellationToken)
    {
        var path = FileToolSet.ResolvePath(
            workspace, invocation.Arguments.GetProperty("path").GetString()!);
        var content = invocation.Arguments.GetProperty("content").GetString() ?? string.Empty;

        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(path, content, cancellationToken).ConfigureAwait(false);

        using var document = JsonDocument.Parse(
            JsonSerializer.Serialize(new { path, bytes = content.Length }));
        return new AgentToolResult(document.RootElement.Clone());
    }
}
