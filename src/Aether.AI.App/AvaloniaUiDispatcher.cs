using Aether.AI.App.Core;
using Avalonia.Threading;

namespace Aether.AI.App;

/// <summary><see cref="IUiDispatcher"/> 的 Avalonia 实现（Dispatcher.UIThread 编组）。</summary>
public sealed class AvaloniaUiDispatcher : IUiDispatcher
{
    public async Task InvokeAsync(Action action) => await Dispatcher.UIThread.InvokeAsync(action);

    public void Post(Action action) => Dispatcher.UIThread.Post(action);
}
