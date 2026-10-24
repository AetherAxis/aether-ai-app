using Aether.AI.Core.Conversations.Runs;
using Aether.AI.Memory;

namespace Aether.AI.App.Core;

/// <summary>历史加载结果：全部重建轮次 + 该会话下一个 run 序号（max(Seq)+1；空会话=1）。</summary>
public sealed record ConversationHistoryResult(List<ChatTurnItem> Turns, int NextRunSeq);

/// <summary>
/// 会话历史加载器：从 typed 事件流重建 transcript——每条 run 一轮（user 消息在 run.UserMessage），
/// events 还原思考/工具/答案/计划卡。本类只做纯 DB 读 + 重建（UI 策略——Task.Run 包裹、分批渲染、
/// IsLoadingHistory 守卫——留 VM）。自 monorepo ConversationHistoryLoader port。
/// </summary>
public sealed class ConversationHistoryLoader
{
    private readonly IConversationStore _store;

    public ConversationHistoryLoader(IConversationStore store)
    {
        _store = store;
    }

    /// <summary>加载并重建整个会话 transcript。返回 null = 会话不存在（调用方据此把 NextRunSeq 归 1）。</summary>
    public async Task<ConversationHistoryResult?> LoadAsync(Guid conversationId, string? agentName)
    {
        var conv = await _store.GetConversationAsync(conversationId);
        if (conv is null) return null;
        var runs = await _store.GetRunsAsync(conversationId);
        var eventsByRun = new Dictionary<Guid, IReadOnlyList<AgentEvent>>();
        foreach (var run in runs)
            eventsByRun[run.Id] = await _store.GetEventsAsync(run.Id);
        var seq = runs.Count == 0 ? 1 : runs.Max(r => r.Seq) + 1;
        var all = TurnRebuilder.BuildTurnsFromRuns(runs, eventsByRun, agentName ?? "助手");
        return new ConversationHistoryResult(all, seq);
    }
}
