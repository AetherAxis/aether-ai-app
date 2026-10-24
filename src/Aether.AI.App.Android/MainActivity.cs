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
        // 模拟器自动演示验收：am start --es demo 1 → App.LaunchArguments（须在 Avalonia 初始化前设）。
        if (Intent?.HasExtra("demo") == true)
            App.LaunchArguments = new[] { "--demo" };
        base.OnCreate(savedInstanceState);
    }
}
