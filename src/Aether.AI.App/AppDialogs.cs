using Aether.Workbench;
using Aether.Workbench.Theming;
using Avalonia.Controls;
using Avalonia.Styling;

namespace Aether.AI.App;

/// <summary>
/// 模态窗的主题接驳。
/// <para>⑩ 的主题 token 是**逐元素** overlay（<see cref="ThemeLoader.Apply"/>）：<c>WorkbenchWindow</c>
/// 自己叠了，但 <c>new Window { Content = ... }</c> 出来的模态窗没有——不叠的话
/// <c>{DynamicResource CardBgBrush / SurfaceBrush / BorderBrush / AccentBrush}</c> 全部解析失败：
/// 卡片变纯白、primary 按钮只剩白字、outline 按钮没有描边，
/// 「新建 / 删除只有鼠标移上去才看得见」就是这么来的（hover 才会用到 Fluent 自己的灰底）。
/// 开关窗时叠加，并跟随 <see cref="IThemeHost.ThemeChanged"/> 即时重染。</para>
/// </summary>
public static class AppDialogs
{
    public static void ApplyTheme(Window dialog)
    {
        if (WorkbenchServices.GetService<WorkbenchShellViewModel>() is not IThemeHost host)
        {
            // 移动形态没有工作台壳（主题直接叠在根视图上，见 App.BuildMobileRoot）——无需处理。
            return;
        }

        void Sync()
        {
            ThemeLoader.Apply(dialog, host.CustomThemeJson);
            dialog.RequestedThemeVariant = host.IsDark ? ThemeVariant.Dark : ThemeVariant.Light;
        }

        Sync();
        EventHandler handler = (_, _) => Sync();
        host.ThemeChanged += handler;
        dialog.Closed += (_, _) => host.ThemeChanged -= handler;
    }
}
