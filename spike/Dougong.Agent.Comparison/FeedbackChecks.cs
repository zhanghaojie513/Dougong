using System.Text;
using System.Text.Json;
using Dougong.Agent;

namespace Dougong.Agent.Comparison;

/// <summary>
/// 消息级反馈的验收。
/// </summary>
/// <remarks>
/// <b>可观察面有两层，都要钉住</b>：
/// <list type="number">
/// <item><b>存储语义</b>：版本守卫、墓碑、幂等删除、备注校验、只追加。
/// 这些是"用户看到的那条分是不是他最后打的那条"。</item>
/// <item><b>它不能碰会话日志</b>：这是本能力最重要的一条约束。
/// 加记录类型会让 <c>Enum.Parse&lt;StoreRecordKind&gt;</c> 抛异常，
/// 整个会话打不开。所以有一条检查专门验"会话文件一个字节都没变"。</item>
/// </list>
/// <para>
/// <b>不验 UI</b>：SDK 不定义按钮、弹窗或 toast。这里只保证"谁来读都能读到同一份值"。
/// </para>
/// </remarks>
public static class FeedbackChecks
{
    public static async Task<int> RunAsync()
    {
        var failures = 0;
        failures += await CheckAsync("feedback-put-then-list-roundtrips", PutThenListAsync);
        failures += await CheckAsync("feedback-put-requires-observed-version", VersionGuardAsync);
        failures += await CheckAsync("feedback-conflict-returns-authoritative-item", ConflictCarriesCurrentAsync);
        failures += await CheckAsync("feedback-noop-keeps-version-and-appends-nothing", NoOpAsync);
        failures += await CheckAsync("feedback-delete-is-idempotent-and-tombstoned", DeleteAsync);
        failures += await CheckAsync("feedback-delete-respects-version", DeleteVersionAsync);
        failures += await CheckAsync("feedback-rejects-blank-and-oversized-note", NoteValidationAsync);
        failures += await CheckAsync("feedback-file-is-append-only-never-rewritten", AppendOnlyAsync);
        failures += await CheckAsync("feedback-never-touches-session-log", SessionLogUntouchedAsync);
        failures += await CheckAsync("feedback-sidecar-is-not-a-session-file", NotASessionFileAsync);
        failures += await CheckAsync("feedback-tail-corruption-does-not-hide-earlier-items", TailCorruptionAsync);
        failures += await CheckAsync("feedback-middle-corruption-is-an-error", MiddleCorruptionAsync);
        failures += await CheckAsync("feedback-target-must-be-model-response", TargetValidationAsync);

        Console.WriteLine(failures == 0
            ? "FEEDBACK: ALL PASS"
            : $"FEEDBACK: {failures} FAILURE(S)");
        return failures;
    }

    /// <summary>写入后读回同一个值。</summary>
    private static async Task<string?> PutThenListAsync()
    {
        using var env = new Env();

        var put = await env.Store.PutAsync(new MessageFeedbackPut(
            7, FeedbackRating.Negative, "  没回答到点上  ", FeedbackCategory.TaskResult));

        if (put is not FeedbackPutResult.Succeeded ok) return $"写入失败：{Describe(put)}";
        if (ok.Item.Note != "没回答到点上") return $"备注没去首尾空白：'{ok.Item.Note}'";
        if (ok.Item.RecordSequence != 7) return "序号没保留";

        var list = await env.Store.ListAsync();
        if (list.Items.Count != 1) return $"应有 1 条，实际 {list.Items.Count}";

        var item = list.Items[0];
        if (item.Category != FeedbackCategory.TaskResult) return "分类没保留";
        if (item.Rating != FeedbackRating.Negative) return "评分没保留";
        if (item.CreatedAt == default) return "创建时间没写";
        return null;
    }

    /// <summary>已存在的条目必须带正确版本才能改。</summary>
    /// <remarks>
    /// <b>这是最容易被简化掉的一条</b>：不传版本就允许覆盖，会让两个宿主
    /// （或两个窗口）互相踩掉对方的修改，而两边都以为自己成功了。
    /// </remarks>
    private static async Task<string?> VersionGuardAsync()
    {
        using var env = new Env();

        // 首次：ifVersion = null 要求"当前不存在"。
        var first = await env.Store.PutAsync(new MessageFeedbackPut(
            3, FeedbackRating.Positive, IfVersion: null));
        if (first is not FeedbackPutResult.Succeeded created) return $"首次写入失败：{Describe(first)}";

        // 再来一次 null 版本：条目已存在，必须冲突。
        var stale = await env.Store.PutAsync(new MessageFeedbackPut(
            3, FeedbackRating.Negative, IfVersion: null));
        if (stale is not FeedbackPutResult.VersionConflict) return $"陈旧版本应冲突，实际 {Describe(stale)}";

        // 用正确版本改：应成功且换版本。
        var updated = await env.Store.PutAsync(new MessageFeedbackPut(
            3, FeedbackRating.Negative, IfVersion: created.Item.Version));
        if (updated is not FeedbackPutResult.Succeeded changed) return $"正确版本应成功，实际 {Describe(updated)}";
        if (changed.Item.Version == created.Item.Version) return "修改后版本号没变";
        if (changed.Item.CreatedAt != created.Item.CreatedAt) return "修改不该改创建时间";

        // 不同序号互不影响：3 的版本不该用于 4。
        var other = await env.Store.PutAsync(new MessageFeedbackPut(
            4, FeedbackRating.Positive, IfVersion: created.Item.Version));
        if (other is not FeedbackPutResult.VersionConflict)
            return $"别的序号不该接受本条目的版本，实际 {Describe(other)}";

        return null;
    }

    private static async Task<string?> ConflictCarriesCurrentAsync()
    {
        using var env = new Env();
        var first = await env.Store.PutAsync(new MessageFeedbackPut(5, FeedbackRating.Positive));
        if (first is not FeedbackPutResult.Succeeded created) return "前置写入失败";

        var conflict = await env.Store.PutAsync(new MessageFeedbackPut(
            5, FeedbackRating.Negative, IfVersion: "not-the-version"));

        if (conflict is not FeedbackPutResult.VersionConflict c) return $"应冲突，实际 {Describe(conflict)}";
        if (c.Current is null) return "冲突没带回当前条目——调用方只能再读一次，而那次读可能又过期了";
        if (c.Current.Version != created.Item.Version) return "带回的不是当前值";
        if (c.Current.Rating != FeedbackRating.Positive) return "带回的评分不是当前值";
        return null;
    }

    /// <summary>值完全相同且版本正确：不追加事件，版本与时间戳都不动。</summary>
    /// <remarks>
    /// <b>负控要点</b>：把这条分支去掉会让文件每次多一行。文件本身仍是合法 JSONL，
    /// 只有"行数"这条断言会红——所以这里必须断言行数，不能只断言返回值。
    /// </remarks>
    private static async Task<string?> NoOpAsync()
    {
        using var env = new Env();
        var first = await env.Store.PutAsync(new MessageFeedbackPut(
            9, FeedbackRating.Positive, "ok", FeedbackCategory.Other));
        if (first is not FeedbackPutResult.Succeeded created) return "前置写入失败";

        var linesBefore = env.LineCount();

        var again = await env.Store.PutAsync(new MessageFeedbackPut(
            9, FeedbackRating.Positive, "ok", FeedbackCategory.Other,
            IfVersion: created.Item.Version));

        if (again is not FeedbackPutResult.Succeeded same) return $"重复写入应成功，实际 {Describe(again)}";
        if (same.Item.Version != created.Item.Version) return "无变化的写入不该换版本";
        if (same.Item.UpdatedAt != created.Item.UpdatedAt) return "无变化的写入不该改更新时间";

        var linesAfter = env.LineCount();
        return linesAfter == linesBefore
            ? null
            : $"无变化的写入多写了 {linesAfter - linesBefore} 行（应只保留原记录）";
    }

    /// <summary>删除留墓碑；重复删除幂等。</summary>
    /// <remarks>
    /// <b>为什么必须是墓碑而不是删行</b>：文件是 append-only 的，
    /// 就地删行意味着重写整个文件——而那会在崩溃时丢掉一半历史。
    /// 墓碑同时保留了"用户改过主意"这件事。
    /// </remarks>
    private static async Task<string?> DeleteAsync()
    {
        using var env = new Env();
        var put = await env.Store.PutAsync(new MessageFeedbackPut(2, FeedbackRating.Positive));
        if (put is not FeedbackPutResult.Succeeded created) return "前置写入失败";

        var linesBefore = env.LineCount();

        var deleted = await env.Store.DeleteAsync(new MessageFeedbackDelete(2, created.Item.Version));
        if (deleted is not FeedbackDeleteResult.Succeeded) return $"删除失败：{Describe(deleted)}";

        // 墓碑必须真的落盘：日志里要有这一行。
        if (env.LineCount() != linesBefore + 1) return "删除没留下记录——早期评分与这次删除都消失了";

        var afterDelete = await env.Store.ListAsync();
        if (afterDelete.Items.Count != 0) return $"删除后仍读到 {afterDelete.Items.Count} 条";

        // 重复删除：成功，且不再追加。
        var linesAfterDelete = env.LineCount();
        var again = await env.Store.DeleteAsync(new MessageFeedbackDelete(2, created.Item.Version));
        if (again is not FeedbackDeleteResult.Succeeded) return $"重复删除应成功，实际 {Describe(again)}";
        if (env.LineCount() != linesAfterDelete) return "重复删除又写了一条记录（应无操作）";

        // 删除后可以用 null 版本重新创建（语义上是"当前不存在"）。
        var recreated = await env.Store.PutAsync(new MessageFeedbackPut(2, FeedbackRating.Negative));
        return recreated is FeedbackPutResult.Succeeded
            ? null
            : $"删除后应能重新创建，实际 {Describe(recreated)}";
    }

    private static async Task<string?> DeleteVersionAsync()
    {
        using var env = new Env();
        var put = await env.Store.PutAsync(new MessageFeedbackPut(6, FeedbackRating.Positive));
        if (put is not FeedbackPutResult.Succeeded created) return "前置写入失败";

        var stale = await env.Store.DeleteAsync(new MessageFeedbackDelete(6, "stale-version"));
        if (stale is not FeedbackDeleteResult.VersionConflict) return $"陈旧版本删除应冲突，实际 {Describe(stale)}";

        var still = await env.Store.ListAsync();
        return still.Items.Count == 1
            ? null
            : "冲突的删除却真的删掉了条目";
    }

    /// <summary>空白备注拒绝，超长备注拒绝，边界值接受。</summary>
    /// <remarks>
    /// <b>按字节不按字符</b>：中文一个字符三字节。用字符数判会让
    /// "看起来没超"的备注在落盘时超出一倍。这里用中文填到边界来验证这条。
    /// </remarks>
    private static async Task<string?> NoteValidationAsync()
    {
        using var env = new Env(maxNoteBytes: 12);

        var blank = await env.Store.PutAsync(new MessageFeedbackPut(1, FeedbackRating.Positive, "   \t "));
        if (blank is not FeedbackPutResult.NoteBlank)
            return $"只有空白的备注应拒绝，实际 {Describe(blank)}";

        // 5 个汉字 = 15 字节 > 12。
        var tooLarge = await env.Store.PutAsync(new MessageFeedbackPut(
            1, FeedbackRating.Positive, "五个汉字啊"));
        if (tooLarge is not FeedbackPutResult.NoteTooLarge large)
            return $"超长备注应拒绝，实际 {Describe(tooLarge)}";
        if (large.MaxBytes != 12) return $"上限应报 12，实际 {large.MaxBytes}";
        if (large.ActualBytes != 15) return $"实际字节应报 15，实际 {large.ActualBytes}";

        // 4 个汉字 = 12 字节，正好等于上限：必须接受（"不超过"而非"小于"）。
        var exact = await env.Store.PutAsync(new MessageFeedbackPut(
            1, FeedbackRating.Positive, "四个汉字"));
        return exact is FeedbackPutResult.Succeeded
            ? null
            : $"恰好等于上限应接受，实际 {Describe(exact)}";
    }

    /// <summary>文件只追加：历史行一个字节都不改。</summary>
    /// <remarks>
    /// <b>只验"最终值对不对"抓不到重写</b>：重写整个文件也会得到正确的最终值，
    /// 而崩溃时丢一半历史。所以这条比较**前 N 字节是否原样保留**。
    /// </remarks>
    private static async Task<string?> AppendOnlyAsync()
    {
        using var env = new Env();

        var first = await env.Store.PutAsync(new MessageFeedbackPut(1, FeedbackRating.Positive, "一天"));
        if (first is not FeedbackPutResult.Succeeded a) return "前置写入失败";
        var prefix = env.RawBytes();

        var second = await env.Store.PutAsync(new MessageFeedbackPut(
            1, FeedbackRating.Negative, "两天", IfVersion: a.Item.Version));
        if (second is not FeedbackPutResult.Succeeded) return "第二次写入失败";

        var after = env.RawBytes();
        if (after.Length <= prefix.Length) return "第二次写入没有追加内容";

        var preserved = after.AsSpan(0, prefix.Length).SequenceEqual(prefix);
        return preserved
            ? null
            : "写入后文件头部被改写——这不是 append-only，崩溃会丢历史";
    }

    /// <summary>
    /// 写反馈不能改动会话日志一个字节。
    /// </summary>
    /// <remarks>
    /// <b>这是本能力最重要的约束</b>。若将来有人把反馈写成日志记录类型，
    /// 旧程序的 <c>Enum.Parse&lt;StoreRecordKind&gt;</c> 会抛异常，
    /// 整个会话打不开——比"看不到评价"严重得多。这条检查专门守住它。
    /// </remarks>
    private static async Task<string?> SessionLogUntouchedAsync()
    {
        using var env = new Env();
        env.WriteSessionLog();

        var before = env.SessionLogBytes();
        var beforeLength = new FileInfo(env.SessionLogPath).Length;

        var put = await env.Store.PutAsync(new MessageFeedbackPut(1, FeedbackRating.Positive, "还行"));
        if (put is not FeedbackPutResult.Succeeded) return "前置写入失败";
        await env.Store.ListAsync();

        var afterLength = new FileInfo(env.SessionLogPath).Length;
        if (afterLength != beforeLength)
            return $"会话日志长度变了：{beforeLength} → {afterLength}";

        var after = env.SessionLogBytes();
        return after.AsSpan().SequenceEqual(before)
            ? null
            : "会话日志内容变了——反馈绝不能写进会话日志";
    }

    /// <summary>伴生文件不能被当成一个会话。</summary>
    /// <remarks>
    /// <b>负控要点</b>：把 <see cref="JsonlMessageFeedbackStore.Suffix"/> 改成
    /// <c>".jsonl"</c> 后，<see cref="SessionBranch.List"/> 会把它列成会话，
    /// 打开时报"格式版本无效"。这条检查要能抓到那个后缀。
    /// </remarks>
    private static async Task<string?> NotASessionFileAsync()
    {
        using var env = new Env();
        env.WriteSessionLog();

        var put = await env.Store.PutAsync(new MessageFeedbackPut(1, FeedbackRating.Positive));
        if (put is not FeedbackPutResult.Succeeded) return "前置写入失败";

        var sessions = SessionBranch.List(env.Directory);
        var extra = sessions.Where(s => s.Path != env.SessionLogPath).ToArray();
        if (extra.Length > 0)
            return $"反馈文件被列成了会话：{string.Join(",", extra.Select(s => Path.GetFileName(s.Path)))}";

        return sessions.Count == 1 ? null : $"应只列出 1 个会话，实际 {sessions.Count}";
    }

    /// <summary>尾部坏行只截断，不隐藏已读到的条目。</summary>
    private static async Task<string?> TailCorruptionAsync()
    {
        using var env = new Env();

        var put = await env.Store.PutAsync(new MessageFeedbackPut(4, FeedbackRating.Positive));
        if (put is not FeedbackPutResult.Succeeded) return "前置写入失败";

        // 模拟崩溃留下的半条记录。
        File.AppendAllText(env.FeedbackPath, "{\"seq\":9,\"rating\":\"pos", Encoding.UTF8);

        var list = await env.Store.ListAsync();
        if (list.Items.Count != 1) return $"已提交的条目应仍可读，实际 {list.Items.Count} 条";
        return list.Truncated ? null : "尾部坏行没有标记 Truncated——用户会以为只打过这些分";
    }

    /// <summary>中间坏行必须报错而不是静默截断。</summary>
    /// <remarks>
    /// <b>与尾部坏行的判据必须分开</b>：append-only 文件的坏行只可能在末尾。
    /// 中间出现坏行说明文件被外部改过，那时"继续读"会把两段不相干的历史拼起来。
    /// </remarks>
    private static async Task<string?> MiddleCorruptionAsync()
    {
        using var env = new Env();

        // **必须造出"坏行后面还有内容"**：只写一条再插入的话，坏行仍落在末尾，
        // 那是"写一半崩溃"的形态，按设计应当只截断不报错。第一版就是这样写的，
        // 于是这条检查验的一直是尾部路径——与它声称的"中间坏行"不是同一件事。
        var first = await env.Store.PutAsync(new MessageFeedbackPut(4, FeedbackRating.Positive, "a"));
        if (first is not FeedbackPutResult.Succeeded created) return "前置写入失败";
        var second = await env.Store.PutAsync(new MessageFeedbackPut(
            4, FeedbackRating.Negative, "b", IfVersion: created.Item.Version));
        if (second is not FeedbackPutResult.Succeeded) return "第二次写入失败";

        var lines = File.ReadAllLines(env.FeedbackPath, Encoding.UTF8).ToList();
        if (lines.Count < 2) return $"前置状态不足：只有 {lines.Count} 行";

        lines.Insert(0, "not json at all");
        File.WriteAllLines(env.FeedbackPath, lines, Encoding.UTF8);

        try
        {
            await env.Store.ListAsync();
            return "中间坏行被静默跳过——会把两段不相干的历史拼在一起";
        }
        catch (MessageFeedbackFormatException)
        {
            return null;
        }
    }

    /// <summary>只能给模型回复打分。</summary>
    /// <remarks>
    /// <b>为什么必须校验</b>：序号写错不会报错，会安静地存一条指向错误位置的分。
    /// 等用户回看"我为什么打了这个分"，那条分已经对不上任何内容。
    /// </remarks>
    private static async Task<string?> TargetValidationAsync()
    {
        using var env = new Env();

        // **不调 WriteSessionLog()**：那会先写一条 UserInput，
        // 于是 ModelResponse 落到序号 3 而不是这里的断言值。
        // 序号是这份检查的整个前提，必须由它自己造出确定的状态。
        var id = Guid.NewGuid().ToString("N");
        await using (var store = await JsonlAgentStore.OpenAsync(env.SessionLogPath, id))
        {
            await store.CommitAsync(new StoreRecord(
                0, StoreRecordKind.UserInput, JsonDocument.Parse("\"hi\"").RootElement.Clone()));
            await store.CommitAsync(new StoreRecord(
                0, StoreRecordKind.ModelResponse, JsonDocument.Parse("{\"text\":\"yo\"}").RootElement.Clone()));
            await store.CommitAsync(new StoreRecord(
                0, StoreRecordKind.ToolCallStarted,
                JsonDocument.Parse("{\"id\":\"c1\",\"tool\":\"read_file\"}").RootElement.Clone()));
        }

        // 序号 1 = UserInput，2 = ModelResponse，3 = ToolCallStarted。

        if (!await MessageFeedbackTargets.IsAssistantMessageAsync(env.SessionLogPath, null, 2))
            return "序号 2 是模型回复，应判定为可评价目标";

        if (await MessageFeedbackTargets.IsAssistantMessageAsync(env.SessionLogPath, null, 1))
            return "序号 1 是用户输入，不该被当成可评价目标";

        if (await MessageFeedbackTargets.IsAssistantMessageAsync(env.SessionLogPath, null, 3))
            return "序号 3 是工具调用，不该被当成可评价目标";

        if (await MessageFeedbackTargets.IsAssistantMessageAsync(env.SessionLogPath, null, 99))
            return "不存在的序号不该被当成可评价目标";

        // 活着的会话：文件被写租约独占（FileShare.None），走传入的 store。
        // 这条也验证了"活路径不靠另开文件句柄"——那条路在这里必然 IOException。
        await using var live = await JsonlAgentStore.OpenAsync(env.SessionLogPath, id);
        if (!await MessageFeedbackTargets.IsAssistantMessageAsync(env.SessionLogPath, live, 2))
            return "有活存储时应能判定，实际判否";

        return null;
    }

    // ── 支撑 ─────────────────────────────────────────────────────

    private static string Describe(object result) => result switch
    {
        FeedbackPutResult.Succeeded s => $"成功（版本 {s.Item.Version[..6]}…）",
        FeedbackPutResult.VersionConflict c => $"版本冲突（当前 {(c.Current is null ? "无" : c.Current.Version[..6] + "…")}）",
        FeedbackPutResult.TargetNotFound t => $"目标不存在（序号 {t.RecordSequence}）",
        FeedbackPutResult.NoteBlank => "备注只有空白",
        FeedbackPutResult.NoteTooLarge l => $"备注过长（{l.ActualBytes}/{l.MaxBytes} 字节）",
        FeedbackDeleteResult.Succeeded => "成功",
        FeedbackDeleteResult.VersionConflict c => $"版本冲突（当前 {(c.Current is null ? "无" : c.Current.Version[..6] + "…")}）",
        _ => result.GetType().Name,
    };

    private sealed class Env : IDisposable
    {
        public Env(int maxNoteBytes = JsonlMessageFeedbackStore.DefaultMaxNoteBytes)
        {
            Directory = Path.Combine(Path.GetTempPath(), $"dougong-feedback-{Guid.NewGuid():N}");
            System.IO.Directory.CreateDirectory(Directory);

            SessionLogPath = Path.Combine(Directory, "1_session.jsonl");
            FeedbackPath = JsonlMessageFeedbackStore.PathFor(SessionLogPath);
            Store = new JsonlMessageFeedbackStore(FeedbackPath, maxNoteBytes);
        }

        public string Directory { get; }

        public string SessionLogPath { get; }

        public string FeedbackPath { get; }

        public JsonlMessageFeedbackStore Store { get; }

        /// <summary>写一个最小的合法会话文件。</summary>
        public void WriteSessionLog()
        {
            if (File.Exists(SessionLogPath)) return;
            File.WriteAllText(
                SessionLogPath,
                $"{SessionFormat.Prefix}{SessionFormat.Current}\n"
                + "{\"Sequence\":1,\"Kind\":\"UserInput\",\"Payload\":\"hi\"}\n",
                Encoding.UTF8);
        }

        public byte[] SessionLogBytes() => File.ReadAllBytes(SessionLogPath);

        public byte[] RawBytes() => File.Exists(FeedbackPath) ? File.ReadAllBytes(FeedbackPath) : [];

        public int LineCount() =>
            File.Exists(FeedbackPath)
                ? File.ReadAllLines(FeedbackPath, Encoding.UTF8).Count(l => l.Length > 0)
                : 0;

        public void Dispose()
        {
            Store.Dispose();
            try { System.IO.Directory.Delete(Directory, recursive: true); }
            catch { /* 清理失败不影响结论 */ }
        }
    }

    private static async Task<int> CheckAsync(string name, Func<Task<string?>> action)
    {
        try
        {
            var failure = await action().ConfigureAwait(false);
            if (failure is null)
            {
                Console.WriteLine($"PASS {name}");
                return 0;
            }

            Console.WriteLine($"FAIL {name}: {failure}");
            return 1;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"FAIL {name}: {ex.GetType().Name}: {ex.Message}");
            return 1;
        }
    }
}
