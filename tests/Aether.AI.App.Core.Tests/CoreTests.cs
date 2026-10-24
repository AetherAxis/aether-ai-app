using System.Runtime.CompilerServices;
using Aether.AI.App.Core;
using Aether.AI.Core.Agents;
using Aether.AI.Core.Conversations;
using Aether.AI.Core.Conversations.Runs;
using Aether.AI.Harness;
using Aether.AI.Harness.Agents;
using Aether.AI.Harness.Sessions;
using Aether.AI.Harness.Skills;
using Aether.AI.Harness.Tools;
using Aether.AI.Memory;
using Aether.AI.Memory.Stores;
using Microsoft.Extensions.AI;
using Xunit;

namespace Aether.AI.App.Core.Tests;

/// <summary>
/// ChatSessionEngine 的分段交错流（含审批 resolver 缝）：scripted IChatClient 直驱 ⑫ HarnessRuntime，
/// 用同步假 dispatcher 观察事件流落位。覆盖：推理/答案分段、工具卡 CallId 配对、审批卡状态迁移、终段翻 markdown。
/// </summary>
public class ChatSessionEngineTests : IDisposable
{
    private readonly string _dbPath;

    public ChatSessionEngineTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"app-core-tests-{Guid.NewGuid():N}.db");
    }

    public void Dispose()
    {
        try { File.Delete(_dbPath); } catch { /* 临时档 */ }
    }

    /// <summary>同步假 dispatcher：立即执行（测试线程即 UI 线程）。</summary>
    private sealed class InlineDispatcher : IUiDispatcher
    {
        public Task InvokeAsync(Action action) { action(); return Task.CompletedTask; }
        public void Post(Action action) => action();
    }

    /// <summary>
    /// 伪 IChatClient：按「轮」消费——每次 GetStreamingResponseAsync 弹出一轮（多个 update 按序 yield），
    /// 对齐真实 LLM 流（一次 run 消费多个 update；审批恢复轮 = 下一轮）。自 ⑫ Harness.Tests 的假件改造。
    /// </summary>
    private sealed class ScriptedChatClient : IChatClient
    {
        public List<ChatResponseUpdate[]> Rounds { get; } = new();
        private int _calls;

        public void Dispose() { }

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => Stream(cancellationToken);

        private async IAsyncEnumerable<ChatResponseUpdate> Stream([EnumeratorCancellation] CancellationToken ct)
        {
            await Task.Yield();
            var index = _calls++;
            if (index < Rounds.Count)
                foreach (var u in Rounds[index])
                    yield return u;
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
    }

    private sealed class ConstChatClientFactory(IChatClient client) : Aether.AI.Brains.Abstractions.IChatClientFactory
    {
        public IChatClient Create(string? modelId) => client;
    }

    private (HarnessRuntime Runtime, Guid SessionId) BuildRuntime(ScriptedChatClient client, bool requireApproval)
    {
        var registry = new ToolRegistry();
        registry.Register("Echo", "回声演示工具", "test",
            _ => AIFunctionFactory.Create((Func<string, string>)(text => $"echo:{text}"), "Echo"),
            requireApproval ? ToolApprovalLevel.Dangerous : ToolApprovalLevel.Safe);
        var factory = new AgentFactory(new ConstChatClientFactory(client), registry, new SkillRegistry());
        var runtime = new HarnessRuntime(factory, new SessionManager());
        runtime.RegisterAgent(new AgentDefinition
        {
            Id = "engine-test",
            Name = "EngineTest",
            Instructions = "be brief",
            Tools = new[] { "Echo" },
            RequireToolApproval = requireApproval,
        });
        return (runtime, runtime.StartSession("engine-test").SessionId);
    }

    /// <summary>LLM 大脑形态的完整轮：推理 → 答案流 → 终段翻 markdown（无审批）。</summary>
    [Fact]
    public async Task Reasoning_then_answer_segments_interleave_and_finalize_to_markdown()
    {
        var client = new ScriptedChatClient();
        client.Rounds.Add(new[]
        {
            new ChatResponseUpdate { Contents = new AIContent[] { new TextReasoningContent("想一下") } },
            new ChatResponseUpdate { Contents = new AIContent[] { new TextContent("答") } },
        });

        var (runtime, sessionId) = BuildRuntime(client, requireApproval: false);
        var engine = new ChatSessionEngine(new InlineDispatcher(), approvalDialog: null, levelLookup: null,
            () => ChatMode.AutoEdit, () => false, _ => true);

        var convRuntime = new ConversationRuntime(Guid.NewGuid());
        var turn = new ChatTurnItem { Header = "问" };
        var full = new System.Text.StringBuilder();

        var items = await engine.RunTurnAsync(convRuntime, turn, "测试",
            (handlers, resolver, ct) => runtime.RunInteractiveAsync(sessionId, "问", handlers, (req, ct2) => resolver(req, ct2), null, ct),
            full);

        // turn.Children[0]=用户消息；随后思考段（IsReasoningContent）与答案段（终答）。
        var texts = items.Where(i => i.IsText && !i.IsUser).ToList();
        Assert.Equal(2, texts.Count);
        Assert.True(texts[0].IsReasoningContent);
        Assert.Equal("想一下", texts[0].Content);
        Assert.False(texts[1].IsReasoningContent);
        Assert.Equal("答", texts[1].Content);
        Assert.False(texts[1].IsStreaming);   // 终段冻结
        Assert.Equal("答", texts[1].MarkdownContent); // 完成态切 markdown
        Assert.Equal(TurnStatus.Done, turn.Status);
        Assert.False(convRuntime.IsRunning);
    }

    /// <summary>审批轮：OnApprovalRequest 建卡 → resolver 用户批准 → 卡转已批准 + 恢复段承接工具与终答。</summary>
    [Fact]
    public async Task Approval_round_creates_card_then_resolver_decision_transitions_it()
    {
        var client = new ScriptedChatClient();
        client.Rounds.Add(new[]
        {
            new ChatResponseUpdate
            {
                Contents = new AIContent[]
                {
                    new FunctionCallContent("call-1", "Echo",
                        new Dictionary<string, object?> { ["text"] = "600000" })
                }
            },
        });
        client.Rounds.Add(new[]
        {
            new ChatResponseUpdate { Contents = new AIContent[] { new TextContent("done") } },
        });

        var (runtime, sessionId) = BuildRuntime(client, requireApproval: true);

        var engine = new ChatSessionEngine(new InlineDispatcher(), approvalDialog: new ApprovalDialogFake(), levelLookup: null,
            () => ChatMode.Manual, () => false, _ => true);

        var convRuntime = new ConversationRuntime(Guid.NewGuid());
        var turn = new ChatTurnItem { Header = "查600000" };
        var full = new System.Text.StringBuilder();

        var items = await engine.RunTurnAsync(convRuntime, turn, "测试",
            (handlers, resolver, ct) => runtime.RunInteractiveAsync(sessionId, "查600000", handlers, (req, ct2) => resolver(req, ct2), null, ct),
            full);

        // 审批卡存在且转已批准；工具卡 call+result 合一；终答段承接。
        var card = items.FirstOrDefault(i => i.IsApprovalCard);
        Assert.NotNull(card);
        Assert.Equal(1, card!.ApprovalState); // 已批准
        Assert.Equal("Echo", card.ToolName);

        var tool = items.FirstOrDefault(i => i.IsToolStep);
        Assert.NotNull(tool);
        Assert.True(tool!.IsToolCompleted);
        Assert.Contains(tool.Output?.Values ?? (System.Collections.Generic.IEnumerable<object>)Array.Empty<object>(), v => v?.ToString()?.Contains("echo:") == true);

        Assert.Contains(items, i => i.IsText && !i.IsUser && !i.IsReasoning && i.Content == "done");
        Assert.Equal("done", full.ToString());
    }

    /// <summary>审批取消：resolver 返回 null → 卡转拒绝、turn 收敛（工具未执行）。</summary>
    [Fact]
    public async Task Approval_cancel_marks_card_rejected_and_ends_turn()
    {
        var client = new ScriptedChatClient();
        client.Rounds.Add(new[]
        {
            new ChatResponseUpdate
            {
                Contents = new AIContent[]
                {
                    new FunctionCallContent("call-1", "Echo",
                        new Dictionary<string, object?> { ["text"] = "600000" })
                }
            },
        });

        var (runtime, sessionId) = BuildRuntime(client, requireApproval: true);
        var engine = new ChatSessionEngine(new InlineDispatcher(), approvalDialog: new CancelDialogFake(), levelLookup: null,
            () => ChatMode.Manual, () => false, _ => true);

        var convRuntime = new ConversationRuntime(Guid.NewGuid());
        var turn = new ChatTurnItem { Header = "查600000" };
        var full = new System.Text.StringBuilder();

        var items = await engine.RunTurnAsync(convRuntime, turn, "测试",
            (handlers, resolver, ct) => runtime.RunInteractiveAsync(sessionId, "查600000", handlers, (req, ct2) => resolver(req, ct2), null, ct),
            full);

        var card = items.FirstOrDefault(i => i.IsApprovalCard);
        Assert.NotNull(card);
        Assert.Equal(2, card!.ApprovalState); // 已拒绝（取消）
        Assert.DoesNotContain(items, i => i.IsToolStep && i.IsToolCompleted); // 工具未执行
        Assert.Equal(TurnStatus.Done, turn.Status);
    }

    private sealed class ApprovalDialogFake : IApprovalDialogService
    {
        public Task<ToolApprovalDecision?> ShowAsync(ToolApprovalRequest request)
            => Task.FromResult<ToolApprovalDecision?>(new ToolApprovalDecision(request.RequestId, Approved: true, Reason: "测试批准", AlwaysApprove: false));
    }

    private sealed class CancelDialogFake : IApprovalDialogService
    {
        public Task<ToolApprovalDecision?> ShowAsync(ToolApprovalRequest request)
            => Task.FromResult<ToolApprovalDecision?>(null);
    }
}

/// <summary>ChatApprovalDecider 决策矩阵（4 档 × 等级）。</summary>
public class ChatApprovalDeciderTests
{
    private sealed class LevelLookup(ToolApprovalLevel level) : IToolApprovalLevelLookup
    {
        public ToolApprovalLevel LevelOf(string toolName) => level;
    }

    [Fact]
    public void Auto_mode_approves_everything()
    {
        var d = ChatApprovalDecider.TryDecide(ChatMode.Auto, hasModeSupport: true, "Echo", new LevelLookup(ToolApprovalLevel.Dangerous), "r1");
        Assert.NotNull(d);
        Assert.True(d!.Approved);
    }

    [Fact]
    public void AutoEdit_mode_auto_approves_confirm_but_not_dangerous()
    {
        var confirm = ChatApprovalDecider.TryDecide(ChatMode.AutoEdit, true, "Echo", new LevelLookup(ToolApprovalLevel.Confirm), "r1");
        Assert.NotNull(confirm);

        var dangerous = ChatApprovalDecider.TryDecide(ChatMode.AutoEdit, true, "Echo", new LevelLookup(ToolApprovalLevel.Dangerous), "r1");
        Assert.Null(dangerous);
    }

    [Fact]
    public void Manual_and_plan_always_prompt_and_no_mode_support_always_prompts()
    {
        Assert.Null(ChatApprovalDecider.TryDecide(ChatMode.Manual, true, "Echo", new LevelLookup(ToolApprovalLevel.Safe), "r1"));
        Assert.Null(ChatApprovalDecider.TryDecide(ChatMode.Plan, true, "Echo", new LevelLookup(ToolApprovalLevel.Safe), "r1"));
        Assert.Null(ChatApprovalDecider.TryDecide(ChatMode.Auto, false, "Echo", new LevelLookup(ToolApprovalLevel.Safe), "r1"));
    }

    [Fact]
    public void Missing_lookup_degrades_to_dangerous()
        => Assert.Null(ChatApprovalDecider.TryDecide(ChatMode.AutoEdit, true, "Echo", null, "r1"));
}

/// <summary>持久化 → 历史重建往返：ChatTranscriptPersister 落的 run+events 经 TurnRebuilder 还原等价 transcript。</summary>
public class TranscriptRoundtripTests : IDisposable
{
    private readonly string _dbPath;

    public TranscriptRoundtripTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"app-core-rt-{Guid.NewGuid():N}.db");
    }

    public void Dispose()
    {
        try { File.Delete(_dbPath); } catch { /* 临时档 */ }
    }

    [Fact]
    public async Task Persist_then_rebuild_yields_equivalent_turns()
    {
        var store = SqliteConversationStore.ForFile(_dbPath);
        var convId = Guid.NewGuid();
        await store.SaveConversationAsync(new Conversation
        {
            ConversationId = convId,
            AgentId = "rule-demo",
            AgentName = "规则演示",
            Title = "测试",
        });

        // 构造一轮 transcript：用户消息 + 推理段 + 工具卡（call+result）+ 答案段。
        var runtime = new ConversationRuntime(convId);
        var turn = new ChatTurnItem { Header = "查600000" };
        turn.Children.Add(new ChatMessageItem { Side = ChatMessageSide.Right, ItemType = "text", Sender = "我", Content = "查600000", IsStreaming = false });
        turn.Children.Add(new ChatMessageItem { Side = ChatMessageSide.Left, ItemType = "text", Sender = "规则演示", Content = "想想", IsStreaming = false, IsReasoning = true, IsReasoningContent = true });
        turn.Children.Add(new ChatMessageItem
        {
            Side = ChatMessageSide.Left, ItemType = "tool", Sender = "规则演示",
            ToolName = "Echo", CallId = "c1", IsToolCompleted = true, IsStreaming = false,
            Input = new() { ["text"] = "600000" }, Output = new() { ["result"] = "echo:600000" },
        });
        turn.Children.Add(new ChatMessageItem { Side = ChatMessageSide.Left, ItemType = "text", Sender = "规则演示", Content = "done:echo:600000", IsStreaming = false });
        turn.Finish(TurnStatus.Done);

        var persister = new ChatTranscriptPersister(store);
        var result = await persister.PersistRunAsync(runtime, "查600000", turn.Children.ToList());
        Assert.NotNull(result);
        Assert.Equal(1, result!.RunSeq);

        // 重建：loader 从事件流还原 turns。
        var loader = new ConversationHistoryLoader(store);
        var rebuilt = await loader.LoadAsync(convId, "规则演示");
        Assert.NotNull(rebuilt);
        Assert.Equal(2, rebuilt!.NextRunSeq); // 已有 seq=1 → 下一轮 2

        var rt = Assert.Single(rebuilt.Turns);
        Assert.Equal("查600000", rt.Header);
        Assert.Equal(4, rt.Children.Count);
        Assert.True(rt.Children[0].IsUserText);
        Assert.True(rt.Children[1].IsAgentThinkingCard);
        Assert.Equal("Echo", rt.Children[2].ToolName);
        Assert.True(rt.Children[2].IsToolCompleted);
        Assert.Equal("echo:600000", rt.Children[2].Output?["result"]?.ToString());
        Assert.True(rt.Children[3].IsAgentMarkdownText);
        Assert.Equal("done:echo:600000", rt.Children[3].Content);
        Assert.True(rt.IsDone);
    }
}
