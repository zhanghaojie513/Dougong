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

    public ToolExecutionPlan DescribeExecution(JsonElement arguments) => ToolExecutionPlan.Serial;

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

    public ToolExecutionPlan DescribeExecution(JsonElement arguments) =>
        new(ToolConcurrency.WorkspaceExclusive);

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
