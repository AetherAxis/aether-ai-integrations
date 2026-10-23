using System;
using System.ComponentModel;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Aether.SystemTools.AI.Tools;

public class FileWriteTool
{
    public class Output
    {
        public bool Success { get; set; }
        public bool IsNewFile { get; set; }
        public string? Error { get; set; }
    }

    [Description("写入文件内容，如果文件不存在则创建新文件")]
    public async Task<Output> WriteFile([Description("要写入的文件路径")] string filePath, [Description("要写入的内容")] string content, CancellationToken cancellationToken = default)
    {
        try
        {
            if (string.IsNullOrEmpty(filePath))
            {
                return new Output { Error = "文件路径不能为空" };
            }

            var fullPath = Path.GetFullPath(filePath);
            var directory = Path.GetDirectoryName(fullPath);
            
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var isNewFile = !File.Exists(fullPath);
            
            await File.WriteAllTextAsync(fullPath, content, cancellationToken);

            return new Output
            {
                Success = true,
                IsNewFile = isNewFile
            };
        }
        catch (Exception ex)
        {
            return new Output { Error = $"写入文件失败: {ex.Message}" };
        }
    }
}
