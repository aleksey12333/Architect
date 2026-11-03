using System.Collections.ObjectModel;
using AIArchitectStudio.Models;
using AIArchitectStudio.Services;
using AIArchitectStudio.Services.AI;
using AIArchitectStudio.Services.Verification;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml.Controls;

namespace AIArchitectStudio.ViewModels;

/// <summary>Строка «Было → Стало» карточки правки.</summary>
public sealed record ReviewChangeRow(string Parameter, string Before, string After);

/// <summary>Карточка правки в листе согласования: отметка «принять», дефект, действие, параметры и прогноз.</summary>
public sealed partial class ReviewFixRow : ObservableObject
{
    private readonly Action _changed;

    public ReviewFixRow(ProposedBimFix fix, System.Globalization.CultureInfo culture, Action changed)
    {
        Fix = fix;
        _changed = changed;
        IsAccepted = fix.IsAccepted;
        Changes = fix.Changes.Where(c => c.Parameter != "K_исп").Select(c => new ReviewChangeRow(c.Parameter, c.Before, c.After)).ToList();
        double dk = fix.DeltaKisp;
        KText = $"K_исп {VerificationText.K(fix.KBefore)} → {VerificationText.K(fix.KAfter)}";
        DeltaKText = (dk <= 0 ? "−" : "+") + VerificationText.K(Math.Abs(dk));
        CostText = CostDelta(fix.DeltaCostRub, culture);
        KColorHex = fix.KAfter <= 1.0 ? "#30D158" : "#FF9F0A";
        SeverityGlyph = fix.Severity == VerificationSeverity.Critical ? "" : "";
        SeverityColorHex = fix.Severity == VerificationSeverity.Critical ? "#FF453A" : "#FF9F0A";
    }

    public ProposedBimFix Fix { get; }
    public string RoleTitle => Fix.AuthorRole;
    public string RoleColorHex => Fix.RoleColorHex;
    public string RoleGlyph => Fix.RoleGlyph;
    public string ElementName => Fix.ElementName;
    public string Category => Fix.Category;
    public string Code => Fix.Code;
    public string Defect => Fix.DefectDescription;
    public string Action => Fix.ProposedActionDescription;
    public string Norm => Fix.NormReference;
    public IReadOnlyList<ReviewChangeRow> Changes { get; }
    public string KText { get; }
    public string DeltaKText { get; }
    public string KColorHex { get; }
    public string CostText { get; }
    public string SeverityGlyph { get; }
    public string SeverityColorHex { get; }

    [ObservableProperty]
    public partial bool IsAccepted { get; set; }

    partial void OnIsAcceptedChanged(bool value)
    {
        Fix.IsAccepted = value;
        _changed();
    }

    public static string CostDelta(double rub, System.Globalization.CultureInfo culture)
    {
        if (Math.Abs(rub) < 0.5) return "0 ₽";
        string sign = rub > 0 ? "+" : "−";
        double a = Math.Abs(rub);
        return a >= 1_000_000 ? $"{sign}{(a / 1_000_000).ToString("0.00", culture)} млн ₽"
            : a >= 10_000 ? $"{sign}{(a / 1000).ToString("0", culture)} тыс. ₽"
            : $"{sign}{a.ToString("N0", culture)} ₽";
    }
}

/// <summary>
/// Проверка достоверности ответа внешнего генератора в Коллоквиуме: шлюз <see cref="VerificationGateService"/> (C#-проверщики
/// и локальный аудитор), лист согласования правок с отметками, «Принять выбранные правки» (один шаг Undo), «Оставить исходный
/// вариант», «Отменить всё» (откат генерации), подсветка элемента правки во вьюпорте и индикатор достоверности в доке.
/// </summary>
public sealed partial class MainViewModel
{
    private int _reviewVersion;

    public ObservableCollection<ReviewFixRow> ReviewFixes { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsReviewOpen), nameof(ReviewSubtitle), nameof(ReviewChecksText), nameof(ReviewChecksDetail), nameof(ReviewFixedText),
        nameof(ReviewKText), nameof(ReviewKColorHex), nameof(ReviewCostText), nameof(ReviewCostDetail), nameof(HasReviewFixes), nameof(ReviewUnresolvedText),
        nameof(IsCouncilTimelineVisible), nameof(ReviewFooterText))]
    public partial VerificationGateReport? Review { get; set; }

    public bool IsReviewOpen => Review is not null;
    public bool HasReviewFixes => ReviewFixes.Count > 0;

    /// <summary>Лента Коллоквиума скрывается, пока открыт лист согласования (то же место экрана).</summary>
    public bool IsCouncilTimelineVisible => IsCouncilTimelineOpen && Review is null;

    partial void OnIsCouncilTimelineOpenChanged(bool value)
    {
        OnPropertyChanged(nameof(IsCouncilTimelineVisible));
        RefreshCouncilDock();
    }

    partial void OnReviewChanged(VerificationGateReport? value)
    {
        // Новый лист согласования разворачивает боковой док (свёрнутая полоса — только по выбору пользователя).
        if (value is not null) IsCouncilDockCollapsed = false;
        RefreshCouncilDock();
    }

    [ObservableProperty]
    public partial bool IsReviewBusy { get; set; }

    public string ReviewSubtitle => Review is null ? string.Empty
        : Loc.Format("ReviewSubtitle", Review.Source, Review.CreatedAt.ToString("HH:mm", Loc.Culture), Review.ElapsedMs.ToString("0", Loc.Culture));

    public string ReviewChecksText => Review is null ? string.Empty : $"{Review.VerifiersPassed} / {Review.VerifiersRun}";

    public string ReviewChecksDetail => Review is null ? string.Empty
        : Loc.Format("ReviewChecksDetail", Review.GeneratorReport.CriticalCount, Review.GeneratorReport.WarningCount);

    public string ReviewFixedText => Review is null ? string.Empty : Review.Fixes.Count.ToString(Loc.Culture);

    public string ReviewKText => Review is null ? string.Empty : $"{VerificationText.K(Review.KBefore)} → {VerificationText.K(ProjectedK)}";

    public string ReviewKColorHex => ProjectedK <= 1.0 ? "#30D158" : "#FF9F0A";

    public string ReviewCostText => Review is null ? string.Empty : ReviewFixRow.CostDelta(ProjectedCost - Review.CostBeforeRub, Loc.Culture);

    public string ReviewCostDetail => Review is null ? string.Empty
        : $"{(Review.CostBeforeRub / 1_000_000).ToString("0.00", Loc.Culture)} → {(ProjectedCost / 1_000_000).ToString("0.00", Loc.Culture)} {Loc.Get("ReviewMillionRub")}";

    public string ReviewUnresolvedText => Review is { Unresolved.Count: > 0 } r ? Loc.Format("ReviewUnresolved", r.Unresolved.Count(f => f.Severity != VerificationSeverity.Info)) : string.Empty;

    /// <summary>Прогноз по отмеченным правкам (пересчитывается при смене отметок).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ReviewKText), nameof(ReviewKColorHex), nameof(ReviewCostText), nameof(ReviewCostDetail), nameof(ReviewFooterText))]
    public partial double ProjectedK { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ReviewCostText), nameof(ReviewCostDetail), nameof(ReviewFooterText))]
    public partial double ProjectedCost { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ReviewFooterText))]
    public partial int ProjectedClashes { get; set; }

    public string ReviewFooterText => Review is null ? string.Empty
        : Loc.Format("ReviewFooter", ReviewFixes.Count(r => r.IsAccepted), ReviewFixes.Count, VerificationText.K(ProjectedK), ProjectedClashes,
            ReviewFixRow.CostDelta(ProjectedCost - Review.CostBeforeRub, Loc.Culture));

    // ───────────────────────────── Индикатор достоверности в доке ─────────────────────────────

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasVerificationBadge))]
    public partial string VerificationBadgeText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string VerificationBadgeColorHex { get; set; } = "#30D158";

    public bool HasVerificationBadge => VerificationBadgeText.Length > 0;

    [ObservableProperty]
    public partial bool IsVerifyExternal { get; set; } = true;

    partial void OnIsVerifyExternalChanged(bool value)
    {
        if (Settings.Current.VerifyExternalDesigns != value) SaveSettingsSafe(s => s.VerifyExternalDesigns = value);
    }

    /// <summary>Генератор внешний: облачный API или веб-аккаунт (локальные модели — сами аудиторы).</summary>
    private static bool IsExternalGenerator(AiModelOption model) => model.IsWeb || !model.IsOnDevice;

    /// <summary>
    /// Шлюз достоверности после ответа внешнего генератора: снимок живой сцены уходит в C#-проверщики и локальный аудитор
    /// в фоне, результат — лист согласования правок (или зелёный индикатор, если замечаний нет).
    /// </summary>
    public async Task RunVerificationGateAsync(string source, ChatMessageModel? message = null)
    {
        if (Scene.Elements.Count == 0) return;
        int version = ++_reviewVersion;
        var sandbox = DesignFixEngine.Sandbox(Scene);
        var loads = Settings.Current.StructuralLoads.Clone();
        var rates = new GateCostRates(Settings.Current.ConcretePricePerM3, Settings.Current.RebarPricePerTon);
        var location = SolarLocations.Resolve(Settings.Current.SolarRegionId);
        int depth = Scene.Transactions.UndoCount;
        IsReviewBusy = true;
        var entry = new AgentDebateEntry(AiAgentRole.NormsAndSafetyVerifier, Loc.Get("ReviewAuditorModel"), Loc.IsRussian);
        PostCouncilEntry(entry);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        VerificationGateReport gate;
        try
        {
            gate = await Task.Run(() => VerificationGateService.Inspect(sandbox, loads, source, rates, location: location, undoDepth: depth));
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            App.WriteCrashLog("verification-gate", ex);
            entry.Complete(AgentEntryStatus.Failed, ex.Message, sw.Elapsed);
            IsReviewBusy = false;
            return;
        }
        IsReviewBusy = false;
        if (version != _reviewVersion) return;
        entry.Found = gate.GeneratorReport.Findings.Count(f => f.Severity != VerificationSeverity.Info);
        entry.Fixed = gate.Fixes.Count;
        entry.Complete(gate.Fixes.Count > 0 ? AgentEntryStatus.Decision : AgentEntryStatus.Done,
            gate.Fixes.Count > 0 ? Loc.Format("ReviewTimelineFixes", gate.VerifiersRun, gate.Fixes.Count, VerificationText.K(gate.KBefore), VerificationText.K(gate.KAfterAll))
                : Loc.Format("ReviewTimelineClean", gate.VerifiersRun, VerificationText.K(gate.KBefore), gate.ClashesBefore), sw.Elapsed);
        ShowReview(gate, message);
    }

    /// <summary>Открыть лист согласования (без правок — только индикатор достоверности).</summary>
    public void ShowReview(VerificationGateReport gate, ChatMessageModel? message = null)
    {
        ReviewFixes.Clear();
        foreach (var fix in gate.Fixes) ReviewFixes.Add(new ReviewFixRow(fix, Loc.Culture, OnReviewSelectionChanged));
        _reviewMessage = message;
        SetBadge(gate, afterFixes: false);
        if (gate.Fixes.Count == 0)
        {
            Review = null;
            return;
        }
        ProjectedK = gate.KAfterAll;
        ProjectedCost = gate.CostAfterAllRub;
        ProjectedClashes = gate.ClashesAfterAll;
        Review = gate;
        OnPropertyChanged(nameof(HasReviewFixes));
        SetCouncilHighlights(gate.Fixes.SelectMany(f => f.AffectedElementIds.Take(1).Select(id => (id, f.Severity == VerificationSeverity.Critical))).Distinct().ToList());
        ChatMessages.Add(ChatMessageModel.System(Loc.Format("ReviewChat", gate.VerifiersRun, gate.Fixes.Count, VerificationText.K(gate.KBefore), VerificationText.K(gate.KAfterAll))));
    }

    private ChatMessageModel? _reviewMessage;

    private void SetBadge(VerificationGateReport gate, bool afterFixes)
    {
        double k = afterFixes ? gate.KAfterAll : gate.KBefore;
        int clashes = afterFixes ? gate.ClashesAfterAll : gate.ClashesBefore;
        bool ok = k <= 1.0 && clashes == 0 && (afterFixes || gate.Fixes.Count == 0);
        VerificationBadgeText = ok ? Loc.Format("ReviewBadgeOk", VerificationText.K(k), clashes) : Loc.Format("ReviewBadgeIssues", gate.Fixes.Count, VerificationText.K(k));
        VerificationBadgeColorHex = ok ? "#30D158" : "#FF9F0A";
        if (_reviewMessage is { } m)
        {
            m.Badge = VerificationBadgeText;
            m.BadgeColorHex = VerificationBadgeColorHex;
        }
    }

    private bool _projectionQueued;

    /// <summary>Отметки правок сменились — прогноз K_исп, коллизий и сметы пересчитывается по отмеченному набору (раз за кадр UI).</summary>
    private void OnReviewSelectionChanged()
    {
        OnPropertyChanged(nameof(ReviewFooterText));
        if (_projectionQueued) return;
        _projectionQueued = true;
        _dispatcher?.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, RefreshProjection);
        if (_dispatcher is null) RefreshProjection();
    }

    private void RefreshProjection()
    {
        _projectionQueued = false;
        if (Review is not { } gate) return;
        var accepted = gate.Fixes.Where(f => f.IsAccepted).ToList();
        if (accepted.Count == 0)
        {
            ProjectedK = gate.KBefore;
            ProjectedClashes = gate.ClashesBefore;
            ProjectedCost = gate.CostBeforeRub;
            return;
        }
        var rates = new GateCostRates(Settings.Current.ConcretePricePerM3, Settings.Current.RebarPricePerTon);
        var (k, clashes, _, cost) = VerificationGateService.Simulate(Scene, accepted, Settings.Current.StructuralLoads, rates);
        ProjectedK = k;
        ProjectedClashes = clashes;
        ProjectedCost = cost;
    }

    /// <summary>Клик по карточке правки: выделение элемента и фокус камеры на узле.</summary>
    public void FocusReviewFix(ReviewFixRow row)
    {
        var fix = row.Fix;
        if (Scene.FindById(fix.ElementId) is { } e) Scene.SelectedElement = e;
        SetCouncilHighlights(fix.AffectedElementIds.Select(id => (id, fix.Severity == VerificationSeverity.Critical)).ToList());
        if (fix.Location is { Length: 3 } p) ClashFocusRequested?.Invoke(this, new ClashFocus(p[0], p[1], p[2], fix.Extent));
    }

    [RelayCommand]
    private void SelectAllReviewFixes()
    {
        bool all = ReviewFixes.All(r => r.IsAccepted);
        foreach (var r in ReviewFixes) r.IsAccepted = !all;
    }

    /// <summary>«Принять выбранные правки»: отмеченные исправления — одной транзакцией журнала (ровно один шаг Undo).</summary>
    [RelayCommand]
    private void AcceptReviewFixes()
    {
        if (Review is not { } gate) return;
        var result = VerificationGateService.ApplyAccepted(Scene, gate, Settings.Current.StructuralLoads,
            new GateCostRates(Settings.Current.ConcretePricePerM3, Settings.Current.RebarPricePerTon));
        gate.KAfterAll = result.KAfter;
        gate.ClashesAfterAll = result.ClashesAfter;
        gate.CostAfterAllRub = result.CostAfterRub;
        SetBadge(gate, afterFixes: true);
        CloseReview();
        var report = new SolverReport { Success = true, InputElements = result.Accepted, GeneratedElements = result.Applied, Actions = [.. result.Log] };
        report.Warnings.AddRange(result.Failed);
        ChatMessages.Add(ChatMessageModel.Assistant(Loc.Format("ReviewAccepted", result.Accepted, gate.Fixes.Count, VerificationText.K(gate.KBefore), VerificationText.K(result.KAfter),
            ReviewFixRow.CostDelta(result.CostAfterRub - gate.CostBeforeRub, Loc.Culture)), Loc.Get("ReviewTitleShort"), report));
        ShowSuccess(Loc.Get("ReviewTitleShort"), Loc.Format("ReviewAcceptedShort", result.Accepted, VerificationText.K(result.KAfter), result.ClashesAfter), "AIA-REVIEW-OK");
        RunClashDetection();
    }

    /// <summary>«Оставить исходный вариант»: правки локального аудитора не применяются, сцена генератора остаётся.</summary>
    [RelayCommand]
    private void KeepGeneratorVariant()
    {
        if (Review is not { } gate) return;
        CloseReview();
        ChatMessages.Add(ChatMessageModel.System(Loc.Format("ReviewKept", gate.Fixes.Count)));
    }

    /// <summary>«Отменить всё»: откат шага генерации — сцена возвращается к состоянию до запроса.</summary>
    [RelayCommand]
    private void RevertGeneration()
    {
        if (Review is not { } gate) return;
        bool reverted = VerificationGateService.RevertGeneration(Scene, gate);
        CloseReview();
        VerificationBadgeText = string.Empty;
        if (_reviewMessage is { } m) m.Badge = string.Empty;
        ChatMessages.Add(ChatMessageModel.System(Loc.Get(reverted ? "ReviewReverted" : "ReviewRevertUnavailable")));
    }

    private void CloseReview()
    {
        Review = null;
        ReviewFixes.Clear();
        OnPropertyChanged(nameof(HasReviewFixes));
        SetCouncilHighlights([]);
    }

#if DEBUG
    /// <summary>Только Debug (AIA_DEBUG_REVIEW): сцена с дефектами как ответ внешнего генератора и лист согласования.</summary>
    public async Task DebugReviewAsync(List<ArchitecturalElement> elements, List<BuildingGroup> groups, List<StructuralGridAxis> axes, string source)
    {
        Scene.LoadScene(elements, groups, recordUndo: true, axes: axes, dimensions: []);
        var message = ChatMessageModel.Assistant(Loc.Get("ReviewDemoMessage"), source);
        ChatMessages.Add(message);
        ZoomExtentsRequested?.Invoke(this, EventArgs.Empty);
        await RunVerificationGateAsync(source, message);
    }
#endif
}
