using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace Aether.SystemTools.AI.Tools;

/// <summary>
/// Shell 命令执行工具 — 支持管道(|)、重定向(>)、链式操作(&&)等 Shell 特性。
/// 与 CommandExecutorTool 的区别：本工具通过 Shell 进程执行命令，
/// 支持 Shell 内置功能和管道组合，适合代码编译、测试运行等复杂命令场景。
/// </summary>
public class ShellCommandTool
{
    [System.ComponentModel.Description("执行 Shell 命令，支持管道(|)、重定向(>)和链式操作(&&)")]
    public async Task<Output> ExecuteShellCommand(
        [System.ComponentModel.Description("Shell 命令字符串，如 'dotnet build 2>&1 | head -20'")] string command,
        [System.ComponentModel.Description("工作目录（可选）")] string? workingDirectory = null,
        [System.ComponentModel.Description("超时时间（毫秒），默认180000（3 分钟）。长构建如 dotnet publish 全量发布可能需 5-10 分钟，此时应显式传更大值")] int? timeoutMs = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(command))
            return new Output { ExitCode = -1, StandardOutput = string.Empty, StandardError = "命令不能为空", Success = false };

        // 默认 180s：覆盖绝大多数 dotnet build/test，超时已是干净退出（杀进程树+返回残留输出），不再有死锁风险。
        // 全量 publish/Lean 首编仍可能不够，由 agent 显式传更大 timeoutMs。
        var actualTimeout = timeoutMs ?? 180000;

        Process? process = null;
        Task<string>? stdoutTask = null;
        Task<string>? stderrTask = null;
        try
        {
            var psi = new ProcessStartInfo
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,   // 重定向 stdin 并立即关闭：防止命令读 stdin 永久阻塞
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = workingDirectory ?? Environment.CurrentDirectory,
            };

            // 根据平台选择 Shell。用 ArgumentList 传参，避免命令串里的引号/空格被错切
            // （旧实现 Arguments = $"-c \"{command}\" 在命令含双引号时会截断/错乱）。
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                psi.FileName = "cmd.exe";
                psi.ArgumentList.Add("/c");
                psi.ArgumentList.Add(command);
            }
            else
            {
                // 登录 shell（-l）：source 用户 profile 拿到完整 PATH。GUI 启动的 .app 继承受限 PATH
                // （缺 /usr/local/share/dotnet、/opt/homebrew/bin 等），非登录 bash 找不到 dotnet/npx/python3。
                // 镜像 McpServerRegistry.ConnectAsync 的相对命令处理。ArgumentList 逐参传递，命令串原样进 -c。
                psi.FileName = ResolveLoginShell();
                psi.ArgumentList.Add("-l");
                psi.ArgumentList.Add("-c");
                psi.ArgumentList.Add(MaybeSourceRc(command));
            }

            process = new Process { StartInfo = psi };
            process.Start();
            // 立即关闭 stdin：任何读 stdin 的子命令拿到 EOF 而非永久阻塞。
            try { process.StandardInput.Close(); } catch { }

            // 并发读取 stdout/stderr（不带取消令牌——取消/超时时应返回残留输出，而非抛出丢数据）。
            // 必须在 WaitForExit 之前并发读：命令输出超 ~64KB 管道缓冲会写阻塞、永不退出 → 死锁。
            stdoutTask = process.StandardOutput.ReadToEndAsync();
            stderrTask = process.StandardError.ReadToEndAsync();

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(actualTimeout);

            bool cancelledByUser = false;
            bool timedOut = false;
            try
            {
                await process.WaitForExitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                // 超时或用户取消：必须先杀进程（让 stdout/stderr 关闭 EOF）再读残留输出——
                // 否则 ReadToEndAsync 等一个永不关闭的管道（进程还活着）→ 死锁卡死。
                ProcessKiller.KillTree(process);
                cancelledByUser = cancellationToken.IsCancellationRequested;
                timedOut = !cancelledByUser;
            }

            // 进程已退出或已被杀 → 管道应已关闭，读取不会阻塞。但 KillTree 后仍可能因 reparent 到
            // launchd(PID 1) 的孙进程（dotnet publish 的 MSBuild/csc）仍持有管道写端而拿不到 EOF →
            // ReadToEndAsync 永久阻塞 → 工具永不返回（UI 一直"运行中"）。给读取加超时兜底：最多等 5s，
            // 超时则放弃残留输出（进程已杀，输出无意义）。正常退出路径 stdoutTask 已完成，WhenAny 立即返回无延迟。
            string stdout, stderr;
            if (await Task.WhenAny(stdoutTask, Task.Delay(5000)) == stdoutTask)
                stdout = await stdoutTask;
            else
                stdout = string.Empty;
            if (await Task.WhenAny(stderrTask, Task.Delay(5000)) == stderrTask)
                stderr = await stderrTask;
            else
                stderr = string.Empty;

            if (timedOut)
            {
                return new Output
                {
                    ExitCode = -1,
                    StandardOutput = Truncate(stdout, 50000),
                    StandardError = $"命令执行超时（{actualTimeout}ms），已终止进程。\n{Truncate(stderr, 10000)}",
                    Success = false,
                };
            }

            if (cancelledByUser)
            {
                return new Output
                {
                    ExitCode = -1,
                    StandardOutput = Truncate(stdout, 50000),
                    StandardError = $"命令执行已被取消（用户停止）。\n{Truncate(stderr, 10000)}",
                    Success = false,
                };
            }

            return new Output
            {
                ExitCode = process.ExitCode,
                StandardOutput = Truncate(stdout, 50000),
                StandardError = Truncate(stderr, 10000),
                Success = process.ExitCode == 0,
            };
        }
        catch (Exception ex)
        {
            return new Output
            {
                ExitCode = -1,
                StandardOutput = string.Empty,
                StandardError = $"执行 Shell 命令失败: {ex.Message}",
                Success = false,
            };
        }
        finally
        {
            // 任何退出路径都确保杀掉进程树（ProcessKiller 先 Kill(true) 再 pgrep 递归兜底——
            // Kill(true) 在 macOS 偶尔漏杀 dotnet publish 等重型子树，会导致取消/超时后 CPU 仍占满）。
            if (process is not null)
            {
                ProcessKiller.KillTree(process);
                process.Dispose();
            }
        }
    }

    /// <summary>取用户登录 shell（$SHELL），空或不存在回退 zsh→bash。让子命令经登录 shell source profile 拿到完整 PATH。</summary>
    private static string ResolveLoginShell()
    {
        var shell = Environment.GetEnvironmentVariable("SHELL");
        if (!string.IsNullOrEmpty(shell) && File.Exists(shell)) return shell;
        if (File.Exists("/bin/zsh")) return "/bin/zsh";
        return "/bin/bash";
    }

    /// <summary>
    /// 可选 source ~/.zshrc。登录 shell <c>-l</c> 只加载 .zprofile/.zshenv，**不加载 .zshrc**（非交互），
    /// 而用户常把 PATH 修正/工具链 token 放在 .zshrc 里 → agent 跑命令的 env 与真实终端不一致。
    /// 设环境变量 <c>AETHER_SOURCE_ZSHRC=1</c> 开启（默认关——.zshrc 可能有交互式副作用，不为所有命令冒回归风险）。
    /// <c>&gt;/dev/null 2&gt;&amp;1</c> 吞掉 .zshrc 的输出（提示符插件/compinit 噪音），<c>|| true</c> 保证加载失败不中断。
    /// </summary>
    private static string MaybeSourceRc(string command)
    {
        if (Environment.GetEnvironmentVariable("AETHER_SOURCE_ZSHRC") == "1")
            return "source ~/.zshrc >/dev/null 2>&1 || true; " + command;
        return command;
    }

    /// <summary>
    /// 保留头 + 尾地截断。构建/命令的**错误信息与摘要在末尾**，纯截头部（旧实现）会把 agent 最需要的错误丢掉。
    /// 头部 20% 给上下文（哪个项目/阶段开始），尾部 80% 给错误/摘要。
    /// </summary>
    private static string Truncate(string text, int maxLength)
    {
        if (string.IsNullOrEmpty(text) || text.Length <= maxLength)
            return text;
        int head = Math.Max(0, maxLength / 5);
        int tail = Math.Max(0, maxLength - head - 80);
        var note = $"\n... (已截断，省略 {text.Length - head - tail} 字符，保留头/尾) ...\n";
        return text[..head] + note + (tail > 0 ? text[^(tail)..] : string.Empty);
    }

    public class Output
    {
        public int ExitCode { get; set; }
        public string StandardOutput { get; set; } = string.Empty;
        public string StandardError { get; set; } = string.Empty;
        public bool Success { get; set; }
    }
}
