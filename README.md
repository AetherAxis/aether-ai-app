# Aether.AI.App（M0 spike 阶段）

⑫ `aether-ai`（MAF 多大脑运行时）的统一 UI App——**方案A：Avalonia 单代码库，桌面 + Android + iOS**。
一套 AXAML / ViewModel / Core，按平台调整主题、控件尺寸、手势与 safe-area。

> 本仓当前处于 **M0 spike**：验证方案A 的六项硬前提（见下）。`spike/` 是可丢原型——
> M0 通过后按正式结构落 `src/`（App.Core 无 UI 引擎 + App 多 TFM + tests），届时重写本 README。

## 依赖（DAG：App 消费者层，与 ⑪ 同层）

- ⑩ `aether-workbench`——桌面壳/主题引擎/布局（spike 仅验证移动 TFM 可引用 + 主题清单可加载）
- ⑫ `aether-ai`——Hosting + Brains.Rule + Memory.Sqlite（**移动端闭包不带 Roslyn/FCS/LLM SDK 之外的重物**）
- M1 起：⑬ `aether-knowledge`（agent/workflow/rule 目录）、⑭ `aether-ai-integrations`（业务工具，opt-in）

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
- **本机运维发现**：Claude Code 会话的 shell 连不上 adb 标准端口 5037（连接被丢弃，非沙箱开关可绕过）。绕法：`adb -P 5038 start-server` 后全程 `-P 5038`——adb server 自己会扫描 5554+ 发现模拟器。

## M1（2026-10-24）：桌面聊天 MVP 落地 `src/`

```
src/Aether.AI.App.Core/     无 UI 引擎层：ChatSessionEngine（分段交错流 + 审批 resolver 缝）、
                            ConversationRuntime/ChatItems、TranscriptPersister/HistoryLoader/TurnRebuilder、
                            AgentRunner（⑫ HarnessRuntime 桥：会话映射/同会话串行/Invalidate/Evict）、
                            LlmConfigStore、AddAetherAiApp() 组合
src/Aether.AI.App/          Avalonia 共享 UI：ChatView/ChatMessagesView（思考卡/工具卡/审批卡/计划卡/
                            markdown 完成态）、ChatViewModel（多会话并发/4 档模式/历史增量）、
                            ApprovalDialog、⑩ workbench 三 tab 壳（chat/agents/settings）
src/Aether.AI.App.Desktop/  桌面 head（--smoke 无头冒烟：规则全链路 + 审批自恢复双断言）
tests/…Core.Tests/          8 例：引擎分段/审批卡状态迁移/取消路径、决策矩阵、持久化→重建往返
```

关键结构决策：**审批协作循环收在 ⑫**（`HarnessRuntime.RunInteractiveAsync`，本仓促成的 API 回馈）——
引擎只经 `ApprovalResponder` 缝做卡片状态迁移，不再自持 while 循环（monorepo 桌面/手机各一份的老病根）。
LLM 大脑凭 `~/.aether/aether-ai-app/llm-configs.json` opt-in（OpenAI-compatible）。

## 构建

```bash
dotnet build src/Aether.AI.App.Desktop -c Debug    # 桌面链（Core+App+⑩⑫ 传递）
dotnet test tests/Aether.AI.App.Core.Tests         # 8 例
dotnet run --project src/Aether.AI.App.Desktop     # GUI
dotnet run --project src/Aether.AI.App.Desktop -- --smoke   # 无头冒烟（exit 0 = L2 无 GUI 等价）
dotnet build aether-ai-app.slnx                    # 全仓（含 spike 的 android/ios TFM，需本机 workload）
```
