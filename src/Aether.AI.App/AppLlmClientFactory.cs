using System.Collections.Concurrent;
using Aether.AI.App.Core;
using Aether.AI.Brains.Abstractions;
using Microsoft.Extensions.AI;
using OpenAI;

namespace Aether.AI.App;

/// <summary>
/// 动态 <see cref="IChatClientFactory"/>（LLM 兼容缝——宿主自注册即走本路径，不用 AddLlmBrains 的固定 options）：
/// <paramref name="modelId"/> 为空（通用助手形态）→ 用 <see cref="LlmConfigState"/> 当前选中档案；
/// 非空且命中档案名 → 该档案。档案保存/切换时清缓存（<see cref="LlmConfigState.Changed"/>），
/// 配合 runner.Invalidate 即实现「设置页换模型，下一轮对话生效」。
/// 自 monorepo LlmConfigChatClientFactory port（去掉手机端 sanitize/重试包装——M2 后续按需回归）。
/// </summary>
public sealed class AppLlmClientFactory : IChatClientFactory
{
    private readonly LlmConfigState _state;
    private readonly ConcurrentDictionary<string, IChatClient> _clients = new();

    public AppLlmClientFactory(LlmConfigState state)
    {
        _state = state;
        _state.Changed += () => _clients.Clear();
    }

    public IChatClient Create(string? modelId)
    {
        var config = Resolve(modelId);
        if (config is null)
            throw new InvalidOperationException(
                "没有可用的 LLM 档案——请在设置页添加（name / endpoint / apiKey / model）。");
        return _clients.GetOrAdd(config.Name, _ => BuildClient(config));
    }

    /// <summary>按档案构建 OpenAI-compatible 客户端（工厂与设置页「测试连接」共用；离线构造，不发请求）。</summary>
    public static IChatClient BuildClient(LlmConfig config)
    {
        OpenAIClient client = string.IsNullOrWhiteSpace(config.Endpoint)
            ? new OpenAIClient(config.ApiKey)
            : new OpenAIClient(
                new System.ClientModel.ApiKeyCredential(config.ApiKey),
                new OpenAIClientOptions { Endpoint = new Uri(config.Endpoint!) });
        return client.GetChatClient(config.ModelId).AsIChatClient();
    }

    private LlmConfig? Resolve(string? modelId)
    {
        // 显式指定且命中档案名 → 该档案；否则当前选中（无选中 → 首份）。
        if (!string.IsNullOrWhiteSpace(modelId))
        {
            var named = _state.Configs.FirstOrDefault(c => c.Name == modelId);
            if (named is not null) return named;
        }
        return _state.Selected;
    }
}
