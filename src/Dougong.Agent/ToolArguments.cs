using System.Text.Json;

namespace Dougong.Agent;

/// <summary>
/// 从工具参数里读字符串，缺字段与类型不符都给出一致的中文说明。
/// </summary>
/// <remarks>
/// <b>为什么需要它</b>：原先四个工具各自写 <c>GetProperty("x").GetString()</c>，
/// 缺字段时抛 <see cref="KeyNotFoundException"/>，消息是英文的
/// <c>The given key was not present in the dictionary.</c>——模型拿到这句话
/// 既不知道缺的是哪个参数，也读不出该补什么，只能瞎猜一次。
/// <para>
/// <b>更糟的是口径不一致</b>：<c>edit_file</c> 对空 <c>old_string</c> 返回
/// 中文失败结果，而缺 <c>path</c> 抛英文异常。同一类问题两种表现，
/// 模型无法形成稳定预期——它学不会"参数错了会怎样"。
/// </para>
/// <para>
/// <b>仍允许抛异常</b>：这是工具内部契约违例（调用方绕过 dispatcher 的
/// schema 校验时才可能发生）。走 dispatcher 时 schema 校验会先拦下并给出
/// 更好的诊断，这里只是让"绕过"那条路不再产出不可读的英文消息。
/// </para>
/// </remarks>
public static class ToolArguments
{
    /// <summary>读必需字符串；缺失或类型不符时抛出带参数名的异常。</summary>
    public static string RequiredString(JsonElement arguments, string name)
    {
        if (!arguments.TryGetProperty(name, out var value))
        {
            throw new InvalidOperationException(
                $"缺少必需参数 '{name}'。请检查调用参数是否完整。");
        }

        if (value.ValueKind is not JsonValueKind.String)
        {
            throw new InvalidOperationException(
                $"参数 '{name}' 必须是字符串，实际是 {value.ValueKind}。");
        }

        return value.GetString() ?? string.Empty;
    }

    /// <summary>读可选字符串；缺失或为 null 时返回 null。</summary>
    /// <remarks>
    /// 空字符串按"未提供"处理：模型传 <c>""</c> 与不传这个参数，
    /// 在可选语义上是同一件事，区别对待只会制造两条要维护的路径。
    /// </remarks>
    public static string? OptionalString(JsonElement arguments, string name)
    {
        if (!arguments.TryGetProperty(name, out var value)) return null;
        if (value.ValueKind is not JsonValueKind.String) return null;

        var text = value.GetString();
        return string.IsNullOrEmpty(text) ? null : text;
    }

    /// <summary>
    /// 读路径参数，供权限与资源键使用；缺失时返回空串。
    /// </summary>
    /// <remarks>
    /// 这里**不抛**：它被 <c>DescribePermission</c> 调用，而那是审批前的描述阶段。
    /// 在描述阶段抛异常会让整次调用以 <c>Failed</c> 结束，
    /// 而参数缺失本应是 <c>RejectedInvalidArguments</c>——
    /// 前者说"工具坏了"，后者说"你的调用不对"，对模型是两条不同的路。
    /// </remarks>
    public static string PathForPermission(JsonElement arguments) =>
        OptionalString(arguments, "path") ?? string.Empty;
}
