using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using Aether.AI.Brains.Code.Compilation;
using Aether.AI.Core.Agents;

namespace Aether.AI.App.Core;

/// <summary>自定义 agent 的大脑类型（序号与 ⑫ <see cref="EngineKind"/> 对齐：0/1/2）。
/// 三类大脑在本 App 里**都是配置**：LLM = 提示词，规则 = 正则/模板，代码 = 一段源码。</summary>
public enum CustomAgentKind
{
    /// <summary>LLM 大脑：系统提示词 + 工具集 + 审批开关，模型取 LLM 档案。</summary>
    Llm,
    /// <summary>声明式规则大脑：正则匹配 → 调工具/直接回复，零 key、零延迟。</summary>
    Rule,
    /// <summary>代码大脑：一段继承 <c>CodeRuleBrain</c> 的 C# 源码，宿主用 Roslyn 本机编译后执行。</summary>
    Code,
}

/// <summary>
/// 一个用户自建的 agent（Agents 页 CRUD，落 &lt;dataDir&gt;/agents.json）。
/// 一个类型装两类大脑的字段——空着的字段即「该类大脑不用」，映射见 <see cref="CustomAgentStore.ToDefinition"/>。
/// </summary>
public sealed record CustomAgent
{
    /// <summary>稳定 Id（<c>custom-*</c>，建后不再变——聊天页选中的恢复靠它）。</summary>
    public string Id { get; init; } = string.Empty;

    public string Name { get; init; } = string.Empty;

    /// <summary>给目录/聊天下拉看的摘要（不含提示词全文）。</summary>
    public string Description { get; init; } = string.Empty;

    public CustomAgentKind Kind { get; init; } = CustomAgentKind.Llm;

    // —————————————— LLM 大脑 ——————————————

    /// <summary>系统提示词。</summary>
    public string Instructions { get; init; } = string.Empty;

    /// <summary>固定用哪份 LLM 档案（档案名）；null/空 = 跟随聊天页当前选中的档案。 </summary>
    public string? ModelProfile { get; init; }

    /// <summary>允许调用的工具名（ToolRegistry 里的名字）。</summary>
    public IReadOnlyList<string> Tools { get; init; } = Array.Empty<string>();

    /// <summary>工具调用前逐个审批（危险操作弹框）。</summary>
    public bool RequireToolApproval { get; init; } = true;

    /// <summary>启用 4 档运行模式选择器（规划 / 手动 / 自动编辑 / 自动）。</summary>
    public bool EnableModePicker { get; init; } = true;

    // —————————————— 规则大脑 ——————————————

    /// <summary>触发正则（对最新一条 user 消息）。空 = 任意消息都命中。</summary>
    public string? MatchRegex { get; init; }

    /// <summary>命中后调用的工具名；空 = 不调工具、直接按 <see cref="ReplyTemplate"/> 回复。</summary>
    public string? ToolName { get; init; }

    /// <summary>工具参数 JSON 模板（值里可用 <c>{user.N}</c> / <c>{user}</c> / <c>{result}</c>）。</summary>
    public string? ToolArgsTemplate { get; init; }

    /// <summary>回复模板：调工具时是「工具执行后的回复」，不调工具时是「直接回复」。</summary>
    public string ReplyTemplate { get; init; } = string.Empty;

    /// <summary>没有规则命中时的兜底回复。</summary>
    public string NoMatchReply { get; init; } = "（这条消息没有匹配到规则）";

    // —————————————— 代码大脑 ——————————————

    /// <summary>源码：一个继承 <c>CodeRuleBrain</c> 的非抽象类，重写 <c>RunAsync()</c>。
    /// 宿主交给 ⑫ 的编译管线（Roslyn）编译并注册进 <c>ICodeBrainRegistry</c>（键 = <see cref="Id"/>）。</summary>
    public string SourceCode { get; init; } = string.Empty;

    /// <summary>源码语言（<c>csharp</c> / <c>fsharp</c>）。当前宿主只注册了 C# 编译器。</summary>
    public string CodeLanguage { get; init; } = "csharp";
}

/// <summary>
/// 自定义 agent 存储（单例）：整表读写 &lt;dataDir&gt;/agents.json，<see cref="Changed"/> 供聊天页热刷新目录。
/// 与 <see cref="LlmConfigState"/> 同款形态（列表小、重建式刷新），但**不含机密**（提示词/规则），故不做 0600。
/// </summary>
public sealed class CustomAgentStore
{
    private readonly string _path;
    private readonly object _lock = new();
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public CustomAgentStore(string path)
    {
        _path = path;
        Agents = Load(path);
    }

    public IReadOnlyList<CustomAgent> Agents { get; private set; }

    /// <summary>保存/删除后广播（消费方：聊天页 agent 下拉热刷新）。</summary>
    public event Action? Changed;

    /// <summary>整表替换并落盘（原子写：先写 .tmp 再 move，避免半截文件）。</summary>
    public void Save(IReadOnlyList<CustomAgent> agents)
    {
        lock (_lock)
        {
            Agents = agents.ToList();
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(Agents, JsonOpts));
            File.Move(tmp, _path, overwrite: true);
        }
        Changed?.Invoke();
    }

    /// <summary>
    /// 首次运行播种：agents.json **不存在**时把出厂 agent（「通用助手」）落成普通记录；
    /// 文件已存在就绝不插手——用户删空也是他自己的选择（不会被复活）。
    /// </summary>
    /// <returns>是否真的播种了。</returns>
    public bool SeedIfMissing(IReadOnlyList<CustomAgent> defaults)
    {
        if (defaults.Count == 0 || File.Exists(_path)) return false;
        Save(defaults);
        return true;
    }

    public static IReadOnlyList<CustomAgent> Load(string path)
    {
        if (!File.Exists(path)) return Array.Empty<CustomAgent>();
        try
        {
            return JsonSerializer.Deserialize<List<CustomAgent>>(File.ReadAllText(path), JsonOpts)
                   ?? (IReadOnlyList<CustomAgent>)Array.Empty<CustomAgent>();
        }
        catch
        {
            return Array.Empty<CustomAgent>();   // 坏文件不阻断启动（下次保存覆盖）
        }
    }

    /// <summary>按名称生成稳定 Id（<c>custom-&lt;slug&gt;</c>；重名追加 -2/-3）。</summary>
    public static string NewId(string name, IEnumerable<string> existingIds)
    {
        var slug = new string((name ?? string.Empty).Trim().ToLowerInvariant()
            .Select(ch => char.IsLetterOrDigit(ch) || ch == '-' || ch == '_' ? ch : '-')
            .ToArray()).Trim('-');
        if (slug.Length == 0) slug = "agent";
        if (slug.Length > 24) slug = slug[..24];
        var baseId = $"custom-{slug}";
        var taken = new HashSet<string>(existingIds ?? Enumerable.Empty<string>(), StringComparer.Ordinal);
        if (!taken.Contains(baseId)) return baseId;
        for (var i = 2; ; i++)
        {
            var candidate = $"{baseId}-{i}";
            if (!taken.Contains(candidate)) return candidate;
        }
    }

    /// <summary>自定义 agent → 引擎的 <see cref="AgentDefinition"/>（组合根把它挂进 DynamicAgents）。</summary>
    public static AgentDefinition ToDefinition(CustomAgent a) => a.Kind switch
    {
        CustomAgentKind.Rule => ToRuleDefinition(a),
        CustomAgentKind.Code => ToCodeDefinition(a),
        _ => ToLlmDefinition(a),
    };

    /// <summary>代码大脑：定义只带 Id/工具/审批——可执行体由编译管线按 Id 注册（见 <see cref="AgentCodeNoteSource"/>）。</summary>
    private static AgentDefinition ToCodeDefinition(CustomAgent a) => new()
    {
        Id = a.Id,
        Name = a.Name,
        Description = a.Description,
        Engine = EngineKind.Code,
        CodeLanguage = CodeCompilationService.TryParseLanguage(a.CodeLanguage, out var language)
            ? language
            : CodeBrainLanguage.CSharp,
        Tools = a.Tools,
        Memory = AgentMemoryMode.Off,
        RequireToolApproval = a.RequireToolApproval,
    };

    /// <summary><see cref="ToLlmDefinition"/> 的反向：出厂 LLM 定义 → 可编辑的 <see cref="CustomAgent"/>。
    /// 首次运行用它把「通用助手」种进 agents.json——之后它就和用户自建的 agent 完全一样。</summary>
    public static CustomAgent FromLlmDefinition(AgentDefinition def) => new()
    {
        Id = def.Id,
        Name = def.Name,
        Description = def.Description,
        Kind = CustomAgentKind.Llm,
        Instructions = def.Instructions,
        // 出厂定义 ModelId 为空（跟随当前选中档案）；这里保持 null 语义一致。
        ModelProfile = string.IsNullOrWhiteSpace(def.ModelId) ? null : def.ModelId,
        Tools = def.Tools,
        RequireToolApproval = def.RequireToolApproval,
        EnableModePicker = def.EnableAgentModeProvider,
    };

    private static AgentDefinition ToLlmDefinition(CustomAgent a) => new()
    {
        Id = a.Id,
        Name = a.Name,
        Description = a.Description,
        Engine = EngineKind.Llm,
        Instructions = a.Instructions,
        // ModelId = 档案名 → AppLlmClientFactory 按名解析；空 = 跟随当前选中档案。
        ModelId = a.ModelProfile ?? string.Empty,
        Tools = a.Tools,
        Memory = AgentMemoryMode.SlidingWindow,
        RequireToolApproval = a.RequireToolApproval,
        EnableTodoProvider = a.EnableModePicker,
        EnableAgentModeProvider = a.EnableModePicker,
    };

    private static AgentDefinition ToRuleDefinition(CustomAgent a)
    {
        var usesTool = !string.IsNullOrWhiteSpace(a.ToolName);
        var trigger = new RuleMatch
        {
            OnUserRegex = string.IsNullOrWhiteSpace(a.MatchRegex) ? null : a.MatchRegex,
            // 调工具时只允许在本轮第一次调用前命中（第 1 步）；否则每轮都会重入。
            MaxToolCalls = usesTool ? 0 : null,
        };
        var steps = new List<RuleStep>();
        if (usesTool)
        {
            steps.Add(new RuleStep
            {
                Name = "trigger",
                Match = trigger,
                Action = new RuleAction { Tool = a.ToolName, Args = ParseArgsTemplate(a.ToolArgsTemplate) },
            });
            steps.Add(new RuleStep
            {
                Name = "reply",
                Match = new RuleMatch { MinToolCalls = 1 },
                Action = new RuleAction
                {
                    ReplyText = string.IsNullOrWhiteSpace(a.ReplyTemplate) ? "{result}" : a.ReplyTemplate,
                },
            });
        }
        else
        {
            steps.Add(new RuleStep
            {
                Name = "reply",
                Match = trigger,
                Action = new RuleAction
                {
                    ReplyText = string.IsNullOrWhiteSpace(a.ReplyTemplate) ? "（未填写回复模板）" : a.ReplyTemplate,
                },
            });
        }

        return new AgentDefinition
        {
            Id = a.Id,
            Name = a.Name,
            Description = a.Description,
            Engine = EngineKind.Rule,
            Memory = AgentMemoryMode.Off,
            Tools = usesTool ? new[] { a.ToolName! } : Array.Empty<string>(),
            RequireToolApproval = a.RequireToolApproval,
            Rules = new RuleProgram
            {
                Name = a.Name,
                Steps = steps,
                NoMatchReply = string.IsNullOrWhiteSpace(a.NoMatchReply) ? null : a.NoMatchReply,
            },
        };
    }

    /// <summary>把 JSON 模板解析成工具参数（值保持字符串，好让 <c>{user.N}</c> 模板被规则大脑替换）。</summary>
    public static Dictionary<string, object?>? ParseArgsTemplate(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
            var args = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var p in doc.RootElement.EnumerateObject())
            {
                args[p.Name] = p.Value.ValueKind switch
                {
                    JsonValueKind.String => p.Value.GetString(),
                    // 显式转 object：否则 long/double 会被统一成 double（工具参数类型走样）
                    JsonValueKind.Number => p.Value.TryGetInt64(out var l) ? (object)l : p.Value.GetDouble(),
                    JsonValueKind.True => true,
                    JsonValueKind.False => false,
                    JsonValueKind.Null => null,
                    _ => p.Value.GetRawText(),
                };
            }
            return args;
        }
        catch (JsonException)
        {
            return null;   // 非法 JSON：忽略参数（工具走默认值）——保存时 UI 已校验
        }
    }

    /// <summary>校验正则是否能编译（保存前用，避免规则大脑运行时抛）。</summary>
    public static bool IsValidRegex(string? pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern)) return true;
        try { _ = new Regex(pattern); return true; }
        catch (ArgumentException) { return false; }
    }
}
