using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Aether.SystemTools.AI.Tools;

/// <summary>
/// 进程树终止助手。<see cref="Process.Kill(bool)"/>（<c>entireProcessTree:true</c>）在 macOS 上偶尔
/// 漏杀后代或抛异常（被调用方 <c>catch</c> 吞掉），导致 <c>dotnet publish</c> 等重型子树在取消/超时后
/// 继续吃满 CPU。本类先 <c>Kill(true)</c>，再在 Unix 下用 <c>pgrep -P</c> 递归枚举后代 +
/// <c>kill -9</c> 兜底，确保子树真正终止。
/// </summary>
internal static class ProcessKiller
{
    /// <summary>尽力杀掉整个进程树：Kill(true) →（Unix）pgrep 递归兜底 → Kill() 单进程兜底。</summary>
    public static void KillTree(Process? process)
    {
        if (process is null) return;
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch { /* 落到下面兜底 */ }

        if (!OperatingSystem.IsWindows())
        {
            try { KillDescendantsUnix(process.Id); }
            catch { /* 尽力，不阻断 */ }
        }

        try { if (!process.HasExited) process.Kill(); }
        catch { /* 进程可能已退出 */ }
    }

    /// <summary>Unix 下递归枚举 root 的全部后代（BFS，避免漏掉孙进程），先杀 root 停止再 spawn，再 kill -9 全部后代。</summary>
    [UnsupportedOSPlatform("windows")]
    private static void KillDescendantsUnix(int rootPid)
    {
        var all = new HashSet<int>();
        var queue = new Queue<int>();
        queue.Enqueue(rootPid);
        while (queue.Count > 0)
        {
            var cur = queue.Dequeue();
            foreach (var child in ListChildPidsUnix(cur))
            {
                if (child != rootPid && all.Add(child))
                    queue.Enqueue(child);
            }
        }
        // 先杀 root（停止它继续 spawn 子进程），再杀所有已枚举后代，尽量不留下孤儿。
        SendKillUnix(rootPid);
        foreach (var pid in all) SendKillUnix(pid);
    }

    /// <summary><c>pgrep -P &lt;pid&gt;</c> 列出直接子进程 pid（macOS/Linux）。</summary>
    [UnsupportedOSPlatform("windows")]
    private static List<int> ListChildPidsUnix(int parentPid)
    {
        var list = new List<int>();
        var pgrep = ResolveUtility("pgrep");
        if (pgrep is null) return list;

        var psi = new ProcessStartInfo
        {
            FileName = pgrep,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("-P");
        psi.ArgumentList.Add(parentPid.ToString());
        using var p = Process.Start(psi);
        if (p is null) return list;
        var stdout = p.StandardOutput.ReadToEnd();
        p.WaitForExit(2000);
        foreach (var tok in stdout.Split(new[] { '\n', '\r', ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries))
            if (int.TryParse(tok, out var pid) && pid > 0)
                list.Add(pid);
        return list;
    }

    /// <summary>P/Invoke libc kill 发 SIGKILL（比 spawn /bin/kill 轻量）。</summary>
    [UnsupportedOSPlatform("windows")]
    private static void SendKillUnix(int pid)
    {
        try { kill(pid, 9 /*SIGKILL*/); }
        catch { /* 权限/已退出 */ }
    }

    private static string? ResolveUtility(string name) => name switch
    {
        "pgrep" => File.Exists("/usr/bin/pgrep") ? "/usr/bin/pgrep"
                  : File.Exists("/bin/pgrep") ? "/bin/pgrep"
                  : null, // .app 受限 PATH 找不到 pgrep 就跳过兜底（Kill(true) 仍已尝试）
        _ => null,
    };

    [DllImport("libc")]
    [UnsupportedOSPlatform("windows")]
    private static extern int kill(int pid, int sig);
}
