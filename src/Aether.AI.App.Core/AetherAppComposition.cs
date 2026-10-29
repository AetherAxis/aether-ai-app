using Aether.AI.Core.Agents;
using Aether.AI.Hosting;
using Aether.AI.Memory;
using Aether.AI.Memory.Stores;
using Microsoft.Extensions.DependencyInjection;

namespace Aether.AI.App.Core;

/// <summary>
/// 出厂 agent 定义。目录里**没有「内置/只读」这一档**了：真正常驻聊天的只有
/// <see cref="LlmAssistant"/>，且它由宿主在首次运行时落成 agents.json 里的一条普通记录
/// （可编辑、可删除，见 <c>CustomAgentStore.SeedIfMissing</c>）。下面两个演示定义只在
/// <see cref="AetherAppOptions.IncludeDemoAgents"/> 打开时（<c>--smoke</c> / <c>--demo</c>）进目录，
/// 正常运行不出现。
/// </summary>
public static class DefaultAgents
{
    /// <summary>规则演示：无 key 全链路——「读&lt;文件路径&gt; → ReadFile 工具 → done:{结果}」。
    /// 2026-10-29 起指向真实工具（Echo 演示工具已随真工具齐备移除）；输入的文件由调用方备好（smoke/demo 各自落临时文件）。</summary>
    public static AgentDefinition RuleDemo(string tool = "ReadFile") => new()
    {
        Id = "rule-demo",
        Name = "规则演示",
        Instructions = string.Empty,
        Engine = EngineKind.Rule,
        Memory = AgentMemoryMode.Off,
        Tools = new[] { tool },
        Rules = new RuleProgram
        {
            Steps = new()
            {
                new RuleStep
                {
                    Match = new() { OnUserRegex = "^读(.+)$", MaxToolCalls = 0 },
                    Action = new() { Tool = tool, Args = new() { ["filePath"] = "{user.1}" } },
                },
                new RuleStep
                {
                    Match = new() { MinToolCalls = 1 },
                    Action = new() { ReplyText = "done:{result}" },
                },
            }
        },
    };

    /// <summary>审批演示：规则链路 + Dangerous 工具（DeleteFile）+ RequireToolApproval——跑一遍即走完整审批协作循环（无 key）。
    /// 输入「删除&lt;文件路径&gt;」：批准后文件**真的被删**（端到端副作用断言）；拒绝则文件保留。</summary>
    public static AgentDefinition ApprovalDemo(string tool = "DeleteFile") => new()
    {
        Id = "rule-approve",
        Name = "审批演示",
        Instructions = string.Empty,
        Engine = EngineKind.Rule,
        Memory = AgentMemoryMode.Off,
        Tools = new[] { tool },
        RequireToolApproval = true,
        Rules = new RuleProgram
        {
            Steps = new()
            {
                new RuleStep
                {
                    Match = new() { OnUserRegex = "^(?:删除|删)(.+)$", MaxToolCalls = 0 },
                    Action = new() { Tool = tool, Args = new() { ["filePath"] = "{user.1}" } },
                },
                new RuleStep
                {
                    Match = new() { MinToolCalls = 1 },
                    Action = new() { ReplyText = "已执行：{result}" },
                },
            }
        },
    };

    /// <summary>LLM 助手：ModelId 留空 = 动态走宿主 IChatClientFactory 的**当前选中档案**（设置页切换即换，无需改定义）。
    /// 工具开 todo provider + 审批（Mode 选择器启用）。这是首次运行时落进 agents.json 的那条「通用助手」的初值。</summary>
    public static AgentDefinition LlmAssistant() => new()
    {
        Id = "assistant",
        Name = "通用助手",
        Description = "默认助手：跟随当前模型档案，提示词/工具/审批都能改",
        Instructions = "你是 Aether 助手。回答简洁、准确；需要时使用工具。",
        ModelId = string.Empty,
        Memory = AgentMemoryMode.SlidingWindow,
        EnableTodoProvider = true,
        RequireToolApproval = true,
        EnableAgentModeProvider = true,
    };
}

/// <summary>App.Core 组合选项。</summary>
public sealed class AetherAppOptions
{
    /// <summary>数据目录（SQLite/偏好/LLM 档案都落这里）。</summary>
    public string DataDirectory { get; set; } = string.Empty;

    /// <summary>内置 agent 之外宿主追加的定义（静态）。</summary>
    public IReadOnlyList<AgentDefinition> Agents { get; set; } = Array.Empty<AgentDefinition>();

    /// <summary>动态 agent 源（每次枚举求值）：用户 agents.json 里的全部 agent。与 <see cref="Agents"/> 合并（同 Id 宿主胜出）。</summary>
    public Func<IServiceProvider, IEnumerable<AgentDefinition>>? DynamicAgents { get; set; }

    /// <summary>把 <c>DefaultAgents.RuleDemo/ApprovalDemo</c> 也挂进目录——只给无头冒烟（<c>--smoke</c>）
    /// 与移动演示（<c>--demo</c>）用；正常运行时为 false，目录里只有用户自己的 agent。</summary>
    public bool IncludeDemoAgents { get; set; }
}

/// <summary>
/// App.Core 组合根：在 <c>AddAetherAI()</c>（与大脑注册）**之后**调用——覆盖存储为 SQLite、
/// 注册 agent 目录/运行桥/持久化器。大脑取舍（Rule/LLM/编译）与工具注册归 UI 组合根，本层只认平台缝。
/// </summary>
public static class AetherAppComposition
{
    public static IServiceCollection AddAetherAiApp(this IServiceCollection services, Action<AetherAppOptions> configure)
    {
        var options = new AetherAppOptions();
        configure(options);
        if (string.IsNullOrWhiteSpace(options.DataDirectory))
            throw new InvalidOperationException("AetherAppOptions.DataDirectory 必须设置（SQLite/偏好落盘根目录）。");
        Directory.CreateDirectory(options.DataDirectory);

        var dbPath = Path.Combine(options.DataDirectory, "chat.db");
        // 覆盖 AddAetherAI 的默认 InMemory（后注册者胜出）——真实动 e_sqlite3，重启续聊靠它。
        services.AddSingleton<IMemoryStore>(_ => SqliteMemoryStore.ForFile(dbPath));
        services.AddSingleton<IConversationStore>(_ => SqliteConversationStore.ForFile(dbPath));

        // agent 目录：宿主静态 + 宿主动态源（每次枚举求值——agents.json 改完不重启即生效）
        // + 演示 agent（仅 --smoke/--demo），同 Id 去重宿主定义胜出。
        services.AddSingleton<IAgentCatalog>(sp => new BuiltInAgentCatalog(() =>
            MergeAgents(
                options.Agents.Concat(options.DynamicAgents?.Invoke(sp) ?? Enumerable.Empty<AgentDefinition>()),
                options.IncludeDemoAgents)));
        services.AddSingleton<IAgentRunner, AgentRunner>();
        services.AddSingleton<ChatTranscriptPersister>();
        services.AddSingleton<ConversationHistoryLoader>();
        return services;
    }

    private static IEnumerable<AgentDefinition> MergeAgents(IEnumerable<AgentDefinition> extra, bool includeDemoAgents)
    {
        var rest = includeDemoAgents
            ? new[] { DefaultAgents.RuleDemo(), DefaultAgents.ApprovalDemo() }
            : Array.Empty<AgentDefinition>();
        return extra.Concat(rest)
            .GroupBy(a => a.Id, StringComparer.Ordinal)
            .Select(g => g.First());
    }
}
