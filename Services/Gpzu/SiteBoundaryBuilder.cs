using System.Globalization;
using System.Text.RegularExpressions;
using AIArchitectStudio.Models.Gpzu;

namespace AIArchitectStudio.Services.Gpzu;

/// <summary>Оформление охранного коридора сети: цвет, полуширина, норматив, подпись.</summary>
public sealed record NetworkStyle(GpzuZoneKind Kind, string Title, string ColorHex, double HalfWidth, string Normative);

/// <summary>
/// Построитель участка ГПЗУ в сцене. Геодезические оси МСК (X — север, Y — восток) переводятся в план сцены относительно
/// центроида контура: восток → +X = Y − Y₀, север → −Z = X − X₀ (север вверху плана, как у Солнца). Сюда же сводятся
/// коридоры сетей топосъёмки (буфер ± полуширина по нормативу), ЗОУИТ кадастрового слоя, контуры ОКС и отметка земли.
/// </summary>
public static partial class SiteBoundaryBuilder
{
    private static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");

    /// <summary>Цвет границы участка ГПЗУ (кобальт) и пятна допустимого размещения (изумруд).</summary>
    public const string BoundaryColor = "#2563EB";
    public const string AllowedColor = "#10B981";
    public const string ExistingColor = "#B07A64";
    public const string CeilingColor = "#3B82F6";

    /// <summary>Коридоры сетей: полуширина — от оси трассы до границы охранной зоны / нормативного расстояния до фундаментов.</summary>
    public static readonly IReadOnlyList<NetworkStyle> NetworkStyles =
    [
        new(GpzuZoneKind.HeatNetwork, "Теплосеть", "#EF4444", 3.0, "Типовые правила охраны тепловых сетей (приказ Минархстроя РФ № 197): 3 м от края конструкции"),
        new(GpzuZoneKind.GasPipeline, "Газопровод", "#EF4444", 2.0, "Правила охраны газораспределительных сетей (ПП РФ № 878): 2 м с каждой стороны"),
        new(GpzuZoneKind.PowerCable, "Электрокабель (КЛ)", "#F59E0B", 1.0, "ПП РФ № 160 от 24.02.2009: 1 м от крайних кабелей (КЛ до 1 кВ)"),
        new(GpzuZoneKind.WaterSupply, "Водопровод", "#06B6D4", 5.0, "СП 42.13330.2016, табл. 12.6: 5 м до фундаментов зданий"),
        new(GpzuZoneKind.Sewer, "Канализация", "#06B6D4", 3.0, "СП 42.13330.2016, табл. 12.6: 3 м до фундаментов зданий"),
        new(GpzuZoneKind.Communication, "Линия связи", "#A855F7", 2.0, "Правила охраны линий связи (ПП РФ № 578): 2 м с каждой стороны")
    ];

    public static NetworkStyle StyleOf(GpzuZoneKind kind) => NetworkStyles.First(s => s.Kind == kind);

    /// <summary>
    /// Дополняет ГПЗУ данными топосъёмки: коридоры сетей (по одной зоне на вид сети, трассы в радиусе reach от участка),
    /// ЗОУИТ кадастрового слоя рядом с участком, контуры ОКС (по кадастровому номеру и по зданиям внутри участка), отметку земли.
    /// </summary>
    public static void Merge(GpzuDocument doc, TopoSurvey topo, double reach = 110)
    {
        if (doc.Boundary.Count < 3) return;
        doc.Zones.RemoveAll(z => z.IsNetwork || z.Kind == GpzuZoneKind.LocalZone);
        double minX = doc.Boundary.Min(p => p.X) - reach, maxX = doc.Boundary.Max(p => p.X) + reach;
        double minY = doc.Boundary.Min(p => p.Y) - reach, maxY = doc.Boundary.Max(p => p.Y) + reach;
        bool Near(GeoPoint p) => p.X >= minX && p.X <= maxX && p.Y >= minY && p.Y <= maxY;
        foreach (var style in NetworkStyles)
        {
            var lines = new List<List<GeoPoint>>();
            var layers = new SortedSet<string>(StringComparer.Ordinal);
            foreach (var net in topo.Networks.Where(n => n.Kind == style.Kind))
            {
                // Оставляем непрерывные куски трассы рядом с участком.
                var piece = new List<GeoPoint>();
                for (int i = 0; i < net.Points.Count; i++)
                {
                    bool keep = Near(net.Points[i]) || (i > 0 && Near(net.Points[i - 1])) || (i + 1 < net.Points.Count && Near(net.Points[i + 1]));
                    if (keep) piece.Add(net.Points[i]);
                    else if (piece.Count >= 2) { lines.Add(piece); piece = []; }
                    else piece.Clear();
                }
                if (piece.Count >= 2) lines.Add(piece);
                layers.Add(net.Layer);
            }
            if (lines.Count == 0) continue;
            double length = lines.Sum(l => l.Zip(l.Skip(1), Geodesy.Length).Sum());
            doc.Zones.Add(new GpzuZone
            {
                Id = "net-" + style.Kind,
                Code = string.Join(", ", layers),
                Name = string.Create(Ru, $"Охранная зона: {style.Title} ± {style.HalfWidth:0.#} м ({lines.Count} тр., {length:N0} м)"),
                Kind = style.Kind,
                Restriction = "Размещение зданий в коридоре — только после переустройства (выноса) сети или по техническим условиям балансодержателя (ГПЗУ, раздел 5.1).",
                Lines = lines,
                CorridorHalfWidth = style.HalfWidth,
                Normative = style.Normative,
                Source = $"ИГДИ {Path.GetFileName(topo.SourceFile)}, слои: {string.Join(", ", layers)}",
                ColorHex = style.ColorHex
            });
        }
        // ЗОУИТ кадастрового слоя, пересекающие окно участка (крупные контуры аэродромных подзон уже есть из ГПЗУ).
        int local = 0;
        foreach (var z in topo.CadastralZones.Where(z => z.Points.Any(Near) && Geodesy.Area(z.Points) < 60_000))
        {
            if (doc.Zones.Any(d => d.Kind == GpzuZoneKind.LocalZone && Geodesy.SameRing(d.Polygon, z.Points, 0.05))) continue;
            doc.Zones.Add(new GpzuZone
            {
                Id = $"local-{++local}",
                Code = z.Label,
                Name = z.Label.Length > 0 ? $"ЗОУИТ {z.Label} (кадастровый слой)" : "ЗОУИТ кадастрового слоя",
                Kind = GpzuZoneKind.LocalZone,
                Restriction = "Сведения ЕГРН из кадастрового слоя топосъёмки; ограничения — по выписке на реестровый номер.",
                Polygon = [.. z.Points],
                Source = $"ИГДИ {Path.GetFileName(topo.SourceFile)}, слой {z.Layer}",
                ColorHex = GpzuParser.ZonePalette[(doc.Zones.Count + 3) % GpzuParser.ZonePalette.Length]
            });
        }
        // ОКС: контур по кадастровому номеру; здания топосъёмки внутри участка — без номера ЕГРН.
        foreach (var o in doc.Objects.Where(o => o.Cadastral.Length > 0))
            if (topo.CapitalContours.FirstOrDefault(c => c.Label == o.Cadastral && c.Points.Any(p => Geodesy.Contains(doc.Boundary, p.X, p.Y))) is { } c)
                o.Footprint = [.. c.Points];
        doc.GroundElevation = topo.GroundElevation(doc.Boundary) ?? doc.GroundElevation;
    }

    /// <summary>Здания топосъёмки, центр которых внутри участка (для объёмов существующих ОКС), с этажностью по подписи «5Ж», «2Н».</summary>
    public static IEnumerable<(TopoContour Contour, int Floors)> BuildingsInside(GpzuDocument doc, TopoSurvey topo)
    {
        foreach (var b in topo.Buildings)
        {
            var (cx, cy) = Geodesy.Centroid(b.Points);
            if (!Geodesy.Contains(doc.Boundary, cx, cy)) continue;
            int floors = FloorsLabel().Match(b.Label) is { Success: true } m ? int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : 1;
            yield return (b, floors);
        }
    }

    /// <summary>План участка в сцене: граница, стороны, зоны (контуры, оси и полосы коридоров), существующие объекты, потолок высоты.</summary>
    public static GpzuSitePlan Build(GpzuDocument doc, TopoSurvey? topo = null)
    {
        if (doc.Boundary.Count < 3) throw new InvalidOperationException("В документе нет контура земельного участка (меньше трёх характерных точек).");
        var (x0, y0) = doc.Centroid;
        // Начало координат округляется до 1 см — числа сцены остаются «ровными» и обратимыми.
        x0 = Math.Round(x0, 2);
        y0 = Math.Round(y0, 2);
        SitePoint S(GeoPoint p) => new(p.Y - y0, -(p.X - x0));
        var boundary = doc.Boundary.Select(S).ToList();
        var segments = new List<SiteSegment>();
        for (int i = 0; i < doc.Boundary.Count; i++)
        {
            var a = doc.Boundary[i];
            var b = doc.Boundary[(i + 1) % doc.Boundary.Count];
            var sa = boundary[i];
            var sb = boundary[(i + 1) % boundary.Count];
            segments.Add(new SiteSegment(a.Number, b.Number, Geodesy.Length(a, b), new SitePoint((sa.X + sb.X) / 2, (sa.Z + sb.Z) / 2), Geodesy.Bearing(a, b)));
        }
        var zones = new List<SiteZone>();
        int order = 0;
        foreach (var z in doc.Zones)
        {
            var lines = z.Lines.Select(l => (IReadOnlyList<SitePoint>)l.Select(S).ToList()).ToList();
            var corridor = z.CorridorHalfWidth > 0 ? lines.SelectMany(l => Buffer(l, z.CorridorHalfWidth)).ToList() : [];
            zones.Add(new SiteZone(z, z.Polygon.Select(S).ToList(), lines, corridor, order++));
        }
        var existing = new List<SiteObject>();
        foreach (var o in doc.Objects.Where(o => o.Footprint.Count >= 3))
            existing.Add(new SiteObject(o.Number, $"№ {o.Number}" + (o.Cadastral.Length > 0 ? " · " + o.Cadastral : string.Empty), o.Footprint.Select(S).ToList(), o.Height));
        if (topo is not null)
            foreach (var (b, floors) in BuildingsInside(doc, topo))
            {
                var ring = b.Points.Select(S).ToList();
                if (existing.Any(e => Overlaps(e.Footprint, ring))) continue;
                existing.Add(new SiteObject(0, floors > 1 ? $"Здание, {floors} эт." : "Здание", ring, Math.Max(3.5, floors * 3.0)));
            }
        double ground = doc.GroundElevation ?? 0;
        double? ceilingAbs = doc.CeilingAbs;
        return new GpzuSitePlan
        {
            Document = doc,
            Topo = topo,
            OriginX = x0,
            OriginY = y0,
            Boundary = boundary,
            PointNumbers = doc.Boundary.Select(p => p.Number).ToList(),
            Segments = segments,
            Zones = zones,
            Existing = existing,
            GroundElevation = ground,
            CeilingAbs = ceilingAbs,
            CeilingHeight = ceilingAbs is { } c && ground > 0 ? c - ground : null
        };
    }

    private static bool Overlaps(IReadOnlyList<SitePoint> a, IReadOnlyList<SitePoint> b)
    {
        double cx = b.Average(p => p.X), cz = b.Average(p => p.Z);
        return Contains(a, cx, cz);
    }

    /// <summary>Точка внутри контура сцены (чётность пересечений).</summary>
    public static bool Contains(IReadOnlyList<SitePoint> poly, double x, double z)
    {
        bool inside = false;
        for (int i = 0, j = poly.Count - 1; i < poly.Count; j = i++)
        {
            var a = poly[i];
            var b = poly[j];
            if ((a.Z > z) != (b.Z > z) && x < (b.X - a.X) * (z - a.Z) / (b.Z - a.Z) + a.X) inside = !inside;
        }
        return inside;
    }

    /// <summary>
    /// Полоса коридора вдоль ломаной: прямоугольник ± w на каждый отрезок и восьмиугольник в каждом изломе (скругление стыка) —
    /// объединение кусков по правилу ненулевой обмотки даёт сплошной коридор без щелей.
    /// </summary>
    public static IEnumerable<IReadOnlyList<SitePoint>> Buffer(IReadOnlyList<SitePoint> line, double w)
    {
        for (int i = 1; i < line.Count; i++)
        {
            var a = line[i - 1];
            var b = line[i];
            double dx = b.X - a.X, dz = b.Z - a.Z, len = Math.Sqrt(dx * dx + dz * dz);
            if (len < 1e-6) continue;
            double nx = -dz / len * w, nz = dx / len * w;
            yield return [new(a.X + nx, a.Z + nz), new(b.X + nx, b.Z + nz), new(b.X - nx, b.Z - nz), new(a.X - nx, a.Z - nz)];
        }
        for (int i = 1; i + 1 < line.Count; i++)
        {
            var c = line[i];
            yield return Enumerable.Range(0, 8).Select(k => new SitePoint(c.X + w * Math.Cos(k * Math.PI / 4), c.Z + w * Math.Sin(k * Math.PI / 4))).ToList();
        }
    }

    /// <summary>Расстояние от точки до ломаной (м).</summary>
    public static double DistanceToLine(IReadOnlyList<SitePoint> line, double x, double z)
    {
        double best = double.MaxValue;
        for (int i = 1; i < line.Count; i++)
            best = Math.Min(best, DistanceToSegment(line[i - 1], line[i], x, z));
        return best;
    }

    public static double DistanceToSegment(SitePoint a, SitePoint b, double x, double z)
    {
        double dx = b.X - a.X, dz = b.Z - a.Z, l2 = dx * dx + dz * dz;
        double t = l2 < 1e-12 ? 0 : Math.Clamp(((x - a.X) * dx + (z - a.Z) * dz) / l2, 0, 1);
        double px = a.X + t * dx - x, pz = a.Z + t * dz - z;
        return Math.Sqrt(px * px + pz * pz);
    }

    /// <summary>Пересечение отрезков (собственное, без касаний концами).</summary>
    public static bool SegmentsCross(SitePoint a, SitePoint b, SitePoint c, SitePoint d)
    {
        static double Cross(SitePoint o, SitePoint p, SitePoint q) => (p.X - o.X) * (q.Z - o.Z) - (p.Z - o.Z) * (q.X - o.X);
        double d1 = Cross(c, d, a), d2 = Cross(c, d, b), d3 = Cross(a, b, c), d4 = Cross(a, b, d);
        return ((d1 > 0 && d2 < 0) || (d1 < 0 && d2 > 0)) && ((d3 > 0 && d4 < 0) || (d3 < 0 && d4 > 0));
    }

    /// <summary>Штриховка контура: отрезки параллельных линий под углом angle с шагом step, обрезанные контуром (чётность).</summary>
    public static List<(SitePoint A, SitePoint B)> HatchSegments(IReadOnlyList<SitePoint> ring, double step, double angleDeg)
    {
        var result = new List<(SitePoint, SitePoint)>();
        if (ring.Count < 3 || step <= 0) return result;
        double a = angleDeg * Math.PI / 180, dx = Math.Cos(a), dz = Math.Sin(a), nx = -dz, nz = dx;
        double minT = double.MaxValue, maxT = double.MinValue;
        foreach (var p in ring)
        {
            double t = p.X * nx + p.Z * nz;
            minT = Math.Min(minT, t);
            maxT = Math.Max(maxT, t);
        }
        for (double t = Math.Ceiling(minT / step) * step; t <= maxT; t += step)
        {
            var hits = new List<double>();
            for (int i = 0; i < ring.Count; i++)
            {
                var p = ring[i];
                var q = ring[(i + 1) % ring.Count];
                double tp = p.X * nx + p.Z * nz, tq = q.X * nx + q.Z * nz;
                if ((tp > t) == (tq > t)) continue;
                double k = (t - tp) / (tq - tp);
                double x = p.X + (q.X - p.X) * k, z = p.Z + (q.Z - p.Z) * k;
                hits.Add(x * dx + z * dz);
            }
            hits.Sort();
            for (int i = 0; i + 1 < hits.Count; i += 2)
                result.Add((new SitePoint(nx * t + dx * hits[i], nz * t + dz * hits[i]), new SitePoint(nx * t + dx * hits[i + 1], nz * t + dz * hits[i + 1])));
        }
        return result;
    }

    /// <summary>Смещение замкнутого контура внутрь на d (миттер по биссектрисе, ограничен 3d у острых углов).</summary>
    public static List<SitePoint> Offset(IReadOnlyList<SitePoint> ring, double d)
    {
        int n = ring.Count;
        double area = 0;
        for (int i = 0; i < n; i++) area += ring[i].X * ring[(i + 1) % n].Z - ring[(i + 1) % n].X * ring[i].Z;
        double sign = area > 0 ? 1 : -1;
        var result = new List<SitePoint>(n);
        for (int i = 0; i < n; i++)
        {
            var p = ring[(i + n - 1) % n];
            var c = ring[i];
            var q = ring[(i + 1) % n];
            (double X, double Z) N(SitePoint a, SitePoint b)
            {
                double ex = b.X - a.X, ez = b.Z - a.Z, l = Math.Sqrt(ex * ex + ez * ez);
                return l < 1e-9 ? (0, 0) : (-ez / l * sign, ex / l * sign);
            }
            var n1 = N(p, c);
            var n2 = N(c, q);
            double bx = n1.X + n2.X, bz = n1.Z + n2.Z, bl = Math.Sqrt(bx * bx + bz * bz);
            if (bl < 1e-9) { result.Add(new SitePoint(c.X + n1.X * d, c.Z + n1.Z * d)); continue; }
            double cos = (bx * n1.X + bz * n1.Z) / bl;
            double m = Math.Min(d / Math.Max(cos, 1e-3), d * 3);
            result.Add(new SitePoint(c.X + bx / bl * m, c.Z + bz / bl * m));
        }
        return result;
    }

    /// <summary>Штрихи пунктира по замкнутому контуру: dash / gap (м), сдвиг фазы phase (м).</summary>
    public static List<(SitePoint A, SitePoint B)> Dashes(IReadOnlyList<SitePoint> ring, double dash, double gap, double phase, bool closed = true)
    {
        var result = new List<(SitePoint, SitePoint)>();
        double period = dash + gap;
        if (period <= 1e-6 || dash <= 0) return result;
        int count = closed ? ring.Count : ring.Count - 1;
        double arc = 0;
        // Штрихи в абсолютной координате дуги контура: [k·период − фаза; … + штрих] ∩ [начало стороны; конец] — без накопления
        // погрешности (пошаговое приращение при шаге ~1e-16 зацикливалось и исчерпывало память).
        for (int i = 0; i < count; i++)
        {
            var a = ring[i];
            var b = ring[(i + 1) % ring.Count];
            double len = Math.Sqrt((b.X - a.X) * (b.X - a.X) + (b.Z - a.Z) * (b.Z - a.Z));
            if (len < 1e-9) continue;
            double a0 = arc, a1 = arc + len;
            for (long k = (long)Math.Floor((a0 + phase) / period); ; k++)
            {
                double d0 = k * period - phase, d1 = d0 + dash;
                if (d0 >= a1) break;
                double s0 = Math.Max(d0, a0), s1 = Math.Min(d1, a1);
                if (s1 - s0 > 1e-6) result.Add((Lerp(a, b, (s0 - a0) / len), Lerp(a, b, (s1 - a0) / len)));
            }
            arc = a1;
        }
        return result;

        static SitePoint Lerp(SitePoint a, SitePoint b, double k) => new(a.X + (b.X - a.X) * k, a.Z + (b.Z - a.Z) * k);
    }

    [GeneratedRegex(@"(\d{1,2})\s*[НнЖжКкМмДд]")]
    private static partial Regex FloorsLabel();
}
