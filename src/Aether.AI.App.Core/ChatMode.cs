using Aether.AI.Core.Agents;

namespace Aether.AI.App.Core;

/// <summary>聊天输入框的 4 档运行模式（对标 Claude Code 的 Plan/Manual/Edit-automatically/Auto）。
/// MAF 1.22 起四档均为<b>真 AgentMode</b>（<see cref="AgentModes"/> 名 + 每档独立指令——模型感知档位）；
/// 审批门控在引擎层（<see cref="ChatApprovalDecider"/> 逐工具拦截）——指令对齐模型行为，门控是硬保证。
/// 自 monorepo ChatMode 逐字 port。</summary>
public enum ChatMode
{
    /// <summary>规划：AgentMode=plan，agent 先出计划不执行；产出计划后弹「接受计划→执行」。</summary>
    Plan,
    /// <summary>手动：AgentMode=manual；每个非 Safe 工具都弹审批框，模型逐步说明再动手。</summary>
    Manual,
    /// <summary>自动编辑（默认）：AgentMode=auto-edit；自动批 Confirm 级工具，Dangerous 仍弹框。</summary>
    AutoEdit,
    /// <summary>自动：AgentMode=auto；全部自动批（含 Dangerous），不弹框。</summary>
    Auto,
}

public static class ChatModeExtensions
{
    /// <summary>是否为规划档（AgentMode=plan）。</summary>
    public static bool IsPlan(this ChatMode mode) => mode == ChatMode.Plan;

    /// <summary>映射到 MAF desiredMode 字符串（4 档一一对应 <see cref="AgentModes"/>）。</summary>
    public static string ToDesiredMode(this ChatMode mode) => mode switch
    {
        ChatMode.Plan => AgentModes.Plan,
        ChatMode.Manual => AgentModes.Manual,
        ChatMode.Auto => AgentModes.Auto,
        _ => AgentModes.AutoEdit,
    };

    /// <summary>MAF 读回的 mode 字符串 → ChatMode（同步用；未知值返 null 保留现值）。</summary>
    public static ChatMode? FromMafMode(string? mafMode) => mafMode switch
    {
        AgentModes.Plan => ChatMode.Plan,
        AgentModes.Manual => ChatMode.Manual,
        AgentModes.AutoEdit => ChatMode.AutoEdit,
        AgentModes.Auto => ChatMode.Auto,
        _ => null,
    };

    /// <summary>从 MAF 读回的 mode 字符串判断是否为 plan。</summary>
    public static bool IsPlanMode(string? mafMode) => mafMode == AgentModes.Plan;
}
