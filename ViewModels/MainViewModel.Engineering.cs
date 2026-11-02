using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using AIArchitectStudio.Models;
using AIArchitectStudio.Rendering;
using AIArchitectStudio.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml.Controls;

namespace AIArchitectStudio.ViewModels;

/// <summary>Режим 3D-карты напряжений.</summary>
public enum StressMapMode
{
    Utilization,
    Deflection,
    Punching
}

/// <summary>Запрос вьюпорту: плавно сфокусировать камеру на узле коллизии.</summary>
public readonly record struct ClashFocus(double X, double Y, double Z, double Extent);

/// <summary>
/// Инженерный модуль студии: параметры площадки (снег, ветер, сейсмика, классы бетона и арматуры), 3D-карта напряжений
/// с легендой K_исп, карточка усилий выбранного элемента, редактор «пирога» конструкции и поиск коллизий с автоисправлением.
/// </summary>
public sealed partial class MainViewModel
{
    private bool _syncingLoads;
    private bool _committingLayers;
    private bool _clashQueued;

    public StructuralAnalysisService Structure { get; }

    public StructuralAnalysisResult Analysis => Structure.Current;

    /// <summary>Расчёт обновлён, включена/выключена карта или сменился режим — вьюпорт перекрашивает элементы.</summary>
    public event EventHandler<bool>? StressMapChanged;

    /// <summary>Плавный фокус камеры на узле коллизии.</summary>
    public event EventHandler<ClashFocus>? ClashFocusRequested;

    private void InitializeEngineering()
    {
        Structure.AnalysisChanged += (_, _) => RunOnUi(OnAnalysisChanged);
        Scene.DocumentChanged += (_, _) => ScheduleClashDetection();
        Settings.SettingsChanged += (_, _) => RunOnUi(SyncLoadsFromSettings);
        SyncLoadsFromSettings();
        RunClashDetection();
        OnAnalysisChanged();
    }

    // ───────────────────────────── Вкладка «Анализ» ─────────────────────────────

    public bool IsAnalysisTab
    {
        get => RightTab == 3;
        set { if (value) RightTab = 3; }
    }

    public IReadOnlyList<string> SnowRegionTitles { get; } =
        StructuralLoadSettings.SnowRegions.Select(r => $"{r.Title} · {r.Value:0} кг/м²").ToList();

    public IReadOnlyList<string> WindRegionTitles { get; } =
        StructuralLoadSettings.WindRegions.Select(r => $"{r.Title} · {r.Value:0} кг/м²").ToList();

    [ObservableProperty] public partial int SnowRegionIndex { get; set; } = -1;
    [ObservableProperty] public partial double SnowLoadKg { get; set; }
    [ObservableProperty] public partial int WindRegionIndex { get; set; } = -1;
    [ObservableProperty] public partial double WindLoadKg { get; set; }
    [ObservableProperty] public partial double LiveLoadValue { get; set; }
    [ObservableProperty] public partial double SoilBearingValue { get; set; }

    private StructuralLoadSettings Loads => Settings.Current.StructuralLoads;

    partial void OnSnowRegionIndexChanged(int value)
    {
        if (_syncingLoads || value < 0 || value >= StructuralLoadSettings.SnowRegions.Count) return;
        SnowLoadKg = StructuralLoadSettings.SnowRegions[value].Value;
    }

    partial void OnSnowLoadKgChanged(double value)
    {
        if (_syncingLoads || !double.IsFinite(value)) return;
        UpdateLoads(l => l.SnowKgM2 = value);
    }

    partial void OnWindRegionIndexChanged(int value)
    {
        if (_syncingLoads || value < 0 || value >= StructuralLoadSettings.WindRegions.Count) return;
        WindLoadKg = StructuralLoadSettings.WindRegions[value].Value;
    }

    partial void OnWindLoadKgChanged(double value)
    {
        if (_syncingLoads || !double.IsFinite(value)) return;
        UpdateLoads(l => l.WindKgM2 = value);
    }

    partial void OnLiveLoadValueChanged(double value)
    {
        if (_syncingLoads || !double.IsFinite(value)) return;
        UpdateLoads(l => l.LiveLoadKPa = value);
    }

    partial void OnSoilBearingValueChanged(double value)
    {
        if (_syncingLoads || !double.IsFinite(value)) return;
        UpdateLoads(l => l.SoilBearingKPa = value);
    }

    public bool IsSeismic6 { get => Loads.Seismicity == 6; set { if (value) UpdateLoads(l => l.Seismicity = 6); } }
    public bool IsSeismic7 { get => Loads.Seismicity == 7; set { if (value) UpdateLoads(l => l.Seismicity = 7); } }
    public bool IsSeismic8 { get => Loads.Seismicity == 8; set { if (value) UpdateLoads(l => l.Seismicity = 8); } }
    public bool IsSeismic9 { get => Loads.Seismicity == 9; set { if (value) UpdateLoads(l => l.Seismicity = 9); } }

    public bool IsConcreteB20 { get => Loads.Concrete == ConcreteClass.B20; set { if (value) UpdateLoads(l => l.Concrete = ConcreteClass.B20); } }
    public bool IsConcreteB25 { get => Loads.Concrete == ConcreteClass.B25; set { if (value) UpdateLoads(l => l.Concrete = ConcreteClass.B25); } }
    public bool IsConcreteB30 { get => Loads.Concrete == ConcreteClass.B30; set { if (value) UpdateLoads(l => l.Concrete = ConcreteClass.B30); } }
    public bool IsConcreteB40 { get => Loads.Concrete == ConcreteClass.B40; set { if (value) UpdateLoads(l => l.Concrete = ConcreteClass.B40); } }

    public bool IsRebarA240 { get => Loads.Rebar == RebarClass.A240; set { if (value) UpdateLoads(l => l.Rebar = RebarClass.A240); } }
    public bool IsRebarA400 { get => Loads.Rebar == RebarClass.A400; set { if (value) UpdateLoads(l => l.Rebar = RebarClass.A400); } }
    public bool IsRebarA500C { get => Loads.Rebar == RebarClass.A500C; set { if (value) UpdateLoads(l => l.Rebar = RebarClass.A500C); } }

    /// <summary>Сохраняет параметры площадки; расчёт и смета пересчитываются по SettingsChanged.</summary>
    private void UpdateLoads(Action<StructuralLoadSettings> mutate)
    {
        if (_syncingLoads) return;
        var probe = Loads.Clone();
        mutate(probe);
        probe.Sanitize();
        if (System.Text.Json.JsonSerializer.Serialize(probe) == System.Text.Json.JsonSerializer.Serialize(Loads)) return;
        SaveSettingsSafe(s =>
        {
            mutate(s.StructuralLoads);
            s.StructuralLoads.Sanitize();
        });
    }

    /// <summary>Поля вкладки «Анализ» из настроек (после загрузки проекта, смены пресета района и т.п.).</summary>
    private void SyncLoadsFromSettings()
    {
        _syncingLoads = true;
        try
        {
            var l = Loads;
            SnowLoadKg = l.SnowKgM2;
            WindLoadKg = l.WindKgM2;
            LiveLoadValue = l.LiveLoadKPa;
            SoilBearingValue = l.SoilBearingKPa;
            SnowRegionIndex = IndexOf(StructuralLoadSettings.SnowRegions, l.SnowKgM2);
            WindRegionIndex = IndexOf(StructuralLoadSettings.WindRegions, l.WindKgM2);
        }
        finally
        {
            _syncingLoads = false;
        }
        foreach (var name in new[]
                 {
                     nameof(IsSeismic6), nameof(IsSeismic7), nameof(IsSeismic8), nameof(IsSeismic9),
                     nameof(IsConcreteB20), nameof(IsConcreteB25), nameof(IsConcreteB30), nameof(IsConcreteB40),
                     nameof(IsRebarA240), nameof(IsRebarA400), nameof(IsRebarA500C)
                 })
            OnPropertyChanged(name);
        RefreshNormTips();
    }

    private static int IndexOf(IReadOnlyList<(string Title, double Value)> regions, double value)
    {
        for (int i = 0; i < regions.Count; i++)
            if (Math.Abs(regions[i].Value - value) < 1e-6) return i;
        return -1;
    }

    // ───────────────────────────── Карта напряжений и легенда ─────────────────────────────

    [ObservableProperty]
    public partial bool IsStressMapEnabled { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsStressModeUtilization), nameof(IsStressModeDeflection), nameof(IsStressModePunching), nameof(StressScaleText))]
    public partial StressMapMode StressMode { get; set; } = StressMapMode.Utilization;

    partial void OnIsStressMapEnabledChanged(bool value) => StressMapChanged?.Invoke(this, true);

    partial void OnStressModeChanged(StressMapMode value) => StressMapChanged?.Invoke(this, false);

    [RelayCommand]
    private void ToggleStressMap() => IsStressMapEnabled = !IsStressMapEnabled;

    public bool IsStressModeUtilization
    {
        get => StressMode == StressMapMode.Utilization;
        set { if (value) StressMode = StressMapMode.Utilization; }
    }

    public bool IsStressModeDeflection
    {
        get => StressMode == StressMapMode.Deflection;
        set { if (value) StressMode = StressMapMode.Deflection; }
    }

    public bool IsStressModePunching
    {
        get => StressMode == StressMapMode.Punching;
        set { if (value) StressMode = StressMapMode.Punching; }
    }

    public string StressScaleText => StressMode switch
    {
        StressMapMode.Deflection => Loc.Get("StressScaleF"),
        StressMapMode.Punching => Loc.Get("StressScaleP"),
        _ => Loc.Get("StressScaleK")
    };

    [ObservableProperty] public partial string StressMaxKText { get; set; } = "—";
    [ObservableProperty] public partial Windows.UI.Color StressMaxKColor { get; set; } = StressPalette.ToUi(StressPalette.Blue);
    [ObservableProperty] public partial string StressMaxDeflectionText { get; set; } = "—";
    [ObservableProperty] public partial string StressOverloadedText { get; set; } = "0";
    [ObservableProperty] public partial string StressPunchingText { get; set; } = "0";
    [ObservableProperty] public partial string StressTimeText { get; set; } = "—";
    [ObservableProperty] public partial string StressBaseShearText { get; set; } = "—";

    private void OnAnalysisChanged()
    {
        var a = Analysis;
        var c = Loc.Culture;
        double maxK = a.MaxUtilization;
        StressMaxKText = maxK.ToString("0.00", c);
        StressMaxKColor = StressPalette.ToUi(StressPalette.ColorFor(maxK));
        var worstF = a.Elements.Values.Where(r => r.DeflectionLimitMm > 0).OrderByDescending(r => r.DeflectionRatio).FirstOrDefault();
        StressMaxDeflectionText = worstF is null ? "—" : $"{worstF.DeflectionMm.ToString("0.0", c)} / {worstF.DeflectionLimitMm.ToString("0", c)} мм";
        StressOverloadedText = a.OverloadedCount.ToString(c);
        StressPunchingText = Loc.Format("PunchCount", a.PunchingCritical, a.PunchingReinforce, a.PunchingNodes.Count);
        StressTimeText = a.ElapsedMilliseconds.ToString("0.0", c) + " мс";
        StressBaseShearText = a.BaseShear.Count == 0
            ? "—"
            : string.Join(" · ", a.BaseShear.Select(p => $"{p.Key}: W {p.Value.Wind.ToString("N0", c)} / S {p.Value.Seismic.ToString("N0", c)} кН"));
        RefreshForces();
        StressMapChanged?.Invoke(this, false);
    }

    // ───────────────────────────── Карточка усилий выбранного элемента ─────────────────────────────

    [ObservableProperty] public partial bool HasForces { get; set; }
    [ObservableProperty] public partial bool IsNonStructuralSelected { get; set; }
    [ObservableProperty] public partial string ForceNText { get; set; } = "—";
    [ObservableProperty] public partial string ForceMText { get; set; } = "—";
    [ObservableProperty] public partial string ForceQText { get; set; } = "—";
    [ObservableProperty] public partial string ForceFText { get; set; } = "—";
    [ObservableProperty] public partial string ForceLambdaText { get; set; } = "—";
    [ObservableProperty] public partial string ForcePunchText { get; set; } = "—";
    [ObservableProperty] public partial string ForceKText { get; set; } = "—";
    [ObservableProperty] public partial string ForceGoverningText { get; set; } = string.Empty;
    [ObservableProperty] public partial string ForceNotesText { get; set; } = string.Empty;
    [ObservableProperty] public partial double ForceKFraction { get; set; }
    [ObservableProperty] public partial Windows.UI.Color ForceKColor { get; set; } = StressPalette.ToUi(StressPalette.Blue);
    [ObservableProperty] public partial Windows.UI.Color ForcePunchColor { get; set; } = StressPalette.ToUi(StressPalette.Blue);

    private void RefreshForces()
    {
        var el = Scene.SelectedElement;
        var r = Analysis.For(el?.Id);
        HasForces = r is not null;
        IsNonStructuralSelected = el is not null && r is null;
        PassportButtonText = el is null ? Loc.Get("PassportButton") : Loc.Format("PassportButtonFor", el.DisplayTitle);
        if (r is null) return;
        var c = Loc.Culture;
        bool slab = r.Type is ElementTypes.Slab or ElementTypes.CantileverSlab or ElementTypes.Roof or ElementTypes.Foundation;
        ForceNText = r.AxialForce > 0.05 ? r.AxialForce.ToString("N1", c) : "—";
        ForceMText = r.Moment > 0.005 ? r.Moment.ToString("N1", c) + (slab ? "/м" : string.Empty) : "—";
        ForceQText = r.Shear > 0.005 ? r.Shear.ToString("N1", c) + (slab ? "/м" : string.Empty) : "—";
        ForceFText = r.DeflectionLimitMm > 0
            ? $"{r.DeflectionMm.ToString("0.0", c)} из {r.DeflectionLimitMm.ToString("0.0", c)}" + (r.IsCantilever ? " (консоль)" : string.Empty)
            : "—";
        ForceLambdaText = r.Slenderness > 0 ? $"{r.Slenderness.ToString("0", c)} · {r.Phi.ToString("0.00", c)}" : r.Span > 0 ? $"L = {r.Span.ToString("0.00", c)} м" : "—";
        ForcePunchText = r.Punching switch
        {
            PunchingStatus.Ok => $"{Loc.Get("PunchOk")} · {r.KPunching.ToString("0.00", c)}",
            PunchingStatus.NeedsReinforcement => $"{Loc.Get("PunchReinf")} · {r.KPunching.ToString("0.00", c)}",
            PunchingStatus.Critical => $"{Loc.Get("PunchCritical")} · {r.KPunching.ToString("0.00", c)}",
            _ => Loc.Get("PunchNone")
        };
        ForcePunchColor = StressPalette.ToUi(r.Punching == PunchingStatus.None ? StressPalette.Blue : StressPalette.ColorFor(r.KPunching));
        double k = r.Utilization;
        ForceKText = k.ToString("0.00", c);
        ForceKFraction = Math.Clamp(k / 1.2, 0.02, 1);
        ForceKColor = StressPalette.ToUi(StressPalette.ColorFor(k));
        ForceGoverningText = Loc.Format("ForceGoverning", r.Governing) + (r.Floor > 0 ? $" · этаж +{r.Floor}" : string.Empty);
        ForceNotesText = string.Join("\n", r.Notes.Take(3));
    }

    // ───────────────────────────── Пирог конструкции ─────────────────────────────

    /// <summary>Редактируемые копии слоёв выбранного элемента (изменение фиксируется одним шагом Undo).</summary>
    public ObservableCollection<MaterialLayer> SelectedLayers { get; } = [];

    public bool IsLayerHostSelected => Scene.SelectedElement is { } el && MaterialLayers.IsLayerHost(el);

    public bool HasLayers => SelectedLayers.Count > 0;

    public bool HasNoLayers => SelectedLayers.Count == 0;

    public string LayersTotalText => SelectedLayers.Count == 0
        ? string.Empty
        : MaterialLayers.Summary(SelectedLayers);

    private void RefreshLayers()
    {
        if (_committingLayers) return;
        foreach (var l in SelectedLayers) l.PropertyChanged -= OnLayerEdited;
        SelectedLayers.Clear();
        if (Scene.SelectedElement is { } el && MaterialLayers.IsLayerHost(el))
            foreach (var layer in el.Layers)
            {
                var copy = layer.Clone();
                copy.PropertyChanged += OnLayerEdited;
                SelectedLayers.Add(copy);
            }
        OnPropertyChanged(nameof(IsLayerHostSelected));
        OnPropertyChanged(nameof(HasLayers));
        OnPropertyChanged(nameof(HasNoLayers));
        OnPropertyChanged(nameof(LayersTotalText));
    }

    private void OnLayerEdited(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is MaterialLayer layer && e.PropertyName == nameof(MaterialLayer.ThicknessMm) && (!double.IsFinite(layer.ThicknessMm) || layer.ThicknessMm < 1))
            layer.ThicknessMm = 1;
        if (e.PropertyName is nameof(MaterialLayer.RoleGlyph) or nameof(MaterialLayer.RoleTitle)) return;
        CommitLayers(SelectedLayers.Select(l => l.Clone()).ToList());
    }

    /// <summary>Записывает пирог в элемент (Undo), синхронизирует толщину элемента и глубину вырезов его проёмов.</summary>
    private void CommitLayers(List<MaterialLayer> layers)
    {
        if (Scene.SelectedElement is not { } el || !MaterialLayers.IsLayerHost(el)) return;
        _committingLayers = true;
        try
        {
            var hosted = Scene.GetOpeningsFor(el);
            Scene.UpdateElement(el, e =>
            {
                e.Layers = layers;
                MaterialLayers.ApplyThickness(e, hosted);
            });
            SyncInspector();
        }
        finally
        {
            _committingLayers = false;
        }
        OnPropertyChanged(nameof(LayersTotalText));
        OnPropertyChanged(nameof(HasLayers));
        OnPropertyChanged(nameof(HasNoLayers));
    }

    [RelayCommand]
    private void AddLayer()
    {
        if (Scene.SelectedElement is not { } el || !MaterialLayers.IsLayerHost(el)) return;
        var layers = SelectedLayers.Select(l => l.Clone()).ToList();
        if (layers.Count == 0)
        {
            // Первый слой — несущий, на всю текущую толщину элемента.
            double t = Math.Round(MaterialLayers.ElementThickness(el) * 1000, 1);
            string name = el.Type == ElementTypes.Wall && !el.Material.Contains("concrete", StringComparison.OrdinalIgnoreCase)
                ? el.Material.Contains("brick", StringComparison.OrdinalIgnoreCase) ? "Кирпичная кладка" : "Кладка"
                : "Монолитный ж/б B25";
            layers.Add(new MaterialLayer(name, t, MaterialLayers.DefaultDensity(MaterialLayerRole.Structural, name), MaterialLayerRole.Structural));
        }
        else layers.Add(new MaterialLayer("Утеплитель", 100, 35, MaterialLayerRole.Insulation));
        CommitLayers(layers);
        RefreshLayers();
    }

    [RelayCommand]
    private void ApplyLayerPreset(string presetId)
    {
        if (Scene.SelectedElement is not { } el || !MaterialLayers.IsLayerHost(el)) return;
        if (Scene.IsMultiSelection)
        {
            CommitLayersBatch(e => MaterialLayers.PresetFor(e, presetId));
            return;
        }
        CommitLayers(MaterialLayers.PresetFor(el, presetId));
        RefreshLayers();
    }

    [RelayCommand]
    private void ClearLayers()
    {
        if (Scene.SelectedElement is null) return;
        if (Scene.IsMultiSelection)
        {
            CommitLayersBatch(_ => []);
            return;
        }
        if (SelectedLayers.Count == 0) return;
        CommitLayers([]);
        RefreshLayers();
    }

    /// <summary>Пакетная правка: пирог всем выделенным стенам/плитам/кровлям/фундаментам одним атомарным шагом Undo.</summary>
    private void CommitLayersBatch(Func<ArchitecturalElement, List<MaterialLayer>> layers)
    {
        var targets = Scene.SelectedElements.Where(MaterialLayers.IsLayerHost).ToList();
        if (targets.Count == 0) return;
        _committingLayers = true;
        try
        {
            Scene.UpdateElements(targets, e =>
            {
                e.Layers = layers(e);
                MaterialLayers.ApplyThickness(e, Scene.GetOpeningsFor(e));
            });
            SyncInspector();
        }
        finally
        {
            _committingLayers = false;
        }
        RefreshLayers();
    }

    public void RemoveLayer(MaterialLayer layer)
    {
        int index = SelectedLayers.IndexOf(layer);
        if (index < 0) return;
        var layers = SelectedLayers.Select(l => l.Clone()).ToList();
        layers.RemoveAt(index);
        CommitLayers(layers);
        RefreshLayers();
    }

    /// <summary>Клик по глифу назначения слоя: Несущий → Изоляция → Стяжка → Гидроизоляция → Отделка.</summary>
    public void CycleLayerRole(MaterialLayer layer)
    {
        int index = SelectedLayers.IndexOf(layer);
        if (index < 0) return;
        var layers = SelectedLayers.Select(l => l.Clone()).ToList();
        var role = (MaterialLayerRole)(((int)layers[index].Role + 1) % Enum.GetValues<MaterialLayerRole>().Length);
        layers[index].Role = role;
        layers[index].ColorHex = MaterialLayers.DefaultColor(role, layers[index].Name);
        CommitLayers(layers);
        RefreshLayers();
    }

    /// <summary>После правки толщины в инспекторе проектную толщину принимает несущий слой пирога.</summary>
    private void AbsorbInspectorThickness(ArchitecturalElement el)
    {
        if (!MaterialLayers.AbsorbThickness(el)) return;
        RefreshLayers();
        Scene.NotifyElementsChanged([el]);
    }

    // ───────────────────────────── Коллизии ─────────────────────────────

    public ObservableCollection<ClashItem> Clashes { get; } = [];

    [ObservableProperty] public partial string ClashSummaryText { get; set; } = string.Empty;
    [ObservableProperty] public partial bool HasClashes { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFocusedClash), nameof(FocusedClashText), nameof(FocusedClashHasFix), nameof(FocusedClashFixLabel))]
    public partial ClashItem? FocusedClash { get; set; }

    public bool HasFocusedClash => FocusedClash is not null;
    public string FocusedClashText => FocusedClash?.Description ?? string.Empty;
    public bool FocusedClashHasFix => FocusedClash?.HasFix == true;
    public string FocusedClashFixLabel => FocusedClash?.FixLabel ?? string.Empty;

    /// <summary>Подсвеченная коллизия изменилась — вьюпорт рисует контрастные контуры пары элементов.</summary>
    public event EventHandler? ClashHighlightChanged;

    partial void OnFocusedClashChanged(ClashItem? value) => ClashHighlightChanged?.Invoke(this, EventArgs.Empty);

    private void ScheduleClashDetection()
    {
        if (_clashQueued) return;
        _clashQueued = true;
        RunOnUiDeferred(() =>
        {
            _clashQueued = false;
            RunClashDetection();
        });
    }

    private void RunOnUiDeferred(Action action)
    {
        if (_dispatcher is null) action();
        else _dispatcher.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => action());
    }

    [RelayCommand]
    private void RunClashDetection()
    {
        var items = ClashDetectionService.Detect(Scene.Elements.ToList());
        Clashes.Clear();
        foreach (var item in items) Clashes.Add(item);
        HasClashes = items.Count > 0;
        int critical = items.Count(i => i.IsCritical);
        ClashSummaryText = items.Count == 0 ? Loc.Get("ClashNone") : Loc.Format("ClashCount", critical, items.Count - critical);
        // Подсветка исчезает, если коллизия устранена (сохраняется по паре элементов и категории).
        if (FocusedClash is { } f)
            FocusedClash = items.FirstOrDefault(i => i.ElementAId == f.ElementAId && i.ElementBId == f.ElementBId && i.Category == f.Category);
    }

    /// <summary>Клик по коллизии: выделение элемента, контрастная подсветка пары и плавный фокус камеры на узле.</summary>
    public void FocusClash(ClashItem item)
    {
        FocusedClash = item;
        if (Scene.FindById(item.ElementAId) is { } a) Scene.SelectedElement = a;
        ClashFocusRequested?.Invoke(this, new ClashFocus(item.LocationPoint[0], item.LocationPoint[1], item.LocationPoint[2], item.Extent));
    }

    public void FixClash(ClashItem item)
    {
        string? result;
        try
        {
            result = ClashDetectionService.ApplyFix(Scene, item);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            App.WriteCrashLog("clash-fix", ex);
            result = null;
        }
        if (result is null)
        {
            ShowNotice(Loc.Get("ClashTitle"), Loc.Get("ClashFixFailed"), "AIA-CLASH-FIX-NONE");
            return;
        }
        if (FocusedClash == item) FocusedClash = null;
        RunClashDetection();
        ShowNotice(Loc.Get("ClashFixDone"), result, "AIA-CLASH-FIX-OK");
        ChatMessages.Add(ChatMessageModel.System($"{Loc.Get("ClashFixDone")}: {result}"));
    }

    [RelayCommand]
    private void FixFocusedClash()
    {
        if (FocusedClash is { } item) FixClash(item);
    }

    [RelayCommand]
    private void CloseFocusedClash() => FocusedClash = null;

    // ───────────────────────────── Смета: сталь ─────────────────────────────

    [ObservableProperty] public partial string TotalSteelText { get; set; } = "0";
    [ObservableProperty] public partial string TotalSteelCostText { get; set; } = "0 ₽";

    private void ApplySteelTotals(BimSpecification spec)
    {
        var c = Loc.Culture;
        TotalSteelText = (spec.TotalSteelKg / 1000.0).ToString("N2", c);
        TotalSteelCostText = spec.TotalSteelCost.ToString("N0", c) + " ₽";
    }

    /// <summary>Параметры площадки, сохранённые в проекте .aia v4, применяются к текущему расчёту.</summary>
    private void ApplyProjectLoads(SceneProjectParameters p)
    {
        if (p.StructuralLoads is not { } loads) return;
        loads.Sanitize();
        SaveSettingsSafe(s =>
        {
            s.StructuralLoads = loads.Clone();
            if (p.RebarPricePerTon > 0) s.RebarPricePerTon = p.RebarPricePerTon;
        });
        SyncLoadsFromSettings();
    }
}
