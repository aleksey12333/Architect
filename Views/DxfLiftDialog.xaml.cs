using System.Globalization;
using AIArchitectStudio.Services;
using Microsoft.UI.Xaml.Controls;

namespace AIArchitectStudio.Views;

/// <summary>
/// Окно верификации параметров перед автоподъёмом плоского DXF в 3D (аналог правой панели диалога PDF): слева — что распознано
/// в чертеже (стены, колонны с типичным сечением, балки, плиты, проёмы, оси), справа — параметры, которых в плоском чертеже
/// нет: отметка и высота этажа, высота стен, толщины плит, фундамента и покрытия, сечения колонн и балок (по чертежу или
/// заданные), окна и двери. Значения в мм (отметка — в м); ошибки диапазонов блокируют кнопку «Построить 3D».
/// </summary>
public sealed partial class DxfLiftDialog : ContentDialog
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private readonly LocalizationService _loc;
    private readonly DxfLiftPreview _preview;
    private readonly DxfLiftOptions _defaults;
    private bool _filling = true;

    /// <summary>Подтверждённые параметры (null — в полях есть ошибки).</summary>
    public DxfLiftOptions? Options { get; private set; }

    public DxfLiftDialog(DxfLiftPreview preview, DxfLiftOptions defaults, LocalizationService loc)
    {
        _loc = loc;
        _preview = preview;
        _defaults = defaults;
        InitializeComponent();
        Title = T("Верификация параметров DXF перед 3D", "Verify DXF parameters before 3D");
        PrimaryButtonText = T("Построить 3D", "Build 3D");
        CloseButtonText = T("Отмена", "Cancel");
        HeaderText.Text = preview.FileName;
        SubtitleText.Text = T($"Плоский чертёж без граней 3DFACE: высоты и сечения не заданы — проверьте значения. Габарит {preview.WidthM:0.###} × {preview.DepthM:0.###} м",
            $"Flat drawing without 3DFACE: heights and sections are missing — check the values. Extents {preview.WidthM:0.###} × {preview.DepthM:0.###} m");

        FactsTitle.Text = T("Распознано в чертеже", "Recognized in drawing");
        FactsText.Text = Facts(preview);
        FlatHint.Text = T("Колонны и балки «по чертежу» берут сечение из контура; заданное сечение заменяет его у всех элементов.",
            "Columns and beams “from drawing” take the section from the contour; a set section replaces it for all elements.");

        BaseLabel.Text = T("Отметка этажа, м", "Level elevation, m");
        LevelLabel.Text = T("Высота этажа, мм", "Storey height, mm");
        WallLabel.Text = T("Высота стен, мм", "Wall height, mm");
        SlabLabel.Text = T("Плита перекрытия, мм", "Floor slab, mm");
        FoundationLabel.Text = T("Фундамент, мм", "Foundation, mm");
        RoofLabel.Text = T("Покрытие, мм", "Roof slab, mm");
        ColumnLabel.Text = T($"Колонны ({preview.Columns}): сечение W × D, мм", $"Columns ({preview.Columns}): section W × D, mm");
        BeamLabel.Text = T($"Балки ({preview.Beams}): B × H, мм", $"Beams ({preview.Beams}): B × H, mm");
        WindowLabel.Text = T("Окна: высота, мм", "Windows: height, mm");
        SillLabel.Text = T("Подоконник, мм", "Sill, mm");
        DoorLabel.Text = T("Двери: высота, мм", "Doors: height, mm");

        ColumnModeBox.Items.Add(T("По чертежу", "From drawing"));
        ColumnModeBox.Items.Add(T("Задать сечение", "Set section"));
        BeamModeBox.Items.Add(T("Ширина по чертежу", "Width from drawing"));
        BeamModeBox.Items.Add(T("Задать сечение", "Set section"));

        BaseBox.Text = defaults.BaseElevation.ToString("0.000", Inv);
        LevelBox.Text = Mm(defaults.LevelHeight);
        WallBox.Text = Mm(defaults.EffectiveWallHeight);
        SlabBox.Text = Mm(defaults.SlabThickness);
        FoundationBox.Text = Mm(defaults.FoundationThickness);
        RoofBox.Text = Mm(defaults.RoofThickness);
        var column = preview.ColumnSection;
        ColumnWBox.Text = Mm(defaults.ColumnWidth > 0 ? defaults.ColumnWidth : column?.W ?? 0.4);
        ColumnDBox.Text = Mm(defaults.ColumnDepth > 0 ? defaults.ColumnDepth : column?.D ?? 0.4);
        ColumnModeBox.SelectedIndex = defaults.ColumnWidth > 0 || preview.Columns == 0 && column is null ? 1 : 0;
        BeamWBox.Text = Mm(defaults.BeamWidth > 0 ? defaults.BeamWidth : preview.BeamWidth ?? 0.3);
        BeamHBox.Text = Mm(defaults.BeamHeight);
        BeamModeBox.SelectedIndex = defaults.BeamWidth > 0 ? 1 : 0;
        WindowBox.Text = Mm(defaults.WindowHeight);
        SillBox.Text = Mm(defaults.WindowSill);
        DoorBox.Text = Mm(defaults.DoorHeight);
        _filling = false;
        Validate();
    }

    private string T(string ru, string en) => _loc.IsRussian ? ru : en;

    private static string Mm(double meters) => Math.Round(meters * 1000).ToString("0", Inv);

    private string Facts(DxfLiftPreview p)
    {
        string column = p.ColumnSection is { } c
            ? c.Circle ? $"Ø{Mm(c.W)}" : $"{Mm(c.W)}×{Mm(c.D)}"
            : "—";
        var lines = new List<string>
        {
            T($"Стены: {p.Walls} контуров, {p.PairedWalls} из пар линий", $"Walls: {p.Walls} contours, {p.PairedWalls} from line pairs"),
            T($"Толщина стен: {(p.WallThickness is { } t ? Mm(t) + " мм" : "по контурам")}", $"Wall thickness: {(p.WallThickness is { } t2 ? Mm(t2) + " mm" : "by contours")}"),
            T($"Колонны: {p.Columns}, типичное {column}", $"Columns: {p.Columns}, typical {column}"),
            T($"Балки: {p.Beams}{(p.BeamWidth is { } b ? $", B = {Mm(b)} мм" : string.Empty)}", $"Beams: {p.Beams}{(p.BeamWidth is { } b2 ? $", B = {Mm(b2)} mm" : string.Empty)}"),
            T($"Плиты: {p.Slabs}, фундамент: {p.Foundations}, кровля: {p.Roofs}", $"Slabs: {p.Slabs}, foundation: {p.Foundations}, roof: {p.Roofs}"),
            T($"Окна: {p.Windows}, двери: {p.Doors}, проёмы: {p.Openings}", $"Windows: {p.Windows}, doors: {p.Doors}, openings: {p.Openings}"),
            T($"Координационные оси: {p.Axes}", $"Grid axes: {p.Axes}")
        };
        return string.Join("\n", lines);
    }

    private void OnFieldChanged(object sender, TextChangedEventArgs e)
    {
        if (!_filling) Validate();
    }

    private void OnModeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_filling) return;
        Validate();
    }

    /// <summary>Разбор полей с проверкой диапазонов; при ошибках — подсказка внизу и неактивная кнопка «Построить 3D».</summary>
    private void Validate()
    {
        var errors = new List<string>();
        double Read(TextBox box, string name, double min, double max, double scale = 0.001)
        {
            string text = box.Text.Trim().Replace(',', '.');
            if (!double.TryParse(text, NumberStyles.Float, Inv, out double v) || !double.IsFinite(v) || v < min || v > max)
            {
                errors.Add(T($"{name}: {min.ToString("0.###", Inv)}…{max.ToString("0.###", Inv)}", $"{name}: {min.ToString("0.###", Inv)}…{max.ToString("0.###", Inv)}"));
                return double.NaN;
            }
            return v * scale;
        }

        bool customColumn = ColumnModeBox.SelectedIndex == 1, customBeam = BeamModeBox.SelectedIndex == 1;
        ColumnWBox.IsEnabled = customColumn;
        ColumnDBox.IsEnabled = customColumn && _preview.ColumnSection is not { Circle: true };
        BeamWBox.IsEnabled = customBeam;

        double baseY = Read(BaseBox, T("Отметка, м", "Elevation, m"), -50, 500, 1);
        double level = Read(LevelBox, T("Высота этажа, мм", "Storey height, mm"), 1800, 12000);
        double wall = Read(WallBox, T("Высота стен, мм", "Wall height, mm"), 300, 12000);
        double slab = Read(SlabBox, T("Плита, мм", "Slab, mm"), 40, 1200);
        double foundation = Read(FoundationBox, T("Фундамент, мм", "Foundation, mm"), 100, 4000);
        double roof = Read(RoofBox, T("Покрытие, мм", "Roof, mm"), 40, 1500);
        double colW = customColumn ? Read(ColumnWBox, T("Колонна W, мм", "Column W, mm"), 100, 3000) : 0;
        double colD = customColumn && ColumnDBox.IsEnabled ? Read(ColumnDBox, T("Колонна D, мм", "Column D, mm"), 100, 3000) : colW;
        double beamW = customBeam ? Read(BeamWBox, T("Балка B, мм", "Beam B, mm"), 80, 2000) : 0;
        double beamH = Read(BeamHBox, T("Балка H, мм", "Beam H, mm"), 100, 3000);
        double window = Read(WindowBox, T("Окно, мм", "Window, mm"), 300, 6000);
        double sill = Read(SillBox, T("Подоконник, мм", "Sill, mm"), 0, 3000);
        double door = Read(DoorBox, T("Дверь, мм", "Door, mm"), 1500, 6000);
        if (errors.Count == 0)
        {
            if (wall > level + 1e-9) errors.Add(T("Высота стен больше высоты этажа", "Wall height exceeds the storey height"));
            if (sill + window > wall + 1e-9) errors.Add(T("Окно с подоконником выше стены", "Window with sill is taller than the wall"));
            if (door > wall + 1e-9) errors.Add(T("Дверь выше стены", "Door is taller than the wall"));
            if (beamH > level - 0.5) errors.Add(T("Балка выше этажа без 0.5 м", "Beam is too deep for the storey"));
        }

        if (errors.Count > 0)
        {
            Options = null;
            StatusGlyph.Glyph = "";
            StatusText.Text = T("Исправьте: ", "Fix: ") + string.Join("; ", errors);
            IsPrimaryButtonEnabled = false;
            return;
        }
        Options = _defaults with
        {
            BaseElevation = baseY,
            LevelHeight = level,
            WallHeight = wall,
            SlabThickness = slab,
            FoundationThickness = foundation,
            RoofThickness = roof,
            ColumnWidth = colW,
            ColumnDepth = colD,
            BeamWidth = beamW,
            BeamHeight = beamH,
            WindowHeight = window,
            WindowSill = sill,
            DoorHeight = door
        };
        StatusGlyph.Glyph = "";
        StatusText.Text = T($"Готово: этаж {Mm(level)} мм на отметке {GostDrawingPdfExporter.Elevation(baseY)}, стены {Mm(wall)} мм, плиты {Mm(slab)} мм, " +
                            $"колонны {(customColumn ? $"{Mm(colW)}×{Mm(colD)} мм" : "по чертежу")}, балки {(customBeam ? Mm(beamW) : "B по чертежу")}×{Mm(beamH)} мм",
            $"Ready: storey {Mm(level)} mm at {GostDrawingPdfExporter.Elevation(baseY)}, walls {Mm(wall)} mm, slabs {Mm(slab)} mm, " +
            $"columns {(customColumn ? $"{Mm(colW)}×{Mm(colD)} mm" : "from drawing")}, beams {(customBeam ? Mm(beamW) : "B from drawing")}×{Mm(beamH)} mm");
        IsPrimaryButtonEnabled = true;
    }
}
