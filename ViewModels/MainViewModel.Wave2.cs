using AIArchitectStudio.Models;
using AIArchitectStudio.Models.Analytical;
using AIArchitectStudio.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AIArchitectStudio.ViewModels;

/// <summary>Фактор цветовой шкалы аналитического оверлея режима «Конструкции».</summary>
public enum StructuralFactor
{
    Utilization,
    Stress,
    Axial,
    Moment,
    Deflection
}

/// <summary>Сведения о выбранном элементе в панели конструктора: напряжения, закрепление, армирование, продавливание.</summary>
public sealed record StructuralSelectionInfo(ArchitecturalElement Element, ElementStressRow? Stress, PunchingStressRow? Punching, EndFixity Fixity,
    double? BarDiameter, double? StirrupSpacing, double? Cover, bool PunchingRebar, bool IsVertical, bool IsSlab, bool IsSteelCandidate);

/// <summary>
/// Волна 2 продуктового ревью в ViewModel: логика проёмов при вводе координат и габаритов в Инспекторе, режим «Конструкции (КЖ / КМ)»
/// (аналитический оверлей, напряжения, закрепление концов, армирование, продавливание, профили КМ, аудит расчётной схемы), паспорт
/// свойств Renga и мастер размеров здания.
/// </summary>
public sealed partial class MainViewModel
{
    // ───────────────────────────── Логика проёмов в Инспекторе ─────────────────────────────

    private static (double, double, double, double, double, double, string?) OpeningState(ArchitecturalElement e) =>
        (e.X, e.Y, e.Z, e.Width, e.Height, e.Depth, e.HostElementId);

    /// <summary>Итог последней проверки проёма после ввода в Инспекторе (автотест и пояснение пользователю).</summary>
    public OpeningGuardResult? LastInspectorGuard { get; private set; }

    /// <summary>
    /// Окно/дверь изменены с клавиатуры (поля X/Y/Z, W/H/D, кнопки сдвига): проём остаётся в стене и уходит от колонн, поперечных стен
    /// и торцов на 150 мм — поправка входит в тот же шаг журнала; места нет — ввод откатывается с понятным уведомлением.
    /// </summary>
    public void EnforceOpeningAfterInspector(ArchitecturalElement element)
    {
        LastInspectorGuard = null;
        if (!BuildingLogicGuard.IsGuarded(element) || !Scene.Elements.Contains(element)) return;
        var before = OpeningState(element);
        var guard = BuildingLogicGuard.Enforce(Scene.Elements.ToList(), element);
        LastInspectorGuard = guard;
        if (!guard.Success)
        {
            Scene.Undo();
            ShowNotice(Loc.Get("W1GuardTitle"), Loc.Format("W2GuardRollback", guard.Message), "AIA-OPENING-INPUT");
            SyncInspector();
            return;
        }
        if (OpeningState(element) != before)
        {
            var changed = new List<ArchitecturalElement> { element };
            if (guard.Host is { } host) changed.Add(host);
            Scene.NotifyElementsChanged(changed);
            ShowNotice(Loc.Get("W1GuardTitle"), guard.Message.Length > 0 ? guard.Message : Loc.Get("W2GuardAligned"), "AIA-OPENING-INPUT");
            SyncInspector();
        }
    }

    // ───────────────────────────── Режим «Конструкции» ─────────────────────────────

    /// <summary>Режим «Конструкции (КЖ / КМ)»: аналитический оверлей, панель конструктора; вид — 3D.</summary>
    [ObservableProperty]
    public partial bool IsStructuralMode { get; set; }

    public bool IsStructuralModeAvailable => IsArchitectUi || IsAnalysisModule;

    /// <summary>Сегмент «Конструкции» рядом с «3D | План | Фасад» (одна группа переключателей).</summary>
    public bool IsStructuralSegment
    {
        get => IsStructuralMode;
        set
        {
            if (value == IsStructuralMode) return;
            IsStructuralMode = value;
        }
    }

    partial void OnIsStructuralModeChanged(bool value)
    {
        if (value && ViewMode != ViewportMode.Perspective3D) ViewMode = ViewportMode.Perspective3D;
        if (value && IsWalkthrough) IsWalkthrough = false;
        OnPropertyChanged(nameof(IsPerspectiveMode));
        OnPropertyChanged(nameof(IsStructuralSegment));
        OnPropertyChanged(nameof(IsSectionPanelVisible));
        InvalidateStructural();
    }

    /// <summary>Панель «Секущий бокс» видна, если он включён и не открыт режим «Конструкции» (одно место у инспектора).</summary>
    public bool IsSectionPanelVisible => IsSectionBoxEnabled && !IsStructuralMode;


    public IReadOnlyList<string> StructuralFactorTitles => Enum.GetValues<StructuralFactor>().Select(f => Loc.Get("W2Factor" + f)).ToList();

    [ObservableProperty]
    public partial int StructuralFactorIndex { get; set; }

    partial void OnStructuralFactorIndexChanged(int value) => StructuralOverlayChanged?.Invoke(this, EventArgs.Empty);

    public StructuralFactor StructuralFactor => Enum.IsDefined((StructuralFactor)StructuralFactorIndex) ? (StructuralFactor)StructuralFactorIndex : StructuralFactor.Utilization;

    /// <summary>Схема, результат или фактор режима «Конструкции» обновились — вьюпорт перестраивает оверлей, панель — сведения.</summary>
    public event EventHandler? StructuralOverlayChanged;

    private AnalyticalModel? _structuralModel;
    private StructuralAnalysisResult? _structuralResult;
    private Dictionary<string, ElementStressRow> _structuralStresses = [];
    private Dictionary<string, PunchingStressRow> _structuralPunching = [];

    /// <summary>Расчётная схема и результат по текущей сцене (строятся в режиме «Конструкции» после каждого пересчёта).</summary>
    public AnalyticalModel? StructuralModel => IsStructuralMode ? EnsureStructural().Model : null;

    public StructuralAnalysisResult? StructuralResult => IsStructuralMode ? EnsureStructural().Result : null;

    private (AnalyticalModel? Model, StructuralAnalysisResult? Result) EnsureStructural()
    {
        if (_structuralModel is null)
        {
            var model = AnalyticalModelBuilder.Build(Scene.Elements.ToList(), Scene.BuildingGroups.ToList(), Settings.Current.StructuralLoads);
            var result = StructuralAnalysisService.Analyze(model);
            _structuralModel = model;
            _structuralResult = result;
            _structuralStresses = FeaLogicalAuditService.Stresses(model, result).GroupBy(r => r.ElementId).ToDictionary(g => g.Key, g => g.First());
            _structuralPunching = FeaLogicalAuditService.PunchingRows(model, result).GroupBy(p => p.ColumnId).ToDictionary(g => g.Key, g => g.MaxBy(p => p.Ratio)!);
        }
        return (_structuralModel, _structuralResult);
    }

    private void InvalidateStructural()
    {
        _structuralModel = null;
        _structuralResult = null;
        _structuralStresses = [];
        _structuralPunching = [];
        RefreshStressInfo();
        StructuralOverlayChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Значение фактора у элемента: доля шкалы (1 — предел) и подпись (МПа, кН, кН·м, мм).</summary>
    public (double Ratio, string Text)? StructuralValue(string id)
    {
        if (StructuralResult is not { } result || result.For(id) is not { } r) return null;
        var c = Loc.Culture;
        switch (StructuralFactor)
        {
            case StructuralFactor.Stress:
                return _structuralStresses.TryGetValue(id, out var s) ? (s.StressRatio, $"σ {s.SigmaMax.ToString("0.0", c)} МПа") : null;
            case StructuralFactor.Axial:
            {
                double max = Math.Max(1e-6, result.Elements.Values.Max(x => x.AxialForce));
                return r.AxialForce > 0.05 ? (r.AxialForce / max, $"N {r.AxialForce.ToString("N0", c)} кН") : null;
            }
            case StructuralFactor.Moment:
            {
                double max = Math.Max(1e-6, result.Elements.Values.Max(x => x.Moment));
                return r.Moment > 0.005 ? (r.Moment / max, $"M {r.Moment.ToString("N1", c)} кН·м") : null;
            }
            case StructuralFactor.Deflection:
                return r.DeflectionLimitMm > 0 ? (r.DeflectionRatio, $"f {r.DeflectionMm.ToString("0.0", c)} мм") : null;
            default:
                return (r.Utilization, $"K {r.Utilization.ToString("0.00", c)}");
        }
    }

    /// <summary>Подписи шкалы оверлея: 0, середина и предел по выбранному фактору.</summary>
    public (string Low, string Mid, string High) StructuralScale()
    {
        var c = Loc.Culture;
        if (StructuralResult is not { } result || result.Elements.Count == 0) return ("0", "—", "—");
        return StructuralFactor switch
        {
            StructuralFactor.Stress => ("0", "σ = 0,5·R", "σ = R"),
            StructuralFactor.Axial => ("0", (result.Elements.Values.Max(x => x.AxialForce) / 2).ToString("N0", c) + " кН", result.Elements.Values.Max(x => x.AxialForce).ToString("N0", c) + " кН"),
            StructuralFactor.Moment => ("0", (result.Elements.Values.Max(x => x.Moment) / 2).ToString("N0", c) + " кН·м", result.Elements.Values.Max(x => x.Moment).ToString("N0", c) + " кН·м"),
            StructuralFactor.Deflection => ("0", "f = 0,5·f_ult", "f = f_ult"),
            _ => ("0", "K = 0,5", "K = 1,0")
        };
    }

    public ElementStressRow? StressOf(string? id) => id is not null && IsStructuralMode && EnsureStructural().Model is not null && _structuralStresses.TryGetValue(id, out var s) ? s : null;

    /// <summary>Выбранный элемент для панели конструктора (null — нет несущего выделения).</summary>
    public StructuralSelectionInfo? StructuralSelection()
    {
        if (Scene.SelectedElement is not { } e) return null;
        EnsureStructural();
        bool vertical = e.Category is BimCategory.Column or BimCategory.Wall or BimCategory.Core;
        bool slab = e.Category is BimCategory.Slab or BimCategory.CantileverSlab or BimCategory.Foundation || e.IsFlatRoof;
        return new StructuralSelectionInfo(e, _structuralStresses.GetValueOrDefault(e.Id), _structuralPunching.GetValueOrDefault(e.Id), StructuralDesign.Fixity(e),
            StructuralDesign.BarDiameter(e), StructuralDesign.StirrupSpacing(e), StructuralDesign.Cover(e), StructuralDesign.HasPunchingRebar(e), vertical, slab,
            e.Category is BimCategory.Beam or BimCategory.Column);
    }

    /// <summary>Закрепление концов всех выделенных стоек — одним шагом журнала (μ сразу в расчёт гибкости и моментов).</summary>
    public void SetFixity(EndFixity fixity)
    {
        var targets = Scene.SelectedElements.Where(e => e.Category is BimCategory.Column or BimCategory.Wall or BimCategory.Core && StructuralDesign.Fixity(e) != fixity).ToList();
        if (targets.Count == 0) return;
        using (Scene.BeginUndoTransaction($"Закрепление концов: μ = {StructuralDesign.LengthFactor(fixity):0.##}"))
        {
            foreach (var e in targets) StructuralDesign.WriteFixity(e, fixity);
            Scene.NotifyElementsChanged(targets);
        }
        AfterStructuralEdit();
    }

    /// <summary>Конструирование армирования выделенных элементов (Ø, шаг хомутов, защитный слой) — один шаг журнала.</summary>
    public void SetRebarDesign(double barDiameter, double stirrupSpacing, double cover)
    {
        var targets = Scene.SelectedElements.Where(ReinforcementCalculator.IsReinforced).ToList();
        if (targets.Count == 0) return;
        using (Scene.BeginUndoTransaction($"Армирование: Ø{barDiameter * 1000:0}, шаг {stirrupSpacing * 1000:0}, а = {cover * 1000:0} мм"))
        {
            foreach (var e in targets) StructuralDesign.WriteRebar(e, barDiameter, stirrupSpacing, cover);
            Scene.NotifyElementsChanged(targets);
        }
        AfterStructuralEdit();
    }

    /// <summary>Поперечное армирование зоны продавливания выделенных плит (F_sw,ult по СП 63 п. 8.1.48) — один шаг журнала.</summary>
    public void SetPunchingRebar(bool on)
    {
        var targets = Scene.SelectedElements.Where(e => (e.Category is BimCategory.Slab or BimCategory.CantileverSlab or BimCategory.Foundation || e.IsFlatRoof)
                                                        && StructuralDesign.HasPunchingRebar(e) != on).ToList();
        if (targets.Count == 0) return;
        using (Scene.BeginUndoTransaction(on ? "Каркасы продавливания: включены" : "Каркасы продавливания: сняты"))
        {
            foreach (var e in targets) StructuralDesign.WritePunchingRebar(e, on);
            Scene.NotifyElementsChanged(targets);
        }
        AfterStructuralEdit();
    }

    /// <summary>Стальные профили ГОСТ Р 57837-2017 из библиотеки (двутавры) для быстрого подбора КМ.</summary>
    public IReadOnlyList<StandardLibraryEntry> SteelProfiles => StandardLibraryService.BuiltIn.Entries
        .Where(x => x.ProfileType == ProfileTypes.IBeam).ToList();

    /// <summary>Профиль КМ выделенным балкам и колоннам — один шаг журнала «Тип из библиотеки».</summary>
    public void ApplySteelProfile(StandardLibraryEntry entry)
    {
        var targets = Scene.SelectedElements.Where(e => e.Category is BimCategory.Beam or BimCategory.Column).ToList();
        if (targets.Count == 0) return;
        StandardLibraryService.Apply(Scene, entry, targets);
        AfterStructuralEdit();
    }

    private void AfterStructuralEdit()
    {
        Structure.Recalculate();
        RefreshSelectionInfo();
        InvalidateStructural();
    }

    // ───────────────────────────── Аудит расчётной схемы ─────────────────────────────

    /// <summary>Готов отчёт аудита — главное окно открывает «Аудит расчётной схемы».</summary>
    public event EventHandler<FeaAuditReport>? AuditReportReady;

    [ObservableProperty]
    public partial bool IsAuditRunning { get; set; }

    /// <summary>«Аудит расчётной схемы»: баланс сил, 5 критериев логики, напряжения σ / τ — в фоне на снимке сцены.</summary>
    [RelayCommand]
    private async Task RunSchemeAudit()
    {
        if (IsAuditRunning) return;
        IsAuditRunning = true;
        try
        {
            var elements = Scene.Elements.Select(e => e.Clone()).ToList();
            var groups = Scene.BuildingGroups.ToList();
            var loads = Settings.Current.StructuralLoads.Clone();
            var report = await Task.Run(() => FeaLogicalAuditService.Audit(elements, groups, loads));
            LastAuditReport = report;
            AuditReportReady?.Invoke(this, report);
        }
        finally
        {
            IsAuditRunning = false;
        }
    }

    public FeaAuditReport? LastAuditReport { get; private set; }

    // ───────────────────────────── Напряжения в Инспекторе ─────────────────────────────

    [ObservableProperty] public partial string ForceStressText { get; set; } = string.Empty;

    /// <summary>σ_max, τ и сопротивления выбранного элемента (МПа) — строка карточки усилий инспектора.</summary>
    private void RefreshStressInfo()
    {
        if (Scene.SelectedElement is not { } el || Analysis.For(el.Id) is null)
        {
            ForceStressText = string.Empty;
            return;
        }
        var model = _structuralModel;
        ElementStressRow? row = _structuralStresses.GetValueOrDefault(el.Id);
        if (row is null && model is null)
        {
            // Вне режима «Конструкции» — схема только выбранного несущего элемента не строится отдельно: расчёт по сцене (мс).
            var built = AnalyticalModelBuilder.Build(Scene.Elements.ToList(), Scene.BuildingGroups.ToList(), Settings.Current.StructuralLoads);
            row = FeaLogicalAuditService.Stresses(built, StructuralAnalysisService.Analyze(built)).FirstOrDefault(r => r.ElementId == el.Id);
        }
        var c = Loc.Culture;
        ForceStressText = row is null
            ? string.Empty
            : Loc.Format("W2StressLine", row.SigmaMax.ToString("0.00", c), row.RSection.ToString("0.0", c), row.Tau.ToString("0.00", c), row.RShear.ToString("0.00", c),
                row.StressRatio.ToString("0.00", c));
    }

    // ───────────────────────────── Свойства Renga ─────────────────────────────

    /// <summary>Паспорт свойств Renga выбранного элемента (на лету по текущей геометрии).</summary>
    public RengaProperties? SelectedRengaProperties()
    {
        if (Scene.SelectedElement is not { } e || ElementTypes.IsVoidType(e.Category)) return null;
        var r = Analysis.For(e.Id);
        var punching = BimAnalyticsService.PunchingReinforcedIds(Analysis);
        return RengaPropertyService.Compute(e, Scene.Elements.ToList(), r?.Utilization ?? 0, punching.Contains(e.Id));
    }

    /// <summary>«Назначить марки»: марка и позиция по типоразмерам всем элементам — один шаг журнала (группировка ведомости по марке).</summary>
    [RelayCommand]
    private void AssignMarks()
    {
        var plan = RengaPropertyService.PlanMarks(Scene.Elements.ToList());
        var targets = Scene.Elements.Where(e => plan.ContainsKey(e.Id) && (RengaPropertyService.StoredMark(e) != plan[e.Id].Mark || RengaPropertyService.StoredPosition(e) != plan[e.Id].Position)).ToList();
        if (targets.Count == 0)
        {
            ShowNotice(Loc.Get("W2Marks"), Loc.Get("W2MarksSame"), "AIA-RENGA-MARKS");
            return;
        }
        using (Scene.BeginUndoTransaction($"Назначение марок: {targets.Count} эл."))
        {
            foreach (var e in targets) RengaPropertyService.WriteMark(e, plan[e.Id].Mark, plan[e.Id].Position);
            Scene.NotifyElementsChanged(targets);
        }
        int marks = plan.Values.Select(v => v.Mark).Distinct().Count();
        ShowSuccess(Loc.Get("W2Marks"), Loc.Format("W2MarksDone", targets.Count, marks), "AIA-RENGA-MARKS");
        RefreshSelectionInfo();
    }

    // ───────────────────────────── Мастер размеров ─────────────────────────────

    /// <summary>«Мастер размеров и пакетное редактирование» — главное окно открывает диалог.</summary>
    public event EventHandler? BuildingDimensionsRequested;

    [RelayCommand]
    private void OpenBuildingDimensions() => BuildingDimensionsRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>Постройка мастера: выделенного элемента, иначе самая населённая.</summary>
    public string DimensionsGroupId() =>
        Scene.SelectedElement?.BuildingGroupId
        ?? Scene.Elements.GroupBy(e => e.BuildingGroupId).OrderByDescending(g => g.Count()).Select(g => g.Key).FirstOrDefault()
        ?? BuildingGroup.DefaultGroupId;

    /// <summary>Итог операции мастера — в чат и InfoBar, расчёт и сведения инспектора обновляются.</summary>
    public void ReportBatch(BatchDimensionResult result, string title)
    {
        if (result.Success)
        {
            ChatMessages.Add(ChatMessageModel.System(result.Message));
            ShowSuccess(title, result.Message, "AIA-BATCH-DIM");
        }
        else ShowNotice(title, result.Message, "AIA-BATCH-DIM");
        RefreshSelectionInfo();
        SyncInspector();
    }

    private void InitializeWave2()
    {
        Scene.DocumentChanged += (_, _) =>
        {
            if (IsStructuralMode) RunOnUi(InvalidateStructural);
        };
        Structure.AnalysisChanged += (_, _) => RunOnUi(() =>
        {
            if (IsStructuralMode) InvalidateStructural();
            else RefreshStressInfo();
        });
        Scene.SelectionChanged += (_, _) => RunOnUi(() =>
        {
            RefreshStressInfo();
            if (IsStructuralMode) StructuralOverlayChanged?.Invoke(this, EventArgs.Empty);
        });
        Loc.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(LocalizationService.Language)) return;
            int factor = StructuralFactorIndex;
            OnPropertyChanged(nameof(StructuralFactorTitles));
            StructuralFactorIndex = factor;
        };
    }
}
