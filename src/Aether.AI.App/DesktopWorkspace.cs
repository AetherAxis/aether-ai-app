using Aether.Workbench.Abstractions;

namespace Aether.AI.App;

/// <summary>App 的 panel key 目录（App 自备——⑩ 引擎不携带任何业务键）。</summary>
public static class PanelKeys
{
    public const string Chat = "chat";
    public const string AppAudit = "app-audit";
    public const string Browser = "browser";
    public const string Settings = "settings";
}

/// <summary>
/// App 的默认工作台：nav rail = 「聊天」+「应用审查」+「浏览器」；设置走 rail 底部齿轮入口
/// （<see cref="WorkspaceDefinition.FooterPanelKey"/>，承载主题/重置布局）。
/// <para>2026-10-26：**Agents 不再是 nav 页**——自定义大脑与「模型档案」同款范式：
/// 聊天顶部 agent 下拉末项「管理 agent…」→ 模态窗（AgentsView）。配了自定义 agent 的用户
/// 不需要为配置切页，配置与使用都在聊天这一处入口。</para>
/// <para>2026-10-29：**浏览器**面板（Playwright headed 会话的网络观测视图）——视图/服务/工具全在
/// 桌面专属切片 Aether.AI.App.Browser（移动 head 不引 ⇒ Playwright 不进移动闭包）；
/// 其视图经桌面 head 注册的 BrowserViewContributor 贡献，移动形态不渲染工作台、天然不受影响。</para>
/// </summary>
public static class DesktopWorkspace
{
    public static WorkspaceDefinition Definition { get; } = new(
        Id: "main",
        Title: "Aether AI",
        Panels: new List<PanelDescriptor>
        {
            new(PanelKeys.Chat, PanelKeys.Chat, "聊天", "chat"),
            // 应用审查：Chrome 网页应用名 → 反查站点 → 判定博彩与否（独立 nav 页，桌面/数据两端同源）
            new(PanelKeys.AppAudit, PanelKeys.AppAudit, "应用审查", "activity"),
            // 浏览器：Playwright headed 独立窗口的 HTTP 请求流观测 + agent 工具入口（桌面专属）
            new(PanelKeys.Browser, PanelKeys.Browser, "浏览器", "browser"),
            new(PanelKeys.Settings, PanelKeys.Settings, "设置", "settings"),
        },
        Layout: new LayoutTabbed(new[] { PanelKeys.Chat, PanelKeys.AppAudit, PanelKeys.Browser, PanelKeys.Settings }),
        FooterPanelKey: PanelKeys.Settings);
}
