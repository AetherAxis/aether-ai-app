using Aether.AI.App.Core;
using Aether.AI.Core.Agents;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;

namespace Aether.AI.App.Views;

/// <summary>
/// 聊天页视图：头部（agent 选择/用量/历史/新聊天）+ 底部输入卡（模式选择器 + 计划面板 + plan-accept 条）
/// + 消息区（<see cref="ChatMessagesView"/>）。
/// 移动形态：内嵌审批 bottom sheet（订阅 <see cref="App.MobileApproval"/> broker；桌面形态 broker 为 null，休眠）。
/// </summary>
public partial class ChatView : UserControl
{
    private ToolApprovalRequest? _pendingRequest;

    private ViewModels.ChatViewModel? _hookedVm;

    public ChatView()
    {
        InitializeComponent();
        // 设置齿轮：仅移动形态显示（桌面走 ⑩ 工作台 rail 左下角齿轮入口，FooterPanelKey=settings）。
        var isDesktop = Avalonia.Application.Current?.ApplicationLifetime
            is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime;
        SettingsGearButton.IsVisible = !isDesktop;
        // 下拉里的「管理…」项 → 打开对应管理窗（View/VM 分工：VM 只发意图，宿主决定形态）
        DataContextChanged += (_, _) => HookVmEvents();
        HookVmEvents();
        if (App.MobileApproval is { } broker)
        {
            broker.Requested += OnApprovalRequested;
            DetachedFromVisualTree += (_, _) => broker.Requested -= OnApprovalRequested;
        }
    }

    private void HookVmEvents()
    {
        if (_hookedVm is not null)
            _hookedVm.ManageLlmRequested -= OnManageLlmRequested;
        _hookedVm = DataContext as ViewModels.ChatViewModel;
        if (_hookedVm is not null)
            _hookedVm.ManageLlmRequested += OnManageLlmRequested;
    }

    /// <summary>模型下拉里选中「管理模型…」→ 开配置窗。
    /// 延到下一帧：先让下拉浮层收起，再弹窗，避免抢焦点。</summary>
    private void OnManageLlmRequested() => Dispatcher.UIThread.Post(ShowModelSettings);

    /// <summary>输入卡 ⚙ 按钮 → agent 管理窗（桌面模态 / 移动内嵌浮层）。
    /// 2026-10-29 起 agent 管理入口从「下拉末项哨兵」改为独立按钮——下拉纯选择，
    /// 哨兵的「选中即弹回」在 ComboBox 提交链里改选中状态会卡旧值。</summary>
    private void OnManageAgents(object? sender, RoutedEventArgs e) => ShowAgentManager();

    // —— 模型设置浮层（移动形态）——

    private void OnSettingsGear(object? sender, RoutedEventArgs e) => OpenModelOverlay();

    private void OpenModelOverlay()
    {
        // 同一份 LlmProfilesView / CapabilitiesView / SettingsViewModel（移动组合根解析）；首次打开时接 DataContext。
        // 能力页（技能包/连接器/链上钱包）在手机上**只能**从这里进——桌面走 rail 左下角齿轮的设置页。
        if (App.MobileProvider is { } provider)
        {
            if (MobileLlmView.DataContext is null)
            {
                var vm = provider.GetRequiredService<ViewModels.SettingsViewModel>();
                MobileLlmView.DataContext = vm;
                MobileCapabilitiesView.DataContext = vm;
            }
            if (MobileAuditView.DataContext is null)
                MobileAuditView.DataContext = provider.GetRequiredService<ViewModels.AppAuditViewModel>();
        }
        ShowSettingsSection(SettingsSection.Llm);   // 每次打开回到「模型」页（其余是深链，不抢默认入口）
        ModelSettingsOverlay.IsVisible = true;
    }

    private enum SettingsSection { Llm, Capabilities, Audit }

    private void OnLlmTab(object? sender, RoutedEventArgs e) => ShowSettingsSection(SettingsSection.Llm);

    private void OnCapabilitiesTab(object? sender, RoutedEventArgs e) => ShowSettingsSection(SettingsSection.Capabilities);

    private void OnAuditTab(object? sender, RoutedEventArgs e) => ShowSettingsSection(SettingsSection.Audit);

    /// <summary>页签切换：视图各自持 DataContext，只换谁可见（不做控件重建，滚动位置/输入内容都保留）。</summary>
    private void ShowSettingsSection(SettingsSection section)
    {
        MobileLlmView.IsVisible = section == SettingsSection.Llm;
        MobileCapabilitiesScroll.IsVisible = section == SettingsSection.Capabilities;
        MobileAuditView.IsVisible = section == SettingsSection.Audit;
        LlmTabButton.Opacity = section == SettingsSection.Llm ? 1.0 : 0.55;
        CapabilitiesTabButton.Opacity = section == SettingsSection.Capabilities ? 1.0 : 0.55;
        AuditTabButton.Opacity = section == SettingsSection.Audit ? 1.0 : 0.55;
    }

    private void OnSettingsClose(object? sender, RoutedEventArgs e)
        => ModelSettingsOverlay.IsVisible = false;

    // —— LLM 档案管理（模型下拉的「管理模型…」项，2026-10-26）——
    // 唯一的 LLM 配置面：桌面 = 模态窗，移动 = 内嵌浮层，两端宿同一份 LlmProfilesView + SettingsViewModel 单例
    // （编辑即时落档、下拉里的档案列表随 Changed 刷新）。设置页（左下角齿轮）只管外观/布局，不放 LLM。

    private void ShowModelSettings()
    {
        if (Avalonia.Application.Current?.ApplicationLifetime
            is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop)
        {
            var vm = Aether.Workbench.WorkbenchServices.GetService<ViewModels.SettingsViewModel>()
                ?? App.MobileProvider?.GetRequiredService<ViewModels.SettingsViewModel>();
            if (vm is null || desktop.MainWindow is null) return;
            var dialog = new Avalonia.Controls.Window
            {
                Title = "模型设置",
                Content = new LlmProfilesView { DataContext = vm },
                // 760x680：够放「列表 + 表单」两段而不挤（旧 520x560 会把表单压出滚动条）。
                Width = 760, Height = 680, MinWidth = 560, MinHeight = 520,
                WindowStartupLocation = Avalonia.Controls.WindowStartupLocation.CenterOwner,
            };
            Aether.AI.App.AppDialogs.ApplyTheme(dialog);   // 模态窗不继承工作台的主题 token（见 AppDialogs 注释）
            _ = dialog.ShowDialog(desktop.MainWindow);
            return;
        }
        OpenModelOverlay();   // 移动形态：内嵌浮层
    }

    // —— agent 管理（顶部 agent 下拉的「管理 agent…」项，2026-10-26）——
    // 与模型设置同款范式：桌面 = 模态窗，移动 = 内嵌浮层，两端宿同一份 AgentsView/AgentsViewModel。

    private void ShowAgentManager()
    {
        if (Avalonia.Application.Current?.ApplicationLifetime
            is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop)
        {
            var vm = Aether.Workbench.WorkbenchServices.GetService<ViewModels.AgentsViewModel>()
                ?? App.MobileProvider?.GetRequiredService<ViewModels.AgentsViewModel>();
            if (vm is null || desktop.MainWindow is null) return;
            var dialog = new Avalonia.Controls.Window
            {
                Title = "Agents",
                Content = new AgentsView { DataContext = vm },
                // 880x780：够放「agent 列表 + 完整表单（LLM/规则两套字段）」，尽量不滚。
                Width = 880, Height = 780, MinWidth = 640, MinHeight = 520,
                WindowStartupLocation = Avalonia.Controls.WindowStartupLocation.CenterOwner,
            };
            Aether.AI.App.AppDialogs.ApplyTheme(dialog);
            _ = dialog.ShowDialog(desktop.MainWindow);
            return;
        }

        if (MobileAgentsView.DataContext is null && App.MobileProvider is { } provider)
            MobileAgentsView.DataContext = provider.GetRequiredService<ViewModels.AgentsViewModel>();
        AgentManagerOverlay.IsVisible = true;
    }

    private void OnAgentManagerClose(object? sender, RoutedEventArgs e)
        => AgentManagerOverlay.IsVisible = false;

    // —— 审批 bottom sheet（移动形态） ——

    private void OnApprovalRequested(ToolApprovalRequest request)
    {
        _pendingRequest = request;
        Dispatcher.UIThread.Post(() =>
        {
            ApprovalToolRun.Text = request.ToolName;
            ApprovalInputText.Text = request.Input is { Count: > 0 }
                ? string.Join("\n", request.Input.Select(kv => $"{kv.Key} = {kv.Value}"))
                : "（无参数）";
            ApprovalBackdrop.IsVisible = true;
        });
    }

    private void OnSheetApprove(object? sender, RoutedEventArgs e) => CompleteSheet(
        _pendingRequest is { } r ? new ToolApprovalDecision(r.RequestId, Approved: true, Reason: "用户批准", AlwaysApprove: false) : null);

    private void OnSheetReject(object? sender, RoutedEventArgs e) => CompleteSheet(
        _pendingRequest is { } r ? new ToolApprovalDecision(r.RequestId, Approved: false, Reason: "用户拒绝", AlwaysApprove: false) : null);

    private void OnSheetCancel(object? sender, RoutedEventArgs e) => CompleteSheet(null);

    private void CompleteSheet(ToolApprovalDecision? decision)
    {
        ApprovalBackdrop.IsVisible = false;
        _pendingRequest = null;
        App.MobileApproval?.Complete(decision);
    }

    // ——

    /// <summary>点击历史会话行后收起下拉。延迟到下一帧再 Hide：Button.OnClick 先 RaiseEvent(Click)
    /// 再 Command.Execute，同步 Hide 会把按钮从视觉树摘掉、打断命令执行。</summary>
    public void OnConversationClicked(object? sender, RoutedEventArgs e)
        => Dispatcher.UIThread.Post(() => HistoryFlyoutButton.Flyout?.Hide());
}
