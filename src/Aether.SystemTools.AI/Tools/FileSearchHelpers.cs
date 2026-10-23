using System;
using System.Collections.Generic;
using System.IO;

namespace Aether.SystemTools.AI.Tools;

/// <summary>文件搜索共享助手：忽略构建产物/VCS/依赖目录（对标 ripgrep 默认尊 .gitignore + 跳 bin/obj），
/// 以及二进制文件检测（对标 <c>grep -I</c> / ripgrep 自动跳二进制）。
/// 供 <see cref="GrepTool"/> / <see cref="GlobTool"/> 共用，避免各自重复实现。</summary>
internal static class FileSearchHelpers
{
    /// <summary>搜索时跳过的目录名（构建产物 / 包管理 / VCS / IDE / 运行时输出）。
    /// 大幅减少无效文件数（bin/obj 常是源码的数倍），是搜索提速的首要手段。</summary>
    private static readonly HashSet<string> _ignoredDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        "bin", "obj", "node_modules", ".git", ".vs", ".idea", ".vscode",
        "packages", "TestResults", ".sass-cache", "dist", "build", "target",
        "__pycache__", ".venv", "venv", ".next", ".nuxt",
    };

    /// <summary>目录是否应被搜索跳过：隐藏目录（<c>.</c> 开头）或在忽略名单内。</summary>
    public static bool IsIgnoredDirectory(string dirName)
        => dirName.StartsWith(".") || _ignoredDirs.Contains(dirName);

    /// <summary>目录是否为符号链接/重解析点。递归时跳过它，防 macOS 符号链接环
    ///（<c>/etc</c>→<c>/private/etc</c>、<c>/var</c>→<c>/private/var</c>、<c>/System/Volumes/Data</c> 等）
    /// 导致递归不终止或同一物理子树被重复扫描指数放大。</summary>
    public static bool IsSymlinkDirectory(string fullPath)
    {
        try
        {
            return (File.GetAttributes(fullPath) & FileAttributes.ReparsePoint) != 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>受保护的系统根目录（顶级系统目录）。扫描它们会触发百万级文件 IO + 符号链接环，
    /// 拖垮整机——桌面 app 从 <c>.app</c> 启动时 CWD=<c>/</c>，若工具 path 为空回退到此即灾难。
    /// 工具入口应拒绝直接扫描这些路径。项目目录绝不会落在它们之下。</summary>
    private static readonly HashSet<string> _protectedRoots = new(StringComparer.OrdinalIgnoreCase)
    {
        "/", "/System", "/Library", "/usr", "/private", "/bin", "/sbin", "/etc", "/var", "/tmp", "/dev", "/opt", "/proc", "/sys",
        "C:\\", "C:\\Windows", "C:\\Program Files", "C:\\Program Files (x86)",
    };

    /// <summary>路径是否为受保护的系统根/系统盘根目录。工具应拒绝以此为搜索根。</summary>
    public static bool IsProtectedRoot(string fullPath)
    {
        if (string.IsNullOrEmpty(fullPath)) return false;
        try
        {
            var norm = Path.GetFullPath(fullPath)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (norm.Length == 0) return false;
            // 命中受保护名单（如 "/System"、"/usr"）
            foreach (var r in _protectedRoots)
            {
                var rn = r.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                if (string.Equals(norm, rn, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            // 系统盘根本身（Unix "/" 或 Windows "C:\"）——norm 已 trim 尾分隔符，"/" trim 后为空，
            // 单独处理：原始路径就是根。
            var root = Path.GetPathRoot(fullPath);
            if (!string.IsNullOrEmpty(root))
            {
                var rn = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                if (string.Equals(norm, rn, StringComparison.OrdinalIgnoreCase) && rn.Length <= 3)
                    return true; // "/" 或 "C:"
            }
            return false;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>二进制文件检测启发式（同 ripgrep/grep -I）：读前 8192 字节，含 NUL(\0) 视为二进制。
    /// 避免把 DLL/图片/压缩包当文本逐行匹配（慢 + 乱码噪音）。读取异常时保守返回 false（交由调用方尝试读）。</summary>
    public static bool IsBinary(string filePath)
    {
        const int sampleSize = 8192;
        try
        {
            using var fs = File.OpenRead(filePath);
            var buf = new byte[Math.Min(sampleSize, fs.Length)];
            int read = fs.Read(buf, 0, buf.Length);
            for (int i = 0; i < read; i++)
                if (buf[i] == 0) return true;
            return false;
        }
        catch
        {
            return false;
        }
    }
}
