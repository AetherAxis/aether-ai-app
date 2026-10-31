# Aether.AI.App 设计说明

> 本文件写「怎么设计 / 为什么这么设计 / 还能往哪升级」。工作区边界与迁移决策以根 `MIGRATION.md` 为准。
> （2026-10-31 补齐——此前本仓只有 README，是全工作区最后一个补 DESIGN 的仓。）

## 1. 定位与边界

⑫ `aether-ai` 之上的统一 UI App，方案A：**Avalonia 单代码库**（桌面 + Android + iOS 三 TFM 共享同一份
View / ViewModel / Core）。消费者层——引 ⑩（布局引擎）⑫（平台）⑭（工具），不产能力；能力取舍全在
组合根 `App.BuildProvider` 一处。

三形态分派（`App.OnFrameworkInitializationCompleted`）：

| 形态 | 壳 | 审批 | 主题 |
|---|---|---|---|
| 桌面（ClassicDesktop） | ⑩ `WorkbenchWindow` 四 tab（聊天 / 浏览器 / 终端 / 设置）+ nav rail + 布局持久化 | 模态对话框 | 4 套可切（含 aurora） |
| Android（IActivity） | 同一份 `ChatView` 直接作根视图（全屏） | 内嵌 bottom sheet（`MobileApprovalService`） | 固定浅色 overlay |
| iOS（ISingleView） | 同上 | 同上 | 同上 |

移动「壳」（Page 族导航 / 底部 tab / 完整设置页）属 M3——当前移动分支是可用的全屏聊天预览形态，
不是最终形态。

## 2. 项目组成

| 项目 | 职责 |
|---|---|
| `src/Aether.AI.App.Core` | 无 UI 引擎层：`ChatSessionEngine`（流式渲染）、`AgentRunner`（会话桥）、`ChatTranscriptPersister` / `ConversationHistoryLoader`（typed 事件流持久化与重建）、`CustomAgentStore`（agents.json）、`CrashGuard`（进程级崩溃防线） |
| `src/Aether.AI.App` | 三端共享 View / VM + 组合根 `App.BuildProvider`（宿主能力判定、工具注册、DI 全在此）；`IUiDispatcher` / `IApprovalDialogService` 端口的桌面实现 |
| `src/Aether.AI.App.Desktop` | 桌面 head：`Program`（无头巡检入口）+ **桌面专属面板切片** `Browser/`（网络观测）与 `Terminal/`（RoyalTerminal 多标签 PTY）+ `HeadToolsConfigure` 钩子（⑭ 浏览器/终端工具的注册点） |
| `src/Aether.AI.App.Android` / `.iOS` | 移动 head：各 2 文件——声明 `HeadHost = MobileOnly` + 起 Avalonia，零其他逻辑 |
| `tests/` ×2 + `spike/` ×4 | VM 级集成（走真实组合根）+ Core 单测；spike 是 M0 原型，仅考古 |

## 3. 关键设计决策

### 3.1 UI：一份 View/VM，三种壳（方案A）

- **决策**：聊天 UI 只写一份（`ChatView` / `ChatViewModel` / Core 全在共享层）；形态差异经端口吸收
  （`IApprovalDialogService`：桌面模态 vs 移动 bottom sheet；`IUiDispatcher`：测试注入内联实现）。
  桌面面板（浏览器/终端）是**桌面 head 里的切片目录**，不与三平台 head 并列成新项目。
- **VM 拆分（2026-10-31，为功能增长铺路）**：输入卡头部的两个选择器（agent 目标 + LLM 档案）自
  ChatViewModel 拆出为 `ChatPickersViewModel`（732 → 589 行）——未来「会话模式切换 / agent 组合 / 更多
  档案动作」落这里，不再往 ChatViewModel 里堆。交互全走三个事件（`SelectionChanged` / `TargetChanged` /
  管理窗意图），恢复会话的程序化设值走 `SetTargetSilent`（不开新会话）；picker 只认 `IAgentCatalog`
  端口，agent 存储（`CustomAgentStore.Changed`）由 ChatViewModel 转发进来。
- **理由**：聊天 UI 的绝大部分与设备形态无关；真正形态相关的东西（窗口、模态、安全区）本来就只有
  head 知道。⑩ 的布局能力（nav rail / 持久化 / 主题）只在桌面分支出场，移动不背。
- **代价**：移动端暂无设置页与外观切换（能力面经「模型设置 → 能力」页签可达，M3 补壳）；⑩ 的能力
  手机用不上但仍在闭包里（轻量，可接受）。

### 3.2 桌面专属能力：注册期过滤，不是运行期护栏

- **决策**：宿主能力在 **DI 组合期**一刀切——`App.HeadHost` 显式声明（桌面 `Program.Main`、移动
  `MainActivity` / iOS `Main` 第一行），`BuildProvider` 据此决定：命令工具挂不挂
  （`includeCommandTools`）、⑭ 浏览器/终端模块挂不挂（`HeadToolsConfigure` 只在桌面 head 设）、
  Roslyn 编译器挂不挂（`ServicesConfigure` 只在桌面 head 设）、MCP stdio 连不连（宿主过滤）。
  ⑫ `ToolRegistry` 再按 `HostCapability` 过滤一遍兜底（误挂载也不炸）。
- **理由**：移动端「连注册都不发生」优于「注册了运行期拦截」——Playwright / RoyalTerminal / Roslyn /
  FCS 直接不进移动闭包（iOS 包体与沙盒约束：禁 fork、AOT 裁剪），误配置在组合期就被吃掉。手机上选
  代码 agent 会明确报「未注册 C# 编译器」，不假装成功。
- **代价**：能力矩阵的判定散在组合根一处 + ⑭ 模块声明两处，靠 ⑮ 侧 `*ToolWiringTests`
  （桌面注册 N 个 / 手机连注册都不发生）锁住不漂移。

### 3.3 桌面端崩溃防线（2026-10-31）：三层纵深

**为什么需要**：两个实锤的崩溃通道——① MAF agent 循环深处 `ConfigureAwait(false)` 丢 UI 上下文，
审批解析器跑在线程池线程上创建 NSWindow → NSException → SIGABRT（10-29 .ips 栈实锤，10-30 修：
`ApprovalDialogService.ShowAsync` 先 marshal 再碰控件）；② CommunityToolkit 8.4.2 的
`AsyncRelayCommand` 在按钮**同步 `Execute`** 路径上把 delegate 异常经 `AwaitAndThrowIfFailed`
重抛到线程池（本地最小复现实测）→ 未处理异常 → 进程死，而 `SendInputAsync` 主链路此前零 try/catch。

| 层 | 落点 | 兜什么 |
|---|---|---|
| ① VM 最后防线 | `ChatViewModel.SendInputAsync` 全体 try/catch | 任何漏网异常收敛为 `[轮次异常：…]` 可见错误段 + turn 转 Error + 清运行态，零外逃；Initialize / 恢复会话 / 接受计划同批加固 |
| ② 引擎层 | `ChatSessionEngine`：flush 定时器 `flusherClosed` 关闸 + 同临界区保序 + 确定性 drain（替换 `Delay(60)` hack，杜绝 Change-after-Dispose 的 ODE）；晚到回调 `RemoveAt(-1)` 崩溃面堵死；收尾失败也清运行态 | 流式竞态、晚到回调 |
| ③ **端口契约（2026-10-31 机械化）** | `IUiDispatcher.Post` 的实现**必须吞掉 lambda 内异常**（记黑匣子）——`AvaloniaUiDispatcher` 落地；View/桌面切片无法注入端口的零星回调走静态 `UiThread.SafePost`（同契约） | **一切 fire-and-forget 上屏回调**——防线从「调用点自觉包 try/catch」升级为「走端口即自动安全」，新代码零成本继承；测试 dispatcher 故意不吞（严格模式暴露 bug） |
| ④ CrashGuard | `TaskScheduler.UnobservedTaskException` → `SetObserved()`；`AppDomain.UnhandledException` → 记录 | fire-and-forget Task 的 finalizer 重抛；真兜不住时**留住栈** |

**黑匣子**：`<dataDir>/logs/app-log.txt`（4MB 截断重开）。macOS 的 .ips / 统一日志对 .NET 托管异常
**零痕迹**（10-30 崩溃实测两者皆空）——黑匣子是唯一现场，再崩先看它。桌面 `Program.Main` 第一行装
（无头路径也覆盖）、`App.Initialize` 幂等补装（移动 head）。

**边界与残余风险**：Avalonia 11 没有全局 UI 线程异常事件（不像 WPF 的 `DispatcherUnhandledException`），
fire-and-forget 的防线因此收在 `IUiDispatcher.Post` 端口契约里（上表③）——**新增上屏路径的唯一纪律是
「走端口」**（View 层用 `UiThread.SafePost`），不再需要记得自己包 try/catch；绕过端口直接调
`Dispatcher.UIThread.Post` 是唯一的漏网姿势（code review 盯）。native 层（Playwright 驱动 /
RoyalTerminal PTY / SQLite）的段错误 managed 防线防不住，黑匣子 + `--browser-smoke` /
`--view-recycle-test` 巡检是唯一手段。

### 3.4 会话并发模型

- **决策**：每会话独立 `ConversationRuntime`（Messages / Todos / RunCts / run-seq）；切会话只换指针
  **不取消背景 run**（背景会话继续往自己的 Messages 追加，切回即见续接）；同会话串行化
  （`AgentRunner` 内 per-conversation `SemaphoreSlim`）；删会话才取消。runtime 订阅常驻（切走不退订），
  背景完成的会话在侧栏正确熄「运行中」。
- **理由**：聊天产品的直觉是多会话并行；MAF 会话状态非原子，串行化防并发踩踏。
- **代价**：`ConversationRuntime` 是可变共享状态，跨线程触碰全靠 `_dispatcher` 编组纪律
  （引擎内已全走 `PostSafe` / `InvokeAsync`）。

### 3.5 持久化：typed 事件流，不是消息表

- **决策**：transcript 落 `AgentRun` + `AgentEvent`（append-only，CallId 在 ToolCall/ToolResult 写入时
  即配对），重建时经 `TurnRebuilder` 还原思考/工具/答案/计划卡；损坏时降级为空会话 + 状态栏提示
  （不崩进程）。
- **理由**：结构不变量（call↔result 配对）在写入时成立，跨重启恢复不需要 sanitize saga；
  序列化移后台线程（`Task.Run`），不与 token flush 争 UI。
- **代价**：事件 schema 演进要考虑旧数据重建兼容。

## 4. 运行与验证

```bash
dotnet test aether-ai-app.slnx          # 86 例 = App 76 + Core 10
dotnet run --project src/Aether.AI.App.Desktop -- --smoke          # 规则全链路 + 审批自恢复（无头）
dotnet run --project src/Aether.AI.App.Desktop -- --tools          # 当前宿主工具清单（桌面 24 / 手机 14）
dotnet run --project src/Aether.AI.App.Desktop -- --llm-smoke      # 真模型一轮流式（无档案 SKIP）
dotnet run --project src/Aether.AI.App.Desktop -- --browser-smoke  # Playwright 双模式五断言（会闪窗）
./scripts/publish-desktop.sh            # Release 自包含 .app（Finder 双击 / /Applications）
```

崩溃防线的专项回归：`CrashContainmentTests` 4 例（runner 炸 / 落库炸 / 晚到工具回调 / 黑匣子落盘）；
GUI 侧 `--approval-thread-test`（线程池发起审批 marshal 实证）、`--tab-switch-test`（轮次进行中切 tab
不中断）、`--view-recycle-test`（视图销毁不杀 PTY）。

## 5. 升级记录

| 日期 | 项 | 变更 | 依据 / 验证 |
|---|---|---|---|
| 2026-10-29 | 浏览器面板 | ⑭ `Aether.Browser.AI` 工具 + 桌面 head `Browser/` 面板 | `--browser-smoke` 五断言 |
| 2026-10-29 | 终端面板 | ⑭ `Aether.Terminal.AI` + 桌面 head `Terminal/`（控件/PTY 归服务所有，视图可销毁） | `--view-recycle-test` 四断言 |
| 2026-10-30 | 审批线程修复 | `ApprovalDialogService` marshal 到 UI 线程（NSWindow 非主线程创建 → SIGABRT 根因） | `--approval-thread-test` |
| 2026-10-30 | AppAudit 退休 | 页面（10-29）与 ⑭ 模块相继撤除；tab 序调整为 聊天→浏览器→终端→设置 | `--tools` 24 个 |
| 2026-10-31 | 崩溃防线 | 三层纵深（§3.3）+ CrashGuard 黑匣子 | `CrashContainmentTests` 4 例 + 86 全绿 + smoke |
| 2026-10-31 | 防线机械化 | `IUiDispatcher.Post` 端口契约化（实现吞异常）+ `UiThread.SafePost`；引擎 `PostSafe` 私有包装移除——新代码走端口即安全 | 全量 86 + smoke + `--tools` 24 |
| 2026-10-31 | VM 拆分 | agent 目标 + LLM 档案两个选择器拆出 `ChatPickersViewModel`（§3.1），ChatViewModel 732→589 行 | 全量 86（哨兵弹回/热刷新/恢复/演示链路原样过） |
| 2026-10-31 | DESIGN.md | 本文件补齐（全工作区最后一仓） | — |

## 6. 待升级项

| # | 优先级 | 项 | 现状证据 | 建议 | 代价 / 风险 |
|---|---|---|---|---|---|
| 1 | P1 | native 崩溃无防线 | Playwright / RoyalTerminal / SQLite 的段错误 managed 防不住；黑匣子只在托管层 | 崩溃复现时优先隔离三方件版本；巡检入口常跑 | 无法根治，只能缩面 |
| 2 | P2 | 绕过端口的裸 Post | 防线已收进 `IUiDispatcher.Post` 契约，但 `Dispatcher.UIThread.Post` 直调仍可绕过（View/桌面切片已收敛到 `UiThread.SafePost`，剩余风险是未来新代码） | code review 盯直调；升级 Avalonia 时关注全局异常钩子 | 低 |
| 3 | P2 | 移动壳 M3 | 移动端无 nav rail / 设置页 / 外观切换；全屏聊天是预览形态 | Page 族导航 + 底部 tab + 移动设置页 | 共享 View/VM 需按形态再分叉一部分 |
| 4 | P3 | 移动文件工具沙箱边界 | 文件类工具只能碰沙箱内路径，无用户可见目录选择器 | iOS `UIDocumentPicker` / Android SAF 接入 | 平台 API 进 head，端口先行 |
| 5 | P3 | spike/ 目录 | M0 原型已无用，仅考古 | M3 落地后删除 | 无 |
