using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Aether.SystemTools.AI.Tools;

/// <summary>
/// 行号定位文件编辑工具 — 支持按行号插入、删除、替换代码段。
/// 与 FileEditTool 的区别：本工具按行号定位而非精确字符串匹配，
/// 适合 Agent 在已知行号的情况下精确修改代码（如"在第42行后插入方法"）。
/// </summary>
public class FileLineEditTool
{
    [System.ComponentModel.Description("按行号编辑文件：插入、删除、替换指定行")]
    public async Task<Output> LineEditFile(
        [System.ComponentModel.Description("文件路径")] string filePath,
        [System.ComponentModel.Description("操作类型: insert / delete / replace")] string operation,
        [System.ComponentModel.Description("起始行号（从1开始）")] int startLine,
        [System.ComponentModel.Description("结束行号（replace/delete时需要，insert时忽略）")] int? endLine = null,
        [System.ComponentModel.Description("要插入或替换的内容（insert/replace时需要）")] string? content = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            return new Output { Success = false, Error = "文件路径不能为空" };

        if (string.IsNullOrWhiteSpace(operation))
            return new Output { Success = false, Error = "操作类型不能为空" };

        var op = operation.ToLowerInvariant();
        if (op != "insert" && op != "delete" && op != "replace")
            return new Output { Success = false, Error = $"不支持的操作类型: {operation}，仅支持 insert/delete/replace" };

        if (!File.Exists(filePath))
            return new Output { Success = false, Error = $"文件不存在: {filePath}" };

        try
        {
            var lines = await File.ReadAllLinesAsync(filePath, cancellationToken);
            var totalLines = lines.Length;

            // 验证行号范围
            if (startLine < 1 || startLine > totalLines + 1)
                return new Output { Success = false, Error = $"起始行号 {startLine} 超出范围（文件共 {totalLines} 行，有效范围 1-{totalLines + 1}）" };

            var effectiveEndLine = endLine ?? startLine;

            if ((op == "delete" || op == "replace") && effectiveEndLine < startLine)
                return new Output { Success = false, Error = $"结束行号 {effectiveEndLine} 不能小于起始行号 {startLine}" };

            if ((op == "delete" || op == "replace") && effectiveEndLine > totalLines)
                return new Output { Success = false, Error = $"结束行号 {effectiveEndLine} 超出范围（文件共 {totalLines} 行）" };

            var newLines = new List<string>(lines);
            var linesAffected = 0;

            switch (op)
            {
                case "insert":
                    // 在 startLine 行之后插入（startLine=1 表示在第1行后插入，即在行2之前）
                    if (content == null)
                        return new Output { Success = false, Error = "insert 操作需要提供 content" };

                    var insertLines = content.Split('\n');
                    newLines.InsertRange(startLine, insertLines);
                    linesAffected = insertLines.Length;
                    break;

                case "delete":
                    // 删除 startLine 到 endLine 的行（1-based → 0-based: startLine-1 到 endLine-1）
                    var deleteCount = effectiveEndLine - startLine + 1;
                    newLines.RemoveRange(startLine - 1, deleteCount);
                    linesAffected = deleteCount;
                    break;

                case "replace":
                    if (content == null)
                        return new Output { Success = false, Error = "replace 操作需要提供 content" };

                    var replaceCount = effectiveEndLine - startLine + 1;
                    var replaceLines = content.Split('\n');
                    newLines.RemoveRange(startLine - 1, replaceCount);
                    newLines.InsertRange(startLine - 1, replaceLines);
                    linesAffected = replaceCount;
                    break;
            }

            await File.WriteAllLinesAsync(filePath, newLines, cancellationToken);

            return new Output
            {
                Success = true,
                LinesAffected = linesAffected,
                TotalLinesBefore = totalLines,
                TotalLinesAfter = newLines.Count,
            };
        }
        catch (Exception ex)
        {
            return new Output { Success = false, Error = $"编辑文件失败: {ex.Message}" };
        }
    }

    public class Output
    {
        public bool Success { get; set; }
        public int LinesAffected { get; set; }
        public int TotalLinesBefore { get; set; }
        public int TotalLinesAfter { get; set; }
        public string? Error { get; set; }
    }
}
