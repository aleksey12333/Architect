using System.Globalization;
using AIArchitectStudio.Models.Gpzu;
using AIArchitectStudio.Services;
using AIArchitectStudio.Services.AI;
using AIArchitectStudio.Services.Gpzu;
using AIArchitectStudio.ViewModels;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using Windows.Storage.Pickers;
using WinRT.Interop;
using static AIArchitectStudio.Views.DialogKit;

namespace AIArchitectStudio.Views;

/// <summary>
/// Студия «Градостроительный план (ГПЗУ) и охранные зоны» (Волна 5, дизайн Liquid Glass): кадастровый паспорт со сверкой площади
/// по Гауссу, регламент подзоны, список зон с цветом и тумблером слоя (тумблер управляет и чертежом, и 3D-вьюпортом), ведомость
/// координат выбранной зоны (№, X, Y, L — моноширинные, по разрядам), проверка посадки и экспертиза через модель; справа —
/// векторный чертёж участка с масштабированием (колесо / щипок), сеткой МСК, «Север ↑» и легендой.
/// </summary>
public sealed partial class GpzuStudioDialog : ContentDialog
{
    private static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");
    private double DrawW = 760, DrawH = 600;

    private readonly MainViewModel _vm;
    private readonly nint _hwnd;
    private readonly LocalizationService L;
    private GpzuSitePlan? _plan;
    private string _selected = "boundary";

    private readonly StackPanel _left = new() { Spacing = 10 };
    private readonly StackPanel _right = new() { Spacing = 10 };
    private readonly Canvas _canvas = new() { Width = 760, Height = 600 };
    private readonly ScrollViewer _zoom = new()
    {
        ZoomMode = ZoomMode.Enabled, MinZoomFactor = 0.6f, MaxZoomFactor = 8f, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollMode = ScrollMode.Enabled, VerticalScrollMode = ScrollMode.Enabled
    };
    private readonly Border _legend = new();
    private readonly TextBlock _status = Text(string.Empty, 11.5, secondary: true, wrap: true);
    private readonly TextBlock _expertise = Text(string.Empty, 12, wrap: true);
    private readonly ComboBox _model = new() { FontSize = 12, MinWidth = 170, CornerRadius = new CornerRadius(8) };
    private Button? _buildButton, _sheetButton, _expertButton;

    /// <summary>Итог работы окна: участок перенесён в сцену (для автотеста и съёмки).</summary>
    public GpzuComplianceReport? Result { get; private set; }

    public string? LastSheetPath { get; private set; }

    public GpzuStudioDialog(MainViewModel viewModel, nint hwnd)
    {
        _vm = viewModel;
        _hwnd = hwnd;
        L = viewModel.Loc;
        InitializeComponent();
        Background = Brush("LiquidGlassSurfaceBrush", "IslandBrush");
        BorderBrush = Brush("LiquidGlassSpecularBorderBrush", "StudioBorderBrush");
        CloseButtonText = L.Get("W41Close");
        BuildLayout();
        Refresh();
        Opened += (_, _) =>
        {
            // Размер окна — по главному окну (кнопки внизу всегда видны), фокус — на чертёж, без рамки на первом тумблере.
            if (XamlRoot is { } root)
            {
                Root.Width = Math.Min(1480, root.Size.Width - 110);
                Root.Height = Math.Min(860, root.Size.Height - 190);
            }
            _zoom.Focus(FocusState.Programmatic);
            Choreograph();
        };
    }

    private static Brush Brush(string key, string fallback) =>
        Application.Current.Resources.TryGetValue(key, out var v) && v is Brush b ? b : Res(fallback);

    // ═════════════════════════════ Компоновка ═════════════════════════════

    private void BuildLayout()
    {
        Root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        Root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        // Заголовок.
        var head = new Grid { ColumnSpacing = 12 };
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        head.Children.Add(GlyphBadge("", 34));
        var titles = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center };
        titles.Children.Add(Text(L.Get("W5StudioTitle"), 19, strong: true));
        titles.Children.Add(Text(L.Get("W5StudioSubtitle"), 11.5, secondary: true));
        Grid.SetColumn(titles, 1);
        head.Children.Add(titles);
        Root.Children.Add(head);

        // Тело: две колонки карточек (паспорт, регламент, проверка | слои, ведомость координат) и чертёж.
        var body = new Grid { ColumnSpacing = 12 };
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(352) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(372) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetRow(body, 1);
        body.Children.Add(new ScrollViewer { Content = _left, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(0, 0, 8, 0) });
        var rightCards = new ScrollViewer { Content = _right, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(0, 0, 8, 0) };
        Grid.SetColumn(rightCards, 1);
        body.Children.Add(rightCards);
        var drawing = new Grid();
        _canvas.Clip = new RectangleGeometry { Rect = new Rect(0, 0, DrawW, DrawH) };
        _zoom.Content = _canvas;
        drawing.Children.Add(Glass(_zoom, 0, elevated: false));
        _legend.HorizontalAlignment = HorizontalAlignment.Left;
        _legend.VerticalAlignment = VerticalAlignment.Bottom;
        _legend.Margin = new Thickness(14);
        drawing.Children.Add(_legend);
        Grid.SetColumn(drawing, 2);
        // Чертёж подстраивается под размер области (перерисовка без изменения масштаба окна).
        _zoom.SizeChanged += (_, e) =>
        {
            if (Math.Abs(e.NewSize.Width - DrawW) < 2 && Math.Abs(e.NewSize.Height - DrawH) < 2) return;
            DrawW = Math.Max(300, e.NewSize.Width - 4);
            DrawH = Math.Max(300, e.NewSize.Height - 4);
            _canvas.Width = DrawW;
            _canvas.Height = DrawH;
            _canvas.Clip = new RectangleGeometry { Rect = new Rect(0, 0, DrawW, DrawH) };
            if (_plan is { } p) DrawPlan(p);
        };
        body.Children.Add(drawing);
        Root.Children.Add(body);

        // Низ: загрузка, выпуск чертежа, перенос в 3D; строка состояния.
        var foot = new Grid { ColumnSpacing = 10 };
        foot.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        foot.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        foot.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        foot.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        foot.Children.Add(_status);
        var load = ActionButton("", L.Get("W5Load"), async (_, _) => await PickFilesAsync());
        _sheetButton = ActionButton("", L.Get("W5Sheet"), async (_, _) => await ExportSheetAsync());
        _buildButton = ActionButton("", L.Get("W5Build"), (_, _) => BuildInScene(), accent: true);
        _buildButton.Background = Res("AccentBrush");
        _buildButton.Foreground = Res("OnAccentBrush");
        Grid.SetColumn(load, 1);
        Grid.SetColumn(_sheetButton, 2);
        Grid.SetColumn(_buildButton, 3);
        foot.Children.Add(load);
        foot.Children.Add(_sheetButton);
        foot.Children.Add(_buildButton);
        Grid.SetRow(foot, 2);
        Root.Children.Add(foot);

        foreach (var (title, alias) in new[] { ("Sonnet 5.5", UnifiedCloudAiClient.Sonnet55.Alias), ("GPT 5.6 Luna", UnifiedCloudAiClient.Luna56.Alias), ("Opus 5.5", UnifiedCloudAiClient.Opus55.Alias) })
            _model.Items.Add(new ComboBoxItem { Content = title, Tag = alias });
        _model.SelectedIndex = 0;
    }

    /// <summary>Стеклянная карточка Liquid Glass: приподнятое стекло, спекулярный кант 1 px, скругление 12.</summary>
    private static Border Glass(UIElement child, double padding = 14, bool elevated = true) => new()
    {
        Background = Brush(elevated ? "LiquidGlassElevatedBrush" : "LiquidGlassSurfaceBrush", "CardBrush"),
        BorderBrush = Brush("LiquidGlassSpecularBorderBrush", "StudioBorderBrush"),
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(12),
        Padding = new Thickness(padding),
        Child = child
    };

    // ═════════════════════════════ Данные ═════════════════════════════

    public void Refresh()
    {
        _plan = _vm.BuildGpzuPlan();
        _left.Children.Clear();
        _right.Children.Clear();
        _canvas.Children.Clear();
        bool has = _plan is not null;
        if (_buildButton is not null) _buildButton.IsEnabled = has;
        if (_sheetButton is not null) _sheetButton.IsEnabled = has;
        if (!has)
        {
            var empty = new StackPanel { Spacing = 8 };
            empty.Children.Add(Text(L.Get("W5EmptyTitle"), 14, strong: true));
            empty.Children.Add(Text(L.Get("W5EmptyHint"), 12, secondary: true, wrap: true));
            empty.Children.Add(ActionButton("", L.Get("W5LoadDemo"), async (_, _) => await LoadDemoAsync(), accent: true));
            _left.Children.Add(Glass(empty));
            _status.Text = L.Get("W5StatusEmpty");
            return;
        }
        var plan = _plan!;
        _left.Children.Add(PassportCard(plan));
        _left.Children.Add(RegulationCard(plan));
        _left.Children.Add(ComplianceCard(plan));
        _right.Children.Add(CoordinatesCard(plan));
        _right.Children.Add(LayersCard(plan));
        DrawPlan(plan);
        var doc = plan.Document;
        _status.Text = string.Create(Ru, $"{System.IO.Path.GetFileName(doc.SourceFile)} · {L.Get("W5Tables")} {doc.Tables.Count} · {L.Get("W5ZonesCount")} {plan.Zones.Count}") +
                       (_vm.GpzuTopo is { } t ? string.Create(Ru, $" · {System.IO.Path.GetFileName(t.SourceFile)} {t.Version}: {L.Get("W5Networks")} {t.Networks.Count}, {L.Get("W5Elevations")} {t.Elevations.Count}") : " · " + L.Get("W5NoTopo"));
    }

    private Border PassportCard(GpzuSitePlan plan)
    {
        var doc = plan.Document;
        var sp = new StackPanel { Spacing = 6 };
        sp.Children.Add(Caption(L.Get("W5Passport")));
        sp.Children.Add(Text(doc.Cadastral, 18, strong: true, mono: true));
        sp.Children.Add(Text($"ГПЗУ № {doc.Number} · {doc.IssueDate}", 11.5, secondary: true, wrap: true));
        sp.Children.Add(Text(doc.Address, 12, wrap: true));
        sp.Children.Add(Text(doc.Applicant + (doc.Application.Length > 0 ? " · " + doc.Application : string.Empty), 11, secondary: true, wrap: true));
        var grid = Metrics(
            (L.Get("W5AreaDoc"), string.Create(Ru, $"{doc.DeclaredArea:N2}"), "м²"),
            (L.Get("W5AreaGauss"), string.Create(Ru, $"{doc.GaussArea:N2}"), "м²"),
            (L.Get("W5AreaDelta"), string.Create(Ru, $"{doc.GaussArea - doc.DeclaredArea:+0.00;-0.00}"), "м²"),
            (L.Get("W5PointsCount"), doc.Boundary.Count.ToString(Ru), doc.CoordinateSystem));
        sp.Children.Add(grid);
        bool match = Math.Abs(doc.GaussArea - doc.DeclaredArea) < 0.5;
        sp.Children.Add(Chip(match ? L.Get("W5AreaMatch") : L.Get("W5AreaMismatch"), match ? "#10B981" : "#F59E0B"));
        return Glass(sp);
    }

    private Border RegulationCard(GpzuSitePlan plan)
    {
        var r = plan.Document.Regulation;
        var sp = new StackPanel { Spacing = 6 };
        sp.Children.Add(Caption(L.Get("W5Regulation")));
        sp.Children.Add(Text($"{r.ZoneCode} · {r.ZoneName}", 13, strong: true, wrap: true));
        sp.Children.Add(Metrics(
            (L.Get("W5Subzone"), r.Subzone, string.Empty),
            (L.Get("W5MaxLiving"), string.Create(Ru, $"{r.MaxLivingArea:N0}"), "м²"),
            (L.Get("W5MinParking"), r.MinParkingPlaces?.ToString(Ru) ?? "—", L.Get("W5Places")),
            (L.Get("W5Ceiling"), plan.CeilingAbs is { } c ? string.Create(Ru, $"{c:0.00}") : "—", plan.CeilingHeight is { } h ? string.Create(Ru, $"м БС · +{h:0.0} м") : "м БС")));
        return Glass(sp);
    }

    /// <summary>Таблица показателей «подпись · значение · единица»: числа моноширинные, по правому краю.</summary>
    private static Grid Metrics(params (string Label, string Value, string Unit)[] rows)
    {
        var g = new Grid { ColumnSpacing = 8, RowSpacing = 3 };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Auto) });
        for (int i = 0; i < rows.Length; i++)
        {
            g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var l = Text(rows[i].Label, 11.5, secondary: true);
            var v = Text(rows[i].Value, 12.5, strong: true, mono: true);
            v.HorizontalAlignment = HorizontalAlignment.Right;
            var u = Text(rows[i].Unit, 10.5, secondary: true);
            Grid.SetRow(l, i); Grid.SetRow(v, i); Grid.SetRow(u, i);
            Grid.SetColumn(v, 1); Grid.SetColumn(u, 2);
            g.Children.Add(l); g.Children.Add(v); g.Children.Add(u);
        }
        return g;
    }

    private Border LayersCard(GpzuSitePlan plan)
    {
        var sp = new StackPanel { Spacing = 2 };
        sp.Children.Add(Caption(L.Get("W5Layers")));
        sp.Children.Add(LayerRow("boundary", SiteBoundaryBuilder.BoundaryColor, string.Create(Ru, $"{L.Get("W5Boundary")} 1…{plan.Boundary.Count}"), L.Get("W5BoundaryHint"), solid: true));
        foreach (var z in plan.Zones)
        {
            string hint = z.Zone.HeightLimitMin.HasValue ? z.Zone.HeightText(Ru) : z.Zone.ExplicationNo > 0 ? L.Format("W5Expl", z.Zone.ExplicationNo) : z.Zone.Source;
            sp.Children.Add(LayerRow(z.Id, z.Zone.ColorHex, z.Zone.Name, hint, solid: z.Zone.IsNetwork || z.Zone.Kind == GpzuZoneKind.AllowedArea));
        }
        if (plan.Existing.Count > 0) sp.Children.Add(LayerRow("existing", SiteBoundaryBuilder.ExistingColor, L.Format("W5Existing", plan.Existing.Count), L.Get("W5ExistingHint"), solid: true));
        if (plan.CeilingHeight is not null) sp.Children.Add(LayerRow("ceiling", SiteBoundaryBuilder.CeilingColor, L.Get("W5CeilingLayer"), string.Create(Ru, $"Hабс {plan.CeilingAbs:0.00} м БС"), solid: true));
        return Glass(sp, 12);
    }

    private Grid LayerRow(string id, string hex, string title, string hint, bool solid)
    {
        var row = new Grid { ColumnSpacing = 10, Padding = new Thickness(6, 5, 6, 5), CornerRadius = new CornerRadius(8), Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            BackgroundTransition = new BrushTransition { Duration = TimeSpan.FromMilliseconds(120) } };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var swatch = new Border { Width = 14, Height = 14, CornerRadius = new CornerRadius(4), VerticalAlignment = VerticalAlignment.Center,
            Background = Hex(hex, solid ? (byte)0xFF : (byte)0x40), BorderBrush = Hex(hex), BorderThickness = new Thickness(solid ? 0 : 1.5) };
        var texts = new StackPanel { Spacing = 0 };
        var t1 = Text(title, 12, strong: _selected == id);
        var t2 = Text(hint, 10.5, secondary: true);
        texts.Children.Add(t1);
        texts.Children.Add(t2);
        var toggle = new ToggleSwitch { IsOn = _vm.IsSiteLayerVisible(id), OnContent = string.Empty, OffContent = string.Empty, MinWidth = 0, VerticalAlignment = VerticalAlignment.Center };
        toggle.Toggled += (_, _) =>
        {
            _vm.SetSiteLayerVisible(id, toggle.IsOn);
            if (_plan is { } p) DrawPlan(p);
        };
        Grid.SetColumn(texts, 1);
        Grid.SetColumn(toggle, 2);
        row.Children.Add(swatch);
        row.Children.Add(texts);
        row.Children.Add(toggle);
        row.PointerEntered += (_, _) => row.Background = Res("HoverBrush");
        row.PointerExited += (_, _) => row.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        row.Tapped += (_, e) =>
        {
            if (e.OriginalSource is FrameworkElement fe && IsInside(fe, toggle)) return;
            _selected = id;
            Refresh();
        };
        return row;
    }

    private static bool IsInside(DependencyObject? d, DependencyObject target)
    {
        for (; d is not null; d = VisualTreeHelper.GetParent(d))
            if (ReferenceEquals(d, target)) return true;
        return false;
    }

    /// <summary>Ведомость координат выбранной зоны: №, X_МСК, Y_МСК, L до следующей точки — моноширинные, по разрядам.</summary>
    private Border CoordinatesCard(GpzuSitePlan plan)
    {
        var doc = plan.Document;
        IReadOnlyList<GeoPoint> pts = doc.Boundary;
        string title = L.Get("W5Boundary");
        if (_selected != "boundary" && doc.Zones.FirstOrDefault(z => z.Id == _selected) is { } zone)
        {
            title = zone.Name;
            pts = zone.Polygon.Count > 0 ? zone.Polygon : zone.Lines.FirstOrDefault() ?? [];
        }
        var sp = new StackPanel { Spacing = 4 };
        sp.Children.Add(Caption(L.Get("W5Coordinates")));
        sp.Children.Add(Text(title, 12, strong: true, wrap: true));
        var g = new Grid { ColumnSpacing = 10, RowSpacing = 1 };
        foreach (var w in new[] { 28.0, 96, 108, 52 }) g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(w) });
        void Cell(string text, int row, int col, bool head)
        {
            var tb = Text(text, head ? 10.5 : 12, strong: head, secondary: head, mono: !head);
            tb.HorizontalAlignment = HorizontalAlignment.Right;
            Grid.SetRow(tb, row);
            Grid.SetColumn(tb, col);
            g.Children.Add(tb);
        }
        g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Cell("№", 0, 0, true); Cell("X, м", 0, 1, true); Cell("Y, м", 0, 2, true); Cell("L, м", 0, 3, true);
        int n = pts.Count;
        bool closed = _selected == "boundary" || doc.Zones.FirstOrDefault(z => z.Id == _selected)?.Polygon.Count > 0;
        for (int i = 0; i < Math.Min(n, 40); i++)
        {
            g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var p = pts[i];
            double len = i + 1 < n ? Geodesy.Length(p, pts[i + 1]) : closed && n > 2 ? Geodesy.Length(p, pts[0]) : double.NaN;
            Cell(p.Number, i + 1, 0, false);
            Cell(p.X.ToString("N2", Ru), i + 1, 1, false);
            Cell(p.Y.ToString("N2", Ru), i + 1, 2, false);
            Cell(double.IsNaN(len) ? "—" : len.ToString("0.00", Ru), i + 1, 3, false);
        }
        sp.Children.Add(g);
        if (n > 40) sp.Children.Add(Text(L.Format("W5More", n - 40), 10.5, secondary: true));
        if (n == 0) sp.Children.Add(Text(L.Get("W5NoCoordinates"), 11, secondary: true, wrap: true));
        return Glass(sp);
    }

    private Border ComplianceCard(GpzuSitePlan plan)
    {
        var sp = new StackPanel { Spacing = 6 };
        sp.Children.Add(Caption(L.Get("W5Compliance")));
        var report = GpzuComplianceVerifier.Verify(plan, _vm.Scene.Elements.ToList());
        foreach (var f in report.Findings.Where(f => f.Code != "GPZU-EMPTY" || report.BuildingElements == 0))
        {
            var row = new Grid { ColumnSpacing = 8 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            string hex = f.Severity switch { GpzuSeverity.Ok => "#10B981", GpzuSeverity.Critical => "#EF4444", GpzuSeverity.Warning => "#F59E0B", _ => "#8E8E93" };
            string glyph = f.Severity switch { GpzuSeverity.Ok => "", GpzuSeverity.Critical => "", GpzuSeverity.Warning => "", _ => "" };
            row.Children.Add(new FontIcon { Glyph = glyph, FontSize = 12, Foreground = Hex(hex), VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 2, 0, 0) });
            var t = new StackPanel();
            t.Children.Add(Text(f.Title, 12, strong: true, wrap: true));
            t.Children.Add(Text(f.Detail, 11, secondary: true, wrap: true));
            Grid.SetColumn(t, 1);
            row.Children.Add(t);
            sp.Children.Add(row);
        }
        sp.Children.Add(Divider(6));
        // Поле модели и текст экспертизы переживают перестройку карточек — отцепляем от прежнего родителя.
        if (_model.Parent is Panel oldBar) oldBar.Children.Remove(_model);
        if (_expertise.Parent is Panel oldText) oldText.Children.Remove(_expertise);
        var bar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        _expertButton = ActionButton("", L.Get("W5Expertise"), async (_, _) => await RunExpertiseAsync(), accent: true);
        bar.Children.Add(_expertButton);
        bar.Children.Add(_model);
        sp.Children.Add(bar);
        sp.Children.Add(_expertise);
        return Glass(sp);
    }

    // ═════════════════════════════ Чертёж ═════════════════════════════

    private void DrawPlan(GpzuSitePlan plan)
    {
        _canvas.Children.Clear();
        var (minX, maxX, minZ, maxZ) = plan.Bounds;
        double margin = 28;
        double s = Math.Min((DrawW - 120) / (maxX - minX + 2 * margin), (DrawH - 110) / (maxZ - minZ + 2 * margin));
        double ox = DrawW / 2 - (minX + maxX) / 2 * s, oz = DrawH / 2 - (minZ + maxZ) / 2 * s + 10;
        Point P(SitePoint p) => new(ox + p.X * s, oz + p.Z * s);
        bool Visible(string id) => _vm.IsSiteLayerVisible(id);
        var light = Hex("#FFFFFF");

        // Сетка МСК-66 через 25 м (линии постоянных X — горизонтальные, Y — вертикальные) с подписями.
        double step = 25;
        var (gx0, gy0) = plan.ToGeo((0 - ox) / s, (DrawH - oz) / s);
        var (gx1, gy1) = plan.ToGeo((DrawW - ox) / s, (0 - oz) / s);
        for (double gx = Math.Ceiling(Math.Min(gx0, gx1) / step) * step; gx <= Math.Max(gx0, gx1); gx += step)
        {
            var a = P(plan.ToScene(gx, gy0));
            _canvas.Children.Add(new Line { X1 = 0, X2 = DrawW, Y1 = a.Y, Y2 = a.Y, Stroke = Res("StudioBorderBrush"), StrokeThickness = 1, StrokeDashArray = [2, 4] });
            AddText(string.Create(Ru, $"X {gx:N0}"), 6, a.Y - 14, 9.5, Res("TextSecondaryBrush"), mono: true);
        }
        for (double gy = Math.Ceiling(Math.Min(gy0, gy1) / step) * step; gy <= Math.Max(gy0, gy1); gy += step)
        {
            var a = P(plan.ToScene(gx0, gy));
            _canvas.Children.Add(new Line { X1 = a.X, X2 = a.X, Y1 = 0, Y2 = DrawH, Stroke = Res("StudioBorderBrush"), StrokeThickness = 1, StrokeDashArray = [2, 4] });
            AddText(string.Create(Ru, $"Y {gy:N0}"), a.X + 3, DrawH - 18, 9.5, Res("TextSecondaryBrush"), mono: true);
        }

        // Коридоры сетей.
        foreach (var z in plan.Zones.Where(z => z.Zone.IsNetwork && Visible(z.Id)))
        {
            foreach (var piece in z.Corridor)
                _canvas.Children.Add(new Polygon { Points = Points(piece.Select(P)), Fill = Hex(z.Zone.ColorHex, 0x40) });
            foreach (var l in z.Lines)
                _canvas.Children.Add(new Polyline { Points = Points(l.Select(P)), Stroke = Hex(z.Zone.ColorHex), StrokeThickness = 1.8 });
        }
        // Существующие ОКС.
        if (Visible("existing"))
            foreach (var o in plan.Existing)
            {
                _canvas.Children.Add(new Polygon { Points = Points(o.Footprint.Select(P)), Fill = Hex(SiteBoundaryBuilder.ExistingColor, 0xB0), Stroke = Hex("#6B4A3C"), StrokeThickness = 1 });
                var c = P(new SitePoint(o.Footprint.Average(p => p.X), o.Footprint.Average(p => p.Z)));
                if (o.Number > 0) AddText("№ " + o.Number, c.X - 10, c.Y - 7, 10, light, bold: true);
            }
        // Пятно допустимого размещения со штриховкой.
        if (plan.Zones.FirstOrDefault(z => z.Zone.Kind == GpzuZoneKind.AllowedArea) is { } allowed && Visible(allowed.Id))
        {
            _canvas.Children.Add(new Polygon { Points = Points(allowed.Polygon.Select(P)), Fill = Hex(SiteBoundaryBuilder.AllowedColor, 0x26) });
            foreach (var (a, b) in SiteBoundaryBuilder.HatchSegments(allowed.Polygon, 2.5, 45))
            {
                var pa = P(a);
                var pb = P(b);
                _canvas.Children.Add(new Line { X1 = pa.X, Y1 = pa.Y, X2 = pb.X, Y2 = pb.Y, Stroke = Hex(SiteBoundaryBuilder.AllowedColor, 0xC0), StrokeThickness = 0.8 });
            }
        }
        // ЗОУИТ и подзоны: смещённый пунктир (зоны совпадают с участком — вложены по 0,6 м и сдвинуты по фазе).
        int k = 0;
        foreach (var z in plan.Zones.Where(z => z.Polygon.Count >= 3 && z.Zone.Kind is GpzuZoneKind.TransportZone or GpzuZoneKind.AirfieldSubzone or GpzuZoneKind.LocalZone))
        {
            k++;
            if (!Visible(z.Id)) continue;
            var poly = z.Zone.CoversWholeParcel ? SiteBoundaryBuilder.Offset(z.Polygon, 0.55 * k) : z.Polygon.ToList();
            _canvas.Children.Add(new Polygon
            {
                Points = Points(poly.Select(P)), Stroke = Hex(z.Zone.ColorHex), StrokeThickness = z.Id == _selected ? 2.6 : 1.4,
                StrokeDashArray = [5, 3], StrokeDashOffset = k * 1.7, Fill = z.Zone.Kind == GpzuZoneKind.LocalZone ? Hex(z.Zone.ColorHex, 0x1A) : null
            });
        }
        // Граница участка, точки и длины сторон.
        if (Visible("boundary"))
        {
            _canvas.Children.Add(new Polygon { Points = Points(plan.Boundary.Select(P)), Stroke = Hex(SiteBoundaryBuilder.BoundaryColor), StrokeThickness = _selected == "boundary" ? 3.2 : 2.6, StrokeLineJoin = PenLineJoin.Round });
            double cx = plan.Boundary.Average(p => p.X), cz = plan.Boundary.Average(p => p.Z);
            foreach (var seg in plan.Segments.Where(sg => sg.Length >= 2.5))
            {
                double dx = seg.Mid.X - cx, dz = seg.Mid.Z - cz, l = Math.Max(1e-6, Math.Sqrt(dx * dx + dz * dz));
                var at = P(new SitePoint(seg.Mid.X + dx / l * 3.2, seg.Mid.Z + dz / l * 3.2));
                AddText(seg.Length.ToString("0.00", Ru), at.X - 14, at.Y - 7, 9.5, Res("TextPrimaryBrush"), mono: true);
            }
            for (int i = 0; i < plan.Boundary.Count; i++)
            {
                var p = P(plan.Boundary[i]);
                double dx = plan.Boundary[i].X - cx, dz = plan.Boundary[i].Z - cz, l = Math.Max(1e-6, Math.Sqrt(dx * dx + dz * dz));
                double lx = p.X + dx / l * 13, lz = p.Y + dz / l * 13;
                _canvas.Children.Add(new Ellipse { Width = 6, Height = 6, Fill = Hex(SiteBoundaryBuilder.BoundaryColor), Margin = new Thickness(p.X - 3, p.Y - 3, 0, 0) });
                var pin = new Border { Width = 20, Height = 18, CornerRadius = new CornerRadius(9), Background = Hex(SiteBoundaryBuilder.BoundaryColor),
                    Child = new TextBlock { Text = plan.PointNumbers[i], FontSize = 10, FontWeight = FontWeights.SemiBold, Foreground = light, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } };
                Canvas.SetLeft(pin, lx - 10);
                Canvas.SetTop(pin, lz - 9);
                _canvas.Children.Add(pin);
            }
        }
        // «Север» и масштаб.
        AddNorth(DrawW - 58, 18);
        double bar = 20 * s;
        _canvas.Children.Add(new Rectangle { Width = bar, Height = 4, Fill = Res("TextPrimaryBrush"), Margin = new Thickness(DrawW - bar - 22, DrawH - 34, 0, 0) });
        AddText("20 м", DrawW - bar / 2 - 36, DrawH - 30, 10, Res("TextPrimaryBrush"), mono: true);
        BuildLegend(plan);
    }

    private static PointCollection Points(IEnumerable<Point> pts)
    {
        var pc = new PointCollection();
        foreach (var p in pts) pc.Add(p);
        return pc;
    }

    private void AddText(string text, double x, double y, double size, Brush brush, bool mono = false, bool bold = false)
    {
        var tb = new TextBlock { Text = text, FontSize = size, Foreground = brush, FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal };
        if (mono) tb.FontFamily = Mono;
        Canvas.SetLeft(tb, x);
        Canvas.SetTop(tb, y);
        _canvas.Children.Add(tb);
    }

    private void AddNorth(double x, double y)
    {
        var arrow = new Polygon { Points = Points([new Point(x + 20, y), new Point(x + 31, y + 30), new Point(x + 20, y + 23), new Point(x + 9, y + 30)]), Fill = Res("AccentBrush") };
        _canvas.Children.Add(arrow);
        AddText(L.Get("W5North"), x + 2, y + 33, 10.5, Res("TextPrimaryBrush"), bold: true);
    }

    private void BuildLegend(GpzuSitePlan plan)
    {
        var sp = new StackPanel { Spacing = 3 };
        sp.Children.Add(Caption(L.Get("W5Legend")));
        void Row(string hex, string text, bool dashed = false)
        {
            var r = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7 };
            r.Children.Add(new Border { Width = 18, Height = dashed ? 3 : 10, CornerRadius = new CornerRadius(2), Background = Hex(hex, dashed ? (byte)0xFF : (byte)0xC0), VerticalAlignment = VerticalAlignment.Center });
            r.Children.Add(Text(text, 10.5));
            sp.Children.Add(r);
        }
        Row(SiteBoundaryBuilder.BoundaryColor, L.Get("W5Boundary"));
        Row(SiteBoundaryBuilder.AllowedColor, L.Get("W5Allowed"));
        foreach (var style in SiteBoundaryBuilder.NetworkStyles.Where(st => plan.Zones.Any(z => z.Zone.Kind == st.Kind)).DistinctBy(st => st.Title))
            Row(style.ColorHex, string.Create(Ru, $"{style.Title} ± {style.HalfWidth:0.#} м"));
        int zouit = plan.Zones.Count(z => z.Zone.Kind is GpzuZoneKind.TransportZone or GpzuZoneKind.AirfieldSubzone or GpzuZoneKind.LocalZone);
        if (zouit > 0) Row("#EC4899", L.Format("W5LegendZouit", zouit), dashed: true);
        if (plan.Existing.Count > 0) Row(SiteBoundaryBuilder.ExistingColor, L.Get("W5ExistingShort"));
        _legend.Child = Glass(sp, 10);
    }

    // ═════════════════════════════ Действия ═════════════════════════════

    private async Task PickFilesAsync()
    {
        var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
        foreach (var ext in new[] { ".pdf", ".dwg", ".dxf", ".xml", ".json", ".csv", ".txt" }) picker.FileTypeFilter.Add(ext);
        InitializeWithWindow.Initialize(picker, _hwnd);
        var files = await picker.PickMultipleFilesAsync();
        if (files is { Count: > 0 }) await LoadFilesAsync(files.Select(f => f.Path));
    }

    /// <summary>Загрузка документов: ГПЗУ (PDF/XML/JSON/TXT/CSV) — первым, затем топосъёмка (DWG/DXF).</summary>
    public async Task LoadFilesAsync(IEnumerable<string> paths)
    {
        var list = paths.ToList();
        try
        {
            _status.Text = L.Get("W5Reading");
            foreach (var p in list.Where(p => !IsTopo(p))) await _vm.LoadGpzuAsync(p);
            foreach (var p in list.Where(IsTopo)) await _vm.LoadTopoAsync(p);
            Refresh();
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or FormatException or UnauthorizedAccessException or System.Xml.XmlException or System.Text.Json.JsonException)
        {
            _status.Text = L.Get("W5ReadFailed") + ": " + ex.Message;
        }
    }

    private static bool IsTopo(string p) => p.EndsWith(".dwg", StringComparison.OrdinalIgnoreCase) || p.EndsWith(".dxf", StringComparison.OrdinalIgnoreCase);

    /// <summary>Демонстрационный комплект DemoKit/GPZU (ГПЗУ и топосъёмка рядом с программой).</summary>
    public async Task LoadDemoAsync()
    {
        string dir = System.IO.Path.Combine(AppContext.BaseDirectory, "DemoKit", "GPZU");
        if (!Directory.Exists(dir)) { _status.Text = L.Get("W5NoDemo"); return; }
        await LoadFilesAsync(Directory.GetFiles(dir).Where(f => f.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase) || IsTopo(f)));
    }

    /// <summary>Выбор зоны (ведомость координат и выделение на чертеже).</summary>
    public void SelectZone(string id)
    {
        _selected = id;
        Refresh();
    }

    public void BuildInScene()
    {
        if (_plan is null) return;
        Result = _vm.BuildSiteInScene(_plan);
        _status.Text = L.Get("W5BuiltTitle") + (Result is null ? string.Empty : " · " + _vm.Summary(Result));
        Refresh();
    }

    private async Task ExportSheetAsync()
    {
        if (_plan is null) return;
        var picker = new FileSavePicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary, SuggestedFileName = "ГПЗУ_" + _plan.Document.Cadastral.Replace(':', '-') };
        picker.FileTypeChoices.Add("PDF", [".pdf"]);
        picker.FileTypeChoices.Add("DXF", [".dxf"]);
        InitializeWithWindow.Initialize(picker, _hwnd);
        var file = await picker.PickSaveFileAsync();
        if (file is null) return;
        await ExportSheetToAsync(file.Path);
    }

    /// <summary>Выпуск листа ГПЗУ / ПЗУ: PDF по ГОСТ Р 21.101 или DXF со слоями участка, зон и сетей.</summary>
    public async Task ExportSheetToAsync(string path)
    {
        if (_plan is null) return;
        var plan = _plan;
        var elements = _vm.Scene.Elements.ToList();
        var report = GpzuComplianceVerifier.Verify(plan, elements);
        string info = await Task.Run(() => path.EndsWith(".dxf", StringComparison.OrdinalIgnoreCase)
            ? GpzuDxfExporter.Export(plan, path)
            : GostDrawingPdfExporter.ExportGpzuSheet(plan, report, path, elements).Summary);
        LastSheetPath = path;
        _status.Text = L.Get("W5SheetDone") + ": " + System.IO.Path.GetFileName(path) + " · " + info;
    }

    private async Task RunExpertiseAsync()
    {
        if (_expertButton is not null) _expertButton.IsEnabled = false;
        _expertise.Text = L.Get("W5ExpertiseRunning");
        try
        {
            string model = (_model.SelectedItem as ComboBoxItem)?.Tag as string ?? UnifiedCloudAiClient.Sonnet55.Alias;
            var r = await _vm.RunGpzuExpertiseAsync(model);
            if (r is not null)
                _expertise.Text = (r.Offline ? L.Get("W5ExpertiseOffline") + (r.Error is { } e ? " (" + e + ")" : string.Empty) : string.Create(Ru, $"{r.Model} · {r.Seconds:0} с · {r.InputTokens:N0} → {r.OutputTokens:N0}")) + "\n" + r.Text;
        }
        finally
        {
            if (_expertButton is not null) _expertButton.IsEnabled = true;
        }
    }

    /// <summary>Хореография появления: карточки слева проявляются по очереди (24 мс, не более 8), чертёж — вместе с первой.</summary>
    private void Choreograph()
    {
        if (!new Windows.UI.ViewManagement.UISettings().AnimationsEnabled) return;
        var sb = new Storyboard();
        int i = 0;
        foreach (var child in _left.Children.OfType<UIElement>().Concat(_right.Children.OfType<UIElement>()).Take(8))
        {
            child.Opacity = 0;
            var a = new DoubleAnimation { From = 0, To = 1, Duration = TimeSpan.FromMilliseconds(220), BeginTime = TimeSpan.FromMilliseconds(24 * i++),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
            Storyboard.SetTarget(a, child);
            Storyboard.SetTargetProperty(a, "Opacity");
            sb.Children.Add(a);
        }
        sb.Begin();
    }
}
