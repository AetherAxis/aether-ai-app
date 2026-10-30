using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Aether.Terminal.AI;
using Avalonia.Threading;
using RoyalTerminal.Avalonia.Controls;

namespace Aether.AI.App.Terminal;

/// <summary>
/// <see cref="ITerminalService"/> 实现：**控件/PTY 归服务所有**（BrowserService 同款形态，2026-10-29 重构）——
/// TerminalControl 字典在服务里，视图是纯投影（附着 = 控件挂进 ContentHost；分离 = 摘下来），**视图可随意销毁**，
/// PTY/会话不受影响；重建视图即原样续看。附带收益：agent 的 run_in_terminal 在用户从未打开终端 tab 时也能跑
/// （旧实现控件归视图，无视图 = 无控件 = 工具报「尚未启动 shell」）。
/// </summary>
/// <remarks>
/// 线程模型：工具可能从 agent 线程调 CreateSession/SendInput/RunAsync——一切触碰控件的路径经 <see cref="OnUi"/>
///（UI 线程内联执行；否则阻塞等 UI 处理。UI 线程此时在 await 轮次，不会死锁）。RunAsync 的哨兵扫描沿事件线程
/// 跑（capture 列表自锁）。app 退出经容器 DisposeAsync 直接 StopPty（进程句柄线程无关，不走视觉树操作）。
/// 控件未附着视图时持名义尺寸（560×320）供 StartPty 取初始行列；附着时清掉让布局接管，resize 防抖自动校正。
/// </remarks>
public sealed class TerminalService : ITerminalService, IAsyncDisposable
{
    private readonly Dictionary<Guid, SessionInfo> _sessions = new();
    private readonly Dictionary<Guid, TerminalControl> _controls = new();
    private readonly object _lock = new();
    private TerminalView? _view;
    private Guid? _activeId;

    /// <summary>RunAsync 捕获缓冲上限（2MB）：防无限输出命令在等哨兵期间内存无界；超限时丢已扫描头部保留最近 1MB。</summary>
    private const int MaxCaptureBytes = 2 * 1024 * 1024;

    /// <summary>未附着视图时控件的名义尺寸（StartPty 的初始行列来源）；挂进视图时清掉由布局接管。</summary>
    private const double DetachedNominalWidth = 560, DetachedNominalHeight = 320;

    public Guid? ActiveSessionId { get { lock (_lock) return _activeId; } }

    public event Action? SessionsChanged;
    public event Action<Guid>? ActiveSessionChanged;
    public event Action<Guid>? SessionExited;

    // —— 视图生命周期（纯投影：挂/摘控件，不动 PTY） ——

    /// <summary>视图附着（OnLoaded）：把控件挂进 ContentHost + 主题重染。幂等。</summary>
    internal void OnViewAttached(TerminalView view)
    {
        lock (_lock) _view = view;
        OnUi(() =>
        {
            List<TerminalControl> snapshot;
            lock (_lock) snapshot = _controls.Values.ToList();
            foreach (var ctrl in snapshot)
            {
                // 清名义尺寸让布局接管（拉伸填充）；防抖 resize 会把 PTY 行列校正到实际值
                ctrl.Width = double.NaN;
                ctrl.Height = double.NaN;
                if (ctrl.Parent is null) view.Host.Children.Add(ctrl);
            }
            view.ApplyThemeToAll();
        });
    }

    /// <summary>视图分离（OnDetachedFromVisualTree / 窗口关闭）：**只从视觉树摘下控件**——
    /// 控件/PTY 归服务所有，视图销毁不杀会话（这是「视图可随意销毁」的关键路径）。</summary>
    internal void OnViewDetached(TerminalView view)
    {
        lock (_lock) { if (!ReferenceEquals(_view, view)) return; _view = null; }
        OnUi(() =>
        {
            foreach (var ctrl in view.Host.Children.OfType<TerminalControl>().ToList())
                view.Host.Children.Remove(ctrl);
        });
    }

    /// <summary>app 退出时容器 DisposeAsync 调此：直接杀所有 PTY 子进程（进程句柄线程无关，不做视觉树操作——
    /// 退出路径 dispatcher 可能已停，不能依赖 OnUi）。防退出后 shell 孤儿。</summary>
    public ValueTask DisposeAsync()
    {
        List<TerminalControl> snapshot;
        lock (_lock) snapshot = _controls.Values.ToList();
        foreach (var ctrl in snapshot)
        {
            try { ctrl.StopPty(); } catch { }
            try { ctrl.RequestClose(); } catch { }
        }
        lock (_lock) { _sessions.Clear(); _controls.Clear(); _activeId = null; }
        _view = null;
        return ValueTask.CompletedTask;
    }

    public ITerminalSession CreateSession(string? cwd, string? title)
    {
        var info = new SessionInfo(Guid.NewGuid(), cwd, title ?? DefaultTitle(), IsRunning: true);
        lock (_lock)
        {
            _sessions[info.Id] = info;
            _activeId = info.Id;
        }
        OnUi(() => CreateSessionCore(info));
        SessionsChanged?.Invoke();
        ActiveSessionChanged?.Invoke(info.Id);
        return info;
    }

    /// <summary>建控件 + 起 PTY（UI 线程内执行）。无视图时控件以 detached 形态存活（名义尺寸），附着时再挂树。</summary>
    private void CreateSessionCore(SessionInfo info)
    {
        if (_controls.ContainsKey(info.Id)) return;
        // 6 参 ctor 注入防抖 session service（Unix PTY resize 防抖——RoyalTerminal 仅 Windows 端自带；见 TerminalResizeDebounce）。
        var ctrl = new TerminalControl(
            TerminalResizeDebounce.Wrap(new RoyalTerminal.Terminal.Services.TerminalSessionService()),
            new RoyalTerminal.Avalonia.Services.DefaultTerminalInputAdapter(),
            new RoyalTerminal.Avalonia.Services.DefaultTerminalSelectionService(),
            new RoyalTerminal.Avalonia.Services.DefaultTerminalScrollService(),
            new RoyalTerminal.Terminal.DefaultVtProcessorFactory(),
            new RoyalTerminal.Terminal.DefaultPtyFactory())
        {
            IsVisible = false,
            TerminalFontSize = 13,
            // 回滚缓冲封顶（默认无限）：长会话无限回滚把已滚动内容驻留内存，10000 行防无界增长。
            ScrollbackLimit = 10_000,
            // 名义尺寸：detached 时 StartPty 取初始行列用；挂树时 OnViewAttached 清掉由布局接管
            Width = DetachedNominalWidth,
            Height = DetachedNominalHeight,
        };
        ctrl.TitleChanged += (_, t) => NotifyTitleChanged(info.Id, t ?? info.Title);
        ctrl.ProcessExited += (_, _) => NotifyExited(info.Id);
        _controls[info.Id] = ctrl;

        TerminalView? view;
        lock (_lock) view = _view;
        view?.Host.Children.Add(ctrl);

        var (shell, args) = ResolveShell();
        var wd = string.IsNullOrWhiteSpace(info.Cwd)
            ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
            : info.Cwd;
        try
        {
            ctrl.StartPty(shell, wd, args);
        }
        catch (Exception ex)
        {
            // 起壳失败不崩——向控件写一条错误（可见）+ 标记退出。
            ctrl.WriteOutput(Encoding.UTF8.GetBytes($"终端启动失败: {ex.Message}\r\n"));
            NotifyExited(info.Id);
        }
        ActivateCore(info.Id);
    }

    public void CloseSession(Guid sessionId)
    {
        OnUi(() =>
        {
            if (_controls.Remove(sessionId, out var ctrl))
            {
                try { ctrl.StopPty(); } catch { }
                try { ctrl.RequestClose(); } catch { }
            }
        });
        bool removed;
        Guid? newActive = null;
        lock (_lock)
        {
            removed = _sessions.Remove(sessionId);
            if (removed && _activeId == sessionId)
            {
                _activeId = _sessions.Count > 0 ? _sessions.Keys.Last() : null;
                newActive = _activeId;
            }
        }
        if (removed)
        {
            if (newActive is { } aid) { OnUi(() => ActivateCore(aid)); ActiveSessionChanged?.Invoke(aid); }
            SessionsChanged?.Invoke();
            // 关掉最后一个会话 → 自动补一个全新会话：终端面板恒有活终端（无空态死区；cwd 走 shell 家目录）。
            lock (_lock) { if (_sessions.Count == 0) { CreateSession(null, null); return; } }
        }
    }

    public void SetActive(Guid sessionId)
    {
        lock (_lock)
        {
            if (!_sessions.ContainsKey(sessionId)) return;
            _activeId = sessionId;
        }
        OnUi(() => ActivateCore(sessionId));
        ActiveSessionChanged?.Invoke(sessionId);
    }

    private void ActivateCore(Guid id)
    {
        foreach (var (cid, ctrl) in _controls)
            ctrl.IsVisible = cid == id;
    }

    public IReadOnlyList<ITerminalSession> GetSessions()
    {
        lock (_lock) return _sessions.Values.ToList();
    }

    public void SendInput(Guid sessionId, string text)
        => OnUi(() => { if (_controls.TryGetValue(sessionId, out var ctrl)) ctrl.SendInput(text); });

    public async Task<TerminalRunResult> RunAsync(Guid sessionId, string command, int timeoutMs, CancellationToken ct)
    {
        TerminalControl? ctrl;
        lock (_lock) _controls.TryGetValue(sessionId, out ctrl);
        if (ctrl is null || !ctrl.HasPty)
            return new TerminalRunResult { Error = "终端会话不存在或尚未启动 shell" };

        // 哨兵：OSC 8331 + 退出码。shell 收到的命令文本含 printf '\x1b]8331;%d\x07'（字面 backslash-x，
        // 非真实 ESC），printf 输出真实 ESC/BEL 字节——回显里无真实 0x1b，扫描不误命中。
        var fullCmd = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? $"{command}; Write-Host -NoNewline \"$([char]27)]8331;$LASTEXITCODE$([char]7)\""
            : $"{command}; printf '\\x1b]8331;%d\\x07' \"$?\"";

        var capture = new List<byte>();
        var captureLock = new object();
        var done = new TaskCompletionSource<int?>(TaskCreationOptions.RunContinuationsAsynchronously);
        int scanPos = 0;

        void OnData(object? s, TerminalDataEventArgs e)
        {
            int exitCode;
            lock (captureLock)
            {
                capture.AddRange(e.Data.Span.ToArray());
                if (TryFindSentinel(capture, ref scanPos, out exitCode))
                {
                    done.TrySetResult(exitCode);
                    return;
                }
                // capture 封顶（防 yes/无限日志类命令在等哨兵期间内存无界）：超 2MB 丢已扫描头部，保留最近 1MB。
                if (capture.Count > MaxCaptureBytes)
                {
                    var drop = Math.Min(scanPos, capture.Count - (MaxCaptureBytes / 2));
                    if (drop > 0)
                    {
                        capture.RemoveRange(0, drop);
                        scanPos -= drop;
                    }
                    else if (capture.Count > MaxCaptureBytes * 2)
                    {
                        capture.RemoveRange(0, capture.Count - (MaxCaptureBytes / 2));
                        scanPos = 0;
                    }
                }
            }
        }

        ctrl.DataReceived += OnData;
        try
        {
            OnUi(() => ctrl.SendInput(fullCmd + "\n"));
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeoutMs);
            try
            {
                await done.Task.WaitAsync(cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                var partial = ReadCapture();
                if (ct.IsCancellationRequested)
                    return new TerminalRunResult { Completed = false, Output = partial,
                        Error = "命令执行已被取消（用户停止），进程仍在终端运行（可手动 Ctrl-C）" };
                return new TerminalRunResult { Completed = false, Output = partial,
                    Error = $"命令执行超时（{timeoutMs}ms），进程仍在终端运行（可在终端手动停止）" };
            }

            int? exit = await done.Task.ConfigureAwait(false);
            return new TerminalRunResult { ExitCode = exit, Completed = true, Output = ReadCapture() };
        }
        finally
        {
            ctrl.DataReceived -= OnData;
        }

        string ReadCapture()
        {
            byte[] bytes;
            lock (captureLock) bytes = capture.ToArray();
            var text = StripAnsi(Encoding.UTF8.GetString(bytes));
            // 丢弃首行（shell 回显的命令行——单行命令的回显占一行；多行回显可能多行，首行截断已大幅降噪）。
            var nl = text.IndexOf('\n');
            if (nl >= 0 && nl < text.Length) text = text[(nl + 1)..];
            return text.Trim();
        }
    }

    // —— 控件事件回呼：更新元数据 + 通知 VM ——
    internal void NotifyTitleChanged(Guid id, string title)
    {
        lock (_lock)
        {
            if (_sessions.TryGetValue(id, out var s))
                _sessions[id] = s with { Title = title };
            else return;
        }
        SessionsChanged?.Invoke();
    }

    internal void NotifyExited(Guid id)
    {
        lock (_lock)
        {
            if (_sessions.TryGetValue(id, out var s))
                _sessions[id] = s with { IsRunning = false };
            else return;
        }
        SessionsChanged?.Invoke();
        SessionExited?.Invoke(id);
    }

    /// <summary>主题重染用：当前全部控件（视图 ApplyThemeToAll 消费）。</summary>
    internal IEnumerable<TerminalControl> GetAllControls()
    {
        lock (_lock) return _controls.Values.ToList();
    }

    /// <summary>控件触碰统一入口：UI 线程内联；否则阻塞等 UI 处理（工具线程调 CreateSession/SendInput 用）。
    /// UI 线程此刻在 await 轮次（异步），不会被占死——无死锁。</summary>
    private static void OnUi(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess()) action();
        else Dispatcher.UIThread.InvokeAsync(action).GetAwaiter().GetResult();
    }

    private static string DefaultTitle() => RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "powershell" : "shell";

    /// <summary>shell 解析（交互式登录 shell：Unix zsh/bash -l；Windows powershell）。</summary>
    private static (string shell, IReadOnlyList<string> args) ResolveShell()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return ("powershell.exe", new[] { "-NoLogo" });
        var shell = Environment.GetEnvironmentVariable("SHELL");
        if (string.IsNullOrEmpty(shell) || !File.Exists(shell))
            shell = File.Exists("/bin/zsh") ? "/bin/zsh" : "/bin/bash";
        return (shell, new[] { "-l" });
    }

    /// <summary>在 capture 的 scanPos.. 之间扫描哨兵 ESC ] 8331 ; &lt;digits&gt; BEL。命中返回退出码并推进 scanPos 过哨兵。</summary>
    private static bool TryFindSentinel(List<byte> buf, ref int scanPos, out int exitCode)
    {
        exitCode = 0;
        for (int i = scanPos; i < buf.Count - 1; i++)
        {
            if (buf[i] != 0x1B || buf[i + 1] != (byte)']') continue;
            if (i + 6 >= buf.Count) break; // 不够长，等更多字节
            if (buf[i + 2] != (byte)'8' || buf[i + 3] != (byte)'3' || buf[i + 4] != (byte)'3' ||
                buf[i + 5] != (byte)'1' || buf[i + 6] != (byte)';')
            {
                continue;
            }
            int j = i + 7;
            var digits = new StringBuilder();
            while (j < buf.Count && buf[j] != 0x07 && buf[j] != 0x1B && j - (i + 7) < 12)
            {
                if (buf[j] >= (byte)'0' && buf[j] <= (byte)'9') digits.Append((char)buf[j]);
                else break;
                j++;
            }
            if (j >= buf.Count || buf[j] != 0x07)
            {
                scanPos = i;
                return false;
            }
            if (digits.Length > 0 && int.TryParse(digits.ToString(), out exitCode))
            {
                scanPos = j + 1;
                return true;
            }
        }
        scanPos = Math.Max(0, buf.Count - 20);
        return false;
    }

    /// <summary>剥离 ANSI/OSC/CSI 转义 + CR，保留换行。OSC 8331 哨兵亦被剥（输出不含哨兵）。
    /// （monorepo 原文用字面 ESC/BEL 字节——port 时改显式 \x1B/\x07 转义，防不可见字符再丢。）</summary>
    private static readonly Regex AnsiStrip = new(
        @"\x1B\][^\x07\x1B]*(\x07|\x1B\\)|\x1B\[[0-9;?]*[ -/]*[@-~]|\x1B[@-Z\\-_]|\r",
        RegexOptions.Compiled);

    private static string StripAnsi(string s) => AnsiStrip.Replace(s, string.Empty);

    private sealed record SessionInfo(Guid Id, string? Cwd, string Title, bool IsRunning) : ITerminalSession;
}
