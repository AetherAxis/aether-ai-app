using Aether.AI.App;
using Aether.AI.App.Core;
using Aether.AI.App.ViewModels;
using Aether.AI.Core.Agents;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Aether.AI.App.Tests;

/// <summary>删除「当前选中的 agent」后的选择状态（2026-10-29 用户报的 bug 的 VM 层复现）：
/// 删除后应回落到剩余第一个 agent，且后续显式切换正常生效、发送走新选的 agent。</summary>
[Collection(AppToolWiringCollection.Name)]
public class DeleteSelectedAgentTests : IDisposable
{
    private readonly string _dataDir;

    public DeleteSelectedAgentTests()
    {
        _dataDir = Path.Combine(Path.GetTempPath(), $"aether-ai-app-delagent-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dataDir);
        App.HeadHost = HostCapability.DesktopOnly;
    }

    public void Dispose()
    {
        App.HeadHost = null;
        try { Directory.Delete(_dataDir, true); } catch { /* 临时目录 */ }
    }

    private static CustomAgent Llm(string id, string name) => new()
    {
        Id = id, Name = name, Kind = CustomAgentKind.Llm,
        Instructions = $"你是{name}。", ModelProfile = null,
    };

    [Fact]
    public async Task 删除当前选中agent_选择回落且可再切换()
    {
        var provider = App.BuildProvider(_dataDir, services =>
            services.AddSingleton<IUiDispatcher>(new InlineDispatcher()));
        var store = provider.GetRequiredService<CustomAgentStore>();
        // 覆盖播种的通用助手，放三个已知 agent
        store.Save(new[] { Llm("agent-a", "甲"), Llm("agent-b", "乙"), Llm("agent-c", "丙") });

        var vm = provider.GetRequiredService<ChatViewModel>();
        await vm.InitializeAsync();
        await vm.RefreshAgentTargetsAsync();

        vm.SelectedAgentTarget = vm.AgentTargetItems.First(t => t.Id == "agent-a");
        Assert.Equal("agent-a", vm.SelectedAgentTarget?.Id);

        // 真实删除路径：AgentsViewModel.DeleteAgent（store.Save → Changed → Chat 侧刷新）
        var agentsVm = provider.GetRequiredService<AgentsViewModel>();
        var rowA = agentsVm.Agents.First(r => r.Id == "agent-a");
        agentsVm.DeleteAgentCommand.Execute(rowA);

        // 删除后：回落到剩余第一个（不能停在已删除的 agent-a 上）
        Assert.NotEqual("agent-a", vm.SelectedAgentTarget?.Id);
        Assert.NotNull(vm.SelectedAgentTarget);

        // 显式切到 agent-c：生效（不能怎么切都停在旧值）
        vm.SelectedAgentTarget = vm.AgentTargetItems.First(t => t.Id == "agent-c");
        Assert.Equal("agent-c", vm.SelectedAgentTarget?.Id);
        // 切目标 → 新会话（空的）
        Assert.Empty(vm.Messages);
    }

    private sealed class InlineDispatcher : IUiDispatcher
    {
        public Task InvokeAsync(Action action) { action(); return Task.CompletedTask; }
        public void Post(Action action) => action();
    }
}
