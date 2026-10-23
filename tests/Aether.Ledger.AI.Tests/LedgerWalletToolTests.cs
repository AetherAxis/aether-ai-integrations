using Aether.Ledger.AI.Tools;
using Aether.Ledger;

namespace Aether.Ledger.AI.Tests;

/// <summary>
/// agent 链上钱包工具（批 4 缺口，2026-10-08——「AI agent 经济」的操作面）：
/// 真链回环 + 真 LedgerNodeLoopback 夹具（同程序集 internal 可见）+ 种子文件生命周期。
/// </summary>
public class LedgerWalletToolTests
{
    [Fact]
    public async Task 链信息_查询成功()
    {
        using var node = await LedgerNodeLoopback.StartNewAsync("tool-info");
        using var _ = SetNodeEnv(node);
        var tool = new LedgerWalletTool(null);

        var info = await tool.LedgerGetInfo();

        Assert.True(info.Success, info.Error);
        Assert.Equal(77u, info.ChainId);
        Assert.Equal(LedgerSupply.Format(LedgerSupply.TotalSupplyAtoms), info.TotalSupply);
    }

    [Fact]
    public async Task 钱包自动生成_种子文件落盘_余额查询()
    {
        using var node = await LedgerNodeLoopback.StartNewAsync("tool-wallet");
        using var _ = SetNodeEnv(node);
        var seedFile = Path.Combine(Path.GetTempPath(), $"agent-wallet-{Guid.NewGuid():N}.seed");
        try
        {
            var tool = new LedgerWalletTool(seedFile);

            // 未充值查询自己 → 0 余额 + 首次签名读路径自动生成种子文件（此处只读不签名——查余额走地址参数不触发生成）
            var self = await tool.LedgerGetBalance();
            Assert.True(self.Success, self.Error);
            Assert.Equal(0UL, self.BalanceAtoms);

            // treasury 给 agent 钱包打 2 币（手动走链——模拟国库发币；直接签名提交：
            // LedgerBridge.DepositAsync 现为充值专用入口（coin 拒），普通转账不再借道它）
            var treasury = new LedgerWallet(Convert.FromHexString(node.TreasurySeed));
            var chainInfo = await node.Client.GetInfoAsync();
            await node.Client.SubmitTxAsync(LedgerTransaction.Sign(
                chainInfo.ChainId, treasury, LedgerAddress.Parse(self.Address!), 200_000_000UL, 1, "gift", LedgerSupply.Assets.Coin));
            await WaitHeightAsync(node, 1);

            var after = await tool.LedgerGetBalance();
            Assert.True(after.Success, after.Error);
            Assert.Equal(200_000_000UL, after.BalanceAtoms);
            Assert.True(File.Exists(seedFile), "首个签名/地址推导操作应生成种子文件");
        }
        finally
        {
            try { File.Delete(seedFile); } catch { }
        }
    }

    [Fact]
    public async Task 转账_签名提交_余额扣减()
    {
        using var node = await LedgerNodeLoopback.StartNewAsync("tool-transfer");
        using var _ = SetNodeEnv(node);
        var seedFile = Path.Combine(Path.GetTempPath(), $"agent-wallet-{Guid.NewGuid():N}.seed");
        try
        {
            var tool = new LedgerWalletTool(seedFile);
            var self = await tool.LedgerGetBalance();   // 触发种子文件生成
            var treasury = new LedgerWallet(Convert.FromHexString(node.TreasurySeed));
            var chainInfo = await node.Client.GetInfoAsync();
            await node.Client.SubmitTxAsync(LedgerTransaction.Sign(
                chainInfo.ChainId, treasury, LedgerAddress.Parse(self.Address!), 300_000_000UL, 1, "gift", LedgerSupply.Assets.Coin));
            await WaitHeightAsync(node, 1);

            // agent 转给另一个钱包 1.5 币（带 memo）
            var payee = LedgerHarness.Wallet("tool-payee");
            var r = await tool.LedgerTransfer(payee.Address.Hex, "1.5", "货款");
            Assert.True(r.Success, r.Error);
            Assert.NotNull(r.TxHash);
            Assert.NotEmpty(r.TxHash);
            Assert.Equal("1.50000000", r.Amount);

            await WaitHeightAsync(node, 2);
            var balance = await tool.LedgerGetBalance();
            Assert.Equal(150_000_000UL, balance.BalanceAtoms);   // 3 − 1.5
            var payeeBalance = await node.Client.GetBalanceAsync(payee.Address.Hex);
            Assert.Equal(150_000_000UL, payeeBalance.BalanceAtoms);
        }
        finally
        {
            try { File.Delete(seedFile); } catch { }
        }
    }

    [Fact]
    public async Task 充值工具_未配custody地址_返回指引()
    {
        using var node = await LedgerNodeLoopback.StartNewAsync("tool-deposit");
        using var _ = SetNodeEnv(node);
        using var __ = UnsetCustodyEnv();
        var tool = new LedgerWalletTool(Path.Combine(Path.GetTempPath(), $"agent-wallet-{Guid.NewGuid():N}.seed"));

        var r = await tool.LedgerDepositToExchange("u1", "1");

        Assert.False(r.Success);
        Assert.Contains("AETHER_LEDGER_CUSTODY_ADDRESS", r.Error);
    }

    [Fact]
    public async Task 币数非法_错误信息友好()
    {
        var seedFile = Path.Combine(Path.GetTempPath(), $"agent-wallet-{Guid.NewGuid():N}.seed");
        try
        {
            var tool = new LedgerWalletTool(seedFile);
            var r = await tool.LedgerTransfer(LedgerHarness.Wallet("x").Address.Hex, "-1");
            Assert.False(r.Success);
            Assert.Contains("币数非法", r.Error);
        }
        finally
        {
            try { File.Delete(seedFile); } catch { }
        }
    }

    // —— 夹具 ——

    /// <summary>把工具的节点地址 env 指向回环节点（测试结束还原）。</summary>
    private static IDisposable SetNodeEnv(LedgerNodeLoopback node)
    {
        Environment.SetEnvironmentVariable("AETHER_LEDGER_NODE_ADDR", $"127.0.0.1:{node.Port}");
        return new EnvRestore("AETHER_LEDGER_NODE_ADDR");
    }

    private static IDisposable UnsetCustodyEnv()
    {
        Environment.SetEnvironmentVariable("AETHER_LEDGER_CUSTODY_ADDRESS", null);
        return new EnvRestore("AETHER_LEDGER_CUSTODY_ADDRESS");
    }

    private sealed class EnvRestore(string name) : IDisposable
    {
        private readonly string? _saved = Environment.GetEnvironmentVariable(name);
        public void Dispose() => Environment.SetEnvironmentVariable(name, _saved);
    }

    private static async Task WaitHeightAsync(LedgerNodeLoopback node, ulong target, int timeoutMs = 8000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if ((await node.Client.GetInfoAsync()).Height >= target) return;
            await Task.Delay(100);
        }
        Assert.Fail($"等出块超时（期望高度 {target}）");
    }
}
