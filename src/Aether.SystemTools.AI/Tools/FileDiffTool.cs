using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Aether.SystemTools.AI.Tools;

/// <summary>
/// 文件变更差异工具 — 比较两段内容或文件编辑前后的差异，生成统一格式 (unified) 的 diff 输出。
/// 用于 Agent 在编辑文件后快速查看变更摘要，或对比两段代码的差异。
/// </summary>
public class FileDiffTool
{
    [System.ComponentModel.Description("查看文件变更差异，生成统一格式的 diff 输出")]
    public async Task<Output> DiffFile(
        [System.ComponentModel.Description("文件路径")] string filePath,
        [System.ComponentModel.Description("原始内容（可选，不提供则与磁盘当前内容对比自身，此时需提供 modifiedContent）")] string? originalContent = null,
        [System.ComponentModel.Description("修改后内容（可选，不提供则用磁盘当前内容作为修改后版本）")] string? modifiedContent = null,
        [System.ComponentModel.Description("上下文行数（显示变更前后各N行），默认3")] int contextLines = 3,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            return new Output { Success = false, Error = "文件路径不能为空" };

        try
        {
            // 确定原始内容和修改后内容
            string[] originalLines;
            string[] modifiedLines;

            if (originalContent != null && modifiedContent != null)
            {
                // 两段内容直接对比
                originalLines = originalContent.Split('\n');
                modifiedLines = modifiedContent.Split('\n');
            }
            else if (originalContent != null)
            {
                // 原始内容 vs 磁盘当前内容
                originalLines = originalContent.Split('\n');
                var diskContent = await File.ReadAllTextAsync(filePath, cancellationToken);
                modifiedLines = diskContent.Split('\n');
            }
            else if (modifiedContent != null)
            {
                // 磁盘当前内容 vs 修改后内容
                if (!File.Exists(filePath))
                    return new Output { Success = false, Error = $"文件不存在: {filePath}" };
                var diskContent = await File.ReadAllTextAsync(filePath, cancellationToken);
                originalLines = diskContent.Split('\n');
                modifiedLines = modifiedContent.Split('\n');
            }
            else
            {
                // 无内容提供，无法对比
                return new Output { Success = false, Error = "必须提供 originalContent 和/或 modifiedContent" };
            }

            // 计算 diff
            var diff = ComputeDiff(originalLines, modifiedLines, contextLines);

            return new Output
            {
                Success = true,
                Diff = diff,
                AddedLines = modifiedLines.Length - originalLines.Length,
                ChangedRegions = CountChangedRegions(originalLines, modifiedLines),
            };
        }
        catch (Exception ex)
        {
            return new Output { Success = false, Error = $"计算差异失败: {ex.Message}" };
        }
    }

    /// <summary>
    /// 简易 LCS-based diff — 生成统一格式输出
    /// </summary>
    private static string ComputeDiff(string[] original, string[] modified, int context)
    {
        var sb = new StringBuilder();

        // 使用最长公共子序列计算编辑操作
        var lcs = ComputeLCS(original, modified);
        var ops = ComputeEditOps(original, modified, lcs);

        // 生成统一格式输出
        var i = 0;
        while (i < ops.Count)
        {
            if (ops[i].Type == EditType.Equal)
            {
                i++;
                continue;
            }

            // 找到变更区域的范围
            var changeStart = Math.Max(0, i - context);
            var changeEnd = i;
            while (changeEnd < ops.Count && ops[changeEnd].Type != EditType.Equal)
                changeEnd++;
            changeEnd = Math.Min(ops.Count, changeEnd + context);

            // 输出变更区域
            var origStart = ops[changeStart].OriginalLine;
            var origCount = 0;
            var modStart = ops[changeStart].ModifiedLine;
            var modCount = 0;

            for (var j = changeStart; j < changeEnd; j++)
            {
                if (ops[j].Type == EditType.Delete)
                    origCount++;
                else if (ops[j].Type == EditType.Insert)
                    modCount++;
                else
                {
                    origCount++;
                    modCount++;
                }
            }

            sb.AppendLine($"@@ -{origStart + 1},{origCount} +{modStart + 1},{modCount} @@");

            for (var j = changeStart; j < changeEnd; j++)
            {
                switch (ops[j].Type)
                {
                    case EditType.Equal:
                        sb.AppendLine($" {ops[j].Text}");
                        break;
                    case EditType.Delete:
                        sb.AppendLine($"-{ops[j].Text}");
                        break;
                    case EditType.Insert:
                        sb.AppendLine($"+{ops[j].Text}");
                        break;
                }
            }

            i = changeEnd;
        }

        return sb.ToString();
    }

    private static int CountChangedRegions(string[] original, string[] modified)
    {
        var lcs = ComputeLCS(original, modified);
        var ops = ComputeEditOps(original, modified, lcs);

        var regions = 0;
        var inChange = false;
        foreach (var op in ops)
        {
            if (op.Type != EditType.Equal)
            {
                if (!inChange) { regions++; inChange = true; }
            }
            else
            {
                inChange = false;
            }
        }
        return regions;
    }

    /// <summary>
    /// 最长公共子序列（简化实现，适合中小文件）
    /// </summary>
    private static List<(int OrigIdx, int ModIdx)> ComputeLCS(string[] a, string[] b)
    {
        var m = a.Length;
        var n = b.Length;

        // 对大文件使用简化策略：逐行匹配
        if (m * n > 10_000_000) // 超过 10M 格点时用贪心匹配
            return GreedyMatch(a, b);

        var dp = new int[m + 1, n + 1];
        for (var i = 1; i <= m; i++)
        {
            for (var j = 1; j <= n; j++)
            {
                dp[i, j] = a[i - 1] == b[j - 1]
                    ? dp[i - 1, j - 1] + 1
                    : Math.Max(dp[i - 1, j], dp[i, j - 1]);
            }
        }

        // 回溯
        var result = new List<(int, int)>();
        var ii = m;
        var jj = n;
        while (ii > 0 && jj > 0)
        {
            if (a[ii - 1] == b[jj - 1])
            {
                result.Add((ii - 1, jj - 1));
                ii--; jj--;
            }
            else if (dp[ii - 1, jj] > dp[ii, jj - 1])
                ii--;
            else
                jj--;
        }

        result.Reverse();
        return result;
    }

    private static List<(int OrigIdx, int ModIdx)> GreedyMatch(string[] a, string[] b)
    {
        var result = new List<(int, int)>();
        var j = 0;
        for (var i = 0; i < a.Length && j < b.Length; i++)
        {
            if (a[i] == b[j])
            {
                result.Add((i, j));
                j++;
            }
            else
            {
                // 在 b 中找下一个匹配
                var nextMatch = Array.IndexOf(b, a[i], j);
                if (nextMatch >= 0 && nextMatch - j < 10) // 只接受近距离匹配
                {
                    result.Add((i, nextMatch));
                    j = nextMatch + 1;
                }
            }
        }
        return result;
    }

    private static List<EditOp> ComputeEditOps(string[] original, string[] modified, List<(int OrigIdx, int ModIdx)> lcs)
    {
        var ops = new List<EditOp>();
        var oi = 0;
        var mi = 0;

        foreach (var (origIdx, modIdx) in lcs)
        {
            // 删除的行
            while (oi < origIdx)
            {
                ops.Add(new EditOp(EditType.Delete, original[oi], oi, mi));
                oi++;
            }
            // 插入的行
            while (mi < modIdx)
            {
                ops.Add(new EditOp(EditType.Insert, modified[mi], oi, mi));
                mi++;
            }
            // 相等的行
            ops.Add(new EditOp(EditType.Equal, original[origIdx], origIdx, modIdx));
            oi++;
            mi++;
        }

        // 剩余删除
        while (oi < original.Length)
        {
            ops.Add(new EditOp(EditType.Delete, original[oi], oi, mi));
            oi++;
        }
        // 剩余插入
        while (mi < modified.Length)
        {
            ops.Add(new EditOp(EditType.Insert, modified[mi], oi, mi));
            mi++;
        }

        return ops;
    }

    private enum EditType { Equal, Delete, Insert }

    private record EditOp(EditType Type, string Text, int OriginalLine, int ModifiedLine);

    public class Output
    {
        public bool Success { get; set; }
        public string Diff { get; set; } = string.Empty;
        public int AddedLines { get; set; }
        public int ChangedRegions { get; set; }
        public string? Error { get; set; }
    }
}
