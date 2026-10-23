using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Aether.SystemTools.AI.Tools;

public class CommandExecutorTool
{
    public class Output
    {
        public int ExitCode { get; set; }
        public string StandardOutput { get; set; } = string.Empty;
        public string StandardError { get; set; } = string.Empty;
        public bool Success => ExitCode == 0;
        public string? Error { get; set; }
    }

    [Description("执行程序（直接进程，无 shell；适合单条命令，不支持管道/重定向。需管道用 ExecuteShellCommand）")]
    public async Task<Output> ExecuteCommand([Description("要执行的命令")] string command, [Description("命令参数")] string? arguments = null, [Description("工作目录")] string? workingDirectory = null, [Description("超时时间（毫秒）")] int? timeoutMs = null, CancellationToken cancellationToken = default)
    {
        Process? process = null;
        try
        {
            if (string.IsNullOrEmpty(command))
            {
                return new Output { Error = "命令不能为空" };
            }

            var processStartInfo = new ProcessStartInfo
            {
                FileName = ResolveCommandPath(command),
                Arguments = arguments ?? string.Empty,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,   // 重定向 stdin 并立即关闭，防止子进程读 stdin 永久阻塞
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = workingDirectory ?? Directory.GetCurrentDirectory()
            };

            var outputBuilder = new StringBuilder();
            var errorBuilder = new StringBuilder();

            using var outputWaitHandle = new ManualResetEvent(false);
            using var errorWaitHandle = new ManualResetEvent(false);

            // StringBuilder 由回调线程写、主流程读（超时/取消路径可能在写进行中读）——
            // StringBuilder 非线程安全，并发读写可抛异常/撕裂数据，故两侧都加锁。
            string TakeOutput() { lock (outputBuilder) return outputBuilder.ToString(); }
            string TakeError() { lock (errorBuilder) return errorBuilder.ToString(); }

            process = new Process { StartInfo = processStartInfo };
            process.OutputDataReceived += (sender, e) =>
            {
                if (e.Data == null)
                {
                    try { outputWaitHandle.Set(); } catch (ObjectDisposedException) { }
                }
                else
                {
                    lock (outputBuilder) outputBuilder.AppendLine(e.Data);
                }
            };

            process.ErrorDataReceived += (sender, e) =>
            {
                if (e.Data == null)
                {
                    try { errorWaitHandle.Set(); } catch (ObjectDisposedException) { }
                }
                else
                {
                    lock (errorBuilder) errorBuilder.AppendLine(e.Data);
                }
            };

            process.Start();
            try { process.StandardInput.Close(); } catch { } // EOF：任何读 stdin 的程序不致永久阻塞
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            var timeout = timeoutMs ?? 30000;
            // Task.Run 把同步 WaitForExit 丢到线程池，cancellationToken 可取消等待。
            bool completed;
            try
            {
                completed = await Task.Run(() => process.WaitForExit(timeout), cancellationToken);
            }
            catch (OperationCanceledException)
            {
                // 用户停止：进程在下方 finally 被 kill。
                return new Output
                {
                    ExitCode = -1,
                    StandardOutput = TakeOutput(),
                    StandardError = TakeError(),
                    Error = "命令执行已被取消（用户停止）"
                };
            }

            if (!completed)
            {
                // 超时：进程在下方 finally 被 kill。
                return new Output
                {
                    ExitCode = -1,
                    StandardOutput = TakeOutput(),
                    StandardError = TakeError(),
                    Error = $"命令执行超时（{timeout}ms）"
                };
            }

            // 读超时（镜像 ShellCommandTool 的 5s）：主进程退出 ≠ 管道 EOF——孙进程继承 stdout/stderr
            // 管道且不关时，OutputDataReceived 的 null 终结符永不发，无超时的 WaitOne 会永久挂死。
            // 超时后照常返回已捕获的部分输出（丢的只是孙进程的尾部噪音）。
            outputWaitHandle.WaitOne(5000);
            errorWaitHandle.WaitOne(5000);

            return new Output
            {
                ExitCode = process.ExitCode,
                StandardOutput = TakeOutput(),
                StandardError = TakeError()
            };
        }
        catch (Exception ex)
        {
            return new Output
            {
                ExitCode = -1,
                Error = $"执行命令失败: {ex.Message}"
            };
        }
        finally
        {
            // 任何退出路径都杀进程树（ProcessKiller 先 Kill(true) 再 pgrep 递归兜底——
            // macOS 上 Kill(true) 偶尔漏杀重型子树，导致取消/超时后 CPU 仍占满）。
            if (process is not null)
            {
                ProcessKiller.KillTree(process);
                process.Dispose();
            }
        }
    }

    /// <summary>把相对命令名解析成绝对路径（镜像 GitService.FindGitExecutable）。
    /// GUI 启动的 .app 继承受限 PATH，<see cref="Process.Start"/> 用父进程 PATH 解析相对 FileName——
    /// 本工具无 shell 不走登录 shell，故扫 PATH + 常见安装目录兜底，让 dotnet/npx 等被直接进程找到。
    /// 绝对路径原样返回；未找到返回原名让 Process.Start 报原生错误（Windows 无 .app PATH 问题且需 PATHEXT，此函数对其基本是 no-op）。</summary>
    private static string ResolveCommandPath(string command)
    {
        if (string.IsNullOrEmpty(command) || Path.IsPathRooted(command)) return command;
        var dirs = new List<string>();
        var pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrEmpty(pathEnv))
            foreach (var d in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
                dirs.Add(d);
        // .app 受限 PATH 兜底：补常见 bin 目录（macOS homebrew / Linux）。
        dirs.AddRange(new[] { "/usr/local/bin", "/opt/homebrew/bin", "/usr/bin", "/bin" });
        foreach (var d in dirs)
        {
            try
            {
                var full = Path.Combine(d.Trim('"'), command);
                if (File.Exists(full)) return full;
            }
            catch { /* 脏 PATH 段跳过 */ }
        }
        return command;
    }
}
