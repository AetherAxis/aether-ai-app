using Aether.Workbench.Abstractions;

namespace Aether.AI.App;

/// <summary>App 的 panel key 目录（App 自备——⑩ 引擎不携带任何业务键）。</summary>
public static class PanelKeys
{
    public const string Chat = "chat";
    public const string Settings = "settings";
}

/// <summary>
/// App 的默认工作台：nav rail 只剩「聊天」；设置走 rail 底部齿轮入口
/// （<see cref="WorkspaceDefinition.FooterPanelKey"/>，承载主题/重置布局）。
/// <para>2026-10-26：**Agents 不再是 nav 页**——自定义大脑与「模型档案」同款范式：
/// 聊天顶部 agent 下拉末项「管理 agent…」→ 模态窗（AgentsView）。配了自定义 agent 的用户
/// 不需要为配置切页，配置与使用都在聊天这一处入口。</para>
/// </summary>
public static class DesktopWorkspace
{
    public static WorkspaceDefinition Definition { get; } = new(
        Id: "main",
        Title: "Aether AI",
        Panels: new List<PanelDescriptor>
        {
            new(PanelKeys.Chat, PanelKeys.Chat, "聊天", "chat"),
            new(PanelKeys.Settings, PanelKeys.Settings, "设置", "settings"),
        },
        Layout: new LayoutTabbed(new[] { PanelKeys.Chat, PanelKeys.Settings }),
        FooterPanelKey: PanelKeys.Settings);
}
