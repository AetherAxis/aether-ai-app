using Aether.AI.Hosting;
using Aether.Terminal.AI;
using Aether.Workbench.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace Aether.AI.App.Terminal;

/// <summary>
/// ⑮ 桌面形态的终端**面板**注册口：⑭ 模块的工具（<c>AddTerminalTools</c>）+ 本 head 的服务实现/View/VM/视图贡献者。
/// 由桌面 head 经 <c>App.HeadToolsConfigure</c> 注入；移动 head 不设钩子 ⇒ 终端工具连注册都不发生、
/// RoyalTerminal/PTY 不进移动闭包。
/// </summary>
public static class TerminalAppExtensions
{
    public static AetherAIBuilder AddTerminalPanel(this AetherAIBuilder builder)
    {
        builder.AddTerminalTools();   // ⑭ Aether.Terminal.AI：run_in_terminal 工具（Dangerous + DesktopOnly）
        // ITerminalService 的 impl 在本 head（UI 耦合——持 RoyalTerminal 控件/PTY）：
        // 具体类型给 TerminalView ctor 注入，接口给工具解析——同一个单例。
        builder.Services.AddSingleton<TerminalService>();
        builder.Services.AddSingleton<ITerminalService>(sp => sp.GetRequiredService<TerminalService>());
        builder.Services.AddSingleton<TerminalViewModel>();
        builder.Services.AddSingleton<IViewRegistryContributor, TerminalViewContributor>();
        return builder;
    }
}
