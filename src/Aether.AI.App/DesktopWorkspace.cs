using Aether.Workbench.Abstractions;

namespace Aether.AI.App;

/// <summary>App 的 panel key 目录（App 自备——⑩ 引擎不携带任何业务键）。</summary>
public static class PanelKeys
{
    public const string Chat = "chat";
    public const string AppAudit = "app-audit";
    public const string Browser = "browser";
    public const string Terminal = "terminal";
    public const string Settings = "settings";
}

/// <summary>
/// App 的默认工作台：nav rail = 「聊天」+「应用审查」+「浏览器」+「终端」；设置走 rail 底部齿轮入口
/// （<see cref="WorkspaceDefinition.FooterPanelKey"/>，承载主题/重置布局）。
/// <para>2026-10-26：**Agents 不再是 nav 页**——自定义大脑与「模型档案」同款范式：
/// 聊天顶部 agent 下拉末项「管理 agent…」→ 模态窗（AgentsView）。配了自定义 agent 的用户
/// 不需要为配置切页，配置与使用都在聊天这一处入口。</para>
/// <para>2026-10-29：**浏览器**面板（Playwright headed 会话的网络观测视图）与**终端**面板（RoyalTerminal
/// 集成终端——真实 PTY，agent run_in_terminal 的可见执行面）——均为桌面专属切片（移动 head 不引 ⇒
/// Playwright / RoyalTerminal 不进移动闭包）；视图经桌面 head 注册的贡献者注入，移动形态不渲染工作台、
/// 天然不受影响。⑩ 工作台「加载即建全部 panel 视图、切 tab 只切 IsVisible」⇒ 终端 PTY 跨 tab 切换不断。</para>
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
            // 终端：RoyalTerminal 集成终端（真实 PTY / 完整登录 env）——agent run_in_terminal 的可见执行面（桌面专属）
            new(PanelKeys.Terminal, PanelKeys.Terminal, "终端", "terminal"),
            new(PanelKeys.Settings, PanelKeys.Settings, "设置", "settings"),
        },
        Layout: new LayoutTabbed(new[] { PanelKeys.Chat, PanelKeys.AppAudit, PanelKeys.Browser, PanelKeys.Terminal, PanelKeys.Settings }),
        FooterPanelKey: PanelKeys.Settings);
}
