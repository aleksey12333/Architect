using AIArchitectStudio.Models;
using AIArchitectStudio.Rendering;
using AIArchitectStudio.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AIArchitectStudio.ViewModels;

/// <summary>
/// Параметрические инструменты инспектора для ручного конструирования: генератор П-образной двухмаршевой лестницы под высоту
/// этажа (без выделения — в центре постройки на активном этаже, у выбранного марша — на его месте) и превращение плоского
/// покрытия или плиты покрытия в скатную (двускатную / вальмовую) кровлю с заданным углом. Каждое действие — один шаг Undo.
/// </summary>
public sealed partial class MainViewModel
{
    /// <summary>Ширина марша генератора лестниц, мм (900…3000).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StairToolPreviewText))]
    public partial double StairFlightWidthMm { get; set; } = ParametricToolsService.DefaultFlightWidth * 1000;

    /// <summary>Угол наклона скатов, ° (5…60).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RoofToolPreviewText))]
    public partial double RoofPitchDeg { get; set; } = 25;

    /// <summary>Генератор лестницы виден без выделения («пустая зона») и при выбранном марше.</summary>
    public bool IsStairToolVisible => Scene.SelectedElement is null || Scene.SelectedElement.Type == ElementTypes.Stairs && !Scene.IsMultiSelection;

    public bool IsStairReplace => Scene.SelectedElement?.Type == ElementTypes.Stairs;

    public bool IsAnyToolVisible => IsStairToolVisible || IsRoofToolVisible;

    public string StairToolGlyph => ElementTypes.Glyph(ElementTypes.Stairs);

    public string RoofToolGlyph => ElementTypes.Glyph(ElementTypes.Roof);

    /// <summary>Скатная кровля по углу: выбрана кровля или плита (перекрытие / консоль) — одиночно.</summary>
    public bool IsRoofToolVisible => !Scene.IsMultiSelection && Scene.SelectedElement?.Type is ElementTypes.Roof or ElementTypes.Slab or ElementTypes.CantileverSlab;

    public string StairToolButtonText => Loc.Get(IsStairReplace ? "StairToolReplace" : "StairToolGenerate");

    /// <summary>«Этаж 3 000 мм → 20 подступенков по 150 мм · марши 10 + 10 · клетка 2 600 × 4 200 мм».</summary>
    public string StairToolPreviewText
    {
        get
        {
            var (_, _, _, _, _, plan) = StairPlacement();
            var c = Loc.Culture;
            var request = CurrentStairRequest();
            if (request.Shape != StairShape.UShaped)
            {
                var g = StairGeneratorService.Build([], request, string.Empty, 0, 0, plan.FloorHeight, 0, 0, 0);
                return Loc.Format("W1StairPreview", Mm(plan.FloorHeight), g.Risers, Mm(g.Riser), g.Flights.Count(), Mm(g.FootprintWidth), Mm(g.FootprintLength));
            }
            return Loc.Format("StairToolPreview", Mm(plan.FloorHeight), plan.Risers, Mm(plan.Riser), plan.FirstFlightRisers, plan.SecondFlightRisers,
                Mm(plan.TotalWidth), Mm(plan.TotalLength));

            string Mm(double m) => (m * 1000).ToString("N0", c);
        }
    }

    /// <summary>«Пролёт 9 000 мм · конёк +2 098 мм при 25°».</summary>
    public string RoofToolPreviewText
    {
        get
        {
            if (Scene.SelectedElement is not { } el || !IsRoofToolVisible) return string.Empty;
            double span = ParametricToolsService.Span(el), angle = ClampPitch(RoofPitchDeg);
            double ridge = Math.Tan(angle * Math.PI / 180) * span / 2;
            var c = Loc.Culture;
            return Loc.Format("RoofToolPreview", (span * 1000).ToString("N0", c), (ridge * 1000).ToString("N0", c), angle.ToString("0.#", c));
        }
    }

    private static double ClampPitch(double angle) => double.IsFinite(angle) ? Math.Clamp(angle, 5, 60) : 25;

    private void RefreshParametricTools()
    {
        OnPropertyChanged(nameof(IsStairToolVisible));
        OnPropertyChanged(nameof(IsStairReplace));
        OnPropertyChanged(nameof(IsRoofToolVisible));
        OnPropertyChanged(nameof(IsAnyToolVisible));
        OnPropertyChanged(nameof(StairToolButtonText));
        OnPropertyChanged(nameof(StairToolPreviewText));
        OnPropertyChanged(nameof(RoofToolPreviewText));
        RefreshManualTools();
    }

    /// <summary>Постройка, этаж, отметка пола, центр и поворот клетки и расчёт маршей под высоту этажа.</summary>
    private (string Group, int Level, double BaseY, (double X, double Z) Center, double Rotation, StairPlan Plan) StairPlacement()
    {
        double width = double.IsFinite(StairFlightWidthMm) ? StairFlightWidthMm / 1000 : ParametricToolsService.DefaultFlightWidth;
        if (Scene.SelectedElement is { Type: ElementTypes.Stairs } st)
        {
            double height = Scene.FloorHeight(st.BuildingGroupId, st.LevelIndex);
            return (st.BuildingGroupId, st.LevelIndex, st.BaseY, (st.X, st.Z), st.RotationY, ParametricToolsService.PlanUShaped(height, width));
        }

        int level = Math.Max(0, Scene.ActiveLevelFilter);
        var onLevel = Scene.Elements.Where(e => e.LevelIndex == level).ToList();
        string group = onLevel.GroupBy(e => e.BuildingGroupId).OrderByDescending(g => g.Count()).FirstOrDefault()?.Key
                       ?? Scene.BuildingGroups.FirstOrDefault()?.Id ?? BuildingGroup.DefaultGroupId;
        var items = Scene.Elements.Where(e => e.BuildingGroupId == group).ToList();
        double baseY = StructuralAxesBuilder.LevelElevations(items).FirstOrDefault(l => l.Level == level)?.Elevation ?? 0;
        double floor = Scene.FloorHeight(group, level);
        var center = ParametricToolsService.DefaultStairCenter(onLevel.Where(e => e.BuildingGroupId == group).ToList());
        return (group, level, baseY, center, 0, ParametricToolsService.PlanUShaped(floor, width));
    }

    /// <summary>«Сгенерировать лестницу»: два марша, площадка и проёмы в перекрытиях над клеткой — один шаг Undo; новые элементы выделяются.</summary>
    [RelayCommand]
    private void GenerateUShapedStair()
    {
        var (group, level, baseY, center, rotation, plan) = StairPlacement();
        var replaced = Scene.SelectedElement is { Type: ElementTypes.Stairs } st ? st : null;
        var scene = Scene.Elements.Where(e => !ReferenceEquals(e, replaced)).ToList();
        int index = Scene.Elements.Count(e => e.Type == ElementTypes.Stairs && e.Name.StartsWith("Марш Л-", StringComparison.Ordinal)) / 2 + 1;
        var result = ParametricToolsService.BuildUShapedStair(scene, plan, group, level, baseY, center.X, center.Z, rotation, $"Л-{index}");

        Scene.PushUndo();
        var removed = result.Removed.ToList();
        if (replaced is not null) removed.Add(replaced);
        if (removed.Count > 0) Scene.RemoveElements(removed, recordUndo: false);
        Scene.AddElements(result.Created, recordUndo: false);
        var solids = result.Created.Where(e => !ElementTypes.IsVoidType(e.Type)).ToList();
        Scene.SetSelection(solids, solids[0]);

        var c = Loc.Culture;
        string Mm(double m) => (m * 1000).ToString("N0", c);
        string openings = result.OpeningsCut > 0 ? Loc.Format("StairToolOpenings", result.OpeningsCut) : string.Empty;
        string message = Loc.Format("StairToolDone", Mm(plan.FloorHeight), plan.Risers, Mm(plan.Riser), Mm(plan.Tread), Mm(plan.StepFormula),
            plan.FirstFlightRisers, plan.SecondFlightRisers, Mm(plan.TotalWidth), Mm(plan.LandingDepth), openings);
        ChatMessages.Add(ChatMessageModel.System(message));
        ShowSuccess(Loc.Get("StairTool"), message, "AIA-STAIR-U");
        RefreshParametricTools();
    }

    /// <summary>«Двускатная» / «Вальмовая»: высота конька по углу и пролёту; у плиты покрытия кровля ставится поверх неё.</summary>
    [RelayCommand]
    private void MakePitchedRoof(string? shape)
    {
        if (Scene.SelectedElement is not { } target || !IsRoofToolVisible) return;
        if (ParametricToolsService.Span(target) < 0.5)
        {
            ShowNotice(Loc.Get("RoofTool"), Loc.Get("RoofToolNoSpan"), "AIA-ROOF-SPAN");
            return;
        }

        // Кровля меняется на месте — снимок Undo до правки; плита покрытия получает новую кровлю (AddElement пишет шаг сам).
        bool inPlace = target.Type == ElementTypes.Roof;
        if (inPlace) Scene.PushUndo();
        var result = ParametricToolsService.MakePitchedRoof(Scene.Elements.ToList(), target, shape == RoofShapes.Hip ? RoofShapes.Hip : RoofShapes.Gable,
            ClampPitch(RoofPitchDeg))!;
        if (result.Created)
        {
            Scene.AddElement(result.Roof);
            Scene.SelectedElement = result.Roof;
        }
        else
        {
            if (result.Removed.Count > 0) Scene.RemoveElements(result.Removed, recordUndo: false);
            Scene.NotifyElementsChanged([result.Roof]);
            SyncInspector();
            RefreshLayers();
        }

        var c = Loc.Culture;
        string removed = result.Removed.Count > 0 ? Loc.Format("RoofToolOpeningsRemoved", result.Removed.Count) : string.Empty;
        string message = Loc.Format("RoofToolDone", Loc.Get(result.Shape == RoofShapes.Hip ? "RoofToolHip" : "RoofToolGable"), result.Roof.DisplayTitle,
            result.AngleDeg.ToString("0.#", c), (result.Span * 1000).ToString("N0", c), (result.RidgeHeight * 1000).ToString("N0", c), removed);
        ChatMessages.Add(ChatMessageModel.System(message));
        ShowSuccess(Loc.Get("RoofTool"), message, "AIA-ROOF-PITCH");
        RefreshProfileInfo();
        RefreshSelectionInfo();
    }
}
