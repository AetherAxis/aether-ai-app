using Aether.AI.App.Core;
using Aether.AI.Core.Agents;
using Avalonia.Interactivity;

namespace Aether.AI.App.Views;

/// <summary>工具审批模态框：批准（本工具）/ 拒绝 / 取消本轮（null → 引擎以已累积文本收敛）。</summary>
public partial class ApprovalDialog : Avalonia.Controls.Window
{
    private readonly ToolApprovalRequest _request;

    public ApprovalDialog(ToolApprovalRequest request)
    {
        _request = request;
        InitializeComponent();
        DataContext = request;
    }

    private void OnApprove(object? sender, RoutedEventArgs e)
        => Close(new ToolApprovalDecision(_request.RequestId, Approved: true, Reason: "用户批准", AlwaysApprove: false));

    private void OnReject(object? sender, RoutedEventArgs e)
        => Close(new ToolApprovalDecision(_request.RequestId, Approved: false, Reason: "用户拒绝", AlwaysApprove: false));

    private void OnCancel(object? sender, RoutedEventArgs e)
        => Close(null);
}
