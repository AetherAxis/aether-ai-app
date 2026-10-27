using Aether.AI.App.Core;
using Aether.AI.App.ViewModels;
using Aether.AI.Core.Agents;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Aether.AI.App.Tests;

/// <summary>
/// VM 级集成测试：走**真实组合根**（App.BuildProvider——与 GUI 完全同一条 DI 链），
/// 只把两个 UI 端口换成测试实现（内联 dispatcher + 假审批框）。
/// 覆盖：目标选择→发送→分段交错→工具卡配对→持久化→侧栏；审批批准/取消路径；重启恢复（新 provider 同目录）。
/// </summary>
public class ChatViewModelIntegrationTests : IDisposable
{
    private readonly string _dataDir;

    public ChatViewModelIntegrationTests()
    {
        _dataDir = Path.Combine(Path.GetTempPath(), $"aether-ai-app-it-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dataDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dataDir, true); } catch { /* SQLite WAL 句柄晚释放——残留无害（临时目录） */ }
    }

    /// <summary>内联 dispatcher：立即执行（测试线程即 UI 线程）。</summary>
    private sealed class InlineDispatcher : IUiDispatcher
    {
        public Task InvokeAsync(Action action) { action(); return Task.CompletedTask; }
        public void Post(Action action) => action();
    }

    private sealed class AutoApproveDialog : IApprovalDialogService
    {
        public Task<ToolApprovalDecision?> ShowAsync(ToolApprovalRequest request)
            => Task.FromResult<ToolApprovalDecision?>(new ToolApprovalDecision(request.RequestId, Approved: true, Reason: "测试批准", AlwaysApprove: false));
    }

    private sealed class CancelDialog : IApprovalDialogService
    {
        public Task<ToolApprovalDecision?> ShowAsync(ToolApprovalRequest request)
            => Task.FromResult<ToolApprovalDecision?>(null);
    }

    /// <summary>demoAgents：挂上规则/审批演示 agent（本类用它们跑无 key 全链路——正常运行目录里没有）。</summary>
    private static async Task<ChatViewModel> CreateChatAsync(string dataDir, IApprovalDialogService dialog,
        bool demoAgents = true)
    {
        var provider = App.BuildProvider(dataDir, services =>
        {
            services.AddSingleton<IUiDispatcher>(new InlineDispatcher());
            services.AddSingleton<IApprovalDialogService>(dialog);
        }, includeDemoAgents: demoAgents);
        var vm = provider.GetRequiredService<ChatViewModel>();
        await vm.InitializeAsync();
        return vm;
    }

    private static AgentTargetItem Target(ChatViewModel vm, string id)
        => vm.AgentTargetItems.First(t => t.Id == id);

    private static async Task SendAsync(ChatViewModel vm, string message)
    {
        vm.InputText = message;
        await vm.SendInputCommand.ExecuteAsync(null);
    }

    /// <summary>规则演示全链路：目标选择→发送→工具卡（call+result 配对）→终答→持久化→侧栏条目。</summary>
    [Fact]
    public async Task Rule_demo_roundtrip_persists_and_renders_tool_card()
    {
        var vm = await CreateChatAsync(_dataDir, new AutoApproveDialog());
        Assert.Contains(vm.AgentTargetItems, t => t.Id == "rule-demo");

        vm.SelectedAgentTarget = Target(vm, "rule-demo");   // 用户切换 → 开新会话
        await SendAsync(vm, "查600000");

        Assert.False(vm.IsAgentRunning);                    // 轮次收敛
        var turn = Assert.Single(vm.Messages);
        Assert.Equal("查600000", turn.Header);
        Assert.True(turn.IsDone);

        // children：用户气泡 + 工具卡（Echo，call+result 合一）+ 终答段。
        Assert.True(turn.Children[0].IsUserText);
        var tool = turn.Children.FirstOrDefault(i => i.IsToolStep);
        Assert.NotNull(tool);
        Assert.Equal("Echo", tool!.ToolName);
        Assert.True(tool.IsToolCompleted);
        Assert.Equal("echo:600000", tool.Output?["result"]?.ToString());
        var answer = turn.Children.FirstOrDefault(i => i.IsAgentMarkdownText);
        Assert.NotNull(answer);
        Assert.Equal("done:echo:600000", answer!.Content);

        // 侧栏：会话已建 + bumped。
        var item = Assert.Single(vm.Conversations);
        Assert.Equal("rule-demo", item.AgentId);
        Assert.Contains("done:echo:600000", item.Preview);
    }

    /// <summary>审批演示·批准路径：审批卡转已批准 → 工具执行 → 终答「已执行：…」。</summary>
    [Fact]
    public async Task Approval_demo_approve_path_executes_tool_after_dialog()
    {
        var vm = await CreateChatAsync(_dataDir, new AutoApproveDialog());
        vm.SelectedAgentTarget = Target(vm, "rule-approve");
        await SendAsync(vm, "报单IF2406");

        var turn = Assert.Single(vm.Messages);
        var card = turn.Children.FirstOrDefault(i => i.IsApprovalCard);
        Assert.NotNull(card);
        Assert.Equal(1, card!.ApprovalState);               // 已批准
        Assert.Equal("Echo", card.ToolName);

        var tool = turn.Children.FirstOrDefault(i => i.IsToolStep);
        Assert.NotNull(tool);
        Assert.True(tool!.IsToolCompleted);                 // 批准后恢复轮执行了工具
        var answer = turn.Children.FirstOrDefault(i => i.IsAgentMarkdownText);
        Assert.Equal("已执行：echo:IF2406", answer?.Content);
    }

    /// <summary>审批演示·取消路径：resolver null → 卡转拒绝、工具不执行、turn 正常收敛。</summary>
    [Fact]
    public async Task Approval_demo_cancel_path_rejects_card_and_skips_tool()
    {
        var vm = await CreateChatAsync(_dataDir, new CancelDialog());
        vm.SelectedAgentTarget = Target(vm, "rule-approve");
        await SendAsync(vm, "报单IF2406");

        var turn = Assert.Single(vm.Messages);
        var card = turn.Children.FirstOrDefault(i => i.IsApprovalCard);
        Assert.NotNull(card);
        Assert.Equal(2, card!.ApprovalState);               // 已拒绝（取消）
        Assert.DoesNotContain(turn.Children, i => i.IsToolStep && i.IsToolCompleted);
        Assert.True(turn.IsDone);                           // 非错误收敛
    }

    /// <summary>重启恢复：同目录新 provider → 会话列表回来 → resume → transcript 从事件流重建。</summary>
    [Fact]
    public async Task Restart_same_directory_restores_conversation_and_history()
    {
        // 第一进程：跑一轮并持久化。
        var vm1 = await CreateChatAsync(_dataDir, new AutoApproveDialog());
        vm1.SelectedAgentTarget = Target(vm1, "rule-demo");
        await SendAsync(vm1, "查600000");
        Assert.Single(vm1.Conversations);

        // 第二进程（同目录）：新组合根 + 新 VM。
        var vm2 = await CreateChatAsync(_dataDir, new AutoApproveDialog());
        var item = Assert.Single(vm2.Conversations);        // SQLite 里的会话回来了
        Assert.Equal("rule-demo", item.AgentId);

        await vm2.ResumeConversationCommand.ExecuteAsync(item);
        var turn = Assert.Single(vm2.Messages);             // transcript 重建
        Assert.Equal("查600000", turn.Header);
        var tool = turn.Children.FirstOrDefault(i => i.IsToolStep);
        Assert.NotNull(tool);
        Assert.True(tool!.IsToolCompleted);                 // call+result 按 CallId 重新配对
        Assert.Equal("done:echo:600000", turn.Children.First(i => i.IsAgentMarkdownText).Content);

        // 续聊不丢 seq：再发一轮 → 两轮并存。
        await SendAsync(vm2, "查000001");
        Assert.Equal(2, vm2.Messages.Count);
    }

    /// <summary>切目标 = 开新会话；切回原会话恢复显示（多会话并发的切换语义）。</summary>
    [Fact]
    public async Task Switching_target_starts_new_chat_and_resume_restores()
    {
        var vm = await CreateChatAsync(_dataDir, new AutoApproveDialog());
        vm.SelectedAgentTarget = Target(vm, "rule-demo");
        await SendAsync(vm, "查600000");
        var firstConversation = Assert.Single(vm.Conversations);

        vm.SelectedAgentTarget = Target(vm, "rule-approve");   // 切目标 → 新会话
        Assert.Empty(vm.Messages);

        await vm.ResumeConversationCommand.ExecuteAsync(firstConversation);   // 切回
        Assert.Single(vm.Messages);
        Assert.Equal("查600000", vm.Messages[0].Header);
    }
}
