using Aether.AI.Core.Agents;
using Aether.Ledger.AI;
using Aether.AI.Harness.Tools;
using Aether.AI.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace Aether.Ledger.AI.Tests;

/// <summary>
/// DI 接线测试（镜像 monorepo Acme.Ctp.AI.Tests/CtpWiringTests 模式）：
/// AddLedgerTools 经 aether-ai 的 AetherAIBuilder.ConfigureToolRegistry 注册 4 个工具进 ToolRegistry，
/// 审批等级按适配器约定（转账 Dangerous 不可逆 / 充值 Confirm 可逆 / 查询 Safe）。
/// 2026-10-23 P2 批次新增——迁移测试优先，此为 aether-ai 仓外首个适配器的接缝钉子。
/// </summary>
public class LedgerWiringTests
{
    [Fact]
    public void AddLedgerTools_registers_four_tools_with_approval_levels()
    {
        var services = new ServiceCollection();
        var aether = services.AddAetherAI();

        aether.AddLedgerTools(walletSeedFilePath: null);

        using var sp = services.BuildServiceProvider();
        var tools = sp.GetRequiredService<ToolRegistry>();

        Assert.All(LedgerAIBuilderExtensions.ToolNames, name => Assert.True(tools.Contains(name), $"工具 {name} 未注册"));
        Assert.Equal(ToolApprovalLevel.Safe, tools.LevelOf("LedgerGetInfo"));
        Assert.Equal(ToolApprovalLevel.Safe, tools.LevelOf("LedgerGetBalance"));
        Assert.Equal(ToolApprovalLevel.Dangerous, tools.LevelOf("LedgerTransfer"));
        Assert.Equal(ToolApprovalLevel.Confirm, tools.LevelOf("LedgerDepositToExchange"));
    }
}
