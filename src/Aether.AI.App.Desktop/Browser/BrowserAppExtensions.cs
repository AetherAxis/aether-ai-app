using System.IO;
using Aether.AI.Hosting;
using Aether.Browser.AI;
using Aether.Workbench.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Aether.AI.App.Browser;

/// <summary>
/// ⑮ 桌面形态的浏览器**面板**注册口：⑭ 模块的工具/服务（<c>AddBrowserTools</c>）+ 本 head 的 View/VM/视图贡献者
/// + 宿主策略（browser.json 的启动模式）。
/// 由桌面 head 经 <c>App.HeadToolsConfigure</c> 注入（那是给「真正只能在桌面跑的工具」留的钩子，
/// 浏览器是它的第一个真实消费者）；移动 head 不设钩子 ⇒ 浏览器工具连注册都不发生、Playwright 不进移动闭包。
/// </summary>
public static class BrowserAppExtensions
{
    public static AetherAIBuilder AddBrowserPanel(this AetherAIBuilder builder, string dataDir)
    {
        builder.AddBrowserTools();   // ⑭ Aether.Browser.AI：IBrowserService + 7 个 agent 工具
        var settings = new BrowserSettingsStore(Path.Combine(dataDir, "browser.json"));
        builder.Services.AddSingleton(settings);

        // 宿主策略接线：启动模式载入即应用为 service 默认——用户没开过浏览器面板也生效
        // （agent 的 browser_open 同样遵循）。后面 BrowserViewModel 的 ctor/切换命令会再同步（幂等）。
        builder.Services.AddSingleton<IBrowserService>(sp =>
        {
            var svc = new BrowserService(sp.GetRequiredService<IOptions<AetherAIOptions>>());
            svc.DefaultLaunchMode = settings.Settings.Mode;
            return svc;
        });

        builder.Services.AddSingleton<BrowserViewModel>();
        builder.Services.AddSingleton<IViewRegistryContributor, BrowserViewContributor>();
        return builder;
    }
}
