using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Linq;

namespace Aether.SystemTools.AI.Tools;

public class GrepTool
{
    public enum OutputMode
    {
        [Description("输出匹配的内容和行号")]
        Content,
        [Description("输出匹配的文件名")]
        FilesWithMatches,
        [Description("输出匹配的行数")]
        Count
    }

    public class Output
    {
        public OutputMode Mode { get; set; }
        public List<string> Files { get; set; } = new();
        public string Content { get; set; } = string.Empty;
        public int? NumMatches { get; set; }
        public string? Error { get; set; }
        public bool? Truncated { get; set; }
    }

    private class SearchResult
    {
        public string FilePath { get; set; } = string.Empty;
        public int MatchCount { get; set; }
        public List<LineMatch> LineMatches { get; set; } = new();
    }

    private class LineMatch
    {
        public int LineNumber { get; set; }
        public string Line { get; set; } = string.Empty;
    }

    // —— 硬上限（防失控）：path 误指大目录/大文件时不至于扫爆整机 IO+CPU+内存 ——
    private const int DefaultMaxFiles = 200;        // 最多匹配的文件数
    private const int MaxTotalMatches = 2000;       // 最多累积的匹配行数（Content 模式输出上限）
    private const int MaxMatchesPerFile = 200;      // 单文件最多保留的匹配行（防单文件刷屏）
    private const int MaxOutputLines = 2000;        // Content/Count 文本输出行数上限

    [Description("在文件中搜索匹配的内容，支持正则表达式")]
    public async Task<Output> Grep([Description("要搜索的模式（正则表达式）")] string pattern, [Description("搜索路径（项目目录绝对路径；为空将报错，请先调用 GetCurrentProjectDirectory 获取）")] string path, [Description("文件扩展名过滤（如 *.cs, *.py）")] string? glob = null, [Description("输出模式")] OutputMode? outputMode = null, [Description("是否忽略大小写")] bool? caseInsensitive = null, [Description("最大结果文件数量")] int? limit = null, CancellationToken cancellationToken = default)
    {
        try
        {
            if (string.IsNullOrEmpty(pattern))
            {
                return new Output { Error = "搜索模式不能为空" };
            }

            // path 为空不再回退到 Directory.GetCurrentDirectory()——桌面 app 从 .app 启动时 CWD=/，
            // 回退会递归扫整个 macOS 根（百万级文件逐行 regex + 符号链接环 → 整机 IO 饥饿、CPU 爆炸）。
            if (string.IsNullOrEmpty(path))
            {
                return new Output { Error = "搜索路径不能为空。请先调用 GetCurrentProjectDirectory 获取项目目录路径，再作为 path 传入。" };
            }

            var searchPath = Path.GetFullPath(path);

            if (FileSearchHelpers.IsProtectedRoot(searchPath))
            {
                return new Output { Error = $"拒绝扫描系统根目录: {searchPath}。请指定项目子目录。" };
            }

            if (!Directory.Exists(searchPath) && !File.Exists(searchPath))
            {
                return new Output { Error = $"路径不存在: {searchPath}" };
            }

            var mode = outputMode ?? OutputMode.FilesWithMatches;
            var results = new List<SearchResult>();

            var regexOptions = RegexOptions.Compiled;
            if (caseInsensitive ?? false)
            {
                regexOptions |= RegexOptions.IgnoreCase;
            }

            var regex = new Regex(pattern, regexOptions);

            Func<string, bool> fileFilter = f => true;
            if (!string.IsNullOrEmpty(glob))
            {
                var globPattern = "^" + Regex.Escape(glob).Replace("\\*", ".*").Replace("\\?", ".") + "$";
                var globRegex = new Regex(globPattern, RegexOptions.IgnoreCase);
                fileFilter = f => globRegex.IsMatch(Path.GetFileName(f));
            }

            var maxFiles = limit is > 0 ? limit.Value : DefaultMaxFiles;

            // 整个目录遍历是同步 Directory.GetFiles/GetDirectories，包 Task.Run 离开调用线程（避免 MAF 工具
            // 分派线程被长遍历占用）。文件读取用同步 File.ReadLines（在 Task.Run 内不阻塞 UI）。
            if (File.Exists(searchPath))
            {
                await Task.Run(() => SearchFile(searchPath, regex, results, maxFiles, MaxMatchesPerFile, cancellationToken), cancellationToken);
            }
            else
            {
                await Task.Run(() => SearchDirectory(searchPath, regex, results, fileFilter, maxFiles, MaxMatchesPerFile, cancellationToken), cancellationToken);
            }

            if (mode == OutputMode.FilesWithMatches && results.Count > maxFiles)
            {
                results = results.Take(maxFiles).ToList();
            }

            var output = new Output
            {
                Mode = mode,
                Files = results.Select(r => r.FilePath).ToList()
            };

            var truncated = false;

            switch (mode)
            {
                case OutputMode.Content:
                    var contentBuilder = new System.Text.StringBuilder();
                    var totalLines = 0;
                    var totalMatches = 0;
                    foreach (var result in results)
                    {
                        foreach (var match in result.LineMatches)
                        {
                            if (totalLines >= MaxOutputLines) { truncated = true; break; }
                            contentBuilder.AppendLine($"{result.FilePath}:{match.LineNumber}:{match.Line}");
                            totalLines++;
                        }
                        totalMatches += result.MatchCount;
                        if (truncated) break;
                    }
                    output.Content = contentBuilder.ToString();
                    output.NumMatches = totalMatches;
                    if (totalMatches >= MaxTotalMatches) truncated = true;
                    output.Truncated = truncated;
                    break;

                case OutputMode.Count:
                    var countBuilder = new System.Text.StringBuilder();
                    var totalMatchCount = 0;
                    var countLines = 0;
                    foreach (var result in results)
                    {
                        if (countLines >= MaxOutputLines) { truncated = true; break; }
                        countBuilder.AppendLine($"{result.FilePath}:{result.MatchCount}");
                        totalMatchCount += result.MatchCount;
                        countLines++;
                    }
                    output.Content = countBuilder.ToString();
                    output.NumMatches = totalMatchCount;
                    if (totalMatchCount >= MaxTotalMatches) truncated = true;
                    output.Truncated = truncated;
                    break;
            }

            return output;
        }
        catch (Exception ex)
        {
            return new Output { Error = $"搜索失败: {ex.Message}" };
        }
    }

    private void SearchDirectory(string directory, Regex regex, List<SearchResult> results, Func<string, bool> fileFilter, int maxFiles, int maxMatchesPerFile, CancellationToken cancellationToken)
    {
        if (results.Count >= maxFiles)
            return;
        try
        {
            var files = Directory.GetFiles(directory);
            foreach (var file in files)
            {
                if (cancellationToken.IsCancellationRequested)
                    return;
                if (results.Count >= maxFiles)
                    return;

                if (!fileFilter(file))
                    continue;

                SearchFile(file, regex, results, maxFiles, maxMatchesPerFile, cancellationToken);
            }

            var subdirectories = Directory.GetDirectories(directory);
            foreach (var subdir in subdirectories)
            {
                if (cancellationToken.IsCancellationRequested)
                    return;
                if (results.Count >= maxFiles)
                    return;

                var dirName = Path.GetFileName(subdir);
                if (FileSearchHelpers.IsIgnoredDirectory(dirName))
                    continue;

                // 跳符号链接目录，防 macOS 环（/etc→/private/etc、/var→/private/var 等）导致递归不终止或重复放大。
                if (FileSearchHelpers.IsSymlinkDirectory(subdir))
                    continue;

                SearchDirectory(subdir, regex, results, fileFilter, maxFiles, maxMatchesPerFile, cancellationToken);
            }
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private void SearchFile(string filePath, Regex regex, List<SearchResult> results, int maxFiles, int maxMatchesPerFile, CancellationToken cancellationToken)
    {
        try
        {
            // 跳二进制文件（DLL/图片/压缩包等），避免当文本逐行匹配的噪音与开销（对标 grep -I / ripgrep）
            if (FileSearchHelpers.IsBinary(filePath)) return;

            var result = new SearchResult { FilePath = filePath };
            var matchCount = 0;
            int lineNo = 0;

            // 同步逐行读（在 Task.Run 内，不阻塞 UI）。单文件匹配行数达上限即停（防巨文件刷屏）。
            foreach (var line in File.ReadLines(filePath))
            {
                lineNo++;
                if (cancellationToken.IsCancellationRequested)
                    return;

                var matches = regex.Matches(line);
                if (matches.Count > 0)
                {
                    matchCount += matches.Count;
                    if (result.LineMatches.Count < maxMatchesPerFile)
                        result.LineMatches.Add(new LineMatch { LineNumber = lineNo, Line = line });
                    if (matchCount >= maxMatchesPerFile)
                        break;
                }
            }

            if (matchCount > 0)
            {
                result.MatchCount = matchCount;
                results.Add(result);
            }
        }
        catch (Exception)
        {
        }
    }
}
