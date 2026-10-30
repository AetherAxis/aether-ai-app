using System;
using Aether.Terminal.AI;
using Avalonia.Controls;
using Avalonia.Media;
using RoyalTerminal.Avalonia.Controls;

namespace Aether.AI.App.Terminal;

/// <summary>
/// 集成终端视图：标签条（绑 TerminalViewModel.Sessions）+ 控件宿主（ContentHost）。
/// **纯投影（2026-10-29 重构）**：控件/PTY 归 <see cref="TerminalService"/> 所有——本视图只负责
/// 附着时把控件挂进 ContentHost、分离时摘下来；**视图可随意销毁**（销毁不杀 PTY，重建即原样续看）。
/// 主题随 ActualThemeVariant 重染（RoyalTerminal 正规换肤走 TerminalTheme，见 ApplyTheme）。
/// </summary>
public partial class TerminalView : UserControl
{
    private readonly TerminalService _service;
    private bool _initialized;

    public TerminalView(TerminalService service)
    {
        _service = service;
        InitializeComponent();
        this.Loaded += OnLoaded;
        this.ActualThemeVariantChanged += (_, _) => ApplyThemeToAll();
    }

    private void OnLoaded(object? sender, EventArgs e)
    {
        if (_initialized) return;
        _initialized = true;
        _service.OnViewAttached(this);
        // 首次打开若无会话，自动建一个（cwd 走 shell 家目录）。
        if (_service.GetSessions().Count == 0)
            _service.CreateSession(null, null);
    }

    protected override void OnDetachedFromVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        // 只摘控件不杀 PTY——视图销毁后会话继续（服务层持有）
        _service.OnViewDetached(this);
    }

    /// <summary>控件宿主面板（服务挂/摘控件用）。</summary>
    internal Grid Host => ContentHost;

    // —— 主题 ——

    internal void ApplyThemeToAll()
    {
        foreach (var ctrl in _service.GetAllControls())
            ApplyTheme(ctrl);
    }

    private void ApplyTheme(TerminalControl ctrl)
    {
        var dark = ActualThemeVariant == Avalonia.Styling.ThemeVariant.Dark;
        // ★ RoyalTerminal 正规换肤走 TerminalTheme（record，经 ApplyThemeToRenderer 整体重刷）。
        // 色值 uint = AARRGGBB（Avalonia Color 转 uint）。
        uint argb(Color c) => ((uint)c.A << 24) | ((uint)c.R << 16) | ((uint)c.G << 8) | c.B;
        ctrl.Theme = ctrl.Theme
            .WithDefaultBackground(dark ? argb(Color.Parse("#1E1E1E")) : argb(Color.Parse("#FFFFFF")))
            .WithDefaultForeground(dark ? argb(Color.Parse("#D4D4D4")) : argb(Color.Parse("#1E1E1E")));
    }
}
