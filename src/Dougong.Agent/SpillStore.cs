using System.Security.Cryptography;
using System.Text;

namespace Dougong.Agent;

/// <summary>
/// 把被截断的工具结果原文落盘，供模型按需取回。
/// </summary>
/// <remarks>
/// <b>解决什么问题</b>：工具结果超过 <see cref="ToolResultLimits.MaxContextChars"/> 时
/// 中间段被丢掉，模型只看到头尾。对"这个文件第 3000 行是什么"这类问题，
/// 丢掉的恰好是答案。而完整原文其实**已经在会话日志里**——只是模型没有路径去读它。
///
/// <para>
/// <b>照 dsh 的 <c>spill</c> 做，核心行为一致</b>：
/// <list type="bullet">
/// <item><b>后端保存原文，返回模型可用的定位符</b>，而不是让模型去猜</item>
/// <item><b>定位符是普通文件路径</b>，模型用已有的 <c>read_file</c> 就能取回——
/// 不需要为取回再造一个工具。dsh 的 <c>spill-policy</c> 也是返回
/// "model-facing locator plus retrieval hint"</item>
/// <item><b>落盘位置在工作区内</b>：否则 <c>read_file</c> 的路径守卫会拒绝，
/// 那个"可取回"的承诺就是假的</item>
/// </list>
/// </para>
///
/// <para>
/// <b>与 dsh 的差别</b>：dsh 把 spill 拆成 seam（<c>ctx.spillStore</c>）+
/// <c>spill-local</c> 后端 + <c>spill-policy</c> 策略三件套，因为它的策略要能被
/// 不同宿主替换。我们只有一个宿主形态（本地 CLI），所以先做具体实现；
/// 将来出现第二个落盘后端时，把 WriteAsync 抽成接口即可——
/// 调用点只有一处（LoopAgentRuntime.AppendToolExchangeAsync）。
/// </para>
///
/// <para>
/// <b>不做自动清理</b>：这些文件会一直留着。理由是它们很小（单条最大几十 KB）、
/// 数量受工具调用次数限制，而"自动删掉模型可能还要读的文件"是更糟的错误。
/// 会话结束后由用户或宿主清理；<c>.dougong/spill</c> 目录本身是可以整个删的。
/// </para>
/// </remarks>
public sealed class SpillStore
{
    /// <summary>落盘目录名（相对工作区）。</summary>
    /// <remarks>
    /// <b>必须在工作区内</b>：<see cref="WorkspacePathGuard"/> 拒绝工作区外的绝对路径，
    /// 落到外面的话模型拿到路径也读不了——而标记里写着"可以读"，那是更坏的结果。
    /// <para>
    /// 名字带点前缀，与项目源码区分；同时它在工作区里，用户一眼能看出是 SDK 写的。
    /// </para>
    /// </remarks>
    public const string DirectoryName = ".dougong/spill";

    /// <summary>存放 spill 文件的顶层目录名（相对工作区）。</summary>
    /// <remarks>
    /// <b>单独暴露是因为有人要靠它判断"这是不是我自己的产物"</b>：
    /// <see cref="GlobTool"/> 会把整个目录当噪声跳过。两处若各写字面量，
    /// 改一处漏一处不会报错——症状是 spill 文件出现在"项目里有哪些文件"里，
    /// 而模型会把它们当成源码。
    /// <para>
    /// 从 <see cref="DirectoryName"/> 派生而不是各写一遍：改一处就够。
    /// </para>
    /// </remarks>
    public static readonly string RootDirectoryName =
        DirectoryName[..DirectoryName.IndexOf('/')];

    private readonly string _directory;

    /// <summary>构造，把 spill 文件放到工作区下的 <c>.dougong/spill</c>。</summary>
    /// <remarks>
    /// <b>在这里就规范化成绝对路径</b>：传入相对路径（比如 <c>"."</c>）时，
    /// 后续每次 <c>Path.Combine</c> 都依赖**当时的**进程工作目录。
    /// 而进程工作目录可能在会话中途被改（<c>cd</c> 类工具、宿主切目录），
    /// 那样同一个 <see cref="SpillStore"/> 会写到两个不同地方，
    /// 而模型拿到的相对路径只对应其中一个。
    /// </remarks>
    public SpillStore(string workspaceRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);
        _directory = Path.GetFullPath(
            Path.Combine(workspaceRoot, ".dougong", "spill"));
    }

    /// <summary>落盘目录的绝对路径。</summary>
    public string DirectoryPath => _directory;

    /// <summary>
    /// 保存一段原文，返回**相对工作区**的路径。
    /// </summary>
    /// <param name="toolId">产生这段内容的工具，只用于文件名可读性。</param>
    /// <param name="content">完整原文。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>相对路径（正斜杠），失败时返回 null。</returns>
    /// <remarks>
    /// <b>返回相对路径而不是绝对路径</b>：绝对路径含用户名，会进模型上下文与
    /// 会话日志，跨机器复制会话时就失效了。相对路径在 <c>read_file</c> 里
    /// 按工作区解析，行为一致且不泄漏用户目录。
    /// <para>
    /// <b>失败返回 null 而不是抛异常</b>：spill 是增强，写不进去不该让整轮失败——
    /// 截断后的结果本身仍然可用，只是少了取回能力。
    /// </para>
    /// </remarks>
    public async ValueTask<string?> WriteAsync(
        string toolId, string content, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);

        try
        {
            System.IO.Directory.CreateDirectory(_directory);

            // 文件名：工具名 + 内容哈希。
            //
            // **不放时间戳**：哈希本身就是内容标识，同样的内容必然同名。
            // 加上时间戳会让"重试同一个工具"产生两个内容完全相同的文件——
            // 而重试正是最常出现重复内容的场景（比如一次 429 后重发）。
            // 第一版就带了时间戳，注释却写着"不会反复落盘"，是注释在撒谎。
            var hash = Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(content)))[..12];

            var safeTool = SanitizeFileName(toolId);
            var name = $"{safeTool}-{hash}.txt";

            var full = Path.Combine(_directory, name);
            if (!File.Exists(full))
            {
                // 一次性写入：先写临时文件再原子改名，
                // 避免模型在写一半时就来读（它拿到的路径是立刻可用的）。
                //
                // 临时名带 GUID：两个并发调用写同一份内容时，
                // 固定 `.tmp` 后缀会让它们互相覆盖半个文件。
                var temp = $"{full}.{Guid.NewGuid():N}.tmp";
                try
                {
                    await File.WriteAllTextAsync(temp, content, Encoding.UTF8, cancellationToken)
                        .ConfigureAwait(false);
                    File.Move(temp, full, overwrite: true);
                }
                finally
                {
                    // 改名成功后临时文件已不在；留着的只可能是失败路径。
                    try { File.Delete(temp); } catch { /* 清理失败不影响结论 */ }
                }
            }

            // 正斜杠：Windows 与 POSIX 都能被 read_file 正确解析，
            // 而且它在提示里看起来是路径而不是转义序列。
            return $"{DirectoryName}/{name}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>把工具名变成安全的文件名片段。</summary>
    private static string SanitizeFileName(string toolId)
    {
        if (string.IsNullOrWhiteSpace(toolId)) return "tool";

        var builder = new StringBuilder(toolId.Length);
        foreach (var ch in toolId)
        {
            builder.Append(char.IsLetterOrDigit(ch) || ch is '-' or '_' ? ch : '_');
        }

        var cleaned = builder.ToString();
        return cleaned.Length == 0 ? "tool" : cleaned[..Math.Min(cleaned.Length, 32)];
    }
}
