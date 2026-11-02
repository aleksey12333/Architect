using AIArchitectStudio.Models;
using AIArchitectStudio.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AIArchitectStudio.ViewModels;

/// <summary>
/// Тактильное моделирование в стиле Shapr3D: торцевые ручки Push/Pull с живой линейкой, умные привязки OSnap,
/// мультивыделение с пакетной правкой, линейные массивы и тиражирование этажа, режим прогулки от первого лица
/// и солнечная инсоляция с падающими тенями.
/// </summary>
public sealed partial class MainViewModel
{
    private void InitializeModeling()
    {
        InitializeSolarRegions();
        Scene.SelectionChanged += (_, _) =>
        {
            RefreshSelectionInfo();
            RefreshMultiSelection();
        };
    }

    // ───────────────────────────── Умные привязки OSnap ─────────────────────────────

    /// <summary>Магнитные привязки к осям и геометрии при перетаскивании Gizmo и ручек Push/Pull (Alt — временно выключить).</summary>
    [ObservableProperty]
    public partial bool IsOSnapEnabled { get; set; } = true;

    // ───────────────────────────── Мультивыделение и пакетная правка ─────────────────────────────

    /// <summary>Материалы инспектора; материал из проекта/ИИ, которого нет в списке, добавляется при выборе элемента.</summary>
    public System.Collections.ObjectModel.ObservableCollection<string> MaterialNames { get; } =
        ["concrete", "brick", "block", "timber", "wood", "steel", "glass", "metal_roof", "tile", "stone"];

    private void EnsureMaterialOption(string? material)
    {
        if (!string.IsNullOrWhiteSpace(material) && !MaterialNames.Contains(material)) MaterialNames.Add(material);
    }

    public int SelectionCount => Scene.SelectedElements.Count;

    public bool IsMultiSelection => Scene.IsMultiSelection;

    /// <summary>Шапка инспектора при групповом выборе: «Выбрано объектов: 12».</summary>
    public string SelectionHeaderText => Loc.Format("SelectedCount", SelectionCount);

    /// <summary>Состав мультивыделения по типам: «column ×12 · beam ×3».</summary>
    public string SelectionTypesText => string.Join(" · ", Scene.SelectedElements.GroupBy(e => e.Type).OrderByDescending(g => g.Count())
        .Select(g => $"{g.Key} ×{g.Count()}"));

    private void RefreshMultiSelection()
    {
        OnPropertyChanged(nameof(SelectionCount));
        OnPropertyChanged(nameof(IsMultiSelection));
        OnPropertyChanged(nameof(SelectionHeaderText));
        OnPropertyChanged(nameof(SelectionTypesText));
        OnPropertyChanged(nameof(SelectedTitle));
        OnPropertyChanged(nameof(SelectedType));
        OnPropertyChanged(nameof(CanArray));
        OnPropertyChanged(nameof(UserParametersBatchText));
        RefreshParametricTools();
    }

    /// <summary>«Однотипные на этаже»: в 1 клик выделяет все элементы того же типа на этаже главного элемента.</summary>
    [RelayCommand]
    private void SelectSimilarOnFloor()
    {
        if (Scene.SelectedElement is not { } sample) return;
        int n = Scene.SelectSimilarOnFloor();
        ShowSuccess(Loc.Get("SelectSimilar"), Loc.Format("SelectSimilarDone", n, sample.Type, sample.LevelIndex + 1), "AIA-SELECT-SIMILAR");
    }

    /// <summary>Короткое уведомление об успешном действии (само скрывается через 10 секунд).</summary>
    private void ShowSuccess(string title, string message, string code)
    {
        _noticeVersion++;
        ErrorSeverity = Microsoft.UI.Xaml.Controls.InfoBarSeverity.Success;
        ErrorTitle = title;
        ErrorMessage = message;
        ErrorCode = code;
        ErrorDetails = message;
        ErrorActionLabel = Loc.Get("SolverLog");
        UpdateErrorReport();
        IsErrorOpen = true;
        AutoCloseNotice();
    }

    /// <summary>
    /// Пакетный инспектор: смещение X/Y/Z — приращением ко всем выделенным, поворот — всей группой вокруг её общего центра,
    /// изменённые W/H/D — абсолютным значением (по высоте низ элементов сохраняется), этаж — всем; один атомарный шаг Undo.
    /// </summary>
    private void ApplyBatchInspector(ArchitecturalElement primary)
    {
        const double eps = 5e-4;
        double dx = PosX - Math.Round(primary.X, 3), dy = PosY - Math.Round(primary.Y, 3), dz = PosZ - Math.Round(primary.Z, 3);
        if (Math.Abs(dx) < eps) dx = 0;
        if (Math.Abs(dy) < eps) dy = 0;
        if (Math.Abs(dz) < eps) dz = 0;
        bool w = Math.Abs(SizeW - Math.Round(primary.Width, 3)) > eps;
        bool h = Math.Abs(SizeH - Math.Round(primary.Height, 3)) > eps;
        bool d = Math.Abs(SizeD - Math.Round(primary.Depth, 3)) > eps;
        double dRot = Math.Abs(RotY - Math.Round(primary.RotationY, 2)) > 0.005 ? RotY - primary.RotationY : 0;
        int level = (int)Math.Round(LevelValue);
        bool levelChanged = level != primary.LevelIndex;
        if (dx == 0 && dy == 0 && dz == 0 && !w && !h && !d && dRot == 0 && !levelChanged) return;

        var selection = Scene.SelectedElements.ToList();
        var ids = selection.Select(e => e.Id).ToHashSet();
        Scene.PushUndo();
        // Сначала корни (их проёмы движутся вместе с хостом), затем проёмы выделенных стен — только габариты и этаж.
        foreach (var e in selection.OrderBy(e => e.HostElementId is { } host && ids.Contains(host) ? 1 : 0))
        {
            bool child = e.HostElementId is { } host && ids.Contains(host);
            double width = w ? SizeW : e.Width, height = h ? SizeH : e.Height, depth = d ? SizeD : e.Depth;
            double x = e.X + (child ? 0 : dx), z = e.Z + (child ? 0 : dz);
            double y = (h ? e.BaseY + height / 2 : e.Y) + (child ? 0 : dy);
            Scene.SetTransform(e, x, y, z, width, height, depth, e.RotationY, levelChanged ? level : e.LevelIndex, recordUndo: false);
            if (MaterialLayers.AbsorbThickness(e)) Scene.NotifyElementsChanged([e]);
        }
        // Поворот — вокруг общего центра группы (жёсткий блок), а не каждого элемента вокруг своего центра.
        if (dRot != 0) Scene.RotateGroup(selection, dRot, recordUndo: false);
        SyncInspector();
        RefreshLayers();
    }

    // ───────────────────────────── Массив и тиражирование этажей ─────────────────────────────

    /// <summary>Количество экземпляров в ряду (вместе с исходным), 2…20.</summary>
    [ObservableProperty]
    public partial double ArrayCount { get; set; } = 4;

    /// <summary>Шаг массива по X, мм.</summary>
    [ObservableProperty]
    public partial double ArrayStepXMm { get; set; } = 6000;

    /// <summary>Шаг массива по Z, мм.</summary>
    [ObservableProperty]
    public partial double ArrayStepZMm { get; set; }

    public bool CanArray => Scene.SelectedElement is not null;

    [RelayCommand]
    private void CreateArray()
    {
        if (Scene.SelectedElement is null) return;
        int count = (int)Math.Clamp(Math.Round(double.IsFinite(ArrayCount) ? ArrayCount : 2), 2, 20);
        double sx = double.IsFinite(ArrayStepXMm) ? ArrayStepXMm : 0, sz = double.IsFinite(ArrayStepZMm) ? ArrayStepZMm : 0;
        var sources = Scene.SelectedElements.ToList();
        var created = Scene.CreateLinearArray(sources, count, sx / 1000, sz / 1000);
        if (created.Count == 0)
        {
            ShowNotice(Loc.Get("ArrayTitle"), Loc.Get("ArrayNothing"), "AIA-ARRAY-NONE");
            return;
        }
        Scene.SetSelection(sources.Concat(created.Where(c => c.HostElementId is null || created.All(o => o.Id != c.HostElementId))), Scene.SelectedElement);
        string message = Loc.Format("ArrayDone", count, PushPullService.Millimeters(sx / 1000, unit: Loc.Get("Mm")), PushPullService.Millimeters(sz / 1000, unit: Loc.Get("Mm")), created.Count);
        ChatMessages.Add(ChatMessageModel.System(message));
        ShowSuccess(Loc.Get("ArrayTitle"), message, "AIA-ARRAY-OK");
    }

    /// <summary>«Копировать этаж вверх»: выделенная группа или весь этаж главного элемента → LevelIndex + 1 с подъёмом на высоту этажа.</summary>
    [RelayCommand]
    private void CopyFloorUp()
    {
        if (Scene.SelectedElement is not { } primary) return;
        var sources = Scene.IsMultiSelection ? Scene.SelectedElements.ToList() : Scene.FloorOf(primary);
        double height = Scene.FloorHeight(primary.BuildingGroupId, primary.LevelIndex);
        var created = Scene.CopyFloorUp(sources);
        if (created.Count == 0) return;
        string message = Loc.Format("FloorCopied", primary.LevelIndex + 1, primary.LevelIndex + 2, created.Count, PushPullService.Millimeters(height, signed: true, unit: Loc.Get("Mm")));
        ChatMessages.Add(ChatMessageModel.System(message));
        ShowSuccess(Loc.Get("ArrayFloorUp"), message, "AIA-FLOOR-COPY");
    }

    // ───────────────────────────── Push/Pull ─────────────────────────────

    /// <summary>Ввод точного размера (мм) с клавиатуры или конец перетаскивания: толщина пирога следует за элементом.</summary>
    public PushPullResult? ApplyPushPull(ArchitecturalElement element, PushPullFace face, double newLength, bool recordUndo = true)
    {
        var result = Scene.PushPull(element, face, newLength, recordUndo);
        CompletePushPull(element);
        return result;
    }

    /// <summary>После изменения толщины стены/плиты ручкой проектную толщину принимает несущий слой пирога.</summary>
    public void CompletePushPull(ArchitecturalElement element)
    {
        if (!MaterialLayers.AbsorbThickness(element)) return;
        RefreshLayers();
        Scene.NotifyElementsChanged([element]);
    }

    // ───────────────────────────── Режим прогулки ─────────────────────────────

    /// <summary>Прогулка от первого лица: рост 1.70 м, WASD и мышь, Q/E — этаж, автоподъём по лестницам и плитам.</summary>
    [ObservableProperty]
    public partial bool IsWalkthrough { get; set; }

    /// <summary>Строка состояния прогулки (этаж и отметка пола под ногами) — обновляет вьюпорт.</summary>
    [ObservableProperty]
    public partial string WalkStatusText { get; set; } = string.Empty;

    partial void OnIsWalkthroughChanged(bool value)
    {
        if (value)
        {
            // Прогулка — только в перспективе; карта напряжений и секущий бокс не мешают обзору.
            if (ViewMode != ViewportMode.Perspective3D) ViewMode = ViewportMode.Perspective3D;
            Scene.SelectedElement = null;
        }
    }

    [RelayCommand]
    private void ToggleWalkthrough() => IsWalkthrough = !IsWalkthrough;

    // ───────────────────────────── Инсоляция и тени ─────────────────────────────

    /// <summary>Панель «Инсоляция и Тени» и падающие тени на участке.</summary>
    [ObservableProperty]
    public partial bool IsSolarEnabled { get; set; }

    /// <summary>Местное время региона проекта, ч (4.0 … 22.0, шаг 15 минут).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SolarTimeText), nameof(SolarInfoText), nameof(SolarShadowText))]
    public partial double SolarHours { get; set; } = 13.5;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSeasonSummer), nameof(IsSeasonEquinox), nameof(IsSeasonWinter), nameof(SolarInfoText), nameof(SolarShadowText))]
    public partial SolarSeason SolarSeason { get; set; } = SolarSeason.Summer;

    /// <summary>Положение Солнца или его вид (время, сезон, вкл/выкл) изменились — вьюпорт перестраивает свет и тени.</summary>
    public event EventHandler? SolarChanged;

    partial void OnIsSolarEnabledChanged(bool value) => SolarChanged?.Invoke(this, EventArgs.Empty);

    partial void OnSolarHoursChanged(double value)
    {
        if (!double.IsFinite(value))
        {
            SolarHours = 13.5;
            return;
        }
        double snapped = Math.Clamp(Math.Round(value * 4) / 4, 4, 22);
        if (Math.Abs(snapped - value) > 1e-9)
        {
            SolarHours = snapped;
            return;
        }
        SolarChanged?.Invoke(this, EventArgs.Empty);
    }

    partial void OnSolarSeasonChanged(SolarSeason value) => SolarChanged?.Invoke(this, EventArgs.Empty);

    /// <summary>Положение Солнца — по широте, долготе и часовому поясу выбранного региона проекта.</summary>
    public SunPosition Sun => SolarShadowService.Compute(SolarHours, SolarSeason, SolarLocation);

    // ───────────────────────────── Регион строительства и инсоляция по СанПиН ─────────────────────────────

    /// <summary>Регионы для ComboBox (подписи переводятся на месте при смене языка — без пересборки списка).</summary>
    public System.Collections.ObjectModel.ObservableCollection<SolarRegionOption> SolarRegions { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SolarLocation), nameof(SolarSiteText), nameof(SolarInfoText), nameof(SolarShadowText), nameof(InsolationNormText))]
    public partial SolarRegionOption? SelectedSolarRegion { get; set; }

    public SolarLocation SolarLocation => SelectedSolarRegion?.Location ?? SolarLocations.Moscow;

    private void InitializeSolarRegions()
    {
        foreach (var location in SolarLocations.All) SolarRegions.Add(new SolarRegionOption(location, location.Name(Loc.IsRussian)));
        SelectRegion(Settings.Current.SolarRegionId);
    }

    /// <summary>Выбор региона по идентификатору (настройки, файл проекта .aia, AIA_DEBUG_REGION).</summary>
    public void SelectRegion(string? id)
    {
        var location = SolarLocations.Resolve(id);
        SelectedSolarRegion = SolarRegions.FirstOrDefault(r => r.Location.Id == location.Id) ?? SolarRegions.FirstOrDefault();
    }

    partial void OnSelectedSolarRegionChanged(SolarRegionOption? value)
    {
        if (value is null) return;
        if (Settings.Current.SolarRegionId != value.Location.Id) SaveSettingsSafe(s => s.SolarRegionId = value.Location.Id);
        InsolationRows.Clear();
        InsolationSummaryText = string.Empty;
        SolarChanged?.Invoke(this, EventArgs.Empty);
    }

    private void RefreshSolarLanguage()
    {
        foreach (var r in SolarRegions) r.Title = r.Location.Name(Loc.IsRussian);
        OnPropertyChanged(nameof(SolarSiteText));
        OnPropertyChanged(nameof(InsolationNormText));
    }

    /// <summary>«Участок: 43.12° с.ш., 131.89° в.д., UTC+10 · южная зона (южнее 48° с.ш.) · север — вверх плана».</summary>
    public string SolarSiteText => Loc.Format("SolarSite", SolarLocation.Coordinates(Loc.IsRussian), InsolationService.NormFor(SolarLocation.Latitude).ZoneTitle(Loc.IsRussian));

    /// <summary>«Норма: 2 ч 00 мин непрерывно · расчётная дата 22 апреля».</summary>
    public string InsolationNormText
    {
        get
        {
            var norm = InsolationService.NormFor(SolarLocation.Latitude);
            return Loc.Format("InsolationNorm", InsolationService.HoursText(norm.RequiredHours, Loc.IsRussian), norm.DateTitle(Loc.IsRussian));
        }
    }

    /// <summary>Результаты последней проверки окон (строки панели «Инсоляция и Тени»).</summary>
    public System.Collections.ObjectModel.ObservableCollection<InsolationRow> InsolationRows { get; } = [];

    [ObservableProperty]
    public partial string InsolationSummaryText { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CheckInsolationCommand))]
    public partial bool IsInsolationBusy { get; set; }

    private bool CanCheckInsolation() => !IsInsolationBusy;

    /// <summary>
    /// Окна для проверки: выделенные окна и витражи, окна выделенных стен; без такого выделения — все видимые окна (фильтр этажа).
    /// </summary>
    public List<ArchitecturalElement> InsolationTargets()
    {
        var picked = new List<ArchitecturalElement>();
        foreach (var e in Scene.SelectedElements)
        {
            if (InsolationService.IsInsolated(e)) picked.Add(e);
            else if (ElementTypes.IsHostType(e.Type)) picked.AddRange(Scene.GetHostedElements(e.Id).Where(InsolationService.IsInsolated));
        }
        if (picked.Count == 0) picked.AddRange(Scene.VisibleElements.Where(e => e.Type == ElementTypes.Window));
        return picked.Distinct().ToList();
    }

    /// <summary>Расчёт продолжительности инсоляции окон по СанПиН в фоне (снимок сцены), итог — в панель, чат и InfoBar.</summary>
    [RelayCommand(CanExecute = nameof(CanCheckInsolation))]
    private async Task CheckInsolationAsync()
    {
        var targets = InsolationTargets();
        InsolationRows.Clear();
        if (targets.Count == 0)
        {
            InsolationSummaryText = Loc.Get("InsolationNone");
            ShowNotice(Loc.Get("InsolationTitle"), InsolationSummaryText, "AIA-INSOLATION-NONE");
            return;
        }
        IsInsolationBusy = true;
        try
        {
            var snapshot = Scene.Elements.Select(e => e.Clone()).ToList();
            var ids = targets.Select(t => t.Id).ToHashSet();
            var windows = snapshot.Where(e => ids.Contains(e.Id)).ToList();
            var location = SolarLocation;
            var report = await Task.Run(() => InsolationService.Analyze(snapshot, windows, location));
            ApplyInsolationReport(report);
        }
        finally
        {
            IsInsolationBusy = false;
        }
    }

    public void ApplyInsolationReport(InsolationReport report)
    {
        bool ru = Loc.IsRussian;
        InsolationRows.Clear();
        foreach (var w in report.Windows.OrderBy(w => w.Passes).ThenBy(w => w.ContinuousHours))
        {
            string detail = w.Intervals.Count == 0
                ? Loc.Get("InsolationNoSun")
                : Loc.Format("InsolationDetail", string.Join(", ", w.Intervals.Select(i => $"{SolarShadowService.TimeText(i.Start)}–{SolarShadowService.TimeText(i.End)}")),
                    InsolationService.HoursText(w.TotalHours, ru)) + (w.Intermittent ? " · " + Loc.Get("InsolationIntermittent") : string.Empty);
            InsolationRows.Add(new InsolationRow(w.Window.Id, w.Name, InsolationService.HoursText(w.ContinuousHours, ru), detail, w.Passes));
        }
        InsolationSummaryText = Loc.Format("InsolationSummary", report.Windows.Count, report.PassCount, report.FailCount, report.ElapsedMilliseconds);
        string message = Loc.Format("InsolationDone", report.Location.Name(ru), report.Norm.ZoneTitle(ru), report.Norm.DateTitle(ru),
            InsolationService.HoursText(report.Norm.RequiredHours, ru), report.Windows.Count, report.PassCount, report.FailCount);
        ChatMessages.Add(ChatMessageModel.System(message));
        if (report.FailCount == 0) ShowSuccess(Loc.Get("InsolationTitle"), message, "AIA-INSOLATION-OK");
        else ShowNotice(Loc.Get("InsolationTitle"), message, "AIA-INSOLATION-FAIL");
    }

    /// <summary>Клик по строке результата — выделить окно во вьюпорте.</summary>
    public void SelectInsolationRow(InsolationRow row) => Scene.SelectById(row.ElementId);

    public bool IsSeasonSummer { get => SolarSeason == SolarSeason.Summer; set { if (value) SolarSeason = SolarSeason.Summer; } }
    public bool IsSeasonEquinox { get => SolarSeason == SolarSeason.Equinox; set { if (value) SolarSeason = SolarSeason.Equinox; } }
    public bool IsSeasonWinter { get => SolarSeason == SolarSeason.Winter; set { if (value) SolarSeason = SolarSeason.Winter; } }

    public string SolarTimeText => SolarShadowService.TimeText(SolarHours);

    public string SolarInfoText
    {
        get
        {
            var sun = Sun;
            if (!sun.IsAboveHorizon) return Loc.Get("SolarBelow");
            return Loc.Format("SolarSunInfo", sun.AltitudeDeg, sun.AzimuthDeg, Compass(sun.AzimuthDeg));
        }
    }

    public string SolarShadowText
    {
        get
        {
            var sun = Sun;
            return sun.IsAboveHorizon ? Loc.Format("SolarShadowInfo", sun.ShadowRatio) : string.Empty;
        }
    }

    [RelayCommand]
    private void ToggleSolar() => IsSolarEnabled = !IsSolarEnabled;

    private string Compass(double azimuth)
    {
        string[] ru = ["С", "СВ", "В", "ЮВ", "Ю", "ЮЗ", "З", "СЗ"];
        string[] en = ["N", "NE", "E", "SE", "S", "SW", "W", "NW"];
        int i = (int)Math.Round(((azimuth % 360) + 360) % 360 / 45) % 8;
        return Loc.IsRussian ? ru[i] : en[i];
    }
}

/// <summary>Регион строительства в списке панели «Инсоляция и Тени»: подпись переводится на месте при смене языка.</summary>
public sealed partial class SolarRegionOption(SolarLocation location, string title) : ObservableObject
{
    public SolarLocation Location { get; } = location;

    [ObservableProperty]
    public partial string Title { get; set; } = title;
}

/// <summary>Строка результата проверки инсоляции окна: непрерывная продолжительность, интервалы, соответствие СанПиН.</summary>
public sealed record InsolationRow(string ElementId, string Name, string HoursText, string Detail, bool Passes)
{
    /// <summary>Глиф Segoe Fluent Icons: галочка — норма выполнена, крест — нет.</summary>
    public string Glyph => Passes ? "\uE73E" : "\uE711";
}
