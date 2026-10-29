using System.IO;
using Aether.AI.Abstractions;
using Aether.AI.App.Core;
using Aether.AppAudit.AI;
using Aether.AI.App.ViewModels;
using Aether.AI.App.Views;
using Aether.AI.Brains.Code.Compilation;
using Aether.AI.Brains.Llm;
using Aether.AI.Brains.Rule;
using Aether.AI.Core.Agents;
using Aether.AI.Hosting;
using Aether.AI.Harness.Tools;
using Aether.AI.Mcp;
using Aether.SystemTools.AI;
using Aether.Workbench;
using Aether.Workbench.Abstractions;
using Aether.Workbench.Theming;
using Avalonia;
using Avalonia.VisualTree;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aether.AI.App;

/// <summary>
/// ⑮ 组合根：⑩ 布局引擎（桌面壳）+ ⑫ AI 平台（Rule 大脑恒注册；LLM 大脑凭 llm-configs.json opt-in）
/// + App.Core（引擎/持久化/运行桥）→ AI 助手 App。
/// <para>三形态分派（方案A）：桌面 → WorkbenchWindow 三 tab 壳；Android（IActivityApplicationLifetime）
/// 与 iOS（ISingleViewApplicationLifetime）→ 同一份 <see cref="ChatView"/> 直接作根视图 + ⑩ 主题 overlay
/// 叠加在根上（无窗口形态）+ 审批走内嵌 bottom sheet（<see cref="MobileApprovalService"/>）。
/// 移动壳（Page 族导航/底部 tab）属 M3；当前移动分支已是可用的「全屏聊天」预览形态。</para>
/// </summary>
public partial class App : Application
{
    /// <summary>移动端审批 broker（bottom sheet 由 ChatView 内嵌宿主订阅）。桌面形态为 null（走模态对话框）。</summary>
    public static MobileApprovalService? MobileApproval { get; private set; }

    /// <summary>移动组合根（ChatView 的齿轮入口经此解析 SettingsViewModel 等服务）。桌面为 null。</summary>
    public static IServiceProvider? MobileProvider { get; private set; }

    /// <summary>
    /// head 专属 DI 注入点（在 <see cref="BuildProvider"/> 的 configure 之后执行）。
    /// 桌面 head 用它注册 <c>AddCSharpBrainCompilation()</c>——Roslyn 编译器只进桌面闭包，
    /// 移动端保持轻量（代码大脑在移动端会明确报「未注册 C# 编译器」）。
    /// </summary>
    public static Action<IServiceCollection>? ServicesConfigure { get; set; }

    /// <summary>
    /// head 专属**工具**注入点：<c>(builder, dataDir) =&gt; …</c>，在共享工具（⑭ SystemTools）注册之后执行。
    /// 目前没有消费者——留着这个钩子是给**真正只能在桌面跑**的工具
    /// （依赖本机进程/窗口/终端的）留位；设了就等于「只有桌面 head 有这些工具」。
    /// </summary>
    public static Action<AetherAIBuilder, string>? HeadToolsConfigure { get; set; }

    /// <summary>
    /// head 显式声明的宿主类型：桌面 head = <see cref="HostCapability.DesktopOnly"/>，移动 head = <see cref="HostCapability.MobileOnly"/>。
    /// <c>null</c>（默认）= 退回按 Avalonia lifetime 推断。
    /// 显式声明的理由：无头路径（<c>--smoke</c> / <c>--tools</c> / CI）里 <c>Application.Current</c> 还不存在，
    /// 推断会一律落到「手机」——桌面 head 的命令工具因此在巡检里消失。声明式判定与 GUI 运行期一致。
    /// </summary>
    public static HostCapability? HeadHost { get; set; }

    /// <summary>启动参数（iOS/Android head 的 Main/OnCreate 转存——移动 lifetime 不暴露 Args）。
    /// --demo：自动跑规则演示 + 审批演示（模拟器验收免点按注入）。</summary>
    public static string[] LaunchArguments { get; set; } = Array.Empty<string>();

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // 演示 agent 只在演示启动里挂（桌面一般不用，留 --demo 口径一致）
            var provider = BuildProvider(includeDemoAgents: desktop.Args?.Contains("--demo") == true);
            WorkbenchServices.Attach(provider);   // ⑩ 视图层（WorkspaceView code-behind）经此解析 IViewRegistry 等

            var shell = provider.GetRequiredService<WorkbenchShellViewModel>();
            shell.RestoreThemeSync();             // 窗口 Show 前同步恢复主题（免首帧 flash）
            var window = new WorkbenchWindow { DataContext = shell };
            window.Show();
            desktop.MainWindow = window;
            // 退出时释放容器：桌面 head 的浏览器（附加模式）自己 Process.Start 了浏览器进程、
            // 不归 Playwright driver 代管——必须在进程退出前 await 杀掉，否则浏览器孤儿。
            // 3s 上限 + 容错：退出路径尽力而为，不因个别服务卡死而拖住关窗。
            desktop.ShutdownRequested += (_, _) =>
            {
                try { ((IAsyncDisposable)provider).DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(3)); }
                catch { /* 退出路径尽力而为 */ }
            };
            _ = shell.InitializeAsync();          // 恢复布局 + 激活工作台（不阻塞首帧）
            _ = provider.GetRequiredService<ChatViewModel>().InitializeAsync();
            if (desktop.Args?.Contains("--dump-layout") == true) DumpLayoutAsync(window, desktop, shell);
        }
        else if (ApplicationLifetime is IActivityApplicationLifetime activity)
        {
            var (provider, root) = BuildMobileRoot();
            activity.MainViewFactory = () => root;
        }
        else if (ApplicationLifetime is ISingleViewApplicationLifetime singleView)
        {
            var (_, root) = BuildMobileRoot();
            singleView.MainView = root;
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>移动根视图：同一份 ChatView + ⑩ 主题 overlay 叠加在根（资源沿树解析，子树全生效）。</summary>
    private static (IServiceProvider Provider, UserControl Root) BuildMobileRoot()
    {
        // --demo/--demo-llm 才挂规则/审批演示 agent；正常启动只有用户自己的 agent。
        var demo = LaunchArguments.Contains("--demo", StringComparer.OrdinalIgnoreCase)
                   || LaunchArguments.Contains("--demo-llm", StringComparer.OrdinalIgnoreCase);
        var provider = BuildProvider(includeDemoAgents: demo);
        MobileProvider = provider;
        var vm = provider.GetRequiredService<ChatViewModel>();
        var root = new ChatView { DataContext = vm };
        ThemeLoader.Apply(root, ThemeLoader.GetLightJson());   // 移动形态无 WorkbenchWindow——主题直接叠根视图
        _ = vm.InitializeAsync().ContinueWith(_ => RunDemoIfRequested(vm));
        return (provider, root);
    }

    /// <summary>
    /// --demo：模拟器验收的自动演示（免点按注入）——规则全链路一轮，随后审批演示触发 bottom sheet
    /// （等待 8s 供截图），再程序化批准走完恢复轮。
    /// --demo-llm：目标「通用助手」发一轮真模型对话（需已配置 LLM 档案；无档案自动退回 --demo 行为）。
    /// </summary>
    private static async void RunDemoIfRequested(ViewModels.ChatViewModel vm)
    {
        var args = LaunchArguments;
        if (!args.Contains("--demo", StringComparer.OrdinalIgnoreCase)
            && !args.Contains("--demo-llm", StringComparer.OrdinalIgnoreCase)) return;
        try
        {
            if (args.Contains("--demo-llm", StringComparer.OrdinalIgnoreCase)
                && vm.AgentTargetItems.Any(t => t.Id == "assistant"))
            {
                // 真模型一轮：流式渲染走真实 LLM（DeepSeek 等）。截图窗 3-15s。
                await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(async () =>
                {
                    vm.SelectedAgentTarget = vm.AgentTargetItems.First(t => t.Id == "assistant");
                    vm.InputText = "用一句话自我介绍，再说 2+2 等于几。";
                    await vm.SendInputCommand.ExecuteAsync(null);
                });
                return;
            }

            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(async () =>
            {
                // 目标列表 InitializeAsync 已填好。道具文件走临时目录（demo agent = ReadFile/DeleteFile 真链路）。
                var readFile = Path.Combine(Path.GetTempPath(), "aether-demo-read.txt");
                await File.WriteAllTextAsync(readFile, "600000");
                vm.SelectedAgentTarget = vm.AgentTargetItems.First(t => t.Id == "rule-demo");
                vm.InputText = $"读{readFile}";
                await vm.SendInputCommand.ExecuteAsync(null);
            });

            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(async () =>
            {
                var delFile = Path.Combine(Path.GetTempPath(), "aether-demo-delete.txt");
                await File.WriteAllTextAsync(delFile, "待删除");
                vm.SelectedAgentTarget = vm.AgentTargetItems.First(t => t.Id == "rule-approve");
                vm.InputText = $"删除{delFile}";
                await vm.SendInputCommand.ExecuteAsync(null);   // → bottom sheet 弹出并挂起等审批
            });

            await Task.Delay(8000);   // 窗口期：审批 sheet 开着供截图（模拟器冷启动 + 轮次排队需几秒）
            Avalonia.Threading.Dispatcher.UIThread.Post(() => MobileApproval?.Complete(
                new Aether.AI.Core.Agents.ToolApprovalDecision(
                    MobileApproval is { } b && b.HasPending ? b.PendingRequestId! : string.Empty,
                    Approved: true, Reason: "演示批准", AlwaysApprove: false)));
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"[demo] {ex}");
        }
    }

    /// <summary>版式诊断（--dump-layout）：启动后按 1280x820 / 900x600 / 1792x997 三档窗口尺寸，
    /// 逐 tab 把整棵视觉树的几何（窗口坐标 / 可见性 / 裁切）写进 %TEMP%/aether-layout-*.txt 后退出。
    /// 截图只能看像素，转储能看清「谁在谁上面 / 谁超出父容器」——排查遮挡与裁切用。</summary>
    private static async void DumpLayoutAsync(Window window, IClassicDesktopStyleApplicationLifetime desktop,
        WorkbenchShellViewModel shell)
    {
        await Task.Delay(2500);
        var tabs = new[] { "chat", "app-audit", "browser", "terminal", "settings" };   // 与 DesktopWorkspace.LayoutTabbed 的页签顺序一致
        foreach (var (w, h) in new[] { (1280.0, 820.0), (900.0, 600.0), (1792.0, 997.0) })
        {
            window.Width = w;
            window.Height = h;
            await Task.Delay(500);
            for (int idx = 0; idx < tabs.Length; idx++)
            {
                if (shell.ActiveWorkspace is { } ws) ws.SelectedTabIndex = idx;
                await Task.Delay(400);
                var sb = new System.Text.StringBuilder();
                sb.AppendLine($"# tab={tabs[idx]} window={window.ClientSize.Width}x{window.ClientSize.Height}");
                void Walk(Avalonia.Visual v, int depth)
                {
                    var line = new string(' ', depth * 2) + v.GetType().Name;
                    if (v is Control c)
                    {
                        var b = c.Bounds;
                        var p = c.TranslatePoint(new Avalonia.Point(0, 0), window);
                        line += $" name={c.Name ?? "-"} vis={c.IsVisible} local=({b.X:0.#},{b.Y:0.#},{b.Width:0.#}x{b.Height:0.#})"
                              + $" win=({p?.X:0.#},{p?.Y:0.#}) clip={c.ClipToBounds}";
                        if (c is TextBlock tb && !string.IsNullOrWhiteSpace(tb.Text)) line += $" text=\"{tb.Text}\"";
                        else if (c is Button btn && btn.Content is string sc) line += $" content=\"{sc}\"";
                    }
                    sb.AppendLine(line);
                    foreach (var child in v.GetVisualChildren()) Walk(child, depth + 1);
                }
                Walk(window, 0);
                File.WriteAllText(Path.Combine(Path.GetTempPath(),
                    $"aether-layout-{(int)w}x{(int)h}-{tabs[idx]}.txt"), sb.ToString());
            }
        }
        desktop.Shutdown();
    }

    /// <summary>组合 DI 容器。configure：测试注入口（最后执行——可覆盖任何默认注册，如假审批框/内联 dispatcher）。
    /// includeDemoAgents：把规则/审批演示挂进目录——只给 <c>--smoke</c>（无头）与 <c>--demo</c>（移动验收）用。</summary>
    public static IServiceProvider BuildProvider(string? dataDirectory = null, Action<IServiceCollection>? configure = null,
        bool includeDemoAgents = false)
    {
        var dataDir = dataDirectory ?? ResolveDataDirectory();
        Directory.CreateDirectory(dataDir);

        // 形态判定放最前：工具注册要按宿主能力取舍（⑫ AetherAIOptions.Host + ⑭ HostCapability）。
        // 优先用 head 的显式声明（无头路径里 Avalonia 还没起来）；未声明才按 lifetime 推断：
        // 桌面 = 窗口形态；Android/iOS = 各自 lifetime（都落到 else 分支）。
        var isDesktop = HeadHost is { } declared
            ? declared == HostCapability.DesktopOnly
            : Avalonia.Application.Current?.ApplicationLifetime
                is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime;

        var services = new ServiceCollection();

        // —— ⑩ 布局引擎：工作台（nav rail = chat；settings 走 rail 左下角齿轮入口）——
        services.AddSingleton<Aether.Workbench.IPreferencesStore>(
            new JsonPreferencesStore(Path.Combine(dataDir, "preferences.json")));
        services.AddWorkbench(DesktopWorkspace.Definition);
        services.AddSingleton<IViewRegistryContributor, AppViewContributor>();

        // —— ⑫ AI 平台：大脑取舍在此（Core 只认 HarnessRuntime）——
        services.AddRuleBrains();                 // program 规则后端：恒注册（零 key 全链路 + 审批演示）
        // LLM 走「宿主自注册 IChatClientFactory」兼容缝（非 AddLlmBrains 固定 options）：
        // 设置页换选中档案 → factory 清缓存 + runner.Invalidate → 下一轮即换模型。
        var llmPath = Path.Combine(dataDir, "llm-configs.json");
        services.AddSingleton(new LlmConfigState(llmPath));
        // agent 目录唯一来源：agents.json（保存即经下面的 DynamicAgents 进目录，聊天下拉热刷新）。
        // 没有「内置只读 agent」这一档了——首次运行把「通用助手」播种成一条普通记录，
        // 之后它与用户自建的 agent 完全同权（可编辑、可删除，删了不会被复活）。
        var agentStore = new CustomAgentStore(Path.Combine(dataDir, "agents.json"));
        agentStore.SeedIfMissing(new[] { CustomAgentStore.FromLlmDefinition(DefaultAgents.LlmAssistant()) });
        services.AddSingleton(agentStore);
        // 日志：本 App 只引 Logging.Abstractions（不引完整 Logging）——编译服务等组件的 ILogger<T>
        // 给一个空实现即可（无输出、无副作用）。
        services.TryAddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        // 代码大脑：⑫ 的「源码 → 编译 → 注册」管线（ICodeCompilationService + CodeBrainFactory）。
        // 编译器本身（Roslyn）由**桌面 head** 经 ServicesConfigure 注入——移动闭包不背 Roslyn。
        // AgentCodeNoteSource 把 agents.json 里的代码 agent 一条条喂给编译管线。
        services.AddCodeCompilation();
        services.AddSingleton<ICodeNoteSource, AgentCodeNoteSource>();
        services.AddSingleton<AppLlmClientFactory>();
        services.AddSingleton<Aether.AI.Brains.Abstractions.IChatClientFactory>(sp => sp.GetRequiredService<AppLlmClientFactory>());
        services.AddLlmBrainFactory();
        var aether = services.AddAetherAI();
        // 宿主能力声明（⑫ ToolRegistry 据此过滤）：桌面 = DesktopOnly，手机 = MobileOnly。
        // 作用有二：① UI 不再展示当前宿主跑不了的工具；② ResolveAll 兜底跳过并记警告——
        // 换端同步 agents.json 时「桌面版勾了 ExecuteCommand、手机版也勾着」不会在调用时炸。
        // 技能包目录 + 连接器目录：手机桌面**同一份布局**（AiCapabilityPaths），跨端同步直接搬。
        var capabilities = AiCapabilityPaths.For(dataDir).EnsureDirectories();
        aether.Configure(o =>
        {
            o.Host = isDesktop ? HostCapability.DesktopOnly : HostCapability.MobileOnly;
            // 技能包（SKILL.md）：提示词级知识两端同源加载；**脚本执行**（run_skill_script 的 .py/.sh/.js）
            // 要 fork 子进程——iOS 沙盒禁止、手机也没有解释器，故只在桌面开放。手机端加载同一份 skill，
            // 模型仍按 SKILL.md 的步骤推理，只是执行脚本会拿到一条明确说明（不是空白失败）。
            o.SkillsDirectory = capabilities.SkillsDirectory;
            // 工作笔记（EnableFileMemory）的根：每个 agent 一个子目录（⑫ 按 definition.Id 再拼一层）。
            // 不给根 = 该能力被静默禁用；桌面/手机都指向数据目录，所以两端行为一致。
            o.FileMemoryRoot = Path.Combine(dataDir, "agent-notes");
            // 浏览器持久 profile（登录一次后续复用）。消费者 = 桌面专属切片 Aether.AI.App.Browser 的
            // BrowserService（经桌面 head 的 HeadToolsConfigure 注册，此选项在此即单一定义点）；
            // 移动端不注册浏览器服务，此选项空转无害。
            o.BrowserProfileDir = Path.Combine(dataDir, "browser-profile");
            o.EnableSkillScripts = AiCapabilityPolicy.SupportsSkillScripts(isDesktop);
        });
        // 连接器（MCP）：HTTP/SSE 两端同源；stdio（command+args 起子进程）只在桌面。
        // 手机端被裁掉的 server 连连接尝试都不发生，配置文件原样留在盘上给桌面用。
        aether.AddMcpServersFromDirectory(capabilities.McpServersDirectory,
            filter: cfg => AiCapabilityPolicy.SupportsMcpServer(cfg, isDesktop));
        // ⑭ 系统工具 16 个（文件/检索/HTTP/WebSearch/命令）——手机桌面同一份注册代码，差别只有命令类：
        // iOS 沙盒禁止 fork，`ExecuteCommand`/`ExecuteShellCommand` 在手机端连注册都跳过
        // （文件/搜索/FetchUrl 是纯托管 + 网络，两端同源可用）。
        aether.AddSystemTools(includeCommandTools: isDesktop);
        // Echo 演示工具已于 2026-10-29 移除（真实工具齐备；demo agent 改走 ReadFile/DeleteFile 真链路）。
        // head 专属工具（设了 = 只有该 head 注册这些工具：浏览器的 HeadToolsConfigure 首个消费者）。
        HeadToolsConfigure?.Invoke(aether, dataDir);

        services.AddSingleton(capabilities);   // 设置页「技能包与连接器」区要显示这两个目录

        // —— App.Core：SQLite 覆盖 + 目录/运行桥/持久化 ——
        services.AddAetherAiApp(o =>
        {
            o.DataDirectory = dataDir;
            o.IncludeDemoAgents = includeDemoAgents;
            // 动态源（每次枚举求值）：agents.json 里的全部 agent（含首次运行播种的「通用助手」，
            // ModelId 空 = 走当前选中档案）——保存/删除后不重启即反映到聊天下拉。
            o.DynamicAgents = sp =>
                sp.GetRequiredService<CustomAgentStore>().Agents.Select(CustomAgentStore.ToDefinition);
        });

        // —— App 服务：UI 端口实现（按形态取舍）+ VM ——（isDesktop 已在方法开头判定）
        services.AddSingleton<IUiDispatcher, AvaloniaUiDispatcher>();
        if (isDesktop)
        {
            services.AddSingleton<IApprovalDialogService, ApprovalDialogService>();
        }
        else
        {
            // 移动形态：无 Window 可模态——审批走 ChatView 内嵌 bottom sheet（broker 经 App.MobileApproval 暴露）。
            MobileApproval = new MobileApprovalService();
            services.AddSingleton<IApprovalDialogService>(MobileApproval);
        }
        // —— 应用审查（**只发布任务**；判定由 agent 做，页面只显示结论）——
        // 领域模块在 ⑭（Aether.AppAudit.AI）：数据模型 / 落盘 / 任务板 / 判定口径 skill / agent 工具
        // 全在那边，本仓只留这页的 View + VM。AddAppAuditTools 顺手把 skill 种进 skills 目录
        // （agent 会自动加载；改口径改 Markdown，不用重编译）。
        services.AddSingleton<AppAuditViewModel>();

        services.AddSingleton<ChatViewModel>();
        services.AddSingleton<SettingsViewModel>();
        services.AddSingleton<AgentsViewModel>();   // agent 管理窗（聊天顶部下拉末项「管理 agent…」进入）

        // ⑭ 应用审查集成模块：注册 store/service/工具（三个工具都不含判定逻辑）。
        aether.AddAppAuditTools(dataDir, capabilities.SkillsDirectory);

        configure?.Invoke(services);
        ServicesConfigure?.Invoke(services);   // head 专属注入（桌面：AddCSharpBrainCompilation —— Roslyn）
        var provider = services.BuildServiceProvider();

        // 代码大脑：启动编译一轮 + agents.json 一变就重编译（Reload 自带 500ms 去抖、跑在后台线程，
        // 不占 UI 线程；编译结果经 ICodeCompilationService.Loaded 广播给管理窗刷新错误）。
        var compilation = provider.GetRequiredService<ICodeCompilationService>();
        provider.GetRequiredService<CustomAgentStore>().Changed += compilation.Reload;
        compilation.Reload();

        // 连接器（MCP）后台连接：不阻塞首帧，连上后工具自动进 ToolRegistry（⑫ McpManagementService）。
        // 没有配置时是空转（零成本）；单个 server 连不上只记在它自己身上（设置页能看到 Error），不影响聊天。
        _ = Task.Run(async () =>
        {
            try { await provider.GetRequiredService<IMcpManagementService>().EnsureStartedAsync(); }
            catch { /* 连接器整体不可用不应拖垮聊天 */ }
        });
        return provider;
    }

    /// <summary>
    /// 数据目录解析（行为驱动，不做平台判断）：首选 <c>~/.aether/aether-ai-app</c>（macOS 桌面 / Android
    /// 的 UserProfile 映射到沙箱 files 目录，均可写）；**iOS 真机的容器根禁止建目录**（EPERM——模拟器不限制，
    /// 真机限制，2026-10-24 实机闪退根因）→ 退 <c>~/Library/Application Support/aether-ai-app</c>
    /// （iOS 惯例位：沙箱内可写、随设备备份）。
    /// </summary>
    private static string ResolveDataDirectory()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrEmpty(home))
            home = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        var preferred = Path.Combine(home, ".aether", "aether-ai-app");
        try
        {
            Directory.CreateDirectory(preferred);
            return preferred;
        }
        catch (UnauthorizedAccessException)
        {
            var fallback = Path.Combine(home, "Library", "Application Support", "aether-ai-app");
            Directory.CreateDirectory(fallback);
            return fallback;
        }
    }
}
