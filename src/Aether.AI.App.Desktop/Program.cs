using System;
using System.IO;
using System.Linq;
using Aether.AI.App;
using Aether.AI.App.Browser;   // AddBrowserPanel（桌面 head 的浏览器面板注册口）
using Aether.AI.App.Core;
using Aether.AI.App.Terminal;  // AddTerminalPanel（桌面 head 的终端面板注册口）
using Aether.Browser.AI;
using Aether.AI.Brains.Code.Compilation.CSharp;
using Aether.AI.Core.Agents;
using Aether.AI.Harness.Tools;
using Avalonia;
using Microsoft.Extensions.DependencyInjection;

namespace Aether.AI.App.Desktop;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // 崩溃防线最先装（2026-10-31「对话运行中整个 app 崩溃」根治层之一）：UnobservedTaskException
        // 兜底 + 黑匣子（<dataDir>/logs/app-log.txt——托管异常在 macOS .ips/统一日志里零痕迹，
        // 下次再崩先看黑匣子）。幂等：App.Initialize 会再装一次（覆盖直接起 App 的移动 head）。
        CrashGuard.Install(App.ResolveDataDirectory());

        // 显式声明桌面宿主：命令工具 / 浏览器工具在无头巡检（--tools/--smoke/CI）里也要按桌面取舍。
        App.HeadHost = HostCapability.DesktopOnly;
        // 代码大脑只在桌面形态可编译执行：把 Roslyn 编译器注入共享组合根（App.BuildProvider）。
        // 移动 head 不引本程序集，也就没有编译器——代码 agent 会明确回「未注册 C# 编译器」。
        App.ServicesConfigure = services => services.AddCSharpBrainCompilation();
        // 浏览器（⑭ Aether.Browser.AI 工具 + 本 head Browser/ 面板）与终端（⑭ Aether.Terminal.AI 的
        // run_in_terminal 工具 + 本 head Terminal/ 面板——RoyalTerminal 真实 PTY）：HeadToolsConfigure 钩子的
        // 桌面专属消费者。移动 head 不设此钩子 ⇒ 两套工具连注册都不发生、Playwright/RoyalTerminal 不进移动闭包。
        // 浏览器启动模式（常规/附加）是宿主策略：browser.json 落盘，面板 chip 切换，对下一次新建会话生效。
        App.HeadToolsConfigure = (aether, dataDir) =>
        {
            aether.AddBrowserPanel(dataDir);
            aether.AddTerminalPanel();
        };
        // head 专属 GUI 实证钩子：--view-recycle-test（视图销毁不杀 PTY 的端到端，需要 head 内具体类型）
        App.HeadGuiHook = Terminal.ViewRecycleTest.Run;

        // 无头冒烟：与 GUI 完全同一条 DI 组合链（App.BuildProvider），跑规则大脑全链路 + 审批自恢复轮。
        // exit 0 = 通过——CI / 命令行验收用（L2 的无 GUI 等价物）。
        if (args.Contains("--smoke"))
        {
            SmokeAsync().GetAwaiter().GetResult();
            return;
        }

        // 工具清单巡检：走真实组合（含桌面 head 钩子）打印**当前宿主可用**的工具——
        // 验收「⑭ 工具 + 浏览器工具是否真接上了」用（`--tools` 里应能看到 系统/应用审查/浏览器 各组）。
        if (args.Contains("--tools"))
        {
            DumpTools();
            return;
        }

        // LLM 真模型冒烟：真实组合 + assistant 一轮流式对话，断言 token 流与终答非空。
        // 需先配置 llm-configs.json（设置页或手工）；无档案时 SKIP（exit 0——不阻塞无 key 环境）。
        if (args.Contains("--llm-smoke"))
        {
            LlmSmokeAsync().GetAwaiter().GetResult();
            return;
        }

        // 浏览器真链路冒烟：Playwright 真启动（headed，屏幕上会闪一个窗口）→ about:blank → 取 cookie（空也可）
        // → 关会话。验证「驱动进程 + 浏览器启动（channel 自动探测）+ 生命周期清理」全链。
        // 无 Chrome/Edge 且未 playwright install chromium 时 FAIL 并给指引（exit 1）。
        if (args.Contains("--browser-smoke"))
        {
            BrowserSmokeAsync().GetAwaiter().GetResult();
            return;
        }

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    /// <summary>
    /// 浏览器真链路冒烟（L2 无 GUI 交互等价物——浏览器窗口本身会闪现，但无需点按）：
    /// ① 常规模式 EnsureOpenAsync 真启动（channel 未传 = 自动探测本机 Chrome/Edge）；
    /// ② GetCookies 在 about:blank 上 Success；③ 常规模式操作三件套离线 E2E（data: URL 页，不依赖外网）；
    /// ④ 附加模式（本机浏览器普通启动 + CDP 连入，webdriver=false 探针已实证）会话启动 + 同一套操作 E2E；
    /// ⑤ 全部关闭后会话清空。五断言全过 = 两种模式的驱动/操作/清理全链健康。
    /// </summary>
    private static async Task BrowserSmokeAsync()
    {
        var ok = true;
        try
        {
            var provider = App.BuildProvider();
            var browser = provider.GetRequiredService<IBrowserService>();

            // —— 常规（Launch）模式 ——
            var session = await browser.EnsureOpenAsync("about:blank", null);
            var pass1 = session.IsAlive;
            Console.WriteLine(pass1
                ? $"[browser-smoke] 1/5 PASS ✅ 常规模式会话启动（title={session.Title}）"
                : "[browser-smoke] 1/5 FAIL ❌ 浏览器启动失败——装 Chrome/Edge，或执行 playwright install chromium");
            ok &= pass1;

            var cookies = await browser.GetCookiesAsync(session.Id);
            var pass2 = cookies.Success;
            Console.WriteLine(pass2
                ? $"[browser-smoke] 2/5 PASS ✅ cookie 提取链路（{cookies.Cookies.Count} 条，about:blank 允许为空）"
                : $"[browser-smoke] 2/5 FAIL ❌ {cookies.Error}");
            ok &= pass2;

            var opsLaunch = session.IsAlive && await OpsE2eAsync(browser, session.Id);
            Console.WriteLine(opsLaunch
                ? "[browser-smoke] 3/5 PASS ✅ 常规模式操作三件套 E2E（type→click→read）"
                : "[browser-smoke] 3/5 FAIL ❌ 常规模式操作 E2E 失败（见上方各步输出）");
            ok &= opsLaunch;
            browser.CloseSession(session.Id);

            // —— 附加（Attach）模式：本机浏览器普通启动 + CDP 连入 ——
            var attach = await browser.EnsureOpenAsync("about:blank", null, BrowserLaunchMode.Attach);
            var opsAttach = attach.IsAlive && await OpsE2eAsync(browser, attach.Id);
            var pass4 = attach.IsAlive && opsAttach;
            Console.WriteLine(pass4
                ? "[browser-smoke] 4/5 PASS ✅ 附加模式 E2E（普通启动 + CDP 连入 + 操作三件套全通）"
                : $"[browser-smoke] 4/5 FAIL ❌ 附加模式失败（alive={attach.IsAlive}——需本机 Chrome/Edge）");
            ok &= pass4;

            // 收尾走 app 退出路径（await 容器批量释放）：附加模式的浏览器进程是我们 Process.Start 的、
            // 不归 Playwright driver 代管——kill 必须 await 落地，否则进程退出时后台清理被截断 → 浏览器孤儿。
            // （DisposeAsync 在 BrowserService 具体类上——接口刻意不继承 IAsyncDisposable，免得 fake 都要补实现。）
            await ((IAsyncDisposable)browser).DisposeAsync();
            var pass5 = browser.GetSessions().Count == 0 && browser.ActiveSessionId is null;
            Console.WriteLine(pass5
                ? "[browser-smoke] 5/5 PASS ✅ 会话全部关闭干净（含附加模式进程 kill 落地）"
                : "[browser-smoke] 5/5 FAIL ❌ 关闭后会话仍在列表");
            ok &= pass5;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[browser-smoke] EXCEPTION ❌ {ex}");
            ok = false;
        }
        Environment.ExitCode = ok ? 0 : 1;
    }

    /// <summary>操作三件套离线 E2E：data: URL 页上 type 填 #q → click 点 #b（把输入框值写进标题）→ read 断言。</summary>
    private static async Task<bool> OpsE2eAsync(IBrowserService browser, Guid sessionId)
    {
        var opsPage = "data:text/html,<input id='q' value=''><button id='b' onclick=\"document.title=document.getElementById('q').value\">Go</button>";
        await browser.NavigateAsync(sessionId, opsPage);
        var typed = await browser.TypeAsync(sessionId, "css=#q", "aether-rocks");
        var clicked = await browser.ClickAsync(sessionId, "css=#b");
        var page = await browser.ReadPageAsync(sessionId);
        var pass = typed.Success && clicked.Success && page.Success
                   && page.Title == "aether-rocks" && page.Text.Contains("Go");
        if (!pass)
            Console.WriteLine($"[browser-smoke]   ops 明细：type={typed.Success}({typed.Error ?? "-"}) click={clicked.Success}({clicked.Error ?? "-"}) read={page.Success} title=\"{page.Title}\"");
        return pass;
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
    /// 两断言（2026-10-29 起走真实工具——Echo 演示工具已移除）：
    /// ① rule-demo 全链路：ReadFile 读临时文件 → done:{JSON 结果含内容}；
    /// ② rule-approve 审批循环：resolver 自动批 → ⑫ RunInteractiveAsync 恢复轮 → DeleteFile **真删临时文件** → 终答
    /// （副作用 = 文件确实没了——端到端最硬的断言）。
    /// </summary>
    private static async Task SmokeAsync()
    {
        var dir = Path.Combine(Path.GetTempPath(), "aether-ai-app-smoke");
        try { Directory.Delete(dir, true); } catch { /* 无旧档 */ }
        Directory.CreateDirectory(dir);

        var ok = true;
        try
        {
            // 演示 agent（规则演示 / 审批演示）只在冒烟/演示路径挂，正常运行目录里只有用户自己的 agent。
            var provider = App.BuildProvider(dir, includeDemoAgents: true);
            var runner = provider.GetRequiredService<IAgentRunner>();

            var readFile = Path.Combine(dir, "read-me.txt");
            await File.WriteAllTextAsync(readFile, "600000");
            var full1 = await runner.RunInteractiveAsync(Guid.NewGuid(), "rule-demo", $"读{readFile}");
            var pass1 = full1.StartsWith("done:") && full1.Contains("600000", StringComparison.Ordinal);
            Console.WriteLine(pass1
                ? "[smoke] 1/2 PASS ✅ rule-demo 全链路（ReadFile 真读文件）"
                : $"[smoke] 1/2 FAIL ❌ 期望 done:{{...600000...}}，实得 {full1}");
            ok &= pass1;

            var delFile = Path.Combine(dir, "to-delete.txt");
            await File.WriteAllTextAsync(delFile, "冒烟道具");
            var full2 = await runner.RunInteractiveAsync(Guid.NewGuid(), "rule-approve", $"删除{delFile}",
                approvalResponder: (req, ct) => Task.FromResult<ToolApprovalDecision?>(
                    new ToolApprovalDecision(req.RequestId, Approved: true, Reason: "冒烟自动批", AlwaysApprove: false)));
            var pass2 = full2.StartsWith("已执行") && full2.Contains("已删除文件", StringComparison.Ordinal)
                        && !File.Exists(delFile);
            Console.WriteLine(pass2
                ? "[smoke] 2/2 PASS ✅ rule-approve 审批自恢复（DeleteFile 真删文件，副作用已核）"
                : $"[smoke] 2/2 FAIL ❌ 回复={full2}；文件仍在？{File.Exists(delFile)}");
            ok &= pass2;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[smoke] EXCEPTION ❌ {ex}");
            ok = false;
        }
        Environment.ExitCode = ok ? 0 : 1;
    }

    /// <summary>按分类打印当前宿主可用的工具（名字 / 审批等级）；同时列出「注册了但当前宿主不可用」的，
    /// 便于一眼看出手机端会少掉哪些。</summary>
    private static void DumpTools()
    {
        var provider = App.BuildProvider();
        var registry = provider.GetRequiredService<ToolRegistry>();

        var usable = registry.GetUsableMetas();
        Console.WriteLine($"[tools] 宿主={registry.CurrentHost}，可用 {usable.Count} 个，注册总数 {registry.GetAllMetas().Count} 个：");
        foreach (var group in usable.GroupBy(m => m.Category).OrderBy(g => g.Key))
        {
            Console.WriteLine($"[tools] {group.Key}（{group.Count()}）");
            foreach (var meta in group)
                Console.WriteLine($"[tools]   - {meta.Name} [{meta.ApprovalLevel}] {meta.Description}");
        }

        // 按名字比对：ToolMeta 每次投影都是新实例，直接 Except 会因引用不等而把全部算成「被过滤」。
        var usableNames = usable.Select(m => m.Name).ToHashSet(StringComparer.Ordinal);
        var hidden = registry.GetAllMetas().Select(m => m.Name).Where(n => !usableNames.Contains(n)).ToList();
        if (hidden.Count > 0)
            Console.WriteLine($"[tools] 当前宿主不可用（已注册、被过滤）：{string.Join(", ", hidden)}");
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

        // 「通用助手」是首次运行播种的普通 agent——用户可能删了或改名；没有就用目录里任意 LLM agent。
        var catalog = provider.GetRequiredService<IAgentCatalog>();
        var agents = await catalog.GetAllAsync();
        var target = agents.FirstOrDefault(a => a.Id == "assistant")
                     ?? agents.FirstOrDefault(a => a.Engine == EngineKind.Llm);
        if (target is null)
        {
            Console.WriteLine("[llm-smoke] SKIP：目录里没有 LLM agent（先在 agent 管理窗新建一个）");
            Environment.ExitCode = 0;
            return;
        }

        var runner = provider.GetRequiredService<Aether.AI.App.Core.IAgentRunner>();
        var tokens = 0;
        var reasoning = 0;
        var usage = "";
        try
        {
            var full = await runner.RunInteractiveAsync(Guid.NewGuid(), target.Id,
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
