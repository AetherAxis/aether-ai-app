using Aether.AI.App.Core;
using Aether.AI.App.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Aether.AI.App.Tests;

/// <summary>
/// 输入卡上方的模型选择器（2026-10-26 四改：选择与配置合并成一处）：
/// 下拉 = 全部档案 + 末项「管理模型…」哨兵。选档案 → 切换模型；选哨兵 → 只发管理意图、值弹回原档案。
/// 走真实组合根（App.BuildProvider），只把 dispatcher 换成内联实现。
/// </summary>
public class ModelPickerTests : IDisposable
{
    private readonly string _dataDir;

    public ModelPickerTests()
    {
        _dataDir = Path.Combine(Path.GetTempPath(), $"aether-ai-app-picker-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dataDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dataDir, true); } catch { /* 临时目录 */ }
    }

    private sealed class InlineDispatcher : IUiDispatcher
    {
        public Task InvokeAsync(Action action) { action(); return Task.CompletedTask; }
        public void Post(Action action) => action();
    }

    private (ChatViewModel Vm, LlmConfigState State) Create()
    {
        var provider = App.BuildProvider(_dataDir, services =>
            services.AddSingleton<IUiDispatcher>(new InlineDispatcher()));
        return (provider.GetRequiredService<ChatViewModel>(), provider.GetRequiredService<LlmConfigState>());
    }

    [Fact]
    public void No_configs_yields_only_the_manage_sentinel()
    {
        var (vm, _) = Create();

        var item = Assert.Single(vm.LlmPickerItems);
        Assert.True(item.IsManage);
        Assert.Null(vm.SelectedLlm);        // 空占位（PlaceholderText = 未配置模型）
        Assert.False(vm.HasLlmProfiles);
    }

    [Fact]
    public void Picker_lists_profiles_then_sentinel_with_current_selected()
    {
        var (vm, state) = Create();
        state.Save(new[]
        {
            new LlmConfig("A", null, "sk-a", "m-a"),
            new LlmConfig("B", null, "sk-b", "m-b"),
        }, "A");

        Assert.Equal(new[] { "A", "B", "管理模型…" }, vm.LlmPickerItems.Select(i => i.Name));
        Assert.Equal("A", vm.SelectedLlm?.Name);
        Assert.True(vm.HasLlmProfiles);
    }

    [Fact]
    public void Selecting_a_profile_switches_current_config()
    {
        var (vm, state) = Create();
        state.Save(new[]
        {
            new LlmConfig("A", null, "sk-a", "m-a"),
            new LlmConfig("B", null, "sk-b", "m-b"),
        }, "A");

        vm.SelectedLlm = vm.LlmPickerItems.First(i => i.Name == "B");

        Assert.Equal("B", state.SelectedName);
        Assert.Equal("B", vm.SelectedLlm?.Name);   // RefreshLlmProfiles 重建后仍指向新选中项
        Assert.True(vm.LlmPickerItems.First(i => i.Name == "B").IsCurrent);
    }

    [Fact]
    public void Selecting_the_sentinel_requests_manage_and_reverts_value()
    {
        var (vm, state) = Create();
        state.Save(new[]
        {
            new LlmConfig("A", null, "sk-a", "m-a"),
            new LlmConfig("B", null, "sk-b", "m-b"),
        }, "A");

        var manageRequests = 0;
        vm.ManageLlmRequested += () => manageRequests++;

        vm.SelectedLlm = vm.LlmPickerItems.First(i => i.IsManage);

        Assert.Equal(1, manageRequests);          // 宿主据此开配置窗
        Assert.Equal("A", state.SelectedName);    // 哨兵不切换档案
        Assert.Equal("A", vm.SelectedLlm?.Name);  // 下拉值弹回原档案（不显示「管理模型…」）
    }

    [Fact]
    public void Deleting_everything_falls_back_to_sentinel_only()
    {
        var (vm, state) = Create();
        state.Save(new[] { new LlmConfig("A", null, "sk-a", "m-a") }, "A");
        Assert.Equal("A", vm.SelectedLlm?.Name);

        state.Save(Array.Empty<LlmConfig>(), null);

        Assert.Single(vm.LlmPickerItems);
        Assert.Null(vm.SelectedLlm);
    }
}
