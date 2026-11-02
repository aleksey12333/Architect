using System.Numerics;
using AIArchitectStudio.Models.Geometry;

namespace AIArchitectStudio.Rendering;

/// <summary>Грань секущего параллелепипеда (плоскость разреза).</summary>
public enum SectionFace
{
    MinX,
    MaxX,
    MinY,
    MaxY,
    MinZ,
    MaxZ
}

/// <summary>
/// Секущий параллелепипед (Section Box) в мировых координатах сцены (Y-Up, метры): остаётся всё, что лежит внутри.
/// </summary>
public readonly record struct SectionBox(double MinX, double MaxX, double MinY, double MaxY, double MinZ, double MaxZ)
{
    public Vector3 Min => new((float)MinX, (float)MinY, (float)MinZ);
    public Vector3 Max => new((float)MaxX, (float)MaxY, (float)MaxZ);
    public Vector3 Center => (Min + Max) / 2;

    /// <summary>Внешняя нормаль грани (направление «отрезанной» стороны).</summary>
    public static Vector3 Normal(SectionFace face) => face switch
    {
        SectionFace.MinX => BimMath.NegativeUnitX,
        SectionFace.MaxX => Vector3.UnitX,
        SectionFace.MinY => BimMath.NegativeUnitY,
        SectionFace.MaxY => Vector3.UnitY,
        SectionFace.MinZ => BimMath.NegativeUnitZ,
        _ => Vector3.UnitZ
    };

    /// <summary>Значение грани вдоль её оси.</summary>
    public double Value(SectionFace face) => face switch
    {
        SectionFace.MinX => MinX,
        SectionFace.MaxX => MaxX,
        SectionFace.MinY => MinY,
        SectionFace.MaxY => MaxY,
        SectionFace.MinZ => MinZ,
        _ => MaxZ
    };

    public SectionBox With(SectionFace face, double value) => face switch
    {
        SectionFace.MinX => this with { MinX = value },
        SectionFace.MaxX => this with { MaxX = value },
        SectionFace.MinY => this with { MinY = value },
        SectionFace.MaxY => this with { MaxY = value },
        SectionFace.MinZ => this with { MinZ = value },
        _ => this with { MaxZ = value }
    };

    /// <summary>Центр грани (положение маркера перетаскивания).</summary>
    public Vector3 FaceCenter(SectionFace face)
    {
        var c = Center;
        return face switch
        {
            SectionFace.MinX => c with { X = (float)MinX },
            SectionFace.MaxX => c with { X = (float)MaxX },
            SectionFace.MinY => c with { Y = (float)MinY },
            SectionFace.MaxY => c with { Y = (float)MaxY },
            SectionFace.MinZ => c with { Z = (float)MinZ },
            _ => c with { Z = (float)MaxZ }
        };
    }

    /// <summary>Плоскость грани в виде «n·p ≤ d» (точки внутри коробки удовлетворяют всем шести неравенствам).</summary>
    public (Vector3 N, float D) Plane(SectionFace face)
    {
        var n = Normal(face);
        float d = (float)Value(face) * (n.X + n.Y + n.Z);
        return (n, d);
    }
}

/// <summary>2D-трапеция сечения в координатах плоскости (U, V).</summary>
public readonly record struct SectionTrapezoid(Vector2 A, Vector2 B, Vector2 C, Vector2 D);

/// <summary>Результат честного отсечения геометрии элемента коробкой: части в мировых координатах, рёбра и заливка сечений.</summary>
public sealed class ClippedGeometry
{
    public required IReadOnlyList<BimMeshPart> Parts { get; init; }
    public BimLineSet? Edges { get; init; }

    /// <summary>Заливка плоскостей разреза (все материалы вместе) — null, если элемент не пересекает грани.</summary>
    public BimMesh? Caps { get; init; }

    /// <summary>Заливка среза по материалам: несущие части — тёмный тон бетона, слои пирога — своими тонами.</summary>
    public IReadOnlyList<BimMeshPart> CapParts { get; init; } = [];

    public bool IsEmpty => Parts.Count == 0 && Caps is null;
}

/// <summary>
/// Отсечение полигонов элемента плоскостями Section Box (алгоритм Сазерленда — Ходжмена для каждого треугольника)
/// и построение заливки сечения: пересечения граней замкнутой оболочки с плоскостью собираются в ориентированные отрезки,
/// которые заполняются полосами по правилу ненулевой обмотки — так разрезанная стена, плита, колонна или узел
/// показываются сплошным срезом, а не пустой «скорлупой».
/// </summary>
public static class SectionBoxClipper
{
    private const float Eps = 1e-5f;

    private static readonly SectionFace[] Faces = Enum.GetValues<SectionFace>();

    /// <summary>
    /// Отсекает геометрию элемента. null — элемент целиком внутри коробки (можно показывать исходные модели без изменений).
    /// Вершины результата — в мировых координатах (матрица модели — единичная).
    /// </summary>
    public static ClippedGeometry? Clip(IGeometryElement geometry, SectionBox box)
    {
        var corners = geometry.WorldCorners();
        var bb = BimBounds.FromPoints(corners);
        var bmin = box.Min;
        var bmax = box.Max;
        if (bb.Minimum.X >= bmin.X - Eps && bb.Maximum.X <= bmax.X + Eps &&
            bb.Minimum.Y >= bmin.Y - Eps && bb.Maximum.Y <= bmax.Y + Eps &&
            bb.Minimum.Z >= bmin.Z - Eps && bb.Maximum.Z <= bmax.Z + Eps)
            return null;

        bool outside = bb.Maximum.X < bmin.X || bb.Minimum.X > bmax.X ||
                       bb.Maximum.Y < bmin.Y || bb.Minimum.Y > bmax.Y ||
                       bb.Maximum.Z < bmin.Z || bb.Minimum.Z > bmax.Z;
        if (outside) return new ClippedGeometry { Parts = [] };

        // Плоскости, которые действительно пересекают габарит элемента.
        var active = Faces.Where(f =>
        {
            var (n, d) = box.Plane(f);
            return corners.Any(c => BimMath.Dot(n, c) > d + Eps);
        }).ToList();

        var m = geometry.Transform;
        var parts = new List<BimMeshPart>();
        var capSegments = new Dictionary<(SectionFace Face, MaterialKind Cut), List<(Vector3 P, Vector3 Q)>>();
        foreach (var part in geometry.Parts)
        {
            var mesh = part.Mesh;
                        var positions = mesh.Positions.Select(p => BimMath.TransformCoordinate(p, m)).ToArray();
            var normals = mesh.Normals is { } nn && nn.Count == positions.Length
                ? nn.Select(v => SafeNormalize(BimMath.TransformNormal(v, m))).ToArray()
                : null;
            bool solid = !MaterialCatalog.IsTransparent(part.Material);
            var cutKind = MaterialCatalog.CutKind(part.Material);

            var outPositions = new List<Vector3>();
            var outNormals = new List<Vector3>();
            var outIndices = new List<int>();
            var idx = mesh.TriangleIndices;
            var poly = new List<(Vector3 P, Vector3 N)>(8);
            var next = new List<(Vector3 P, Vector3 N)>(8);
            for (int t = 0; t + 2 < idx.Count; t += 3)
            {
                int i0 = idx[t], i1 = idx[t + 1], i2 = idx[t + 2];
                var a = positions[i0];
                var b = positions[i1];
                var c = positions[i2];
                var faceNormal = BimMath.Cross(b - a, c - a);
                var n0 = normals?[i0] ?? SafeNormalize(faceNormal);
                var n1 = normals?[i1] ?? n0;
                var n2 = normals?[i2] ?? n0;

                if (solid)
                {
                    // Ориентация отрезка сечения — по «внешней» нормали грани (сглаженные нормали усредняются).
                    var orient = n0 + n1 + n2;
                    if (BimMath.LengthSquared(orient) < 1e-12f) orient = faceNormal;
                    foreach (var f in active)
                        if (TriangleSection(a, b, c, orient, box.Plane(f)) is { } seg)
                        {
                            if (!capSegments.TryGetValue((f, cutKind), out var list)) capSegments[(f, cutKind)] = list = [];
                            list.Add(seg);
                        }
                }

                poly.Clear();
                poly.Add((a, n0));
                poly.Add((b, n1));
                poly.Add((c, n2));
                foreach (var f in active)
                {
                    var (pn, pd) = box.Plane(f);
                    ClipPolygon(poly, next, pn, pd);
                    (poly, next) = (next, poly);
                    if (poly.Count < 3) break;
                }
                if (poly.Count < 3) continue;

                int start = outPositions.Count;
                foreach (var (p, n) in poly)
                {
                    outPositions.Add(p);
                    outNormals.Add(n);
                }
                for (int k = 1; k + 1 < poly.Count; k++)
                {
                    outIndices.Add(start);
                    outIndices.Add(start + k);
                    outIndices.Add(start + k + 1);
                }
            }

            if (outIndices.Count >= 3)
                parts.Add(new BimMeshPart(new BimMesh(outPositions, outNormals, outIndices), part.Material));

        }

        // Рёбра: отрезки, обрезанные коробкой, плюс контуры сечения.
        var edgeBuilder = new BimLineSet();
        bool anyEdge = false;
        if (geometry.Edges is { } sourceEdges)
        {
            var edgePositions = sourceEdges.Positions;
            var edgeIdx = sourceEdges.Indices;
            for (int i = 0; i + 1 < edgeIdx.Count; i += 2)
            {
                var p = BimMath.TransformCoordinate(edgePositions[edgeIdx[i]], m);
                var q = BimMath.TransformCoordinate(edgePositions[edgeIdx[i + 1]], m);
                if (ClipSegment(ref p, ref q, box))
                {
                    edgeBuilder.AddLine(p, q);
                    anyEdge = true;
                }
            }
        }

        // Заливка сечения по каждой пересечённой грани и материалу среза, обрезанная остальными гранями коробки.
        var capPositions = new List<Vector3>();
        var capNormals = new List<Vector3>();
        var capIndices = new List<int>();
        var capByKind = new Dictionary<MaterialKind, (int Start, List<int> Indices)>();
        foreach (var ((face, cutKind), segments) in capSegments)
        {
            if (!capByKind.TryGetValue(cutKind, out var kindCaps)) capByKind[cutKind] = kindCaps = (0, []);
            var (u, v) = PlaneAxes(face);
            float level = (float)box.Value(face);
            var normal = SectionBox.Normal(face);
            var poly = new List<(Vector3 P, Vector3 N)>(8);
            var next = new List<(Vector3 P, Vector3 N)>(8);
            foreach (var trap in FillSegments(segments.Select(s => (To2D(s.P, u, v), To2D(s.Q, u, v))).ToList()))
            {
                poly.Clear();
                foreach (var p2 in new[] { trap.A, trap.B, trap.C, trap.D })
                    poly.Add((From2D(p2, u, v, normal, level), normal));
                foreach (var other in Faces)
                {
                    if (other == face) continue;
                    var (pn, pd) = box.Plane(other);
                    ClipPolygon(poly, next, pn, pd);
                    (poly, next) = (next, poly);
                    if (poly.Count < 3) break;
                }
                if (poly.Count < 3) continue;
                int start = capPositions.Count;
                foreach (var (p, n) in poly)
                {
                    // Чуть наружу плоскости: срез не мерцает на совпадающих гранях соседних элементов.
                    capPositions.Add(p + n * 0.0015f);
                    capNormals.Add(n);
                }
                for (int k = 1; k + 1 < poly.Count; k++)
                {
                    capIndices.Add(start);
                    capIndices.Add(start + k);
                    capIndices.Add(start + k + 1);
                    kindCaps.Indices.Add(start);
                    kindCaps.Indices.Add(start + k);
                    kindCaps.Indices.Add(start + k + 1);
                }
            }
            foreach (var (p0, q0) in segments)
            {
                var p = p0;
                var q = q0;
                if (ClipSegment(ref p, ref q, box))
                {
                    edgeBuilder.AddLine(p + normal * 0.002f, q + normal * 0.002f);
                    anyEdge = true;
                }
            }
        }

        var capParts = new List<BimMeshPart>();
        foreach (var (kind, (_, indices)) in capByKind)
        {
            if (indices.Count < 3) continue;
            // Отдельная сетка на материал: вершины переиндексируются из общего буфера среза.
            var map = new Dictionary<int, int>();
            var pos = new List<Vector3>();
            var nor = new List<Vector3>();
            var idx = new List<int>();
            foreach (int i in indices)
            {
                if (!map.TryGetValue(i, out int j))
                {
                    j = pos.Count;
                    map[i] = j;
                    pos.Add(capPositions[i]);
                    nor.Add(capNormals[i]);
                }
                idx.Add(j);
            }
            capParts.Add(new BimMeshPart(new BimMesh(pos, nor, idx), kind));
        }

        return new ClippedGeometry
        {
            Parts = parts,
            CapParts = capParts,
            Edges = anyEdge ? edgeBuilder : null,
            Caps = capIndices.Count >= 3 ? new BimMesh(capPositions, capNormals, capIndices) : null

        };
    }

    /// <summary>
    /// Сечение геометрии элемента произвольной плоскостью n·p = d (для чертежа разреза): заливка и контур среза
    /// в осях плоскости (U, V).
    /// </summary>
    public static (List<SectionTrapezoid> Fill, List<(Vector2 P, Vector2 Q)> Outline) PlaneSection(IGeometryElement geometry, Vector3 n, float d, Vector3 u, Vector3 v)
    {
        var m = geometry.Transform;
        var segments = new List<(Vector2, Vector2)>();
        foreach (var part in geometry.Parts)
        {
            if (MaterialCatalog.IsTransparent(part.Material)) continue;
            var mesh = part.Mesh;
                        var positions = mesh.Positions.Select(p => BimMath.TransformCoordinate(p, m)).ToArray();
            var normals = mesh.Normals is { } nn && nn.Count == positions.Length
                ? nn.Select(x => SafeNormalize(BimMath.TransformNormal(x, m))).ToArray()
                : null;
            var idx = mesh.TriangleIndices;
            for (int t = 0; t + 2 < idx.Count; t += 3)
            {
                var a = positions[idx[t]];
                var b = positions[idx[t + 1]];
                var c = positions[idx[t + 2]];
                var orient = normals is null ? BimMath.Cross(b - a, c - a) : normals[idx[t]] + normals[idx[t + 1]] + normals[idx[t + 2]];
                if (TriangleSection(a, b, c, orient, (n, d)) is { } seg)
                    segments.Add((new Vector2(BimMath.Dot(seg.P, u), BimMath.Dot(seg.P, v)), new Vector2(BimMath.Dot(seg.Q, u), BimMath.Dot(seg.Q, v))));
            }
        }
        return (FillSegments(segments), segments);
    }

    /// <summary>Отрезок пересечения треугольника с плоскостью, ориентированный по внешней нормали грани (контур сечения — против часовой).</summary>
    private static (Vector3 P, Vector3 Q)? TriangleSection(Vector3 a, Vector3 b, Vector3 c, Vector3 outward, (Vector3 N, float D) plane)
    {
        float da = BimMath.Dot(plane.N, a) - plane.D;
        float db = BimMath.Dot(plane.N, b) - plane.D;
        float dc = BimMath.Dot(plane.N, c) - plane.D;
        // Вершина ровно на плоскости считается лежащей «снаружи» (≥ 0) — без двойного счёта.
        bool sa = da >= 0, sb = db >= 0, sc = dc >= 0;
        if (sa == sb && sb == sc) return null;

        var points = new List<Vector3>(2);
        void Edge(Vector3 p, float dp, bool sp, Vector3 q, float dq, bool sq)
        {
            if (sp == sq) return;
            float t = dp / (dp - dq);
            points.Add(p + (q - p) * t);
        }
        Edge(a, da, sa, b, db, sb);
        Edge(b, db, sb, c, dc, sc);
        Edge(c, dc, sc, a, da, sa);
        if (points.Count != 2 || BimMath.LengthSquared(points[1] - points[0]) < 1e-12f) return null;

        var dir = BimMath.Cross(outward, plane.N);
        return BimMath.Dot(points[1] - points[0], dir) >= 0 ? (points[0], points[1]) : (points[1], points[0]);
    }

    /// <summary>
    /// Заливка области, ограниченной ориентированными отрезками, полосами между всеми вершинами (правило ненулевой обмотки:
    /// пересекающиеся тела одного элемента не дают «дыр»).
    /// </summary>
    public static List<SectionTrapezoid> FillSegments(IReadOnlyList<(Vector2 P, Vector2 Q)> segments)
    {
        var result = new List<SectionTrapezoid>();
        var edges = segments.Where(s => Math.Abs(s.Q.Y - s.P.Y) > 1e-7f).ToList();
        if (edges.Count < 2) return result;

        var ys = edges.SelectMany(e => new[] { e.P.Y, e.Q.Y }).ToList();
        ys.Sort();
        var levels = new List<float>();
        foreach (float y in ys)
            if (levels.Count == 0 || y - levels[^1] > 1e-6f) levels.Add(y);

        var crossings = new List<(float Xm, float X0, float X1, int Dir)>();
        for (int s = 0; s + 1 < levels.Count; s++)
        {
            float y0 = levels[s], y1 = levels[s + 1], ym = (y0 + y1) / 2;
            crossings.Clear();
            foreach (var (p, q) in edges)
            {
                float lo = Math.Min(p.Y, q.Y), hi = Math.Max(p.Y, q.Y);
                if (ym <= lo || ym >= hi) continue;
                float XAt(float y) => p.X + (q.X - p.X) * (y - p.Y) / (q.Y - p.Y);
                crossings.Add((XAt(ym), XAt(y0), XAt(y1), q.Y > p.Y ? 1 : -1));
            }
            crossings.Sort((a, b) => a.Xm.CompareTo(b.Xm));
            int winding = 0;
            for (int k = 0; k + 1 < crossings.Count; k++)
            {
                winding += crossings[k].Dir;
                if (winding == 0 || crossings[k + 1].Xm - crossings[k].Xm < 1e-7f) continue;
                result.Add(new SectionTrapezoid(
                    new Vector2(crossings[k].X0, y0), new Vector2(crossings[k + 1].X0, y0),
                    new Vector2(crossings[k + 1].X1, y1), new Vector2(crossings[k].X1, y1)));
            }
        }
        return result;
    }

    /// <summary>Оси плоскости грани: для граней X — (Z, Y), для Y — (X, Z), для Z — (X, Y).</summary>
    public static (Vector3 U, Vector3 V) PlaneAxes(SectionFace face) => face switch
    {
        SectionFace.MinX or SectionFace.MaxX => (Vector3.UnitZ, Vector3.UnitY),
        SectionFace.MinY or SectionFace.MaxY => (Vector3.UnitX, Vector3.UnitZ),
        _ => (Vector3.UnitX, Vector3.UnitY)
    };

    private static Vector2 To2D(Vector3 p, Vector3 u, Vector3 v) => new(BimMath.Dot(p, u), BimMath.Dot(p, v));

    private static Vector3 From2D(Vector2 p, Vector3 u, Vector3 v, Vector3 normal, float level) =>
        u * p.X + v * p.Y + new Vector3(Math.Abs(normal.X), Math.Abs(normal.Y), Math.Abs(normal.Z)) * level;

    /// <summary>Отсечение выпуклого многоугольника полупространством n·p ≤ d (Сазерленд — Ходжмен).</summary>
    private static void ClipPolygon(List<(Vector3 P, Vector3 N)> input, List<(Vector3 P, Vector3 N)> output, Vector3 n, float d)
    {
        output.Clear();
        int count = input.Count;
        for (int i = 0; i < count; i++)
        {
            var cur = input[i];
            var prev = input[(i + count - 1) % count];
            float dc = BimMath.Dot(n, cur.P) - d;
            float dp = BimMath.Dot(n, prev.P) - d;
            bool inCur = dc <= Eps, inPrev = dp <= Eps;
            if (inCur)
            {
                if (!inPrev) output.Add(Lerp(prev, cur, dp / (dp - dc)));
                output.Add(cur);
            }
            else if (inPrev)
            {
                output.Add(Lerp(prev, cur, dp / (dp - dc)));
            }
        }
    }

    private static (Vector3 P, Vector3 N) Lerp((Vector3 P, Vector3 N) a, (Vector3 P, Vector3 N) b, float t) =>
        (a.P + (b.P - a.P) * t, SafeNormalize(a.N + (b.N - a.N) * t));

    /// <summary>Отсечение отрезка коробкой (Лян — Барски). false — отрезок целиком снаружи.</summary>
    public static bool ClipSegment(ref Vector3 p, ref Vector3 q, SectionBox box)
    {
        float t0 = 0, t1 = 1;
        var dir = q - p;
        bool Edge(float pp, float qq)
        {
            if (Math.Abs(pp) < 1e-12f) return qq >= -Eps;
            float r = qq / pp;
            if (pp < 0) { if (r > t1) return false; if (r > t0) t0 = r; }
            else { if (r < t0) return false; if (r < t1) t1 = r; }
            return true;
        }
        if (!Edge(-dir.X, p.X - (float)box.MinX) || !Edge(dir.X, (float)box.MaxX - p.X) ||
            !Edge(-dir.Y, p.Y - (float)box.MinY) || !Edge(dir.Y, (float)box.MaxY - p.Y) ||
            !Edge(-dir.Z, p.Z - (float)box.MinZ) || !Edge(dir.Z, (float)box.MaxZ - p.Z))
            return false;
        var start = p;
        p = start + dir * t0;
        q = start + dir * t1;
        return t1 - t0 > 1e-6f;
    }

    private static Vector3 SafeNormalize(Vector3 v) => BimMath.LengthSquared(v) > 1e-12f ? BimMath.Normalize(v) : Vector3.UnitY;
}
