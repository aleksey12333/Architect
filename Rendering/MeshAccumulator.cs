using System.Numerics;
using AIArchitectStudio.Models.Geometry;

namespace AIArchitectStudio.Rendering;

/// <summary>
/// Лёгкий построитель треугольной сетки с плоскими нормалями и списком рёбер для CAD-контуров.
/// Все точки проходят через текущую матрицу Transform (поэтажная закрутка/сужение параметрических элементов).
/// Обход граней — против часовой стрелки при взгляде снаружи (правая система, Y-Up).
/// </summary>
public sealed class MeshAccumulator
{
    private readonly List<Vector3> _positions = [];
    private readonly List<Vector3> _normals = [];
    private readonly List<int> _indices = [];
    private readonly List<Vector3> _edges = [];

    public Matrix4x4 Transform { get; set; } = Matrix4x4.Identity;

    /// <summary>Собирать ли рёбра (CAD-контуры элемента).</summary>
    public bool CollectEdges { get; set; } = true;

    public bool IsEmpty => _indices.Count == 0;
    public bool HasEdges => _edges.Count > 0;

    /// <summary>Пары точек отрезков-рёбер (уже в координатах после Transform).</summary>
    public IReadOnlyList<Vector3> EdgePoints => _edges;

    /// <summary>Вершины сетки (для расчёта габаритов выделения).</summary>
    public IReadOnlyList<Vector3> Positions => _positions;

    private Vector3 T(Vector3 p) => BimMath.TransformCoordinate(p, Transform);

    private Vector3 TN(Vector3 n)
    {
        var r = BimMath.TransformNormal(n, Transform);
        return BimMath.LengthSquared(r) > 1e-12f ? BimMath.Normalize(r) : Vector3.UnitY;
    }

    // ───────────────────────────── Базовые грани ─────────────────────────────

    /// <summary>Треугольник в локальных координатах; outward (локальный) — желаемое направление нормали.</summary>
    public void AddTriangle(Vector3 a, Vector3 b, Vector3 c, Vector3? outward = null)
    {
        a = T(a); b = T(b); c = T(c);
        var n = BimMath.Cross(b - a, c - a);
        if (BimMath.LengthSquared(n) < 1e-14f) return;
        if (outward is { } o && BimMath.Dot(n, TN(o)) < 0) { (b, c) = (c, b); n = BimMath.Negate(n); }
        n = BimMath.Normalize(n);
        int i = _positions.Count;
        _positions.Add(a); _positions.Add(b); _positions.Add(c);
        _normals.Add(n); _normals.Add(n); _normals.Add(n);
        _indices.Add(i); _indices.Add(i + 1); _indices.Add(i + 2);
    }

    /// <summary>Четырёхугольник a-b-c-d (против часовой снаружи).</summary>
    public void AddQuad(Vector3 a, Vector3 b, Vector3 c, Vector3 d, Vector3? outward = null)
    {
        var ta = T(a); var tb = T(b); var tc = T(c); var td = T(d);
        var n = BimMath.Cross(tb - ta, tc - ta);
        if (BimMath.LengthSquared(n) < 1e-14f) n = BimMath.Cross(tc - ta, td - ta);
        if (BimMath.LengthSquared(n) < 1e-14f) return;
        if (outward is { } o && BimMath.Dot(n, TN(o)) < 0)
        {
            (tb, td) = (td, tb);
            n = BimMath.Negate(n);
        }
        n = BimMath.Normalize(n);
        int i = _positions.Count;
        _positions.Add(ta); _positions.Add(tb); _positions.Add(tc); _positions.Add(td);
        for (int k = 0; k < 4; k++) _normals.Add(n);
        _indices.AddRange([i, i + 1, i + 2, i, i + 2, i + 3]);
    }

    /// <summary>
    /// Четырёхугольник с гладкими нормалями вершин (криволинейные поверхности: дуговые стены, витражи, своды). Обход выбирается так,
    /// чтобы нормаль грани совпадала со средней нормалью вершин.
    /// </summary>
    public void AddQuadSmooth(Vector3 a, Vector3 b, Vector3 c, Vector3 d, Vector3 na, Vector3 nb, Vector3 nc, Vector3 nd)
    {
        var ta = T(a); var tb = T(b); var tc = T(c); var td = T(d);
        var face = BimMath.Cross(tb - ta, tc - ta);
        if (BimMath.LengthSquared(face) < 1e-14f) face = BimMath.Cross(tc - ta, td - ta);
        if (BimMath.LengthSquared(face) < 1e-14f) return;
        var tna = TN(na); var tnb = TN(nb); var tnc = TN(nc); var tnd = TN(nd);
        var avg = tna + tnb + tnc + tnd;
        if (BimMath.Dot(face, avg) < 0)
        {
            (tb, td) = (td, tb);
            (tnb, tnd) = (tnd, tnb);
        }
        int i = _positions.Count;
        _positions.Add(ta); _positions.Add(tb); _positions.Add(tc); _positions.Add(td);
        _normals.Add(tna); _normals.Add(tnb); _normals.Add(tnc); _normals.Add(tnd);
        _indices.AddRange([i, i + 1, i + 2, i, i + 2, i + 3]);
    }

    public void AddEdge(Vector3 a, Vector3 b)
    {
        if (!CollectEdges) return;
        _edges.Add(T(a));
        _edges.Add(T(b));
    }

    public void AddEdgeLoop(IReadOnlyList<Vector3> points)
    {
        for (int i = 0; i < points.Count; i++) AddEdge(points[i], points[(i + 1) % points.Count]);
    }

    // ───────────────────────────── Примитивы ─────────────────────────────

    /// <summary>Прямоугольный параллелепипед по центру и полным размерам.</summary>
    public void AddBox(Vector3 center, float sx, float sy, float sz, bool edges = true)
    {
        if (sx <= 0 || sy <= 0 || sz <= 0) return;
        float x0 = center.X - sx / 2, x1 = center.X + sx / 2;
        float y0 = center.Y - sy / 2, y1 = center.Y + sy / 2;
        float z0 = center.Z - sz / 2, z1 = center.Z + sz / 2;

        var p000 = new Vector3(x0, y0, z0); var p100 = new Vector3(x1, y0, z0);
        var p010 = new Vector3(x0, y1, z0); var p110 = new Vector3(x1, y1, z0);
        var p001 = new Vector3(x0, y0, z1); var p101 = new Vector3(x1, y0, z1);
        var p011 = new Vector3(x0, y1, z1); var p111 = new Vector3(x1, y1, z1);

        AddQuad(p100, p110, p111, p101, Vector3.UnitX);
        AddQuad(p001, p011, p010, p000, BimMath.NegativeUnitX);
        AddQuad(p010, p011, p111, p110, Vector3.UnitY);
        AddQuad(p000, p100, p101, p001, BimMath.NegativeUnitY);
        AddQuad(p001, p101, p111, p011, Vector3.UnitZ);
        AddQuad(p100, p000, p010, p110, BimMath.NegativeUnitZ);

        if (!edges) return;
        AddEdgeLoop([p000, p100, p101, p001]);
        AddEdgeLoop([p010, p110, p111, p011]);
        AddEdge(p000, p010); AddEdge(p100, p110); AddEdge(p101, p111); AddEdge(p001, p011);
    }

    /// <summary>Призма: многоугольник в плане (X, Z), выдавленный по Y от y0 до y1.</summary>
    public void AddPrism(IReadOnlyList<Vector2> polygon, float y0, float y1, bool edges = true)
    {
        if (polygon.Count < 3 || y1 <= y0) return;
        var poly = EnsureCounterClockwise(polygon);

        foreach (var (a, b, c) in Triangulate(poly))
        {
            AddTriangle(new Vector3(a.X, y1, a.Y), new Vector3(b.X, y1, b.Y), new Vector3(c.X, y1, c.Y), Vector3.UnitY);
            AddTriangle(new Vector3(a.X, y0, a.Y), new Vector3(c.X, y0, c.Y), new Vector3(b.X, y0, b.Y), BimMath.NegativeUnitY);
        }

        for (int i = 0; i < poly.Count; i++)
        {
            var p = poly[i];
            var q = poly[(i + 1) % poly.Count];
            var dir = q - p;
            // Для многоугольника против часовой (в осях X, Z) внешняя нормаль ребра — (dz, -dx).
            var outward = new Vector3(dir.Y, 0, -dir.X);
            AddQuad(new Vector3(p.X, y0, p.Y), new Vector3(q.X, y0, q.Y), new Vector3(q.X, y1, q.Y), new Vector3(p.X, y1, p.Y), outward);
            if (edges)
            {
                AddEdge(new Vector3(p.X, y0, p.Y), new Vector3(q.X, y0, q.Y));
                AddEdge(new Vector3(p.X, y1, p.Y), new Vector3(q.X, y1, q.Y));
            }
        }

        if (edges && poly.Count <= 12)
            foreach (var p in poly) AddEdge(new Vector3(p.X, y0, p.Y), new Vector3(p.X, y1, p.Y));
    }

    /// <summary>
    /// Призма с отверстиями: внешний контур минус контуры holes, выдавленные по Y от y0 до y1. Верх и низ заполняются
    /// трапециями (правило ненулевой обмотки: отверстия, выходящие за край или перекрывающие друг друга, вырезаются корректно),
    /// торцы отверстий формируют внутренние грани.
    /// </summary>
    public void AddPrismWithHoles(IReadOnlyList<Vector2> outer, IReadOnlyList<IReadOnlyList<Vector2>> holes, float y0, float y1, bool edges = true)
    {
        var validHoles = holes.Where(h => h.Count >= 3 && Math.Abs(SignedArea(h)) > 1e-6f).ToList();
        if (validHoles.Count == 0)
        {
            AddPrism(outer, y0, y1, edges);
            return;
        }
        AddRegionSolid(outer, validHoles, y0, _ => y1, [], false, edges);
    }

    /// <summary>
    /// Тело над областью «контур минус отверстия»: низ — плоскость y0, верх — кусочно-линейная функция top(x, z)
    /// (плоская плита, двускатная или односкатная кровля по произвольному контуру). Функция должна быть линейной
    /// по поперечной координате внутри каждой полосы между точками излома breaks (по Z, либо по X при alongX).
    /// </summary>
    public void AddRegionSolid(IReadOnlyList<Vector2> outer, IReadOnlyList<IReadOnlyList<Vector2>> holes, float y0,
        Func<Vector2, float> top, IReadOnlyList<float> breaks, bool alongX, bool edges = true)
    {
        if (outer.Count < 3) return;
        var outerLoop = EnsureCounterClockwise(outer);
        var holeLoops = holes.Select(h => EnsureClockwise(h)).ToList();
        var loops = new List<List<Vector2>> { outerLoop };
        loops.AddRange(holeLoops);

        // Верх и низ: трапеции полос.
        foreach (var (a, b, c, d) in FillTrapezoids(loops, breaks, alongX))
        {
            AddQuad(new Vector3(a.X, top(a), a.Y), new Vector3(b.X, top(b), b.Y), new Vector3(c.X, top(c), c.Y), new Vector3(d.X, top(d), d.Y), Vector3.UnitY);
            AddQuad(new Vector3(a.X, y0, a.Y), new Vector3(d.X, y0, d.Y), new Vector3(c.X, y0, c.Y), new Vector3(b.X, y0, b.Y), BimMath.NegativeUnitY);
        }

        // Боковые грани: рёбра делятся точками пересечения контуров и линиями излома, лишние участки отбрасываются.
        for (int li = 0; li < loops.Count; li++)
        {
            var loop = loops[li];
            bool isOuter = li == 0;
            for (int i = 0; i < loop.Count; i++)
            {
                var p = loop[i];
                var q = loop[(i + 1) % loop.Count];
                var ts = new List<float> { 0f, 1f };
                for (int lj = 0; lj < loops.Count; lj++)
                {
                    if (lj == li) continue;
                    var other = loops[lj];
                    for (int k = 0; k < other.Count; k++)
                        if (SegmentIntersection(p, q, other[k], other[(k + 1) % other.Count], out float t, out _)) ts.Add(t);
                }
                foreach (float br in breaks)
                {
                    float pc = alongX ? p.X : p.Y, qc = alongX ? q.X : q.Y;
                    if (Math.Abs(qc - pc) > 1e-7f)
                    {
                        float t = (br - pc) / (qc - pc);
                        if (t > 1e-5f && t < 1 - 1e-5f) ts.Add(t);
                    }
                }
                ts.Sort();

                for (int s = 0; s + 1 < ts.Count; s++)
                {
                    if (ts[s + 1] - ts[s] < 1e-6f) continue;
                    var a = BimMath.Lerp(p, q, ts[s]);
                    var b = BimMath.Lerp(p, q, ts[s + 1]);
                    var mid = (a + b) / 2;
                    bool keep = isOuter
                        ? holeLoops.All(h => !PointInPolygon(mid, h))
                        : PointInPolygon(mid, outerLoop) && holeLoops.Where((h, hi) => hi != li - 1).All(h => !PointInPolygon(mid, h));
                    if (!keep) continue;

                    var dir = b - a;
                    var outward = new Vector3(dir.Y, 0, -dir.X);
                    float ta = top(a), tb = top(b);
                    AddQuad(new Vector3(a.X, y0, a.Y), new Vector3(b.X, y0, b.Y), new Vector3(b.X, tb, b.Y), new Vector3(a.X, ta, a.Y), outward);
                    if (!edges) continue;
                    AddEdge(new Vector3(a.X, y0, a.Y), new Vector3(b.X, y0, b.Y));
                    AddEdge(new Vector3(a.X, ta, a.Y), new Vector3(b.X, tb, b.Y));
                }
            }

            if (!edges || loop.Count > 16) continue;
            foreach (var v in loop)
            {
                bool visible = isOuter ? holeLoops.All(h => !PointInPolygon(v, h)) : PointInPolygon(v, outerLoop);
                if (visible) AddEdge(new Vector3(v.X, y0, v.Y), new Vector3(v.X, top(v), v.Y));
            }
        }

        // Линии излома верха (конёк) — как рёбра.
        if (edges && breaks.Count > 0)
            foreach (var (a, b, c, d) in FillTrapezoids(loops, breaks, alongX))
                foreach (var (u, v) in new[] { (a, b), (d, c) })
                {
                    float uc = alongX ? u.X : u.Y;
                    if (breaks.Any(br => Math.Abs(br - uc) < 1e-4f)) AddEdge(new Vector3(u.X, top(u), u.Y), new Vector3(v.X, top(v), v.Y));
                }
    }

    /// <summary>
    /// Круглое/эллиптическое сечение, выдавленное по Y (колонна, труба): гладкие нормали, при thickness &gt; 0 — полая труба
    /// с внутренней поверхностью и кольцевыми торцами.
    /// </summary>
    public void AddTube(float rx, float rz, float y0, float y1, float thickness = 0, int segments = 32)
    {
        if (rx <= 0 || rz <= 0 || y1 <= y0) return;
        bool hollow = thickness > 0 && thickness < Math.Min(rx, rz) * 0.98f;
        float irx = hollow ? rx - thickness : 0, irz = hollow ? rz - thickness : 0;

        var outerRing = new List<Vector2>(segments);
        var innerRing = new List<Vector2>(segments);
        for (int i = 0; i < segments; i++)
        {
            double t = 2 * Math.PI * i / segments;
            outerRing.Add(new Vector2(rx * (float)Math.Cos(t), rz * (float)Math.Sin(t)));
            if (hollow) innerRing.Add(new Vector2(irx * (float)Math.Cos(t), irz * (float)Math.Sin(t)));
        }

        AddSmoothWall(outerRing, rx, rz, y0, y1, inward: false);
        if (hollow)
        {
            AddSmoothWall(innerRing, irx, irz, y0, y1, inward: true);
            for (int i = 0; i < segments; i++)
            {
                int j = (i + 1) % segments;
                var o0 = outerRing[i]; var o1 = outerRing[j]; var i0 = innerRing[i]; var i1 = innerRing[j];
                AddQuad(new Vector3(o0.X, y1, o0.Y), new Vector3(o1.X, y1, o1.Y), new Vector3(i1.X, y1, i1.Y), new Vector3(i0.X, y1, i0.Y), Vector3.UnitY);
                AddQuad(new Vector3(o0.X, y0, o0.Y), new Vector3(i0.X, y0, i0.Y), new Vector3(i1.X, y0, i1.Y), new Vector3(o1.X, y0, o1.Y), BimMath.NegativeUnitY);
            }
        }
        else
        {
            for (int i = 1; i + 1 < segments; i++)
            {
                AddTriangle(new Vector3(outerRing[0].X, y1, outerRing[0].Y), new Vector3(outerRing[i].X, y1, outerRing[i].Y), new Vector3(outerRing[i + 1].X, y1, outerRing[i + 1].Y), Vector3.UnitY);
                AddTriangle(new Vector3(outerRing[0].X, y0, outerRing[0].Y), new Vector3(outerRing[i + 1].X, y0, outerRing[i + 1].Y), new Vector3(outerRing[i].X, y0, outerRing[i].Y), BimMath.NegativeUnitY);
            }
        }

        AddEdgeLoop(outerRing.Select(p => new Vector3(p.X, y0, p.Y)).ToList());
        AddEdgeLoop(outerRing.Select(p => new Vector3(p.X, y1, p.Y)).ToList());
        if (!hollow) return;
        AddEdgeLoop(innerRing.Select(p => new Vector3(p.X, y0, p.Y)).ToList());
        AddEdgeLoop(innerRing.Select(p => new Vector3(p.X, y1, p.Y)).ToList());
    }

    /// <summary>Боковая поверхность эллиптического цилиндра с гладкими нормалями (наружу или внутрь).</summary>
    private void AddSmoothWall(List<Vector2> ring, float rx, float rz, float y0, float y1, bool inward)
    {
        int start = _positions.Count;
        int n = ring.Count;
        for (int i = 0; i <= n; i++)
        {
            var p = ring[i % n];
            var normal = BimMath.Normalize(new Vector3(p.X / (rx * rx), 0, p.Y / (rz * rz)));
            if (inward) normal = BimMath.Negate(normal);
            _positions.Add(T(new Vector3(p.X, y0, p.Y))); _normals.Add(TN(normal));
            _positions.Add(T(new Vector3(p.X, y1, p.Y))); _normals.Add(TN(normal));
        }
        for (int i = 0; i < n; i++)
        {
            int k = start + i * 2;
            if (inward) _indices.AddRange([k, k + 2, k + 3, k, k + 3, k + 1]);
            else _indices.AddRange([k, k + 3, k + 2, k, k + 1, k + 3]);
        }
    }

    /// <summary>Цилиндр-стержень между двумя точками (колонны, раскосы ферм, диагрид).</summary>
    public void AddCylinder(Vector3 a, Vector3 b, float radius, int segments = 10, bool caps = true)
    {
        var axis = b - a;
        float length = BimMath.Length(axis);
        if (length < 1e-5f || radius <= 0) return;
        axis /= length;
        var helper = Math.Abs(axis.Y) < 0.95f ? Vector3.UnitY : Vector3.UnitX;
        var u = BimMath.Normalize(BimMath.Cross(axis, helper));
        var v = BimMath.Cross(axis, u);

        int start = _positions.Count;
        for (int i = 0; i <= segments; i++)
        {
            double t = 2 * Math.PI * i / segments;
            var n = u * (float)Math.Cos(t) + v * (float)Math.Sin(t);
            _positions.Add(T(a + n * radius)); _normals.Add(TN(n));
            _positions.Add(T(b + n * radius)); _normals.Add(TN(n));
        }
        for (int i = 0; i < segments; i++)
        {
            int k = start + i * 2;
            // Обход выбран так, чтобы нормаль (u × v = axis-ориентированная) смотрела наружу.
            _indices.AddRange([k, k + 2, k + 1, k + 1, k + 2, k + 3]);
        }

        if (!caps) return;
        var ringA = new List<Vector3>(segments);
        var ringB = new List<Vector3>(segments);
        for (int i = 0; i < segments; i++)
        {
            double t = 2 * Math.PI * i / segments;
            var n = u * (float)Math.Cos(t) + v * (float)Math.Sin(t);
            ringA.Add(a + n * radius);
            ringB.Add(b + n * radius);
        }
        for (int i = 1; i < segments - 1; i++)
        {
            AddTriangle(ringA[0], ringA[i], ringA[i + 1], BimMath.Negate(axis));
            AddTriangle(ringB[0], ringB[i], ringB[i + 1], axis);
        }
    }

    /// <summary>Купол (половина эллипсоида) с гладкими нормалями: основание на baseY, радиусы rx, rz, высота h.</summary>
    public void AddDome(float baseY, float rx, float h, float rz, int slices = 48, int stacks = 16)
    {
        int start = _positions.Count;
        for (int s = 0; s <= stacks; s++)
        {
            double phi = Math.PI / 2 * s / stacks; // 0 — экватор, π/2 — вершина
            float cy = (float)Math.Sin(phi), cr = (float)Math.Cos(phi);
            for (int i = 0; i <= slices; i++)
            {
                double theta = 2 * Math.PI * i / slices;
                float cx = (float)Math.Cos(theta), cz = (float)Math.Sin(theta);
                var p = new Vector3(rx * cr * cx, baseY + h * cy, rz * cr * cz);
                var n = new Vector3(cr * cx / Math.Max(rx, 1e-3f), cy / Math.Max(h, 1e-3f), cr * cz / Math.Max(rz, 1e-3f));
                _positions.Add(T(p));
                _normals.Add(TN(BimMath.Normalize(n)));
            }
        }
        int row = slices + 1;
        for (int s = 0; s < stacks; s++)
        {
            for (int i = 0; i < slices; i++)
            {
                int k = start + s * row + i;
                _indices.AddRange([k, k + row, k + 1, k + 1, k + row, k + row + 1]);
            }
        }

        if (!CollectEdges) return;
        var ring = new List<Vector3>(slices);
        for (int i = 0; i < slices; i++)
        {
            double theta = 2 * Math.PI * i / slices;
            ring.Add(new Vector3(rx * (float)Math.Cos(theta), baseY, rz * (float)Math.Sin(theta)));
        }
        AddEdgeLoop(ring);
    }

    // ───────────────────────────── Результат ─────────────────────────────

    /// <summary>Сетка из накопленных треугольников: списки передаются во владение сетке (аккумулятор после этого не меняется).</summary>
    public BimMesh ToMesh() => new(_positions, _normals, _indices);

    public BimLineSet? ToEdges() => _edges.Count == 0 ? null : BimLineSet.FromSegments(_edges);

    // ───────────────────────────── Геометрия многоугольников ─────────────────────────────

    private static float SignedArea(IReadOnlyList<Vector2> poly)
    {
        float sum = 0;
        for (int i = 0; i < poly.Count; i++)
        {
            var a = poly[i];
            var b = poly[(i + 1) % poly.Count];
            sum += a.X * b.Y - b.X * a.Y;
        }
        return sum / 2;
    }

    /// <summary>Приводит обход к «против часовой» в осях (X, Z).</summary>
    public static List<Vector2> EnsureCounterClockwise(IReadOnlyList<Vector2> polygon)
    {
        var list = polygon.ToList();
        if (SignedArea(list) < 0) list.Reverse();
        return list;
    }

    /// <summary>Обход «по часовой» (контуры отверстий).</summary>
    public static List<Vector2> EnsureClockwise(IReadOnlyList<Vector2> polygon)
    {
        var list = polygon.ToList();
        if (SignedArea(list) > 0) list.Reverse();
        return list;
    }

    /// <summary>Точка внутри многоугольника (правило чётности).</summary>
    public static bool PointInPolygon(Vector2 p, IReadOnlyList<Vector2> poly)
    {
        bool inside = false;
        for (int i = 0, j = poly.Count - 1; i < poly.Count; j = i++)
        {
            var a = poly[i];
            var b = poly[j];
            if ((a.Y > p.Y) != (b.Y > p.Y) && p.X < (b.X - a.X) * (p.Y - a.Y) / (b.Y - a.Y) + a.X) inside = !inside;
        }
        return inside;
    }

    /// <summary>Пересечение отрезков p-q и a-b во внутренних точках: параметры t (по p-q) и u (по a-b).</summary>
    private static bool SegmentIntersection(Vector2 p, Vector2 q, Vector2 a, Vector2 b, out float t, out float u)
    {
        t = u = 0;
        var r = q - p;
        var s = b - a;
        float denom = Cross(r, s);
        if (Math.Abs(denom) < 1e-12f) return false;
        t = Cross(a - p, s) / denom;
        u = Cross(a - p, r) / denom;
        return t > 1e-5f && t < 1 - 1e-5f && u > -1e-5f && u < 1 + 1e-5f;
    }

    /// <summary>
    /// Разбиение области (контуры с ориентацией: внешний — против часовой, отверстия — по часовой) на трапеции полосами
    /// между всеми вершинами, точками пересечения рёбер и линиями излома; заполнение по правилу ненулевой обмотки.
    /// alongX — полосы по координате X (для кровли с коньком вдоль Z).
    /// </summary>
    public static List<(Vector2 A, Vector2 B, Vector2 C, Vector2 D)> FillTrapezoids(IReadOnlyList<IReadOnlyList<Vector2>> loops, IReadOnlyList<float> breaks, bool alongX)
    {
        // В «повёрнутой» системе полосы всегда идут по второй координате.
        static Vector2 Swap(Vector2 v) => new(v.Y, v.X);
        var work = loops.Select(l => alongX ? l.Select(Swap).Reverse().ToList() : l.ToList()).ToList();

        var edges = new List<(Vector2 P, Vector2 Q)>();
        foreach (var loop in work)
            for (int i = 0; i < loop.Count; i++)
            {
                var p = loop[i];
                var q = loop[(i + 1) % loop.Count];
                if (Math.Abs(q.Y - p.Y) > 1e-7f) edges.Add((p, q));
            }

        var ys = new List<float>();
        foreach (var loop in work) ys.AddRange(loop.Select(v => v.Y));
        ys.AddRange(breaks);
        for (int i = 0; i < edges.Count; i++)
            for (int j = i + 1; j < edges.Count; j++)
                if (SegmentIntersection(edges[i].P, edges[i].Q, edges[j].P, edges[j].Q, out float t, out _))
                    ys.Add(edges[i].P.Y + (edges[i].Q.Y - edges[i].P.Y) * t);
        ys.Sort();
        var levels = new List<float>();
        foreach (float y in ys)
            if (levels.Count == 0 || y - levels[^1] > 1e-5f) levels.Add(y);

        static float XAt((Vector2 P, Vector2 Q) e, float y) => e.P.X + (e.Q.X - e.P.X) * (y - e.P.Y) / (e.Q.Y - e.P.Y);

        var result = new List<(Vector2, Vector2, Vector2, Vector2)>();
        var crossings = new List<(float Xm, float X0, float X1, int Dir)>();
        for (int s = 0; s + 1 < levels.Count; s++)
        {
            float y0 = levels[s], y1 = levels[s + 1], ym = (y0 + y1) / 2;
            crossings.Clear();
            foreach (var e in edges)
            {
                float lo = Math.Min(e.P.Y, e.Q.Y), hi = Math.Max(e.P.Y, e.Q.Y);
                if (ym <= lo || ym >= hi) continue;
                crossings.Add((XAt(e, ym), XAt(e, y0), XAt(e, y1), e.Q.Y > e.P.Y ? 1 : -1));
            }
            crossings.Sort((a, b) => a.Xm.CompareTo(b.Xm));

            int winding = 0;
            for (int k = 0; k + 1 < crossings.Count; k++)
            {
                winding -= crossings[k].Dir;
                if (winding <= 0 || crossings[k + 1].Xm - crossings[k].Xm < 1e-7f) continue;
                var a = new Vector2(crossings[k].X0, y0);
                var b = new Vector2(crossings[k + 1].X0, y0);
                var c = new Vector2(crossings[k + 1].X1, y1);
                var d = new Vector2(crossings[k].X1, y1);
                result.Add(alongX ? (Swap(a), Swap(b), Swap(c), Swap(d)) : (a, b, c, d));
            }
        }
        return result;
    }

    /// <summary>Триангуляция простого многоугольника отсечением ушей (поддерживает невыпуклые контуры).</summary>
    public static IEnumerable<(Vector2 A, Vector2 B, Vector2 C)> Triangulate(IReadOnlyList<Vector2> ccwPolygon)
    {
        var idx = Enumerable.Range(0, ccwPolygon.Count).ToList();
        int guard = 0;
        while (idx.Count > 3 && guard++ < 10000)
        {
            bool clipped = false;
            for (int i = 0; i < idx.Count; i++)
            {
                var a = ccwPolygon[idx[(i + idx.Count - 1) % idx.Count]];
                var b = ccwPolygon[idx[i]];
                var c = ccwPolygon[idx[(i + 1) % idx.Count]];
                if (Cross(b - a, c - b) <= 1e-9f) continue; // вогнутая вершина

                bool containsOther = false;
                for (int j = 0; j < idx.Count && !containsOther; j++)
                {
                    var p = ccwPolygon[idx[j]];
                    if (p == a || p == b || p == c) continue;
                    containsOther = PointInTriangle(p, a, b, c);
                }
                if (containsOther) continue;

                yield return (a, b, c);
                idx.RemoveAt(i);
                clipped = true;
                break;
            }
            if (!clipped) break; // вырожденный контур: оставшееся закрываем веером
        }
        for (int i = 1; i + 1 < idx.Count; i++)
            yield return (ccwPolygon[idx[0]], ccwPolygon[idx[i]], ccwPolygon[idx[i + 1]]);
    }

    private static float Cross(Vector2 a, Vector2 b) => a.X * b.Y - a.Y * b.X;

    private static bool PointInTriangle(Vector2 p, Vector2 a, Vector2 b, Vector2 c) =>
        Cross(b - a, p - a) >= 0 && Cross(c - b, p - b) >= 0 && Cross(a - c, p - c) >= 0;
}
