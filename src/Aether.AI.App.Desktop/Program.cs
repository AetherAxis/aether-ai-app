using System;
using System.IO;
using System.Linq;
using Aether.AI.App;
using Aether.AI.App.Core;
using Aether.AI.Core.Agents;
using Avalonia;
using Microsoft.Extensions.DependencyInjection;

namespace Aether.AI.App.Desktop;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // 无头冒烟：与 GUI 完全同一条 DI 组合链（App.BuildProvider），跑规则大脑全链路 + 审批自恢复轮。
        // exit 0 = 通过——CI / 命令行验收用（L2 的无 GUI 等价物）。
        if (args.Contains("--smoke"))
        {
            SmokeAsync().GetAwaiter().GetResult();
            return;
        }

        // LLM 真模型冒烟：真实组合 + assistant 一轮流式对话，断言 token 流与终答非空。
        // 需先配置 llm-configs.json（设置页或手工）；无档案时 SKIP（exit 0——不阻塞无 key 环境）。
        if (args.Contains("--llm-smoke"))
        {
            LlmSmokeAsync().GetAwaiter().GetResult();
            return;
        }

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
#if DEBUG
            .WithDeveloperTools()
#endif
            .WithInterFont()
            .LogToTrace();

    /// <summary>
    /// 两断言：① rule-demo 全链路（BrainMatrix 同构）；② rule-approve 审批循环（resolver 自动批 →
    /// ⑫ RunInteractiveAsync 恢复轮 → 工具执行 → 终答）——覆盖新引擎的审批缝。
    /// </summary>
    private static async Task SmokeAsync()
    {
        var dir = Path.Combine(Path.GetTempPath(), "aether-ai-app-smoke");
        try { Directory.Delete(dir, true); } catch { /* 无旧档 */ }

        var ok = true;
        try
        {
            var provider = App.BuildProvider(dir);
            var runner = provider.GetRequiredService<IAgentRunner>();

            var full1 = await runner.RunInteractiveAsync(Guid.NewGuid(), "rule-demo", "查600000");
            var pass1 = full1 == "done:echo:600000";
            Console.WriteLine(pass1
                ? "[smoke] 1/2 PASS ✅ rule-demo 全链路：done:echo:600000"
                : $"[smoke] 1/2 FAIL ❌ 期望 done:echo:600000，实得 {full1}");
            ok &= pass1;

            var full2 = await runner.RunInteractiveAsync(Guid.NewGuid(), "rule-approve", "报单IF2406",
                approvalResponder: (req, ct) => Task.FromResult<ToolApprovalDecision?>(
                    new ToolApprovalDecision(req.RequestId, Approved: true, Reason: "冒烟自动批", AlwaysApprove: false)));
            var pass2 = full2 == "已执行：echo:IF2406";
            Console.WriteLine(pass2
                ? "[smoke] 2/2 PASS ✅ rule-approve 审批自恢复：已执行：echo:IF2406"
                : $"[smoke] 2/2 FAIL ❌ 期望 已执行：echo:IF2406，实得 {full2}");
            ok &= pass2;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[smoke] EXCEPTION ❌ {ex}");
            ok = false;
        }
        Environment.ExitCode = ok ? 0 : 1;
    }

    /// <summary>
    /// LLM 真模型链路冒烟：走真实数据目录（~/.aether/aether-ai-app）与完整 DI 组合，
    /// 「通用助手」一轮流式对话——断言流式 token 段数 &gt; 0 且终答非空（reasoning 段计数随模型而定，
    /// deepseek-reasoner 应 &gt; 0）。无档案时 SKIP。
    /// </summary>
    private static async Task LlmSmokeAsync()
    {
        var provider = App.BuildProvider();
        var state = provider.GetRequiredService<Aether.AI.App.Core.LlmConfigState>();
        if (!state.HasConfigs)
        {
            Console.WriteLine("[llm-smoke] SKIP：无 LLM 档案（先在设置页配置）");
            Environment.ExitCode = 0;
            return;
        }

        var runner = provider.GetRequiredService<Aether.AI.App.Core.IAgentRunner>();
        var tokens = 0;
        var reasoning = 0;
        var usage = "";
        try
        {
            var full = await runner.RunInteractiveAsync(Guid.NewGuid(), "assistant",
                "用一句话自我介绍，然后说出 2+2 等于几。",
                handlers: new Aether.AI.Core.Agents.StreamHandlers(
                    OnToken: (t, _) => { tokens++; return Task.CompletedTask; },
                    OnReasoning: (r, _) => { reasoning++; return Task.CompletedTask; },
                    OnUsage: u => usage = u));

            var ok = !string.IsNullOrWhiteSpace(full) && tokens > 0;
            Console.WriteLine($"[llm-smoke] 档案={state.SelectedName} 模型={state.Selected?.ModelId}");
            Console.WriteLine($"[llm-smoke] 流式 token 段={tokens}，reasoning 段={reasoning}，usage={usage}");
            Console.WriteLine(ok
                ? $"[llm-smoke] PASS ✅ 终答：{full}"
                : $"[llm-smoke] FAIL ❌ tokens={tokens}，full=\"{full}\"");
            Environment.ExitCode = ok ? 0 : 1;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[llm-smoke] EXCEPTION ❌ {ex}");
            Environment.ExitCode = 1;
        }
    }
}
