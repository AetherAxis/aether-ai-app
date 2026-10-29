using Aether.AI.App;
using Aether.Workbench.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace Aether.AI.App.Terminal;

/// <summary>终端面板的视图贡献者（桌面 head 经 AddTerminalPanel 注册；移动 head 不注册——
/// 终端是桌面专属能力，移动形态不渲染工作台、天然不受影响）。</summary>
public sealed class TerminalViewContributor : IViewRegistryContributor
{
    public void Contribute(IViewRegistry registry, IServiceProvider services)
    {
        // 视图工厂闭包解析具体 TerminalService（视图 ctor 注入——不经静态定位器）。
        registry.Register(PanelKeys.Terminal,
            () => new TerminalView(services.GetRequiredService<TerminalService>()),
            () => services.GetRequiredService<TerminalViewModel>());
    }
}
