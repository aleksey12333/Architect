using System.Globalization;
using AIArchitectStudio.Models.Gpzu;
using AIArchitectStudio.Models.Layouts;
using AIArchitectStudio.Services.Gpzu;
using AIArchitectStudio.Services.Layouts;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using static AIArchitectStudio.Views.DialogKit;

namespace AIArchitectStudio.Views;

/// <summary>
/// Волна 7 — главная область раздела «3. Планировки и ИОС»: мульти-план (2–4 пакета этажей бок о бок в одном масштабе с общей сеткой
/// координационных осей, сквозными лифтовыми шахтами, лестницами, квартирами, мокрыми зонами и точками стояков) или подвал с узлами
/// ввода и магистралями и наложение планов на границе пакетов (проверка вертикальной совместимости мокрых и газовых зон).
/// Рисуется кодом на Canvas (векторные фигуры), перерисовка — по размеру, планировке и отчёту проверки.
/// </summary>
public sealed class FloorTierPlanView : UserControl
{
    private static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");
    private readonly Grid _root = new() { Padding = new Thickness(14, 12, 14, 12), RowSpacing = 10 };
    private BuildingLayoutPlan? _plan;
    private StackReport? _report;
    private int _count = 4;
    private bool _basement;

    public FloorTierPlanView()
    {
        Content = _root;
        SizeChanged += (_, _) => Render();
    }

    /// <summary>Последние нарисованные колонки (автотест и съёмка).</summary>
    public IReadOnlyList<PlanTier> LastTiers { get; private set; } = [];

    public void Show(BuildingLayoutPlan? plan, StackReport? report, int count, bool basement)
    {
        _plan = plan;
        _report = report;
        _count = count;
        _basement = basement;
        Render();
    }

    private static Brush B(string hex, byte alpha = 0xFF) => Hex(hex, alpha);

    private void Render()
    {
        _root.Children.Clear();
        _root.RowDefinitions.Clear();
        _root.ColumnDefinitions.Clear();
        if (_plan is not { } plan || ActualWidth < 200 || ActualHeight < 200) return;
        _root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        _root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var head = new Grid { ColumnSpacing = 12 };
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        head.Children.Add(GlyphBadge(_basement ? "\uE7C0" : "\uE8A1", 30));
        var titles = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center };
        var tep = plan.Tep();
        titles.Children.Add(Text(_basement ? "Подвал и узлы ввода · вертикальная проверка зон" : $"Мульти-план · {plan.BuildingName}", 15, strong: true));
        titles.Children.Add(Text(string.Create(Ru, $"{plan.Levels} эт. · {plan.Length:0.0} × {plan.Width:0.0} м · ядро: лифтов {plan.Core.Lifts}, лестниц {plan.Core.Stairs} ({LayoutRooms.StairTitle(plan.Core.StairForm)}, {plan.Core.StairCategory}) · ") +
                                 string.Create(Ru, $"квартир {tep.Apartments:N0}, S кв. {tep.ApartmentArea:N0} м², S жил. {tep.LivingArea:N0} м², K1 {tep.K1:0.00}"), 11, secondary: true, wrap: true));
        Grid.SetColumn(titles, 1);
        head.Children.Add(titles);
        _root.Children.Add(head);

        var body = new Grid { ColumnSpacing = 12, RowSpacing = 12 };
        Grid.SetRow(body, 1);
        _root.Children.Add(body);
        double bodyW = ActualWidth - 28, bodyH = ActualHeight - 24 - 46 - 34;
        if (_basement) BuildBasement(plan, body, bodyW, bodyH);
        else BuildMulti(plan, body, bodyW, bodyH);

        var legend = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        foreach (var k in new[] { LayoutRoomKind.Living, LayoutRoomKind.Kitchen, LayoutRoomKind.Bathroom, LayoutRoomKind.Hall, LayoutRoomKind.Corridor, LayoutRoomKind.LiftShaft,
                     LayoutRoomKind.Stair, LayoutRoomKind.Vestibule })
            legend.Children.Add(Swatch(LayoutRooms.ColorHex(k), LayoutRooms.Title(k)));
        foreach (var r in new[] { RiserKind.ColdWater, RiserKind.HotWater, RiserKind.Sewer, RiserKind.Heating, RiserKind.Power })
            legend.Children.Add(Swatch(LayoutRooms.RiserColor(r), LayoutRooms.RiserTitle(r), round: true));
        var scroll = new ScrollViewer { Content = legend, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, HorizontalScrollMode = ScrollMode.Auto };
        Grid.SetRow(scroll, 2);
        _root.Children.Add(scroll);
    }

    private static StackPanel Swatch(string hex, string text, bool round = false)
    {
        var s = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5 };
        s.Children.Add(new Border { Width = 10, Height = 10, CornerRadius = new CornerRadius(round ? 5 : 2), Background = B(hex, round ? (byte)0xFF : (byte)0xC0), VerticalAlignment = VerticalAlignment.Center });
        s.Children.Add(Text(text, 10.5, secondary: true));
        return s;
    }

    // ═════════════════════════════ Мульти-план ═════════════════════════════

    private void BuildMulti(BuildingLayoutPlan plan, Grid body, double w, double h)
    {
        var tiers = FloorTierLayoutService.Tiers(plan, _count);
        LastTiers = tiers;
        int n = tiers.Count;
        for (int i = 0; i < n; i++) body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        double colW = (w - 12 * (n - 1)) / n;
        // Общий масштаб: план повёрнут (длина корпуса — по вертикали), в колонку входит ширина корпуса с осями.
        double s = Math.Min((colW - 44) / (plan.Width + 2), (h - 64) / (plan.Length + 2));
        for (int i = 0; i < n; i++)
        {
            var t = tiers[i];
            var card = new Grid { RowSpacing = 6 };
            card.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            card.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            var cap = new StackPanel { Spacing = 0 };
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            row.Children.Add(Text(t.Title, 13, strong: true));
            row.Children.Add(Text(t.Elevation, 11.5, mono: true, secondary: true));
            if (t.PackagesMerged > 1) row.Children.Add(Chip(string.Create(Ru, $"типовые · пакетов {t.PackagesMerged}"), "#64748B", 10));
            cap.Children.Add(row);
            var tp = BuildingLayoutPlan.TepOf(t.Package);
            cap.Children.Add(Text(t.Package.IsBasement
                ? string.Create(Ru, $"ИТП, насосная, ВРУ, венткамера · приямки лифтов {plan.Core.Lifts}")
                : string.Create(Ru, $"квартир на этаже {tp.Apartments} · S кв. {tp.ApartmentArea:N1} м² · тип {t.Package.LayoutCode}"), 10.5, secondary: true));
            card.Children.Add(cap);
            var canvas = new Canvas { Width = plan.Width * s + 40, Height = plan.Length * s + 30, HorizontalAlignment = HorizontalAlignment.Center };
            DrawRotated(plan, t.Package, canvas, s, 28, 18);
            var holder = new Border { Child = canvas, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top };
            Grid.SetRow(holder, 1);
            card.Children.Add(holder);
            var glass = new Border
            {
                Background = Res("CardBrush"),
                BorderBrush = Application.Current.Resources.TryGetValue("LiquidGlassSpecularBorderBrush", out var rim) && rim is Brush rb ? rb : Res("StudioBorderBrush"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(10, 8, 10, 8),
                Child = card
            };
            Grid.SetColumn(glass, i);
            body.Children.Add(glass);
        }
    }

    /// <summary>План пакета повёрнутым: X корпуса → вниз, Z → вправо; оси, помещения, шахты, лестницы, стояки, подсветка замечаний.</summary>
    private void DrawRotated(BuildingLayoutPlan plan, FloorPackagePlan p, Canvas c, double s, double ox, double oy)
    {
        Point P(double x, double z) => new(ox + (z - plan.Z0) * s, oy + (x - plan.X0) * s);
        DrawAxes(plan, c, s, P, rotated: true, ox, oy);
        var hot = HotRooms(p.Id);
        foreach (var r in p.Rooms) DrawRoom(c, r, P, hot.Contains(r.Id), s >= 9);
        foreach (var st in p.Risers.GroupBy(r => (Math.Round(r.X, 1), Math.Round(r.Z, 1))).Select(g => g.First()))
            Dot(c, P(st.X, st.Z), 2.6, LayoutRooms.RiserColor(st.Kind));
        DrawRiserBreaks(c, p, P);
        // Подписи квартир (марка · тип · площадь) — в полосе комнат.
        if (s >= 8)
            foreach (var a in p.Apartments)
            {
                var at = P((a.X0 + a.X1) / 2, a.North ? a.Z0 + (a.Z1 - a.Z0) * 0.3 : a.Z1 - (a.Z1 - a.Z0) * 0.3);
                var label = Text($"{a.Mark} {LayoutRooms.ApartmentTitle(a.Type)}\n{a.TotalArea.ToString("0.0", Ru)}", 8.5);
                label.TextAlignment = TextAlignment.Center;
                label.Foreground = B("#1C1C1E");
                label.Measure(new Size(200, 60));
                Canvas.SetLeft(label, at.X - label.DesiredSize.Width / 2);
                Canvas.SetTop(label, at.Y - label.DesiredSize.Height / 2);
                c.Children.Add(label);
            }
    }

    private HashSet<string> HotRooms(string packageId) =>
        _report?.Findings.Where(f => f.Severity == StackSeverity.Critical && (f.UpperPackageId == packageId || f.LowerPackageId == packageId)).SelectMany(f => f.RoomIds).ToHashSet() ?? [];

    private void DrawRiserBreaks(Canvas c, FloorPackagePlan p, Func<double, double, Point> P)
    {
        if (_report is null) return;
        foreach (var f in _report.Findings.Where(f => f.Code is "MEP-RISER-BREAK" or "MEP-RISER-MAIN" && (f.UpperPackageId == p.Id || f.LowerPackageId == p.Id)))
        {
            var pt = P(f.X, f.Z);
            c.Children.Add(Placed(new Ellipse { Width = 12, Height = 12, Stroke = B("#EF4444"), StrokeThickness = 2 }, pt.X - 6, pt.Y - 6));
        }
    }

    private static void DrawAxes(BuildingLayoutPlan plan, Canvas c, double s, Func<double, double, Point> P, bool rotated, double ox, double oy)
    {
        var axisBrush = B("#0A84FF", 0x90);
        foreach (var (label, x) in plan.AxesX)
        {
            var a = P(x, plan.Z0 - 0.6);
            var b = P(x, plan.Z1 + 0.6);
            c.Children.Add(new Line { X1 = a.X, Y1 = a.Y, X2 = b.X, Y2 = b.Y, Stroke = axisBrush, StrokeThickness = 0.8, StrokeDashArray = [6, 3, 1, 3] });
            Bubble(c, rotated ? new Point(a.X - 10, a.Y) : new Point(a.X, a.Y - 10), label);
        }
        foreach (var (label, z) in plan.AxesZ)
        {
            var a = P(plan.X0 - 0.6, z);
            var b = P(plan.X1 + 0.6, z);
            c.Children.Add(new Line { X1 = a.X, Y1 = a.Y, X2 = b.X, Y2 = b.Y, Stroke = axisBrush, StrokeThickness = 0.8, StrokeDashArray = [6, 3, 1, 3] });
            Bubble(c, rotated ? new Point(a.X, a.Y - 10) : new Point(a.X - 10, a.Y), label);
        }
    }

    private static void Bubble(Canvas c, Point at, string label)
    {
        c.Children.Add(Placed(new Ellipse { Width = 15, Height = 15, Stroke = B("#0A84FF"), StrokeThickness = 1, Fill = Res("CardBrush") }, at.X - 7.5, at.Y - 7.5));
        var t = new TextBlock { Text = label, FontSize = 8.5, FontFamily = new FontFamily("Cascadia Mono, Consolas"), Foreground = B("#0A84FF") };
        t.Measure(new Size(40, 20));
        c.Children.Add(Placed(t, at.X - t.DesiredSize.Width / 2, at.Y - t.DesiredSize.Height / 2));
    }

    private static void DrawRoom(Canvas c, LayoutRoom r, Func<double, double, Point> P, bool hot, bool details)
    {
        var a = P(r.X0, r.Z0);
        var b = P(r.X1, r.Z1);
        double x = Math.Min(a.X, b.X), y = Math.Min(a.Y, b.Y), w = Math.Abs(b.X - a.X), h = Math.Abs(b.Y - a.Y);
        bool dark = r.Kind is LayoutRoomKind.LiftShaft;
        var rect = new Rectangle
        {
            Width = Math.Max(1, w), Height = Math.Max(1, h),
            Fill = B(LayoutRooms.ColorHex(r.Kind), dark ? (byte)0xE0 : (byte)0xB8),
            Stroke = hot ? B("#EF4444") : B("#1C1C1E", 0x70),
            StrokeThickness = hot ? 2.2 : 0.6
        };
        c.Children.Add(Placed(rect, x, y));
        if (r.Kind == LayoutRoomKind.LiftShaft)
        {
            c.Children.Add(new Line { X1 = x, Y1 = y, X2 = x + w, Y2 = y + h, Stroke = B("#F5F5F7", 0xB0), StrokeThickness = 0.8 });
            c.Children.Add(new Line { X1 = x + w, Y1 = y, X2 = x, Y2 = y + h, Stroke = B("#F5F5F7", 0xB0), StrokeThickness = 0.8 });
        }
        else if (r.Kind == LayoutRoomKind.Stair && details)
        {
            // Ступени марша — штриховка поперёк длинной стороны.
            bool alongY = h > w;
            int n = (int)((alongY ? h : w) / 5);
            for (int i = 1; i < n; i++)
            {
                double k = i / (double)n;
                c.Children.Add(alongY
                    ? new Line { X1 = x + 2, Y1 = y + h * k, X2 = x + w / 2 - 1, Y2 = y + h * k, Stroke = B("#1C1C1E", 0x80), StrokeThickness = 0.6 }
                    : new Line { X1 = x + w * k, Y1 = y + 2, X2 = x + w * k, Y2 = y + h / 2 - 1, Stroke = B("#1C1C1E", 0x80), StrokeThickness = 0.6 });
            }
            c.Children.Add(alongY
                ? new Line { X1 = x + w / 2, Y1 = y + 2, X2 = x + w / 2, Y2 = y + h - 2, Stroke = B("#1C1C1E", 0x80), StrokeThickness = 0.8 }
                : new Line { X1 = x + 2, Y1 = y + h / 2, X2 = x + w - 2, Y2 = y + h / 2, Stroke = B("#1C1C1E", 0x80), StrokeThickness = 0.8 });
        }
        else if (LayoutRooms.IsSanitary(r.Kind) && details)
        {
            // Мокрая зона — диагональная штриховка.
            for (double d = 4; d < w + h; d += 5)
            {
                double x1 = x + Math.Min(d, w), y1 = y + Math.Max(0, d - w), x2 = x + Math.Max(0, d - h), y2 = y + Math.Min(d, h);
                c.Children.Add(new Line { X1 = x1, Y1 = y1, X2 = x2, Y2 = y2, Stroke = B("#0369A1", 0x60), StrokeThickness = 0.5 });
            }
        }
    }

    private static void Dot(Canvas c, Point p, double r, string hex) =>
        c.Children.Add(Placed(new Ellipse { Width = r * 2, Height = r * 2, Fill = B(hex), Stroke = B("#FFFFFF", 0xC0), StrokeThickness = 0.6 }, p.X - r, p.Y - r));

    private static UIElement Placed(UIElement e, double x, double y)
    {
        Canvas.SetLeft(e, x);
        Canvas.SetTop(e, y);
        return e;
    }

    // ═════════════════════════════ Подвал и вертикальная проверка ═════════════════════════════

    private void BuildBasement(BuildingLayoutPlan plan, Grid body, double w, double h)
    {
        LastTiers = [];
        body.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1.15, GridUnitType.Star) });
        body.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(360) });
        double topH = (h - 12) * 1.15 / 2.15, botH = (h - 12) - topH;
        double planW = w - 372;
        // 1) Подвал: помещения, узлы ввода (стрелки снаружи), магистрали по видам сетей до каждого стояка.
        if (plan.Basement is { } bas)
        {
            double s = Math.Min((planW - 90) / (plan.Length + 6), (topH - 70) / (plan.Width + 8));
            double ox = 46, oy = 40 + 4 * s;
            Point P(double x, double z) => new(ox + (x - plan.X0) * s, oy + (z - plan.Z0) * s);
            var canvas = new Canvas { Width = plan.Length * s + 90, Height = plan.Width * s + 8 * s + 50 };
            DrawAxes(plan, canvas, s, P, rotated: false, ox, oy);
            foreach (var r in bas.Rooms) DrawRoom(canvas, r, P, false, true);
            foreach (var r in bas.Rooms.Where(r => r.Kind is LayoutRoomKind.Itp or LayoutRoomKind.WaterMeter or LayoutRoomKind.Electrical or LayoutRoomKind.Ventilation))
            {
                var a0 = P(r.X0, r.Z0);
                var a1 = P(r.X1, r.Z1);
                double rw = Math.Abs(a1.X - a0.X) - 6;
                // Узкое помещение — короткое наименование (полное — в доке «Инженерные стояки»).
                string label = rw >= 110 ? r.Name : r.Kind switch
                {
                    LayoutRoomKind.Electrical => "ВРУ",
                    LayoutRoomKind.WaterMeter => "Водомерный узел",
                    LayoutRoomKind.Ventilation => "Венткамера",
                    _ => r.Name
                };
                var t = Text(label, 9.5, strong: true, wrap: true);
                t.Foreground = B("#1C1C1E");
                t.TextAlignment = TextAlignment.Center;
                t.Width = Math.Max(30, rw);
                t.Measure(new Size(t.Width, 80));
                canvas.Children.Add(Placed(t, Math.Min(a0.X, a1.X) + 3, (a0.Y + a1.Y) / 2 - t.DesiredSize.Height / 2));
            }
            foreach (var m in plan.Mains)
            {
                var line = new Polyline { Stroke = B(LayoutRooms.RiserColor(m.Kind)), StrokeThickness = m.Kind == RiserKind.Sewer ? 2.2 : 1.6, StrokeLineJoin = PenLineJoin.Round };
                foreach (var (x, z) in m.Path) line.Points.Add(P(x, z));
                canvas.Children.Add(line);
                var end = m.Path[^1];
                Dot(canvas, P(end.X, end.Z), 3, LayoutRooms.RiserColor(m.Kind));
            }
            int k = 0;
            foreach (var i in plan.Inlets.Where(i => i.IntoBasement).OrderBy(i => i.X))
            {
                bool north = Math.Abs(i.Z - plan.Z0) < 0.01;
                var a = P(i.X, north ? i.Z - 3 : i.Z + 3);
                var b = P(i.X, i.Z);
                string hex = SiteBoundaryBuilder.StyleOf(i.Kind).ColorHex;
                canvas.Children.Add(new Line { X1 = a.X, Y1 = a.Y, X2 = b.X, Y2 = b.Y, Stroke = B(hex), StrokeThickness = 2.4 });
                canvas.Children.Add(new Polygon { Points = { new Point(b.X - 5, b.Y + (north ? -8 : 8)), new Point(b.X + 5, b.Y + (north ? -8 : 8)), b }, Fill = B(hex) });
                // Подпись — марка сети (полное наименование — в доке), соседние подписи разнесены по высоте.
                var t = Text(InletMark(i.Kind), 10, strong: true);
                t.Foreground = B(hex);
                t.Measure(new Size(120, 30));
                double dy = (k++ % 2) * 13;
                canvas.Children.Add(Placed(t, a.X - t.DesiredSize.Width / 2, north ? a.Y - t.DesiredSize.Height - 2 - dy : a.Y + 2 + dy));
            }
            var card = Card("Подвал (−1 этаж): ИТП, водомерный узел и насосная, ВРУ, венткамера · узлы ввода и магистрали к стоякам", canvas);
            body.Children.Add(card);
        }
        // 2) Наложение: нижний этаж пакета (заливка) и мокрые / газовые зоны верхнего (контуры), конфликты — красным.
        var floors = plan.Floors.ToList();
        var pair = PairForCheck(floors);
        if (pair is var (lower, upper))
        {
            double s = Math.Min((planW - 90) / (plan.Length + 6), (botH - 60) / (plan.Width + 4));
            double ox = 46, oy = 34;
            Point P(double x, double z) => new(ox + (x - plan.X0) * s, oy + (z - plan.Z0) * s);
            var canvas = new Canvas { Width = plan.Length * s + 90, Height = plan.Width * s + 50 };
            DrawAxes(plan, canvas, s, P, rotated: false, ox, oy);
            var hot = _report?.Findings.Where(f => f.Severity == StackSeverity.Critical && f.UpperPackageId == upper.Id).SelectMany(f => f.RoomIds).ToHashSet() ?? [];
            foreach (var r in lower.Rooms)
            {
                var a = P(r.X0, r.Z0);
                var b = P(r.X1, r.Z1);
                canvas.Children.Add(Placed(new Rectangle
                {
                    Width = Math.Max(1, b.X - a.X), Height = Math.Max(1, b.Y - a.Y),
                    Fill = B(LayoutRooms.ColorHex(r.Kind), 0x70), Stroke = hot.Contains(r.Id) ? B("#EF4444") : B("#1C1C1E", 0x40), StrokeThickness = hot.Contains(r.Id) ? 2 : 0.5
                }, a.X, a.Y));
            }
            foreach (var r in upper.Rooms.Where(r => LayoutRooms.IsWet(r.Kind)))
            {
                var a = P(r.X0, r.Z0);
                var b = P(r.X1, r.Z1);
                bool conflict = hot.Contains(r.Id);
                canvas.Children.Add(Placed(new Rectangle
                {
                    Width = Math.Max(1, b.X - a.X), Height = Math.Max(1, b.Y - a.Y),
                    Fill = B(conflict ? "#EF4444" : LayoutRooms.IsGas(r.Kind) ? "#F59E0B" : "#0EA5E9", 0x38),
                    Stroke = B(conflict ? "#EF4444" : LayoutRooms.IsGas(r.Kind) ? "#F59E0B" : "#0369A1"),
                    StrokeThickness = conflict ? 2.4 : 1.2,
                    StrokeDashArray = [4, 2]
                }, a.X, a.Y));
            }
            foreach (var st in upper.Risers) Dot(canvas, P(st.X, st.Z), 2.6, LayoutRooms.RiserColor(st.Kind));
            DrawRiserBreaks(canvas, upper, P);
            var card = Card(string.Create(Ru, $"Наложение: {lower.ToLevel}-й этаж (помещения) и {upper.FromLevel}-й этаж (мокрые и газовые зоны — контуром)"), canvas);
            Grid.SetRow(card, 1);
            body.Children.Add(card);
        }
        // 3) Справа — итог проверки.
        var list = new StackPanel { Spacing = 8 };
        list.Children.Add(Text("Проверка вертикальной совместимости", 13, strong: true));
        list.Children.Add(Text("СП 54.13330.2016 п. 9.22 · ПУЭ п. 7.1.29 · СП 30.13330 · СП 62.13330", 10.5, secondary: true, wrap: true));
        if (_report is { } rep)
        {
            var verdict = Text(rep.Passed ? "Без нарушений" : string.Create(Ru, $"Нарушений: {rep.Critical}"), 12.5, strong: true);
            verdict.Foreground = B(rep.Passed ? "#10B981" : "#EF4444");
            list.Children.Add(verdict);
            foreach (var (f, count) in Grouped(rep.Findings.OrderBy(f => f.Severity == StackSeverity.Critical ? 0 : 1)).Take(9))
            {
                var row = new Grid { ColumnSpacing = 8 };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.Children.Add(Glyph(f.Severity == StackSeverity.Critical ? "\uE7BA" : "\uE73E", 12, f.Severity == StackSeverity.Critical ? "TextPrimaryBrush" : "AccentBrush"));
                var tx = new StackPanel { Spacing = 1 };
                var title = Text(f.Title + (count > 1 ? string.Create(Ru, $" · квартир {count}") : string.Empty), 11.5, strong: true, wrap: true);
                if (f.Severity == StackSeverity.Critical) title.Foreground = B("#EF4444");
                tx.Children.Add(title);
                tx.Children.Add(Text(f.Detail + (count > 1 ? " …" : string.Empty), 10.5, secondary: true, wrap: true));
                tx.Children.Add(Text(f.Norm, 10, secondary: true, wrap: true));
                Grid.SetColumn(tx, 1);
                row.Children.Add(tx);
                list.Children.Add(row);
            }
        }
        var side = Card(null, new ScrollViewer { Content = list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        Grid.SetColumn(side, 1);
        Grid.SetRowSpan(side, 2);
        body.Children.Add(side);
    }

    private static string InletMark(GpzuZoneKind k) => k switch
    {
        GpzuZoneKind.WaterSupply => "В1 ввод",
        GpzuZoneKind.HeatNetwork => "Т1/Т2 ввод",
        GpzuZoneKind.PowerCable => "ЭС ввод",
        GpzuZoneKind.Communication => "СС ввод",
        GpzuZoneKind.Sewer => "К1 выпуск",
        _ => "Г ввод"
    };

    /// <summary>Однотипные замечания (одна формулировка, разные квартиры) — одной строкой с числом квартир.</summary>
    public static IEnumerable<(StackFinding First, int Count)> Grouped(IEnumerable<StackFinding> findings) =>
        findings.GroupBy(f => (f.Code, f.Title)).Select(g => (g.First(), g.Count()));

    /// <summary>Пара этажей для наложения: граница с нарушениями, иначе граница первого и второго пакетов (5 / 6 этаж).</summary>
    private (FloorPackagePlan Lower, FloorPackagePlan Upper)? PairForCheck(List<FloorPackagePlan> floors)
    {
        if (floors.Count < 2) return null;
        if (_report?.Findings.FirstOrDefault(f => f.Severity == StackSeverity.Critical && f.UpperPackageId is not null && f.LowerPackageId is not null &&
                                                   floors.Any(p => p.Id == f.LowerPackageId)) is { } hit)
        {
            var u = floors.First(p => p.Id == hit.UpperPackageId);
            var l = floors.First(p => p.Id == hit.LowerPackageId);
            return (l, u);
        }
        return (floors[0], floors[1]);
    }

    private static Border Card(string? title, UIElement content)
    {
        var grid = new Grid { RowSpacing = 6 };
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        if (title is not null) grid.Children.Add(Text(title, 12, strong: true, wrap: true));
        if (content is FrameworkElement fe) fe.HorizontalAlignment = HorizontalAlignment.Left;
        Grid.SetRow((FrameworkElement)content, 1);
        grid.Children.Add(content);
        return new Border
        {
            Background = Res("CardBrush"),
            BorderBrush = Application.Current.Resources.TryGetValue("LiquidGlassSpecularBorderBrush", out var rim) && rim is Brush rb ? rb : Res("StudioBorderBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(12, 10, 12, 10),
            Child = grid
        };
    }
}
