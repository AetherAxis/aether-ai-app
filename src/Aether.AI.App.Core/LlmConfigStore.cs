using System.IO;
using System.Text.Json;

namespace Aether.AI.App.Core;

/// <summary>一份 LLM 连接档案（OpenAI-compatible）。经设置页 CRUD，落 llm-configs.json。</summary>
public sealed record LlmConfig(string Name, string? Endpoint, string ApiKey, string ModelId);

/// <summary>
/// LLM 档案运行时状态（单例）：配置列表 + 选中项，持久化到 &lt;dataDir&gt;/llm-configs.json。
/// <see cref="Changed"/> 在保存/切换后触发——消费方：客户端工厂（清缓存）、agent 目录（LLM 助手显隐）。
/// M2-lite：切换经 runner.Invalidate 生效；新增首份配置后 agent 池热刷新留后续。
/// </summary>
public sealed class LlmConfigState
{
    private readonly string _path;
    private readonly object _lock = new();

    public LlmConfigState(string path)
    {
        _path = path;
        Reload();
    }

    public IReadOnlyList<LlmConfig> Configs { get; private set; } = Array.Empty<LlmConfig>();

    /// <summary>选中的档案名（null = 无选中/无配置）。</summary>
    public string? SelectedName { get; private set; }

    public bool HasConfigs => Configs.Count > 0;

    /// <summary>配置或选中变更（保存/删除/切换后）。</summary>
    public event Action? Changed;

    public LlmConfig? Selected =>
        Configs.FirstOrDefault(c => c.Name == SelectedName) ?? Configs.FirstOrDefault();

    /// <summary>落盘并广播（原子替换列表 + 选中）。</summary>
    public void Save(IReadOnlyList<LlmConfig> configs, string? selectedName)
    {
        lock (_lock)
        {
            Configs = configs.ToList();
            SelectedName = selectedName is not null && configs.Any(c => c.Name == selectedName)
                ? selectedName
                : configs.FirstOrDefault()?.Name;
            LlmConfigStore.Save(_path, Configs, SelectedName);
        }
        Changed?.Invoke();
    }

    /// <summary>仅切选中（不重建列表）。</summary>
    public void Select(string name)
    {
        if (!Configs.Any(c => c.Name == name)) return;
        if (SelectedName == name) return;
        lock (_lock)
        {
            SelectedName = name;
            LlmConfigStore.Save(_path, Configs, name);
        }
        Changed?.Invoke();
    }

    private void Reload()
    {
        var (configs, selected) = LlmConfigStore.Load(_path);
        Configs = configs;
        SelectedName = selected;
    }
}

/// <summary>
/// LLM 档案存储（JSON 单文件）：[{name, endpoint, apiKey, modelId}] + selected。
/// 自 Core 的 LlmConfigStore 语义并入（保留静态读给早期组合用）。
/// </summary>
public static class LlmConfigStore
{
    private sealed record LlmConfigFile(List<LlmConfig> Configs, string? Selected);

    public static (IReadOnlyList<LlmConfig> Configs, string? Selected) Load(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                Protect(path);   // 自愈：旧版写的默认权限（644）也收紧到 0600
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
            Protect(path);
        }
        catch { /* 磁盘失败不阻断（best-effort） */ }
    }

    /// <summary>
    /// 密钥文件 0600（2026-10-26，对齐 ⑦ 凭据仓纪律）：文件含 API Key，仅属主可读写——
    /// llm-configs.json 与 CTP 的 ctp.db 同属凭据存放，不再用默认 644。Windows 无 Unix 文件模式（走 ACL），不动。
    /// </summary>
    private static void Protect(string path)
    {
        if (OperatingSystem.IsWindows()) return;
        try { File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite); }
        catch { /* best-effort——同落盘吞异常语义 */ }
    }
}
