using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using Aether.AI.App.Core;
using Aether.AI.Core.Agents;
using Aether.AI.Core.Conversations;
using Aether.AI.Memory;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;

namespace Aether.AI.App.ViewModels;

/// <summary>对话目标（下拉项）：catalog AgentDefinition 的展示形态。</summary>
public sealed partial class AgentTargetItem : ObservableObject
{
    public string Id { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    /// <summary>引擎类型显示图标（✨ LLM / 🧭 规则 / ⌨ 代码）。</summary>
    public string DisplayIcon { get; init; } = "🧭";
    /// <summary>是否启用 4 档模式选择器（EnableAgentModeProvider 的 agent 才有）。</summary>
    public bool EnableAgentModeProvider { get; init; }

    /// <summary>末项哨兵「管理 agent…」：选中它不开新会话，只打开 agent 管理窗口（与模型下拉同一套交互）。</summary>
    public bool IsManage { get; init; }
}

/// <summary>
/// 聊天页 ViewModel：多会话并发（每会话独立 <see cref="ConversationRuntime"/>，切会话不取消背景 run）+
/// 目标选择 + 4 档模式 + 计划面板 + 历史增量加载 + 会话侧栏。
/// 自 monorepo ChatWorkspaceViewModel port（2026-10-24）：剥离设备/SignalR 远程链、工作流/规则目标池、
/// LLM 下拉切换（M2 以设置页回归）；执行核心已下沉 <see cref="ChatSessionEngine"/>
/// （审批循环经 ⑫ RunInteractiveAsync 收编）。
/// </summary>
public partial class ChatViewModel : ObservableObject
{
    private readonly IAgentRunner _agentRunner;
    private readonly IUiDispatcher _dispatcher;
    private readonly IConversationStore _conversationStore;
    private readonly IAgentCatalog _agentCatalog;
    private readonly ChatSessionEngine _engine;
    private readonly ChatTranscriptPersister _persister;
    private readonly ConversationHistoryLoader _historyLoader;
    private readonly Aether.Workbench.IPreferencesStore _preferences;
    private readonly LlmConfigState _llmState;
    private readonly CustomAgentStore _agentStore;

    // —— 多会话并发：每会话独立 runtime，背景 run 往自己的 Messages 追加，切会话只换 _current 不取消 ——
    private readonly ConcurrentDictionary<Guid, ConversationRuntime> _runtimes = new();
    private ConversationRuntime _current; // 当前显示的会话 runtime（永不 null：待新聊天=Guid.Empty 占位）
    private Guid _currentConversationId = Guid.Empty;

    private bool _isInitializing;
    private bool _rebuildingTargets; // 目标池热刷新期：SelectedAgentTarget 重设不算「用户切目标」，不重置会话
    private string? _lastAgentId;    // 最后一个「真实」agent Id（「管理 agent…」哨兵不计入，选中后弹回它）
    private bool _syncingMode; // 程序化刷新 SelectedChatMode 时置 true，避免回触 SetMode

    // —— 历史增量加载：切换会话时只渲染最近若干轮，更早的暂存，滚到顶部再加载 ——
    private const int HistoryInitialBatch = 10;
    private const int HistoryLoadBatch = 10;

    public ChatViewModel(
        IServiceProvider serviceProvider,
        Aether.Workbench.IPreferencesStore preferences)
    {
        _agentRunner = serviceProvider.GetRequiredService<IAgentRunner>();
        _dispatcher = serviceProvider.GetRequiredService<IUiDispatcher>();
        _conversationStore = serviceProvider.GetRequiredService<IConversationStore>();
        _agentCatalog = serviceProvider.GetRequiredService<IAgentCatalog>();
        _persister = serviceProvider.GetRequiredService<ChatTranscriptPersister>();
        _historyLoader = serviceProvider.GetRequiredService<ConversationHistoryLoader>();
        _preferences = preferences;
        _llmState = serviceProvider.GetRequiredService<LlmConfigState>();
        _agentStore = serviceProvider.GetRequiredService<CustomAgentStore>();

        // 模型下拉的档案列表：配置窗保存/切换后 Changed 即时刷新（重建式，列表小）。
        _llmState.Changed += RefreshLlmProfiles;
        RefreshLlmProfiles();

        // Agents 页保存/删除自定义 agent → 目标池热刷新（保持在当前 agent 上，不打断会话）。
        _agentStore.Changed += () => _ = RefreshAgentTargetsAsync();

        // 流式渲染引擎：审批/计划快照全在引擎内。模式/模式支持逐工具实时读（运行中切档立即生效）；
        // isCurrentRuntime 决定背景会话计划变更是否上抛 PlanSummary。
        _engine = new ChatSessionEngine(_dispatcher,
            serviceProvider.GetService<IApprovalDialogService>(),
            serviceProvider.GetService<IToolApprovalLevelLookup>(),
            () => SelectedChatMode,
            () => HasModeSupport,
            rt => ReferenceEquals(_current, rt));
        _engine.PlanSummaryChanged += () => OnPropertyChanged(nameof(PlanSummary));

        Conversations.CollectionChanged += (_, _) => HasConversations = Conversations.Count > 0;

        // 初始 runtime = 待新聊天（Guid.Empty 占位；首次发送落库拿到真 Guid 后回填 + 注册）。
        _current = new ConversationRuntime(Guid.Empty);
        SetCurrentInternal(_current);
    }

    /// <summary>启动期：加载目标池 + 恢复选中 + 最近会话列表。</summary>
    public async Task InitializeAsync()
    {
        _isInitializing = true;
        try
        {
            var saved = await _preferences.GetAsync("chat.selectedAgent");
            await LoadAgentTargetsAsync(saved);
            await RefreshConversationsAsync();
        }
        finally { _isInitializing = false; }
    }

    /// <summary>Agents 保存/删除自定义 agent 后热刷新目标池：保留当前选中（按 Id），不重置会话。</summary>
    public Task RefreshAgentTargetsAsync() => LoadAgentTargetsAsync(SelectedAgentTarget?.Id);

    /// <summary>末项哨兵「管理 agent…」被选中 → 宿主（ChatView）打开 agent 管理窗口。</summary>
    public event Action? ManageAgentsRequested;

    /// <summary>哨兵 Id（不进偏好、不参与会话映射）。</summary>
    public const string ManageAgentsId = "__manage_agents__";

    private async Task LoadAgentTargetsAsync(string? preferId)
    {
        var defs = await _agentCatalog.GetAllAsync();   // 目录每次枚举求值——新 agent 立刻可见
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
                    Id = ManageAgentsId,
                    Name = "管理 agent…",
                    Description = "新建 / 编辑自定义 agent（LLM 或规则大脑）",
                    DisplayIcon = "⚙",
                    IsManage = true,
                });
                // 优先恢复原选中；否则落第一个真实 agent。全删光时 restore 为 null → 清空选中
                // （输入卡会给「还没有 agent」的提示，而不是停在一个已不存在的 agent 上）。
                var restore = AgentTargetItems.FirstOrDefault(t => t.Id == preferId)
                              ?? AgentTargetItems.FirstOrDefault(t => !t.IsManage);
                _lastAgentId = restore?.Id;
                SelectedAgentTarget = restore;
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

    // —— 会话切换与代理属性（镜像 monorepo 语义） ——

    private void SetCurrentInternal(ConversationRuntime rt)
    {
        if (ReferenceEquals(_current, rt)) return;
        if (_current is not null) _current.PropertyChanged -= OnRuntimePropertyChanged;
        _current = rt;
        _currentConversationId = rt.ConversationId;
        if (rt is not null) rt.PropertyChanged += OnRuntimePropertyChanged;
        // 代理属性全量通知：Messages/ActiveTodos 换集合实例（视图重绑），标量重读。
        OnPropertyChanged(nameof(Messages));
        OnPropertyChanged(nameof(ActiveTodos));
        OnPropertyChanged(nameof(IsAgentRunning));
        OnPropertyChanged(nameof(HasActivePlan));
        OnPropertyChanged(nameof(PlanAwaitingAccept));
        OnPropertyChanged(nameof(TokenInfo));
        OnPropertyChanged(nameof(HasMessages));
        OnPropertyChanged(nameof(HasMoreHistory));
        OnPropertyChanged(nameof(PlanSummary));
        OnPropertyChanged(nameof(ShowPlanAccept));
        OnPropertyChanged(nameof(ShowPlanDismiss));
        UpdateConversationRunningFlag(rt);
    }

    private void OnRuntimePropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (sender is not ConversationRuntime rt) return;
        if (e.PropertyName == nameof(ConversationRuntime.IsRunning))
            UpdateConversationRunningFlag(rt);
        if (!ReferenceEquals(sender, _current)) return; // 背景会话的标量变更不转发
        var proxyName = e.PropertyName == nameof(ConversationRuntime.IsRunning)
            ? nameof(IsAgentRunning)
            : e.PropertyName!;
        OnPropertyChanged(proxyName);
        switch (e.PropertyName)
        {
            case nameof(ConversationRuntime.IsRunning):
                OnPropertyChanged(nameof(ShowPlanAccept));
                OnPropertyChanged(nameof(ShowPlanDismiss));
                break;
            case nameof(ConversationRuntime.HasActivePlan):
                OnPropertyChanged(nameof(PlanSummary));
                OnPropertyChanged(nameof(ShowPlanDismiss));
                break;
            case nameof(ConversationRuntime.PlanAwaitingAccept):
                OnPropertyChanged(nameof(ShowPlanAccept));
                OnPropertyChanged(nameof(ShowPlanDismiss));
                break;
        }
    }

    private ConversationRuntime GetOrCreateRuntime(Guid conversationId)
    {
        if (conversationId == Guid.Empty) return _current;
        return _runtimes.GetOrAdd(conversationId, id => new ConversationRuntime(id));
    }

    private void UpdateConversationRunningFlag(ConversationRuntime rt)
    {
        var item = Conversations.FirstOrDefault(c => c.Id == rt.ConversationId);
        if (item is not null) item.IsRunning = rt.IsRunning;
    }

    // —— 代理属性：读当前会话 runtime ——

    public ObservableCollection<ChatTurnItem> Messages => _current.Messages;
    public ObservableCollection<TodoItemVM> ActiveTodos => _current.ActiveTodos;
    public bool HasActivePlan { get => _current.HasActivePlan; set => _current.HasActivePlan = value; }
    public bool PlanAwaitingAccept { get => _current.PlanAwaitingAccept; set => _current.PlanAwaitingAccept = value; }
    public string? TokenInfo { get => _current.TokenInfo; set => _current.TokenInfo = value; }
    public bool HasMessages => _current.HasMessages;
    public bool IsAgentRunning => _current.IsRunning;
    public bool HasMoreHistory => _current.PendingHistoryTurns is { Count: > 0 };

    [ObservableProperty] private bool _isPlanExpanded = true;
    public string PlanSummary => HasActivePlan ? $"📋 计划 ({ActiveTodos.Count(t => t.IsComplete)}/{ActiveTodos.Count})" : "";
    partial void OnIsPlanExpandedChanged(bool value) => OnPropertyChanged(nameof(PlanSummary));
    [RelayCommand] private void TogglePlanExpanded() => IsPlanExpanded = !IsPlanExpanded;

    /// <summary>确认执行完毕：收起浮动计划面板。</summary>
    [RelayCommand]
    private void DismissPlan()
    {
        _current.ActiveTodos.Clear();
        _current.HasActivePlan = false;
    }

    /// <summary>当前 agent 是否启用 AgentModeProvider（控制模式选择器/plan-accept 显隐）。</summary>
    [ObservableProperty]
    private bool _hasModeSupport;

    /// <summary>当前会话运行模式（4 档：Plan/Manual/AutoEdit/Auto）。默认 AutoEdit。</summary>
    [ObservableProperty]
    private ChatMode _selectedChatMode = ChatMode.AutoEdit;

    /// <summary>plan 模式轮产出计划后、非运行中时显示"接受计划→执行"条。</summary>
    public bool ShowPlanAccept => HasModeSupport && PlanAwaitingAccept && !IsAgentRunning;

    /// <summary>有计划、非运行中、且不在待接受态 → 可点「确认执行完毕」收起面板。</summary>
    public bool ShowPlanDismiss => HasActivePlan && !IsAgentRunning && !PlanAwaitingAccept;

    // —— 目标选择 ——

    public ObservableCollection<AgentTargetItem> AgentTargetItems { get; } = new();

    [ObservableProperty]
    private AgentTargetItem? _selectedAgentTarget;

    /// <summary>选中目标变化：更新模式支持；非启动期统一开新会话 + 持久化选中。
    /// 末项哨兵「管理 agent…」被选中 → 弹回原 agent 并请宿主开管理窗口（不开新会话、不改模式）。</summary>
    partial void OnSelectedAgentTargetChanged(AgentTargetItem? value)
    {
        if (value is { IsManage: true })
        {
            _selectedAgentTarget = AgentTargetItems.FirstOrDefault(t => t.Id == _lastAgentId);
            OnPropertyChanged(nameof(SelectedAgentTarget));
            HasModeSupport = _selectedAgentTarget?.EnableAgentModeProvider == true;
            ManageAgentsRequested?.Invoke();
            return;
        }

        if (!_rebuildingTargets && value is not null) _lastAgentId = value.Id;
        HasModeSupport = value?.EnableAgentModeProvider == true;
        _syncingMode = true;
        SelectedChatMode = ChatMode.AutoEdit;
        _syncingMode = false;
        if (_isInitializing || _rebuildingTargets || value is null) return;

        // 用户主动切换目标 → 开新会话（会话已持久化，误切可从历史找回）。切会话不取消背景 run。
        SetCurrentInternal(new ConversationRuntime(Guid.Empty));
        foreach (var c in Conversations) c.IsCurrent = false;
        _preferences.SetAsync("chat.selectedAgent", value.Id).SafeFireAndForget();
        RefreshModeAsync().SafeFireAndForget();
    }

    // —— 最近会话列表（侧栏） ——

    public ObservableCollection<ConversationItem> Conversations { get; } = new();

    [ObservableProperty] private bool _hasConversations;

    // —— 模型选择器（输入卡上方）：**选择与配置在同一处** ——
    // 下拉列出全部 LLM 档案（选中即切换，下一轮生效），末项「管理模型…」打开配置窗（LlmProfilesView）。
    // 2026-10-26 四改：原「每条档案一个 chip + 独立的『模型 ⚙』按钮」两个控件合并成这一个下拉。

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
                // 逐个失效，下一轮对话即用新模型（与配置窗同一条路）。
                foreach (var item in AgentTargetItems.Where(t => !t.IsManage))
                    _agentRunner.Invalidate(item.Id);
            }
        }
    }

    /// <summary>「管理模型…」被选中 → 宿主（ChatView）打开配置窗/浮层。</summary>
    public event Action? ManageLlmRequested;

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

    [RelayCommand]
    private void NewChat()
    {
        // 切到新聊天**不取消**进行中的流式运行——背景 run 继续往自己的 runtime.Messages 追加，切回即见续接。
        SetCurrentInternal(new ConversationRuntime(Guid.Empty));
        foreach (var c in Conversations) c.IsCurrent = false;
    }

    [RelayCommand]
    private async Task ResumeConversationAsync(ConversationItem item)
    {
        if (item is null) return;
        var runtime = GetOrCreateRuntime(item.Id);
        var alreadyLoaded = runtime.Messages.Count > 0;
        SetCurrentInternal(runtime);
        foreach (var c in Conversations) c.IsCurrent = c.Id == item.Id;

        _isInitializing = true;
        if (AgentTargetItems.FirstOrDefault(t => t.Id == item.AgentId) is { } target)
            SelectedAgentTarget = target; // 启动期语义：不重置会话
        _isInitializing = false;

        // 首次切来（runtime 空）→ 从 typed 事件流重建 transcript；已加载过 → 直接显（背景 run 可能还在追加）。
        if (!alreadyLoaded && !runtime.IsLoadingHistory)
        {
            runtime.IsLoadingHistory = true;
            try
            {
                var built = await Task.Run(() => _historyLoader.LoadAsync(item.Id, item.AgentName)).ConfigureAwait(true);
                if (built is { } result)
                {
                    runtime.NextRunSeq = result.NextRunSeq;
                    if (result.Turns.Count <= HistoryInitialBatch)
                    {
                        foreach (var t in result.Turns) runtime.Messages.Add(t);
                    }
                    else
                    {
                        runtime.PendingHistoryTurns = result.Turns.Take(result.Turns.Count - HistoryInitialBatch).ToList();
                        foreach (var t in result.Turns.Skip(result.Turns.Count - HistoryInitialBatch)) runtime.Messages.Add(t);
                    }
                }
                else
                {
                    runtime.NextRunSeq = 1;
                }
            }
            finally
            {
                runtime.IsLoadingHistory = false;
            }
        }
    }

    [RelayCommand]
    private async Task DeleteConversationAsync(ConversationItem item)
    {
        if (item is null) return;
        // 删会话**要**取消其 run，从 _runtimes 移除 runtime。
        if (_runtimes.TryRemove(item.Id, out var delRt))
            CancelRunningRun(delRt);
        try
        {
            await _conversationStore.DeleteAsync(item.Id);
            _agentRunner.EvictConversation(item.Id);
            Conversations.Remove(item);
            if (_currentConversationId == item.Id)
                SetCurrentInternal(new ConversationRuntime(Guid.Empty));
        }
        catch (Exception ex)
        {
            StatusText = $"删除会话失败：{ex.Message}";
        }
    }

    /// <summary>从存储加载最近会话列表（按 UpdatedAt 倒序）。</summary>
    public async Task RefreshConversationsAsync()
    {
        try
        {
            var summaries = await _conversationStore.GetSummariesAsync(string.Empty);
            await _dispatcher.InvokeAsync(() =>
            {
                Conversations.Clear();
                foreach (var s in summaries)
                {
                    Conversations.Add(new ConversationItem
                    {
                        Id = s.ConversationId,
                        Title = string.IsNullOrWhiteSpace(s.Title) ? "新会话" : s.Title,
                        Preview = s.LastMessagePreview ?? string.Empty,
                        DisplayTime = FormatTime(s.UpdatedAt),
                        AgentId = s.AgentId,
                        AgentName = s.AgentName,
                        IsCurrent = s.ConversationId == _currentConversationId,
                        ResumeCommand = ResumeConversationCommand,
                        DeleteCommand = DeleteConversationCommand,
                    });
                }
            });
        }
        catch { /* 启动期失败不阻塞 */ }
    }

    private static string FormatTime(DateTimeOffset t)
    {
        var local = t.LocalDateTime;
        if (local.Date == DateTime.Today) return local.ToString("HH:mm");
        return local.ToString("MM-dd HH:mm");
    }

    [ObservableProperty] private string _inputText = string.Empty;
    [ObservableProperty] private bool _canSendInput = true;
    [ObservableProperty] private string _statusText = string.Empty;

    // —— 模式 ——

    partial void OnSelectedChatModeChanged(ChatMode value)
    {
        OnPropertyChanged(nameof(ShowPlanAccept));
        if (_syncingMode || _isInitializing || !HasModeSupport) return;
        // 已存在会话：立即 SetMode；新会话：不改，由 run 轮的 desiredMode 在首轮应用。
        if (_currentConversationId != Guid.Empty)
            _agentRunner.SetModeAsync(_currentConversationId, value.ToDesiredMode()).SafeFireAndForget();
    }

    partial void OnHasModeSupportChanged(bool value) => OnPropertyChanged(nameof(ShowPlanAccept));

    /// <summary>轮后从会话读实际模式（带 guard 不回触 SetMode）。</summary>
    private async Task RefreshModeAsync(Guid? conversationId = null)
    {
        var convId = conversationId ?? _currentConversationId;
        if (convId == Guid.Empty || !HasModeSupport) return;
        var m = await _agentRunner.GetModeAsync(convId).ConfigureAwait(false);
        if (m is not null && ChatModeExtensions.FromMafMode(m) is { } synced)
        {
            _syncingMode = true;
            SelectedChatMode = synced;
            _syncingMode = false;
        }
    }

    [RelayCommand]
    private async Task AcceptPlanAsync()
    {
        if (!HasModeSupport || IsAgentRunning || _currentConversationId == Guid.Empty) return;
        PlanAwaitingAccept = false;
        _syncingMode = true; SelectedChatMode = ChatMode.AutoEdit; _syncingMode = false;
        await _agentRunner.SetModeAsync(_currentConversationId, ChatMode.AutoEdit.ToDesiredMode());
        InputText = "（已接受计划，进入执行模式）";
        await SendInputAsync();
    }

    // —— 停止 ——

    /// <summary>停止当前显示会话的流式生成（取消 CT）。只影响当前会话。</summary>
    [RelayCommand]
    private void StopGeneration() => _current.RunCts?.Cancel();

    private void CancelRunningRun(ConversationRuntime? runtime = null)
        => (runtime ?? _current).RunCts?.Cancel();

    /// <summary>取消所有会话的进行中流式运行（app 真退出时调）。</summary>
    public void CancelAllRunning()
    {
        _current.RunCts?.Cancel();
        foreach (var rt in _runtimes.Values)
            rt.RunCts?.Cancel();
    }

    // —— 发送（主链路） ——

    [RelayCommand]
    private async Task SendInputAsync()
    {
        if (!CanSendInput || string.IsNullOrWhiteSpace(InputText)) return;
        if (IsAgentRunning) return; // 防重入兜底
        // agent 全被删光时（目录里只剩「管理 agent…」）给一条能自救的提示。
        if (SelectedAgentTarget is null)
        {
            StatusText = "还没有 agent——在顶部下拉的「管理 agent…」里新建一个";
            return;
        }

        var target = SelectedAgentTarget;
        var content = InputText.Trim();
        InputText = string.Empty;
        var runtime = _current;
        runtime.RunCts = new CancellationTokenSource();

        // 本地显示用户消息（右侧）并起一轮
        var turn = new ChatTurnItem { Header = content };
        turn.Children.Add(new ChatMessageItem
        {
            Side = ChatMessageSide.Right,
            ItemType = "text",
            Sender = "我",
            Content = content
        });
        Messages.Add(turn);

        var justCreated = false;
        // 待新聊天 → 首次发送才落库创建会话：占位 runtime 提升为真 runtime。
        if (_currentConversationId == Guid.Empty)
        {
            justCreated = true;
            var newId = Guid.NewGuid();
            var realRt = new ConversationRuntime(newId) { NextRunSeq = 1, RunCts = runtime.RunCts };
            runtime.RunCts = null;
            foreach (var t in runtime.Messages) realRt.Messages.Add(t);
            runtime.Messages.Clear();
            _runtimes[newId] = realRt;
            SetCurrentInternal(realRt);
            runtime = realRt;
            _currentConversationId = newId;
            var conv = new Conversation
            {
                ConversationId = _currentConversationId,
                AgentId = target.Id,
                AgentName = target.Name,
                Title = content.Length > 20 ? content[..20] + "…" : content,
            };
            await _conversationStore.SaveConversationAsync(conv);
            await _dispatcher.InvokeAsync(() =>
            {
                Conversations.Insert(0, new ConversationItem
                {
                    Id = conv.ConversationId, Title = conv.Title, Preview = content,
                    DisplayTime = FormatTime(conv.UpdatedAt), AgentId = conv.AgentId, AgentName = conv.AgentName, IsCurrent = true,
                    ResumeCommand = ResumeConversationCommand,
                    DeleteCommand = DeleteConversationCommand,
                });
                foreach (var c in Conversations) c.IsCurrent = c.Id == _currentConversationId;
            });
        }

        var fullText = new System.Text.StringBuilder();
        var desiredMode = HasModeSupport ? SelectedChatMode.ToDesiredMode() : null;
        runtime.TodosTouchedThisTurn = false;
        var runItems = await _engine.RunTurnAsync(runtime, turn, target.Name,
            (handlers, approvalResponder, ct) => _agentRunner.RunInteractiveAsync(
                _currentConversationId, target.Id, content, handlers, approvalResponder, desiredMode, ct),
            fullText, runtime.RunCts?.Token ?? default);

        // plan 模式轮、且本轮确实产出计划 → 待接受（驱动接受条）。已开始执行则不弹。
        runtime.PlanAwaitingAccept = desiredMode == AgentModes.Plan && SelectedChatMode == ChatMode.Plan
            && runtime.TodosTouchedThisTurn && runtime.HasActivePlan
            && !runtime.ActiveTodos.Any(t => t.IsComplete);

        // 持久化本轮为 run+events 并乐观 bump 侧栏项。
        await PersistRunAsync(runtime, content, runItems, fullText.ToString());
        if (HasModeSupport) await RefreshModeAsync();

        return;

        // —— 持久化 + 侧栏 bump（本地函数保持 SendInputAsync 主体可读） ——
        async Task PersistRunAsync(ConversationRuntime rt,
            string userContent, IReadOnlyList<ChatMessageItem> items, string full)
        {
            var result = await _persister.PersistRunAsync(rt, userContent, items);
            if (result is null) return;
            try
            {
                await _dispatcher.InvokeAsync(() =>
                {
                    var item = Conversations.FirstOrDefault(c => c.Id == result.ConversationId);
                    if (item is not null)
                    {
                        var preview = string.IsNullOrWhiteSpace(full) ? userContent : full;
                        item.Preview = preview.Length > 40 ? preview[..40] + "…" : preview;
                        item.DisplayTime = FormatTime(result.FinishedAt);
                        Conversations.Remove(item);
                        Conversations.Insert(0, item);
                    }
                });
            }
            catch { /* bump 失败不阻塞对话 */ }
        }
    }

    // —— 历史增量加载 ——

    /// <summary>增量加载更早的历史轮：从暂存队列取最多 HistoryLoadBatch 轮插入 Messages 顶部。返回实际加载数。</summary>
    public int LoadMoreHistory()
    {
        var pending = _current.PendingHistoryTurns;
        if (pending is null || pending.Count == 0) return 0;
        int take = Math.Min(HistoryLoadBatch, pending.Count);
        // 暂存按时间正序（最旧在前）；取最新的待加载批次（队列尾部），倒序 Insert(0) 保持正序。
        int start = pending.Count - take;
        for (int i = start + take - 1; i >= start; i--)
            Messages.Insert(0, pending[i]);
        pending.RemoveRange(start, take);
        return take;
    }
}

/// <summary>模型下拉的一项（输入卡上方的模型选择器）——重建式刷新（列表小，无需逐项 INPC）。
/// <paramref name="IsManage"/> = 末项哨兵「管理模型…」：选中它不开档案切换，只打开配置窗。</summary>
public sealed record LlmProfileChip(string Name, string ModelId, bool IsCurrent, bool IsManage = false)
{
    public static readonly LlmProfileChip Manage = new("管理模型…", string.Empty, IsCurrent: false, IsManage: true);
}
