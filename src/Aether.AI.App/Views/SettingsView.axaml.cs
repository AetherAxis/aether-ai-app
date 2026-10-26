using Avalonia.Controls;

namespace Aether.AI.App.Views;

/// <summary>
/// 设置页（⑩ 工作台底部齿轮直达的那一页）：外观 + 布局。
/// LLM 档案不在此——见 <see cref="LlmProfilesView"/>（聊天输入卡「模型 ⚙」）。
/// </summary>
public partial class SettingsView : UserControl
{
    public SettingsView()
    {
        InitializeComponent();
    }
}
