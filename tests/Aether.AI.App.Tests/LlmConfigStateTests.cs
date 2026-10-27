using Aether.AI.App.Core;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Aether.AI.App.Tests;

/// <summary>
/// LLM 档案状态与 agent 目录：档案 CRUD 持久化/切换广播；
/// 「通用助手」由首次运行播种进 agents.json（普通 agent，与档案存亡无关）。
/// </summary>
public class LlmConfigStateTests : IDisposable
{
    private readonly string _dataDir;

    public LlmConfigStateTests()
    {
        _dataDir = Path.Combine(Path.GetTempPath(), $"aether-ai-app-llm-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dataDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dataDir, true); } catch { /* 临时目录 */ }
    }

    [Fact]
    public void Save_persists_and_roundtrips()
    {
        var path = Path.Combine(_dataDir, "llm-configs.json");
        var state = new LlmConfigState(path);
        Assert.False(state.HasConfigs);

        state.Save(new[] { new LlmConfig("GLM", "https://api.example.com/v1", "sk-x", "glm-4.7") }, "GLM");

        Assert.True(state.HasConfigs);
        Assert.Equal("GLM", state.SelectedName);
        var reloaded = new LlmConfigState(path);   // 新实例（模拟重启）
        Assert.Equal("glm-4.7", reloaded.Selected?.ModelId);
    }

    [Fact]
    public void Select_switches_and_broadcasts()
    {
        var state = new LlmConfigState(Path.Combine(_dataDir, "llm-configs.json"));
        state.Save(new[]
        {
            new LlmConfig("A", null, "sk-a", "m-a"),
            new LlmConfig("B", null, "sk-b", "m-b"),
        }, "A");

        var changed = 0;
        state.Changed += () => changed++;
        state.Select("B");
        Assert.Equal("B", state.SelectedName);
        Assert.Equal(1, changed);
        state.Select("B");   // 重复选择不再广播
        Assert.Equal(1, changed);
    }

    /// <summary>目录唯一来源是 agents.json：首次运行播种的「通用助手」是一条普通 agent——
    /// 不再随 LLM 档案存亡（无档案也在，只是跑起来会提示先配模型），档案增删也不影响它。</summary>
    [Fact]
    public async Task Catalog_holds_seeded_assistant_as_a_normal_agent()
    {
        var provider = App.BuildProvider(_dataDir);
        var catalog = provider.GetRequiredService<IAgentCatalog>();
        var state = provider.GetRequiredService<LlmConfigState>();

        var assistant = Assert.Single(await catalog.GetAllAsync());
        Assert.Equal("assistant", assistant.Id);
        Assert.Equal(string.Empty, assistant.ModelId);   // 空 = 走当前选中档案

        state.Save(new[] { new LlmConfig("GLM", null, "sk-x", "glm-4.7") }, "GLM");
        Assert.Single(await catalog.GetAllAsync(), a => a.Id == "assistant");   // 有档案也不重复
    }

    /// <summary>「通用助手」与自建 agent 同权：删掉后重启不复活（agents.json 存在即尊重用户）。</summary>
    [Fact]
    public void Deleted_assistant_is_not_resurrected_on_restart()
    {
        var provider = App.BuildProvider(_dataDir);
        var store = provider.GetRequiredService<CustomAgentStore>();
        Assert.Single(store.Agents);
        store.Save(Array.Empty<CustomAgent>());

        // 新实例 + 同一条播种逻辑（模拟重启）：文件已存在 → 不插手
        var reopened = new CustomAgentStore(Path.Combine(_dataDir, "agents.json"));
        Assert.False(reopened.SeedIfMissing(new[] { CustomAgentStore.FromLlmDefinition(DefaultAgents.LlmAssistant()) }));
        Assert.Empty(reopened.Agents);
    }

    /// <summary>工厂：离线构造（无网络）——选中档案决定客户端；切换后拿到新实例（缓存已清）。</summary>
    [Fact]
    public void Factory_builds_client_and_clears_cache_on_change()
    {
        var path = Path.Combine(_dataDir, "llm-configs.json");
        var state = new LlmConfigState(path);
        state.Save(new[]
        {
            new LlmConfig("A", null, "sk-a", "m-a"),
            new LlmConfig("B", null, "sk-b", "m-b"),
        }, "A");
        var factory = new AppLlmClientFactory(state);

        var c1 = factory.Create(null);
        Assert.NotNull(c1);
        Assert.Same(c1, factory.Create(null));          // 同档案 → 缓存同实例

        state.Select("B");
        var c2 = factory.Create(null);
        Assert.NotSame(c1, c2);                          // 切档案 → 新客户端

        state.Save(Array.Empty<LlmConfig>(), null);
        Assert.Throws<InvalidOperationException>(() => factory.Create(null));   // 档案清空 → 明确报错
    }
}
