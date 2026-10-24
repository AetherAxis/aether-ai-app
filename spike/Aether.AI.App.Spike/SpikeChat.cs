using System.Text;
using Aether.AI.Brains.Rule;
using Aether.AI.Core.Agents;
using Aether.AI.Harness;
using Aether.AI.Hosting;
using Aether.AI.Memory;
using Aether.AI.Memory.Stores;
using Microsoft.Extensions.DependencyInjection;

namespace Aether.AI.App.Spike;

/// <summary>
/// M0 spike 的 ⑫ 接线面：Rule 大脑（program 后端）+ Echo 工具 + SQLite 存储，
/// 一个会话跑「查600000 → Echo 工具回填 → done:echo:600000」全链路（无 LLM key）。
/// 刻意与 ⑫ samples/BrainMatrix 的 program agent 同构——终答一致即证明平台链路在
/// net10.0 / net10.0-android / net10.0-ios 三种 TFM 上行为不变。
/// </summary>
public sealed class SpikeChat
{
    /// <summary>BrainMatrix 同构断言的期望终答。</summary>
    public const string ExpectedResult = "done:echo:600000";

    private readonly ServiceProvider _provider;
    private readonly HarnessRuntime _runtime;
    private readonly Guid _sessionId;

    private SpikeChat(ServiceProvider provider, HarnessRuntime runtime, Guid sessionId)
    {
        _provider = provider;
        _runtime = runtime;
        _sessionId = sessionId;
    }

    /// <summary>
    /// 组装最小 ⑫ 宿主。sqlitePath 非空时以 SQLite 覆盖默认 InMemory 存储
    /// （真实动 e_sqlite3 native——移动端 native 载入验证点）。
    /// </summary>
    public static SpikeChat Create(string? sqlitePath = null)
    {
        var services = new ServiceCollection();
        services.AddRuleBrains();              // program 规则后端（闭包零 OpenAI/Roslyn/FCS）
        services.AddAetherAI()
            .AddTool((Func<string, string>)(text => $"echo:{text}"), "Echo");
        if (sqlitePath is not null)
        {
            services.AddSingleton<IMemoryStore>(_ => SqliteMemoryStore.ForFile(sqlitePath));
            services.AddSingleton<IConversationStore>(_ => SqliteConversationStore.ForFile(sqlitePath));
        }
        var provider = services.BuildServiceProvider();

        var runtime = provider.GetRequiredService<HarnessRuntime>();
        runtime.RegisterAgent(new AgentDefinition
        {
            Id = "spike-rule",
            Name = "Spike 规则大脑",
            Engine = EngineKind.Rule,
            Memory = AgentMemoryMode.Off,
            Tools = new[] { "Echo" },
            Rules = new RuleProgram
            {
                Steps = new()
                {
                    new RuleStep
                    {
                        Match = new() { OnUserRegex = "^查(.+)$", MaxToolCalls = 0 },
                        Action = new() { Tool = "Echo", Args = new() { ["text"] = "{user.1}" } },
                    },
                    new RuleStep
                    {
                        Match = new() { MinToolCalls = 1 },
                        Action = new() { ReplyText = "done:{result}" },
                    },
                },
            },
        });
        var sessionId = runtime.StartSession("spike-rule").SessionId;
        return new SpikeChat(provider, runtime, sessionId);
    }

    /// <summary>跑一轮规则链路，把每条流事件（token/工具/进度/用量）经 log 上抛，返回终答全文。</summary>
    public async Task<string> RunAsync(string message, Action<string> log, CancellationToken cancellationToken = default)
    {
        var full = await _runtime.RunStreamingAsync(_sessionId, message, new StreamHandlers(
            OnToken: (t, _) =>
            {
                log($"token    {t}");
                return Task.CompletedTask;
            },
            OnToolEvent: (ev, _) =>
            {
                log($"tool     {ev.Kind} {ev.Name} callId={ev.CallId}");
                return Task.CompletedTask;
            },
            OnProgress: p =>
            {
                var detail = p.Kind switch
                {
                    RunProgressKind.ToolStarted => $" tool={p.ToolName} arg={p.ToolArgument}",
                    RunProgressKind.ToolFinished => $" tool={p.ToolName}",
                    RunProgressKind.RunError => $" text={p.Text}",
                    _ => "",
                };
                log($"progress  {p.Kind}{detail}");
            },
            OnUsage: u => log($"usage    {u}")), cancellationToken: cancellationToken).ConfigureAwait(false);

        // SQLite native 证据：Memory=Off 的规则链路不会自动碰存储——显式摸一次（建表 + 查询）。
        // 失败不判死链路（终答已断言），但 log 必须留痕，供 M0 判定表用。
        try
        {
            var store = _provider.GetService<IConversationStore>();
            if (store is not null)
            {
                var summaries = await store.GetSummariesAsync(string.Empty, cancellationToken).ConfigureAwait(false);
                log($"sqlite   store 在线（{store.GetType().Name}，summaries={summaries.Count}）");
            }
        }
        catch (Exception ex)
        {
            log($"sqlite   失败：{ex.Message}");
        }

        log($"full     {full}");
        return full;
    }
}
