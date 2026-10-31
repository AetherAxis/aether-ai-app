using System.Text;
using Aether.AI.App.Core;
using Aether.AI.App.ViewModels;
using Aether.AI.Core.Agents;
using Aether.AI.Core.Conversations;
using Aether.AI.Core.Conversations.Runs;
using Aether.AI.Memory;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Aether.AI.App.Tests;

/// <summary>
/// 崩溃防线回归（2026-10-31「AI 对话运行中整个 app 崩溃」根治层）。
/// 实锤根因：CommunityToolkit 8.4.2 的 AsyncRelayCommand 在按钮的**同步 Execute** 路径上会把
/// delegate 异常经 AwaitAndThrowIfFailed 重抛到线程池（本地控制台最小复现实测）→ 未处理异常 →
/// 整个进程死。引擎的 catch 只罩 runAsync 本体；落库/收尾/模式刷新/晚到回调此前全都裸奔。
/// 本组断言：runner 崩、落库崩、晚到工具回调踩已收尾轮次——一律收敛为可见的轮次错误，
/// 命令方法本身零外逃。（同步 Execute 的重抛路径无法在测试进程内断言——那会真杀进程；
/// 这里断言的是它的前置条件：**没有任何异常能逃出命令方法**。）
/// </summary>
public class CrashContainmentTests : IDisposable
{
    private readonly string _dataDir;

    public CrashContainmentTests()
    {
        _dataDir = Path.Combine(Path.GetTempPath(), $"aether-ai-app-crash-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dataDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dataDir, true); } catch { /* SQLite WAL 句柄晚释放——临时目录残留无害 */ }
    }

    /// <summary>内联 dispatcher：立即执行（测试线程即 UI 线程）。</summary>
    private sealed class InlineDispatcher : IUiDispatcher
    {
        public Task InvokeAsync(Action action) { action(); return Task.CompletedTask; }
        public void Post(Action action) => action();
    }

    /// <summary>Post 延迟入队、InvokeAsync 立即执行的 dispatcher——制造「晚到回调」：
    /// 工具事件先入队、轮次先收尾（空末段被 FinalizeSegment 移除）、再 Drain——
    /// 复现旧代码 HandleToolEventSegmented 走到 RemoveAt(-1) 的崩溃面。</summary>
    private sealed class DeferredPostDispatcher : IUiDispatcher
    {
        private readonly Queue<Action> _posts = new();
        public Task InvokeAsync(Action action) { action(); return Task.CompletedTask; }
        public void Post(Action action) => _posts.Enqueue(action);
        public void Drain()
        {
            while (_posts.Count > 0) _posts.Dequeue()();
        }
    }

    /// <summary>一碰就炸的 runner（模拟 LLM/工具链路深处抛异常——引擎 catch 层应吞成 [运行失败]）。</summary>
    private sealed class ThrowingRunner : IAgentRunner
    {
        public Task<string> RunInteractiveAsync(Guid conversationId, string agentId, string message,
            StreamHandlers? handlers = null, ApprovalResponder? approvalResponder = null,
            string? desiredMode = null, CancellationToken cancellationToken = default)
        {
            handlers?.OnToken?.Invoke("炸", cancellationToken);
            throw new InvalidOperationException("工具链炸了");
        }

        public Task<string?> GetModeAsync(Guid conversationId, CancellationToken cancellationToken = default)
            => Task.FromResult<string?>(null);
        public Task<bool> SetModeAsync(Guid conversationId, string mode, CancellationToken cancellationToken = default)
            => Task.FromResult(false);
        public void Invalidate(string agentId) { }
        public void EvictConversation(Guid conversationId) { }
    }

    /// <summary>SaveConversationAsync 永远失败的装饰器（模拟 SQLite 锁/磁盘故障——VM 最后防线层应收敛）。</summary>
    private sealed class ThrowingSaveStore : IConversationStore
    {
        private readonly IConversationStore _inner;
        public ThrowingSaveStore(IConversationStore inner) => _inner = inner;

        public Task SaveConversationAsync(Conversation conversation, CancellationToken cancellationToken = default)
            => throw new IOException("db locked（模拟）");

        public Task<Conversation?> GetConversationAsync(Guid conversationId, CancellationToken cancellationToken = default)
            => _inner.GetConversationAsync(conversationId, cancellationToken);
        public Task<IReadOnlyList<ConversationSummary>> GetSummariesAsync(string userId, CancellationToken cancellationToken = default)
            => _inner.GetSummariesAsync(userId, cancellationToken);
        public Task<IReadOnlyList<Conversation>> FindByUserAsync(string userId, CancellationToken cancellationToken = default)
            => _inner.FindByUserAsync(userId, cancellationToken);
        public Task<bool> DeleteAsync(Guid conversationId, CancellationToken cancellationToken = default)
            => _inner.DeleteAsync(conversationId, cancellationToken);
        public Task AppendRunAsync(AgentRun run, IReadOnlyList<AgentEvent> events, CancellationToken cancellationToken = default)
            => _inner.AppendRunAsync(run, events, cancellationToken);
        public Task<IReadOnlyList<AgentRun>> GetRunsAsync(Guid conversationId, CancellationToken cancellationToken = default)
            => _inner.GetRunsAsync(conversationId, cancellationToken);
        public Task<IReadOnlyList<AgentEvent>> GetEventsAsync(Guid runId, CancellationToken cancellationToken = default)
            => _inner.GetEventsAsync(runId, cancellationToken);
        public Task<IReadOnlyList<RunSummary>> GetRunsInRangeAsync(string userId, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken = default)
            => _inner.GetRunsInRangeAsync(userId, from, to, cancellationToken);
    }

    private static async Task<ChatViewModel> CreateChatAsync(string dataDir,
        Action<IServiceCollection>? extra = null, bool demoAgents = true)
    {
        var provider = App.BuildProvider(dataDir, services =>
        {
            services.AddSingleton<IUiDispatcher>(new InlineDispatcher());
            extra?.Invoke(services);
        }, includeDemoAgents: demoAgents);
        var vm = provider.GetRequiredService<ChatViewModel>();
        await vm.InitializeAsync();
        return vm;
    }

    private static AgentTargetItem Target(ChatViewModel vm, string id)
        => vm.Pickers.AgentTargetItems.First(t => t.Id == id);

    /// <summary>runner 在 run 中途抛异常：引擎层吞掉 → 轮次以 [运行失败] 收敛（Error 态），命令零外逃。</summary>
    [Fact]
    public async Task Runner_throwing_mid_turn_converges_to_error_turn()
    {
        var vm = await CreateChatAsync(_dataDir, services =>
            services.AddSingleton<IAgentRunner>(new ThrowingRunner()));
        vm.Pickers.SelectedAgentTarget = Target(vm, "rule-demo");   // 只当目标用（runner 已被换成炸的）
        vm.InputText = "随便跑点什么";

        var ex = await Record.ExceptionAsync(() => vm.SendInputCommand.ExecuteAsync(null));

        Assert.Null(ex);                                     // 命令方法零外逃 = 同步 Execute 无从重抛
        Assert.False(vm.IsAgentRunning);                     // 运行态已清
        var turn = Assert.Single(vm.Messages);
        Assert.True(turn.IsError);                           // 引擎 catch 收敛为 Error 轮
        Assert.Contains(turn.Children, c => c.Content?.Contains("运行失败") == true
                                            && c.Content.Contains("工具链炸了"));
    }

    /// <summary>落库炸（新会话 SaveConversationAsync 抛 IOException）：VM 最后防线收敛 →
    /// 轮次转 Error + 可见 [轮次异常] 段 + 状态栏提示，命令零外逃。</summary>
    [Fact]
    public async Task Store_failure_on_new_conversation_converges_to_error_turn()
    {
        // 装饰器需要一个真实 store 兜底非 Save 成员（独立子目录，避免与外层 SQLite 同文件）。
        var realStore = App.BuildProvider(Path.Combine(_dataDir, "inner-store"))
            .GetRequiredService<IConversationStore>();

        var vm = await CreateChatAsync(_dataDir, services =>
            services.AddSingleton<IConversationStore>(new ThrowingSaveStore(realStore)));
        vm.Pickers.SelectedAgentTarget = Target(vm, "rule-demo");
        vm.InputText = "读一个不存在的文件";

        var ex = await Record.ExceptionAsync(() => vm.SendInputCommand.ExecuteAsync(null));

        Assert.Null(ex);
        Assert.False(vm.IsAgentRunning);
        var turn = Assert.Single(vm.Messages);
        Assert.True(turn.IsError);
        Assert.Contains(turn.Children, c => c.Content?.StartsWith("[轮次异常：") == true);
        Assert.StartsWith("本轮失败", vm.StatusText);
    }

    /// <summary>晚到的工具回调踩已收尾轮次（cursor.Current 已被 FinalizeSegment 移除）：
    /// 旧代码 RemoveAt(-1) → UI 线程 ArgumentOutOfRangeException → 进程死；
    /// 现在索引防御 + PostSafe 吞异常双保险——Drain 不抛，工具卡降级追加到末尾。</summary>
    [Fact]
    public async Task Late_tool_event_after_finalize_does_not_crash()
    {
        var dispatcher = new DeferredPostDispatcher();
        var engine = new ChatSessionEngine(dispatcher, approvalDialog: null, levelLookup: null,
            selectedMode: () => ChatMode.Auto, hasModeSupport: () => false, isCurrentRuntime: _ => true);
        var runtime = new ConversationRuntime(Guid.NewGuid());
        var turn = new ChatTurnItem { Header = "q" };

        var items = await engine.RunTurnAsync(runtime, turn, "agent",
            (handlers, approvalResponder, ct) =>
            {
                // 工具 Call 事件只入队（DeferredPost 不立即执行），run 随即正常返回——
                // 制造「回调比轮次收尾晚到」的真实时序（MAF 深处 ConfigureAwait 丢上下文时会出现）。
                handlers.OnToolEvent?.Invoke(new ToolStreamEvent(
                    ToolStreamEventKind.Call, "ReadFile", "c1",
                    new Dictionary<string, object> { ["path"] = "/tmp/x" }, null), ct);
                return Task.FromResult("done");
            }, new StringBuilder());

        Assert.True(turn.IsDone);                            // 轮次已收尾：空末段被移除

        var ex = Record.Exception(() => dispatcher.Drain()); // 晚到的工具卡插入此刻才执行

        Assert.Null(ex);                                     // 旧代码在此抛 ArgumentOutOfRangeException
        Assert.True(turn.IsDone);                            // 收尾态不被晚到回调破坏
        // 卡降级追加到末尾（不丢）——注意断言 turn.Children：RunTurnAsync 返回的是收尾时的快照副本，
        // 晚到的插入发生在快照之后，落在真实集合上。
        Assert.Contains(turn.Children, i => i.IsToolStep && i.ToolName == "ReadFile");
    }

    /// <summary>黑匣子：Install 后 Log 落 &lt;dataDir&gt;/logs/app-log.txt，内容含异常文本。</summary>
    [Fact]
    public void CrashGuard_logs_to_black_box_file()
    {
        CrashGuard.Install(_dataDir);
        CrashGuard.Log("test-source", new InvalidOperationException("black-box-marker"));

        var logPath = Path.Combine(_dataDir, "logs", "app-log.txt");
        Assert.True(File.Exists(logPath));
        var text = File.ReadAllText(logPath);
        Assert.Contains("test-source", text);
        Assert.Contains("black-box-marker", text);
    }
}
