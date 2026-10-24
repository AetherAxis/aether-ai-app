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
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace Aether.AI.App;

/// <summary>
/// ⑮ 组合根：⑩ 布局引擎（桌面壳）+ ⑫ AI 平台（Rule 大脑恒注册；LLM 大脑凭 llm-configs.json opt-in）
/// + App.Core（引擎/持久化/运行桥）→ AI 助手 App。M1 为桌面 head（WorkbenchWindow）；
/// 移动 head（Page 族壳 + 主题接入）按里程碑 M3 落地。
/// </summary>
public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var provider = BuildProvider();

            var shell = provider.GetRequiredService<WorkbenchShellViewModel>();
            shell.RestoreThemeSync();             // 窗口 Show 前同步恢复主题（免首帧 flash）
            var window = new WorkbenchWindow { DataContext = shell };
            window.Show();
            desktop.MainWindow = window;
            _ = shell.InitializeAsync();          // 恢复布局 + 激活工作台（不阻塞首帧）
            _ = provider.GetRequiredService<ChatViewModel>().InitializeAsync();
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>组合 DI 容器（供桌面 head 与未来测试/移动 head 复用）。</summary>
    public static IServiceProvider BuildProvider(string? dataDirectory = null)
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
        var (llmConfigs, selectedName) = LlmConfigStore.Load(Path.Combine(dataDir, "llm-configs.json"));
        LlmConfig? llm = null;
        if (llmConfigs.Count > 0 && selectedName is not null)
        {
            llm = llmConfigs.FirstOrDefault(c => c.Name == selectedName) ?? llmConfigs[0];
            services.AddLlmBrains(o =>
            {
                o.ApiKey = llm.ApiKey;
                o.ModelId = llm.ModelId;
                if (!string.IsNullOrEmpty(llm.Endpoint)) o.Endpoint = llm.Endpoint;
            });
        }
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
            if (llm is not null)
                o.Agents = new[] { DefaultAgents.LlmAssistant(llm.ModelId) };
        });

        // —— App 服务：UI 端口实现 + VM ——
        services.AddSingleton<IUiDispatcher, AvaloniaUiDispatcher>();
        services.AddSingleton<IApprovalDialogService, ApprovalDialogService>();
        services.AddSingleton<ChatViewModel>();

        return services.BuildServiceProvider();
    }
}
