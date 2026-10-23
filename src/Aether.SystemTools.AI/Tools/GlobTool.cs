using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Aether.SystemTools.AI.Tools;

public class GlobTool
{
    public class Output
    {
        public List<string> Files { get; set; } = new List<string>();
        public string? Error { get; set; }
    }

    [Description("使用通配符模式查找文件")]
    public async Task<Output> Glob([Description("要匹配的模式（如 *.cs, **/*.txt）")] string pattern, [Description("搜索路径")] string path = "", [Description("最大结果数量")] int? limit = null, CancellationToken cancellationToken = default)
    {
        try
        {
            if (string.IsNullOrEmpty(pattern))
            {
                return new Output { Error = "匹配模式不能为空" };
            }

            // path 为空不再回退到 Directory.GetCurrentDirectory()——桌面 app 从 .app 启动时 CWD=/，
            // 回退会递归扫整个 macOS 根（百万级文件 + 符号链接环 → 整机 IO 饥饿、CPU 爆炸、只能强制重启）。
            // 引导 agent 先调 GetCurrentProjectDirectory 取项目目录再传入。
            if (string.IsNullOrEmpty(path))
            {
                return new Output { Error = "搜索路径不能为空。请先调用 GetCurrentProjectDirectory 获取项目目录路径，再作为 path 传入。" };
            }

            var searchPath = Path.GetFullPath(path);

            if (FileSearchHelpers.IsProtectedRoot(searchPath))
            {
                return new Output { Error = $"拒绝扫描系统根目录: {searchPath}。请指定项目子目录。" };
            }

            if (!Directory.Exists(searchPath))
            {
                return new Output { Error = $"目录不存在: {searchPath}" };
            }

            var files = new List<string>();
            var maxResults = limit ?? 100;

            await Task.Run(() =>
            {
                // 匹配目标按模式是否含路径分隔符分流：含 `/`（**/*.cs、src/**/x.cs）按
                // 「相对搜索根的全路径」匹配；不含 `/`（*.cs）保持既有「递归每层按文件名」语义。
                // 旧实现只用 Path.GetFileName 对含 `/` 的正则匹配 → **/ 前的 `/` 永远匹不上 → 返 0。
                var normalized = pattern.Replace('\\', '/').TrimStart('/');
                var regex = BuildRegex(normalized);
                var matchFullPath = normalized.Contains('/');
                SearchFiles(searchPath, searchPath, regex, matchFullPath, files, maxResults, cancellationToken);
            }, cancellationToken);

            return new Output
            {
                Files = files
            };
        }
        catch (Exception ex)
        {
            return new Output { Error = $"查找文件失败: {ex.Message}" };
        }
    }

    private void SearchFiles(string searchRoot, string directory, Regex regex, bool matchFullPath, List<string> results, int maxResults, CancellationToken cancellationToken)
    {
        if (results.Count >= maxResults)
            return;

        try
        {
            var files = Directory.GetFiles(directory);

            foreach (var file in files)
            {
                if (cancellationToken.IsCancellationRequested)
                    return;

                if (results.Count >= maxResults)
                    return;

                var matchTarget = matchFullPath
                    ? Path.GetRelativePath(searchRoot, file).Replace('\\', '/')
                    : Path.GetFileName(file);
                if (regex.IsMatch(matchTarget))
                {
                    results.Add(file);
                }
            }

            var subdirectories = Directory.GetDirectories(directory);
            foreach (var subdir in subdirectories)
            {
                if (cancellationToken.IsCancellationRequested)
                    return;

                var dirName = Path.GetFileName(subdir);
                if (FileSearchHelpers.IsIgnoredDirectory(dirName))
                    continue;

                // 跳符号链接目录，防 macOS 环（/etc→/private/etc 等）导致递归不终止或重复放大。
                if (FileSearchHelpers.IsSymlinkDirectory(subdir))
                    continue;

                SearchFiles(searchRoot, subdir, regex, matchFullPath, results, maxResults, cancellationToken);
            }
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private Regex BuildRegex(string normalizedGlob)
    {
        // **/ = 零个或多个目录段 + 尾部斜杠（`**/*.cs` 命中根文件，`src/**/x.cs` 命中 src/x.cs）；
        // ** = 任意字符（跨段）；* = 单路径段内任意；? = 单路径段内单字符。
        // 段分隔符固定 /（传入时已把 \ 规整为 /，Windows 相对路径同样转 /）。
        // 替换顺序：先最长 `\*\*/`（含尾随斜杠），再孤立 `\*\*`，再单 `\*`，最后 `\?`。
        var escaped = Regex.Escape(normalizedGlob)
            .Replace("\\*\\*/", "(?:.*/)?")
            .Replace("\\*\\*", ".*")
            .Replace("\\*", "[^/]*")
            .Replace("\\?", "[^/]");

        return new Regex("^" + escaped + "$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    }
}
