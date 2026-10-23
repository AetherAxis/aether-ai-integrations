using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Aether.SystemTools.AI.Tools;

/// <summary>
/// 多文件批量编辑工具 — 一次调用完成多个文件的创建/编辑/行编辑/删除。
/// 与单文件工具（WriteFile/EditFile/LineEditFile/DeleteFile）的区别：本工具接收 patch 数组，
/// 顺序执行；任一失败时尽力回滚已执行的操作（逆序恢复到执行前状态），返回每个操作的明细。
/// 适合 agent 需要原子性多文件改动的场景（如重命名一个符号涉及多处文件 + 更新引用）。
/// </summary>
/// <remarks>
/// 原子性语义（用户决策：尽力回滚）：
/// - 每个 patch 顺序执行；执行成功前先记快照（已有文件读 old，新建文件记"原不存在"）。
/// - 任一 patch 失败 → 逆序回滚已成功的 patch（写回 old / 删除新建文件），返回失败明细。
/// - 回滚本身也可能失败（文件锁定/权限）——回滚失败不中断后续回滚，在 RollbackErrors 里上报。
/// 审批级（用户决策：对齐现有分级）：
/// - patch 含 delete 操作 → 整个 ApplyPatch 视为 Dangerous。
/// - 纯 create/edit/lineEdit → Safe。
/// 分级在 <c>SystemToolsAIBuilderExtensions</c> 注册时按 patch 内容动态判定不便实现（注册时不知道内容），
/// 故注册两个入口：ApplyPatch（Safe，禁止 delete）/ ApplyPatchWithDelete（Dangerous，允许 delete）。
/// </remarks>
public class ApplyPatchTool
{
    /// <summary>单个 patch 条目。</summary>
    public class PatchEntry
    {
        [Description("文件路径")]
        public string FilePath { get; set; } = string.Empty;

        [Description("操作类型: create / edit / lineEdit / delete")]
        public string Operation { get; set; } = string.Empty;

        [Description("edit 操作：要替换的旧字符串（整个文件内首次匹配，非全部）")]
        public string? OldString { get; set; }

        [Description("edit 操作：替换成的新字符串")]
        public string? NewString { get; set; }

        [Description("edit 操作：是否替换所有匹配项，默认 false")]
        public bool? ReplaceAll { get; set; }

        [Description("create 操作：要写入的完整内容")]
        public string? Content { get; set; }

        [Description("lineEdit 操作：insert/delete/replace")]
        public string? LineOperation { get; set; }

        [Description("lineEdit 操作：起始行号（从1开始）")]
        public int? StartLine { get; set; }

        [Description("lineEdit 操作：结束行号（replace/delete 时需要）")]
        public int? EndLine { get; set; }

        [Description("lineEdit 操作：要插入或替换的内容（insert/replace 时需要）")]
        public string? LineContent { get; set; }
    }

    public class PatchResult
    {
        public string FilePath { get; set; } = string.Empty;
        public string Operation { get; set; } = string.Empty;
        public bool Success { get; set; }
        public string? Error { get; set; }
        /// <summary>edit 替换次数 / lineEdit 影响行数 / create 是否新建。</summary>
        public int? Count { get; set; }
    }

    public class Output
    {
        public bool Success { get; set; }
        public int Total { get; set; }
        public int Succeeded { get; set; }
        public int Failed { get; set; }
        public List<PatchResult> Results { get; set; } = new();
        /// <summary>若有 patch 失败，已执行的 patch 是否全部回滚成功。</summary>
        public bool RollbackComplete { get; set; }
        public List<string> RollbackErrors { get; set; } = new();
        public string? Error { get; set; }
    }

    /// <summary>执行多文件批量 patch（不含 delete 操作）。任一失败尽力回滚。</summary>
    [Description("批量编辑多个文件（一次调用完成多个 create/edit/lineEdit）。不含删除操作——需要删除用 ApplyPatchWithDelete。任一 patch 失败时尽力回滚已执行的操作。比逐个调 WriteFile/EditFile/LineEditFile 更高效，适合需要原子性多文件改动的场景（如重命名一个符号涉及多处文件 + 更新引用）。")]
    public Task<Output> ApplyPatch(
        [Description("patch 条目数组")] PatchEntry[] patches,
        CancellationToken cancellationToken = default)
        => ApplyPatchCore(patches, allowDelete: false, cancellationToken);

    /// <summary>执行多文件批量 patch（含 delete 操作，Dangerous 级）。</summary>
    [Description("批量编辑多个文件，允许 delete 操作（危险，会触发审批）。其余同 ApplyPatch。")]
    public Task<Output> ApplyPatchWithDelete(
        [Description("patch 条目数组（可含 delete）")] PatchEntry[] patches,
        CancellationToken cancellationToken = default)
        => ApplyPatchCore(patches, allowDelete: true, cancellationToken);

    private async Task<Output> ApplyPatchCore(PatchEntry[] patches, bool allowDelete, CancellationToken ct)
    {
        if (patches is null || patches.Length == 0)
            return new Output { Success = false, Error = "patches 不能为空" };
        if (patches.Length > 100)
            return new Output { Success = false, Error = "单次最多 100 个 patch（防止误操作大批量改动）" };

        var results = new List<PatchResult>();
        // 已执行 patch 的回滚信息（逆序回滚）：path → (existedBefore, contentBefore)
        var rollback = new Stack<(string path, bool existed, string? content)>();
        bool rollbackComplete = true;
        var rollbackErrors = new List<string>();

        for (int i = 0; i < patches.Length; i++)
        {
            var p = patches[i];
            var r = new PatchResult { FilePath = p.FilePath, Operation = p.Operation };

            // 参数校验
            var op = (p.Operation ?? string.Empty).Trim().ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(p.FilePath))
            {
                r.Success = false;
                r.Error = "FilePath 不能为空";
                results.Add(r);
                return FailWithRollback(results, rollback, ref rollbackComplete, rollbackErrors);
            }
            if (op is not ("create" or "edit" or "lineedit" or "delete"))
            {
                r.Success = false;
                r.Error = $"不支持的操作: {p.Operation}（仅 create/edit/lineEdit/delete）";
                results.Add(r);
                return FailWithRollback(results, rollback, ref rollbackComplete, rollbackErrors);
            }
            if (op == "delete" && !allowDelete)
            {
                r.Success = false;
                r.Error = "本入口（ApplyPatch）不允许 delete 操作，请改用 ApplyPatchWithDelete";
                results.Add(r);
                return FailWithRollback(results, rollback, ref rollbackComplete, rollbackErrors);
            }

            string fullPath;
            try { fullPath = Path.GetFullPath(p.FilePath); }
            catch (Exception ex)
            {
                r.Success = false;
                r.Error = $"路径非法: {ex.Message}";
                results.Add(r);
                return FailWithRollback(results, rollback, ref rollbackComplete, rollbackErrors);
            }

            // 记快照（用于回滚 + 事后 diff）
            bool existedBefore = File.Exists(fullPath);
            string? contentBefore = existedBefore ? TryReadAllText(fullPath) : null;

            try
            {
                int? count = op switch
                {
                    "create" => await DoCreate(fullPath, p, ct),
                    "edit" => await DoEdit(fullPath, p, ct),
                    "lineedit" => await DoLineEdit(fullPath, p, ct),
                    "delete" => DoDelete(fullPath),
                    _ => null,
                };
                r.Success = true;
                r.Count = count;
                rollback.Push((fullPath, existedBefore, contentBefore));
            }
            catch (PatchException ex)
            {
                r.Success = false;
                r.Error = ex.Message;
                results.Add(r);
                return FailWithRollback(results, rollback, ref rollbackComplete, rollbackErrors);
            }
            catch (Exception ex)
            {
                r.Success = false;
                r.Error = $"{op} 失败: {ex.Message}";
                results.Add(r);
                return FailWithRollback(results, rollback, ref rollbackComplete, rollbackErrors);
            }

            results.Add(r);
        }

        int succeeded = results.Count(x => x.Success);
        return new Output
        {
            Success = succeeded == results.Count,
            Total = results.Count,
            Succeeded = succeeded,
            Failed = results.Count - succeeded,
            Results = results,
            RollbackComplete = true,
            RollbackErrors = new(),
        };
    }

    // —— 单操作实现（失败抛 PatchException，参数问题；其它异常透传给上层）——

    private static async Task<int> DoCreate(string fullPath, PatchEntry p, CancellationToken ct)
    {
        if (p.Content is null)
            throw new PatchException("create 操作需要 Content");
        var dir = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(fullPath, p.Content, ct);
        return 1;
    }

    private static async Task<int> DoEdit(string fullPath, PatchEntry p, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(p.OldString))
            throw new PatchException("edit 操作需要 OldString");
        if (p.OldString == p.NewString)
            throw new PatchException("OldString 与 NewString 相同，无需修改");
        if (!File.Exists(fullPath))
            throw new PatchException($"文件不存在: {fullPath}");

        var content = await File.ReadAllTextAsync(fullPath, ct);
        var replaceAll = p.ReplaceAll ?? false;
        int count;
        string newContent;
        if (replaceAll)
        {
            count = CountOccurrences(content, p.OldString);
            if (count == 0) throw new PatchException("文件中未找到匹配的字符串");
            newContent = content.Replace(p.OldString, p.NewString ?? string.Empty);
        }
        else
        {
            int idx = content.IndexOf(p.OldString, StringComparison.Ordinal);
            if (idx < 0) throw new PatchException("文件中未找到匹配的字符串");
            count = 1;
            newContent = content.Substring(0, idx) + (p.NewString ?? string.Empty) + content.Substring(idx + p.OldString.Length);
        }
        await File.WriteAllTextAsync(fullPath, newContent, ct);
        return count;
    }

    private static async Task<int> DoLineEdit(string fullPath, PatchEntry p, CancellationToken ct)
    {
        if (p.StartLine is null || p.StartLine < 1)
            throw new PatchException("lineEdit 操作需要有效的 StartLine（≥1）");
        var lineOp = (p.LineOperation ?? string.Empty).Trim().ToLowerInvariant();
        if (lineOp is not ("insert" or "delete" or "replace"))
            throw new PatchException($"lineEdit 的 LineOperation 不支持: {p.LineOperation}（仅 insert/delete/replace）");
        if (!File.Exists(fullPath))
            throw new PatchException($"文件不存在: {fullPath}");

        var lines = await File.ReadAllLinesAsync(fullPath, ct);
        int total = lines.Length;
        int start = p.StartLine.Value;
        int end = p.EndLine ?? start;

        if (start > total + 1)
            throw new PatchException($"起始行号 {start} 超出范围（文件共 {total} 行，有效范围 1-{total + 1}）");
        if ((lineOp == "delete" || lineOp == "replace") && end < start)
            throw new PatchException($"结束行号 {end} 不能小于起始行号 {start}");
        if ((lineOp == "delete" || lineOp == "replace") && end > total)
            throw new PatchException($"结束行号 {end} 超出范围（文件共 {total} 行）");
        if ((lineOp == "insert" || lineOp == "replace") && p.LineContent is null)
            throw new PatchException($"{lineOp} 操作需要 LineContent");

        var newLines = new List<string>(lines);
        int affected;
        switch (lineOp)
        {
            case "insert":
                // StartLine 是 1-based（同 delete/replace 的 `start - 1`），插入到该行**之前**。
                // start-1 == total（StartLine = 总行数+1）→ 追加到文件末尾，List.InsertRange 允许 index == Count。
                var ins = p.LineContent!.Split('\n');
                newLines.InsertRange(start - 1, ins);
                affected = ins.Length;
                break;
            case "delete":
                affected = end - start + 1;
                newLines.RemoveRange(start - 1, affected);
                break;
            default: // replace
                affected = end - start + 1;
                var rep = p.LineContent!.Split('\n');
                newLines.RemoveRange(start - 1, affected);
                newLines.InsertRange(start - 1, rep);
                affected = rep.Length;
                break;
        }
        await File.WriteAllLinesAsync(fullPath, newLines, ct);
        return affected;
    }

    private static int DoDelete(string fullPath)
    {
        if (!File.Exists(fullPath))
            throw new PatchException($"文件不存在: {fullPath}");
        if (Directory.Exists(fullPath))
            throw new PatchException("不支持删除目录，只能删除文件");
        File.Delete(fullPath);
        return 1;
    }

    // —— 回滚（尽力，失败不中断后续回滚）——

    private static Output FailWithRollback(
        List<PatchResult> results,
        Stack<(string path, bool existed, string? content)> rollback,
        ref bool rollbackComplete,
        List<string> rollbackErrors)
    {
        while (rollback.Count > 0)
        {
            var (path, existed, content) = rollback.Pop();
            try
            {
                if (existed)
                    File.WriteAllText(path, content ?? string.Empty);
                else if (File.Exists(path))
                    File.Delete(path);
            }
            catch (Exception ex)
            {
                rollbackComplete = false;
                rollbackErrors.Add($"{path}: {ex.Message}");
            }
        }
        int succeeded = results.Count(x => x.Success);
        return new Output
        {
            Success = false,
            Total = results.Count,
            Succeeded = succeeded,
            Failed = results.Count - succeeded,
            Results = results,
            RollbackComplete = rollbackComplete,
            RollbackErrors = rollbackErrors,
            Error = "部分 patch 失败，已执行的操作已" + (rollbackComplete ? "全部回滚" : "尽力回滚（部分回滚失败见 RollbackErrors）"),
        };
    }

    // —— helpers ——

    private static int CountOccurrences(string haystack, string needle)
    {
        int count = 0, i = 0;
        while ((i = haystack.IndexOf(needle, i, StringComparison.Ordinal)) != -1)
        {
            count++;
            i += needle.Length;
        }
        return count;
    }

    private static string? TryReadAllText(string path)
    {
        try { return File.ReadAllText(path); }
        catch { return null; }
    }

    /// <summary>patch 参数/业务校验异常（区别于系统异常，消息直接给 agent 看）。</summary>
    private sealed class PatchException : Exception
    {
        public PatchException(string message) : base(message) { }
    }
}
