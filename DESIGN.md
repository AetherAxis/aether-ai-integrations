# Aether.AI.Integrations 设计说明

> 本文件写「怎么设计 / 为什么这么设计 / 还能往哪升级」。工作区边界与迁移决策以根 `MIGRATION.md` 为准。

## 1. 定位与边界

本仓是「业务/宿主能力 → AI 工具」的适配层。平台契约（工具池/审批/宿主能力/Harness）在 ⑫ aether-ai；本仓只放工具实现与 DI 入口。

- 工具是「业务/宿主 + AI」，不属平台仓；aether-ai 零具体工具，宿主按需注册。
- 适配器只注册工具、调业务入口（如 BrowserService 的会话生命周期），不复制业务规则。
- 配置全在宿主侧：数据目录 / skills 目录 / 凭据由调用方传入，不进 LLM 上下文。
- **工具面 UI-free 即进本仓**（2026-10-29 修订口径）：不依赖宿主窗口/控件生命周期的能力（含「桌面专属模块」——全 `DesktopOnly`，移动端连注册都不发生）；宿主窗口耦合的面板归 UI 仓（⑮ 桌面 head `Browser/`、`Terminal/`）。

不在本仓：Ctp/Trading/Quotes（零消费者、门面未迁）、Agents、ApkParser（依赖未迁）、Ledger（2026-10-28 移除——AI 线去业务化，源码在 git 历史，恢复按同惯例重建）、AppAudit（2026-10-30 整体退休——页面（⑮ 侧 10-29）与模块相继撤除，判定改由 agent 直调 `WebSearch`/`FetchUrl`；源码在 git 历史）。

## 2. 项目组成

| 项目 | 职责 | 关键类型 | 出向依赖 |
|---|---|---|---|
| `src/Aether.SystemTools.AI` | 16 工具：文件 9、搜索 3、网络 2（`FetchUrl`/`WebSearch`）、命令 2 | `SystemToolsAIBuilderExtensions`、`FileSearchHelpers` | aether-ai Hosting、M.E.Http |
| `src/Aether.Browser.AI` | **桌面专属模块**：浏览器 7 工具（观测/凭证四件 + 操作三件套）+ Playwright headed 会话服务（双启动模式 Launch/Attach） | `BrowserService`、`BrowserTool` | aether-ai Hosting、Playwright、Mvvm |
| `src/Aether.Terminal.AI` | **桌面专属模块**：`run_in_terminal`（Dangerous）——在可见终端执行命令，OSC 8331 哨兵捕获；只含工具面，`ITerminalService` impl 在 ⑮ 桌面 head | `TerminalTool` | aether-ai Hosting、Mvvm |
| `tests/Aether.SystemTools.AI.Tests` | 10 例：审批元数据、命令开关、真实文件系统、空 path、参数绑定、规则大脑闭环、命令工具实跑、WebSearch 解析 ×3 | `SystemToolsTests`、`WebSearchTests` | 适配器 |
| `tests/Aether.Browser.AI.Tests` | 13 例：fake `IBrowserService`（7 工具行为 + 无会话口径 + 透传断言） | `BrowserToolsTests` | 适配器 |
| `tests/Aether.Terminal.AI.Tests` | 7 例：fake service（会话取用/新建、不等待模式、cd 前缀、结果映射、超时口径、长输出截断） | `TerminalToolsTests` | 适配器 |
| `samples/Aether.SystemTools.AI.Sample` | 大脑实跑 `ReadFile`/`ExploreDirectory` | `Program.cs` | 适配器、Rule |

## 3. 关键设计决策

### 3.1 工具归属本仓，一适配一项目，只注册不复制规则
- **决策**：工具全在本仓、每适配独立 csproj、无聚合包；工具只做参数解析与错误包装，规则交给业务入口（如 `BrowserService` 的会话管理）。
- **理由**：平台只留契约才能独立发版；依赖按适配隔离；领域规则只有一份实现。
- **代价**：跨仓引用与 CI 多仓 checkout；上游语义变更直接穿透到入参。

### 3.2 审批按不可逆性定，危险能力双闸
- **决策**：只读 Safe；可逆 Confirm（`MoveFile`、`browser_get_cookies`、`browser_click`/`browser_type`——动凭证/动已登录会话须过审批）；不可逆 Dangerous（`DeleteFile`、`ApplyPatchWithDelete`、命令工具、`run_in_terminal`，命令工具另加 `includeCommandTools`+`DesktopOnly`）。
- **理由**：等级由最了解副作用的适配器声明；iOS 禁止 fork，需整组跳过。
- **代价**：批量 patch 无法按内容定级，拆成两个入口。

### 3.3 搜索必须显式根路径
- **决策**：`Grep`/`Glob` 空 `path` 直接报错，拒绝系统根，跳过隐藏目录、符号链接与二进制。
- **理由**：桌面 app 的 CWD 可能是 `/`，隐式回退会扫整机。
- **代价**：错误文案指向不存在的 `GetCurrentProjectDirectory`（见 §6）。

### 3.4 桌面专属模块：注册期过滤，不是运行期护栏（2026-10-29）
- **决策**：浏览器 / 终端两模块全工具 `DesktopOnly`，且**由宿主 head 决定挂不挂**（⑮ 桌面 head 的 `HeadToolsConfigure` 钩子注册，移动 head 不设）——移动端「连注册都不发生」，Playwright / RoyalTerminal 不进移动闭包。
- **理由**：能力过滤发生在 DI 组合期（`ToolRegistry` 按 `HostCapability` 过滤 + 宿主只注册跑得了的），误挂载由平台层兜底；比「注册了再在运行期拦截」省掉整条依赖链。
- **代价**：工具与面板分居两仓（⑭ 工具面 / ⑮ 桌面 head 面板），靠同一 service 单例接线——接线正确性由 ⑮ 侧 `*ToolWiringTests` 断言。

## 4. 运行与验证

```bash
dotnet test aether-ai-integrations.slnx      # 30 例 = SystemTools 10 + Browser 13 + Terminal 7
dotnet run --project samples/Aether.SystemTools.AI.Sample
```

- 浏览器 13 例用 fake `IBrowserService`（不起 Playwright）；**真链路**（两种启动模式 + 进程清理）由 ⑮ 桌面 head 的 `--browser-smoke` 五断言覆盖。终端真 PTY 由 ⑮ 的 `--view-recycle-test` 端到端覆盖。
- CI checkout 两仓（本仓 + ⑫ aether-ai）并注入 GitHub Packages 凭据，Release build → test → sample。
- `Directory.Packages.props` 是唯一版本权威。

## 5. 升级记录（2026-10-24）

| 项 | 变更前 | 变更后 | 依据 / 验证 |
|---|---|---|---|
| `Microsoft.Extensions.Http` | 10.0.10 | 10.0.13 | `WebFetchTool` 经 `IHttpClientFactory` 取客户端 |
| `Microsoft.Extensions.DependencyInjection` | 10.0.11 | 10.0.13 | 宿主 DI 对齐 |
| 测试栈（Test.Sdk / runner / coverlet） | 17.14.1 / 3.1.4 / 6.0.4 | 18.11.0 / 4.0.1 / 10.1.0 | Test.Sdk 18、runner 4.0.1、coverlet 10.1.0 |
| `LedgerNodeLoopback` 种子回收 | 取不到返回 null，后续以空种子签名 | 取不到即抛异常，消息带日志路径 | 签名失败提前为启动期错误 |
| `LedgerWalletToolTests` 断言 | `NotEmpty` 作用于可空属性 | 先 `Assert.NotNull` 再 `NotEmpty` | 消除可空性告警 |

**2026-10-28**：`Aether.Ledger.AI`（src + tests）移除——AI 线去业务化（用户定调：AI 线本质可调任意工具，业务适配未来按需再加；钱包能力归虚拟币线 ⑯/⑰）。源码在 git 历史。

**2026-10-29**：`Aether.Browser.AI`（浏览器 7 工具 + Playwright 会话服务）与 `Aether.Terminal.AI`（`run_in_terminal`）自 monorepo 桌面线迁入——「工具面 UI-free 即进 ⑭（桌面专属模块），宿主窗口耦合的面板留 UI 仓」修订口径的首批落地（§1）。

**2026-10-30**：`Aether.AppAudit.AI`（src + tests）整体退休移除——应用审查页（⑮ 侧 10-29）与模块相继撤除，用户定调：判定用 SystemTools 的 `WebSearch`/`FetchUrl` 直接做，不需要专门任务板。源码在 git 历史，恢复按同惯例重建。

验证口径：`git diff` 仅 3 个文件（9 增 6 删），测试仍 13 项；本轮只读，未跑 build/test。`tests/Consumers`→`samples/` 重构早于本轮，未计入。

## 6. 待升级项

| # | 优先级 | 项目 | 现状证据 | 建议 | 代价 / 风险 |
|---|---|---|---|---|---|
| 1 | P1 | `FetchUrl` | 接受任意 http(s)，无域/私网限制 | 宿主注入允许域或拒绝 metadata 地址 | 可能挡住内网抓取 |
| 2 | P2 | `Grep`/`Glob` 文案 | 提示调用 `GetCurrentProjectDirectory`，但注册表无此工具 | 改为「项目根由宿主传入」或补注册 | 低 |
| 3 | P2 | 两个命令工具 | 手写；README 记录 `Microsoft.Agents.AI.Tools.Shell` 未发布（2026-10-24 核对） | 上游发布后换 `ShellExecutor`，名称与等级不变 | 需对齐超时与输出上限 |
| 4 | P2 | 两个 src csproj | 三级上溯引用，CI 需 checkout 兄弟仓 | 切 PackageReference + GitHub Packages | 版本联动与私有源鉴权 |
| 5 | P3 | 工具错误语义 | 22 处 `catch` 转 `Error`，取消与失败不可区分 | 取消时重抛或单列状态 | 需逐工具复核 |