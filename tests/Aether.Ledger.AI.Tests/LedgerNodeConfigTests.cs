using Aether.Ledger.AI.Tools;

namespace Aether.Ledger.AI.Tests;

/// <summary>
/// 节点地址/custody 地址的解析优先级：**构造参数（宿主配置）&gt; 环境变量 &gt; 开发默认**。
/// 这条规则是「钱包两端可用」的地基——桌面把节点指本机、手机指服务端，同一份工具实现，
/// 而手机端根本设不了环境变量，所以宿主传进来的值必须压过 env。
/// </summary>
[Collection(nameof(LedgerNodeConfigCollection))]
public class LedgerNodeConfigTests
{
    private const string NodeAddrEnv = "AETHER_LEDGER_NODE_ADDR";
    private const string CustodyAddrEnv = "AETHER_LEDGER_CUSTODY_ADDRESS";

    /// <summary>每个用例前后都还原环境变量（进程级共享状态）。</summary>
    private static void WithEnv(string? node, string? custody, Action body)
    {
        var oldNode = Environment.GetEnvironmentVariable(NodeAddrEnv);
        var oldCustody = Environment.GetEnvironmentVariable(CustodyAddrEnv);
        try
        {
            Environment.SetEnvironmentVariable(NodeAddrEnv, node);
            Environment.SetEnvironmentVariable(CustodyAddrEnv, custody);
            body();
        }
        finally
        {
            Environment.SetEnvironmentVariable(NodeAddrEnv, oldNode);
            Environment.SetEnvironmentVariable(CustodyAddrEnv, oldCustody);
        }
    }

    /// <summary>宿主给了地址就用宿主的（即使 env 指别处）——手机端唯一能配地址的通道。</summary>
    [Fact]
    public void 构造参数优先于环境变量()
    {
        WithEnv("127.0.0.1:17805", null, () =>
        {
            var tool = new LedgerWalletTool(null, "ledger.example.com:17805", "c1c1c1c1c1c1c1c1c1c1c1c1c1c1c1c1c1c1c1c1");
            Assert.Equal("ledger.example.com:17805", tool.ResolvedNodeAddress);
            Assert.True(tool.HasCustodyAddress);
        });
    }

    /// <summary>宿主没给 → 环境变量（服务器/桌面兼容既有部署方式）。</summary>
    [Fact]
    public void 环境变量次之()
    {
        WithEnv("10.0.0.9:27805", "d2d2d2d2d2d2d2d2d2d2d2d2d2d2d2d2d2d2d2d2", () =>
        {
            var tool = new LedgerWalletTool("/tmp/a.seed");
            Assert.Equal("10.0.0.9:27805", tool.ResolvedNodeAddress);
            Assert.True(tool.HasCustodyAddress);
        });
    }

    /// <summary>都没有 → 开发默认（本地节点），custody 视为未配置（充值工具会返回指引而不是瞎转钱）。</summary>
    [Fact]
    public void 都没有时回落开发默认且custody视为未配置()
    {
        WithEnv(null, null, () =>
        {
            var tool = new LedgerWalletTool("/tmp/a.seed");
            Assert.Equal("127.0.0.1:17805", tool.ResolvedNodeAddress);
            Assert.False(tool.HasCustodyAddress);
        });
    }

    /// <summary>空串/纯空白等同于「没给」——配置文件里留空不该把 env 顶掉。</summary>
    [Fact]
    public void 空字符串不覆盖环境变量()
    {
        WithEnv("10.0.0.9:27805", null, () =>
        {
            var tool = new LedgerWalletTool("/tmp/a.seed", "   ", "");
            Assert.Equal("10.0.0.9:27805", tool.ResolvedNodeAddress);
        });
    }

    /// <summary>
    /// 首次签名自动建的钱包种子文件必须是 0600（明文私钥 = 全部财富，不能让同机其他用户读到）。
    /// 建完立刻收紧权限，而不是指望 umask。
    /// </summary>
    [Fact]
    public async Task 种子文件权限收紧到0600()
    {
        var seedFile = Path.Combine(Path.GetTempPath(), $"agent-wallet-{Guid.NewGuid():N}.seed");
        WithEnv("127.0.0.1:1", null, () =>
        {
            // 节点端口故意无效：文件在连节点之前就已建好，这里只要触发「自动建钱包」这条路。
            var tool = new LedgerWalletTool(seedFile);
            _ = tool.LedgerGetBalance().GetAwaiter().GetResult();
        });
        try
        {
            Assert.True(File.Exists(seedFile));
            if (!OperatingSystem.IsWindows())
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(seedFile));
        }
        finally
        {
            try { File.Delete(seedFile); } catch { /* 临时文件 */ }
        }
    }

    /// <summary>地址写法宽容：host:port / URL / 带路径 / IPv6 方括号 / 只给 host（用默认端口）。</summary>
    [Theory]
    [InlineData("127.0.0.1:17805", "127.0.0.1", 17805)]
    [InlineData("http://ledger.example.com:17805", "ledger.example.com", 17805)]
    [InlineData("https://ledger.example.com:443/rpc", "ledger.example.com", 443)]
    [InlineData("  ledger.example.com:8080  ", "ledger.example.com", 8080)]
    [InlineData("ledger.example.com", "ledger.example.com", 17805)]
    [InlineData("[::1]:17805", "::1", 17805)]
    public void 节点地址解析(string input, string host, int port)
    {
        var (h, p) = LedgerWalletTool.ParseNodeAddress(input);
        Assert.Equal(host, h);
        Assert.Equal(port, p);
    }
}

/// <summary>进程级环境变量会被改——与其他测试并行会打架。</summary>
[CollectionDefinition(nameof(LedgerNodeConfigCollection), DisableParallelization = true)]
public class LedgerNodeConfigCollection;
