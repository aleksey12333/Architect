using AIArchitectStudio.Models;

namespace AIArchitectStudio.Services;

/// <summary>Расчёт двухмаршевой лестницы под высоту этажа: подступенки, проступи, длины маршей и площадки.</summary>
public sealed record StairPlan(
    double FloorHeight,
    int Risers,
    int FirstFlightRisers,
    int SecondFlightRisers,
    double Riser,
    double Tread,
    double FlightWidth,
    double Gap,
    double LandingDepth)
{
    public double FirstFlightLength => FirstFlightRisers * Tread;
    public double SecondFlightLength => SecondFlightRisers * Tread;
    public double FirstFlightHeight => FirstFlightRisers * Riser;
    public double SecondFlightHeight => SecondFlightRisers * Riser;

    /// <summary>Габарит лестничной клетки в плане: поперёк маршей (два марша и зазор) и вдоль маршей (марш и площадка).</summary>
    public double TotalWidth => 2 * FlightWidth + Gap;
    public double TotalLength => Math.Max(FirstFlightLength, SecondFlightLength) + LandingDepth;

    /// <summary>Формула шага Блонделя 2h + b (норма 600…640 мм).</summary>
    public double StepFormula => 2 * Riser + Tread;
}

/// <summary>Итог генерации лестницы: план, новые элементы (марши, площадка, проёмы) и снятые элементы.</summary>
public sealed record StairBuildResult(StairPlan Plan, List<ArchitecturalElement> Created, List<ArchitecturalElement> Removed, int OpeningsCut);

/// <summary>Итог преобразования в скатную кровлю: кровля (новая или изменённая), высота конька, пролёт, снятые отверстия.</summary>
public sealed record PitchedRoofResult(ArchitecturalElement Roof, bool Created, string Shape, double AngleDeg, double RidgeHeight, double Span, List<ArchitecturalElement> Removed);

/// <summary>
/// Параметрические инструменты ручного конструирования (без промптов): П-образная двухмаршевая лестница с междуэтажной
/// площадкой под высоту этажа (подступенок 150 мм, проступь 300 мм) и скатная кровля (двускатная / вальмовая) по углу наклона.
/// Чистые функции над списком элементов; применение к сцене — одним шагом Undo в <see cref="MainViewModel"/>.
/// </summary>
public static class ParametricToolsService
{
    public const double DefaultRiser = 0.15;
    public const double DefaultTread = 0.30;
    public const double DefaultFlightWidth = 1.2;
    public const double FlightGap = 0.2;
    public const double LandingThickness = 0.2;

    /// <summary>Перекрытие площадки под первую ступень второго марша (опора марша в плане).</summary>
    private const double LandingBearing = 0.05;

    /// <summary>Число подступенков — ближайшее к высоте этажа / 150 мм (не меньше 4); марши делятся поровну, нечётный — первому.</summary>
    public static StairPlan PlanUShaped(double floorHeight, double flightWidth = DefaultFlightWidth, double riser = DefaultRiser, double tread = DefaultTread)
    {
        floorHeight = double.IsFinite(floorHeight) && floorHeight > 0.6 ? floorHeight : 3.0;
        flightWidth = double.IsFinite(flightWidth) ? Math.Clamp(flightWidth, 0.9, 3.0) : DefaultFlightWidth;
        int risers = Math.Max(4, (int)Math.Round(floorHeight / riser));
        int first = (risers + 1) / 2;
        return new StairPlan(floorHeight, risers, first, risers - first, floorHeight / risers, tread, flightWidth, FlightGap, Math.Max(1.2, flightWidth));
    }

    /// <summary>
    /// П-образная лестница: марш 1 поднимается вдоль локальной +Z от ближнего края, площадка на половине высоты этажа, марш 2
    /// возвращается вдоль −Z рядом с первым. (centerX, centerZ) — центр лестничной клетки в плане, rotationY — поворот клетки.
    /// В перекрытиях постройки над маршами вырезается проём (отверстия этих плит над лестницей объединяются в один).
    /// </summary>
    public static StairBuildResult BuildUShapedStair(IReadOnlyList<ArchitecturalElement> scene, StairPlan plan, string groupId, int level,
        double baseElevation, double centerX, double centerZ, double rotationY, string namePrefix = "Л-1")
    {
        double w = plan.FlightWidth, half = plan.TotalLength / 2;
        double xFirst = -(w + plan.Gap) / 2, xSecond = (w + plan.Gap) / 2;
        double zStart = -half, zLanding = zStart + plan.FirstFlightLength;

        ArchitecturalElement Make(string type, string name, double lx, double y, double lz, double sw, double sh, double sd, double rot) =>
            Place(new ArchitecturalElement
            {
                Type = type,
                Name = name,
                BuildingGroupId = groupId,
                LevelIndex = level,
                Material = "concrete",
                Size = [sw, sh, sd],
                Rotation = [0, ArchitecturalElement.NormalizeAngle(rotationY + rot), 0]
            }, centerX, centerZ, rotationY, lx, y, lz);

        var first = Make(ElementTypes.Stairs, $"Марш {namePrefix}.1", xFirst, baseElevation + plan.FirstFlightHeight / 2,
            zStart + plan.FirstFlightLength / 2, w, plan.FirstFlightHeight, plan.FirstFlightLength, 0);
        first.StepCount = plan.FirstFlightRisers;

        // Площадка: верх — на отметке конца первого марша, заходит под первую ступень второго марша.
        double landingTop = baseElevation + plan.FirstFlightHeight;
        double landingDepth = plan.LandingDepth + LandingBearing;
        var landing = Make(ElementTypes.Terrace, $"Площадка {namePrefix}.П", 0, landingTop - LandingThickness / 2,
            zLanding - LandingBearing + landingDepth / 2, plan.TotalWidth, LandingThickness, landingDepth, 0);

        // Второй марш повёрнут на 180°: подъём вдоль −Z от площадки к перекрытию этажа.
        var second = Make(ElementTypes.Stairs, $"Марш {namePrefix}.2", xSecond, landingTop + plan.SecondFlightHeight / 2,
            zLanding - plan.SecondFlightLength / 2, w, plan.SecondFlightHeight, plan.SecondFlightLength, 180);
        second.StepCount = plan.SecondFlightRisers;

        var created = new List<ArchitecturalElement> { first, landing, second };
        var removed = new List<ArchitecturalElement>();
        int cut = 0;

        // Проём в перекрытиях над лестничной клеткой: плиты постройки между площадкой и верхом второго марша (+ запас).
        double top = baseElevation + plan.FloorHeight;
        var footprint = Footprint(centerX, centerZ, rotationY, plan.TotalWidth, plan.TotalLength);
        foreach (var slab in scene.Where(s => s.BuildingGroupId == groupId && s.CanHostSlabOpenings && s.Type is not (ElementTypes.Foundation or ElementTypes.Terrace)
                                              && s.BaseY > landingTop + 0.3 && s.BaseY < top + 0.3 && s.TopY > top - 0.3))
        {
            var slabOutline = Outline(slab);
            if (Geometry2D.IntersectionArea(footprint, slabOutline) < 0.15 * plan.TotalWidth * plan.TotalLength) continue;
            var overlapping = SceneStateService.OpeningsFor(slab, scene).Where(h => Geometry2D.IntersectionArea(footprint, Outline(h)) > 1e-4).ToList();
            var points = footprint.Concat(overlapping.SelectMany(Outline))
                .Select(p => ArchitecturalElement.RotateY(p.X - centerX, p.Z - centerZ, -rotationY)).ToList();
            double x0 = points.Min(p => p.X), x1 = points.Max(p => p.X), z0 = points.Min(p => p.Z), z1 = points.Max(p => p.Z);
            var (cx, cz) = ArchitecturalElement.RotateY((x0 + x1) / 2, (z0 + z1) / 2, rotationY);
            removed.AddRange(overlapping);
            created.Add(new ArchitecturalElement
            {
                Type = ElementTypes.SlabOpening,
                Name = $"Проём лестницы {namePrefix}",
                BuildingGroupId = slab.BuildingGroupId,
                LevelIndex = slab.LevelIndex,
                Material = "void",
                HostElementId = slab.Id,
                Position = [centerX + cx, slab.Y, centerZ + cz],
                Size = [x1 - x0, slab.Height + ParametricSolverService.OpeningClearance, z1 - z0],
                Rotation = [0, ArchitecturalElement.NormalizeAngle(rotationY), 0]
            });
            cut++;
        }
        return new StairBuildResult(plan, created, removed, cut);
    }

    /// <summary>
    /// Скатная кровля по углу наклона: высота конька H = tg(α) · половина пролёта (пролёт — короткая сторона габарита: конёк идёт
    /// вдоль длинной стороны, у вальмовой все скаты одного уклона). Кровля (roof) меняет форму на месте, низ сохраняется;
    /// плоская кровля теряет отверстия и пирог (скатная кровля их не несёт). Для плиты покрытия создаётся новая кровля
    /// по её контуру поверх плиты.
    /// </summary>
    public static PitchedRoofResult? MakePitchedRoof(IReadOnlyList<ArchitecturalElement> scene, ArchitecturalElement target, string shape, double angleDeg)
    {
        if (target.Type is not (ElementTypes.Roof or ElementTypes.Slab or ElementTypes.CantileverSlab)) return null;
        shape = shape == RoofShapes.Hip ? RoofShapes.Hip : RoofShapes.Gable;
        angleDeg = double.IsFinite(angleDeg) ? Math.Clamp(angleDeg, 5, 60) : 25;
        double span = Span(target);
        if (span < 0.5) return null;
        double ridge = Math.Round(Math.Tan(angleDeg * Math.PI / 180) * span / 2, 3);

        if (target.Type == ElementTypes.Roof)
        {
            double baseY = target.BaseY;
            var removed = scene.Where(h => h.Type == ElementTypes.SlabOpening && h.HostElementId == target.Id).ToList();
            target.RoofShape = shape;
            target.Layers = [];
            target.SetSize(target.Width, ridge, target.Depth);
            target.PlaceOnBase(baseY);
            return new PitchedRoofResult(target, false, shape, angleDeg, ridge, span, removed);
        }

        var roof = new ArchitecturalElement
        {
            Type = ElementTypes.Roof,
            Name = shape == RoofShapes.Hip ? "Вальмовая кровля" : "Двускатная кровля",
            BuildingGroupId = target.BuildingGroupId,
            LevelIndex = target.LevelIndex,
            Material = "metal_roof",
            RoofShape = shape,
            Position = (double[])target.Position.Clone(),
            Size = [target.Width, ridge, target.Depth],
            Rotation = (double[])target.Rotation.Clone(),
            Polygon2D = target.Polygon2D.Select(p => p.Clone()).ToList()
        };
        roof.PlaceOnBase(target.TopY);
        return new PitchedRoofResult(roof, true, shape, angleDeg, ridge, span, []);
    }

    /// <summary>Пролёт скатов: короткая сторона габарита плана (контура Polygon2D или W × D) в осях элемента.</summary>
    public static double Span(ArchitecturalElement e)
    {
        if (e.Polygon2D.Count >= 3)
            return Math.Min(e.Polygon2D.Max(p => p.X) - e.Polygon2D.Min(p => p.X), e.Polygon2D.Max(p => p.Z) - e.Polygon2D.Min(p => p.Z));
        return Math.Min(e.Width, e.Depth);
    }

    /// <summary>Центр новой лестницы: середина габарита постройки на этаже (без выделения) или начало координат.</summary>
    public static (double X, double Z) DefaultStairCenter(IReadOnlyList<ArchitecturalElement> floor)
    {
        var solid = floor.Where(e => !ElementTypes.IsVoidType(e.Type) && !ElementTypes.IsHostedType(e.Type)).ToList();
        if (solid.Count == 0) return (0, 0);
        var (minX, maxX, minZ, maxZ) = SceneStateService.Bounds(solid);
        return ((minX + maxX) / 2, (minZ + maxZ) / 2);
    }

    private static ArchitecturalElement Place(ArchitecturalElement e, double cx, double cz, double rotationY, double lx, double y, double lz)
    {
        var (wx, wz) = ArchitecturalElement.RotateY(lx, lz, rotationY);
        e.Position = [cx + wx, y, cz + wz];
        return e;
    }

    private static List<(double X, double Z)> Footprint(double cx, double cz, double rotationY, double width, double length)
    {
        var corners = new[] { (-width / 2, -length / 2), (width / 2, -length / 2), (width / 2, length / 2), (-width / 2, length / 2) };
        return corners.Select(c =>
        {
            var (x, z) = ArchitecturalElement.RotateY(c.Item1, c.Item2, rotationY);
            return (cx + x, cz + z);
        }).ToList();
    }

    private static List<(double X, double Z)> Outline(ArchitecturalElement e) => ClashDetectionService.PlanOutline(e);
}
