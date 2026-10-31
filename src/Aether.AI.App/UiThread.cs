using Aether.AI.App.Core;
using Avalonia.Threading;

namespace Aether.AI.App;

/// <summary>
/// View code-behind / 桌面切片等**无法注入 <see cref="IUiDispatcher"/>** 的场合使用的静态安全投递：
/// 与 <see cref="AvaloniaUiDispatcher.Post"/> 同一契约——lambda 内异常记黑匣子不上抛
/// （fire-and-forget 上抛 = UI 线程未处理异常 = 进程死，2026-10-31 防线机械化）。
/// VM / 引擎层一律走注入端口，只有 View 层的零星回调用这个。
/// </summary>
public static class UiThread
{
    /// <summary>安全投递到 UI 线程（fire-and-forget，吞异常记黑匣子）。</summary>
    public static void SafePost(Action action) => Dispatcher.UIThread.Post(() =>
    {
        try { action(); }
        catch (Exception ex) { CrashGuard.Log("UiThread.SafePost", ex); }
    });
}
