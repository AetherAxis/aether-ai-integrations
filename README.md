# Aether.AI.Integrations

AI 工具适配器仓——把能力以 `AIFunction` 工具形式接入 ⑫ aether-ai 平台：宿主能力（文件/搜索/HTTP/命令）+ 领域模块（浏览器、终端）；其余业务域（链/交易/CTP/…）按需再加（AI 线本质可调任意工具，业务适配回归时按「一个领域一个模块」惯例新建）。**每个适配独立项目**（一个适配器一个 csproj，避免一个总包拉入全部依赖）；适配器只注册工具、**调用业务入口，不复制业务规则**；目录/凭据全部宿主侧接线（不进 LLM 上下文）。

aether-ai 是纯平台层（大脑接缝 + Harness + 工具池契约），**不放任何具体工具实现**：工具是「业务/宿主 + AI」，归属本仓。

## 组成

| 项目 | 说明 |
|---|---|
| `src/Aether.SystemTools.AI` | 宿主能力工具 16 个：文件（`ReadFile` `WriteFile` `EditFile` `LineEditFile` `DeleteFile` `MoveFile` `DiffFile` `ApplyPatch` `ApplyPatchWithDelete`）、搜索（`Grep` `Glob` `ExploreDirectory`）、网络（`FetchUrl`、**`WebSearch`**——Bing HTML 抓取，失败退 DuckDuckGo，纯 HTTP 无浏览器）、命令（`ExecuteCommand` `ExecuteShellCommand`）。`AddSystemTools(builder)` / `AddSystemTools(builder, includeCommandTools: false)`（可移植 14 个） |
| `src/Aether.Browser.AI` | **浏览器工具 + Playwright 会话服务**（7 工具，2026-10-29 迁入）：观测/凭证四件 `browser_open` / `browser_get_cookies` / `browser_get_requests` / `browser_close` + **操作三件套** `browser_read`（读页面：URL/标题/正文 innerText，~2 万字截断）/ `browser_click`（点元素，Playwright selector：`text=` / `role=` / `css=` / `xpath=`）/ `browser_type`（填输入框，fill 语义，可带回车）。Playwright headed Chromium **独立 OS 窗口**（用户手动登录、全程可见），服务记录该会话的 HTTP 请求流（DevTools Network 式，1000 条上限 / body 32k 截断 / 150ms 去抖）；工作流 = `browser_open` 登录页 → 用户登录 → agent `browser_read` 看页 → `browser_click`/`browser_type` 操作 → `browser_get_cookies` 拿 curl 直接用的 CookieHeader 走 API。持久 profile 读宿主设的 `AetherAIOptions.BrowserProfileDir`（登录一次复用）；channel 未指定自动探测本机 Chrome/Edge（免 playwright install）。**双启动模式**（`BrowserLaunchMode`，宿主策略 `DefaultLaunchMode`——OpenSession 可显式传，agent 工具不暴露）：`Launch`（默认，Playwright 启动，`navigator.webdriver=true` 明牌）/ `Attach`（本机浏览器普通启动 + 调试端口 CDP 连入，`webdriver=false`——2026-10-29 探针实证；行为层仍可被识别，不承诺隐身）。**桌面专属模块**（⑭ 首例）：全部工具 `DesktopOnly`；`get_cookies` / `click` / `type` = Confirm（动凭证、动已登录会话须过审批）。`AddBrowserTools(builder)`。**面板 UI 不在本仓**（⑮ 桌面 head 的 `Browser/`，经 `AddBrowserPanel(dataDir)` 组合——含 browser.json 模式开关）——工具与面板共用同一 service 单例、互不感知 |
| `src/Aether.Terminal.AI` | **集成终端工具**（`run_in_terminal`，2026-10-29 迁入）：在**可见**终端面板执行命令——真实 PTY / 完整登录 env（含 .zshrc）/ 可观察可中断（超时/取消后进程继续在终端跑，可手动 Ctrl-C）。与 `ExecuteShellCommand` 分工：后者静默管道拿干净输出供判断，本工具走可见终端（需 TTY / 长任务观察介入 / 完整环境时用）。捕获机制 = OSC 8331 哨兵回显退出码 + ANSI 剥离（实现在 ⑮）；返回输出头尾截断（6000 字符，失败 tail 优先）。`AddTerminalTools(builder)`——**本模块只注册工具**：`ITerminalService` 的 impl 是 UI 耦合的（持 RoyalTerminal 控件/PTY），归宿主 UI 仓（⑮ 桌面 head `Terminal/`，`AddTerminalPanel` 一并接线） |

测试（`tests/Aether.SystemTools.AI.Tests`，10 例）：DI 元数据与审批等级、命令工具开关、真实文件系统写/读/编辑/Grep/Glob/目录浏览/删除、`Grep`/`Glob` 空 `path` 拒绝回退 CWD、`ToolRegistry` 解析出的 `AIFunction` 按参数名绑定调用（MAF 调工具的已知陷阱）、**规则大脑经 Harness 调 `ReadFile` 的完整闭环**、命令工具真跑子进程、WebSearch 结果解析 ×3（Bing 标题摘要配对/上限与站内跳转丢弃、DuckDuckGo 跳转链解开）。

测试（`tests/Aether.Browser.AI.Tests`，13 例，fake `IBrowserService` 不起 Playwright——真链路见 ⑮ 桌面 head 的 `--browser-smoke`）：`browser_open` 有活跃会话则导航/无则新建、`get_cookies` 无活跃会话口径与结果透传、`get_requests` 按 URL 过滤 / 只看错误 / 限量与叠加、`browser_close` 无会话口径与关闭、**操作三件套**（无活跃会话口径 / click 委托与落点 URL 透传 / type 透传 selector·text·pressEnter / read 返回页面内容）。⑮ 侧另有接线断言（桌面注册 7 个 / 手机连注册都不发生 / 误挂载被宿主能力兜底——见 ⑮ `tests/Aether.AI.App.Tests`）。

示例宿主（`samples/Aether.SystemTools.AI.Sample`）：规则大脑实跑 `ReadFile` / `ExploreDirectory` 并打印结果，`dotnet run` 即可；断言在 `tests/Aether.SystemTools.AI.Tests` 中，CI 两者都跑。

### 系统工具审批与宿主能力

| 工具 | 审批等级 | 宿主能力 |
|---|---|---|
| `ReadFile` `WriteFile` `EditFile` `LineEditFile` `Grep` `Glob` `ExploreDirectory` `FetchUrl` `DiffFile` `ApplyPatch` | Safe | 两端 |
| `MoveFile` | Confirm | 两端 |
| `DeleteFile` `ApplyPatchWithDelete` | Dangerous | 两端 |
| `ExecuteCommand` `ExecuteShellCommand` | Dangerous | 仅桌面（spawn 子进程） |
| `browser_open` `browser_get_requests` `browser_close` `browser_read` | Safe | 仅桌面（headed 浏览器） |
| `browser_get_cookies` | Confirm | 仅桌面（cookie = 登录凭证，会进 agent 上下文） |
| `browser_click` `browser_type` | Confirm | 仅桌面（在已登录会话上执行操作——审批卡可勾「总是允许」减摩擦） |
| `run_in_terminal` | Dangerous | 仅桌面（真实 PTY——在可见集成终端执行任意命令，对齐 ExecuteShellCommand） |

`Grep` / `Glob` 要求显式 `path`（空路径直接报错，不回退进程 CWD——桌面 app 从 `.app` 启动时 CWD 是 `/`），并拒绝把系统根目录当搜索根。

**命令工具的上游替代（暂不可用）**：MAF 主仓里有 `Microsoft.Agents.AI.Tools.Shell`——`LocalShellExecutor`
（`ShellPolicy` deny/allow 正则 + 超时 + 输出上限）与 `DockerShellExecutor`（network=none、只读根、
pids/内存限制）。2026-10-24 核对：该包**未发布到 nuget.org**（`dotnet package search --exact-match` 无结果），
所以这里仍是手写实现（已有超时、进程树清理、stdout/stderr 重定向与 stdin 关闭）。上游一旦发布，
应把 `CommandExecutorTool` / `ShellCommandTool` 换成 `ShellExecutor`，工具名与审批等级保持不变。

## 跨仓依赖（工作区 ProjectReference，兄弟仓并排于 AetherAxis 根下）

```
aether-ai-integrations/src/Aether.SystemTools.AI
  → ../../aether-ai/src/Aether.AI.Hosting      （AetherAIBuilder / ConfigureToolRegistry / ToolRegistry；工具池、审批、宿主过滤全在平台层）

aether-ai-integrations/src/Aether.Browser.AI
  → ../../aether-ai/src/Aether.AI.Hosting      （同上；+ Microsoft.Playwright / CommunityToolkit.Mvvm 两个包。桌面专属——
                                               移动宿主不挂载即零成本，误挂载由 HostCapability 兜底过滤）

aether-ai-integrations/src/Aether.Terminal.AI
  → ../../aether-ai/src/Aether.AI.Hosting      （同上；+ CommunityToolkit.Mvvm。桌面专属；只含工具面——ITerminalService
                                               的 impl 是 UI 耦合的（持 RoyalTerminal 控件/PTY），归宿主 UI 仓 ⑮ 桌面 head
                                               Terminal/ 注册（AddTerminalPanel），宿主不注册则工具首次调用才解析失败）
```

CI 按拓扑 checkout 兄弟仓（aether-ai）。

## 范围决策记录（2026-10-23，逐个迁——「只迁保留产品需要的适配器」）

- **Ledger**：✅ 2026-10-23 迁入 → **2026-10-28 移除**——AI 线去业务化（用户定调：AI 线本质可调任意工具，业务适配未来按需再加；钱包能力归虚拟币线——⑯ 钱包 CLI / ⑰ 钱包 UI）。源码与测试在 git 历史，恢复时按「一个领域一个模块」惯例重建。
- **Ctp / Trading / Quotes**：⏸ 暂缓——桌面组合根**未注册**这三个工具集；唯一消费者是 **Stockapp**（A 股 app，`AddQuotesTools().AddCtpTools().AddTradingTools()`——属 MIGRATION.md §6「范围未定」桶，未经范围决定不机械建仓）。另：Ctp 适配器依赖 `Acme.Ctp.Application` 门面（ICtpTradingService）——该门面未迁（ctp-net-client 暴露的是 ICtpSession 直面），迁它需按 Ctp.Net 契约重写工具层；Quotes 依赖 `Acme.Quotes.Application` / `Acme.Securities.Application`（均未迁）。待 P3 桌面 AI 功能明确需求后逐个评估。
- **Agents**（ai/Agents）：⏸ 暂缓——通用执行机制与业务适配需分开归属（MIGRATION.md §6），且依赖 Notes.Domain（独立仓已删除，需时从 monorepo `business/Notes` 重取）——随桌面批次定。
- **SystemTools**：✅ 2026-10-23 迁入（用户决定：工具本质是「业务/宿主 + AI」，不留在平台层，也不再留 Desktop 内部）。迁入的这份**不依赖窗口/终端/浏览器生命周期**——只做文件系统、`Grep`/`Glob`、`IHttpClientFactory` 抓取与子进程执行，因此可无头运行；命令类工具标 `DesktopOnly`，手机端由 `ToolRegistry` 自动过滤。
- **AppAudit**：✅ 2026-10-27 迁入 → **2026-10-30 退休移除**（用户定调：应用审查页（⑮ 侧 10-29）撤除后，
  agent 用 SystemTools 的 **WebSearch/FetchUrl** 直接查证据判定即可——专用任务板/3 工具/skill 播种不再需要。
  历史数据 `app-audit.json` 与已种的 SKILL.md 留在用户数据目录不受影响。源码与 31 例测试在 git 历史，
  恢复按「一个领域一个模块」惯例重建）。
- **Browser**：✅ 2026-10-29 迁入（自 monorepo `apps/Desktop/Acme.Desktop.Shared/Shell/Browser`——BrowserService/BrowserTool/接口原样 port）——用户定调「**提供给 AI 调用的工具就进 ⑭**」。据此切法：工具 + Playwright 服务（UI-free）落 ⑭，面板 View/VM 留 ⑮ 桌面 head（`AddBrowserPanel` 组合两边）。**⑭ 首个桌面专属模块**：工具全 `DesktopOnly`——这不违背「适配器可被任意宿主复用」，复用方 = 任意**桌面**宿主（CLI/别的桌面 app 引 `AddBrowserTools` 即有自己的浏览器工具，不需要 ⑮ 的面板）。这条同时把「依赖浏览器生命周期 ⇒ 留桌面侧」的旧口径细化为：**工具面不依赖 UI 的进 ⑭（桌面专属模块），宿主窗口耦合的面板留 UI 仓**。
- **ApkParser**：⏸ 留 Desktop 内部（依赖窗口/浏览器生命周期——MIGRATION.md §6 明确先留 Desktop 内部项目；与 Browser 不同类：其解析流程本身耦合宿主窗口，切不出 UI-free 的工具面）。

## 构建与测试

```bash
dotnet test aether-ai-integrations.slnx    # 30 例：系统工具 ×10 + 浏览器 ×13 + 终端 ×7
dotnet run --project samples/Aether.SystemTools.AI.Sample   # 示例：规则大脑调真实系统工具
```

## 来历

2026-10-23 拆自 Aether monorepo `ai/Ledger/Acme.Ledger.AI` + `tests/Aether.Ledger.Tests/LedgerWalletToolTests`（源提交 `8686503`，P2 次批；`Acme.Ledger.AI` → `Aether.Ledger.AI` 机械改名；LedgerNodeLoopback 夹具与 LedgerHarness.Wallet helper 为测试复制件——internal 夹具无法跨仓复用，上游改动须手动同步）。

**2026-10-28**：`Aether.Ledger.AI`（src + tests）随「AI 线去业务化」移除——上段来历保留作历史；需要恢复时从 git 历史取回（2026-10-23 迁入至 2026-10-27 参数化之间的提交），或按上文「范围决策记录」重建。

`Aether.SystemTools.AI` 同日从 monorepo `ai/SystemTools/Acme.SystemTools.AI` 迁入（`Acme.SystemTools.AI` → `Aether.SystemTools.AI` 机械改名，工具源码逐字随迁），中途曾落在 aether-ai 内，同批按「工具 = 业务/宿主 + AI」的归属原则移入本仓。完整决策记录见工作区根 `MIGRATION.md`。
