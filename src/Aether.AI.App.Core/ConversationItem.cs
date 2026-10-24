using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Aether.AI.App.Core;

/// <summary>最近会话列表行（侧边栏/历史下拉）。</summary>
public sealed partial class ConversationItem : ObservableObject
{
    public Guid Id { get; init; }

    [ObservableProperty] private string _title = string.Empty;
    [ObservableProperty] private string _preview = string.Empty;
    [ObservableProperty] private string _displayTime = string.Empty;
    [ObservableProperty] private string _agentName = string.Empty;

    /// <summary>绑定的 agent Id（字符串 Guid）。</summary>
    public string AgentId { get; init; } = string.Empty;

    /// <summary>是否是当前激活会话（高亮）。</summary>
    [ObservableProperty] private bool _isCurrent;

    /// <summary>该会话是否有 agent 在后台/前台运行（角标 ⏳）。由 VM 在 runtime.IsRunning 变更时推送。</summary>
    [ObservableProperty] private bool _isRunning;

    /// <summary>打开此会话的命令（由 VM 注入）。直接绑在行上，避免 Flyout 子树里 AncestorType 取不到 Window。</summary>
    public ICommand? ResumeCommand { get; set; }

    /// <summary>删除此会话的命令（由 VM 注入），同上。</summary>
    public ICommand? DeleteCommand { get; set; }
}
