namespace Aether.AI.App.Core;

/// <summary>
/// 进程级崩溃防线（2026-10-31 增设——「AI 对话运行中整个 app 崩溃」的兜底层）：
/// <list type="bullet">
///   <item><see cref="TaskScheduler.UnobservedTaskException"/> → 记黑匣子 + <c>SetObserved</c>：
///     fire-and-forget Task 的漏网异常原本会在 finalizer 线程再抛、直接杀进程。</item>
///   <item><see cref="AppDomain.UnhandledException"/> → 记黑匣子（.NET 上此事件后进程仍将退出——
///     兜不住，但栈留住了）。macOS 的 .ips / 统一日志对托管异常**零痕迹**（实测 2026-10-30 的崩溃
///     两者皆空），黑匣子是唯一现场。</item>
/// </list>
/// 黑匣子落 <c>&lt;dataDir&gt;/logs/app-log.txt</c>（超 4MB 截断重开）。幂等：桌面 Program.Main 与
/// App.Initialize（三 head 共用）各装一次，重复 Install 无副作用（换目录则更新落点）。
/// 这不是「不崩」的保证——各调用点的 try/catch（引擎 PostSafe / VM 轮次防线）才是第一道；
/// 本类保证的是「真兜不住时，下次启动有栈可查」。
/// </summary>
public static class CrashGuard
{
    private const long MaxLogBytes = 4 * 1024 * 1024;

    private static readonly object Gate = new();
    private static bool _handlersWired;
    private static string? _logDir;

    /// <summary>装防线（幂等）。<paramref name="logDirectory"/> = 数据目录（黑匣子落其 logs/ 子目录）。</summary>
    public static void Install(string? logDirectory)
    {
        lock (Gate)
        {
            if (logDirectory is not null) _logDir = logDirectory;
            if (_handlersWired) return;
            _handlersWired = true;
        }
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Log("UnobservedTaskException", e.Exception);
            e.SetObserved();   // 不标记 = finalizer 线程重抛 = 进程死
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Log(e.IsTerminating ? "UnhandledException(fatal)" : "UnhandledException",
                e.ExceptionObject as Exception ?? new Exception(e.ExceptionObject?.ToString() ?? "<null>"));
    }

    /// <summary>黑匣子追加一条异常记录。自身绝不抛（写不进文件退 stderr）——防线不能成为新崩溃源。</summary>
    public static void Log(string source, Exception? ex)
    {
        try
        {
            var line = $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff}] [{source}]\n{ex}\n\n";
            if (_logDir is null)
            {
                Console.Error.WriteLine(line);
                return;
            }
            var dir = Path.Combine(_logDir, "logs");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "app-log.txt");
            if (File.Exists(path) && new FileInfo(path).Length > MaxLogBytes)
                File.WriteAllText(path, line);   // 截断重开（黑匣子只追最近的）
            else
                File.AppendAllText(path, line);
        }
        catch
        {
            try { Console.Error.WriteLine($"[{source}] {ex}"); } catch { /* 最后退路也失败——静默 */ }
        }
    }
}
