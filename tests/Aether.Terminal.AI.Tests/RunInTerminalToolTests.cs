using Aether.Terminal.AI;
using Xunit;

namespace Aether.Terminal.AI.Tests;

/// <summary>run_in_terminal 工具行为测试（fake ITerminalService，不起 PTY——真实 PTY/控件链路见 ⑮ 桌面 head）：
/// 会话取用/新建、waitForCompletion 两种模式、workingDirectory 的 cd 前缀、结果映射与长输出截断。</summary>
public class RunInTerminalToolTests
{
    [Fact]
    public async Task 空命令拒绝()
    {
        var tool = new RunInTerminalTool(new FakeTerminalService());
        var output = await tool.RunInTerminal("  ");
        Assert.NotNull(output.Error);
        Assert.Contains("命令不能为空", output.Error);
    }

    [Fact]
    public async Task 有活跃会话则复用()
    {
        var fake = new FakeTerminalService();
        var session = (FakeTerminalService.FakeSession)fake.CreateSession("/tmp", "user");
        var tool = new RunInTerminalTool(fake);

        var output = await tool.RunInTerminal("ls -la");

        Assert.Single(fake.Runs);
        Assert.Equal(session.Id, fake.Runs[0].SessionId);
        Assert.Equal("ls -la", fake.Runs[0].Command);
        Assert.Equal(output.SessionId, session.Id.ToString());
    }

    [Fact]
    public async Task 无活跃会话则新建_cwd取workingDirectory()
    {
        var fake = new FakeTerminalService();
        var tool = new RunInTerminalTool(fake);

        var output = await tool.RunInTerminal("make", workingDirectory: "/build/dir");

        Assert.Contains(("/build/dir", "agent"), fake.Created);
        Assert.Single(fake.Runs);
        // 指定了工作目录 → 命令带 cd 前缀（POSIX 单引号）
        Assert.StartsWith("cd '/build/dir' && make", fake.Runs[0].Command);
        Assert.Equal(output.SessionId, fake.Created[0].Item1 == null ? null : fake.Sessions[0].Id.ToString());
    }

    [Fact]
    public async Task 不等待模式_只发送不Run()
    {
        var fake = new FakeTerminalService();
        var session = (FakeTerminalService.FakeSession)fake.CreateSession(null, null);
        var tool = new RunInTerminalTool(fake);

        var output = await tool.RunInTerminal("tail -f log", waitForCompletion: false);

        Assert.True(output.Success);
        Assert.False(output.Completed);
        Assert.Empty(fake.Runs);
        Assert.Contains((session.Id, "tail -f log\n"), fake.Inputs);
    }

    [Fact]
    public async Task 结果映射_成功与失败()
    {
        var fake = new FakeTerminalService();
        fake.CreateSession(null, null);
        var tool = new RunInTerminalTool(fake);

        fake.NextRunResult = new TerminalRunResult { ExitCode = 0, Completed = true, Output = "all good" };
        var ok = await tool.RunInTerminal("true");
        Assert.True(ok.Success);
        Assert.Equal(0, ok.ExitCode);
        Assert.Contains("all good", ok.OutputText);

        fake.NextRunResult = new TerminalRunResult { ExitCode = 2, Completed = true, Output = "boom" };
        var bad = await tool.RunInTerminal("false");
        Assert.False(bad.Success);
        Assert.Equal(2, bad.ExitCode);
    }

    [Fact]
    public async Task 超时未完成_进程继续跑的口径()
    {
        var fake = new FakeTerminalService();
        fake.CreateSession(null, null);
        fake.NextRunResult = new TerminalRunResult { Completed = false, Output = "partial", Error = "命令执行超时（100ms），进程仍在终端运行（可在终端手动停止）" };
        var tool = new RunInTerminalTool(fake);

        var output = await tool.RunInTerminal("sleep 100", timeoutMs: 100);

        Assert.False(output.Success);
        Assert.False(output.Completed);
        Assert.Contains("超时", output.Error);
        Assert.Contains("partial", output.OutputText);
    }

    [Fact]
    public async Task 长输出_头尾截断并通知()
    {
        var fake = new FakeTerminalService();
        fake.CreateSession(null, null);
        fake.NextRunResult = new TerminalRunResult { ExitCode = 0, Completed = true, Output = new string('a', 20000) };
        var tool = new RunInTerminalTool(fake);

        var output = await tool.RunInTerminal("cat big");

        Assert.True(output.OutputText.Length < 7000);   // 6000 上限 + 通知行
        Assert.Contains("已截断", output.OutputText);
        Assert.EndsWith("a", output.OutputText);        // 尾部保留（成功态头 20% 尾 80%）
    }
}
