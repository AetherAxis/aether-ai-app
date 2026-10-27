using Avalonia.Controls;

namespace Aether.AI.App.Views;

/// <summary>
/// 宿主能力面（技能包 / 连接器 / 链上钱包）——**一份控件，两端同宿**：
/// 桌面挂在设置页（rail 左下角齿轮），移动挂在「模型设置」浮层的能力页签。
/// 数据来自 <see cref="ViewModels.SettingsViewModel"/>（单例，两处共享同一份状态）。
/// </summary>
public partial class CapabilitiesView : UserControl
{
    public CapabilitiesView() => InitializeComponent();
}
