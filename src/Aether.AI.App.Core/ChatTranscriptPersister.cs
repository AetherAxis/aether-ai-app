using Aether.AI.Core.Agents;
using Aether.AI.Core.Conversations.Runs;
using Aether.AI.Memory;

namespace Aether.AI.App.Core;

/// <summary>一次 run 持久化成功的结果（供 VM 做乐观侧栏 bump——需 ConversationId/时间戳）。</summary>
public sealed record ChatRunPersistResult(Guid ConversationId, int RunSeq, DateTimeOffset FinishedAt);

/// <summary>
/// 聊天 transcript 持久化器：把本轮 transcript 持久化为一个 <see cref="AgentRun"/> + 其 <see cref="AgentEvent"/>
/// 事件流（typed、append-only，CallId 在 ToolCall/ToolResult 写入时即配对），调 <see cref="IConversationStore.AppendRunAsync"/>
/// 原子写入。用户消息在 <see cref="AgentRun.UserMessage"/> 上，不另存；审批卡运行期态不持久化。
/// 自 monorepo ChatTranscriptPersister 逐字 port。
/// </summary>
public sealed class ChatTranscriptPersister
{
    private readonly IConversationStore _store;

    public ChatTranscriptPersister(IConversationStore store)
    {
        _store = store;
    }

    /// <summary>持久化本轮 run + events。返回 null = 空会话 id / 会话缺失 / 持久化失败（吞异常）。</summary>
    public async Task<ChatRunPersistResult?> PersistRunAsync(ConversationRuntime runtime,
        string userContent, IReadOnlyList<ChatMessageItem> runItems)
    {
        var convId = runtime.ConversationId;
        if (convId == Guid.Empty) return null;
        try
        {
            var conv = await _store.GetConversationAsync(convId);
            if (conv is null) return null;

            var now = DateTimeOffset.UtcNow;
            var hasAnswer = runItems.Any(i => i.IsText && !i.IsReasoning && !string.IsNullOrWhiteSpace(i.Content));
            var status = hasAnswer ? RunStatus.Completed : RunStatus.Interrupted;

            // 原子取号再构建——关闭 runtime.NextRunSeq 跨 await AppendRunAsync 的并发窗口。
            // 失败时已消耗的序号留空隙无害（seq 仅用于排序）。per-conversation 取号，跨会话并发不撞。
            var runSeq = System.Threading.Interlocked.Increment(ref runtime.NextRunSeq) - 1;
            var run = new AgentRun
            {
                ConversationId = convId,
                Seq = runSeq,
                Status = status,
                AgentId = conv.AgentId,
                AgentName = conv.AgentName,
                UserMessage = userContent,
                StartedAt = now,   // 近似（实际开始更早）；持久化/排序用
                FinishedAt = now,
            };

            // 事件构建 + JSON 序列化移到后台线程：run 轮含多 tool/reasoning/text 段时序列化在 UI
            // 线程会与 token flush 争线程导致卡顿。runItems 是 POCO 只读、run.Id 已是值——安全。
            var events = await Task.Run(() =>
            {
                var evs = new List<AgentEvent>();
                var seq = 1;
                foreach (var item in runItems)
                {
                    // 用户消息已在 run.UserMessage；审批卡运行期态不持久化；跳过空思考段。
                    if (item.IsUser || item.IsApprovalCard) continue;
                    if (item.IsText && item.IsReasoning && string.IsNullOrWhiteSpace(item.Content)) continue;

                    if (item.IsPlanCard)
                    {
                        var todos = item.Todos.Select(t => new TodoItemInfo(t.Title, t.Title, t.Description, t.IsComplete)).ToList();
                        evs.Add(MakeEvent(run.Id, ref seq, AgentEventType.PlanUpdated, new AgentEventPayload.PlanUpdated(todos)));
                    }
                    else if (item.IsToolStep)
                    {
                        evs.Add(MakeEvent(run.Id, ref seq, AgentEventType.ToolCall,
                            new AgentEventPayload.ToolCall(item.CallId, item.ToolName ?? "tool", item.Input)));
                        // 未完成的工具步（中断）只存 call，无 result——重建时为孤儿 call（UI 仍展示 call 卡），
                        // LLM 历史重建时由 EventHistoryRebuilder.DropOrphanToolCalls 丢弃。
                        if (item.IsToolCompleted)
                            evs.Add(MakeEvent(run.Id, ref seq, AgentEventType.ToolResult,
                                new AgentEventPayload.ToolResult(item.CallId, item.ToolName ?? "tool", item.Output)));
                    }
                    else if (item.IsReasoning)
                        evs.Add(MakeEvent(run.Id, ref seq, AgentEventType.ReasoningChunk,
                            new AgentEventPayload.ReasoningChunk(item.Content ?? string.Empty)));
                    else
                        evs.Add(MakeEvent(run.Id, ref seq, AgentEventType.TextChunk,
                            new AgentEventPayload.TextChunk(item.Content ?? string.Empty)));
                }
                return evs;
            });

            await _store.AppendRunAsync(run, events);
            return new ChatRunPersistResult(convId, runSeq, now);
        }
        catch
        {
            return null; // run 持久化失败不阻塞对话
        }
    }

    /// <summary>构造一个事件：Seq 自增分配，Payload 序列化为紧凑 JSON。</summary>
    private static AgentEvent MakeEvent(Guid runId, ref int seq, AgentEventType type, object payload)
    {
        var e = new AgentEvent { RunId = runId, Seq = seq, EventType = type, Payload = AgentEventPayload.Serialize(payload) };
        seq++;
        return e;
    }
}
