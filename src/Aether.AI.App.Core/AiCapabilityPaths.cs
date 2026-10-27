namespace Aether.AI.App.Core;

/// <summary>
/// 能力目录（技能包 / 连接器）在数据目录下的固定位置——手机与桌面**同一份布局**，
/// 用户把 <c>SKILL.md</c> 放进 <c>skills/&lt;name&gt;/</c>、把 MCP 配置放进 <c>mcp-servers/*.json</c> 即可生效。
/// 两端目录名一致，跨端同步（iCloud/自建同步）时同一份能力档案可直接搬。
/// </summary>
/// <param name="DataDirectory">App 数据目录（agents.json / llm-configs.json 也在这里）。</param>
/// <param name="SkillsDirectory">技能包目录：<c>&lt;dir&gt;/&lt;skill-name&gt;/SKILL.md</c>（YAML frontmatter + Markdown 正文，可带脚本）。</param>
/// <param name="McpServersDirectory">连接器目录：<c>*.json</c>，兼容 Claude Code / Cursor 的 <c>mcpServers</c> 映射格式。</param>
public sealed record AiCapabilityPaths(string DataDirectory, string SkillsDirectory, string McpServersDirectory)
{
    public static AiCapabilityPaths For(string dataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        return new AiCapabilityPaths(
            dataDirectory,
            Path.Combine(dataDirectory, "skills"),
            Path.Combine(dataDirectory, "mcp-servers"));
    }

    /// <summary>建目录（幂等）——调用方在注册能力前先确保目录存在，空目录是合法状态（= 没有能力）。</summary>
    public AiCapabilityPaths EnsureDirectories()
    {
        Directory.CreateDirectory(SkillsDirectory);
        Directory.CreateDirectory(McpServersDirectory);
        return this;
    }
}
