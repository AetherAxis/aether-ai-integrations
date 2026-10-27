using Aether.AppAudit.AI.Tools;
using Aether.AppAudit.AI;
using Xunit;

namespace Aether.AppAudit.AI.Tests;

/// <summary>
/// 聊天侧的**任务接口**：agent 用 ListAuditTasks 取页面发布的任务、自己怎么判都行，
/// 再用 SaveAppVerdict 把结论落进同一份记录（页面只是视图）。这里验的是这条链路本身，
/// 以及「结论必须是合法值、没有记录也不能编」这两条底线。
/// </summary>
public class AppAuditToolTests : IDisposable
{
    private readonly string _dir;
    private readonly AppAuditStore _store;
    private readonly AppAuditService _service;
    private readonly AppAuditTool _tools;

    public AppAuditToolTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"aether-audit-tools-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
        _store = new AppAuditStore(Path.Combine(_dir, "app-audit.json"));
        _service = new AppAuditService(_store);
        _tools = new AppAuditTool(_store, _service);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { /* 临时目录 */ }
    }

    /// <summary>页面发布任务 → agent 在聊天里取到（名字 + 清单网址 + 上次结论）。</summary>
    [Fact]
    public void ListAuditTasks_取到页面发布的任务()
    {
        _store.MergeNames(new[] { ("Sunwin", (string?)"https://sunwin.example"), ("BK8", (string?)null) }, DateTimeOffset.Now);
        _service.Publish();

        var text = _tools.ListAuditTasks(20);

        Assert.Contains("待处理 2 条", text);
        Assert.Contains("Sunwin", text);
        Assert.Contains("https://sunwin.example", text);
        Assert.Contains("BK8", text);
        Assert.Contains("上次未判定", text);
    }

    /// <summary>没有任务时明说没有（不是编一个任务清单出来）。</summary>
    [Fact]
    public void ListAuditTasks_无任务时明说()
    {
        Assert.Contains("没有待处理任务", _tools.ListAuditTasks());
    }

    /// <summary>agent 判完写回：落进页面同一份记录、出池、身份标「聊天 agent」、站点进候选链。</summary>
    [Fact]
    public void SaveAppVerdict_写回后页面能读到()
    {
        _store.MergeNames(new[] { ("LuckyBet", (string?)null) }, DateTimeOffset.Now);
        _service.Publish();

        var text = _tools.SaveAppVerdict("LuckyBet", "gambling", "真人赌场", "首页写「首存送彩金」", "https://luckybet.example", 0.8);

        var entry = Assert.Single(_store.Entries);
        Assert.Equal(AuditVerdict.Gambling, entry.Verdict);
        Assert.Equal("真人赌场", entry.SiteType);
        Assert.Equal(AuditStage.Done, entry.Stage);
        Assert.Empty(_service.Tasks());                                  // 已出池
        Assert.Equal("https://luckybet.example", entry.Candidates[0].Url);
        Assert.Equal("agent", entry.Candidates[0].Source);                // 证据链上看得出站点从 agent 来
        Assert.Equal("聊天 agent", Assert.Single(entry.Attempts).Judge);
        Assert.Contains("博彩", text);
        Assert.Contains("已写入", text);
    }

    /// <summary>聊天里直接报个名字（页面没这条记录）也能写结论——顺手建记录，不用先去页面导入。</summary>
    [Fact]
    public void SaveAppVerdict_未导入的名字也能写()
    {
        _tools.SaveAppVerdict("LuckyWin", "not_gambling", "游戏", "只是普通游戏站", null, 0.5);

        var entry = Assert.Single(_store.Entries);
        Assert.Equal("LuckyWin", entry.AppName);
        Assert.Equal(AuditVerdict.NotGambling, entry.Verdict);
        Assert.Equal(AuditStage.Done, entry.Stage);
    }

    /// <summary>非法结论不落盘（不然页面会出现一个谁也看不懂的结论）。</summary>
    [Fact]
    public void SaveAppVerdict_拒绝非法结论()
    {
        var text = _tools.SaveAppVerdict("BK8", "maybe", "未知", "看着像");

        Assert.Contains("只能是", text);
        Assert.Empty(_store.Entries);
    }

    /// <summary>中文/同义词也认（agent 未必严格按英文枚举输出）。</summary>
    [Theory]
    [InlineData("博彩", AuditVerdict.Gambling)]
    [InlineData("赌博", AuditVerdict.Gambling)]
    [InlineData("CASINO", AuditVerdict.Gambling)]
    [InlineData("非博彩", AuditVerdict.NotGambling)]
    [InlineData("干净", AuditVerdict.NotGambling)]
    [InlineData("不确定", AuditVerdict.Inconclusive)]
    public void SaveAppVerdict_容忍同义词(string verdict, AuditVerdict expected)
    {
        _tools.SaveAppVerdict("X1", verdict, "未知", "同义词测试");

        Assert.Equal(expected, Assert.Single(_store.Entries).Verdict);
    }

    /// <summary>先读后判：已有结论能读出来；没记录时明说没有（不是编一个）。</summary>
    [Fact]
    public void ReadAppVerdict_有则读出无则明说()
    {
        Assert.Contains("还没有审查记录", _tools.ReadAppVerdict("Nothing"));

        _tools.SaveAppVerdict("BK8", "not_gambling", "工具", "文档站", null, 0.6);
        var text = _tools.ReadAppVerdict("BK8");

        Assert.Contains("非博彩", text);
        Assert.Contains("工具", text);
        Assert.Contains("判定次数：1", text);
    }
}
