using Aether.AI.Brains.Rule;
using Aether.AI.Core.Agents;
using Aether.AI.Harness.Agents;
using Aether.AI.Hosting;
using Aether.SystemTools.AI;
using Microsoft.Extensions.DependencyInjection;

// 用法：dotnet run --project tests/Consumers/Aether.SystemTools.AI.Consumer [目录]
// 不给目录时自建临时目录 + 示例文件，保证可重复运行。
var target = args.Length > 0 ? Path.GetFullPath(args[0]) : CreateFixture();
try
{
    var services = new ServiceCollection();
    services.AddRuleBrains();
    services.AddAetherAI().AddSystemTools(includeCommandTools: false);
    using var provider = services.BuildServiceProvider();
    var agents = provider.GetRequiredService<AgentFactory>();

    // 大脑真正调用系统工具：ReadFile / ExploreDirectory，走 MAF 的调用-回填循环。
    await RunAsync(agents, "read", "system-tools-read", "ReadFile",
        new Dictionary<string, object?> { ["filePath"] = "{user.1}" },
        @"^读取 (.+)$", $"读取 {Path.Combine(target, "demo.txt")}",
        expected: "system-tools-demo");

    await RunAsync(agents, "list", "system-tools-list", "ExploreDirectory",
        new Dictionary<string, object?> { ["path"] = "{user.1}" },
        @"^列出 (.+)$", $"列出 {target}",
        expected: "demo.txt");

    Console.WriteLine("SystemTools:PASS");
}
finally
{
    if (args.Length == 0)
    {
        try { Directory.Delete(target, recursive: true); } catch { /* 清理失败不影响结果 */ }
    }
}

static async Task RunAsync(
    AgentFactory agents,
    string id,
    string name,
    string tool,
    Dictionary<string, object?> args,
    string userPattern,
    string input,
    string expected)
{
    var definition = new AgentDefinition
    {
        Id = id,
        Name = name,
        Engine = EngineKind.Rule,
        Tools = new[] { tool },
        Memory = AgentMemoryMode.Off,
        Rules = new RuleProgram
        {
            Steps = new()
            {
                new RuleStep
                {
                    Match = new() { OnUserRegex = userPattern, MaxToolCalls = 0 },
                    Action = new() { Tool = tool, Args = args },
                },
                new RuleStep
                {
                    Match = new() { MinToolCalls = 1 },
                    Action = new() { ReplyText = "{result}" },
                },
            },
        },
    };

    var agent = agents.Create(definition);
    var session = await agent.CreateSessionAsync();
    var output = string.Empty;
    await foreach (var update in agent.RunStreamingAsync(input, session))
    {
        output += update.Text;
    }

    if (!output.Contains(expected, StringComparison.Ordinal))
    {
        throw new InvalidOperationException($"{tool}: expected '{expected}' in output, got '{output}'");
    }

    Console.WriteLine($"{tool}:PASS");
}

static string CreateFixture()
{
    var dir = Path.Combine(Path.GetTempPath(), "aether-systemtools-consumer-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(dir);
    File.WriteAllText(Path.Combine(dir, "demo.txt"), "system-tools-demo\n");
    return dir;
}
