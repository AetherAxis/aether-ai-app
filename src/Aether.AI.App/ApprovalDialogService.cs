using Aether.AI.App.Core;
using Aether.AI.App.Views;
using Aether.AI.Core.Agents;
using Avalonia.Controls.ApplicationLifetimes;

namespace Aether.AI.App;

/// <summary>
/// <see cref="IApprovalDialogService"/> 的桌面实现：模态 <see cref="ApprovalDialog"/>。
/// 移动 head（M3）换 bottom sheet——VM/引擎只认端口。
/// </summary>
public sealed class ApprovalDialogService : IApprovalDialogService
{
    public async Task<ToolApprovalDecision?> ShowAsync(ToolApprovalRequest request)
    {
        var owner = (Avalonia.Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;
        if (owner is null) return null;
        var dialog = new ApprovalDialog(request);
        var decision = await dialog.ShowDialog<ToolApprovalDecision?>(owner);
        return decision;
    }
}
