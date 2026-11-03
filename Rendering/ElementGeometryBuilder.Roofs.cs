using System.Numerics;
using AIArchitectStudio.Models;
using AIArchitectStudio.Models.Geometry;

namespace AIArchitectStudio.Rendering;

/// <summary>
/// Геометрия полой кровли: профиль ската (низ — по грани стены на отметке карниза, верх — смещение на толщину пирога по нормали
/// ската) выдавливается вдоль конька; вальма — четыре наклонные плиты; плоская эксплуатируемая — плита покрытия с ж/б парапетом.
/// Под скатами остаётся чердак: фронтоны (с фронтонным окном по параметру), подшивка свесов, у односкатной — стенка высокой
/// стороны. Высота H — от карниза (низ элемента) до верха конька.
/// </summary>
public static partial class ElementGeometryBuilder
{
    /// <summary>Сведения о полой кровле: уклон, полупролёт, свес, толщина, площадь скатов и объём оболочки (м³).</summary>
    public sealed record HollowRoofInfo(string Shape, double Slope, double HalfSpan, double Overhang, double Thickness, double SlopeArea, double ShellVolume,
        double SolidPrismVolume);

    /// <summary>Поворот рамки на 90° (конёк — вдоль длинной стороны плана).</summary>
    private static Matrix4x4 RoofFrame(float cx, float cz, bool rotated) =>
        BimMath.Multiply(BimMath.RotationY(rotated ? MathF.PI / 2 : 0), BimMath.Translation(cx, 0, cz));

    /// <summary>Перестановка осей (x, y, z) ← (Y, Z, X) призмы: профиль в плоскости (z, y) выдавливается вдоль локальной X (det = +1).</summary>
    private static readonly Matrix4x4 ProfileToX = new(0, 0, 1, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 1);

    private readonly record struct RoofFrameData(float Cx, float Cz, float Length, float Span, bool Rotated);

    /// <summary>Рамка кровли: центр и габарит плана (контур Polygon2D — по габариту), длинная сторона — вдоль локальной X рамки.</summary>
    private static RoofFrameData FrameOf(List<Vector2>? poly, float w, float d, bool alignLong)
    {
        float cx = 0, cz = 0, lx = w, lz = d;
        if (poly is not null)
        {
            float minX = poly.Min(p => p.X), maxX = poly.Max(p => p.X), minZ = poly.Min(p => p.Y), maxZ = poly.Max(p => p.Y);
            cx = (minX + maxX) / 2;
            cz = (minZ + maxZ) / 2;
            lx = Math.Max(0.1f, maxX - minX);
            lz = Math.Max(0.1f, maxZ - minZ);
        }
        bool rotated = alignLong && lz > lx;
        return rotated ? new RoofFrameData(cx, cz, lz, lx, true) : new RoofFrameData(cx, cz, lx, lz, false);
    }

    /// <summary>Уклон k = tg α низа ската из высоты H над карнизом: k·run + t·√(1 + k²) = H (простая итерация, сходится при t &lt; run).</summary>
    private static float SolveSlope(float h, float run, float t)
    {
        run = Math.Max(0.1f, run);
        float k = h / run;
        for (int i = 0; i < 40; i++) k = Math.Max(0.02f, (h - t * MathF.Sqrt(1 + k * k)) / run);
        return k;
    }

    /// <summary>
    /// Низ ската (профиль поперёк конька в плоскости (z, y), по возрастанию z) для выдавленных форм; null — вальмовая.
    /// gableTop — верх фронтона (точки профиля на гранях стен ±s).
    /// </summary>
    private static List<Vector2>? HollowUnderside(string shape, float span, float h, float t, float o, float y0, out float k, out float apexRise)
    {
        float s = span / 2;
        k = 0;
        apexRise = 0;
        switch (shape)
        {
            case RoofShapes.GableHollow:
                k = SolveSlope(h, s, t);
                apexRise = k * s;
                return [new(-s - o, y0 - k * o), new(-s, y0), new(0, y0 + k * s), new(s, y0), new(s + o, y0 - k * o)];
            case RoofShapes.ShedHollow:
                k = SolveSlope(h, span, t);
                apexRise = k * span;
                return [new(-s - o, y0 - k * o), new(-s, y0), new(s, y0 + k * span), new(s + o, y0 + k * (span + o))];
            case RoofShapes.Mansard:
            {
                // Ломаный скат: нижний крутой (70°) до излома, верхний пологий до конька.
                const float steep = 2.7474774f; // tg 70°
                float a = Math.Clamp(0.22f * s, 0.4f, Math.Max(0.4f, 0.45f * s));
                float h1 = a * steep;
                float k2 = SolveSlope(Math.Max(0.3f, h - h1), s - a, t);
                if (h - h1 < 0.5f)
                {
                    h1 = Math.Max(0.3f, 0.6f * h);
                    a = h1 / steep;
                    k2 = SolveSlope(Math.Max(0.2f, h - h1), Math.Max(0.2f, s - a), t);
                }
                float h2 = k2 * (s - a);
                k = k2;
                apexRise = h1 + h2;
                return [new(-s - o, y0 - steep * o), new(-s, y0), new(-s + a, y0 + h1), new(0, y0 + h1 + h2), new(s - a, y0 + h1), new(s, y0), new(s + o, y0 - steep * o)];
            }
            case RoofShapes.BarrelVault:
            {
                // Сегментный свод: дуга через грани стен на отметке карниза и замок на высоте подъёма.
                float rise = Math.Clamp(h - t, 0.2f, s);
                float r = (s * s + rise * rise) / (2 * rise);
                float cy = y0 + rise - r;
                float half = MathF.Asin(Math.Clamp(s / r, 0, 1));
                const int segments = 28;
                var arc = new List<Vector2>(segments + 1);
                for (int i = 0; i <= segments; i++)
                {
                    float a = -half + 2 * half * i / segments;
                    arc.Add(new Vector2(r * MathF.Sin(a), cy + r * MathF.Cos(a)));
                }
                arc[0] = new Vector2(-s, y0);
                arc[^1] = new Vector2(s, y0);
                k = rise / s;
                apexRise = rise;
                return arc;
            }
            default:
                return null;
        }
    }

    /// <summary>Смещение ломаной (z по возрастанию) вверх по нормали каждого участка; концы — вертикальный срез по крайним z.</summary>
    private static List<Vector2> OffsetPolyline(IReadOnlyList<Vector2> pts, float dist)
    {
        var result = new List<Vector2>(pts.Count);
        if (dist == 0)
        {
            result.AddRange(pts);
            return result;
        }
        int n = pts.Count;
        var dirs = new Vector2[n - 1];
        var normals = new Vector2[n - 1];
        for (int i = 0; i + 1 < n; i++)
        {
            var u = BimMath.Normalize(pts[i + 1] - pts[i]);
            dirs[i] = u;
            normals[i] = new Vector2(-u.Y, u.X);
        }
        static float YAt(Vector2 p, Vector2 u, float z) => p.Y + (z - p.X) * u.Y / u.X;
        var p0 = pts[0] + normals[0] * dist;
        result.Add(new Vector2(pts[0].X, YAt(p0, dirs[0], pts[0].X)));
        for (int i = 1; i + 1 < n; i++)
        {
            var a = pts[i] + normals[i - 1] * dist;
            var b = pts[i] + normals[i] * dist;
            float cross = dirs[i - 1].X * dirs[i].Y - dirs[i - 1].Y * dirs[i].X;
            if (Math.Abs(cross) < 1e-5f)
            {
                result.Add(b);
                continue;
            }
            var diff = b - a;
            float tt = (diff.X * dirs[i].Y - diff.Y * dirs[i].X) / cross;
            result.Add(a + dirs[i - 1] * tt);
        }
        var pl = pts[n - 1] + normals[n - 2] * dist;
        result.Add(new Vector2(pts[n - 1].X, YAt(pl, dirs[n - 2], pts[n - 1].X)));
        return result;
    }

    /// <summary>Полоса профиля между смещениями низа d0 … d1 (замкнутый контур в (z, y)).</summary>
    private static List<Vector2> ProfileBand(IReadOnlyList<Vector2> underside, float d0, float d1)
    {
        var top = OffsetPolyline(underside, d1);
        var bottom = OffsetPolyline(underside, d0);
        bottom.Reverse();
        top.AddRange(bottom);
        return top;
    }

    /// <summary>Выдавливание профиля (z, y) вдоль локальной X от x0 до x1 (с отверстиями — фронтонное окно).</summary>
    private static void ExtrudeAlongX(MeshAccumulator acc, IReadOnlyList<Vector2> profile, float x0, float x1, IReadOnlyList<IReadOnlyList<Vector2>>? holes = null)
    {
        var saved = acc.Transform;
        acc.Transform = BimMath.Multiply(ProfileToX, saved);
        // Трапецеидальная заливка (а не отсечение ушей): длинная тонкая дуговая полоса свода и пилообразный профиль марша
        // заливаются без перекрытия внутренней области.
        acc.AddRegionSolid(profile, holes ?? [], x0, _ => x1, [], false);
        acc.Transform = saved;
    }

    /// <summary>Материалы оболочки: ж/б — одна полоса бетона; иначе кровельное покрытие сверху и стропильный слой с утеплителем.</summary>
    private static (MaterialKind Structure, MaterialKind Cover, float CoverThickness) RoofBands(ArchitecturalElement e, MaterialKind mat, float t)
    {
        bool concrete = (e.Material ?? string.Empty).Contains("concrete", StringComparison.OrdinalIgnoreCase);
        return concrete ? (MaterialKind.Concrete, MaterialKind.Concrete, 0) : (MaterialKind.Insulation, mat, Math.Min(0.03f, t * 0.2f));
    }

    private static void BuildHollowRoof(PartSet set, ArchitecturalElement e, List<Vector2>? poly, List<IReadOnlyList<Vector2>> holes, float w, float h, float d, MaterialKind mat)
    {
        string shape = RoofShapes.Normalize(e.RoofShape);
        if (shape == RoofShapes.FlatParapet)
        {
            BuildParapetRoof(set, e, poly, holes, w, h, d, mat);
            return;
        }

        float t = (float)RoofDesign.SlabThickness(e), o = (float)RoofDesign.Overhang(e);
        if (shape == RoofShapes.BarrelVault) o = 0;
        var frame = FrameOf(poly, w, d, alignLong: shape != RoofShapes.ShedHollow);
        float y0 = -h / 2, s = frame.Span / 2, lx = frame.Length / 2;
        var savedSet = set.Transform;
        set.Transform = BimMath.Multiply(RoofFrame(frame.Cx, frame.Cz, frame.Rotated), savedSet);
        var (structure, cover, tc) = RoofBands(e, mat, t);

        if (shape == RoofShapes.HipHollow)
        {
            BuildHipShell(set, frame, h, t, o, y0, structure, cover, tc);
            set.Transform = savedSet;
            return;
        }

        var underside = HollowUnderside(shape, frame.Span, h, t, o, y0, out _, out float apexRise)!;
        float xo = shape == RoofShapes.BarrelVault ? 0 : o;
        if (tc > 0)
        {
            ExtrudeAlongX(set[structure], ProfileBand(underside, 0, t - tc), -lx - xo, lx + xo);
            ExtrudeAlongX(set[cover], ProfileBand(underside, t - tc, t), -lx - xo, lx + xo);
        }
        else ExtrudeAlongX(set[structure], ProfileBand(underside, 0, t), -lx - xo, lx + xo);

        // Подшивка свесов: горизонтальная доска от грани стены до кромки карниза по нижней точке.
        if (o > 0.05f)
        {
            var finish = set[MaterialKind.Finish];
            float low = Math.Min(underside[0].Y, underside[1].Y), high = Math.Min(underside[^1].Y, underside[^2].Y);
            finish.AddBox(new Vector3(0, low - 0.0125f, -s - o / 2), 2 * lx + 2 * xo, 0.025f, o);
            finish.AddBox(new Vector3(0, high - 0.0125f, s + o / 2), 2 * lx + 2 * xo, 0.025f, o);
        }

        // Фронтоны (торцевые стены чердака) по низу ската между гранями стен; у односкатной — ещё стенка высокой стороны.
        var gable = underside.Where(p => p.X >= -s - 1e-4f && p.X <= s + 1e-4f).ToList();
        if (shape == RoofShapes.ShedHollow) gable.Add(new Vector2(s, y0));
        float tw = Math.Min((float)RoofDesign.GableWallThickness, frame.Length * 0.2f);
        var wallKind = MaterialKind.Plaster;
        (float X, float Y, float Z, float W) rect = default;
        IReadOnlyList<IReadOnlyList<Vector2>>? window = shape is RoofShapes.GableHollow or RoofShapes.Mansard or RoofShapes.BarrelVault && RoofDesign.HasGableWindow(e)
            && GableWindowHole(gable, s, apexRise, y0, out rect) is { } hole ? [hole] : null;
        for (int side = -1; side <= 1; side += 2)
        {
            float x0 = side < 0 ? -lx : lx - tw, x1 = side < 0 ? -lx + tw : lx;
            ExtrudeAlongX(set[wallKind], gable, x0, x1, window);
        }
        if (window is not null)
        {
            // Рама и стекло фронтонного окна в теле фронтона (окно повёрнуто: ширина — поперёк конька).
            var framed = set.Transform;
            for (int side = -1; side <= 1; side += 2)
            {
                set.Transform = BimMath.Multiply(BimMath.Multiply(BimMath.RotationY(MathF.PI / 2), BimMath.Translation(side * (lx - tw / 2), rect.Y + rect.W / 2, rect.X)), framed);
                BuildWindow(set, rect.Z, rect.W, tw + 0.02f);
            }
            set.Transform = framed;
        }
        if (shape == RoofShapes.ShedHollow && apexRise > 0.05f)
            set[wallKind].AddBox(new Vector3(0, y0 + apexRise / 2, s - tw / 2), 2 * lx, apexRise, tw);
        set.Transform = savedSet;
    }

    /// <summary>Проём фронтонного окна в профиле фронтона (z, y); rect — (центр z, низ y, ширина, высота).</summary>
    private static IReadOnlyList<Vector2>? GableWindowHole(IReadOnlyList<Vector2> gable, float s, float apexRise, float y0, out (float X, float Y, float Z, float W) rect)
    {
        rect = default;
        float ww = Math.Clamp(0.25f * 2 * s, 0.6f, 1.4f);
        float wh = Math.Clamp(0.35f * apexRise, 0.5f, 1.2f);
        float ys = y0 + 0.3f * apexRise;
        // Верх окна — с запасом 150 мм под низом ската у краёв окна.
        float limit = Math.Min(UndersideAt(gable, -ww / 2), UndersideAt(gable, ww / 2)) - 0.15f;
        if (ys + wh > limit) wh = limit - ys;
        if (wh < 0.3f) return null;
        rect = (0, ys, ww, wh);
        return [new(-ww / 2, ys), new(ww / 2, ys), new(ww / 2, ys + wh), new(-ww / 2, ys + wh)];
    }

    private static float UndersideAt(IReadOnlyList<Vector2> pts, float z)
    {
        for (int i = 0; i + 1 < pts.Count; i++)
        {
            var a = pts[i];
            var b = pts[i + 1];
            if (z < Math.Min(a.X, b.X) || z > Math.Max(a.X, b.X) || Math.Abs(b.X - a.X) < 1e-6f) continue;
            return a.Y + (b.Y - a.Y) * (z - a.X) / (b.X - a.X);
        }
        return pts.Max(p => p.Y);
    }

    /// <summary>Вальмовая полая кровля: четыре наклонные плиты одного уклона (вертикальная толщина v = t·√(1 + k²)) и подшивка по периметру.</summary>
    private static void BuildHipShell(PartSet set, RoofFrameData frame, float h, float t, float o, float y0, MaterialKind structure, MaterialKind cover, float tc)
    {
        float s = frame.Span / 2, lx = frame.Length / 2;
        float k = SolveSlope(h, s, t);
        float root = MathF.Sqrt(1 + k * k), v = t * root, vc = tc * root;
        float rx = lx - s, X = lx + o, Z = s + o;
        // Низ ската: y = y0 + k·(s − расстояние до ближайшей стороны по уклону).
        float South(Vector2 p) => y0 + k * (s + p.Y);
        float North(Vector2 p) => y0 + k * (s - p.Y);
        float West(Vector2 p) => y0 + k * (s + p.X + rx);
        float East(Vector2 p) => y0 + k * (s - p.X + rx);
        var faces = new (List<Vector2> Plan, Func<Vector2, float> Bottom)[]
        {
            ([new(-X, -Z), new(X, -Z), new(rx, 0), new(-rx, 0)], South),
            ([new(X, Z), new(-X, Z), new(-rx, 0), new(rx, 0)], North),
            ([new(-X, Z), new(-X, -Z), new(-rx, 0)], West),
            ([new(X, -Z), new(X, Z), new(rx, 0)], East)
        };
        foreach (var (plan, bottom) in faces)
        {
            if (Math.Abs(SignedArea2(plan)) < 1e-5f) continue;
            if (vc > 0)
            {
                AddSlopedSlab(set[structure], plan, p => bottom(p), v - vc);
                AddSlopedSlab(set[cover], plan, p => bottom(p) + v - vc, vc);
            }
            else AddSlopedSlab(set[structure], plan, p => bottom(p), v);
        }
        if (o > 0.05f)
        {
            var finish = set[MaterialKind.Finish];
            float low = y0 - k * o - 0.0125f;
            finish.AddBox(new Vector3(0, low, -s - o / 2), 2 * X, 0.025f, o);
            finish.AddBox(new Vector3(0, low, s + o / 2), 2 * X, 0.025f, o);
            finish.AddBox(new Vector3(-lx - o / 2, low, 0), o, 0.025f, 2 * s);
            finish.AddBox(new Vector3(lx + o / 2, low, 0), o, 0.025f, 2 * s);
        }
    }

    private static float SignedArea2(IReadOnlyList<Vector2> p)
    {
        float a = 0;
        for (int i = 0; i < p.Count; i++)
        {
            var q = p[i];
            var r = p[(i + 1) % p.Count];
            a += q.X * r.Y - r.X * q.Y;
        }
        return a / 2;
    }

    /// <summary>Наклонная плита над плоским (выпуклым) участком плана: низ — линейная функция, верх — низ + v; торцы — вертикальные.</summary>
    private static void AddSlopedSlab(MeshAccumulator acc, IReadOnlyList<Vector2> plan, Func<Vector2, float> bottom, float v)
    {
        if (v <= 1e-5f) return;
        var poly = MeshAccumulator.EnsureCounterClockwise(plan);
        foreach (var (a, b, c) in MeshAccumulator.Triangulate(poly))
        {
            acc.AddTriangle(new Vector3(a.X, bottom(a) + v, a.Y), new Vector3(b.X, bottom(b) + v, b.Y), new Vector3(c.X, bottom(c) + v, c.Y), Vector3.UnitY);
            acc.AddTriangle(new Vector3(a.X, bottom(a), a.Y), new Vector3(c.X, bottom(c), c.Y), new Vector3(b.X, bottom(b), b.Y), BimMath.NegativeUnitY);
        }
        for (int i = 0; i < poly.Count; i++)
        {
            var p = poly[i];
            var q = poly[(i + 1) % poly.Count];
            var dir = q - p;
            var outward = new Vector3(dir.Y, 0, -dir.X);
            float bp = bottom(p), bq = bottom(q);
            acc.AddQuad(new Vector3(p.X, bp, p.Y), new Vector3(q.X, bq, q.Y), new Vector3(q.X, bq + v, q.Y), new Vector3(p.X, bp + v, p.Y), outward);
            acc.AddEdge(new Vector3(p.X, bp + v, p.Y), new Vector3(q.X, bq + v, q.Y));
            acc.AddEdge(new Vector3(p.X, bp, p.Y), new Vector3(q.X, bq, q.Y));
        }
    }

    /// <summary>Плоская эксплуатируемая кровля: плита покрытия (пирог, отверстия) и контурный ж/б парапет по внутренней грани контура.</summary>
    private static void BuildParapetRoof(PartSet set, ArchitecturalElement e, List<Vector2>? poly, List<IReadOnlyList<Vector2>> holes, float w, float h, float d, MaterialKind mat)
    {
        if (e.Layers.Count > 0)
            foreach (var (layer, y0, y1) in LayerBands(e.Layers, -h / 2, h / 2, fromTop: false))
                AddSlabBand(set[MaterialCatalog.LayerKind(layer, mat)], poly, holes, w, d, y0, y1);
        else AddSlabBand(set[mat], poly, holes, w, d, -h / 2, h / 2);

        float ph = (float)RoofDesign.ParapetHeight(e), pt = (float)RoofDesign.ParapetThickness;
        var outline = MeshAccumulator.EnsureCounterClockwise(poly ?? Rect(w, d));
        var concrete = set[MaterialKind.Concrete];
        for (int i = 0; i < outline.Count; i++)
        {
            var p = outline[i];
            var q = outline[(i + 1) % outline.Count];
            var dir = q - p;
            if (BimMath.LengthSquared(dir) < 1e-6f) continue;
            var u = BimMath.Normalize(dir);
            var inward = new Vector2(-u.Y, u.X) * pt;
            concrete.AddPrism([p, q, q + inward, p + inward], h / 2, h / 2 + ph);
        }
    }

    /// <summary>
    /// Описание полой кровли для смет и проверок: уклон, свес, толщина, площадь скатов и объём оболочки (по построенной
    /// геометрии скатов; фронтоны, подшивка и парапет не входят) против объёма сплошной призмы прежней формы.
    /// </summary>
    public static HollowRoofInfo? DescribeHollowRoof(ArchitecturalElement e)
    {
        string shape = RoofShapes.Normalize(e.RoofShape);
        if (e.Category != BimCategory.Roof || !RoofShapes.IsHollow(shape)) return null;
        var geometry = Build(e, [], new GeometryContext(-1, 0, 0));
        double shell = 0;
        var mat = MaterialCatalog.ForElement(e);
        var (structure, cover, _) = RoofBands(e, mat, (float)RoofDesign.SlabThickness(e));
        foreach (var part in geometry.Parts)
            if (part.Material == structure || part.Material == cover) shell += MeshVolume(part.Mesh);
        double t = RoofDesign.SlabThickness(e), o = shape == RoofShapes.BarrelVault ? 0 : RoofDesign.Overhang(e);
        var poly = PolygonOf(e);
        var frame = FrameOf(poly, (float)e.Width, (float)e.Depth, shape != RoofShapes.ShedHollow);
        double s = frame.Span / 2, length = frame.Length + 2 * o, h = e.Height;
        double k, area;
        switch (shape)
        {
            case RoofShapes.ShedHollow:
                k = SolveSlope((float)h, (float)(2 * s), (float)t);
                area = length * (2 * s + 2 * o) * Math.Sqrt(1 + k * k);
                break;
            case RoofShapes.HipHollow:
                k = SolveSlope((float)h, (float)s, (float)t);
                area = (frame.Length + 2 * o) * (2 * s + 2 * o) * Math.Sqrt(1 + k * k);
                break;
            case RoofShapes.FlatParapet:
                k = 0;
                area = e.Width * e.Depth;
                t = e.Height;
                break;
            default:
            {
                var underside = HollowUnderside(shape, frame.Span, (float)h, (float)t, (float)o, 0, out float kk, out _)!;
                k = kk;
                double len = 0;
                for (int i = 0; i + 1 < underside.Count; i++) len += BimMath.Length(underside[i + 1] - underside[i]);
                area = len * length;
                break;
            }
        }
        double solid = shape switch
        {
            RoofShapes.FlatParapet => e.Width * e.Depth * e.Height,
            RoofShapes.HipHollow => 2 * s * h * (3 * frame.Length - 2 * s) / 6,
            _ => frame.Length * 2 * s * h / 2
        };
        return new HollowRoofInfo(shape, k, s, o, t, area, shell, solid);
    }

    /// <summary>Объём замкнутой сетки по теореме о дивергенции (Σ a·(b × c) / 6).</summary>
    public static double MeshVolume(BimMesh mesh)
    {
        var pos = mesh.Positions;
        var idx = mesh.TriangleIndices;
        double sum = 0;
        for (int i = 0; i + 2 < idx.Count; i += 3)
        {
            var a = pos[idx[i]];
            var b = pos[idx[i + 1]];
            var c = pos[idx[i + 2]];
            sum += (double)a.X * ((double)b.Y * c.Z - (double)b.Z * c.Y) - (double)a.Y * ((double)b.X * c.Z - (double)b.Z * c.X) + (double)a.Z * ((double)b.X * c.Y - (double)b.Y * c.X);
        }
        return Math.Abs(sum) / 6;
    }
}
