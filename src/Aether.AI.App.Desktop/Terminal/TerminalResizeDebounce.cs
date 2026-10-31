using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Aether.AI.App;
using Avalonia.Threading;
using RoyalTerminal.Terminal;
using RoyalTerminal.Terminal.Services;

namespace Aether.AI.App.Terminal;

/// <summary>
/// Unix PTY 的 resize 防抖（VSCode 式「本地网格实时重排 + PTY 只拿稳定后尺寸」）。
/// （自 monorepo Acme.Desktop.Shared.Shell port，2026-10-29，原样。）
///
/// 背景：RoyalTerminal 只在 **Windows PTY** 上防抖 transport resize（`WindowsPtyTransportResizeDebounceInterval`
/// = 75ms，防 ConPTY 逐帧重画碎片）——**Unix 端没有**：控件在 ArrangeOverride 逐帧检测尺寸，拖 splitter/改窗的每个中间尺寸
/// 都即时 `ResizeSession/ResizePty → TIOCSWINSZ → SIGWINCH`，zsh 每收一次就重画提示符，连续拖拽 = 几十次
/// 重画堆成空行瀑布。本包装器在 <see cref="ITerminalSessionService"/> 层补齐同款 75ms 防抖——控件的 resize
/// 无论走 transport 路径（ResizeSession）还是 IPty 直连路径（ResizePty）都汇到这两个方法，单点拦截全覆盖；
/// 本地网格照常逐帧重排（不经 session service），PTY 只收最终尺寸。
///
/// 用法：TerminalView.CreateSessionControl 以 6 参 ctor 建 TerminalControl，第 1 参传
/// <c>TerminalResizeDebounce.Wrap(new TerminalSessionService())</c>（其余参数 = 库公开默认实现，与控件
/// 无参 ctor 等价）。**移除判据**：RoyalTerminal 升级后若 `ShouldDebounceWindowsPtyTransportResize`
/// 去掉 `IsWindows` 门（Unix 补齐），本文件可整体删除（TerminalView 恢复无参 ctor + 对象初始化器）。
/// </summary>
internal static class TerminalResizeDebounce
{
    /// <summary>对齐库内 Windows 端常量（75ms）。</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(75);

    /// <summary>包 session service：ResizePty/ResizeSession 防抖（各自独立槽位），其余成员（含事件）透传。</summary>
    public static ITerminalSessionService Wrap(ITerminalSessionService inner) => new DebouncedSessionService(inner);

    private sealed class DebouncedSessionService : ITerminalSessionService
    {
        private readonly ITerminalSessionService _inner;
        private readonly ActionDebounce _resizePty = new();
        private readonly ActionDebounce _resizeSession = new();

        public DebouncedSessionService(ITerminalSessionService inner)
        {
            _inner = inner;
            _inner.InputSent += (s, e) => InputSent?.Invoke(s, e);
        }

        public event EventHandler<TerminalSessionInputEventArgs>? InputSent;

        public ITerminalEndpoint Endpoint => _inner.Endpoint;
        public ITerminalInputSink InputSink => _inner.InputSink;
        public ITerminalSelectionSource SelectionSource => _inner.SelectionSource;
        public ITerminalModeSource ModeSource => _inner.ModeSource;
        public ITerminalTransport Transport => _inner.Transport;
        public bool HasActiveTransport => _inner.HasActiveTransport;
        public IPty Pty => _inner.Pty;
        public bool HasPty => _inner.HasPty;

        public void AttachEndpoint(ITerminalEndpoint endpoint) => _inner.AttachEndpoint(endpoint);

        public void DetachEndpoint() => _inner.DetachEndpoint();

        public void SendInput(string text) => _inner.SendInput(text);

        public void SendInput(ReadOnlySpan<byte> data) => _inner.SendInput(data);

        public void StartPty(IPtyFactory ptyFactory, string shell, int columns, int rows, string workingDirectory,
            IVtProcessor vtProcessor,
            Action<byte[], int> onPtyDataReceived, Action<int> onPtyProcessExited,
            Action<byte[]> onVtResponse, Action onVtBell, Action<string> onVtTitleChanged,
            IReadOnlyList<string> arguments)
            => _inner.StartPty(ptyFactory, shell, columns, rows, workingDirectory, vtProcessor,
                onPtyDataReceived, onPtyProcessExited, onVtResponse, onVtBell, onVtTitleChanged, arguments);

        public ValueTask StartSessionAsync(ITerminalTransportFactory transportFactory,
            ITerminalTransportOptions transportOptions, IVtProcessor vtProcessor,
            Action<byte[], int> onTransportDataReceived, Action<int> onTransportProcessExited,
            Action<byte[]> onVtResponse, Action onVtBell, Action<string> onVtTitleChanged,
            CancellationToken cancellationToken)
            => _inner.StartSessionAsync(transportFactory, transportOptions, vtProcessor,
                onTransportDataReceived, onTransportProcessExited, onVtResponse, onVtBell, onVtTitleChanged,
                cancellationToken);

        public void StopPty(IVtProcessor vtProcessor,
            Action<byte[], int> onPtyDataReceived, Action<int> onPtyProcessExited)
            => _inner.StopPty(vtProcessor, onPtyDataReceived, onPtyProcessExited);

        public ValueTask StopSessionAsync(IVtProcessor vtProcessor,
            Action<byte[], int> onTransportDataReceived, Action<int> onTransportProcessExited)
            => _inner.StopSessionAsync(vtProcessor, onTransportDataReceived, onTransportProcessExited);

        public void ResizePty(int columns, int rows, int widthPixels, int heightPixels)
            => _resizePty.Schedule(() => _inner.ResizePty(columns, rows, widthPixels, heightPixels));

        public void ResizeSession(int columns, int rows, int widthPixels, int heightPixels)
            => _resizeSession.Schedule(() => _inner.ResizeSession(columns, rows, widthPixels, heightPixels));
    }

    /// <summary>末位胜出的动作防抖：连续 Schedule 只在静默 Interval 后执行最后一次。UI 线程计时（Resize 来自控件布局）。</summary>
    private sealed class ActionDebounce
    {
        private readonly object _lock = new();
        private Action? _pending;
        private DispatcherTimer? _timer;

        public void Schedule(Action commit)
        {
            UiThread.SafePost(() =>
            {
                lock (_lock)
                {
                    _pending = commit;
                    if (_timer is null)
                    {
                        _timer = new DispatcherTimer { Interval = Interval };
                        _timer.Tick += (_, _) =>
                        {
                            Action? fire;
                            lock (_lock) { fire = _pending; _pending = null; _timer!.Stop(); }
                            fire?.Invoke();
                        };
                    }
                    _timer.Stop();
                    _timer.Start();
                }
            });
        }

        public void Cancel()
        {
            lock (_lock) { _timer?.Stop(); _pending = null; }
        }
    }
}
