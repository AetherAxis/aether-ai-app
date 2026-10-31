using System.Collections.ObjectModel;
using Aether.AI.App.Core;
using Aether.AI.Core.Agents;
using Aether.Workbench;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Aether.AI.App.ViewModels;

/// <summary>对话目标（下拉项）：catalog AgentDefinition 的展示形态。
/// **record（值相等，2026-10-29 对齐模型下拉）**——目标池热刷新（Clear+重建）后 ComboBox 按**值**匹配新实例，
/// 选中无缝保持；class 引用相等的年代重建即失配，是「删/编 agent 后下拉卡旧值」的根因之一。</summary>
public sealed record AgentTargetItem
{
    public string Id { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    /// <summary>引擎类型显示图标（✨ LLM / 🧭 规则 / ⌨ 代码）。</summary>
    public string DisplayIcon { get; init; } = "🧭";
    /// <summary>是否启用 4 档模式选择器（EnableAgentModeProvider 的 agent 才有）。</summary>
    public bool EnableAgentModeProvider { get; init; }

    /// <summary>末项哨兵「管理 agent…」：选中它不开新会话，只打开管理窗（与模型下拉同一套交互）。</summary>
    public bool IsManage { get; init; }
}

/// <summary>模型下拉的一项（输入卡上方的模型选择器）——重建式刷新（列表小，无需逐项 INPC）。
/// <paramref name="IsManage"/> = 末项哨兵「管理模型…」：选中它不开档案切换，只打开配置窗。</summary>
public sealed record LlmProfileChip(string Name, string ModelId, bool IsCurrent, bool IsManage = false)
{
    public static readonly LlmProfileChip Manage = new("管理模型…", string.Empty, IsCurrent: false, IsManage: true);
}

/// <summary>
/// 输入卡头部的**两个选择器**（2026-10-31 自 ChatViewModel 拆出——它是未来功能的主要堆加点：
/// 会话模式切换 / agent 组合 / 更多档案动作都落这里，不再往 ChatViewModel 里塞）：
/// ① agent 目标下拉（目录热刷新 + 哨兵「管理 agent…」+ 偏好持久化）；
/// ② LLM 档案下拉（选中即切换、下一轮生效 + 哨兵「管理模型…」）。
/// <para>与 ChatViewModel 的交互全走三个事件：<see cref="SelectionChanged"/>（任何选中变化——宿主据此
/// 更新模式支持/重置模式档）、<see cref="TargetChanged"/>（**用户主动**切目标——宿主开新会话）、
/// <see cref="ManageAgentsRequested"/> / <see cref="ManageLlmRequested"/>（开管理窗）。恢复会话期的程序化
/// 设值走 <see cref="SetTargetSilent"/>（不开新会话）。</para>
/// </summary>
public sealed class ChatPickersViewModel : ObservableObject
{
    private readonly IAgentCatalog _agentCatalog;
    private readonly LlmConfigState _llmState;
    private readonly IAgentRunner _agentRunner;
    private readonly IPreferencesStore _preferences;
    private readonly IUiDispatcher _dispatcher;

    private bool _rebuildingTargets; // 目标池热刷新期：重设选中不算「用户切目标」，不触发开新会话

    public ChatPickersViewModel(
        IAgentCatalog agentCatalog,
        LlmConfigState llmState,
        IAgentRunner agentRunner,
        IPreferencesStore preferences,
        IUiDispatcher dispatcher)
    {
        _agentCatalog = agentCatalog;
        _llmState = llmState;
        _agentRunner = agentRunner;
        _preferences = preferences;
        _dispatcher = dispatcher;

        // 模型下拉的档案列表：配置窗保存/切换后 Changed 即时刷新（重建式，列表小）。
        _llmState.Changed += RefreshLlmProfiles;
        RefreshLlmProfiles();

        // Agents 页保存/删除自定义 agent 后热刷新目标池（保留当前选中，按 Id）。
        // 订阅链：ChatViewModel 订阅 CustomAgentStore.Changed → _pickers.OnAgentStoreChanged()——
        // picker 只认 IAgentCatalog 端口，不依赖 CustomAgentStore 具体类型（谁管存储谁转发）。
    }

    /// <summary>agent 目录热刷新入口：ChatViewModel 把 CustomAgentStore.Changed 转发进来
    /// （picker 不依赖 CustomAgentStore 具体类型，保持「只认 IAgentCatalog 端口」）。</summary>
    internal void OnAgentStoreChanged() => RefreshAsync().SafeFireAndForget();

    // —— 事件（宿主 = ChatViewModel 的全部交互面） ——

    /// <summary>任何目标选中变化（用户切换 / 热刷新重建 / 静默恢复）——宿主据此更新模式支持并重置模式档。</summary>
    public event Action? SelectionChanged;

    /// <summary>**用户主动**切换目标（非重建/非静默）——宿主开新会话（偏好持久化在本 VM 内完成）。</summary>
    public event Action? TargetChanged;

    /// <summary>末项哨兵「管理 agent…」被选中 → 宿主（ChatView）打开 agent 管理窗口。</summary>
    public event Action? ManageAgentsRequested;

    /// <summary>「管理模型…」被选中 → 宿主（ChatView）打开配置窗/浮层。</summary>
    public event Action? ManageLlmRequested;

    // —— ① agent 目标下拉 ——

    public ObservableCollection<AgentTargetItem> AgentTargetItems { get; } = new();

    private string? _lastAgentId;    // 最后一个「真实」agent Id（哨兵不计入，选中哨兵后弹回它）

    private AgentTargetItem? _selectedAgentTarget;

    /// <summary>选中的聊天目标（手写 setter——哨兵「管理 agent…」**双通知弹回**原 agent：先置 null 终结
    /// ComboBox 的选中提交批次、再回原值；生成 setter 的单次写回会被提交链吞、卡在哨兵上）。
    /// 用户主动切换 → 偏好持久化 + <see cref="TargetChanged"/>；重建/静默路径只 <see cref="SelectionChanged"/>。</summary>
    public AgentTargetItem? SelectedAgentTarget
    {
        get => _selectedAgentTarget;
        set
        {
            if (ReferenceEquals(_selectedAgentTarget, value)) return;
            if (value is { IsManage: true })
            {
                _selectedAgentTarget = null;
                OnPropertyChanged(nameof(SelectedAgentTarget));
                _selectedAgentTarget = AgentTargetItems.FirstOrDefault(t => t.Id == _lastAgentId);
                OnPropertyChanged(nameof(SelectedAgentTarget));
                ManageAgentsRequested?.Invoke();
                return;
            }
            _selectedAgentTarget = value;
            OnPropertyChanged(nameof(SelectedAgentTarget));
            if (value is not null) _lastAgentId = value.Id;
            SelectionChanged?.Invoke();
            if (_rebuildingTargets || value is null) return;
            // 用户主动切换目标 → 持久化偏好 + 通知宿主（开新会话是宿主的职责——它管 runtime 池）。
            _preferences.SetAsync("chat.selectedAgent", value.Id).SafeFireAndForget();
            TargetChanged?.Invoke();
        }
    }

    /// <summary>恢复会话期的程序化设值：更新选中与 _lastAgentId、发 <see cref="SelectionChanged"/>（宿主
    /// 重置模式支持），但**不开新会话**（不触发 <see cref="TargetChanged"/>）。</summary>
    public void SetTargetSilent(AgentTargetItem? item)
    {
        if (ReferenceEquals(_selectedAgentTarget, item)) return;
        _selectedAgentTarget = item;
        OnPropertyChanged(nameof(SelectedAgentTarget));
        if (item is not null) _lastAgentId = item.Id;
        SelectionChanged?.Invoke();
    }

    /// <summary>启动期加载目标池：恢复偏好选中，否则落第一个真实 agent。全删光时清空选中
    /// （输入卡会给「还没有 agent」的提示，而不是停在一个已不存在的 agent 上）。</summary>
    public Task InitializeAsync()
        => LoadCoreAsync(null, restoreSaved: true);

    /// <summary>目标池热刷新：保留当前选中（按 Id），不重置会话。</summary>
    public Task RefreshAsync()
        => LoadCoreAsync(SelectedAgentTarget?.Id, restoreSaved: false);

    private async Task LoadCoreAsync(string? preferId, bool restoreSaved)
    {
        if (restoreSaved)
            preferId = await _preferences.GetAsync("chat.selectedAgent").ConfigureAwait(false);
        var defs = await _agentCatalog.GetAllAsync().ConfigureAwait(false);   // 目录每次枚举求值——新 agent 立刻可见
        await _dispatcher.InvokeAsync(() =>
        {
            _rebuildingTargets = true;
            try
            {
                AgentTargetItems.Clear();
                foreach (var d in defs)
                    AgentTargetItems.Add(ToTargetItem(d));
                // 末项：管理入口（与模型下拉的「管理模型…」同一套交互）
                AgentTargetItems.Add(new AgentTargetItem
                {
                    Id = ChatViewModel.ManageAgentsId,
                    Name = "管理 agent…",
                    Description = "新建 / 编辑自定义 agent（LLM 或规则大脑）",
                    DisplayIcon = "⚙",
                    IsManage = true,
                });
                var restore = AgentTargetItems.FirstOrDefault(t => t.Id == preferId)
                              ?? AgentTargetItems.FirstOrDefault(t => !t.IsManage);
                _lastAgentId = restore?.Id;
                SetTargetSilent(restore);   // 重建路径：只 SelectionChanged（宿主更新模式支持），不开新会话
            }
            finally { _rebuildingTargets = false; }
        });
    }

    private static AgentTargetItem ToTargetItem(AgentDefinition d) => new()
    {
        Id = d.Id,
        Name = string.IsNullOrWhiteSpace(d.Name) ? d.Id : d.Name,
        // 优先用定义里的「给人看的摘要」；没写才退回提示词前 60 字（规则 agent 提示词为空 → 用规则名兜底）。
        Description = !string.IsNullOrWhiteSpace(d.Description)
            ? d.Description
            : d.Instructions.Length > 60 ? d.Instructions[..60] + "…" : d.Instructions,
        DisplayIcon = d.Engine switch
        {
            EngineKind.Llm => "✨",
            EngineKind.Code => "⌨",
            _ => "🧭",
        },
        EnableAgentModeProvider = d.EnableAgentModeProvider,
    };

    // —— ② 模型选择器（输入卡上方）：**选择与配置在同一处** ——
    // 下拉列出全部 LLM 档案（选中即切换，下一轮生效），末项「管理模型…」打开配置窗（LlmProfilesView）。

    public ObservableCollection<LlmProfileChip> LlmPickerItems { get; } = new();

    private LlmProfileChip? _selectedLlm;

    public LlmProfileChip? SelectedLlm
    {
        get => _selectedLlm;
        set
        {
            if (ReferenceEquals(_selectedLlm, value)) return;
            if (value is { IsManage: true })
            {
                // 哨兵项不参与选择：把值弹回原档案（下拉里不显示「管理模型…」），再请宿主开配置窗。
                _selectedLlm = null;
                OnPropertyChanged(nameof(SelectedLlm));
                _selectedLlm = LlmPickerItems.FirstOrDefault(i => i.IsCurrent);
                OnPropertyChanged(nameof(SelectedLlm));
                ManageLlmRequested?.Invoke();
                return;
            }
            _selectedLlm = value;
            OnPropertyChanged(nameof(SelectedLlm));
            if (value is not null && value.Name != _llmState.SelectedName)
            {
                _llmState.Select(value.Name);
                // 换档案对「跟随当前档案」的 agent 都生效（通用助手 / 用户自建 LLM agent）：
                // 逐个失效，下一轮对话即用新模型（与配置窗同一条路）。哨兵项不参与。
                foreach (var item in AgentTargetItems.Where(t => !t.IsManage))
                    _agentRunner.Invalidate(item.Id);
            }
        }
    }

    public bool HasLlmProfiles => _llmState.Configs.Count > 0;

    private void RefreshLlmProfiles()
    {
        LlmPickerItems.Clear();
        foreach (var c in _llmState.Configs)
            LlmPickerItems.Add(new LlmProfileChip(c.Name, c.ModelId, c.Name == _llmState.SelectedName));
        LlmPickerItems.Add(LlmProfileChip.Manage);
        _selectedLlm = LlmPickerItems.FirstOrDefault(i => i.IsCurrent);
        OnPropertyChanged(nameof(SelectedLlm));
        OnPropertyChanged(nameof(HasLlmProfiles));
    }
}
