using Aether.AI.App;
using Aether.AI.App.Core;
using Aether.AI.App.ViewModels;
using Aether.AI.Core.Agents;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Aether.AI.App.Tests;

/// <summary>
/// 多会话并发（2026-10-29 实证）：A 会话轮次进行中新开聊天——A **不取消**、侧栏运行标记亮、
/// 完成后侧栏 bump 终答、切回 A 直接续看（runtime 仍在内存）。
/// 跨会话并行 = 每会话独立 runtime + runner 同会话串行（不同会话互不阻塞）。
/// 走 AppToolWiringCollection 串行 + 显式声明桌面宿主（sleep 道具用 ExecuteCommand——手机宿主不注册命令工具），
/// HeadHost 静态位用完即复位（同集合的「手机宿主」断言依赖默认 null）。
/// </summary>
[Collection(AppToolWiringCollection.Name)]
public class BackgroundConversationTests : IDisposable
{
    private readonly string _dataDir;

    public BackgroundConversationTests()
    {
        _dataDir = Path.Combine(Path.GetTempPath(), $"aether-ai-app-bgconv-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dataDir);
        App.HeadHost = HostCapability.DesktopOnly;   // 桌面宿主：ExecuteCommand 可用
    }

    public void Dispose()
    {
        App.HeadHost = null;                          // 复位——别污染同集合后续用例的手机宿主断言
        try { Directory.Delete(_dataDir, true); } catch { /* 临时目录 */ }
    }

    [Fact]
    public async Task 新开会话不取消背景轮次_切回续看()
    {
        var provider = App.BuildProvider(_dataDir, services =>
        {
            services.AddSingleton<IUiDispatcher>(new InlineDispatcher());
            services.AddSingleton<IApprovalDialogService>(new AutoApproveDialog());
        });
        var store = provider.GetRequiredService<CustomAgentStore>();
        store.Save(new[]
        {
            new CustomAgent
            {
                Id = "custom-sleep", Name = "慢动作", Kind = CustomAgentKind.Rule,
                MatchRegex = "^跑$",
                Tools = new[] { "ExecuteCommand" }, ToolName = "ExecuteCommand",
                ToolArgsTemplate = "{\"command\":\"sleep\",\"arguments\":\"3\"}",
                ReplyTemplate = "done:{result}",
                RequireToolApproval = false, EnableModePicker = false,
            },
        });
        var vm = provider.GetRequiredService<ChatViewModel>();
        await vm.InitializeAsync();
        await vm.RefreshAgentTargetsAsync();

        // A 会话：发一轮（不 await——背景跑）
        vm.Pickers.SelectedAgentTarget = vm.Pickers.AgentTargetItems.First(t => t.Id == "custom-sleep");
        vm.InputText = "跑";
        var runA = vm.SendInputCommand.ExecuteAsync(null);

        await Task.Delay(800);                       // 等会话落库 + 轮次起跑（sleep 3）
        var convA = Assert.Single(vm.Conversations);
        Assert.True(convA.IsRunning);                // 侧栏运行标记亮（背景会话的 IsRunning 转发）

        vm.NewChatCommand.Execute(null);             // 新开聊天——不取消 A
        Assert.False(vm.IsAgentRunning);             // 当前（新会话）不在跑
        Assert.True(convA.IsRunning);                // A 不受影响，仍在跑

        await runA;                                  // A 完成
        Assert.False(convA.IsRunning);
        Assert.StartsWith("done:", convA.Preview);   // 侧栏 bump 到终答

        vm.ResumeConversationCommand.Execute(convA); // 切回 A：runtime 在内存——直接显
        var turn = Assert.Single(vm.Messages);
        Assert.True(turn.IsDone);
        Assert.Contains("done:", turn.Children.First(c => c.IsAgentMarkdownText).Content);
    }

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
}
