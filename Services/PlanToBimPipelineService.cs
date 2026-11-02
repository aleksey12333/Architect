using System.Globalization;
using AIArchitectStudio.Models;
using AIArchitectStudio.Models.Geometry;
using AIArchitectStudio.Rendering;

namespace AIArchitectStudio.Services;

/// <summary>Откуда получен план: векторные отрезки (DXF, PDF, ответ модели), растровое изображение или страница PDF.</summary>
public enum PlanSourceKind
{
    Vector,
    Raster,
    Pdf,
    Dxf
}

public enum PlanOpeningKind
{
    Window,
    Door
}

/// <summary>Стена плана: ось (X, Y — координаты листа, м, Y вверх) и толщина. Нормализация правит отрезок на месте.</summary>
public sealed class PlanWall
{
    public double X1 { get; set; }
    public double Y1 { get; set; }
    public double X2 { get; set; }
    public double Y2 { get; set; }
    public double Thickness { get; set; }

    /// <summary>Наружная стена (с одной стороны — улица); вычисляется нормализацией.</summary>
    public bool IsExterior { get; set; }

    public PlanWall() { }

    public PlanWall(double x1, double y1, double x2, double y2, double thickness)
    {
        (X1, Y1, X2, Y2, Thickness) = (x1, y1, x2, y2, thickness);
    }

    public double Length => Math.Sqrt((X2 - X1) * (X2 - X1) + (Y2 - Y1) * (Y2 - Y1));

    /// <summary>Ось вдоль X листа (точно горизонтальная после нормализации).</summary>
    public bool IsHorizontal => Math.Abs(Y2 - Y1) < 1e-9 && Math.Abs(X2 - X1) > 1e-9;

    public bool IsVertical => Math.Abs(X2 - X1) < 1e-9 && Math.Abs(Y2 - Y1) > 1e-9;

    public bool IsOrthogonal => IsHorizontal || IsVertical;

    /// <summary>Перегородка (не несущая): тоньше 150 мм.</summary>
    public bool IsPartition => Thickness < PlanToBimPipelineService.PartitionThickness;

    /// <summary>Угол оси к оси X листа, градусы (−180…180].</summary>
    public double AngleDeg => Math.Atan2(Y2 - Y1, X2 - X1) * 180 / Math.PI;

    public PlanWall Clone() => new(X1, Y1, X2, Y2, Thickness) { IsExterior = IsExterior };

    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"({X1:0.###};{Y1:0.###})–({X2:0.###};{Y2:0.###}) t={Thickness:0.###}");
}

/// <summary>Проём плана: центр (м, координаты листа), ширина и вид.</summary>
public sealed record PlanOpening(double X, double Y, double Width, PlanOpeningKind Kind);

/// <summary>Распознанный план: стены, проёмы и масштаб (для растра — мм на пиксель и как он определён).</summary>
public sealed class RecognizedPlan
{
    public string Name { get; init; } = "План";
    public PlanSourceKind Source { get; init; }
    public List<PlanWall> Walls { get; init; } = [];
    public List<PlanOpening> Openings { get; init; } = [];

    /// <summary>Масштаб растра, мм/пиксель (0 — векторный план в метрах).</summary>
    public double MmPerPixel { get; set; }

    /// <summary>Как определён масштаб: «по калибровочному размеру 12 000 мм», «по толщине наружной стены» и т.п.</summary>
    public string ScaleSource { get; set; } = string.Empty;

    public int PixelWidth { get; init; }
    public int PixelHeight { get; init; }

    /// <summary>Исходное изображение плана (JPEG/PNG) — миниатюра окна проверки и вложение запроса.</summary>
    public byte[]? Image { get; init; }

    public string? ImageMime { get; init; }

    public List<string> Notes { get; } = [];

    public RecognizedPlan Clone() => new()
    {
        Name = Name, Source = Source, Walls = Walls.Select(w => w.Clone()).ToList(), Openings = [.. Openings], MmPerPixel = MmPerPixel,
        ScaleSource = ScaleSource, PixelWidth = PixelWidth, PixelHeight = PixelHeight, Image = Image, ImageMime = ImageMime
    };
}

/// <summary>Параметры сборки здания по плану (подтверждаются в окне проверки распознанного плана).</summary>
public sealed record PlanToBimOptions
{
    public string GroupId { get; init; } = "Здание по плану";
    public int Storeys { get; init; } = 1;

    /// <summary>Высота этажа (от пола до пола), м.</summary>
    public double FloorHeight { get; init; } = 3.0;

    public double SlabThickness { get; init; } = 0.2;
    public double FoundationThickness { get; init; } = 0.5;
    public double ColumnSize { get; init; } = 0.4;

    /// <summary>Наибольший пролёт перекрытия без промежуточной опоры, м (больше — сетка колонн).</summary>
    public double MaxSpan { get; init; } = 6.0;

    /// <summary>Допуск ортогонализации, градусы: отклонение меньше — стена выпрямляется до 0°/90°.</summary>
    public double OrthoToleranceDeg { get; init; } = 3.0;

    /// <summary>Допуск замыкания углов, м: незамкнутый узел ближе — стена дотягивается до оси соседней.</summary>
    public double CornerTolerance { get; init; } = 0.25;

    public double WindowSill { get; init; } = 0.9;
    public double WindowHeight { get; init; } = 1.5;
    public double DoorHeight { get; init; } = 2.1;

    /// <summary>Подбирать сечения по результатам КЭ-расчёта до K_исп ≤ 0.95.</summary>
    public bool AutoSize { get; init; } = true;
}

/// <summary>Что сделала геометрическая постобработка плана.</summary>
public sealed class PlanNormalizationStats
{
    public int InputWalls { get; set; }
    public int Orthogonalized { get; set; }
    public double MaxCorrectionDeg { get; set; }
    public int AxesAligned { get; set; }
    public int DuplicatesMerged { get; set; }
    public int CornersClosed { get; set; }
    public double MaxGapClosedMm { get; set; }
    public int OpeningsBridged { get; set; }
    public int NonOrthogonal { get; set; }
}

/// <summary>Итог реконструкции: элементы, оси, сводка распознанного и конструктивная целостность.</summary>
public sealed class PlanToBimResult
{
    public RecognizedPlan Plan { get; init; } = new();
    public List<PlanWall> Walls { get; init; } = [];
    public List<ArchitecturalElement> Elements { get; } = [];
    public List<BuildingGroup> Groups { get; } = [];
    public List<StructuralGridAxis> Axes { get; } = [];
    public PlanNormalizationStats Normalization { get; init; } = new();

    /// <summary>Наружный контур (м, координаты листа, против часовой) — фундамент и перекрытия.</summary>
    public List<(double X, double Y)> Footprint { get; init; } = [];

    public bool IsClosedContour { get; init; }
    public double FootprintArea { get; init; }
    public double WidthM { get; init; }
    public double DepthM { get; init; }

    public int ExteriorWalls => Walls.Count(w => w.IsExterior);
    public int InteriorWalls => Walls.Count(w => !w.IsExterior);
    public int Windows { get; set; }
    public int Doors { get; set; }
    public int Columns { get; set; }
    public int Slabs { get; set; }
    public int Storeys { get; set; }
    public double SlabThickness { get; set; }
    public double ColumnSize { get; set; }
    public double FoundationThickness { get; set; }

    /// <summary>Стен, утолщённых подбором (перегородки под перекрытием, перегруженные несущие стены).</summary>
    public int WallsThickened { get; set; }

    public double MaxUtilization { get; set; }
    public string GoverningElement { get; set; } = string.Empty;
    public int PhysicsErrors { get; set; }
    public int SizingPasses { get; set; }
    public double ConcreteM3 { get; set; }
    public double RebarKg { get; set; }

    /// <summary>Несущих монолитных элементов со схемой армирования (колонны, плиты, фундамент).</summary>
    public int RebarCages { get; set; }

    public int RebarCandidates { get; set; }
    public double ElapsedMs { get; set; }
    public List<string> Log { get; } = [];

    /// <summary>Конструктивная целостность: опоры у всех элементов, K_исп ≤ 1.0, армирование и замкнутый контур.</summary>
    public bool IsStructurallySound => PhysicsErrors == 0 && MaxUtilization <= 1.0 && RebarCages == RebarCandidates && Elements.Count > 0;
}

/// <summary>
/// Инженерный конвейер «План → BIM»: детерминированная геометрическая постобработка распознанного плана (ортогонализация
/// стен с отклонением меньше 3°, выравнивание осей, слияние дублей, перекрытие разрывов у проёмов, замыкание узлов до 250 мм,
/// разделка L- и T-узлов по граням), конструктивная триада (фундаментная плита под наружным контуром, плиты перекрытий
/// и покрытие, колонны в узлах капитальных осей при пролётах больше 6 м, проёмы в стенах-хостах) и проверка на выходе —
/// расчётная схема, экспресс-расчёт, армирование и аудит опор с подбором толщины плит, сечения колонн и фундамента.
/// Чистая функция: сцена не меняется, результат вставляется вызывающим кодом одним шагом Undo.
/// </summary>
public static class PlanToBimPipelineService
{
    public const double PartitionThickness = 0.15;
    private const double Eps = 1e-6;
    private static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");

    /// <summary>Полный прогон: нормализация, сборка, расчёт и подбор сечений.</summary>
    public static PlanToBimResult Run(RecognizedPlan plan, PlanToBimOptions? options = null, StructuralLoadSettings? loads = null)
    {
        options ??= new PlanToBimOptions();
        loads ??= new StructuralLoadSettings();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var stats = new PlanNormalizationStats { InputWalls = plan.Walls.Count };
        var walls = Normalize(plan.Walls, plan.Openings, options, stats);
        var outline = Footprint(walls);
        var footprint = outline.Polygon;
        double minX = walls.Count == 0 ? 0 : walls.Min(w => Math.Min(w.X1, w.X2) - (w.IsVertical ? w.Thickness / 2 : 0));
        double maxX = walls.Count == 0 ? 0 : walls.Max(w => Math.Max(w.X1, w.X2) + (w.IsVertical ? w.Thickness / 2 : 0));
        double minY = walls.Count == 0 ? 0 : walls.Min(w => Math.Min(w.Y1, w.Y2) - (w.IsHorizontal ? w.Thickness / 2 : 0));
        double maxY = walls.Count == 0 ? 0 : walls.Max(w => Math.Max(w.Y1, w.Y2) + (w.IsHorizontal ? w.Thickness / 2 : 0));
        if (footprint.Count < 3 && walls.Count > 0) footprint = [(minX, minY), (maxX, minY), (maxX, maxY), (minX, maxY)];

        var result = new PlanToBimResult
        {
            Plan = plan, Walls = walls, Normalization = stats, Footprint = footprint, IsClosedContour = outline.Closed,
            FootprintArea = Math.Abs(SignedArea(footprint)), WidthM = maxX - minX, DepthM = maxY - minY, Storeys = Math.Max(1, options.Storeys)
        };
        if (walls.Count == 0)
        {
            result.Log.Add("На плане не найдено стен — здание не собрано");
            return result;
        }
        if (!outline.Closed) result.Log.Add("Наружный контур не замкнут — фундамент и перекрытия построены по габариту стен");

        var grid = ColumnGrid(walls, footprint, options);
        var sizing = (Slab: options.SlabThickness, Column: options.ColumnSize, Foundation: options.FoundationThickness);
        // Утолщение перегруженных стен (перегородка под перекрытием работает как опора и при перегрузке становится несущей стеной).
        var wallBoost = new Dictionary<PlanWall, double>(ReferenceEqualityComparer.Instance);
        var center = ((minX + maxX) / 2, (minY + maxY) / 2);
        StructuralAnalysisResult analysis = StructuralAnalysisResult.Empty;
        List<ArchitecturalElement> elements = [];
        BuildingGroup group = new(options.GroupId, "residential", "#34C759");
        for (int pass = 0; pass < 8; pass++)
        {
            elements = Assemble(result, grid, options, sizing, center, wallBoost, out var axes, out var wallOf);
            result.Axes.Clear();
            result.Axes.AddRange(axes);
            analysis = StructuralAnalysisService.Analyze(elements, [group], loads);
            result.SizingPasses = pass + 1;
            if (!options.AutoSize) break;
            // Подбор: несущие элементы с K_исп > 0.95 получают следующую ступень сечения (плиты +20 мм, колонны +100 мм,
            // фундамент +100 мм, стены и перегородки +50 мм).
            bool slab = false, column = false, foundation = false, wall = false;
            foreach (var e in elements)
                if (analysis.For(e.Id) is { Utilization: > DesignTarget })
                {
                    slab |= e.Category is BimCategory.Slab or BimCategory.Roof;
                    column |= e.Category == BimCategory.Column;
                    foundation |= e.Category == BimCategory.Foundation;
                    if (e.Category == BimCategory.Wall && wallOf.TryGetValue(e.Id, out var pw) && pw.Thickness + wallBoost.GetValueOrDefault(pw) < 0.4 - Eps)
                    {
                        wallBoost[pw] = wallBoost.GetValueOrDefault(pw) + 0.05;
                        wall = true;
                    }
                }
            var next = (Slab: slab ? Math.Min(0.4, sizing.Slab + 0.02) : sizing.Slab, Column: column ? Math.Min(0.8, sizing.Column + 0.1) : sizing.Column,
                Foundation: foundation ? Math.Min(1.5, sizing.Foundation + 0.1) : sizing.Foundation);
            if (next == sizing && !wall) break;
            result.Log.Add(string.Create(Ru, $"Подбор сечений, проход {pass + 1}: K_исп max {analysis.MaxUtilization:0.00} → плиты {next.Slab * 1000:0} мм, колонны {next.Column * 1000:0} мм, " +
                                             $"фундамент {next.Foundation * 1000:0} мм{(wall ? $", утолщено стен {wallBoost.Count}" : string.Empty)}"));
            sizing = next;
        }
        result.WallsThickened = wallBoost.Count;

        result.Elements.AddRange(elements);
        result.Groups.Add(group);
        result.SlabThickness = sizing.Slab;
        result.ColumnSize = sizing.Column;
        result.FoundationThickness = sizing.Foundation;
        result.Windows = elements.Count(e => e.Category == BimCategory.Window);
        result.Doors = elements.Count(e => e.Category == BimCategory.Door);
        result.Columns = elements.Count(e => e.Category == BimCategory.Column);
        result.Slabs = elements.Count(e => e.Category is BimCategory.Slab or BimCategory.Roof);
        result.MaxUtilization = analysis.MaxUtilization;
        if (analysis.Elements.Count > 0)
        {
            var worst = analysis.Elements.MaxBy(p => p.Value.Utilization);
            result.GoverningElement = elements.FirstOrDefault(e => e.Id == worst.Key)?.DisplayTitle ?? string.Empty;
        }

        // Аудит опор на копиях (флаги HasPhysicsError не попадают в выдаваемые элементы).
        var audit = new SolverReport();
        StructuralAuditor.Audit(elements.Select(e => e.Clone()).ToList(), audit);
        result.PhysicsErrors = audit.PhysicsErrors;
        foreach (var error in audit.Errors) result.Log.Add("Аудит: " + error);

        // Армирование: ведомость стали по K_исп и схема каркаса у каждого монолитного несущего элемента.
        var punching = BimAnalyticsService.PunchingReinforcedIds(analysis);
        var schedule = QuantityScheduleService.Calculate(elements, analysis.UtilizationById, punching, loads.Rebar);
        result.ConcreteM3 = schedule.ConcreteTotal;
        result.RebarKg = schedule.Rebar.Total;
        foreach (var e in elements)
        {
            if (!RebarCageBuilder.Supports(e)) continue;
            result.RebarCandidates++;
            var half = new System.Numerics.Vector3((float)(e.Width / 2), (float)(e.Height / 2), (float)(e.Depth / 2));
            var cage = RebarCageBuilder.Build(e, new BimBounds(-half, half), analysis.UtilizationById.GetValueOrDefault(e.Id), punching.Contains(e.Id));
            if (cage is { Working.Count: > 0 }) result.RebarCages++;
        }
        sw.Stop();
        result.ElapsedMs = sw.Elapsed.TotalMilliseconds;
        result.Log.Insert(0, string.Create(Ru,
            $"Нормализация: выпрямлено {stats.Orthogonalized} (до {stats.MaxCorrectionDeg:0.0}°), осей выровнено {stats.AxesAligned}, дублей слито {stats.DuplicatesMerged}, " +
            $"разрывов у проёмов {stats.OpeningsBridged}, узлов замкнуто {stats.CornersClosed} (до {stats.MaxGapClosedMm:0} мм)"));
        return result;
    }

    private const double DesignTarget = 0.95;

    // ───────────────────────────── 1. Геометрическая постобработка ─────────────────────────────

    /// <summary>Snap &amp; Ortho: ортогонализация, оси, дубли, разрывы у проёмов, замыкание узлов, разделка L/T-узлов, наружные стены.</summary>
    public static List<PlanWall> Normalize(IReadOnlyList<PlanWall> input, IReadOnlyList<PlanOpening> openings, PlanToBimOptions options, PlanNormalizationStats stats)
    {
        var walls = input.Where(w => w.Length > 0.05 && w.Thickness > 0.01).Select(w => w.Clone()).ToList();
        double tol = options.OrthoToleranceDeg;
        foreach (var w in walls)
        {
            double a = w.AngleDeg;
            double toH = Math.IEEERemainder(a, 180), toV = Math.IEEERemainder(a - 90, 180);
            double cx = (w.X1 + w.X2) / 2, cy = (w.Y1 + w.Y2) / 2, half = w.Length / 2;
            if (Math.Abs(toH) < tol)
            {
                if (Math.Abs(toH) > 1e-9) Corrected(stats, toH);
                (w.X1, w.Y1, w.X2, w.Y2) = (cx - half, cy, cx + half, cy);
            }
            else if (Math.Abs(toV) < tol)
            {
                if (Math.Abs(toV) > 1e-9) Corrected(stats, toV);
                (w.X1, w.Y1, w.X2, w.Y2) = (cx, cy - half, cx, cy + half);
            }
            else stats.NonOrthogonal++;
        }

        AlignAxes(walls, stats);
        MergeDuplicates(walls, stats);
        BridgeGaps(walls, openings, options.CornerTolerance, stats);
        MergeDuplicates(walls, stats);
        CloseCorners(walls, options.CornerTolerance, stats);
        TrimJoints(walls);
        ClassifyExterior(walls);
        return walls;
    }

    private static void Corrected(PlanNormalizationStats stats, double deviation)
    {
        stats.Orthogonalized++;
        stats.MaxCorrectionDeg = Math.Max(stats.MaxCorrectionDeg, Math.Abs(deviation));
    }

    /// <summary>Оси параллельных стен ближе 30 мм приводятся к общей линии (средневзвешенной по длине).</summary>
    private static void AlignAxes(List<PlanWall> walls, PlanNormalizationStats stats)
    {
        foreach (bool horizontal in new[] { true, false })
        {
            var list = walls.Where(w => horizontal ? w.IsHorizontal : w.IsVertical).OrderBy(w => horizontal ? w.Y1 : w.X1).ToList();
            int i = 0;
            while (i < list.Count)
            {
                int j = i + 1;
                double Coord(PlanWall w) => horizontal ? w.Y1 : w.X1;
                while (j < list.Count && Coord(list[j]) - Coord(list[j - 1]) <= 0.03) j++;
                if (j - i > 1)
                {
                    double sum = 0, weight = 0;
                    for (int k = i; k < j; k++) { sum += Coord(list[k]) * list[k].Length; weight += list[k].Length; }
                    double axis = Math.Round(sum / weight, 6);
                    for (int k = i; k < j; k++)
                    {
                        if (Math.Abs(Coord(list[k]) - axis) > Eps) stats.AxesAligned++;
                        if (horizontal) list[k].Y1 = list[k].Y2 = axis;
                        else list[k].X1 = list[k].X2 = axis;
                    }
                }
                i = j;
            }
        }
    }

    /// <summary>Наложенные и перекрывающиеся стены одной оси сливаются в одну (габарит — объединение, толщина — наибольшая).</summary>
    private static void MergeDuplicates(List<PlanWall> walls, PlanNormalizationStats stats)
    {
        bool changed = true;
        while (changed)
        {
            changed = false;
            for (int i = 0; i < walls.Count && !changed; i++)
                for (int j = i + 1; j < walls.Count && !changed; j++)
                {
                    var (a, b) = (walls[i], walls[j]);
                    if (!SameLine(a, b, Math.Max(a.Thickness, b.Thickness) / 2)) continue;
                    var (a0, a1) = Span(a);
                    var (b0, b1) = Span(b);
                    if (a0 > b1 + 0.01 || b0 > a1 + 0.01) continue;
                    var keep = a.Length >= b.Length ? a : b;
                    SetSpan(keep, Math.Min(a0, b0), Math.Max(a1, b1));
                    keep.Thickness = Math.Max(a.Thickness, b.Thickness);
                    walls.Remove(ReferenceEquals(keep, a) ? b : a);
                    stats.DuplicatesMerged++;
                    changed = true;
                }
        }
    }

    /// <summary>Соосные стены, разделённые проёмом (растровый план) или разрывом до допуска, соединяются в одну стену-хост.</summary>
    private static void BridgeGaps(List<PlanWall> walls, IReadOnlyList<PlanOpening> openings, double tolerance, PlanNormalizationStats stats)
    {
        bool changed = true;
        while (changed)
        {
            changed = false;
            for (int i = 0; i < walls.Count && !changed; i++)
                for (int j = 0; j < walls.Count && !changed; j++)
                {
                    if (i == j) continue;
                    var (a, b) = (walls[i], walls[j]);
                    if (!SameLine(a, b, 0.05)) continue;
                    var (a0, a1) = Span(a);
                    var (b0, b1) = Span(b);
                    double gap = b0 - a1;
                    if (gap <= 0.01 || gap > 3.5) continue;
                    bool opening = openings.Any(o => a.IsHorizontal
                        ? Math.Abs(o.Y - a.Y1) <= Math.Max(a.Thickness, 0.3) && o.X >= a1 - 0.05 && o.X <= b0 + 0.05
                        : Math.Abs(o.X - a.X1) <= Math.Max(a.Thickness, 0.3) && o.Y >= a1 - 0.05 && o.Y <= b0 + 0.05);
                    if (!opening && gap > tolerance) continue;
                    SetSpan(a, a0, b1);
                    a.Thickness = Math.Max(a.Thickness, b.Thickness);
                    walls.RemoveAt(j);
                    if (opening) stats.OpeningsBridged++;
                    else
                    {
                        stats.CornersClosed++;
                        stats.MaxGapClosedMm = Math.Max(stats.MaxGapClosedMm, gap * 1000);
                    }
                    changed = true;
                }
        }
    }

    /// <summary>
    /// Замыкание узлов: конец стены ближе допуска (до грани соседней перпендикулярной стены) переносится на её ось, соседняя
    /// при необходимости дотягивается до оси первой — все узлы становятся «ось в ось».
    /// </summary>
    private static void CloseCorners(List<PlanWall> walls, double tolerance, PlanNormalizationStats stats)
    {
        for (int pass = 0; pass < 2; pass++)
            foreach (var a in walls.Where(w => w.IsOrthogonal))
                foreach (bool end in new[] { false, true })
                {
                    double px = end ? a.X2 : a.X1, py = end ? a.Y2 : a.Y1;
                    PlanWall? best = null;
                    double bestScore = double.MaxValue;
                    foreach (var b in walls)
                    {
                        if (ReferenceEquals(a, b) || !b.IsOrthogonal || a.IsHorizontal == b.IsHorizontal) continue;
                        // Расстояние от конца до оси b (вдоль a) и выход за габарит b (поперёк).
                        double along = a.IsHorizontal ? Math.Abs(b.X1 - px) : Math.Abs(b.Y1 - py);
                        var (b0, b1) = Span(b);
                        double c = a.IsHorizontal ? py : px;
                        double outside = Math.Max(0, Math.Max(b0 - c, c - b1));
                        if (along > tolerance + b.Thickness / 2 || outside > tolerance + a.Thickness / 2) continue;
                        double score = along + outside;
                        if (score < bestScore) (best, bestScore) = (b, score);
                    }
                    if (best is null) continue;
                    double gap = Math.Max(0, (a.IsHorizontal ? Math.Abs(best.X1 - px) : Math.Abs(best.Y1 - py)) - best.Thickness / 2);
                    var (s0, s1) = Span(best);
                    double cross = a.IsHorizontal ? py : px;
                    double extend = Math.Max(0, Math.Max(s0 - cross, cross - s1));
                    if (a.IsHorizontal) { if (end) a.X2 = best.X1; else a.X1 = best.X1; }
                    else { if (end) a.Y2 = best.Y1; else a.Y1 = best.Y1; }
                    if (extend > Eps) SetSpan(best, Math.Min(s0, cross), Math.Max(s1, cross));
                    double closed = Math.Max(gap, extend - a.Thickness / 2);
                    if (closed > 0.001)
                    {
                        stats.CornersClosed++;
                        stats.MaxGapClosedMm = Math.Max(stats.MaxGapClosedMm, closed * 1000);
                    }
                }
        foreach (var w in walls) Canonical(w);
    }

    /// <summary>
    /// Разделка узлов «ось в ось» по граням: стена вдоль X в угловом (L) узле проходит до наружной грани поперечной, в T-узле —
    /// до её ближней грани; стена вдоль Y всегда заканчивается у грани стены вдоль X. Наложений тел нет, наружный угол закрыт.
    /// </summary>
    private static void TrimJoints(List<PlanWall> walls)
    {
        var snapshot = walls.Select(w => w.Clone()).ToList();
        for (int i = 0; i < walls.Count; i++)
        {
            var w = walls[i];
            if (!w.IsOrthogonal) continue;
            foreach (bool end in new[] { false, true })
            {
                double px = end ? snapshot[i].X2 : snapshot[i].X1, py = end ? snapshot[i].Y2 : snapshot[i].Y1;
                double dir = end ? 1 : -1;
                for (int j = 0; j < snapshot.Count; j++)
                {
                    var o = snapshot[j];
                    if (i == j || !o.IsOrthogonal || o.IsHorizontal == w.IsHorizontal) continue;
                    double axis = w.IsHorizontal ? o.X1 : o.Y1;
                    if (Math.Abs(axis - (w.IsHorizontal ? px : py)) > 1e-6) continue;
                    var (o0, o1) = Span(o);
                    double c = w.IsHorizontal ? py : px;
                    if (c < o0 - 1e-6 || c > o1 + 1e-6) continue;
                    bool tee = c > o0 + 1e-3 && c < o1 - 1e-3;
                    double shift = w.IsHorizontal && !tee ? o.Thickness / 2 : -o.Thickness / 2;
                    if (w.IsHorizontal) { if (end) w.X2 = px + dir * shift; else w.X1 = px + dir * shift; }
                    else { if (end) w.Y2 = py + dir * shift; else w.Y1 = py + dir * shift; }
                    break;
                }
            }
        }
        walls.RemoveAll(w => w.Length < 0.05);
    }

    private static void ClassifyExterior(List<PlanWall> walls)
    {
        var grid = new OccupancyGrid(walls);
        foreach (var w in walls)
        {
            w.IsExterior = false;
            double nx = -(w.Y2 - w.Y1) / Math.Max(Eps, w.Length), ny = (w.X2 - w.X1) / Math.Max(Eps, w.Length);
            foreach (double t in new[] { 0.2, 0.5, 0.8 })
            {
                double x = w.X1 + (w.X2 - w.X1) * t, y = w.Y1 + (w.Y2 - w.Y1) * t, off = w.Thickness / 2 + 0.05;
                if (grid.IsOutside(x + nx * off, y + ny * off) || grid.IsOutside(x - nx * off, y - ny * off))
                {
                    w.IsExterior = true;
                    break;
                }
            }
        }
    }

    private static bool SameLine(PlanWall a, PlanWall b, double tolerance) =>
        a.IsHorizontal && b.IsHorizontal ? Math.Abs(a.Y1 - b.Y1) <= tolerance + Eps
        : a.IsVertical && b.IsVertical && Math.Abs(a.X1 - b.X1) <= tolerance + Eps;

    private static (double Min, double Max) Span(PlanWall w) =>
        w.IsHorizontal ? (Math.Min(w.X1, w.X2), Math.Max(w.X1, w.X2)) : (Math.Min(w.Y1, w.Y2), Math.Max(w.Y1, w.Y2));

    private static void SetSpan(PlanWall w, double min, double max)
    {
        if (w.IsHorizontal) (w.X1, w.X2) = (min, max);
        else (w.Y1, w.Y2) = (min, max);
    }

    private static void Canonical(PlanWall w)
    {
        if (w.IsHorizontal && w.X1 > w.X2) (w.X1, w.X2) = (w.X2, w.X1);
        if (w.IsVertical && w.Y1 > w.Y2) (w.Y1, w.Y2) = (w.Y2, w.Y1);
    }

    // ───────────────────────────── 2. Наружный контур ─────────────────────────────

    /// <summary>
    /// Сжатая сетка по граням стен: ячейка — стена, если её центр внутри тела стены; заливка снаружи отделяет улицу,
    /// граница внутренней области — точный ортогональный контур по наружным граням (против часовой стрелки).
    /// </summary>
    private sealed class OccupancyGrid
    {
        private readonly double[] _xs, _ys;
        private readonly bool[,] _outside;

        public bool Closed { get; }

        public OccupancyGrid(IReadOnlyList<PlanWall> walls)
        {
            var xs = new SortedSet<double>();
            var ys = new SortedSet<double>();
            double minX = double.MaxValue, maxX = double.MinValue, minY = double.MaxValue, maxY = double.MinValue;
            foreach (var w in walls)
            {
                var (x0, y0, x1, y1) = Box(w);
                xs.Add(R(x0)); xs.Add(R(x1)); ys.Add(R(y0)); ys.Add(R(y1));
                (minX, maxX, minY, maxY) = (Math.Min(minX, x0), Math.Max(maxX, x1), Math.Min(minY, y0), Math.Max(maxY, y1));
            }
            if (walls.Count == 0) (minX, maxX, minY, maxY) = (0, 1, 0, 1);
            xs.Add(R(minX - 1)); xs.Add(R(maxX + 1)); ys.Add(R(minY - 1)); ys.Add(R(maxY + 1));
            _xs = [.. xs];
            _ys = [.. ys];
            int nx = _xs.Length - 1, ny = _ys.Length - 1;
            var wall = new bool[nx, ny];
            for (int i = 0; i < nx; i++)
                for (int j = 0; j < ny; j++)
                {
                    double cx = (_xs[i] + _xs[i + 1]) / 2, cy = (_ys[j] + _ys[j + 1]) / 2;
                    foreach (var w in walls)
                    {
                        if (!w.IsOrthogonal)
                        {
                            if (DistanceToSegment(cx, cy, w) <= w.Thickness / 2) { wall[i, j] = true; break; }
                            continue;
                        }
                        var (x0, y0, x1, y1) = Box(w);
                        if (cx >= x0 && cx <= x1 && cy >= y0 && cy <= y1) { wall[i, j] = true; break; }
                    }
                }
            _outside = new bool[nx, ny];
            var stack = new Stack<(int, int)>();
            stack.Push((0, 0));
            _outside[0, 0] = true;
            while (stack.Count > 0)
            {
                var (i, j) = stack.Pop();
                foreach (var (di, dj) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) })
                {
                    int a = i + di, b = j + dj;
                    if (a < 0 || b < 0 || a >= nx || b >= ny || _outside[a, b] || wall[a, b]) continue;
                    _outside[a, b] = true;
                    stack.Push((a, b));
                }
            }
            bool interior = false;
            for (int i = 0; i < nx && !interior; i++)
                for (int j = 0; j < ny && !interior; j++)
                    interior = !_outside[i, j] && !wall[i, j];
            Closed = interior;
        }

        private static double R(double v) => Math.Round(v, 6);

        public static (double X0, double Y0, double X1, double Y1) Box(PlanWall w)
        {
            double h = w.Thickness / 2;
            if (w.IsHorizontal) return (Math.Min(w.X1, w.X2), w.Y1 - h, Math.Max(w.X1, w.X2), w.Y1 + h);
            if (w.IsVertical) return (w.X1 - h, Math.Min(w.Y1, w.Y2), w.X1 + h, Math.Max(w.Y1, w.Y2));
            return (Math.Min(w.X1, w.X2) - h, Math.Min(w.Y1, w.Y2) - h, Math.Max(w.X1, w.X2) + h, Math.Max(w.Y1, w.Y2) + h);
        }

        public bool IsOutside(double x, double y)
        {
            int i = Array.BinarySearch(_xs, x), j = Array.BinarySearch(_ys, y);
            if (i < 0) i = ~i;
            if (j < 0) j = ~j;
            i -= 1;
            j -= 1;
            if (i < 0 || j < 0 || i >= _outside.GetLength(0) || j >= _outside.GetLength(1)) return true;
            return _outside[i, j];
        }

        /// <summary>Наибольший замкнутый контур внутренней области (стены + помещения).</summary>
        public List<(double X, double Y)> Outline()
        {
            int nx = _outside.GetLength(0), ny = _outside.GetLength(1);
            bool In(int i, int j) => i >= 0 && j >= 0 && i < nx && j < ny && !_outside[i, j];
            var next = new Dictionary<(int, int), List<(int, int)>>();
            void Edge((int, int) a, (int, int) b)
            {
                if (!next.TryGetValue(a, out var list)) next[a] = list = [];
                list.Add(b);
            }
            for (int i = 0; i < nx; i++)
                for (int j = 0; j < ny; j++)
                {
                    if (!In(i, j)) continue;
                    if (!In(i, j - 1)) Edge((i, j), (i + 1, j));
                    if (!In(i + 1, j)) Edge((i + 1, j), (i + 1, j + 1));
                    if (!In(i, j + 1)) Edge((i + 1, j + 1), (i, j + 1));
                    if (!In(i - 1, j)) Edge((i, j + 1), (i, j));
                }
            var best = new List<(double X, double Y)>();
            double bestArea = 0;
            while (next.Count > 0)
            {
                var start = next.Keys.Min();
                var loop = new List<(int, int)>();
                var p = start;
                for (int guard = 0; guard < 200_000; guard++)
                {
                    loop.Add(p);
                    if (!next.TryGetValue(p, out var outs) || outs.Count == 0) break;
                    var q = outs[^1];
                    outs.RemoveAt(outs.Count - 1);
                    if (outs.Count == 0) next.Remove(p);
                    p = q;
                    if (p == start) break;
                }
                var poly = Simplify(loop.Select(v => (_xs[v.Item1], _ys[v.Item2])).ToList());
                double area = SignedArea(poly);
                if (area > bestArea) (best, bestArea) = (poly, area);
            }
            return best;
        }
    }

    private static (List<(double X, double Y)> Polygon, bool Closed) Footprint(IReadOnlyList<PlanWall> walls)
    {
        if (walls.Count == 0) return ([], false);
        var grid = new OccupancyGrid(walls);
        return grid.Closed ? (grid.Outline(), true) : ([], false);
    }

    private static List<(double X, double Y)> Simplify(List<(double X, double Y)> poly)
    {
        var list = poly.ToList();
        bool changed = true;
        while (changed && list.Count > 3)
        {
            changed = false;
            for (int i = 0; i < list.Count; i++)
            {
                var a = list[(i - 1 + list.Count) % list.Count];
                var b = list[i];
                var c = list[(i + 1) % list.Count];
                double cross = (b.X - a.X) * (c.Y - b.Y) - (b.Y - a.Y) * (c.X - b.X);
                if (Math.Abs(cross) < 1e-9 || Math.Abs(a.X - b.X) + Math.Abs(a.Y - b.Y) < 1e-9)
                {
                    list.RemoveAt(i);
                    changed = true;
                    break;
                }
            }
        }
        return list;
    }

    public static double SignedArea(IReadOnlyList<(double X, double Y)> poly)
    {
        double s = 0;
        for (int i = 0; i < poly.Count; i++)
        {
            var a = poly[i];
            var b = poly[(i + 1) % poly.Count];
            s += a.X * b.Y - b.X * a.Y;
        }
        return s / 2;
    }

    private static double DistanceToSegment(double px, double py, PlanWall w)
    {
        double dx = w.X2 - w.X1, dy = w.Y2 - w.Y1, l2 = dx * dx + dy * dy;
        double t = l2 < Eps ? 0 : Math.Clamp(((px - w.X1) * dx + (py - w.Y1) * dy) / l2, 0, 1);
        double x = w.X1 + dx * t - px, y = w.Y1 + dy * t - py;
        return Math.Sqrt(x * x + y * y);
    }

    // ───────────────────────────── 3. Капитальные оси и колонны ─────────────────────────────

    /// <summary>Сетка осей: оси несущих стен + промежуточные оси в пролётах больше MaxSpan; колонны — в узлах с пролётом в обе стороны больше MaxSpan.</summary>
    private sealed record GridPlan(List<double> Xs, List<double> Ys, List<(double X, double Y)> Columns);

    private static GridPlan ColumnGrid(IReadOnlyList<PlanWall> walls, IReadOnlyList<(double X, double Y)> footprint, PlanToBimOptions options)
    {
        var bearing = walls.Where(w => w.IsOrthogonal && !w.IsPartition).ToList();
        var xs = Cluster(bearing.Where(w => w.IsVertical).Select(w => w.X1));
        var ys = Cluster(bearing.Where(w => w.IsHorizontal).Select(w => w.Y1));
        List<double> Subdivide(List<double> axes)
        {
            var result = new List<double>();
            for (int i = 0; i < axes.Count; i++)
            {
                result.Add(axes[i]);
                if (i + 1 >= axes.Count) continue;
                double gap = axes[i + 1] - axes[i];
                int k = (int)Math.Ceiling(gap / options.MaxSpan - 1e-9) - 1;
                for (int s = 1; s <= k; s++) result.Add(Math.Round(axes[i] + gap * s / (k + 1), 6));
            }
            return result;
        }
        var gx = Subdivide(xs);
        var gy = Subdivide(ys);
        var columns = new List<(double X, double Y)>();
        var poly = footprint.Select(p => (p.X, Z: p.Y)).ToList();
        foreach (double x in gx)
            foreach (double y in gy)
            {
                if (poly.Count >= 3 && (!SceneStateService.PointInPolygon(x, y, poly) || Geometry2D.DistanceToBoundary(poly, x, y) < 0.5)) continue;
                if (walls.Any(w => DistanceToSegment(x, y, w) < w.Thickness / 2 + 0.3)) continue;
                var (spanX, spanY) = Spans(bearing, x, y);
                if (Math.Min(spanX, spanY) > options.MaxSpan + 1e-6) columns.Add((x, y));
            }
        // Оси сетки колонн: только оси с колоннами или несущими стенами.
        var usedX = Cluster(xs.Concat(columns.Select(c => c.X)));
        var usedY = Cluster(ys.Concat(columns.Select(c => c.Y)));
        return new GridPlan(usedX, usedY, columns);
    }

    private static (double X, double Y) Spans(IReadOnlyList<PlanWall> bearing, double x, double y)
    {
        double left = double.NegativeInfinity, right = double.PositiveInfinity, down = double.NegativeInfinity, up = double.PositiveInfinity;
        foreach (var w in bearing)
        {
            var (a, b) = Span(w);
            if (w.IsVertical && y >= a && y <= b)
            {
                if (w.X1 < x) left = Math.Max(left, w.X1);
                else right = Math.Min(right, w.X1);
            }
            if (w.IsHorizontal && x >= a && x <= b)
            {
                if (w.Y1 < y) down = Math.Max(down, w.Y1);
                else up = Math.Min(up, w.Y1);
            }
        }
        return (right - left, up - down);
    }

    private static List<double> Cluster(IEnumerable<double> values)
    {
        var sorted = values.OrderBy(v => v).ToList();
        var result = new List<double>();
        foreach (double v in sorted)
            if (result.Count == 0 || v - result[^1] > 0.3) result.Add(v);
        return result;
    }

    // ───────────────────────────── 4. Сборка здания ─────────────────────────────

    private static List<ArchitecturalElement> Assemble(PlanToBimResult result, GridPlan grid, PlanToBimOptions options,
        (double Slab, double Column, double Foundation) sizing, (double X, double Y) center, IReadOnlyDictionary<PlanWall, double> wallBoost,
        out List<StructuralGridAxis> axes, out Dictionary<string, PlanWall> wallOf)
    {
        var elements = new List<ArchitecturalElement>();
        wallOf = new Dictionary<string, PlanWall>(StringComparer.Ordinal);
        double Thick(PlanWall w) => Math.Round(w.Thickness + wallBoost.GetValueOrDefault(w), 6);
        string g = options.GroupId;
        double fh = Math.Max(2.4, options.FloorHeight), slabT = sizing.Slab, wallH = fh - slabT;
        int storeys = Math.Max(1, options.Storeys);
        // Лист → сцена: X = x − cx, Z = −(y − cy) (север листа — −Z сцены).
        (double X, double Z) S(double x, double y) => (x - center.X, -(y - center.Y));

        axes = [];
        var numeric = grid.Xs.OrderBy(v => v).ToList();
        var letters = grid.Ys.OrderBy(v => v).ToList();
        for (int i = 0; i < numeric.Count; i++) axes.Add(new StructuralGridAxis((i + 1).ToString(CultureInfo.InvariantCulture), StructuralGridAxis.AlongZ, S(numeric[i], 0).X, g));
        for (int j = 0; j < letters.Count; j++) axes.Add(new StructuralGridAxis(StructuralAxesBuilder.Letter(j), StructuralGridAxis.AlongX, S(0, letters[j]).Z, g));
        string AxisX(double x) => numeric.Count == 0 ? string.Empty : (numeric.Select((v, i) => (Math.Abs(v - x), i)).Min().i + 1).ToString(CultureInfo.InvariantCulture);
        string AxisY(double y) => letters.Count == 0 ? string.Empty : StructuralAxesBuilder.Letter(letters.Select((v, i) => (Math.Abs(v - y), i)).Min().i);

        ArchitecturalElement Make(string type, string name, int level, string material)
        {
            var e = new ArchitecturalElement { Id = ArchitecturalElement.NewId(), Type = type, Name = name, BuildingGroupId = g, LevelIndex = level, Material = material };
            elements.Add(e);
            return e;
        }

        ArchitecturalElement Plate(string type, string name, int level, double baseY, double thickness)
        {
            var e = Make(type, name, level, "concrete");
            var fp = result.Footprint;
            double x0 = fp.Min(p => p.X), x1 = fp.Max(p => p.X), y0 = fp.Min(p => p.Y), y1 = fp.Max(p => p.Y);
            var (cx, cz) = S((x0 + x1) / 2, (y0 + y1) / 2);
            e.SetSize(x1 - x0, thickness, y1 - y0);
            e.SetPosition(cx, baseY + thickness / 2, cz);
            bool rectangle = fp.Count == 4 && fp.All(p => (Math.Abs(p.X - x0) < Eps || Math.Abs(p.X - x1) < Eps) && (Math.Abs(p.Y - y0) < Eps || Math.Abs(p.Y - y1) < Eps));
            if (!rectangle)
                e.Polygon2D = fp.Select(p =>
                {
                    var (px, pz) = S(p.X, p.Y);
                    return new Point2D(Math.Round(px - cx, 6), Math.Round(pz - cz, 6));
                }).ToList();
            return e;
        }

        var foundation = Plate(ElementTypes.Foundation, "Фундаментная плита", 0, -sizing.Foundation, sizing.Foundation);
        _ = foundation;
        int wallIndex = 0;
        var wallElements = new List<(PlanWall Plan, ArchitecturalElement Element, int Level)>();
        for (int level = 0; level < storeys; level++)
        {
            double baseY = level * fh;
            foreach (var w in result.Walls)
            {
                wallIndex++;
                string where = w.IsHorizontal ? $"по оси {AxisY(w.Y1)}" : w.IsVertical ? $"по оси {AxisX(w.X1)}" : $"№ {wallIndex}";
                double t = Thick(w);
                bool partition = t < PartitionThickness;
                string title = w.IsExterior ? "Наружная стена" : partition ? "Перегородка" : "Внутренняя стена";
                var e = Make(ElementTypes.Wall, $"{title} {where} · эт. {level + 1}", level, partition ? "brick" : "concrete");
                wallOf[e.Id] = w;
                var (ax, az) = S(w.X1, w.Y1);
                var (bx, bz) = S(w.X2, w.Y2);
                e.SetSize(w.Length, wallH, t);
                e.SetPosition((ax + bx) / 2, baseY + wallH / 2, (az + bz) / 2);
                double rotation = Math.Round(Math.Atan2(-(bz - az), bx - ax) * 180 / Math.PI, 9);
                e.SetRotationY(Math.Abs(rotation) < 1e-9 ? 0 : rotation);
                wallElements.Add((w, e, level));
            }
            foreach (var (x, y) in grid.Columns)
            {
                var c = Make(ElementTypes.Column, $"Колонна {AxisX(x)}/{AxisY(y)} · эт. {level + 1}", level, "concrete");
                var (cx, cz) = S(x, y);
                c.SetSize(sizing.Column, wallH, sizing.Column);
                c.SetPosition(cx, baseY + wallH / 2, cz);
            }
            bool top = level == storeys - 1;
            var plate = Plate(top ? ElementTypes.Roof : ElementTypes.Slab, top ? "Плоская кровля" : $"Плита перекрытия {level + 1}", level, baseY + wallH, slabT);
            if (top) plate.RoofShape = RoofShapes.Flat;
        }

        // Проёмы: на ось ближайшей стены, хост — стена этажа (наружные двери верхних этажей — окна).
        int windows = 0, doors = 0;
        foreach (var o in result.Plan.Openings)
        {
            var host = result.Walls.Where(w => w.IsOrthogonal)
                .Select(w => (Wall: w, Distance: DistanceToSegment(o.X, o.Y, w)))
                .Where(p => p.Distance <= p.Wall.Thickness / 2 + 0.35).OrderBy(p => p.Distance).Select(p => p.Wall).FirstOrDefault();
            if (host is null) continue;
            var (s0, s1) = Span(host);
            double along = host.IsHorizontal ? o.X : o.Y;
            if (s1 - s0 < 0.9) continue;
            double width = Math.Clamp(o.Width, 0.6, s1 - s0 - 0.3);
            along = Math.Clamp(along, s0 + width / 2 + 0.1, s1 - width / 2 - 0.1);
            double px = host.IsHorizontal ? along : host.X1, py = host.IsHorizontal ? host.Y1 : along;
            for (int level = 0; level < storeys; level++)
            {
                var wall = wallElements.First(p => ReferenceEquals(p.Plan, host) && p.Level == level).Element;
                bool door = o.Kind == PlanOpeningKind.Door && (level == 0 || !host.IsExterior);
                double h = door ? options.DoorHeight : options.WindowHeight, sill = door ? 0 : options.WindowSill;
                if (sill + h > wallH - 0.15) h = Math.Max(0.6, wallH - 0.15 - sill);
                var e = Make(door ? ElementTypes.Door : ElementTypes.Window, door ? $"Дверь Д-{++doors}" : $"Окно ОК-{++windows}", level, door ? "wood" : "glass");
                var (cx, cz) = S(px, py);
                e.SetSize(width, h, Thick(host) + 0.06);
                e.SetPosition(cx, level * fh + sill + h / 2, cz);
                e.SetRotationY(wall.RotationY);
                e.HostElementId = wall.Id;
            }
        }
        return elements;
    }

    // ───────────────────────────── Источники ─────────────────────────────

    /// <summary>
    /// План из DXF: пары параллельных линий слоёв стен (и неизвестных) на расстоянии 100…600 мм — оси стен, контуры
    /// слоёв окон и дверей — проёмы на ближайшей стене.
    /// </summary>
    public static RecognizedPlan FromDxf(DxfDrawing drawing)
    {
        var lines = drawing.Contours.Where(c => c.Category is DxfLayerCategory.Wall or DxfLayerCategory.Unknown && !c.IsCircle)
            .SelectMany(c => c.Segments()).Select(s => (X1: s.A.X, Y1: s.A.Y, X2: s.B.X, Y2: s.B.Y)).Where(s => Math.Abs(s.X2 - s.X1) + Math.Abs(s.Y2 - s.Y1) > 0.3).ToList();
        var plan = new RecognizedPlan { Name = drawing.FileName, Source = PlanSourceKind.Dxf, ScaleSource = drawing.UnitsSource.Length > 0 ? drawing.UnitsSource : "единицы чертежа" };
        plan.Walls.AddRange(PairParallelLines(lines));
        foreach (var c in drawing.Contours.Where(c => c.Category is DxfLayerCategory.Window or DxfLayerCategory.Door))
        {
            var ring = c.Ring();
            if (ring.Count < 2) continue;
            double x0 = ring.Min(p => p.X), x1 = ring.Max(p => p.X), y0 = ring.Min(p => p.Y), y1 = ring.Max(p => p.Y);
            plan.Openings.Add(new PlanOpening((x0 + x1) / 2, (y0 + y1) / 2, Math.Max(x1 - x0, y1 - y0), c.Category == DxfLayerCategory.Door ? PlanOpeningKind.Door : PlanOpeningKind.Window));
        }
        return plan;
    }

    /// <summary>Пары параллельных отрезков с перекрытием и расстоянием 100…600 мм → ось стены и толщина.</summary>
    public static List<PlanWall> PairParallelLines(IReadOnlyList<(double X1, double Y1, double X2, double Y2)> lines)
    {
        var walls = new List<PlanWall>();
        var used = new bool[lines.Count];
        for (int i = 0; i < lines.Count; i++)
        {
            if (used[i]) continue;
            var a = lines[i];
            double dx = a.X2 - a.X1, dy = a.Y2 - a.Y1, len = Math.Sqrt(dx * dx + dy * dy);
            if (len < 0.3) continue;
            double ux = dx / len, uy = dy / len;
            int best = -1;
            double bestDist = double.MaxValue;
            for (int j = 0; j < lines.Count; j++)
            {
                if (i == j || used[j]) continue;
                var b = lines[j];
                double bdx = b.X2 - b.X1, bdy = b.Y2 - b.Y1, blen = Math.Sqrt(bdx * bdx + bdy * bdy);
                if (blen < 0.3 || Math.Abs(ux * bdy / blen - uy * bdx / blen) > 0.02) continue;
                double dist = Math.Abs((b.X1 - a.X1) * -uy + (b.Y1 - a.Y1) * ux);
                if (dist < 0.1 - 1e-6 || dist > 0.6 + 1e-6) continue;
                double t0 = (b.X1 - a.X1) * ux + (b.Y1 - a.Y1) * uy, t1 = (b.X2 - a.X1) * ux + (b.Y2 - a.Y1) * uy;
                double overlap = Math.Min(len, Math.Max(t0, t1)) - Math.Max(0, Math.Min(t0, t1));
                if (overlap < 0.5 * Math.Min(len, blen)) continue;
                if (dist < bestDist) (best, bestDist) = (j, dist);
            }
            if (best < 0) continue;
            used[i] = used[best] = true;
            var p = lines[best];
            double s0 = (p.X1 - a.X1) * ux + (p.Y1 - a.Y1) * uy, s1 = (p.X2 - a.X1) * ux + (p.Y2 - a.Y1) * uy;
            double from = Math.Min(0, Math.Min(s0, s1)), to = Math.Max(len, Math.Max(s0, s1));
            double side = (p.X1 - a.X1) * -uy + (p.Y1 - a.Y1) * ux;
            double mx = a.X1 + -uy * side / 2, my = a.Y1 + ux * side / 2;
            walls.Add(new PlanWall(mx + ux * from, my + uy * from, mx + ux * to, my + uy * to, Math.Round(bestDist, 4)));
        }
        return walls;
    }

    /// <summary>Сводка для чата и журнала.</summary>
    public static string Summary(PlanToBimResult r) => string.Create(Ru,
        $"осей {r.Axes.Count}, наружных стен {r.ExteriorWalls}, внутренних {r.InteriorWalls}, окон {r.Windows}, дверей {r.Doors}, колонн {r.Columns}, " +
        $"плит {r.Slabs} + фундамент; этажей {r.Storeys}, {r.WidthM:0.##} × {r.DepthM:0.##} м; K_исп max {r.MaxUtilization:0.00}, без опоры {r.PhysicsErrors}, " +
        $"арматура {r.RebarKg / 1000:0.00} т");
}
