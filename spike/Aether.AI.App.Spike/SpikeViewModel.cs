using System.Collections.ObjectModel;
using System.IO;
using Aether.Workbench.Theming;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Aether.AI.App.Spike;

/// <summary>
/// spike 页 VM：一次「发送」= ⑫ 全链路（Rule 大脑 → 工具 → 回填 → 终答断言），
/// 逐事件流进 Events（token/工具/进度全部留痕），另在页头展示 ⑩ 主题清单读取结果。
/// </summary>
public partial class SpikeViewModel : ObservableObject
{
    public ObservableCollection<string> Events { get; } = new();

    [ObservableProperty] private string _input = "查600000";
    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private string _result = "（未运行）";
    [ObservableProperty] private string _header = "";

    private readonly SpikeChat? _chat;

    public SpikeViewModel()
    {
        // 数据目录：桌面/移动各自映射 LocalApplicationData；拿不到就退 InMemory（纯托管路径）。
        string? db = null;
        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "aether-ai-app-spike");
            Directory.CreateDirectory(dir);
            db = Path.Combine(dir, "spike.db");
        }
        catch { /* 目录不可用 → InMemory */ }

        string? initError = null;
        try
        {
            _chat = SpikeChat.Create(db);
        }
        catch (Exception ex)
        {
            initError = ex.ToString();
        }

        // ⑩ 引擎在当前 TFM 可加载的证据：内置主题清单（M1 起真正接壳用主题）。
        try
        {
            var names = new List<string>();
            foreach (var (name, _) in ThemeLoader.BuiltInThemes) names.Add(name);
            Header = $"workbench 主题：{string.Join(" / ", names)}";
        }
        catch (Exception ex)
        {
            Header = $"workbench 主题读取失败：{ex.Message}";
        }

        if (initError is not null)
        {
            Add($"init-error {initError}");
        }
        else
        {
            Add($"ready    db={db ?? "(in-memory)"}，发送「查600000」验证 ⑫ 全链路");
        }
    }

    /// <summary>
    /// 启动即自动跑一轮（M0：移动端无点按注入手段，截图即证据；桌面顺带受益——开窗即见全链路）。
    /// 由 App.OnFrameworkInitializationCompleted 在 UI 线程发起。
    /// </summary>
    public void AutoSmoke() => Dispatcher.UIThread.Post(async () =>
    {
        await Task.Delay(800);   // 等首帧/布局完成，事件流可见
        if (!IsRunning) await SendAsync();
    }, DispatcherPriority.Background);

    [RelayCommand]
    private async Task SendAsync()
    {
        if (IsRunning || _chat is null || string.IsNullOrWhiteSpace(Input)) return;
        var message = Input.Trim();
        IsRunning = true;
        Result = "运行中…";
        Add($"user     {message}");
        try
        {
            var full = await Task.Run(() =>
                _chat.RunAsync(message, line => Dispatcher.UIThread.Post(() => Add(line))));

            Result = full == SpikeChat.ExpectedResult
                ? $"✅ {full}（BrainMatrix 同构断言通过）"
                : $"❌ 期望「{SpikeChat.ExpectedResult}」，实得「{full}」";
        }
        catch (Exception ex)
        {
            Result = $"❌ {ex.Message}";
            Add($"error    {ex}");
        }
        finally
        {
            IsRunning = false;
        }
    }

    private void Add(string line) => Events.Add(line);
}
