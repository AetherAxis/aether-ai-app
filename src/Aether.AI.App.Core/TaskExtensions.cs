using System.Diagnostics;

namespace Aether.AI.App.Core;

/// <summary>
/// fire-and-forget Task 扩展：await + 记异常到 trace 通道，防 UnobservedTaskException 静默吞错误。
/// 自 monorepo TaskExtensions port（去掉崩溃遥测订阅口——App 侧需要时再回归）。
/// </summary>
public static class TaskExtensions
{
    /// <summary>安全触发一个不需等待的 Task（异常记到 trace 不崩进程）。</summary>
    public static async void SafeFireAndForget(this Task task)
    {
        try { await task; }
        catch (Exception ex)
        {
            Trace.WriteLine($"[SafeFireAndForget] {ex}");
        }
    }
}
