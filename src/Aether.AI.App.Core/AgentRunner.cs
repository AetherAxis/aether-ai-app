using System.Collections.Concurrent;
using Aether.AI.Core.Agents;

namespace Aether.AI.App.Core;

/// <summary>
/// 内置 agent 目录（M1）：进程内定义——规则演示 agent（无 key 全链路）+ LLM 助手（有配置时才有意义）
/// + 审批演示 agent（Dangerous 工具走审批循环）。宿主可经 <see cref="AetherAppOptions.Agents"/> /
/// <see cref="AetherAppOptions.DynamicAgents"/> 追加（动态源每次枚举求值）。
/// </summary>
public interface IAgentCatalog
{
    Task<IReadOnlyList<AgentDefinition>> GetAllAsync(CancellationToken cancellationToken = default);

    Task<AgentDefinition?> GetAsync(string agentId, CancellationToken cancellationToken = default);
}

/// <summary>基于 <see cref="Func{TResult}"/> 源的目录实现（源每次枚举求值——支持动态增删）。</summary>
public sealed class BuiltInAgentCatalog : IAgentCatalog
{
    private readonly Func<IEnumerable<AgentDefinition>> _source;

    public BuiltInAgentCatalog(Func<IEnumerable<AgentDefinition>> source)
    {
        _source = source;
    }

    public Task<IReadOnlyList<AgentDefinition>> GetAllAsync(CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<AgentDefinition>>(_source().ToList());

    public Task<AgentDefinition?> GetAsync(string agentId, CancellationToken cancellationToken = default)
        => Task.FromResult(_source().FirstOrDefault(a => a.Id == agentId));
}

/// <summary>
/// <see cref="IAgentRunner"/> 默认实现：桥接 <see cref="IAgentCatalog"/> 与平台 <see cref="Aether.AI.Harness.HarnessRuntime"/>。
/// conversationId → sessionId 映射（每会话独立运行时会话，多会话记忆隔离）+ 同会话串行化 +
/// 编辑后 Invalidate 重建（保记忆换定义）。自 monorepo CustomAgentRunner port；
/// <see cref="RunInteractiveAsync"/> 为新增——直达 ⑫ 的审批自恢复重载。
/// </summary>
public interface IAgentRunner
{
    Task<string> RunInteractiveAsync(Guid conversationId, string agentId, string message,
        StreamHandlers? handlers = null, ApprovalResponder? approvalResponder = null,
        string? desiredMode = null, CancellationToken cancellationToken = default);

    Task<string?> GetModeAsync(Guid conversationId, CancellationToken cancellationToken = default);

    Task<bool> SetModeAsync(Guid conversationId, string mode, CancellationToken cancellationToken = default);

    /// <summary>agent 编辑后让新定义下次对话生效，但不重置会话、不丢多轮记忆。</summary>
    void Invalidate(string agentId);

    /// <summary>会话删除：清运行时会话状态（映射 + FrameworkAgentSession + 平台 session）。</summary>
    void EvictConversation(Guid conversationId);
}

public sealed class AgentRunner : IAgentRunner
{
    private readonly Aether.AI.Harness.HarnessRuntime _runtime;
    private readonly IAgentCatalog _catalog;
    // conversationId -> (runtime sessionId, agentId)：每会话独立运行时会话，多会话记忆隔离。
    private readonly ConcurrentDictionary<Guid, (Guid sessionId, string agentId)> _sessionsByConversation = new();
    // conversationId -> SemaphoreSlim(1,1)：同会话串行化（防两轮并发踩非原子竞态）。跨会话并发不受限。
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _runLocks = new();

    private SemaphoreSlim GetRunLock(Guid conversationId)
        => _runLocks.GetOrAdd(conversationId, _ => new SemaphoreSlim(1, 1));

    public AgentRunner(Aether.AI.Harness.HarnessRuntime runtime, IAgentCatalog catalog)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
    }

    public async Task<string> RunInteractiveAsync(
        Guid conversationId,
        string agentId,
        string message,
        StreamHandlers? handlers = null,
        ApprovalResponder? approvalResponder = null,
        string? desiredMode = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(message))
            throw new ArgumentException("消息不能为空", nameof(message));

        // 按需注册（首次或 Invalidate 后）：sessionId 复用（映射未删）→ 历史由 RestoringChatHistoryProvider 从 DB 恢复。
        if (!_runtime.HasAgent(agentId))
        {
            var def = await _catalog.GetAsync(agentId, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException($"找不到 agent：{agentId}");
            _runtime.RegisterAgent(def);
        }

        var sessionId = _sessionsByConversation.GetOrAdd(
            conversationId,
            _ => (_runtime.StartSession(agentId).SessionId, agentId)).sessionId;

        // 同会话串行化：本地路径靠 VM 的 IsAgentRunning 守卫，但两条路径可能同时命中同一会话
        // → 串行化避免 AgentSession.Messages/GetOrCreateFrameworkSessionAsync 非原子竞态。
        var sem = GetRunLock(conversationId);
        await sem.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // 命名 delegate → ⑫ 的 Func 缝（签名一致，显式包一层避免类型不兼容）。
            Func<ToolApprovalRequest, CancellationToken, Task<ToolApprovalDecision?>>? resolver =
                approvalResponder is null ? null : (req, ct) => approvalResponder(req, ct);
            return await _runtime.RunInteractiveAsync(sessionId, message, handlers, resolver, desiredMode, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            sem.Release();
        }
    }

    public async Task<string?> GetModeAsync(Guid conversationId, CancellationToken cancellationToken = default)
    {
        if (!_sessionsByConversation.TryGetValue(conversationId, out var entry)) return null;
        return await _runtime.GetModeAsync(entry.sessionId, cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> SetModeAsync(Guid conversationId, string mode, CancellationToken cancellationToken = default)
    {
        if (!_sessionsByConversation.TryGetValue(conversationId, out var entry)) return false;
        return await _runtime.SetModeAsync(entry.sessionId, mode, cancellationToken).ConfigureAwait(false);
    }

    public void Invalidate(string agentId)
    {
        // 编辑配置后让新定义（tools/systemPrompt/model 等）下次对话生效，但**不重置会话、不丢多轮记忆**：
        // ① _runtime.Invalidate 清 definition + agent 实例缓存 → 下次 run HasAgent=false → 重新 GetAsync + RegisterAgent。
        // ② 仅清各会话的 frameworkSession，**保留** conversationId→sessionId 映射 → 下次 run 复用同一 sessionId，
        //    frameworkSession 重建后由 RestoringChatHistoryProvider 按 conversationId 从 DB 恢复历史。
        foreach (var kv in _sessionsByConversation.Where(x => x.Value.agentId == agentId))
            _runtime.RebuildSessionFramework(kv.Value.sessionId);
        _runtime.Invalidate(agentId);
    }

    public void EvictConversation(Guid conversationId)
    {
        // 会话删除：丢弃映射并清平台运行时会话——RestoringChatHistoryProvider 的弱键缓存随 GC 自动回收。
        if (_sessionsByConversation.TryRemove(conversationId, out var entry))
            _runtime.EvictSession(entry.sessionId);
    }
}
