using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace Aether.AI.App.Spike;

/// <summary>
/// 三形态分派（Avalonia 12 官方跨平台形态）：桌面 → MainWindow；Android → MainViewFactory；
/// iOS → MainView。spike 全形态共用同一 MainView + SpikeViewModel——这本身就是方案A 的验证内容。
/// </summary>
public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        SpikeViewModel? vm = null;
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            vm = new SpikeViewModel();
            desktop.MainWindow = new MainWindow
            {
                DataContext = vm,
            };
        }
        else if (ApplicationLifetime is IActivityApplicationLifetime activity)
        {
            vm = new SpikeViewModel();
            var captured = vm;
            activity.MainViewFactory = () => new MainView { DataContext = captured };
        }
        else if (ApplicationLifetime is ISingleViewApplicationLifetime singleView)
        {
            vm = new SpikeViewModel();
            singleView.MainView = new MainView
            {
                DataContext = vm,
            };
        }

        vm?.AutoSmoke();   // 启动即跑一轮全链路（M0 证据面）

        base.OnFrameworkInitializationCompleted();
    }
}
