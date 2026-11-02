using System.Diagnostics;
using AIArchitectStudio.Models;
using AIArchitectStudio.Rendering;
using AIArchitectStudio.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml.Controls;

namespace AIArchitectStudio.ViewModels;

/// <summary>
/// Двусторонний обмен с AutoCAD (DXF-подложка, автоподъём и создание по клику) и Renga/Revit (импорт IFC), секущий
/// параллелепипед Section Box и выпуск рабочих чертежей ГОСТ в векторный PDF.
/// </summary>
public sealed partial class MainViewModel
{
    private CadDxfImporterService DxfImporter => App.Services.GetRequiredService<CadDxfImporterService>();
    private CadIfcImporterService IfcImporter => App.Services.GetRequiredService<CadIfcImporterService>();
    private GostDrawingPdfExporter GostExporter => App.Services.GetRequiredService<GostDrawingPdfExporter>();

    // ───────────────────────────── Открытие файлов ─────────────────────────────

    /// <summary>Единое «Открыть»: .aia — проект, .ifc — модель Renga/Revit/ArchiCAD, .dxf — подложка AutoCAD.</summary>
    public async Task OpenAnyAsync(string path)
    {
        switch (Path.GetExtension(path).ToLowerInvariant())
        {
            case ".ifc":
                await ImportIfcAsync(path);
                break;
            case ".dxf":
                await LoadDxfAsync(path, liftTo3D: false);
                break;
            default:
                await OpenProjectAsync(path);
                break;
        }
    }

    /// <summary>
    /// Импорт IFC: новая сцена (с шагом Undo) или «+ К участку» — постройки ставятся на свободное место участка.
    /// </summary>
    public async Task ImportIfcAsync(string path)
    {
        IsBusy = true;
        BusyText = Loc.Format("ImportBusy", "IFC");
        try
        {
            var sw = Stopwatch.StartNew();
            var result = await IfcImporter.ImportAsync(path);
            if (result.Elements.Count == 0) throw new InvalidDataException(Loc.Get("ImportEmpty"));
            string message;
            if (IsAppendToSite && Scene.Elements.Count > 0)
            {
                double offset = Scene.AppendBuildings(result.Groups, result.Elements, axes: result.Axes);
                message = Loc.Format("IfcAppended", Path.GetFileName(path), result.Elements.Count, offset);
            }
            else
            {
                Scene.LoadScene(result.Elements, result.Groups, recordUndo: Scene.Elements.Count > 0, axes: result.Axes, dimensions: []);
                Scene.ProjectName = string.IsNullOrWhiteSpace(result.ProjectName) ? Path.GetFileNameWithoutExtension(path) : result.ProjectName;
                Scene.ProjectFilePath = null;
                message = Loc.Format("IfcLoaded", Path.GetFileName(path), result.Elements.Count, result.Schema);
            }
            sw.Stop();
            ReportImport(message, "CadIfcImporterService", result.Report, result.Elements.Count, sw.Elapsed.TotalMilliseconds, "AIA-IMPORT-IFC-OK");
            ZoomExtentsRequested?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or FormatException or ArgumentException or InvalidOperationException or KeyNotFoundException)
        {
            App.WriteCrashLog("import-ifc", ex);
            ShowError(Loc.Get("ErrorImport"), ex.Message, $"AIA-IMPORT-IFC-{ex.HResult:X8}", ex.ToString());
        }
        finally
        {
            IsBusy = false;
            BusyText = string.Empty;
        }
    }

    private void ReportImport(string message, string source, IReadOnlyList<string> details, int count, double ms, string code)
    {
        var report = new SolverReport
        {
            Success = true,
            ElapsedMilliseconds = ms,
            InputElements = count,
            GeneratedElements = count,
            HostedOpenings = Scene.Elements.Count(e => e.HostElementId is not null),
            Actions = details.ToList()
        };
        ChatMessages.Add(ChatMessageModel.Assistant(message, source, report));
        _noticeVersion++;
        ErrorSeverity = InfoBarSeverity.Success;
        ErrorTitle = Loc.Get("ImportDoneTitle");
        ErrorMessage = message;
        ErrorCode = code;
        ErrorDetails = string.Join("\n", details);
        ErrorActionLabel = Loc.Get("SolverLog");
        UpdateErrorReport();
        IsErrorOpen = true;
        AutoCloseNotice();
    }

    // ───────────────────────────── DXF-подложка ─────────────────────────────

    /// <summary>Загруженная подложка AutoCAD (null — нет).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUnderlay), nameof(UnderlayTitle))]
    public partial DxfDrawing? Underlay { get; set; }

    [ObservableProperty]
    public partial bool IsUnderlayVisible { get; set; } = true;

    public bool HasUnderlay => Underlay is not null;

    public string UnderlayTitle => Underlay is { } d
        ? $"{d.FileName} · {d.Contours.Count} · {d.UnitsTitle}"
        : string.Empty;

    /// <summary>Контур подложки, выбранный кликом во вьюпорте (режим Б).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUnderlaySelection), nameof(UnderlaySelectionTitle))]
    public partial DxfContour? UnderlaySelection { get; set; }

    public DxfPoint UnderlayClick { get; private set; }

    public bool HasUnderlaySelection => UnderlaySelection is not null;

    public string UnderlaySelectionTitle => UnderlaySelection is { } c
        ? $"{c.Layer} · {(c.IsCircle ? Loc.Get("ContourCircle") : c.Closed ? Loc.Get("ContourClosed") : Loc.Get("ContourLine"))}"
        : string.Empty;

    /// <summary>Подложка или её выделение изменились — вьюпорт перерисовывает линии.</summary>
    public event EventHandler? UnderlayChanged;

    partial void OnUnderlayChanged(DxfDrawing? value)
    {
        UnderlaySelection = null;
        UnderlayChanged?.Invoke(this, EventArgs.Empty);
    }

    partial void OnIsUnderlayVisibleChanged(bool value)
    {
        if (!value) UnderlaySelection = null;
        UnderlayChanged?.Invoke(this, EventArgs.Empty);
    }

    partial void OnUnderlaySelectionChanged(DxfContour? value) => UnderlayChanged?.Invoke(this, EventArgs.Empty);

    /// <summary>Отметка подложки: отметка активного этажа фильтра (или 0) — линии лежат на 1 см выше.</summary>
    public double UnderlayBaseElevation
    {
        get
        {
            if (Scene.ActiveLevelFilter < 0) return 0;
            var levels = StructuralAxesBuilder.LevelElevations(Scene.Elements.ToList());
            return levels.FirstOrDefault(l => l.Level == Scene.ActiveLevelFilter)?.Elevation ?? 0;
        }
    }

    /// <summary>Высота этажа под подложкой: по соседним отметкам, иначе 3 м.</summary>
    private double UnderlayLevelHeight
    {
        get
        {
            var levels = StructuralAxesBuilder.LevelElevations(Scene.Elements.ToList());
            int level = Math.Max(0, Scene.ActiveLevelFilter);
            var cur = levels.FirstOrDefault(l => l.Level == level);
            var next = levels.FirstOrDefault(l => l.Level == level + 1);
            return cur is not null && next is not null && next.Elevation - cur.Elevation > 1.5 ? next.Elevation - cur.Elevation : 3.0;
        }
    }

    public async Task LoadDxfAsync(string path, bool liftTo3D)
    {
        IsBusy = true;
        BusyText = Loc.Format("ImportBusy", "DXF");
        try
        {
            var sw = Stopwatch.StartNew();
            var drawing = await DxfImporter.LoadAsync(path);
            Underlay = drawing;
            IsUnderlayVisible = true;
            sw.Stop();
            var details = new List<string>
            {
                $"Единицы: {drawing.UnitsTitle} ({drawing.UnitsSource}), масштаб ×{drawing.UnitScale:0.####}",
                drawing.IsOwnExport ? "Файл Architect Studio Pro — координаты сцены без сдвига" : $"Центрирование: сдвиг ({drawing.Offset.X:0.###}; {drawing.Offset.Y:0.###}) м",
                $"Габарит: {drawing.MaxX - drawing.MinX:0.###} × {drawing.MaxY - drawing.MinY:0.###} м",
                $"Слоёв {drawing.Layers.Count}, примитивов {drawing.Entities.Count:N0}, контуров {drawing.Contours.Count:N0}, граней 3DFACE {drawing.FaceCount:N0}"
            };
            ReportImport(Loc.Format("DxfLoaded", drawing.FileName, drawing.Contours.Count), "CadDxfImporterService", details, drawing.Contours.Count, sw.Elapsed.TotalMilliseconds, "AIA-IMPORT-DXF-OK");
            if (liftTo3D)
            {
                IsBusy = false;
                await LiftUnderlayAsync();
            }
            else ZoomExtentsRequested?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or FormatException or ArgumentException or InvalidOperationException)
        {
            App.WriteCrashLog("import-dxf", ex);
            ShowError(Loc.Get("ErrorImport"), ex.Message, $"AIA-IMPORT-DXF-{ex.HResult:X8}", ex.ToString());
        }
        finally
        {
            IsBusy = false;
            BusyText = string.Empty;
        }
    }

    /// <summary>
    /// Окно верификации параметров плоского DXF (задаёт MainWindow): предпросмотр распознанного и параметры по умолчанию →
    /// подтверждённые параметры или null (отмена). Без обработчика подъём идёт с параметрами по умолчанию.
    /// </summary>
    public Func<DxfLiftPreview, DxfLiftOptions, Task<DxfLiftOptions?>>? DxfLiftVerifier { get; set; }

    /// <summary>
    /// Режим А — автоподъём подложки в 3D: «Новое» заменяет сцену, «+ К участку» добавляет элементы на месте подложки.
    /// Плоский чертёж (без 3DFACE) сначала проходит окно верификации высот, толщин и сечений.
    /// </summary>
    [RelayCommand]
    private async Task LiftUnderlayAsync()
    {
        if (Underlay is not { } drawing) return;
        string gid = $"DXF · {Path.GetFileNameWithoutExtension(drawing.FileName)}";
        var options = new DxfLiftOptions(gid, UnderlayBaseElevation, UnderlayLevelHeight, Math.Max(0, Scene.ActiveLevelFilter));
        if (drawing.FaceCount == 0 && DxfLiftVerifier is { } verify)
        {
            var preview = CadDxfImporterService.AnalyzeLift(drawing);
            if (await verify(preview, options) is not { } confirmed)
            {
                ChatMessages.Add(ChatMessageModel.System(Loc.Get("DxfLiftCancelled")));
                return;
            }
            options = confirmed;
        }
        if (!ReferenceEquals(Underlay, drawing)) return;
        var sw = Stopwatch.StartNew();
        var result = CadDxfImporterService.LiftTo3D(drawing, options);
        if (result.Elements.Count == 0)
        {
            ShowNotice(Loc.Get("DxfLiftTitle"), Loc.Get("DxfLiftEmpty"), "AIA-DXF-LIFT-EMPTY");
            return;
        }
        if (IsAppendToSite && Scene.Elements.Count > 0)
        {
            Scene.AddElements(result.Elements);
            if (result.Axes.Count > 0) Scene.SetGroupAxes(gid, result.Axes, recordUndo: false);
        }
        else
        {
            Scene.LoadScene(result.Elements, [new BuildingGroup(gid)], recordUndo: Scene.Elements.Count > 0, axes: result.Axes, dimensions: []);
            Scene.ProjectName = Path.GetFileNameWithoutExtension(drawing.FileName);
            Scene.ProjectFilePath = null;
        }
        sw.Stop();
        ReportImport(Loc.Format("DxfLifted", result.Elements.Count, result.Axes.Count), "CadDxfImporterService · 3D", result.Report, result.Elements.Count, sw.Elapsed.TotalMilliseconds, "AIA-DXF-LIFT-OK");
        ZoomExtentsRequested?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void CloseUnderlay() => Underlay = null;

    [RelayCommand]
    private void ToggleUnderlay() => IsUnderlayVisible = !IsUnderlayVisible;

    /// <summary>Клик по линии подложки во вьюпорте.</summary>
    public void SelectUnderlay(DxfContour? contour, DxfPoint click)
    {
        UnderlayClick = click;
        if (contour is not null) Scene.SelectedElement = null;
        UnderlaySelection = contour;
    }

    /// <summary>Режим Б — кнопки пилюли: стена, колонна, балка, плита, проём, ось по выбранному контуру.</summary>
    [RelayCommand]
    private void UnderlayCreate(string action)
    {
        if (Underlay is not { } drawing || UnderlaySelection is not { } contour) return;
        string gid = Scene.SelectedElement?.BuildingGroupId
                     ?? Scene.BuildingGroups.FirstOrDefault()?.Id
                     ?? $"DXF · {Path.GetFileNameWithoutExtension(drawing.FileName)}";
        int level = Math.Max(0, Scene.ActiveLevelFilter);

        if (action == "axis")
        {
            CreateAxisFromUnderlay(contour, gid);
            return;
        }

        var options = new DxfLiftOptions(gid, UnderlayBaseElevation, UnderlayLevelHeight, level);
        var (elements, problem) = CadDxfImporterService.CreateFromContour(drawing, contour, UnderlayClick, action, options, Scene.Elements.ToList());
        if (problem is not null || elements.Count == 0)
        {
            ShowNotice(Loc.Get("DxfLiftTitle"), problem ?? Loc.Get("DxfLiftEmpty"), "AIA-DXF-PICK");
            return;
        }
        Scene.AddElements(elements);
        UnderlaySelection = null;
        Scene.SelectedElement = elements[0];
    }

    private void CreateAxisFromUnderlay(DxfContour contour, string gid)
    {
        var (_, a, b) = contour.Nearest(UnderlayClick);
        double dx = Math.Abs(b.X - a.X), dy = Math.Abs(b.Y - a.Y);
        if (dx < 1e-6 && dy < 1e-6) return;
        var axes = Scene.AxesOf(gid).ToList();
        if (axes.Count == 0) axes = StructuralAxesBuilder.AutoAxes(Scene.Elements.Where(e => e.BuildingGroupId == gid).ToList(), gid);
        bool numeric = dy >= dx;
        if (Math.Min(dx, dy) > 0.02 * Math.Max(dx, dy))
        {
            ShowNotice(Loc.Get("DxfLiftTitle"), Loc.Get("AxisNotOrthogonal"), "AIA-DXF-AXIS");
            return;
        }
        double coordinate = Math.Round(numeric ? (a.X + b.X) / 2 : -(a.Y + b.Y) / 2, 4);
        int count = axes.Count(x => x.IsNumeric == numeric);
        string label = numeric ? (count + 1).ToString() : StructuralAxesBuilder.Letter(count);
        axes.Add(new StructuralGridAxis(label, numeric ? StructuralGridAxis.AlongZ : StructuralGridAxis.AlongX, coordinate, gid));
        Scene.EnsureGroup(gid);
        Scene.SetGroupAxes(gid, axes);
        UnderlaySelection = null;
        if (IsAxesTab) RefreshAxesEditor();
    }

    // ───────────────────────────── Section Box ─────────────────────────────

    [ObservableProperty]
    public partial bool IsSectionBoxEnabled { get; set; }

    [ObservableProperty] public partial double SectionMinX { get; set; }
    [ObservableProperty] public partial double SectionMaxX { get; set; }
    [ObservableProperty] public partial double SectionMinY { get; set; }
    [ObservableProperty] public partial double SectionMaxY { get; set; }
    [ObservableProperty] public partial double SectionMinZ { get; set; }
    [ObservableProperty] public partial double SectionMaxZ { get; set; }

    /// <summary>Пределы ползунков: габарит сцены с запасом 1 м.</summary>
    [ObservableProperty] public partial double SectionLimitMinX { get; set; } = -10;
    [ObservableProperty] public partial double SectionLimitMaxX { get; set; } = 10;
    [ObservableProperty] public partial double SectionLimitMinY { get; set; } = -1;
    [ObservableProperty] public partial double SectionLimitMaxY { get; set; } = 10;
    [ObservableProperty] public partial double SectionLimitMinZ { get; set; } = -10;
    [ObservableProperty] public partial double SectionLimitMaxZ { get; set; } = 10;

    /// <summary>Последняя сдвинутая грань — плоскость активного разреза (камера и чертёж разреза).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ActiveSectionTitle))]
    public partial SectionFace ActiveSectionFace { get; set; } = SectionFace.MaxZ;

    public string ActiveSectionTitle => Loc.Format("SectionActive", ActiveSectionFace switch
    {
        SectionFace.MinX => "X мин", SectionFace.MaxX => "X макс", SectionFace.MinY => "Y мин",
        SectionFace.MaxY => "Y макс", SectionFace.MinZ => "Z мин", _ => "Z макс"
    });

    /// <summary>Текущая коробка (с защитой от «вывернутых» граней).</summary>
    public SectionBox SectionBox => new(
        Math.Min(SectionMinX, SectionMaxX - 0.05), Math.Max(SectionMaxX, SectionMinX + 0.05),
        Math.Min(SectionMinY, SectionMaxY - 0.05), Math.Max(SectionMaxY, SectionMinY + 0.05),
        Math.Min(SectionMinZ, SectionMaxZ - 0.05), Math.Max(SectionMaxZ, SectionMinZ + 0.05));

    /// <summary>Коробка включена/сдвинута — вьюпорт перестраивает отсечение.</summary>
    public event EventHandler? SectionBoxChanged;

    /// <summary>Запрос ортогональной камеры перпендикулярно активной плоскости разреза.</summary>
    public event EventHandler<SectionFace>? SectionCameraRequested;

    private bool _settingSection;

    partial void OnIsSectionBoxEnabledChanged(bool value)
    {
        if (value) ResetSectionBox();
        SectionBoxChanged?.Invoke(this, EventArgs.Empty);
    }

    partial void OnSectionMinXChanged(double value) => SectionMoved(SectionFace.MinX);
    partial void OnSectionMaxXChanged(double value) => SectionMoved(SectionFace.MaxX);
    partial void OnSectionMinYChanged(double value) => SectionMoved(SectionFace.MinY);
    partial void OnSectionMaxYChanged(double value) => SectionMoved(SectionFace.MaxY);
    partial void OnSectionMinZChanged(double value) => SectionMoved(SectionFace.MinZ);
    partial void OnSectionMaxZChanged(double value) => SectionMoved(SectionFace.MaxZ);

    private void SectionMoved(SectionFace face)
    {
        if (_settingSection) return;
        ActiveSectionFace = face;
        if (IsSectionBoxEnabled) SectionBoxChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Сдвиг грани мышью во вьюпорте.</summary>
    public void SetSectionFace(SectionFace face, double value)
    {
        switch (face)
        {
            case SectionFace.MinX: SectionMinX = Math.Clamp(value, SectionLimitMinX, SectionMaxX - 0.05); break;
            case SectionFace.MaxX: SectionMaxX = Math.Clamp(value, SectionMinX + 0.05, SectionLimitMaxX); break;
            case SectionFace.MinY: SectionMinY = Math.Clamp(value, SectionLimitMinY, SectionMaxY - 0.05); break;
            case SectionFace.MaxY: SectionMaxY = Math.Clamp(value, SectionMinY + 0.05, SectionLimitMaxY); break;
            case SectionFace.MinZ: SectionMinZ = Math.Clamp(value, SectionLimitMinZ, SectionMaxZ - 0.05); break;
            default: SectionMaxZ = Math.Clamp(value, SectionMinZ + 0.05, SectionLimitMaxZ); break;
        }
    }

    /// <summary>Габарит видимой сцены (X, Y, Z) для пределов коробки.</summary>
    private (double MinX, double MaxX, double MinY, double MaxY, double MinZ, double MaxZ) SceneBox()
    {
        var items = Scene.VisibleElements.Where(e => !ElementTypes.IsVoidType(e.Type)).ToList();
        if (items.Count == 0) return (-10, 10, 0, 6, -10, 10);
        var (x0, x1, z0, z1) = SceneStateService.Bounds(items);
        double y0 = items.Min(e => e.BaseY);
        double y1 = items.Max(e => e.RepeatLevels > 1 ? e.TopY + e.LevelHeight * (e.RepeatLevels - 1) : e.TopY);
        return (x0, x1, y0, y1, z0, z1);
    }

    private void SetBox(double x0, double x1, double y0, double y1, double z0, double z1, SectionFace active)
    {
        _settingSection = true;
        try
        {
            SectionMinX = x0; SectionMaxX = x1;
            SectionMinY = y0; SectionMaxY = y1;
            SectionMinZ = z0; SectionMaxZ = z1;
        }
        finally
        {
            _settingSection = false;
        }
        ActiveSectionFace = active;
        if (IsSectionBoxEnabled) SectionBoxChanged?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void ResetSectionBox()
    {
        var b = SceneBox();
        const double margin = 0.5;
        SectionLimitMinX = Math.Floor(b.MinX - 1); SectionLimitMaxX = Math.Ceiling(b.MaxX + 1);
        SectionLimitMinY = Math.Floor(b.MinY - 1); SectionLimitMaxY = Math.Ceiling(b.MaxY + 1);
        SectionLimitMinZ = Math.Floor(b.MinZ - 1); SectionLimitMaxZ = Math.Ceiling(b.MaxZ + 1);
        SetBox(b.MinX - margin, b.MaxX + margin, b.MinY - margin, b.MaxY + margin, b.MinZ - margin, b.MaxZ + margin, ActiveSectionFace);
    }

    /// <summary>Продольный разрез 1-1: плоскость вдоль длинной стороны здания через его середину.</summary>
    [RelayCommand]
    private void SectionLongitudinal() => CutAlong(longitudinal: true);

    /// <summary>Поперечный разрез 2-2: плоскость поперёк здания через его середину.</summary>
    [RelayCommand]
    private void SectionTransverse() => CutAlong(longitudinal: false);

    private void CutAlong(bool longitudinal)
    {
        if (!IsSectionBoxEnabled) IsSectionBoxEnabled = true;
        ResetSectionBox();
        var b = SceneBox();
        bool longX = b.MaxX - b.MinX >= b.MaxZ - b.MinZ;
        // Продольная плоскость параллельна длинной стороне: при длинной оси X — плоскость z = const.
        if (longitudinal == longX) SetBox(SectionMinX, SectionMaxX, SectionMinY, SectionMaxY, SectionMinZ, Math.Round((b.MinZ + b.MaxZ) / 2, 2), SectionFace.MaxZ);
        else SetBox(SectionMinX, Math.Round((b.MinX + b.MaxX) / 2, 2), SectionMinY, SectionMaxY, SectionMinZ, SectionMaxZ, SectionFace.MaxX);
    }

    /// <summary>Горизонтальный срез по этажу: на 1.2 м выше отметки активного этажа (или первого).</summary>
    [RelayCommand]
    private void SectionFloorCut()
    {
        if (!IsSectionBoxEnabled) IsSectionBoxEnabled = true;
        ResetSectionBox();
        var levels = StructuralAxesBuilder.LevelElevations(Scene.VisibleElements.ToList());
        double elevation = levels.FirstOrDefault(l => l.Level == Math.Max(0, Scene.ActiveLevelFilter))?.Elevation ?? 0;
        SetBox(SectionMinX, SectionMaxX, SectionMinY, Math.Round(elevation + 1.2, 2), SectionMinZ, SectionMaxZ, SectionFace.MaxY);
    }

    [RelayCommand]
    private void SectionCamera()
    {
        if (!IsSectionBoxEnabled) IsSectionBoxEnabled = true;
        SectionCameraRequested?.Invoke(this, ActiveSectionFace);
    }

    [RelayCommand]
    private void ToggleSectionBox() => IsSectionBoxEnabled = !IsSectionBoxEnabled;

    // ───────────────────────────── Рабочие чертежи ГОСТ ─────────────────────────────

    public static bool TryParseGost(string? tag, out GostSheetFormat format, out GostDrawingView view)
    {
        format = GostSheetFormat.A3;
        view = GostDrawingView.FloorPlan;
        var parts = (tag ?? string.Empty).Split('|');
        if (parts.Length != 2) return false;
        format = parts[0] == "A2" ? GostSheetFormat.A2 : GostSheetFormat.A3;
        view = parts[1] switch { "frame" => GostDrawingView.FrameScheme, "section" => GostDrawingView.Section, _ => GostDrawingView.FloorPlan };
        return true;
    }

    /// <summary>Выпуск листа ГОСТ: постройка выбранного элемента (или самая крупная), этаж фильтра, разрез — по Section Box.</summary>
    public async Task ExportGostAsync(GostSheetFormat format, GostDrawingView view, string path)
    {
        if (Scene.Elements.Count == 0)
        {
            ShowError(Loc.Get("ErrorExport"), Loc.Get("ExportEmpty"), "AIA-EXPORT-GOST-EMPTY");
            return;
        }
        IsBusy = true;
        BusyText = Loc.Format("ExportBusy", "PDF");
        try
        {
            var sw = Stopwatch.StartNew();
            var request = GostRequest(format, view);
            var result = await GostExporter.ExportAsync(request, path);
            sw.Stop();
            var report = new SolverReport
            {
                Success = true,
                ElapsedMilliseconds = sw.Elapsed.TotalMilliseconds,
                InputElements = Scene.Elements.Count,
                GeneratedElements = result.ElementCount,
                Actions = [.. result.Details, $"{Path.GetFileName(path)} — {new FileInfo(path).Length / 1024.0:N0} КБ"]
            };
            ChatMessages.Add(ChatMessageModel.Assistant(Loc.Format("GostDone", result.SheetTitle, result.ScaleDenominator, Path.GetFileName(path)), "GostDrawingPdfExporter", report));
            _noticeVersion++;
            ErrorSeverity = InfoBarSeverity.Success;
            ErrorTitle = Loc.Format("ExportDoneTitle", "PDF");
            ErrorMessage = $"{result.SheetTitle} · М 1:{result.ScaleDenominator} · {path}";
            ErrorCode = "AIA-EXPORT-GOST-OK";
            ErrorDetails = string.Join("\n", result.Details);
            ErrorActionLabel = Loc.Get("SolverLog");
            UpdateErrorReport();
            IsErrorOpen = true;
            AutoCloseNotice();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            App.WriteCrashLog("export-gost", ex);
            ShowError(Loc.Get("ErrorExport"), ex.Message, $"AIA-EXPORT-GOST-{ex.HResult:X8}", ex.ToString());
        }
        finally
        {
            IsBusy = false;
            BusyText = string.Empty;
        }
    }

    /// <summary>Снимок сцены для выпуска чертежей: постройка выбранного элемента, этаж фильтра, разрез — по Section Box.</summary>
    private GostDrawingRequest GostRequest(GostSheetFormat format, GostDrawingView view) => new(format, view,
        Scene.Elements.Select(e => e.Clone()).ToList(),
        Scene.BuildingGroups.Select(g => g.Clone()).ToList(),
        Scene.GridAxes.Select(a => a.Clone()).ToList(),
        Scene.ProjectName,
        Scene.SelectedElement?.BuildingGroupId,
        Scene.ActiveLevelFilter,
        IsSectionBoxEnabled ? SectionBox : null,
        IsSectionBoxEnabled ? ActiveSectionFace : null,
        Loads: Settings.Current.StructuralLoads.Clone(),
        Dimensions: Scene.PinnedDimensions.Select(d => d.Clone()).ToList());

    /// <summary>
    /// «Выпуск альбома чертежей (3 листа PDF)»: лист 1 — план этажа фильтра с осями и размерами (1:100), лист 2 — разрез по
    /// Section Box с отметками, лист 3 — спецификация этажа и ведомость расхода стали; шрифт — подмножество глифов.
    /// </summary>
    public async Task ExportGostAlbumAsync(string path)
    {
        if (Scene.Elements.Count == 0)
        {
            ShowError(Loc.Get("ErrorExport"), Loc.Get("ExportEmpty"), "AIA-EXPORT-ALBUM-EMPTY");
            return;
        }
        IsBusy = true;
        BusyText = Loc.Format("ExportBusy", "PDF");
        try
        {
            var sw = Stopwatch.StartNew();
            var result = await GostExporter.ExportAlbumAsync(GostRequest(GostSheetFormat.A3, GostDrawingView.FloorPlan), path);
            sw.Stop();
            var report = new SolverReport
            {
                Success = true,
                ElapsedMilliseconds = sw.Elapsed.TotalMilliseconds,
                InputElements = Scene.Elements.Count,
                GeneratedElements = result.Sheets.Count,
                Actions = [.. result.Details, $"{Path.GetFileName(path)} — {result.Pdf.Bytes / 1024.0:N0} КБ"]
            };
            string sheets = string.Join("; ", result.Sheets.Select(s => s.ScaleDenominator > 0
                ? $"{s.Number}. {s.Title} ({GostDrawingPdfExporter.FormatTitle(s.Format)}, М 1:{s.ScaleDenominator})"
                : $"{s.Number}. {s.Title} ({GostDrawingPdfExporter.FormatTitle(s.Format)})"));
            ChatMessages.Add(ChatMessageModel.Assistant(Loc.Format("GostAlbumDone", result.Sheets.Count, Path.GetFileName(path), sheets), "GostDrawingPdfExporter", report));
            _noticeVersion++;
            ErrorSeverity = InfoBarSeverity.Success;
            ErrorTitle = Loc.Format("ExportDoneTitle", "PDF");
            ErrorMessage = Loc.Format("GostAlbumNotice", result.Sheets.Count, result.Pdf.Bytes / 1024.0, result.Pdf.Glyphs, path);
            ErrorCode = "AIA-EXPORT-ALBUM-OK";
            ErrorDetails = string.Join("\n", result.Details);
            ErrorActionLabel = Loc.Get("SolverLog");
            UpdateErrorReport();
            IsErrorOpen = true;
            AutoCloseNotice();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException or InvalidDataException)
        {
            App.WriteCrashLog("export-album", ex);
            ShowError(Loc.Get("ErrorExport"), ex.Message, $"AIA-EXPORT-ALBUM-{ex.HResult:X8}", ex.ToString());
        }
        finally
        {
            IsBusy = false;
            BusyText = string.Empty;
        }
    }
}
