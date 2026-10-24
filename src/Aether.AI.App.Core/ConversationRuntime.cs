using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Aether.AI.App.Core;

/// <summary>
/// 单个会话的运行时状态：per-conversation 可变状态（Messages/ActiveTodos/IsRunning/计划状态/CT/
/// run-seq/历史增量缓冲）收拢于此，使多会话可并发——每个会话持独立 runtime，背景 run 往自己的
/// Messages 追加，切会话只换指针不取消背景 run。自 monorepo ConversationRuntime 逐字 port。
/// </summary>
public sealed partial class ConversationRuntime : ObservableObject
{
    /// <summary>会话 Id。待新聊天态为 <see cref="Guid.Empty"/>（首次发送落库拿到真 Guid 后回填 + 注册进字典）。</summary>
    public Guid ConversationId;

    /// <summary>当前轮的取消令牌（停止生成 / 删会话用）。切会话不取消——背景 run 继续跑。</summary>
    public CancellationTokenSource? RunCts;

    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private bool _hasActivePlan;
    [ObservableProperty] private bool _planAwaitingAccept;
    [ObservableProperty] private string? _tokenInfo;
    [ObservableProperty] private bool _hasMessages;

    /// <summary>本轮是否调过 todos_*（区分「本轮新计划」vs「上轮残留」）。每轮开始重置。非 observable（内部旗标）。</summary>
    public bool TodosTouchedThisTurn;

    /// <summary>本会话下一个 run 的序号（1-based；新建=1，恢复=max seq+1）。persist 用 Interlocked 取号。</summary>
    public int NextRunSeq = 1;

    /// <summary>更早的历史轮暂存（上滚增量加载）。null=未从 DB 加载过/已全量在 Messages。</summary>
    public List<ChatTurnItem>? PendingHistoryTurns;

    /// <summary>历史正在从 DB 加载中（防切走又切回双重加载：Messages 仍空但加载 Task 未完）。</summary>
    public volatile bool IsLoadingHistory;

    /// <summary>本会话消息流（turns）。run 的 turn.Children 属于此集合——背景 run 继续追加即使非当前显示会话。</summary>
    public ObservableCollection<ChatTurnItem> Messages { get; } = new();

    /// <summary>浮动计划面板的 todos（贴着输入框上方，实时显示最新计划）。</summary>
    public ObservableCollection<TodoItemVM> ActiveTodos { get; } = new();

    public ConversationRuntime(Guid conversationId)
    {
        ConversationId = conversationId;
        // Messages 变更驱动 HasMessages（空态 hero 显隐）——经 VM 转发到 UI。
        Messages.CollectionChanged += (_, _) => HasMessages = Messages.Count > 0;
    }
}
