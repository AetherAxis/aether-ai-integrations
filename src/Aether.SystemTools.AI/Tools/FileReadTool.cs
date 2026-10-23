using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Aether.SystemTools.AI.Tools;

public class FileReadTool
{
    public class Output
    {
        public string Content { get; set; } = string.Empty;
        public int NumLines { get; set; }
        public int TotalLines { get; set; }
        public string? Error { get; set; }
    }

    [Description("读取文件内容，支持文本文件、图片等")]
    public async Task<Output> ReadFile([Description("要读取的文件路径")] string filePath, [Description("从第几行开始读取（从1开始）")] int offset = 1, [Description("最多读取多少行")] int? limit = null, CancellationToken cancellationToken = default)
    {
        try
        {
            if (string.IsNullOrEmpty(filePath))
            {
                return new Output { Error = "文件路径不能为空" };
            }

            var fullPath = Path.GetFullPath(filePath);

            if (!File.Exists(fullPath))
            {
                return new Output { Error = $"文件不存在: {filePath}" };
            }

            var ext = Path.GetExtension(fullPath).ToLower();
            var imageExts = new HashSet<string> { ".png", ".jpg", ".jpeg", ".gif", ".webp", ".bmp" };
            if (imageExts.Contains(ext))
            {
                var imageBytes = await File.ReadAllBytesAsync(fullPath, cancellationToken);
                var base64 = Convert.ToBase64String(imageBytes);
                return new Output
                {
                    Content = $"[IMAGE:base64]{base64}[/IMAGE]",
                    NumLines = 1,
                    TotalLines = 1
                };
            }

            var content = await File.ReadAllTextAsync(fullPath, cancellationToken);
            var lines = content.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
            var totalLines = lines.Length;

            var startIndex = Math.Max(0, offset - 1);
            if (startIndex >= lines.Length)
            {
                return new Output
                {
                    Content = string.Empty,
                    NumLines = 0,
                    TotalLines = totalLines
                };
            }

            var endIndex = limit.HasValue ? Math.Min(startIndex + limit.Value, lines.Length) : lines.Length;
            var selectedLines = lines[startIndex..endIndex];
            var selectedContent = string.Join(Environment.NewLine, selectedLines);

            return new Output
            {
                Content = selectedContent,
                NumLines = selectedLines.Length,
                TotalLines = totalLines
            };
        }
        catch (Exception ex)
        {
            return new Output { Error = $"读取文件失败: {ex.Message}" };
        }
    }
}
