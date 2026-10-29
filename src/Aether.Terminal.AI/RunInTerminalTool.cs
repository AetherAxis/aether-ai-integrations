using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Aether.Terminal.AI;

/// <summary>
/// <c>run_in_terminal</c> 工具：在集成终端（可见面板）执行命令——真实 PTY / 完整 env（含 .zshrc）/ 可观察可中断。
/// 与 <c>ExecuteShellCommand</c> 的分工：后者静默管道拿干净 stdout/stderr 供判断；本工具走可见终端，适合
/// 需要 TTY、长任务想观察/介入、或需完整登录 env 的场景。输出含命令回显 + 轻量 ANSI 剥离（不如管道干净）。
/// 标 Dangerous（执行任意命令），对齐 <c>ExecuteShellCommand</c>。
/// （自 monorepo Acme.Desktop.Shared.Shell port，2026-10-29；差异：去掉 ICurrentProjectDirectoryProvider
/// ——⑮ 无「文档页项目目录」概念，默认 cwd 落 shell 家目录。）
/// </summary>
public sealed class RunInTerminalTool
{
    private readonly ITerminalService _terminal;

    public RunInTerminalTool(ITerminalService terminal)
    {
        _terminal = terminal;
    }

    [Description("在集成终端（可见面板）执行命令——真实 PTY / 完整 env（含 .zshrc）/ 可观察可中断。" +
                 "需要 TTY、长任务想观察/介入、或需完整登录环境时用此工具；拿干净输出做判断仍用 ExecuteShellCommand。" +
                 "命令在终端面板可见，超时/取消后进程继续在终端跑（可手动 Ctrl-C）。")]
    public async Task<Output> RunInTerminal(
        [Description("要在终端执行的命令（单条；多命令用 && 连接）")] string command,
        [Description("是否等待命令完成并返回退出码+输出（默认 true）。false=发完即返回，命令在终端继续跑")] bool waitForCompletion = true,
        [Description("超时（毫秒），默认180000。超时返回 completed=false 且进程继续在终端跑（可观察/可手动停）")] int? timeoutMs = null,
        [Description("工作目录（可选，默认 shell 家目录或活跃会话 cwd）")] string? workingDirectory = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(command))
            return new Output { Error = "命令不能为空" };

        // 取/建会话：优先活跃会话（可见）；无则建一个（cwd=workingDirectory 或家目录）。
        Guid sessionId;
        var active = _terminal.ActiveSessionId;
        var sessions = _terminal.GetSessions();
        if (active is { } aid && sessions.Count > 0)
            sessionId = aid;
        else
            sessionId = _terminal.CreateSession(workingDirectory, "agent").Id;

        // 若指定工作目录，先 cd 再跑（会话已有 cwd 时覆盖一次）。
        var fullCmd = !string.IsNullOrWhiteSpace(workingDirectory)
            ? $"cd {Quote(workingDirectory)} && {command}"
            : command;

        if (!waitForCompletion)
        {
            _terminal.SendInput(sessionId, fullCmd + "\n");
            return new Output { Success = true, Completed = false, OutputText = "命令已发送到终端（未等待完成）", SessionId = sessionId.ToString() };
        }

        var timeout = timeoutMs ?? 180000;
        var result = await _terminal.RunAsync(sessionId, fullCmd, timeout, cancellationToken).ConfigureAwait(false);
        var success = result.Completed && result.ExitCode == 0;
        return new Output
        {
            Success = success,
            ExitCode = result.ExitCode,
            Completed = result.Completed,
            // 头尾截断省 token（对齐 Claude Code/Codex 主流做法）：终端原始输出可能数千行，全量返回浪费上下文。
            // 失败时 tail 优先（错误/堆栈在末尾，主流都重视尾部）；成功时均衡头尾。完整输出在终端面板可见。
            OutputText = TruncateForAgent(result.Output, success),
            Error = result.Error,
            SessionId = sessionId.ToString(),
        };
    }

    /// <summary>路径含空格/特殊字符时加引号（POSIX 单引号转义；Windows 双引号）。</summary>
    private static string Quote(string path)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return "\"" + path.Replace("\"", "\"\"") + "\"";
        return "'" + path.Replace("'", @"'\''") + "'";
    }

    /// <summary>返回给 agent 的输出上限（字符）。终端原始输出（含 VT 噪音剥离后的文本）可能数千行，
    /// 全量返回浪费上下文。阈值比 Claude Code 的 ~30k 小——run_in_terminal 输出不如管道干净、且本地 agent 上下文有限。</summary>
    private const int MaxOutputChars = 6000;

    /// <summary>
    /// 头尾截断省 token（对齐 Claude Code/Codex 主流：长输出截断 + 通知，不搞人话摘要）。
    /// 失败时 tail 优先（错误/堆栈在末尾）：头 10% + 尾 90%；成功时均衡：头 20% + 尾 80%。
    /// 截断后附通知，告知 agent 完整输出在终端面板可见（命令本就在可见终端跑）。
    /// </summary>
    private static string TruncateForAgent(string text, bool success)
    {
        if (string.IsNullOrEmpty(text) || text.Length <= MaxOutputChars)
            return text;
        int headPct = success ? 20 : 10;
        int head = Math.Max(0, MaxOutputChars * headPct / 100);
        int tail = Math.Max(0, MaxOutputChars - head - 80);
        var note = $"\n... (已截断，省略 {text.Length - head - tail} 字符，完整输出在终端面板可见) ...\n";
        return text[..head] + note + (tail > 0 ? text[^(tail)..] : string.Empty);
    }

    public class Output
    {
        public bool Success { get; set; }
        public int? ExitCode { get; set; }
        public bool Completed { get; set; }
        public string OutputText { get; set; } = string.Empty;
        public string? Error { get; set; }
        public string? SessionId { get; set; }
    }
}
