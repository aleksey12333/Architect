using AIArchitectStudio.Models;

namespace AIArchitectStudio.Services;

/// <summary>Параметры генератора лестниц: форма в плане, конструкция марша, ширина марша (м), подступенок и проступь (м).</summary>
public sealed record StairDesignRequest(StairShape Shape, StairConstruction Construction, double FlightWidth = ParametricToolsService.DefaultFlightWidth,
    double Riser = ParametricToolsService.DefaultRiser, double Tread = ParametricToolsService.DefaultTread);

/// <summary>Итог генерации: марши, площадки и проёмы в перекрытиях, снятые отверстия, число подступенков и габарит клетки (м).</summary>
public sealed record StairGeneration(StairDesignRequest Request, int Risers, double Riser, double FootprintWidth, double FootprintLength,
    List<ArchitecturalElement> Created, List<ArchitecturalElement> Removed, int OpeningsCut)
{
    public IEnumerable<ArchitecturalElement> Flights => Created.Where(e => e.Category == BimCategory.Stairs);
    public IEnumerable<ArchitecturalElement> Openings => Created.Where(e => e.Category == BimCategory.SlabOpening);
}

/// <summary>
/// Параметрический конструктор лестниц: прямой одномаршевый, Г-образный с поворотной площадкой 90°, П-образный с промежуточной
/// площадкой 180°, трёхмаршевый вокруг шахты и винтовой; конструкция марша — монолитный по наклонной плите, на двух косоурах или
/// консольный на больцах (общий параметр марша «Конструкция марша»). Подступенки — round(высота этажа / 150 мм), проступь 300 мм.
/// В перекрытиях постройки над лестницей вырезается сквозной проём (отверстия этих плит в зоне клетки объединяются в один).
/// Чистая функция над списком элементов; применение — одним шагом журнала в <see cref="ViewModels.MainViewModel"/>.
/// </summary>
public static class StairGeneratorService
{
    public const double LandingThickness = ParametricToolsService.LandingThickness;

    /// <summary>Запас проёма по контуру клетки, м.</summary>
    public const double OpeningMargin = 0.05;

    public static string ShapeTitle(StairShape s) => s switch
    {
        StairShape.Straight => "Прямой одномаршевый",
        StairShape.LShaped => "Г-образный с поворотной площадкой",
        StairShape.UShaped => "П-образный двухмаршевый",
        StairShape.ThreeFlight => "Трёхмаршевый вокруг шахты",
        _ => "Винтовой"
    };

    public static string ConstructionTitle(StairConstruction c) => c switch
    {
        StairConstruction.Stringers => "На двух косоурах",
        StairConstruction.Cantilever => "Консольный на больцах",
        _ => "Монолитный по наклонной плите"
    };

    public static StairGeneration Build(IReadOnlyList<ArchitecturalElement> scene, StairDesignRequest request, string groupId, int level,
        double baseElevation, double floorHeight, double centerX, double centerZ, double rotationY, string prefix = "Л-1")
    {
        floorHeight = double.IsFinite(floorHeight) && floorHeight > 0.6 ? floorHeight : 3.0;
        double w = double.IsFinite(request.FlightWidth) ? Math.Clamp(request.FlightWidth, 0.8, 3.0) : ParametricToolsService.DefaultFlightWidth;
        double tread = Math.Clamp(request.Tread, 0.25, 0.35);
        int risers = Math.Max(4, (int)Math.Round(floorHeight / Math.Clamp(request.Riser, 0.12, 0.2)));
        double riser = floorHeight / risers;

        if (request.Shape == StairShape.UShaped)
        {
            var plan = ParametricToolsService.PlanUShaped(floorHeight, w, request.Riser, tread);
            var u = ParametricToolsService.BuildUShapedStair(scene, plan, groupId, level, baseElevation, centerX, centerZ, rotationY, prefix);
            foreach (var f in u.Created.Where(e => e.Category == BimCategory.Stairs)) StairDesign.Write(f, request.Construction);
            return new StairGeneration(request, plan.Risers, plan.Riser, plan.TotalWidth, plan.TotalLength, u.Created, u.Removed, u.OpeningsCut);
        }

        // Раскладка в локальной системе клетки: марш 1 поднимается вдоль +Z; затем сдвиг к центру габарита.
        var parts = new List<(string Type, string Name, double X, double BaseY, double Z, double W, double H, double D, double Rot, int Steps)>();
        double minX, maxX, minZ, maxZ;
        List<(double X, double Z)>? circle = null;
        switch (request.Shape)
        {
            case StairShape.Straight:
            {
                double l = risers * tread;
                parts.Add((ElementTypes.Stairs, $"Марш {prefix}.1", 0, baseElevation, l / 2, w, floorHeight, l, 0, risers));
                (minX, maxX, minZ, maxZ) = (-w / 2, w / 2, 0, l);
                break;
            }
            case StairShape.LShaped:
            {
                int n1 = (risers + 1) / 2, n2 = risers - n1;
                double l1 = n1 * tread, l2 = n2 * tread, h1 = n1 * riser;
                parts.Add((ElementTypes.Stairs, $"Марш {prefix}.1", 0, baseElevation, l1 / 2, w, h1, l1, 0, n1));
                parts.Add((ElementTypes.Terrace, $"Площадка {prefix}.П", 0, baseElevation + h1 - LandingThickness, l1 + w / 2, w, LandingThickness, w, 0, 0));
                parts.Add((ElementTypes.Stairs, $"Марш {prefix}.2", w / 2 + l2 / 2, baseElevation + h1, l1 + w / 2, w, n2 * riser, l2, 90, n2));
                (minX, maxX, minZ, maxZ) = (-w / 2, w / 2 + l2, 0, l1 + w);
                break;
            }
            case StairShape.ThreeFlight:
            {
                int n1 = Math.Max(2, (int)Math.Round(risers / 3.0)), n2 = Math.Max(2, risers - 2 * n1), n3 = risers - n1 - n2;
                if (n3 < 2) { n1 = (risers - 2) / 2; n2 = 2; n3 = risers - n1 - n2; }
                double l1 = n1 * tread, l2 = n2 * tread, l3 = n3 * tread, h1 = n1 * riser, h2 = n2 * riser, h3 = n3 * riser;
                double xl = -(l2 / 2 + w / 2), xr = l2 / 2 + w / 2, zTop = Math.Max(l1, l3) + w / 2;
                parts.Add((ElementTypes.Stairs, $"Марш {prefix}.1", xl, baseElevation, zTop - w / 2 - l1 / 2, w, h1, l1, 0, n1));
                parts.Add((ElementTypes.Terrace, $"Площадка {prefix}.П1", xl, baseElevation + h1 - LandingThickness, zTop, w, LandingThickness, w, 0, 0));
                parts.Add((ElementTypes.Stairs, $"Марш {prefix}.2", 0, baseElevation + h1, zTop, w, h2, l2, 90, n2));
                parts.Add((ElementTypes.Terrace, $"Площадка {prefix}.П2", xr, baseElevation + h1 + h2 - LandingThickness, zTop, w, LandingThickness, w, 0, 0));
                parts.Add((ElementTypes.Stairs, $"Марш {prefix}.3", xr, baseElevation + h1 + h2, zTop - w / 2 - l3 / 2, w, h3, l3, 180, n3));
                (minX, maxX, minZ, maxZ) = (xl - w / 2, xr + w / 2, zTop - w / 2 - Math.Max(l1, l3), zTop + w / 2);
                break;
            }
            default:
            {
                // Винтовая: диаметр = 2 × ширина марша + стойка; ступени одним элементом.
                double diameter = Math.Round(2 * w + 0.2, 3);
                int steps = Math.Max(6, (int)Math.Round(floorHeight / 0.18));
                parts.Add((ElementTypes.Stairs, $"Винтовая лестница {prefix}", 0, baseElevation, 0, diameter, floorHeight, diameter, 0, steps));
                double r = diameter / 2 + 0.1;
                circle = Enumerable.Range(0, 16).Select(i => (r * Math.Cos(i * Math.PI / 8), r * Math.Sin(i * Math.PI / 8))).ToList();
                (minX, maxX, minZ, maxZ) = (-r, r, -r, r);
                risers = steps;
                riser = floorHeight / steps;
                break;
            }
        }

        double ox = (minX + maxX) / 2, oz = (minZ + maxZ) / 2;
        var created = new List<ArchitecturalElement>();
        foreach (var p in parts)
        {
            var (wx, wz) = ArchitecturalElement.RotateY(p.X - ox, p.Z - oz, rotationY);
            var e = new ArchitecturalElement
            {
                Type = p.Type,
                Name = p.Name,
                BuildingGroupId = groupId,
                LevelIndex = level,
                Material = "concrete",
                Size = [p.W, p.H, p.D],
                Rotation = [0, ArchitecturalElement.NormalizeAngle(rotationY + p.Rot), 0],
                Position = [centerX + wx, p.BaseY + p.H / 2, centerZ + wz]
            };
            if (p.Type == ElementTypes.Stairs)
            {
                e.StepCount = p.Steps;
                if (request.Construction != StairConstruction.MonolithicSlab) e.Material = request.Construction == StairConstruction.Stringers ? "steel" : "concrete";
                StairDesign.Write(e, request.Shape == StairShape.Spiral && request.Construction == StairConstruction.MonolithicSlab ? StairConstruction.Cantilever : request.Construction,
                    spiral: request.Shape == StairShape.Spiral);
            }
            created.Add(e);
        }

        double width = maxX - minX, length = maxZ - minZ;
        var (removed, cut) = CutOpenings(scene, created, groupId, baseElevation, floorHeight, centerX, centerZ, rotationY, width, length, circle, prefix);
        return new StairGeneration(request, risers, riser, width, length, created, removed, cut);
    }

    /// <summary>
    /// Сквозной проём над лестницей в перекрытиях постройки на уровне верха этажа: прямоугольник габарита клетки (+50 мм) или круг
    /// винтовой лестницы; пересекающиеся с ним отверстия той же плиты объединяются в один проём (ненулевая обмотка не допускает внахлёст).
    /// </summary>
    private static (List<ArchitecturalElement> Removed, int Cut) CutOpenings(IReadOnlyList<ArchitecturalElement> scene, List<ArchitecturalElement> created, string groupId,
        double baseElevation, double floorHeight, double cx, double cz, double rotationY, double width, double length, List<(double X, double Z)>? circle, string prefix)
    {
        var removed = new List<ArchitecturalElement>();
        int cut = 0;
        double top = baseElevation + floorHeight;
        double fw = width + 2 * OpeningMargin, fl = length + 2 * OpeningMargin;
        var footprint = new[] { (-fw / 2, -fl / 2), (fw / 2, -fl / 2), (fw / 2, fl / 2), (-fw / 2, fl / 2) }
            .Select(c => { var (x, z) = ArchitecturalElement.RotateY(c.Item1, c.Item2, rotationY); return (X: cx + x, Z: cz + z); }).ToList();
        foreach (var slab in scene.Where(s => s.BuildingGroupId == groupId && s.CanHostSlabOpenings && s.Type is not (ElementTypes.Foundation or ElementTypes.Terrace)
                                              && s.BaseY > baseElevation + floorHeight / 2 + 0.3 && s.BaseY < top + 0.3 && s.TopY > top - 0.3))
        {
            var slabOutline = ClashDetectionService.PlanOutline(slab);
            if (Geometry2D.IntersectionArea(footprint, slabOutline) < 0.15 * fw * fl) continue;
            var overlapping = SceneStateService.OpeningsFor(slab, scene).Where(h => Geometry2D.IntersectionArea(footprint, ClashDetectionService.PlanOutline(h)) > 1e-4).ToList();
            var opening = new ArchitecturalElement
            {
                Type = ElementTypes.SlabOpening,
                Name = $"Проём лестницы {prefix}",
                BuildingGroupId = slab.BuildingGroupId,
                LevelIndex = slab.LevelIndex,
                Material = "void",
                HostElementId = slab.Id,
                Rotation = [0, ArchitecturalElement.NormalizeAngle(rotationY), 0]
            };
            if (circle is not null && overlapping.Count == 0)
            {
                opening.Polygon2D = circle.Select(p => new Point2D(Math.Round(p.X, 4), Math.Round(p.Z, 4))).ToList();
                double d = circle.Max(p => p.X) - circle.Min(p => p.X);
                opening.Position = [cx, slab.Y, cz];
                opening.Size = [d, slab.Height + ParametricSolverService.OpeningClearance, d];
            }
            else
            {
                var points = footprint.Concat(overlapping.SelectMany(ClashDetectionService.PlanOutline))
                    .Select(p => ArchitecturalElement.RotateY(p.X - cx, p.Z - cz, -rotationY)).ToList();
                double x0 = points.Min(p => p.X), x1 = points.Max(p => p.X), z0 = points.Min(p => p.Z), z1 = points.Max(p => p.Z);
                var (ccx, ccz) = ArchitecturalElement.RotateY((x0 + x1) / 2, (z0 + z1) / 2, rotationY);
                opening.Position = [cx + ccx, slab.Y, cz + ccz];
                opening.Size = [x1 - x0, slab.Height + ParametricSolverService.OpeningClearance, z1 - z0];
            }
            removed.AddRange(overlapping);
            created.Add(opening);
            cut++;
        }
        return (removed, cut);
    }
}
