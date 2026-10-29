using System;
using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using Aether.Browser.AI;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace Aether.AI.App.Browser;

/// <summary>浏览器网络面板视图（DevTools/BurpSuite 式 HTTP 请求记录）。
/// 布局：工具栏/会话标签/过滤栏/请求列表(上) + GridSplitter(可拖) + 详情(下，Tab：概览/请求/响应)。
/// 详情体用只读 TextBox（JSON 美化，无语法高亮——⑮ 不带编辑器栈，要高亮随编辑器页批次再议）。
/// SelectedRequest 变（VM PropertyChanged）→ 刷新 4 个详情框；切详情 tab 也重读（兼容 body 异步回填）。
/// （自 monorepo port，2026-10-29；属桌面 head——移动形态不编译不注册。）</summary>
public partial class BrowserView : UserControl
{
    private BrowserViewModel? _vm;

    public BrowserView()
    {
        InitializeComponent();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_vm is not null) _vm.PropertyChanged -= OnVmPropertyChanged;
        _vm = DataContext as BrowserViewModel;
        if (_vm is not null) _vm.PropertyChanged += OnVmPropertyChanged;
        UpdateModeSegActive(_vm?.LaunchMode ?? BrowserLaunchMode.Launch);
        RefreshDetailBoxes();
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(BrowserViewModel.SelectedRequest))
            RefreshDetailBoxes();
    }

    /// <summary>按当前 SelectedRequest 刷新 4 个详情框（请求/响应头美化 JSON、请求/响应体原样、二进制显占位）。</summary>
    private void RefreshDetailBoxes()
    {
        var req = _vm?.SelectedRequest;
        if (req is null)
        {
            RequestHeadersBox.Text = "";
            RequestBodyBox.Text = "";
            ResponseHeadersBox.Text = "";
            ResponseBodyBox.Text = "";
            return;
        }
        RequestHeadersBox.Text = PrettifyJson(req.RequestHeaders) ?? "";
        RequestBodyBox.Text = req.RequestBody ?? "";
        ResponseHeadersBox.Text = PrettifyJson(req.ResponseHeaders) ?? "";
        ResponseBodyBox.Text = req.IsBinary
            ? $"[二进制响应体 {req.Size ?? 0} 字节，不存文本——用 browser_get_cookies + curl 重发获取]"
            : req.ResponseBody ?? "";
    }

    /// <summary>美化 JSON 串（缩进）；非 JSON 或解析失败原样返回。</summary>
    private static string? PrettifyJson(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return s;
        try { return JsonNode.Parse(s)?.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) ?? s; }
        catch { return s; }
    }

    // —— 地址栏 / 会话标签 ——

    private void OnAddressKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && DataContext is BrowserViewModel vm)
        {
            vm.NavigateCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void OnTabPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Control c && c.DataContext is BrowserSessionItem item && DataContext is BrowserViewModel vm)
            vm.SwitchSessionCommand.Execute(item);
    }

    private void OnCloseTab(object? sender, RoutedEventArgs e)
    {
        if (sender is Button b && b.DataContext is BrowserSessionItem item && DataContext is BrowserViewModel vm)
            vm.CloseSessionCommand.Execute(item);
    }

    // —— 状态段按钮组（fseg） ——

    private void OnStatusSeg(object? sender, RoutedEventArgs e)
    {
        if (sender is Button b && b.Tag is string tag && DataContext is BrowserViewModel vm)
        {
            vm.SetStatusFilterCommand.Execute(tag);
            // 更新 active 样式：遍历同级 Button，仅当前高亮。
            if (b.Parent is Panel panel)
                foreach (var c in panel.Children.OfType<Button>())
                    c.Classes.Set("active", ReferenceEquals(c, b));
        }
    }

    // —— 启动模式按钮组（宿主策略：写盘 + 应用为 service 默认，对下一次新建会话生效） ——

    private void OnModeSeg(object? sender, RoutedEventArgs e)
    {
        if (sender is Button b && b.Tag is string tag
            && Enum.TryParse<BrowserLaunchMode>(tag, out var mode)
            && DataContext is BrowserViewModel vm)
        {
            vm.SetLaunchModeCommand.Execute(mode);
            UpdateModeSegActive(mode);
        }
    }

    private void UpdateModeSegActive(BrowserLaunchMode mode)
    {
        foreach (var c in ModeSegPanel.Children.OfType<Button>())
            c.Classes.Set("active", c.Tag is string t && t == mode.ToString());
    }

    // —— 详情 Tab（dtab）切换：叠放 pane 切 IsVisible（不销毁，保留滚动/渲染状态） ——

    private void OnDetailTab(object? sender, RoutedEventArgs e)
    {
        if (sender is Button b && b.Tag is string tag)
        {
            SummaryPane.IsVisible = tag == "summary";
            RequestPane.IsVisible = tag == "request";
            ResponsePane.IsVisible = tag == "response";
            if (b.Parent is Panel panel)
                foreach (var c in panel.Children.OfType<Button>())
                    c.Classes.Set("active", ReferenceEquals(c, b));
            // 切 tab 时重读详情（兼容 body 异步回填后切到响应 tab 才刷新）。
            RefreshDetailBoxes();
        }
    }
}
