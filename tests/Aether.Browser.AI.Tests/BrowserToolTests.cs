using Aether.Browser.AI;
using Xunit;

namespace Aether.Browser.AI.Tests;

/// <summary>BrowserTool 行为测试（fake IBrowserService，不起 Playwright）：
/// 委托 EnsureOpen / 无活跃会话的错误口径 / get_requests 的过滤与限量。</summary>
public class BrowserToolTests
{
    private static NetworkRequestItem Req(Guid sessionId, string url, int? status, bool isError = false) =>
        new(Guid.NewGuid(), sessionId, DateTimeOffset.UtcNow, "GET", url) { Status = status, IsError = isError };

    [Fact]
    public async Task BrowserOpen_有活跃会话则导航并回传元数据()
    {
        var fake = new FakeBrowserService();
        var session = (FakeBrowserService.FakeSession)fake.OpenSession("https://start.test/");
        var tool = new BrowserTool(fake);

        var output = await tool.BrowserOpenAsync("https://login.test/", "chrome");

        Assert.True(output.Success);
        Assert.Equal(session.Id, output.SessionId);
        Assert.Equal("https://login.test/", output.Url);
        Assert.Contains((session.Id, "https://login.test/"), fake.Navigations);
    }

    [Fact]
    public async Task BrowserOpen_无活跃会话则新建()
    {
        var fake = new FakeBrowserService();
        var tool = new BrowserTool(fake);

        var output = await tool.BrowserOpenAsync("https://first.test/");

        Assert.True(output.Success);
        Assert.Contains("https://first.test/", fake.OpenedUrls);
    }

    [Fact]
    public async Task GetCookies_无活跃会话返回失败()
    {
        var tool = new BrowserTool(new FakeBrowserService());
        var result = await tool.GetCookiesAsync();
        Assert.False(result.Success);
        Assert.Contains("无活跃浏览器会话", result.Error);
    }

    [Fact]
    public async Task GetCookies_透传服务结果()
    {
        var fake = new FakeBrowserService();
        fake.OpenSession("https://a.test/");
        fake.NextCookieResult = new BrowserCookieResult { Success = true, CookieHeader = "sid=1; token=2" };
        var tool = new BrowserTool(fake);

        var result = await tool.GetCookiesAsync(new List<string> { "sid" });

        Assert.True(result.Success);
        Assert.Equal("sid=1; token=2", result.CookieHeader);
    }

    [Fact]
    public async Task GetRequests_按URL过滤()
    {
        var fake = new FakeBrowserService();
        var s = (FakeBrowserService.FakeSession)fake.OpenSession("https://a.test/");
        fake.RequestsMap[s.Id] = new List<NetworkRequestItem>
        {
            Req(s.Id, "https://a.test/api/x", 200),
            Req(s.Id, "https://a.test/api/y", 500, isError: true),
            Req(s.Id, "https://b.test/page", 404, isError: true),
        };
        var tool = new BrowserTool(fake);

        var output = await tool.GetRequestsAsync(urlContains: "a.test/api");

        Assert.True(output.Success);
        Assert.Equal(2, output.Count);
    }

    [Fact]
    public async Task GetRequests_只看错误()
    {
        var fake = new FakeBrowserService();
        var s = (FakeBrowserService.FakeSession)fake.OpenSession("https://a.test/");
        fake.RequestsMap[s.Id] = new List<NetworkRequestItem>
        {
            Req(s.Id, "https://a.test/ok", 200),
            Req(s.Id, "https://a.test/boom", 500, isError: true),
            Req(s.Id, "https://b.test/nope", 404, isError: true),
        };
        var tool = new BrowserTool(fake);

        var output = await tool.GetRequestsAsync(errorsOnly: true);

        Assert.Equal(2, output.Count);
        Assert.All(output.Requests, r => Assert.True(r.IsError));
    }

    [Fact]
    public async Task GetRequests_限量与叠加过滤()
    {
        var fake = new FakeBrowserService();
        var s = (FakeBrowserService.FakeSession)fake.OpenSession("https://a.test/");
        fake.RequestsMap[s.Id] = new List<NetworkRequestItem>
        {
            Req(s.Id, "https://a.test/api/x", 200),
            Req(s.Id, "https://a.test/api/y", 500, isError: true),
            Req(s.Id, "https://b.test/page", 404, isError: true),
        };
        var tool = new BrowserTool(fake);

        var limited = await tool.GetRequestsAsync(limit: 1);
        Assert.Equal(1, limited.Count);

        var combined = await tool.GetRequestsAsync(urlContains: "a.test/api", errorsOnly: true);
        Assert.Single(combined.Requests);
        Assert.Equal("https://a.test/api/y", combined.Requests[0].Url);
    }

    [Fact]
    public async Task GetRequests_无活跃会话返回失败()
    {
        var tool = new BrowserTool(new FakeBrowserService());
        var output = await tool.GetRequestsAsync();
        Assert.False(output.Success);
    }

    [Fact]
    public async Task Close_无活跃会话返回失败_有则关闭()
    {
        var fake = new FakeBrowserService();
        var tool = new BrowserTool(fake);

        var miss = await tool.CloseAsync();
        Assert.False(miss.Success);

        var s = (FakeBrowserService.FakeSession)fake.OpenSession("https://a.test/");
        var hit = await tool.CloseAsync();
        Assert.True(hit.Success);
        Assert.Contains(s.Id, fake.Closed);
    }

    [Fact]
    public async Task 操作三件套_无活跃会话返回失败()
    {
        var tool = new BrowserTool(new FakeBrowserService());

        Assert.Contains("无活跃浏览器会话", (await tool.BrowserClickAsync("text=登录")).Error);
        Assert.Contains("无活跃浏览器会话", (await tool.BrowserTypeAsync("css=#q", "hi")).Error);
        Assert.Contains("无活跃浏览器会话", (await tool.BrowserReadAsync()).Error);
    }

    [Fact]
    public async Task BrowserClick_委托service并回传落点URL()
    {
        var fake = new FakeBrowserService();
        var s = (FakeBrowserService.FakeSession)fake.OpenSession("https://a.test/");
        fake.NextActionResult = new BrowserActionResult { Success = true, Url = "https://a.test/after" };
        var tool = new BrowserTool(fake);

        var output = await tool.BrowserClickAsync("text=登录");

        Assert.True(output.Success);
        Assert.Equal("https://a.test/after", output.Url);
        Assert.Contains((s.Id, "text=登录"), fake.Clicks);
    }

    [Fact]
    public async Task BrowserType_透传selector_text与pressEnter()
    {
        var fake = new FakeBrowserService();
        var s = (FakeBrowserService.FakeSession)fake.OpenSession("https://a.test/");
        fake.NextActionResult = new BrowserActionResult { Success = true };
        var tool = new BrowserTool(fake);

        var output = await tool.BrowserTypeAsync("css=#q", "查询词", pressEnter: true);

        Assert.True(output.Success);
        Assert.Contains((s.Id, "css=#q", "查询词", true), fake.Types);
    }

    [Fact]
    public async Task BrowserRead_返回页面内容()
    {
        var fake = new FakeBrowserService();
        var s = (FakeBrowserService.FakeSession)fake.OpenSession("https://a.test/");
        fake.NextPageContent = new BrowserPageContent { Success = true, Url = "https://a.test/", Title = "测试页", Text = "页面正文" };
        var tool = new BrowserTool(fake);

        var output = await tool.BrowserReadAsync();

        Assert.True(output.Success);
        Assert.Equal("测试页", output.Title);
        Assert.Contains("页面正文", output.Text);
        Assert.Contains(s.Id, fake.Reads);
    }
}
