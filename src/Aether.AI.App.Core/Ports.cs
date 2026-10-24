using Aether.AI.Core.Agents;

namespace Aether.AI.App.Core;

/// <summary>
/// UI 线程调度抽象，使本库的聊天引擎/ViewModel 不依赖任何具体 UI 框架
/// （Avalonia / 未来任何宿主各自提供实现）。
/// </summary>
public interface IUiDispatcher
{
    /// <summary>在 UI 线程执行并等待完成。</summary>
    Task InvokeAsync(Action action);

    /// <summary>把动作投递到 UI 线程异步执行（fire-and-forget，不等待）。</summary>
    void Post(Action action);
}

/// <summary>
/// 弹出工具审批对话框，返回用户的审批决定；用户关闭/取消返回 <c>null</c>。
/// UI 无关接口；实现由各 UI 框架提供（桌面 = 模态对话框，移动 = bottom sheet）。
/// </summary>
public interface IApprovalDialogService
{
    Task<ToolApprovalDecision?> ShowAsync(ToolApprovalRequest request);
}

/// <summary>审批决定获取器：<see cref="Aether.AI.Harness.HarnessRuntime.RunInteractiveAsync"/> 的 resolver 缝。</summary>
public delegate Task<ToolApprovalDecision?> ApprovalResponder(
    ToolApprovalRequest request, CancellationToken cancellationToken);
