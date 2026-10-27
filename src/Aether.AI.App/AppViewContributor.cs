using Aether.AI.App.ViewModels;
using Aether.AI.App.Views;
using Aether.Workbench.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace Aether.AI.App;

/// <summary>App 视图贡献者：把三个 panel 的视图/VM 工厂注册进 ⑩ 引擎的 IViewRegistry。</summary>
public sealed class AppViewContributor : IViewRegistryContributor
{
    public void Contribute(IViewRegistry registry, IServiceProvider services)
    {
        registry.Register(PanelKeys.Chat,
            () => new ChatView(),
            () => services.GetRequiredService<ChatViewModel>());

        // 设置面板 = rail 底部齿轮直达的那一页（FooterPanelKey），只管外观（主题）+ 布局（重置）。
        // LLM 档案走聊天输入卡的「模型 ⚙」→ LlmProfilesView（同一份 SettingsViewModel）——一处配置，不重复出现。
        registry.Register(PanelKeys.Settings,
            () => new SettingsView(),
            () => services.GetRequiredService<SettingsViewModel>());
    }
}
