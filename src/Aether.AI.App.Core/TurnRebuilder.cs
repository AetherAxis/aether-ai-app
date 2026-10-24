using Aether.AI.Core.Conversations.Runs;

namespace Aether.AI.App.Core;

/// <summary>
/// 从 typed 事件流重建 transcript turns 的纯函数（无实例状态，可单测）。
/// 自 monorepo TurnRebuilder 逐字 port——「事件流 → UI turns」是历史恢复的独立职责。
/// </summary>
public static class TurnRebuilder
{
    /// <summary>
    /// 按 run 重建全部 turn：每条 <see cref="AgentRun"/> 一轮（Header=run.UserMessage + 用户气泡），其 <see cref="AgentEvent"/>
    /// 还原思考段/工具步/答案/计划卡。工具 call+result 按 CallId 合并到同一张工具卡。历史轮标记 Done（Failed→Error）。
    /// </summary>
    public static List<ChatTurnItem> BuildTurnsFromRuns(
        IReadOnlyList<AgentRun> runs,
        IReadOnlyDictionary<Guid, IReadOnlyList<AgentEvent>> eventsByRun,
        string agentName)
    {
        var turns = new List<ChatTurnItem>();
        foreach (var run in runs.OrderBy(r => r.Seq))
        {
            var name = string.IsNullOrWhiteSpace(run.AgentName) ? agentName : run.AgentName;
            var turn = new ChatTurnItem { Header = run.UserMessage };
            // 用户气泡（用户请求属于本次执行）
            turn.Children.Add(new ChatMessageItem
            {
                Side = ChatMessageSide.Right, ItemType = "text", Sender = "我",
                Content = run.UserMessage, IsStreaming = false
            });

            if (eventsByRun.TryGetValue(run.Id, out var events) && events is not null && events.Count > 0)
            {
                // CallId → 工具卡，result 到来时回填 Output + IsToolCompleted。
                var toolCardsByCallId = new Dictionary<string, ChatMessageItem>();
                foreach (var e in events.OrderBy(x => x.Seq))
                {
                    switch (e.EventType)
                    {
                        case AgentEventType.TextChunk:
                            var text = AgentEventPayload.Deserialize<AgentEventPayload.TextChunk>(e.Payload)?.Text ?? string.Empty;
                            turn.Children.Add(new ChatMessageItem
                            {
                                Side = ChatMessageSide.Left, ItemType = "text", Sender = name,
                                Content = text, IsStreaming = false
                            });
                            break;

                        case AgentEventType.ReasoningChunk:
                            var rtext = AgentEventPayload.Deserialize<AgentEventPayload.ReasoningChunk>(e.Payload)?.Text ?? string.Empty;
                            turn.Children.Add(new ChatMessageItem
                            {
                                Side = ChatMessageSide.Left, ItemType = "text", Sender = name,
                                Content = rtext, IsStreaming = false,
                                IsReasoning = true, IsReasoningContent = true
                            });
                            break;

                        case AgentEventType.ToolCall:
                            var tc = AgentEventPayload.Deserialize<AgentEventPayload.ToolCall>(e.Payload);
                            var card = new ChatMessageItem
                            {
                                Side = ChatMessageSide.Left,
                                ItemType = "tool",
                                Sender = name,
                                ToolName = tc?.Name ?? "tool",
                                CallId = tc?.CallId ?? string.Empty,
                                Input = tc?.Input,
                                IsToolCompleted = false,
                                IsStreaming = false,
                            };
                            if (!string.IsNullOrEmpty(card.CallId)) toolCardsByCallId[card.CallId] = card;
                            turn.Children.Add(card);
                            break;

                        case AgentEventType.ToolResult:
                            var tr = AgentEventPayload.Deserialize<AgentEventPayload.ToolResult>(e.Payload);
                            var cid = tr?.CallId ?? string.Empty;
                            if (!string.IsNullOrEmpty(cid) && toolCardsByCallId.TryGetValue(cid, out var existing))
                            {
                                existing.Output = tr?.Output;
                                existing.IsToolCompleted = true;
                            }
                            else
                            {
                                // 无前置 call（不应发生）——单独 result 卡。
                                turn.Children.Add(new ChatMessageItem
                                {
                                    Side = ChatMessageSide.Left, ItemType = "tool", Sender = name,
                                    ToolName = tr?.Name ?? "tool", CallId = cid,
                                    Output = tr?.Output, IsToolCompleted = true, IsStreaming = false,
                                });
                            }
                            break;

                        case AgentEventType.PlanUpdated:
                            var pc = AgentEventPayload.Deserialize<AgentEventPayload.PlanUpdated>(e.Payload);
                            var planCard = new ChatMessageItem
                            {
                                Side = ChatMessageSide.Left, ItemType = "plan", Sender = name, IsStreaming = false
                            };
                            if (pc?.Todos is not null)
                                foreach (var t in pc.Todos)
                                    planCard.Todos.Add(new TodoItemVM { Title = t.Title, Description = t.Description, IsComplete = t.IsComplete });
                            turn.Children.Add(planCard);
                            break;

                        // Approval/Error/Usage 不持久化，跳过。
                        case AgentEventType.ApprovalRequested:
                        case AgentEventType.ApprovalDecision:
                        case AgentEventType.Error:
                        case AgentEventType.Usage:
                            break;
                    }
                }
            }

            turn.Finish(run.Status == RunStatus.Failed ? TurnStatus.Error : TurnStatus.Done);
            // 用 run 实际时长覆盖（比 ~0s 更准）。
            if (run.FinishedAt is not null)
                turn.ElapsedText = $"{(run.FinishedAt.Value - run.StartedAt).TotalSeconds:F1}s";
            turns.Add(turn);
        }
        return turns;
    }
}
