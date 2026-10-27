using System.Collections.ObjectModel;
using Aether.AI.App.Core;
using Aether.AI.Abstractions;
using Aether.AI.Core.Agents;
using Aether.AI.Harness.Skills;
using Aether.AI.Mcp;
using Aether.Workbench;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.AI;
using OpenAI;

namespace Aether.AI.App.ViewModels;

/// <summary>档案列表行。</summary>
public sealed partial class LlmConfigItemVM : ObservableObject
{
    public required string Name { get; init; }
    [ObservableProperty] private string _modelId = string.Empty;
    [ObservableProperty] private string? _endpoint;
    [ObservableProperty] private bool _isCurrent;
    public IRelayCommand? SelectCommand { get; set; }
    public IRelayCommand? EditCommand { get; set; }
    public IRelayCommand? DeleteCommand { get; set; }
}

/// <summary>技能包行（只读展示：名称/分类/描述）。正文由 agent 运行时按需 load，UI 不展开。</summary>
public sealed record SkillRowVM(string Name, string Category, string Description);

/// <summary>连接器行：状态 + 工具数（或失败原因）。</summary>
public sealed record McpServerRowVM(string Name, string Transport, bool IsConnected, string Detail);

/// <summary>
/// 设置页 VM：LLM 档案 CRUD + 选中切换（换模型免重启——state.Select + runner.Invalidate，下一轮对话生效）
/// + 测试连接（用表单当前值直连一轮）。密钥只进 <see cref="FormApiKey"/> 与 llm-configs.json，不进列表显示。
/// </summary>
public partial class SettingsViewModel : ObservableObject
{
    private readonly LlmConfigState _state;
    private readonly IAgentRunner _runner;
    private readonly AiCapabilityPaths? _capabilities;
    private readonly SkillRegistry? _skills;
    private readonly IMcpManagementService? _mcp;
    private readonly bool _isDesktop;

    public ObservableCollection<SkillRowVM> Skills { get; } = new();
    public ObservableCollection<McpServerRowVM> McpServers { get; } = new();

    [ObservableProperty] private string _capabilitiesStatus = string.Empty;

    public ObservableCollection<LlmConfigItemVM> Configs { get; } = new();

    // —— 表单（编辑态：EditingName=null 为新增）——
    [ObservableProperty] private string? _editingName;
    [ObservableProperty] private string _formName = string.Empty;
    [ObservableProperty] private string _formModelId = string.Empty;
    [ObservableProperty] private string? _formEndpoint;
    [ObservableProperty] private string _formApiKey = string.Empty;
    [ObservableProperty] private string _statusText = string.Empty;
    [ObservableProperty] private bool _isTesting;

    public bool IsEditingExisting => EditingName is not null;
    public string SaveButtonText => IsEditingExisting ? "保存修改" : "添加档案";
    partial void OnEditingNameChanged(string? value)
    {
        OnPropertyChanged(nameof(IsEditingExisting));
        OnPropertyChanged(nameof(SaveButtonText));
    }

    /// <summary>⑩ 工作台 shell——设置页的「外观」区（主题列表 + 重置布局）从这里取宿主能力。
    /// 旧版这些在 rail 底部齿轮的 flyout 里；底部入口改成直达设置页后合并进来。null = 无工作台形态（隐藏外观区）。</summary>
    public WorkbenchShellViewModel? Shell { get; }

    public SettingsViewModel(IServiceProvider serviceProvider)
    {
        _state = serviceProvider.GetRequiredService<LlmConfigState>();
        _runner = serviceProvider.GetRequiredService<IAgentRunner>();
        Shell = serviceProvider.GetService<WorkbenchShellViewModel>();
        // 能力目录（技能包 / 连接器）：手机桌面同一份布局；缺失时（老档案/测试）整个区块隐藏。
        _capabilities = serviceProvider.GetService<AiCapabilityPaths>();
        _skills = serviceProvider.GetService<SkillRegistry>();
        _mcp = serviceProvider.GetService<IMcpManagementService>();
        // 宿主类型从工具注册表读（同一份判定源：⑫ ToolRegistry.CurrentHost → AetherAIOptions.Host）。
        _isDesktop = serviceProvider.GetService<Aether.AI.Harness.Tools.ToolRegistry>()?.CurrentHost
                     != HostCapability.MobileOnly;
        _state.Changed += () => Reload();
        Reload();
    }

    private void Reload()
    {
        Configs.Clear();
        foreach (var c in _state.Configs)
        {
            Configs.Add(new LlmConfigItemVM
            {
                Name = c.Name,
                ModelId = c.ModelId,
                Endpoint = c.Endpoint,
                IsCurrent = c.Name == _state.SelectedName,
                SelectCommand = SelectConfigCommand,
                EditCommand = EditConfigCommand,
                DeleteCommand = DeleteConfigCommand,
            });
        }
        OnPropertyChanged(nameof(HasConfigs));
    }

    public bool HasConfigs => Configs.Count > 0;

    /// <summary>状态行是否有内容（无内容时整行不占位——空 TextBlock 会白占一行高度）。</summary>
    public bool HasStatus => !string.IsNullOrWhiteSpace(StatusText);
    partial void OnStatusTextChanged(string value) => OnPropertyChanged(nameof(HasStatus));

    [RelayCommand]
    private void NewConfig()
    {
        EditingName = null;
        FormName = string.Empty;
        FormModelId = string.Empty;
        FormEndpoint = null;
        FormApiKey = string.Empty;
        StatusText = string.Empty;
    }

    [RelayCommand]
    private void EditConfig(LlmConfigItemVM item)
    {
        var c = _state.Configs.FirstOrDefault(x => x.Name == item.Name);
        if (c is null) return;
        EditingName = c.Name;
        FormName = c.Name;
        FormModelId = c.ModelId;
        FormEndpoint = c.Endpoint;
        FormApiKey = c.ApiKey;   // 编辑态回显密钥（本地单用户 App；显示侧仍不落日志）
        StatusText = string.Empty;
    }

    [RelayCommand]
    private void SaveConfig()
    {
        var name = FormName.Trim();
        if (name.Length == 0) { StatusText = "❌ 名称必填"; return; }
        if (string.IsNullOrWhiteSpace(FormModelId)) { StatusText = "❌ 模型必填（如 glm-4.7）"; return; }
        if (string.IsNullOrWhiteSpace(FormApiKey)) { StatusText = "❌ API Key 必填"; return; }

        var updated = new LlmConfig(name, string.IsNullOrWhiteSpace(FormEndpoint) ? null : FormEndpoint.Trim(), FormApiKey.Trim(), FormModelId.Trim());
        var list = _state.Configs.Where(c => c.Name != name).ToList();
        list.Add(updated);
        // 编辑既有档案 → 保持选中；新增且此前无选中 → 设为选中。
        var selected = _state.SelectedName == name || _state.SelectedName is null ? name : _state.SelectedName;
        _state.Save(list, selected);

        // 换了模型/密钥 → 失效已构建 agent，下一轮用新客户端（客户端缓存已被 Changed 清空）。
        _runner.Invalidate("assistant");
        EditingName = name;
        StatusText = $"✅ 已保存「{name}」";
    }

    [RelayCommand]
    private void DeleteConfig(LlmConfigItemVM item)
    {
        var list = _state.Configs.Where(c => c.Name != item.Name).ToList();
        _state.Save(list, _state.SelectedName == item.Name ? null : _state.SelectedName);
        _runner.Invalidate("assistant");
        if (EditingName == item.Name) NewConfig();
        StatusText = $"已删除「{item.Name}」";
    }

    /// <summary>设为当前：下一轮对话即用该档案（agent 失效重建 + 客户端缓存清空）。</summary>
    [RelayCommand]
    private void SelectConfig(LlmConfigItemVM item)
    {
        _state.Select(item.Name);
        _runner.Invalidate("assistant");
        StatusText = $"已切换到「{item.Name}」——下一轮对话生效";
    }

    /// <summary>用**表单当前值**直连测试（未保存也可测）：一轮极小请求验证 endpoint/key/model。</summary>
    [RelayCommand]
    private async Task TestConnectionAsync()
    {
        if (IsTesting) return;
        if (string.IsNullOrWhiteSpace(FormModelId) || string.IsNullOrWhiteSpace(FormApiKey))
        {
            StatusText = "❌ 先填模型与 API Key 再测试";
            return;
        }
        IsTesting = true;
        StatusText = "测试中…";
        try
        {
            var config = new LlmConfig(
                FormName.Trim().Length > 0 ? FormName.Trim() : "test",
                string.IsNullOrWhiteSpace(FormEndpoint) ? null : FormEndpoint.Trim(),
                FormApiKey.Trim(), FormModelId.Trim());
            var client = AppLlmClientFactory.BuildClient(config);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var response = await client.GetResponseAsync(
                new[] { new ChatMessage(ChatRole.User, "请只回复两个字：正常") },
                cancellationToken: cts.Token);
            StatusText = $"✅ 连接成功：{response.Text}";
        }
        catch (Exception ex)
        {
            StatusText = $"❌ {ex.Message}";
        }
        finally
        {
            IsTesting = false;
        }
    }

    // ── 技能包 / 连接器 ───────────────────────────────────────────────

    public bool HasCapabilities => _capabilities is not null;

    /// <summary>「本机能跑什么」的一句话说明——手机端会明说脚本与 stdio 连接器不可用（不做静默降级）。</summary>
    public string HostCapabilityText => _isDesktop
        ? "桌面：技能包脚本可执行，连接器支持 stdio / HTTP"
        : "手机：技能包正文可用（脚本不可执行），连接器仅 HTTP";

    public string SkillsDirectory => _capabilities?.SkillsDirectory ?? string.Empty;
    public string McpServersDirectory => _capabilities?.McpServersDirectory ?? string.Empty;

    public bool HasSkills => Skills.Count > 0;
    public bool HasMcpServers => McpServers.Count > 0;
    public bool HasCapabilitiesStatus => !string.IsNullOrWhiteSpace(CapabilitiesStatus);

    partial void OnCapabilitiesStatusChanged(string value) => OnPropertyChanged(nameof(HasCapabilitiesStatus));

    /// <summary>
    /// 刷新两块列表：技能包重扫目录（ReloadFromDirectory——与运行时同一个 SKILL.md 解析器，
    /// 新增/编辑/删除后无需重启）；连接器先确保已连接再取状态。
    /// </summary>
    [RelayCommand]
    private async Task RefreshCapabilitiesAsync()
    {
        if (!HasCapabilities) return;
        CapabilitiesStatus = "刷新中…";
        try
        {
            if (_skills is not null)
            {
                _skills.ReloadFromDirectory(SkillsDirectory);
                Skills.Clear();
                foreach (var meta in _skills.GetAllMetas())
                    Skills.Add(new SkillRowVM(meta.Name, meta.Category, meta.Description));
            }

            if (_mcp is not null)
            {
                await _mcp.EnsureStartedAsync();
                var servers = await _mcp.GetServersAsync();
                McpServers.Clear();
                foreach (var s in servers)
                    McpServers.Add(new McpServerRowVM(s.Name, s.Transport, s.IsConnected,
                        s.IsConnected ? $"{s.Tools.Count} 个工具" : (s.Error ?? "未连接")));
            }

            OnPropertyChanged(nameof(HasSkills));
            OnPropertyChanged(nameof(HasMcpServers));
            CapabilitiesStatus = Skills.Count == 0 && McpServers.Count == 0
                ? "还没有技能包或连接器——把 SKILL.md / mcp 配置放进上面的目录即可"
                : string.Empty;
        }
        catch (Exception ex)
        {
            CapabilitiesStatus = $"刷新失败：{ex.Message}";
        }
    }
}
