using System.Globalization;
using System.Numerics;
using AIArchitectStudio.Models;
using AIArchitectStudio.Models.Geometry;

namespace AIArchitectStudio.Services;

/// <summary>Результат обмера рулеткой: проекции ΔX/ΔY/ΔZ, длина, горизонтальная проекция и угол наклона к горизонту (м, градусы).</summary>
public sealed record TapeMeasurement((double X, double Y, double Z) A, (double X, double Y, double Z) B)
{
    public double Dx => B.X - A.X;
    public double Dy => B.Y - A.Y;
    public double Dz => B.Z - A.Z;
    public double Length => Math.Sqrt(Dx * Dx + Dy * Dy + Dz * Dz);

    /// <summary>Длина проекции на горизонтальную плоскость XZ.</summary>
    public double Horizontal => Math.Sqrt(Dx * Dx + Dz * Dz);

    /// <summary>Угол наклона к горизонтали, ° (0 — горизонтальный размер, 90 — вертикальный).</summary>
    public double SlopeDegrees => Length < 1e-9 ? 0 : Math.Atan2(Math.Abs(Dy), Horizontal) * 180 / Math.PI;

    /// <summary>Наклон показывается, если ΔY заметно отличается от нуля (≥ 0.5 мм).</summary>
    public bool HasSlope => Math.Abs(Dy) >= 5e-4;

    public (double X, double Y, double Z) Mid => ((A.X + B.X) / 2, (A.Y + B.Y) / 2, (A.Z + B.Z) / 2);
}

/// <summary>Отрезок 3D-размерной линии (мировые координаты сцены, м).</summary>
public readonly record struct DimensionStroke((double X, double Y, double Z) A, (double X, double Y, double Z) B, bool IsTick);

/// <summary>Точка привязки рулетки на габарите геометрии элемента: угол или середина ребра (мировые координаты, м).</summary>
public readonly record struct MeasureSnapCandidate(Vector3 Point, bool IsMidpoint);

/// <summary>
/// Интерактивная 3D-рулетка (клавиша M): обмер между двумя точками сцены с привязкой OSnap, геометрия размерной линии
/// с засечками 45° по ГОСТ 2.307 (размерная линия выступает за засечки), подписи капсулы и закрепление размеров
/// в SceneStateService.PinnedDimensions. Чистые функции — используются вьюпортом, экспортом DXF/PDF и автотестом.
/// </summary>
public static class MeasurementService
{
    /// <summary>Минимальная длина закрепляемого размера, м (1 мм).</summary>
    public const double MinLength = 0.001;

    public static TapeMeasurement Measure((double X, double Y, double Z) a, (double X, double Y, double Z) b) => new(a, b);

    public static TapeMeasurement Measure(PinnedDimension d) => new(d.A, d.B);

    /// <summary>Значение размера по ГОСТ: целые миллиметры без единиц («5000»), как в размерных цепочках листов.</summary>
    public static string GostValue(double meters) => Math.Round(Math.Abs(meters) * 1000).ToString("0", CultureInfo.InvariantCulture);

    /// <summary>Подпись закреплённого размера: собственная подпись или значение длины в мм.</summary>
    public static string LabelOf(PinnedDimension d) => string.IsNullOrWhiteSpace(d.Label) ? GostValue(d.Length) : d.Label;

    /// <summary>
    /// Строки стеклянной капсулы: «L = 5 000 мм», «ΔX 3 000 · ΔY 4 000 · ΔZ 0 мм», «Наклон 53,13°» (только при ΔY ≠ 0).
    /// </summary>
    public static (string Length, string Projections, string? Slope) CapsuleLines(TapeMeasurement m, string unit = "мм", bool english = false)
    {
        string length = "L = " + PushPullService.Millimeters(m.Length, unit: unit);
        string projections = $"ΔX {Signed(m.Dx)} · ΔY {Signed(m.Dy)} · ΔZ {Signed(m.Dz)} {unit}";
        string? slope = m.HasSlope
            ? (english ? "Slope " : "Наклон ") + m.SlopeDegrees.ToString("0.00", english ? CultureInfo.InvariantCulture : CultureInfo.GetCultureInfo("ru-RU")) + "°"
            : null;
        return (length, projections, slope);
    }

    private static string Signed(double meters)
    {
        string s = PushPullService.Millimeters(meters, unit: string.Empty).TrimEnd();
        return s == "−0" ? "0" : s;
    }

    /// <summary>
    /// Направление засечек: в плоскости, содержащей размерную линию и обращённой к наблюдателю (look — направление взгляда);
    /// для линии вдоль взгляда — любая перпендикулярная горизонталь/вертикаль.
    /// </summary>
    public static (double X, double Y, double Z) Perpendicular((double X, double Y, double Z) dir, (double X, double Y, double Z) look)
    {
        var p = Cross(dir, look);
        if (Norm(p) < 1e-6) p = Cross(dir, (0, 1, 0));
        if (Norm(p) < 1e-6) p = Cross(dir, (1, 0, 0));
        return Normalize(p);
    }

    /// <summary>
    /// Отрезки 3D-размера: размерная линия P1–P2 с выступом overshoot за засечки и две засечки 45° длиной tick
    /// (по ГОСТ 2.307 — короткие штрихи под 45° к размерной линии, проведённые через её концы).
    /// </summary>
    public static List<DimensionStroke> Strokes((double X, double Y, double Z) a, (double X, double Y, double Z) b,
        (double X, double Y, double Z) perpendicular, double tick, double overshoot)
    {
        var d = Sub(b, a);
        double len = Norm(d);
        if (len < 1e-9) return [];
        var u = Scale(d, 1 / len);
        var n = Normalize(Sub(perpendicular, Scale(u, Dot(perpendicular, u))));
        // Засечка — под 45° к размерной линии в плоскости (u, n).
        var t = Scale(Normalize(Add(u, n)), tick / 2);
        return
        [
            new(Sub(a, Scale(u, overshoot)), Add(b, Scale(u, overshoot)), false),
            new(Sub(a, t), Add(a, t), true),
            new(Sub(b, t), Add(b, t), true)
        ];
    }

    /// <summary>Размер засечки и выступа линии по длине размера (м): заметны и на стене 600 мм, и на фасаде 60 м.</summary>
    public static (double Tick, double Overshoot) StrokeSizes(double length, double worldPerPixel = 0)
    {
        double tick = Math.Clamp(length * 0.05, 0.08, 0.45);
        if (worldPerPixel > 0) tick = Math.Clamp(worldPerPixel * 16, Math.Min(tick, 0.04), Math.Max(tick, 0.08));
        return (tick, tick * 0.45);
    }

    // ───────────── Независимая геометрия: привязка к габариту и размерные линии ─────────────

    /// <summary>
    /// Кандидаты привязки рулетки на элементе: 8 углов габарита его геометрии в мировых координатах (нумерация
    /// <see cref="BimBounds.GetCorners"/>), затем середины 12 рёбер габарита (<see cref="BimBounds.Edges"/>).
    /// </summary>
    public static List<MeasureSnapCandidate> BoxSnapCandidates(IGeometryElement geometry)
    {
        var corners = geometry.WorldCorners();
        var candidates = new List<MeasureSnapCandidate>(corners.Length + BimBounds.Edges.Length);
        foreach (var c in corners) candidates.Add(new MeasureSnapCandidate(c, false));
        foreach (var (i, j) in BimBounds.Edges) candidates.Add(new MeasureSnapCandidate((corners[i] + corners[j]) / 2, true));
        return candidates;
    }

    /// <summary>
    /// Ближайший к курсору кандидат в радиусе radius (единицы screenDistance — пиксели экрана): угол побеждает середину ребра,
    /// если ближе неё хотя бы на 3 px. screenDistance возвращает null для точки вне кадра.
    /// </summary>
    public static (MeasureSnapCandidate Candidate, double Distance)? PickSnap(IReadOnlyList<MeasureSnapCandidate> candidates,
        Func<Vector3, double?> screenDistance, double radius)
    {
        MeasureSnapCandidate? best = null;
        double bestDistance = radius;
        foreach (var c in candidates)
        {
            if (screenDistance(c.Point) is not { } d) continue;
            if (d < bestDistance - (c.IsMidpoint ? 0 : 3))
            {
                bestDistance = d;
                best = c;
            }
        }
        return best is { } b ? (b, bestDistance) : null;
    }

    /// <summary>
    /// Отрезки 3D-размера a–b (линия с выступом и засечки 45° в плоскости, обращённой к взгляду look) в набор линий;
    /// worldPerPixel — масштаб экрана у середины размера. Возвращает точку подписи у середины (сдвиг по нормали засечек).
    /// </summary>
    public static Vector3 AddDimensionStrokes(BimLineSet lines, Vector3 a, Vector3 b, Vector3 look, double worldPerPixel)
    {
        var mid = (a + b) / 2;
        float length = BimMath.Length(b - a);
        var (tick, overshoot) = StrokeSizes(length, worldPerPixel);
        var dir = BimMath.Normalize(b - a);
        var perp = Perpendicular(ToTuple(dir), ToTuple(look));
        foreach (var s in Strokes(ToTuple(a), ToTuple(b), perp, tick, overshoot))
            lines.AddLine(ToPoint(s.A), ToPoint(s.B));
        return mid + ToPoint(perp) * (float)(tick * 0.9);
    }

    public static Vector3 ToPoint((double X, double Y, double Z) p) => new((float)p.X, (float)p.Y, (float)p.Z);

    public static (double X, double Y, double Z) ToTuple(Vector3 v) => (v.X, v.Y, v.Z);

    /// <summary>Проекция размера на плоскость чертежа (u, v): длина видимой на листе линии, м.</summary>
    public static double ProjectedLength((double U, double V) a, (double U, double V) b) =>
        Math.Sqrt((b.U - a.U) * (b.U - a.U) + (b.V - a.V) * (b.V - a.V));

    // ───────────── Векторная алгебра на кортежах ─────────────

    public static (double X, double Y, double Z) Sub((double X, double Y, double Z) a, (double X, double Y, double Z) b) => (a.X - b.X, a.Y - b.Y, a.Z - b.Z);
    public static (double X, double Y, double Z) Add((double X, double Y, double Z) a, (double X, double Y, double Z) b) => (a.X + b.X, a.Y + b.Y, a.Z + b.Z);
    public static (double X, double Y, double Z) Scale((double X, double Y, double Z) a, double k) => (a.X * k, a.Y * k, a.Z * k);
    public static double Dot((double X, double Y, double Z) a, (double X, double Y, double Z) b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;
    public static double Norm((double X, double Y, double Z) a) => Math.Sqrt(Dot(a, a));

    public static (double X, double Y, double Z) Cross((double X, double Y, double Z) a, (double X, double Y, double Z) b) =>
        (a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);

    public static (double X, double Y, double Z) Normalize((double X, double Y, double Z) a)
    {
        double n = Norm(a);
        return n < 1e-12 ? (1, 0, 0) : Scale(a, 1 / n);
    }
}
