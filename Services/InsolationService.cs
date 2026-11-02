using AIArchitectStudio.Models;
using AIArchitectStudio.Rendering;

namespace AIArchitectStudio.Services;

/// <summary>Светоклиматическая зона по СанПиН 1.2.3685-21 (по широте участка).</summary>
public enum InsolationZone
{
    /// <summary>Севернее 58° с.ш.</summary>
    North,

    /// <summary>58° … 48° с.ш.</summary>
    Central,

    /// <summary>Южнее 48° с.ш.</summary>
    South
}

/// <summary>
/// Норматив зоны: продолжительность непрерывной инсоляции, расчётная дата (начало нормируемого периода — день с наименьшей
/// продолжительностью), исключаемое время после восхода и перед заходом Солнца.
/// </summary>
public sealed record InsolationNorm(InsolationZone Zone, double RequiredHours, int Month, int Day, double ExcludedHours)
{
    public int DayOfYear => new DateTime(2025, Month, Day).DayOfYear;

    public string ZoneTitle(bool russian) => Zone switch
    {
        InsolationZone.North => russian ? "северная зона (севернее 58° с.ш.)" : "northern zone (north of 58° N)",
        InsolationZone.South => russian ? "южная зона (южнее 48° с.ш.)" : "southern zone (south of 48° N)",
        _ => russian ? "центральная зона (58°–48° с.ш.)" : "central zone (58°–48° N)"
    };

    public string DateTitle(bool russian) => russian
        ? $"{Day} {(Month == 2 ? "февраля" : "апреля")}"
        : $"{(Month == 2 ? "February" : "April")} {Day}";
}

/// <summary>Отрезок времени, когда Солнце видно из расчётной точки окна (местное время, ч).</summary>
public readonly record struct SunInterval(double Start, double End)
{
    public double Hours => End - Start;
}

/// <summary>Итог расчёта по одному окну: интервалы освещения, наибольший непрерывный, сумма и вывод о соответствии норме.</summary>
public sealed record WindowInsolation(ArchitecturalElement Window, (double X, double Y, double Z) Point, (double X, double Z) Normal,
    IReadOnlyList<SunInterval> Intervals, double ContinuousHours, double TotalHours, bool Passes, bool Intermittent)
{
    public string Name => Window.DisplayTitle;
}

/// <summary>Итог проверки набора окон: регион, норматив зоны и результат по каждому окну.</summary>
public sealed record InsolationReport(SolarLocation Location, InsolationNorm Norm, double Sunrise, double Sunset, IReadOnlyList<WindowInsolation> Windows, double ElapsedMilliseconds)
{
    public int PassCount => Windows.Count(w => w.Passes);
    public int FailCount => Windows.Count - PassCount;
}

/// <summary>
/// Продолжительность инсоляции окон по СанПиН 1.2.3685-21 (табл. 5.58) и ГОСТ Р 57795-2017. Расчётная точка — на оси окна на уровне
/// низа светового проёма, на внутренней поверхности наружной стены (откосы проёма затеняют её сами). Солнце видно из точки,
/// если его луч выходит через световой проём на наружной грани стены и до неба не пересекает ни одной призмы элементов сцены
/// (стены, плиты, балконы, кровли, соседние корпуса — с повторами этажей). Время перебирается с шагом 1 мин на расчётную дату
/// зоны (22 апреля — северная и центральная, 22 февраля — южная) без первого и последнего часа (южная зона — полутора часов)
/// после восхода / перед заходом. Норма выполнена, если непрерывная инсоляция ≥ нормы (2.5 / 2.0 / 1.5 ч) или прерывистая:
/// один период ≥ 1 ч и сумма ≥ норма + 0.5 ч.
/// </summary>
public static class InsolationService
{
    public const double StepHours = 1.0 / 60;
    private const double RayLength = 400;

    public static InsolationNorm NormFor(double latitude) => latitude > 58
        ? new InsolationNorm(InsolationZone.North, 2.5, 4, 22, 1.0)
        : latitude >= 48
            ? new InsolationNorm(InsolationZone.Central, 2.0, 4, 22, 1.0)
            : new InsolationNorm(InsolationZone.South, 1.5, 2, 22, 1.5);

    /// <summary>Местное время восхода и захода (ч) при склонении declination; полярный день — (0, 24), полярная ночь — (12, 12).</summary>
    public static (double Sunrise, double Sunset) SunTimes(SolarLocation location, double declination)
    {
        const double deg = Math.PI / 180;
        double cosH = -Math.Tan(location.Latitude * deg) * Math.Tan(declination * deg);
        double shift = (location.Longitude - location.UtcOffset * 15) / 15;
        if (cosH <= -1) return (0, 24);
        if (cosH >= 1) return (12 - shift, 12 - shift);
        double h0 = Math.Acos(cosH) / deg / 15;
        return (12 - h0 - shift, 12 + h0 - shift);
    }

    /// <summary>Проверка окон windows на фоне всех элементов сцены scene.</summary>
    public static InsolationReport Analyze(IReadOnlyList<ArchitecturalElement> scene, IReadOnlyList<ArchitecturalElement> windows, SolarLocation location)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var norm = NormFor(location.Latitude);
        double declination = SolarShadowService.Declination(norm.DayOfYear);
        var (sunrise, sunset) = SunTimes(location, declination);
        bool polarDay = sunrise <= 0 && sunset >= 24;
        double from = polarDay ? 0 : sunrise + norm.ExcludedHours, to = polarDay ? 24 : sunset - norm.ExcludedHours;

        // Солнечные лучи расчётного дня (один раз для всех окон).
        var samples = new List<(double Hours, SunPosition Sun)>();
        for (double t = from; t <= to + 1e-9; t += StepHours)
        {
            var sun = SolarShadowService.ComputeAt(t, declination, location);
            if (sun.IsAboveHorizon) samples.Add((t, sun));
        }

        var results = new List<WindowInsolation>();
        foreach (var window in windows.Where(IsInsolated))
        {
            var host = scene.FirstOrDefault(e => e.Id == window.HostElementId);
            var frame = Frame(window, host, scene);
            var prisms = BuildPrisms(scene, window, host);
            var lit = new bool[samples.Count];
            for (int i = 0; i < samples.Count; i++) lit[i] = SeesSun(frame, samples[i].Sun, prisms);
            var intervals = Intervals(samples, lit);
            double longest = intervals.Count > 0 ? intervals.Max(x => x.Hours) : 0, total = intervals.Sum(x => x.Hours);
            bool continuous = longest >= norm.RequiredHours - 1e-6;
            bool intermittent = !continuous && intervals.Count >= 2 && longest >= 1.0 - 1e-6 && total >= norm.RequiredHours + 0.5 - 1e-6;
            results.Add(new WindowInsolation(window, frame.Point, (frame.NX, frame.NZ), intervals, longest, total, continuous || intermittent, intermittent));
        }
        sw.Stop();
        return new InsolationReport(location, norm, sunrise, sunset, results, sw.Elapsed.TotalMilliseconds);
    }

    /// <summary>Расчётные светопроёмы: окна и плоские витражи (полигональный витраж-оболочка башни — не светопроём помещения).</summary>
    public static bool IsInsolated(ArchitecturalElement e) =>
        e.Category == BimCategory.Window || e.Category == BimCategory.CurtainWall && e.Polygon2D.Count < 3;

    /// <summary>Часы «7 ч 05 мин» / «7 h 05 min».</summary>
    public static string HoursText(double hours, bool russian)
    {
        int minutes = (int)Math.Round(hours * 60);
        return russian ? $"{minutes / 60} ч {minutes % 60:00} мин" : $"{minutes / 60} h {minutes % 60:00} min";
    }

    // ───────────────────────────── Геометрия окна ─────────────────────────────

    /// <summary>
    /// Система окна: расчётная точка (ось окна, низ проёма, внутренняя грань стены), наружная нормаль, оси проёма и
    /// толщина стены. Наружная сторона — та, где нет перекрытия своего этажа (иначе — от центра постройки).
    /// </summary>
    private sealed record WindowFrame((double X, double Y, double Z) Point, double NX, double NZ, double UX, double UZ,
        double HalfWidth, double Bottom, double Top, double Thickness, double CX, double CZ);

    private static WindowFrame Frame(ArchitecturalElement window, ArchitecturalElement? host, IReadOnlyList<ArchitecturalElement> scene)
    {
        double rot = window.RotationY;
        var (ux, uz) = ArchitecturalElement.RotateY(1, 0, rot);
        var (nx, nz) = ArchitecturalElement.RotateY(0, 1, rot);
        double thickness = host is not null ? host.Depth : Math.Max(0.05, window.Depth - ParametricSolverService.OpeningClearance);
        // Центр стены по толщине: окно стоит в теле стены-хоста.
        double cx = window.X, cz = window.Z;
        if (host is not null)
        {
            var (lx, _) = ArchitecturalElement.RotateY(window.X - host.X, window.Z - host.Z, -host.RotationY);
            var (wx, wz) = ArchitecturalElement.RotateY(lx, 0, host.RotationY);
            cx = host.X + wx;
            cz = host.Z + wz;
        }

        // Наружу — в сторону, где за стеной нет перекрытия этажа окна (или от центра постройки).
        double probe = thickness / 2 + 0.6;
        var floors = scene.Where(e => e.BuildingGroupId == window.BuildingGroupId && e.Category is BimCategory.Slab or BimCategory.Foundation or BimCategory.CantileverSlab
                                      && e.TopY <= window.BaseY + 0.05 && e.TopY > window.BaseY - 4.5).ToList();
        bool plusInside = floors.Any(f => SceneStateService.PointInPolygon(cx + nx * probe, cz + nz * probe, ElementGeometryBuilder.WorldOutline(f)));
        bool minusInside = floors.Any(f => SceneStateService.PointInPolygon(cx - nx * probe, cz - nz * probe, ElementGeometryBuilder.WorldOutline(f)));
        bool flip;
        if (plusInside != minusInside) flip = plusInside;
        else
        {
            var group = scene.Where(e => e.BuildingGroupId == window.BuildingGroupId && !ElementTypes.IsVoidType(e.Category)).ToList();
            var (gx, gz) = SceneStateService.GroupPivot(group.Count > 0 ? group : [window]);
            flip = nx * (gx - cx) + nz * (gz - cz) > 0;
        }
        if (flip) { nx = -nx; nz = -nz; }

        double bottom = window.BaseY, top = window.TopY;
        var point = (cx - nx * thickness / 2, bottom + 0.01, cz - nz * thickness / 2);
        return new WindowFrame(point, nx, nz, ux, uz, window.Width / 2, bottom, top, thickness, cx, cz);
    }

    /// <summary>
    /// Солнце видно из расчётной точки: луч к Солнцу выходит на наружную грань стены внутри светового проёма (откосы) и до
    /// неба не встречает призм элементов сцены.
    /// </summary>
    private static bool SeesSun(WindowFrame f, SunPosition sun, List<Prism> prisms)
    {
        double dn = sun.DirX * f.NX + sun.DirZ * f.NZ;
        if (dn <= 1e-4) return false;
        double s = f.Thickness / dn;
        double ex = f.Point.X + sun.DirX * s, ey = f.Point.Y + sun.DirY * s, ez = f.Point.Z + sun.DirZ * s;
        double along = (ex - f.CX) * f.UX + (ez - f.CZ) * f.UZ;
        if (Math.Abs(along) > f.HalfWidth || ey < f.Bottom || ey > f.Top) return false;
        // Старт чуть снаружи фасада — без самопересечения с телом своей стены.
        double ox = ex + f.NX * 0.02, oz = ez + f.NZ * 0.02;
        foreach (var p in prisms)
            if (p.Hit(ox, ey, oz, sun.DirX, sun.DirY, sun.DirZ, RayLength)) return false;
        return true;
    }

    private static List<SunInterval> Intervals(List<(double Hours, SunPosition Sun)> samples, bool[] lit)
    {
        var list = new List<SunInterval>();
        int start = -1;
        for (int i = 0; i <= lit.Length; i++)
        {
            bool on = i < lit.Length && lit[i];
            if (on && start < 0) start = i;
            if (!on && start >= 0)
            {
                // Интервал между отсчётами: от первого освещённого до последнего освещённого + шаг.
                double end = Math.Min(samples[i - 1].Hours + StepHours, samples[^1].Hours);
                if (end - samples[start].Hours > 1e-9) list.Add(new SunInterval(samples[start].Hours, end));
                start = -1;
            }
        }
        return list;
    }

    // ───────────────────────────── Затеняющие призмы ─────────────────────────────

    /// <summary>Вертикальная призма в плане (мир) между отметками y0…y1 — затеняющее тело.</summary>
    private sealed class Prism
    {
        private readonly List<(double X, double Z)> outline;
        private readonly double y0, y1, _cx, _cz, _r;

        public Prism(List<(double X, double Z)> outline, double y0, double y1)
        {
            this.outline = outline;
            this.y0 = y0;
            this.y1 = y1;
            _cx = outline.Average(p => p.X);
            _cz = outline.Average(p => p.Z);
            _r = outline.Max(p => Math.Sqrt((p.X - _cx) * (p.X - _cx) + (p.Z - _cz) * (p.Z - _cz)));
        }

        public bool Hit(double ox, double oy, double oz, double dx, double dy, double dz, double maxLength)
        {
            // Участок луча внутри слоя y0…y1.
            double s0 = 0, s1 = maxLength;
            if (Math.Abs(dy) < 1e-9)
            {
                if (oy < y0 || oy > y1) return false;
            }
            else
            {
                double a = (y0 - oy) / dy, b = (y1 - oy) / dy;
                s0 = Math.Max(s0, Math.Min(a, b));
                s1 = Math.Min(s1, Math.Max(a, b));
                if (s1 <= s0) return false;
            }
            double ax = ox + dx * s0, az = oz + dz * s0, bx = ox + dx * s1, bz = oz + dz * s1;
            // Быстрый отсев по описанной окружности.
            if (Geometry2D.DistanceToSegment(_cx, _cz, ax, az, bx, bz) > _r + 1e-6) return false;
            if (SceneStateService.PointInPolygon(ax, az, outline) || SceneStateService.PointInPolygon(bx, bz, outline)) return true;
            for (int i = 0; i < outline.Count; i++)
            {
                var p = outline[i];
                var q = outline[(i + 1) % outline.Count];
                if (SegmentsCross(ax, az, bx, bz, p.X, p.Z, q.X, q.Z)) return true;
            }
            return false;
        }
    }

    private static bool SegmentsCross(double ax, double az, double bx, double bz, double cx, double cz, double dx, double dz)
    {
        static double Cross(double ox, double oz, double px, double pz, double qx, double qz) => (px - ox) * (qz - oz) - (pz - oz) * (qx - ox);
        double d1 = Cross(cx, cz, dx, dz, ax, az), d2 = Cross(cx, cz, dx, dz, bx, bz);
        double d3 = Cross(ax, az, bx, bz, cx, cz), d4 = Cross(ax, az, bx, bz, dx, dz);
        return (d1 > 0) != (d2 > 0) && (d3 > 0) != (d4 > 0);
    }

    /// <summary>
    /// Затеняющие тела: все непустые элементы сцены, кроме самого окна и его стены-хоста (луч уже вышел через проём),
    /// проёмов и заполнений проёмов; параметрические элементы — поэтажно с закруткой и сужением.
    /// </summary>
    private static List<Prism> BuildPrisms(IReadOnlyList<ArchitecturalElement> scene, ArchitecturalElement window, ArchitecturalElement? host)
    {
        var prisms = new List<Prism>();
        foreach (var e in scene)
        {
            if (ReferenceEquals(e, window) || ReferenceEquals(e, host) || ElementTypes.IsVoidType(e.Category) || ElementTypes.IsHostedType(e.Category) || e.Category == BimCategory.Pool) continue;
            var pieces = SolarShadowService.ShadowPieces(e);
            int repeat = Math.Max(1, e.RepeatLevels);
            double taper = e.TaperScalePerFloor <= 0 ? 1 : e.TaperScalePerFloor, scale = 1;
            for (int i = 0; i < repeat; i++, scale *= taper)
            {
                double rot = e.RotationY + e.TwistAnglePerFloor * i;
                double y0 = e.BaseY + e.LevelHeight * i, y1 = e.TopY + e.LevelHeight * i;
                foreach (var piece in pieces)
                {
                    var world = piece.Select(p =>
                    {
                        var (wx, wz) = ArchitecturalElement.RotateY(p.X * scale, p.Z * scale, rot);
                        return (e.X + wx, e.Z + wz);
                    }).ToList();
                    if (world.Count >= 3) prisms.Add(new Prism(world, y0, y1));
                }
            }
        }
        return prisms;
    }
}
