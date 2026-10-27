using Aether.AI.App.Core;
using Aether.AI.App.ViewModels;
using Aether.AI.Brains.Code.Compilation;
using Aether.AI.Brains.Code.Compilation.CSharp;
using Aether.AI.Core.Agents;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Aether.AI.App.Tests;

/// <summary>
/// 自定义 agent（Agents 页 = 自定义大脑）：存储往返 + 两类大脑的定义映射 +
/// 保存后**不重启**即进 agent 目录并能真跑一轮（规则大脑走 Echo 工具）。
/// </summary>
public class CustomAgentTests : IDisposable
{
    private readonly string _dataDir;

    public CustomAgentTests()
    {
        _dataDir = Path.Combine(Path.GetTempPath(), $"aether-ai-app-agents-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dataDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dataDir, true); } catch { /* 临时目录 */ }
    }

    private sealed class InlineDispatcher : IUiDispatcher
    {
        public Task InvokeAsync(Action action) { action(); return Task.CompletedTask; }
        public void Post(Action action) => action();
    }

    private sealed class AutoApproveDialog : IApprovalDialogService
    {
        public Task<ToolApprovalDecision?> ShowAsync(ToolApprovalRequest request)
            => Task.FromResult<ToolApprovalDecision?>(
                new ToolApprovalDecision(request.RequestId, Approved: true, Reason: "测试批准", AlwaysApprove: false));
    }

    /// <summary>demoAgents：挂上规则/审批演示（目录默认只有 agents.json 里的 agent）。
    /// codeCompiler：注册 Roslyn 编译器（代码大脑的端到端测试要真编译）。</summary>
    private IServiceProvider BuildProvider(bool demoAgents = false, bool codeCompiler = false)
        => App.BuildProvider(_dataDir, services =>
        {
            services.AddSingleton<IUiDispatcher>(new InlineDispatcher());
            services.AddSingleton<IApprovalDialogService>(new AutoApproveDialog());
            if (codeCompiler) services.AddCSharpBrainCompilation();
        }, includeDemoAgents: demoAgents);

    private static CustomAgent RuleAgent(string id = "custom-echo") => new()
    {
        Id = id,
        Name = "回显员",
        Description = "把参数回显出来",
        Kind = CustomAgentKind.Rule,
        MatchRegex = "^查(.+)$",
        ToolName = "Echo",
        ToolArgsTemplate = "{\"text\":\"{user.1}\"}",
        ReplyTemplate = "done:{result}",
        RequireToolApproval = false,
    };

    /// <summary>代码大脑：一段继承 <c>CodeRuleBrain</c> 的源码，跑起来调 Echo。</summary>
    private static CustomAgent CodeAgent(string id = "custom-code") => new()
    {
        Id = id,
        Name = "代码员",
        Description = "一段 C# 源码",
        Kind = CustomAgentKind.Code,
        Tools = new[] { "Echo" },
        RequireToolApproval = false,
        SourceCode = """
            using System.Threading.Tasks;
            using Aether.AI.Brains.Code;

            public sealed class EchoBrain : CodeRuleBrain
            {
                protected override async Task<string> RunAsync()
                    => "code:" + await Call("Echo", new { text = UserText });
            }
            """,
    };

    // —————————————— 存储 ——————————————

    [Fact]
    public void Store_roundtrips_agents_json()
    {
        var path = Path.Combine(_dataDir, "agents.json");
        var store = new CustomAgentStore(path);
        Assert.Empty(store.Agents);

        var changed = 0;
        store.Changed += () => changed++;
        store.Save(new[] { RuleAgent(), new CustomAgent { Id = "custom-llm", Name = "评审员", Kind = CustomAgentKind.Llm } });
        Assert.Equal(1, changed);

        var reloaded = new CustomAgentStore(path);   // 模拟重启
        Assert.Equal(2, reloaded.Agents.Count);
        Assert.Equal(CustomAgentKind.Rule, reloaded.Agents[0].Kind);
        Assert.Equal("Echo", reloaded.Agents[0].ToolName);
    }

    [Fact]
    public void NewId_slugs_and_dedupes()
    {
        Assert.Equal("custom-log-agent", CustomAgentStore.NewId("Log Agent", Array.Empty<string>()));
        Assert.Equal("custom-中文名", CustomAgentStore.NewId("中文名", Array.Empty<string>()));
        Assert.Equal("custom-a-2", CustomAgentStore.NewId("a", new[] { "custom-a" }));
    }

    // —————————————— 定义映射 ——————————————

    [Fact]
    public void Llm_agent_maps_to_llm_definition()
    {
        var def = CustomAgentStore.ToDefinition(new CustomAgent
        {
            Id = "custom-reviewer",
            Name = "评审员",
            Description = "审代码",
            Kind = CustomAgentKind.Llm,
            Instructions = "你是严格的代码评审员。",
            ModelProfile = "DeepSeek",
            Tools = new[] { "Echo" },
            RequireToolApproval = true,
            EnableModePicker = true,
        });

        Assert.Equal(EngineKind.Llm, def.Engine);
        Assert.Equal("custom-reviewer", def.Id);
        Assert.Equal("审代码", def.Description);
        Assert.Equal("DeepSeek", def.ModelId);           // 档案名 → 客户端工厂按名解析
        Assert.Equal(new[] { "Echo" }, def.Tools);
        Assert.True(def.RequireToolApproval);
        Assert.True(def.EnableAgentModeProvider);
        Assert.True(def.EnableTodoProvider);
    }

    [Fact]
    public void Rule_agent_with_tool_maps_to_two_step_program()
    {
        var def = CustomAgentStore.ToDefinition(RuleAgent());

        Assert.Equal(EngineKind.Rule, def.Engine);
        Assert.NotNull(def.Rules);
        var steps = def.Rules!.Steps;
        Assert.Equal(2, steps.Count);
        Assert.Equal("^查(.+)$", steps[0].Match.OnUserRegex);
        Assert.Equal(0, steps[0].Match.MaxToolCalls);          // 只在本轮第一次工具调用前命中
        Assert.Equal("Echo", steps[0].Action.Tool);
        Assert.Equal("{user.1}", steps[0].Action.Args!["text"]);
        Assert.Equal(1, steps[1].Match.MinToolCalls);
        Assert.Equal("done:{result}", steps[1].Action.ReplyText);
        Assert.Equal(new[] { "Echo" }, def.Tools);
    }

    [Fact]
    public void Rule_agent_without_tool_maps_to_single_reply_step()
    {
        var def = CustomAgentStore.ToDefinition(new CustomAgent
        {
            Id = "custom-hi", Name = "打招呼", Kind = CustomAgentKind.Rule,
            MatchRegex = "^你好$", ReplyTemplate = "你好，我是规则 agent。",
            NoMatchReply = "只认「你好」",
        });

        var step = Assert.Single(def.Rules!.Steps);
        Assert.Equal("你好，我是规则 agent。", step.Action.ReplyText);
        Assert.Null(step.Action.Tool);
        Assert.Empty(def.Tools);
        Assert.Equal("只认「你好」", def.Rules.NoMatchReply);
    }

    // —————————————— 代码大脑（第三类大脑） ——————————————

    [Fact]
    public void Code_agent_maps_to_code_definition()
    {
        var def = CustomAgentStore.ToDefinition(CodeAgent());

        Assert.Equal(EngineKind.Code, def.Engine);
        Assert.Equal(CodeBrainLanguage.CSharp, def.CodeLanguage);
        Assert.Equal(new[] { "Echo" }, def.Tools);
        Assert.False(def.RequireToolApproval);
    }

    /// <summary>端到端：保存 → 编译（Roslyn）→ 进目录 → 聊天里真跑一轮（源码内部调 Echo 工具）。</summary>
    [Fact]
    public async Task Code_agent_compiles_and_runs_a_turn()
    {
        var provider = BuildProvider(codeCompiler: true);
        var store = provider.GetRequiredService<CustomAgentStore>();
        var compilation = provider.GetRequiredService<ICodeCompilationService>();
        var vm = provider.GetRequiredService<ChatViewModel>();
        await vm.InitializeAsync();

        store.Save(new[] { CodeAgent() });
        await Task.Run(() => compilation.LoadAsync());          // 组合根已挂 Reload，这里显式等一轮编译
        Assert.False(compilation.LoadErrors.ContainsKey("custom-code"),
            compilation.LoadErrors.GetValueOrDefault("custom-code"));

        await vm.RefreshAgentTargetsAsync();
        var target = vm.AgentTargetItems.First(t => t.Id == "custom-code");
        Assert.Equal("⌨", target.DisplayIcon);                   // 代码大脑图标

        vm.SelectedAgentTarget = target;
        vm.InputText = "你好";
        await vm.SendInputCommand.ExecuteAsync(null);

        var turn = Assert.Single(vm.Messages);
        var tool = turn.Children.FirstOrDefault(i => i.IsToolStep);
        Assert.Equal("Echo", tool?.ToolName);
        var answer = turn.Children.FirstOrDefault(i => i.IsAgentMarkdownText);
        Assert.Equal("code:echo:你好", answer?.Content);
    }

    /// <summary>坏源码：不注册、错误进 LoadErrors，并在管理窗那一行上显示（不是静默失败）。</summary>
    [Fact]
    public async Task Broken_code_agent_reports_compile_error_on_its_row()
    {
        var provider = BuildProvider(codeCompiler: true);
        var store = provider.GetRequiredService<CustomAgentStore>();
        var compilation = provider.GetRequiredService<ICodeCompilationService>();
        var agents = provider.GetRequiredService<AgentsViewModel>();

        store.Save(new[] { CodeAgent() with { SourceCode = "这不是 C#" } });
        await Task.Run(() => compilation.LoadAsync());

        Assert.True(compilation.LoadErrors.TryGetValue("custom-code", out var error));
        Assert.NotEmpty(error!);

        // 订阅 Loaded → Reload（内联 dispatcher 在编译线程上同步跑完）
        var row = Assert.Single(agents.Agents, a => a.Id == "custom-code");
        Assert.True(row.HasCompileError);
        Assert.Contains("error", row.CompileError, StringComparison.OrdinalIgnoreCase);
    }

    // —————————————— 出厂播种（没有「内置只读 agent」这一档） ——————————————

    /// <summary>首次运行把「通用助手」落成 agents.json 里的一条普通记录：目录/下拉里都有它，
    /// 而且它和自建 agent 完全同权（改名 / 改提示词 / 删除都行）。</summary>
    [Fact]
    public async Task First_run_seeds_assistant_as_a_normal_editable_agent()
    {
        var provider = BuildProvider();
        var store = provider.GetRequiredService<CustomAgentStore>();
        var catalog = provider.GetRequiredService<IAgentCatalog>();
        var vm = provider.GetRequiredService<ChatViewModel>();
        await vm.InitializeAsync();

        var seeded = Assert.Single(store.Agents);
        Assert.Equal("assistant", seeded.Id);
        Assert.Equal("通用助手", seeded.Name);
        Assert.Equal(CustomAgentKind.Llm, seeded.Kind);

        var target = vm.AgentTargetItems.First(t => t.Id == "assistant");
        Assert.Equal("✨", target.DisplayIcon);

        // 改：和自建 agent 走同一条 Save 通道（这里直接改提示词 + 改名）
        store.Save(new[] { seeded with { Name = "我的助手", Instructions = "只回答一个字。" } });
        await vm.RefreshAgentTargetsAsync();
        Assert.Equal("我的助手", vm.AgentTargetItems.First(t => t.Id == "assistant").Name);

        var def = Assert.Single(await catalog.GetAllAsync(), a => a.Id == "assistant");
        Assert.Equal("只回答一个字。", def.Instructions);
        Assert.Equal(string.Empty, def.ModelId);         // 仍跟随当前选中档案

        // 删：目录里彻底没了，且不会在下一次启动时复活（见 LlmConfigStateTests）
        store.Save(Array.Empty<CustomAgent>());
        Assert.Empty(await catalog.GetAllAsync());
        await vm.RefreshAgentTargetsAsync();
        Assert.Null(vm.SelectedAgentTarget);              // 没 agent 了 → 输入卡提示去「管理 agent…」
        Assert.Single(vm.AgentTargetItems, t => t.IsManage);
    }

    [Theory]
    [InlineData("^查(.+)$", true)]
    [InlineData("([unclosed", false)]
    [InlineData("", true)]
    public void Regex_validation(string pattern, bool ok)
        => Assert.Equal(ok, CustomAgentStore.IsValidRegex(pattern));

    [Fact]
    public void Args_template_parses_json_and_rejects_garbage()
    {
        var ok = CustomAgentStore.ParseArgsTemplate("{\"text\":\"{user.1}\",\"n\":3,\"flag\":true}");
        Assert.Equal("{user.1}", ok!["text"]);
        Assert.Equal(3L, ok["n"]);
        Assert.Equal(true, ok["flag"]);

        Assert.Null(CustomAgentStore.ParseArgsTemplate("not json"));
        Assert.Null(CustomAgentStore.ParseArgsTemplate("[1,2]"));   // 必须是对象
        Assert.Null(CustomAgentStore.ParseArgsTemplate("  "));
    }

    // —————————————— 目录 + 端到端 ——————————————

    /// <summary>保存即进目录（DynamicAgents 每次枚举求值）+ 聊天下拉热刷新 + 规则大脑真跑一轮。</summary>
    [Fact]
    public async Task Saved_agent_appears_in_catalog_and_runs()
    {
        var provider = BuildProvider();
        var store = provider.GetRequiredService<CustomAgentStore>();
        var catalog = provider.GetRequiredService<IAgentCatalog>();
        var vm = provider.GetRequiredService<ChatViewModel>();
        await vm.InitializeAsync();

        Assert.DoesNotContain(await catalog.GetAllAsync(), a => a.Id == "custom-echo");

        store.Save(new[] { RuleAgent() });

        var def = Assert.Single(await catalog.GetAllAsync(), a => a.Id == "custom-echo");
        Assert.Equal("回显员", def.Name);
        Assert.Equal("把参数回显出来", def.Description);

        await vm.RefreshAgentTargetsAsync();             // 渲染层热刷新（Changed 也已触发，这里显式等一次）
        var target = vm.AgentTargetItems.First(t => t.Id == "custom-echo");
        Assert.Equal("🧭", target.DisplayIcon);           // 规则大脑图标

        vm.SelectedAgentTarget = target;
        vm.InputText = "查600000";
        await vm.SendInputCommand.ExecuteAsync(null);

        var turn = Assert.Single(vm.Messages);
        var tool = turn.Children.FirstOrDefault(i => i.IsToolStep);
        Assert.Equal("Echo", tool?.ToolName);
        Assert.Equal("echo:600000", tool?.Output?["result"]?.ToString());
        var answer = turn.Children.FirstOrDefault(i => i.IsAgentMarkdownText);
        Assert.Equal("done:echo:600000", answer?.Content);
    }

    /// <summary>下拉末项「管理 agent…」= 管理入口：选中它只发意图、弹回原 agent，**不**触发「切目标开新会话」。</summary>
    [Fact]
    public async Task Manage_sentinel_opens_manager_without_resetting_conversation()
    {
        var provider = BuildProvider(demoAgents: true);
        var vm = provider.GetRequiredService<ChatViewModel>();
        await vm.InitializeAsync();

        vm.SelectedAgentTarget = vm.AgentTargetItems.First(t => t.Id == "rule-demo");
        vm.InputText = "查600000";
        await vm.SendInputCommand.ExecuteAsync(null);

        var manage = Assert.Single(vm.AgentTargetItems, t => t.IsManage);
        Assert.Same(vm.AgentTargetItems[^1], manage);            // 永远是末项
        var before = vm.SelectedAgentTarget;
        var conversation = Assert.Single(vm.Conversations);

        var requests = 0;
        vm.ManageAgentsRequested += () => requests++;

        vm.SelectedAgentTarget = manage;

        Assert.Equal(1, requests);                              // 宿主据此开管理窗
        Assert.Same(before, vm.SelectedAgentTarget);            // 弹回原 agent，不停在「管理 agent…」
        Assert.Same(conversation, Assert.Single(vm.Conversations));
        Assert.True(conversation.IsCurrent);                    // 没开新会话
    }

    /// <summary>删除后从目录与下拉里消失（不需要重启）。</summary>
    [Fact]
    public async Task Deleted_agent_disappears_from_catalog()
    {
        var provider = BuildProvider();
        var store = provider.GetRequiredService<CustomAgentStore>();
        var catalog = provider.GetRequiredService<IAgentCatalog>();
        var vm = provider.GetRequiredService<ChatViewModel>();
        await vm.InitializeAsync();

        store.Save(new[] { RuleAgent() });
        await vm.RefreshAgentTargetsAsync();
        Assert.Contains(vm.AgentTargetItems, t => t.Id == "custom-echo");

        store.Save(Array.Empty<CustomAgent>());

        Assert.DoesNotContain(await catalog.GetAllAsync(), a => a.Id == "custom-echo");
        await vm.RefreshAgentTargetsAsync();
        Assert.DoesNotContain(vm.AgentTargetItems, t => t.Id == "custom-echo");
    }
}
