# Aether.AI.App（M0 spike 阶段）

⑫ `aether-ai`（MAF 多大脑运行时）的统一 UI App——**方案A：Avalonia 单代码库，桌面 + Android + iOS**。
一套 AXAML / ViewModel / Core，按平台调整主题、控件尺寸、手势与 safe-area。

> 本仓当前处于 **M0 spike**：验证方案A 的六项硬前提（见下）。`spike/` 是可丢原型——
> M0 通过后按正式结构落 `src/`（App.Core 无 UI 引擎 + App 多 TFM + tests），届时重写本 README。

## 依赖（DAG：App 消费者层，与 ⑪ 同层）

- ⑩ `aether-workbench`——桌面壳/主题引擎/布局（spike 仅验证移动 TFM 可引用 + 主题清单可加载）
- ⑫ `aether-ai`——Hosting + Brains.Rule + Memory.Sqlite（**移动端闭包不带 Roslyn/FCS/LLM SDK 之外的重物**）
- 待接：⑭ `aether-ai-integrations`（业务工具，opt-in）——**⑬ `aether-knowledge` 已于 2026-10-27 归档**（知识对象能力未定，接 ⑬ 从待办移除）

## M0 验证清单（2026-10-24 全部通过 ✅）

| # | 项 | 结果 |
|---|---|---|
| 1 | Avalonia 12.1.4 三 TFM 构建通过 | ✅ net10.0 / net10.0-android / net10.0-ios；iOS 另过 **Release（全 AOT + ILLink trim）**——MEAI/MAF 反射面在裁剪下存活 |
| 2 | iOS 模拟器实跑 + 截图 | ✅ iPhone 16 Pro 模拟器（iOS 18.1）安装、启动、自动冒烟、截图留证 |
| 3 | safe-area API | ✅ iOS 实测 `Page.SafeAreaPadding = T59`（Dynamic Island 避让生效）；Android T0（无刘海 AVD + 系统栏由窗口层处理）——Android 侧 edge-to-edge 深化留 M1 |
| 4 | markdown 移动端渲染 | ✅ Markdown.Avalonia.Tight 12.0.0-a3 在 iOS/Android 均正常渲染 |
| 5 | ⑫ 全链路（Rule 大脑 + 工具 + SQLite native）在移动 TFM 跑通 | ✅ 两平台均 `done:echo:600000`（与 BrainMatrix 同构断言）；`SqliteConversationStore` native 载入成功 |
| 6 | ⑩ 引擎在移动 TFM 可加载 | ✅ 两平台均读出 `ThemeLoader.BuiltInThemes`（light/dark/dream/dream-dark） |

桌面无头冒烟：`dotnet run --project spike/Aether.AI.App.Spike.Desktop -- --smoke`（exit 0 = 全链路通过，已验证）。

### M0 遗留观察（不阻塞，进 M1 清单）

- `OperatingSystem.IsIOS()/IsAndroid()` 在 net10.0 共享程序集内实测返回 false（iOS/Android 运行时均如此）——M1 平台分支改用 Avalonia 运行时探测或 TFM 条件编译。
- Android safe-area 报 0：需 edge-to-edge 模式 + `IInsetsManager` 直读验证。
- iOS 软键盘行为、真机（AOT 发布档）验证：需实机，M1/M3 安排。
- **本机运维发现（两条，Android 验收时踩过两次）**：
  1. Claude Code 会话的 shell 连不上 adb 标准端口 5037（连接被丢弃）。绕法：`adb -P 5038 start-server` 后全程 `-P 5038`——注意要在模拟器起来**之后**再（重）启 5038 server，它只在启动时扫描 5554+。
  2. **本机启动无头 Android 模拟器（qemu + swiftshader）会弄坏登录会话的 CGS 显示列表**（`CGGetActiveDisplayList` → 0）：此后所有新起的 GUI 进程（含 Avalonia RenderTimer -6661）与 `screencapture` 全部失败，重登/显示器睡眠唤醒后才恢复。Android 验收完若要跑桌面 GUI，先确认 `swift disptest.swift` 报 count=1。

## M1（2026-10-24）：桌面聊天 MVP 落地 `src/`

```
src/Aether.AI.App.Core/     无 UI 引擎层：ChatSessionEngine（分段交错流 + 审批 resolver 缝）、
                            ConversationRuntime/ChatItems、TranscriptPersister/HistoryLoader/TurnRebuilder、
                            AgentRunner（⑫ HarnessRuntime 桥：会话映射/同会话串行/Invalidate/Evict）、
                            LlmConfigStore、CustomAgentStore（自定义 agent→AgentDefinition）、AddAetherAiApp() 组合
src/Aether.AI.App/          Avalonia 共享 UI：ChatView/ChatMessagesView（思考卡/工具卡/审批卡/计划卡/
                            markdown 完成态）、ChatViewModel（多会话并发/4 档模式/历史增量）、
                            AgentsView/AgentsViewModel（自定义大脑 CRUD）、LlmProfilesView、ApprovalDialog、
                            ⑩ workbench 壳（nav rail = chat；settings 收进 rail **左下角齿轮**入口——`FooterPanelKey`，见下）
src/Aether.AI.App.Desktop/  桌面 head（--smoke 无头冒烟：规则全链路 + 审批自恢复双断言）
tests/…Core.Tests/          8 例：引擎分段/审批卡状态迁移/取消路径、决策矩阵、持久化→重建往返
```

关键结构决策：**审批协作循环收在 ⑫**（`HarnessRuntime.RunInteractiveAsync`，本仓促成的 API 回馈）——
引擎只经 `ApprovalResponder` 缝做卡片状态迁移，不再自持 while 循环（monorepo 桌面/手机各一份的老病根）。
LLM 大脑凭「模型 ⚙」管理（OpenAI-compatible 档案 CRUD + 测试连接 + **切换免重启**——M2-lite，2026-10-24 实装）：
宿主自注册 `IChatClientFactory`（⑫ 兼容缝）+ `LlmConfigState` 动态选中 → `AppLlmClientFactory` 清缓存 +
`runner.Invalidate` → 切档案下一轮对话即换模型（2026-10-27 起对目录里所有 agent 逐个失效，
不再写死 `assistant`）。
**2026-10-26 对齐 ⑪ 连接档案范式**：① 聊天输入卡上方加**模型 chips 快速切换行 + 「模型 ⚙」管理入口**
（点 chip 即换模型，下一轮生效）；
② **密钥文件 0600**（llm-configs.json 含 API Key，对齐 ⑦ 凭据仓纪律——保存即收紧，旧文件加载时自愈）。

**2026-10-26 设置入口收进左下角**：⑩ workbench 新增 `WorkspaceDefinition.FooterPanelKey`——配了 key 的 panel
不进 nav rail，改由 rail 底部齿轮直达（`IsFooterCurrent` 高亮）；本 App 把 `settings` 指过去，nav rail 只剩
chat（Agents 页已于同日改为 agent 下拉里的管理弹窗，见下文）。旧版底部齿轮的 flyout（主题 + 重置布局）**并入设置页的「外观」区**，一个入口两件事。

**2026-10-26 三改：LLM 配置只留一处（用户反馈“设置里的 LLM 是多余的”）**——原先设置页和「模型 ⚙」模态
各显示一份档案 CRUD，看着重复。改为**按职责拆分 + 复用同一控件**：

| 入口 | 宿主 | 内容 |
|---|---|---|
| 输入卡「模型」下拉（2026-10-26 四改：原 chips + 「模型 ⚙」两个控件合并成一个） | 桌面 = 760×680 模态窗；移动 = ChatView 内嵌浮层 | **`LlmProfilesView`**（档案 CRUD + 测试连接）——唯一的 LLM 配置面 |
| rail 左下角齿轮 | ⑩ 工作台面板 | **`SettingsView`**（外观：主题 chips + 重置布局） |

**四改：选择与配置合并成一个下拉**——`LlmPickerItems` = 全部档案 + 末项哨兵「管理模型…」；
选档案即切换（`SelectedLlm` setter → `state.Select` + `runner.Invalidate`，下一轮生效），
选哨兵只发 `ManageLlmRequested` 事件并由 VM 把值**弹回**原档案（下拉里始终显示当前模型，不会停在「管理模型…」上），
宿主 `ChatView` 收到事件再开配置窗（桌面模态 / 移动浮层）。单测见 `tests/…App.Tests/ModelPickerTests.cs`（5 例）。

两处共用同一个 `SettingsViewModel` 单例（编辑即时落档，下拉随 `Changed` 重建），但**互不重复渲染对方的内容**；
`SettingsView.ShowAppearance` 因此删除（不再需要“同一页按宿主藏区”的开关）。
`ChatMessagesView` 空态文案同步改成“在输入卡的「模型」里配置”。

**设置页版式（2026-10-26 二改）**：原 640 窄居中栏把内容挤成一片——重排为「页头 + 分区卡片」：
880 上限（窗口窄时自动收窄）、分区间 22px、卡片内 20px；长 model/endpoint 走 `TextTrimming`（原先会压到右侧按钮下面），状态行空时不占位。
**主题 chips 别再套 `Border.chip`**——那个引擎样式写死 `Height=26`，加内距后内容只剩 12px、文字被裁成一条缝（设置页“看不到切换项”的根因）；改用自适应高度容器 + chip 自身 `MinHeight=32`。
验收用 `--dump-layout` 三档尺寸转储几何核对（无文本互相压住/越界）。

### M2-lite（2026-10-26）：Agents = 自定义大脑（agent 下拉里的管理弹窗）

用户自建 agent，两类大脑都开放（用户反馈“LLM 的和规则大脑似乎都需要”）：

| 大脑 | 用户填什么 | 映射到 ⑫ |
|---|---|---|
| LLM | 系统提示词、模型档案（可设「跟随当前选中档案」）、允许的工具、工具审批开关、是否开 4 档模式 | `EngineKind.Llm` + `Instructions`/`ModelId`/`Tools`/`RequireToolApproval`/`EnableAgentModeProvider` |
| 规则 | 触发正则、动作工具（可空=直接回复）、工具参数 JSON 模板、回复模板、未命中兜底 | `EngineKind.Rule` + `RuleProgram`（调工具时自动生成两步：trigger(MaxToolCalls=0) → reply(MinToolCalls=1)） |
| 代码 | 一段继承 `CodeRuleBrain` 的 C# 源码（重写 `RunAsync`，`await Call("Echo", ...)` 调工具）、允许的工具、工具审批开关 | `EngineKind.Code` + ⑫ 编译管线：`CustomAgentStore` → `AgentCodeNoteSource`（`ICodeNoteSource`）→ `ICodeCompilationService`（Roslyn，可卸载 ALC）→ `ICodeBrainRegistry`，跑起来就是标准 code brain |

- 代码大脑 Roslyn **只在桌面 head** 注册（`Program.Main` 里 `App.ServicesConfigure = s => s.AddCSharpBrainCompilation()`）——移动闭包不带 Roslyn/FCS，移动端遇到代码 agent 会明确报「未注册 C# 编译器」。
- 保存即编译：`store.Save` → `Changed` → `ICodeCompilationService.Reload`（500ms 去抖，后台线程）；编译失败显示在列表那一行的红字 + 保存状态条里，不是静默失败。
- 源码没有隐式 using（`Task`、`List` 等要自己 using）；模板里已写好一个能跑通的 Echo 示例。

- 存储 `<dataDir>/agents.json`（`CustomAgentStore`，原子写；与 `llm-configs.json` 同款形态，但**不含机密**故不做 0600）。
- **生效链不重启**：`App.BuildProvider` 的 `DynamicAgents` 把 `store.Agents` 映射进 agent 目录（每次枚举求值）；
  保存/删除 → `store.Changed` → `ChatViewModel.RefreshAgentTargetsAsync()` 热刷新聊天下拉，
  重设选中项时经 `_rebuildingTargets` 抑制「用户切目标 → 开新会话」的副作用。
- ⑫ 侧只加了一个**纯增量**字段 `AgentDefinition.Description`（给人看的摘要，与喂模型的 `Instructions` 分开）。
- **入口与模型完全同构**：Agents 不再是 nav 页 —— 聊天顶部 agent 下拉末项「管理 agent…」→ 模态窗（桌面 880×860）/
  内嵌浮层（移动），宿同一份 `AgentsView` + `AgentsViewModel`；选中哨兵时 VM 把值弹回原 agent 并抛 `ManageAgentsRequested`，
  **不触发**「切目标开新会话」（`AgentTargetItem.IsManage` + `_lastAgentId` 回弹）。
  旧布局档案（含 agents tab）由 ⑩ 的 `CoversSameRegions` 判定结构变化后自动回退默认，无迁移负担。
- 单测 `tests/…App.Tests/CustomAgentTests.cs`：JSON 往返、两类映射、正则/JSON 校验、
  保存后进目录 + 聊天下拉 + **规则 agent 真跑一轮（Echo 全链路）**、删除后消失、管理哨兵不重置会话。

**2026-10-27：取消「内置 agent」这一档**（用户反馈“压根不需要内置 agent，内置 agent 也是可以管理编辑删除的，
只留一个通用助手就行，通用助手也能管理”）。目录从此**只有一个来源**：`<dataDir>/agents.json`。

| 项 | 改前 | 改后 |
|---|---|---|
| 通用助手 | 只有在配了 LLM 档案时才动态出现在目录，不可编辑 | **首次运行播种**成 agents.json 里的一条普通记录（`CustomAgentStore.SeedIfMissing`），可改名/改提示词/改工具/删 |
| 规则演示·审批演示 | 恒在目录（`MergeAgents` 内置） | 只在 `AetherAppOptions.IncludeDemoAgents` 时挂——即 `--smoke` / `--demo`；正常运行看不到 |
| 管理窗 | 顶部列表 + 底部「内置 agent（只读）」卡片 | 只剩「我的 agent」列表，每一行都有 编辑 / 删除 |

- 播种只发生在 `agents.json` **不存在**时：用户删掉通用助手或删空整表，重启都不会复活（尊重用户）。
- 全删光时聊天顶部下拉只剩「管理 agent…」，输入卡提示“还没有 agent——在顶部下拉的「管理 agent…」里新建一个”。
- `IAgentRunner.Invalidate` 改对目录里全部 agent 逐个失效（通用助手改过名、或用户自建的“跟随当前档案”agent 都要跟着换模型）。

**2026-10-27 修订：模态窗要叠主题 token**（用户反馈“删除/新建只有鼠标移上去才看得见”）。⑩ 的主题
是**逐元素** overlay（`ThemeLoader.Apply`），`new Window { Content = AgentsView }` 出来的模态窗不会自动
继承——`CardBgBrush/SurfaceBrush/BorderBrush/AccentBrush` 解析失败，卡片变纯白、`primary` 只剩白字。
修法：`AppDialogs.ApplyTheme(dialog)` 在开关窗时叠加 + 跟随 `IThemeHost.ThemeChanged` 重染；
同时把卡片里的动作按钮从透明 `Button.ghost` 换成「浅底 + 描边」的 `Button.outline`（静止态可见）。
单测 `tests/…App.Tests/CustomAgentTests.cs` 新增 3 例：代码大脑映射 / **真编译跑一轮（Echo 全链路）** /
编译失败显示在列表行上。

### 移动端（方案A 实证，2026-10-24）

同一份 ChatView/ChatViewModel/Core 直跑 iOS 与 Android（`src/Aether.AI.App.{iOS,Android}` head）：

- **iOS（iPhone 16 Pro 模拟器）**：主题 overlay 叠根视图（无 Window 形态）、规则全链路、
  **审批 bottom sheet**（截图留证：sheet 打开态 + 已批准终态）、safe-area 正确。
- **Android（ai_spike AVD）**：同款 sheet 打开态 + 已批准终态截图留证；`--demo` 经
  `am start --es demo 1` → `App.LaunchArguments`（MainActivity 在 Avalonia 初始化前转存）。
- 移动端审批 = `MobileApprovalService` broker + ChatView 内嵌 bottom sheet（桌面休眠）；
  桌面 = 模态对话框——同一 VM/引擎，只换 IApprovalDialogService 端口实现。
- 待 M3：Page 族移动壳（NavigationPage/TabbedPage + TopCommandBar/BottomCommandBar——Avalonia 12 原语）、
  iOS 软键盘/真机 AOT、Android edge-to-edge 深化。

## 构建

```bash
dotnet build src/Aether.AI.App.Desktop -c Debug    # 桌面链（Core+App+⑩⑫ 传递）
dotnet test tests/Aether.AI.App.Core.Tests         # 8 例
dotnet run --project src/Aether.AI.App.Desktop     # GUI
dotnet run --project src/Aether.AI.App.Desktop -- --smoke       # 规则全链路 + 审批自恢复（exit 0 = L2 无 GUI 等价）
dotnet run --project src/Aether.AI.App.Desktop -- --llm-smoke    # LLM 真模型冒烟（需已配置档案；无档案 SKIP）
dotnet run --project src/Aether.AI.App.Desktop -- --dump-layout  # 版式诊断：三档窗口尺寸 × 各 tab 的视觉树几何 → %TEMP%/aether-layout-*.txt
# 实测（2026-10-24，DeepSeek）：deepseek-chat 流式 token 段>0 ✅；deepseek-reasoner reasoning 段>0（💭 思考卡路径）✅
dotnet build aether-ai-app.slnx                    # 全仓（含 spike 的 android/ios TFM，需本机 workload）
```
