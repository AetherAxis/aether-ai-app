using Avalonia.Controls;

namespace Aether.AI.App.Views;

/// <summary>
/// LLM 连接档案管理面（DataContext = <see cref="ViewModels.SettingsViewModel"/> 单例）。
/// <para>唯一的 LLM 配置入口：桌面由聊天输入卡的「模型 ⚙」以模态窗宿它，移动端由 ChatView 内嵌浮层宿它——
/// 一份 XAML 两端共用，不再和「设置页（外观）」各维护一份表单。</para>
/// </summary>
public partial class LlmProfilesView : UserControl
{
    public LlmProfilesView()
    {
        InitializeComponent();
    }
}
