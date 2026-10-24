using System;
using System.IO;
using System.Linq;
using Aether.AI.App.Spike;
using Avalonia;

namespace Aether.AI.App.Spike.Desktop;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // 无头冒烟：CI / 命令行验证 ⑫ 全链路（Rule 大脑 + Echo 工具 + SQLite native），exit 0 = 通过。
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

    /// <summary>跑一轮「查600000」规则链路，断言终答与 ⑫ samples/BrainMatrix 同构（done:echo:600000）。</summary>
    private static async Task SmokeAsync()
    {
        var dir = Path.Combine(Path.GetTempPath(), "aether-ai-app-spike");
        Directory.CreateDirectory(dir);
        var db = Path.Combine(dir, "smoke.db");
        try { File.Delete(db); } catch { /* 无旧档 */ }

        Console.WriteLine($"[smoke] db={db}");
        try
        {
            var chat = SpikeChat.Create(db);
            var full = await chat.RunAsync("查600000", line => Console.WriteLine("[smoke] " + line));
            var ok = full == SpikeChat.ExpectedResult;
            Console.WriteLine(ok
                ? "[smoke] PASS ✅"
                : $"[smoke] FAIL ❌ 期望 {SpikeChat.ExpectedResult}，实得 {full}");
            Environment.ExitCode = ok ? 0 : 1;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[smoke] EXCEPTION ❌ {ex}");
            Environment.ExitCode = 1;
        }
    }
}
