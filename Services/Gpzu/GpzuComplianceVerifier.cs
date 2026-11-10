using System.Globalization;
using AIArchitectStudio.Models;
using AIArchitectStudio.Models.Gpzu;
using AIArchitectStudio.Rendering;

namespace AIArchitectStudio.Services.Gpzu;

public enum GpzuSeverity { Ok, Info, Warning, Critical }

/// <summary>Замечание проверки посадки: код, важность, заголовок, пояснение, элементы, зона и место (центр в плане сцены).</summary>
public sealed record GpzuFinding(GpzuSeverity Severity, string Code, string Title, string Detail, IReadOnlyList<string> ElementIds, string? ZoneId, SitePoint? At = null);

/// <summary>Итог проверки посадки: замечания и показатели застройки.</summary>
public sealed class GpzuComplianceReport
{
    public List<GpzuFinding> Findings { get; } = [];
    public int BuildingElements { get; init; }
    public double FootprintArea { get; init; }
    public double CoveragePercent { get; init; }
    public double MaxHeight { get; init; }
    public double? TopAbs { get; init; }
    public double GrossFloorArea { get; init; }
    public double LivingAreaEstimate { get; init; }
    public double LivingRatio { get; init; }
    public int Critical => Findings.Count(f => f.Severity == GpzuSeverity.Critical);
    public int Warnings => Findings.Count(f => f.Severity == GpzuSeverity.Warning);
    public bool Passed => Critical == 0;
}

/// <summary>
/// Детерминированная проверка посадки здания на участок ГПЗУ (без моделей): выход контура за границу точек 1…N, попадание в
/// коридоры охранных зон сетей топосъёмки, превышение ограничения абсолютной высоты ПАТ (верх здания = отметка земли + высота),
/// лимит общей площади жилых помещений подзоны (оценка: поэтажная площадь перекрытий × коэффициент 0,70), наложение на
/// существующие ОКС, минимальная площадь участка и машино-места (справочно — по регламенту).
/// </summary>
public static class GpzuComplianceVerifier
{
    private static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");

    /// <summary>Доля общей площади жилых помещений в поэтажной площади многоквартирного дома (оценка для предпроектной проверки).</summary>
    public const double LivingRatioDefault = 0.70;

    public static GpzuComplianceReport Verify(GpzuSitePlan plan, IReadOnlyList<ArchitecturalElement> elements, double livingRatio = LivingRatioDefault)
    {
        var building = elements.Where(IsBuildingPart).ToList();
        var outlines = building.Select(e => (Element: e, Ring: ElementGeometryBuilder.WorldOutline(e).Select(p => new SitePoint(p.X, p.Z)).ToList())).ToList();
        double gross = 0, footprint = 0;
        foreach (var e in building.Where(e => e.Category is BimCategory.Slab or BimCategory.CantileverSlab))
            gross += Area(ElementGeometryBuilder.WorldOutline(e)) * Math.Max(1, e.RepeatLevels);
        var footprintElements = building.Where(e => e.Category is BimCategory.Foundation).ToList();
        if (footprintElements.Count == 0) footprintElements = building.Where(e => e.Category is BimCategory.Slab && e.LevelIndex == building.Min(b => b.LevelIndex)).ToList();
        foreach (var e in footprintElements) footprint += Area(ElementGeometryBuilder.WorldOutline(e));
        double maxTop = building.Count == 0 ? 0 : building.Max(e => e.TopY + Math.Max(0, e.RepeatLevels - 1) * e.LevelHeight);
        double ground = plan.GroundElevation;
        var report = new GpzuComplianceReport
        {
            BuildingElements = building.Count,
            FootprintArea = footprint,
            CoveragePercent = plan.Area > 0 ? footprint / plan.Area * 100 : 0,
            MaxHeight = maxTop,
            TopAbs = ground > 0 ? ground + maxTop : null,
            GrossFloorArea = gross,
            LivingAreaEstimate = gross * livingRatio,
            LivingRatio = livingRatio
        };
        var doc = plan.Document;
        if (building.Count == 0)
        {
            report.Findings.Add(new GpzuFinding(GpzuSeverity.Info, "GPZU-EMPTY", "На участке нет здания", "Посадите здание — проверка выполнится по его контуру, высоте и площади.", [], null));
            return report;
        }

        // 1. Граница участка.
        var outside = new List<string>();
        double protrusion = 0;
        SitePoint? worst = null;
        foreach (var (e, ring) in outlines)
        {
            bool bad = false;
            foreach (var p in ring)
                if (!SiteBoundaryBuilder.Contains(plan.Boundary, p.X, p.Z))
                {
                    bad = true;
                    double d = DistanceToRing(plan.Boundary, p.X, p.Z);
                    if (d > protrusion) { protrusion = d; worst = p; }
                }
            if (!bad) bad = EdgesCross(ring, plan.Boundary);
            if (bad) outside.Add(e.Id);
        }
        report.Findings.Add(outside.Count == 0
            ? new GpzuFinding(GpzuSeverity.Ok, "GPZU-BOUNDARY", $"Здание в границах участка (точки 1…{plan.Boundary.Count})", string.Create(Ru, $"Все {building.Count} элементов внутри контура ГПЗУ; площадь застройки {footprint:N0} м² ({report.CoveragePercent:0.0} % участка)."), [], "allowed")
            : new GpzuFinding(GpzuSeverity.Critical, "GPZU-BOUNDARY", "Выход за границу земельного участка",
                string.Create(Ru, $"Элементов за границей точек 1…{plan.Boundary.Count}: {outside.Count}, наибольший вынос {protrusion:0.00} м. Место допустимого размещения ОКС совпадает с границей участка (отступы не установлены)."),
                outside, "allowed", worst));

        // 2. Коридоры сетей.
        foreach (var zone in plan.Zones.Where(z => z.Zone.IsNetwork && z.Lines.Count > 0))
        {
            var hit = new List<string>();
            double min = double.MaxValue;
            SitePoint? at = null;
            foreach (var (e, ring) in outlines)
            {
                double d = DistanceRingToLines(ring, zone.Lines, out var p);
                if (d < min) { min = d; at = p; }
                if (d < zone.Zone.CorridorHalfWidth) hit.Add(e.Id);
            }
            var style = SiteBoundaryBuilder.StyleOf(zone.Zone.Kind);
            report.Findings.Add(hit.Count == 0
                ? new GpzuFinding(GpzuSeverity.Ok, "GPZU-NET-" + zone.Zone.Kind, $"{style.Title}: охранная зона не затронута",
                    string.Create(Ru, $"Ближайшее расстояние от здания до оси трассы {min:0.0} м ≥ {style.HalfWidth:0.#} м ({style.Normative})."), [], zone.Id)
                : new GpzuFinding(GpzuSeverity.Critical, "GPZU-NET-" + zone.Zone.Kind, $"{style.Title}: здание в охранной зоне",
                    string.Create(Ru, $"Элементов в коридоре ± {style.HalfWidth:0.#} м: {hit.Count}, до оси трассы {Math.Max(0, min):0.00} м. {style.Normative}. Нужен вынос сети или ТУ балансодержателя (ГПЗУ, раздел 5.1)."),
                    hit, zone.Id, at));
        }

        // 3. Высота: ограничение абсолютной отметки ПАТ.
        if (plan.CeilingAbs is { } ceiling && doc.CeilingZone is { } cz)
        {
            if (ground <= 0)
                report.Findings.Add(new GpzuFinding(GpzuSeverity.Warning, "GPZU-HEIGHT", "Отметка земли неизвестна",
                    string.Create(Ru, $"Ограничение {cz.HeightText(Ru)} ({cz.Name}) нельзя сравнить с высотой здания {maxTop:0.0} м — загрузите топосъёмку с отметками."), [], cz.Id));
            else
            {
                double top = ground + maxTop;
                var tallest = building.OrderByDescending(e => e.TopY + Math.Max(0, e.RepeatLevels - 1) * e.LevelHeight).Take(3).Select(e => e.Id).ToList();
                report.Findings.Add(top <= ceiling
                    ? new GpzuFinding(GpzuSeverity.Ok, "GPZU-HEIGHT", "Высота в пределах ограничений приаэродромной территории",
                        string.Create(Ru, $"Верх здания {top:0.00} м БС (земля {ground:0.00} + {maxTop:0.00} м) ниже {cz.HeightText(Ru)} — запас {ceiling - top:0.00} м ({cz.Name})."), [], cz.Id)
                    : new GpzuFinding(GpzuSeverity.Critical, "GPZU-HEIGHT", "Превышение ограничения абсолютной высоты ПАТ",
                        string.Create(Ru, $"Верх здания {top:0.00} м БС выше {cz.HeightText(Ru)} на {top - ceiling:0.00} м ({cz.Name}); допустимая высота над землёй {ceiling - ground:0.00} м. Нужна корректировка высоты или согласование с оператором аэродрома."),
                        tallest, cz.Id));
            }
        }

        // 4. Общая площадь жилых помещений (регламент подзоны).
        if (doc.Regulation.MaxLivingArea is { } maxLiving)
        {
            double living = report.LivingAreaEstimate;
            report.Findings.Add(living <= maxLiving
                ? new GpzuFinding(GpzuSeverity.Ok, "GPZU-LIVING", $"Жилая площадь в пределах лимита подзоны {doc.Regulation.Subzone}",
                    string.Create(Ru, $"Оценка {living:N0} м² (поэтажная {gross:N0} м² × {livingRatio:0.00}) ≤ {maxLiving:N0} м²; резерв {maxLiving - living:N0} м²."), [], null)
                : new GpzuFinding(GpzuSeverity.Critical, "GPZU-LIVING", $"Превышен лимит жилой площади подзоны {doc.Regulation.Subzone}",
                    string.Create(Ru, $"Оценка {living:N0} м² (поэтажная {gross:N0} м² × {livingRatio:0.00}) > {maxLiving:N0} м² на {living - maxLiving:N0} м² — уменьшите этажность или площадь этажа."), [], null));
        }

        // 5. Существующие ОКС под пятном застройки.
        var overlapped = plan.Existing.Where(o => outlines.Any(x => RingsOverlap(x.Ring, o.Footprint))).ToList();
        if (overlapped.Count > 0)
            report.Findings.Add(new GpzuFinding(GpzuSeverity.Warning, "GPZU-OKS", "Пятно застройки на существующих ОКС",
                $"Под зданием: {string.Join(", ", overlapped.Select(o => o.Label))} — потребуется снос или реконструкция (ГПЗУ, раздел 3.1).", [], null));

        // 6. Справочно: площадь участка и машино-места по регламенту.
        if (doc.Regulation.MinParcelArea is { } minArea)
            report.Findings.Add(new GpzuFinding(plan.Area >= minArea ? GpzuSeverity.Ok : GpzuSeverity.Critical, "GPZU-PARCEL", "Минимальная площадь участка",
                string.Create(Ru, $"{plan.Area:N2} м² (по Гауссу) {(plan.Area >= minArea ? "≥" : "<")} {minArea:N0} м² для многоэтажной жилой застройки."), [], null));
        if (doc.Regulation.MinParkingPlaces is { } parking)
            report.Findings.Add(new GpzuFinding(GpzuSeverity.Info, "GPZU-PARKING", $"Машино-места: не менее {parking}",
                "Количество машино-мест по регламенту подзоны проверяется в проекте (подземная автостоянка или открытые места) — в 3D-модели не подсчитывается.", [], null));
        if (plan.Constraints.Count > 0) VerifyConstraints(report, plan, building, outlines, maxTop, ground);
        return report;
    }

    /// <summary>
    /// Подбор посадки: перебор сдвигов (шаг step) контура здания внутри участка; допустимы сдвиги, при которых все вершины внутри
    /// границы и рёбра её не пересекают; из них — с наибольшим запасом до коридоров сетей (ось трассы − полуширина), затем ближе к центру.
    /// Возвращает сдвиг (dX, dZ) или null, если здание не помещается.
    /// </summary>
    public static (double Dx, double Dz, double Clearance)? FindPlacement(GpzuSitePlan plan, IReadOnlyList<ArchitecturalElement> elements, double step = 1.5,
        IReadOnlyList<IReadOnlyList<SitePoint>>? obstacles = null, double gap = 0)
    {
        var rings = elements.Where(IsBuildingPart).Select(e => ElementGeometryBuilder.WorldOutline(e).Select(p => new SitePoint(p.X, p.Z)).ToList()).ToList();
        if (rings.Count == 0) return null;
        var hull = rings.SelectMany(r => r).ToList();
        double cx = (hull.Min(p => p.X) + hull.Max(p => p.X)) / 2, cz = (hull.Min(p => p.Z) + hull.Max(p => p.Z)) / 2;
        var (minX, maxX, minZ, maxZ) = plan.Bounds;
        double tx0 = plan.Boundary.Average(p => p.X), tz0 = plan.Boundary.Average(p => p.Z);
        // Габаритные точки здания (внешние вершины колец) — для скорости проверяем только их и рёбра крайних колец.
        var outline = rings.OrderByDescending(r => Area(r.Select(p => (p.X, p.Z)).ToList())).First();
        (double, double, double)? best = null;
        double bestScore = double.MinValue;
        for (double x = minX; x <= maxX; x += step)
            for (double z = minZ; z <= maxZ; z += step)
            {
                double dx = x - cx, dz = z - cz;
                if (!hull.All(p => SiteBoundaryBuilder.Contains(plan.Boundary, p.X + dx, p.Z + dz))) continue;
                var moved = outline.Select(p => new SitePoint(p.X + dx, p.Z + dz)).ToList();
                if (EdgesCross(moved, plan.Boundary)) continue;
                // Волна 6: несколько строений на участке — разрыв gap до уже стоящих зданий.
                if (obstacles is { Count: > 0 } && obstacles.Any(o => RingDistance(moved, o) < gap)) continue;
                double clearance = double.MaxValue;
                foreach (var zone in plan.Zones.Where(zn => zn.Zone.IsNetwork && zn.Lines.Count > 0))
                    clearance = Math.Min(clearance, DistanceRingToLines(moved, zone.Lines, out _) - zone.Zone.CorridorHalfWidth);
                if (clearance == double.MaxValue) clearance = 50;
                double score = Math.Min(clearance, 8) * 10 - Math.Sqrt((x - tx0) * (x - tx0) + (z - tz0) * (z - tz0));
                if (score > bestScore) { bestScore = score; best = (dx, dz, clearance); }
            }
        return best;
    }

    /// <summary>Часть здания для проверки посадки: всё, кроме проёмов, пустот и объектов благоустройства.</summary>
    public static bool IsBuildingPart(ArchitecturalElement e) =>
        e.Category is not (BimCategory.Window or BimCategory.Door or BimCategory.WallOpening or BimCategory.SlabOpening or BimCategory.Asset);

    public static double Area(IReadOnlyList<(double X, double Z)> ring)
    {
        double s = 0;
        for (int i = 0; i < ring.Count; i++)
        {
            var a = ring[i];
            var b = ring[(i + 1) % ring.Count];
            s += a.X * b.Z - b.X * a.Z;
        }
        return Math.Abs(s) / 2;
    }

    private static double DistanceToRing(IReadOnlyList<SitePoint> ring, double x, double z)
    {
        double best = double.MaxValue;
        for (int i = 0; i < ring.Count; i++) best = Math.Min(best, SiteBoundaryBuilder.DistanceToSegment(ring[i], ring[(i + 1) % ring.Count], x, z));
        return best;
    }

    private static bool EdgesCross(IReadOnlyList<SitePoint> a, IReadOnlyList<SitePoint> b)
    {
        for (int i = 0; i < a.Count; i++)
            for (int j = 0; j < b.Count; j++)
                if (SiteBoundaryBuilder.SegmentsCross(a[i], a[(i + 1) % a.Count], b[j], b[(j + 1) % b.Count])) return true;
        return false;
    }

    /// <summary>Расстояние от контура до набора ломаных: 0 — трасса внутри контура или пересекает его.</summary>
    private static double DistanceRingToLines(IReadOnlyList<SitePoint> ring, IReadOnlyList<IReadOnlyList<SitePoint>> lines, out SitePoint at)
    {
        double best = double.MaxValue;
        at = ring.Count > 0 ? ring[0] : default;
        foreach (var line in lines)
        {
            for (int k = 0; k < line.Count; k++)
                if (SiteBoundaryBuilder.Contains(ring, line[k].X, line[k].Z)) { at = line[k]; return 0; }
            for (int k = 1; k < line.Count; k++)
                for (int i = 0; i < ring.Count; i++)
                {
                    var a = ring[i];
                    var b = ring[(i + 1) % ring.Count];
                    if (SiteBoundaryBuilder.SegmentsCross(a, b, line[k - 1], line[k])) { at = a; return 0; }
                    double d = Math.Min(Math.Min(SiteBoundaryBuilder.DistanceToSegment(line[k - 1], line[k], a.X, a.Z), SiteBoundaryBuilder.DistanceToSegment(line[k - 1], line[k], b.X, b.Z)),
                        Math.Min(SiteBoundaryBuilder.DistanceToSegment(a, b, line[k - 1].X, line[k - 1].Z), SiteBoundaryBuilder.DistanceToSegment(a, b, line[k].X, line[k].Z)));
                    if (d < best) { best = d; at = a; }
                }
        }
        return best;
    }

    /// <summary>Расстояние между контурами (0 — пересекаются или один внутри другого).</summary>
    public static double RingDistance(IReadOnlyList<SitePoint> a, IReadOnlyList<SitePoint> b)
    {
        if (RingsOverlap(a, b)) return 0;
        double best = double.MaxValue;
        foreach (var p in a) best = Math.Min(best, DistanceToRing(b, p.X, p.Z));
        foreach (var p in b) best = Math.Min(best, DistanceToRing(a, p.X, p.Z));
        return best;
    }

    /// <summary>
    /// Волна 6: проверки по подтверждённым пользователем ограничениям и «НО» документа (план участка → Constraints): предельная
    /// высота над землёй и абсолютная отметка, отступ от границ, буфер охранной / санитарной зоны вокруг сети, вынос сетей,
    /// лимит жилой площади, машино-места, пункты, требующие согласования (СТУ, оператор аэродрома, культурное наследие).
    /// Без подтверждённых пунктов проверка не меняется.
    /// </summary>
    private static void VerifyConstraints(GpzuComplianceReport report, GpzuSitePlan plan, List<ArchitecturalElement> building,
        List<(ArchitecturalElement Element, List<SitePoint> Ring)> outlines, double maxTop, double ground)
    {
        foreach (var c in plan.Constraints.Where(c => c.IsActive))
        {
            string head = c.Title.Length > 0 ? c.Title : c.Category.ToString();
            string src = (c.Clause.Length > 0 ? c.Clause + ", " : string.Empty) + (c.Source.Length > 0 ? c.Source : "подтверждено пользователем");
            if (c.MaxHeightM is { } hmax)
            {
                var tallest = building.OrderByDescending(e => e.TopY + Math.Max(0, e.RepeatLevels - 1) * e.LevelHeight).Take(3).Select(e => e.Id).ToList();
                report.Findings.Add(maxTop <= hmax + 1e-6
                    ? new GpzuFinding(GpzuSeverity.Ok, "GPZU-CNS-HEIGHT", $"Регламент:{head}", string.Create(Ru, $"Высота здания {maxTop:0.00} м ≤ {hmax:0.00} м ({src})."), [], null)
                    : new GpzuFinding(GpzuSeverity.Critical, "GPZU-CNS-HEIGHT", $"Регламент:превышена предельная высота — {head}",
                        string.Create(Ru, $"Высота здания {maxTop:0.00} м > {hmax:0.00} м на {maxTop - hmax:0.00} м ({src})."), tallest, null));
            }
            if (c.MaxHeightAbsM is { } habs && ground > 0)
            {
                double top = ground + maxTop;
                report.Findings.Add(top <= habs + 1e-6
                    ? new GpzuFinding(GpzuSeverity.Ok, "GPZU-CNS-HABS", $"Регламент:{head}", string.Create(Ru, $"Верх {top:0.00} м БС ≤ {habs:0.00} м БС ({src})."), [], null)
                    : new GpzuFinding(GpzuSeverity.Critical, "GPZU-CNS-HABS", $"Регламент:превышена абсолютная отметка — {head}",
                        string.Create(Ru, $"Верх {top:0.00} м БС > {habs:0.00} м БС ({src})."), [], null));
            }
            if (c.MinSetbackM is { } setback && setback > 0)
            {
                var bad = new List<string>();
                double worst = double.MaxValue;
                SitePoint? at = null;
                foreach (var (e, ring) in outlines)
                    foreach (var p in ring)
                    {
                        double d = SiteBoundaryBuilder.Contains(plan.Boundary, p.X, p.Z) ? DistanceToRing(plan.Boundary, p.X, p.Z) : 0;
                        if (d < worst) { worst = d; at = p; }
                        if (d < setback - 1e-6 && !bad.Contains(e.Id)) bad.Add(e.Id);
                    }
                report.Findings.Add(bad.Count == 0
                    ? new GpzuFinding(GpzuSeverity.Ok, "GPZU-CNS-SETBACK", $"Регламент:{head}", string.Create(Ru, $"Наименьший отступ здания от границы {worst:0.00} м ≥ {setback:0.0#} м ({src})."), [], null)
                    : new GpzuFinding(GpzuSeverity.Critical, "GPZU-CNS-SETBACK", $"Регламент:нарушен отступ от границы — {head}",
                        string.Create(Ru, $"Элементов ближе {setback:0.0#} м к границе: {bad.Count}, наименьший отступ {Math.Max(0, worst):0.00} м ({src})."), bad, null, at));
            }
            if (c.BufferRadiusM is { } buffer && buffer > 0 && c.Category is ConstraintCategory.ProtectionZone or ConstraintCategory.SanitaryZone or ConstraintCategory.NetworkRelocation)
            {
                var zones = plan.Zones.Where(z => z.Zone.IsNetwork && z.Lines.Count > 0 && (c.NetworkKind is null || z.Zone.Kind == c.NetworkKind)).ToList();
                if (zones.Count == 0)
                {
                    report.Findings.Add(new GpzuFinding(GpzuSeverity.Info, "GPZU-CNS-BUFFER", $"Регламент:{head}",
                        string.Create(Ru, $"Зона ± {buffer:0.0#} м: сети этого вида на участке не найдены в топосъёмке ({src})."), [], null));
                }
                else
                {
                    var hit = new List<string>();
                    double min = double.MaxValue;
                    foreach (var zone in zones)
                        foreach (var (e, ring) in outlines)
                        {
                            double d = DistanceRingToLines(ring, zone.Lines, out _);
                            min = Math.Min(min, d);
                            if (d < buffer && !hit.Contains(e.Id)) hit.Add(e.Id);
                        }
                    report.Findings.Add(hit.Count == 0
                        ? new GpzuFinding(GpzuSeverity.Ok, "GPZU-CNS-BUFFER", $"Регламент:{head}", string.Create(Ru, $"До оси сети {min:0.0} м ≥ {buffer:0.0#} м ({src})."), [], null)
                        : new GpzuFinding(c.RequiresNetworkRelocation ? GpzuSeverity.Warning : GpzuSeverity.Critical, "GPZU-CNS-BUFFER",
                            $"Регламент:здание в зоне ± {buffer:0.0#} м — {head}",
                            string.Create(Ru, $"Элементов в зоне: {hit.Count}, до оси {Math.Max(0, min):0.00} м ({src}).") +
                            (c.RequiresNetworkRelocation ? " Документ допускает размещение после выноса сети по ТУ." : string.Empty), hit, null));
                }
            }
            else if (c.RequiresNetworkRelocation)
                report.Findings.Add(new GpzuFinding(GpzuSeverity.Warning, "GPZU-CNS-RELOCATE", $"Регламент:{head}",
                    $"Требуется вынос (переустройство) сетей / технические условия балансодержателя до начала строительства ({src}).", [], null));
            if (c.MaxLivingAreaM2 is { } living)
                report.Findings.Add(report.LivingAreaEstimate <= living
                    ? new GpzuFinding(GpzuSeverity.Ok, "GPZU-CNS-LIVING", $"Регламент:{head}", string.Create(Ru, $"Жильё (оценка) {report.LivingAreaEstimate:N0} ≤ {living:N0} м² ({src})."), [], null)
                    : new GpzuFinding(GpzuSeverity.Critical, "GPZU-CNS-LIVING", $"Регламент:превышен лимит жилой площади — {head}",
                        string.Create(Ru, $"Жильё (оценка) {report.LivingAreaEstimate:N0} > {living:N0} м² ({src})."), [], null));
            if (c.MinParkingSpots is { } spots)
            {
                // Машино-места: площадь уровней паркингов (постройки с «Паркинг» в имени) / 25 м² на место.
                double parkingArea = building.Where(e => e.Category == BimCategory.Slab && (e.Name?.Contains("Паркинг", StringComparison.OrdinalIgnoreCase) ?? false))
                    .Sum(e => Area(ElementGeometryBuilder.WorldOutline(e)) * Math.Max(1, e.RepeatLevels));
                int estimate = (int)Math.Floor(parkingArea / 25.0);
                report.Findings.Add(new GpzuFinding(estimate >= spots ? GpzuSeverity.Ok : GpzuSeverity.Warning, "GPZU-CNS-PARKING", $"Регламент:{head}",
                    string.Create(Ru, $"Машино-мест в модели (оценка по площади паркингов, 25 м²/место): {estimate} {(estimate >= spots ? "≥" : "<")} {spots} ({src})."), [], null));
            }
            if (c.Impact == ConstraintImpact.RequiresApproval && !c.HasNumbers && !c.RequiresNetworkRelocation)
                report.Findings.Add(new GpzuFinding(GpzuSeverity.Warning, "GPZU-CNS-APPROVAL", $"Регламент:требуется согласование — {head}",
                    (c.ImpactText.Length > 0 ? c.ImpactText + " " : string.Empty) + $"({src}).", [], null));
        }
    }

    private static bool RingsOverlap(IReadOnlyList<SitePoint> a, IReadOnlyList<SitePoint> b) =>
        a.Any(p => SiteBoundaryBuilder.Contains(b, p.X, p.Z)) || b.Any(p => SiteBoundaryBuilder.Contains(a, p.X, p.Z)) || EdgesCross(a, b);
}
