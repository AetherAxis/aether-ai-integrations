using System.Text.Json;

namespace Aether.Ledger.AI.Tests;

/// <summary>节点级测试夹具：进程内起节点（临时端口 + 临时链文件）+ 客户端回环。
/// 2026-10-23 迁移复制件——源自 aether-ledger/tests/Aether.Ledger.Tests/LedgerNodeTests.cs（internal
/// 夹具无法跨程序集/跨仓复用；上游改动须手动同步两份）。</summary>
internal sealed class LedgerNodeLoopback : IDisposable
{
    private readonly LedgerNode _node;
    private readonly string _chainFile;
    private readonly string? _producerSeed;

    public LedgerClient Client { get; }
    /// <summary>实际监听端口（临时端口 0 = 内核分配——LedgerWalletToolTests 把它写进工具的节点地址 env）。</summary>
    public int Port { get; private init; }
    public string ChainFile => _chainFile;
    public LedgerGenesis Genesis => LedgerChainFile.Read(_chainFile).Genesis;

    private LedgerNodeLoopback(LedgerNode node, LedgerClient client, string chainFile, string? producerSeed)
    {
        _node = node;
        Client = client;
        _chainFile = chainFile;
        _producerSeed = producerSeed;
    }

    /// <summary>启动新链回环节点（临时端口 0 + 临时链文件；捕获生成日志里的 treasury/producer 种子供测试用）。</summary>
    public static async Task<LedgerNodeLoopback> StartNewAsync(string label)
    {
        var chainFile = Path.Combine(Path.GetTempPath(), $"ledger-node-{label}-{Guid.NewGuid():N}.chain");
        var logFile = chainFile + ".log";
        var options = new LedgerNodeOptions("127.0.0.1", 0, chainFile, 77, 60);
        var node = new LedgerNode(options, logFile);
        node.Start();
        var port = node.LocalEndpoint?.Port ?? throw new InvalidOperationException("节点未绑定端口");
        // 种子（生成的种子打印在日志里——测试从那里回收）
        var log = await File.ReadAllTextAsync(logFile);
        // 种子必须回收成功，否则后面所有签名断言都会以「空种子」这种难以定位的方式失败。
        var treasurySeed = ExtractSeed(log, "treasury")
            ?? throw new InvalidOperationException($"节点日志中未找到 treasury 种子：{logFile}");
        var producerSeed = ExtractSeed(log, "producer");
        return new LedgerNodeLoopback(node, new LedgerClient("127.0.0.1", port), chainFile, producerSeed)
        {
            TreasurySeed = treasurySeed,
            Port = port,
        };
    }

    /// <summary>生成链时日志里的 treasury 种子（hex）——测试直接用它签名转账。</summary>
    public string TreasurySeed { get; private init; } = "";

    private static string? ExtractSeed(string log, string label)
    {
        var match = System.Text.RegularExpressions.Regex.Match(
            log, $@"⚠ {label} 种子（hex[^：]*）：([0-9a-f]{{64}})");
        return match.Success ? match.Groups[1].Value : null;
    }

    public void Dispose()
    {
        try { _node.StopAsync().GetAwaiter().GetResult(); } catch { }
        try { _node.Dispose(); } catch { }
        try { File.Delete(_chainFile); } catch { }
        try { File.Delete(_chainFile + ".log"); } catch { }
    }
}
