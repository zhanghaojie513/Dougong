using System.Text;
using System.Text.Json;

namespace Dougong.Agent;

/// <summary>
/// 逐行读会话文件，统一处理格式行与坏行。
/// </summary>
/// <remarks>
/// <b>为什么需要它</b>：这个逻辑此前手写在**六处**（两写四读），每处都要
/// 记得跳过 <see cref="SessionFormat.Prefix"/> 开头的格式行。加一个格式行的
/// 代价是改六处，而漏一处的症状全部是"数据看起来没了"：
/// <list type="bullet">
/// <item>漏在序号扫描 → 老会话的历史全部消失</item>
/// <item>漏在历史读取 → 恢复后没有历史</item>
/// <item>漏在分支复制 → "分支点 #2 不存在"</item>
/// <item>漏在列表统计 → 记录数显示 0</item>
/// <item>漏在分支写入 → 分支文件无格式声明，将来按错的格式解析</item>
/// </list>
/// 实际开发中，这些全都在同一轮里真实发生过。
///
/// <para>
/// <b>抽象点是"逐行产出已解析记录"</b>，不是"读文件"：各调用方对记录的
/// 使用完全不同（算序号、建历史、复制行、统计缺口），但"哪些行是记录、
/// 哪些行该跳、坏行怎么办"是一致的。
/// </para>
///
/// <para>
/// <b>坏行一律停止，不跳过</b>：JSONL 是 append-only 的，坏行只可能出现在
/// <b>末尾</b>（写一半崩溃）。中间出现坏行说明文件被外部改动——那时继续往后读
/// 会把两段不相干的历史拼在一起，比停下更危险。
/// </para>
/// </remarks>
internal static class SessionRecordReader
{
    /// <summary>
    /// 逐行读出一行会话文件。
    /// </summary>
    /// <param name="line">原始行（含格式行，供需要保留它的调用方使用）。</param>
    /// <param name="record">
    /// 解析出的记录；<see cref="SessionLineKind.Record"/> 时非 null。
    /// </param>
    /// <returns>
    /// 这一行的种类。**三种结果必须分开处理**——把 <see cref="SessionLineKind.Stop"/>
    /// 与 <see cref="SessionLineKind.Skip"/> 搞混，就是一个静默 bug：
    /// 前者要停止读取，后者要继续。
    /// </returns>
    /// <remarks>
    /// <b>为什么用枚举而不是"bool + 可空 out"</b>：那个形态有两个出口
    /// （返回值 + 参数是否为 null），调用方很容易只判一个。
    /// 枚举强迫写 <c>switch</c>，漏一个分支编译期就报。
    /// <para>
    /// <b>格式行返回 <see cref="SessionLineKind.Skip"/>，原始行仍在 <paramref name="line"/> 里</b>：
    /// 分支复制需要保留源文件的格式行并带到新文件。若这里直接吞掉，那条信息就丢了。
    /// </para>
    /// </remarks>
    internal static SessionLineKind Read(
        string? line, out SessionRecordLine? record)
    {
        record = null;

        if (line is null) return SessionLineKind.End;              // 文件结束
        if (string.IsNullOrWhiteSpace(line)) return SessionLineKind.Skip;   // 空行

        // 格式行不是记录：跳过，但原始行仍在调用方手里。
        if (line.StartsWith(SessionFormat.Prefix, StringComparison.Ordinal))
            return SessionLineKind.Skip;

        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;

            if (!root.TryGetProperty("Sequence", out var sequence)
                || sequence.ValueKind is not JsonValueKind.Number)
            {
                // 没有序号：不是本格式的记录线，当作坏行停止。
                return SessionLineKind.Stop;
            }

            var kind = root.TryGetProperty("Kind", out var kindElement)
                ? kindElement.GetString() ?? string.Empty
                : string.Empty;

            var payload = root.TryGetProperty("Payload", out var payloadElement)
                ? payloadElement.Clone()
                : default;

            record = new SessionRecordLine(
                sequence.GetInt64(), kind, payload, line, root.Clone());
            return SessionLineKind.Record;
        }
        catch (JsonException)
        {
            // 尾部半条记录：停止，已提交部分仍然有效。
            return SessionLineKind.Stop;
        }
    }

    /// <summary>判断一行是否为格式行。</summary>
    /// <remarks>
    /// 单独暴露是因为写入侧也要判断（分支复制要把源文件的格式行写进新文件）。
    /// </remarks>
    internal static bool IsFormatLine(string line) =>
        line.StartsWith(SessionFormat.Prefix, StringComparison.Ordinal);

    /// <summary>构造本次要写入的格式行（含换行）。</summary>
    internal static string FormatLine(string? inherited) =>
        (inherited ?? $"{SessionFormat.Prefix}{SessionFormat.Current}") + "\n";

    /// <summary>
    /// 校验文件首行的版本；空文件返回是否应写入格式行。
    /// </summary>
    /// <remarks>
    /// <b>与 <see cref="Read"/> 分开</b>：版本校验必须发生在**逐行读取之前**。
    /// 顺序反了的话，高版本文件会被当成坏行而静默停在第一行——
    /// 那正是整套机制要防的症状。
    /// </remarks>
    /// <param name="firstLine">文件首行；null 表示空文件。</param>
    /// <param name="path">文件路径，用于异常消息。</param>
    /// <param name="needsFormatLine">空文件时为 true，调用方应写入格式行。</param>
    /// <exception cref="SessionFormatException">版本不兼容或版本行无效。</exception>
    internal static void ValidateHeader(string? firstLine, string path, out bool needsFormatLine)
    {
        needsFormatLine = false;

        if (string.IsNullOrWhiteSpace(firstLine))
        {
            needsFormatLine = true;
            return;
        }

        // 没有版本行的旧文件：按当前版本处理。它们本来就是那一版，
        // 而这个缺省不需要解码器。
        if (!IsFormatLine(firstLine)) return;

        var raw = firstLine[SessionFormat.Prefix.Length..].Trim();

        // **非正整数一律拒绝**：`abc`、`0`、`-5` 都说明这一行没被正确理解。
        // 只判 `> Current` 会让 0 与负数通过，那是"读不懂却当成合法"。
        if (!int.TryParse(raw, out var fileVersion) || fileVersion < 1)
        {
            throw new SessionFormatException(0, path,
                $"版本行内容无效：'{raw}'。这不是文件损坏——"
                + "文件可能被手工编辑过，或来自非本项目的写入。");
        }

        if (fileVersion > SessionFormat.Current)
            throw new SessionFormatException(fileVersion, path);
    }
}

/// <summary>一行已解析的会话记录。</summary>
/// <remarks>
/// <paramref name="Raw"/> 与 <paramref name="Root"/> 都保留：分支复制要写原文
/// （保证字节不变），需要结构化访问的地方用 <paramref name="Root"/>。
/// </remarks>
/// <param name="Sequence">存储分配的序号。</param>
/// <param name="Kind"><see cref="StoreRecordKind"/> 的序列化名。</param>
/// <param name="Payload">记录载荷。</param>
/// <param name="Raw">原始行文本。</param>
/// <param name="Root">解析后的 JSON 根。</param>
internal sealed record SessionRecordLine(
    long Sequence,
    string Kind,
    JsonElement Payload,
    string Raw,
    JsonElement Root);

/// <summary>一行会话文件的种类。</summary>
/// <remarks>
/// <b>三态而不是两态</b>：<see cref="Skip"/> 与 <see cref="Stop"/> 都是从
/// "没有记录"里分出来的，但后续动作相反——前者继续读，后者停止。
/// 用 bool 表达时这两者都落在 <c>false</c>/<c>null</c> 上，调用方很容易
/// 只判一个，而漏判的后果是"读到坏行还继续往后拼"。
/// </remarks>
internal enum SessionLineKind
{
    /// <summary>解析出一条记录。</summary>
    Record,

    /// <summary>空行或格式行：不是记录，继续读。</summary>
    Skip,

    /// <summary>坏行：停止读取，已读出的部分仍然有效。</summary>
    Stop,

    /// <summary>文件结束。</summary>
    End,
}
