using Aether.AI.App;
using Aether.Workbench.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace Aether.AI.App.Browser;

/// <summary>浏览器面板的视图贡献者（桌面 head 经 AddBrowserPanel 注册；移动 head 不注册——面板键在
/// 共享 DesktopWorkspace 里但移动形态不渲染工作台，天然不受影响）。</summary>
public sealed class BrowserViewContributor : IViewRegistryContributor
{
    public void Contribute(IViewRegistry registry, IServiceProvider services)
    {
        registry.Register(PanelKeys.Browser,
            () => new BrowserView(),
            () => services.GetRequiredService<BrowserViewModel>());
    }
}
