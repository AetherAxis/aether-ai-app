using Aether.AI.App.Core;
using Aether.AI.App.Views;
using Aether.AI.Core.Agents;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;

namespace Aether.AI.App;

/// <summary>
/// <see cref="IApprovalDialogService"/> 的桌面实现：模态 <see cref="ApprovalDialog"/>。
/// 移动 head（M3）换 bottom sheet——VM/引擎只认端口。
/// </summary>
public sealed class ApprovalDialogService : IApprovalDialogService
{
    public async Task<ToolApprovalDecision?> ShowAsync(ToolApprovalRequest request)
    {
        // ⚠ 审批解析器可能跑在**线程池线程**上（MAF agent 循环深处 ConfigureAwait(false) 丢 UI 上下文——
        // 2026-10-29/30 崩溃根因：NSWindow 在非主线程创建 → NSException → abort → 整个进程 SIGABRT，
        /// ⑮ DiagnosticReports Retired/Aether.AI.App.Desktop-2026-10-29-183431.ips 栈实锤）。
        // 必须先 marshal 到 UI 线程再碰任何控件/窗口。
        if (!Dispatcher.UIThread.CheckAccess())
            return await Dispatcher.UIThread.InvokeAsync(() => ShowAsync(request));

        var owner = (Avalonia.Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;
        if (owner is null) return null;
        var dialog = new ApprovalDialog(request);
        AppDialogs.ApplyTheme(dialog);   // 模态窗要自己叠主题 token，否则卡片/按钮全是裸白
        var decision = await dialog.ShowDialog<ToolApprovalDecision?>(owner);
        return decision;
    }
}
