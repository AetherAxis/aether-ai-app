using Aether.AI.App.Core;
using Aether.AI.App.ViewModels;
using Aether.AI.App;
using Aether.AI.Core.Agents;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Aether.AI.App.Tests;

/// <summary>
/// 链上钱包的宿主配置（<c>&lt;dataDir&gt;/ledger.json</c>）：**配置文件 &gt; 环境变量 &gt; 开发默认**。
/// 手机端设不了环境变量，所以文件是唯一入口；桌面沿用同一份文件（想指本机节点就填 127.0.0.1）。
/// 本类改进程级环境变量，故整类串行。
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class LedgerSettingsCollection
{
    public const string Name = "LedgerSettings";
}

[Collection(LedgerSettingsCollection.Name)]
public class LedgerSettingsTests : IDisposable
{
    private readonly string _dataDir;
    private readonly string? _oldNode = Environment.GetEnvironmentVariable(LedgerSettings.NodeAddressEnv);
    private readonly string? _oldCustody = Environment.GetEnvironmentVariable(LedgerSettings.CustodyAddressEnv);

    public LedgerSettingsTests()
    {
        _dataDir = Path.Combine(Path.GetTempPath(), $"aether-ai-app-ledger-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dataDir);
        Environment.SetEnvironmentVariable(LedgerSettings.NodeAddressEnv, null);
        Environment.SetEnvironmentVariable(LedgerSettings.CustodyAddressEnv, null);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(LedgerSettings.NodeAddressEnv, _oldNode);
        Environment.SetEnvironmentVariable(LedgerSettings.CustodyAddressEnv, _oldCustody);
        try { Directory.Delete(_dataDir, true); } catch { /* 临时目录 */ }
    }

    /// <summary>什么都没有 → 开发默认本机节点；custody 视为未配置（充值工具会给出指引，而不是把钱转错地方）。</summary>
    [Fact]
    public void 未配置时回落开发默认()
    {
        var settings = LedgerSettings.Load(_dataDir);
        Assert.Equal(LedgerSettings.DefaultNodeAddress, settings.EffectiveNodeAddress);
        Assert.Null(settings.EffectiveCustodyAddress);
        Assert.Contains("开发默认", settings.NodeAddressSource);
    }

    /// <summary>只有环境变量（服务器/既有部署）→ 用环境变量；设置页要如实标出来源。</summary>
    [Fact]
    public void 环境变量次之且标出来源()
    {
        Environment.SetEnvironmentVariable(LedgerSettings.NodeAddressEnv, "10.0.0.9:27805");
        Environment.SetEnvironmentVariable(LedgerSettings.CustodyAddressEnv, "c1c1c1c1c1c1c1c1c1c1c1c1c1c1c1c1c1c1c1c1");
        var settings = LedgerSettings.Load(_dataDir);
        Assert.Equal("10.0.0.9:27805", settings.EffectiveNodeAddress);
        Assert.Equal("c1c1c1c1c1c1c1c1c1c1c1c1c1c1c1c1c1c1c1c1", settings.EffectiveCustodyAddress);
        Assert.Contains(LedgerSettings.NodeAddressEnv, settings.NodeAddressSource);
    }

    /// <summary>用户在应用里填了 → 压过环境变量（这是手机端唯一能配地址的通道，必须赢）。</summary>
    [Fact]
    public void 配置文件优先于环境变量且可落盘读回()
    {
        Environment.SetEnvironmentVariable(LedgerSettings.NodeAddressEnv, "10.0.0.9:27805");
        var settings = LedgerSettings.Load(_dataDir);
        settings.Save("ledger.example.com:17805", "d2d2d2d2d2d2d2d2d2d2d2d2d2d2d2d2d2d2d2d2");

        var reloaded = LedgerSettings.Load(_dataDir);
        Assert.Equal("ledger.example.com:17805", reloaded.EffectiveNodeAddress);
        Assert.Equal("ledger.json", reloaded.NodeAddressSource);
        Assert.Equal("d2d2d2d2d2d2d2d2d2d2d2d2d2d2d2d2d2d2d2d2", reloaded.EffectiveCustodyAddress);
    }

    /// <summary>保存空串 = 清掉该项（回落到环境变量/默认）——「我想改回自动」必须有出口。</summary>
    [Fact]
    public void 保存空串等于清除该项()
    {
        var settings = LedgerSettings.Load(_dataDir);
        settings.Save("10.0.0.5:17805", null);
        settings.Save("", "");

        var reloaded = LedgerSettings.Load(_dataDir);
        Assert.Null(reloaded.NodeAddress);
        Assert.Equal(LedgerSettings.DefaultNodeAddress, reloaded.EffectiveNodeAddress);
    }

    /// <summary>坏配置不该让 App 起不来：退回未配置。</summary>
    [Fact]
    public void 坏JSON不影响启动()
    {
        File.WriteAllText(Path.Combine(_dataDir, LedgerSettings.FileName), "{ this is not json");
        var settings = LedgerSettings.Load(_dataDir);
        Assert.Null(settings.NodeAddress);
        Assert.Equal(LedgerSettings.DefaultNodeAddress, settings.EffectiveNodeAddress);
    }

    /// <summary>设置页状态行：显示节点+来源+托管地址摘要（托管地址只显示前缀，避免整串铺在界面上）。</summary>
    [Fact]
    public void 状态行显示节点来源与托管地址摘要()
    {
        var settings = LedgerSettings.Load(_dataDir);
        settings.Save("10.0.0.5:17805", "abcdef1234567890abcdef1234567890abcdef12");
        var text = settings.Describe();
        Assert.Contains("10.0.0.5:17805", text);
        Assert.Contains("ledger.json", text);
        Assert.Contains("abcdef123456", text);
        Assert.DoesNotContain("abcdef1234567890abcdef1234567890abcdef12", text);
    }

    // —— 设置页 VM（两端同一份）——

    private sealed class InlineDispatcher : IUiDispatcher
    {
        public Task InvokeAsync(Action action) { action(); return Task.CompletedTask; }
        public void Post(Action action) => action();
    }

    private IServiceProvider Build() => App.BuildProvider(_dataDir, services =>
        services.AddSingleton<IUiDispatcher>(new InlineDispatcher()));

    /// <summary>VM 能显示当前生效值，保存后状态与生效值一起更新（手机端用户就靠这一行确认「连的是服务端」）。</summary>
    [Fact]
    public void 设置页_保存钱包配置后生效值刷新()
    {
        var vm = Build().GetRequiredService<SettingsViewModel>();
        Assert.True(vm.HasLedger);
        Assert.Contains(LedgerSettings.DefaultNodeAddress, vm.LedgerEffectiveText);
        Assert.EndsWith(LedgerSettings.SeedFileName, vm.WalletSeedPath);

        vm.FormLedgerNode = "http://ledger.example.com:17805";
        vm.FormLedgerCustody = "c3c3c3c3c3c3c3c3c3c3c3c3c3c3c3c3c3c3c3c3";
        vm.SaveLedgerSettingsCommand.Execute(null);

        Assert.Contains("已保存", vm.LedgerStatus);
        Assert.Contains("ledger.example.com:17805", vm.LedgerEffectiveText);
        Assert.Equal("http://ledger.example.com:17805", LedgerSettings.Load(_dataDir).NodeAddress);
    }

    /// <summary>填错当场报：节点地址解析不出、托管地址不是 40 位小写 hex——都不要等到真转账才发现。</summary>
    [Fact]
    public void 设置页_校验非法输入()
    {
        var vm = Build().GetRequiredService<SettingsViewModel>();

        vm.FormLedgerCustody = "NOT-A-HEX-ADDRESS";
        vm.SaveLedgerSettingsCommand.Execute(null);
        Assert.Contains("40 位小写 hex", vm.LedgerStatus);
        Assert.False(File.Exists(Path.Combine(_dataDir, LedgerSettings.FileName)));
    }
}
