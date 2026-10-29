using System;
using System.IO;

namespace Aether.Browser.AI;

/// <summary>
/// 默认浏览器通道探测：channel 未显式指定时，优先复用本机已装的 Chrome/Edge（免 <c>playwright install chromium</c>
/// 的 ~150MB 下载）——⑮ 面向自用桌面，机器上几乎必有其一；都没有才落回 Playwright 自带 chromium。
/// Playwright 的 channel 启动走系统浏览器稳定版，自动化协议（CDP）由 Playwright 驱动进程接管，与 bundled 无差。
/// </summary>
public static class BrowserChannel
{
    /// <summary>探测本机可复用的浏览器通道：有 Chrome → "chrome"；否则有 Edge → "msedge"；都没有 → null（Playwright 自带 chromium）。</summary>
    public static string? DetectDefault()
    {
        if (File.Exists(ChromePath())) return "chrome";
        if (File.Exists(EdgePath())) return "msedge";
        return null;
    }

    /// <summary>解析浏览器**可执行文件路径**（附加模式要自己 Process 启动它）：按 channel 指定或探测优先级找，
    /// 没有已装浏览器返回 null（附加模式不可用，调用方给明确报错）。</summary>
    public static string? ResolveExecutable(string? channel = null)
    {
        if (channel == "msedge") return File.Exists(EdgePath()) ? EdgePath() : null;
        if (channel == "chrome") return File.Exists(ChromePath()) ? ChromePath() : null;
        if (File.Exists(ChromePath())) return ChromePath();
        if (File.Exists(EdgePath())) return EdgePath();
        return null;
    }

    private static string ChromePath()
    {
        if (OperatingSystem.IsMacOS())
            return "/Applications/Google Chrome.app/Contents/MacOS/Google Chrome";
        if (OperatingSystem.IsWindows())
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "Google", "Chrome", "Application", "chrome.exe");
        return "/usr/bin/google-chrome";   // Linux（或 google-chrome-stable 的软链）
    }

    private static string EdgePath()
    {
        if (OperatingSystem.IsMacOS())
            return "/Applications/Microsoft Edge.app/Contents/MacOS/Microsoft Edge";
        if (OperatingSystem.IsWindows())
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "Microsoft", "Edge", "Application", "msedge.exe");
        return "/usr/bin/microsoft-edge";
    }
}
