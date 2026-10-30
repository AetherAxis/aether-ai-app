using Aether.AI.App;
using Aether.AI.App.Core;
using Aether.AI.App.ViewModels;
using Aether.AI.Core.Agents;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Aether.AI.App.Tests;

/// <summary>规则大脑的工具勾选（2026-10-29 开放——与 LLM/代码大脑同享「允许的工具」）：
/// 白名单=勾选集（映射）、旧数据回退、编辑加载自动补勾动作工具、动作下拉只列勾选项、保存回路。</summary>
public class RuleToolSelectionTests : IDisposable
{
    private readonly string _dataDir;

    public RuleToolSelectionTests()
    {
        _dataDir = Path.Combine(Path.GetTempPath(), $"aether-ai-app-ruletools-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dataDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dataDir, true); } catch { /* 临时目录 */ }
    }

    private AgentsViewModel BuildVm()
        => App.BuildProvider(_dataDir, services =>
            services.AddSingleton<Aether.AI.App.Core.IUiDispatcher>(new InlineDispatcher()))
            .GetRequiredService<AgentsViewModel>();

    [Fact]
    public void 映射_白名单等于勾选集_动作工具从中选()
    {
        var def = CustomAgentStore.ToDefinition(new CustomAgent
        {
            Id = "custom-multi", Name = "多工具规则", Kind = CustomAgentKind.Rule,
            MatchRegex = "^读(.+)$",
            Tools = new[] { "ReadFile", "Grep", "WriteFile" },
            ToolName = "ReadFile",
            ToolArgsTemplate = "{\"filePath\":\"{user.1}\"}",
            ReplyTemplate = "done:{result}",
        });

        Assert.Equal(EngineKind.Rule, def.Engine);
        Assert.Equal(new[] { "ReadFile", "Grep", "WriteFile" }, def.Tools);   // 白名单 = 勾选集
        Assert.Equal("ReadFile", def.Rules!.Steps[0].Action.Tool);           // 程序动作 = 从中选的那个
    }

    [Fact]
    public void 映射_旧数据没有集合时回退动作工具()
    {
        var def = CustomAgentStore.ToDefinition(new CustomAgent
        {
            Id = "custom-legacy", Name = "旧式规则", Kind = CustomAgentKind.Rule,
            MatchRegex = "^读(.+)$",
            Tools = Array.Empty<string>(),          // 勾选区对规则大脑隐藏的年代存的
            ToolName = "ReadFile",
            ReplyTemplate = "done:{result}",
        });

        Assert.Equal(new[] { "ReadFile" }, def.Tools);   // 回退 [ToolName]，行为与旧版一致
    }

    [Fact]
    public void 编辑加载_旧数据动作工具自动补进勾选集()
    {
        var provider = App.BuildProvider(_dataDir, services =>
            services.AddSingleton<Aether.AI.App.Core.IUiDispatcher>(new InlineDispatcher()));
        var store = provider.GetRequiredService<CustomAgentStore>();
        var vm = provider.GetRequiredService<AgentsViewModel>();

        store.Save(new[]
        {
            new CustomAgent
            {
                Id = "custom-legacy", Name = "旧式规则", Kind = CustomAgentKind.Rule,
                MatchRegex = "^读(.+)$", Tools = Array.Empty<string>(),
                ToolName = "ReadFile", ReplyTemplate = "done:{result}",
            },
        });

        var row = Assert.Single(vm.Agents, a => a.Id == "custom-legacy");
        vm.EditAgentCommand.Execute(row);

        // 动作工具被自动补勾（下拉只列勾选项——不补的话重存一次 ToolName 就丢了）
        Assert.Contains(vm.ToolOptions, t => t.Name == "ReadFile" && t.IsSelected);
        Assert.Contains("ReadFile", vm.RuleToolNames);
        Assert.Equal("ReadFile", vm.FormToolName);
    }

    [Fact]
    public void 动作工具下拉_只列勾选项_取消勾选回落()
    {
        var vm = BuildVm();
        vm.NewAgentCommand.Execute(null);
        Assert.Equal(new[] { AgentsViewModel.NoToolChoice }, vm.RuleToolNames);   // 全不勾 = 只有哨兵

        var readFile = vm.ToolOptions.First(t => t.Name == "ReadFile");
        var grep = vm.ToolOptions.First(t => t.Name == "Grep");
        readFile.IsSelected = true;
        grep.IsSelected = true;
        Assert.Equal(3, vm.RuleToolNames.Count);                       // 哨兵 + 两个勾选项（顺序=ToolOptions 注册序）
        Assert.Contains("ReadFile", vm.RuleToolNames);
        Assert.Contains("Grep", vm.RuleToolNames);

        // 选中的动作工具被取消勾选 → 回落到「不调用工具」
        vm.FormToolName = "ReadFile";
        readFile.IsSelected = false;
        Assert.Equal(AgentsViewModel.NoToolChoice, vm.FormToolName);
        Assert.Equal(new[] { AgentsViewModel.NoToolChoice, "Grep" }, vm.RuleToolNames);
    }

    [Fact]
    public void 保存回路_规则大脑勾选集与动作工具都落盘()
    {
        var vm = BuildVm();
        vm.NewAgentCommand.Execute(null);
        vm.FormName = "读文件员";
        vm.FormKindIndex = 1;                                   // 规则大脑
        vm.FormMatchRegex = "^读(.+)$";
        vm.ToolOptions.First(t => t.Name == "ReadFile").IsSelected = true;
        vm.ToolOptions.First(t => t.Name == "Grep").IsSelected = true;
        vm.FormToolName = "ReadFile";
        vm.FormToolArgs = "{\"filePath\":\"{user.1}\"}";
        vm.FormReplyTemplate = "done:{result}";
        vm.SaveAgentCommand.ExecuteAsync(null).Wait();

        var saved = Assert.Single(vm.Agents, a => a.Id.StartsWith("custom-"));
        var source = Assert.Single(App.BuildProvider(_dataDir, services =>
                services.AddSingleton<Aether.AI.App.Core.IUiDispatcher>(new InlineDispatcher()))
            .GetRequiredService<CustomAgentStore>().Agents, a => a.Name == "读文件员");
        Assert.Equal(2, source.Tools.Count);                        // 勾选集（顺序=ToolOptions 注册序）
        Assert.Contains("ReadFile", source.Tools);
        Assert.Contains("Grep", source.Tools);
        Assert.Equal("ReadFile", source.ToolName);                  // 动作工具
    }
}

file sealed class InlineDispatcher : Aether.AI.App.Core.IUiDispatcher
{
    public Task InvokeAsync(Action action) { action(); return Task.CompletedTask; }
    public void Post(Action action) => action();
}
