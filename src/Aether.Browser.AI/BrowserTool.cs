using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Aether.Browser.AI;

/// <summary>浏览器 agent 工具组（宿主层，需 <see cref="IBrowserService"/>——依赖本机浏览器进程，桌面专属）。
/// agent 工作流：<see cref="BrowserOpenAsync"/> 打开浏览器到登录页（用户手动登录）→ <see cref="GetCookiesAsync"/> 拿 curl
/// 直接用的 CookieHeader → ExecuteShellCommand 等命令工具跑 <c>curl -s -H "Cookie: {CookieHeader}" https://同域/任意路径</c>
/// 访问需登录接口。与用户面板共用同一 <see cref="IBrowserService"/> 单例：agent 取活跃会话、无则建新（用户可见）。
/// （自 monorepo port，2026-10-29；审批等级沿用原版——open/get_requests/close Safe，get_cookies Confirm：cookie 是登录凭证。）</summary>
public sealed class BrowserTool
{
    private readonly IBrowserService _service;
    public BrowserTool(IBrowserService service) => _service = service;

    /// <summary>打开浏览器到指定 URL（headed 独立 OS 窗口，用户手动登录）。有活跃会话则导航它，否则建新。
    /// channel=chrome/msedge 显式指定浏览器；不传则自动探测本机 Chrome/Edge（免 playwright install chromium）。
    /// 启动方式（常规/附加）遵循宿主在浏览器面板设的默认——agent 不参与此决策。</summary>
    public async Task<BrowserOpenOutput> BrowserOpenAsync(
        [Description("要打开的登录页或网站 URL")] string url,
        [Description("浏览器通道：chrome/msedge 显式指定；不传自动探测本机 Chrome/Edge，都没有才用 Playwright 自带 chromium（需 playwright install chromium）")] string? channel = null,
        CancellationToken cancellationToken = default)
    {
        var session = await _service.EnsureOpenAsync(url, channel, mode: null, cancellationToken);
        return new BrowserOpenOutput
        {
            Success = session.IsAlive,
            SessionId = session.Id,
            Url = session.Url ?? url,
            Title = session.Title,
            IsAlive = session.IsAlive,
            Error = session.IsAlive ? null : "浏览器打开失败。装 Chrome/Edge（默认自动复用），或执行 playwright install chromium。",
        };
    }

    /// <summary>提取当前活跃浏览器会话的 cookie（Playwright 按 host 过滤，含父子域 + HttpOnly）。
    /// 返回 curl 直接用的 <see cref="BrowserCookieResult.CookieHeader"/>（name=value; name=value; ...）+ 明细。
    /// ⚠ cookie 是登录凭证，会进 agent 上下文供后续 curl 用。</summary>
    public async Task<BrowserCookieResult> GetCookiesAsync(
        [Description("只返回指定名字的 cookie（过滤 analytics/csrf 噪音）。不传则返回该 URL 适用的所有 cookie")] List<string>? cookieNames = null,
        CancellationToken cancellationToken = default)
    {
        var activeId = _service.ActiveSessionId;
        if (activeId is null)
            return new BrowserCookieResult { Success = false, Error = "无活跃浏览器会话。先用 browser_open 打开登录页并完成登录。" };
        return await _service.GetCookiesAsync(activeId.Value, cookieNames, cancellationToken);
    }

    /// <summary>取当前活跃浏览器会话已记录的 HTTP 请求列表（DevTools Network 风格）。
    /// 用于发现登录后调用的 API 端点与其状态，或确认登录是否成功（登录接口 200 + Set-Cookie）。</summary>
    public Task<BrowserRequestsOutput> GetRequestsAsync(
        [Description("只返回 URL 含此片段的请求（大小写不敏感）。不传则全量")] string? urlContains = null,
        [Description("限制返回条数（默认 50）。agent 可传大值取全量")] int? limit = 50,
        [Description("只看失败/4xx/5xx 请求")] bool errorsOnly = false,
        CancellationToken cancellationToken = default)
    {
        var activeId = _service.ActiveSessionId;
        if (activeId is null)
            return Task.FromResult(new BrowserRequestsOutput { Success = false, Error = "无活跃浏览器会话。" });
        var reqs = _service.GetRequests(activeId.Value);
        IEnumerable<NetworkRequestItem> filtered = reqs;
        if (!string.IsNullOrEmpty(urlContains))
            filtered = filtered.Where(r => r.Url.Contains(urlContains, StringComparison.OrdinalIgnoreCase));
        if (errorsOnly) filtered = filtered.Where(r => r.IsError);
        var limited = limit is { } l && l > 0 ? filtered.Take(l) : filtered;
        var summaries = limited.Select(r => new BrowserRequestSummary
        {
            Method = r.Method,
            Url = r.Url,
            Status = r.Status,
            StatusText = r.StatusText,
            DurationMs = r.DurationMs,
            ResourceType = r.ResourceType,
            IsError = r.IsError,
        }).ToList();
        return Task.FromResult(new BrowserRequestsOutput { Success = true, Count = summaries.Count, Requests = summaries });
    }

    /// <summary>关闭当前活跃浏览器会话（关浏览器 + 释放 Playwright 资源）。</summary>
    public Task<BrowserCloseOutput> CloseAsync(CancellationToken cancellationToken = default)
    {
        var activeId = _service.ActiveSessionId;
        if (activeId is null)
            return Task.FromResult(new BrowserCloseOutput { Success = false, Error = "无活跃浏览器会话。" });
        _service.CloseSession(activeId.Value);
        return Task.FromResult(new BrowserCloseOutput { Success = true });
    }

    /// <summary>读取当前活跃会话的页面内容：URL、标题、正文可见文本。操作前先读页面，据此决定点哪/填哪。</summary>
    public async Task<BrowserPageContent> BrowserReadAsync(CancellationToken cancellationToken = default)
    {
        var activeId = _service.ActiveSessionId;
        if (activeId is null)
            return new BrowserPageContent { Success = false, Error = "无活跃浏览器会话。先用 browser_open 打开页面。" };
        return await _service.ReadPageAsync(activeId.Value, cancellationToken);
    }

    /// <summary>点击当前活跃会话页面上的元素（点击可能触发导航，返回操作后的 URL）。</summary>
    public async Task<BrowserActionResult> BrowserClickAsync(
        [Description("要点击的元素选择器（Playwright 语法）：text=登录（按可见文本）/ role=button[name=\"提交\"] / css=#id / css=.class / xpath=//button[1]。找不到或不可点约 10s 超时报错（报错含等待的选择器）——换一种写法重试")] string selector,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(selector))
            return new BrowserActionResult { Success = false, Error = "selector 不能为空。" };
        var activeId = _service.ActiveSessionId;
        if (activeId is null)
            return new BrowserActionResult { Success = false, Error = "无活跃浏览器会话。先用 browser_open 打开页面。" };
        return await _service.ClickAsync(activeId.Value, selector, cancellationToken);
    }

    /// <summary>向页面输入框填文本（fill 语义——整体替换框内现有内容，不是追加）。</summary>
    public async Task<BrowserActionResult> BrowserTypeAsync(
        [Description("目标输入框的选择器（Playwright 语法，同 browser_click）")] string selector,
        [Description("要输入的文本")] string text,
        [Description("输入后按回车（搜索框无按钮时用；会提交表单的场景慎用）")] bool pressEnter = false,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(selector))
            return new BrowserActionResult { Success = false, Error = "selector 不能为空。" };
        var activeId = _service.ActiveSessionId;
        if (activeId is null)
            return new BrowserActionResult { Success = false, Error = "无活跃浏览器会话。先用 browser_open 打开页面。" };
        return await _service.TypeAsync(activeId.Value, selector, text, pressEnter, cancellationToken);
    }
}

public sealed class BrowserOpenOutput
{
    public bool Success { get; set; }
    public Guid SessionId { get; set; }
    public string? Url { get; set; }
    public string? Title { get; set; }
    public bool IsAlive { get; set; }
    public string? Error { get; set; }
}

public sealed class BrowserRequestsOutput
{
    public bool Success { get; set; }
    public int Count { get; set; }
    public List<BrowserRequestSummary> Requests { get; set; } = new();
    public string? Error { get; set; }
}

public sealed class BrowserRequestSummary
{
    public string Method { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public int? Status { get; set; }
    public string? StatusText { get; set; }
    public int? DurationMs { get; set; }
    public string? ResourceType { get; set; }
    public bool IsError { get; set; }
}

public sealed class BrowserCloseOutput
{
    public bool Success { get; set; }
    public string? Error { get; set; }
}
