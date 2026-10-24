using System.Collections.Specialized;
using System.Runtime.InteropServices;
using Avalonia.Controls;

namespace Aether.AI.App.Spike;

/// <summary>
/// spike 视图 code-behind：safe-area / 平台信息探测（M0 清单 #3），事件流自动滚底。
/// 根控件用 Avalonia 12 的 <see cref="ContentPage"/>——自带 SafeAreaPadding 属性与自动避让
/// （ContentPage.AutomaticallyApplySafeAreaPadding），即 M1 移动壳的同款原语。
/// </summary>
public partial class MainView : ContentPage
{
    public MainView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => HookTranscript();
        AttachedToVisualTree += (_, _) => UpdatePlatformInfo();
        LayoutUpdated += (_, _) => UpdateSafeArea();
    }

    private void HookTranscript()
    {
        if (DataContext is SpikeViewModel vm)
        {
            vm.Events.CollectionChanged -= OnEventsChanged;
            vm.Events.CollectionChanged += OnEventsChanged;
        }
    }

    private void OnEventsChanged(object? sender, NotifyCollectionChangedEventArgs e)
        => Transcript?.ScrollToEnd();

    private void UpdatePlatformInfo()
    {
        if (PlatformText is null) return;
        PlatformText.Text = $"platform: {RuntimeInformation.FrameworkDescription} · {Environment.OSVersion}";
    }

    /// <summary>读本页 SafeAreaPadding 实测值（Page 原生属性——编译即证明 API 在位；
    /// 移动端应报出刘海/Home Indicator 数值，桌面为 0 属预期——M0 清单 #3）。</summary>
    private void UpdateSafeArea()
    {
        if (SafeAreaText is null) return;
        var p = SafeAreaPadding;
        var text = $"safe-area: L{p.Left:F0} T{p.Top:F0} R{p.Right:F0} B{p.Bottom:F0}"
                 + (OperatingSystem.IsIOS() || OperatingSystem.IsAndroid() ? "" : "（桌面为 0 属预期）");
        if (SafeAreaText.Text != text) SafeAreaText.Text = text;
    }
}
