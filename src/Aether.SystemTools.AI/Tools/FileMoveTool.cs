using System;
using System.ComponentModel;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Aether.SystemTools.AI.Tools;

/// <summary>
/// 文件移动/改名工具，供 AI Agent 重命名或移动本地文件。
/// 直接调用 <see cref="File.Move"/>（不走 shell），避免 ShellCommandTool 的引号转义、
/// stdin 阻塞等问题。同卷移动是原子的；跨卷移动会复制后删除源文件。
/// </summary>
public class FileMoveTool
{
    public class Output
    {
        public bool Success { get; set; }
        public string Result { get; set; } = string.Empty;
        public string? Error { get; set; }
    }

    [Description("移动或重命名文件（同卷为原子操作，跨卷为复制+删除源）。不能移动目录。")]
    public async Task<Output> MoveFile(
        [Description("源文件完整路径")] string sourcePath,
        [Description("目标文件完整路径")] string destinationPath,
        [Description("目标已存在时是否覆盖，默认 false")] bool overwrite = false,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (string.IsNullOrEmpty(sourcePath))
                return new Output { Error = "源文件路径不能为空" };
            if (string.IsNullOrEmpty(destinationPath))
                return new Output { Error = "目标文件路径不能为空" };

            var fullSource = Path.GetFullPath(sourcePath);
            var fullDest = Path.GetFullPath(destinationPath);

            if (!File.Exists(fullSource))
                return new Output { Error = $"源文件不存在: {sourcePath}" };
            // 不允许移动目录
            if (Directory.Exists(fullSource))
                return new Output { Error = "不支持移动目录，只能移动文件" };
            // 防误操作：源 == 目标
            if (string.Equals(fullSource, fullDest, StringComparison.OrdinalIgnoreCase))
                return new Output { Error = "源路径与目标路径相同，无需移动" };

            // 确保目标目录存在（File.Move 不会自动建目录）
            var destDir = Path.GetDirectoryName(fullDest);
            if (!string.IsNullOrEmpty(destDir) && !Directory.Exists(destDir))
                Directory.CreateDirectory(destDir);

            // File.Move 在 .NET 上是同步的（元数据操作，sub-ms ~ 几 ms），
            // 放到 Task.Run 里以便响应取消（跨卷大文件复制可能耗时）。
            await Task.Run(() => File.Move(fullSource, fullDest, overwrite), cancellationToken);

            return new Output
            {
                Success = true,
                Result = $"已移动文件: {sourcePath} → {destinationPath}"
            };
        }
        catch (OperationCanceledException)
        {
            // 取消时 File.Move 要么未开始（源仍在），要么跨卷复制中途（目标可能不完整）。
            // 不强行清理——返回取消状态，让 agent/用户决定。
            throw;
        }
        catch (Exception ex)
        {
            return new Output { Error = $"移动文件失败: {ex.Message}" };
        }
    }
}
