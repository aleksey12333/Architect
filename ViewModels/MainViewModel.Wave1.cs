using AIArchitectStudio.Models;
using AIArchitectStudio.Rendering;
using AIArchitectStudio.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AIArchitectStudio.ViewModels;

/// <summary>
/// Волна 1 продуктового ревью в ViewModel: боковой док Коллоквиума (лента и лист согласования — отдельной колонкой справа от
/// вьюпорта, с полосой-свёрткой), возврат на начальный экран, конструктор лестниц (5 форм × 3 конструкции) и полых кровель
/// (6 форм), проверка окна/двери после перемещения (<see cref="BuildingLogicGuard"/>).
/// </summary>
public sealed partial class MainViewModel
{
    // ───────────────────────────── Боковой док Коллоквиума ─────────────────────────────

    /// <summary>Док виден, пока открыта лента Коллоквиума или лист согласования: вьюпорт сужается, но не перекрывается.</summary>
    public bool IsCouncilDockVisible => IsCouncilTimelineOpen || Review is not null || IsAgentPanelOpen;

    /// <summary>Док свёрнут в полосу 48 px (лента и лист — по кнопке разворота).</summary>
    [ObservableProperty]
    public partial bool IsCouncilDockCollapsed { get; set; }

    partial void OnIsCouncilDockCollapsedChanged(bool value) => RefreshCouncilDock();

    public bool IsCouncilDockExpanded => IsCouncilDockVisible && !IsCouncilDockCollapsed;

    public bool IsCouncilDockStrip => IsCouncilDockVisible && IsCouncilDockCollapsed;

    /// <summary>Число на полосе свёрнутого дока: правок листа согласования или реплик ленты.</summary>
    public string CouncilDockBadge => Review is not null ? ReviewFixes.Count.ToString(Loc.Culture) : CouncilTimeline.Count.ToString(Loc.Culture);

    [RelayCommand]
    private void ToggleCouncilDockCollapsed() => IsCouncilDockCollapsed = !IsCouncilDockCollapsed;

    /// <summary>Закрыть док: лента скрывается, лист согласования остаётся неизменным (решение по нему — кнопками листа).</summary>
    [RelayCommand]
    private void CloseCouncilDock()
    {
        IsCouncilTimelineOpen = false;
        IsAgentPanelOpen = false;
        IsCouncilDockCollapsed = Review is not null;
    }

    private void RefreshCouncilDock()
    {
        OnPropertyChanged(nameof(IsCouncilDockVisible));
        OnPropertyChanged(nameof(IsCouncilDockExpanded));
        OnPropertyChanged(nameof(IsCouncilDockStrip));
        OnPropertyChanged(nameof(CouncilDockBadge));
        CouncilDockChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Видимость или свёртка дока сменились — главное окно перекладывает колонку вьюпорта.</summary>
    public event EventHandler? CouncilDockChanged;

    private void InitializeWave1()
    {
        CouncilTimeline.CollectionChanged += (_, _) => OnPropertyChanged(nameof(CouncilDockBadge));
        ReviewFixes.CollectionChanged += (_, _) => OnPropertyChanged(nameof(CouncilDockBadge));
        Loc.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(LocalizationService.Language)) return;
            // Новые подписи списков: ComboBox сбрасывает выбор при смене ItemsSource — индексы восстанавливаются.
            int shape = StairShapeIndex, construction = StairConstructionIndex;
            OnPropertyChanged(nameof(StairShapeTitles));
            OnPropertyChanged(nameof(StairConstructionTitles));
            StairShapeIndex = shape;
            StairConstructionIndex = construction;
        };
    }

    // ───────────────────────────── Начальный экран ─────────────────────────────

    /// <summary>«Начальный экран»: главное окно открывает стартовое окно (шаблоны, недавние проекты) без перезапуска.</summary>
    public event EventHandler? StartScreenRequested;

    [RelayCommand]
    private void OpenStartScreen() => StartScreenRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>«Показать всё» после смены проекта с начального экрана.</summary>
    public void RequestZoomExtents() => ZoomExtentsRequested?.Invoke(this, EventArgs.Empty);

    // ───────────────────────────── Конструктор лестниц ─────────────────────────────

    public IReadOnlyList<string> StairShapeTitles => Enum.GetValues<StairShape>().Select(s => Loc.Get("W1Stair" + s)).ToList();

    public IReadOnlyList<string> StairConstructionTitles => Enum.GetValues<StairConstruction>().Select(c => Loc.Get("W1Stair" + c)).ToList();

    /// <summary>Форма лестницы (по умолчанию — П-образная, как прежний генератор).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StairToolPreviewText))]
    public partial int StairShapeIndex { get; set; } = (int)StairShape.UShaped;

    [ObservableProperty]
    public partial int StairConstructionIndex { get; set; } = (int)StairConstruction.MonolithicSlab;

    private StairDesignRequest CurrentStairRequest()
    {
        double width = double.IsFinite(StairFlightWidthMm) ? StairFlightWidthMm / 1000 : ParametricToolsService.DefaultFlightWidth;
        var shape = Enum.IsDefined((StairShape)StairShapeIndex) ? (StairShape)StairShapeIndex : StairShape.UShaped;
        var construction = Enum.IsDefined((StairConstruction)StairConstructionIndex) ? (StairConstruction)StairConstructionIndex : StairConstruction.MonolithicSlab;
        return new StairDesignRequest(shape, construction, width);
    }

    /// <summary>
    /// «Сгенерировать лестницу»: марши выбранной формы и конструкции, площадки и сквозной проём в перекрытии над клеткой —
    /// один шаг журнала; у выбранного марша — замена на месте. Новые элементы выделяются.
    /// </summary>
    [RelayCommand]
    private void GenerateStair()
    {
        var (group, level, baseY, center, rotation, plan) = StairPlacement();
        var replaced = Scene.SelectedElement is { Type: ElementTypes.Stairs } st ? st : null;
        var scene = Scene.Elements.Where(e => !ReferenceEquals(e, replaced)).ToList();
        int index = Scene.Elements.Count(e => e.Type == ElementTypes.Stairs && (e.Name.StartsWith("Марш Л-", StringComparison.Ordinal) || e.Name.StartsWith("Винтовая", StringComparison.Ordinal))) + 1;
        var request = CurrentStairRequest();
        var result = StairGeneratorService.Build(scene, request, group, level, baseY, plan.FloorHeight, center.X, center.Z, rotation, $"Л-{index}");
        ApplyStairGeneration(result, replaced);
    }

    /// <summary>Применение генерации к сцене одной транзакцией журнала (снятые отверстия и заменяемый марш удаляются).</summary>
    public void ApplyStairGeneration(StairGeneration result, ArchitecturalElement? replaced = null)
    {
        string step = $"Лестница: {StairGeneratorService.ShapeTitle(result.Request.Shape)} · {StairGeneratorService.ConstructionTitle(result.Request.Construction)}";
        using (Scene.BeginUndoTransaction(step))
        {
            var removed = result.Removed.ToList();
            if (replaced is not null) removed.Add(replaced);
            if (removed.Count > 0) Scene.RemoveElements(removed, recordUndo: false);
            Scene.AddElements(result.Created, recordUndo: false);
        }
        var solids = result.Created.Where(e => !ElementTypes.IsVoidType(e.Type)).ToList();
        if (solids.Count > 0) Scene.SetSelection(solids, solids[0]);

        var c = Loc.Culture;
        string Mm(double m) => (m * 1000).ToString("N0", c);
        string message = Loc.Format("W1StairDone", Loc.Get("W1Stair" + result.Request.Shape), Loc.Get("W1Stair" + result.Request.Construction),
            result.Risers, Mm(result.Riser), result.Flights.Count(), Mm(result.FootprintWidth), Mm(result.FootprintLength), result.OpeningsCut);
        ChatMessages.Add(ChatMessageModel.System(message));
        ShowSuccess(Loc.Get("W1StairTitle"), message, "AIA-STAIR-GEN");
        RefreshParametricTools();
    }

    // ───────────────────────────── Полые кровли ─────────────────────────────

    /// <summary>Толщина пирога ската, мм (80…600).</summary>
    [ObservableProperty]
    public partial double RoofSlabThicknessMm { get; set; } = RoofDesign.DefaultSlabThickness * 1000;

    /// <summary>Карнизный свес, мм (0…1 500).</summary>
    [ObservableProperty]
    public partial double RoofOverhangMm { get; set; } = RoofDesign.DefaultOverhang * 1000;

    /// <summary>Полая кровля выбранной формы: у кровли — на месте, над плитой — новая кровля. Один шаг журнала.</summary>
    [RelayCommand]
    private void MakeHollowRoof(string? shape)
    {
        if (Scene.SelectedElement is not { } target || !IsRoofToolVisible) return;
        string normalized = RoofShapes.Normalize(shape, RoofShapes.GableHollow);
        double t = double.IsFinite(RoofSlabThicknessMm) ? RoofSlabThicknessMm / 1000 : RoofDesign.DefaultSlabThickness;
        double o = double.IsFinite(RoofOverhangMm) ? RoofOverhangMm / 1000 : RoofDesign.DefaultOverhang;
        var request = new RoofDesignRequest(RoofShapes.IsHollow(normalized) ? normalized : RoofShapes.GableHollow, ClampPitch(RoofPitchDeg), t, o);
        if (ParametricToolsService.Span(target) < 0.5)
        {
            ShowNotice(Loc.Get("RoofTool"), Loc.Get("RoofToolNoSpan"), "AIA-ROOF-SPAN");
            return;
        }
        PitchedRoofResult? result;
        string step = $"Кровля: {RoofGeneratorService.ShapeTitle(request.Shape)}";
        using (var transaction = Scene.BeginUndoTransaction(step))
        {
            result = RoofGeneratorService.Apply(Scene.Elements.ToList(), target, request);
            if (result is null) transaction.Discard();
            else if (result.Created) Scene.AddElement(result.Roof, recordUndo: false);
            else
            {
                if (result.Removed.Count > 0) Scene.RemoveElements(result.Removed, recordUndo: false);
                Scene.NotifyElementsChanged([result.Roof]);
            }
        }
        if (result is null) return;
        Scene.SelectedElement = result.Roof;
        SyncInspector();
        RefreshLayers();
        var c = Loc.Culture;
        string message = Loc.Format("W1RoofDone", Loc.Get("W1Roof_" + request.Shape), result.Roof.DisplayTitle, result.AngleDeg.ToString("0.#", c),
            (result.Span * 1000).ToString("N0", c), (result.RidgeHeight * 1000).ToString("N0", c), (t * 1000).ToString("N0", c), (o * 1000).ToString("N0", c));
        ChatMessages.Add(ChatMessageModel.System(message));
        ShowSuccess(Loc.Get("RoofTool"), message, "AIA-ROOF-HOLLOW");
        RefreshProfileInfo();
        RefreshSelectionInfo();
    }

    // ───────────────────────────── Логика проёмов после перемещения ─────────────────────────────

    /// <summary>
    /// Окно/дверь отпущены после перетаскивания: проём остаётся в стене (привязка в радиусе 0,5 м), уходит от колонн и поперечных
    /// стен на 150 мм; поправка входит в тот же шаг журнала. Места нет — шаг перетаскивания откатывается с сообщением.
    /// </summary>
    public void EnforceOpeningAfterMove(ArchitecturalElement element)
    {
        if (!BuildingLogicGuard.IsGuarded(element) || !Scene.Elements.Contains(element)) return;
        var before = (element.X, element.Y, element.Z, element.HostElementId);
        var guard = BuildingLogicGuard.Enforce(Scene.Elements.ToList(), element);
        if (!guard.Success)
        {
            Scene.Undo();
            ShowNotice(Loc.Get("W1GuardTitle"), guard.Message, "AIA-OPENING-GUARD");
            return;
        }
        if ((element.X, element.Y, element.Z, element.HostElementId) != before)
        {
            var changed = new List<ArchitecturalElement> { element };
            if (guard.Host is { } host) changed.Add(host);
            Scene.NotifyElementsChanged(changed);
            if (guard.Message.Length > 0) ShowNotice(Loc.Get("W1GuardTitle"), guard.Message, "AIA-OPENING-SHIFT");
        }
    }
}
