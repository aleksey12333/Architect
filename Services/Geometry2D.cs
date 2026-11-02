namespace AIArchitectStudio.Services;

/// <summary>
/// Плоская геометрия плана (X, Z) для инженерного расчёта и поиска коллизий: площади и пересечения контуров
/// (Сазерленд — Ходжмен для выпуклых, сеточная оценка для невыпуклых), расстояния до отрезков и контуров,
/// выпуклая оболочка (монотонная цепь Эндрю).
/// </summary>
public static class Geometry2D
{
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

    public static double Area(IReadOnlyList<(double X, double Z)> poly) => poly.Count < 3 ? 0 : Math.Abs(SignedArea(poly));

    public static (double X, double Z) Centroid(IReadOnlyList<(double X, double Z)> poly)
    {
        double a = SignedArea(poly);
        if (Math.Abs(a) < 1e-12) return (poly.Average(p => p.X), poly.Average(p => p.Z));
        double cx = 0, cz = 0;
        for (int i = 0; i < poly.Count; i++)
        {
            var p = poly[i];
            var q = poly[(i + 1) % poly.Count];
            double k = p.X * q.Z - q.X * p.Z;
            cx += (p.X + q.X) * k;
            cz += (p.Z + q.Z) * k;
        }
        return (cx / (6 * a), cz / (6 * a));
    }

    public static (double MinX, double MaxX, double MinZ, double MaxZ) Bounds(IReadOnlyList<(double X, double Z)> poly)
    {
        double minX = double.MaxValue, maxX = double.MinValue, minZ = double.MaxValue, maxZ = double.MinValue;
        foreach (var (x, z) in poly)
        {
            minX = Math.Min(minX, x); maxX = Math.Max(maxX, x);
            minZ = Math.Min(minZ, z); maxZ = Math.Max(maxZ, z);
        }
        return (minX, maxX, minZ, maxZ);
    }

    public static bool IsConvex(IReadOnlyList<(double X, double Z)> poly)
    {
        if (poly.Count < 4) return true;
        int sign = 0;
        for (int i = 0; i < poly.Count; i++)
        {
            var a = poly[i];
            var b = poly[(i + 1) % poly.Count];
            var c = poly[(i + 2) % poly.Count];
            double cross = (b.X - a.X) * (c.Z - b.Z) - (b.Z - a.Z) * (c.X - b.X);
            if (Math.Abs(cross) < 1e-12) continue;
            int s = Math.Sign(cross);
            if (sign == 0) sign = s;
            else if (s != sign) return false;
        }
        return true;
    }

    public static bool Contains(IReadOnlyList<(double X, double Z)> poly, double x, double z) => SceneStateService.PointInPolygon(x, z, poly);

    /// <summary>Отсечение произвольного контура выпуклым (Сазерленд — Ходжмен).</summary>
    public static List<(double X, double Z)> ClipConvex(IReadOnlyList<(double X, double Z)> subject, IReadOnlyList<(double X, double Z)> convexClip)
    {
        var output = subject.ToList();
        double orientation = Math.Sign(SignedArea(convexClip));
        if (orientation == 0) return [];
        for (int i = 0; i < convexClip.Count && output.Count > 0; i++)
        {
            var a = convexClip[i];
            var b = convexClip[(i + 1) % convexClip.Count];
            var input = output;
            output = new List<(double X, double Z)>(input.Count + 4);
            double Side((double X, double Z) p) => orientation * ((b.X - a.X) * (p.Z - a.Z) - (b.Z - a.Z) * (p.X - a.X));
            for (int k = 0; k < input.Count; k++)
            {
                var p = input[k];
                var q = input[(k + 1) % input.Count];
                double sp = Side(p), sq = Side(q);
                if (sp >= 0) output.Add(p);
                if (sp >= 0 != sq >= 0)
                {
                    double t = sp / (sp - sq);
                    output.Add((p.X + (q.X - p.X) * t, p.Z + (q.Z - p.Z) * t));
                }
            }
        }
        return output;
    }

    /// <summary>Площадь пересечения двух контуров: точная, если хотя бы один выпуклый; иначе — сеткой 48 × 48 по общему габариту.</summary>
    public static double IntersectionArea(IReadOnlyList<(double X, double Z)> a, IReadOnlyList<(double X, double Z)> b)
    {
        if (a.Count < 3 || b.Count < 3) return 0;
        var (ax0, ax1, az0, az1) = Bounds(a);
        var (bx0, bx1, bz0, bz1) = Bounds(b);
        double x0 = Math.Max(ax0, bx0), x1 = Math.Min(ax1, bx1), z0 = Math.Max(az0, bz0), z1 = Math.Min(az1, bz1);
        if (x1 <= x0 || z1 <= z0) return 0;
        if (IsConvex(b)) return Area(ClipConvex(a, b));
        if (IsConvex(a)) return Area(ClipConvex(b, a));
        const int n = 48;
        double dx = (x1 - x0) / n, dz = (z1 - z0) / n;
        int hits = 0;
        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++)
            {
                double x = x0 + (i + 0.5) * dx, z = z0 + (j + 0.5) * dz;
                if (Contains(a, x, z) && Contains(b, x, z)) hits++;
            }
        return hits * dx * dz;
    }

    public static double DistanceToSegment(double px, double pz, double ax, double az, double bx, double bz)
    {
        double vx = bx - ax, vz = bz - az;
        double len2 = vx * vx + vz * vz;
        double t = len2 < 1e-18 ? 0 : Math.Clamp(((px - ax) * vx + (pz - az) * vz) / len2, 0, 1);
        double cx = ax + vx * t - px, cz = az + vz * t - pz;
        return Math.Sqrt(cx * cx + cz * cz);
    }

    /// <summary>Расстояние до границы контура (замкнутого или открытого).</summary>
    public static double DistanceToBoundary(IReadOnlyList<(double X, double Z)> poly, double x, double z, bool closed = true)
    {
        double best = double.MaxValue;
        int count = closed ? poly.Count : poly.Count - 1;
        for (int i = 0; i < count; i++)
        {
            var a = poly[i];
            var b = poly[(i + 1) % poly.Count];
            best = Math.Min(best, DistanceToSegment(x, z, a.X, a.Z, b.X, b.Z));
        }
        return best;
    }

    /// <summary>Расстояние до заполненного контура (0 внутри).</summary>
    public static double DistanceToPolygon(IReadOnlyList<(double X, double Z)> poly, double x, double z) =>
        Contains(poly, x, z) ? 0 : DistanceToBoundary(poly, x, z);

    /// <summary>Выпуклая оболочка против часовой стрелки (монотонная цепь Эндрю).</summary>
    public static List<(double X, double Z)> ConvexHull(IEnumerable<(double X, double Z)> points)
    {
        var pts = points.Distinct().OrderBy(p => p.X).ThenBy(p => p.Z).ToList();
        if (pts.Count < 3) return pts;
        static double Cross((double X, double Z) o, (double X, double Z) a, (double X, double Z) b) =>
            (a.X - o.X) * (b.Z - o.Z) - (a.Z - o.Z) * (b.X - o.X);
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

    /// <summary>
    /// Разбиение простого (в т. ч. невыпуклого: Г-, Т-, Н-образного, ступенчатого) контура на выпуклые части:
    /// триангуляция отсечением ушей, затем слияние соседних треугольников по общей диагонали, пока часть остаётся
    /// выпуклой (Хертель — Мельхорн). Выпуклый контур возвращается одной частью.
    /// </summary>
    public static List<List<(double X, double Z)>> ConvexPieces(IReadOnlyList<(double X, double Z)> polygon)
    {
        var poly = polygon.Where((p, i) => i == 0 || p != polygon[i - 1]).ToList();
        if (poly.Count > 1 && poly[0] == poly[^1]) poly.RemoveAt(poly.Count - 1);
        if (poly.Count < 3) return [];
        if (IsConvex(poly)) return [poly];
        bool ccw = SignedArea(poly) > 0;

        static double Cross((double X, double Z) o, (double X, double Z) a, (double X, double Z) b) =>
            (a.X - o.X) * (b.Z - o.Z) - (a.Z - o.Z) * (b.X - o.X);

        // Отсечение ушей по индексам исходного контура.
        var idx = Enumerable.Range(0, poly.Count).ToList();
        var pieces = new List<List<int>>();
        int guard = 0;
        while (idx.Count > 3 && guard++ < 10000)
        {
            bool clipped = false;
            for (int i = 0; i < idx.Count; i++)
            {
                int ia = idx[(i - 1 + idx.Count) % idx.Count], ib = idx[i], ic = idx[(i + 1) % idx.Count];
                var a = poly[ia];
                var b = poly[ib];
                var c = poly[ic];
                double turn = Cross(a, b, c) * (ccw ? 1 : -1);
                if (turn <= 1e-12) continue;
                bool contains = false;
                foreach (int k in idx)
                {
                    if (k == ia || k == ib || k == ic) continue;
                    var p = poly[k];
                    double s1 = Cross(a, b, p) * (ccw ? 1 : -1), s2 = Cross(b, c, p) * (ccw ? 1 : -1), s3 = Cross(c, a, p) * (ccw ? 1 : -1);
                    if (s1 >= -1e-12 && s2 >= -1e-12 && s3 >= -1e-12) { contains = true; break; }
                }
                if (contains) continue;
                pieces.Add([ia, ib, ic]);
                idx.RemoveAt(i);
                clipped = true;
                break;
            }
            if (!clipped) break;
        }
        if (idx.Count >= 3) pieces.Add([.. idx]);

        // Слияние соседних частей через общую диагональ, если результат выпуклый.
        bool merged = true;
        while (merged)
        {
            merged = false;
            for (int i = 0; i < pieces.Count && !merged; i++)
                for (int j = i + 1; j < pieces.Count && !merged; j++)
                {
                    var p = pieces[i];
                    var q = pieces[j];
                    for (int e = 0; e < p.Count && !merged; e++)
                    {
                        int u = p[e], v = p[(e + 1) % p.Count];
                        int f = q.FindIndex(x => x == v);
                        if (f < 0 || q[(f + 1) % q.Count] != u) continue;
                        // p: … u v …, q: … v u … → p до u, затем хвост q от u до v, затем остаток p от v.
                        var joined = new List<int>();
                        for (int k = 0; k < p.Count; k++)
                        {
                            joined.Add(p[(e + 1 + k) % p.Count]);
                        }
                        // joined начинается с v и заканчивается u; добавляем вершины q между u и v.
                        for (int k = 2; k < q.Count; k++) joined.Add(q[(f + k) % q.Count]);
                        var pts = joined.Select(x => poly[x]).ToList();
                        if (!IsConvex(pts)) continue;
                        pieces[i] = joined;
                        pieces.RemoveAt(j);
                        merged = true;
                    }
                }
        }
        return pieces.Select(p => p.Select(x => poly[x]).ToList()).ToList();
    }

    /// <summary>Длина ломаной (замкнутой — с замыкающим сегментом).</summary>
    public static double Perimeter(IReadOnlyList<(double X, double Z)> poly, bool closed = true)
    {
        double s = 0;
        int count = closed ? poly.Count : poly.Count - 1;
        for (int i = 0; i < count; i++)
        {
            var a = poly[i];
            var b = poly[(i + 1) % poly.Count];
            s += Math.Sqrt((b.X - a.X) * (b.X - a.X) + (b.Z - a.Z) * (b.Z - a.Z));
        }
        return s;
    }

    /// <summary>Точки на ломаной с шагом не больше step и длиной участка каждой точки (для дискретизации линейных нагрузок).</summary>
    public static List<(double X, double Z, double Length)> Resample(IReadOnlyList<(double X, double Z)> poly, double step, bool closed)
    {
        var result = new List<(double X, double Z, double Length)>();
        int count = closed ? poly.Count : poly.Count - 1;
        for (int i = 0; i < count; i++)
        {
            var a = poly[i];
            var b = poly[(i + 1) % poly.Count];
            double len = Math.Sqrt((b.X - a.X) * (b.X - a.X) + (b.Z - a.Z) * (b.Z - a.Z));
            int n = Math.Max(1, (int)Math.Ceiling(len / Math.Max(0.05, step)));
            for (int k = 0; k < n; k++)
            {
                double t = (k + 0.5) / n;
                result.Add((a.X + (b.X - a.X) * t, a.Z + (b.Z - a.Z) * t, len / n));
            }
        }
        return result;
    }
}
