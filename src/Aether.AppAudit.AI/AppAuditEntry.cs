using System.Text.Json.Serialization;

namespace Aether.AppAudit.AI;

/// <summary>判定结论。<see cref="Unknown"/> = 还没判定过（不是「判为非博彩」）。</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AuditVerdict
{
    /// <summary>尚未判定。</summary>
    Unknown = 0,

    /// <summary>判定为博彩/赌博类站点。</summary>
    Gambling = 1,

    /// <summary>判定为非博彩（正常站点；或压根搜不到可信对应站点）。</summary>
    NotGambling = 2,

    /// <summary>搜到了候选站点但证据不足以定论——要人看（这是**要保留**的结论，不能静默吞成非博彩）。</summary>
    Inconclusive = 3,
}

/// <summary>
/// 单条的任务/结论状态（与结论正交——结论只由判定方给出）。
/// <para>页面只负责「发布任务」（<see cref="Pending"/> → <see cref="Published"/>），
/// 之后由 agent 读取任务、判定、回写结论（→ <see cref="Done"/>）。页面自己不跑判定。</para>
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AuditStage
{
    /// <summary>待发布（名单已导入，还没进任务池）。</summary>
    Pending = 0,

    /// <summary>已发布：在任务池里等 agent 处理。</summary>
    Published = 1,

    /// <summary>完成（<see cref="AppAuditEntry.Verdict"/> 有效）。</summary>
    Done = 4,

    /// <summary>出错（看 <see cref="AppAuditEntry.LastError"/>）。</summary>
    Error = 5,

    // —— 以下是旧版页面内流水线的阶段，只为读得懂老文件而保留，不再写出 ——

    /// <summary>（旧）正在网搜反查站点。</summary>
    Searching = 2,

    /// <summary>（旧）搜到了候选，等判定。</summary>
    Searched = 6,

    /// <summary>（旧）正在判定。</summary>
    Judging = 3,
}

/// <summary>一条候选站点（网搜反查的产物）。</summary>
public sealed class AppSiteCandidate
{
    public required string Url { get; set; }
    public string? Title { get; set; }
    public string? Snippet { get; set; }

    /// <summary>来源（bing / duckduckgo / list-url：清单里直接给的网址）。</summary>
    public string? Source { get; set; }
}

/// <summary>
/// 一次判定的留痕。**判定不是一次定终身**：标准会调、模型会换、搜索结果会变，
/// 所以每次判定都追加一条，当前结论只是「最近一次」——旧的保留，方便对比与回溯。
/// </summary>
public sealed class AppVerdictAttempt
{
    public DateTimeOffset At { get; set; } = DateTimeOffset.UtcNow;
    public AuditVerdict Verdict { get; set; } = AuditVerdict.Unknown;
    public string? SiteType { get; set; }
    public string? SiteUrl { get; set; }
    public double? Confidence { get; set; }
    public string? Reason { get; set; }

    /// <summary>谁判的（模型档案/实现标识）——换模型重判时能看出结论变化的原因。</summary>
    public string? Judge { get; set; }

    /// <summary>判定时的候选数（0 = 没有站点证据，结论只是猜）。</summary>
    public int CandidateCount { get; set; }
}

/// <summary>
/// 一条待审查记录：**应用名字（用户手上的清单）** → 反查到的站点 → 站点类型 + 是否博彩。
/// <para>名字是证据链的起点，之后一切（网搜、判定）都只往这条记录上写，不改名字。</para>
/// </summary>
public sealed class AppAuditEntry
{
    /// <summary>稳定 id（名字规范化：小写 + 去空白）——同名重复导入不会产生重复行。</summary>
    public required string Id { get; set; }

    /// <summary>应用名（用户给的显示名，如 "Sunwin"）。</summary>
    public required string AppName { get; set; }

    /// <summary>清单里顺带写的网址（有的话等于「不用网搜也知道站点」，直接当第一条候选证据）。</summary>
    public string? ShortcutUrl { get; set; }

    /// <summary>网搜候选站点（按相关度排序，第一个是首选）。</summary>
    public List<AppSiteCandidate> Candidates { get; set; } = new();

    /// <summary>最终采用的站点（判定基于它；可人工改）。</summary>
    public string? PrimaryUrl { get; set; }

    /// <summary>站点类型（判定给的短标签：体育博彩 / 视频 / 电商 / 工具 …）——「是什么类型的网站」的答案。</summary>
    public string? SiteType { get; set; }

    public AuditVerdict Verdict { get; set; } = AuditVerdict.Unknown;

    /// <summary>判定置信度 0-1（null = 未给）。</summary>
    public double? Confidence { get; set; }

    /// <summary>判定理由（AI 给的一句话，人要看的就是这句）。</summary>
    public string? Reason { get; set; }

    public AuditStage Stage { get; set; } = AuditStage.Pending;

    /// <summary>最近一次「发布任务」的时间（null = 从没发布过）。</summary>
    public DateTimeOffset? PublishedAt { get; set; }

    public string? LastError { get; set; }

    /// <summary>历次判定（按时间顺序，最后一条 = 当前结论的来源）。</summary>
    public List<AppVerdictAttempt> Attempts { get; set; } = new();

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>结论是否已定（Done 且有非 Unknown 结论）。</summary>
    public bool HasVerdict => Stage == AuditStage.Done && Verdict != AuditVerdict.Unknown;

    /// <summary>
    /// 是否在任务池里等 agent 处理。旧的「网搜中/已搜到/判定中」算进来——
    /// 老版本中途退出的记录不会卡成永远没人管的孤儿。
    /// </summary>
    [JsonIgnore]
    public bool IsQueued => Stage is AuditStage.Published or AuditStage.Searching
        or AuditStage.Searched or AuditStage.Judging;

    /// <summary>还没发布过（新导入、或发布前被挡下）。</summary>
    [JsonIgnore]
    public bool IsDraft => Stage == AuditStage.Pending;

    /// <summary>规范化名字 → id（幂等：同名同 id）。</summary>
    public static string IdFor(string appName)
    {
        var normalized = new string(appName.Trim().ToLowerInvariant()
            .Where(c => !char.IsWhiteSpace(c)).ToArray());
        return normalized.Length == 0 ? "unnamed" : normalized;
    }
}

/// <summary>整份审查结果的持久化形态（`&lt;dataDir&gt;/app-audit.json`）。</summary>
public sealed class AppAuditDocument
{
    /// <summary>格式版本（将来加字段时用；读旧文件不炸）。</summary>
    public int Version { get; set; } = 1;

    /// <summary>最近一次导入清单的时间。</summary>
    public DateTimeOffset? LastImportAt { get; set; }

    public List<AppAuditEntry> Entries { get; set; } = new();
}
