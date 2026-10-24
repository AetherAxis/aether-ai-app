using Aether.AI.App.Core;
using Aether.AI.Core.Agents;

namespace Aether.AI.App;

/// <summary>
/// 移动端审批 broker：<see cref="ShowAsync"/> 发出请求事件（ChatView 内嵌的 bottom sheet 宿主订阅并展示），
/// 用户点按钮后经 <see cref="Complete"/> 回填 TaskCompletionSource。桌面形态不用本类（走模态对话框）。
/// </summary>
public sealed class MobileApprovalService : IApprovalDialogService
{
    private TaskCompletionSource<ToolApprovalDecision?>? _tcs;

    /// <summary>当前挂起待批的请求（一次一个——MAF 协作式审批）。null = 无。</summary>
    public ToolApprovalRequest? Pending { get; private set; }

    public bool HasPending => Pending is not null;

    /// <summary>挂起请求的 RequestId（--demo 自动验收用）。</summary>
    public string? PendingRequestId => Pending?.RequestId;

    /// <summary>新的审批请求到达（UI 宿主展示 bottom sheet）。</summary>
    public event Action<ToolApprovalRequest>? Requested;

    public Task<ToolApprovalDecision?> ShowAsync(ToolApprovalRequest request)
    {
        var tcs = new TaskCompletionSource<ToolApprovalDecision?>();
        _tcs = tcs;
        Pending = request;
        Requested?.Invoke(request);
        return tcs.Task;
    }

    /// <summary>bottom sheet 按钮回填决定（null = 取消本轮）。</summary>
    public void Complete(ToolApprovalDecision? decision)
    {
        Pending = null;
        var tcs = _tcs;
        _tcs = null;
        tcs?.TrySetResult(decision);
    }
}
