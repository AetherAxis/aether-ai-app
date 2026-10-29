using System;
using System.Collections.ObjectModel;
using System.Linq;
using Aether.AI.App.Core;
using Aether.Terminal.AI;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Aether.AI.App.Terminal;

/// <summary>
/// 「终端」页 ViewModel：多标签会话元数据投影 + 新建/关闭/切换命令面。
/// 真实 PTY/控件生命周期在 <see cref="ITerminalService"/>（⑮ 桌面 head 的 impl）——本 VM 仅持元数据投影，UI 无关。
/// （自 monorepo Acme.Desktop.Mvvm port，2026-10-29；差异：去 ICurrentProjectDirectoryProvider——
/// ⑮ 无「文档页项目目录」概念，新建会话 cwd 走 shell 家目录。）
/// </summary>
public partial class TerminalViewModel : ObservableObject
{
    private readonly ITerminalService _service;
    private readonly IUiDispatcher _dispatcher;

    /// <summary>标签条数据源（绑 TerminalView 标签 ItemsControl）。</summary>
    public ObservableCollection<TerminalSessionItem> Sessions { get; } = new();

    [ObservableProperty] private TerminalSessionItem? _activeSession;

    public TerminalViewModel(ITerminalService service, IUiDispatcher dispatcher)
    {
        _service = service;
        _dispatcher = dispatcher;
        _service.SessionsChanged += OnSessionsChanged;
        _service.ActiveSessionChanged += OnActiveSessionChanged;
        _service.SessionExited += OnSessionExited;
        RebuildSessions();
    }

    private void OnSessionsChanged() => _dispatcher.Post(RebuildSessions);

    private void OnActiveSessionChanged(Guid id) =>
        _dispatcher.Post(() =>
        {
            ActiveSession = Sessions.FirstOrDefault(s => s.Id == id);
            foreach (var s in Sessions)
                s.IsActive = s.Id == id;
        });

    private void OnSessionExited(Guid id) =>
        _dispatcher.Post(() =>
        {
            var s = Sessions.FirstOrDefault(x => x.Id == id);
            if (s is not null) s.IsRunning = false;
        });

    /// <summary>从 service 元数据快照重建标签列表（diff：增删 + 同步标题/运行态），保留选中。</summary>
    private void RebuildSessions()
    {
        var snapshot = _service.GetSessions();
        for (int i = Sessions.Count - 1; i >= 0; i--)
            if (!snapshot.Any(s => s.Id == Sessions[i].Id))
                Sessions.RemoveAt(i);

        foreach (var s in snapshot)
        {
            var existing = Sessions.FirstOrDefault(x => x.Id == s.Id);
            if (existing is null)
                Sessions.Add(new TerminalSessionItem(s.Id, s.Cwd, s.Title, s.IsRunning));
            else
            {
                existing.Title = s.Title;
                existing.IsRunning = s.IsRunning;
            }
        }

        if (ActiveSession is null || !Sessions.Contains(ActiveSession))
            ActiveSession = Sessions.FirstOrDefault(s => s.Id == _service.ActiveSessionId);
        foreach (var s in Sessions)
            s.IsActive = s.Id == _service.ActiveSessionId;
    }

    [RelayCommand]
    private void NewSession() => _service.CreateSession(null, null);

    [RelayCommand]
    private void CloseSession(TerminalSessionItem? item)
    {
        if (item is not null) _service.CloseSession(item.Id);
    }

    [RelayCommand]
    private void SwitchSession(TerminalSessionItem? item)
    {
        if (item is not null) _service.SetActive(item.Id);
    }
}
