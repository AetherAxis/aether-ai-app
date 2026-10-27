using Aether.AI.App;
using Aether.AI.App.Core;
using Aether.AI.App.ViewModels;
using Aether.AI.Core.Agents;
using Aether.AI.Harness.Agents;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Aether.AI.App.Tests;

/// <summary>
/// 长期记忆 / 工作笔记开关：agents.json 里的布尔 → ⑫ <see cref="AgentDefinition"/> → 运行时 provider，
/// 且落盘根目录在 App 数据目录下（手机与桌面同一套路径，换端接着用）。
/// </summary>
public class MemoryToggleTests : IDisposable
{
    private readonly string _dataDir;

    public MemoryToggleTests()
    {
        _dataDir = Path.Combine(Path.GetTempPath(), $"aether-ai-app-memory-{Guid.NewGuid():N}");
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

    private IServiceProvider Build() => App.BuildProvider(_dataDir, services =>
        services.AddSingleton<IUiDispatcher>(new InlineDispatcher()));

    /// <summary>工作笔记的根落在数据目录下（⑫ 再按 agent Id 拼一层）；不给根 = 能力被静默禁用。</summary>
    [Fact]
    public void 工作笔记根目录_落在数据目录下()
    {
        var factory = Build().GetRequiredService<AgentFactory>();
        Assert.Equal(Path.Combine(_dataDir, "agent-notes"), factory.FileMemoryRoot);
    }

    /// <summary>定义映射：两个开关原样带到 ⑫ 的定义上（关着就是关着，不会被默认值偷偷打开）。</summary>
    [Fact]
    public void 自定义agent_记忆开关映射到定义()
    {
        var on = CustomAgentStore.ToDefinition(new CustomAgent
        {
            Id = "custom-mem", Name = "记事员", Kind = CustomAgentKind.Llm,
            Instructions = "记录并复用上下文", EnableLongTermMemory = true, EnableFileMemory = true,
        });
        Assert.True(on.EnableLongTermMemory);
        Assert.True(on.EnableFileMemory);

        var off = CustomAgentStore.ToDefinition(new CustomAgent
        {
            Id = "custom-plain", Name = "普通", Kind = CustomAgentKind.Llm, Instructions = "x",
        });
        Assert.False(off.EnableLongTermMemory);
        Assert.False(off.EnableFileMemory);

        // 反向（出厂定义 → 可编辑记录）也要带上，否则编辑一次就把开关丢了。
        var back = CustomAgentStore.FromLlmDefinition(on);
        Assert.True(back.EnableLongTermMemory);
        Assert.True(back.EnableFileMemory);
    }

    /// <summary>端到端（管理窗）：勾上 → 保存 → 落盘读回为 true，列表行出现「记忆」标。</summary>
    [Fact]
    public async Task 管理窗_勾选后保存落盘并显示记忆标()
    {
        var vm = Build().GetRequiredService<AgentsViewModel>();

        vm.NewAgentCommand.Execute(null);
        vm.FormName = "记事员";
        vm.FormInstructions = "记住我说过的偏好";
        vm.FormLongTermMemory = true;
        await vm.SaveAgentCommand.ExecuteAsync(null);

        var saved = Assert.Single(vm.Agents, a => a.Name == "记事员");
        Assert.True(saved.HasMemory);
        Assert.True(saved.Source!.EnableLongTermMemory);
        Assert.False(saved.Source.EnableFileMemory);

        // 重新读盘（新 provider = 新 store），确认真的落进 agents.json。
        var reloaded = Build().GetRequiredService<AgentsViewModel>();
        var again = Assert.Single(reloaded.Agents, a => a.Name == "记事员");
        Assert.True(again.Source!.EnableLongTermMemory);
    }

    /// <summary>聊天目录里因此拿到的定义也带开关——这是运行期真正生效的那份。</summary>
    [Fact]
    public async Task 目录里的定义_带记忆开关()
    {
        var provider = Build();
        var vm = provider.GetRequiredService<AgentsViewModel>();
        vm.NewAgentCommand.Execute(null);
        vm.FormName = "记事员";
        vm.FormInstructions = "记住偏好";
        vm.FormLongTermMemory = true;
        await vm.SaveAgentCommand.ExecuteAsync(null);

        var catalog = provider.GetRequiredService<IAgentCatalog>();
        var def = Assert.Single(await catalog.GetAllAsync(), d => d.Name == "记事员");
        Assert.True(def.EnableLongTermMemory);
    }
}
