using System.Collections.ObjectModel;
using Aether.AI.App.Core;
using Aether.AI.Brains.Code.Compilation;
using Aether.AI.Harness.Tools;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;

namespace Aether.AI.App.ViewModels;

/// <summary>「我的 agent」列表行。</summary>
public sealed class CustomAgentRow
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Description { get; init; }
    public required string KindLabel { get; init; }
    public required bool IsRule { get; init; }

    /// <summary>是否开了记忆（长期记忆或工作笔记）——列表行上给个「记忆」标，免得要进编辑窗才知道。</summary>
    public bool HasMemory { get; init; }

    /// <summary>代码大脑编译错误（无错 = 空）。由 <c>ICodeCompilationService.LoadErrors</c> 回填。</summary>
    public string CompileError { get; init; } = string.Empty;

    public bool HasCompileError => !string.IsNullOrEmpty(CompileError);

    public IRelayCommand? EditCommand { get; set; }
    public IRelayCommand? DeleteCommand { get; set; }
    public CustomAgent? Source { get; set; }
}

/// <summary>工具多选项（LLM agent 的允许工具集 / 规则 agent 的动作工具）。</summary>
public sealed partial class ToolOption : ObservableObject
{
    public required string Name { get; init; }
    public required string Description { get; init; }
    public required string LevelLabel { get; init; }
    [ObservableProperty] private bool _isSelected;
}

/// <summary>
/// agent 管理窗 VM：目录里**全部** agent 的 CRUD（LLM / 规则 / 代码三类大脑）——没有「内置只读」档，
/// 首次运行播种的「通用助手」与用户自建的同权（可编辑、可删除）。
/// 保存即落 agents.json 并广播 —— 聊天页 agent 下拉热刷新，无需重启；
/// 代码大脑保存后立刻走 ⑫ 的编译管线，编译错误回填到行上。
/// </summary>
public partial class AgentsViewModel : ObservableObject
{
    private readonly CustomAgentStore _store;
    private readonly ToolRegistry _tools;
    private readonly LlmConfigState _llm;
    private readonly ICodeCompilationService _compilation;
    private readonly IUiDispatcher _dispatcher;

    /// <summary>新建代码大脑时的起步模板：一个能直接跑通的 Echo 示例。</summary>
    public const string CodeTemplate = """
        // 代码大脑：继承 CodeRuleBrain，重写 RunAsync()。框架负责把 Call(...) 变成工具调用并回填结果。
        // 可用：UserText（本轮用户消息）、History、Step、LastResult、RunCancellationToken
        // 注意：源码没有隐式 using——Task/List 等要自己 using。
        using System.Threading.Tasks;
        using Aether.AI.Brains.Code;

        public sealed class MyBrain : CodeRuleBrain
        {
            protected override async Task<string> RunAsync()
            {
                var echo = await Call("Echo", new { text = UserText });
                return "代码大脑收到：" + echo;
            }
        }
        """;

    public AgentsViewModel(IServiceProvider serviceProvider)
    {
        _store = serviceProvider.GetRequiredService<CustomAgentStore>();
        _tools = serviceProvider.GetRequiredService<ToolRegistry>();
        _llm = serviceProvider.GetRequiredService<LlmConfigState>();
        _compilation = serviceProvider.GetRequiredService<ICodeCompilationService>();
        _dispatcher = serviceProvider.GetRequiredService<IUiDispatcher>();

        // 只列**当前宿主可用**的工具：手机端不出现「执行命令」等桌面专属工具，桌面端不出现手机专属工具。
        // 过滤口径与 ⑫ ToolRegistry.ResolveAll 一致（同一 HostCapability 判定）——UI 与运行时不会说法不一。
        foreach (var meta in _tools.GetUsableMetas())
        {
            ToolOptions.Add(new ToolOption
            {
                Name = meta.Name,
                Description = meta.Description,
                LevelLabel = meta.ApprovalLevel.ToString(),
            });
        }

        Reload();
        _store.Changed += Reload;
        _llm.Changed += RefreshProfiles;
        // 编译在后台线程完成 → 回到 UI 线程刷新行上的错误提示。
        _compilation.Loaded += () => _dispatcher.Post(Reload);
    }

    // —————————————— 列表 ——————————————

    public ObservableCollection<CustomAgentRow> Agents { get; } = new();

    public bool HasAgents => Agents.Count > 0;

    private void Reload()
    {
        var errors = _compilation.LoadErrors;
        Agents.Clear();
        foreach (var a in _store.Agents)
        {
            Agents.Add(new CustomAgentRow
            {
                Id = a.Id,
                Name = a.Name,
                Description = a.Description,
                KindLabel = KindLabelOf(a.Kind),
                IsRule = a.Kind == CustomAgentKind.Rule,
                HasMemory = a.EnableLongTermMemory || a.EnableFileMemory,
                CompileError = errors.TryGetValue(a.Id, out var error) ? error : string.Empty,
                Source = a,
                EditCommand = EditAgentCommand,
                DeleteCommand = DeleteAgentCommand,
            });
        }
        OnPropertyChanged(nameof(HasAgents));
    }

    private static string KindLabelOf(CustomAgentKind kind) => kind switch
    {
        CustomAgentKind.Rule => "规则大脑",
        CustomAgentKind.Code => "代码大脑",
        _ => "LLM 大脑",
    };

    // —————————————— 共享选项 ——————————————

    public ObservableCollection<ToolOption> ToolOptions { get; } = new();

    /// <summary>规则 agent 的「动作工具」下拉：空 = 不调工具（直接回复）。</summary>
    public ObservableCollection<string> RuleToolNames { get; } = new();

    /// <summary>LLM agent 可选档案：第一项 = 跟随聊天页当前选中档案。</summary>
    public ObservableCollection<string> ModelProfiles { get; } = new() { FollowCurrentProfile };

    public const string FollowCurrentProfile = "（跟随当前选中档案）";

    private void RefreshProfiles()
    {
        var keep = FormModelProfile;
        ModelProfiles.Clear();
        ModelProfiles.Add(FollowCurrentProfile);
        foreach (var c in _llm.Configs) ModelProfiles.Add(c.Name);
        FormModelProfile = ModelProfiles.Contains(keep ?? string.Empty) ? keep : FollowCurrentProfile;
    }

    private void RefreshToolNames()
    {
        var keep = FormToolName;
        RuleToolNames.Clear();
        RuleToolNames.Add(NoToolChoice);
        foreach (var t in ToolOptions) RuleToolNames.Add(t.Name);
        FormToolName = RuleToolNames.Contains(keep ?? string.Empty) ? keep : NoToolChoice;
    }

    public const string NoToolChoice = "（不调用工具，直接回复）";

    // —————————————— 表单 ——————————————

    /// <summary>正在编辑的自定义 agent Id；null = 新建。</summary>
    [ObservableProperty] private string? _editingId;

    [ObservableProperty] private string _formName = string.Empty;
    [ObservableProperty] private string _formDescription = string.Empty;

    /// <summary>0 = LLM 大脑，1 = 规则大脑，2 = 代码大脑（与 <see cref="CustomAgentKind"/> 同序）。</summary>
    [ObservableProperty] private int _formKindIndex;

    [ObservableProperty] private string _formInstructions = string.Empty;
    [ObservableProperty] private string _formModelProfile = FollowCurrentProfile;
    [ObservableProperty] private bool _formRequireApproval = true;
    /// <summary>长期记忆（跨会话）：⑫ MemoryContextProvider + &lt;dataDir&gt;/memory.db。</summary>
    [ObservableProperty] private bool _formLongTermMemory;
    /// <summary>工作笔记：agent 自建笔记文件（&lt;dataDir&gt;/agent-notes/&lt;id&gt;/）。</summary>
    [ObservableProperty] private bool _formFileMemory;
    [ObservableProperty] private bool _formModePicker = true;

    [ObservableProperty] private string _formMatchRegex = string.Empty;
    [ObservableProperty] private string _formToolName = NoToolChoice;
    [ObservableProperty] private string _formToolArgs = "{\n  \"text\": \"{user.1}\"\n}";
    [ObservableProperty] private string _formReplyTemplate = string.Empty;
    [ObservableProperty] private string _formNoMatchReply = "（这条消息没有匹配到规则）";

    /// <summary>代码大脑源码（C#）。</summary>
    [ObservableProperty] private string _formSourceCode = string.Empty;

    [ObservableProperty] private string _statusText = string.Empty;

    public bool IsLlmForm => FormKindIndex == 0;
    public bool IsRuleForm => FormKindIndex == 1;
    public bool IsCodeForm => FormKindIndex == 2;
    /// <summary>LLM / 代码大脑都会调工具（规则大脑的工具走「动作工具」单选框）——决定「允许的工具」一栏显不显示。</summary>
    public bool IsToolForm => IsLlmForm || IsCodeForm;
    /// <summary>规则表单：选中了动作工具（决定「工具参数」一栏显不显示）。</summary>
    public bool UsesRuleTool => !string.IsNullOrEmpty(FormToolName) && FormToolName != NoToolChoice;
    public bool IsEditing => EditingId is not null;
    public string EditorTitle => IsEditing ? "编辑 agent" : "新建 agent";
    public string SaveButtonText => IsEditing ? "保存修改" : "创建 agent";
    public bool HasStatus => !string.IsNullOrWhiteSpace(StatusText);

    partial void OnFormKindIndexChanged(int value)
    {
        OnPropertyChanged(nameof(IsLlmForm));
        OnPropertyChanged(nameof(IsRuleForm));
        OnPropertyChanged(nameof(IsCodeForm));
        OnPropertyChanged(nameof(IsToolForm));
        // 切到代码大脑且还是空白 → 预填一个能跑通的示例（省得用户对着空编辑器猜契约）。
        if (value == 2 && string.IsNullOrWhiteSpace(FormSourceCode)) FormSourceCode = CodeTemplate;
    }

    partial void OnEditingIdChanged(string? value)
    {
        OnPropertyChanged(nameof(IsEditing));
        OnPropertyChanged(nameof(EditorTitle));
        OnPropertyChanged(nameof(SaveButtonText));
    }

    partial void OnStatusTextChanged(string value) => OnPropertyChanged(nameof(HasStatus));

    partial void OnFormToolNameChanged(string value) => OnPropertyChanged(nameof(UsesRuleTool));

    [RelayCommand]
    private void NewAgent()
    {
        EditingId = null;
        FormName = string.Empty;
        FormDescription = string.Empty;
        FormKindIndex = 0;
        FormInstructions = string.Empty;
        RefreshProfiles();
        FormModelProfile = FollowCurrentProfile;
        foreach (var t in ToolOptions) t.IsSelected = false;
        FormRequireApproval = true;
        FormModePicker = true;
        FormLongTermMemory = false;
        FormFileMemory = false;
        FormMatchRegex = string.Empty;
        RefreshToolNames();
        FormToolName = NoToolChoice;
        FormToolArgs = "{\n  \"text\": \"{user.1}\"\n}";
        FormReplyTemplate = string.Empty;
        FormNoMatchReply = "（这条消息没有匹配到规则）";
        FormSourceCode = string.Empty;
        StatusText = string.Empty;
    }

    [RelayCommand]
    private void EditAgent(CustomAgentRow row)
    {
        if (row.Source is not { } a) return;
        EditingId = a.Id;
        FormName = a.Name;
        FormDescription = a.Description;
        FormKindIndex = (int)a.Kind;
        FormInstructions = a.Instructions;
        RefreshProfiles();
        FormModelProfile = string.IsNullOrWhiteSpace(a.ModelProfile) ? FollowCurrentProfile : a.ModelProfile;
        foreach (var t in ToolOptions) t.IsSelected = a.Tools.Contains(t.Name, StringComparer.Ordinal);
        FormRequireApproval = a.RequireToolApproval;
        FormModePicker = a.EnableModePicker;
        FormLongTermMemory = a.EnableLongTermMemory;
        FormFileMemory = a.EnableFileMemory;
        FormMatchRegex = a.MatchRegex ?? string.Empty;
        RefreshToolNames();
        FormToolName = string.IsNullOrWhiteSpace(a.ToolName) ? NoToolChoice : a.ToolName;
        FormToolArgs = string.IsNullOrWhiteSpace(a.ToolArgsTemplate) ? "{\n  \"text\": \"{user.1}\"\n}" : a.ToolArgsTemplate;
        FormReplyTemplate = a.ReplyTemplate;
        FormNoMatchReply = string.IsNullOrWhiteSpace(a.NoMatchReply) ? string.Empty : a.NoMatchReply;
        FormSourceCode = a.SourceCode;
        StatusText = string.Empty;
    }

    [RelayCommand]
    private void DeleteAgent(CustomAgentRow row)
    {
        var list = _store.Agents.Where(a => a.Id != row.Id).ToList();
        _store.Save(list);
        if (EditingId == row.Id) NewAgent();
        StatusText = $"已删除「{row.Name}」";
    }

    [RelayCommand]
    private async Task SaveAgent()
    {
        var name = FormName.Trim();
        if (name.Length == 0) { StatusText = "❌ 名称必填"; return; }

        var kind = FormKindIndex switch
        {
            1 => CustomAgentKind.Rule,
            2 => CustomAgentKind.Code,
            _ => CustomAgentKind.Llm,
        };

        if (kind == CustomAgentKind.Llm && string.IsNullOrWhiteSpace(FormInstructions))
        {
            StatusText = "❌ LLM 大脑需要系统提示词（它就是 agent 的行为定义）";
            return;
        }
        if (kind == CustomAgentKind.Code && string.IsNullOrWhiteSpace(FormSourceCode))
        {
            StatusText = "❌ 代码大脑需要源码（继承 CodeRuleBrain 的类，重写 RunAsync）";
            return;
        }
        if (kind == CustomAgentKind.Rule)
        {
            if (!CustomAgentStore.IsValidRegex(FormMatchRegex))
            {
                StatusText = "❌ 触发正则写错了（编译失败）";
                return;
            }
            var usesTool = FormToolName != NoToolChoice;
            if (!usesTool && string.IsNullOrWhiteSpace(FormReplyTemplate))
            {
                StatusText = "❌ 不调工具时，回复模板必填";
                return;
            }
            if (usesTool && CustomAgentStore.ParseArgsTemplate(FormToolArgs) is null
                && !string.IsNullOrWhiteSpace(FormToolArgs))
            {
                StatusText = "❌ 工具参数不是合法 JSON 对象";
                return;
            }
        }

        var id = EditingId ?? CustomAgentStore.NewId(name, _store.Agents.Select(a => a.Id));
        var agent = new CustomAgent
        {
            Id = id,
            Name = name,
            Description = FormDescription.Trim(),
            Kind = kind,
            Instructions = FormInstructions.Trim(),
            ModelProfile = FormModelProfile == FollowCurrentProfile || string.IsNullOrWhiteSpace(FormModelProfile)
                ? null
                : FormModelProfile,
            Tools = ToolOptions.Where(t => t.IsSelected).Select(t => t.Name).ToList(),
            RequireToolApproval = FormRequireApproval,
            EnableModePicker = FormModePicker,
            EnableLongTermMemory = FormLongTermMemory,
            EnableFileMemory = FormFileMemory,
            MatchRegex = string.IsNullOrWhiteSpace(FormMatchRegex) ? null : FormMatchRegex,
            ToolName = FormToolName == NoToolChoice ? null : FormToolName,
            ToolArgsTemplate = FormToolName == NoToolChoice ? null : FormToolArgs,
            ReplyTemplate = FormReplyTemplate,
            NoMatchReply = FormNoMatchReply,
            SourceCode = FormSourceCode,
        };

        var list = _store.Agents.Where(a => a.Id != id).ToList();
        list.Add(agent);
        _store.Save(list);

        EditingId = id;
        if (kind != CustomAgentKind.Code)
        {
            StatusText = $"✅ 已保存「{name}」——聊天页 agent 下拉里立即可选";
            return;
        }

        // 代码大脑：保存即编译一轮，把编译结果直接告诉用户（失败也算保存成功——源码已落盘，改完再存即可）。
        StatusText = $"⏳ 已保存「{name}」，正在编译…";
        await Task.Run(() => _compilation.LoadAsync());
        Reload();
        StatusText = _compilation.LoadErrors.TryGetValue(id, out var compileError)
            ? $"⚠️ 已保存「{name}」，但编译失败：{compileError}"
            : $"✅ 已保存「{name}」，代码已编译——聊天页 agent 下拉里立即可选";
    }

    [RelayCommand]
    private void CancelEdit()
    {
        NewAgent();
        StatusText = string.Empty;
    }
}
