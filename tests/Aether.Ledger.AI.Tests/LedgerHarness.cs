namespace Aether.Ledger.AI.Tests;

/// <summary>测试钱包 helper——2026-10-23 迁移最小复制件（源自 aether-ledger/tests 的 LedgerHarness，
/// 仅 Wallet：本测试程序集只用到确定性钱包；引擎级夹具其余部分留在 ledger 仓）。</summary>
internal static class LedgerHarness
{
    /// <summary>固定种子钱包（32B——同种子恒同钱包）。</summary>
    public static LedgerWallet Wallet(string label)
        => new(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(label)));
}
