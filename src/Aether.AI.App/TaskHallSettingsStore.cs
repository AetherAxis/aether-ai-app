using System.Text.Json;

namespace Aether.AI.App;

/// <summary>任务堂终端凭证（⑱ aether-taskhall，DESIGN §2 设备面）：URL + 设备号 + secret。
/// 落数据目录 taskhall.json——与 llm-configs.json 同一威胁模型（本机明文，沙箱内可接受）。
/// 设备号/secret 在任务堂 web 端「设备」页创建（secret 只显示一次），粘到这里。</summary>
public sealed class TaskHallSettings
{
    public string Url { get; set; } = "http://127.0.0.1:5000";
    public string DeviceNumber { get; set; } = "";
    public string Secret { get; set; } = "";
}

public sealed class TaskHallSettingsStore(string path)
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public TaskHallSettings Load()
    {
        try
        {
            if (File.Exists(path))
                return JsonSerializer.Deserialize<TaskHallSettings>(File.ReadAllText(path)) ?? new TaskHallSettings();
        }
        catch { /* 损坏则回默认——凭证要重粘，但不崩设置页 */ }
        return new TaskHallSettings();
    }

    public void Save(TaskHallSettings settings) =>
        File.WriteAllText(path, JsonSerializer.Serialize(settings, Json));
}
