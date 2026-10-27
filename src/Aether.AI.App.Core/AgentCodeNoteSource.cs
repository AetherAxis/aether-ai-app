using Aether.AI.Brains.Code.Compilation;

namespace Aether.AI.App.Core;

/// <summary>
/// 把 agents.json 里的「代码大脑」agent 喂给 ⑫ 的编译管线：一条 agent = 一条 <see cref="CodeNote"/>。
/// <para>编译后注册进 <c>ICodeBrainRegistry</c>（键 = agent.Id），
/// <see cref="CustomAgentStore.ToDefinition"/> 产出的 <c>Engine=Code</c> 定义即按 Id 取到可执行大脑。
/// 编译失败的条目不会注册，错误留在 <c>ICodeCompilationService.LoadErrors</c> 里给管理窗显示。</para>
/// </summary>
public sealed class AgentCodeNoteSource : ICodeNoteSource
{
    private readonly CustomAgentStore _store;

    public AgentCodeNoteSource(CustomAgentStore store) => _store = store;

    public Task<IReadOnlyList<CodeNote>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        IReadOnlyList<CodeNote> notes = _store.Agents
            .Where(a => a.Kind == CustomAgentKind.Code)
            .Select(a => new CodeNote(
                a.Id,
                string.IsNullOrWhiteSpace(a.Name) ? a.Id : a.Name,
                a.SourceCode,
                string.IsNullOrWhiteSpace(a.CodeLanguage) ? "csharp" : a.CodeLanguage))
            .ToList();
        return Task.FromResult(notes);
    }
}
