using Aether.AI.Brains.Rule;
using Aether.AI.Core.Agents;
using Aether.AI.Harness.Agents;
using Aether.AI.Harness.Tools;
using Aether.AI.Hosting;
using Aether.SystemTools.AI.Tools;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace Aether.SystemTools.AI.Tests;

/// <summary>
/// 系统工具从 Aether monorepo 迁入后的契约：
/// ① DI 注册元数据（15 个工具、审批等级、命令工具的 DesktopOnly）；
/// ② 工具本身在真实文件系统上工作（写/读/编辑/搜索/浏览/删除）；
/// ③ <c>AIFunction</c> 参数按名绑定可被调用（MAF 调用工具的已知陷阱）；
/// ④ 规则大脑能经 Harness 真正调用系统工具并拿到回填结果。
/// </summary>
public class SystemToolsTests
{
    [Fact]
    public void AddSystemTools_registers_all_metadata_with_approval_levels()
    {
        using var provider = BuildProvider(includeCommandTools: true);
        var registry = provider.GetRequiredService<ToolRegistry>();

        var metas = registry.GetAllMetas();
        Assert.Equal(SystemToolsAIBuilderExtensions.ToolNames.Length, metas.Count);
        Assert.Equal(16, metas.Count);
        Assert.All(metas, meta => Assert.Equal("系统工具", meta.Category));

        Assert.Equal(ToolApprovalLevel.Safe, registry.LevelOf("ReadFile"));
        Assert.Equal(ToolApprovalLevel.Confirm, registry.LevelOf("MoveFile"));
        Assert.Equal(ToolApprovalLevel.Dangerous, registry.LevelOf("DeleteFile"));
        Assert.Equal(ToolApprovalLevel.Dangerous, registry.LevelOf("ExecuteCommand"));

        // 命令工具 spawn 子进程：桌面宿主可用，手机端自动过滤。
        var execute = Assert.Single(metas, meta => meta.Name == "ExecuteCommand");
        Assert.Equal(HostCapability.DesktopOnly, execute.HostCapability);
        Assert.Equal(HostCapability.All, Assert.Single(metas, meta => meta.Name == "ReadFile").HostCapability);
    }

    [Fact]
    public void AddSystemTools_can_omit_command_tools_for_sandboxed_hosts()
    {
        using var provider = BuildProvider(includeCommandTools: false);
        var registry = provider.GetRequiredService<ToolRegistry>();

        Assert.Equal(14, registry.GetAllMetas().Count);
        Assert.False(registry.Contains("ExecuteCommand"));
        Assert.False(registry.Contains("ExecuteShellCommand"));
    }

    [Fact]
    public async Task File_tools_work_on_the_real_file_system()
    {
        using var dir = new TempDirectory();
        var path = Path.Combine(dir.Path, "note.txt");

        var write = await new FileWriteTool().WriteFile(path, "hello\nworld\n");
        Assert.True(write.Success);
        Assert.True(write.IsNewFile);

        var read = await new FileReadTool().ReadFile(path);
        Assert.Equal("hello" + Environment.NewLine + "world" + Environment.NewLine, read.Content);
        Assert.Equal(3, read.TotalLines);

        var edit = await new FileEditTool().EditFile(path, "world", "aether");
        Assert.True(edit.Success);
        Assert.Equal(1, edit.ReplaceCount);
        Assert.Contains("aether", (await new FileReadTool().ReadFile(path)).Content);

        var grep = await new GrepTool().Grep("aether", dir.Path);
        Assert.Null(grep.Error);
        Assert.Contains(path, grep.Files);

        var glob = await new GlobTool().Glob("*.txt", dir.Path);
        Assert.Null(glob.Error);
        Assert.Contains(path, glob.Files);

        var explore = await new DirectoryExplorerTool().ExploreDirectory(dir.Path);
        Assert.Null(explore.Error);
        Assert.Contains(explore.Items, item => item.Name == "note.txt" && !item.IsDirectory);

        var delete = await new FileDeleteTool().DeleteFile(path);
        Assert.True(delete.Success);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task Search_tools_refuse_an_empty_path_instead_of_scanning_the_working_directory()
    {
        // 桌面 app 从 .app 启动时 CWD=/，空 path 回退会递归扫整机——必须显式报错。
        Assert.Contains("搜索路径不能为空", (await new GrepTool().Grep("x", string.Empty)).Error);
        Assert.Contains("搜索路径不能为空", (await new GlobTool().Glob("*.cs")).Error);
    }

    [Fact]
    public async Task Registry_resolves_an_invocable_AIFunction_with_binding_by_parameter_name()
    {
        using var provider = BuildProvider(includeCommandTools: false);
        using var dir = new TempDirectory();
        var path = Path.Combine(dir.Path, "invoke.txt");
        await File.WriteAllTextAsync(path, "from-ai-function");

        var registry = provider.GetRequiredService<ToolRegistry>();
        var function = Assert.Single(registry.ResolveAll(new[] { "ReadFile" }));

        var result = await function.InvokeAsync(new AIFunctionArguments { ["filePath"] = path });

        Assert.Contains("from-ai-function", result?.ToString());
    }

    [Fact]
    public async Task A_rule_brain_calls_a_real_system_tool_through_the_harness()
    {
        using var provider = BuildProvider(includeCommandTools: false);
        using var dir = new TempDirectory();
        var path = Path.Combine(dir.Path, "brain.txt");
        await File.WriteAllTextAsync(path, "system-tools-work");

        var agents = provider.GetRequiredService<AgentFactory>();
        var agent = agents.Create(new AgentDefinition
        {
            Id = "system-tools-rule",
            Name = "SystemTools Rule",
            Engine = EngineKind.Rule,
            Tools = new[] { "ReadFile" },
            Memory = AgentMemoryMode.Off,
            Rules = new RuleProgram
            {
                Steps = new()
                {
                    new RuleStep
                    {
                        Match = new() { OnUserRegex = @"^读取 (.+)$", MaxToolCalls = 0 },
                        Action = new() { Tool = "ReadFile", Args = new() { ["filePath"] = "{user.1}" } },
                    },
                    new RuleStep
                    {
                        Match = new() { MinToolCalls = 1 },
                        Action = new() { ReplyText = "done:{result}" },
                    },
                },
            },
        });

        var session = await agent.CreateSessionAsync();
        var output = string.Empty;
        await foreach (var update in agent.RunStreamingAsync($"读取 {path}", session))
        {
            output += update.Text;
        }

        Assert.StartsWith("done:", output);
        Assert.Contains("system-tools-work", output);
    }

    [Fact]
    public async Task Command_tool_runs_a_process_and_captures_stdout()
    {
        if (OperatingSystem.IsWindows())
        {
            return; // 该用例依赖 unix 的 echo 可执行文件。
        }

        var output = await new CommandExecutorTool().ExecuteCommand("echo", "system-tools");

        Assert.True(output.Success, output.Error ?? output.StandardError);
        Assert.Contains("system-tools", output.StandardOutput);
    }

    private static ServiceProvider BuildProvider(bool includeCommandTools)
    {
        var services = new ServiceCollection();
        services.AddRuleBrains();
        services.AddAetherAI().AddSystemTools(includeCommandTools);
        return services.BuildServiceProvider();
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "aether-systemtools-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Path))
                {
                    Directory.Delete(Path, recursive: true);
                }
            }
            catch
            {
                // 清理失败不影响断言结果。
            }
        }
    }
}
