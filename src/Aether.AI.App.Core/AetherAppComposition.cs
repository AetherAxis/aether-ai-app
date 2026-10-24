using Aether.AI.Core.Agents;
using Aether.AI.Hosting;
using Aether.AI.Memory;
using Aether.AI.Memory.Stores;
using Microsoft.Extensions.DependencyInjection;

namespace Aether.AI.App.Core;

/// <summary>M1 内置 agent 定义（宿主可经 <see cref="AetherAppOptions.Agents"/> 追加/覆盖同 Id 项）。</summary>
public static class DefaultAgents
{
    /// <summary>规则演示：BrainMatrix 同构——「查600000 → Echo 工具 → done:echo:600000」，无 key 全链路。</summary>
    public static AgentDefinition RuleDemo(string echoTool = "Echo") => new()
    {
        Id = "rule-demo",
        Name = "规则演示",
        Instructions = string.Empty,
        Engine = EngineKind.Rule,
        Memory = AgentMemoryMode.Off,
        Tools = new[] { echoTool },
        Rules = new RuleProgram
        {
            Steps = new()
            {
                new RuleStep
                {
                    Match = new() { OnUserRegex = "^查(.+)$", MaxToolCalls = 0 },
                    Action = new() { Tool = echoTool, Args = new() { ["text"] = "{user.1}" } },
                },
                new RuleStep
                {
                    Match = new() { MinToolCalls = 1 },
                    Action = new() { ReplyText = "done:{result}" },
                },
            }
        },
    };

    /// <summary>审批演示：规则链路 + Dangerous 工具 + RequireToolApproval——跑一遍即走完整审批协作循环（无 key）。</summary>
    public static AgentDefinition ApprovalDemo(string echoTool = "Echo") => new()
    {
        Id = "rule-approve",
        Name = "审批演示",
        Instructions = string.Empty,
        Engine = EngineKind.Rule,
        Memory = AgentMemoryMode.Off,
        Tools = new[] { echoTool },
        RequireToolApproval = true,
        Rules = new RuleProgram
        {
            Steps = new()
            {
                new RuleStep
                {
                    Match = new() { OnUserRegex = "^(?:报单|买)(.+)$", MaxToolCalls = 0 },
                    Action = new() { Tool = echoTool, Args = new() { ["text"] = "{user.1}" } },
                },
                new RuleStep
                {
                    Match = new() { MinToolCalls = 1 },
                    Action = new() { ReplyText = "已执行：{result}" },
                },
            }
        },
    };

    /// <summary>LLM 助手：选中 LLM 档案的 modelId；工具开 todo provider + 审批（Mode 选择器启用）。</summary>
    public static AgentDefinition LlmAssistant(string modelId) => new()
    {
        Id = "assistant",
        Name = "通用助手",
        Instructions = "你是 Aether 助手。回答简洁、准确；需要时使用工具。",
        ModelId = modelId,
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

    /// <summary>内置 agent 之外宿主追加的定义（M2 起被 labs 目录目录取代/补充）。</summary>
    public IReadOnlyList<AgentDefinition> Agents { get; set; } = Array.Empty<AgentDefinition>();
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

        // agent 目录：宿主追加优先于内置（同 Id 覆盖——定义表按 Id 去重，宿主定义胜出）。
        var builtin = new List<AgentDefinition> { DefaultAgents.RuleDemo(), DefaultAgents.ApprovalDemo() };
        var merged = options.Agents.Concat(builtin)
            .GroupBy(a => a.Id, StringComparer.Ordinal)
            .Select(g => options.Agents.FirstOrDefault(x => x.Id == g.Key) ?? g.First())
            .ToList();
        services.AddSingleton<IAgentCatalog>(new BuiltInAgentCatalog(merged));
        services.AddSingleton<IAgentRunner, AgentRunner>();
        services.AddSingleton<ChatTranscriptPersister>();
        services.AddSingleton<ConversationHistoryLoader>();
        return services;
    }
}
