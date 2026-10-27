using Aether.AppAudit.AI;
using Xunit;

namespace Aether.AppAudit.AI.Tests;

/// <summary>
/// 应用审查的数据层 + **任务板**：导入名单 → 发布任务 → agent 取任务/写结论 → 落盘。
/// <para>这层没有判定逻辑（判定是 agent 的活），所以这里验的是**状态机与写入纪律**：
/// 发布只改状态不发请求、已判过的不被默认重新发布、复核时旧结论不被洗掉、
/// 写回一次就出池，以及旧文件的阶段读得懂。</para>
/// </summary>
public class AppAuditTests : IDisposable
{
    private readonly string _dir;

    public AppAuditTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"aether-app-audit-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { /* 临时目录 */ }
    }

    private string StorePath => Path.Combine(_dir, "app-audit.json");

    // —————————————— 存储 ——————————————

    /// <summary>重复导入只补空缺，**已有结论与状态不动**（否则每次导入都白判一遍）。</summary>
    [Fact]
    public void 合并导入_不覆盖已有结论()
    {
        var store = new AppAuditStore(StorePath);
        store.MergeNames(new[] { ("Sunwin", (string?)null) }, DateTimeOffset.Now);
        var id = AppAuditEntry.IdFor("Sunwin");
        store.Mutate(doc =>
        {
            var e = doc.Entries.Single(x => x.Id == id);
            e.Verdict = AuditVerdict.Gambling;
            e.Reason = "站名为博彩品牌";
            e.Stage = AuditStage.Done;
        });

        var added = store.MergeNames(new[] { ("sunwin ", (string?)"https://example.com") }, DateTimeOffset.Now);

        Assert.Equal(0, added);                        // 同名（规范化后）不算新增
        var entry = Assert.Single(store.Entries);
        Assert.Equal(AuditVerdict.Gambling, entry.Verdict);
        Assert.Equal("站名为博彩品牌", entry.Reason);
        Assert.Equal("https://example.com", entry.ShortcutUrl);   // 空缺被补上
    }

    /// <summary>落盘 → 新 store 读回，结论与候选都在（换端/重启接着看）。</summary>
    [Fact]
    public void 落盘后可以读回()
    {
        var store = new AppAuditStore(StorePath);
        store.MergeNames(new[] { ("LuckyBet", (string?)null) }, DateTimeOffset.Now);
        store.Mutate(doc =>
        {
            var e = doc.Entries.Single();
            e.Candidates.Add(new AppSiteCandidate { Url = "https://lucky.example", Title = "LuckyBet 官方", Source = "agent" });
            e.Verdict = AuditVerdict.Gambling;
            e.Confidence = 0.92;
            e.Reason = "候选站点为体育博彩";
            e.Stage = AuditStage.Done;
        });

        var reloaded = new AppAuditStore(StorePath);
        var entry = Assert.Single(reloaded.Entries);
        Assert.Equal(AuditVerdict.Gambling, entry.Verdict);
        Assert.Equal(0.92, entry.Confidence);
        Assert.Equal("https://lucky.example", entry.Candidates[0].Url);
    }

    /// <summary>坏文件不该让页面炸：当空表处理。</summary>
    [Fact]
    public void 坏JSON按空表处理()
    {
        File.WriteAllText(StorePath, "{ oops");
        var store = new AppAuditStore(StorePath);
        Assert.Empty(store.Entries);
    }

    // —————————————— 清单来源 ——————————————

    /// <summary>粘贴清单：去序号/项目符号、容忍 "名字 URL" 两段、跳过空行。</summary>
    [Fact]
    public void 粘贴清单解析()
    {
        var parsed = AppNameList.Parse("""
            1. Sunwin
            2) luckybet https://lucky.example
            - LuckyWin

            BK8	https://bk8.example
            """);

        Assert.Equal(4, parsed.Count);
        Assert.Equal(("Sunwin", (string?)null), parsed[0]);
        Assert.Equal(("luckybet", (string?)"https://lucky.example"), parsed[1]);
        Assert.Equal(("LuckyWin", (string?)null), parsed[2]);
        Assert.Equal(("BK8", (string?)"https://bk8.example"), parsed[3]);
    }

    // —————————————— 任务板：发布 / 取任务 / 写回 ——————————————

    private static (AppAuditStore Store, AppAuditService Service) NewBoard(string path)
    {
        var store = new AppAuditStore(path);
        return (store, new AppAuditService(store));
    }

    /// <summary>导入后是「待发布」：不在任务池里，agent 取不到（页面还没发布）。</summary>
    [Fact]
    public void 导入后是待发布_不在任务池()
    {
        var (store, service) = NewBoard(StorePath);
        store.MergeNames(new[] { ("A1", (string?)null) }, DateTimeOffset.Now);

        var entry = Assert.Single(store.Entries);
        Assert.True(entry.IsDraft);
        Assert.False(entry.IsQueued);
        Assert.Empty(service.Tasks());
        Assert.Equal(0, service.QueuedCount);
    }

    /// <summary>发布任务只改状态：待发布的进池，**已判过的默认不动**（勾复核才一起发）。</summary>
    [Fact]
    public void 发布任务_默认跳过已判过的()
    {
        var (store, service) = NewBoard(StorePath);
        store.MergeNames(new[] { ("A1", (string?)null), ("B2", (string?)null) }, DateTimeOffset.Now);
        service.RecordVerdict(AppAuditEntry.IdFor("B2"),
            new AppVerdictResult(AuditVerdict.NotGambling, 0.8, "工具站", "工具", null, "agent"));

        Assert.Equal(1, service.Publish());                      // 只发 A1
        Assert.Equal(new[] { "A1" }, service.Tasks().Select(t => t.AppName));
        Assert.Equal(AuditStage.Done, store.Entries.Single(e => e.AppName == "B2").Stage);
        Assert.Equal(0, service.Publish());                      // 再点一次不会重复发布

        Assert.Equal(1, service.Publish(includeJudged: true));    // 勾复核 → B2 也进池
        Assert.Equal(2, service.QueuedCount);
        Assert.Equal(AuditVerdict.NotGambling,
            store.Entries.Single(e => e.AppName == "B2").Verdict);   // 复核不清空结论
    }

    /// <summary>取任务 → 写回：写回即出池，结论落当前字段，历史留痕（谁判的、几次）。</summary>
    [Fact]
    public void 取任务后写回结论_出池()
    {
        var (store, service) = NewBoard(StorePath);
        store.MergeNames(new[] { ("Sunwin", (string?)"https://sunwin.example") }, DateTimeOffset.Now);
        service.Publish();

        var task = Assert.Single(service.Tasks());
        Assert.Equal("Sunwin", task.AppName);
        Assert.Equal("https://sunwin.example", task.ShortcutUrl);   // 清单里的网址一并给 agent

        service.RecordVerdict(task.Id, new AppVerdictResult(
            AuditVerdict.Gambling, 0.9, "候选站点含体育投注", "体育博彩", null, "聊天 agent"));

        var entry = Assert.Single(store.Entries);
        Assert.Equal(AuditStage.Done, entry.Stage);
        Assert.False(entry.IsQueued);
        Assert.Empty(service.Tasks());
        Assert.Equal(AuditVerdict.Gambling, entry.Verdict);
        Assert.Equal("体育博彩", entry.SiteType);
        Assert.Equal("聊天 agent", Assert.Single(entry.Attempts).Judge);
    }

    /// <summary>
    /// 判定**不是一次定终身**：再写一次覆盖当前结论，旧结论进历史（调口径/换判定方后能看出结论怎么变的）。
    /// </summary>
    [Fact]
    public void 重判保留历史且当前结论取最近一次()
    {
        var (store, service) = NewBoard(StorePath);
        store.MergeNames(new[] { ("F6", (string?)null) }, DateTimeOffset.Now);
        var id = AppAuditEntry.IdFor("F6");

        service.RecordVerdict(id, new AppVerdictResult(AuditVerdict.Gambling, 0.9, "首存送彩金", "体育博彩", "https://f6.example", "agent-1"));
        service.Republish(id);                                        // 复核：重回任务池
        service.RecordVerdict(id, new AppVerdictResult(AuditVerdict.NotGambling, 0.7, "看了正文是工具站", "工具", null, "agent-2"));

        var entry = Assert.Single(store.Entries);
        Assert.Equal(2, entry.Attempts.Count);
        Assert.Equal(AuditVerdict.Gambling, entry.Attempts[0].Verdict);
        Assert.Equal(AuditVerdict.NotGambling, entry.Attempts[1].Verdict);
        Assert.Equal("工具", entry.Attempts[1].SiteType);
        Assert.Equal("agent-2", entry.Attempts[1].Judge);
        Assert.Equal(AuditVerdict.NotGambling, entry.Verdict);        // 当前 = 最近一次
        Assert.Equal("看了正文是工具站", entry.Reason);
        Assert.Equal("https://f6.example", entry.PrimaryUrl);          // 站点沿用上一轮给的
    }

    /// <summary>单条重发：只有那一条回池，页面据此显示「待复核」。</summary>
    [Fact]
    public void 单条重发只影响那一条()
    {
        var (store, service) = NewBoard(StorePath);
        store.MergeNames(new[] { ("G7", (string?)null), ("H8", (string?)null) }, DateTimeOffset.Now);
        foreach (var name in new[] { "G7", "H8" })
            service.RecordVerdict(AppAuditEntry.IdFor(name),
                new AppVerdictResult(AuditVerdict.NotGambling, 0.6, "工具站", "工具", null, "agent"));

        Assert.True(service.Republish(AppAuditEntry.IdFor("G7")));

        Assert.Equal(new[] { "G7" }, service.Tasks().Select(t => t.AppName));
        var g7 = store.Entries.Single(e => e.AppName == "G7");
        Assert.True(g7.IsQueued);
        Assert.False(g7.HasVerdict);          // 复核中：状态不再是「已完成」
        Assert.Single(g7.Attempts);           // 结论还留着（历史里的那条）
        Assert.False(store.Entries.Single(e => e.AppName == "H8").IsQueued);
        Assert.False(service.Republish("nope"));
    }

    /// <summary>老文件里「网搜中/已搜到/判定中」的记录不能变成没人管的孤儿：一律算待处理。</summary>
    [Fact]
    public void 旧版中途阶段算作待处理()
    {
        var (store, service) = NewBoard(StorePath);
        store.MergeNames(new[] { ("I9", (string?)null) }, DateTimeOffset.Now);
        store.Mutate(doc => doc.Entries.Single().Stage = AuditStage.Searched);

        Assert.True(store.Entries.Single().IsQueued);
        Assert.Equal(new[] { "I9" }, service.Tasks().Select(t => t.AppName));
    }

    /// <summary>任务按发布时间先来后到，limit 截断（别一次把整张清单塞给 agent）。</summary>
    [Fact]
    public void 取任务按发布顺序且可截断()
    {
        var (store, service) = NewBoard(StorePath);
        store.MergeNames(new[] { ("K1", (string?)null), ("K2", (string?)null), ("K3", (string?)null) }, DateTimeOffset.Now);
        service.Publish();
        // 同一次发布的时间戳相同，这里手动错开模拟「分批发布」。
        store.Mutate(doc =>
        {
            doc.Entries.Single(e => e.AppName == "K3").PublishedAt = DateTimeOffset.UtcNow.AddMinutes(-3);
            doc.Entries.Single(e => e.AppName == "K1").PublishedAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        });

        Assert.Equal(new[] { "K3", "K1", "K2" }, service.Tasks().Select(t => t.AppName));
        Assert.Equal(2, service.Tasks(limit: 2).Count);
        Assert.Equal(3, service.QueuedCount);       // queued 总数不受 limit 影响
    }
}
