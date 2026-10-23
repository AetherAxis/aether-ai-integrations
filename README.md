# Aether.AI.Integrations

AI 业务适配器仓——把业务能力（链/交易/CTP/…）以 `AIFunction` 工具形式接入 ⑫ aether-ai 平台。**每个业务适配独立项目**（一个适配器一个 csproj，避免一个总包拉入全部依赖）；适配器只注册工具、**调用业务入口，不复制业务规则**；节点地址/种子文件/凭据全部宿主侧接线（工具只做「签名在调用方」——种子不出宿主进程，不进 LLM 上下文）。

## 组成（首迁：链上钱包适配器）

| 项目 | 说明 |
|---|---|
| `src/Aether.Ledger.AI` | 链上钱包工具（**AI agent 经济的操作面**，桌面已接线）：`LedgerGetInfo` / `LedgerGetBalance`（Safe）/ `LedgerTransfer`（**Dangerous**——链上转账不可逆）/ `LedgerDepositToExchange`（Confirm——充值到交易所 custody，桥自动补铸场内现金）。`AddLedgerTools(builder, walletSeedFilePath)` 经 aether-ai 的 `ConfigureToolRegistry` 注册；节点地址 env `AETHER_LEDGER_NODE_ADDR`、custody env `AETHER_LEDGER_CUSTODY_ADDRESS`；种子文件每次调用现读（换文件免重启，首个签名操作自动生成落盘） |

测试（`tests/Aether.Ledger.AI.Tests`，6 例）：5 例**真链回环**随迁自 monorepo（进程内 LedgerNode + 签名转账/充值路径 + 种子文件生命周期——跨仓直连 ① aether-ledger 引擎）+ 1 例 DI 接线钉子（`AddLedgerTools` → ToolRegistry 四工具与审批等级，镜像 monorepo CtpWiringTests 模式）。

## 跨仓依赖（工作区 ProjectReference，兄弟仓并排于 AetherAxis 根下）

```
aether-ai-integrations/src/Aether.Ledger.AI
  → ../../aether-ai/src/Aether.AI.Hosting      （AetherAIBuilder / ConfigureToolRegistry / ToolRegistry）
  → ../../aether-ledger/src/Aether.Ledger       （LedgerClient / LedgerWallet / 签名）
```

CI 按拓扑 checkout 兄弟仓（aether-ai + aether-ledger，同 aether-desktop 模式）。

## 范围决策记录（2026-10-23，逐个迁——「只迁保留产品需要的适配器」）

- **Ledger**：✅ 本批迁入——桌面组合根已注册（`DesktopCompositionExtensions` → `AddLedgerTools`），依赖仓全部已迁。
- **Ctp / Trading / Quotes**：⏸ 暂缓——桌面组合根**未注册**这三个工具集；唯一消费者是 **Stockapp**（A 股 app，`AddQuotesTools().AddCtpTools().AddTradingTools()`——属 MIGRATION.md §6「范围未定」桶，未经范围决定不机械建仓）。另：Ctp 适配器依赖 `Acme.Ctp.Application` 门面（ICtpTradingService）——该门面未迁（ctp-net-client 暴露的是 ICtpSession 直面），迁它需按 Ctp.Net 契约重写工具层；Quotes 依赖 `Acme.Quotes.Application` / `Acme.Securities.Application`（均未迁）。待 P3 桌面 AI 功能明确需求后逐个评估。
- **Agents**（ai/Agents）：⏸ 暂缓——通用执行机制与业务适配需分开归属（MIGRATION.md §6），且依赖 Notes.Domain（→ ⑬ aether-knowledge）——随桌面批次定。
- **SystemTools / ApkParser**：⏸ 留 Desktop 内部（依赖窗口/终端/浏览器生命周期——MIGRATION.md §6 明确先留 Desktop 内部项目）。

## 构建与测试

```bash
dotnet test aether-ai-integrations.slnx    # 6 例：真链回环 ×5（进程内节点，无需外部环境）+ 接线钉子 ×1
```

## 来历

2026-10-23 拆自 Aether monorepo `ai/Ledger/Acme.Ledger.AI` + `tests/Aether.Ledger.Tests/LedgerWalletToolTests`（源提交 `8686503`，P2 次批；`Acme.Ledger.AI` → `Aether.Ledger.AI` 机械改名；LedgerNodeLoopback 夹具与 LedgerHarness.Wallet helper 为测试复制件——internal 夹具无法跨仓复用，上游改动须手动同步）。完整决策记录见工作区根 `MIGRATION.md`。
