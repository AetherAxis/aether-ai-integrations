using System.Reflection;
using Aether.AI.Core.Agents;
using Aether.AI.Hosting;
using Aether.AI.Harness.Tools;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace Aether.Terminal.AI;

/// <summary>
/// 集成终端工具模块的注册入口（**桌面专属**，同 Aether.Browser.AI 惯例——依赖本机 PTY/控件）。
/// 本模块只注册工具；<see cref="ITerminalService"/> 的 impl 是 UI 耦合的（持 RoyalTerminal 控件/PTY），
/// **由宿主 UI 仓自行注册**（⑮ 桌面 head 的 Terminal/ 经 AddTerminalPanel 一并接线）——
/// 宿主不注册时工具在首次调用才解析失败（AIFunction 工厂是惰性的），不阻塞启动。
/// </summary>
public static class TerminalAIBuilderExtensions
{
    /// <summary>本模块注册的工具名（宿主可据此做工具白名单/能力策略）。</summary>
    public static readonly string[] ToolNames = { "run_in_terminal" };

    public static AetherAIBuilder AddTerminalTools(this AetherAIBuilder builder)
    {
        builder.Services.AddTransient<RunInTerminalTool>();
        builder.ConfigureToolRegistry(reg =>
        {
            reg.Register("run_in_terminal", "在集成终端（可见面板）执行命令——真实 PTY / 完整登录 env（含 .zshrc）/ 可观察可中断。" +
                "需要 TTY、长任务想观察/介入、或需完整环境时用此工具；拿干净输出做判断仍用 ExecuteShellCommand。" +
                "命令在终端可见，超时/取消后进程继续在终端跑（可手动停止）。", "终端",
                sp => Create(sp),
                ToolApprovalLevel.Dangerous, HostCapability.DesktopOnly);
        });
        return builder;
    }

    private static AIFunction Create(IServiceProvider sp)
    {
        var tool = sp.GetRequiredService<RunInTerminalTool>();
        var m = typeof(RunInTerminalTool).GetMethod(nameof(RunInTerminalTool.RunInTerminal), BindingFlags.Public | BindingFlags.Instance)
            ?? throw new InvalidOperationException("Tool method 'RunInTerminal' not found on RunInTerminalTool.");
        return AIFunctionFactory.Create(m, tool, "run_in_terminal");
    }
}
