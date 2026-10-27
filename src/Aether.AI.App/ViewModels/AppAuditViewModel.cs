using System.Collections.ObjectModel;
using Aether.AI.App.Core;
using Aether.AppAudit.AI;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;

namespace Aether.AI.App.ViewModels;

/// <summary>一行：一个名字 + 对应的站点 + 站点类型 + 博彩结论（都是 agent 写回来的）。</summary>
public sealed class AppAuditRow
{
    public required string Id { get; init; }
    public required string AppName { get; init; }

    /// <summary>对应站点（null = agent 没给出站点）。</summary>
    public string? PrimaryUrl { get; init; }

    /// <summary>站点类型（agent 给的短标签；null = 未知/未判定）。</summary>
    public string? SiteType { get; init; }

    /// <summary>记录过的候选数（agent 给站点时会补一条，所以判过的通常 ≥1）。</summary>
    public int CandidateCount { get; init; }

    public required string VerdictLabel { get; init; }
    public required string StageLabel { get; init; }
    public string? Reason { get; init; }
    public string? ConfidenceText { get; init; }
    public string? Error { get; init; }

    /// <summary>判过几次（&gt;1 = 重判过，鼠标悬停看历次结论）。</summary>
    public int AttemptCount { get; init; }

    /// <summary>历次判定的可读清单（行 ToolTip；没判过就是 null，不弹空框）。</summary>
    public string? HistoryText { get; init; }

    /// <summary>在任务池里等 agent 处理。</summary>
    public bool IsQueued { get; init; }

    public bool IsGambling { get; init; }
    public bool IsClean { get; init; }
    public bool IsUncertain { get; init; }
    public bool HasVerdict { get; init; }
    public bool HasCandidate => !string.IsNullOrEmpty(PrimaryUrl);
    public bool HasSecondLine => true;   // 第二行固定显示「状态 · 理由/错误」，空则只显示状态

    public string SecondLine
    {
        get
        {
            var prefix = AttemptCount > 1 ? $"{StageLabel} · 第 {AttemptCount} 次判定" : StageLabel;
            if (!string.IsNullOrWhiteSpace(Error)) return $"{prefix} · ⚠ {Error}";
            return string.IsNullOrWhiteSpace(Reason) ? prefix : $"{prefix} · {Reason}";
        }
    }

    public string CandidatesText => CandidateCount switch
    {
        0 => "（还没有站点）",
        1 => PrimaryUrl!,
        _ => $"{PrimaryUrl}  +{CandidateCount - 1}",
    };

    /// <summary>类型列：有的显示类型，没有的显示「—」。</summary>
    public string TypeText => string.IsNullOrWhiteSpace(SiteType) ? "—" : SiteType!;

    /// <summary>行上的动作（由 VM 注入，参数是本行）：单独把这个名字重新发布成任务。</summary>
    public IRelayCommand<AppAuditRow>? RepublishCommand { get; set; }
}

/// <summary>
/// 「应用审查」页 VM——**只发布任务、只显示结论**，不做任何判定。
/// <para>流程：粘贴名字 → 导入（待发布）→ 点「发布任务」进任务池 → agent 用 <c>ListAuditTasks</c> 取任务、
/// 自己找证据判定、用 <c>SaveAppVerdict</c> 写回 → 这个页面在 <see cref="AppAuditStore.Changed"/> 上刷新。</para>
/// <para>所以判定标准、判定方式、判定者都不在这个仓里：改口径改 skill（<c>skills/app-audit/SKILL.md</c>），
/// 换判定方换 agent，页面和存储都不用动。</para>
/// </summary>
public partial class AppAuditViewModel : ObservableObject
{
    private readonly AppAuditStore _store;
    private readonly AppAuditService _service;
    private readonly IUiDispatcher _dispatcher;

    public ObservableCollection<AppAuditRow> Rows { get; } = new();

    /// <summary>粘贴导入的文本框（一行一个名字，也允许 `名字 https://…`）。</summary>
    [ObservableProperty] private string _importText = string.Empty;

    [ObservableProperty] private string _statusText = string.Empty;

    /// <summary>「发布任务」是否连已判过的一起发布（复核任务：结论不清空，留在历史里）。</summary>
    [ObservableProperty] private bool _includeJudged;

    public AppAuditViewModel(IServiceProvider serviceProvider)
    {
        _store = serviceProvider.GetRequiredService<AppAuditStore>();
        _service = serviceProvider.GetRequiredService<AppAuditService>();
        _dispatcher = serviceProvider.GetRequiredService<IUiDispatcher>();

        // 结论可能在后台线程写回（agent 工具调用）→ 统一回 UI 线程重建行。
        _store.Changed += () => _dispatcher.Post(Reload);
        Reload();
    }

    // —————————————— 列表 ——————————————

    public bool HasRows => Rows.Count > 0;

    /// <summary>页面顶部的流程说明（写清「谁判」，免得以为这页自己会判）。</summary>
    public string FlowHint =>
        "这页只发布任务和显示结论：发布后由 agent 读取任务、自己查证据判定、把结论写回来"
        + "（在聊天里说「处理应用审查任务」即可；判定口径在 skills/app-audit/SKILL.md）。";

    private void Reload()
    {
        Rows.Clear();
        foreach (var e in _store.Entries.OrderBy(e => e.AppName, StringComparer.CurrentCultureIgnoreCase))
        {
            Rows.Add(new AppAuditRow
            {
                Id = e.Id,
                AppName = e.AppName,
                PrimaryUrl = e.PrimaryUrl,
                SiteType = e.SiteType,
                CandidateCount = e.Candidates.Count,
                VerdictLabel = VerdictLabelOf(e),
                StageLabel = StageLabelOf(e),
                Reason = e.Reason,
                ConfidenceText = e.Confidence is { } c ? $"{c:P0}" : null,
                Error = e.LastError,
                AttemptCount = e.Attempts.Count,
                HistoryText = BuildHistory(e),
                IsQueued = e.IsQueued,
                IsGambling = !e.IsQueued && e.Verdict == AuditVerdict.Gambling,
                IsClean = !e.IsQueued && e.Verdict == AuditVerdict.NotGambling,
                IsUncertain = !e.IsQueued && e.Verdict == AuditVerdict.Inconclusive,
                HasVerdict = e.HasVerdict,
                RepublishCommand = RepublishRowCommand,
            });
        }
        OnPropertyChanged(nameof(HasRows));
        OnPropertyChanged(nameof(SummaryText));
        OnPropertyChanged(nameof(PublishButtonText));
    }

    /// <summary>历次判定：时间 · 结论 · 类型 · 置信度 · 谁判的 · 理由（最近 5 次）。</summary>
    private static string? BuildHistory(AppAuditEntry e)
    {
        if (e.Attempts.Count == 0) return null;
        var lines = e.Attempts.TakeLast(5).Select(a =>
            $"{a.At.ToLocalTime():MM-dd HH:mm} · {VerdictLabelOf(a.Verdict)}"
            + (string.IsNullOrWhiteSpace(a.SiteType) ? "" : $" · {a.SiteType}")
            + (a.Confidence is { } c ? $" · {c:P0}" : "")
            + (string.IsNullOrWhiteSpace(a.Judge) ? "" : $" · {a.Judge}")
            + $"\n    {a.Reason}");
        return "历次判定（最近 5 次）:\n" + string.Join("\n", lines);
    }

    /// <summary>
    /// 徽章文字。**在池里的行不显示「博彩」，显示「待处理/待复核」**——
    /// 否则复核中的记录会被读成「当前结论就是它」。
    /// </summary>
    private static string VerdictLabelOf(AppAuditEntry e)
        => e.IsQueued
            ? (e.Attempts.Count > 0 ? "待复核" : "待处理")
            : VerdictLabelOf(e.Verdict);

    private static string VerdictLabelOf(AuditVerdict verdict) => verdict switch
    {
        AuditVerdict.Gambling => "博彩",
        AuditVerdict.NotGambling => "非博彩",
        AuditVerdict.Inconclusive => "不确定",
        _ => "未判定",
    };

    private static string StageLabelOf(AppAuditEntry e) => e switch
    {
        { IsQueued: true } when e.Attempts.Count > 0 => "已发布 · 待复核",
        { IsQueued: true } => "已发布 · 等 agent 处理",
        { Stage: AuditStage.Done } => "已完成",
        { Stage: AuditStage.Error } => "出错",
        _ => "待发布",
    };

    /// <summary>汇总一行：总数 / 任务池 / 博彩 / 非博彩 / 不确定 / 待发布。</summary>
    public string SummaryText
    {
        get
        {
            var entries = _store.Entries;
            if (entries.Count == 0) return "还没有记录——把名字粘到下面一行一条，点「导入清单」";
            int Count(AuditVerdict v) => entries.Count(e => e.Verdict == v);
            return $"共 {entries.Count} 个应用 · 任务池 {entries.Count(e => e.IsQueued)} · "
                   + $"待发布 {entries.Count(e => e.IsDraft)} · "
                   + $"博彩 {Count(AuditVerdict.Gambling)} · 非博彩 {Count(AuditVerdict.NotGambling)} · "
                   + $"不确定 {Count(AuditVerdict.Inconclusive)} · 未判定 {Count(AuditVerdict.Unknown)}";
        }
    }

    /// <summary>「发布任务」按钮上的计数（一眼看出这次会发几条）。</summary>
    public string PublishButtonText
    {
        get
        {
            var n = _store.Entries.Count(e => !e.IsQueued && (IncludeJudged || !e.HasVerdict));
            return n == 0 ? "发布任务" : $"发布任务（{n}）";
        }
    }

    public bool HasStatus => !string.IsNullOrWhiteSpace(StatusText);

    partial void OnStatusTextChanged(string value) => OnPropertyChanged(nameof(HasStatus));

    partial void OnIncludeJudgedChanged(bool value) => OnPropertyChanged(nameof(PublishButtonText));

    // —————————————— 清单来源（只有一条：用户手上有名字） ——————————————

    /// <summary>导入清单：一行一个名字（也允许 `名字 https://…`）。导入后是「待发布」。</summary>
    [RelayCommand]
    private void ImportPasted()
    {
        var parsed = AppNameList.Parse(ImportText);
        if (parsed.Count == 0)
        {
            StatusText = "没有解析出名字（一行一个，例如 Sunwin 或 Sunwin https://example.com）";
            return;
        }
        var added = _store.MergeNames(parsed, DateTimeOffset.Now);
        ImportText = string.Empty;
        StatusText = added == 0
            ? $"导入：{parsed.Count} 条都是已有记录（已有结论和任务状态保持不动）"
            : $"导入完成：新增 {added} 条（解析到 {parsed.Count} 条）——点「发布任务」交给 agent";
    }

    [RelayCommand]
    private void Clear()
    {
        _store.Clear();
        StatusText = "已清空审查记录";
    }

    // —————————————— 发布任务（这页唯一的写操作） ——————————————

    /// <summary>
    /// 发布任务：把待发布的（勾了「含已判过的」时连已判过的一起）放进任务池。
    /// **不发请求、不判定**——只改状态，等 agent 来领。
    /// </summary>
    [RelayCommand]
    private void Publish()
    {
        if (!HasRows) { StatusText = "还没有记录——先导入一份名字清单"; return; }

        var published = _service.Publish(IncludeJudged);
        StatusText = published == 0
            ? (IncludeJudged
                ? "没有可发布的了（都在任务池里）"
                : "没有待发布的任务（已判过的没动；勾上「含已判过的」可以发布复核任务）")
            : $"已发布 {published} 条任务——到聊天里说「处理应用审查任务」，agent 会读取并写回结论";
    }

    /// <summary>单条重发：把这一条重新放进任务池（结论留在历史里，页面上标「待复核」）。</summary>
    [RelayCommand]
    private void RepublishRow(AppAuditRow? row)
    {
        if (row is null) return;
        StatusText = _service.Republish(row.Id)
            ? $"已重新发布「{row.AppName}」——agent 会再判一次，新结论写进历史"
            : $"❌ 找不到「{row.AppName}」";
    }
}
