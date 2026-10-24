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

        registry.Register(PanelKeys.Agents,
            () => new AgentsView(),
            () => null);

        registry.Register(PanelKeys.Settings,
            () => new SettingsView(),
            () => services.GetRequiredService<SettingsViewModel>());
    }
}
