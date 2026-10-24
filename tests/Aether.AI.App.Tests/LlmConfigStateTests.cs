using Aether.AI.App.Core;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Aether.AI.App.Tests;

/// <summary>
/// LLM 档案状态与动态 agent 目录：有档案 → 「通用助手」出现在目录（每次枚举求值）；
/// 选中切换/保存 → Changed 广播；持久化往返。
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

    /// <summary>动态目录：无档案时无「通用助手」；保存档案后（不重启、不重建 provider）即出现。</summary>
    [Fact]
    public async Task Catalog_gains_llm_assistant_when_config_saved()
    {
        var provider = App.BuildProvider(_dataDir);
        var catalog = provider.GetRequiredService<IAgentCatalog>();
        var state = provider.GetRequiredService<LlmConfigState>();

        var before = await catalog.GetAllAsync();
        Assert.DoesNotContain(before, a => a.Id == "assistant");   // 无档案 → 只有规则/审批演示

        state.Save(new[] { new LlmConfig("GLM", null, "sk-x", "glm-4.7") }, "GLM");

        var after = await catalog.GetAllAsync();
        var assistant = Assert.Single(after, a => a.Id == "assistant");
        Assert.Equal(string.Empty, assistant.ModelId);   // 空 = 动态走当前选中档案
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
