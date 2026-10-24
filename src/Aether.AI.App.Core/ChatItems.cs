using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Aether.AI.App.Core;

/// <summary>一轮对话的分组容器：用户请求 + 其下所有 agent 产出（思考/工具/答案/审批）。</summary>
public enum TurnStatus
{
    Working,
    Done,
    Error,
    AwaitingApproval,
}

/// <summary>
/// 聊天一轮的视觉分组：左侧竖线 + Children（用户气泡 + agent 卡片）+ 状态/耗时。
/// 纯 UI 聚合，不持久化（持久化按 AgentRun + typed 事件流，恢复时重建）。
/// </summary>
public partial class ChatTurnItem : ObservableObject
{
    /// <summary>用户请求文本（turn 头部展示）。</summary>
    public string Header { get; init; } = string.Empty;

    /// <summary>本轮所有消息项（用户气泡为第一项，其余为 agent 产出）。</summary>
    public ObservableCollection<ChatMessageItem> Children { get; } = new();

    private DateTime _startedAt = DateTime.Now;

    [ObservableProperty]
    private TurnStatus _status = TurnStatus.Working;

    [ObservableProperty]
    private string? _elapsedText;

    public bool IsWorking => Status == TurnStatus.Working;
    public bool IsDone => Status == TurnStatus.Done;
    public bool IsError => Status == TurnStatus.Error;
    public bool IsAwaitingApproval => Status == TurnStatus.AwaitingApproval;

    /// <summary>结束本轮：设状态 + 计算耗时。</summary>
    public void Finish(TurnStatus status)
    {
        Status = status;
        ElapsedText = $"{(DateTime.Now - _startedAt).TotalSeconds:F1}s";
    }

    partial void OnStatusChanged(TurnStatus value)
    {
        OnPropertyChanged(nameof(IsWorking));
        OnPropertyChanged(nameof(IsDone));
        OnPropertyChanged(nameof(IsError));
        OnPropertyChanged(nameof(IsAwaitingApproval));
    }
}

public enum ChatMessageSide
{
    Left,
    Right,
}

/// <summary>计划卡里的单条待办（完成项 UI 划横线）。</summary>
public partial class TodoItemVM : ObservableObject
{
    [ObservableProperty] private string _title = string.Empty;
    [ObservableProperty] private string? _description;
    [ObservableProperty] private bool _isComplete;
}

/// <summary>计划卡持久化/恢复用的待办 DTO（JSON 序列化）。</summary>
public sealed record TodoDto(string? Title, string? Desc, bool IsComplete);

/// <summary>
/// 聊天消息项：分段交错流的最小渲染单元——用户气泡 / 流式答案 / 思考卡 / 工具卡（call+result 合一）/
/// 计划卡 / 审批卡。流式期只更 <see cref="Content"/>（TextBlock 廉价），完成态切 markdown 渲染。
/// <para>自 monorepo ChatMessageItem port（2026-10-24）：剥离文件编辑 diff 闭环（FileEditSnapshot/
/// DiffModel/Undo——待 M1.5 以端口形式回归）与 Git 依赖；其余行为保持。</para>
/// </summary>
public partial class ChatMessageItem : ObservableObject
{
    public ChatMessageSide Side { get; init; }

    public string ItemType { get; init; } = "text";

    public string Time { get; init; } = DateTime.Now.ToString("HH:mm:ss");

    public string Sender { get; init; } = string.Empty;

    [ObservableProperty]
    private string _content = string.Empty;

    [ObservableProperty]
    private string? _markdownContent;

    [ObservableProperty]
    private bool _isStreaming;

    [ObservableProperty]
    private string? _toolName;

    /// <summary>工具调用 Id，用于把 call 与 result 配对到同一条消息。</summary>
    public string CallId { get; set; } = string.Empty;

    [ObservableProperty]
    private Dictionary<string, object>? _input;

    [ObservableProperty]
    private Dictionary<string, object>? _output;

    /// <summary>结果渲染类型：text/code/table/error/json。由 <see cref="NormalizeToolResult"/> 据 Output 归一。</summary>
    [ObservableProperty]
    private string _resultKind = "text";

    /// <summary>从 Output 归一出的主结果文本（取 result/output/content 等键的字符串值）。</summary>
    [ObservableProperty]
    private string? _resultText;

    /// <summary>结果是否超长（>20 行），控制默认折叠+限高。</summary>
    [ObservableProperty]
    private bool _isResultLong;

    /// <summary>结果行数（用于"· 142 行"徽标）。</summary>
    [ObservableProperty]
    private int _resultLineCount;

    /// <summary>工具结果已返回（call+result 合并到一条，完成后置 true）。</summary>
    [ObservableProperty]
    private bool _isToolCompleted;

    [ObservableProperty]
    private bool _isExpanded;

    [ObservableProperty]
    private bool _isStreamingExpanded = true;

    /// <summary>本条文本是"思考段"（agent 运行中途的推理），非最终回答。分段交错展示用。</summary>
    [ObservableProperty]
    private bool _isReasoning;

    /// <summary>本段持有 TextReasoningContent（模型推理），与 TextContent 的思考/答案段区分；推理段永不翻为答案。</summary>
    [ObservableProperty]
    private bool _isReasoningContent;

    /// <summary>审批状态：0=待审 1=已批准 2=已拒绝。仅 ItemType="approval" 用。</summary>
    [ObservableProperty]
    private int _approvalState;

    /// <summary>段创建时刻，用于计算推理/答案耗时（UI 展示"思考 · 2s"）。</summary>
    private DateTime _startedAt = DateTime.Now;

    /// <summary>流式停止后填入的耗时摘要（如 "2s"），仅展示用。</summary>
    [ObservableProperty]
    private string? _elapsedText;

    /// <summary>计划卡的待办列表（仅 ItemType="plan" 用）。完成项 UI 划横线。</summary>
    public ObservableCollection<TodoItemVM> Todos { get; } = new();

    public bool IsUser => Side == ChatMessageSide.Right;

    public bool IsText => ItemType == "text";

    /// <summary>工具步骤（调用+结果合并为一条）。</summary>
    public bool IsToolStep => ItemType == "tool";

    /// <summary>计划卡（todos_* 调用渲染）。</summary>
    public bool IsPlanCard => ItemType == "plan";

    /// <summary>审批卡（工具调用需审批）。</summary>
    public bool IsApprovalCard => ItemType == "approval";

    public bool IsUserText => IsUser && IsText;

    public bool IsAgentStreamingText => !IsUser && IsText && IsStreaming;

    /// <summary>流式推理段（TextReasoningContent）：等宽"思考中…"卡，淡色。</summary>
    public bool IsAgentStreamingReasoning => !IsUser && IsText && IsStreaming && IsReasoningContent;

    /// <summary>流式答案段（TextContent）：实时纯文本。</summary>
    public bool IsAgentStreamingAnswer => !IsUser && IsText && IsStreaming && !IsReasoningContent;

    /// <summary>已结束的思考段：等宽可折叠"思考"卡片（非最终回答）。</summary>
    public bool IsAgentThinkingCard => !IsUser && IsText && !IsStreaming && IsReasoning;

    /// <summary>最终回答：markdown 气泡（非思考段）。</summary>
    public bool IsAgentMarkdownText => !IsUser && IsText && !IsStreaming && !IsReasoning;

    public bool IsApprovalPending => IsApprovalCard && ApprovalState == 0;
    public bool IsApprovalApproved => IsApprovalCard && ApprovalState == 1;
    public bool IsApprovalRejected => IsApprovalCard && ApprovalState == 2;

    /// <summary>结果渲染分支（按 ResultKind 挑模板，XAML 用 IsVisible 绑定）。</summary>
    public bool IsResultText => IsToolStep && ResultKind == "text";
    public bool IsResultCode => IsToolStep && ResultKind == "code";
    public bool IsResultError => IsToolStep && ResultKind == "error";
    public bool IsResultTable => IsToolStep && ResultKind == "table";
    public bool IsResultJson => IsToolStep && ResultKind == "json";

    /// <summary>输入一行摘要（折叠态展示），如 skill=hello-py  arguments=["张三"]。</summary>
    public string ArgSummary => Summarize(Input);

    /// <summary>输出一行摘要（折叠态展示）。</summary>
    public string ResultSummary => Summarize(Output);

    /// <summary>折叠态结果预览：结果首行截断 60 字（无需展开即可看到工具产出）。</summary>
    public string ResultPreview
    {
        get
        {
            if (string.IsNullOrEmpty(ResultText)) return string.Empty;
            var first = ResultText.Split('\n')[0].Trim();
            return first.Length > 60 ? first[..60] + "…" : first;
        }
    }

    public bool HasResultPreview => IsToolCompleted && !string.IsNullOrEmpty(ResultPreview);

    /// <summary>主参数（折叠态"动词+对象"一行用）：按 path/file/command/query… 优先键取首个标量，截断 30 字。</summary>
    public string MainArg => PickMainArg(Input);

    partial void OnInputChanged(Dictionary<string, object>? value)
    {
        OnPropertyChanged(nameof(ArgSummary));
        OnPropertyChanged(nameof(MainArg));
    }

    partial void OnOutputChanged(Dictionary<string, object>? value)
    {
        OnPropertyChanged(nameof(ResultSummary));
        NormalizeToolResult(value);
    }

    /// <summary>
    /// 据 Output 字典归一结果展示信息：取主结果字符串（result/output/content/text/value/message/data 键），
    /// 判 <see cref="ResultKind"/>（含换行→code；短串含 error/失败/异常→error；多键 dict→table；否则 json 兜底）。
    /// 超过 20 行置 <see cref="IsResultLong"/>。由各工具结果渲染模板消费。
    /// </summary>
    private void NormalizeToolResult(Dictionary<string, object>? output)
    {
        ResultKind = "text"; ResultText = null; IsResultLong = false; ResultLineCount = 0;
        if (output is null || output.Count == 0) return;

        string? main = null;
        foreach (var key in new[] { "result", "output", "content", "text", "value", "message", "data" })
        {
            if (output.TryGetValue(key, out var v) && ToStr(v) is { } s && s.Length > 0) { main = s; break; }
        }

        if (main is not null)
        {
            ResultText = main;
            ResultLineCount = main.Count(c => c == '\n') + 1;
            bool hasError = main.Contains("error", StringComparison.OrdinalIgnoreCase)
                            || main.Contains("失败") || main.Contains("异常");
            if (hasError && main.Length < 300) ResultKind = "error";
            else if (main.Contains('\n')) ResultKind = "code";
            else ResultKind = "text";
            IsResultLong = ResultLineCount > 20;
        }
        else if (output.Count > 1)
        {
            ResultKind = "table";
        }
        else
        {
            ResultKind = "json"; // 兜底
        }
    }

    private static string? ToStr(object? v) => v switch
    {
        null => null,
        string s => s,
        System.Text.Json.JsonElement je => je.ValueKind == System.Text.Json.JsonValueKind.String
            ? je.GetString()
            : (je.ValueKind == System.Text.Json.JsonValueKind.Null ? null : je.ToString()),
        _ => v.ToString(),
    };

    partial void OnApprovalStateChanged(int value)
    {
        OnPropertyChanged(nameof(IsApprovalPending));
        OnPropertyChanged(nameof(IsApprovalApproved));
        OnPropertyChanged(nameof(IsApprovalRejected));
    }

    partial void OnResultKindChanged(string value)
    {
        OnPropertyChanged(nameof(IsResultText));
        OnPropertyChanged(nameof(IsResultCode));
        OnPropertyChanged(nameof(IsResultError));
        OnPropertyChanged(nameof(IsResultTable));
        OnPropertyChanged(nameof(IsResultJson));
    }

    partial void OnResultTextChanged(string? value)
    {
        OnPropertyChanged(nameof(ResultPreview));
        OnPropertyChanged(nameof(HasResultPreview));
    }

    partial void OnIsToolCompletedChanged(bool value) => OnPropertyChanged(nameof(HasResultPreview));

    private static string Summarize(Dictionary<string, object>? dict)
    {
        if (dict is null || dict.Count == 0) return string.Empty;
        var parts = new List<string>();
        foreach (var kv in dict)
            parts.Add($"{kv.Key}={FormatValue(kv.Value)}");
        var s = string.Join("  ", parts);
        return s.Length > 80 ? s[..80] + "…" : s;
    }

    /// <summary>主参数：优先键取首个标量，截断 30 字（折叠态"ToolName(主参数)"用）。</summary>
    private static string PickMainArg(Dictionary<string, object>? input)
    {
        if (input is null || input.Count == 0) return string.Empty;
        foreach (var key in new[] { "path", "file", "command", "city", "symbol", "query", "name", "title", "id", "input" })
        {
            if (input.TryGetValue(key, out var v) && ToStr(v) is { } s && s.Length > 0)
                return s.Length > 30 ? s[..30] + "…" : s;
        }
        foreach (var kv in input)
        {
            if (ToStr(kv.Value) is { } s2 && s2.Length > 0)
                return s2.Length > 30 ? s2[..30] + "…" : s2;
        }
        return string.Empty;
    }

    private static string FormatValue(object? v)
    {
        if (v is null) return "null";
        if (v is string s) return s;
        if (v is System.Text.Json.JsonElement je)
        {
            return je.ValueKind switch
            {
                System.Text.Json.JsonValueKind.String => je.GetString() ?? string.Empty,
                System.Text.Json.JsonValueKind.Null => "null",
                _ => je.ToString(),
            };
        }
        return v.ToString() ?? string.Empty;
    }

    partial void OnIsStreamingChanged(bool value)
    {
        // 流式停止时计算耗时摘要（供推理卡"思考 · 2s"展示）。
        if (!value) ElapsedText = $"{(DateTime.Now - _startedAt).TotalSeconds:F0}s";
        OnPropertyChanged(nameof(IsAgentStreamingText));
        OnPropertyChanged(nameof(IsAgentStreamingReasoning));
        OnPropertyChanged(nameof(IsAgentStreamingAnswer));
        OnPropertyChanged(nameof(IsAgentMarkdownText));
        OnPropertyChanged(nameof(IsAgentThinkingCard));
    }

    partial void OnIsReasoningChanged(bool value)
    {
        OnPropertyChanged(nameof(IsAgentThinkingCard));
        OnPropertyChanged(nameof(IsAgentMarkdownText));
    }

    partial void OnIsReasoningContentChanged(bool value)
    {
        OnPropertyChanged(nameof(IsAgentStreamingReasoning));
        OnPropertyChanged(nameof(IsAgentStreamingAnswer));
    }
}
