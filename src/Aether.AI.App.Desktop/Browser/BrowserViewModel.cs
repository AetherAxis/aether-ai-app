using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Aether.AI.App.Core;
using Aether.Browser.AI;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Aether.AI.App.Browser;

/// <summary>「浏览器」面板 ViewModel：标签条（会话）+ 请求列表（当前活跃会话的 HTTP 请求流）。
/// 元数据投影 + 订阅 <see cref="IBrowserService"/> 事件 diff 重建列表 + 命令转发 service（不持控件）。
/// 浏览器画面是 Playwright 独立 OS 窗口；本 VM/View 只展示请求列表。UI-agnostic（零 Avalonia）。
/// （自 monorepo Acme.Desktop.Mvvm port，2026-10-29；ctor 从 IServiceProvider 改为显式双参；
/// 接口/模型来自 ⑭ Aether.Browser.AI——本文件属桌面 head，移动形态不编译不注册。）</summary>
public partial class BrowserViewModel : ObservableObject
{
    private readonly IBrowserService _service;
    private readonly IUiDispatcher _dispatcher;
    private readonly BrowserSettingsStore _settings;

    /// <summary>当前启动模式（chip 初值来自 store；切换 = 写盘 + 应用 service 默认，对下一次新建会话生效）。</summary>
    [ObservableProperty] private BrowserLaunchMode _launchMode;

    /// <summary>会话标签条数据源。</summary>
    public ObservableCollection<BrowserSessionItem> Sessions { get; } = new();
    /// <summary>当前活跃会话的请求列表数据源。</summary>
    public ObservableCollection<NetworkRequestItem> Requests { get; } = new();

    [ObservableProperty] private BrowserSessionItem? _activeSession;
    [ObservableProperty] private NetworkRequestItem? _selectedRequest;
    /// <summary>地址栏输入（回车 Navigate；空时禁用）。</summary>
    [ObservableProperty] private string? _addressBar;

    /// <summary>过滤后的请求列表（列表控件绑此；全量 <see cref="Requests"/> 供过滤计算）。</summary>
    public ObservableCollection<NetworkRequestItem> FilteredRequests { get; } = new();
    /// <summary>URL 关键词搜索（忽略大小写，匹配 Url）。空=不过滤。</summary>
    [ObservableProperty] private string? _filterText;
    /// <summary>状态码段过滤：null/""=全部、"2xx"/"3xx"/"4xx"/"5xx"/"err"（err=失败或≥400）。</summary>
    [ObservableProperty] private string? _statusFilter;
    /// <summary>过滤后条数（过滤栏显示 {FilteredCount}/{TotalCount} 条）。</summary>
    [ObservableProperty] private int _filteredCount;
    /// <summary>当前会话请求总数。</summary>
    [ObservableProperty] private int _totalCount;

    public BrowserViewModel(IBrowserService service, IUiDispatcher dispatcher, BrowserSettingsStore settings)
    {
        _service = service;
        _dispatcher = dispatcher;
        _settings = settings;
        // 启动模式：chip 显示 store 值，并同步 service 默认（幂等——组合根载入时已应用过一次；
        // 这里兜底「组合根没接」的宿主/测试形态）。
        LaunchMode = settings.Settings.Mode;
        _service.DefaultLaunchMode = settings.Settings.Mode;
        _service.SessionsChanged += OnSessionsChanged;
        _service.ActiveSessionChanged += OnActiveSessionChanged;
        _service.RequestsChanged += OnRequestsChanged;
        _service.SessionClosed += OnSessionClosed;
        RebuildSessions();
    }

    /// <summary>切换启动模式（面板 chip）：写盘 + 应用为 service 默认——对下一次新建会话生效，运行中的会话不动。</summary>
    [RelayCommand]
    private void SetLaunchMode(BrowserLaunchMode mode)
    {
        LaunchMode = mode;
        _settings.Save(mode);
        _service.DefaultLaunchMode = mode;
    }

    // —— service 事件 → marshal UI 线程 → diff 重建 ——

    private void OnSessionsChanged() => _dispatcher.Post(RebuildSessions);
    private void OnActiveSessionChanged(Guid id) => _dispatcher.Post(() => { RebuildSessions(); RebuildRequests(); });
    private void OnRequestsChanged(Guid id)
    {
        // 仅当变更的是当前活跃会话时刷新请求列表（非活跃会话的请求累积在 service，切回时再显示）。
        if (_service.ActiveSessionId == id) _dispatcher.Post(RebuildRequests);
    }
    private void OnSessionClosed(Guid id) => _dispatcher.Post(() =>
    {
        // 标签 IsAlive=false（标签保留供用户关闭）。
        var item = Sessions.FirstOrDefault(s => s.Id == id);
        if (item is not null) item.IsAlive = false;
    });

    /// <summary>从 service 快照 diff 重建标签条（删消失/加新/同步 Title/Url/IsAlive/IsActive/RequestCount）。</summary>
    private void RebuildSessions()
    {
        var snapshot = _service.GetSessions();
        // 删消失项。
        for (int i = Sessions.Count - 1; i >= 0; i--)
            if (snapshot.All(s => s.Id != Sessions[i].Id)) Sessions.RemoveAt(i);
        // 加新项 + 同步现有项。
        var activeId = _service.ActiveSessionId;
        foreach (var s in snapshot)
        {
            var item = Sessions.FirstOrDefault(x => x.Id == s.Id);
            if (item is null)
            {
                item = new BrowserSessionItem(s.Id, s.Title, s.Url, s.IsAlive);
                Sessions.Add(item);
            }
            item.Title = s.Title;
            item.Url = s.Url;
            item.IsAlive = s.IsAlive;
            item.IsActive = s.Id == activeId;
            item.RequestCount = _service.GetRequests(s.Id).Count;
        }
        ActiveSession = Sessions.FirstOrDefault(s => s.Id == activeId);
        NavigateCommand.NotifyCanExecuteChanged();
    }

    /// <summary>从 service 快照 diff 重建当前活跃会话的请求列表（加新项；请求不删故无需移除）。
    /// NetworkRequestItem 是 service 持有的同一对象——其 Status/Duration 等 ObservableProperty 由 service 在 Response 事件改，UI 自动刷新；
    /// VM 只负责把新请求加进集合。</summary>
    private void RebuildRequests()
    {
        var activeId = _service.ActiveSessionId;
        if (activeId is null) { Requests.Clear(); return; }
        var snapshot = _service.GetRequests(activeId.Value);
        // diff：集合里没有的（新请求）追加；已有的不动（service 已改其字段）。
        var existingIds = new HashSet<Guid>(Requests.Select(r => r.Id));
        foreach (var r in snapshot)
            if (existingIds.Add(r.Id)) Requests.Add(r);
        // 清除集合里有但快照没有的（ClearRequests 后）。
        var snapshotIds = snapshot.Select(r => r.Id).ToHashSet();
        for (int i = Requests.Count - 1; i >= 0; i--)
            if (!snapshotIds.Contains(Requests[i].Id)) Requests.RemoveAt(i);
        TotalCount = Requests.Count;
        ApplyFilter();
        ClearRequestsCommand.NotifyCanExecuteChanged();
    }

    /// <summary>按当前过滤条件重算 <see cref="FilteredRequests"/>（叠加在 RebuildRequests 后；过滤条件变更也调）。</summary>
    private void ApplyFilter()
    {
        FilteredRequests.Clear();
        foreach (var r in Requests)
            if (PassesFilter(r)) FilteredRequests.Add(r);
        FilteredCount = FilteredRequests.Count;
    }

    private bool PassesFilter(NetworkRequestItem r)
    {
        if (!string.IsNullOrEmpty(FilterText) && !r.Url.Contains(FilterText, StringComparison.OrdinalIgnoreCase))
            return false;
        if (!string.IsNullOrEmpty(StatusFilter))
        {
            var s = r.Status ?? 0;
            var pass = StatusFilter switch
            {
                "2xx" => s >= 200 && s < 300,
                "3xx" => s >= 300 && s < 400,
                "4xx" => s >= 400 && s < 500,
                "5xx" => s >= 500 && s < 600,
                "err" => r.IsError || s >= 400,
                _ => true,
            };
            if (!pass) return false;
        }
        return true;
    }

    partial void OnFilterTextChanged(string? value) => ApplyFilter();
    partial void OnStatusFilterChanged(string? value) => ApplyFilter();

    /// <summary>状态段 Button 组点击（CommandParameter=段名；空=全部）。</summary>
    [RelayCommand]
    private void SetStatusFilter(string? s) => StatusFilter = string.IsNullOrEmpty(s) ? null : s;

    // —— 命令（纯转发 service，不持控件）——

    /// <summary>地址栏回车 / 「打开」按钮：有活跃会话则导航，无则建新会话。</summary>
    [RelayCommand(CanExecute = nameof(CanNavigate))]
    private void Navigate()
    {
        var url = AddressBar;
        if (string.IsNullOrWhiteSpace(url)) return;
        if (!Uri.TryCreate(url, UriKind.Absolute, out _) && !url.StartsWith("about:", StringComparison.Ordinal))
        {
            // 非完整 URL 自动补 https://。
            url = "https://" + url;
        }
        if (_service.ActiveSessionId is { } id) _service.NavigateAsync(id, url).SafeFireAndForget();
        else _service.OpenSession(url);
    }
    private bool CanNavigate() => !string.IsNullOrWhiteSpace(AddressBar);

    /// <summary>新建空白会话（用户随后在地址栏输入 URL 导航）。</summary>
    [RelayCommand]
    private void NewSession() => _service.OpenSession("about:blank");

    /// <summary>关闭会话（关浏览器 + 移除标签）。</summary>
    [RelayCommand]
    private void CloseSession(BrowserSessionItem? item)
    {
        if (item is not null) _service.CloseSession(item.Id);
    }

    /// <summary>切换活跃会话标签。</summary>
    [RelayCommand]
    private void SwitchSession(BrowserSessionItem? item)
    {
        if (item is not null) _service.SetActive(item.Id);
    }

    /// <summary>清当前会话请求记录。</summary>
    [RelayCommand(CanExecute = nameof(CanClearRequests))]
    private void ClearRequests()
    {
        if (_service.ActiveSessionId is { } id) _service.ClearRequests(id);
    }
    private bool CanClearRequests() => Requests.Count > 0;

    partial void OnAddressBarChanged(string? value) => NavigateCommand.NotifyCanExecuteChanged();
}
