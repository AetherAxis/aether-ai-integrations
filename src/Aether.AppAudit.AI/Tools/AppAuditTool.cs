using System.ComponentModel;
using Aether.AppAudit.AI;

namespace Aether.AppAudit.AI.Tools;

/// <summary>
/// 「应用审查」对 agent 暴露的工具——**取任务 / 写结论 / 读结论，没有判定流程**。
/// <para>设计上的分工：页面只**发布任务**；agent 用 <c>ListAuditTasks</c> 读任务，自己决定怎么判
/// （只用 WebSearch、FetchUrl 看正文、凭已有知识、多轮追问都行），再用 <c>SaveAppVerdict</c> 写回。
/// 工具只做这一层薄薄的读写，所以任何 agent（聊天里的、将来别的入口/自动化）都能产出结论，
/// 页面只是这份记录的一个视图——两边不需要互相知道对方存在。</para>
/// <para>刻意**不提供**「一条龙判定工具」：那会把“怎么判”重新钉死在工具里，
/// 又回到了「换个判法就得改代码」。</para>
/// <para>只读写本地 JSON，一律 Safe 级；不触发审批。</para>
/// </summary>
public sealed class AppAuditTool
{
    private readonly AppAuditStore _store;
    private readonly AppAuditService _service;

    public AppAuditTool(AppAuditStore store, AppAuditService service)
    {
        _store = store;
        _service = service;
    }

    [Description("读取「应用审查」页发布的任务（待处理的应用名清单）。处理审查任务前先调它：返回每个名字、清单里给的网址、以及上次的结论。")]
    public string ListAuditTasks([Description("最多返回多少条（默认 20）")] int limit = 20)
    {
        var tasks = _service.Tasks(limit);
        if (tasks.Count == 0)
            return "「应用审查」当前没有待处理任务（发布任务是在应用审查页点「发布任务」）。";

        var lines = new List<string> { $"待处理 {tasks.Count} 条（池中共 {_service.QueuedCount} 条）：" };
        for (var i = 0; i < tasks.Count; i++)
        {
            var t = tasks[i];
            var url = string.IsNullOrWhiteSpace(t.ShortcutUrl) ? "" : $"，清单给的网址 {t.ShortcutUrl}";
            var previous = t.Verdict == AuditVerdict.Unknown
                ? "，上次未判定"
                : $"，上次 {VerdictLabel(t.Verdict)}"
                  + (string.IsNullOrWhiteSpace(t.SiteType) ? "" : $"·{t.SiteType}")
                  + $"（{t.Attempts.Count} 次判定）";
            lines.Add($"{i + 1}. {t.AppName}{url}{previous}");
        }
        lines.Add("逐条判定后调 SaveAppVerdict 写回；结论一写回，页面就会显示。");
        return string.Join('\n', lines);
    }

    [Description("把判定结论写进「应用审查」库（追加一次判定并保留历史，页面会立刻显示，该条从任务池里完成）。判定方式随你：先 WebSearch、再 FetchUrl 看正文、或多轮核对都行，这里只负责落结论。")]
    public string SaveAppVerdict(
        [Description("应用名/显示名")] string name,
        [Description("结论：gambling（博彩）/ not_gambling（非博彩）/ inconclusive（不确定）")] string verdict,
        [Description("站点类型短标签：体育博彩/真人赌场/棋牌/视频/直播/社交/电商/新闻资讯/工具/软件下载/网盘/游戏/色情/金融理财/诈骗站/未知")] string siteType,
        [Description("一句话理由，必须指出依据（例如「候选站点含体育投注/真人视讯」）")] string reason,
        [Description("对应的网站 URL（可选，填你查证过的真实地址）")] string? site = null,
        [Description("置信度 0-1（可选）")] double? confidence = null)
    {
        if (string.IsNullOrWhiteSpace(name)) return "❌ 应用名不能为空。";
        if (!TryParseVerdict(verdict, out var parsed))
            return "❌ verdict 只能是 gambling / not_gambling / inconclusive。";

        var id = EnsureEntry(name, null);
        _service.RecordVerdict(id, new AppVerdictResult(
            parsed,
            confidence is { } c ? Math.Clamp(c, 0, 1) : null,
            string.IsNullOrWhiteSpace(reason) ? "（agent 未给理由）" : reason.Trim(),
            string.IsNullOrWhiteSpace(siteType) ? null : siteType.Trim(),
            string.IsNullOrWhiteSpace(site) ? null : site.Trim(),
            "聊天 agent"));

        return Describe(Find(id) ?? throw new InvalidOperationException($"记录不见了：{id}"));
    }

    [Description("读取「应用审查」页里某个应用名已有的结论（含判过几次、历次结论）。判之前先看看，别重复劳动。")]
    public string ReadAppVerdict([Description("应用名/显示名")] string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "❌ 应用名不能为空。";
        var entry = Find(AppAuditEntry.IdFor(name));
        return entry is null ? $"「{name.Trim()}」还没有审查记录。" : Describe(entry);
    }

    // —————————————— 内部 ——————————————

    /// <summary>没有这条记录就建一条（聊天里直接报名字也能用），已存在则只补空缺。</summary>
    private string EnsureEntry(string name, string? url)
    {
        _store.MergeNames(new[] { (name, url) }, DateTimeOffset.Now);
        return AppAuditEntry.IdFor(name);
    }

    private AppAuditEntry? Find(string id) => _store.Entries.FirstOrDefault(e => e.Id == id);

    private static string Describe(AppAuditEntry e)
    {
        var lines = new List<string>
        {
            $"{e.AppName} → {VerdictLabel(e.Verdict)}"
            + (string.IsNullOrWhiteSpace(e.SiteType) ? "" : $" · 类型 {e.SiteType}")
            + (e.Confidence is { } c ? $" · 置信度 {c:P0}" : ""),
            $"站点：{e.PrimaryUrl ?? "（没搜到）"}（候选 {e.Candidates.Count} 条）",
            $"理由：{e.Reason ?? "—"}",
            $"判定次数：{e.Attempts.Count}（已写入「应用审查」页）",
        };
        if (e.Attempts.Count > 1)
        {
            var last = e.Attempts.TakeLast(3).Select(a => $"{a.At.ToLocalTime():MM-dd HH:mm} {VerdictLabel(a.Verdict)}");
            lines.Add($"最近几次：{string.Join(" / ", last)}");
        }
        return string.Join('\n', lines);
    }

    private static string VerdictLabel(AuditVerdict v) => v switch
    {
        AuditVerdict.Gambling => "博彩",
        AuditVerdict.NotGambling => "非博彩",
        AuditVerdict.Inconclusive => "不确定",
        _ => "未判定",
    };

    private static bool TryParseVerdict(string? raw, out AuditVerdict verdict)
    {
        switch (raw?.Trim().ToLowerInvariant())
        {
            case "gambling" or "betting" or "casino" or "博彩" or "赌博":
                verdict = AuditVerdict.Gambling;
                return true;
            case "not_gambling" or "not-gambling" or "notgambling" or "safe" or "非博彩" or "干净":
                verdict = AuditVerdict.NotGambling;
                return true;
            case "inconclusive" or "unknown" or "不确定":
                verdict = AuditVerdict.Inconclusive;
                return true;
            default:
                verdict = AuditVerdict.Unknown;
                return false;
        }
    }
}
