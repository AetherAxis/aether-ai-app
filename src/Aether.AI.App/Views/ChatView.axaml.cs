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

    public ChatView()
    {
        InitializeComponent();
        // 设置齿轮：仅移动形态显示（桌面走 ⑩ 工作台的设置 tab）。
        var isDesktop = Avalonia.Application.Current?.ApplicationLifetime
            is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime;
        SettingsGearButton.IsVisible = !isDesktop;
        if (App.MobileApproval is { } broker)
        {
            broker.Requested += OnApprovalRequested;
            DetachedFromVisualTree += (_, _) => broker.Requested -= OnApprovalRequested;
        }
    }

    // —— 设置浮层（移动形态）——

    private void OnSettingsGear(object? sender, RoutedEventArgs e)
    {
        // 同一份 SettingsView/SettingsViewModel（移动组合根解析）；首次打开时接 DataContext。
        if (MobileSettingsView.DataContext is null && App.MobileProvider is { } provider)
            MobileSettingsView.DataContext = provider.GetRequiredService<ViewModels.SettingsViewModel>();
        SettingsOverlay.IsVisible = true;
    }

    private void OnSettingsClose(object? sender, RoutedEventArgs e)
        => SettingsOverlay.IsVisible = false;

    // —— LLM 档案管理（输入卡「模型 ⚙」入口，2026-10-26——⑪ 连接档案同款范式）——
    // 桌面 = 模态窗（复用设置页同一份 SettingsView/SettingsViewModel 单例，编辑即时落档、chips 随 Changed 刷新）；
    // 移动 = 已有的设置浮层（同一入口语义）。

    private void OnManageLlm(object? sender, RoutedEventArgs e)
    {
        if (Avalonia.Application.Current?.ApplicationLifetime
            is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop)
        {
            var vm = Aether.Workbench.WorkbenchServices.GetService<ViewModels.SettingsViewModel>()
                ?? App.MobileProvider?.GetRequiredService<ViewModels.SettingsViewModel>();
            if (vm is null || desktop.MainWindow is null) return;
            var dialog = new Avalonia.Controls.Window
            {
                Title = "管理 LLM 档案",
                Content = new SettingsView { DataContext = vm },
                MinWidth = 520, MinHeight = 560,
                WindowStartupLocation = Avalonia.Controls.WindowStartupLocation.CenterOwner,
            };
            _ = dialog.ShowDialog(desktop.MainWindow);
            return;
        }
        OnSettingsGear(sender, e);   // 移动形态：复用设置浮层
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
