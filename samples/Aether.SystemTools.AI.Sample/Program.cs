using Aether.AI.Brains.Rule;
using Aether.AI.Core.Agents;
using Aether.AI.Harness.Agents;
using Aether.AI.Hosting;
using Aether.SystemTools.AI;
using Microsoft.Extensions.DependencyInjection;

// SystemTools 示例：规则大脑真正调用系统工具（ReadFile / ExploreDirectory），
// 走 MAF 的「调用 → 执行 → 回填 → 终答」循环。断言在 tests/Aether.SystemTools.AI.Tests 里，
// 这里只演示接线与输出。
//
// 用法：dotnet run --project samples/Aether.SystemTools.AI.Sample [目录]
// 不给目录时自建临时目录 + 示例文件，保证可重复运行。
var target = args.Length > 0 ? Path.GetFullPath(args[0]) : CreateFixture();
try
{
    var services = new ServiceCollection();
    services.AddRuleBrains();
    services.AddAetherAI().AddSystemTools(includeCommandTools: false);
    using var provider = services.BuildServiceProvider();
    var agents = provider.GetRequiredService<AgentFactory>();

    var content = await RunAsync(agents, "system-tools-read", "ReadFile",
        new Dictionary<string, object?> { ["filePath"] = "{user.1}" },
        @"^读取 (.+)$", $"读取 {Path.Combine(target, "demo.txt")}");
    Console.WriteLine($"ReadFile  → {content.Trim()}");

    var listing = await RunAsync(agents, "system-tools-list", "ExploreDirectory",
        new Dictionary<string, object?> { ["path"] = "{user.1}" },
        @"^列出 (.+)$", $"列出 {target}");
    Console.WriteLine($"ExploreDirectory → {listing.Trim()}");
}
finally
{
    if (args.Length == 0)
    {
        try { Directory.Delete(target, recursive: true); } catch { /* 清理失败不影响结果 */ }
    }
}

static async Task<string> RunAsync(
    AgentFactory agents, string id, string tool, Dictionary<string, object?> args, string userPattern, string input)
{
    var definition = new AgentDefinition
    {
        Id = id,
        Name = id,
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
        output += update.Text;

    return output;
}

static string CreateFixture()
{
    var dir = Path.Combine(Path.GetTempPath(), "aether-systemtools-sample-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(dir);
    File.WriteAllText(Path.Combine(dir, "demo.txt"), "system-tools-demo\n");
    return dir;
}
