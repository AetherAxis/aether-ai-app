using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Aether.Terminal.AI;

namespace Aether.AI.App.Terminal;

/// <summary>
/// <see cref="ITerminalService"/> 实现：持会话元数据 + 哨兵捕获逻辑；控件/PTY 生命周期委托给已附着的 <see cref="TerminalView"/>。
/// 单例——一个终端面板。app 退出经容器 DisposeAsync 杀所有 PTY（防 shell 孤儿——ShutdownRequested 钩子接线）。
/// （自 monorepo Acme.Desktop.Shared.Shell.TerminalService port，2026-10-29；差异：去掉了底部面板显隐
/// 持久化三成员——⑮ 是 nav tab 形态，没有折叠 toggle。）
/// </summary>
/// <remarks>
/// 控件由 <see cref="TerminalView"/> 拥有（每会话一个 RoyalTerminal TerminalControl + 真实 PTY）。本服务经
/// AttachView/DetachView 拿视图引用，转发 Create/Close/SendInput/RunAsync。RunAsync 的哨兵扫描在本服务内
/// （统一一处）：向控件 SendInput 命令 + OSC 8331 退出码哨兵，订阅 TerminalControl.DataReceived 扫原始字节
/// 命中哨兵拿退出码。⑩ 工作台加载即建全部 panel 视图（IsVisible 切换不销毁）→ 视图常驻，PTY 跨 nav 不断。
/// </remarks>
public sealed class TerminalService : ITerminalService, IAsyncDisposable
{
    private readonly Dictionary<Guid, SessionInfo> _sessions = new();
    private readonly object _lock = new();
    private TerminalView? _view;
    private Guid? _activeId;

    /// <summary>RunAsync 捕获缓冲上限（2MB）：防无限输出命令在等哨兵期间内存无界；超限时丢已扫描头部保留最近 1MB。</summary>
    private const int MaxCaptureBytes = 2 * 1024 * 1024;

    public Guid? ActiveSessionId { get { lock (_lock) return _activeId; } }

    public event Action? SessionsChanged;
    public event Action<Guid>? ActiveSessionChanged;
    public event Action<Guid>? SessionExited;

    // —— 视图附着（TerminalView.OnLoaded 调）——
    public void AttachView(TerminalView view)
    {
        _view = view;
        // 视图附着前可能已建会话（agent 工具先于用户打开终端 tab），补建控件 + 激活。
        List<SessionInfo> snapshot;
        Guid? active;
        lock (_lock) { snapshot = _sessions.Values.ToList(); active = _activeId; }
        foreach (var s in snapshot)
            view.CreateSessionControl(s);
        if (active is { } aid)
            view.Activate(aid);
    }

    public void DetachView(TerminalView view)
    {
        if (ReferenceEquals(_view, view))
        {
            view.DisposeAllControls();
            _view = null;
        }
    }

    /// <summary>app 退出时容器 DisposeAsync 调此：杀所有仍在跑的 PTY 子进程，防退出后 shell 孤儿。
    /// PTY 控件归视图所有，正常退出经视觉树分离（DetachView→DisposeAllControls）已清；此处兜底 dispose
    /// 先于树分离或视图仍附着的情形——经视图 StopAllPtys() 只动进程句柄（线程无关），不触视觉树 Remove
    /// （须 UI 线程），故 dispose 跑非 UI 线程也安全。</summary>
    public ValueTask DisposeAsync()
    {
        TerminalView? view;
        lock (_lock) { view = _view; }
        try { view?.StopAllPtys(); }
        catch (Exception ex) { Trace.WriteLine($"terminal dispose stop-pty failed: {ex}"); }
        lock (_lock) { _sessions.Clear(); _activeId = null; }
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
        _view?.CreateSessionControl(info);
        _view?.Activate(info.Id);
        SessionsChanged?.Invoke();
        ActiveSessionChanged?.Invoke(info.Id);
        return info;
    }

    public void CloseSession(Guid sessionId)
    {
        _view?.RemoveControl(sessionId);
        bool removed;
        Guid? newActive = null;
        lock (_lock)
        {
            removed = _sessions.Remove(sessionId);
            if (removed && _activeId == sessionId)
            {
                // 没有剩余会话时显式置 null（LastOrDefault() 会给 Guid.Empty——ActiveSessionId 误判「还有会话」）。
                _activeId = _sessions.Count > 0 ? _sessions.Keys.Last() : null;
                newActive = _activeId;
            }
        }
        if (removed)
        {
            if (newActive is { } aid) { _view?.Activate(aid); ActiveSessionChanged?.Invoke(aid); }
            SessionsChanged?.Invoke();
            // 关掉最后一个会话 → 自动补一个全新会话：终端面板恒有活终端（无空态死区——
            // 标签条 [+]/空态浮层均已按用户心智删除，无其它新建入口；cwd 走 shell 默认家目录）。
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
        _view?.Activate(sessionId);
        ActiveSessionChanged?.Invoke(sessionId);
    }

    public IReadOnlyList<ITerminalSession> GetSessions()
    {
        lock (_lock) return _sessions.Values.ToList();
    }

    public void SendInput(Guid sessionId, string text) => _view?.SendInput(sessionId, text);

    public async Task<TerminalRunResult> RunAsync(Guid sessionId, string command, int timeoutMs, CancellationToken ct)
    {
        var view = _view;
        var ctrl = view?.GetControl(sessionId);
        if (ctrl is null || !ctrl.HasPty)
            return new TerminalRunResult { Error = "终端会话不存在或尚未启动 shell（打开「终端」tab 后重试）" };

        // 哨兵：OSC 8331 + 退出码。shell 收到的命令文本含 printf '\x1b]8331;%d\x07'（字面 backslash-x，
        // 非真实 ESC），printf 输出真实 ESC/BEL 字节——回显里无真实 0x1b，扫描不误命中。
        var fullCmd = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? $"{command}; Write-Host -NoNewline \"$([char]27)]8331;$LASTEXITCODE$([char]7)\""
            : $"{command}; printf '\\x1b]8331;%d\\x07' \"$?\"";

        var capture = new List<byte>();
        var captureLock = new object();
        var done = new TaskCompletionSource<int?>(TaskCreationOptions.RunContinuationsAsynchronously);
        int scanPos = 0;

        void OnData(object? s, RoyalTerminal.Avalonia.Controls.TerminalDataEventArgs e)
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
                // capture 封顶（防 yes/无限日志类命令在等哨兵期间内存无界）：超 2MB 时丢弃
                // 已扫描确认无哨兵的头部（scanPos 之前的字节不可能再命中），保留最近 1MB。
                // 哨兵在命令末尾输出，丢头不影响检测；上层 OutputText 另有 6000 字符截断。
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
                        // scanPos 被卡在 0（病态流：开头是永不完结的部分哨兵）——硬丢到 1MB 保底。
                        capture.RemoveRange(0, capture.Count - (MaxCaptureBytes / 2));
                        scanPos = 0;
                    }
                }
            }
        }

        ctrl.DataReceived += OnData;
        try
        {
            ctrl.SendInput(fullCmd + "\n");
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
            // 截到哨兵前（哨兵字节之后无内容，但保守起见截到首个 ESC]8331;）
            var text = StripAnsi(Encoding.UTF8.GetString(bytes));
            // 丢弃首行（shell 回显的命令行——单行命令的回显占一行；多行命令的回显可能多行，但首行截断已大幅降噪）。
            var nl = text.IndexOf('\n');
            if (nl >= 0 && nl < text.Length) text = text[(nl + 1)..];
            return text.Trim();
        }
    }

    // —— 视图回呼：控件运行时事件 → 更新元数据 + 通知 VM ——
    internal void NotifyTitleChanged(Guid id, string title)
    {
        lock (_lock)
        {
            if (_sessions.TryGetValue(id, out var s))
                s = s with { Title = title };
            else return;
            _sessions[id] = s;
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

    private static string DefaultTitle() => RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "powershell" : "shell";

    /// <summary>在 capture 的 scanPos.. 之间扫描哨兵 ESC ] 8331 ; &lt;digits&gt; BEL。命中返回退出码并推进 scanPos 过哨兵。</summary>
    private static bool TryFindSentinel(List<byte> buf, ref int scanPos, out int exitCode)
    {
        exitCode = 0;
        // 哨兵前缀：ESC(0x1B) ']' '8' '3' '3' '1' ';'
        for (int i = scanPos; i < buf.Count - 1; i++)
        {
            if (buf[i] != 0x1B || buf[i + 1] != (byte)']') continue;
            // 匹配 ]8331;
            if (i + 6 >= buf.Count) break; // 不够长，等更多字节
            if (buf[i + 2] != (byte)'8' || buf[i + 3] != (byte)'3' || buf[i + 4] != (byte)'3' ||
                buf[i + 5] != (byte)'1' || buf[i + 6] != (byte)';')
            {
                // 别的 OSC（如标题），跳过这个 ESC——但仍可能其后跟 8331，保守只进 1
                continue;
            }
            // 读数字直到 BEL(0x07)
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
                // 哨兵未完整（BEL 未到）或异常中断；保留 scanPos 在 i 之前，等更多字节
                scanPos = i;
                return false;
            }
            if (digits.Length > 0 && int.TryParse(digits.ToString(), out exitCode))
            {
                scanPos = j + 1;
                return true;
            }
            // 数字解析失败，跳过
        }
        // 保留尾部一小段（哨兵前缀最长 7 字节 + 数字 12 + BEL）防漏
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
