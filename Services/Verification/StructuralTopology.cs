using AIArchitectStudio.Models;
using AIArchitectStudio.Rendering;

namespace AIArchitectStudio.Services.Verification;

/// <summary>
/// Топология несущего каркаса для проверщиков и исправлений: опоры под плитой, поле «расстояние до опоры» (точка
/// наибольшего пролёта), поверхность под точкой, отрезки сетки колонн, вертикальные конструкции под элементом.
/// Повторяемые параметрические элементы (RepeatLevels &gt; 1) считаются сквозными на всю свою высоту.
/// </summary>
public static class StructuralTopology
{
    /// <summary>Наибольший зазор между верхом опоры и низом плиты над ней (толщина перекрытия и капители), м.</summary>
    public const double SlabGap = 0.65;

    /// <summary>Вертикальная опора: колонна, ядро, несущая стена (явно ненесущая перегородка опорой не считается).</summary>
    public static bool IsVerticalBearing(ArchitecturalElement e) =>
        e.Category is BimCategory.Column or BimCategory.Core || e.Category == BimCategory.Wall && (e.UserParameters is null || StructuralRole.IsLoadBearing(e));

    public static bool IsLineMember(ArchitecturalElement e) => e.Category is BimCategory.Beam or BimCategory.Truss && e.Polygon2D.Count < 3;

    /// <summary>Плитные конструкции, на которые можно опереть колонну или стену.</summary>
    public static bool IsSurface(ArchitecturalElement e) =>
        e.Category is BimCategory.Slab or BimCategory.CantileverSlab or BimCategory.Foundation or BimCategory.Terrace || e.IsFlatRoof;

    /// <summary>Плиты перекрытий и покрытий, работающие на изгиб (кроме фундамента).</summary>
    public static bool IsSpanningSlab(ArchitecturalElement e) =>
        e.Category is BimCategory.Slab or BimCategory.CantileverSlab || e.IsFlatRoof;

    /// <summary>Верх элемента с учётом повторов этажей.</summary>
    public static double Top(ArchitecturalElement e) => e.RepeatLevels > 1 ? e.TopY + e.LevelHeight * (e.RepeatLevels - 1) : e.TopY;

    public static List<(double X, double Z)> Outline(ArchitecturalElement e) => ClashDetectionService.PlanOutline(e);

    /// <summary>Ось линейного элемента (стена, балка) в плане: концы по локальной X.</summary>
    public static ((double X, double Z) A, (double X, double Z) B) Axis(ArchitecturalElement e)
    {
        var (ax, az) = ArchitecturalElement.RotateY(-e.Width / 2, 0, e.RotationY);
        var (bx, bz) = ArchitecturalElement.RotateY(e.Width / 2, 0, e.RotationY);
        return ((e.X + ax, e.Z + az), (e.X + bx, e.Z + bz));
    }

    /// <summary>Вертикальные несущие и балки, на которые опирается плита (верх в пределах толщины перекрытия под её низом).</summary>
    public static List<ArchitecturalElement> SupportsUnder(ArchitecturalElement slab, IReadOnlyList<ArchitecturalElement> all)
    {
        var outline = Outline(slab);
        var result = new List<ArchitecturalElement>();
        foreach (var v in all)
        {
            if (ReferenceEquals(v, slab)) continue;
            bool vertical = IsVerticalBearing(v) && v.BaseY < slab.BaseY - 0.3 && Top(v) >= slab.BaseY - SlabGap && Top(v) <= slab.TopY + 0.05;
            bool beam = IsLineMember(v) && v.TopY >= slab.BaseY - 0.2 && v.TopY <= slab.TopY + 0.05;
            if (!vertical && !beam) continue;
            if (Geometry2D.IntersectionArea(Outline(v), outline) > 1e-4) result.Add(v);
        }
        return result;
    }

    /// <summary>Расстояние от точки плана до ближайшей опоры (0 — внутри контура колонны/ядра или на оси стены/балки).</summary>
    public static double DistanceToSupports(double x, double z, IReadOnlyList<ArchitecturalElement> supports)
    {
        double best = double.MaxValue;
        foreach (var s in supports)
        {
            double d;
            if (s.Category is BimCategory.Wall or BimCategory.Beam or BimCategory.Truss && s.Polygon2D.Count < 3)
            {
                var (a, b) = Axis(s);
                d = Math.Max(0, Geometry2D.DistanceToSegment(x, z, a.X, a.Z, b.X, b.Z) - s.Depth / 2);
            }
            else d = Geometry2D.DistanceToPolygon(Outline(s), x, z);
            if (d < best) best = d;
        }
        return best;
    }

    /// <summary>
    /// Точка плиты, наиболее удалённая от опор (центр самого большого пролёта), с учётом отверстий и запретных зон
    /// (лестницы, проёмы). Сетка с шагом step; полезна для расстановки промежуточных колонн.
    /// </summary>
    public static (double X, double Z, double Distance) WorstPoint(ArchitecturalElement slab, IReadOnlyList<ArchitecturalElement> supports,
        IReadOnlyList<List<(double X, double Z)>> keepOut, double step = 0.25, double edgeMargin = 0.6)
    {
        var outline = Outline(slab);
        var (x0, x1, z0, z1) = Geometry2D.Bounds(outline);
        (double X, double Z, double D) best = (slab.X, slab.Z, -1);
        for (double x = x0 + step / 2; x < x1; x += step)
            for (double z = z0 + step / 2; z < z1; z += step)
            {
                if (!Geometry2D.Contains(outline, x, z) || Geometry2D.DistanceToBoundary(outline, x, z) < edgeMargin) continue;
                if (keepOut.Any(k => Geometry2D.DistanceToPolygon(k, x, z) < 0.35)) continue;
                double d = DistanceToSupports(x, z, supports);
                if (d > best.D) best = (x, z, d);
            }
        return best;
    }

    /// <summary>Запретные зоны плиты для новых колонн: её отверстия, лестничные марши и проёмы в стенах этажа под ней.</summary>
    public static List<List<(double X, double Z)>> KeepOutZones(ArchitecturalElement slab, IReadOnlyList<ArchitecturalElement> all)
    {
        var zones = SceneStateService.OpeningsFor(slab, all).Select(Outline).ToList();
        foreach (var e in all)
        {
            if (e.Category == BimCategory.Stairs && e.TopY <= slab.TopY + 0.1 && e.TopY >= slab.BaseY - 4.5) zones.Add(Outline(e));
            if (ElementTypes.IsHostedType(e.Category) && e.TopY <= slab.BaseY + 0.05 && e.TopY >= slab.BaseY - 4.5) zones.Add(Outline(e));
            if (e.Category == BimCategory.Pool) zones.Add(Outline(e));
        }
        return zones;
    }

    /// <summary>Наивысшая плитная поверхность под точкой (верх не выше y + tolerance), не попадающая в отверстие.</summary>
    public static ArchitecturalElement? SurfaceBelow(double x, double z, double y, IReadOnlyList<ArchitecturalElement> all, double tolerance = 0.1,
        ArchitecturalElement? exclude = null)
    {
        ArchitecturalElement? best = null;
        foreach (var s in all)
        {
            if (ReferenceEquals(s, exclude) || !IsSurface(s) || s.RepeatLevels > 1) continue;
            if (s.TopY > y + tolerance) continue;
            if (best is not null && s.TopY <= best.TopY) continue;
            if (!Geometry2D.Contains(Outline(s), x, z)) continue;
            if (SceneStateService.OpeningsFor(s, all).Any(h => Geometry2D.Contains(Outline(h), x, z))) continue;
            best = s;
        }
        return best;
    }

    /// <summary>Вертикальная несущая конструкция, на которую опирается точка (x, z) на отметке y (верх в пределах толщины плиты).</summary>
    public static ArchitecturalElement? VerticalBelow(double x, double z, double y, IReadOnlyList<ArchitecturalElement> all, ArchitecturalElement? exclude = null, double inflate = 0.05)
    {
        foreach (var v in all)
        {
            if (ReferenceEquals(v, exclude) || !IsVerticalBearing(v)) continue;
            double top = Top(v);
            if (top > y + 0.05 || top < y - SlabGap - 0.1 || v.BaseY >= y - 0.3) continue;
            if (Geometry2D.DistanceToPolygon(Outline(v), x, z) <= inflate) return v;
        }
        return null;
    }

    /// <summary>Пары соседних опорных колонн плиты на общей оси (сетка колонн) без балки и стены между ними.</summary>
    public static List<(ArchitecturalElement A, ArchitecturalElement B)> GridSegments(ArchitecturalElement slab, IReadOnlyList<ArchitecturalElement> supports, double maxSpan = 13.0)
    {
        var columns = supports.Where(s => s.Category is BimCategory.Column or BimCategory.Core).ToList();
        var lines = supports.Where(s => s.Category is BimCategory.Wall or BimCategory.Beam or BimCategory.Truss && s.Polygon2D.Count < 3).ToList();
        var pairs = new List<(ArchitecturalElement, ArchitecturalElement)>();
        for (int i = 0; i < columns.Count; i++)
            for (int j = i + 1; j < columns.Count; j++)
            {
                var a = columns[i];
                var b = columns[j];
                bool alongX = Math.Abs(a.Z - b.Z) < 0.05, alongZ = Math.Abs(a.X - b.X) < 0.05;
                if (!alongX && !alongZ) continue;
                double len = Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Z - b.Z) * (a.Z - b.Z));
                if (len < 1.0 || len > maxSpan) continue;
                // Соседние: между ними на той же оси нет другой колонны.
                bool between = columns.Any(c => c != a && c != b && (alongX ? Math.Abs(c.Z - a.Z) < 0.05 && c.X > Math.Min(a.X, b.X) + 0.05 && c.X < Math.Max(a.X, b.X) - 0.05
                                                                             : Math.Abs(c.X - a.X) < 0.05 && c.Z > Math.Min(a.Z, b.Z) + 0.05 && c.Z < Math.Max(a.Z, b.Z) - 0.05));
                if (between) continue;
                double mx = (a.X + b.X) / 2, mz = (a.Z + b.Z) / 2;
                if (lines.Any(l =>
                    {
                        var (p, q) = Axis(l);
                        return Geometry2D.DistanceToSegment(mx, mz, p.X, p.Z, q.X, q.Z) < 0.2 && Geometry2D.DistanceToSegment(a.X, a.Z, p.X, p.Z, q.X, q.Z) < 0.5;
                    })) continue;
                var slabOutline = Outline(slab);
                if (!Geometry2D.Contains(slabOutline, mx, mz)) continue;
                pairs.Add((a, b));
            }
        return pairs;
    }

    /// <summary>Типичное сечение колонн набора (медиана), по умолчанию 400 × 400 мм.</summary>
    public static (double W, double D, string Profile, string Material) TypicalColumn(IEnumerable<ArchitecturalElement> columns)
    {
        var list = columns.Where(c => c.Category == BimCategory.Column && c.RepeatLevels <= 1).OrderBy(c => c.Width * c.Depth).ToList();
        if (list.Count == 0) return (0.4, 0.4, ProfileTypes.Rectangle, "concrete");
        var m = list[list.Count / 2];
        return (Math.Max(0.3, m.Width), Math.Max(0.3, m.Depth), m.EffectiveProfile == ProfileTypes.Polygon ? ProfileTypes.Rectangle : m.EffectiveProfile, m.Material);
    }

    /// <summary>Индекс этажа колонны, стоящей на поверхности (на плите этажа L — этаж L + 1, на фундаменте/земле — 0).</summary>
    public static int LevelAbove(ArchitecturalElement? surface) =>
        surface is null || surface.Category is BimCategory.Foundation or BimCategory.Terrace ? 0 : surface.LevelIndex + 1;

    /// <summary>Выпуклая оболочка опор плиты (для определения консольной зоны).</summary>
    public static List<(double X, double Z)> SupportHull(IReadOnlyList<ArchitecturalElement> supports) =>
        Geometry2D.ConvexHull(supports.Where(IsVerticalBearing).SelectMany(Outline));

    /// <summary>Отпечаток состояния сцены: меняется при любом изменении состава, положения или габаритов элементов.</summary>
    public static long Fingerprint(IEnumerable<ArchitecturalElement> elements)
    {
        long h = 17;
        foreach (var e in elements)
        {
            h = h * 31 + e.Id.GetHashCode(StringComparison.Ordinal);
            foreach (var v in e.Position) h = h * 31 + Math.Round(v * 1000).GetHashCode();
            foreach (var v in e.Size) h = h * 31 + Math.Round(v * 1000).GetHashCode();
            h = h * 31 + Math.Round(e.RotationY * 100).GetHashCode();
            h = h * 31 + e.Type.GetHashCode(StringComparison.Ordinal);
        }
        return h;
    }
}
