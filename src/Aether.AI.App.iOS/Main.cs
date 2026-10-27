using UIKit;

namespace Aether.AI.App.iOS;

public class Application
{
    // This is the main entry point of the application.
    static void Main(string[] args)
    {
        // 显式声明移动宿主：共享组合根据此只注册手机可执行的能力（不注册命令工具、不设桌面钱包钩子）。
        Aether.AI.App.App.HeadHost = Aether.AI.Core.Agents.HostCapability.MobileOnly;
        // 启动参数转存给 App（移动 lifetime 不暴露 Args；--demo = 模拟器自动演示验收）。
        Aether.AI.App.App.LaunchArguments = args ?? Array.Empty<string>();
        // if you want to use a different Application Delegate class from "AppDelegate"
        // you can specify it here.
        UIApplication.Main(args, null, typeof(AppDelegate));
    }
}
