using Aether.AI.Core.Agents;

namespace Aether.AI.App.Core;

/// <summary>
/// 4 档聊天模式 × 工具审批等级的自动批决策矩阵（纯函数，可单测）。
/// 返回非 null = 自动批准（跳过对话框+审批卡，走与用户批准相同的恢复路径）；null = 弹审批框。
/// 自 monorepo ChatApprovalDecider 逐字 port。
/// </summary>
public static class ChatApprovalDecider
{
    /// <param name="mode">当前档位（运行中逐工具读即时值——中途切档立即影响下一个工具）。</param>
    /// <param name="hasModeSupport">会话目标是否有 4 档选择器（EnableAgentModeProvider）。false = RequireToolApproval-only，保留全弹框旧行为。</param>
    /// <param name="toolName">待批工具名（AutoEdit 档按其声明等级区分）。</param>
    /// <param name="levelLookup">等级查询（注入 ToolRegistry 的 Core 抽象）。null（未注册）退化为 Dangerous → 弹框（安全降级）。</param>
    public static ToolApprovalDecision? TryDecide(
        ChatMode mode,
        bool hasModeSupport,
        string toolName,
        IToolApprovalLevelLookup? levelLookup,
        string requestId)
    {
        if (!hasModeSupport) return null;
        switch (mode)
        {
            case ChatMode.Auto:
                // 全部自动批（含 Dangerous）。
                return new ToolApprovalDecision(requestId, Approved: true, "自动批准（自动模式）", AlwaysApprove: false);
            case ChatMode.AutoEdit:
                // 自动批非 Dangerous（Safe/Confirm）；Dangerous 弹框。lookup 缺失 → 视为 Dangerous（弹框，安全降级）。
                var level = levelLookup?.LevelOf(toolName) ?? ToolApprovalLevel.Dangerous;
                return level != ToolApprovalLevel.Dangerous
                    ? new ToolApprovalDecision(requestId, Approved: true, "自动批准（自动编辑模式）", AlwaysApprove: false)
                    : null;
            default: // Manual / Plan：始终弹框（Plan 轮一般不调写工具，防御性弹框）。
                return null;
        }
    }
}
