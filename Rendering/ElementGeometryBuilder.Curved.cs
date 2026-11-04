using System.Numerics;
using AIArchitectStudio.Models;
using AIArchitectStudio.Models.Geometry;

namespace AIArchitectStudio.Rendering;

/// <summary>
/// Криволинейная и непрямоугольная геометрия (волна 4): дуговые стены по окружности (24–48 сегментов на дугу, гладкие нормали,
/// радиальная врезка окон и дверей — откосы по радиусу, подоконная зона и перемычка по дуге), биссектрисный рез торцов стен под
/// произвольным углом (стыковка без клиновидных щелей), дуговые и арочные витражи с вертикальными импостами и ригелями этажей,
/// арочные балки (стрела вверх), купольная, коническая и складчатая кровли. Элементы без параметров формы строятся прежним путём.
/// </summary>
public static partial class ElementGeometryBuilder
{
    /// <summary>Наименьшее и наибольшее число сегментов дуги на полный угол дуги.</summary>
    private const int ArcMinSegments = 24, ArcMaxSegments = 48;

    /// <summary>Строит криволинейный элемент; false — элемент без формы волны 4 (прежний путь).</summary>
    private static bool TryBuildCurved(PartSet set, ArchitecturalElement e, IReadOnlyList<ArchitecturalElement> hosted, float w, float h, float d, MaterialKind mat,
        List<Vector2>? poly, (float Scale, float TwistDegrees)? floorTransform)
    {
        switch (e.Category)
        {
            case BimCategory.Roof:
            {
                string shape = RoofShapes.Normalize(e.RoofShape);
                if (shape is not (RoofShapes.Dome or RoofShapes.Cone or RoofShapes.Folded)) return false;
                BuildSpecialRoof(set[mat], shape, poly, SlabHoles(e, hosted, floorTransform), w, h, d);
                return true;
            }
            case BimCategory.Wall when poly is null && CurvedDesign.HasShape(e):
                if (e.Layers.Count > 0)
                    foreach (var (layer, z0, z1) in LayerBands(e.Layers, -d / 2, d / 2, fromTop: true))
                        BuildShapedWall(set[MaterialCatalog.LayerKind(layer, mat)], e, hosted, w, h, z1 - z0, (z0 + z1) / 2);
                else BuildShapedWall(set[mat], e, hosted, w, h, d, 0);
                return true;
            case BimCategory.CurtainWall when poly is null && CurvedDesign.HasShape(e):
                BuildShapedCurtainWall(set, e, w, h, d);
                return true;
            case BimCategory.Beam when poly is null && CurvedDesign.IsArc(e):
                BuildArchBeam(set[mat], e, w, h, d);
                return true;
            default:
                return false;
        }
    }

    /// <summary>Число сегментов полной дуги: шаг ≈ 3,75°, не меньше 24 и не больше 48.</summary>
    private static int ArcSegments(double halfAngle) =>
        Math.Clamp((int)Math.Ceiling(2 * halfAngle * 180 / Math.PI / 3.75), ArcMinSegments, ArcMaxSegments);

    // ───────────────────────────── Стена: дуга или рез торцов ─────────────────────────────

    /// <summary>
    /// Стена по дуге или с косыми торцами, разрезанная привязанными окнами и дверями (HostElementId) так же, как прямая: участки
    /// между проёмами, подоконная зона и перемычка. Хордовая координата проёма переводится в угол дуги — откосы радиальные.
    /// </summary>
    private static void BuildShapedWall(MeshAccumulator acc, ArchitecturalElement wall, IReadOnlyList<ArchitecturalElement> hosted, float w, float h, float d, float zc)
    {
        var openings = new List<(float X0, float X1, float Y0, float Y1)>();
        double bulge0 = CurvedDesign.ArcBulge(wall);
        ArcGeometry? arc0 = bulge0 != 0 ? new ArcGeometry(w, bulge0) : null;
        foreach (var o in hosted)
        {
            if (!ElementTypes.IsHostedType(o.Category)) continue;
            var (lx, _) = ArchitecturalElement.RotateY(o.X - wall.X, o.Z - wall.Z, -wall.RotationY);
            float x0 = Math.Max(-w / 2, (float)(lx - o.Width / 2));
            float x1 = Math.Min(w / 2, (float)(lx + o.Width / 2));
            if (arc0 is { } ag)
            {
                // Дуга: ширина проёма откладывается по длине дуги от угла центра (радиальные откосы), а не по проекции на хорду.
                double phi = ag.AngleAt(lx), half = o.Width / 2 / ag.Radius;
                x0 = Math.Max(-w / 2, (float)(ag.Radius * Math.Sin(phi - half)));
                x1 = Math.Min(w / 2, (float)(ag.Radius * Math.Sin(phi + half)));
            }
            float y0 = Math.Max(-h / 2, (float)(o.BaseY - wall.Y));
            float y1 = Math.Min(h / 2, (float)(o.TopY - wall.Y));
            if (x1 - x0 > 0.05f && y1 - y0 > 0.05f) openings.Add((x0, x1, y0, y1));
        }
        openings.Sort((a, b) => a.X0.CompareTo(b.X0));

        double bulge = CurvedDesign.ArcBulge(wall);
        ArcGeometry? arc = bulge != 0 ? new ArcGeometry(w, bulge) : null;
        var (ms, me) = CurvedDesign.Miter(wall);
        float ts = (float)Math.Tan(ms * Math.PI / 180), te = (float)Math.Tan(me * Math.PI / 180);
        float z0 = zc - d / 2, z1 = zc + d / 2;

        void Piece(float xa, float xb, float ya, float yb)
        {
            if (xb - xa < 0.001f || yb - ya < 0.001f) return;
            if (arc is { } a) AddArcWallPiece(acc, a, xa, xb, ya, yb, z0, z1);
            else AddSkewPiece(acc, xa, xb, ya, yb, z0, z1, xa <= -w / 2 + 1e-4f ? ts : 0, xb >= w / 2 - 1e-4f ? te : 0);
        }

        float cursor = -w / 2;
        foreach (var (ox0, x1, y0, y1) in openings)
        {
            float x0 = Math.Max(ox0, cursor);
            if (x1 <= x0) continue;
            Piece(cursor, x0, -h / 2, h / 2);
            Piece(x0, x1, -h / 2, y0);
            Piece(x0, x1, y1, h / 2);
            cursor = x1;
        }
        Piece(cursor, w / 2, -h / 2, h / 2);
    }

    /// <summary>Прямой участок стены с косыми торцами: призма по четырёхугольнику в плане (x = x0 + z·tg α).</summary>
    private static void AddSkewPiece(MeshAccumulator acc, float x0, float x1, float y0, float y1, float z0, float z1, float tanStart, float tanEnd)
    {
        if (tanStart == 0 && tanEnd == 0)
        {
            acc.AddBox(new Vector3((x0 + x1) / 2, (y0 + y1) / 2, (z0 + z1) / 2), x1 - x0, y1 - y0, z1 - z0);
            return;
        }
        // Косой торец не должен перехлестнуть противоположный: при коротком участке рез ограничивается.
        float a0 = x0 + tanStart * z0, a1 = x0 + tanStart * z1, b0 = x1 + tanEnd * z0, b1 = x1 + tanEnd * z1;
        if (b0 - a0 < 0.01f || b1 - a1 < 0.01f) { tanStart = tanEnd = 0; a0 = a1 = x0; b0 = b1 = x1; }
        acc.AddPrism([new Vector2(a0, z0), new Vector2(b0, z0), new Vector2(b1, z1), new Vector2(a1, z1)], y0, y1);
    }

    /// <summary>
    /// Участок дуговой стены между хордовыми координатами x0…x1 и отметками y0…y1: наружная и внутренняя поверхности с гладкими
    /// нормалями (по радиусу), верх, низ и радиальные торцы. z0…z1 — смещения граней по локальной Z в вершине дуги.
    /// </summary>
    private static void AddArcWallPiece(MeshAccumulator acc, ArcGeometry arc, float x0, float x1, float y0, float y1, float z0, float z1)
    {
        double fa = arc.AngleAt(x0), fb = arc.AngleAt(x1);
        double full = arc.HalfAngle;
        int n = Math.Max(1, (int)Math.Round(ArcSegments(full) * (fb - fa) / (2 * full)));
        // Радиальные смещения граней: локальная +Z в вершине совпадает с направлением «от центра» при стреле > 0.
        double ra = arc.Sign * z0, rb = arc.Sign * z1;
        double rin = Math.Min(ra, rb), rout = Math.Max(ra, rb);

        Vector3 P(double phi, double off, float y)
        {
            var (x, z) = arc.Point(phi, off);
            return new Vector3((float)x, y, (float)z);
        }
        Vector3 N(double phi)
        {
            var (nx, nz) = arc.Normal(phi);
            return new Vector3((float)nx, 0, (float)nz);
        }

        for (int i = 0; i < n; i++)
        {
            double p0 = fa + (fb - fa) * i / n, p1 = fa + (fb - fa) * (i + 1) / n;
            var n0 = N(p0); var n1 = N(p1);
            acc.AddQuadSmooth(P(p0, rout, y0), P(p1, rout, y0), P(p1, rout, y1), P(p0, rout, y1), n0, n1, n1, n0);
            acc.AddQuadSmooth(P(p0, rin, y0), P(p0, rin, y1), P(p1, rin, y1), P(p1, rin, y0), -n0, -n0, -n1, -n1);
            acc.AddQuad(P(p0, rin, y1), P(p0, rout, y1), P(p1, rout, y1), P(p1, rin, y1), Vector3.UnitY);
            acc.AddQuad(P(p0, rin, y0), P(p1, rin, y0), P(p1, rout, y0), P(p0, rout, y0), BimMath.NegativeUnitY);
            acc.AddEdge(P(p0, rout, y0), P(p1, rout, y0));
            acc.AddEdge(P(p0, rout, y1), P(p1, rout, y1));
            acc.AddEdge(P(p0, rin, y1), P(p1, rin, y1));
            acc.AddEdge(P(p0, rin, y0), P(p1, rin, y0));
        }
        // Радиальные торцы (у проёма — откосы).
        var ta = Tangent(arc, fa); var tb = Tangent(arc, fb);
        acc.AddQuad(P(fa, rin, y0), P(fa, rout, y0), P(fa, rout, y1), P(fa, rin, y1), -ta);
        acc.AddQuad(P(fb, rin, y0), P(fb, rout, y0), P(fb, rout, y1), P(fb, rin, y1), tb);
        acc.AddEdge(P(fa, rout, y0), P(fa, rout, y1));
        acc.AddEdge(P(fa, rin, y0), P(fa, rin, y1));
        acc.AddEdge(P(fb, rout, y0), P(fb, rout, y1));
        acc.AddEdge(P(fb, rin, y0), P(fb, rin, y1));
    }

    /// <summary>Касательная дуги по возрастанию φ (в плоскости X, Z).</summary>
    private static Vector3 Tangent(ArcGeometry arc, double phi) =>
        new((float)Math.Cos(phi), 0, (float)(-arc.Sign * Math.Sin(phi)));

    // ───────────────────────────── Витраж: дуга и арочный верх ─────────────────────────────

    /// <summary>
    /// Витраж по дуге в плане и/или с арочной верхней кромкой (панорамный торец свода): стекло — гладкая лента по станциям, импосты
    /// через ~1,6 м длины дуги до верхней кромки, ригели через ~3,6 м по высоте (только там, где витраж выше ригеля), обвязка по низу
    /// и по арочному верху.
    /// </summary>
    private static void BuildShapedCurtainWall(PartSet set, ArchitecturalElement e, float w, float h, float d)
    {
        var glass = set[MaterialKind.Glass];
        var frame = set[MaterialKind.Frame];
        double bulge = CurvedDesign.ArcBulge(e), rise = CurvedDesign.ArchRise(e);
        ArcGeometry? arc = bulge != 0 ? new ArcGeometry(w, bulge) : null;
        ArcGeometry? top = rise > 0.01 ? new ArcGeometry(w, rise) : null;
        float y0 = -h / 2;
        int stations = Math.Max(arc is not null || top is not null ? 36 : 8, (int)Math.Ceiling(w / 0.8f));

        (Vector3 P, Vector3 N, float Top) Station(int i)
        {
            float x = -w / 2 + w * i / stations;
            float yTop = top is { } t ? (float)(h / 2 - rise + t.ZAt(x)) : h / 2;
            if (arc is { } a)
            {
                double phi = a.AngleAt(x);
                var (px, pz) = a.Point(phi);
                var (nx, nz) = a.Normal(phi);
                return (new Vector3((float)px, 0, (float)pz), new Vector3((float)nx, 0, (float)nz), yTop);
            }
            return (new Vector3(x, 0, 0), Vector3.UnitZ, yTop);
        }

        var st = Enumerable.Range(0, stations + 1).Select(Station).ToList();
        for (int i = 0; i < stations; i++)
        {
            var a = st[i];
            var b = st[i + 1];
            glass.AddQuadSmooth(a.P with { Y = y0 }, b.P with { Y = y0 }, b.P with { Y = b.Top }, a.P with { Y = a.Top }, a.N, b.N, b.N, a.N);
            frame.AddCylinder(a.P with { Y = a.Top }, b.P with { Y = b.Top }, 0.06f, 4, caps: false);
            frame.AddCylinder(a.P with { Y = y0 }, b.P with { Y = y0 }, 0.06f, 4, caps: false);
            frame.AddEdge(a.P with { Y = a.Top }, b.P with { Y = b.Top });
            frame.AddEdge(a.P with { Y = y0 }, b.P with { Y = y0 });
        }

        // Импосты через ~1,6 м длины по плану.
        double length = arc?.Length ?? w;
        int mullions = Math.Max(1, (int)Math.Ceiling(length / 1.6));
        for (int k = 0; k <= mullions; k++)
        {
            int i = (int)Math.Round((double)stations * k / mullions);
            var s = st[Math.Clamp(i, 0, stations)];
            frame.AddCylinder(s.P with { Y = y0 }, s.P with { Y = s.Top }, 0.05f, 4, caps: false);
        }
        // Ригели по высоте.
        int rows = Math.Max(1, (int)Math.Ceiling(h / 3.6f));
        for (int j = 1; j < rows; j++)
        {
            float y = y0 + h * j / rows;
            for (int i = 0; i < stations; i++)
            {
                var a = st[i];
                var b = st[i + 1];
                if (Math.Min(a.Top, b.Top) <= y + 0.05f) continue;
                frame.AddCylinder(a.P with { Y = y }, b.P with { Y = y }, 0.04f, 4, caps: false);
            }
        }
    }

    // ───────────────────────────── Арочная балка ─────────────────────────────

    /// <summary>
    /// Арка (балка со стрелой вверх): ось — дуга в вертикальной плоскости XY через опоры ±W/2 на отметке центра, сечение — высота H
    /// по радиусу × ширина D вдоль Z; гладкие нормали по экстрадосу и интрадосу.
    /// </summary>
    private static void BuildArchBeam(MeshAccumulator acc, ArchitecturalElement e, float w, float h, float d)
    {
        var arc = new ArcGeometry(w, CurvedDesign.ArcBulge(e));
        double full = arc.HalfAngle;
        int n = ArcSegments(full);
        Vector3 P(double phi, double off, float z)
        {
            var (x, y) = arc.Point(phi, off);
            return new Vector3((float)x, (float)y, z);
        }
        Vector3 N(double phi)
        {
            var (nx, ny) = arc.Normal(phi);
            return new Vector3((float)nx, (float)ny, 0);
        }
        float z0 = -d / 2, z1 = d / 2;
        double lo = -h / 2.0, hi = h / 2.0;
        for (int i = 0; i < n; i++)
        {
            double p0 = -full + 2 * full * i / n, p1 = -full + 2 * full * (i + 1) / n;
            var n0 = N(p0); var n1 = N(p1);
            acc.AddQuadSmooth(P(p0, hi, z0), P(p1, hi, z0), P(p1, hi, z1), P(p0, hi, z1), n0, n1, n1, n0);
            acc.AddQuadSmooth(P(p0, lo, z0), P(p0, lo, z1), P(p1, lo, z1), P(p1, lo, z0), -n0, -n0, -n1, -n1);
            acc.AddQuad(P(p0, lo, z1), P(p0, hi, z1), P(p1, hi, z1), P(p1, lo, z1), Vector3.UnitZ);
            acc.AddQuad(P(p0, lo, z0), P(p1, lo, z0), P(p1, hi, z0), P(p0, hi, z0), BimMath.NegativeUnitZ);
            acc.AddEdge(P(p0, hi, z0), P(p1, hi, z0));
            acc.AddEdge(P(p0, hi, z1), P(p1, hi, z1));
            acc.AddEdge(P(p0, lo, z0), P(p1, lo, z0));
            acc.AddEdge(P(p0, lo, z1), P(p1, lo, z1));
        }
        foreach (double phi in new[] { -full, full })
        {
            var t = new Vector3((float)Math.Cos(phi), (float)(-arc.Sign * Math.Sin(phi)), 0) * (phi < 0 ? -1 : 1);
            acc.AddQuad(P(phi, lo, z0), P(phi, hi, z0), P(phi, hi, z1), P(phi, lo, z1), t);
        }
    }

    // ───────────────────────────── Купол, конус, складки ─────────────────────────────

    /// <summary>
    /// Купольная (полуэллипсоид W × D высотой H с плоским основанием), коническая (шатёр над контуром или эллипсом W × D) и складчатая
    /// (пилообразные складки поперёк длинной оси, шаг ~3 м) кровли.
    /// </summary>
    private static void BuildSpecialRoof(MeshAccumulator acc, string shape, List<Vector2>? poly, List<IReadOnlyList<Vector2>> holes, float w, float h, float d)
    {
        float y0 = -h / 2;
        switch (shape)
        {
            case RoofShapes.Dome:
            {
                AddCappedDome(acc, y0, w / 2, h, d / 2);
                var rim = Ellipse(w / 2, d / 2, 48);
                for (int i = 1; i + 1 < rim.Count; i++)
                    acc.AddTriangle(new Vector3(rim[0].X, y0, rim[0].Y), new Vector3(rim[i + 1].X, y0, rim[i + 1].Y), new Vector3(rim[i].X, y0, rim[i].Y), BimMath.NegativeUnitY);
                break;
            }
            case RoofShapes.Cone:
            {
                var rim = MeshAccumulator.EnsureCounterClockwise(poly ?? Ellipse(w / 2, d / 2, 48));
                var apex = new Vector3(0, h / 2, 0);
                for (int i = 0; i < rim.Count; i++)
                {
                    var a = rim[i];
                    var b = rim[(i + 1) % rim.Count];
                    var mid = (a + b) / 2;
                    acc.AddTriangle(new Vector3(a.X, y0, a.Y), new Vector3(b.X, y0, b.Y), apex, new Vector3(mid.X, 0.3f, mid.Y));
                }
                foreach (var (a, b, c) in MeshAccumulator.Triangulate(rim))
                    acc.AddTriangle(new Vector3(a.X, y0, a.Y), new Vector3(c.X, y0, c.Y), new Vector3(b.X, y0, b.Y), BimMath.NegativeUnitY);
                acc.AddEdgeLoop(rim.Select(p => new Vector3(p.X, y0, p.Y)).ToList());
                break;
            }
            default:
            {
                var outline = poly ?? Rect(w, d);
                float minX = outline.Min(p => p.X), maxX = outline.Max(p => p.X);
                float span = Math.Max(0.5f, maxX - minX);
                int folds = Math.Max(2, (int)Math.Round(span / 3f));
                float f = span / folds, eave = Math.Min(0.2f, h * 0.15f);
                float Top(Vector2 p)
                {
                    float u = (p.X - minX) / f;
                    float frac = u - MathF.Floor(u);
                    return y0 + eave + (h - eave) * (1 - Math.Abs(2 * frac - 1));
                }
                var breaks = Enumerable.Range(1, 2 * folds - 1).Select(k => minX + f * k / 2f).ToList();
                acc.AddRegionSolid(outline, holes, y0, Top, breaks, true);
                break;
            }
        }
    }

    /// <summary>
    /// Контур в плане (мировые X, Z) дуговой стены/витража — полоса толщиной D по дуге (наружная дуга, затем внутренняя в обратном
    /// порядке), стены с косыми торцами — четырёхугольник реза; null — обычная прямая стена.
    /// </summary>
    public static List<(double X, double Z)>? ShapedOutline(ArchitecturalElement e)
    {
        double w = Math.Max(0.01, e.Width), d = Math.Max(0.01, e.Depth);
        var local = new List<(double X, double Z)>();
        if (CurvedDesign.IsArc(e))
        {
            var arc = new ArcGeometry(w, CurvedDesign.ArcBulge(e));
            double full = arc.HalfAngle;
            int n = ArcSegments(full);
            for (int i = 0; i <= n; i++) local.Add(arc.Point(-full + 2 * full * i / n, d / 2));
            for (int i = n; i >= 0; i--) local.Add(arc.Point(-full + 2 * full * i / n, -d / 2));
        }
        else if (CurvedDesign.HasMiter(e))
        {
            var (ms, me) = CurvedDesign.Miter(e);
            double ts = Math.Tan(ms * Math.PI / 180), te = Math.Tan(me * Math.PI / 180);
            local.Add((-w / 2 - ts * d / 2, -d / 2));
            local.Add((w / 2 - te * d / 2, -d / 2));
            local.Add((w / 2 + te * d / 2, d / 2));
            local.Add((-w / 2 + ts * d / 2, d / 2));
        }
        else return null;
        return local.Select(p =>
        {
            var (rx, rz) = ArchitecturalElement.RotateY(p.X, p.Z, e.RotationY);
            return (e.X + rx, e.Z + rz);
        }).ToList();
    }

    /// <summary>Ось дуговой стены/витража в плане (мировые X, Z), 13…49 точек от начала к концу.</summary>
    public static List<(double X, double Z)> ArcAxis(ArchitecturalElement e)
    {
        var arc = new ArcGeometry(Math.Max(0.01, e.Width), CurvedDesign.ArcBulge(e));
        double full = arc.HalfAngle;
        int n = Math.Max(12, ArcSegments(full) / 2);
        var pts = new List<(double X, double Z)>(n + 1);
        for (int i = 0; i <= n; i++)
        {
            var (lx, lz) = arc.Point(-full + 2 * full * i / n);
            var (rx, rz) = ArchitecturalElement.RotateY(lx, lz, e.RotationY);
            pts.Add((e.X + rx, e.Z + rz));
        }
        return pts;
    }

    /// <summary>
    /// Купол кровли (полуэллипсоид) без вырожденных треугольников у вершины: кольца — четырёхугольниками с гладкими нормалями,
    /// последнее кольцо замыкается веером треугольников на вершину (у <see cref="MeshAccumulator.AddDome"/> полюс — нулевые грани).
    /// </summary>
    private static void AddCappedDome(MeshAccumulator acc, float baseY, float rx, float h, float rz, int slices = 48, int stacks = 16)
    {
        Vector3 P(int s, int i, out Vector3 n)
        {
            double phi = Math.PI / 2 * s / stacks, theta = 2 * Math.PI * i / slices;
            float cy = (float)Math.Sin(phi), cr = (float)Math.Cos(phi), cx = (float)Math.Cos(theta), cz = (float)Math.Sin(theta);
            n = BimMath.Normalize(new Vector3(cr * cx / Math.Max(rx, 1e-3f), cy / Math.Max(h, 1e-3f), cr * cz / Math.Max(rz, 1e-3f)));
            return new Vector3(rx * cr * cx, baseY + h * cy, rz * cr * cz);
        }
        for (int s = 0; s < stacks - 1; s++)
            for (int i = 0; i < slices; i++)
            {
                var a = P(s, i, out var na); var b = P(s, i + 1, out var nb); var c = P(s + 1, i + 1, out var nc); var d = P(s + 1, i, out var nd);
                acc.AddQuadSmooth(a, b, c, d, na, nb, nc, nd);
            }
        var apex = new Vector3(0, baseY + h, 0);
        for (int i = 0; i < slices; i++)
            acc.AddTriangle(P(stacks - 1, i, out _), P(stacks - 1, i + 1, out _), apex, Vector3.UnitY);
        acc.AddEdgeLoop(Enumerable.Range(0, slices).Select(i => P(0, i, out _)).ToList());
    }

    private static List<Vector2> Ellipse(float rx, float rz, int n) =>
        Enumerable.Range(0, n).Select(i => 2 * Math.PI * i / n).Select(t => new Vector2(rx * (float)Math.Cos(t), rz * (float)Math.Sin(t))).ToList();
}
