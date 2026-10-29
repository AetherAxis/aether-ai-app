using Aether.AI.App;
using Aether.AI.App.Browser;
using Aether.Browser.AI;
using Aether.AI.Core.Agents;
using Aether.AI.Harness.Tools;
using Aether.AI.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Aether.AI.App.Tests;

/// <summary>
/// 浏览器工具在 ⑮ 的接线断言：① 移动 head（不设钩子）⇒ 连注册都不发生；② 桌面宿主 ⇒ ⑭ 模块 4 工具
/// 全注册、等级正确（open/requests/close = Safe，get_cookies = Confirm——cookie 是登录凭证）、
/// 全部声明 DesktopOnly；③ 误在手机宿主挂载 ⇒ 注册但被宿主能力兜底过滤（跨端同步 agents.json 不炸）。
/// 桌面/手机宿主用手动组合（⑫ AddAetherAI + o.Host + ⑭ AddBrowserTools）而非改 App.HeadHost 静态位——
/// 避免污染并行用例；与 AppToolWiringTests 同 Collection 串行（仍会读 HeadToolsConfigure 静态位）。
/// 注意 BrowserService 只实现 IAsyncDisposable——容器必须 DisposeAsync（同步 Dispose 会抛）。
/// </summary>
[Collection(AppToolWiringCollection.Name)]
public class BrowserToolWiringTests : IDisposable
{
    private readonly string _dataDir;

    public BrowserToolWiringTests()
    {
        _dataDir = Path.Combine(Path.GetTempPath(), $"aether-ai-app-browser-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dataDir);
    }

    public void Dispose()
    {
        App.HeadToolsConfigure = null;   // 静态钩子不留到下一个用例
        try { Directory.Delete(_dataDir, true); } catch { /* 临时目录 */ }
    }

    /// <summary>共享组合根（BuildProvider）在 head 未注入钩子时注册不了浏览器工具——移动 head 正是这条路。</summary>
    [Fact]
    public void 手机宿主_浏览器工具连注册都不发生()
    {
        App.HeadToolsConfigure = null;
        var registry = App.BuildProvider(_dataDir).GetRequiredService<ToolRegistry>();

        foreach (var name in BrowserAIBuilderExtensions.ToolNames)
            Assert.False(registry.Contains(name));
    }

    /// <summary>桌面/手机宿主的最小组合（同 AppToolWiringTests.BuildDesktopRegistry 范式）+ ⑭ AddBrowserTools。
    /// 注意容器必须由调用方在断言后 DisposeAsync——ToolRegistry 的宿主判定是惰性回容器解析的，提前 Dispose 会炸。</summary>
    private static ServiceProvider BuildHosted(HostCapability host)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        var aether = services.AddAetherAI();
        aether.Configure(o => o.Host = host);
        aether.AddBrowserTools();   // ⑭ 模块（服务 + 工具；面板 VM/视图是 ⑮ 侧 AddBrowserPanel 的事）
        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task 桌面宿主_四工具注册_等级与宿主能力正确()
    {
        var provider = BuildHosted(HostCapability.DesktopOnly);
        try
        {
            var registry = provider.GetRequiredService<ToolRegistry>();

            var usable = registry.GetUsableMetas().Select(m => m.Name).ToList();
            foreach (var name in BrowserAIBuilderExtensions.ToolNames)
            {
                Assert.True(registry.Contains(name), $"{name} 未注册");
                Assert.Contains(name, usable);
            }

            ToolMeta Meta(string name) => registry.GetAllMetas().Single(m => m.Name == name);
            Assert.Equal(ToolApprovalLevel.Safe, Meta("browser_open").ApprovalLevel);
            Assert.Equal(ToolApprovalLevel.Confirm, Meta("browser_get_cookies").ApprovalLevel);
            Assert.Equal(ToolApprovalLevel.Safe, Meta("browser_get_requests").ApprovalLevel);
            Assert.Equal(ToolApprovalLevel.Safe, Meta("browser_close").ApprovalLevel);
            // 操作三件套：read 只读 = Safe；click/type 在已登录会话上动手 = Confirm（审批卡可勾「总是允许」）
            Assert.Equal(ToolApprovalLevel.Safe, Meta("browser_read").ApprovalLevel);
            Assert.Equal(ToolApprovalLevel.Confirm, Meta("browser_click").ApprovalLevel);
            Assert.Equal(ToolApprovalLevel.Confirm, Meta("browser_type").ApprovalLevel);
            Assert.Equal(HostCapability.DesktopOnly, Meta("browser_open").HostCapability);
            Assert.Equal("浏览器", Meta("browser_open").Category);

            // 顺带断言：面板与工具共用的服务单例已注册（注册 ≠ 启动——Playwright 是惰性的）。
            Assert.NotNull(provider.GetRequiredService<IBrowserService>());
        }
        finally
        {
            await provider.DisposeAsync();   // BrowserService 只实现 IAsyncDisposable
        }
    }

    /// <summary>agents.json 跨端同步误带勾选：手机宿主下即使模块被挂上，ResolveAll 也按宿主能力兜底跳过。</summary>
    [Fact]
    public async Task 手机宿主_误挂载也被宿主能力兜底过滤()
    {
        var provider = BuildHosted(HostCapability.MobileOnly);
        try
        {
            var registry = provider.GetRequiredService<ToolRegistry>();

            Assert.True(registry.Contains("browser_open"));
            Assert.DoesNotContain("browser_open", registry.GetUsableMetas().Select(m => m.Name));
            Assert.Empty(registry.ResolveAll(new[] { "browser_open" }));
        }
        finally
        {
            await provider.DisposeAsync();
        }
    }

    /// <summary>桌面 head 的 AddBrowserPanel：设置 store 落 dataDir/browser.json，载入即应用为 service 默认
    /// （用户没开过面板也生效——agent 的 browser_open 遵循同一策略）。</summary>
    [Fact]
    public async Task AddBrowserPanel_载入browserjson并应用为默认启动模式()
    {
        var settingsPath = Path.Combine(_dataDir, "browser.json");
        await File.WriteAllTextAsync(settingsPath, "{\n  \"Mode\": \"Attach\"\n}");

        var services = new ServiceCollection();
        services.AddLogging();
        var aether = services.AddAetherAI();
        aether.Configure(o => o.Host = HostCapability.DesktopOnly);
        aether.AddBrowserPanel(_dataDir);
        var provider = services.BuildServiceProvider();
        try
        {
            var svc = provider.GetRequiredService<IBrowserService>();
            Assert.Equal(BrowserLaunchMode.Attach, svc.DefaultLaunchMode);
            Assert.NotNull(provider.GetRequiredService<BrowserSettingsStore>());
        }
        finally
        {
            await provider.DisposeAsync();
        }
    }
}
