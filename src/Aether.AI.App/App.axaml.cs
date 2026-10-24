using System.IO;
using Aether.AI.App.Core;
using Aether.AI.App.ViewModels;
using Aether.AI.App.Views;
using Aether.AI.Brains.Llm;
using Aether.AI.Brains.Rule;
using Aether.AI.Core.Agents;
using Aether.AI.Hosting;
using Aether.AI.Harness.Tools;
using Aether.Workbench;
using Aether.Workbench.Abstractions;
using Aether.Workbench.Theming;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

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
            var provider = BuildProvider();
            WorkbenchServices.Attach(provider);   // ⑩ 视图层（WorkspaceView code-behind）经此解析 IViewRegistry 等

            var shell = provider.GetRequiredService<WorkbenchShellViewModel>();
            shell.RestoreThemeSync();             // 窗口 Show 前同步恢复主题（免首帧 flash）
            var window = new WorkbenchWindow { DataContext = shell };
            window.Show();
            desktop.MainWindow = window;
            _ = shell.InitializeAsync();          // 恢复布局 + 激活工作台（不阻塞首帧）
            _ = provider.GetRequiredService<ChatViewModel>().InitializeAsync();
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
        var provider = BuildProvider();
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
                // 目标列表 InitializeAsync 已填好。
                vm.SelectedAgentTarget = vm.AgentTargetItems.First(t => t.Id == "rule-demo");
                vm.InputText = "查600000";
                await vm.SendInputCommand.ExecuteAsync(null);
            });

            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(async () =>
            {
                vm.SelectedAgentTarget = vm.AgentTargetItems.First(t => t.Id == "rule-approve");
                vm.InputText = "报单IF2406";
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

    /// <summary>组合 DI 容器。configure：测试注入口（最后执行——可覆盖任何默认注册，如假审批框/内联 dispatcher）。</summary>
    public static IServiceProvider BuildProvider(string? dataDirectory = null, Action<IServiceCollection>? configure = null)
    {
        var dataDir = dataDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".aether", "aether-ai-app");
        Directory.CreateDirectory(dataDir);

        var services = new ServiceCollection();

        // —— ⑩ 布局引擎：三 tab 工作台（chat / agents / settings）——
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
        services.AddSingleton<AppLlmClientFactory>();
        services.AddSingleton<Aether.AI.Brains.Abstractions.IChatClientFactory>(sp => sp.GetRequiredService<AppLlmClientFactory>());
        services.AddLlmBrainFactory();
        var aether = services.AddAetherAI();
        // Echo 演示工具：Dangerous 等级（审批演示 agent 的 RequireToolApproval 会拦它走审批循环；
        // 不开审批的 agent 照常直跑——等级只在审批开启时参与门控）。
        aether.ConfigureToolRegistry(registry => registry.Register(
            "Echo", "回声演示工具：原样返回输入", "Demo",
            _ => AIFunctionFactory.Create((Func<string, string>)(text => $"echo:{text}"), "Echo"),
            ToolApprovalLevel.Dangerous));

        // —— App.Core：SQLite 覆盖 + 目录/运行桥/持久化 ——
        services.AddAetherAiApp(o =>
        {
            o.DataDirectory = dataDir;
            // 动态源：有 LLM 档案才挂「通用助手」（ModelId 空 = 走当前选中档案）。
            o.DynamicAgents = sp => sp.GetRequiredService<LlmConfigState>().HasConfigs
                ? new[] { DefaultAgents.LlmAssistant() }
                : Array.Empty<AgentDefinition>();
        });

        // —— App 服务：UI 端口实现（按形态取舍）+ VM ——
        var isDesktop = Avalonia.Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime;
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
        services.AddSingleton<ChatViewModel>();
        services.AddSingleton<SettingsViewModel>();

        configure?.Invoke(services);
        return services.BuildServiceProvider();
    }
}
