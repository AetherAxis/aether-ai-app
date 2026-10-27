using Aether.AI.Abstractions;

namespace Aether.AI.App.Core;

/// <summary>
/// 能力（技能包 / 连接器）的**宿主取舍**——手机与桌面共用同一份档案、同一条注册路径，
/// 差别只在「这台机器跑不跑得了」。集中在这里而不是散在组合根里，是为了：
/// ① 组合根与测试用同一套判定（不会 UI 说能跑、运行期跑不了）；② 一处写清「为什么」。
/// <para>参照手机端 AI 助手的通行做法（豆包等的「技能 / 连接器 / 工作伙伴」）：
/// 技能包 = 声明式专业流程（提示词 + 资源 + 可选脚本）；连接器 = 远端服务（HTTP/SSE）。
/// 于是手机端的可移植子集天然是「技能包正文 + HTTP 连接器」，需要本地进程的（脚本执行、
/// stdio 连接器）留在桌面。</para>
/// </summary>
public static class AiCapabilityPolicy
{
    /// <summary>
    /// skill 脚本（<c>run_skill_script</c>，.py/.sh/.js 子进程）是否可用：仅桌面。
    /// iOS 沙盒禁止 fork、手机也没有解释器；关掉后 SKILL.md 正文照常加载，只挡脚本执行。
    /// </summary>
    public static bool SupportsSkillScripts(bool isDesktop) => isDesktop;

    /// <summary>
    /// 某个 MCP server 是否接进当前宿主：HTTP/SSE 两端同源（连接器是远端服务）；
    /// stdio（<c>command</c>+<c>args</c>）要 spawn 子进程 → 仅桌面。
    /// </summary>
    public static bool SupportsMcpServer(McpServerConfig config, bool isDesktop)
    {
        ArgumentNullException.ThrowIfNull(config);
        return isDesktop || !config.IsStdio;
    }
}
