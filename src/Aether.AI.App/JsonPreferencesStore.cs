using System.IO;
using System.Text.Json;

namespace Aether.AI.App;

/// <summary>
/// <see cref="Aether.Workbench.IPreferencesStore"/> 的 JSON 文件实现（单文件字典）。
/// <see cref="GetAsync"/> 同步完成——引擎启动期 <c>RestoreThemeSync</c> 会在 UI 线程上
/// <c>GetAwaiter().GetResult()</c>，异步实现会死锁。<see cref="SetAsync"/> 同步落盘
/// （best-effort：失败吞）。自 ⑪ aether-desktop 逐字 port。
/// </summary>
public sealed class JsonPreferencesStore : Aether.Workbench.IPreferencesStore
{
    private readonly string _path;
    private readonly object _lock = new();
    private Dictionary<string, string?> _values;

    public JsonPreferencesStore(string path)
    {
        _path = path;
        _values = Load();
    }

    public Task<string?> GetAsync(string key, CancellationToken cancellationToken = default)
    {
        lock (_lock) return Task.FromResult(_values.TryGetValue(key, out var v) ? v : null);
    }

    public Task SetAsync(string key, string? value, CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            if (value is null) _values.Remove(key);
            else _values[key] = value;
            Save();
        }
        return Task.CompletedTask;
    }

    private Dictionary<string, string?> Load()
    {
        try
        {
            if (File.Exists(_path))
            {
                var dict = JsonSerializer.Deserialize<Dictionary<string, string?>>(File.ReadAllText(_path));
                return dict ?? new();
            }
        }
        catch { /* 损坏文件当空——首启重建 */ }
        return new();
    }

    private void Save()
    {
        try
        {
            File.WriteAllText(_path, JsonSerializer.Serialize(_values, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* 磁盘失败不阻断（best-effort） */ }
    }
}
