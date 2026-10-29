using Aether.Browser.AI;

namespace Aether.AI.App.Tests;

/// <summary>IBrowserService（⑭ Aether.Browser.AI）测试替身：记录调用、按需抛事件——
/// ⑮ 侧 VM 测试用，不起 Playwright（工具层同款 fake 在 ⑭ 自己的测试仓）。</summary>
internal sealed class FakeBrowserService : IBrowserService
{
    public sealed class FakeSession : IBrowserSession
    {
        public Guid Id { get; init; }
        public string? Url { get; set; }
        public string Title { get; set; } = "fake";
        public bool IsAlive { get; set; } = true;
    }

    public List<FakeSession> Sessions { get; } = new();
    public FakeSession? Active;
    /// <summary>会话 id → 请求列表（GetRequests 的数据源）。</summary>
    public Dictionary<Guid, List<NetworkRequestItem>> RequestsMap { get; } = new();
    /// <summary>NavigateAsync 的调用记录（会话 id, url）。</summary>
    public List<(Guid SessionId, string Url)> Navigations { get; } = new();
    /// <summary>OpenSession 的 url 记录。</summary>
    public List<string> OpenedUrls { get; } = new();
    /// <summary>CloseSession 的调用记录。</summary>
    public List<Guid> Closed { get; } = new();

    public Guid? ActiveSessionId => Active?.Id;

    public BrowserLaunchMode DefaultLaunchMode { get; set; } = BrowserLaunchMode.Launch;

    public event Action? SessionsChanged;
    public event Action<Guid>? ActiveSessionChanged;
    public event Action<Guid>? RequestsChanged;
    public event Action<Guid>? SessionClosed;

    public IBrowserSession OpenSession(string url, string? channel = null, BrowserLaunchMode? mode = null)
    {
        var s = new FakeSession { Id = Guid.NewGuid(), Url = url };
        Sessions.Add(s);
        Active = s;
        OpenedUrls.Add(url);
        SessionsChanged?.Invoke();
        ActiveSessionChanged?.Invoke(s.Id);
        return s;
    }

    public Task<IBrowserSession> EnsureOpenAsync(string url, string? channel = null, BrowserLaunchMode? mode = null, CancellationToken cancellationToken = default)
    {
        if (Active is { IsAlive: true } a)
        {
            Navigations.Add((a.Id, url));
            a.Url = url;
            return Task.FromResult<IBrowserSession>(a);
        }
        return Task.FromResult(OpenSession(url, channel, mode));
    }

    public void CloseSession(Guid sessionId)
    {
        var s = Sessions.FirstOrDefault(x => x.Id == sessionId);
        if (s is null) return;
        Sessions.Remove(s);
        if (Active?.Id == sessionId) Active = Sessions.LastOrDefault();
        Closed.Add(sessionId);
        SessionClosed?.Invoke(sessionId);
        SessionsChanged?.Invoke();
    }

    public void SetActive(Guid sessionId)
    {
        Active = Sessions.FirstOrDefault(x => x.Id == sessionId);
        ActiveSessionChanged?.Invoke(sessionId);
        SessionsChanged?.Invoke();
    }

    public IReadOnlyList<IBrowserSession> GetSessions() => Sessions.ToList();

    public Task NavigateAsync(Guid sessionId, string url)
    {
        Navigations.Add((sessionId, url));
        return Task.CompletedTask;
    }

    public IReadOnlyList<NetworkRequestItem> GetRequests(Guid sessionId)
        => RequestsMap.TryGetValue(sessionId, out var list) ? list.ToList() : Array.Empty<NetworkRequestItem>();

    public void ClearRequests(Guid sessionId) => RequestsMap.Remove(sessionId);

    public Task<BrowserCookieResult> GetCookiesAsync(Guid sessionId, List<string>? cookieNames = null, CancellationToken cancellationToken = default)
        => Task.FromResult(new BrowserCookieResult { Success = false, Error = "fake: 未配置" });

    public Task<BrowserActionResult> ClickAsync(Guid sessionId, string selector, CancellationToken cancellationToken = default)
        => Task.FromResult(new BrowserActionResult { Success = false, Error = "fake: 未实现" });

    public Task<BrowserActionResult> TypeAsync(Guid sessionId, string selector, string text, bool pressEnter = false, CancellationToken cancellationToken = default)
        => Task.FromResult(new BrowserActionResult { Success = false, Error = "fake: 未实现" });

    public Task<BrowserPageContent> ReadPageAsync(Guid sessionId, CancellationToken cancellationToken = default)
        => Task.FromResult(new BrowserPageContent { Success = false, Error = "fake: 未实现" });

    // —— 测试驱动口 ——

    public void RaiseRequests(Guid sessionId) => RequestsChanged?.Invoke(sessionId);
    public void RaiseSessions() => SessionsChanged?.Invoke();
}
