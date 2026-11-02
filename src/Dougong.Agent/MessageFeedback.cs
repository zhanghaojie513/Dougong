using System.Text;

namespace Dougong.Agent;

/// <summary>
/// 一条助手消息的人类评价。
/// </summary>
/// <remarks>
/// <b>解决什么问题</b>：用户对某条回答满意或不满意，这个判断值得留下来——
/// 它是最直接的信号，而会话日志里没有任何字段能表达它。
/// dsh 的 <c>feedback/message-feedback</c> 就是这一层。
///
/// <para>
/// <b>为什么不进模型上下文</b>：这是给人看的元数据，不是对话内容。
/// 让模型知道"用户给上一条打了差评"会让它开始讨好式改写，
/// 而那正是要避免的。本文件的任何类型都不被 <see cref="LoopAgentRuntime"/> 读取。
/// </para>
///
/// <para>
/// <b>按日志序号定位，不按宿主自己的条目 id</b>：宿主渲染用的
/// <c>ConversationItem.Id</c> 是每次进程启动重新生成的，冷启动后对不上任何东西。
/// 只有会话日志里的 <see cref="StoreRecord.Sequence"/> 是跨进程稳定的。
/// 这个选择也是本能力与具体宿主无关的原因——SDK 只说"第几条记录"，
/// 谁渲染、用什么 id，是宿主自己的事。
/// </para>
///
/// <para>
/// <b>与 dsh 的差别</b>：dsh 把评价写进权威会话日志（<c>feedback/message-put</c>）。
/// 我们没有那样做，理由见 <see cref="JsonlMessageFeedbackStore"/> 的类注释——
/// 核心一条是<b>加新记录类型会让旧程序在 <c>Enum.Parse&lt;StoreRecordKind&gt;</c> 抛异常</b>，
/// 整个会话直接读不了。为了一个评价功能付这个代价不划算。
/// </para>
/// </remarks>
public sealed class MessageFeedback
{
    /// <summary>备注的最大字节数（UTF-8）。</summary>
    /// <remarks>
    /// <b>按字节不按字符</b>：与 dsh 的 <c>maxNoteBytes</c> 一致，也与
    /// <see cref="WorkspaceInstructionsSection"/> 的预算口径一致——
    /// 落盘的是字节，按字符限会让中文备注超出一倍。
    /// </remarks>
    public const int DefaultMaxNoteBytes = 8192;
}

/// <summary>人类对一条助手消息的整体判断。</summary>
public enum FeedbackRating
{
    /// <summary>正面。</summary>
    Positive,

    /// <summary>负面。</summary>
    Negative,
}

/// <summary>
/// 评价分类。
/// </summary>
/// <remarks>
/// <b>id 是落盘词汇，不能改</b>：改了之后旧文件里的分类就读不出来了。
/// 表本身照 dsh 的 <c>FeedbackCategory</c> 抄——它是两份实现共用的语义，
/// 而"我们自己另想一套分类"会让两边的统计对不上。
/// </remarks>
public enum FeedbackCategory
{
    /// <summary>任务结果。</summary>
    TaskResult,

    /// <summary>指令遵循。</summary>
    InstructionFollowing,

    /// <summary>产品功能与交互。</summary>
    ProductInteraction,

    /// <summary>服务稳定性与速度。</summary>
    ServiceStability,

    /// <summary>资源与成本。</summary>
    ResourceCost,

    /// <summary>安全、隐私与权限。</summary>
    SecurityPrivacyPermission,

    /// <summary>其它。</summary>
    Other,
}

/// <summary>一条当前生效的评价。</summary>
/// <param name="RecordSequence">
/// 被评价的助手消息：它对应的 <see cref="StoreRecordKind.ModelResponse"/> 记录的序号。
/// </param>
/// <param name="Rating">正面或负面。</param>
/// <param name="Note">可选的说明；写入前已去掉首尾空白，不会是空白串。</param>
/// <param name="Category">可选分类；null 表示未分类。</param>
/// <param name="Version">
/// 不透明版本，只能做相等比较。每次实质修改都换一个新值——
/// 调用方拿它做"我改的是我看到的那一版"的校验。
/// </param>
/// <param name="CreatedAt">首次创建时间。</param>
/// <param name="UpdatedAt">最近一次实质修改时间。</param>
public sealed record MessageFeedbackItem(
    long RecordSequence,
    FeedbackRating Rating,
    string? Note,
    FeedbackCategory? Category,
    string Version,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

/// <summary>一次列表读取的结果。</summary>
/// <param name="Items">当前生效的评价，按首次创建顺序。</param>
/// <param name="Truncated">
/// 文件尾部有读不出来的内容。<b>必须如实告诉宿主</b>：否则用户会以为
/// "只打过两条分"，而实际是后面几条读不出来。
/// </param>
public sealed record MessageFeedbackList(
    IReadOnlyList<MessageFeedbackItem> Items,
    bool Truncated);

/// <summary>
/// 写入或替换一条评价。
/// </summary>
/// <param name="RecordSequence">目标助手消息的日志序号。</param>
/// <param name="Rating">要记录的评价。</param>
/// <param name="Note">可选说明；空白串会被拒绝，不是被当成"没有"。</param>
/// <param name="Category">可选分类。</param>
/// <param name="IfVersion">
/// 观察到的当前版本，或 null 表示"要求当前不存在"。
/// <b>严格乐观并发</b>：与当前版本不等即冲突，即使要写入的值与已存值相同。
/// </param>
public sealed record MessageFeedbackPut(
    long RecordSequence,
    FeedbackRating Rating,
    string? Note = null,
    FeedbackCategory? Category = null,
    string? IfVersion = null);

/// <summary><see cref="IMessageFeedbackStore.PutAsync"/> 的结果。</summary>
public abstract record FeedbackPutResult
{
    /// <summary>已生效；返回完整条目（含新版本）。</summary>
    public sealed record Succeeded(MessageFeedbackItem Item) : FeedbackPutResult;

    /// <summary>
    /// 版本不符。
    /// </summary>
    /// <remarks>
    /// <b>带回权威条目</b>：调用方可以据此直接协调（"原来是别人改成了差评"），
    /// 不必再读一次——而那次读可能又落在下一次修改之后。
    /// </remarks>
    /// <param name="Current">当前条目；null 表示它已被删除。</param>
    public sealed record VersionConflict(MessageFeedbackItem? Current) : FeedbackPutResult;

    /// <summary>目标序号不存在，或它不是一条模型回复（例如指向用户输入）。</summary>
    public sealed record TargetNotFound(long RecordSequence) : FeedbackPutResult;

    /// <summary>备注只有空白。</summary>
    public sealed record NoteBlank : FeedbackPutResult;

    /// <summary>备注超过字节上限。</summary>
    /// <param name="MaxBytes">允许的上限。</param>
    /// <param name="ActualBytes">实际的 UTF-8 字节数。</param>
    public sealed record NoteTooLarge(int MaxBytes, int ActualBytes) : FeedbackPutResult;
}

/// <summary>
/// 删除一条评价。
/// </summary>
/// <param name="RecordSequence">目标助手消息的日志序号。</param>
/// <param name="IfVersion">
/// 观察到的版本。<b>条目已不存在时它被忽略</b>——删除的语义是
/// "删除之后它不存在"，重试应当照样成功。
/// </param>
public sealed record MessageFeedbackDelete(long RecordSequence, string? IfVersion = null);

/// <summary><see cref="IMessageFeedbackStore.DeleteAsync"/> 的结果。</summary>
public abstract record FeedbackDeleteResult
{
    /// <summary>已删除，或本来就不存在（两者的后置条件相同）。</summary>
    public sealed record Succeeded : FeedbackDeleteResult;

    /// <summary>版本不符，未删除。</summary>
    /// <param name="Current">当前条目；null 表示它已经不存在了。</param>
    public sealed record VersionConflict(MessageFeedbackItem? Current) : FeedbackDeleteResult;
}

/// <summary>
/// 评价存储。
/// </summary>
/// <remarks>
/// <b>为什么要有接缝</b>：存储位置是宿主的部署决定（本地文件、数据库、远端服务）。
/// 但<b>校验与并发语义不能跟着换</b>——备注上限、空白拒绝、按版本比较，
/// 这些是契约，换后端也必须一样。所以它们在这里定义，实现只负责落盘。
/// </remarks>
public interface IMessageFeedbackStore
{
    /// <summary>读取当前生效的评价。</summary>
    Task<MessageFeedbackList> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>创建或替换一条评价。</summary>
    Task<FeedbackPutResult> PutAsync(
        MessageFeedbackPut request, CancellationToken cancellationToken = default);

    /// <summary>删除一条评价。</summary>
    Task<FeedbackDeleteResult> DeleteAsync(
        MessageFeedbackDelete request, CancellationToken cancellationToken = default);
}

/// <summary>
/// 评价文件读不懂。
/// </summary>
/// <remarks>
/// <b>它是基础设施故障，不是业务失败</b>，所以抛异常而不是返回结果码：
/// 返回结果码会让调用方以为"这次没改成，重试一下就行"，
/// 而实际是"这个文件我不认识，重试多少次都一样"。
/// </remarks>
public sealed class MessageFeedbackFormatException(string path, string detail)
    : Exception($"反馈文件无法解析：{path}\n{detail}")
{
    /// <summary>文件路径。</summary>
    public string Path { get; } = path;
}

/// <summary>分类与评分的落盘词汇。</summary>
/// <remarks>
/// <b>映射写成显式两张表而不是靠 ToString</b>：枚举名是代码里的标识，
/// 落盘词汇是对外契约。让它们耦合的话，一次无害的重命名就会让旧文件失效。
/// </remarks>
public static class MessageFeedbackVocabulary
{
    /// <summary>评分的落盘文本。</summary>
    public static string ToId(FeedbackRating rating) => rating switch
    {
        FeedbackRating.Positive => "positive",
        FeedbackRating.Negative => "negative",
        _ => throw new ArgumentOutOfRangeException(nameof(rating), rating, "未知评分"),
    };

    /// <summary>解析评分；无法识别时返回 null。</summary>
    public static FeedbackRating? ParseRating(string? id) => id switch
    {
        "positive" => FeedbackRating.Positive,
        "negative" => FeedbackRating.Negative,
        _ => null,
    };

    /// <summary>分类的落盘文本。</summary>
    public static string ToId(FeedbackCategory category) => category switch
    {
        FeedbackCategory.TaskResult => "task-result",
        FeedbackCategory.InstructionFollowing => "instruction-following",
        FeedbackCategory.ProductInteraction => "product-interaction",
        FeedbackCategory.ServiceStability => "service-stability",
        FeedbackCategory.ResourceCost => "resource-cost",
        FeedbackCategory.SecurityPrivacyPermission => "security-privacy-permission",
        FeedbackCategory.Other => "other",
        _ => throw new ArgumentOutOfRangeException(nameof(category), category, "未知分类"),
    };

    /// <summary>解析分类；无法识别时返回 null。</summary>
    public static FeedbackCategory? ParseCategory(string? id) => id switch
    {
        "task-result" => FeedbackCategory.TaskResult,
        "instruction-following" => FeedbackCategory.InstructionFollowing,
        "product-interaction" => FeedbackCategory.ProductInteraction,
        "service-stability" => FeedbackCategory.ServiceStability,
        "resource-cost" => FeedbackCategory.ResourceCost,
        "security-privacy-permission" => FeedbackCategory.SecurityPrivacyPermission,
        "other" => FeedbackCategory.Other,
        _ => null,
    };

    /// <summary>全部分类，按 dsh 声明的顺序。</summary>
    public static IReadOnlyList<FeedbackCategory> AllCategories { get; } =
    [
        FeedbackCategory.TaskResult,
        FeedbackCategory.InstructionFollowing,
        FeedbackCategory.ProductInteraction,
        FeedbackCategory.ServiceStability,
        FeedbackCategory.ResourceCost,
        FeedbackCategory.SecurityPrivacyPermission,
        FeedbackCategory.Other,
    ];
}

/// <summary>评价目标的校验。</summary>
/// <remarks>
/// <b>为什么需要它</b>：评价挂在"第几条记录"上。序号写错（比如把工具调用的序号
/// 当成助手消息）不会报错——它会安静地存下一条指向错误位置的分。
/// 等用户回看"我为什么打了差评"，那条分已经对不上任何内容了。
/// </remarks>
public static class MessageFeedbackTargets
{
    /// <summary>
    /// 指定序号是否是一条模型回复。
    /// </summary>
    /// <remarks>
    /// <b>优先用宿主已经持有的 <paramref name="liveStore"/></b>：会话活着时
    /// 日志文件被写租约独占（<c>FileShare.None</c>），另开一个读句柄必然失败。
    /// 那不是错误，是正常状态。
    /// </remarks>
    /// <param name="sessionLogPath">会话文件路径。</param>
    /// <param name="liveStore">会话当前持有的存储；null 表示会话不在本进程内。</param>
    /// <param name="recordSequence">要校验的序号。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public static async Task<bool> IsAssistantMessageAsync(
        string sessionLogPath,
        IAgentStore? liveStore,
        long recordSequence,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionLogPath);
        if (recordSequence <= 0) return false;

        if (liveStore is not null)
        {
            await foreach (var record in liveStore.ReadCommittedAsync(cancellationToken)
                .ConfigureAwait(false))
            {
                if (record.Sequence == recordSequence)
                    return record.Kind is StoreRecordKind.ModelResponse;
                if (record.Sequence > recordSequence) return false;
            }

            return false;
        }

        await using var stream = new FileStream(
            sessionLogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite,
            bufferSize: 4096, useAsync: true);
        using var reader = new StreamReader(
            stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false);

        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            // 与其它读取方共用同一判据：格式行跳过、坏行停止。
            switch (SessionRecordReader.Read(line, out var parsed))
            {
                case SessionLineKind.End:
                case SessionLineKind.Stop:
                    return false;
                case SessionLineKind.Skip:
                    continue;
            }

            if (parsed!.Sequence == recordSequence)
                return parsed.Kind is nameof(StoreRecordKind.ModelResponse);
            if (parsed.Sequence > recordSequence) return false;
        }

        return false;
    }
}
