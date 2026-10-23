using System;
using System.ComponentModel;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Aether.SystemTools.AI.Tools;

public class FileEditTool
{
    public class Output
    {
        public bool Success { get; set; }
        public int ReplaceCount { get; set; }
        public string? Error { get; set; }
    }

    [Description("编辑文件内容，使用字符串替换方式")]
    public async Task<Output> EditFile([Description("要编辑的文件路径")] string filePath, [Description("要替换的旧字符串")] string oldString, [Description("新字符串")] string newString, [Description("是否替换所有匹配项")] bool replaceAll = false, CancellationToken cancellationToken = default)
    {
        try
        {
            if (string.IsNullOrEmpty(filePath))
            {
                return new Output { Error = "文件路径不能为空" };
            }

            if (string.IsNullOrEmpty(oldString))
            {
                return new Output { Error = "旧字符串不能为空" };
            }

            if (oldString == newString)
            {
                return new Output { Error = "新旧字符串相同，无需修改" };
            }

            var fullPath = Path.GetFullPath(filePath);

            if (!File.Exists(fullPath))
            {
                return new Output { Error = $"文件不存在: {filePath}" };
            }

            var content = await File.ReadAllTextAsync(fullPath, cancellationToken);

            var count = 0;
            var newContent = content;

            if (replaceAll)
            {
                var index = 0;
                while ((index = newContent.IndexOf(oldString, index, StringComparison.Ordinal)) != -1)
                {
                    count++;
                    newContent = newContent.Substring(0, index) + newString + newContent.Substring(index + oldString.Length);
                    index += newString.Length;
                }
            }
            else
            {
                var index = newContent.IndexOf(oldString, StringComparison.Ordinal);
                if (index != -1)
                {
                    count = 1;
                    newContent = newContent.Substring(0, index) + newString + newContent.Substring(index + oldString.Length);
                }
            }

            if (count == 0)
            {
                return new Output { Error = $"在文件中未找到匹配的字符串" };
            }

            await File.WriteAllTextAsync(fullPath, newContent, cancellationToken);

            return new Output
            {
                Success = true,
                ReplaceCount = count
            };
        }
        catch (Exception ex)
        {
            return new Output { Error = $"编辑文件失败: {ex.Message}" };
        }
    }
}
