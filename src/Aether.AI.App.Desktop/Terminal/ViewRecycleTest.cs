using Aether.AI.App.Terminal;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Microsoft.Extensions.DependencyInjection;

namespace Aether.AI.App.Desktop.Terminal;

/// <summary>
/// --view-recycle-test（2026-10-29）：「视图可随意销毁」的 GUI 端到端实证（终端 = 最后一个视图持状态的组件，
/// 本批重构为服务持有——BrowserService 同款）。四段断言：
/// ① 无任何视图时 CreateSession + RunAsync 照常（agent 的 run_in_terminal 不再要求用户开过终端 tab）；
/// ② 挂真视图（辅助窗口）→ 会话续用；③ **销毁视图（关窗）→ PTY 仍活**（RunAsync 照常完成）；
/// ④ 重建视图 → 原会话原样续看。exit 0 = 视图生命周期与 PTY 生命周期彻底解耦。
/// 经 App.HeadGuiHook 注入（harness 需要 TerminalView 具体类型，共享层够不着 head 命名空间）。
/// </summary>
internal static class ViewRecycleTest
{
    public static void Run(IServiceProvider provider, IClassicDesktopStyleApplicationLifetime desktop)
        => RunAsync(provider, desktop);

    private static async void RunAsync(IServiceProvider provider, IClassicDesktopStyleApplicationLifetime desktop)
    {
        var ok = false;
        var detail = "";
        TerminalService? svc = null;
        try
        {
            svc = provider.GetRequiredService<TerminalService>();

            // ① 无视图：建会话 + 跑命令（服务层直接持控件/PTY）
            var s = svc.CreateSession(null, "recycle");
            await Task.Delay(600);   // 等 PTY 起壳（zsh -l 冷启动）
            var r1 = await svc.RunAsync(s.Id, "echo alive-no-view", 10_000, default);
            var p1 = r1.Completed && r1.Output.Contains("alive-no-view");
            Console.WriteLine(p1
                ? "[recycle] 1/4 PASS ✅ 无视图即可建会话并执行（服务持有控件/PTY）"
                : $"[recycle] 1/4 FAIL ❌ completed={r1.Completed} err={r1.Error}");
            if (!p1) { detail = "no-view run failed"; goto done; }

            // ② 挂真视图（辅助窗口）：Loaded → OnViewAttached → 控件进树
            var w1 = new Window
            {
                Title = "recycle-w1", Width = 520, Height = 360,
                Content = new TerminalView(svc),
            };
            w1.Show();
            await Task.Delay(1200);
            var r2 = await svc.RunAsync(s.Id, "echo alive-attached", 10_000, default);
            var p2 = r2.Completed && r2.Output.Contains("alive-attached");
            Console.WriteLine(p2
                ? "[recycle] 2/4 PASS ✅ 视图附着后续用（控件已进树）"
                : $"[recycle] 2/4 FAIL ❌ completed={r2.Completed} err={r2.Error}");
            if (!p2) { detail = "attached run failed"; goto done; }

            // ③ 销毁视图（关窗 = OnDetachedFromVisualTree 真路径）：PTY 必须仍活
            w1.Close();
            await Task.Delay(800);
            var r3 = await svc.RunAsync(s.Id, "echo alive-after-destroy", 10_000, default);
            var p3 = r3.Completed && r3.Output.Contains("alive-after-destroy");
            Console.WriteLine(p3
                ? "[recycle] 3/4 PASS ✅ 视图销毁后 PTY 仍活（解耦成立）"
                : $"[recycle] 3/4 FAIL ❌ completed={r3.Completed} err={r3.Error}");
            if (!p3) { detail = "post-destroy run failed"; goto done; }

            // ④ 重建视图：原会话原样续看
            var w2 = new Window
            {
                Title = "recycle-w2", Width = 520, Height = 360,
                Content = new TerminalView(svc),
            };
            w2.Show();
            await Task.Delay(1200);
            var sessionAfter = svc.GetSessions().FirstOrDefault(x => x.Id == s.Id);
            var r4 = await svc.RunAsync(s.Id, "echo alive-recreated", 10_000, default);
            var p4 = sessionAfter is { IsRunning: true } && r4.Completed && r4.Output.Contains("alive-recreated");
            Console.WriteLine(p4
                ? "[recycle] 4/4 PASS ✅ 重建视图原样续看（会话未断）"
                : $"[recycle] 4/4 FAIL ❌ running={sessionAfter?.IsRunning} completed={r4.Completed}");
            ok = p4;
        }
        catch (Exception ex)
        {
            detail = ex.Message;
            Console.WriteLine($"[recycle] EXCEPTION ❌ {ex}");
        }
    done:
        if (svc is not null)
        {
            try { foreach (var s in svc.GetSessions().ToList()) svc.CloseSession(s.Id); } catch { /* 清理尽力而为 */ }
        }
        Console.WriteLine(ok
            ? "[recycle] PASS ✅ 视图生命周期与 PTY 生命周期彻底解耦——视图可随意销毁"
            : $"[recycle] FAIL ❌ {detail}");
        Environment.ExitCode = ok ? 0 : 1;
        await Task.Delay(300);
        desktop.Shutdown();
    }
}
