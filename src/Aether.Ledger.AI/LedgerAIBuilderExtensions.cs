using System.Reflection;
using Aether.Ledger.AI.Tools;
using Aether.AI.Hosting;
using Aether.AI.Core.Agents;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace Aether.Ledger.AI;

public static class LedgerAIBuilderExtensions
{
    public static readonly string[] ToolNames =
        { "LedgerGetInfo", "LedgerGetBalance", "LedgerTransfer", "LedgerDepositToExchange" };

    /// <summary>
    /// 注册链上钱包工具（仅工具——适配器约定）。本工具是**纯客户端**：桌面可把 <paramref name="nodeAddress"/>
    /// 指向本机节点，手机指向服务端节点，同一份实现。
    /// <para><paramref name="walletSeedFilePath"/> = agent 钱包种子文件（hex 一行——**每次调用现读**，换种子文件免重启；
    /// 文件不存在时首个签名操作自动生成并落盘）。</para>
    /// <para>地址解析优先级：本方法参数 &gt; 环境变量（<c>AETHER_LEDGER_NODE_ADDR</c> / <c>AETHER_LEDGER_CUSTODY_ADDRESS</c>）
    /// &gt; 开发默认 <c>127.0.0.1:17805</c>。手机端设不了环境变量，必须由宿主把地址传进来。</para>
    /// <para>转账标 <see cref="ToolApprovalLevel.Dangerous"/>（链上转账不可逆）；充值标 Confirm（可逆——充错仍可提回）。</para>
    /// </summary>
    /// <param name="nodeAddress">链节点地址（<c>host:port</c> 或 <c>http://host:port</c>）；空 = 环境变量 → 默认。</param>
    /// <param name="custodyAddress">交易所 custody 托管地址（40 位 hex）；空 = 环境变量。</param>
    public static AetherAIBuilder AddLedgerTools(this AetherAIBuilder builder,
        string? walletSeedFilePath = null, string? nodeAddress = null, string? custodyAddress = null)
    {
        var s = builder.Services;
        s.AddTransient<LedgerWalletTool>(sp => new LedgerWalletTool(walletSeedFilePath, nodeAddress, custodyAddress));

        const string cat = "链上钱包";
        builder.ConfigureToolRegistry(reg =>
        {
            reg.Register("LedgerGetInfo", "查询虚拟币链信息（高度/chainId/固定总供给）", cat,
                sp => CreateTool<LedgerWalletTool>(sp, "LedgerGetInfo"), ToolApprovalLevel.Safe);
            reg.Register("LedgerGetBalance", "查询链上地址余额（缺省 = 自己钱包的地址）", cat,
                sp => CreateTool<LedgerWalletTool>(sp, "LedgerGetBalance"), ToolApprovalLevel.Safe);
            reg.Register("LedgerTransfer", "链上转账（签名提交——不可逆），金额单位为币数（支持 8 位小数）", cat,
                sp => CreateTool<LedgerWalletTool>(sp, "LedgerTransfer"), ToolApprovalLevel.Dangerous);
            reg.Register("LedgerDepositToExchange", "充值到交易所（转账到 custody 托管地址、memo=交易所账号——桥自动补铸现货持仓）", cat,
                sp => CreateTool<LedgerWalletTool>(sp, "LedgerDepositToExchange"), ToolApprovalLevel.Confirm);
        });
        return builder;
    }

    private static AIFunction CreateTool<T>(IServiceProvider sp, string method) where T : class
    {
        var tool = sp.GetRequiredService<T>();
        var m = typeof(T).GetMethod(method, BindingFlags.Public | BindingFlags.Instance)
            ?? throw new InvalidOperationException($"Tool method '{method}' not found on {typeof(T).Name}.");
        return AIFunctionFactory.Create(m, tool);
    }
}
