using System.IO;
using System.Text.Json;

namespace Aether.AI.App.Core;

/// <summary>一份 LLM 连接档案（OpenAI-compatible）。M1 从 JSON 文件加载；M2 换 UI 编辑。</summary>
public sealed record LlmConfig(string Name, string? Endpoint, string ApiKey, string ModelId);

/// <summary>
/// LLM 档案存储（JSON 单文件）：&lt;dataDir&gt;/llm-configs.json——[{name, endpoint, apiKey, modelId}] + selected。
/// M1：启动加载、组合根据此决定是否注册 LLM 大脑（选中的档案进 AddLlmBrains）；M2：设置页 CRUD + 运行中切换（Invalidate 链）。
/// </summary>
public sealed class LlmConfigStore
{
    private sealed record LlmConfigFile(List<LlmConfig> Configs, string? Selected);

    public static (IReadOnlyList<LlmConfig> Configs, string? Selected) Load(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                var file = JsonSerializer.Deserialize<LlmConfigFile>(File.ReadAllText(path));
                if (file?.Configs is { Count: > 0 })
                    return (file.Configs, file.Selected ?? file.Configs[0].Name);
            }
        }
        catch { /* 损坏文件当空——首启重建 */ }
        return (Array.Empty<LlmConfig>(), null);
    }

    public static void Save(string path, IReadOnlyList<LlmConfig> configs, string? selected)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(new LlmConfigFile(configs.ToList(), selected),
                new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* 磁盘失败不阻断（best-effort） */ }
    }
}
