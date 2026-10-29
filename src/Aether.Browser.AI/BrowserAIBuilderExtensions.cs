using System.Reflection;
using Aether.AI.Core.Agents;
using Aether.AI.Hosting;
using Aether.AI.Harness.Tools;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace Aether.Browser.AI;

/// <summary>
/// 浏览器工具模块的注册入口——**一个领域一个模块**（同 SystemTools/AppAudit 惯例）。
/// 注册内容：IBrowserService 单例（持久 Playwright 会话）+ 4 个 agent 工具。
/// <para>本模块是 ⑭ 的第一个**桌面专属**模块（2026-10-29 用户定调「AI 调用的工具就进 ⑭」）：
/// 依赖 headed 浏览器与本机进程，全部工具声明 <see cref="HostCapability.DesktopOnly"/>——
/// 移动/无浏览器宿主不挂本模块即可；误挂载时 ResolveAll 也按宿主能力兜底跳过。
/// profile 目录读 <see cref="AetherAIOptions.BrowserProfileDir"/>（宿主侧接线——⑮ 设 dataDir/browser-profile），
/// 与「凭据全宿主侧接线」的适配器约定一致。**UI 面板不在本模块**：⑮ 的桌面切片（AddBrowserPanel）
/// 引用本模块并贡献 View/VM——工具与面板互不感知，共用同一 service 单例。</para>
/// <para>审批等级沿用 monorepo 原版：open/get_requests/close = Safe；get_cookies = Confirm
/// （cookie 是登录凭证，会进 agent 上下文——必须过用户确认）。</para>
/// </summary>
public static class BrowserAIBuilderExtensions
{
    /// <summary>本模块注册的工具名（宿主可据此做工具白名单/能力策略）。</summary>
    public static readonly string[] ToolNames =
        { "browser_open", "browser_get_cookies", "browser_get_requests", "browser_close",
          "browser_read", "browser_click", "browser_type" };

    public static AetherAIBuilder AddBrowserTools(this AetherAIBuilder builder)
    {
        var s = builder.Services;
        s.AddSingleton<IBrowserService, BrowserService>();
        s.AddTransient<BrowserTool>();

        const string cat = "浏览器";
        builder.ConfigureToolRegistry(reg =>
        {
            reg.Register("browser_open", "打开浏览器到指定 URL（headed 独立 OS 窗口，用户手动登录）。有活跃会话则导航它，否则建新。" +
                "channel 不传自动探测本机 Chrome/Edge。用法：browser_open(url='https://xxx/login') → 让用户登录 → " +
                "browser_get_cookies() 拿 CookieHeader → 命令工具跑 curl -s -H \"Cookie: <CookieHeader>\" https://同域/任意路径。", cat,
                sp => Create(sp, nameof(BrowserTool.BrowserOpenAsync), "browser_open"),
                ToolApprovalLevel.Safe, HostCapability.DesktopOnly);

            reg.Register("browser_get_cookies", "提取当前活跃浏览器会话的 cookie（Playwright 按 host 过滤，含父子域 + HttpOnly）。" +
                "返回 curl 直接用的 CookieHeader 串（name=value; name=value; ...）+ 明细。⚠ cookie 是登录凭证，会进 agent 上下文供后续 curl 用。" +
                "cookieNames 非空时只取指定 cookie。建议先用 browser_open + 让用户登录，再调此工具。", cat,
                sp => Create(sp, nameof(BrowserTool.GetCookiesAsync), "browser_get_cookies"),
                ToolApprovalLevel.Confirm, HostCapability.DesktopOnly);

            reg.Register("browser_get_requests", "取当前活跃浏览器会话已记录的 HTTP 请求列表（DevTools Network 风格）。" +
                "urlContains 过滤 URL；errorsOnly 只看失败/4xx/5xx；limit 限制条数（默认 50）。用于发现登录后调用的 API 端点与请求状态。", cat,
                sp => Create(sp, nameof(BrowserTool.GetRequestsAsync), "browser_get_requests"),
                ToolApprovalLevel.Safe, HostCapability.DesktopOnly);

            reg.Register("browser_close", "关闭当前活跃浏览器会话（关浏览器 + 释放 Playwright 资源）。", cat,
                sp => Create(sp, nameof(BrowserTool.CloseAsync), "browser_close"),
                ToolApprovalLevel.Safe, HostCapability.DesktopOnly);

            // —— 操作三件套（2026-10-29 同批补齐「AI 操作浏览器」）：read = 看页（Safe），
            //    click/type = 在已登录会话上动手（Confirm——审批卡可勾「总是允许」减摩擦）。
            reg.Register("browser_read", "读取当前活跃浏览器会话的页面内容：URL、标题、正文可见文本（innerText，约 2 万字截断）。" +
                "操作浏览器前先读页面，据此决定点哪/填哪。", cat,
                sp => Create(sp, nameof(BrowserTool.BrowserReadAsync), "browser_read"),
                ToolApprovalLevel.Safe, HostCapability.DesktopOnly);

            reg.Register("browser_click", "点击当前活跃浏览器会话页面上的元素。selector 用 Playwright 语法：" +
                "text=登录（按可见文本）/ role=button[name=\"提交\"] / css=#id / css=.class / xpath=//button[1]。" +
                "找不到或不可点约 10s 超时报错（报错含等待的选择器）——换一种写法重试。点击可能触发导航，返回操作后的 URL。", cat,
                sp => Create(sp, nameof(BrowserTool.BrowserClickAsync), "browser_click"),
                ToolApprovalLevel.Confirm, HostCapability.DesktopOnly);

            reg.Register("browser_type", "向页面输入框填文本（fill 语义——整体替换框内现有内容，不是追加）。" +
                "selector 语法同 browser_click；pressEnter=true 输入后按回车（搜索框无按钮时用，会提交表单的场景慎用）。", cat,
                sp => Create(sp, nameof(BrowserTool.BrowserTypeAsync), "browser_type"),
                ToolApprovalLevel.Confirm, HostCapability.DesktopOnly);
        });
        return builder;
    }

    private static AIFunction Create(IServiceProvider sp, string method, string toolName)
    {
        var tool = sp.GetRequiredService<BrowserTool>();
        var m = typeof(BrowserTool).GetMethod(method, BindingFlags.Public | BindingFlags.Instance)
            ?? throw new InvalidOperationException($"Tool method '{method}' not found on BrowserTool.");
        return AIFunctionFactory.Create(m, tool, toolName);
    }
}
