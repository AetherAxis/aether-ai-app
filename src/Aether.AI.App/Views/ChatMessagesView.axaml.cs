using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Aether.AI.App.Views;

/// <summary>
/// 消息区（聊天的消息渲染面）：turn 模板渲染 + 贴底滚动 + 滚到底部浮动按钮 + 上滚历史增量加载。
/// 自 monorepo ChatMessagesView port（剥离文件 diff/撤销区——M1.5 以端口回归）。
/// </summary>
public partial class ChatMessagesView : UserControl
{
    /// <summary>距底部小于该值视为"贴底"（新事件自动滚底）；上滚超过该值显示回底按钮 + 触发历史加载。</summary>
    private const double BottomThreshold = 80;

    public ChatMessagesView()
    {
        InitializeComponent();
    }

    private void OnMessagesScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (sender is not ScrollViewer sv) return;
        var distance = sv.Extent.Height - sv.Offset.Y - sv.Viewport.Height;
        var nearBottom = distance < BottomThreshold;
        ScrollToBottomButton.IsVisible = !nearBottom && sv.Extent.Height > sv.Viewport.Height + BottomThreshold;

        // 滚到顶部 → 增量加载更早历史轮（VM 从暂存队列取一批插入顶部）。
        if (sv.Offset.Y <= 0 && sv.Extent.Height > sv.Viewport.Height && DataContext is ViewModels.ChatViewModel vm)
        {
            if (vm.LoadMoreHistory() > 0)
            {
                // 保持视觉位置：插入发生在上方，把偏移补回刚加载的高度（近似——不追完美锚定）。
                sv.Offset = new Avalonia.Vector(sv.Offset.X, sv.Offset.Y + e.ExtentDelta.Y);
            }
        }
    }

    private void OnScrollToBottomClick(object? sender, RoutedEventArgs e)
        => MessagesScrollViewer?.ScrollToEnd();

    /// <summary>工具卡/思考卡点击展开-收起。</summary>
    private void OnToolCardTapped(object? sender, Avalonia.Input.PointerPressedEventArgs e)
    {
        if ((sender as Avalonia.Controls.Control)?.DataContext is Core.ChatMessageItem item)
        {
            // 流式思考卡默认展开（IsStreamingExpanded），完成后点击切换折叠态。
            if (item.IsStreaming) item.IsStreamingExpanded = !item.IsStreamingExpanded;
            else item.IsExpanded = !item.IsExpanded;
            e.Handled = true;
        }
    }
}
