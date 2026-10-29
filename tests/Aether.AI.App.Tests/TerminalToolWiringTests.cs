using Aether.AI.App;
using Aether.AI.App.Terminal;
using Aether.AI.Core.Agents;
using Aether.AI.Harness.Tools;
using Aether.AI.Hosting;
using Aether.Terminal.AI;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Aether.AI.App.Tests;

/// <summary>
/// 终端工具在 ⑮ 的接线断言：① 移动 head（不设钩子）⇒ 连注册都不发生；② 桌面宿主 ⇒ ⑭ 模块的
/// run_in_terminal 注册（Dangerous + DesktopOnly）；③ ⑮ AddTerminalPanel 把 UI 耦合的 ITerminalService
/// impl（TerminalService，持 RoyalTerminal 控件）一并接线——工具与面板共用同一单例。
/// 同 AppToolWiringCollection 串行（读 HeadToolsConfigure 静态位）。
/// </summary>
[Collection(AppToolWiringCollection.Name)]
public class TerminalToolWiringTests : IDisposable
{
    private readonly string _dataDir;

    public TerminalToolWiringTests()
    {
        _dataDir = Path.Combine(Path.GetTempPath(), $"aether-ai-app-terminal-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dataDir);
    }

    public void Dispose()
    {
        App.HeadToolsConfigure = null;
        try { Directory.Delete(_dataDir, true); } catch { /* 临时目录 */ }
    }

    /// <summary>共享组合根（BuildProvider）在 head 未注入钩子时注册不了终端工具——移动 head 正是这条路。</summary>
    [Fact]
    public void 手机宿主_终端工具连注册都不发生()
    {
        App.HeadToolsConfigure = null;
        var registry = App.BuildProvider(_dataDir).GetRequiredService<ToolRegistry>();

        foreach (var name in TerminalAIBuilderExtensions.ToolNames)
            Assert.False(registry.Contains(name));
    }

    /// <summary>桌面宿主的最小组合（⑭ AddTerminalTools——只注册工具，不含 UI 耦合的服务 impl）。</summary>
    [Fact]
    public void 桌面宿主_终端工具注册_Dangerous且DesktopOnly()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        var aether = services.AddAetherAI();
        aether.Configure(o => o.Host = HostCapability.DesktopOnly);
        aether.AddTerminalTools();
        using var provider = services.BuildServiceProvider();

        var registry = provider.GetRequiredService<ToolRegistry>();
        var usable = registry.GetUsableMetas().Select(m => m.Name).ToList();
        foreach (var name in TerminalAIBuilderExtensions.ToolNames)
        {
            Assert.True(registry.Contains(name), $"{name} 未注册");
            Assert.Contains(name, usable);
        }
        var meta = registry.GetAllMetas().Single(m => m.Name == "run_in_terminal");
        Assert.Equal(ToolApprovalLevel.Dangerous, meta.ApprovalLevel);
        Assert.Equal(HostCapability.DesktopOnly, meta.HostCapability);
        Assert.Equal("终端", meta.Category);
    }

    /// <summary>⑮ 桌面 head 的 AddTerminalPanel：工具 + UI 耦合的服务 impl 一并接线。
    /// TerminalService 解析不创建控件/PTY（视图附着后才建）——单测可安全解析。</summary>
    [Fact]
    public async Task AddTerminalPanel_工具与服务一并接线()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        var aether = services.AddAetherAI();
        aether.Configure(o => o.Host = HostCapability.DesktopOnly);
        aether.AddTerminalPanel();
        var provider = services.BuildServiceProvider();
        try
        {
            Assert.True(provider.GetRequiredService<ToolRegistry>().Contains("run_in_terminal"));
            var svc = provider.GetRequiredService<ITerminalService>();
            Assert.IsType<TerminalService>(svc);
            // 工具与面板共用同一单例：具体类型与接口解析到同一实例。
            Assert.Same(svc, provider.GetRequiredService<TerminalService>());
        }
        finally
        {
            await provider.DisposeAsync();   // TerminalService 实现 IAsyncDisposable
        }
    }
}
