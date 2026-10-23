using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Aether.SystemTools.AI.Tools;

public class DirectoryExplorerTool
{
    public class Output
    {
        public List<DirectoryItem> Items { get; set; } = new();
        public string? Error { get; set; }
        public bool? Truncated { get; set; }
    }

    public class DirectoryItem
    {
        public string Name { get; set; } = string.Empty;
        public string FullPath { get; set; } = string.Empty;
        public bool IsDirectory { get; set; }
        public bool IsHidden { get; set; }
        public long? Size { get; set; }
        public DateTime? LastModified { get; set; }
        public List<DirectoryItem>? Children { get; set; }
    }

    // 递归深度硬上限（防 agent 传巨大 recursionDepth 扫爆）+ 总条目硬上限（防大目录返回数万项）。
    private const int MaxRecursionDepth = 5;
    private const int MaxItems = 1000;

    /// <summary>跨递归层共享的条目计数器，保证整棵树不超过 <see cref="MaxItems"/>。</summary>
    private class Counter
    {
        public int Value;
    }

    [Description("浏览目录结构")]
    public async Task<Output> ExploreDirectory([Description("要浏览的目录路径（项目目录绝对路径）")] string path, [Description("递归深度（0 = 当前目录，上限 5）")] int recursionDepth = 0, [Description("是否包含隐藏文件")] bool includeHidden = false, [Description("是否仅显示目录")] bool directoriesOnly = false, CancellationToken cancellationToken = default)
    {
        try
        {
            if (string.IsNullOrEmpty(path))
            {
                return new Output { Error = "目录路径不能为空" };
            }

            var fullPath = Path.GetFullPath(path);

            if (FileSearchHelpers.IsProtectedRoot(fullPath))
            {
                return new Output { Error = $"拒绝扫描系统根目录: {fullPath}。请指定项目子目录。" };
            }

            if (!Directory.Exists(fullPath))
            {
                return new Output { Error = $"目录不存在: {path}" };
            }

            var depth = Math.Min(Math.Max(recursionDepth, 0), MaxRecursionDepth);
            var counter = new Counter();
            var items = await Task.Run(() =>
            {
                return GetDirectoryItems(fullPath, depth, includeHidden, directoriesOnly, counter, cancellationToken);
            }, cancellationToken);

            return new Output
            {
                Items = items,
                Truncated = counter.Value >= MaxItems
            };
        }
        catch (Exception ex)
        {
            return new Output { Error = $"浏览目录失败: {ex.Message}" };
        }
    }

    private List<DirectoryItem> GetDirectoryItems(string directoryPath, int remainingDepth, bool includeHidden, bool directoriesOnly, Counter counter, CancellationToken cancellationToken)
    {
        var result = new List<DirectoryItem>();

        try
        {
            if (cancellationToken.IsCancellationRequested)
                return result;

            var dirInfo = new DirectoryInfo(directoryPath);

            // 获取子目录
            var directories = dirInfo.GetDirectories();
            foreach (var dir in directories)
            {
                if (cancellationToken.IsCancellationRequested)
                    break;
                if (counter.Value >= MaxItems)
                    break;

                if (!includeHidden && (dir.Attributes & FileAttributes.Hidden) != 0)
                    continue;

                // 跳构建产物/VCS/依赖缓存（bin/obj/node_modules/.git 等），对标 Glob/Grep。
                if (FileSearchHelpers.IsIgnoredDirectory(dir.Name))
                    continue;

                // 跳符号链接目录，防环/防重复扫描同一物理子树。
                if (FileSearchHelpers.IsSymlinkDirectory(dir.FullName))
                    continue;

                var item = new DirectoryItem
                {
                    Name = dir.Name,
                    FullPath = dir.FullName,
                    IsDirectory = true,
                    IsHidden = (dir.Attributes & FileAttributes.Hidden) != 0,
                    LastModified = dir.LastWriteTime
                };
                counter.Value++;

                if (remainingDepth > 0)
                {
                    item.Children = GetDirectoryItems(dir.FullName, remainingDepth - 1, includeHidden, directoriesOnly, counter, cancellationToken);
                }

                result.Add(item);
            }

            if (!directoriesOnly)
            {
                // 获取文件
                var files = dirInfo.GetFiles();
                foreach (var file in files)
                {
                    if (cancellationToken.IsCancellationRequested)
                        break;
                    if (counter.Value >= MaxItems)
                        break;

                    if (!includeHidden && (file.Attributes & FileAttributes.Hidden) != 0)
                        continue;

                    counter.Value++;

                    result.Add(new DirectoryItem
                    {
                        Name = file.Name,
                        FullPath = file.FullName,
                        IsDirectory = false,
                        IsHidden = (file.Attributes & FileAttributes.Hidden) != 0,
                        Size = file.Length,
                        LastModified = file.LastWriteTime
                    });
                }
            }

            return result.OrderBy(x => !x.IsDirectory).ThenBy(x => x.Name).ToList();
        }
        catch (Exception)
        {
            return result;
        }
    }
}
