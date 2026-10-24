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
}
