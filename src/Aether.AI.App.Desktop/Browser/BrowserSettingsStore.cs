using System.Text.Json;
using System.Text.Json.Serialization;
using Aether.Browser.AI;

namespace Aether.AI.App.Browser;

/// <summary>浏览器宿主策略（落 <c>&lt;dataDir&gt;/browser.json</c>）。字段语义见 <see cref="Mode"/>。</summary>
public sealed class BrowserSettings
{
    /// <summary>新建会话的默认启动方式——对「下一次新建会话」生效（运行中会话不动，模式是会话的出生属性）。
    /// 两种模式共用同一 profile 目录，登录态互通，来回切零成本。</summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public BrowserLaunchMode Mode { get; set; } = BrowserLaunchMode.Launch;
}

/// <summary>浏览器设置的落盘 store（原子写 tmp→rename，同 llm-configs/agents 惯例）。
/// path=null = 纯内存（测试用）。改动的应用链：面板 chip → Save + service.DefaultLaunchMode（即时生效于后续新建）。</summary>
public sealed class BrowserSettingsStore
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };
    private readonly string? _path;
    private readonly object _lock = new();

    public BrowserSettings Settings { get; private set; } = new();

    /// <summary>设置变更（保存后触发）。</summary>
    public event Action? Changed;

    public BrowserSettingsStore(string? path)
    {
        _path = path;
        if (path is null) return;
        try
        {
            if (File.Exists(path))
                Settings = JsonSerializer.Deserialize<BrowserSettings>(File.ReadAllText(path)) ?? new();
        }
        catch { /* 坏文件当默认 */ }
    }

    public void Save(BrowserLaunchMode mode)
    {
        lock (_lock)
        {
            Settings = new BrowserSettings { Mode = mode };
            if (_path is not null)
            {
                var tmp = _path + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(Settings, JsonOpts));
                File.Move(tmp, _path, overwrite: true);
            }
        }
        Changed?.Invoke();
    }
}
