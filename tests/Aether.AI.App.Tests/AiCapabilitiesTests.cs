using Aether.AI.Abstractions;
using Aether.AI.App;
using Aether.AI.App.Core;
using Aether.AI.App.ViewModels;
using Aether.AI.Core.Agents;
using Aether.AI.Harness.Agents;
using Aether.AI.Harness.Skills;
using Aether.AI.Hosting;
using Aether.AI.Mcp;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Aether.AI.App.Tests;

/// <summary>
/// 技能包（SKILL.md）+ 连接器（MCP）的接线：目录布局、提示词级 skill 两端同源、
/// 手机端自动裁掉「脚本执行」与 stdio 连接器（跑不了的明说，不静默降级）。
/// 会改 <see cref="App.HeadHost"/> 静态钩子 → 与工具接线测试同集合，串行跑。
/// </summary>
[Collection(AppToolWiringCollection.Name)]
public class AiCapabilitiesTests : IDisposable
{
    private readonly string _dataDir;

    public AiCapabilitiesTests()
    {
        _dataDir = Path.Combine(Path.GetTempPath(), $"aether-ai-app-caps-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dataDir);
    }

    public void Dispose()
    {
        App.HeadHost = null;
        try { Directory.Delete(_dataDir, true); } catch { /* 临时目录 */ }
    }

    private void WriteSkill(string name, string description, string body = "第一步：……\n第二步：……")
    {
        var dir = Path.Combine(_dataDir, "skills", name);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "SKILL.md"),
            $"---\nname: {name}\ndescription: {description}\ncategory: 报表\n---\n\n{body}\n");
    }

    private void WriteMcpConfig()
    {
        var dir = Path.Combine(_dataDir, "mcp-servers");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "servers.json"), """
            { "mcpServers": {
                "http-connector": { "url": "http://127.0.0.1:1/mcp" },
                "stdio-connector": { "command": "echo", "args": ["hi"] }
            } }
            """);
    }

    [Fact]
    public void 能力目录_按数据目录派生并自动建好()
    {
        var paths = AiCapabilityPaths.For(_dataDir).EnsureDirectories();
        Assert.Equal(Path.Combine(_dataDir, "skills"), paths.SkillsDirectory);
        Assert.Equal(Path.Combine(_dataDir, "mcp-servers"), paths.McpServersDirectory);
        Assert.True(Directory.Exists(paths.SkillsDirectory));
        Assert.True(Directory.Exists(paths.McpServersDirectory));
    }

    /// <summary>技能包正文 = 提示词级知识：两端都能加载（手机也没有任何降级）。</summary>
    [Fact]
    public async Task 技能包_手机端照样加载正文()
    {
        WriteSkill("daily-report", "把当日数据整理成日报");

        App.HeadHost = HostCapability.MobileOnly;
        var vm = App.BuildProvider(_dataDir).GetRequiredService<SettingsViewModel>();
        await vm.RefreshCapabilitiesCommand.ExecuteAsync(null);

        var skill = Assert.Single(vm.Skills.Where(x => x.Name == "daily-report"));
        Assert.Equal("daily-report", skill.Name);
        Assert.Equal("报表", skill.Category);
        Assert.Contains("脚本不可执行", vm.HostCapabilityText);
        Assert.Equal(Path.Combine(_dataDir, "skills"), vm.SkillsDirectory);
    }

    /// <summary>同一份 skill 在桌面端也加载，且提示语说明脚本可执行——两端是同一份档案、同一套解析器。</summary>
    [Fact]
    public async Task 技能包_桌面端加载同一份且提示脚本可执行()
    {
        WriteSkill("daily-report", "把当日数据整理成日报");

        App.HeadHost = HostCapability.DesktopOnly;
        var vm = App.BuildProvider(_dataDir).GetRequiredService<SettingsViewModel>();
        await vm.RefreshCapabilitiesCommand.ExecuteAsync(null);

        Assert.Contains(vm.Skills, x => x.Name == "daily-report");
        Assert.Contains("脚本可执行", vm.HostCapabilityText);
    }

    /// <summary>宿主取舍落到 ⑫ 的 AgentFactory 上：手机端关脚本执行，桌面端开。</summary>
    [Fact]
    public void 技能脚本执行_按宿主开关()
    {
        App.HeadHost = HostCapability.MobileOnly;
        Assert.False(App.BuildProvider(_dataDir).GetRequiredService<AgentFactory>().EnableSkillScripts);

        App.HeadHost = HostCapability.DesktopOnly;
        Assert.True(App.BuildProvider(_dataDir).GetRequiredService<AgentFactory>().EnableSkillScripts);
    }

    /// <summary>连接器取舍（纯判定）：HTTP 两端都接，stdio 只在桌面。</summary>
    [Fact]
    public void 连接器_HTTP两端可用_stdio仅桌面()
    {
        var http = new McpServerConfig { Name = "remote", Url = "https://example.com/mcp" };
        var stdio = new McpServerConfig { Name = "local", Command = "npx", Arguments = new[] { "srv" } };

        Assert.True(AiCapabilityPolicy.SupportsMcpServer(http, isDesktop: false));
        Assert.False(AiCapabilityPolicy.SupportsMcpServer(stdio, isDesktop: false));
        Assert.True(AiCapabilityPolicy.SupportsMcpServer(http, isDesktop: true));
        Assert.True(AiCapabilityPolicy.SupportsMcpServer(stdio, isDesktop: true));
    }

    /// <summary>端到端：手机宿主的 provider 只把 HTTP 连接器交给注册表（stdio 连连接尝试都不发生）。</summary>
    [Fact]
    public async Task 连接器_手机端注册表里只剩HTTP()
    {
        WriteMcpConfig();
        var paths = AiCapabilityPaths.For(_dataDir).EnsureDirectories();

        var services = new ServiceCollection();
        services.AddLogging();
        var aether = services.AddAetherAI();
        aether.Configure(o => o.Host = HostCapability.MobileOnly);
        aether.AddMcpServersFromDirectory(paths.McpServersDirectory,
            filter: cfg => AiCapabilityPolicy.SupportsMcpServer(cfg, isDesktop: false));
        await using var provider = services.BuildServiceProvider();

        var mcp = provider.GetRequiredService<IMcpManagementService>();
        await mcp.EnsureStartedAsync();
        var servers = await mcp.GetServersAsync();

        var only = Assert.Single(servers);
        Assert.Equal("http-connector", only.Name);
        Assert.False(only.IsConnected);                  // 127.0.0.1:1 拒绝连接 → 记录错误但不抛
        Assert.False(string.IsNullOrEmpty(only.Error));
    }

    /// <summary>设置页的连接器列表：把 MCP 状态映射到行（含失败原因），手机端只列 HTTP 那条。</summary>
    [Fact]
    public async Task 设置页_列出连接器状态()
    {
        WriteMcpConfig();

        App.HeadHost = HostCapability.MobileOnly;
        var vm = App.BuildProvider(_dataDir).GetRequiredService<SettingsViewModel>();
        await vm.RefreshCapabilitiesCommand.ExecuteAsync(null);

        // 后台连接也在跑（BuildProvider 里发起的），列表内容以「HTTP 那条」为准。
        Assert.All(vm.McpServers, s => Assert.Equal("http-connector", s.Name));
    }
}
