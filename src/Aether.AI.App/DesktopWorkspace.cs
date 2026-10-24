using Aether.Workbench.Abstractions;

namespace Aether.AI.App;

/// <summary>App 的 panel key 目录（App 自备——⑩ 引擎不携带任何业务键）。</summary>
public static class PanelKeys
{
    public const string Chat = "chat";
    public const string Agents = "agents";
    public const string Settings = "settings";
}

/// <summary>App 的默认工作台：聊天 / Agents / 设置 三 tab。M1 = chat 实装，
/// agents（目录+编辑器）M2、settings（LLM 档案/主题）M2——先占位保证导航形态稳定。</summary>
public static class DesktopWorkspace
{
    public static WorkspaceDefinition Definition { get; } = new(
        Id: "main",
        Title: "Aether AI",
        Panels: new List<PanelDescriptor>
        {
            new(PanelKeys.Chat, PanelKeys.Chat, "聊天", "chat"),
            new(PanelKeys.Agents, PanelKeys.Agents, "Agents", "agents"),
            new(PanelKeys.Settings, PanelKeys.Settings, "设置", "settings"),
        },
        Layout: new LayoutTabbed(new[] { PanelKeys.Chat, PanelKeys.Agents, PanelKeys.Settings }));
}
