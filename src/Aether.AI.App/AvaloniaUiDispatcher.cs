using Aether.AI.App.Core;
using Avalonia.Threading;

namespace Aether.AI.App;

/// <summary><see cref="IUiDispatcher"/> 的 Avalonia 实现（Dispatcher.UIThread 编组）。
/// <see cref="Post"/> 按 2026-10-31 契约吞掉 lambda 内异常（记黑匣子）——fire-and-forget 上抛
/// = UI 线程未处理异常 = 进程死，这是防线从「调用点自觉包 try/catch」下沉为「端口结构保证」的落点。</summary>
public sealed class AvaloniaUiDispatcher : IUiDispatcher
{
    public async Task InvokeAsync(Action action) => await Dispatcher.UIThread.InvokeAsync(action);

    public void Post(Action action) => Dispatcher.UIThread.Post(() =>
    {
        try { action(); }
        catch (Exception ex) { CrashGuard.Log("UiDispatcher.Post", ex); }
    });
}
