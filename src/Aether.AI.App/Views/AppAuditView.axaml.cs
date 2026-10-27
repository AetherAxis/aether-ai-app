using Avalonia.Controls;

namespace Aether.AI.App.Views;

/// <summary>
/// 应用审查页（nav rail 一页）：Chrome 网页应用名 → 候选站点 → 博彩与否的结论。
/// 视图只做展示，驱动全在 <see cref="ViewModels.AppAuditViewModel"/>（逐条「网搜/判定」与「全部开始」共用一条管线）。
/// </summary>
public partial class AppAuditView : UserControl
{
    public AppAuditView() => InitializeComponent();
}
