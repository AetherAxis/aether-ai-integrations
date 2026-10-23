using System;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Aether.SystemTools.AI.Tools;

/// <summary>
/// 文件删除工具，供 AI Agent 删除本地文件（如查询结果 CSV）。
/// 支持通过 allowedDirectories 限制可删除的目录范围，确保安全性。
/// </summary>
public class FileDeleteTool
{
    public class Output
    {
        public bool Success { get; set; }
        public string Result { get; set; } = string.Empty;
        public string? Error { get; set; }
    }

    private readonly string[]? _allowedDirectories;

    /// <summary>
    /// 创建 FileDeleteTool 实例。
    /// </summary>
    /// <param name="allowedDirectories">允许删除文件的目录白名单（完整路径）。为 null 时不限制目录。</param>
    public FileDeleteTool(string[]? allowedDirectories = null)
    {
        _allowedDirectories = allowedDirectories?
            .Select(d => Path.GetFullPath(d).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
            .ToArray();
    }

    [Description("删除指定的本地文件（如不再需要的查询结果CSV文件）。只能删除文件，不能删除目录。")]
    public Task<Output> DeleteFile(
        [Description("要删除的文件完整路径")] string filePath,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (string.IsNullOrEmpty(filePath))
            {
                return Task.FromResult(new Output { Success = false, Error = "文件路径不能为空" });
            }

            var fullPath = Path.GetFullPath(filePath);

            // 安全检查：路径必须在允许的目录范围内
            if (_allowedDirectories != null && _allowedDirectories.Length > 0)
            {
                var normalizedPath = fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                var isAllowed = _allowedDirectories.Any(allowedDir =>
                    normalizedPath.StartsWith(allowedDir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                    || normalizedPath.Equals(allowedDir, StringComparison.OrdinalIgnoreCase));

                if (!isAllowed)
                {
                    return Task.FromResult(new Output
                    {
                        Success = false,
                        Error = $"安全限制：只能删除以下目录中的文件：{string.Join("、", _allowedDirectories)}"
                    });
                }
            }

            if (!File.Exists(fullPath))
            {
                return Task.FromResult(new Output { Success = false, Error = $"文件不存在: {filePath}" });
            }

            // 不允许删除目录
            if (Directory.Exists(fullPath))
            {
                return Task.FromResult(new Output { Success = false, Error = "不支持删除目录，只能删除文件" });
            }

            var fileSize = new FileInfo(fullPath).Length;
            File.Delete(fullPath);

            return Task.FromResult(new Output
            {
                Success = true,
                Result = $"已删除文件: {filePath}（大小: {FormatFileSize(fileSize)}）"
            });
        }
        catch (UnauthorizedAccessException ex)
        {
            return Task.FromResult(new Output { Success = false, Error = $"无权限删除文件: {ex.Message}" });
        }
        catch (Exception ex)
        {
            return Task.FromResult(new Output { Success = false, Error = $"删除文件失败: {ex.Message}" });
        }
    }

    private static string FormatFileSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
        return $"{bytes / (1024.0 * 1024.0):F1} MB";
    }
}
