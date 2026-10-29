# Aether.AI.App

⑫ `aether-ai`（MAF 多大脑运行时）的统一 UI App——**方案A：Avalonia 单代码库，桌面 + Android + iOS**。
一套 AXAML / ViewModel / Core，按平台调整主题、控件尺寸、手势与 safe-area。

**当前状态（2026-10-29）**：M0 方案A 六项硬前提全过 → M1 桌面聊天 MVP → M2-lite 自定义大脑（LLM/规则/代码）
→ M2-tools ⑭ 工具接入 → M2-caps 技能包 + 连接器 → M2-memory 记忆开关 → M2-wallet 链上钱包（**2026-10-28 随 AI 线去业务化移除**，见该节）→ M2-appaudit 应用审查页
→ M2-browser 浏览器面板（⑭ `Aether.Browser.AI` 工具 + 桌面 head `Browser/` 面板，见该节）。正式结构在 `src/`
（App.Core 无 UI 引擎 + App 共享 UI + `.Desktop`/`.Android`/`.iOS` 三个 head + `tests/`）；
`spike/` 是 M0 期的可丢原型，仅留作历史证据。
**先看下节「平台能力对照」**——手机端与桌面端能做什么、差在哪，一张表说清。

## 依赖（DAG：App 消费者层，与 ⑪ 同层）

- ⑩ `aether-workbench`——桌面壳/主题引擎/布局（spike 仅验证移动 TFM 可引用 + 主题清单可加载）
- ⑫ `aether-ai`——Hosting + Brains.Rule + Memory.Sqlite（**移动端闭包不带 Roslyn/FCS/LLM SDK 之外的重物**）
- ⑭ `aether-ai-integrations`——系统工具 16 个（含 **WebSearch** 网搜；命令类仅桌面）+ 应用审查 3 个（`Aether.AppAudit.AI`，本仓只留页面）+ **浏览器 4 个（`Aether.Browser.AI`，2026-10-29：工具/Playwright 服务在 ⑭，面板 UI 在本仓桌面 head `Browser/`——桌面专属，移动 head 不引 ⇒ Playwright 不进移动闭包）**。~~链上钱包 4 个~~（2026-10-28 随 AI 线去业务化移除——AI 线本质可调任意工具，业务适配未来按需再加）

## 平台能力对照：手机端 / 桌面端（2026-10-27 实测口径）

**同一份代码、同一条注册路径**，差别只在「这台机器跑不跑得了」。判定集中在一处：`App.HeadHost`
（head 显式声明）→ ⑫ `AetherAIOptions.Host` + `ToolRegistry` 的 `HostCapability`；工具 UI 与服务端口径一致。

### 一、agent 大脑

| 功能 | 手机 | 桌面 | 为什么 / 手机上的表现 |
|---|:--:|:--:|---|
| LLM 大脑（系统提示词 + 工具集 + 4 档模式 + 审批） | ✅ | ✅ | 纯网络 + 进程内，无平台依赖 |
| 规则大脑（正则 → 调工具/模板回复，进程内执行） | ✅ | ✅ | 零编译、零 key，两端同一份 `RuleProgram` |
| **代码大脑（写 C# 源码，本机编译后执行）** | ❌ | ✅ | Roslyn 编译器**只在桌面 head 注册**（`AddCSharpBrainCompilation` 走 `App.ServicesConfigure`），移动包不背 Roslyn/FCS。手机上选中代码 agent 会明确报「未注册 C# 编译器」，不是静默无响应 |

### 二、工具（⑭ 适配器）

| 功能 | 手机 | 桌面 | 为什么 / 手机上的表现 |
|---|:--:|:--:|---|
| 文件类：ReadFile / WriteFile / EditFile / LineEditFile / DeleteFile / MoveFile | ⚠️ | ✅ | 工具本身可用，但**只能碰 App 沙箱内路径**（iOS 容器外的绝对路径读不到也写不了） |
| **WebSearch（网搜，返回候选站点）** | ✅ | ✅ | 纯 HTTP 抓搜索引擎结果页，两端同源；应用审查页的反查用的是**同一份实现**（`WebSearchClient`） |
| 检索与批处理：Grep / Glob / ExploreDirectory / DiffFile / ApplyPatch / ApplyPatchWithDelete | ✅ | ✅ | 同上：纯托管 + 文件 IO，沙箱内正常 |
| FetchUrl（抓网页/PDF） | ✅ | ✅ | 纯 HTTP |
| **ExecuteCommand / ExecuteShellCommand（起子进程）** | ❌ | ✅ | iOS 沙盒禁止 fork。手机端**连注册都不发生**（`includeCommandTools:false`），编辑页的工具列表里也没有它们 |
| **应用审查 3 个（ListAuditTasks / SaveAppVerdict / ReadAppVerdict）** | ✅ | ✅ | **取任务 + 写/读结论，不含判定逻辑**——页面只发布任务，agent 自己查证据判、把结论写回同一份记录。只用本地 JSON，Safe 级不触发审批 |
| **浏览器 7 个（open / get_cookies / get_requests / close + **操作三件套** read / click / type）** | ❌ | ✅ | Playwright **headed 独立 OS 窗口**（用户手动登录），agent 读页面（`browser_read`）、点元素/填表单（`browser_click`/`browser_type`，Playwright selector）、取请求流 / cookie（拼 CookieHeader 给 curl 带访问需登录接口）。工具/服务在 ⑭ `Aether.Browser.AI`、面板 UI 在桌面 head `Browser/`；手机端**连注册都不发生**（桌面 head 的 `HeadToolsConfigure` 钩子，移动 head 不设）。`browser_get_cookies` / `browser_click` / `browser_type` = **Confirm**（动凭证、动已登录会话须过审批——审批卡可勾「总是允许」） |
| Echo（演示/回显） | ✅ | ✅ | 两端都在，用于审批演示与冒烟 |

### 三、技能包与连接器

| 功能 | 手机 | 桌面 | 为什么 / 手机上的表现 |
|---|:--:|:--:|---|
| 技能包正文（`skills/<name>/SKILL.md`，按需 load 进上下文） | ✅ | ✅ | 提示词级知识，两端同一份目录、同一个解析器 |
| **技能脚本 `run_skill_script`（.py / .sh / .js）** | ❌ | ✅ | `AetherAIOptions.EnableSkillScripts` 桌面为 true、手机 false → 手机上调脚本返回一条明确说明（「当前宿主不支持执行 skill 脚本，请只依据 SKILL.md 推理」），不假装成功 |
| 连接器 HTTP / SSE（MCP 远端服务） | ✅ | ✅ | 连接器是远端服务，手机与桌面同源 |
| **连接器 stdio（`command`+`args` 起本地子进程）** | ❌ | ✅ | 手机端**连连接尝试都不发生**（`AddMcpServersFromDirectory` 的宿主过滤），配置 JSON 原样留盘给桌面用 |

### 四、记忆

| 功能 | 手机 | 桌面 | 说明 |
|---|:--:|:--:|---|
| 会话内历史（滑动窗口 / SQLite 落盘重建） | ✅ | ✅ | SQLite native 在移动 TFM 已验证可载入 |
| 长期记忆 `EnableLongTermMemory`（召回 + 沉淀） | ✅ | ✅ | 落 `<dataDir>/memory.db`；换端同步同一份档案可接着用 |
| 工作笔记 `EnableFileMemory`（agent 自建笔记） | ✅ | ✅ | 落 `<dataDir>/agent-notes/<agentId>/`，沙箱内可写 |

### 五、界面形态（同一份 View/VM，两套壳）

| 功能 | 手机 | 桌面 | 说明 |
|---|:--:|:--:|---|
| 聊天：多会话、分段流、思考卡/工具卡/计划卡、markdown | ✅ | ✅ | 同一份 `ChatView` / `ChatViewModel` |
| 工具审批 | ✅ bottom sheet | ✅ 模态对话框 | 同一个 `IApprovalDialogService` 端口的两种实现 |
| agent 管理（增删改、三类大脑、记忆开关） | ✅ 内嵌浮层 | ✅ 880×860 模态窗 | 同一份 `AgentsView` |
| 模型档案管理（CRUD + 测试连接 + 切换免重启） | ✅ 内嵌浮层 | ✅ 模态窗 | 同一份 `LlmProfilesView` |
| **设置页**（外观主题 / 重置布局） | ❌ | ✅ | 设置页挂在 ⑩ 工作台的 nav rail 齿轮上；移动形态没有 nav rail（移动壳属 M3）。**但其「能力」内容手机可达**：`CapabilitiesView`（技能包 / 连接器）已挂到手机「模型设置」浮层的「能力」页签 |
| **应用审查页**（发布任务 + 显示结论） | ✅ 「模型设置 → 审查」页签 | ✅ nav rail 独立 tab | 同一份 `AppAuditView`/VM；**页面不搜不判**，结论由 agent 通过工具写回同一份 `app-audit.json`，见 M2-appaudit |
| **浏览器面板**（会话标签 + 请求流 + 详情） | ❌ | ✅ nav rail 独立 tab | 桌面 head `Browser/BrowserView`；浏览器画面是 Playwright 独立 OS 窗口，面板只观测请求流（agent 与用户开的会话同一个），见 M2-browser |
| 布局持久化、分隔条、多 tab | ❌ | ✅ | 桌面壳专属；移动是单视图全屏 |
| 主题 | 固定浅色 overlay | 4 套主题可切 | 移动端 `ThemeLoader.Apply` 直接叠在根视图上 |

### 六、开发 / 诊断入口（只有桌面 head 有命令行）

| 入口 | 手机 | 桌面 |
|---|:--:|:--:|
| `--smoke`（规则全链路 + 审批自恢复） | ❌ | ✅ |
| `--llm-smoke`（真模型一轮流式对话） | ❌ | ✅ |
| `--browser-smoke`（Playwright 真启动 + cookie 提取 + 干净关闭三断言） | ❌ | ✅ |
| `--tools`（当前宿主可用工具清单） | ❌ | ✅ |
| `--dump-layout`（版式几何转储）、Avalonia DeveloperTools | ❌ | ✅ |

### 已知未验证 / 待补

- iOS **真机 AOT** 未验证（模拟器已验证）；Android edge-to-edge 与软键盘行为待 M3。
- 移动壳（Page 族导航 / 底部 tab / 完整设置页）属 M3——上面「设置页 ❌」就是这条的直接体现；
  其中能力面（技能包/连接器）已通过「模型设置 → 能力」页签在手机可达，外观/布局仍是桌面专属。
- 手机端文件类工具的实际可用范围以沙箱为界，还没做「用户可见的目录选择器」。
- **应用审查的判定质量取决于你用的 agent**：页面只发布任务，判定由 agent 做（它搜几次、抓不抓正文都由它决定）。
  想让判定更硬就让它多抓几页；遇到同名站/落地页的正确处置是落「不确定」，不是硬给一个结论。
  判定模块本身不在本仓（在 ⑭ `Aether.AppAudit.AI`），所以换判定方/换宿主都不用动这里。

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

**2026-10-27 输入栏收敛（用户反馈：助手选择不该在左上、模型下拉前的「模型」文字多余）**：
- **助手选择器从头部下移到输入卡**，与「运行模式」「模型」并排成一行——「跟谁聊 / 用什么模型」两个开关集中在一处。
  头部**不再出现 agent 名**（下拉里就在显示，重复一遍只是噪音），左上只剩 token 用量。
- **去掉模型下拉前的「模型」文字前缀**：下拉本身已显示档案名/模型 id，前缀只是挤位置（悬停提示仍说明「选档案即切换」）。
- 顺带对齐尺寸：三个 picker 统一 `Padding="8,4" FontSize="11"`——模型下拉原来没设内距，比邻居矮 8px
  （版式转储实测：AgentPicker/ModelPicker 现在同为 23.5 高、同一 y 基线；900×600 / 1280×820 / 1792×997 三档无裁切）。

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
- **入口与模型完全同构**：Agents 不再是 nav 页 —— 输入栏 agent 下拉末项「管理 agent…」→ 模态窗（桌面 880×860）/
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

### M2-tools（2026-10-27）：⑭ 工具真接入（手机 / 桌面**同一条**注册路径）

此前 ⑮ 的工具注册表里只有演示用的 `Echo`——agent 勾得到工具，却全是空的。现在把 ⑭ 适配器接进来：

| 分组 | 工具 | 来源 | 宿主 |
|---|---|---|---|
| 系统工具（15） | ReadFile / WriteFile / EditFile / LineEditFile / DeleteFile / MoveFile / Grep / Glob / ExploreDirectory / DiffFile / ApplyPatch / ApplyPatchWithDelete / FetchUrl | ⑭ `AddSystemTools` | 两端通用 |
| 系统工具·命令类（2） | ExecuteCommand / ExecuteShellCommand | 同上 | **仅桌面**（iOS 沙盒禁止 fork） |

**一体化设计（关键：手机桌面同一个 App、同一份注册代码）**

- `App.BuildProvider` 里**一处**注册共享工具：`aether.AddSystemTools(includeCommandTools: isDesktop)`。
  手机端 `includeCommandTools:false` ⇒ 命令工具连注册都不发生（不是「注册了再隐藏」）；桌面端注册并声明 `DesktopOnly`。
- 宿主由 head **显式声明**：`App.HeadHost`（桌面 `DesktopOnly` / 移动 `MobileOnly`），未声明才按 Avalonia lifetime 推断
  —— 无头路径（`--smoke`/`--tools`/CI）里 Avalonia 还没起来，靠推断会一律落到「手机」。声明值同时喂给
  ⑫ `AetherAIOptions.Host`，`ToolRegistry.ResolveAll` 在运行期按 `HostCapability` 兜底跳过不匹配的工具。
- 需要桌面宿主的**额外**能力走 head 钩子 `App.HeadToolsConfigure`（与 `ServicesConfigure` 同款分工）；
  目前**没有消费者**——留着给真正只能在桌面跑的工具（依赖本机进程/窗口/终端的）。
  ⑭ SystemTools 是纯托管代码 + IHttpClientFactory，可以安心进移动闭包。
- UI 与服务端口径一致：agent 编辑页的工具勾选列表改用 `ToolRegistry.GetUsableMetas()`（新增，过滤口径与
  `ResolveAll` 逐条相同）——手机上不会出现「执行命令」，桌面上也不会有手机专属工具。

**验收**：`--tools` 打印当前宿主可用工具清单（桌面 27 个 = 系统 16 + 应用审查 3 + **浏览器 7** + Echo；手机 18 个 = 系统 14 + 应用审查 3 + Echo；
2026-10-29 浏览器 4→7，加入前为 20/18，2026-10-28 移除钱包 4 个前为 24/22）；
`tests/…App.Tests/AppToolWiringTests.cs` 5 例覆盖：手机宿主不含命令工具（且未注册）、Echo 等级、
桌面专属工具「注册但不可勾选且 ResolveAll 跳过」、head 钩子语义、以及**真解析 ReadFile 真读文件**。
桌面 head / Android / iOS 三个 TFM 均编译通过（iOS 链接也已通过）。

### M2-caps（2026-10-27）：技能包 + 连接器（手机桌面一体）

参照手机端 AI 助手通行做法（豆包的「技能 / 连接器 / 工作伙伴」口径：技能 = 声明式专业流程，
连接器 = 远端服务），把 ⑫ 已有的两套能力接进 ⑮，**同一个 App、同一份目录布局、同一条注册路径**，
差别只在「这台机器跑不跑得了」：

| 能力 | 放哪 | 格式 | 手机 | 桌面 |
|---|---|---|---|---|
| 技能包 | `<dataDir>/skills/<name>/SKILL.md` | YAML frontmatter（name/description/category）+ Markdown 正文 | ✅ 正文加载，模型按步骤推理 | ✅ 正文 + **脚本可执行**（.py/.sh/.js） |
| 连接器 | `<dataDir>/mcp-servers/*.json` | 兼容 Claude Code/Cursor 的 `mcpServers` 映射 | ✅ 仅 HTTP/SSE | ✅ HTTP + stdio |

- **技能包正文两端同源**：`AetherAIOptions.SkillsDirectory` 两端都设，走 MAF 的文件技能源（按需 load 正文，索引常驻）。
  脚本执行由新增的 `AetherAIOptions.EnableSkillScripts` 控制——手机端 false，`run_skill_script` 返回一条明确说明
  （「当前宿主不支持执行 skill 脚本，请只依据 SKILL.md 推理」），不是空白失败。
- **连接器按宿主裁**：`AddMcpServersFromDirectory(dir, filter)` 新增 filter 重载；手机端传
  `AiCapabilityPolicy.SupportsMcpServer`（= 只要 `!IsStdio`）。被裁掉的 server **连连接尝试都不发生**，
  配置 JSON 原样留盘给桌面用。
- 取舍判定集中在 `Aether.AI.App.Core/AiCapabilityPolicy`（组合根与测试同一份口径），目录布局在
  `AiCapabilityPaths`（`skills/` + `mcp-servers/`，`EnsureDirectories` 幂等建目录）。
- App 启动后台连一次 MCP（`IMcpManagementService.EnsureStartedAsync`，不阻塞首帧）；连上的工具自动进
  `ToolRegistry`，agent 编辑页的勾选列表立刻能看到（MCP 工具默认 `Dangerous` 审批等级）。
- **能力面抽成 `CapabilitiesView`（一份 XAML 两处宿主）**：桌面 = 设置页；手机 = 「模型设置」浮层的「能力」页签
  （手机没有 nav rail，这一步让能力面在手机上真正可达）。内容：本机能力一句话说明（桌面=脚本+stdio 可用 / 手机=正文+HTTP）、
  两个目录路径、已加载技能包列表、连接器状态（已连接/失败原因/工具数）+ 刷新（技能重扫盘、连接器重连）
  （同批曾加入「链上钱包」卡片，2026-10-28 随 AI 线去业务化移除）。
- 单测 `AiCapabilitiesTests` 7 例：目录派生、技能两端都加载、脚本开关按宿主、HTTP/stdio 取舍、
  **手机端注册表只剩 HTTP（stdio 不连）**、设置页连接器状态映射。

### M2-memory（2026-10-27）：长期记忆 / 工作笔记开关

⑫ 早就有这两条能力，⑮ 之前没开（`EnableLongTermMemory` / `EnableFileMemory` 都拿不到）。现在 agent 编辑窗
LLM 大脑下多了「记忆」区两个开关，列表行出现「记忆」标：

| 开关 | 映射 | 落盘 | 语义 |
|---|---|---|---|
| 长期记忆（跨会话） | `AgentDefinition.EnableLongTermMemory` → ⑫ `MemoryContextProvider` | `<dataDir>/memory.db`（`SqliteMemoryStore`，⑮ 早已注册） | 每轮前召回、每轮后沉淀，换会话/重启/换端都记得 |
| 工作笔记 | `AgentDefinition.EnableFileMemory` → MAF `FileSystemAgentFileStore` | `<dataDir>/agent-notes/<agentId>/` | agent 自己写的长文档笔记（`AetherAIOptions.FileMemoryRoot`） |

- 两开关默认**关**（沿用 ⑫「默认全轻量」的口径），存在 `agents.json` 里，随 agent 跨端同步。
- `FileMemoryRoot` 必须给根，否则 ⑫ 静默禁用文件记忆——⑮ 在 `BuildProvider` 里指向数据目录，两端一致。
- 单测 `MemoryToggleTests` 4 例：根目录落在数据目录、开关双向映射（含编辑一次不丢）、管理窗勾选→落盘→
  读回为 true 且行上有标记、聊天目录里的定义带开关。

### M2-wallet（2026-10-27 落地，2026-10-28 随「AI 线去业务化」移除）

2026-10-27 曾把链上钱包四工具（⑭ `Aether.Ledger.AI`：查询 Safe / 转账 Dangerous / 充值 Confirm）接入两端：
① 的签名实现同日从 NSec（libsodium 原生）换成纯托管 BouncyCastle Ed25519（Ed25519 确定性签名 ⇒ 签名字节逐字节不变，
iOS 链接问题随之消失），⑮ 侧新增 `LedgerSettings`（`<dataDir>/ledger.json`，优先级 文件 > env > 默认）
与设置面「链上钱包」卡片（节点地址 / 托管地址 / 种子文件路径）。

**2026-10-28 用户定调 AI 线先去业务化**（AI 线本质可调任意工具，业务适配未来按需再加）：
⑭ 的 `Aether.Ledger.AI` 模块（src + tests）与本仓的钱包注册、`LedgerSettings`、设置卡片、相关测试全部移除，
CI 亦不再 checkout ① aether-ledger。虚拟币线的钱包能力不受影响（⑯ 钱包 CLI / ⑰ 钱包 UI）；
恢复 AI 侧钱包工具时按 ⑭「一个领域一个模块」惯例重建（源码在两仓 git 历史）。
Ed25519 换库的回归（`LedgerEd25519ParityTests`）留在 ①，与 AI 线无关。

### M2-appaudit（2026-10-27）：应用审查页（页面只发布任务，判定由 agent 做）

需求：用户手上有**一份应用名清单**（手机上以 Chrome 网页应用方式装的那些，只知道显示名），
想知道①这些名字对应的是哪个网站、②这是什么类型的网站，顺带判定它是不是博彩站。
**结构性决定**：这页只做两件事——**发布任务**和**显示结论**。判定怎么做得由 agent 自己决定
（它用 `WebSearch` 搜、用 `FetchUrl` 抓正文、凭已有知识都行），页面一行判定逻辑都没有。

**任务流**

```
应用审查页 ──导入名单──▶ 待发布 ──发布任务──▶ 任务池
                                              │  ListAuditTasks（agent 读）
                                              ▼
                                        agent 自己判定（工具不限）
                                              │  SaveAppVerdict（agent 写）
                                              ▼
应用审查页 ◀── store.Changed 刷新 ──── app-audit.json
```

| # | 环节 | 实现 | 位置 |
|---|---|---|---|
| 1 | 清单来源 | **只有名字输入**：粘贴框一行一条 →「导入清单」（容忍 `1.` `2)` `1、` `- ` `• ` 前缀、`名字 https://…` 两段；给的名字自带网址就等于自带一条站点证据）。**不做本机扫描**——名字是用户的输入 | ⑭ `AppAudit.AI/AppNameList` |
| 2 | 发布任务 | 「发布任务」只改状态：`待发布 → 已发布`（不搜、不判、不发请求）。默认跳过已判过的；勾「含已判过的（复核）」把它们也放进池，**旧结论不清空、留在历史里**，行上标「待复核」 | ⑭ `AppAudit.AI/AppAuditService.Publish` |
| 3 | 取任务 | `ListAuditTasks`：待处理名字 + 清单里给的网址 + 上次结论（含判过几次），按发布时间先来后到、`limit` 截断 | ⑭ `AppAudit.AI/Tools/AppAuditTool` |
| 4 | 判定 | **没有判断逻辑**：agent 自己找证据、自己下结论。口径写在 skill 里：`skills/app-audit/SKILL.md`（何时用 WebSearch/FetchUrl、类型标签、博彩判定、`不确定 ≠ 干净`） | ⑭ `AppAudit.AI/AppAuditSkill`（skill 正文）+ ⑭ `SystemTools.AI` 的 `WebSearch`/`FetchUrl`（证据工具） |
| 5 | 写回 | `SaveAppVerdict`：追加一条 `AppVerdictAttempt`（时间/结论/类型/置信度/谁判的/理由/候选数）+ 覆盖当前结论 + 出池。合法性在入口拦（非法 verdict 不落盘；同义词/中文也认） | ⑭ `AppAudit.AI/Tools/AppAuditTool` + `AppAuditService.RecordVerdict` |
| 6 | 页面 | 汇总一行（任务池/待发布/博彩/非博彩/不确定）+「发布任务(N)」+ 粘贴框 + 导入/清空 + 逐行：名称 / 对应网站 / **类型** / 徽章 / 置信度 /「重发」+ 第二行「状态 · 理由」+ 任务流说明卡 | `Views/AppAuditView.axaml` + `ViewModels/AppAuditViewModel` |
| 7 | 落盘 | `<dataDir>/app-audit.json`（两端同一份，原子写 tmp→rename）；**再导入只补空缺，已判结论绝不被洗掉** | ⑭ `AppAudit.AI/AppAuditStore` |

- 为什么这么切：判定是**会变的东西**（口径、模型、要不要抓正文、要不要多轮），把它放进页面/服务里，
  换个判法就得改代码重编译；切出去以后——调口径改 Markdown、换判定方换 agent、页面和存储一行不动。
  刻意**不提供**「一条龙判定工具」（把“怎么判”钉死在工具里的老路）。
- **判定不是一次定终身**：行上「重发」把单条放回任务池（或勾「含已判过的」批量发布复核任务），
  再判一次就再写一条历史；行上显示「第 N 次判定」、悬停看历次结论。
- 类型标签口径：体育博彩 / 真人赌场 / 棋牌 / 视频 / 直播 / 社交 / 电商 / 新闻资讯 / 工具 / 软件下载 / 网盘 /
  游戏 / 色情 / 金融理财 / 诈骗站 / 未知；判不准时填「未知」，不许猜（页面类型列显示 `—` 即未判定）。
- 纪律：**「不确定」是保留结论，不是「非博彩」**——搜不到/同名不同义/证据不足都落成可见的 Inconclusive。
- 老文件兼容：旧版页面内流水线的阶段（`Searching`/`Searched`/`Judging`）**都算「待处理」**，
  中途退出的记录不会变成没人管的孤儿；枚举名保留就是为了读得懂老 JSON。
- skill 升级也守「不覆盖用户改动」：盘上内容若还是**旧版默认正文**（说明没改过）才升级成新版，
  改过的一律不动。
- **领域代码不在本仓**（2026-10-27）：模型 / 落盘 / 任务板 / 判定口径 skill / 三个工具全部搬到
  ⑭ `aether-ai-integrations/src/Aether.AppAudit.AI`（`AddAppAuditTools(builder, dataDir, skillsDir)`），
  沿用 ⑭ 的「一个领域一个模块」惯例。本仓只剩这页的 `AppAuditView` + `AppAuditViewModel`
  与一行注册——所以任何宿主（别的 app/CLI/服务）引用 ⑭ 那个模块就能有自己的应用审查，不需要这个 UI 仓。
  回归测试也随之搬到 ⑭ `tests/Aether.AppAudit.AI.Tests`（31 例：任务板状态机、写入纪律、工具链路、
  skill 落盘/迁移）；本仓 58 例里只剩「页面动作」这一层。
- 验收：桌面 `--tools` 有 `应用审查（3）`；`--dump-layout` 三档尺寸（1280/900/1792）该页无遮挡、无横向溢出。

### M2-browser（2026-10-29）：浏览器面板 + agent 浏览器工具（桌面专属）

自 monorepo 桌面端 `Acme.Desktop.Shared/Shell/Browser` port（BrowserService/BrowserTool/接口原样，面板 View/VM 随迁简化）。
**切法（用户定调「提供给 AI 调用的工具就进 ⑭」）**：工具 + Playwright 服务（UI-free）在 ⑭ `Aether.Browser.AI`，
面板 UI 在本仓**桌面 head** `src/Aether.AI.App.Desktop/Browser/`——不与三平台 head 并列成新项目（桌面形态的功能待在桌面 head 里）。

**形态**：浏览器画面是 Playwright 驱动的 **headed Chromium 独立 OS 窗口**（用户在这里手动登录/操作）；
app 内「浏览器」tab 只是该会话的**网络观测面板**（DevTools Network 式：请求流 + 状态/耗时/大小 + 请求/响应头体详情，
上下分栏可拖、URL 关键词 + 状态码段过滤、会话标签条多开）。**agent 与用户共用同一 service 单例**——
agent `browser_open` 开的会话，用户在面板上看得见（同 run_in_terminal 口径）。

**工作流**：`browser_open(url=登录页)` → 用户手动登录（验证码/二步验证人干）→ agent `browser_read()` 看页面 →
`browser_click()` / `browser_type()` 操作（**Confirm 审批卡**，可勾「总是允许」）→ `browser_get_cookies()` 拿凭证走
`curl -s -H "Cookie: …"` 访问需登录接口；`browser_get_requests` 顺带发现 API 端点。**操作三件套与观测/凭证四件共用同一
session**（登录态 + 面板观测 + 审批门都在其上）——agent 的每一次点击，你都在独立窗口里实时看得见。

**操作三件套**（2026-10-29 同批补齐，selector 用 Playwright 语法：`text=登录` / `role=button[name="提交"]` / `css=#id` / `xpath=`）：

| 工具 | 等级 | 语义 |
|---|---|---|
| `browser_read` | Safe | URL + 标题 + 正文可见文本（innerText，~2 万字截断）——agent 的"眼睛"，操作前先读页决定点哪 |
| `browser_click` | Confirm | 点元素（10s 超时，报错含选择器供 agent 自纠）；点击后等 DOMContentLoaded ≤3s 回传落点 URL |
| `browser_type` | Confirm | 填输入框（fill 语义=整体替换）；`pressEnter` 供无按钮的搜索框 |

已知边界：`target=_blank` 新标签页不在跟踪内（会话单页模型，同 monorepo 原版）；页面操作选择器找不到时约 10s 超时——
agent 换 selector 重试（报错原文透传）。

**启动模式开关（2026-10-29 同批，宿主策略——agent 不参与决策）**：面板工具栏「模式」chip，两档：

| 档 | 机制 | `navigator.webdriver` |
|---|---|---|
| 常规（默认） | Playwright 直接启动（channel 自动探测本机 Chrome/Edge） | `true`（明牌，站点一行 JS 可查） |
| **附加** | 本机浏览器**普通启动**（仅 `--remote-debugging-port` + 同一 profile，无自动化标志）→ Playwright 经 CDP 连入 | **`false`**（2026-10-29 探针实证；操作行为层——无轨迹点击/瞬时输入——仍可被行为分析识别，**不承诺隐身**） |

落 `<dataDir>/browser.json`，**对下一次新建会话生效**（运行中会话不动——模式是会话的出生属性）；两模式共用同一
profile，登录态互通、来回切零成本；agent 的 `browser_open` 遵循同一默认（工具不暴露 mode 参数）。附加模式需本机装有
Chrome/Edge（无则明确报错）。**进程纪律**：附加模式的浏览器是我们自己 `Process.Start` 的、不归 Playwright driver
代管——app 退出经容器 `DisposeAsync` await 杀掉（`ShutdownRequested` 钩子 + 3s 上限），否则浏览器孤儿（冒烟 5/5 钉死）。

| 项 | 落位 / 取值 |
|---|---|
| 工具注册 | 桌面 head `Program.Main` 设 `App.HeadToolsConfigure = aether.AddBrowserPanel`——该钩子的**第一个真实消费者**（「依赖本机进程/窗口」的工具）；移动 head 不设 ⇒ 连注册都不发生 |
| 工具等级 | open / get_requests / close = Safe；**get_cookies = Confirm** |
| 宿主能力 | 4 个工具全 `DesktopOnly`——agents.json 跨端同步误带勾选时手机端 `ResolveAll` 兜底跳过 |
| 持久 profile | `<dataDir>/browser-profile`（BuildProvider 设 `AetherAIOptions.BrowserProfileDir`，⑭ 服务读取）——登录一次后续复用，不碰日常浏览器 |
| channel | 未指定时自动探测本机 Chrome/Edge（`BrowserChannel.DetectDefault`，免 `playwright install chromium` ~150MB）；都没有才用 Playwright 自带 chromium |
| 负载逻辑（照搬 monorepo 踩坑件） | 请求 1000 条上限丢最旧 + body 32k 字符截断（总内存 ~64MB 封顶）+ 150ms 去抖合批；per-session 操作锁串行化；app 退出容器 `DisposeAsync` 批量释放（防 Chromium 进程残留） |
| port 时的修正 | monorepo `CloseSession` 关光会话后 `ActiveSessionId` 落 `Guid.Empty` 而非 null（`LastOrDefault()` 装箱陷阱）——工具层误判「还有活跃会话」，已修（⑭ BrowserService） |
| 与原版差异 | 详情体不用 AvaloniaEdit（⑮ 无编辑器栈）——只读 TextBox + JSON 美化，无语法高亮；图标 ✕ 文本替代 Codicons |

**验收**：⑭ `tests/Aether.Browser.AI.Tests` 13 例（fake service 工具行为，含操作三件套）+ 本仓 `BrowserToolWiringTests` 3 例（桌面注册 7 个、
等级断言 / 手机连注册都不发生 / 误挂载被宿主能力兜底）+ `BrowserViewModelTests` 5 例（投影/过滤/命令）；
`--tools` 桌面 27 个含「浏览器（7）」组；**`--browser-smoke` 五断言全过**（常规模式真启动 + cookie 提取链路 + 操作三件套离线 E2E
（data: URL 页 type→click→read 闭环，不依赖外网）+ **附加模式同套 E2E**（普通启动 + CDP 连入）+ 容器释放关闭
（含附加模式进程 kill 落地），exit 0），退出后无浏览器/驱动进程残留。

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

## CI

`.github/workflows/ci.yml`：checkout 本仓 + 三个兄弟仓（`aether-ai` / `aether-workbench` / `aether-ai-integrations`）
→ **断言 csproj 里每个 `..\..\..\<repo>\…` 引用的兄弟仓都已 checkout**（本地工作区永远解析得到，只有干净 runner 会暴露 MSB3202）
→ 桌面链 Release 构建 → 两个测试项目 → `--smoke`（L2 无 GUI 等价）。
**加跨仓引用时，记得同步 CI 的 checkout 列表**——落下了会被上面那条断言当场拦下。

## 构建

```bash
dotnet build src/Aether.AI.App.Desktop -c Debug    # 桌面链（Core+App+⑩⑫ 传递）
dotnet test tests/Aether.AI.App.Tests              # 47 例（含工具接线 / 能力 / 记忆 / 页面动作）
dotnet run --project src/Aether.AI.App.Desktop     # GUI
dotnet run --project src/Aether.AI.App.Desktop -- --smoke       # 规则全链路 + 审批自恢复（exit 0 = L2 无 GUI 等价）
dotnet run --project src/Aether.AI.App.Desktop -- --llm-smoke    # LLM 真模型冒烟（需已配置档案；无档案 SKIP）
dotnet run --project src/Aether.AI.App.Desktop -- --browser-smoke # 浏览器真链路五断言（常规+附加两种模式真启动、操作 E2E、干净关闭；屏幕会闪浏览器窗口）
dotnet run --project src/Aether.AI.App.Desktop -- --dump-layout  # 版式诊断：三档窗口尺寸 × 各 tab 的视觉树几何 → %TEMP%/aether-layout-*.txt
dotnet run --project src/Aether.AI.App.Desktop -- --tools        # 工具巡检：当前宿主可用工具（分类/审批等级）+ 被过滤项
# 实测（2026-10-24，DeepSeek）：deepseek-chat 流式 token 段>0 ✅；deepseek-reasoner reasoning 段>0（💭 思考卡路径）✅
dotnet build aether-ai-app.slnx                    # 全仓 7 个项目（src 的 android/ios head 需本机 workload）
# spike/（M0 原型、与 src 一一对应的「双份 head」）已移出 slnx，需要时显式指定 csproj 构建
```
