using Aether.AI.App;
using Aether.AI.Core.Agents;
using Aether.AI.Harness.Tools;
using Aether.AI.Hosting;
using Aether.SystemTools.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Aether.AI.App.Tests;

/// <summary>
/// ⑭ 系统工具接进 ⑮ 的接线断言（手机/桌面**同一条**注册路径）：
/// ① 共享工具两端都有；② 命令工具只在桌面（含 head 未注入钩子时手机看不到）；
/// ③ 「当前宿主可用」过滤口径 = 运行时 ResolveAll 口径；④ head 钩子注入的桌面专属工具只在设了钩子时出现；
/// ⑤ 真解析出的工具能真跑。
/// 本类会改 <see cref="App.HeadToolsConfigure"/> 这个静态钩子，故整类串行（不与其它集合并行）。
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class AppToolWiringCollection
{
    public const string Name = "AppToolWiring";
}

[Collection(AppToolWiringCollection.Name)]
public class AppToolWiringTests : IDisposable
{
    private readonly string _dataDir;

    public AppToolWiringTests()
    {
        _dataDir = Path.Combine(Path.GetTempPath(), $"aether-ai-app-tools-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dataDir);
    }

    public void Dispose()
    {
        App.HeadToolsConfigure = null;   // 静态钩子不留到下一个用例
        try { Directory.Delete(_dataDir, true); } catch { /* 临时目录 */ }
    }

    private IServiceProvider Build() => App.BuildProvider(_dataDir);

    /// <summary>测试进程里没有 Avalonia 桌面生命周期 ⇒ 共享组合根判为**手机宿主**——
    /// 这正是断言「手机端看不到桌面专属工具」的天然环境。</summary>
    [Fact]
    public void 手机宿主_注册共享工具_但不含命令工具()
    {
        var registry = Build().GetRequiredService<ToolRegistry>();

        Assert.Equal(HostCapability.MobileOnly, registry.CurrentHost);

        var usable = registry.GetUsableMetas().Select(m => m.Name).ToList();
        foreach (var name in new[] { "ReadFile", "WriteFile", "EditFile", "Grep", "Glob", "ExploreDirectory", "FetchUrl", "DiffFile" })
            Assert.Contains(name, usable);

        Assert.DoesNotContain("ExecuteCommand", usable);
        Assert.DoesNotContain("ExecuteShellCommand", usable);
        // 未注册（includeCommandTools:false 时连注册都跳过），不只是被过滤。
        Assert.False(registry.Contains("ExecuteCommand"));
    }

    /// <summary>共享组合根自带的演示工具（Echo）也在，审批等级原样带出——Agent 编辑页的勾选列表靠它。</summary>
    [Fact]
    public void 演示工具Echo_出现在可用列表且为危险级()
    {
        var registry = Build().GetRequiredService<ToolRegistry>();
        var echo = Assert.Single(registry.GetUsableMetas(), m => m.Name == "Echo");
        Assert.Equal(ToolApprovalLevel.Dangerous, echo.ApprovalLevel);
    }

    /// <summary>UI 过滤口径 = 运行时口径：命令工具仍注册着（桌面会用到），但在手机宿主下不可勾选。</summary>
    [Fact]
    public void 桌面专属工具_注册但手机宿主不可勾选_且ResolveAll跳过()
    {
        var provider = Build();
        var registry = provider.GetRequiredService<ToolRegistry>();

        // 直接往注册表塞一个桌面专属工具（模拟 ⑭ 里声明 DesktopOnly 的那批）。
        registry.Register("DesktopProbe", "桌面探针", "测试",
            _ => AIFunctionFactory.Create((Func<string>)(() => "desktop"), "DesktopProbe"),
            ToolApprovalLevel.Safe, HostCapability.DesktopOnly);

        Assert.Contains("DesktopProbe", registry.GetAllMetas().Select(m => m.Name));
        Assert.DoesNotContain("DesktopProbe", registry.GetUsableMetas().Select(m => m.Name));
        Assert.Empty(registry.ResolveAll(new[] { "DesktopProbe" }));

        // 桌面宿主下同一工具既可见也可解析——证明是「宿主 × 能力」判定，不是按名字写死。
        using var desktop = BuildDesktopRegistry();
        var desktopRegistry = desktop.GetRequiredService<ToolRegistry>();
        desktopRegistry.Register("DesktopProbe", "桌面探针", "测试",
            _ => AIFunctionFactory.Create((Func<string>)(() => "desktop"), "DesktopProbe"),
            ToolApprovalLevel.Safe, HostCapability.DesktopOnly);
        Assert.Contains("DesktopProbe", desktopRegistry.GetUsableMetas().Select(m => m.Name));
        Assert.Single(desktopRegistry.ResolveAll(new[] { "DesktopProbe" }));
    }

    /// <summary>head 工具钩子：不设 ⇒ 桌面专属工具不进注册表；设了 ⇒ 进表、可解析。
    /// （链路留给真正只能在桌面跑的工具——依赖本机进程/窗口/终端的。）</summary>
    [Fact]
    public void HeadToolsConfigure_钩子决定桌面专属工具是否进注册表()
    {
        App.HeadToolsConfigure = null;
        Assert.False(Build().GetRequiredService<ToolRegistry>().Contains("HeadProbe"));

        App.HeadToolsConfigure = (aether, dataDir) =>
        {
            Assert.True(Directory.Exists(dataDir));   // 钩子拿到的是已建好的数据目录
            aether.ConfigureToolRegistry(registry => registry.Register(
                "HeadProbe", "head 探针（测试替身）", "测试",
                _ => AIFunctionFactory.Create((Func<string>)(() => "probe"), "HeadProbe"),
                ToolApprovalLevel.Safe));
        };

        var registry = Build().GetRequiredService<ToolRegistry>();
        Assert.True(registry.Contains("HeadProbe"));
        Assert.Single(registry.ResolveAll(new[] { "HeadProbe" }));
    }

    /// <summary>端到端：真解析出 ⑭ 的 ReadFile，真读一个临时文件。</summary>
    [Fact]
    public async Task ReadFile_能真读文件()
    {
        var file = Path.Combine(_dataDir, "hello.txt");
        await File.WriteAllTextAsync(file, "第一行\n第二行");

        var registry = Build().GetRequiredService<ToolRegistry>();
        var tool = Assert.Single(registry.ResolveAll(new[] { "ReadFile" }));

        var args = new AIFunctionArguments(new Dictionary<string, object?> { ["filePath"] = file });
        var result = await tool.InvokeAsync(args);
        var text = result?.ToString() ?? "";
        Assert.Contains("第一行", text);
        Assert.Contains("第二行", text);
    }

    /// <summary>桌面宿主的最小组合：同一套 ⑫ 平台 + ⑭ 系统工具，只是 <c>Host=DesktopOnly</c> 且带命令工具。</summary>
    private ServiceProvider BuildDesktopRegistry()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        var aether = services.AddAetherAI();
        aether.Configure(o => o.Host = HostCapability.DesktopOnly);
        aether.AddSystemTools(includeCommandTools: true);
        return services.BuildServiceProvider();
    }
}
