using System.Net.Http;
using Aether.AI.App.Core;
using Aether.AI.Abstractions;
using Aether.TaskHall.Client;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Aether.AI.App.ViewModels;

/// <summary>
/// 设置页「任务堂」卡（M2，DESIGN §5 的 ⑮ 纯终端接线）：粘设备号凭证 → 测试连接 →
/// 写入 MCP 连接器（mcp-servers/taskhall.json——⑫ 连接器重启后自动装载 task_list/claim/submit 工具）。
/// 凭证在任务堂 web 端「设备」页创建（secret 只显示一次）；本卡只存不发。M3 的 TaskDispatchService
/// 读同一份 TaskHallSettingsStore 收派遣。
/// </summary>
public partial class TaskHallSettingsViewModel : ObservableObject
{
    private readonly TaskHallSettingsStore _store;
    private readonly string? _mcpServersDirectory;

    [ObservableProperty] private string _url = string.Empty;
    [ObservableProperty] private string _deviceNumber = string.Empty;
    [ObservableProperty] private string _secret = string.Empty;
    [ObservableProperty] private string _statusText = string.Empty;
    [ObservableProperty] private bool _isTesting;

    public TaskHallSettingsViewModel(TaskHallSettingsStore store, AiCapabilityPaths? capabilities)
    {
        _store = store;
        _mcpServersDirectory = capabilities?.McpServersDirectory;
        var s = store.Load();
        Url = s.Url;
        DeviceNumber = s.DeviceNumber;
        Secret = s.Secret;
    }

    [RelayCommand]
    private void Save()
    {
        if (string.IsNullOrWhiteSpace(Url) || string.IsNullOrWhiteSpace(Secret))
        {
            StatusText = "URL 和 secret 不能为空（设备号可选，仅作显示对照）";
            return;
        }
        _store.Save(new TaskHallSettings
        {
            Url = Url.Trim().TrimEnd('/'),
            DeviceNumber = DeviceNumber.Trim(),
            Secret = Secret.Trim(),
        });
        StatusText = "已保存。";
    }

    /// <summary>测试连接：设备 Bearer 调 /api/device/me——验证 URL + secret + 未吊销三件事一次过。</summary>
    [RelayCommand]
    private async Task TestConnectionAsync()
    {
        if (string.IsNullOrWhiteSpace(Url) || string.IsNullOrWhiteSpace(Secret))
        {
            StatusText = "先填 URL 和 secret。";
            return;
        }
        IsTesting = true;
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri(Url.Trim().TrimEnd('/') + "/"), Timeout = TimeSpan.FromSeconds(10) };
            var client = new TaskHallClient(http);
            client.UseDeviceCredentials(Secret.Trim());
            var me = await client.GetDeviceMeAsync().ConfigureAwait(true);
            StatusText = me is null
                ? "连上了，但接口返回空——检查任务堂版本。"
                : $"✓ 已连接：{me.DeviceNumber}（{me.Name}）· 归属用户 {me.OwnerUserId}";
        }
        catch (Exception ex)
        {
            StatusText = $"✗ 连接失败：{ex.Message}";
        }
        finally { IsTesting = false; }
    }

    /// <summary>写入 MCP 连接器：⑫ AddMcpServersFromDirectory 目录下的 taskhall.json
    /// （mcpServers 映射格式，与 Claude Code/Cursor 同构）。重启 app 后 task_* 四工具进池。</summary>
    [RelayCommand]
    private void WriteConnector()
    {
        if (string.IsNullOrWhiteSpace(Url) || string.IsNullOrWhiteSpace(Secret))
        {
            StatusText = "先填 URL 和 secret。";
            return;
        }
        if (_mcpServersDirectory is null)
        {
            StatusText = "本宿主无连接器目录（能力未启用）。";
            return;
        }
        Save();
        var cfg = new McpServerConfig
        {
            Name = "taskhall",
            Url = Url.Trim().TrimEnd('/'),
            Headers = new Dictionary<string, string> { ["Authorization"] = $"Bearer {Secret.Trim()}" },
        };
        Directory.CreateDirectory(_mcpServersDirectory);
        File.WriteAllText(Path.Combine(_mcpServersDirectory, "taskhall.json"), cfg.ToJson());
        StatusText = "✓ 已写入连接器（taskhall.json）——重启 app 后任务工具（task_list/claim/submit/detail）自动进池。";
    }
}
