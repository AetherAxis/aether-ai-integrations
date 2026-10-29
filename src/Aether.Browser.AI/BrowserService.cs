using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Aether.AI.Hosting;
using Microsoft.Extensions.Options;
using Microsoft.Playwright;

namespace Aether.Browser.AI;

/// <summary>浏览器网络面板服务（impl <see cref="IBrowserService"/>）。
/// 持久 Playwright 会话：每个会话一个 headed Chromium 独立 OS 窗口（用户手动登录），app 内面板只展示该会话的 HTTP 请求流。
/// 用户面板与 agent 工具共用同一 service 实例（agent browser_open 取活跃会话、无则建新——用户可见）。
/// （自 monorepo Acme.Desktop.Shared.Shell.BrowserService port，2026-10-29；与原版的差异仅两处：
/// ① profile 目录改读 ⑫ AetherAIOptions.BrowserProfileDir（⑮ BuildProvider 设 dataDir/browser-profile）；
/// ② channel 未指定时经 <see cref="BrowserChannel.DetectDefault"/> 探测本机 Chrome/Edge。）
///
/// 线程模型：Playwright .NET 无线程亲和硬约束，但同一 page/context 的并发操作会竞争——
/// 故 per-session <see cref="BrowserSession.OpLock"/> 串行化所有主动操作（Navigate/GetCookies/Close）。
/// Playwright 的 Request/Response 事件在驱动回调线程触发，经 <see cref="BrowserSession._stateLock"/> 保护请求字典/列表的增改；
/// <see cref="NetworkRequestItem"/> 的字段更新经 Avalonia 绑定引擎自动 marshal 到 UI 线程。请求通知经 per-session Timer 去抖（150ms）合批。</summary>
public sealed class BrowserService : IBrowserService, IAsyncDisposable
{
    private readonly Dictionary<Guid, BrowserSession> _sessions = new();
    private readonly object _lock = new();
    private readonly string? _profileDir;

    /// <param name="options">读 <see cref="AetherAIOptions.BrowserProfileDir"/>——非空则持久 profile（登录一次后续复用），
    /// null 退回临时 profile（每次新开、不持久）。</param>
    public BrowserService(IOptions<AetherAIOptions> options) => _profileDir = options.Value.BrowserProfileDir;

    public event Action? SessionsChanged;
    public event Action<Guid>? ActiveSessionChanged;
    public event Action<Guid>? RequestsChanged;
    public event Action<Guid>? SessionClosed;

    /// <summary>宿主策略：新建会话的默认启动方式（面板开关写；未显式传 mode 的 OpenSession/EnsureOpenAsync 用它）。</summary>
    public BrowserLaunchMode DefaultLaunchMode { get; set; } = BrowserLaunchMode.Launch;

    public Guid? ActiveSessionId
    {
        get { lock (_lock) return _activeId; }
    }
    private Guid? _activeId;

    public IBrowserSession OpenSession(string url, string? channel = null, BrowserLaunchMode? mode = null)
    {
        var id = Guid.NewGuid();
        var session = new BrowserSession(id, this, _profileDir);
        lock (_lock) _sessions[id] = session;
        _activeId = id;
        // Playwright 启动在后台跑（不阻塞 UI 线程 / agent 工具返回）；建好/失败都经事件通知 VM。
        session.InitializeAsync(url, channel, mode ?? DefaultLaunchMode).ContinueWith(t =>
        {
            if (t.IsFaulted)
            {
                Trace.WriteLine($"browser session {id} init failed: {t.Exception}");
                session.MarkDead();
                SessionClosed?.Invoke(id);
            }
            SessionsChanged?.Invoke();
            ActiveSessionChanged?.Invoke(id);
        }, TaskScheduler.Default);
        // 立即返回句柄（IsAlive=true，Title 占位）；真启动在后台。
        return session;
    }

    public void CloseSession(Guid sessionId)
    {
        BrowserSession? session;
        lock (_lock)
        {
            if (!_sessions.TryGetValue(sessionId, out session)) return;
            _sessions.Remove(sessionId);
            // 关掉的是活跃会话 → 切到剩余最后一个；没有剩余必须显式置 null（monorepo 原版此处
            // LastOrDefault() 返回 Guid.Empty 赋给 Guid?，导致关光后 ActiveSessionId = Guid.Empty ≠ null，
            // 工具层误判「还有活跃会话」——2026-10-29 port 时由 --browser-smoke 三号断言暴露，此处修正）。
            if (_activeId == sessionId)
                _activeId = _sessions.Count > 0 ? _sessions.Keys.Last() : null;
        }
        session.MarkDead();
        SessionClosed?.Invoke(sessionId);
        SessionsChanged?.Invoke();
        // 实际关浏览器在后台跑（不阻塞 UI 线程）。
        session.DisposeAsync().AsTask().SafeFireAndForget();
        if (_activeId is { } newActive) ActiveSessionChanged?.Invoke(newActive);
    }

    public void SetActive(Guid sessionId)
    {
        lock (_lock)
        {
            if (!_sessions.ContainsKey(sessionId)) return;
            _activeId = sessionId;
        }
        ActiveSessionChanged?.Invoke(sessionId);
        SessionsChanged?.Invoke();
    }

    public IReadOnlyList<IBrowserSession> GetSessions()
    {
        lock (_lock) return _sessions.Values.Select(s => (IBrowserSession)s).ToList();
    }

    /// <summary>app 退出时容器 DisposeAsync 调此：释放所有仍在 _sessions 的 Playwright 会话，
    /// 关闭 headed Chromium 子进程 + 释放持久 profile 句柄。否则退出后 Chromium 进程残留 + 句柄泄漏
    /// （CloseSession 只释放单个会话；app 关闭走容器批量释放）。关窗事件不触发（app 正在退出，UI 已拆）。</summary>
    public async ValueTask DisposeAsync()
    {
        BrowserSession[] sessions;
        lock (_lock) sessions = _sessions.Values.ToArray();
        foreach (var s in sessions)
        {
            try { await s.DisposeAsync(); }
            catch (Exception ex) { Trace.WriteLine($"browser session {s.Id} shutdown dispose failed: {ex}"); }
        }
        lock (_lock)
        {
            _sessions.Clear();
            _activeId = null;
        }
    }

    public Task NavigateAsync(Guid sessionId, string url)
    {
        BrowserSession? s;
        lock (_lock) { if (!_sessions.TryGetValue(sessionId, out s)) return Task.CompletedTask; }
        return s.NavigateAsync(url);
    }

    public async Task<IBrowserSession> EnsureOpenAsync(string url, string? channel, BrowserLaunchMode? mode, CancellationToken ct)
    {
        BrowserSession? active = null;
        lock (_lock) { if (_activeId is { } aid && _sessions.TryGetValue(aid, out var a) && a.IsAlive) active = a; }
        if (active is not null) { await active.NavigateAsync(url); return active; }
        return await OpenSessionAsync(url, channel, mode, ct);
    }

    /// <summary>建新会话并等 Playwright 就绪（agent 工具路径）。失败也返回会话句柄（IsAlive=false）。</summary>
    public async Task<IBrowserSession> OpenSessionAsync(string url, string? channel, BrowserLaunchMode? mode, CancellationToken ct)
    {
        var id = Guid.NewGuid();
        var session = new BrowserSession(id, this, _profileDir);
        lock (_lock) { _sessions[id] = session; _activeId = id; }
        SessionsChanged?.Invoke();
        ActiveSessionChanged?.Invoke(id);
        try { await session.InitializeAsync(url, channel, mode ?? DefaultLaunchMode, ct); }
        catch (OperationCanceledException) { session.MarkDead(); SessionClosed?.Invoke(id); throw; }
        catch (Exception ex)
        {
            Trace.WriteLine($"browser session {id} init failed: {ex}");
            session.MarkDead();
            SessionClosed?.Invoke(id);
            SessionsChanged?.Invoke();
        }
        return session;
    }

    public IReadOnlyList<NetworkRequestItem> GetRequests(Guid sessionId)
    {
        lock (_lock)
        {
            return _sessions.TryGetValue(sessionId, out var s) ? s.GetRequestsSnapshot() : Array.Empty<NetworkRequestItem>();
        }
    }

    public void ClearRequests(Guid sessionId) =>
        WithSession(sessionId, session => { session.ClearRequests(); RequestsChanged?.Invoke(sessionId); });

    public Task<BrowserCookieResult> GetCookiesAsync(Guid sessionId, List<string>? cookieNames = null, CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            if (!_sessions.TryGetValue(sessionId, out var s))
                return Task.FromResult(new BrowserCookieResult { Success = false, Error = "会话不存在" });
            if (!s.IsAlive)
                return Task.FromResult(new BrowserCookieResult { Success = false, Error = "会话已关闭" });
            return s.GetCookiesAsync(cookieNames, cancellationToken);
        }
    }

    public Task<BrowserActionResult> ClickAsync(Guid sessionId, string selector, CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            if (!_sessions.TryGetValue(sessionId, out var s))
                return Task.FromResult(new BrowserActionResult { Success = false, Error = "会话不存在" });
            if (!s.IsAlive)
                return Task.FromResult(new BrowserActionResult { Success = false, Error = "会话已关闭" });
            return s.ClickAsync(selector, cancellationToken);
        }
    }

    public Task<BrowserActionResult> TypeAsync(Guid sessionId, string selector, string text, bool pressEnter = false, CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            if (!_sessions.TryGetValue(sessionId, out var s))
                return Task.FromResult(new BrowserActionResult { Success = false, Error = "会话不存在" });
            if (!s.IsAlive)
                return Task.FromResult(new BrowserActionResult { Success = false, Error = "会话已关闭" });
            return s.TypeAsync(selector, text, pressEnter, cancellationToken);
        }
    }

    public Task<BrowserPageContent> ReadPageAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            if (!_sessions.TryGetValue(sessionId, out var s))
                return Task.FromResult(new BrowserPageContent { Success = false, Error = "会话不存在" });
            if (!s.IsAlive)
                return Task.FromResult(new BrowserPageContent { Success = false, Error = "会话已关闭" });
            return s.ReadPageAsync(cancellationToken);
        }
    }

    private void WithSession(Guid sessionId, Action<BrowserSession> action)
    {
        BrowserSession? s;
        lock (_lock)
        {
            if (!_sessions.TryGetValue(sessionId, out s)) return;
        }
        action(s);
    }

    // —— BrowserSession 内部回调（事件触发时经此通知 service → VM）——
    internal void RaiseRequestsChanged(Guid id) => RequestsChanged?.Invoke(id);
    internal void RaiseSessionClosed(Guid id) { SessionClosed?.Invoke(id); SessionsChanged?.Invoke(); }
    internal void RaiseSessionsChanged() => SessionsChanged?.Invoke();
}

/// <summary>单个浏览器会话（含 Playwright 资源 + 请求记录）。实现 <see cref="IBrowserSession"/> 供 VM 投影元数据。</summary>
internal sealed class BrowserSession : IBrowserSession, IAsyncDisposable
{
    private readonly BrowserService _owner;
    private readonly SemaphoreSlim _opLock = new(1, 1);   // 串行化主动操作（防 Playwright 对象并发竞争）
    private readonly object _stateLock = new();           // 保护请求字典/列表的增改（事件来自 Playwright 线程）
    private readonly Dictionary<IRequest, NetworkRequestItem> _byRequest = new();
    private readonly List<NetworkRequestItem> _requests = new();
    /// <summary>_requests 上限（丢最旧）。防长浏览会话请求只增不减 → 万级 × body 常驻 = 数百 MB。
    /// 1000 覆盖一次浏览的最近请求；超限丢最旧并反向清理 _byRequest（否则被丢 item 的 body 经字典常驻，cap 失效）。</summary>
    private const int MaxRequests = 1000;
    /// <summary>response body 文本截断字符数（.NET UTF-16 串 ≈2 字节/字符 → ~64KB/条）。配合 MaxRequests 上限总内存 ~64MB 封顶。</summary>
    private const int MaxBodyChars = 32_000;
    private readonly Timer _requestsDebounce;

    private IPlaywright? _playwright;
    private IBrowser? _browser;          // 临时/附加模式用；持久模式（LaunchPersistentContextAsync）无独立 IBrowser 句柄
    private IBrowserContext? _context;
    private IPage? _page;
    private readonly string? _userDataDir;  // 持久 profile 目录；非空则持久启动（登录一次复用），null 退回临时
    private System.Diagnostics.Process? _attachProcess;   // 附加模式自持的浏览器进程（Dispose 时回收）

    private volatile string _url;
    private volatile string _title;
    private volatile bool _isAlive = true;

    public Guid Id { get; }
    public string? Url => _url;
    public string Title => _title;
    public bool IsAlive => _isAlive;

    public BrowserSession(Guid id, BrowserService owner, string? userDataDir)
    {
        Id = id;
        _owner = owner;
        _userDataDir = userDataDir;
        _url = "about:blank";
        _title = "新建会话";
        _requestsDebounce = new Timer(_ => _owner.RaiseRequestsChanged(Id), null, Timeout.Infinite, Timeout.Infinite);
    }

    public async Task InitializeAsync(string url, string? channel, BrowserLaunchMode mode = BrowserLaunchMode.Launch, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        await _opLock.WaitAsync();
        try
        {
            _playwright = await Playwright.CreateAsync();
            if (mode == BrowserLaunchMode.Attach)
            {
                // 附加模式：本机浏览器普通启动（无自动化标志——navigator.webdriver=false）+ CDP 连入。
                await InitializeAttachAsync(ct);
            }
            else
            {
                // channel 未指定 → 探测本机 Chrome/Edge（免 playwright install chromium；都没有才用自带 chromium）。
                channel = string.IsNullOrWhiteSpace(channel) ? BrowserChannel.DetectDefault() : channel;
                if (!string.IsNullOrWhiteSpace(_userDataDir))
                {
                    // 持久 profile：cookie/会话存盘，登录一次后续复用（不碰日常浏览器）。
                    var popts = new BrowserTypeLaunchPersistentContextOptions { Headless = false };
                    if (!string.IsNullOrWhiteSpace(channel)) popts.Channel = channel;
                    _context = await _playwright.Chromium.LaunchPersistentContextAsync(_userDataDir, popts);
                    _context.Close += OnContextClosed;
                    _page = _context.Pages.Count > 0 ? _context.Pages[0] : await _context.NewPageAsync();
                }
                else
                {
                    // 临时 profile（兼容旧行为）：每次新开、不持久。
                    var opts = new BrowserTypeLaunchOptions { Headless = false };
                    if (!string.IsNullOrWhiteSpace(channel)) opts.Channel = channel;
                    _browser = await _playwright.Chromium.LaunchAsync(opts);
                    _browser.Disconnected += OnBrowserDisconnected;
                    _context = await _browser.NewContextAsync();
                    _page = await _context.NewPageAsync();
                }
            }
            _page.Request += OnRequest;
            _page.Response += OnResponse;
            _page.RequestFinished += OnRequestFinished;
            _page.RequestFailed += OnRequestFailed;
            _page.FrameNavigated += OnFrameNavigated;
            if (!string.IsNullOrEmpty(url) && url != "about:blank")
            {
                try { await _page.GotoAsync(url, new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 60000 }); }
                catch (Exception ex) { Trace.WriteLine($"browser goto {url} failed: {ex.Message}"); }
            }
            UpdateMetadata();
        }
        finally { _opLock.Release(); }
    }

    /// <summary>附加模式初始化：自己 Process 启动本机浏览器（仅 --remote-debugging-port + profile，无自动化标志），
    /// 等调试端口就绪后 ConnectOverCDP 连入。浏览器进程由本会话持有——Dispose 时一并回收。
    /// 请求/cookie/操作三件套经 CDP 与常规模式完全同构（面板与工具零感知差异）。</summary>
    private async Task InitializeAttachAsync(CancellationToken ct)
    {
        var exe = BrowserChannel.ResolveExecutable();
        if (exe is null)
            throw new InvalidOperationException("附加模式需要本机安装 Chrome/Edge（未找到可执行文件）。切回「常规」模式，或装好浏览器再试。");
        var port = GetFreeTcpPort();
        var userDataDir = _userDataDir ?? Path.Combine(Path.GetTempPath(), $"pw-attach-{Id:N}");
        var psi = new ProcessStartInfo
        {
            FileName = exe,
            Arguments = $"--remote-debugging-port={port} --user-data-dir=\"{userDataDir}\" --no-first-run --no-default-browser-check about:blank",
            UseShellExecute = false,
        };
        _attachProcess = Process.Start(psi) ?? throw new InvalidOperationException("浏览器进程启动失败。");
        await WaitForDebugEndpointAsync(port, ct);
        _browser = await _playwright!.Chromium.ConnectOverCDPAsync($"http://127.0.0.1:{port}");
        _browser.Disconnected += OnBrowserDisconnected;
        _context = _browser.Contexts.Count > 0 ? _browser.Contexts[0] : await _browser.NewContextAsync();
        _page = _context.Pages.Count > 0 ? _context.Pages[0] : await _context.NewPageAsync();
    }

    /// <summary>挑一个空闲 TCP 端口（附加模式每会话一个调试端口，用完即弃）。</summary>
    private static int GetFreeTcpPort()
    {
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        try { return ((System.Net.IPEndPoint)listener.LocalEndpoint).Port; }
        finally { listener.Stop(); }
    }

    /// <summary>等调试端口的 /json/version 就绪（浏览器冷启动要几秒）：250ms 轮询，20s 超时。</summary>
    private static async Task WaitForDebugEndpointAsync(int port, CancellationToken ct)
    {
        using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        var deadline = DateTimeOffset.UtcNow.AddSeconds(20);
        while (DateTimeOffset.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                using var resp = await http.GetAsync($"http://127.0.0.1:{port}/json/version");
                if (resp.IsSuccessStatusCode) return;
            }
            catch { /* 未就绪，继续轮询 */ }
            await Task.Delay(250, ct);
        }
        throw new TimeoutException($"浏览器调试端口 127.0.0.1:{port} 20s 内未就绪。");
    }

    public async Task NavigateAsync(string url)
    {
        if (!_isAlive || _page is null) return;
        await _opLock.WaitAsync();
        try
        {
            if (!_isAlive || _page is null) return;
            if (url == "about:blank") { await _page.GotoAsync("about:blank"); return; }
            await _page.GotoAsync(url, new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 60000 });
            UpdateMetadata();
        }
        catch (Exception ex) { Trace.WriteLine($"browser navigate {url} failed: {ex.Message}"); }
        finally { _opLock.Release(); }
    }

    public async Task<BrowserCookieResult> GetCookiesAsync(List<string>? cookieNames, CancellationToken ct)
    {
        if (!_isAlive || _context is null || _page is null)
            return new BrowserCookieResult { Success = false, Error = "会话已关闭" };
        await _opLock.WaitAsync(ct);
        try
        {
            if (!_isAlive || _context is null || _page is null)
                return new BrowserCookieResult { Success = false, Error = "会话已关闭" };
            var pageUrl = _url;
            var cookies = string.IsNullOrEmpty(pageUrl) || pageUrl == "about:blank"
                ? await _context.CookiesAsync()
                : await _context.CookiesAsync(new[] { pageUrl });
            var list = cookies.ToList();
            if (cookieNames is { Count: > 0 })
            {
                var wanted = cookieNames.ToHashSet();
                list = list.Where(c => wanted.Contains(c.Name)).ToList();
            }
            var cookieInfos = list.Select(c => new CookieInfo
            {
                Name = c.Name,
                Value = Truncate(c.Value),
                Domain = c.Domain ?? string.Empty,
                Path = c.Path ?? string.Empty,
            }).ToList();
            var header = string.Join("; ", list.Where(c => !string.IsNullOrEmpty(c.Value)).Select(c => $"{c.Name}={Truncate(c.Value)}"));
            return new BrowserCookieResult
            {
                Success = true,
                CookieHeader = header,
                Cookies = cookieInfos,
                FinalUrl = pageUrl,
            };
        }
        catch (Exception ex)
        {
            return new BrowserCookieResult { Success = false, Error = ex.Message };
        }
        finally { _opLock.Release(); }
    }

    /// <summary>点击当前页面元素（10s 超时；Playwright 报错原文透传给 agent 自纠——含等待的选择器）。</summary>
    public async Task<BrowserActionResult> ClickAsync(string selector, CancellationToken ct)
    {
        if (!_isAlive || _page is null)
            return new BrowserActionResult { Success = false, Error = "会话已关闭" };
        await _opLock.WaitAsync(ct);
        try
        {
            if (!_isAlive || _page is null)
                return new BrowserActionResult { Success = false, Error = "会话已关闭" };
            await _page.ClickAsync(selector, new PageClickOptions { Timeout = 10_000 });
            await TrySettleAfterAction();
            UpdateMetadata();
            return new BrowserActionResult { Success = true, Url = _url };
        }
        catch (Exception ex)
        {
            return new BrowserActionResult { Success = false, Error = ex.Message };
        }
        finally { _opLock.Release(); }
    }

    /// <summary>向输入框填文本（fill 语义——整体替换；不是追加）；pressEnter 后等导航落定再取 URL。</summary>
    public async Task<BrowserActionResult> TypeAsync(string selector, string text, bool pressEnter, CancellationToken ct)
    {
        if (!_isAlive || _page is null)
            return new BrowserActionResult { Success = false, Error = "会话已关闭" };
        await _opLock.WaitAsync(ct);
        try
        {
            if (!_isAlive || _page is null)
                return new BrowserActionResult { Success = false, Error = "会话已关闭" };
            await _page.FillAsync(selector, text, new PageFillOptions { Timeout = 10_000 });
            if (pressEnter)
            {
                await _page.Keyboard.PressAsync("Enter");
                await TrySettleAfterAction();
            }
            UpdateMetadata();
            return new BrowserActionResult { Success = true, Url = _url };
        }
        catch (Exception ex)
        {
            return new BrowserActionResult { Success = false, Error = ex.Message };
        }
        finally { _opLock.Release(); }
    }

    /// <summary>读当前页面：URL + 标题 + 正文可见文本（innerText 截断 ~2 万字——喂 agent 上下文的量级）。
    /// 标题/正文各自的读取失败不作为整体失败（页面可能正在导航），拿到多少给多少。</summary>
    public async Task<BrowserPageContent> ReadPageAsync(CancellationToken ct)
    {
        if (!_isAlive || _page is null)
            return new BrowserPageContent { Success = false, Error = "会话已关闭" };
        await _opLock.WaitAsync(ct);
        try
        {
            if (!_isAlive || _page is null)
                return new BrowserPageContent { Success = false, Error = "会话已关闭" };
            var url = _page.Url ?? string.Empty;
            var title = string.Empty;
            try { title = await _page.TitleAsync(); } catch { /* 导航中 */ }
            var text = string.Empty;
            try { text = await _page.InnerTextAsync("body"); } catch { /* 导航中/无 body */ }
            const int maxReadChars = 20_000;
            var truncated = text.Length > maxReadChars;
            return new BrowserPageContent
            {
                Success = true,
                Url = url,
                Title = title,
                Text = truncated
                    ? text[..maxReadChars] + "\n…(已截断，需要更多内容时用 browser_click 翻页/展开后再读)"
                    : text,
                Truncated = truncated,
            };
        }
        catch (Exception ex)
        {
            return new BrowserPageContent { Success = false, Error = ex.Message };
        }
        finally { _opLock.Release(); }
    }

    /// <summary>操作后等 DOMContentLoaded（≤3s）——点击/回车常触发导航，等一下再读 URL 才是落点；超时/无导航忽略不算失败。</summary>
    private async Task TrySettleAfterAction()
    {
        try { await _page!.WaitForLoadStateAsync(LoadState.DOMContentLoaded, new PageWaitForLoadStateOptions { Timeout = 3000 }); }
        catch { /* 超时或已在导航——非致命 */ }
    }

    public IReadOnlyList<NetworkRequestItem> GetRequestsSnapshot()
    {
        lock (_stateLock) return _requests.ToList();
    }

    public void ClearRequests()
    {
        lock (_stateLock) { _byRequest.Clear(); _requests.Clear(); }
    }

    public void MarkDead()
    {
        _isAlive = false;
        _title = "(已关闭)";
    }

    public async ValueTask DisposeAsync()
    {
        _requestsDebounce.Dispose();
        if (_page is not null)
        {
            _page.Request -= OnRequest;
            _page.Response -= OnResponse;
            _page.RequestFinished -= OnRequestFinished;
            _page.RequestFailed -= OnRequestFailed;
            _page.FrameNavigated -= OnFrameNavigated;
        }
        if (_browser is not null) _browser.Disconnected -= OnBrowserDisconnected;
        if (_context is not null) _context.Close -= OnContextClosed;
        await _opLock.WaitAsync();
        try
        {
            try { if (_page is not null) await _page.CloseAsync(); } catch { }
            try { if (_context is not null) await _context.CloseAsync(); } catch { }
            try { if (_browser is not null) await _browser.CloseAsync(); } catch { }
            // 附加模式：连入的浏览器可能只断连不退出——进程是我们起的，兜底杀掉（连进程树）。
            try { if (_attachProcess is { HasExited: false }) _attachProcess.Kill(entireProcessTree: true); } catch { }
            _attachProcess?.Dispose();
            _playwright?.Dispose();
        }
        catch { }
        finally { _opLock.Release(); }
    }

    // —— Playwright 事件（驱动回调线程；只动请求字典/列表，经 _stateLock 保护；字段更新靠 Avalonia marshal）——

    private void OnRequest(object? sender, IRequest request)
    {
        var item = new NetworkRequestItem(Guid.NewGuid(), Id, DateTimeOffset.UtcNow, request.Method, request.Url);
        try { item.RequestHeaders = JsonSerializer.Serialize(request.Headers); } catch { }
        try { item.ResourceType = request.ResourceType.ToString(); } catch { }
        try { item.RequestBody = TryGetRequestBody(request); } catch { }
        lock (_stateLock)
        {
            if (!_byRequest.TryAdd(request, item)) return;   // 同请求重复事件去重
            _requests.Add(item);
            // 上限丢最旧：超 MaxRequests 移除头部溢出；并反向清理 _byRequest 中对应条目
            // （_byRequest 只增不减，不随 _requests 封顶则被丢 item 的 body 经字典常驻 → cap 形同虚设）。
            if (_requests.Count > MaxRequests)
            {
                var overflow = _requests.Count - MaxRequests;
                var dropped = new HashSet<NetworkRequestItem>(_requests.Take(overflow));
                _requests.RemoveRange(0, overflow);
                foreach (var k in _byRequest.Where(kvp => dropped.Contains(kvp.Value)).Select(kvp => kvp.Key).ToArray())
                    _byRequest.Remove(k);
            }
        }
        ScheduleDebounce();
    }

    private void OnResponse(object? sender, IResponse response)
    {
        NetworkRequestItem? item;
        lock (_stateLock) { _byRequest.TryGetValue(response.Request, out item); }
        if (item is null) return;
        try
        {
            item.Status = response.Status;
            item.StatusText = response.StatusText;
            try { item.ResponseHeaders = JsonSerializer.Serialize(response.Headers); } catch { }
            item.IsError = !response.Ok;
            // 异步捕获 response body（不阻塞事件线程；redirect/已导航拿 body 会抛异常，catch 留空）。
            CaptureResponseBodyAsync(response, item).SafeFireAndForget();
        }
        catch { }
        ScheduleDebounce();
    }

    /// <summary>异步抓 response body（BurpSuite 式——看 API/页面实际返回内容）。等 FinishedAsync 再 BodyAsync 防不完整；
    /// 二进制判别（content-type + 前 1024 字节含 null）不存文本；文本截断 MaxBodyChars 防内存无界，超长提示用 curl 重发取全。</summary>
    private async Task CaptureResponseBodyAsync(IResponse response, NetworkRequestItem item)
    {
        try
        {
            await response.FinishedAsync();
            var bytes = await response.BodyAsync();
            if (bytes is null || bytes.Length == 0) return;
            item.Size = bytes.Length;
            var ct = TryGetContentType(response);
            item.MimeType = ct;
            var isBinary = IsBinaryContentType(ct) || HasBinaryBytes(bytes);
            item.IsBinary = isBinary;
            if (isBinary) return;   // 二进制不存文本（详情显示「[二进制 {size} 字节]」占位）
            var text = Encoding.UTF8.GetString(bytes);
            item.ResponseBody = text.Length > MaxBodyChars
                ? text[..MaxBodyChars] + "\n…(已截断，完整内容请用 browser_get_cookies + curl 重发获取)"
                : text;
            ScheduleDebounce();   // body 回填后刷列表 Size 列 + 触发详情重读
        }
        catch { /* redirect/已导航/服务工人：BodyAsync 抛异常，留空 */ }
    }

    private static string? TryGetRequestBody(IRequest request)
    {
        // Playwright 1.61: PostDataBuffer(byte[]?)/PostData(string?) 同步属性，无 PostDataAsync。截断 4000（同 cookie Truncate 范式防灌爆）。
        if (request.PostDataBuffer is { Length: > 0 } buf)
            return buf.Length > 4000 ? Encoding.UTF8.GetString(buf, 0, 4000) + "…(已截断)" : Encoding.UTF8.GetString(buf);
        return request.PostData is { Length: > 0 } ps ? (ps.Length > 4000 ? ps[..4000] + "…(已截断)" : ps) : null;
    }

    private static string? TryGetContentType(IResponse response)
    {
        try { return response.Headers.TryGetValue("content-type", out var v) && !string.IsNullOrWhiteSpace(v) ? v.ToLowerInvariant() : null; }
        catch { return null; }
    }

    private static bool IsBinaryContentType(string? ct) =>
        !string.IsNullOrEmpty(ct) && (
            ct.Contains("image/") || ct.Contains("video/") || ct.Contains("audio/") ||
            ct.Contains("font") || ct.Contains("octet-stream") || ct.Contains("protobuf") ||
            ct.Contains("application/zip") || ct.Contains("application/gzip") || ct.Contains("application/pdf") ||
            ct.Contains("application/x-"));

    private static bool HasBinaryBytes(byte[] bytes)
    {
        // 前 1024 字节含 null 字节 → 二进制（同 file 命令思路；text/utf8 早期不含 null）。
        var n = Math.Min(bytes.Length, 1024);
        for (int i = 0; i < n; i++) if (bytes[i] == 0) return true;
        return false;
    }

    private void OnRequestFinished(object? sender, IRequest request)
    {
        NetworkRequestItem? item;
        lock (_stateLock) { _byRequest.TryGetValue(request, out item); }
        if (item is null) return;
        item.IsPending = false;
        item.DurationMs = (int)(DateTimeOffset.UtcNow - item.Time).TotalMilliseconds;
        ScheduleDebounce();
    }

    private void OnRequestFailed(object? sender, IRequest request)
    {
        NetworkRequestItem? item;
        lock (_stateLock) { _byRequest.TryGetValue(request, out item); }
        if (item is null) return;
        item.IsPending = false;
        item.IsError = true;
        item.DurationMs = (int)(DateTimeOffset.UtcNow - item.Time).TotalMilliseconds;
        ScheduleDebounce();
    }

    private void OnFrameNavigated(object? sender, IFrame frame)
    {
        // 主框架导航 → 更新会话 URL/Title（标签条 + 地址栏同步）。
        if (frame != _page?.MainFrame) return;
        UpdateMetadata();
    }

    private void OnBrowserDisconnected(object? sender, IBrowser browser)
    {
        // 用户手动关了浏览器窗口 / 浏览器崩溃 → 标记会话已死，标签保留供用户关闭。
        if (!_isAlive) return;
        MarkDead();
        _owner.RaiseSessionClosed(Id);
        // 后台清理 Playwright 资源（不阻塞事件线程；已断开，主要释放驱动进程）。
        DisposeAsync().AsTask().SafeFireAndForget();
    }

    private void OnContextClosed(object? sender, IBrowserContext context)
    {
        // 持久模式：context 关闭（用户关浏览器窗口 / 崩溃）→ 标记会话已死。同 OnBrowserDisconnected 语义。
        if (!_isAlive) return;
        MarkDead();
        _owner.RaiseSessionClosed(Id);
        DisposeAsync().AsTask().SafeFireAndForget();
    }

    private void UpdateMetadata()
    {
        try
        {
            if (_page is null) return;
            var u = _page.Url ?? string.Empty;
            _url = u;
            _title = DeriveTitle(u);
            _owner.RaiseSessionsChanged();
        }
        catch { }
    }

    private static string DeriveTitle(string url)
    {
        if (string.IsNullOrEmpty(url) || url == "about:blank") return "新建会话";
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri)) return uri.Host;
        return url.Length > 40 ? url[..40] + "…" : url;
    }

    /// <summary>请求通知去抖：每次请求重置 150ms 定时器，合批页面加载的突发请求为一次刷新。</summary>
    private void ScheduleDebounce() =>
        _requestsDebounce.Change(150, Timeout.Infinite);

    private static string Truncate(string? value, int max = 4000) =>
        string.IsNullOrEmpty(value) ? string.Empty :
        (value.Length <= max ? value : value[..max] + "…(已截断)");
}
