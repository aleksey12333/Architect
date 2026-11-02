using System.Globalization;
using AIArchitectStudio.Models;
using AIArchitectStudio.Rendering;

namespace AIArchitectStudio.Services;

/// <summary>
/// Умный поиск коллизий BIM-модели: геометрические пересечения в плане (Сазерленд — Ходжмен) с проверкой перекрытия по
/// высоте и инженерные правила — отверстие плиты не должно резать балку и зону опирания колонны (h0 от граней), проём стены —
/// колонну и габарит стены; над маршем нужен проём в плите и высота в свету не менее 2.0 м; однотипные конструкции не должны
/// перекрываться более чем на 35 % объёма. Для каждой коллизии — точка узла и автоисправление (одним шагом Undo).
/// </summary>
public static class ClashDetectionService
{
    private static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");
    private const double MinHeadroom = 2.0;
    private const double DuplicateRatio = 0.35;

    public static List<ClashItem> Detect(IReadOnlyList<ArchitecturalElement> elements)
    {
        var items = new List<ClashItem>();
        var outlines = new Dictionary<ArchitecturalElement, List<(double X, double Z)>>();
        List<(double X, double Z)> Outline(ArchitecturalElement e)
        {
            if (!outlines.TryGetValue(e, out var o)) outlines[e] = o = PlanOutline(e);
            return o;
        }

        var valid = elements.Where(e => e.Position.Concat(e.Size).All(double.IsFinite)).ToList();
        var beams = valid.Where(e => e.Category is BimCategory.Beam or BimCategory.Truss && e.Polygon2D.Count < 3).ToList();
        var columns = valid.Where(e => e.Category == BimCategory.Column).ToList();
        var slabs = valid.Where(e => e.CanHostSlabOpenings && e.Category != BimCategory.Foundation).ToList();

        CheckSlabOpenings(valid, beams, columns, Outline, items);
        CheckWallOpenings(valid, columns, Outline, items);
        CheckStairs(valid, beams, slabs, Outline, items);
        CheckDuplicates(valid, Outline, items);

        int n = 1;
        foreach (var item in items) item.Id = $"CL-{n++:D3}";
        return items.OrderBy(i => i.Severity).ThenBy(i => i.Category).ToList();
    }

    /// <summary>Контур в плане (мировые X, Z): круглые колонны — 24-угольником, остальное — Polygon2D или W × D.</summary>
    public static List<(double X, double Z)> PlanOutline(ArchitecturalElement e)
    {
        if (e.Category == BimCategory.Column && e.EffectiveProfile is ProfileTypes.Circle or ProfileTypes.Pipe && e.Polygon2D.Count < 3)
            return Enumerable.Range(0, 24).Select(i => 2 * Math.PI * i / 24).Select(a =>
            {
                var (x, z) = ArchitecturalElement.RotateY(e.Width / 2 * Math.Cos(a), e.Depth / 2 * Math.Sin(a), e.RotationY);
                return (e.X + x, e.Z + z);
            }).ToList();
        return ElementGeometryBuilder.WorldOutline(e);
    }

    /// <summary>Контур, раздвинутый на offset от граней элемента (зона опирания колонны).</summary>
    private static List<(double X, double Z)> Inflate(ArchitecturalElement e, double offset)
    {
        if (e.EffectiveProfile is ProfileTypes.Circle or ProfileTypes.Pipe && e.Polygon2D.Count < 3)
            return Enumerable.Range(0, 24).Select(i => 2 * Math.PI * i / 24).Select(a =>
            {
                var (x, z) = ArchitecturalElement.RotateY((e.Width / 2 + offset) * Math.Cos(a), (e.Depth / 2 + offset) * Math.Sin(a), e.RotationY);
                return (e.X + x, e.Z + z);
            }).ToList();
        double hw = e.Width / 2 + offset, hd = e.Depth / 2 + offset;
        return new[] { (-hw, -hd), (hw, -hd), (hw, hd), (-hw, hd) }.Select(p =>
        {
            var (x, z) = ArchitecturalElement.RotateY(p.Item1, p.Item2, e.RotationY);
            return (e.X + x, e.Z + z);
        }).ToList();
    }

    private static string Name(ArchitecturalElement e) => e.DisplayTitle;

    private static string M(double v) => v.ToString("0.00", Ru);

    // ───────────── 1. Отверстие плиты режет балку или колонну ─────────────

    private static ArchitecturalElement? HostSlab(ArchitecturalElement hole, IReadOnlyList<ArchitecturalElement> all) =>
        all.FirstOrDefault(h => h.Id == hole.HostElementId) ??
        all.FirstOrDefault(h => h.CanHostSlabOpenings && h.BuildingGroupId == hole.BuildingGroupId && h.LevelIndex == hole.LevelIndex &&
                                SceneStateService.PointInPolygon(hole.X, hole.Z, ElementGeometryBuilder.WorldOutline(h)));

    /// <summary>Каркас, затрагиваемый отверстием: балки под плитой или в её толще; колонны под плитой и на ней.</summary>
    private static IEnumerable<(ArchitecturalElement Member, List<(double X, double Z)> Zone, bool Column)> FrameAt(ArchitecturalElement slab,
        IReadOnlyList<ArchitecturalElement> beams, IReadOnlyList<ArchitecturalElement> columns, Func<ArchitecturalElement, List<(double X, double Z)>> outline)
    {
        foreach (var b in beams)
            if (b.TopY >= slab.BaseY - 0.15 && b.BaseY <= slab.TopY + 0.05) yield return (b, outline(b), false);
        double h0 = Math.Max(0.05, MaterialLayers.StructuralThickness(slab) - 0.03);
        foreach (var c in columns)
        {
            bool below = c.TopY >= slab.BaseY - 0.15 && c.TopY <= slab.TopY + 0.05;
            bool above = c.BaseY >= slab.TopY - 0.05 && c.BaseY <= slab.TopY + 0.15;
            if (below || above) yield return (c, Inflate(c, h0), true);
        }
    }

    private static void CheckSlabOpenings(IReadOnlyList<ArchitecturalElement> all, IReadOnlyList<ArchitecturalElement> beams, IReadOnlyList<ArchitecturalElement> columns,
        Func<ArchitecturalElement, List<(double X, double Z)>> outline, List<ClashItem> items)
    {
        foreach (var hole in all.Where(e => e.Category == BimCategory.SlabOpening))
        {
            if (HostSlab(hole, all) is not { } slab) continue;
            var ho = outline(hole);
            foreach (var (member, zone, isColumn) in FrameAt(slab, beams, columns, outline))
            {
                double cut = Geometry2D.IntersectionArea(ho, outline(member));
                double inZone = isColumn ? Geometry2D.IntersectionArea(ho, zone) : cut;
                if (inZone < 1e-3) continue;
                var where = Geometry2D.ClipConvex(ho, Geometry2D.IsConvex(zone) ? zone : outline(member));
                var (cx, cz) = where.Count >= 3 ? Geometry2D.Centroid(where) : (hole.X, hole.Z);
                bool critical = !isColumn || cut > 1e-3;
                string text = isColumn
                    ? cut > 1e-3
                        ? $"Отверстие «{Name(hole)}» в плите «{Name(slab)}» вырезает сечение колонны «{Name(member)}» ({M(cut)} м²)"
                        : $"Отверстие «{Name(hole)}» попадает в зону опирания колонны «{Name(member)}» (h0 от граней) — ослаблен контур продавливания"
                    : $"Отверстие «{Name(hole)}» в плите «{Name(slab)}» перерезает балку «{Name(member)}» ({M(cut)} м² в плане) — нарушена целостность каркаса";
                items.Add(new ClashItem
                {
                    Severity = critical ? ClashSeverity.Critical : ClashSeverity.Warning,
                    Category = ClashCategory.SlabOpeningCutsFrame,
                    ElementAId = hole.Id,
                    ElementBId = member.Id,
                    Description = text,
                    LocationPoint = [cx, slab.BaseY, cz],
                    Extent = Math.Max(2, Math.Max(hole.Width, hole.Depth) * 1.5),
                    Fix = ClashFix.MoveSlabOpening
                });
            }
        }
    }

    // ───────────── 2. Проём стены врезается в колонну или выходит за стену ─────────────

    private static void CheckWallOpenings(IReadOnlyList<ArchitecturalElement> all, IReadOnlyList<ArchitecturalElement> columns,
        Func<ArchitecturalElement, List<(double X, double Z)>> outline, List<ClashItem> items)
    {
        foreach (var o in all.Where(e => ElementTypes.IsHostedType(e.Category)))
        {
            var oo = outline(o);
            foreach (var c in columns)
            {
                double dy = Math.Min(o.TopY, c.TopY) - Math.Max(o.BaseY, c.BaseY);
                if (dy <= 0.05) continue;
                double cut = Geometry2D.IntersectionArea(oo, outline(c));
                if (cut < 1e-4) continue;
                var where = Geometry2D.ClipConvex(oo, Geometry2D.IsConvex(outline(c)) ? outline(c) : Geometry2D.ConvexHull(outline(c)));
                var (cx, cz) = where.Count >= 3 ? Geometry2D.Centroid(where) : (c.X, c.Z);
                items.Add(new ClashItem
                {
                    Severity = ClashSeverity.Critical,
                    Category = ClashCategory.WallOpeningHitsColumn,
                    ElementAId = o.Id,
                    ElementBId = c.Id,
                    Description = $"{(o.Category == BimCategory.Window ? "Окно" : o.Category == BimCategory.Door ? "Дверь" : "Проём")} «{Name(o)}» врезается в колонну «{Name(c)}» " +
                                  $"на {M(cut)} м² в плане и {M(dy)} м по высоте",
                    LocationPoint = [cx, (Math.Max(o.BaseY, c.BaseY) + Math.Min(o.TopY, c.TopY)) / 2, cz],
                    Extent = Math.Max(2, o.Width * 2),
                    Fix = o.HostElementId is not null ? ClashFix.MoveWallOpening : ClashFix.None
                });
            }

            if (all.FirstOrDefault(h => h.Id == o.HostElementId) is not { } wall || !ElementTypes.IsHostType(wall.Category)) continue;
            var (lx, _) = ArchitecturalElement.RotateY(o.X - wall.X, o.Z - wall.Z, -wall.RotationY);
            double left = -wall.Width / 2 - (lx - o.Width / 2);
            double right = lx + o.Width / 2 - wall.Width / 2;
            double below = wall.BaseY - o.BaseY;
            double above = o.TopY - wall.TopY;
            double over = Math.Max(Math.Max(left, right), Math.Max(below, above));
            if (over <= 0.01) continue;
            items.Add(new ClashItem
            {
                Severity = ClashSeverity.Warning,
                Category = ClashCategory.WallOpeningOutsideWall,
                ElementAId = o.Id,
                ElementBId = wall.Id,
                Description = $"Проём «{Name(o)}» выходит за габарит стены «{Name(wall)}» на {M(over)} м",
                LocationPoint = [o.X, o.Y, o.Z],
                Extent = Math.Max(2, o.Width * 2),
                Fix = ClashFix.ClampWallOpening
            });
        }
    }

    // ───────────── 3. Лестница: проём в плите и высота в свету ─────────────

    /// <summary>Отметка поверхности марша в точке плана (подъём вдоль локальной +Z от −D/2 к +D/2).</summary>
    private static double StairSurface(ArchitecturalElement st, double x, double z)
    {
        var (_, lz) = ArchitecturalElement.RotateY(x - st.X, z - st.Z, -st.RotationY);
        double t = Math.Clamp((lz + st.Depth / 2) / Math.Max(0.01, st.Depth), 0, 1);
        return st.BaseY + st.Height * t;
    }

    private static void CheckStairs(IReadOnlyList<ArchitecturalElement> all, IReadOnlyList<ArchitecturalElement> beams, IReadOnlyList<ArchitecturalElement> slabs,
        Func<ArchitecturalElement, List<(double X, double Z)>> outline, List<ClashItem> items)
    {
        foreach (var st in all.Where(e => e.Category == BimCategory.Stairs))
        {
            var fo = outline(st);
            double area = Math.Max(1e-3, Geometry2D.Area(fo));

            // Плиты над маршем без отверстия.
            foreach (var slab in slabs)
            {
                if (slab.BaseY <= st.BaseY + 0.5 || slab.BaseY > st.TopY + MinHeadroom + 0.2) continue;
                double overlap = Geometry2D.IntersectionArea(fo, outline(slab));
                if (overlap < 0.15 * area) continue;
                var holes = SceneStateService.OpeningsFor(slab, all).Select(outline).ToList();
                double covered = Math.Min(overlap, holes.Sum(h => Geometry2D.IntersectionArea(fo, h)));
                double open = overlap - covered;
                if (open < 0.2 * area) continue;
                bool none = covered < 0.05 * area;
                items.Add(new ClashItem
                {
                    Severity = ClashSeverity.Critical,
                    Category = ClashCategory.StairsWithoutOpening,
                    ElementAId = st.Id,
                    ElementBId = slab.Id,
                    Description = none
                        ? $"Над маршем «{Name(st)}» нет проёма в плите «{Name(slab)}» — плита перекрывает {M(open)} м² лестницы"
                        : $"Проём в плите «{Name(slab)}» перекрывает марш «{Name(st)}» лишь частично — закрыто {M(open)} м²",
                    LocationPoint = [st.X, slab.BaseY, st.Z],
                    Extent = Math.Max(3, Math.Max(st.Width, st.Depth) * 1.4),
                    Fix = ClashFix.CreateStairsOpening
                });
            }

            // Высота в свету под балками над маршем (сетка 6 × 12 точек по маршу).
            var (minClear, blocker, at) = (double.MaxValue, (ArchitecturalElement?)null, (X: 0.0, Y: 0.0, Z: 0.0));
            for (int i = 0; i < 6; i++)
                for (int j = 0; j < 12; j++)
                {
                    double lx = -st.Width / 2 + st.Width * (i + 0.5) / 6, lz = -st.Depth / 2 + st.Depth * (j + 0.5) / 12;
                    var (rx, rz) = ArchitecturalElement.RotateY(lx, lz, st.RotationY);
                    double x = st.X + rx, z = st.Z + rz;
                    double surface = StairSurface(st, x, z);
                    foreach (var b in beams)
                    {
                        if (b.BaseY <= surface - 0.01 || b.BaseY >= surface + MinHeadroom) continue;
                        if (!Geometry2D.Contains(outline(b), x, z)) continue;
                        double clear = b.BaseY - surface;
                        if (clear < minClear) (minClear, blocker, at) = (clear, b, (x, b.BaseY, z));
                    }
                }
            if (blocker is not null)
                items.Add(new ClashItem
                {
                    Severity = ClashSeverity.Critical,
                    Category = ClashCategory.StairsHeadroom,
                    ElementAId = st.Id,
                    ElementBId = blocker.Id,
                    Description = $"Балка «{Name(blocker)}» над маршем «{Name(st)}»: высота в свету {M(minClear)} м < {M(MinHeadroom)} м — поднимите балку или смените направление марша",
                    LocationPoint = [at.X, at.Y, at.Z],
                    Extent = Math.Max(3, st.Depth),
                    Fix = ClashFix.None
                });
        }
    }

    // ───────────── 4. Дубликаты и пересечения однотипных конструкций ─────────────

    private static void CheckDuplicates(IReadOnlyList<ArchitecturalElement> all, Func<ArchitecturalElement, List<(double X, double Z)>> outline, List<ClashItem> items)
    {
        foreach (var type in new[] { ElementTypes.Column, ElementTypes.Beam, ElementTypes.Wall })
        {
            var list = all.Where(e => e.Type == type).ToList();
            var boxes = list.Select(e => Geometry2D.Bounds(outline(e))).ToList();
            for (int i = 0; i < list.Count; i++)
                for (int j = i + 1; j < list.Count; j++)
                {
                    var a = list[i];
                    var b = list[j];
                    var (ax0, ax1, az0, az1) = boxes[i];
                    var (bx0, bx1, bz0, bz1) = boxes[j];
                    if (ax1 <= bx0 || bx1 <= ax0 || az1 <= bz0 || bz1 <= az0) continue;
                    double dy = Math.Min(a.TopY, b.TopY) - Math.Max(a.BaseY, b.BaseY);
                    if (dy <= 0.01) continue;
                    double plan = Geometry2D.IntersectionArea(outline(a), outline(b));
                    if (plan <= 1e-4) continue;
                    double va = Geometry2D.Area(outline(a)) * a.Height, vb = Geometry2D.Area(outline(b)) * b.Height;
                    double ratio = plan * dy / Math.Max(1e-6, Math.Min(va, vb));
                    if (ratio <= DuplicateRatio) continue;
                    // Удаляется меньший (или второй из одинаковых) элемент.
                    var (keep, remove) = vb <= va ? (a, b) : (b, a);
                    var where = Geometry2D.ClipConvex(outline(remove), Geometry2D.ConvexHull(outline(keep)));
                    var (cx, cz) = where.Count >= 3 ? Geometry2D.Centroid(where) : (remove.X, remove.Z);
                    string kind = type == ElementTypes.Column ? "колонны" : type == ElementTypes.Beam ? "балки" : "стены";
                    items.Add(new ClashItem
                    {
                        Severity = ratio > 0.9 ? ClashSeverity.Critical : ClashSeverity.Warning,
                        Category = ClashCategory.Duplicate,
                        ElementAId = keep.Id,
                        ElementBId = remove.Id,
                        Description = ratio > 0.9
                            ? $"Дубликат: {kind} «{Name(keep)}» и «{Name(remove)}» совпадают на {ratio * 100:0} % объёма"
                            : $"Пересечение: {kind} «{Name(keep)}» и «{Name(remove)}» перекрываются на {ratio * 100:0} % объёма",
                        LocationPoint = [cx, (Math.Max(a.BaseY, b.BaseY) + Math.Min(a.TopY, b.TopY)) / 2, cz],
                        Extent = Math.Max(2, Math.Max(remove.Width, remove.Depth) * 1.5),
                        Fix = ClashFix.DeleteDuplicate
                    });
                }
        }
    }

    // ═════════════════════════════ Автоисправление ═════════════════════════════

    /// <summary>Исправляет коллизию в сцене (один шаг Undo). Возвращает описание результата или null, если исправление невозможно.</summary>
    public static string? ApplyFix(SceneStateService scene, ClashItem item)
    {
        var a = scene.FindById(item.ElementAId);
        var b = scene.FindById(item.ElementBId);
        var all = scene.Elements.ToList();
        switch (item.Fix)
        {
            case ClashFix.MoveSlabOpening when a is not null:
                return MoveSlabOpening(scene, a, all);
            case ClashFix.MoveWallOpening when a is not null:
                return MoveWallOpening(scene, a, all);
            case ClashFix.ClampWallOpening when a is not null && b is not null:
                return ClampWallOpening(scene, a, b);
            case ClashFix.CreateStairsOpening when a is not null && b is not null:
                return CreateStairsOpening(scene, a, b, all);
            case ClashFix.DeleteDuplicate when b is not null:
                scene.RemoveElement(b);
                return $"Удалён дубликат «{Name(b)}»";
            default:
                return null;
        }
    }

    /// <summary>
    /// Минимальный сдвиг отверстия (8 направлений, шаг 5 см, до 8 м), при котором оно не задевает балки и зоны опирания колонн,
    /// остаётся внутри плиты и не налезает на другие отверстия.
    /// </summary>
    private static string? MoveSlabOpening(SceneStateService scene, ArchitecturalElement hole, List<ArchitecturalElement> all)
    {
        if (HostSlab(hole, all) is not { } slab) return null;
        var beams = all.Where(e => e.Category is BimCategory.Beam or BimCategory.Truss && e.Polygon2D.Count < 3).ToList();
        var columns = all.Where(e => e.Category == BimCategory.Column).ToList();
        var obstacles = FrameAt(slab, beams, columns, PlanOutline).Select(f => f.Zone).ToList();
        var slabOutline = ElementGeometryBuilder.WorldOutline(slab);
        var others = SceneStateService.OpeningsFor(slab, all).Where(o => o != hole).Select(PlanOutline).ToList();
        var outline = PlanOutline(hole);
        var (ux, uz) = ArchitecturalElement.RotateY(1, 0, slab.RotationY);
        var (vx, vz) = ArchitecturalElement.RotateY(0, 1, slab.RotationY);
        var dirs = new List<(double X, double Z)> { (ux, uz), (-ux, -uz), (vx, vz), (-vx, -vz) };
        double s2 = Math.Sqrt(0.5);
        dirs.AddRange([((ux + vx) * s2, (uz + vz) * s2), ((ux - vx) * s2, (uz - vz) * s2), ((-ux + vx) * s2, (-uz + vz) * s2), ((-ux - vx) * s2, (-uz - vz) * s2)]);

        for (double d = 0.05; d <= 8.0001; d += 0.05)
            foreach (var (dx, dz) in dirs)
            {
                var moved = outline.Select(p => (p.X + dx * d, p.Z + dz * d)).ToList();
                if (!moved.All(p => Geometry2D.Contains(slabOutline, p.Item1, p.Item2))) continue;
                if (obstacles.Any(o => Geometry2D.IntersectionArea(moved, o) > 1e-4)) continue;
                if (others.Any(o => Geometry2D.IntersectionArea(moved, o) > 1e-4)) continue;
                scene.SetTransform(hole, Math.Round(hole.X + dx * d, 3), hole.Y, Math.Round(hole.Z + dz * d, 3), hole.Width, hole.Height, hole.Depth, hole.RotationY, hole.LevelIndex);
                return $"Отверстие «{Name(hole)}» сдвинуто на {M(d)} м и не задевает каркас";
            }
        return null;
    }

    /// <summary>Сдвиг проёма вдоль стены к ближайшему свободному от колонн и других проёмов участку.</summary>
    private static string? MoveWallOpening(SceneStateService scene, ArchitecturalElement o, List<ArchitecturalElement> all)
    {
        if (all.FirstOrDefault(h => h.Id == o.HostElementId) is not { } wall) return null;
        double Local(double x, double z) => ArchitecturalElement.RotateY(x - wall.X, z - wall.Z, -wall.RotationY).X;
        var (lx, lz) = ArchitecturalElement.RotateY(o.X - wall.X, o.Z - wall.Z, -wall.RotationY);
        double half = o.Width / 2, limit = wall.Width / 2 - 0.05;
        var band = Inflate(wall, 0.02);
        var blocked = new List<(double A, double B)>();
        foreach (var c in all.Where(e => e.Category == BimCategory.Column))
        {
            if (Math.Min(o.TopY, c.TopY) - Math.Max(o.BaseY, c.BaseY) <= 0.05) continue;
            var co = PlanOutline(c);
            if (Geometry2D.IntersectionArea(band, co) < 1e-5) continue;
            var xs = co.Select(p => Local(p.X, p.Z)).ToList();
            blocked.Add((xs.Min() - 0.05, xs.Max() + 0.05));
        }
        foreach (var other in all.Where(e => e != o && e.HostElementId == wall.Id && ElementTypes.IsHostedType(e.Category)))
        {
            if (Math.Min(o.TopY, other.TopY) - Math.Max(o.BaseY, other.BaseY) <= 0.02) continue;
            double ox = Local(other.X, other.Z);
            blocked.Add((ox - other.Width / 2 - 0.1, ox + other.Width / 2 + 0.1));
        }
        var candidates = new List<double> { -limit + half, limit - half };
        foreach (var (a, b) in blocked)
        {
            candidates.Add(a - half);
            candidates.Add(b + half);
        }
        bool Free(double x) => x - half >= -limit - 1e-6 && x + half <= limit + 1e-6 && blocked.All(r => x + half <= r.A + 1e-6 || x - half >= r.B - 1e-6);
        var best = candidates.Where(Free).OrderBy(x => Math.Abs(x - lx)).Cast<double?>().FirstOrDefault();
        if (best is not { } nx) return null;
        var (wx, wz) = ArchitecturalElement.RotateY(nx, lz, wall.RotationY);
        scene.SetTransform(o, Math.Round(wall.X + wx, 3), o.Y, Math.Round(wall.Z + wz, 3), o.Width, o.Height, o.Depth, o.RotationY, o.LevelIndex);
        return $"Проём «{Name(o)}» сдвинут вдоль стены на {M(Math.Abs(nx - lx))} м от колонны";
    }

    /// <summary>Проём вписывается в стену: ширина и высота не больше стены, центр — в её пределах.</summary>
    private static string? ClampWallOpening(SceneStateService scene, ArchitecturalElement o, ArchitecturalElement wall)
    {
        double w = Math.Min(o.Width, Math.Max(0.3, wall.Width - 0.1));
        double h = Math.Min(o.Height, Math.Max(0.3, wall.Height - 0.05));
        var (lx, lz) = ArchitecturalElement.RotateY(o.X - wall.X, o.Z - wall.Z, -wall.RotationY);
        lx = Math.Clamp(lx, -wall.Width / 2 + w / 2 + 0.05, wall.Width / 2 - w / 2 - 0.05);
        double baseY = Math.Clamp(o.BaseY, wall.BaseY, wall.TopY - h);
        var (wx, wz) = ArchitecturalElement.RotateY(lx, lz, wall.RotationY);
        scene.SetTransform(o, Math.Round(wall.X + wx, 3), baseY + h / 2, Math.Round(wall.Z + wz, 3), w, h, o.Depth, o.RotationY, o.LevelIndex);
        return $"Проём «{Name(o)}» вписан в габарит стены «{Name(wall)}»";
    }

    /// <summary>
    /// Проём в плите над маршем: прямоугольник по маршу (+0.1 м с каждой стороны) в осях лестницы; частичные отверстия
    /// этой плиты над маршем объединяются в один проём (перекрывающиеся вырезы дали бы ошибку заливки).
    /// </summary>
    private static string? CreateStairsOpening(SceneStateService scene, ArchitecturalElement stairs, ArchitecturalElement slab, List<ArchitecturalElement> all)
    {
        var footprint = PlanOutline(stairs);
        var overlapping = SceneStateService.OpeningsFor(slab, all).Where(h => Geometry2D.IntersectionArea(footprint, PlanOutline(h)) > 1e-4).ToList();
        var points = footprint.Concat(overlapping.SelectMany(PlanOutline))
            .Select(p => ArchitecturalElement.RotateY(p.X - stairs.X, p.Z - stairs.Z, -stairs.RotationY)).ToList();
        const double margin = 0.1;
        double x0 = points.Min(p => p.X) - margin, x1 = points.Max(p => p.X) + margin;
        double z0 = points.Min(p => p.Z) - margin, z1 = points.Max(p => p.Z) + margin;
        var (cx, cz) = ArchitecturalElement.RotateY((x0 + x1) / 2, (z0 + z1) / 2, stairs.RotationY);

        scene.PushUndo();
        foreach (var h in overlapping) scene.RemoveElement(h, recordUndo: false);
        var opening = new ArchitecturalElement
        {
            Type = ElementTypes.SlabOpening,
            Name = "Проём над лестницей",
            BuildingGroupId = slab.BuildingGroupId,
            LevelIndex = slab.LevelIndex,
            Material = "void",
            HostElementId = slab.Id,
            Position = [stairs.X + cx, slab.Y, stairs.Z + cz],
            Size = [x1 - x0, slab.Height + ParametricSolverService.OpeningClearance, z1 - z0],
            Rotation = [0, stairs.RotationY, 0]
        };
        scene.AddElement(opening, recordUndo: false);
        return $"В плите «{Name(slab)}» создан проём {M(x1 - x0)} × {M(z1 - z0)} м над маршем «{Name(stairs)}»";
    }
}
