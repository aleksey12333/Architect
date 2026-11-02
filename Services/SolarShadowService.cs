using AIArchitectStudio.Models;
using AIArchitectStudio.Models.Geometry;
using AIArchitectStudio.Rendering;

namespace AIArchitectStudio.Services;

/// <summary>Сезон для расчёта инсоляции (склонение Солнца): лето — 22 июня, весна/осень — равноденствие, зима — 22 декабря.</summary>
public enum SolarSeason
{
    Summer,
    Equinox,
    Winter
}

/// <summary>
/// Положение Солнца: высота над горизонтом и азимут (от севера по часовой: 90° — восток, 180° — юг, 270° — запад);
/// Direction — единичный вектор из сцены на Солнце (север плана = −Z, восток = +X).
/// </summary>
public readonly record struct SunPosition(double Hours, SolarSeason Season, double AltitudeDeg, double AzimuthDeg, double DirX, double DirY, double DirZ)
{
    public bool IsAboveHorizon => AltitudeDeg > 0.5;

    /// <summary>Длина тени от вертикали высотой 1 м (м); бесконечность — Солнце у горизонта.</summary>
    public double ShadowRatio => IsAboveHorizon ? 1 / Math.Tan(AltitudeDeg * Math.PI / 180) : double.PositiveInfinity;

    /// <summary>Тёплый «золотой» свет у горизонта (0…1): 1 — рассвет/закат, 0 — высокое дневное Солнце.</summary>
    public double Warmth => Math.Clamp(1 - (AltitudeDeg - 4) / 26, 0, 1);
}

/// <summary>
/// Регион (город) строительства: широта и долгота участка (° с.ш. / ° в.д.) и стандартное смещение часового пояса, ч.
/// Положение Солнца, тени и расчёт инсоляции по СанПиН берутся по выбранному региону проекта.
/// </summary>
public sealed record SolarLocation(string Id, string NameRu, string NameEn, double Latitude, double Longitude, double UtcOffset)
{
    public string Name(bool russian) => russian ? NameRu : NameEn;

    /// <summary>«43.12° с.ш., 131.89° в.д., UTC+10».</summary>
    public string Coordinates(bool russian)
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        string lat = Math.Abs(Latitude).ToString("0.00", inv) + (russian ? (Latitude >= 0 ? "° с.ш." : "° ю.ш.") : (Latitude >= 0 ? "° N" : "° S"));
        string lon = Math.Abs(Longitude).ToString("0.00", inv) + (russian ? (Longitude >= 0 ? "° в.д." : "° з.д.") : (Longitude >= 0 ? "° E" : "° W"));
        return $"{lat}, {lon}, UTC{(UtcOffset >= 0 ? "+" : "−")}{Math.Abs(UtcOffset).ToString("0.#", inv)}";
    }
}

/// <summary>Каталог регионов строительства (центры городов, стандартное время без сезонного перевода).</summary>
public static class SolarLocations
{
    public const string MoscowId = "moscow";

    public static readonly IReadOnlyList<SolarLocation> All =
    [
        new(MoscowId, "Москва", "Moscow", 55.7558, 37.6173, 3),
        new("spb", "Санкт-Петербург", "Saint Petersburg", 59.9343, 30.3351, 3),
        new("kaliningrad", "Калининград", "Kaliningrad", 54.7104, 20.4522, 2),
        new("murmansk", "Мурманск", "Murmansk", 68.9585, 33.0827, 3),
        new("kazan", "Казань", "Kazan", 55.7963, 49.1088, 3),
        new("krasnodar", "Краснодар", "Krasnodar", 45.0355, 38.9753, 3),
        new("sochi", "Сочи", "Sochi", 43.5855, 39.7231, 3),
        new("yekaterinburg", "Екатеринбург", "Yekaterinburg", 56.8389, 60.6057, 5),
        new("novosibirsk", "Новосибирск", "Novosibirsk", 55.0084, 82.9357, 7),
        new("krasnoyarsk", "Красноярск", "Krasnoyarsk", 56.0153, 92.8932, 7),
        new("irkutsk", "Иркутск", "Irkutsk", 52.2870, 104.3050, 8),
        new("yakutsk", "Якутск", "Yakutsk", 62.0355, 129.6755, 9),
        new("khabarovsk", "Хабаровск", "Khabarovsk", 48.4802, 135.0719, 10),
        new("vladivostok", "Владивосток", "Vladivostok", 43.1155, 131.8855, 10),
        new("yuzhno_sakhalinsk", "Южно-Сахалинск", "Yuzhno-Sakhalinsk", 46.9591, 142.7380, 11),
        new("petropavlovsk", "Петропавловск-Камчатский", "Petropavlovsk-Kamchatsky", 53.0452, 158.6483, 12)
    ];

    public static SolarLocation Moscow => All[0];

    /// <summary>Регион по идентификатору (неизвестный или пустой — Москва).</summary>
    public static SolarLocation Resolve(string? id) => All.FirstOrDefault(l => string.Equals(l.Id, id, StringComparison.OrdinalIgnoreCase)) ?? Moscow;
}

/// <summary>Трапеция объединённой тени на земле: полоса z0…z1, левая и правая границы на обеих кромках.</summary>
public readonly record struct ShadowTrapezoid(double Z0, double Z1, double XL0, double XR0, double XL1, double XR1);

/// <summary>Результат построения падающих теней: выпуклые силуэты элементов и их объединение (без двойного затемнения).</summary>
public sealed class GroundShadow
{
    public required SunPosition Sun { get; init; }
    public required List<List<(double X, double Z)>> Silhouettes { get; init; }
    public required List<ShadowTrapezoid> Union { get; init; }
    public double Area => Union.Sum(t => (t.Z1 - t.Z0) * ((t.XR0 - t.XL0) + (t.XR1 - t.XL1)) / 2);
    public bool IsEmpty => Union.Count == 0;
}

/// <summary>
/// Солнечная инсоляция и геометрические падающие тени на участке. Положение Солнца — по астрономическим формулам для широты
/// и долготы участка (по умолчанию Москва, 55.75° с.ш., 37.62° в.д., UTC+3): склонение по сезону, часовой угол по истинному
/// солнечному времени. Тени: вершины призм элементов проецируются вдоль солнечного луча на плоскость земли, силуэт —
/// выпуклая оболочка проекций каждой выпуклой части плана (невыпуклый Polygon2D разбивается на части, поэтому тень
/// Г- и Н-образных плит повторяет их реальный контур); силуэты объединяются полосовой развёрткой без двойного затемнения.
/// </summary>
public static class SolarShadowService
{
    public const double DefaultLatitude = 55.75;
    public const double DefaultLongitude = 37.62;
    public const double DefaultUtcOffset = 3;

    /// <summary>Отметка теневого меша над землёй, м.</summary>
    public const double GroundY = 0.004;

    /// <summary>Наибольшая длина тени (от низкого Солнца), м — дальше тень обрезается.</summary>
    public const double MaxShadowLength = 160;

    public static double Declination(SolarSeason season) => season switch
    {
        SolarSeason.Summer => 23.44,
        SolarSeason.Winter => -23.44,
        _ => 0
    };

    /// <summary>Склонение Солнца (°) в день года dayOfYear (1…365) по формуле Купера: δ = 23.44° · sin(360° · (284 + N) / 365).</summary>
    public static double Declination(int dayOfYear) => 23.44 * Math.Sin(2 * Math.PI * (284 + dayOfYear) / 365.0);

    /// <summary>Положение Солнца в местное время hours для сезона в выбранном регионе проекта.</summary>
    public static SunPosition Compute(double hours, SolarSeason season, SolarLocation location) =>
        Compute(hours, season, location.Latitude, location.Longitude, location.UtcOffset);

    /// <summary>Положение Солнца в местное время hours (6.0 … 21.0) для сезона.</summary>
    public static SunPosition Compute(double hours, SolarSeason season, double latitude = DefaultLatitude, double longitude = DefaultLongitude, double utcOffset = DefaultUtcOffset) =>
        ComputeAt(hours, Declination(season), latitude, longitude, utcOffset, season);

    /// <summary>Положение Солнца при склонении declination (°) — для расчётных дат СанПиН (22 апреля, 22 февраля).</summary>
    public static SunPosition ComputeAt(double hours, double declination, SolarLocation location) =>
        ComputeAt(hours, declination, location.Latitude, location.Longitude, location.UtcOffset, SolarSeason.Equinox);

    private static SunPosition ComputeAt(double hours, double declination, double latitude, double longitude, double utcOffset, SolarSeason season)
    {
        const double deg = Math.PI / 180;
        // Истинное солнечное время: поправка на долготу относительно центрального меридиана часового пояса.
        double solar = hours + (longitude - utcOffset * 15) / 15;
        double h = (solar - 12) * 15 * deg;
        double phi = latitude * deg, delta = declination * deg;
        double sinAlt = Math.Sin(phi) * Math.Sin(delta) + Math.Cos(phi) * Math.Cos(delta) * Math.Cos(h);
        double alt = Math.Asin(Math.Clamp(sinAlt, -1, 1));
        double cosAz = (Math.Sin(delta) - Math.Sin(alt) * Math.Sin(phi)) / Math.Max(1e-9, Math.Cos(alt) * Math.Cos(phi));
        double az = Math.Acos(Math.Clamp(cosAz, -1, 1));
        if (h > 0) az = 2 * Math.PI - az;
        // Север плана — −Z, восток — +X.
        double cosAlt = Math.Cos(alt);
        return new SunPosition(hours, season, alt / deg, az / deg, Math.Sin(az) * cosAlt, Math.Sin(alt), -Math.Cos(az) * cosAlt);
    }

    /// <summary>Цвет солнечного света (R, G, B 0…1) по высоте Солнца: золотистый у горизонта, чистый дневной — высоко.</summary>
    public static (double R, double G, double B) LightColor(SunPosition sun)
    {
        if (!sun.IsAboveHorizon) return (0.28, 0.32, 0.42);
        double w = sun.Warmth;
        // 6500 K (дневной) → ~2700 K (золотой час).
        double r = 1.0, g = 0.97 - 0.27 * w, b = 0.94 - 0.56 * w;
        double intensity = 0.55 + 0.45 * Math.Clamp(Math.Sin(sun.AltitudeDeg * Math.PI / 180) * 1.6, 0, 1);
        return (r * intensity, g * intensity, b * intensity);
    }

    /// <summary>Падающие тени видимых элементов на плоскость земли при заданном положении Солнца.</summary>
    public static GroundShadow Build(IEnumerable<ArchitecturalElement> elements, SunPosition sun, double groundY = GroundY)
    {
        var silhouettes = new List<List<(double X, double Z)>>();
        if (sun.IsAboveHorizon)
        {
            double sx = sun.DirX / sun.DirY, sz = sun.DirZ / sun.DirY;
            double maxT = MaxShadowLength * sun.DirY / Math.Max(1e-6, Math.Sqrt(sun.DirX * sun.DirX + sun.DirZ * sun.DirZ));
            foreach (var e in elements)
            {
                if (!CastsShadow(e)) continue;
                // Невыпуклый контур (Г-, Т-, Н-образные и ступенчатые плиты и кровли) — тень по выпуклым частям реального
                // Polygon2D: тень призмы = объединение теней её выпуклых частей, без заливки «внутренних углов».
                foreach (var piece in ShadowPieces(e))
                {
                    var projected = new List<(double X, double Z)>();
                    foreach (var (x, y, z) in Corners(e, piece))
                    {
                        double t = Math.Min(Math.Max(0, y - groundY), maxT);
                        projected.Add((x - sx * t, z - sz * t));
                    }
                    var hull = ConvexHull(projected);
                    if (hull.Count >= 3 && Math.Abs(SignedArea(hull)) > 1e-6) silhouettes.Add(hull);
                }
            }
        }
        return new GroundShadow { Sun = sun, Silhouettes = silhouettes, Union = UnionTrapezoids(silhouettes) };
    }

    /// <summary>Отбрасывают тень: надземные несущие и ограждающие элементы (без пустот, бассейна и элементов ниже земли).</summary>
    public static bool CastsShadow(ArchitecturalElement e) =>
        !ElementTypes.IsVoidType(e.Category) && e.Category is not BimCategory.Pool && e.TopY + (e.RepeatLevels > 1 ? e.LevelHeight * (e.RepeatLevels - 1) : 0) > 0.05;

    /// <summary>
    /// Плановые части элемента для тени: контур Polygon2D (невыпуклый — выпуклыми частями) или прямоугольник W × D.
    /// У балок и ферм Polygon2D — путь стержня, а у диагрида — параметрическая оболочка: для них — выпуклая оболочка контура.
    /// </summary>
    public static List<List<(double X, double Z)>> ShadowPieces(ArchitecturalElement e)
    {
        if (e.Polygon2D.Count < 3)
            return [[(-e.Width / 2, -e.Depth / 2), (e.Width / 2, -e.Depth / 2), (e.Width / 2, e.Depth / 2), (-e.Width / 2, e.Depth / 2)]];
        var local = e.Polygon2D.Select(p => (p.X, p.Z)).ToList();
        if (e.Category is BimCategory.Beam or BimCategory.Truss or BimCategory.Diagrid || Geometry2D.IsConvex(local)) return [local];
        var pieces = Geometry2D.ConvexPieces(local);
        return pieces.Count > 0 ? pieces : [local];
    }

    /// <summary>Вершины призмы плановой части элемента (поворот, наклон, повторы этажей с закруткой и сужением).</summary>
    private static IEnumerable<(double X, double Y, double Z)> Corners(ArchitecturalElement e, List<(double X, double Z)> local)
    {
        bool tilt = e.Rotation.Length > 2 && (Math.Abs(e.Rotation[0]) > 0.01 || Math.Abs(e.Rotation[2]) > 0.01) && e.RepeatLevels <= 1;
        if (tilt)
        {
            var m = ElementGeometryBuilder.WorldMatrix(e, includeTilt: true);
            foreach (var (lx, lz) in local)
                foreach (var ly in new[] { -e.Height / 2, e.Height / 2 })
                {
                    var v = BimMath.TransformCoordinate(new System.Numerics.Vector3((float)lx, (float)ly, (float)lz), m);
                    yield return (v.X, v.Y, v.Z);
                }
            yield break;
        }

        int repeat = Math.Max(1, e.RepeatLevels);
        double taper = e.TaperScalePerFloor <= 0 ? 1 : e.TaperScalePerFloor, scale = 1;
        for (int i = 0; i < repeat; i++, scale *= taper)
        {
            // У башни — каждый 3-й этаж и последний (силуэт выпуклый, промежуточные этажи внутри оболочки).
            if (repeat > 6 && i % 3 != 0 && i != repeat - 1) continue;
            double rot = e.RotationY + e.TwistAnglePerFloor * i;
            double y0 = e.BaseY + e.LevelHeight * i, y1 = e.TopY + e.LevelHeight * i;
            foreach (var (lx, lz) in local)
            {
                var (wx, wz) = ArchitecturalElement.RotateY(lx * scale, lz * scale, rot);
                yield return (e.X + wx, y0, e.Z + wz);
                yield return (e.X + wx, y1, e.Z + wz);
            }
        }
    }

    /// <summary>Выпуклая оболочка (монотонная цепь Эндрю), обход против часовой стрелки в плоскости XZ.</summary>
    public static List<(double X, double Z)> ConvexHull(List<(double X, double Z)> points)
    {
        var pts = points.Distinct().OrderBy(p => p.X).ThenBy(p => p.Z).ToList();
        if (pts.Count < 3) return pts;
        static double Cross((double X, double Z) o, (double X, double Z) a, (double X, double Z) b) => (a.X - o.X) * (b.Z - o.Z) - (a.Z - o.Z) * (b.X - o.X);
        var hull = new List<(double X, double Z)>(pts.Count * 2);
        foreach (var p in pts)
        {
            while (hull.Count >= 2 && Cross(hull[^2], hull[^1], p) <= 1e-12) hull.RemoveAt(hull.Count - 1);
            hull.Add(p);
        }
        int lower = hull.Count + 1;
        for (int i = pts.Count - 2; i >= 0; i--)
        {
            var p = pts[i];
            while (hull.Count >= lower && Cross(hull[^2], hull[^1], p) <= 1e-12) hull.RemoveAt(hull.Count - 1);
            hull.Add(p);
        }
        hull.RemoveAt(hull.Count - 1);
        return hull;
    }

    public static double SignedArea(IReadOnlyList<(double X, double Z)> poly)
    {
        double s = 0;
        for (int i = 0; i < poly.Count; i++)
        {
            var a = poly[i];
            var b = poly[(i + 1) % poly.Count];
            s += a.X * b.Z - b.X * a.Z;
        }
        return s / 2;
    }

    /// <summary>
    /// Объединение выпуклых силуэтов полосовой развёрткой по Z: границы полос — вершины силуэтов и шаг не более 0.1 м;
    /// в полосе каждый силуэт даёт интервал по X, пересекающиеся интервалы сливаются в одну трапецию (границы — по точным рёбрам).
    /// </summary>
    public static List<ShadowTrapezoid> UnionTrapezoids(IReadOnlyList<List<(double X, double Z)>> polygons, double maxStrip = 0.1)
    {
        var result = new List<ShadowTrapezoid>();
        if (polygons.Count == 0) return result;
        var events = polygons.SelectMany(p => p.Select(v => v.Z)).Distinct().OrderBy(z => z).ToList();
        var ranges = polygons.Select(p => (Min: p.Min(v => v.Z), Max: p.Max(v => v.Z))).ToArray();
        var order = Enumerable.Range(0, polygons.Count).OrderBy(i => ranges[i].Min).ToArray();
        var intervals = new List<(double Mid0, double L0, double R0, double L1, double R1, double LM, double RM)>();
        int next = 0;
        var active = new List<int>();

        for (int k = 0; k + 1 < events.Count; k++)
        {
            double za = events[k], zb = events[k + 1];
            if (zb - za < 1e-9) continue;
            while (next < order.Length && ranges[order[next]].Min <= za + 1e-12) active.Add(order[next++]);
            active.RemoveAll(i => ranges[i].Max <= za + 1e-12);
            int steps = Math.Max(1, (int)Math.Ceiling((zb - za) / maxStrip));
            for (int s = 0; s < steps; s++)
            {
                double z0 = za + (zb - za) * s / steps, z1 = za + (zb - za) * (s + 1) / steps, zm = (z0 + z1) / 2;
                intervals.Clear();
                foreach (int i in active)
                {
                    if (ranges[i].Max < zm || ranges[i].Min > zm) continue;
                    var poly = polygons[i];
                    if (!Span(poly, zm, out double lm, out double rm)) continue;
                    Span(poly, z0, out double l0, out double r0, clampTo: (lm, rm));
                    Span(poly, z1, out double l1, out double r1, clampTo: (lm, rm));
                    intervals.Add((lm, l0, r0, l1, r1, lm, rm));
                }
                if (intervals.Count == 0) continue;
                intervals.Sort((a, b) => a.LM.CompareTo(b.LM));
                var cur = intervals[0];
                for (int j = 1; j <= intervals.Count; j++)
                {
                    if (j < intervals.Count && intervals[j].LM <= cur.RM + 1e-9)
                    {
                        var n = intervals[j];
                        if (n.RM > cur.RM) cur = (cur.Mid0, cur.L0, n.R0, cur.L1, n.R1, cur.LM, n.RM);
                        continue;
                    }
                    if (cur.RM - cur.LM > 1e-9)
                        result.Add(new ShadowTrapezoid(z0, z1, cur.L0, Math.Max(cur.L0, cur.R0), cur.L1, Math.Max(cur.L1, cur.R1)));
                    if (j < intervals.Count) cur = intervals[j];
                }
            }
        }
        return result;
    }

    /// <summary>Интервал [left, right] сечения выпуклого многоугольника прямой z = const.</summary>
    private static bool Span(List<(double X, double Z)> poly, double z, out double left, out double right, (double L, double R)? clampTo = null)
    {
        left = double.MaxValue;
        right = double.MinValue;
        for (int i = 0; i < poly.Count; i++)
        {
            var a = poly[i];
            var b = poly[(i + 1) % poly.Count];
            if ((a.Z > z) == (b.Z > z) && Math.Abs(a.Z - z) > 1e-12 && Math.Abs(b.Z - z) > 1e-12) continue;
            double x;
            if (Math.Abs(b.Z - a.Z) < 1e-12)
            {
                left = Math.Min(left, Math.Min(a.X, b.X));
                right = Math.Max(right, Math.Max(a.X, b.X));
                continue;
            }
            double t = (z - a.Z) / (b.Z - a.Z);
            if (t < -1e-9 || t > 1 + 1e-9) continue;
            x = a.X + (b.X - a.X) * Math.Clamp(t, 0, 1);
            left = Math.Min(left, x);
            right = Math.Max(right, x);
        }
        if (left > right)
        {
            // Вершина полосы вне многоугольника (касание) — берём интервал средней линии.
            if (clampTo is { } c) { left = c.L; right = c.R; return true; }
            return false;
        }
        return true;
    }

    /// <summary>Текст времени «13:30».</summary>
    public static string TimeText(double hours)
    {
        int minutes = (int)Math.Round(hours * 60);
        return $"{minutes / 60:00}:{minutes % 60:00}";
    }
}
