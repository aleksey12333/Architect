using System.Numerics;
using AIArchitectStudio.Models;
using AIArchitectStudio.Models.Geometry;
using AIArchitectStudio.Services;

namespace AIArchitectStudio.Rendering;

/// <summary>
/// Полая скатная кровля по произвольному контуру Polygon2D (Г-, П-, Т-образные секции): ортогональный контур делится на
/// максимальные прямоугольники-крылья, у каждого крыла — плоскости скатов одного уклона k (двускатное — два ската вдоль длинной
/// стороны, торец под соседним крылом замыкается вальмой; вальмовое — четыре; односкатное — одна плоскость на весь контур).
/// Низ оболочки — поле высот f(p) = max по крыльям (min по плоскостям крыла): ендовы и вальмы получаются пересечением скатов.
/// Свес — смещение рёбер контура наружу на o (миттер), карниз опускается по тому же уклону. Контур режется на выпуклые куски, в
/// каждом из которых f — одна плоскость (точная сетка без аппроксимации), толщина по вертикали v = t·√(1 + k²).
/// Фронтоны — вдоль каждого ребра контура, над которым скат поднимается выше карниза; подшивка — вдоль карнизных рёбер.
/// </summary>
public static partial class ElementGeometryBuilder
{
    /// <summary>Плоскость поля высот y = Gx·x + Gz·z + C (x, z — локальные координаты плана).</summary>
    private readonly record struct RoofPlane(float Gx, float Gz, float C)
    {
        public float At(Vector2 p) => Gx * p.X + Gz * p.Y + C;
    }

    /// <summary>Крыло кровли: прямоугольник плана, направление конька, плоскости скатов (min — поверхность крыла).</summary>
    private sealed record RoofWing(float X0, float X1, float Z0, float Z1, bool AlongX, RoofPlane[] Planes)
    {
        public bool Covers(Vector2 p, float expand) =>
            p.X >= X0 - expand - 1e-4f && p.X <= X1 + expand + 1e-4f && p.Y >= Z0 - expand - 1e-4f && p.Y <= Z1 + expand + 1e-4f;

        public float HalfSpan => (AlongX ? Z1 - Z0 : X1 - X0) / 2;
    }

    /// <summary>Модель полигональной кровли: контур стен, контур карниза, крылья, уклон, толщина по вертикали и куски плана.</summary>
    private sealed class PolygonRoofModel
    {
        public PolygonRoofModel(List<Vector2> outline, List<Vector2> eaves, List<RoofWing> wings, bool domains, float slope, float vertical, float eaveY)
        {
            Outline = outline;
            Eaves = eaves;
            Wings = wings;
            UseDomains = domains;
            Slope = slope;
            Vertical = vertical;
            EaveY = eaveY;
        }

        public List<Vector2> Outline { get; }
        public List<Vector2> Eaves { get; }
        public List<RoofWing> Wings { get; }
        public bool UseDomains { get; }
        public float Slope { get; }
        public float Vertical { get; }
        public float EaveY { get; }
        public float Expand { get; init; }

        /// <summary>Выпуклые куски контура карниза, в каждом — одна плоскость (номер крыла × 8 + номер плоскости).</summary>
        public List<(List<Vector2> Poly, int Plane)> Pieces { get; } = [];

        public RoofPlane PlaneOf(int id) => Wings[id / 8].Planes[id % 8];

        /// <summary>Номер плоскости, дающей низ ската в точке (max по крыльям, min по плоскостям крыла).</summary>
        public int Active(Vector2 p)
        {
            float best = float.NegativeInfinity;
            int id = -1;
            for (int pass = 0; pass < 2 && id < 0; pass++)
                for (int wi = 0; wi < Wings.Count; wi++)
                {
                    var w = Wings[wi];
                    if (pass == 0 && UseDomains && !w.Covers(p, Expand)) continue;
                    float m = float.PositiveInfinity;
                    int mj = 0;
                    for (int j = 0; j < w.Planes.Length; j++)
                    {
                        float v = w.Planes[j].At(p);
                        if (v < m) { m = v; mj = j; }
                    }
                    if (m > best) { best = m; id = wi * 8 + mj; }
                }
            return id;
        }

        public float Underside(Vector2 p) => PlaneOf(Active(p)).At(p);
    }

    private static bool IsAxisRectangle(IReadOnlyList<Vector2> poly) =>
        poly.Count == 4 && IsOrthogonal(poly);

    private static bool IsOrthogonal(IReadOnlyList<Vector2> poly)
    {
        for (int i = 0; i < poly.Count; i++)
        {
            var d = poly[(i + 1) % poly.Count] - poly[i];
            if (Math.Abs(d.X) > 1e-4f && Math.Abs(d.Y) > 1e-4f) return false;
        }
        return true;
    }

    /// <summary>Полигональный путь полой кровли: двускатная, вальмовая, односкатная по непрямоугольному контуру.</summary>
    private static bool UsesPolygonRoof(string shape, List<Vector2>? poly) =>
        poly is { Count: >= 3 } && !IsAxisRectangle(poly) && shape is RoofShapes.GableHollow or RoofShapes.HipHollow or RoofShapes.ShedHollow;

    /// <summary>
    /// Смещение контура (против часовой) наружу на dist (отрицательное — внутрь) с миттером в вершинах: вершина — пересечение
    /// смещённых рёбер, у почти параллельных рёбер — сдвиг по нормали.
    /// </summary>
    internal static List<Vector2> OffsetOutline(IReadOnlyList<Vector2> ccw, float dist)
    {
        int n = ccw.Count;
        var result = new List<Vector2>(n);
        if (Math.Abs(dist) < 1e-6f)
        {
            result.AddRange(ccw);
            return result;
        }
        static Vector2 Outward(Vector2 a, Vector2 b)
        {
            var d = b - a;
            float len = MathF.Sqrt(d.X * d.X + d.Y * d.Y);
            return len < 1e-9f ? Vector2.Zero : new Vector2(d.Y / len, -d.X / len);
        }
        for (int i = 0; i < n; i++)
        {
            var prev = ccw[(i - 1 + n) % n];
            var p = ccw[i];
            var next = ccw[(i + 1) % n];
            var n1 = Outward(prev, p);
            var n2 = Outward(p, next);
            float denom = 1 + n1.X * n2.X + n1.Y * n2.Y;
            result.Add(denom < 0.15f ? p + n2 * dist : p + (n1 + n2) * (dist / denom));
        }
        return result;
    }

    /// <summary>Максимальные прямоугольники ортогонального контура (крылья), null — контур не ортогональный или слишком сложный.</summary>
    private static List<(float X0, float X1, float Z0, float Z1)>? MaximalRectangles(IReadOnlyList<Vector2> poly)
    {
        if (!IsOrthogonal(poly)) return null;
        static List<float> Coords(IEnumerable<float> values)
        {
            var list = new List<float>();
            foreach (float v in values.OrderBy(v => v))
                if (list.Count == 0 || v - list[^1] > 1e-4f) list.Add(v);
            return list;
        }
        var xs = Coords(poly.Select(p => p.X));
        var zs = Coords(poly.Select(p => p.Y));
        int nx = xs.Count - 1, nz = zs.Count - 1;
        if (nx < 1 || nz < 1 || nx > 14 || nz > 14) return null;
        var inside = new bool[nx, nz];
        for (int i = 0; i < nx; i++)
            for (int j = 0; j < nz; j++)
                inside[i, j] = MeshAccumulator.PointInPolygon(new Vector2((xs[i] + xs[i + 1]) / 2, (zs[j] + zs[j + 1]) / 2), poly);
        bool Full(int i0, int i1, int j0, int j1)
        {
            if (i0 < 0 || j0 < 0 || i1 >= nx || j1 >= nz) return false;
            for (int i = i0; i <= i1; i++)
                for (int j = j0; j <= j1; j++)
                    if (!inside[i, j]) return false;
            return true;
        }
        var result = new List<(float, float, float, float)>();
        for (int i0 = 0; i0 < nx; i0++)
            for (int i1 = i0; i1 < nx; i1++)
                for (int j0 = 0; j0 < nz; j0++)
                    for (int j1 = j0; j1 < nz; j1++)
                    {
                        if (!Full(i0, i1, j0, j1)) continue;
                        bool maximal = !Full(i0 - 1, i1, j0, j1) && !Full(i0, i1 + 1, j0, j1) && !Full(i0, i1, j0 - 1, j1) && !Full(i0, i1, j0, j1 + 1);
                        if (maximal) result.Add((xs[i0], xs[i1 + 1], zs[j0], zs[j1 + 1]));
                    }
        return result;
    }

    /// <summary>Модель полигональной полой кровли элемента: крылья, плоскости скатов, куски плана; null — контур не подходит.</summary>
    private static PolygonRoofModel? BuildPolygonRoofModel(string shape, List<Vector2> poly, float h, float t, float o)
    {
        var outline = MeshAccumulator.EnsureCounterClockwise(poly);
        if (Math.Abs(SignedArea2(outline)) < 1e-3f) return null;
        var eaves = OffsetOutline(outline, o);
        float y0 = -h / 2;
        float minX = outline.Min(p => p.X), maxX = outline.Max(p => p.X), minZ = outline.Min(p => p.Y), maxZ = outline.Max(p => p.Y);
        var wings = new List<RoofWing>();
        bool domains;
        float k;

        if (shape == RoofShapes.ShedHollow)
        {
            // Одна плоскость на весь контур: подъём вдоль +Z от нижней стороны.
            k = SolveSlope(h, maxZ - minZ, t);
            wings.Add(new RoofWing(minX, maxX, minZ, maxZ, true, [new RoofPlane(0, k, y0 - k * minZ)]));
            domains = false;
        }
        else
        {
            var rects = MaximalRectangles(outline) ?? [(minX, maxX, minZ, maxZ)];
            domains = rects.Count > 1;
            float sRef = rects.Max(r => Math.Min(r.X1 - r.X0, r.Z1 - r.Z0)) / 2;
            k = SolveSlope(h, sRef, t);
            var along = rects.Select(r => r.X1 - r.X0 >= r.Z1 - r.Z0).ToList();
            for (int i = 0; i < rects.Count; i++)
            {
                var (x0, x1, z0, z1) = rects[i];
                bool alongX = along[i];
                var planes = new List<RoofPlane>();
                if (alongX)
                {
                    planes.Add(new RoofPlane(0, k, y0 - k * z0));
                    planes.Add(new RoofPlane(0, -k, y0 + k * z1));
                }
                else
                {
                    planes.Add(new RoofPlane(k, 0, y0 - k * x0));
                    planes.Add(new RoofPlane(-k, 0, y0 + k * x1));
                }
                // Торцы: у вальмовой — всегда скат; у двускатной — вальма, если торец уходит под крыло с поперечным коньком
                // (Г-, Т-, П-образные секции), иначе — фронтон.
                bool Covered(Vector2 inner) => rects.Where((r, j) => j != i && along[j] != alongX)
                    .Any(r => inner.X > r.X0 + 1e-4f && inner.X < r.X1 - 1e-4f && inner.Y > r.Z0 + 1e-4f && inner.Y < r.Z1 - 1e-4f);
                bool hip = shape == RoofShapes.HipHollow;
                const float eps = 0.01f;
                if (alongX)
                {
                    float zc = (z0 + z1) / 2;
                    if (hip || Covered(new Vector2(x0 + eps, zc))) planes.Add(new RoofPlane(k, 0, y0 - k * x0));
                    if (hip || Covered(new Vector2(x1 - eps, zc))) planes.Add(new RoofPlane(-k, 0, y0 + k * x1));
                }
                else
                {
                    float xc = (x0 + x1) / 2;
                    if (hip || Covered(new Vector2(xc, z0 + eps))) planes.Add(new RoofPlane(0, k, y0 - k * z0));
                    if (hip || Covered(new Vector2(xc, z1 - eps))) planes.Add(new RoofPlane(0, -k, y0 + k * z1));
                }
                wings.Add(new RoofWing(x0, x1, z0, z1, alongX, [.. planes]));
            }
        }

        var model = new PolygonRoofModel(outline, eaves, wings, domains, k, t * MathF.Sqrt(1 + k * k), y0) { Expand = o };
        SplitRoofPieces(model);
        return model;
    }

    /// <summary>Разбиение контура карниза на выпуклые куски с одной плоскостью ската в каждом.</summary>
    private static void SplitRoofPieces(PolygonRoofModel model)
    {
        var start = Geometry2D.ConvexPieces(model.Eaves.Select(p => ((double)p.X, (double)p.Y)).ToList())
            .Select(piece => MeshAccumulator.EnsureCounterClockwise(piece.Select(p => new Vector2((float)p.X, (float)p.Z)).ToList()))
            .ToList();
        var pieces = start;
        if (model.UseDomains)
        {
            // Границы областей крыльев (прямоугольник + свес): внутри куска состав действующих крыльев постоянен.
            float e = model.Expand;
            foreach (var w in model.Wings)
                foreach (var (a, b, c) in new[] { (1f, 0f, -(w.X0 - e)), (1f, 0f, -(w.X1 + e)), (0f, 1f, -(w.Z0 - e)), (0f, 1f, -(w.Z1 + e)) })
                    pieces = SplitAll(pieces, a, b, c);
        }

        foreach (var piece in pieces)
        {
            var c0 = PolygonCentroid(piece);
            var active = model.Wings.Where(w => !model.UseDomains || w.Covers(c0, model.Expand)).ToList();
            if (active.Count == 0) active = model.Wings;
            var planes = active.SelectMany(w => w.Planes).Distinct().ToList();
            var parts = new List<List<Vector2>> { piece };
            for (int i = 0; i < planes.Count; i++)
                for (int j = i + 1; j < planes.Count; j++)
                {
                    float a = planes[i].Gx - planes[j].Gx, b = planes[i].Gz - planes[j].Gz, c = planes[i].C - planes[j].C;
                    if (a * a + b * b < 1e-10f) continue;
                    parts = SplitAll(parts, a, b, c);
                }
            foreach (var part in parts)
                model.Pieces.Add((part, model.Active(PolygonCentroid(part))));
        }
    }

    private static List<List<Vector2>> SplitAll(List<List<Vector2>> pieces, float a, float b, float c)
    {
        var result = new List<List<Vector2>>(pieces.Count + 2);
        foreach (var p in pieces) SplitConvex(p, a, b, c, result);
        return result;
    }

    /// <summary>Разрез выпуклого многоугольника прямой a·x + b·z + c = 0 (одна или две части, вырожденные отбрасываются).</summary>
    private static void SplitConvex(List<Vector2> poly, float a, float b, float c, List<List<Vector2>> output)
    {
        float eps = 1e-5f * MathF.Sqrt(a * a + b * b);
        var s = new float[poly.Count];
        bool pos = false, neg = false;
        for (int i = 0; i < poly.Count; i++)
        {
            s[i] = a * poly[i].X + b * poly[i].Y + c;
            pos |= s[i] > eps;
            neg |= s[i] < -eps;
        }
        if (!(pos && neg))
        {
            output.Add(poly);
            return;
        }
        var left = new List<Vector2>();
        var right = new List<Vector2>();
        for (int i = 0; i < poly.Count; i++)
        {
            int j = (i + 1) % poly.Count;
            if (s[i] >= -eps) left.Add(poly[i]);
            if (s[i] <= eps) right.Add(poly[i]);
            if ((s[i] > eps && s[j] < -eps) || (s[i] < -eps && s[j] > eps))
            {
                var x = BimMath.Lerp(poly[i], poly[j], s[i] / (s[i] - s[j]));
                left.Add(x);
                right.Add(x);
            }
        }
        if (left.Count >= 3 && Math.Abs(SignedArea2(left)) > 1e-7f) output.Add(left);
        if (right.Count >= 3 && Math.Abs(SignedArea2(right)) > 1e-7f) output.Add(right);
    }

    private static Vector2 PolygonCentroid(IReadOnlyList<Vector2> p)
    {
        double a = 0, cx = 0, cz = 0;
        for (int i = 0; i < p.Count; i++)
        {
            var q = p[i];
            var r = p[(i + 1) % p.Count];
            double k = (double)q.X * r.Y - (double)r.X * q.Y;
            a += k;
            cx += (q.X + r.X) * k;
            cz += (q.Y + r.Y) * k;
        }
        if (Math.Abs(a) < 1e-12) return new Vector2(p.Average(v => v.X), p.Average(v => v.Y));
        return new Vector2((float)(cx / (3 * a)), (float)(cz / (3 * a)));
    }

    /// <summary>Полая скатная кровля по контуру: оболочка (стропильный слой и покрытие), фронтоны над рёбрами контура, подшивка.</summary>
    private static void BuildPolygonHollowRoof(PartSet set, ArchitecturalElement e, PolygonRoofModel model, MaterialKind mat)
    {
        float t = (float)RoofDesign.SlabThickness(e);
        var (structure, cover, tc) = RoofBands(e, mat, t);
        float v = model.Vertical, vc = tc * MathF.Sqrt(1 + model.Slope * model.Slope);
        if (vc > 0)
        {
            AddRoofShellBand(set[structure], model, 0, v - vc);
            AddRoofShellBand(set[cover], model, v - vc, v);
        }
        else AddRoofShellBand(set[structure], model, 0, v);

        // Фронтоны: над каждым ребром контура стен, где низ ската поднимается выше карниза (торцы двускатной, стенки односкатной).
        var outline = model.Outline;
        float tw = (float)RoofDesign.GableWallThickness;
        float y0 = model.EaveY;
        var finish = set[MaterialKind.Finish];
        for (int i = 0; i < outline.Count; i++)
        {
            var p = outline[i];
            var q = outline[(i + 1) % outline.Count];
            var dir = q - p;
            float len = BimMath.Length(dir);
            if (len < 0.05f) continue;
            var u = dir / len;
            var inward = new Vector2(-u.Y, u.X);
            var profile = EdgeProfile(model, p, q, inward);
            float rise = profile.Max(pt => pt.Y) - y0;
            if (rise > 0.05f)
            {
                var wall = new List<Vector2> { new(0, y0), new(len, y0) };
                for (int k = profile.Count - 1; k >= 0; k--)
                    if (profile[k].Y > y0 + 1e-4f || k == 0 || k == profile.Count - 1) wall.Add(new Vector2(profile[k].X, Math.Max(y0, profile[k].Y)));
                AddVerticalPrism(set[MaterialKind.Plaster], p, u, inward, CleanLoop(wall), Math.Min(tw, len * 0.5f));
            }
            else if (model.Expand > 0.05f)
            {
                // Карнизное ребро: подшивка свеса от грани стены до кромки карниза.
                float low = y0 - model.Slope * model.Expand;
                var board = new List<Vector2> { p, q, model.Eaves[(i + 1) % outline.Count], model.Eaves[i] };
                finish.AddPrism(board, low - 0.025f, low);
            }
        }
    }

    /// <summary>Низ ската вдоль ребра p→q (точки (u, y), u — расстояние от p) со всеми изломами, чуть внутрь от грани стены.</summary>
    private static List<Vector2> EdgeProfile(PolygonRoofModel model, Vector2 p, Vector2 q, Vector2 inward)
    {
        float len = BimMath.Length(q - p);
        var ts = new List<float> { 0, 1 };
        var lines = new List<(float A, float B, float C)>();
        var planes = model.Wings.SelectMany(w => w.Planes).Distinct().ToList();
        for (int i = 0; i < planes.Count; i++)
            for (int j = i + 1; j < planes.Count; j++)
                lines.Add((planes[i].Gx - planes[j].Gx, planes[i].Gz - planes[j].Gz, planes[i].C - planes[j].C));
        if (model.UseDomains)
            foreach (var w in model.Wings)
            {
                float e = model.Expand;
                lines.AddRange([(1, 0, -(w.X0 - e)), (1, 0, -(w.X1 + e)), (0, 1, -(w.Z0 - e)), (0, 1, -(w.Z1 + e)), (1, 0, -w.X0), (1, 0, -w.X1), (0, 1, -w.Z0), (0, 1, -w.Z1)]);
            }
        foreach (var (a, b, c) in lines)
        {
            float sp = a * p.X + b * p.Y + c, sq = a * q.X + b * q.Y + c;
            if (Math.Abs(sp - sq) < 1e-9f) continue;
            float tt = sp / (sp - sq);
            if (tt > 1e-4f && tt < 1 - 1e-4f) ts.Add(tt);
        }
        ts.Sort();
        var result = new List<Vector2>();
        foreach (float tt in ts)
        {
            if (result.Count > 0 && (tt * len - result[^1].X) < 1e-4f) continue;
            var pt = BimMath.Lerp(p, q, tt) + inward * 0.002f;
            result.Add(new Vector2(tt * len, model.Underside(pt)));
        }
        return result;
    }

    /// <summary>Замкнутый контур без повторов и коллинеарных точек.</summary>
    private static List<Vector2> CleanLoop(List<Vector2> pts)
    {
        var list = new List<Vector2>();
        foreach (var p in pts)
            if (list.Count == 0 || BimMath.LengthSquared(p - list[^1]) > 1e-8f) list.Add(p);
        if (list.Count > 1 && BimMath.LengthSquared(list[0] - list[^1]) < 1e-8f) list.RemoveAt(list.Count - 1);
        for (int i = 0; i < list.Count && list.Count > 3;)
        {
            var a = list[(i - 1 + list.Count) % list.Count];
            var b = list[i];
            var c = list[(i + 1) % list.Count];
            float cross = (b.X - a.X) * (c.Y - b.Y) - (b.Y - a.Y) * (c.X - b.X);
            if (Math.Abs(cross) < 1e-7f) list.RemoveAt(i);
            else i++;
        }
        return list;
    }

    /// <summary>
    /// Вертикальная стенка: контур (u, y) в плоскости ребра (u — вдоль unit от origin), выдавленный на thickness по inward.
    /// </summary>
    private static void AddVerticalPrism(MeshAccumulator acc, Vector2 origin, Vector2 unit, Vector2 inward, List<Vector2> profile, float thickness)
    {
        if (profile.Count < 3 || thickness <= 1e-4f) return;
        var poly = MeshAccumulator.EnsureCounterClockwise(profile);
        Vector3 Map(Vector2 uv, float w)
        {
            var plan = origin + unit * uv.X + inward * w;
            return new Vector3(plan.X, uv.Y, plan.Y);
        }
        var outward = new Vector3(-inward.X, 0, -inward.Y);
        var inner = new Vector3(inward.X, 0, inward.Y);
        foreach (var (a, b, c) in MeshAccumulator.Triangulate(poly))
        {
            acc.AddTriangle(Map(a, 0), Map(b, 0), Map(c, 0), outward);
            acc.AddTriangle(Map(a, thickness), Map(c, thickness), Map(b, thickness), inner);
        }
        for (int i = 0; i < poly.Count; i++)
        {
            var a = poly[i];
            var b = poly[(i + 1) % poly.Count];
            var d = b - a;
            var n2 = new Vector2(d.Y, -d.X);
            var n = new Vector3(unit.X * n2.X, n2.Y, unit.Y * n2.X);
            acc.AddQuad(Map(a, 0), Map(b, 0), Map(b, thickness), Map(a, thickness), n);
            acc.AddEdge(Map(a, 0), Map(b, 0));
            acc.AddEdge(Map(a, thickness), Map(b, thickness));
        }
    }

    /// <summary>Слой оболочки между низом ската + d0 и + d1 (по вертикали): куски плана сверху и снизу, торцы по контуру карниза.</summary>
    private static void AddRoofShellBand(MeshAccumulator acc, PolygonRoofModel model, float d0, float d1)
    {
        if (d1 - d0 <= 1e-5f) return;
        foreach (var (poly, id) in model.Pieces)
        {
            var plane = model.PlaneOf(id);
            for (int i = 1; i + 1 < poly.Count; i++)
            {
                Vector3 V(Vector2 p, float d) => new(p.X, plane.At(p) + d, p.Y);
                var up = new Vector3(-plane.Gx, 1, -plane.Gz);
                acc.AddTriangle(V(poly[0], d1), V(poly[i], d1), V(poly[i + 1], d1), up);
                acc.AddTriangle(V(poly[0], d0), V(poly[i + 1], d0), V(poly[i], d0), BimMath.Negate(up));
            }
            for (int i = 0; i < poly.Count; i++)
            {
                var a = poly[i];
                var b = poly[(i + 1) % poly.Count];
                var dir = b - a;
                var mid = (a + b) / 2;
                float len = BimMath.Length(dir);
                if (len < 1e-6f) continue;
                var outward2 = new Vector2(dir.Y, -dir.X) / len;
                float ya = plane.At(a), yb = plane.At(b);
                if (!MeshAccumulator.PointInPolygon(mid + outward2 * 1e-3f, model.Eaves))
                {
                    // Торец оболочки по кромке карниза.
                    acc.AddQuad(new Vector3(a.X, ya + d0, a.Y), new Vector3(b.X, yb + d0, b.Y), new Vector3(b.X, yb + d1, b.Y), new Vector3(a.X, ya + d1, a.Y), new Vector3(outward2.X, 0, outward2.Y));
                    acc.AddEdge(new Vector3(a.X, ya + d1, a.Y), new Vector3(b.X, yb + d1, b.Y));
                    acc.AddEdge(new Vector3(a.X, ya + d0, a.Y), new Vector3(b.X, yb + d0, b.Y));
                    continue;
                }
                // Конёк, вальма, ендова: соседний кусок с другой плоскостью — ребро по верху (один раз на пару).
                int other = model.Active(mid + outward2 * 1e-3f);
                if (other <= id) continue;
                var op = model.PlaneOf(other);
                if (Math.Abs(op.Gx - plane.Gx) + Math.Abs(op.Gz - plane.Gz) < 1e-5f) continue;
                acc.AddEdge(new Vector3(a.X, ya + d1, a.Y), new Vector3(b.X, yb + d1, b.Y));
            }
        }
    }

    /// <summary>Площадь куска плана (м²) — для описания полигональной кровли.</summary>
    private static double PiecesArea(PolygonRoofModel model) => model.Pieces.Sum(p => Math.Abs((double)SignedArea2(p.Poly)));

    /// <summary>Объём под оболочкой от отметки карниза до верха (аналог сплошной призмы прежней формы), м³.</summary>
    private static double PiecesSolidVolume(PolygonRoofModel model) =>
        model.Pieces.Sum(p => Math.Abs((double)SignedArea2(p.Poly)) * Math.Max(0, model.PlaneOf(p.Plane).At(PolygonCentroid(p.Poly)) + model.Vertical - model.EaveY));
}
