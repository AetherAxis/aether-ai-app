using Android.App;
using Android.Content.PM;
using Avalonia;
using Avalonia.Android;

namespace Aether.AI.App.Android;

[Activity(
    Label = "Aether AI",
    Theme = "@style/MyTheme.NoActionBar",
    Icon = "@drawable/icon",
    MainLauncher = true,
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.UiMode)]
public class MainActivity : AvaloniaMainActivity
{
    protected override void OnCreate(global::Android.OS.Bundle? savedInstanceState)
    {
        // 显式声明移动宿主：共享组合根据此只注册手机可执行的能力（不注册命令工具、不设桌面钱包钩子）。
        App.HeadHost = Aether.AI.Core.Agents.HostCapability.MobileOnly;
        // 模拟器自动演示验收：am start --es demo 1 → App.LaunchArguments（须在 Avalonia 初始化前设）。
        if (Intent?.HasExtra("demo") == true)
            App.LaunchArguments = new[] { "--demo" };
        base.OnCreate(savedInstanceState);
    }
}
