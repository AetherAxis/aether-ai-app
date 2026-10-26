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
        // 模型下拉的「管理模型…」项 → 打开配置窗（View/VM 分工：VM 只发意图，宿主决定形态）
        DataContextChanged += (_, _) => HookModelManage();
        HookModelManage();
        if (App.MobileApproval is { } broker)
        {
            broker.Requested += OnApprovalRequested;
            DetachedFromVisualTree += (_, _) => broker.Requested -= OnApprovalRequested;
        }
    }

    private void HookModelManage()
    {
        if (_hookedVm is not null) _hookedVm.ManageLlmRequested -= OnManageLlmRequested;
        _hookedVm = DataContext as ViewModels.ChatViewModel;
        if (_hookedVm is not null) _hookedVm.ManageLlmRequested += OnManageLlmRequested;
    }

    /// <summary>下拉里选中「管理模型…」→ 开配置窗。延到下一帧：先让下拉浮层收起，再弹窗，避免抢焦点。</summary>
    private void OnManageLlmRequested() => Dispatcher.UIThread.Post(ShowModelSettings);

    // —— 模型设置浮层（移动形态）——

    private void OnSettingsGear(object? sender, RoutedEventArgs e) => OpenModelOverlay();

    private void OpenModelOverlay()
    {
        // 同一份 LlmProfilesView/SettingsViewModel（移动组合根解析）；首次打开时接 DataContext。
        if (MobileLlmView.DataContext is null && App.MobileProvider is { } provider)
            MobileLlmView.DataContext = provider.GetRequiredService<ViewModels.SettingsViewModel>();
        ModelSettingsOverlay.IsVisible = true;
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
            _ = dialog.ShowDialog(desktop.MainWindow);
            return;
        }
        OpenModelOverlay();   // 移动形态：内嵌浮层
    }

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
