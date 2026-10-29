using Aether.Browser.AI;

namespace Aether.Browser.AI.Tests;

/// <summary>IBrowserService 测试替身：记录调用、按需抛事件——工具/VM 层测试用，不起 Playwright。</summary>
internal sealed class FakeBrowserService : IBrowserService
{
    public sealed class FakeSession : IBrowserSession
    {
        public Guid Id { get; init; }
        public string? Url { get; set; }
        public string Title { get; set; } = "fake";
        public bool IsAlive { get; set; } = true;
    }

    public List<FakeSession> Sessions { get; } = new();
    public FakeSession? Active;
    /// <summary>会话 id → 请求列表（GetRequests 的数据源）。</summary>
    public Dictionary<Guid, List<NetworkRequestItem>> RequestsMap { get; } = new();
    /// <summary>NavigateAsync 的调用记录（会话 id, url）。</summary>
    public List<(Guid SessionId, string Url)> Navigations { get; } = new();
    /// <summary>OpenSession 的 url 记录。</summary>
    public List<string> OpenedUrls { get; } = new();
    /// <summary>CloseSession 的调用记录。</summary>
    public List<Guid> Closed { get; } = new();
    /// <summary>GetCookiesAsync 的应答（null = 返回失败）。</summary>
    public BrowserCookieResult? NextCookieResult { get; set; }
    /// <summary>ClickAsync/TypeAsync 的应答（null = 返回失败）。</summary>
    public BrowserActionResult? NextActionResult { get; set; }
    /// <summary>ReadPageAsync 的应答（null = 返回失败）。</summary>
    public BrowserPageContent? NextPageContent { get; set; }
    /// <summary>ClickAsync 的调用记录。</summary>
    public List<(Guid SessionId, string Selector)> Clicks { get; } = new();
    /// <summary>TypeAsync 的调用记录。</summary>
    public List<(Guid SessionId, string Selector, string Text, bool PressEnter)> Types { get; } = new();
    /// <summary>ReadPageAsync 的调用记录。</summary>
    public List<Guid> Reads { get; } = new();

    public Guid? ActiveSessionId => Active?.Id;

    /// <summary>宿主策略默认模式（接口成员；fake 上直读直写）。</summary>
    public BrowserLaunchMode DefaultLaunchMode { get; set; } = BrowserLaunchMode.Launch;
    /// <summary>OpenSession/EnsureOpenAsync 收到的显式 mode 参数记录（null = 未传，走默认）。</summary>
    public List<BrowserLaunchMode?> RequestedModes { get; } = new();

    public event Action? SessionsChanged;
    public event Action<Guid>? ActiveSessionChanged;
    public event Action<Guid>? RequestsChanged;
    public event Action<Guid>? SessionClosed;

    public IBrowserSession OpenSession(string url, string? channel = null, BrowserLaunchMode? mode = null)
    {
        var s = new FakeSession { Id = Guid.NewGuid(), Url = url };
        Sessions.Add(s);
        Active = s;
        OpenedUrls.Add(url);
        RequestedModes.Add(mode);
        SessionsChanged?.Invoke();
        ActiveSessionChanged?.Invoke(s.Id);
        return s;
    }

    public Task<IBrowserSession> EnsureOpenAsync(string url, string? channel = null, BrowserLaunchMode? mode = null, CancellationToken cancellationToken = default)
    {
        if (Active is { IsAlive: true } a)
        {
            Navigations.Add((a.Id, url));
            a.Url = url;
            return Task.FromResult<IBrowserSession>(a);
        }
        return Task.FromResult(OpenSession(url, channel, mode));
    }

    public void CloseSession(Guid sessionId)
    {
        var s = Sessions.FirstOrDefault(x => x.Id == sessionId);
        if (s is null) return;
        Sessions.Remove(s);
        if (Active?.Id == sessionId) Active = Sessions.LastOrDefault();
        Closed.Add(sessionId);
        SessionClosed?.Invoke(sessionId);
        SessionsChanged?.Invoke();
    }

    public void SetActive(Guid sessionId)
    {
        Active = Sessions.FirstOrDefault(x => x.Id == sessionId);
        ActiveSessionChanged?.Invoke(sessionId);
        SessionsChanged?.Invoke();
    }

    public IReadOnlyList<IBrowserSession> GetSessions() => Sessions.ToList();

    public Task NavigateAsync(Guid sessionId, string url)
    {
        Navigations.Add((sessionId, url));
        return Task.CompletedTask;
    }

    public IReadOnlyList<NetworkRequestItem> GetRequests(Guid sessionId)
        => RequestsMap.TryGetValue(sessionId, out var list) ? list.ToList() : Array.Empty<NetworkRequestItem>();

    public void ClearRequests(Guid sessionId) => RequestsMap.Remove(sessionId);

    public Task<BrowserCookieResult> GetCookiesAsync(Guid sessionId, List<string>? cookieNames = null, CancellationToken cancellationToken = default)
        => Task.FromResult(NextCookieResult ?? new BrowserCookieResult { Success = false, Error = "fake: 未配置 NextCookieResult" });

    public Task<BrowserActionResult> ClickAsync(Guid sessionId, string selector, CancellationToken cancellationToken = default)
    {
        Clicks.Add((sessionId, selector));
        return Task.FromResult(NextActionResult ?? new BrowserActionResult { Success = false, Error = "fake: 未配置 NextActionResult" });
    }

    public Task<BrowserActionResult> TypeAsync(Guid sessionId, string selector, string text, bool pressEnter = false, CancellationToken cancellationToken = default)
    {
        Types.Add((sessionId, selector, text, pressEnter));
        return Task.FromResult(NextActionResult ?? new BrowserActionResult { Success = false, Error = "fake: 未配置 NextActionResult" });
    }

    public Task<BrowserPageContent> ReadPageAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        Reads.Add(sessionId);
        return Task.FromResult(NextPageContent ?? new BrowserPageContent { Success = false, Error = "fake: 未配置 NextPageContent" });
    }

    // —— 测试驱动口 ——

    public void RaiseRequests(Guid sessionId) => RequestsChanged?.Invoke(sessionId);
    public void RaiseSessions() => SessionsChanged?.Invoke();
}
