using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace Aether.AI.App.Views;

/// <summary>
/// 聊天页视图：头部（agent 选择/用量/历史/新聊天）+ 底部输入卡（模式选择器 + 计划面板 + plan-accept 条）
/// + 消息区（<see cref="ChatMessagesView"/>）。
/// </summary>
public partial class ChatView : UserControl
{
    public ChatView()
    {
        InitializeComponent();
    }

    /// <summary>点击历史会话行后收起下拉。延迟到下一帧再 Hide：Button.OnClick 先 RaiseEvent(Click)
    /// 再 Command.Execute，同步 Hide 会把按钮从视觉树摘掉、打断命令执行。</summary>
    public void OnConversationClicked(object? sender, RoutedEventArgs e)
        => Dispatcher.UIThread.Post(() => HistoryFlyoutButton.Flyout?.Hide());
}
