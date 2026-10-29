using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Aether.Browser.AI;

/// <summary>浏览器会话的启动方式（**宿主策略**——⑮ 面板开关/配置选，agent 工具不暴露此参数）。</summary>
public enum BrowserLaunchMode
{
    /// <summary>常规：Playwright 直接启动。浏览器以自动化标志运行——<c>navigator.webdriver=true</c>，站点一行 JS 可查（明牌）。</summary>
    Launch,
    /// <summary>附加：先以普通方式启动本机 Chrome/Edge（仅带 --remote-debugging-port，无自动化标志——
    /// <c>navigator.webdriver=false</c>，2026-10-29 探针实测），Playwright 经 CDP 连入操作。
    /// ⚠ 操作行为层（无轨迹点击/瞬时输入）仍可被行为分析识别——不承诺隐身。需本机装有 Chrome/Edge。</summary>
    Attach,
}

/// <summary>一个浏览器会话的 UI 无关句柄（标识 + 元数据）。真实 Playwright 浏览器/页面生命周期由 <see cref="BrowserService"/> 代管。
/// 浏览器画面是 Playwright headed 独立 OS 窗口；app 内面板只展示请求列表，不嵌入浏览器画面。
/// （自 monorepo Acme.Desktop.Mvvm port，2026-10-29；线程/内存语义原样——见 BrowserService 头注释。）</summary>
public interface IBrowserSession
{
    Guid Id { get; }
    /// <summary>当前页面 URL（导航/跳转时更新）。</summary>
    string? Url { get; }
    /// <summary>会话标题（页面 host 或地址，标签条显示）。</summary>
    string Title { get; }
    /// <summary>浏览器进程是否仍在（关闭后 false，标签保留供用户关闭）。</summary>
    bool IsAlive { get; }
}

/// <summary>单条 HTTP 请求记录（VM 绑定 + agent 工具返回）。Request/Response 事件落此对象，Response 到达时回填 status/duration。</summary>
public partial class NetworkRequestItem : ObservableObject
{
    public Guid Id { get; }
    public Guid SessionId { get; }
    /// <summary>请求发起时刻（UTC，VM 显示转本地时间）。</summary>
    public DateTimeOffset Time { get; }
    public string Method { get; } = string.Empty;
    public string Url { get; } = string.Empty;

    [ObservableProperty] private int? _status;          // null=未收到响应（pending）
    [ObservableProperty] private string? _statusText;
    [ObservableProperty] private int? _durationMs;      // 请求→响应耗时
    [ObservableProperty] private string? _resourceType; // document/xhr/fetch/image/css/script...
    [ObservableProperty] private bool _isError;         // 请求失败/4xx/5xx
    [ObservableProperty] private bool _isPending;       // 已发未收响应（true=转圈）

    /// <summary>请求头 JSON（点开详情看，含 Cookie——辅助 agent 知道带了哪些凭证）。</summary>
    public string? RequestHeaders { get; set; }
    /// <summary>响应头 JSON（含 Set-Cookie——看登录后服务端 set 了哪些 cookie）。</summary>
    public string? ResponseHeaders { get; set; }
    /// <summary>请求体文本（POST 表单/JSON body；二进制留空）。点开详情看。</summary>
    public string? RequestBody { get; set; }
    /// <summary>响应体文本（非二进制 body 的文本渲染；二进制留空，详情显示占位）。点开详情看。</summary>
    public string? ResponseBody { get; set; }

    /// <summary>响应 Content-Type。列表 Size 列实时刷新故 Observable。</summary>
    [ObservableProperty] private string? _mimeType;
    /// <summary>响应体大小（字节；截断体标提示）。列表实时刷新故 Observable。</summary>
    [ObservableProperty] private int? _size;
    /// <summary>响应体是否二进制（二进制不存文本，详情显示「[二进制 {size} 字节]」）。列表实时刷新。</summary>
    [ObservableProperty] private bool _isBinary;

    public NetworkRequestItem(Guid id, Guid sessionId, DateTimeOffset time, string method, string url)
    {
        Id = id; SessionId = sessionId; Time = time; Method = method; Url = url;
        _isPending = true;
    }
}

/// <summary><see cref="IBrowserService.GetCookiesAsync"/> 的返回：curl 直接用的 Cookie 头串 + 明细。</summary>
public sealed class BrowserCookieResult
{
    public bool Success { get; set; }
    /// <summary>curl 直接用的 Cookie 头串："name=value; name=value; ..."。agent 拿到拼 curl -H "Cookie: {CookieHeader}" 即可。</summary>
    public string CookieHeader { get; set; } = string.Empty;
    /// <summary>cookie 明细（含 domain/path，调试 + agent 判断作用域用）。</summary>
    public List<CookieInfo> Cookies { get; set; } = new();
    /// <summary>提取时的页面 URL（调试用）。</summary>
    public string? FinalUrl { get; set; }
    public string? Error { get; set; }
}

/// <summary>单条 cookie 明细。</summary>
public sealed class CookieInfo
{
    public string Name { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
    public string Domain { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
}

/// <summary>一次页面操作（click/type）的结果。</summary>
public sealed class BrowserActionResult
{
    public bool Success { get; set; }
    /// <summary>操作后的页面 URL（点击/回车可能触发导航）。</summary>
    public string? Url { get; set; }
    public string? Error { get; set; }
}

/// <summary><see cref="IBrowserService.ReadPageAsync"/> 的返回：当前页面的可见内容。</summary>
public sealed class BrowserPageContent
{
    public bool Success { get; set; }
    public string? Url { get; set; }
    public string? Title { get; set; }
    /// <summary>正文可见文本（innerText；超长截断并置 <see cref="Truncated"/>）。</summary>
    public string Text { get; set; } = string.Empty;
    public bool Truncated { get; set; }
    public string? Error { get; set; }
}

/// <summary>浏览器网络面板程序化 API（UI 无关接口；impl = <see cref="BrowserService"/> 持 Playwright）。
/// 用户面板（<see cref="BrowserViewModel"/>）与 agent 工具（<see cref="BrowserTool"/>）共用同一单例：
/// 前者管会话 + 实时看请求列表，后者让 agent 开浏览器（用户登录）→ 拿 cookie → curl 带访问需登录接口。
/// 不嵌入浏览器画面（Playwright headed 独立窗口）。</summary>
public interface IBrowserService
{
    /// <summary>宿主策略：新建会话的默认启动方式（OpenSession 未显式传 mode 时使用；agent 的 browser_open 也遵循它）。
    /// 运行中会话不受影响——模式是会话创建时固化的出生属性。两种模式共用同一 profile 目录，登录态互通。</summary>
    BrowserLaunchMode DefaultLaunchMode { get; set; }

    /// <summary>开浏览器会话并导航到 url（启动在后台，立即返回句柄）。channel=chrome/msedge 复用本机浏览器；mode 默认走 <see cref="DefaultLaunchMode"/>。</summary>
    IBrowserSession OpenSession(string url, string? channel = null, BrowserLaunchMode? mode = null);
    /// <summary>关闭会话：关 page+context+browser+dispose Playwright 资源 + 移除标签 + 清请求记录。</summary>
    void CloseSession(Guid sessionId);
    /// <summary>设活跃会话（标签切换 → VM 切显示该会话的请求列表）。</summary>
    void SetActive(Guid sessionId);
    /// <summary>当前所有会话元数据快照（供 VM 投影标签列表）。</summary>
    IReadOnlyList<IBrowserSession> GetSessions();
    /// <summary>当前活跃会话 id（无会话为 null）。</summary>
    Guid? ActiveSessionId { get; }
    /// <summary>某会话导航到新 url（地址栏回车；agent browser_open 复用活跃会话时经 EnsureOpenAsync 间接调）。</summary>
    Task NavigateAsync(Guid sessionId, string url);
    /// <summary>复用-or-新建：有活跃会话则导航它（模式不变），否则按 mode（默认 <see cref="DefaultLaunchMode"/>）建新会话并等其就绪。agent browser_open 用此（用户可见）。</summary>
    Task<IBrowserSession> EnsureOpenAsync(string url, string? channel = null, BrowserLaunchMode? mode = null, CancellationToken cancellationToken = default);

    /// <summary>某会话的请求记录（按时间序）。VM 投影当前活跃会话的列表。</summary>
    IReadOnlyList<NetworkRequestItem> GetRequests(Guid sessionId);
    /// <summary>清某会话的请求记录。</summary>
    void ClearRequests(Guid sessionId);

    /// <summary>拿某会话当前页面适用 cookie（Playwright 按 host 过滤，含父子域匹配）。
    /// 返回 curl 直接用的 CookieHeader 串 + 明细。cookieNames 非空时只返回指定 cookie。</summary>
    Task<BrowserCookieResult> GetCookiesAsync(Guid sessionId, List<string>? cookieNames = null, CancellationToken cancellationToken = default);

    /// <summary>点击某会话当前页面的元素（Playwright selector：text= / role= / css= / xpath=）。
    /// 操作经 per-session 锁串行；点击后等 DOMContentLoaded ≤3s 再取落点 URL。</summary>
    Task<BrowserActionResult> ClickAsync(Guid sessionId, string selector, CancellationToken cancellationToken = default);

    /// <summary>向某会话页面的输入框填文本（fill 语义——整体替换框内现有内容）；pressEnter 输入后按回车。</summary>
    Task<BrowserActionResult> TypeAsync(Guid sessionId, string selector, string text, bool pressEnter = false, CancellationToken cancellationToken = default);

    /// <summary>读某会话当前页面：URL + 标题 + 正文可见文本（innerText，约 2 万字截断）。</summary>
    Task<BrowserPageContent> ReadPageAsync(Guid sessionId, CancellationToken cancellationToken = default);

    /// <summary>会话列表变更（新建/关闭/活跃态变化）——VM 刷新标签。</summary>
    event Action? SessionsChanged;
    /// <summary>活跃会话变更——VM 切显示该会话的请求列表。</summary>
    event Action<Guid>? ActiveSessionChanged;
    /// <summary>某会话有新请求/状态更新——VM 刷新该会话请求列表。</summary>
    event Action<Guid>? RequestsChanged;
    /// <summary>会话浏览器关闭（用户关了独立窗口/agent browser_close）——VM 标记标签为已停止。</summary>
    event Action<Guid>? SessionClosed;
}

/// <summary>浏览器标签元数据（VM 绑定用；由 <see cref="IBrowserService.GetSessions"/> 投影）。</summary>
public partial class BrowserSessionItem : ObservableObject
{
    public Guid Id { get; }

    [ObservableProperty] private string _title;
    [ObservableProperty] private string? _url;
    [ObservableProperty] private bool _isAlive;
    [ObservableProperty] private bool _isActive;
    /// <summary>该会话的请求计数（标签角标显示）。</summary>
    [ObservableProperty] private int _requestCount;

    public BrowserSessionItem(Guid id, string title, string? url, bool isAlive = true)
    {
        Id = id; _title = title; _url = url; _isAlive = isAlive;
    }
}
