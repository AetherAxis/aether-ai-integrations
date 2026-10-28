# Aether.AI.Integrations

AI 工具适配器仓——把业务能力（链/交易/CTP/…）与宿主能力（文件/搜索/HTTP/命令）以 `AIFunction` 工具形式接入 ⑫ aether-ai 平台。**每个适配独立项目**（一个适配器一个 csproj，避免一个总包拉入全部依赖）；适配器只注册工具、**调用业务入口，不复制业务规则**；节点地址/种子文件/凭据全部宿主侧接线（工具只做「签名在调用方」——种子不出宿主进程，不进 LLM 上下文）。

aether-ai 是纯平台层（大脑接缝 + Harness + 工具池契约），**不放任何具体工具实现**：工具是「业务/宿主 + AI」，归属本仓。

## 组成（首迁：链上钱包适配器）

| 项目 | 说明 |
|---|---|
| `src/Aether.SystemTools.AI` | 宿主能力工具 16 个：文件（`ReadFile` `WriteFile` `EditFile` `LineEditFile` `DeleteFile` `MoveFile` `DiffFile` `ApplyPatch` `ApplyPatchWithDelete`）、搜索（`Grep` `Glob` `ExploreDirectory`）、网络（`FetchUrl`、**`WebSearch`**——Bing HTML 抓取，失败退 DuckDuckGo，纯 HTTP 无浏览器）、命令（`ExecuteCommand` `ExecuteShellCommand`）。`AddSystemTools(builder)` / `AddSystemTools(builder, includeCommandTools: false)`（可移植 14 个） |
| `src/Aether.Ledger.AI` | 链上钱包工具（**AI agent 经济的操作面**，⑮ 手机/桌面共享接线）：`LedgerGetInfo` / `LedgerGetBalance`（Safe）/ `LedgerTransfer`（**Dangerous**——链上转账不可逆）/ `LedgerDepositToExchange`（Confirm——充值到交易所 custody，桥自动补铸场内现金）。`AddLedgerTools(builder, walletSeedFilePath, nodeAddress, custodyAddress)` 经 aether-ai 的 `ConfigureToolRegistry` 注册；地址解析优先级 **参数（宿主配置）&gt; env（`AETHER_LEDGER_NODE_ADDR` / `AETHER_LEDGER_CUSTODY_ADDRESS`）&gt; 默认 `127.0.0.1:17805`**——手机端设不了 env，必须由宿主传进来；种子文件每次调用现读（换文件免重启，首个签名操作自动生成并 `0600` 落盘） |

| `src/Aether.AppAudit.AI` | **应用审查任务板 + 工具**（`ListAuditTasks` / `SaveAppVerdict` / `ReadAppVerdict`，全 Safe）。模块自带数据模型、`app-audit.json` 落盘、发布/取任务/写结论的状态机，以及判定口径 skill（`app-audit/SKILL.md`，首次运行种进宿主 skills 目录，改口径只改 Markdown）。`AddAppAuditTools(builder, dataDirectory, skillsDirectory)`。**关键设计：模块里没有判定逻辑**——页面/宿主只发布任务，怎么判（搜几次、抓不抓正文、要不要多轮）由 agent 自己决定，工具只负责把结论写回；所以任何宿主复用这个模块就有一套自己的应用审查，不依赖任何 UI 仓 |

测试（`tests/Aether.Ledger.AI.Tests`，17 例）：5 例**真链回环**随迁自 monorepo（进程内 LedgerNode + 签名转账/充值路径 + 种子文件生命周期——跨仓直连 ① aether-ledger 引擎）+ 1 例 DI 接线钉子（`AddLedgerTools` → ToolRegistry 四工具与审批等级）+ **9 例节点/托管地址配置**（优先级 参数&gt;env&gt;默认、`host:port`/URL/路径/IPv6 写法宽容、种子文件 `0600`）+ 1 例充值未配 custody 的指引文案。

测试（`tests/Aether.AppAudit.AI.Tests`，31 例）：任务板状态机（导入=待发布、发布默认跳过已判过、勾复核才重发、取任务→写回→出池、单条重发只影响那一条、旧版中途阶段算待处理、按发布顺序 + `limit` 截断）、写入纪律（重复导入不覆盖结论、落盘读回、坏 JSON 当空表、重判保留历史且当前取最近一次）、工具链路（取任务带清单网址与上次结论、未导入的名字也能写、非法 verdict 拒绝、同义词/中文、无任务明说）、skill（落盘去 frontmatter、不改用户正文、旧默认值升级、文件缺失回退内置）。

测试（`tests/Aether.SystemTools.AI.Tests`，7 例）：DI 元数据与审批等级、命令工具开关、真实文件系统写/读/编辑/Grep/Glob/目录浏览/删除、`Grep`/`Glob` 空 `path` 拒绝回退 CWD、`ToolRegistry` 解析出的 `AIFunction` 按参数名绑定调用（MAF 调工具的已知陷阱）、**规则大脑经 Harness 调 `ReadFile` 的完整闭环**。

示例宿主（`samples/Aether.SystemTools.AI.Sample`）：规则大脑实跑 `ReadFile` / `ExploreDirectory` 并打印结果，`dotnet run` 即可；断言在 `tests/Aether.SystemTools.AI.Tests` 中，CI 两者都跑。

### 系统工具审批与宿主能力

| 工具 | 审批等级 | 宿主能力 |
|---|---|---|
| `ReadFile` `WriteFile` `EditFile` `LineEditFile` `Grep` `Glob` `ExploreDirectory` `FetchUrl` `DiffFile` `ApplyPatch` | Safe | 两端 |
| `MoveFile` | Confirm | 两端 |
| `DeleteFile` `ApplyPatchWithDelete` | Dangerous | 两端 |
| `ExecuteCommand` `ExecuteShellCommand` | Dangerous | 仅桌面（spawn 子进程） |

`Grep` / `Glob` 要求显式 `path`（空路径直接报错，不回退进程 CWD——桌面 app 从 `.app` 启动时 CWD 是 `/`），并拒绝把系统根目录当搜索根。

**命令工具的上游替代（暂不可用）**：MAF 主仓里有 `Microsoft.Agents.AI.Tools.Shell`——`LocalShellExecutor`
（`ShellPolicy` deny/allow 正则 + 超时 + 输出上限）与 `DockerShellExecutor`（network=none、只读根、
pids/内存限制）。2026-10-24 核对：该包**未发布到 nuget.org**（`dotnet package search --exact-match` 无结果），
所以这里仍是手写实现（已有超时、进程树清理、stdout/stderr 重定向与 stdin 关闭）。上游一旦发布，
应把 `CommandExecutorTool` / `ShellCommandTool` 换成 `ShellExecutor`，工具名与审批等级保持不变。

## 跨仓依赖（工作区 ProjectReference，兄弟仓并排于 AetherAxis 根下）

```
aether-ai-integrations/src/Aether.Ledger.AI
  → ../../aether-ai/src/Aether.AI.Hosting      （AetherAIBuilder / ConfigureToolRegistry / ToolRegistry）
  → ../../aether-ledger/src/Aether.Ledger       （LedgerClient / LedgerWallet / 签名）

aether-ai-integrations/src/Aether.SystemTools.AI
  → ../../aether-ai/src/Aether.AI.Hosting      （同上；工具池、审批、宿主过滤全在平台层）

aether-ai-integrations/src/Aether.AppAudit.AI
  → ../../aether-ai/src/Aether.AI.Hosting      （同上；本模块**不引任何 UI/应用仓**，可被任意宿主复用）
```

CI 按拓扑 checkout 兄弟仓（aether-ai + aether-ledger，同 aether-desktop 模式）。

## 范围决策记录（2026-10-23，逐个迁——「只迁保留产品需要的适配器」）

- **Ledger**：✅ 本批迁入——桌面组合根已注册（`DesktopCompositionExtensions` → `AddLedgerTools`），依赖仓全部已迁。
- **Ctp / Trading / Quotes**：⏸ 暂缓——桌面组合根**未注册**这三个工具集；唯一消费者是 **Stockapp**（A 股 app，`AddQuotesTools().AddCtpTools().AddTradingTools()`——属 MIGRATION.md §6「范围未定」桶，未经范围决定不机械建仓）。另：Ctp 适配器依赖 `Acme.Ctp.Application` 门面（ICtpTradingService）——该门面未迁（ctp-net-client 暴露的是 ICtpSession 直面），迁它需按 Ctp.Net 契约重写工具层；Quotes 依赖 `Acme.Quotes.Application` / `Acme.Securities.Application`（均未迁）。待 P3 桌面 AI 功能明确需求后逐个评估。
- **Agents**（ai/Agents）：⏸ 暂缓——通用执行机制与业务适配需分开归属（MIGRATION.md §6），且依赖 Notes.Domain（独立仓已删除，需时从 monorepo `business/Notes` 重取）——随桌面批次定。
- **SystemTools**：✅ 2026-10-23 迁入（用户决定：工具本质是「业务/宿主 + AI」，不留在平台层，也不再留 Desktop 内部）。迁入的这份**不依赖窗口/终端/浏览器生命周期**——只做文件系统、`Grep`/`Glob`、`IHttpClientFactory` 抓取与子进程执行，因此可无头运行；命令类工具标 `DesktopOnly`，手机端由 `ToolRegistry` 自动过滤。
- **ApkParser**：⏸ 留 Desktop 内部（依赖窗口/浏览器生命周期——MIGRATION.md §6 明确先留 Desktop 内部项目）。

## 构建与测试

```bash
dotnet test aether-ai-integrations.slnx    # 13 例：真链回环 ×5 + Ledger 接线钉子 ×1 + 系统工具 ×7
dotnet run --project samples/Aether.SystemTools.AI.Sample   # 示例：规则大脑调真实系统工具
```

## 来历

2026-10-23 拆自 Aether monorepo `ai/Ledger/Acme.Ledger.AI` + `tests/Aether.Ledger.Tests/LedgerWalletToolTests`（源提交 `8686503`，P2 次批；`Acme.Ledger.AI` → `Aether.Ledger.AI` 机械改名；LedgerNodeLoopback 夹具与 LedgerHarness.Wallet helper 为测试复制件——internal 夹具无法跨仓复用，上游改动须手动同步）。

`Aether.SystemTools.AI` 同日从 monorepo `ai/SystemTools/Acme.SystemTools.AI` 迁入（`Acme.SystemTools.AI` → `Aether.SystemTools.AI` 机械改名，工具源码逐字随迁），中途曾落在 aether-ai 内，同批按「工具 = 业务/宿主 + AI」的归属原则移入本仓。完整决策记录见工作区根 `MIGRATION.md`。
