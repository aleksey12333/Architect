using AIArchitectStudio.Models;

namespace AIArchitectStudio.Services;

/// <summary>
/// Инженерный аудит сцены после солвера: висящие в воздухе элементы, проёмы без стен и перекрывающиеся проёмы,
/// гибкость колонн, вылеты консолей, высотки без ядра жёсткости, отсутствие кровли.
/// Ошибки помечают элемент флагом HasPhysicsError (подсветка в 3D) и попадают в SolverReport.
/// </summary>
public static class StructuralAuditor
{
    private const double SupportTolerance = 0.12;
    private const double MaxCantilever = 6.0;
    private const double MaxSlenderness = 30.0;

    public static void Audit(IReadOnlyList<ArchitecturalElement> elements, SolverReport report)
    {
        int errorsBefore = report.Errors.Count;
        var flagged = new HashSet<ArchitecturalElement>();

        void Error(ArchitecturalElement e, string text)
        {
            e.HasPhysicsError = true;
            flagged.Add(e);
            report.Errors.Add($"«{e.DisplayTitle}»: {text}");
        }

        void Warn(ArchitecturalElement e, string text) => report.Warnings.Add($"«{e.DisplayTitle}»: {text}");

        foreach (var e in elements)
        {
            if (e.Position.Concat(e.Size).Concat(e.Rotation).Any(v => !double.IsFinite(v)))
            {
                Error(e, "недопустимые координаты или габариты");
                continue;
            }

            if (ElementTypes.IsHostedType(e.Category))
            {
                if (e.HostElementId is null || elements.All(h => h.Id != e.HostElementId))
                    Error(e, "проём не привязан к стене");
                continue;
            }

            if (e.Category == BimCategory.SlabOpening)
            {
                var slab = elements.FirstOrDefault(h => h.Id == e.HostElementId);
                if (slab is null) Error(e, "отверстие не привязано к плите");
                else if (e.Width >= slab.Width * 0.95 && e.Depth >= slab.Depth * 0.95)
                    Warn(e, $"отверстие почти целиком вырезает плиту «{slab.DisplayTitle}»");
                continue;
            }

            if (e.Category != BimCategory.Pool && e.Category != BimCategory.Foundation && e.BaseY < -0.05)
                Warn(e, $"ниже уровня земли ({e.BaseY:0.##} м)");

            if (e.Category == BimCategory.Column && Math.Min(e.Width, e.Depth) > 0 && e.Height / Math.Min(e.Width, e.Depth) > MaxSlenderness)
                Warn(e, $"гибкость колонны {e.Height / Math.Min(e.Width, e.Depth):0} > {MaxSlenderness:0} — увеличьте сечение");

            if (e.Category == BimCategory.Wall && e.Depth < 0.1)
                Warn(e, $"толщина стены {e.Depth:0.###} м меньше 0.1 м");

            if (NeedsSupport(e) && e.BaseY > SupportTolerance && !IsSupported(e, elements))
                Error(e, $"висит в воздухе на отметке {e.BaseY:0.##} м — нет опоры снизу");
        }

        AuditOpeningOverlaps(elements, report);
        AuditCantilevers(elements, report);
        AuditBuildings(elements, report);

        report.PhysicsErrors = flagged.Count;
        if (report.Errors.Count > errorsBefore)
            report.ErrorCode ??= "AIA-SOLVER-AUDIT";
    }

    private static bool NeedsSupport(ArchitecturalElement e) =>
        e.Category is BimCategory.Wall or BimCategory.Column or BimCategory.Stairs or BimCategory.Terrace or BimCategory.Asset or BimCategory.Pool
        && e.RepeatLevels <= 1;

    /// <summary>Опора — любой элемент постройки, чей верх совпадает с низом элемента и план которого пересекается с ним.</summary>
    private static bool IsSupported(ArchitecturalElement e, IReadOnlyList<ArchitecturalElement> all)
    {
        var (minX, maxX, minZ, maxZ) = SceneStateService.Bounds([e]);
        foreach (var s in all)
        {
            if (ReferenceEquals(s, e) || ElementTypes.IsHostedType(s.Category)) continue;
            double top = s.RepeatLevels > 1 ? s.TopY + s.LevelHeight * (s.RepeatLevels - 1) : s.TopY;
            if (s.BaseY > e.BaseY + SupportTolerance || top < e.BaseY - SupportTolerance) continue;
            var (sMinX, sMaxX, sMinZ, sMaxZ) = SceneStateService.Bounds([s]);
            if (sMinX < maxX && sMaxX > minX && sMinZ < maxZ && sMaxZ > minZ) return true;
        }
        return false;
    }

    private static void AuditOpeningOverlaps(IReadOnlyList<ArchitecturalElement> elements, SolverReport report)
    {
        foreach (var hostGroup in elements.Where(e => ElementTypes.IsHostedType(e.Category) && e.HostElementId is not null).GroupBy(e => e.HostElementId!))
        {
            var host = elements.FirstOrDefault(h => h.Id == hostGroup.Key);
            if (host is null) continue;
            var spans = hostGroup.Select(o =>
            {
                var (lx, _) = ArchitecturalElement.RotateY(o.X - host.X, o.Z - host.Z, -host.RotationY);
                return (Opening: o, X0: lx - o.Width / 2, X1: lx + o.Width / 2, Y0: o.BaseY, Y1: o.TopY);
            }).OrderBy(s => s.X0).ToList();

            for (int i = 0; i < spans.Count; i++)
                for (int j = i + 1; j < spans.Count && spans[j].X0 < spans[i].X1 - 0.02; j++)
                    if (spans[j].Y0 < spans[i].Y1 - 0.02 && spans[j].Y1 > spans[i].Y0 + 0.02)
                        report.Warnings.Add($"Проёмы «{spans[i].Opening.DisplayTitle}» и «{spans[j].Opening.DisplayTitle}» перекрываются в стене «{host.DisplayTitle}»");
        }
    }

    private static void AuditCantilevers(IReadOnlyList<ArchitecturalElement> elements, SolverReport report)
    {
        foreach (var slab in elements.Where(e => e.Category is BimCategory.CantileverSlab or BimCategory.Slab))
        {
            var supports = elements.Where(s => s.BuildingGroupId == slab.BuildingGroupId &&
                                               s.Category is BimCategory.Wall or BimCategory.CurtainWall or BimCategory.Column or BimCategory.Core &&
                                               s.BaseY < slab.BaseY && (s.RepeatLevels > 1 ? s.TopY + s.LevelHeight * (s.RepeatLevels - 1) : s.TopY) >= slab.BaseY - 0.3).ToList();
            if (supports.Count == 0) continue;
            var (minX, maxX, minZ, maxZ) = SceneStateService.Bounds([slab]);
            var (sMinX, sMaxX, sMinZ, sMaxZ) = SceneStateService.Bounds(supports);
            double overhang = new[] { sMinX - minX, maxX - sMaxX, sMinZ - minZ, maxZ - sMaxZ }.Max();
            if (overhang > MaxCantilever)
                report.Warnings.Add($"«{slab.DisplayTitle}»: вылет консоли {overhang:0.#} м больше {MaxCantilever:0} м — нужны подкосы или балки");
        }
    }

    private static void AuditBuildings(IReadOnlyList<ArchitecturalElement> elements, SolverReport report)
    {
        foreach (var group in elements.GroupBy(e => e.BuildingGroupId))
        {
            var items = group.ToList();
            int storeys = items.Max(e => e.LevelIndex + Math.Max(1, e.RepeatLevels));
            double height = items.Max(e => e.RepeatLevels > 1 ? e.TopY + e.LevelHeight * (e.RepeatLevels - 1) : e.TopY);
            bool hasCore = items.Any(e => e.Category == BimCategory.Core);
            if ((storeys >= 10 || height > 30) && !hasCore)
                report.Warnings.Add($"«{group.Key}»: высотное здание ({storeys} эт., {height:0} м) без ядра жёсткости (core)");

            bool hasWalls = items.Any(e => e.Category is BimCategory.Wall or BimCategory.CurtainWall);
            bool hasCover = items.Any(e => e.Category is BimCategory.Roof or BimCategory.Shell or BimCategory.Slab or BimCategory.CantileverSlab);
            if (hasWalls && !hasCover)
                report.Warnings.Add($"«{group.Key}»: нет перекрытия или кровли над стенами");
        }
    }
}
