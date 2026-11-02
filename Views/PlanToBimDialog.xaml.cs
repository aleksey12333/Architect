using System.Globalization;
using System.Runtime.InteropServices.WindowsRuntime;
using AIArchitectStudio.Models;
using AIArchitectStudio.Rendering;
using AIArchitectStudio.Services;
using AIArchitectStudio.ViewModels;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using Windows.UI;

namespace AIArchitectStudio.Views;

/// <summary>
/// Проверка распознанного поэтажного плана перед вставкой: изометрия собранного каркаса (стены, колонны, плиты, фундамент,
/// проёмы) и нормализованный план (оси с марками, стены, проёмы, колонны, контур фундамента), сводка распознанного,
/// масштаб, постобработка и конструктивная целостность. Этажность, высота этажа и калибровочный размер пересобирают здание.
/// </summary>
public sealed partial class PlanToBimDialog : ContentDialog
{
    private readonly MainViewModel _vm;
    private readonly LocalizationService _loc;
    private readonly string? _path;
    private RecognizedPlan _plan;

    public PlanToBimResult? Result { get; private set; }

    public PlanToBimDialog(MainViewModel viewModel, RecognizedPlan plan, string? path, int storeys = 2)
    {
        _vm = viewModel;
        _loc = viewModel.Loc;
        _plan = plan;
        _path = path;
        InitializeComponent();
        Title = null;
        TitleText.Text = _loc.Get("PlanDialogTitle");
        PrimaryButtonText = _loc.Get("PlanInsert");
        CloseButtonText = _loc.Get("Close");
        View3DRadio.Content = _loc.Get("PlanView3D");
        // Отметка — после InitializeComponent: Checked из разметки сработал бы до создания холста предпросмотра.
        View3DRadio.IsChecked = true;
        ViewPlanRadio.Content = _loc.Get("PlanViewPlan");
        RecognizedTitle.Text = _loc.Get("PlanRecognized");
        ScaleTitle.Text = _loc.Get("PlanScale");
        NormalizationTitle.Text = _loc.Get("PlanNormalization");
        IntegrityTitle.Text = _loc.Get("PlanIntegrity");
        StoreysLabel.Text = _loc.Get("PlanStoreys");
        FloorHeightLabel.Text = _loc.Get("PlanFloorHeight");
        CalibrationLabel.Text = _loc.Get("PlanCalibration");
        CalibrationHint.Text = _loc.Get("PlanCalibrationHint");
        RebuildText.Text = _loc.Get("PlanRebuild");
        StoreysBox.Value = storeys;
        bool raster = plan.Source is PlanSourceKind.Raster or PlanSourceKind.Pdf;
        CalibrationBox.IsEnabled = raster && path is not null;
        if (plan.Image is { Length: > 0 } image) _ = ShowSourceAsync(image);
        else SourceInset.Visibility = Visibility.Collapsed;
        Opened += (_, _) => FocusSink.Focus(FocusState.Programmatic);
        PreviewCanvas.SizeChanged += (_, _) => RenderPreview();
        Rebuild();
    }

    private CultureInfo Culture => _loc.Culture;
    private bool Light => ActualTheme == ElementTheme.Light;

    private PlanToBimOptions Options => new()
    {
        Storeys = (int)Math.Clamp(double.IsNaN(StoreysBox.Value) ? 1 : StoreysBox.Value, 1, 40),
        FloorHeight = Math.Clamp(double.IsNaN(FloorHeightBox.Value) ? 3000 : FloorHeightBox.Value, 2400, 6000) / 1000,
        GroupId = System.IO.Path.GetFileNameWithoutExtension(_plan.Name) is { Length: > 0 } n ? n : "Здание по плану"
    };

    private void Rebuild()
    {
        Result = _vm.BuildFromPlan(_plan, Options);
        IsPrimaryButtonEnabled = Result.Elements.Count > 0;
        RenderSummary(Result);
        RenderPreview();
    }

    private async void OnRebuildClick(object sender, RoutedEventArgs e)
    {
        double calibration = double.IsNaN(CalibrationBox.Value) ? 0 : CalibrationBox.Value;
        if (CalibrationBox.IsEnabled && _path is not null)
            try
            {
                _plan = await RasterPlanTracer.LoadAsync(_path, calibration);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException or ArgumentException)
            {
                App.WriteCrashLog("plan-retrace", ex);
            }
        Rebuild();
    }

    private void OnPreviewModeChecked(object sender, RoutedEventArgs e) => RenderPreview();

    private async Task ShowSourceAsync(byte[] bytes)
    {
        var bitmap = new BitmapImage();
        using var stream = new Windows.Storage.Streams.InMemoryRandomAccessStream();
        await stream.WriteAsync(bytes.AsBuffer());
        stream.Seek(0);
        await bitmap.SetSourceAsync(stream);
        SourceImage.Source = bitmap;
    }

    // ───────────────────────────── Сводка ─────────────────────────────

    private void RenderSummary(PlanToBimResult r)
    {
        SubtitleText.Text = $"{_plan.Name} · {_loc.Get("PlanSource")}: {SourceTitle(_plan.Source)}";
        SizeText.Text = _loc.Format("PlanSize", r.WidthM.ToString("0.00", Culture), r.DepthM.ToString("0.00", Culture), r.FootprintArea.ToString("0.0", Culture));
        CountsPanel.Children.Clear();
        int axesX = r.Axes.Count(a => a.IsNumeric), axesY = r.Axes.Count - axesX;
        AddCount("", _loc.Get("PlanAxes"), $"{r.Axes.Count}  ({axesX} + {axesY})");
        AddCount("", _loc.Get("PlanExtWalls"), r.ExteriorWalls.ToString(Culture));
        AddCount("", _loc.Get("PlanIntWalls"), r.InteriorWalls.ToString(Culture));
        AddCount("", _loc.Get("PlanOpenings"), $"{r.Windows} / {r.Doors}");
        AddCount("", _loc.Get("PlanColumns"), r.Columns.ToString(Culture));
        AddCount("", _loc.Get("PlanSlabs"), r.Slabs.ToString(Culture));
        AddCount("", _loc.Get("PlanFoundation"), r.Elements.Any(e => e.Category == BimCategory.Foundation) ? "1" : "—");

        ScaleText.Text = _plan.MmPerPixel > 0
            ? _loc.Format("PlanScaleRaster", _plan.MmPerPixel.ToString("0.0", Culture), _plan.ScaleSource)
            : _loc.Format("PlanScaleVector", _plan.ScaleSource.Length > 0 ? _plan.ScaleSource : "м");
        var s = r.Normalization;
        NormalizationText.Text = _loc.Format("PlanNormDetail", s.Orthogonalized, s.MaxCorrectionDeg.ToString("0.0", Culture), s.CornersClosed,
            s.MaxGapClosedMm.ToString("0", Culture), s.DuplicatesMerged, s.OpeningsBridged) + (r.IsClosedContour ? string.Empty : ". " + _loc.Get("PlanOpenContour"));
        IntegrityText.Text = r.Elements.Count == 0 ? _loc.Get("PlanNoWalls")
            : _loc.Format("PlanIntegrityDetail", r.MaxUtilization.ToString("0.00", Culture), r.GoverningElement, r.PhysicsErrors,
                (r.RebarKg / 1000).ToString("0.00", Culture), r.RebarCages, r.RebarCandidates);
        SizingText.Text = _loc.Format("PlanSizing", r.SizingPasses, (r.SlabThickness * 1000).ToString("0", Culture), (r.ColumnSize * 1000).ToString("0", Culture),
            (r.FoundationThickness * 1000).ToString("0", Culture));

        var color = r.IsStructurallySound ? Color.FromArgb(255, 0x30, 0xD1, 0x58) : Color.FromArgb(255, 0xFF, 0x9F, 0x0A);
        VerdictPill.Background = new SolidColorBrush(Color.FromArgb(0x26, color.R, color.G, color.B));
        VerdictText.Text = _loc.Get(r.IsStructurallySound ? "PlanSound" : "PlanNotSound");
        VerdictText.Foreground = new SolidColorBrush(color);
        VerdictDetail.Text = $"K_исп max · {r.Storeys} эт.";
        KValueText.Text = r.MaxUtilization.ToString("0.00", Culture);
        KValueText.Foreground = new SolidColorBrush(StressPalette.ToUi(StressPalette.ColorFor(r.MaxUtilization)));
    }

    private string SourceTitle(PlanSourceKind kind) => kind switch
    {
        PlanSourceKind.Raster => "PNG/JPG",
        PlanSourceKind.Pdf => "PDF",
        PlanSourceKind.Dxf => "DXF",
        _ => _loc.IsRussian ? "векторные отрезки" : "vector segments"
    };

    private void AddCount(string glyph, string label, string value)
    {
        var grid = new Grid { ColumnSpacing = 8 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(new FontIcon { Glyph = glyph, FontSize = 12, Foreground = (Brush)Application.Current.Resources["AccentBrush"], VerticalAlignment = VerticalAlignment.Center });
        var text = new TextBlock { Text = label, FontSize = 12, Foreground = (Brush)Application.Current.Resources["TextPrimaryBrush"], VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);
        var v = new TextBlock { Text = value, FontSize = 12, FontWeight = FontWeights.SemiBold, FontFamily = (FontFamily)Application.Current.Resources["MonoFont"],
            Foreground = (Brush)Application.Current.Resources["TextPrimaryBrush"], VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(v, 2);
        grid.Children.Add(v);
        CountsPanel.Children.Add(grid);
    }

    // ───────────────────────────── Предпросмотр ─────────────────────────────

    private void RenderPreview()
    {
        PreviewCanvas.Children.Clear();
        if (Result is not { Elements.Count: > 0 } r) return;
        double w = PreviewCanvas.ActualWidth, h = PreviewCanvas.ActualHeight;
        if (w < 10 || h < 10) return;
        if (ViewPlanRadio.IsChecked == true) RenderPlan(r, w, h);
        else RenderIsometric(r, w, h);
    }

    /// <summary>Изометрия: призмы элементов (контур плана × отметки), видимые грани — по нормали к камере, порядок — снизу вверх и от дальних.</summary>
    private void RenderIsometric(PlanToBimResult r, double width, double height)
    {
        const double cos30 = 0.8660254, sin30 = 0.5;
        Point P(double x, double y, double z) => new((x - z) * cos30, -y + (x + z) * sin30);
        var prisms = new List<(ArchitecturalElement E, List<(double X, double Z)> Ring)>();
        foreach (var e in r.Elements)
        {
            if (e.Category is BimCategory.WallOpening or BimCategory.SlabOpening) continue;
            prisms.Add((e, Ring(e)));
        }
        var all = prisms.SelectMany(p => p.Ring.SelectMany(q => new[] { P(q.X, p.E.BaseY, q.Z), P(q.X, p.E.TopY, q.Z) })).ToList();
        var (fit, ox, oy) = Fit(all, width, height, 18);
        Point S(double x, double y, double z)
        {
            var p = P(x, y, z);
            return new Point(p.X * fit + ox, p.Y * fit + oy);
        }

        int Order(ArchitecturalElement e) => e.Category switch
        {
            BimCategory.Foundation => 0,
            BimCategory.Slab or BimCategory.Roof => 3,
            BimCategory.Window or BimCategory.Door => 2,
            _ => 1
        };
        foreach (var (e, ring) in prisms.OrderBy(p => Math.Round(p.E.BaseY, 2)).ThenBy(p => Order(p.E)).ThenBy(p => p.E.X + p.E.Z))
        {
            var (fill, alpha, stroke) = Palette(e);
            double area = Geometry2D.SignedArea(ring);
            for (int i = 0; i < ring.Count; i++)
            {
                var a = ring[i];
                var b = ring[(i + 1) % ring.Count];
                // Наружная нормаль ребра в плане XZ; камера — со стороны +X, +Z.
                double nx = (b.Z - a.Z) * Math.Sign(area), nz = -(b.X - a.X) * Math.Sign(area);
                if (nx + nz <= 1e-9) continue;
                double shade = nx > nz ? 0.78 : 0.62;
                AddPolygon([S(a.X, e.BaseY, a.Z), S(b.X, e.BaseY, b.Z), S(b.X, e.TopY, b.Z), S(a.X, e.TopY, a.Z)], Shade(fill, shade), alpha, stroke);
            }
            AddPolygon(ring.Select(q => S(q.X, e.TopY, q.Z)).ToList(), fill, alpha, stroke);
        }
    }

    private void RenderPlan(PlanToBimResult r, double width, double height)
    {
        var walls = r.Walls;
        var pts = walls.SelectMany(w => new[] { new Point(w.X1, -w.Y1), new Point(w.X2, -w.Y2) }).Concat(r.Footprint.Select(p => new Point(p.X, -p.Y))).ToList();
        var (fit, ox, oy) = Fit(pts, width, height, 40);
        Point S(double x, double y) => new(x * fit + ox, -y * fit + oy);
        var accent = (Application.Current.Resources["AccentBrush"] as SolidColorBrush)?.Color ?? Color.FromArgb(255, 0x0A, 0x84, 0xFF);
        var ink = Light ? Color.FromArgb(255, 0x1C, 0x1C, 0x1E) : Color.FromArgb(255, 0xE5, 0xE5, 0xEA);

        // Контур фундамента.
        if (r.Footprint.Count >= 3)
        {
            var outline = new Polygon { Stroke = new SolidColorBrush(Color.FromArgb(0xB0, accent.R, accent.G, accent.B)), StrokeThickness = 1.2, StrokeDashArray = [4, 3],
                Fill = new SolidColorBrush(Color.FromArgb(0x14, accent.R, accent.G, accent.B)) };
            foreach (var p in r.Footprint) outline.Points.Add(S(p.X, p.Y));
            PreviewCanvas.Children.Add(outline);
        }
        // Координационные оси (центр сцены — центр габарита стен).
        double minX = walls.Min(w => Math.Min(w.X1, w.X2)), maxX = walls.Max(w => Math.Max(w.X1, w.X2));
        double minY = walls.Min(w => Math.Min(w.Y1, w.Y2)), maxY = walls.Max(w => Math.Max(w.Y1, w.Y2));
        double cx = (r.Footprint.Count > 0 ? (r.Footprint.Min(p => p.X) + r.Footprint.Max(p => p.X)) / 2 : (minX + maxX) / 2);
        double cy = (r.Footprint.Count > 0 ? (r.Footprint.Min(p => p.Y) + r.Footprint.Max(p => p.Y)) / 2 : (minY + maxY) / 2);
        foreach (var axis in r.Axes)
        {
            var dash = new Line { Stroke = new SolidColorBrush(Color.FromArgb(0x90, accent.R, accent.G, accent.B)), StrokeThickness = 0.8, StrokeDashArray = [8, 3, 1, 3] };
            Point a, b;
            if (axis.IsNumeric)
            {
                double x = axis.Coordinate + cx;
                (a, b) = (S(x, maxY + 1.2), S(x, minY - 0.6));
            }
            else
            {
                double y = -axis.Coordinate + cy;
                (a, b) = (S(minX - 1.2, y), S(maxX + 0.6, y));
            }
            (dash.X1, dash.Y1, dash.X2, dash.Y2) = (a.X, a.Y, b.X, b.Y);
            PreviewCanvas.Children.Add(dash);
            var mark = new Border { Width = 18, Height = 18, CornerRadius = new CornerRadius(9), BorderThickness = new Thickness(1), BorderBrush = new SolidColorBrush(accent),
                Background = (Brush)Application.Current.Resources["IslandBrush"],
                Child = new TextBlock { Text = axis.Label, FontSize = 9, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                    Foreground = new SolidColorBrush(accent) } };
            Canvas.SetLeft(mark, a.X - 9);
            Canvas.SetTop(mark, a.Y - 9);
            PreviewCanvas.Children.Add(mark);
        }
        // Стены (тела по толщине): наружные — темнее.
        foreach (var w in walls)
        {
            double len = Math.Max(1e-6, w.Length), nx = -(w.Y2 - w.Y1) / len * w.Thickness / 2, ny = (w.X2 - w.X1) / len * w.Thickness / 2;
            var poly = new Polygon { Fill = new SolidColorBrush(Color.FromArgb(w.IsExterior ? (byte)0xE6 : (byte)0x9A, ink.R, ink.G, ink.B)) };
            foreach (var p in new[] { (w.X1 + nx, w.Y1 + ny), (w.X2 + nx, w.Y2 + ny), (w.X2 - nx, w.Y2 - ny), (w.X1 - nx, w.Y1 - ny) }) poly.Points.Add(S(p.Item1, p.Item2));
            PreviewCanvas.Children.Add(poly);
        }
        // Проёмы и колонны первого этажа (из собранных элементов: сцена → лист).
        foreach (var e in r.Elements.Where(e => e.LevelIndex == 0 && e.Category is BimCategory.Window or BimCategory.Door or BimCategory.Column))
        {
            var ring = Ring(e).Select(q => S(q.X + cx, -q.Z + cy)).ToList();
            var color = e.Category switch
            {
                BimCategory.Window => Color.FromArgb(255, 0x5A, 0xC8, 0xFA),
                BimCategory.Door => Color.FromArgb(255, 0xFF, 0x9F, 0x0A),
                _ => Color.FromArgb(255, 0xFF, 0x45, 0x3A)
            };
            var poly = new Polygon { Fill = new SolidColorBrush(color), Stroke = new SolidColorBrush(Light ? Colors.White : Color.FromArgb(255, 0x1C, 0x1C, 0x1E)), StrokeThickness = 0.8 };
            foreach (var p in ring) poly.Points.Add(p);
            PreviewCanvas.Children.Add(poly);
        }
    }

    /// <summary>Контур элемента в плане XZ сцены (Polygon2D или прямоугольник с поворотом).</summary>
    private static List<(double X, double Z)> Ring(ArchitecturalElement e)
    {
        if (e.Polygon2D.Count >= 3)
            return e.Polygon2D.Select(p =>
            {
                var (rx, rz) = ArchitecturalElement.RotateY(p.X, p.Z, e.RotationY);
                return (e.X + rx, e.Z + rz);
            }).ToList();
        double hw = e.Width / 2, hd = e.Depth / 2;
        return new[] { (-hw, -hd), (hw, -hd), (hw, hd), (-hw, hd) }.Select(p =>
        {
            var (rx, rz) = ArchitecturalElement.RotateY(p.Item1, p.Item2, e.RotationY);
            return (e.X + rx, e.Z + rz);
        }).ToList();
    }

    private (Color Fill, int Alpha, Color Stroke) Palette(ArchitecturalElement e)
    {
        bool light = Light;
        var stroke = light ? Color.FromArgb(0x70, 0x3A, 0x3A, 0x3C) : Color.FromArgb(0x60, 0x00, 0x00, 0x00);
        return e.Category switch
        {
            BimCategory.Foundation => (light ? Color.FromArgb(255, 0x8E, 0x8E, 0x93) : Color.FromArgb(255, 0x48, 0x48, 0x4A), 255, stroke),
            BimCategory.Column => (Color.FromArgb(255, 0xFF, 0x7A, 0x45), 255, stroke),
            BimCategory.Slab or BimCategory.Roof => (light ? Color.FromArgb(255, 0x9F, 0xC9, 0xF5) : Color.FromArgb(255, 0x3A, 0x6E, 0xA5), 0xB8, stroke),
            BimCategory.Window => (Color.FromArgb(255, 0x5A, 0xC8, 0xFA), 0xD8, stroke),
            BimCategory.Door => (Color.FromArgb(255, 0xC9, 0x8B, 0x52), 0xF0, stroke),
            _ => e.Material == "brick"
                ? (light ? Color.FromArgb(255, 0xE8, 0xD9, 0xC8) : Color.FromArgb(255, 0x6E, 0x60, 0x52), 0xE0, stroke)
                : (light ? Color.FromArgb(255, 0xF2, 0xF2, 0xF7) : Color.FromArgb(255, 0x8E, 0x8E, 0x93), 0xE8, stroke)
        };
    }

    private static Color Shade(Color c, double k) => Color.FromArgb(c.A, (byte)(c.R * k), (byte)(c.G * k), (byte)(c.B * k));

    private void AddPolygon(IReadOnlyList<Point> points, Color fill, int alpha, Color stroke)
    {
        var poly = new Polygon
        {
            Fill = new SolidColorBrush(Color.FromArgb((byte)alpha, fill.R, fill.G, fill.B)),
            Stroke = new SolidColorBrush(stroke),
            StrokeThickness = 0.6,
            StrokeLineJoin = PenLineJoin.Round
        };
        foreach (var p in points) poly.Points.Add(p);
        PreviewCanvas.Children.Add(poly);
    }

    private static (double Scale, double OffsetX, double OffsetY) Fit(IReadOnlyList<Point> points, double width, double height, double pad)
    {
        if (points.Count == 0) return (1, 0, 0);
        double x0 = points.Min(p => p.X), x1 = points.Max(p => p.X), y0 = points.Min(p => p.Y), y1 = points.Max(p => p.Y);
        double k = Math.Min((width - 2 * pad) / Math.Max(1e-6, x1 - x0), (height - 2 * pad) / Math.Max(1e-6, y1 - y0));
        return (k, (width - (x1 - x0) * k) / 2 - x0 * k, (height - (y1 - y0) * k) / 2 - y0 * k);
    }
}
