using System.ComponentModel;
using System.Security.Cryptography;
using Aether.Ledger;

namespace Aether.Ledger.AI.Tools;

/// <summary>
/// 链上钱包工具：agent 持币转账（「AI agent 经济」的操作面）。节点地址/钱包种子文件/custody 地址全部
/// 宿主侧配置——工具只做「签名在调用方」（种子不出宿主进程，不进 LLM 上下文）。
/// <para>钱包种子文件 = agent 的全部财富与身份；每次调用现读（换文件免重启），首个签名操作时自动生成。</para>
/// </summary>
public class LedgerWalletTool
{
    private const string NodeAddrEnv = "AETHER_LEDGER_NODE_ADDR";
    private const string CustodyAddrEnv = "AETHER_LEDGER_CUSTODY_ADDRESS";
    private readonly string? _walletSeedFilePath;

    public LedgerWalletTool(string? walletSeedFilePath) => _walletSeedFilePath = walletSeedFilePath;

    public class ChainInfoOutput
    {
        public bool Success { get; set; }
        public ulong Height { get; set; }
        public uint ChainId { get; set; }
        public string? GenesisHash { get; set; }
        /// <summary>固定总供给（21M 币，8 位小数显示——结构上不可增发）。</summary>
        public string? TotalSupply { get; set; }
        public string? Error { get; set; }
    }

    public class BalanceOutput
    {
        public bool Success { get; set; }
        /// <summary>查询的地址（40 位 hex）。</summary>
        public string? Address { get; set; }
        /// <summary>币余额（币数显示，如 "3.50000000"）。</summary>
        public string? Balance { get; set; }
        /// <summary>币余额原子数（1 币 = 1e8 原子）。</summary>
        public ulong BalanceAtoms { get; set; }
        /// <summary>计价币余额（AQ，币数显示——充进交易所变成场内现金）。</summary>
        public string? QuoteBalance { get; set; }
        /// <summary>计价币余额原子数。</summary>
        public ulong QuoteBalanceAtoms { get; set; }
        /// <summary>该地址已发出的交易数（nonce——转账前自增防重放）。</summary>
        public ulong Nonce { get; set; }
        public string? Error { get; set; }
    }

    public class TransferOutput
    {
        public bool Success { get; set; }
        /// <summary>链上交易哈希（出块由节点异步完成）。</summary>
        public string? TxHash { get; set; }
        /// <summary>付款方地址。</summary>
        public string? From { get; set; }
        public string? To { get; set; }
        /// <summary>金额（币数显示）。</summary>
        public string? Amount { get; set; }
        public string? Memo { get; set; }
        public string? Error { get; set; }
    }

    [Description("查询虚拟币链信息：当前高度、chainId、固定总供给（21M，不可增发）。链未运行时返回错误。")]
    public async Task<ChainInfoOutput> LedgerGetInfo(CancellationToken cancellationToken = default)
    {
        try
        {
            var info = await NodeClient().GetInfoAsync(cancellationToken);
            return new ChainInfoOutput
            {
                Success = true,
                Height = info.Height,
                ChainId = info.ChainId,
                GenesisHash = info.GenesisHash,
                TotalSupply = LedgerSupply.Format(LedgerSupply.TotalSupplyAtoms),
            };
        }
        catch (Exception ex) { return new ChainInfoOutput { Error = ex.Message }; }
    }

    [Description("查询链上地址余额。address 缺省 = 本 agent 钱包的地址。余额为零的钱包/地址也可查询。")]
    public async Task<BalanceOutput> LedgerGetBalance(
        [Description("要查询的链上地址（40 位 hex）；缺省查自己钱包")] string? address = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            string target;
            if (string.IsNullOrWhiteSpace(address)) target = LoadOrCreateWallet().Address.Hex;
            else target = LedgerAddress.Parse(address.Trim()).Hex;
            var account = await NodeClient().GetBalanceAsync(target, cancellationToken);
            return new BalanceOutput
            {
                Success = true,
                Address = target,
                Balance = LedgerSupply.Format(account.BalanceAtoms),
                BalanceAtoms = account.BalanceAtoms,
                QuoteBalance = LedgerSupply.Format(account.QuoteBalanceAtoms),
                QuoteBalanceAtoms = account.QuoteBalanceAtoms,
                Nonce = account.Nonce,
            };
        }
        catch (Exception ex) { return new BalanceOutput { Error = ex.Message }; }
    }

    [Description("链上转账（用本 agent 钱包签名提交——不可逆）。金额单位是币数（支持 8 位小数，如 \"1.5\"）。asset 可选 coin（币，默认）/ quote（计价币 AQ）。memo 可选（≤64 字符，充值场景须填交易所账号）。链节点须在运行。")]
    public async Task<TransferOutput> LedgerTransfer(
        [Description("收款方链上地址（40 位 hex）")] string to,
        [Description("金额（币数，字符串，支持 8 位小数，如 \"1.5\"；必须 > 0）")] string amountCoins,
        [Description("备注 memo（可选，≤64 字符；充值到交易所时须填交易所账号）")] string? memo = null,
        [Description("资产：coin（币，默认）/ quote（计价币 AQ）")] string? asset = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var wallet = LoadOrCreateWallet();
            var target = LedgerAddress.Parse(to.Trim());
            var atoms = ParseCoins(amountCoins);
            var assetId = ParseAsset(asset);
            var client = NodeClient();
            var result = await TransferAsync(client, wallet, target, atoms, memo ?? "", cancellationToken, assetId);
            return result;
        }
        catch (Exception ex) { return new TransferOutput { Error = ex.Message }; }
    }

    [Description("充值到交易所（AQ 入金）：把计价币 AQ 转入交易所 custody 托管地址、memo=交易所账号——1~2 秒自动入账场内现金（1 AQ = 1 现金，像股票/期货账户充钱）。须先配置环境变量 AETHER_LEDGER_CUSTODY_ADDRESS（custody 地址，40 位 hex）。币（coin）通道已退役——coin 不进交易所。")]
    public async Task<TransferOutput> LedgerDepositToExchange(
        [Description("交易所账号（memo 归账键——充值到账的现金记到这个账号名下）")] string accountKey,
        [Description("金额（AQ 数，字符串，支持 8 位小数）")] string amountCoins,
        [Description("资产：quote（计价币 AQ，默认——唯一可充值资产；coin 已退役会被拒绝）")] string? asset = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var custodyHex = Environment.GetEnvironmentVariable(CustodyAddrEnv);
            if (string.IsNullOrWhiteSpace(custodyHex))
                return new TransferOutput
                {
                    Error = $"未配置 {CustodyAddrEnv}（交易所 custody 托管地址，40 位 hex）——向运维索取后设置环境变量重启。",
                };
            var custody = LedgerAddress.Parse(custodyHex.Trim());
            var wallet = LoadOrCreateWallet();
            var atoms = ParseCoins(amountCoins);
            var assetId = ParseAsset(asset ?? "quote");   // 充值默认 AQ（币通道已退役）
            if (assetId == LedgerSupply.Assets.Coin)
                return new TransferOutput
                {
                    Error = "币通道已退役（2026-10-10 拆除——交易所单资产化只收付计价币 AQ；coin 留在链上钱包）。充值请用 quote（AQ）。",
                };
            var client = NodeClient();
            var result = await TransferAsync(client, wallet, custody, atoms, accountKey.Trim(), cancellationToken, assetId);
            if (result.Success is true) result.Memo = accountKey.Trim();
            return result;
        }
        catch (Exception ex) { return new TransferOutput { Error = ex.Message }; }
    }

    // —— 内部 ——

    private static async Task<TransferOutput> TransferAsync(
        LedgerClient client, LedgerWallet wallet, LedgerAddress to, ulong atoms, string memo, CancellationToken ct,
        uint asset = LedgerSupply.Assets.Coin)
    {
        var info = await client.GetInfoAsync(ct);
        var account = await client.GetBalanceAsync(wallet.Address.Hex, ct);
        var available = account.BalanceOf(asset);
        if (available < atoms)
            return new TransferOutput
            {
                From = wallet.Address.Hex,
                To = to.Hex,
                Amount = LedgerSupply.Format(atoms),
                Error = $"余额不足（资产 {asset}）：{LedgerSupply.Format(available)} < {LedgerSupply.Format(atoms)}",
            };
        var tx = LedgerTransaction.Sign(info.ChainId, wallet, to, atoms, account.Nonce + 1, memo, asset);
        await client.SubmitTxAsync(tx, ct);
        return new TransferOutput
        {
            Success = true,
            TxHash = tx.Hash,
            From = wallet.Address.Hex,
            To = to.Hex,
            Amount = LedgerSupply.Format(atoms),
            Memo = memo is { Length: > 0 } ? memo : null,
        };
    }

    /// <summary>asset 参数解析：null/"coin" → 币；"quote" → 计价币；其余抛异常。</summary>
    private static uint ParseAsset(string? asset)
    {
        var value = (asset ?? "coin").Trim().ToLowerInvariant();
        return value switch
        {
            "coin" => LedgerSupply.Assets.Coin,
            "quote" => LedgerSupply.Assets.Quote,
            _ => throw new ArgumentException($"asset 非法：\"{value}\"（coin | quote）"),
        };
    }

    private static LedgerClient NodeClient()
    {
        var addr = Environment.GetEnvironmentVariable(NodeAddrEnv) ?? "127.0.0.1:17805";
        var colon = addr.LastIndexOf(':');
        var (host, port) = colon > 0 && int.TryParse(addr[(colon + 1)..], out var p)
            ? (addr[..colon], p)
            : ("127.0.0.1", 17805);
        return new LedgerClient(host, port);
    }

    /// <summary>钱包种子文件（hex 一行）→ <see cref="LedgerWallet"/>。文件不存在则自动生成并落盘（首次签名操作的引导）。</summary>
    private LedgerWallet LoadOrCreateWallet()
    {
        if (string.IsNullOrWhiteSpace(_walletSeedFilePath))
            throw new InvalidOperationException("宿主未配置 agent 钱包种子文件路径（AddLedgerTools 的 walletSeedFilePath）。");
        if (File.Exists(_walletSeedFilePath))
        {
            var hex = File.ReadAllText(_walletSeedFilePath).Trim();
            return new LedgerWallet(Convert.FromHexString(hex));
        }
        var seed = RandomNumberGenerator.GetBytes(32);
        var dir = Path.GetDirectoryName(_walletSeedFilePath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(_walletSeedFilePath, Convert.ToHexString(seed).ToLowerInvariant() + Environment.NewLine);
        var wallet = new LedgerWallet(seed);
        Console.WriteLine($"[LEDGER-WALLET] ⚠ 已为 agent 生成新钱包：{_walletSeedFilePath}（种子明文 hex——⚠ 全部财富与身份，备份它；丢了没人能找回）地址 {wallet.Address.Hex}");
        return wallet;
    }

    private static ulong ParseCoins(string coins)
    {
        if (!decimal.TryParse(coins, out var value) || value <= 0)
            throw new ArgumentException($"币数非法：\"{coins}\"（应为正数，支持 8 位小数，如 \"1.5\"）");
        return (ulong)Math.Round(value * (decimal)LedgerSupply.AtomsPerCoin);
    }
}
