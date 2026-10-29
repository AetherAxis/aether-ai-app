using Aether.AI.App;
using Aether.AI.App.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Aether.AI.App.Tests;

/// <summary>agent 编辑窗「允许的工具」按类别分组的逻辑测试（走真实组合根；测试进程 = 手机宿主的可用集）：
/// 分组聚合（同实例/计数）、组头全选/全不选、子项部分勾选 → 组头不定态、载入既有 agent 后组头态正确。</summary>
public class ToolGroupingTests : IDisposable
{
    private readonly string _dataDir;

    public ToolGroupingTests()
    {
        _dataDir = Path.Combine(Path.GetTempPath(), $"aether-ai-app-toolgroups-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dataDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dataDir, true); } catch { /* 临时目录 */ }
    }

    private AgentsViewModel BuildVm()
        => App.BuildProvider(_dataDir, services =>
            services.AddSingleton<Aether.AI.App.Core.IUiDispatcher>(new InlineDispatcher()))
            .GetRequiredService<AgentsViewModel>();

    [Fact]
    public void 分组_按类别聚合计数正确()
    {
        var vm = BuildVm();

        Assert.NotEmpty(vm.ToolGroups);
        Assert.Equal(vm.ToolOptions.Count, vm.ToolGroups.Sum(g => g.Tools.Count));
        // 组内类别一致、组头带计数、组内与平铺集合是同一批实例
        foreach (var g in vm.ToolGroups)
        {
            Assert.All(g.Tools, t => Assert.Equal(g.Category, t.Category));
            Assert.Contains("（", g.Header);
            foreach (var t in g.Tools)
                Assert.Contains(t, vm.ToolOptions);
        }
    }

    [Fact]
    public void 组头勾选_全选与全不选子工具()
    {
        var vm = BuildVm();
        var group = vm.ToolGroups.First(g => g.Tools.Count >= 2);

        group.IsChecked = true;
        Assert.All(group.Tools, t => Assert.True(t.IsSelected));
        Assert.True(group.IsChecked!.Value);

        group.IsChecked = false;
        Assert.All(group.Tools, t => Assert.False(t.IsSelected));
        Assert.False(group.IsChecked!.Value);
    }

    [Fact]
    public void 子项部分勾选_组头不定态_再补全为真()
    {
        var vm = BuildVm();
        var group = vm.ToolGroups.First(g => g.Tools.Count >= 2);

        group.Tools[0].IsSelected = true;
        Assert.Null(group.IsChecked);   // 部分 → 不定态

        foreach (var t in group.Tools) t.IsSelected = true;
        Assert.True(group.IsChecked!.Value);   // 全选

        group.Tools[0].IsSelected = false;
        Assert.Null(group.IsChecked);   // 又回不定态
    }

    [Fact]
    public void 子项不定态不会误伤兄弟_部分选中保持原状()
    {
        var vm = BuildVm();
        var group = vm.ToolGroups.First(g => g.Tools.Count >= 3);

        group.IsChecked = false;                       // 全不选
        group.Tools[0].IsSelected = true;              // 只选一个
        Assert.Null(group.IsChecked);                  // 回推只改组头显示，不改子项
        Assert.True(group.Tools[0].IsSelected);
        Assert.All(group.Tools.Skip(1), t => Assert.False(t.IsSelected));   // 兄弟未被「全选」误伤
    }

    [Fact]
    public void 展开_默认收起且切换不影响勾选()
    {
        var vm = BuildVm();
        var group = vm.ToolGroups.First();

        Assert.False(group.IsExpanded);                // 默认收起
        group.IsChecked = true;                        // 收起状态下也能整组打勾
        group.IsExpanded = true;                       // 展开/收起不影响勾选态
        Assert.All(group.Tools, t => Assert.True(t.IsSelected));
        group.IsExpanded = false;
        Assert.All(group.Tools, t => Assert.True(t.IsSelected));
    }
}

/// <summary>同步执行 dispatcher（VM 构造期回推即时生效，测试断言免等待）。</summary>
file sealed class InlineDispatcher : Aether.AI.App.Core.IUiDispatcher
{
    public Task InvokeAsync(Action action) { action(); return Task.CompletedTask; }
    public void Post(Action action) => action();
}
