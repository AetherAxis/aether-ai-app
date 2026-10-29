using Aether.AI.App.Browser;
using Aether.AI.App.Core;
using Aether.Browser.AI;
using Xunit;

namespace Aether.AI.App.Tests;

/// <summary>BrowserViewModel 投影/过滤测试（fake service + 内联 dispatcher，不起 Playwright、不初始化 Avalonia）。</summary>
public class BrowserViewModelTests
{
    private sealed class InlineDispatcher : IUiDispatcher
    {
        public Task InvokeAsync(Action action) { action(); return Task.CompletedTask; }
        public void Post(Action action) => action();
    }

    private static NetworkRequestItem Req(Guid sessionId, string url, int? status, bool isError = false) =>
        new(Guid.NewGuid(), sessionId, DateTimeOffset.UtcNow, "GET", url) { Status = status, IsError = isError };

    private static (FakeBrowserService Fake, FakeBrowserService.FakeSession Session, BrowserViewModel Vm) Build(
        BrowserSettingsStore? settings = null)
    {
        var fake = new FakeBrowserService();
        var session = (FakeBrowserService.FakeSession)fake.OpenSession("https://a.test/");
        var vm = new BrowserViewModel(fake, new InlineDispatcher(), settings ?? new BrowserSettingsStore(null));
        fake.RaiseRequests(session.Id);
        return (fake, session, vm);
    }

    /// <summary>往活跃会话塞请求记录并通知 VM 重建（模拟 Playwright 事件到达）。</summary>
    private static void Seed(FakeBrowserService fake, Guid sessionId, params NetworkRequestItem[] items)
    {
        fake.RequestsMap[sessionId] = items.ToList();
        fake.RaiseRequests(sessionId);
    }

    [Fact]
    public void 会话经构造投影进标签条()
    {
        var (_, session, vm) = Build();

        var item = Assert.Single(vm.Sessions);
        Assert.Equal(session.Id, item.Id);
        Assert.True(item.IsActive);
        Assert.Equal(session.Id, vm.ActiveSession?.Id);
    }

    [Fact]
    public void 请求经事件进列表并计数()
    {
        var (fake, session, vm) = Build();
        Seed(fake, session.Id,
            Req(session.Id, "https://a.test/1", 200),
            Req(session.Id, "https://a.test/2", 500, isError: true));

        Assert.Equal(2, vm.Requests.Count);
        Assert.Equal(2, vm.TotalCount);
        Assert.Equal(2, vm.FilteredRequests.Count);
        Assert.Equal(2, vm.FilteredCount);
    }

    [Fact]
    public void 过滤_关键词与状态段叠加()
    {
        var (fake, session, vm) = Build();
        Seed(fake, session.Id,
            Req(session.Id, "https://a.test/ok", 200),
            Req(session.Id, "https://a.test/boom", 500, isError: true),
            Req(session.Id, "https://b.test/nope", 404, isError: true));

        vm.FilterText = "a.test";
        Assert.Equal(2, vm.FilteredCount);

        vm.SetStatusFilterCommand.Execute("err");
        Assert.Single(vm.FilteredRequests);
        Assert.Equal("https://a.test/boom", vm.FilteredRequests[0].Url);

        vm.FilterText = null;   // 只剩状态段：两条错误（500 + 404）
        Assert.Equal(2, vm.FilteredCount);

        vm.SetStatusFilterCommand.Execute("");   // 段清空 = 全部
        Assert.Equal(3, vm.FilteredCount);
    }

    [Fact]
    public void 新建会话命令转发service()
    {
        var (fake, _, vm) = Build();
        vm.NewSessionCommand.Execute(null);
        Assert.Contains("about:blank", fake.OpenedUrls);
    }

    [Fact]
    public void 地址栏导航_非完整URL自动补https()
    {
        var (fake, session, vm) = Build();
        vm.AddressBar = "example.com/login";
        vm.NavigateCommand.Execute(null);
        Assert.Contains((session.Id, "https://example.com/login"), fake.Navigations);
    }

    [Fact]
    public void 启动模式_初始值来自store并同步service默认()
    {
        var store = new BrowserSettingsStore(null);
        store.Save(BrowserLaunchMode.Attach);
        var fake = new FakeBrowserService();
        var session = (FakeBrowserService.FakeSession)fake.OpenSession("https://a.test/");
        var vm = new BrowserViewModel(fake, new InlineDispatcher(), store);

        Assert.Equal(BrowserLaunchMode.Attach, vm.LaunchMode);
        Assert.Equal(BrowserLaunchMode.Attach, fake.DefaultLaunchMode);   // ctor 同步到 service
    }

    [Fact]
    public void 切换启动模式_写store并应用service默认()
    {
        var (fake, _, vm) = Build();   // 默认 Launch
        var store = new BrowserSettingsStore(null);
        // Build 用的是内存 store；这里直接驱动命令验证链路：命令 → store + service
        var vm2 = new BrowserViewModel(fake, new InlineDispatcher(), store);

        vm2.SetLaunchModeCommand.Execute(BrowserLaunchMode.Attach);

        Assert.Equal(BrowserLaunchMode.Attach, vm2.LaunchMode);
        Assert.Equal(BrowserLaunchMode.Attach, fake.DefaultLaunchMode);
        Assert.Equal(BrowserLaunchMode.Attach, store.Settings.Mode);
    }
}
