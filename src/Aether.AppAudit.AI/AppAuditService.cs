namespace Aether.AppAudit.AI;

/// <summary>
/// 一条结论文本（判定方给的）：是不是博彩 + 是**什么类型的网站** + 理由 + 选定的站点。
/// <para><c>SiteUrl</c> 是判定方查证过的真实地址（页面会把它补成一条候选，来源标 agent）；
/// <c>Judge</c> 是判定方自报的身份（模型档案 / 「聊天 agent」等），只留痕，不参与判定。</para>
/// </summary>
public sealed record AppVerdictResult(
    AuditVerdict Verdict, double? Confidence, string Reason,
    string? SiteType = null, string? SiteUrl = null,
    string? Judge = null);

/// <summary>
/// 「应用审查」的任务板：**只做发布、取任务、写结论三件事，不包含任何判定逻辑**。
/// <para>分工：页面（<c>AppAuditViewModel</c>）调 <see cref="Publish"/> 把名字发布成任务；
/// agent 用工具调 <see cref="Tasks"/> 读任务、自己想办法判定（WebSearch / FetchUrl / 已有知识都行）、
/// 再调 <see cref="RecordVerdict"/> 写回。页面只是这份记录的一个视图，判定方式完全由 agent 决定，
/// 所以换判定方（聊天、自动化、将来的别的入口）不需要改这里一行。</para>
/// </summary>
public sealed class AppAuditService
{
    private readonly AppAuditStore _store;

    public AppAuditService(AppAuditStore store) => _store = store;

    /// <summary>
    /// 发布任务：待发布的进池；<paramref name="includeJudged"/> = true 时把**已判过的也重新入池**
    /// （复核任务：结论不清空，留在历史里，页面显示「待复核」）。返回本次发布了几条。
    /// </summary>
    public int Publish(bool includeJudged = false)
    {
        var published = 0;
        var now = DateTimeOffset.UtcNow;
        _store.Mutate(doc =>
        {
            foreach (var entry in doc.Entries)
            {
                if (entry.IsQueued) continue;          // 已经在池里，不重复发布
                if (entry.HasVerdict && !includeJudged) continue;   // 已判过且没勾复核
                entry.Stage = AuditStage.Published;
                entry.PublishedAt = now;
                entry.LastError = null;
                published++;
            }
        });
        return published;
    }

    /// <summary>单条重回任务池（页面上某行的「重发」，用来单独复核一个名字）。</summary>
    public bool Republish(string id)
    {
        var hit = false;
        _store.Mutate(doc =>
        {
            var entry = doc.Entries.FirstOrDefault(e => e.Id == id);
            if (entry is null) return;
            entry.Stage = AuditStage.Published;
            entry.PublishedAt = DateTimeOffset.UtcNow;
            entry.LastError = null;
            hit = true;
        });
        return hit;
    }

    /// <summary>取待处理任务（先发布的先给），<paramref name="limit"/> 截断——一次别把整张清单塞进上下文。</summary>
    public IReadOnlyList<AppAuditEntry> Tasks(int limit = 20)
        => _store.Entries
            .Where(e => e.IsQueued)
            .OrderBy(e => e.PublishedAt ?? DateTimeOffset.MinValue)
            .ThenBy(e => e.AppName, StringComparer.CurrentCultureIgnoreCase)
            .Take(Math.Clamp(limit, 1, 200))
            .ToList();

    /// <summary>任务池里还有几条。</summary>
    public int QueuedCount => _store.Entries.Count(e => e.IsQueued);

    /// <summary>
    /// 把一次判定写入记录：**追加历史 + 覆盖当前结论 + 出池（Done）**。
    /// <para>页面上的「重发」和聊天里的 agent 工具（<c>SaveAppVerdict</c>）走的是同一条写入路径——
    /// 所以「agent 判的」和「复核后重判的」落在同一份记录里，历史里也分得清是谁判的。</para>
    /// </summary>
    public void RecordVerdict(string id, AppVerdictResult verdict)
    {
        _store.Mutate(doc =>
        {
            var entry = doc.Entries.FirstOrDefault(e => e.Id == id);
            if (entry is null) return;

            // 判定方给的站点若不在候选里，补成一条候选（来源 agent）——证据链上要看得见它从哪来。
            if (!string.IsNullOrWhiteSpace(verdict.SiteUrl)
                && entry.Candidates.All(c => !string.Equals(c.Url, verdict.SiteUrl, StringComparison.OrdinalIgnoreCase)))
            {
                entry.Candidates.Add(new AppSiteCandidate
                {
                    Url = verdict.SiteUrl!,
                    Title = "判定时给出的站点",
                    Source = "agent",
                });
            }

            // 旧结论进历史，当前字段只是「最近一次」——调标准/换判定方重判时能看出结论怎么变的。
            entry.Attempts.Add(new AppVerdictAttempt
            {
                At = DateTimeOffset.UtcNow,
                Verdict = verdict.Verdict,
                SiteType = verdict.SiteType,
                SiteUrl = verdict.SiteUrl,
                Confidence = verdict.Confidence,
                Reason = verdict.Reason,
                Judge = verdict.Judge,
                CandidateCount = entry.Candidates.Count,
            });

            entry.Verdict = verdict.Verdict;
            entry.SiteType = verdict.SiteType;
            if (!string.IsNullOrWhiteSpace(verdict.SiteUrl)) entry.PrimaryUrl = verdict.SiteUrl;
            entry.Confidence = verdict.Confidence;
            entry.Reason = verdict.Reason;
            entry.Stage = AuditStage.Done;
            entry.LastError = null;
            entry.UpdatedAt = DateTimeOffset.UtcNow;
        });
    }
}
