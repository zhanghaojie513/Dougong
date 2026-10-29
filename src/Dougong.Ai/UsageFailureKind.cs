namespace Dougong.Ai;

/// <summary>
/// 一次模型调用失败的类别。**用于聚合与告警，不用于重试决策。**
/// </summary>
/// <remarks>
/// <b>为什么需要它</b>：只有 <c>succeeded: true/false</c> 时，"今天失败率 30%"
/// 无法回答"是 key 过期、还是 provider 挂了、还是我们的代码出错"——
/// 这三种的处理方式完全不同（换 key / 等 / 改代码）。
/// <para>
/// <b>分类只依据异常本身，不猜测</b>：判不出来的进
/// <see cref="Unknown"/>，不用最像的那一类凑数。错的分类比没有分类更糟——
/// 它会让排查往错误方向走。
/// </para>
/// <para>
/// <b>每个成员显式赋值，不要依赖自动编号</b>。这个枚举可能被序列化
/// （落到会话日志或指标标签里），插入新成员时若让后续成员自动顺移，
/// 历史数据里的数字就会指向错误的类别——那种错误不会报错，只会让统计静默失真。
/// ApiCompat 会报 <c>CP0011</c>（字段值变化），这只是提醒；
/// 显式赋值让"追加不改变已有值"成为结构上的保证。
/// </para>
/// <para>
/// <b>范围只含 LLM 调用</b>：工具审批拒绝、schema 校验失败、权限不足
/// 都发生在调用模型之外，不计 token，也不该出现在这里。
/// 那些由 <c>ToolDispatchResult.Outcome</c> 表达。
/// </para>
/// </remarks>
public enum UsageFailureKind
{
    /// <summary>调用成功。</summary>
    None = 0,

    /// <summary>
    /// 调用方主动取消（<c>CancellationToken</c> 已触发）。
    /// </summary>
    /// <remarks>
    /// 与 <see cref="Timeout"/> 分开：主动取消是正常操作（用户按了 Ctrl+C），
    /// 不该计入故障率；超时才是需要关注的问题。混在一起会让指标失真。
    /// </remarks>
    Cancelled = 1,

    /// <summary>
    /// 超时：等不到响应，且不是调用方取消的。
    /// </summary>
    /// <remarks>
    /// 判据是"异常是取消类 / 超时类，但调用方的令牌没被触发"。
    /// 这时无法区分"provider 慢"与"网络卡住"，所以不细分成两类——
    /// 猜错会让人去查网络，而实际是 provider 侧的问题。
    /// </remarks>
    Timeout = 2,

    /// <summary>认证失败（HTTP 401 / 403）：密钥无效、过期或无权限。</summary>
    Unauthorized = 3,

    /// <summary>余额或配额不足（HTTP 402 / 429 中的配额类）。</summary>
    /// <remarks>
    /// 402 与 429 都可能是配额，但含义不同：402 通常是钱不够，
    /// 429 是请求太频繁。这里把 402 归入本类，429 归 <see cref="RateLimited"/>。
    /// </remarks>
    QuotaExceeded = 4,

    /// <summary>请求过于频繁（HTTP 429）。</summary>
    RateLimited = 5,

    /// <summary>
    /// 上下文超出模型窗口（provider 判定 input 太长）。
    /// </summary>
    /// <remarks>
    /// <b>必须与其它 400 分开</b>：它是唯一能靠**压缩历史**自动恢复的一类失败。
    /// 混进 <see cref="InvalidResponse"/> 或 <see cref="Unknown"/> 时，
    /// 宿主看不出"这个错误值得重试"，只会当成永久失败。
    /// <para>
    /// <b>为什么不能只靠状态码</b>：超限与参数不合法都返回 400，状态码分不开。
    /// 判据是响应体文案（见 <c>ContextOverflowDetector</c>）。
    /// </para>
    /// <para>
    /// <b>关于枚举值的稳定性</b>：本成员插在中间，后面的值随之顺移。
    /// 这是刻意的——<c>FailureKind</c> 只用于内存比较与指标标签
    /// （标签走 <c>ToString()</c> 而非数字），不落盘、不跨进程，
    /// 因此调整顺序没有兼容风险。若将来要序列化它，
    /// 必须先固定所有值再发布。
    /// </para>
    /// </remarks>
    ContextOverflow = 6,

    /// <summary>
    /// 上下文超出模型窗口（provider 判定 input 太长）。
    /// </summary>
    /// <summary>端点或模型不存在（HTTP 404）。</summary>
    /// <remarks>
    /// 绝大多数情况是地址写错（少了 <c>/v1</c>、协议与路径不匹配）或模型名打错，
    /// 属于配置问题而不是服务故障。Diagnostic 会带地址与协议提示。
    /// </remarks>
    NotFound = 7,

    /// <summary>服务端错误（HTTP 5xx）。</summary>
    ServerError = 8,

    /// <summary>网络层失败：连不上、DNS 解析失败、TLS 失败。</summary>
    /// <remarks>
    /// 与 <see cref="ServerError"/> 分开：前者是本机到服务之间的路不通，
    /// 后者是服务本身出错。排查动作完全不同。
    /// </remarks>
    NetworkError = 9,

    /// <summary>
    /// 响应无法解析：流中断、JSON 不合法、缺少必需字段。
    /// </summary>
    /// <remarks>
    /// 这类失败通常意味着**协议实现与 provider 的实际输出不符**，
    /// 是 SDK 侧要修的问题，不是用户配置问题。所以必须单独分类。
    /// </remarks>
    InvalidResponse = 10,

    /// <summary>判不出来的失败。</summary>
    Unknown = 11,

}
