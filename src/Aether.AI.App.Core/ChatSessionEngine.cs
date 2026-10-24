using System.Text;
using Aether.AI.Core.Agents;

namespace Aether.AI.App.Core;

/// <summary>
/// 聊天流式渲染引擎：agent 推理→调工具→再推理→… 的过程中，把每段推理(<c>TextReasoningContent</c>)、
/// 答案(<c>TextContent</c>)、工具步、审批卡、计划卡按真实时序交错插入聊天流。
/// 推理进 💭 思考卡、答案进 markdown 气泡（建段时即区分 IsReasoning）。
/// <para>
/// 自 monorepo ChatStreamEngine port（2026-10-24），两处结构升级：
/// <list type="bullet">
///   <item>**审批协作循环不再自持**——runAsync 委托带 <see cref="ApprovalResponder"/> 缝，
///     经 ⑫ <c>HarnessRuntime.RunInteractiveAsync</c> 的 resolver 收编 while 循环（本引擎只负责
///     卡片状态迁移与恢复段创建）。</item>
///   <item>剥离桌面血统：桌宠打字机、文件编辑 diff 闭环、SQL 轮询工具名单（改为可注入
///     <see cref="PollingToolNames"/>）。</item>
/// </list>
/// </para>
/// 委托缝（行为契约）：<c>_selectedMode/_hasModeSupport</c> 逐工具实时读（运行中切档立即生效）；
/// <c>_isCurrentRuntime</c> 决定背景会话的计划变更是否上抛 <see cref="PlanSummaryChanged"/>。
/// </summary>
public sealed class ChatSessionEngine
{
    private readonly IUiDispatcher _dispatcher;
    private readonly IApprovalDialogService? _approvalDialog;
    private readonly IToolApprovalLevelLookup? _levelLookup; // AutoEdit 模式按工具等级区分自动批/弹框；null 退化为全弹（Manual 语义）
    private readonly Func<ChatMode> _selectedMode;
    private readonly Func<bool> _hasModeSupport;
    private readonly Func<ConversationRuntime, bool> _isCurrentRuntime;

    /// <summary>当前显示会话的计划计数变化通知（VM 转发 PlanSummary 计算属性）。</summary>
    public event Action? PlanSummaryChanged;

    /// <summary>
    /// 轮询型工具名单：这些工具语义上就是「检查当前状态/等待完成」，agent 会反复调用直到终态。
    /// 连续重复调用时合并为一张卡（刷新状态 + 轮询计数），避免刷屏。宿主按业务注入。
    /// </summary>
    public IReadOnlyCollection<string> PollingToolNames { get; set; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    public ChatSessionEngine(IUiDispatcher dispatcher, IApprovalDialogService? approvalDialog,
        IToolApprovalLevelLookup? levelLookup,
        Func<ChatMode> selectedMode, Func<bool> hasModeSupport,
        Func<ConversationRuntime, bool> isCurrentRuntime)
    {
        _dispatcher = dispatcher;
        _approvalDialog = approvalDialog;
        _levelLookup = levelLookup;
        _selectedMode = selectedMode;
        _hasModeSupport = hasModeSupport;
        _isCurrentRuntime = isCurrentRuntime;
    }

    /// <summary>流式分段光标：指向"当前正在追加 token 的消息项"；PendingTools 按 CallId 配对调用与结果。</summary>
    private sealed class StreamCursor
    {
        public ChatMessageItem Current;
        public readonly Dictionary<string, ChatMessageItem> PendingTools = new();
        // 轮询型工具去重：连续重复调用同一轮询工具时复用同一张工具卡。
        public ChatMessageItem? LastPollingCard;
        public string? LastPollingTool;
        public int PollCount;
        public StreamCursor(ChatMessageItem initial) => Current = initial;
    }

    /// <summary>
    /// 分段交错运行一轮：把每段推理/答案/工具步/审批卡/计划卡按真实时序交错插入聊天流，返回本轮产生的
    /// transcript 项（按时序），供持久化。runAsync 委托 = (handlers, approvalResponder, ct) → 终答全文
    /// （典型实现：IAgentRunner.RunInteractiveAsync 的闭包）。
    /// </summary>
    public async Task<List<ChatMessageItem>> RunTurnAsync(
        ConversationRuntime runtime, ChatTurnItem turn,
        string senderName,
        Func<StreamHandlers, ApprovalResponder, CancellationToken, Task<string>> runAsync,
        StringBuilder fullText, CancellationToken cancellationToken = default)
    {
        var items = turn.Children; // 本轮工作列表 = turn 的 Children（用户气泡为第一项，agent 产出随后）
        var cursor = new StreamCursor(NewTextSegment(senderName, isReasoningContent: false));
        // 本轮 todos 信号重置：只有本轮真的 todos_* 了才在末尾插计划快照卡（避免上一轮计划被误当本轮产出）。
        runtime.TodosTouchedThisTurn = false;
        await _dispatcher.InvokeAsync(() =>
        {
            items.Add(cursor.Current);
            turn.Status = TurnStatus.Working;
            runtime.IsRunning = true;
        });

        // —— Token 批量更新：不逐 token Post 到 UI 线程（会假死），攒批量刷新一次 ——
        // 有序 chunk 队列而非两个独立 StringBuilder：MAF 一个 update 可含混合 TextContent +
        // TextReasoningContent，且 reasoning→answer 边界常落在同一 flush 窗口。分两个 builder 会把
        // 边界倒置（一段连续 reasoning 被劈成两卡、answer 夹中间）。有序队列保留真实时序，
        // 连续同类型 chunk 合并进同一卡，仅在类型真切换时换段。
        // 流式期只更 Content（TextBlock 廉价、无 markdown 重解析）；完成时 FinalizeSegment 切
        // markdown viewer 一次性渲染格式。
        var pendingChunks = new List<(string Text, bool IsReasoning)>();
        var pendingLock = new object();
        var flushTimer = new System.Threading.Timer(_ => _dispatcher.Post(() =>
        {
            List<(string Text, bool IsReasoning)> snapshot;
            lock (pendingLock)
            {
                if (pendingChunks.Count == 0) return;
                snapshot = pendingChunks;          // 整表换出：drain 原子，流式线程继续往新表 append 不互斥
                pendingChunks = new List<(string, bool)>();
            }
            // 按真实时序处理；连续同类型 chunk 合并成一个字符串再追加（O(段数) 次拼接）。
            var buf = new System.Text.StringBuilder();
            bool? segType = null;
            foreach (var (text, isReasoning) in snapshot)
            {
                if (segType is { } prev && prev != isReasoning)
                {
                    EnsureTextSegment(cursor, items, senderName, isReasoningContent: prev);
                    cursor.Current.Content += buf.ToString();
                    buf.Clear();
                }
                segType = isReasoning;
                buf.Append(text);
            }
            if (buf.Length > 0 && segType is { } last)
            {
                EnsureTextSegment(cursor, items, senderName, isReasoningContent: last);
                cursor.Current.Content += buf.ToString();
            }
        }), null, Timeout.Infinite, Timeout.Infinite);

        // 审批状态持有（per-turn 闭包）：OnApprovalRequest 建卡，resolver 决定后迁移卡状态。
        // 请求是协作式的（一次只挂起一个），单一持有槽即够。
        ChatMessageItem? approvalCard = null;

        var handlers = new StreamHandlers(
            OnToken: (token, _) =>
            {
                fullText.Append(token);
                lock (pendingLock) pendingChunks.Add((token, false));
                flushTimer.Change(150, Timeout.Infinite); // 批量刷新（节流 Content 增量→TextBlock 重排频率）
                return Task.CompletedTask;
            },
            OnReasoning: (text, _) =>
            {
                lock (pendingLock) pendingChunks.Add((text, true));
                flushTimer.Change(150, Timeout.Infinite);
                return Task.CompletedTask;
            },
            OnToolEvent: (ev, _) =>
            {
                // todos_* 调用由 OnTodoList 渲染为计划卡，不重复出工具卡。
                if (ev.Name.StartsWith("todo", StringComparison.OrdinalIgnoreCase))
                    return Task.CompletedTask;
                _dispatcher.Post(() => HandleToolEventSegmented(cursor, items, ev, senderName));
                return Task.CompletedTask;
            },
            OnApprovalRequest: (req, _) =>
            {
                // 自动批（Auto / AutoEdit-非Dangerous）不建审批卡，工具 call+result 仍经 OnToolEvent 正常渲染；
                // 用户审批才建卡 + turn 转 AwaitingApproval。
                var autoDecision = ChatApprovalDecider.TryDecide(
                    _selectedMode(), _hasModeSupport(), req.ToolName, _levelLookup, req.RequestId);
                if (autoDecision is not null)
                    return Task.CompletedTask;
                var card = new ChatMessageItem
                {
                    Side = ChatMessageSide.Left,
                    ItemType = "approval",
                    Sender = senderName,
                    ToolName = req.ToolName,
                    CallId = req.CallId,
                    Input = req.Input,
                    ApprovalState = 0,
                };
                approvalCard = card;
                _dispatcher.Post(() =>
                {
                    var idx = items.IndexOf(cursor.Current);
                    if (idx < 0) idx = items.Count - 1;
                    items.Insert(idx + 1, card);
                    cursor.Current = card;
                    turn.Status = TurnStatus.AwaitingApproval;
                });
                return Task.CompletedTask;
            },
            OnTodoList: (list, _) =>
            {
                _dispatcher.Post(() => HandleTodoList(runtime, items, list, senderName));
                return Task.CompletedTask;
            },
            OnError: (text, _) =>
            {
                _dispatcher.Post(() =>
                {
                    EnsureTextSegment(cursor, items, senderName, isReasoningContent: false);
                    cursor.Current.Content += $"\n\n[错误：{text}]";
                });
                return Task.CompletedTask;
            },
            OnUsage: info => _dispatcher.Post(() => runtime.TokenInfo = info));

        // 审批决定获取器（⑫ RunInteractiveAsync 的 resolver 缝）：
        // 自动批策略 → 立即决定；用户审批 → 模态对话框；取消 → null（本轮以已累积文本收敛）。
        Task<ToolApprovalDecision?> DecideApprovalAsync(ToolApprovalRequest req, CancellationToken ct)
        {
            var auto = ChatApprovalDecider.TryDecide(
                _selectedMode(), _hasModeSupport(), req.ToolName, _levelLookup, req.RequestId);
            if (auto is not null)
                return Task.FromResult<ToolApprovalDecision?>(auto);

            var cardRef = approvalCard; // 与 OnApprovalRequest 建的卡同槽（协作式：一次一个）
            return ShowUserApprovalAsync(req, cardRef, turn, items, cursor, senderName, ct);
        }

        var errored = false;
        try
        {
            await runAsync(handlers, DecideApprovalAsync, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            errored = true;
            fullText.Append($"\n\n[运行失败：{ex.Message}]");
            await _dispatcher.InvokeAsync(() =>
            {
                EnsureTextSegment(cursor, items, senderName, isReasoningContent: false);
                cursor.Current.Content += $"\n\n[运行失败：{ex.Message}]";
            });
        }
        finally
        {
            // 最后一次 flush（确保剩余 token 写入）+ 停止定时器
            flushTimer.Change(0, Timeout.Infinite);
            await Task.Delay(60); // 等 flush 完成
            flushTimer.Dispose();

            await _dispatcher.InvokeAsync(() =>
            {
                FinalizeSegment(cursor.Current, items);
                // 每轮一快照：本轮确有 todos 更新且仍有活动计划时，把计划卡插到本轮首（用户消息之后、
                // agent 工作之前）——出现在"制定计划"的位置（参考 Claude Code）。TodosTouchedThisTurn
                // 门控：HasActivePlan/ActiveTodos 跨轮保留供浮动面板显示，不门控则每轮插陈旧快照。
                if (runtime.TodosTouchedThisTurn && runtime.HasActivePlan && runtime.ActiveTodos.Count > 0)
                {
                    var planCard = new ChatMessageItem { Side = ChatMessageSide.Left, ItemType = "plan", Sender = senderName, IsStreaming = false };
                    foreach (var t in runtime.ActiveTodos)
                        planCard.Todos.Add(new TodoItemVM { Title = t.Title, Description = t.Description, IsComplete = t.IsComplete });
                    items.Insert(1, planCard); // items[0] = 用户消息
                }
                turn.Finish(errored ? TurnStatus.Error : TurnStatus.Done);
                runtime.IsRunning = false;
                runtime.RunCts?.Dispose();
                runtime.RunCts = null;
            });
        }
        return items.ToList();
    }

    /// <summary>用户审批路径：弹框拿决定；null（取消）→ 卡转拒绝 + 本轮收敛；决定 → 卡转批准 + 开新答案段承接恢复流。</summary>
    private async Task<ToolApprovalDecision?> ShowUserApprovalAsync(
        ToolApprovalRequest req, ChatMessageItem? card, ChatTurnItem turn,
        IList<ChatMessageItem> items, StreamCursor cursor, string senderName, CancellationToken ct)
    {
        var userDecision = _approvalDialog is null ? null : await _approvalDialog.ShowAsync(req).ConfigureAwait(false);
        if (userDecision is null)
        {
            _dispatcher.Post(() => { if (card is not null) card.ApprovalState = 2; });
            return null; // 取消（框被关）→ 本轮以已累积文本收敛
        }
        var approved = userDecision.Approved;
        _dispatcher.Post(() =>
        {
            if (card is not null) card.ApprovalState = approved ? 1 : 2;
            turn.Status = TurnStatus.Working;
            // 开新答案段承接恢复流
            var seg = NewTextSegment(senderName, isReasoningContent: false);
            var idx = items.IndexOf(cursor.Current);
            if (idx < 0) idx = items.Count - 1;
            items.Insert(idx + 1, seg);
            cursor.Current = seg;
        });
        return userDecision;
    }

    private static ChatMessageItem NewTextSegment(string senderName, bool isReasoningContent) => new()
    {
        Side = ChatMessageSide.Left,
        ItemType = "text",
        Sender = senderName,
        Content = string.Empty,
        IsStreaming = true,
        // 只有 TextReasoningContent（真·思考）才 IsReasoning=true（折叠 💭）；
        // TextContent（可见叙述/答案）IsReasoning=false，冻结后渲染为展开的 markdown 文本。
        IsReasoning = isReasoningContent,
        IsReasoningContent = isReasoningContent,
    };

    private bool IsPollingTool(string toolName)
        => !string.IsNullOrEmpty(toolName) && PollingToolNames.Contains(toolName);

    /// <summary>确保当前段是目标类型的文本段；否则冻结/移除当前并新开一段。串行在 UI 线程调用。</summary>
    private void EnsureTextSegment(StreamCursor cursor, IList<ChatMessageItem> items, string senderName, bool isReasoningContent)
    {
        if (cursor.Current.IsText && cursor.Current.IsReasoningContent == isReasoningContent) return;
        var idx = items.IndexOf(cursor.Current);
        if (cursor.Current.IsText && string.IsNullOrWhiteSpace(cursor.Current.Content))
        {
            items.RemoveAt(idx); // 空段不留
        }
        else
        {
            if (cursor.Current.IsText) cursor.Current.IsStreaming = false; // 冻结非空异类段
            idx++;
        }
        var seg = NewTextSegment(senderName, isReasoningContent);
        items.Insert(idx, seg);
        cursor.Current = seg;
    }

    /// <summary>工具事件处理：Call 冻结当前段并插一条工具步（带 CallId）；Result 按 CallId 回填到同一条，再开新答案段。</summary>
    private void HandleToolEventSegmented(StreamCursor cursor, IList<ChatMessageItem> items, ToolStreamEvent ev, string senderName)
    {
        if (ev.Kind == ToolStreamEventKind.Call)
        {
            // 审批恢复轮 MAF 会重新发出已批准工具的 Call（同 CallId）。第一轮已建过同 CallId 的工具卡
            // （在审批卡之前），若不处理会留下 orphan 卡 + 恢复轮再建一张 → 同一逻辑调用两张卡。
            // 移除旧 orphan，在当前位置（审批卡之后）新建一张，保证 Result 落在审批卡之后、时序正确。
            if (!string.IsNullOrEmpty(ev.CallId) && cursor.PendingTools.TryGetValue(ev.CallId, out var stale))
            {
                cursor.PendingTools.Remove(ev.CallId);
                items.Remove(stale);
            }
            var idx = items.IndexOf(cursor.Current);
            if (cursor.Current.IsText && string.IsNullOrWhiteSpace(cursor.Current.Content))
            {
                items.RemoveAt(idx); // 空段不留
            }
            else
            {
                if (cursor.Current.IsText) cursor.Current.IsStreaming = false; // 冻结非空段
                idx++;
            }
            var tool = new ChatMessageItem
            {
                Side = ChatMessageSide.Left,
                ItemType = "tool",
                Sender = senderName,
                ToolName = ev.Name,
                CallId = ev.CallId,
                Input = ev.Input,
            };
            items.Insert(idx, tool);
            cursor.Current = tool;
            if (!string.IsNullOrEmpty(ev.CallId)) cursor.PendingTools[ev.CallId] = tool;

            // 轮询型工具去重：连续重复调用同一工具时复用上一张卡（刷新 callId/input/轮询计数），不新建。
            if (IsPollingTool(ev.Name))
            {
                if (cursor.PendingTools.TryGetValue(ev.CallId, out var p)) cursor.PendingTools.Remove(ev.CallId);
                if (cursor.LastPollingCard is not null && cursor.LastPollingTool == ev.Name)
                {
                    // 复用：拿掉刚插入的新卡，复用旧卡（刷新 CallId/Input）。轮询计数 +1。
                    items.Remove(tool);
                    cursor.Current = cursor.LastPollingCard;
                    cursor.LastPollingCard.CallId = ev.CallId;
                    cursor.LastPollingCard.Input = ev.Input;
                    // 复用卡时旧卡的 result 已落，置为「再轮询中」——保持单卡形态，result 到达再置完成。
                    cursor.LastPollingCard.IsToolCompleted = false;
                    cursor.PollCount++;
                    if (!string.IsNullOrEmpty(ev.CallId)) cursor.PendingTools[ev.CallId] = cursor.LastPollingCard;
                    cursor.LastPollingCard.ToolName = $"{ev.Name}（第 {cursor.PollCount} 次轮询）";
                    return; // 复用卡不走后续分支
                }
                cursor.LastPollingCard = tool;
                cursor.LastPollingTool = ev.Name;
                cursor.PollCount = 1;
            }
            else
            {
                // 非轮询工具打断了轮询序列 → 清状态（后续若再轮询，重新计一张）。
                cursor.LastPollingCard = null;
                cursor.LastPollingTool = null;
                cursor.PollCount = 0;
            }
        }
        else // Result：回填到对应工具步（合并为一条），再开新答案段
        {
            ChatMessageItem? tool = null;
            if (!string.IsNullOrEmpty(ev.CallId) && cursor.PendingTools.TryGetValue(ev.CallId, out var t))
            {
                tool = t;
                cursor.PendingTools.Remove(ev.CallId);
            }
            else if (cursor.Current.ItemType == "tool")
            {
                tool = cursor.Current; // 退路：顺序配对
            }
            if (tool is null) return;

            tool.Output = ev.Output;
            tool.IsToolCompleted = true;

            var idx = items.IndexOf(tool);
            if (idx < 0) idx = items.Count - 1;
            var next = NewTextSegment(senderName, isReasoningContent: false);
            items.Insert(idx + 1, next);
            cursor.Current = next;
        }
    }

    /// <summary>todos_* 调用后更新浮动计划面板（runtime.ActiveTodos）。快照（每轮一卡、入对话流持久化）在 RunTurnAsync 末尾插入。
    /// 计划卡随 transcript 持久化、恢复，重启后仍在。操作指定 runtime——背景会话的计划不污染当前显示会话的面板。</summary>
    private void HandleTodoList(ConversationRuntime runtime, IList<ChatMessageItem> items, IReadOnlyList<TodoItemInfo> list, string senderName)
    {
        // 计划只显示在浮动面板（输入框上方），不在对话流里重复显示。
        // 去重：agent 跨轮重新规划时有时对同一事项重复 todos_add。按 Title（忽略大小写/首尾空白）去重，
        // 保留首次出现顺序；若任一副本已完成，则合并为已完成。
        runtime.ActiveTodos.Clear();
        var seen = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var merged = new List<(string Title, string? Description, bool IsComplete)>();
        foreach (var t in list)
        {
            var key = (t.Title ?? string.Empty).Trim();
            if (key.Length == 0)
            {
                merged.Add((t.Title ?? string.Empty, t.Description, t.IsComplete));
                continue;
            }
            if (seen.TryGetValue(key, out var i))
            {
                if (t.IsComplete) merged[i] = (merged[i].Title, merged[i].Description, true);
            }
            else
            {
                seen[key] = merged.Count;
                merged.Add((t.Title ?? string.Empty, t.Description, t.IsComplete));
            }
        }
        foreach (var m in merged)
            runtime.ActiveTodos.Add(new TodoItemVM { Title = m.Title, Description = m.Description ?? string.Empty, IsComplete = m.IsComplete });
        runtime.HasActivePlan = runtime.ActiveTodos.Count > 0;
        // PlanSummary（"X/Y" 计数）是 VM 计算属性。仅当此 runtime 是当前显示会话时才上抛。
        if (_isCurrentRuntime(runtime))
            PlanSummaryChanged?.Invoke();
        runtime.TodosTouchedThisTurn = true; // 本轮确有 todos 更置（VM 末尾据此置 PlanAwaitingAccept）
    }

    /// <summary>运行结束：末段空则移除；否则停止流式。仅 TextContent 末段翻为最终回答（markdown 气泡），
    /// TextReasoningContent 段保持 💭 思考卡。中间各段保持思考态。</summary>
    private static void FinalizeSegment(ChatMessageItem last, IList<ChatMessageItem> items)
    {
        if (!last.IsText) return;
        if (string.IsNullOrWhiteSpace(last.Content))
        {
            items.Remove(last);
        }
        else
        {
            last.IsStreaming = false;
            if (!last.IsReasoningContent)
            {
                last.IsReasoning = false; // 末段 TextContent = 最终回答
                // IsStreaming=false 切到完成态：完成态 viewer 绑 Content 一次性渲染 markdown 格式。
                // MarkdownContent 同步赋值仅作安全网，不驱动桌面展示。
                last.MarkdownContent = last.Content;
            }
        }
    }
}
