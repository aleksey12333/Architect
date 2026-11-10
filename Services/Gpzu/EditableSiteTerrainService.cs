using System.Globalization;
using AIArchitectStudio.Models;
using AIArchitectStudio.Models.Gpzu;
using AIArchitectStudio.Rendering;

namespace AIArchitectStudio.Services.Gpzu;

/// <summary>Слой редактируемой вершины участка: граница (точки 1…N) или контур зоны (пятно застройки, местная ЗОУИТ).</summary>
public enum SiteVertexLayer { Boundary, Zone }

/// <summary>Ссылка на вершину участка: слой, Id зоны (для зоны) и индекс вершины в контуре.</summary>
public readonly record struct SiteVertexRef(SiteVertexLayer Layer, string? ZoneId, int Index)
{
    public static SiteVertexRef Boundary(int index) => new(SiteVertexLayer.Boundary, null, index);
    public static SiteVertexRef Zone(string id, int index) => new(SiteVertexLayer.Zone, id, index);
}

/// <summary>Показатели живого участка: площадь по Гауссу, периметр, стороны, пятно застройки, земляные работы, проверка посадки.</summary>
public sealed record SiteMetrics(double GaussArea, double Perimeter, IReadOnlyList<SiteSegment> Sides, double AllowedArea, SiteEarthworks Earthworks,
    GpzuComplianceReport Compliance, int Buildings, double MinRelief, double MaxRelief);

/// <summary>Вид строения, вызываемого на участок (несколько строений на одном участке).</summary>
public enum SiteStructureKind { ResidentialSection, Tower, TransformerSubstation, Parking }

/// <summary>
/// Волна 6 — участок ГПЗУ как живая изменяемая земля проекта. Каждая операция — чистая функция «план → новый план»
/// (план неизменяем): перенос поворотной точки границы или вершины зоны (в сцене или в МСК), вставка и удаление вершины, отметка
/// рельефа Z БС поворотной точки, набор подтверждённых ограничений. Зоны, совпадавшие с границей (пятно допустимого размещения,
/// ЗОУИТ на весь участок), следуют за границей. Начало координат сцены сохраняется — здания не сдвигаются.
/// Применение к документу — <see cref="SceneStateService.ApplySitePlan"/>: один шаг журнала (Undo/Redo возвращают прежний план).
/// Пересчёт на лету — <see cref="Analyze"/>: площадь Гаусса, длины сторон, пятно застройки, выемка/насыпь под площадками зданий,
/// проверка посадки всех зданий участка.
/// </summary>
public static class EditableSiteTerrainService
{
    private static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");

    /// <summary>Минимальная площадь контура (м²) — вырожденный контур не принимается.</summary>
    public const double MinRingArea = 1.0;

    // ═════════════════════════════ Вершины ═════════════════════════════

    /// <summary>Контур слоя в сцене (граница или контур зоны).</summary>
    public static IReadOnlyList<SitePoint> Ring(GpzuSitePlan plan, SiteVertexRef r) =>
        r.Layer == SiteVertexLayer.Boundary ? plan.Boundary : plan.Zones.FirstOrDefault(z => z.Id == r.ZoneId)?.Polygon ?? [];

    /// <summary>Положение вершины в сцене (null — нет такой).</summary>
    public static SitePoint? PointOf(GpzuSitePlan plan, SiteVertexRef r)
    {
        var ring = Ring(plan, r);
        return r.Index >= 0 && r.Index < ring.Count ? ring[r.Index] : null;
    }

    /// <summary>
    /// Редактируемые вершины: точки границы 1…N и вершины собственных контуров зон (пятно застройки, местные ЗОУИТ), если они не
    /// совпадают с границей (такие зоны следуют за границей автоматически).
    /// </summary>
    public static IEnumerable<(SiteVertexRef Ref, SitePoint Point, string Label)> EditableVertices(GpzuSitePlan plan)
    {
        for (int i = 0; i < plan.Boundary.Count; i++) yield return (SiteVertexRef.Boundary(i), plan.Boundary[i], plan.PointNumbers[i]);
        foreach (var z in plan.Zones.Where(IsZoneEditable))
            for (int i = 0; i < z.Polygon.Count; i++) yield return (SiteVertexRef.Zone(z.Id, i), z.Polygon[i], string.Empty);
    }

    public static bool IsZoneEditable(SiteZone z) =>
        z.Polygon.Count >= 3 && !z.Zone.CoversWholeParcel && !z.Zone.IsNetwork && z.Zone.Kind is GpzuZoneKind.AllowedArea or GpzuZoneKind.LocalZone;

    /// <summary>Перенос вершины в точку сцены (x, z). null — контур стал бы самопересекающимся или вырожденным.</summary>
    public static GpzuSitePlan? MoveVertex(GpzuSitePlan plan, SiteVertexRef r, double x, double z)
    {
        var (gx, gy) = plan.ToGeo(x, z);
        return MoveVertexGeo(plan, r, Math.Round(gx, 2), Math.Round(gy, 2));
    }

    /// <summary>Перенос вершины в координаты МСК (X — север, Y — восток).</summary>
    public static GpzuSitePlan? MoveVertexGeo(GpzuSitePlan plan, SiteVertexRef r, double geoX, double geoY)
    {
        var doc = plan.Document;
        if (r.Layer == SiteVertexLayer.Boundary)
        {
            if (r.Index < 0 || r.Index >= doc.Boundary.Count) return null;
            var ring = doc.Boundary.ToList();
            ring[r.Index] = ring[r.Index] with { X = geoX, Y = geoY };
            return WithBoundary(plan, ring, plan.VertexElevations.ToList());
        }
        if (FindZone(doc, r.ZoneId) is not { } zone || r.Index < 0 || r.Index >= zone.Polygon.Count) return null;
        var poly = zone.Polygon.ToList();
        poly[r.Index] = poly[r.Index] with { X = geoX, Y = geoY };
        return WithZonePolygon(plan, zone, poly);
    }

    /// <summary>Вставка вершины в середину ребра index → index + 1 (новая точка границы получает номер «нK»).</summary>
    public static GpzuSitePlan? InsertVertex(GpzuSitePlan plan, SiteVertexRef edge)
    {
        var doc = plan.Document;
        if (edge.Layer == SiteVertexLayer.Boundary)
        {
            int n = doc.Boundary.Count;
            if (edge.Index < 0 || edge.Index >= n) return null;
            var a = doc.Boundary[edge.Index];
            var b = doc.Boundary[(edge.Index + 1) % n];
            int k = 1;
            while (doc.Boundary.Any(p => p.Number == "н" + k.ToString(CultureInfo.InvariantCulture))) k++;
            var ring = doc.Boundary.ToList();
            ring.Insert(edge.Index + 1, new GeoPoint("н" + k.ToString(CultureInfo.InvariantCulture), Math.Round((a.X + b.X) / 2, 2), Math.Round((a.Y + b.Y) / 2, 2)));
            var elevations = Elevations(plan, n);
            double ea = elevations[edge.Index], eb = elevations[(edge.Index + 1) % n];
            elevations.Insert(edge.Index + 1, ea > 0 && eb > 0 ? Math.Round((ea + eb) / 2, 2) : 0);
            return WithBoundary(plan, ring, elevations);
        }
        if (FindZone(doc, edge.ZoneId) is not { } zone || edge.Index < 0 || edge.Index >= zone.Polygon.Count) return null;
        var poly = zone.Polygon.ToList();
        var p0 = poly[edge.Index];
        var p1 = poly[(edge.Index + 1) % poly.Count];
        poly.Insert(edge.Index + 1, new GeoPoint(string.Empty, Math.Round((p0.X + p1.X) / 2, 2), Math.Round((p0.Y + p1.Y) / 2, 2)));
        return WithZonePolygon(plan, zone, poly);
    }

    /// <summary>Удаление вершины (минимум 3). null — нельзя.</summary>
    public static GpzuSitePlan? RemoveVertex(GpzuSitePlan plan, SiteVertexRef r)
    {
        var doc = plan.Document;
        if (r.Layer == SiteVertexLayer.Boundary)
        {
            if (doc.Boundary.Count <= 3 || r.Index < 0 || r.Index >= doc.Boundary.Count) return null;
            var ring = doc.Boundary.ToList();
            ring.RemoveAt(r.Index);
            var elevations = Elevations(plan, doc.Boundary.Count);
            elevations.RemoveAt(r.Index);
            return WithBoundary(plan, ring, elevations);
        }
        if (FindZone(doc, r.ZoneId) is not { } zone || zone.Polygon.Count <= 3 || r.Index < 0 || r.Index >= zone.Polygon.Count) return null;
        var poly = zone.Polygon.ToList();
        poly.RemoveAt(r.Index);
        return WithZonePolygon(plan, zone, poly);
    }

    /// <summary>Отметка рельефа поворотной точки границы Z БС (м); 0 — вернуть к рельефу топосъёмки.</summary>
    public static GpzuSitePlan? SetVertexElevation(GpzuSitePlan plan, int index, double elevationBs)
    {
        if (index < 0 || index >= plan.Boundary.Count || !double.IsFinite(elevationBs) || elevationBs < 0 || elevationBs > 9000) return null;
        var elevations = Elevations(plan, plan.Boundary.Count);
        elevations[index] = Math.Round(elevationBs, 2);
        return plan.With(vertexElevations: elevations);
    }

    /// <summary>Подтверждённый набор ограничений (только действующие пункты).</summary>
    public static GpzuSitePlan WithConstraints(GpzuSitePlan plan, IEnumerable<ProjectConstraintItem> items) =>
        plan.With(constraints: items.Where(i => i.IsActive).ToList());

    /// <summary>Отметка поворотной точки по умолчанию: заданная, иначе рельеф топосъёмки в этой точке, иначе земля участка.</summary>
    public static double EffectiveVertexElevation(GpzuSitePlan plan, int index)
    {
        if (plan.VertexElevation(index) > 0) return plan.VertexElevation(index);
        if (index < 0 || index >= plan.Boundary.Count || plan.GroundElevation <= 0) return plan.GroundElevation;
        var p = plan.Boundary[index];
        return plan.GroundElevation + plan.Terrain().NaturalAt(p.X, p.Z);
    }

    private static List<double> Elevations(GpzuSitePlan plan, int count)
    {
        var list = new List<double>(count);
        for (int i = 0; i < count; i++) list.Add(plan.VertexElevation(i));
        return list;
    }

    private static GpzuZone? FindZone(GpzuDocument doc, string? id) => id is null ? null : doc.Zones.FirstOrDefault(z => z.Id == id);

    // ═════════════════════════════ Пересборка ═════════════════════════════

    private static GpzuSitePlan? WithBoundary(GpzuSitePlan plan, List<GeoPoint> ring, List<double> elevations)
    {
        if (!IsSimple(ring)) return null;
        var old = plan.Document.Boundary;
        var doc = CloneDocument(plan.Document, ring);
        // Зоны, совпадавшие с границей, следуют за ней (пятно размещения без отступов, ЗОУИТ на весь участок, подзоны ПАТ).
        for (int i = 0; i < doc.Zones.Count; i++)
        {
            var z = doc.Zones[i];
            if (z.Polygon.Count >= 3 && (z.CoversWholeParcel || Geodesy.SameRing(z.Polygon, old, 0.01)))
                doc.Zones[i] = CloneZone(z, [.. ring]);
        }
        var built = SiteBoundaryBuilder.Build(doc, plan.Topo, (plan.OriginX, plan.OriginY));
        // Существующие объекты, рельеф и ограничения — прежние (рельеф не зависит от контура).
        return built.With(existing: plan.Existing, vertexElevations: elevations, constraints: plan.Constraints, relief: plan.Relief);
    }

    private static GpzuSitePlan? WithZonePolygon(GpzuSitePlan plan, GpzuZone zone, List<GeoPoint> polygon)
    {
        if (!IsSimple(polygon)) return null;
        var doc = CloneDocument(plan.Document, plan.Document.Boundary.ToList());
        int idx = doc.Zones.IndexOf(zone);
        if (idx < 0) return null;
        doc.Zones[idx] = CloneZone(zone, polygon);
        var built = SiteBoundaryBuilder.Build(doc, plan.Topo, (plan.OriginX, plan.OriginY));
        return built.With(existing: plan.Existing, vertexElevations: plan.VertexElevations, constraints: plan.Constraints, relief: plan.Relief);
    }

    /// <summary>Неглубокая копия документа с новой границей и новым списком зон (сами зоны неизменяемы).</summary>
    public static GpzuDocument CloneDocument(GpzuDocument d, List<GeoPoint> boundary) => new()
    {
        Number = d.Number,
        IssueDate = d.IssueDate,
        Applicant = d.Applicant,
        Application = d.Application,
        Address = d.Address,
        Cadastral = d.Cadastral,
        DeclaredArea = d.DeclaredArea,
        CoordinateSystem = d.CoordinateSystem,
        Boundary = boundary,
        Regulation = d.Regulation,
        Zones = [.. d.Zones],
        Objects = d.Objects,
        Tables = d.Tables,
        Warnings = d.Warnings,
        GroundElevation = d.GroundElevation,
        SourceFile = d.SourceFile,
        PageCount = d.PageCount
    };

    public static GpzuZone CloneZone(GpzuZone z, List<GeoPoint> polygon) => new()
    {
        Id = z.Id,
        Code = z.Code,
        Name = z.Name,
        Kind = z.Kind,
        ExplicationNo = z.ExplicationNo,
        Restriction = z.Restriction,
        HeightLimitMin = z.HeightLimitMin,
        HeightLimitMax = z.HeightLimitMax,
        Polygon = polygon,
        Lines = z.Lines,
        CorridorHalfWidth = z.CorridorHalfWidth,
        Normative = z.Normative,
        Source = z.Source,
        ColorHex = z.ColorHex,
        CoversWholeParcel = z.CoversWholeParcel
    };

    /// <summary>Простой многоугольник: ≥ 3 вершин, площадь ≥ 1 м², рёбра не пересекаются (кроме соседних).</summary>
    public static bool IsSimple(IReadOnlyList<GeoPoint> ring)
    {
        int n = ring.Count;
        if (n < 3 || Geodesy.Area(ring) < MinRingArea) return false;
        var pts = ring.Select(p => new SitePoint(p.Y, p.X)).ToList();
        for (int i = 0; i < n; i++)
            for (int j = i + 1; j < n; j++)
            {
                if (j == i + 1 || i == 0 && j == n - 1) continue;
                if (SiteBoundaryBuilder.SegmentsCross(pts[i], pts[(i + 1) % n], pts[j], pts[(j + 1) % n])) return false;
            }
        return true;
    }

    // ═════════════════════════════ Показатели ═════════════════════════════

    /// <summary>Площадь контура сцены (м²).</summary>
    public static double RingArea(IReadOnlyList<SitePoint> ring) => GpzuComplianceVerifier.Area(ring.Select(p => (p.X, p.Z)).ToList());

    /// <summary>Площадки зданий: по постройке — выпуклая оболочка контуров нижнего этажа, проектная отметка — низ постройки.</summary>
    public static List<TerrainPad> BuildingPads(IReadOnlyList<ArchitecturalElement> elements)
    {
        var pads = new List<TerrainPad>();
        foreach (var g in elements.Where(GpzuComplianceVerifier.IsBuildingPart).GroupBy(e => e.BuildingGroupId))
        {
            int low = g.Min(e => e.LevelIndex);
            var pts = g.Where(e => e.LevelIndex == low).SelectMany(e => ElementGeometryBuilder.WorldOutline(e)).Select(p => new SitePoint(p.X, p.Z)).ToList();
            if (pts.Count < 3) continue;
            var hull = ConvexHull(pts);
            if (hull.Count < 3) continue;
            double level = Math.Max(0, g.Where(e => e.LevelIndex == low).Min(e => e.BaseY));
            // Планировочная отметка — низ первого этажа (фундамент заглублён ниже нуля — площадка на нуле).
            pads.Add(new TerrainPad(hull, level <= 0.05 ? 0 : level));
        }
        return pads;
    }

    /// <summary>Пересчёт на лету: площадь по Гауссу, длины сторон, пятно, земляные работы, проверка посадки всех зданий.</summary>
    public static SiteMetrics Analyze(GpzuSitePlan plan, IReadOnlyList<ArchitecturalElement> elements)
    {
        var pads = BuildingPads(elements);
        var terrain = plan.Terrain(pads);
        var earth = terrain.Earthworks(1.0);
        double allowed = plan.Zones.FirstOrDefault(z => z.Zone.Kind == GpzuZoneKind.AllowedArea && z.Polygon.Count >= 3) is { } a ? RingArea(a.Polygon) : RingArea(plan.Boundary);
        double perimeter = plan.Segments.Sum(s => s.Length);
        double minR = double.MaxValue, maxR = double.MinValue;
        var (x0, x1, z0, z1) = plan.Bounds;
        double step = Math.Max(1, Math.Max(x1 - x0, z1 - z0) / 40);
        for (double x = x0; x <= x1; x += step)
            for (double z = z0; z <= z1; z += step)
            {
                if (!SiteBoundaryBuilder.Contains(plan.Boundary, x, z)) continue;
                double h = terrain.NaturalAt(x, z);
                minR = Math.Min(minR, h);
                maxR = Math.Max(maxR, h);
            }
        if (minR > maxR) minR = maxR = 0;
        var report = GpzuComplianceVerifier.Verify(plan, elements);
        return new SiteMetrics(plan.Area, perimeter, plan.Segments, allowed, earth, report, pads.Count, minR, maxR);
    }

    /// <summary>Сводка показателей одной строкой для ленты и автотеста.</summary>
    public static string Describe(SiteMetrics m) => string.Create(Ru,
        $"S = {m.GaussArea:N2} м², P = {m.Perimeter:N2} м, пятно {m.AllowedArea:N2} м², рельеф {m.MinRelief:+0.00;-0.00;0.00}…{m.MaxRelief:+0.00;-0.00;0.00} м, " +
        $"выемка {m.Earthworks.CutM3:N1} м³ / насыпь {m.Earthworks.FillM3:N1} м³, зданий {m.Buildings}, проверка: {(m.Compliance.Passed ? "без нарушений" : $"нарушений {m.Compliance.Critical}")}");

    public static List<SitePoint> ConvexHull(IReadOnlyList<SitePoint> points)
    {
        var pts = points.Distinct().OrderBy(p => p.X).ThenBy(p => p.Z).ToList();
        if (pts.Count < 3) return pts;
        static double Cross(SitePoint o, SitePoint a, SitePoint b) => (a.X - o.X) * (b.Z - o.Z) - (a.Z - o.Z) * (b.X - o.X);
        var hull = new List<SitePoint>();
        foreach (var p in pts)
        {
            while (hull.Count >= 2 && Cross(hull[^2], hull[^1], p) <= 0) hull.RemoveAt(hull.Count - 1);
            hull.Add(p);
        }
        int lower = hull.Count + 1;
        for (int i = pts.Count - 2; i >= 0; i--)
        {
            var p = pts[i];
            while (hull.Count >= lower && Cross(hull[^2], hull[^1], p) <= 0) hull.RemoveAt(hull.Count - 1);
            hull.Add(p);
        }
        hull.RemoveAt(hull.Count - 1);
        return hull;
    }

    // ═════════════════════════════ Строения на участке ═════════════════════════════

    public static string StructureTitle(SiteStructureKind kind) => kind switch
    {
        SiteStructureKind.Tower => "Жилая башня",
        SiteStructureKind.TransformerSubstation => "Трансформаторная подстанция",
        SiteStructureKind.Parking => "Паркинг",
        _ => "Корпус ЖК"
    };

    /// <summary>
    /// Строение в локальных координатах (центр плана — начало, низ — 0): корпус ЖК 24 × 14 м (этажи повторами), башня 18 × 18 м
    /// со сквозным ядром, ТП 6 × 4 м, открытый паркинг 30 × 16 м в 2 уровня. Элементы — обычные BIM-элементы постройки groupId.
    /// </summary>
    public static List<ArchitecturalElement> CreateStructure(SiteStructureKind kind, string groupId, int floors = 0)
    {
        var list = new List<ArchitecturalElement>();
        ArchitecturalElement E(string type, string name, double x, double y, double z, double w, double h, double d, string material = "concrete", int repeat = 1, double levelHeight = 3.0)
        {
            var e = new ArchitecturalElement
            {
                Type = type,
                Name = name,
                BuildingGroupId = groupId,
                LevelIndex = 0,
                Material = material,
                Size = [w, h, d],
                Position = [x, y + h / 2, z]
            };
            if (repeat > 1)
            {
                e.RepeatLevels = repeat;
                e.LevelHeight = levelHeight;
            }
            list.Add(e);
            return e;
        }
        switch (kind)
        {
            case SiteStructureKind.TransformerSubstation:
            {
                E(ElementTypes.Foundation, "ТП: фундаментная плита", 0, -0.4, 0, 6.6, 0.4, 4.6);
                E(ElementTypes.Wall, "ТП: стена 1", 0, 0, -1.875, 6.0, 3.0, 0.25, "brick");
                E(ElementTypes.Wall, "ТП: стена 2", 0, 0, 1.875, 6.0, 3.0, 0.25, "brick");
                var w3 = E(ElementTypes.Wall, "ТП: стена 3", -2.875, 0, 0, 3.5, 3.0, 0.25, "brick");
                w3.Rotation = [0, 90, 0];
                var w4 = E(ElementTypes.Wall, "ТП: стена 4", 2.875, 0, 0, 3.5, 3.0, 0.25, "brick");
                w4.Rotation = [0, 90, 0];
                E(ElementTypes.Slab, "ТП: покрытие", 0, 3.0, 0, 6.4, 0.2, 4.4);
                break;
            }
            case SiteStructureKind.Parking:
            {
                E(ElementTypes.Foundation, "Паркинг: фундаментная плита", 0, -0.5, 0, 30.6, 0.5, 16.6);
                for (int i = 0; i <= 4; i++)
                    for (int j = 0; j <= 2; j++)
                        E(ElementTypes.Column, $"Паркинг: колонна {i + 1}/{(char)('А' + j)}", -15 + i * 7.5, 0, -8 + j * 8, 0.4, 2.8, 0.4, repeat: 2, levelHeight: 3.0);
                E(ElementTypes.Slab, "Паркинг: перекрытие", 0, 2.8, 0, 30.6, 0.2, 16.6, repeat: 2, levelHeight: 3.0);
                break;
            }
            case SiteStructureKind.Tower:
            {
                int n = floors > 0 ? floors : 16;
                E(ElementTypes.Foundation, "Башня: фундаментная плита", 0, -1.2, 0, 20, 1.2, 20);
                for (int i = 0; i < 4; i++)
                    for (int j = 0; j < 4; j++)
                    {
                        if (i is 1 or 2 && j is 1 or 2) continue;
                        E(ElementTypes.Column, $"Башня: колонна {i + 1}/{(char)('А' + j)}", -9 + i * 6, 0, -9 + j * 6, 0.6, 2.8, 0.6, repeat: n);
                    }
                E(ElementTypes.Core, "Башня: ядро жёсткости", 0, 0, 0, 7.0, 3.0, 7.0, repeat: n);
                E(ElementTypes.Slab, "Башня: перекрытие", 0, 2.8, 0, 19.6, 0.2, 19.6, repeat: n);
                break;
            }
            default:
            {
                int n = floors > 0 ? floors : 9;
                E(ElementTypes.Foundation, "Корпус: фундаментная плита", 0, -0.8, 0, 24.8, 0.8, 14.8);
                for (int i = 0; i <= 4; i++)
                    for (int j = 0; j <= 2; j++)
                        E(ElementTypes.Column, $"Корпус: колонна {i + 1}/{(char)('А' + j)}", -12 + i * 6, 0, -7 + j * 7, 0.5, 2.8, 0.5, repeat: n);
                E(ElementTypes.Slab, "Корпус: перекрытие", 0, 2.8, 0, 24.6, 0.2, 14.6, repeat: n);
                break;
            }
        }
        return list;
    }

    /// <summary>
    /// Посадка нового строения рядом с уже стоящими: подбор сдвига внутри границы с запасом до коридоров сетей и разрывом gap
    /// до существующих зданий. Возвращает сдвиг или null (места нет).
    /// </summary>
    public static (double Dx, double Dz, double Clearance)? FindFreePlace(GpzuSitePlan plan, IReadOnlyList<ArchitecturalElement> existing,
        IReadOnlyList<ArchitecturalElement> structure, double gap = 6)
    {
        var obstacles = BuildingPads(existing).Select(p => p.Outline).ToList();
        return GpzuComplianceVerifier.FindPlacement(plan, structure, 1.5, obstacles, gap);
    }
}
