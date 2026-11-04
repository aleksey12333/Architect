#if DEBUG
using System.Numerics;
using AIArchitectStudio.Rendering;

namespace AIArchitectStudio.Services.Photo;

/// <summary>
/// Фотореалистичные рендеры пяти демонстрационных сооружений для DemoKit/InternetPhotos (замена снимкам из интернета без сети):
/// перспектива с высоты человека/дрона, небо по высоте луча с облаками, Солнце с мягкими тенями (карта теней, PCF), стекло с
/// отражением неба и бликом Френеля, импосты и межэтажные пояса, ритм окон по развёртке фасада, мощение площади, виньетирование и
/// шум матрицы. Геометрия снимков собирается здесь отдельно от BIM-ядра (свои примитивы с развёрткой u — длина по фасаду, v — высота).
/// Эталонные цвета материалов (<see cref="TruthColors"/>) — для проверки извлечённой палитры.
/// </summary>
public static class PhotoSceneStudio
{
    public const int Width = 1280, Height = 800;

    public static readonly (string File, string Title)[] Photos =
    [
        ("Foto_1_Curved_Glass_Tower.png", "Изогнутая стеклянная башня со стилобатом"),
        ("Foto_2_Cascading_Terraced_Complex.png", "Каскадный жилой комплекс с террасами"),
        ("Foto_3_Classical_Rotunda_Dome.png", "Ротонда с куполом и колоннадой"),
        ("Foto_4_Flatiron_Triangular_Building.png", "Треугольное здание на стрелке улиц"),
        ("Foto_5_Unique_Arch_Span_Arena.png", "Арочный большепролётный павильон")
    ];

    /// <summary>Эталонные цвета основных материалов снимков (стена/стекло/кровля) — sRGB.</summary>
    public static readonly (string Wall, string Glass, string Roof)[] TruthColors =
    [
        ("#CBBFA6", "#5E8FB3", "#8E949B"),
        ("#C46A44", "#2E3A44", "#9C8F80"),
        ("#E6D8B8", "#34404A", "#5E9C88"),
        ("#D6C6A4", "#2F3940", "#4A4440"),
        ("#9EA3A8", "#6A9EC4", "#B7C3CF")
    ];

    private enum Mat { Ground, Glass, Facade, Plain, Metal, Dome, Green, Frame, Column }

    private sealed record Surface(Mat Kind, Vector3 Albedo, float Bay = 3f, float FloorH = 3.3f, float WinW = 0.5f, float WinH = 0.55f, float Sill = 0.3f);

    private sealed class Scene
    {
        public readonly SoftwareRasterizer Raster = new();
        public readonly List<Surface> Surfaces = [];

        public int Surface(Surface s)
        {
            Surfaces.Add(s);
            return Surfaces.Count - 1;
        }

        public void Tri(Vector3 a, Vector3 b, Vector3 c, Vector3 na, Vector3 nb, Vector3 nc, Vector2 ua, Vector2 ub, Vector2 uc, int m) =>
            Raster.AddTriangle(new RasterVertex(a, na, ua), new RasterVertex(b, nb, ub), new RasterVertex(c, nc, uc), m);

        /// <summary>Призма по контуру (против часовой в X, Z): бока с развёрткой по периметру, гладкие нормали при изломе &lt; 25°, крышка.</summary>
        public void Extrude(IReadOnlyList<Vector2> loop, float y0, float y1, int side, int? cap, bool bottom = false)
        {
            int n = loop.Count;
            var edgeN = new Vector3[n];
            for (int i = 0; i < n; i++)
            {
                var d = loop[(i + 1) % n] - loop[i];
                edgeN[i] = Vector3.Normalize(new Vector3(d.Y, 0, -d.X));
            }
            float u = 0;
            for (int i = 0; i < n; i++)
            {
                var p = loop[i];
                var q = loop[(i + 1) % n];
                float len = Vector2.Distance(p, q);
                var nPrev = edgeN[(i - 1 + n) % n];
                var nNext = edgeN[(i + 1) % n];
                var np = Vector3.Dot(nPrev, edgeN[i]) > 0.9f ? Vector3.Normalize(nPrev + edgeN[i]) : edgeN[i];
                var nq = Vector3.Dot(nNext, edgeN[i]) > 0.9f ? Vector3.Normalize(nNext + edgeN[i]) : edgeN[i];
                var a = new Vector3(p.X, y0, p.Y); var b = new Vector3(q.X, y0, q.Y);
                var c = new Vector3(q.X, y1, q.Y); var dd = new Vector3(p.X, y1, p.Y);
                Tri(a, b, c, np, nq, nq, new(u, y0), new(u + len, y0), new(u + len, y1), side);
                Tri(a, c, dd, np, nq, np, new(u, y0), new(u + len, y1), new(u, y1), side);
                u += len;
            }
            if (cap is { } cm)
                foreach (var (a, b, c) in AIArchitectStudio.Rendering.MeshAccumulator.Triangulate(AIArchitectStudio.Rendering.MeshAccumulator.EnsureCounterClockwise(loop)))
                {
                    Tri(new(a.X, y1, a.Y), new(b.X, y1, b.Y), new(c.X, y1, c.Y), Vector3.UnitY, Vector3.UnitY, Vector3.UnitY, a, b, c, cm);
                    if (bottom) Tri(new(a.X, y0, a.Y), new(c.X, y0, c.Y), new(b.X, y0, b.Y), -Vector3.UnitY, -Vector3.UnitY, -Vector3.UnitY, a, c, b, cm);
                }
        }

        public void Box(float cx, float cz, float w, float d, float y0, float y1, float rotDeg, int side, int? cap)
        {
            var pts = new[] { new Vector2(-w / 2, -d / 2), new Vector2(w / 2, -d / 2), new Vector2(w / 2, d / 2), new Vector2(-w / 2, d / 2) };
            float r = rotDeg * MathF.PI / 180, c = MathF.Cos(r), s = MathF.Sin(r);
            Extrude(pts.Select(p => new Vector2(cx + p.X * c + p.Y * s, cz - p.X * s + p.Y * c)).ToList(), y0, y1, side, cap);
        }

        public void Cylinder(float cx, float cz, float r, float y0, float y1, int side, int? cap, int seg = 28) =>
            Extrude(Enumerable.Range(0, seg).Select(i => 2 * MathF.PI * i / seg).Select(a => new Vector2(cx + r * MathF.Cos(a), cz + r * MathF.Sin(a))).ToList(), y0, y1, side, cap);

        public void Dome(float cx, float cz, float y0, float r, float h, int m, int slices = 48, int stacks = 18)
        {
            Vector3 P(int s, int i, out Vector3 n, out Vector2 uv)
            {
                float phi = MathF.PI / 2 * s / stacks, th = 2 * MathF.PI * i / slices;
                float cr = MathF.Cos(phi), sy = MathF.Sin(phi);
                n = Vector3.Normalize(new Vector3(cr * MathF.Cos(th) / r, sy / h, cr * MathF.Sin(th) / r));
                uv = new Vector2(th * r, phi * r);
                return new Vector3(cx + r * cr * MathF.Cos(th), y0 + h * sy, cz + r * cr * MathF.Sin(th));
            }
            for (int s = 0; s < stacks; s++)
                for (int i = 0; i < slices; i++)
                {
                    var a = P(s, i, out var na, out var ua); var b = P(s, i + 1, out var nb, out var ub);
                    var c = P(s + 1, i + 1, out var nc, out var uc); var d = P(s + 1, i, out var nd, out var ud);
                    Tri(a, b, c, na, nb, nc, ua, ub, uc, m);
                    Tri(a, c, d, na, nc, nd, ua, uc, ud, m);
                }
        }

        /// <summary>Свод вдоль Z: дуга окружности поперёк X (пролёт span, стрела rise) от z0 до z1, основание на y0.</summary>
        public void Vault(float span, float rise, float z0, float z1, float y0, int m, int seg = 40)
        {
            float R = (span * span / 4 + rise * rise) / (2 * rise), yc = y0 + rise - R, half = MathF.Asin(span / 2 / R);
            for (int i = 0; i < seg; i++)
            {
                float a0 = -half + 2 * half * i / seg, a1 = -half + 2 * half * (i + 1) / seg;
                var n0 = new Vector3(MathF.Sin(a0), MathF.Cos(a0), 0); var n1 = new Vector3(MathF.Sin(a1), MathF.Cos(a1), 0);
                var p0 = new Vector3(R * MathF.Sin(a0), yc + R * MathF.Cos(a0), 0); var p1 = new Vector3(R * MathF.Sin(a1), yc + R * MathF.Cos(a1), 0);
                float u0 = a0 * R, u1 = a1 * R;
                Tri(p0 with { Z = z0 }, p1 with { Z = z0 }, p1 with { Z = z1 }, n0, n1, n1, new(u0, z0), new(u1, z0), new(u1, z1), m);
                Tri(p0 with { Z = z0 }, p1 with { Z = z1 }, p0 with { Z = z1 }, n0, n1, n0, new(u0, z0), new(u1, z1), new(u0, z1), m);
            }
        }

        /// <summary>Вертикальная плоскость торца z = z0 с арочным верхом (стрела rise над y1), шириной span по X.</summary>
        public void ArchedEnd(float span, float y0, float y1, float rise, float z, int m, int seg = 40)
        {
            float R = (span * span / 4 + rise * rise) / (2 * rise), yc = y1 + rise - R;
            var n = new Vector3(0, 0, z < 0 ? -1 : 1);
            for (int i = 0; i < seg; i++)
            {
                float x0 = -span / 2 + span * i / seg, x1 = -span / 2 + span * (i + 1) / seg;
                float t0 = yc + MathF.Sqrt(MathF.Max(0, R * R - x0 * x0)), t1 = yc + MathF.Sqrt(MathF.Max(0, R * R - x1 * x1));
                Tri(new(x0, y0, z), new(x1, y0, z), new(x1, t1, z), n, n, n, new(x0, y0), new(x1, y0), new(x1, t1), m);
                Tri(new(x0, y0, z), new(x1, t1, z), new(x0, t0, z), n, n, n, new(x0, y0), new(x1, t1), new(x0, t0), m);
            }
        }

        public void Ground(int m)
        {
            // Мощение до горизонта: плитки 20 м в радиусе 300 м, дальше — 400 м до 6 км (край земли не виден на снимке).
            void Tile(float x0, float z0, float x1, float z1)
            {
                var up = Vector3.UnitY;
                Tri(new(x0, 0, z0), new(x0, 0, z1), new(x1, 0, z1), up, up, up, new(x0, z0), new(x0, z1), new(x1, z1), m);
                Tri(new(x0, 0, z0), new(x1, 0, z1), new(x1, 0, z0), up, up, up, new(x0, z0), new(x1, z1), new(x1, z0), m);
            }
            for (int i = -15; i < 15; i++)
                for (int j = -15; j < 15; j++) Tile(i * 20, j * 20, i * 20 + 20, j * 20 + 20);
            for (int i = -15; i < 15; i++)
                for (int j = -15; j < 15; j++)
                {
                    float x0 = i * 400, z0 = j * 400;
                    if (x0 >= -300 && x0 + 400 <= 300 && z0 >= -300 && z0 + 400 <= 300) continue;
                    if (x0 + 400 > -300 && x0 < 300 && z0 + 400 > -300 && z0 < 300)
                    {
                        // Кольцо вокруг ближней зоны — плитками 20 м вне квадрата ±300.
                        for (float x = x0; x < x0 + 400; x += 20)
                            for (float z = z0; z < z0 + 400; z += 20)
                                if (!(x >= -300 && x < 300 && z >= -300 && z < 300)) Tile(x, z, x + 20, z + 20);
                        continue;
                    }
                    Tile(x0, z0, x0 + 400, z0 + 400);
                }
        }
    }
    // ───────────────────────────── Сцены ─────────────────────────────

    private static Vector3 Hex(string hex)
    {
        Models.CurvedDesign.TryParseHex(hex, out var c);
        return new Vector3(c.R / 255f, c.G / 255f, c.B / 255f);
    }

    private static List<Vector2> Arc(Vector2 a, Vector2 b, float sagitta, int n)
    {
        // Дуга от a к b со стрелой наружу (вправо по ходу обхода против часовой).
        float c = Vector2.Distance(a, b);
        float R = (c * c / 4 + sagitta * sagitta) / (2 * MathF.Abs(sagitta));
        var u = (b - a) / c;
        var nOut = new Vector2(u.Y, -u.X);
        var mid = (a + b) / 2;
        float half = MathF.Asin(c / 2 / R), zc = sagitta - R;
        var pts = new List<Vector2>();
        for (int i = 0; i < n; i++)
        {
            float phi = -half + 2 * half * i / n;
            float lx = R * MathF.Sin(phi), lz = zc + R * MathF.Cos(phi);
            pts.Add(mid + u * lx + nOut * lz);
        }
        return pts;
    }

    private static List<Vector2> RoundRect(float w, float d, float r, float cx = 0, float cz = 0)
    {
        var pts = new List<Vector2>();
        var corners = new[] { (w / 2 - r, -d / 2 + r, -90f), (w / 2 - r, d / 2 - r, 0f), (-w / 2 + r, d / 2 - r, 90f), (-w / 2 + r, -d / 2 + r, 180f) };
        foreach (var (x, z, a0) in corners)
            for (int i = 0; i <= 6; i++)
            {
                float a = (a0 + 90f * i / 6) * MathF.PI / 180;
                pts.Add(new Vector2(cx + x + r * MathF.Cos(a), cz + z + r * MathF.Sin(a)));
            }
        return pts;
    }

    private static (Scene Scene, RasterCamera Camera) Build(int index)
    {
        var s = new Scene();
        int ground = s.Surface(new Surface(Mat.Ground, new Vector3(0.47f, 0.52f, 0.45f)));
        s.Ground(ground);
        var truth = TruthColors[index];
        switch (index)
        {
            case 0:
            {
                int stone = s.Surface(new Surface(Mat.Facade, Hex(truth.Wall), Bay: 4.5f, FloorH: 4.2f, WinW: 0.8f, WinH: 0.5f, Sill: 0.3f));
                int glass = s.Surface(new Surface(Mat.Glass, Hex(truth.Glass), Bay: 1.5f, FloorH: 3.6f));
                int roof = s.Surface(new Surface(Mat.Plain, Hex(truth.Roof)));
                s.Extrude(RoundRect(56, 40, 5), 0, 8.4f, stone, roof);
                // Башня: дуговой фасад к зрителю (стрела 7 м на хорде 38 м), тыл со скруглёнными углами.
                var plan = Arc(new Vector2(-19, -10), new Vector2(19, -10), 7, 40);
                plan.AddRange(RoundCornerSeq(new Vector2(19, -10), new Vector2(19, 10), new Vector2(-19, 10), 4));
                plan.AddRange(RoundCornerSeq(new Vector2(19, 10), new Vector2(-19, 10), new Vector2(-19, -10), 4));
                s.Extrude(plan, 8.4f, 8.4f + 22 * 3.6f, glass, roof);
                return (s, new RasterCamera(new Vector3(-62, 22, -118), new Vector3(0, 40, 0), 46));
            }
            case 1:
            {
                int wall = s.Surface(new Surface(Mat.Facade, Hex(truth.Wall), Bay: 3f, FloorH: 3.1f, WinW: 0.48f, WinH: 0.52f, Sill: 0.3f));
                int band = s.Surface(new Surface(Mat.Plain, Hex("#D8B48A")));
                int roof = s.Surface(new Surface(Mat.Plain, Hex(truth.Roof)));
                int green = s.Surface(new Surface(Mat.Green, new Vector3(0.36f, 0.52f, 0.24f)));
                int[] floors = [14, 12, 10, 8, 6];
                float bay = 13, d = 18, x0 = -bay * floors.Length / 2;
                for (int i = 0; i < floors.Length; i++)
                {
                    float xa = x0 + i * bay, top = floors[i] * 3.1f;
                    s.Extrude([new(xa, -d / 2), new(xa + bay, -d / 2), new(xa + bay, d / 2), new(xa, d / 2)], 0, top, wall, roof);
                    // Парапет террасы и озеленение.
                    s.Extrude([new(xa, -d / 2), new(xa + bay, -d / 2), new(xa + bay, -d / 2 + 0.25f), new(xa, -d / 2 + 0.25f)], top, top + 1.1f, band, band);
                    if (i > 0) s.Box(xa + bay / 2, 0, bay * 0.5f, d * 0.4f, top, top + 0.7f, 0, green, green);
                }
                return (s, new RasterCamera(new Vector3(52, 30, -92), new Vector3(-2, 18, 0), 50));
            }
            case 2:
            {
                int cream = s.Surface(new Surface(Mat.Facade, Hex(truth.Wall), Bay: 4f, FloorH: 5f, WinW: 0.34f, WinH: 0.6f, Sill: 0.2f));
                int white = s.Surface(new Surface(Mat.Column, Hex("#F2EEE4")));
                int dome = s.Surface(new Surface(Mat.Dome, Hex(truth.Roof)));
                int plain = s.Surface(new Surface(Mat.Plain, Hex("#D9CBA8")));
                float R = 12;
                s.Cylinder(0, 0, R, 0, 10, cream, plain, 48);
                s.Cylinder(0, 0, R + 0.4f, 10, 11, plain, plain, 48);
                s.Dome(0, 0, 11, R + 0.3f, 10.5f, dome);
                // Портик: стилобат, 6 колонн, антаблемент, фронтон.
                s.Box(0, -R - 4, 20, 7, 0, 0.8f, 0, plain, plain);
                for (int i = 0; i < 6; i++) s.Cylinder(-7.5f + 3 * i, -R - 5.6f, 0.55f, 0.8f, 9.4f, white, white, 20);
                s.Box(0, -R - 4.2f, 19, 4, 9.4f, 10.6f, 0, plain, plain);
                ExtrudeZ(s, [new(-9.8f, 10.6f), new(9.8f, 10.6f), new(0, 13.6f)], -R - 6.3f, -R - 2.2f, plain);
                // Крылья под углом.
                foreach (int side in new[] { -1, 1 })
                {
                    float ang = 22, len = 22, dep = 13;
                    float a = ang * MathF.PI / 180;
                    float cx = side * (R + len / 2 - 0.8f) * MathF.Cos(a), cz = (R + len / 2 - 0.8f) * MathF.Sin(a);
                    s.Box(cx, cz, len, dep, 0, 10, side * -ang, cream, plain);
                }
                return (s, new RasterCamera(new Vector3(-18, 13, -60), new Vector3(0, 8.5f, 0), 50));
            }
            case 3:
            {
                int lime = s.Surface(new Surface(Mat.Facade, Hex(truth.Wall), Bay: 2.2f, FloorH: 3.4f, WinW: 0.5f, WinH: 0.56f, Sill: 0.25f));
                int baseM = s.Surface(new Surface(Mat.Facade, Hex("#A89878"), Bay: 2.2f, FloorH: 3.4f, WinW: 0.5f, WinH: 0.5f, Sill: 0.3f));
                int cornice = s.Surface(new Surface(Mat.Plain, Hex(truth.Roof)));
                // Треугольник: стрелка (угол 50°) к зрителю, скруглена R = 2,5 м.
                float Lt = 60, theta = 50 * MathF.PI / 180, Bw = 2 * Lt * MathF.Tan(theta / 2);
                var prow = new Vector2(0, -Lt * 2 / 3); var right = new Vector2(Bw / 2, Lt / 3); var left = new Vector2(-Bw / 2, Lt / 3);
                var loop = RoundCornerSeq(left, prow, right, 2.5f);
                loop.Add(right);
                loop.Add(left);
                s.Extrude(loop, 0, 6.8f, baseM, null);
                s.Extrude(loop, 6.8f, 16 * 3.4f, lime, null);
                // Карниз с выносом.
                var big = Grow(loop, 0.9f);
                s.Extrude(big, 16 * 3.4f, 16 * 3.4f + 1.0f, cornice, cornice, bottom: true);
                return (s, new RasterCamera(new Vector3(0, 12, -118), new Vector3(0, 25, 0), 40));
            }
            default:
            {
                int concrete = s.Surface(new Surface(Mat.Facade, Hex(truth.Wall), Bay: 6f, FloorH: 6f, WinW: 0.4f, WinH: 0.3f, Sill: 0.45f));
                int glass = s.Surface(new Surface(Mat.Glass, Hex(truth.Glass), Bay: 2.2f, FloorH: 4.2f));
                int metal = s.Surface(new Surface(Mat.Metal, Hex(truth.Roof)));
                int pylon = s.Surface(new Surface(Mat.Frame, Hex("#3F4A55")));
                float span = 36, len = 66, wallH = 6, rise = 13;
                s.Box(span / 2 - 0.25f, 0, 0.5f, len, 0, wallH, 0, concrete, concrete);
                s.Box(-span / 2 + 0.25f, 0, 0.5f, len, 0, wallH, 0, concrete, concrete);
                s.Vault(span + 1, rise, -len / 2 - 0.5f, len / 2 + 0.5f, wallH, metal);
                s.ArchedEnd(span - 0.4f, 0, wallH, rise - 0.3f, -len / 2, glass);
                s.ArchedEnd(span - 0.4f, 0, wallH, rise - 0.3f, len / 2, glass);
                for (float z = -len / 2 + 3; z < len / 2 - 2; z += 6)
                    foreach (int side in new[] { -1, 1 })
                        Pylon(s, side * (span / 2 + 1.4f), z, wallH + 0.6f, side, pylon);
                return (s, new RasterCamera(new Vector3(-58, 26, -84), new Vector3(0, 8, 0), 50));
            }
        }
    }

    /// <summary>Последовательность точек: прямая к углу v и дуга скругления радиуса r до следующего направления.</summary>
    private static List<Vector2> RoundCornerSeq(Vector2 a, Vector2 v, Vector2 b, float r)
    {
        var d1 = Vector2.Normalize(v - a);
        var d2 = Vector2.Normalize(b - v);
        float turn = MathF.Acos(Math.Clamp(Vector2.Dot(d1, d2), -1, 1));
        float t = r * MathF.Tan(turn / 2);
        var p0 = v - d1 * t;
        var p1 = v + d2 * t;
        var pts = new List<Vector2> { p0 };
        // Центр дуги — по внутренней нормали.
        var nIn = new Vector2(-d1.Y, d1.X);
        if (d1.X * d2.Y - d1.Y * d2.X < 0) nIn = -nIn;
        var center = p0 + nIn * r;
        float a0 = MathF.Atan2(p0.Y - center.Y, p0.X - center.X), a1 = MathF.Atan2(p1.Y - center.Y, p1.X - center.X);
        float da = a1 - a0;
        while (da > MathF.PI) da -= 2 * MathF.PI;
        while (da < -MathF.PI) da += 2 * MathF.PI;
        for (int i = 1; i <= 8; i++) pts.Add(center + r * new Vector2(MathF.Cos(a0 + da * i / 8), MathF.Sin(a0 + da * i / 8)));
        return pts;
    }

    private static List<Vector2> Grow(List<Vector2> loop, float delta)
    {
        var ccw = AIArchitectStudio.Rendering.MeshAccumulator.EnsureCounterClockwise(loop);
        int n = ccw.Count;
        var result = new List<Vector2>();
        for (int i = 0; i < n; i++)
        {
            var a = ccw[(i - 1 + n) % n]; var b = ccw[i]; var c = ccw[(i + 1) % n];
            var n1 = Vector2.Normalize(new Vector2((b - a).Y, -(b - a).X));
            var n2 = Vector2.Normalize(new Vector2((c - b).Y, -(c - b).X));
            var bis = n1 + n2;
            if (bis.LengthSquared() < 1e-6f) { result.Add(b + n1 * delta); continue; }
            bis = Vector2.Normalize(bis);
            result.Add(b + bis * delta * MathF.Min(3, 1 / MathF.Max(0.05f, Vector2.Dot(bis, n1))));
        }
        return result;
    }

    private static void ExtrudeX(Scene s, List<Vector2> profileZY, float x0, float x1, int m)
    {
        // Профиль (z, y), выдавленный по X: бока и торцы.
        int n = profileZY.Count;
        for (int i = 0; i < n; i++)
        {
            var p = profileZY[i];
            var q = profileZY[(i + 1) % n];
            var e = q - p;
            var nrm = Vector3.Normalize(new Vector3(0, -e.X, e.Y));
            s.Tri(new(x0, p.Y, p.X), new(x1, p.Y, p.X), new(x1, q.Y, q.X), nrm, nrm, nrm, new(x0, 0), new(x1, 0), new(x1, 1), m);
            s.Tri(new(x0, p.Y, p.X), new(x1, q.Y, q.X), new(x0, q.Y, q.X), nrm, nrm, nrm, new(x0, 0), new(x1, 1), new(x0, 1), m);
        }
        var a = profileZY[0]; var b = profileZY[1]; var c = profileZY[2];
        foreach (float x in new[] { x0, x1 })
        {
            var nrm = new Vector3(x < 0 ? -1 : 1, 0, 0);
            s.Tri(new(x, a.Y, a.X), new(x, b.Y, b.X), new(x, c.Y, c.X), nrm, nrm, nrm, a, b, c, m);
        }
    }

    private static void ExtrudeZ(Scene s, List<Vector2> profileXY, float z0, float z1, int m)
    {
        // Профиль (x, y) против часовой, выдавленный по Z: бока и торцы (фронтон портика).
        int n = profileXY.Count;
        for (int i = 0; i < n; i++)
        {
            var p = profileXY[i];
            var q = profileXY[(i + 1) % n];
            var e = q - p;
            var nrm = Vector3.Normalize(new Vector3(e.Y, -e.X, 0));
            s.Tri(new(p.X, p.Y, z0), new(q.X, q.Y, z0), new(q.X, q.Y, z1), nrm, nrm, nrm, new(0, 0), new(1, 0), new(1, 1), m);
            s.Tri(new(p.X, p.Y, z0), new(q.X, q.Y, z1), new(p.X, p.Y, z1), nrm, nrm, nrm, new(0, 0), new(1, 1), new(0, 1), m);
        }
        foreach (float z in new[] { z0, z1 })
        {
            var nrm = new Vector3(0, 0, z == z0 ? -1 : 1);
            for (int i = 1; i + 1 < n; i++)
                s.Tri(new(profileXY[0].X, profileXY[0].Y, z), new(profileXY[i].X, profileXY[i].Y, z), new(profileXY[i + 1].X, profileXY[i + 1].Y, z), nrm, nrm, nrm,
                    profileXY[0], profileXY[i], profileXY[i + 1], m);
        }
    }

    private static void Pylon(Scene s, float x, float z, float h, int side, int m)
    {
        // Наклонный пилон: призма 0,7 × 0,9, верх смещён наружу на 1,4 м.
        float lean = 1.4f * side;
        var b0 = new[] { new Vector2(x - 0.35f, z - 0.45f), new Vector2(x + 0.35f, z - 0.45f), new Vector2(x + 0.35f, z + 0.45f), new Vector2(x - 0.35f, z + 0.45f) };
        for (int i = 0; i < 4; i++)
        {
            var p = b0[i]; var q = b0[(i + 1) % 4];
            var a = new Vector3(p.X, 0, p.Y); var b = new Vector3(q.X, 0, q.Y);
            var c = new Vector3(q.X + lean, h, q.Y); var d = new Vector3(p.X + lean, h, p.Y);
            var nrm = Vector3.Normalize(Vector3.Cross(b - a, d - a));
            if (Vector3.Dot(nrm, new Vector3((p + q).X / 2 - x, 0, (p + q).Y / 2 - z)) < 0) nrm = -nrm;
            s.Tri(a, b, c, nrm, nrm, nrm, Vector2.Zero, Vector2.UnitX, Vector2.One, m);
            s.Tri(a, c, d, nrm, nrm, nrm, Vector2.Zero, Vector2.One, Vector2.UnitY, m);
        }
    }

    // ───────────────────────────── Рендер ─────────────────────────────

    private static readonly Vector3 ToSun = Vector3.Normalize(new Vector3(-0.55f, 0.62f, -0.56f));
    private static readonly Vector3 SunColor = new(1.0f, 0.95f, 0.86f);

    /// <summary>Рендер снимка index (0…4) в BGRA8 1280 × 800.</summary>
    public static byte[] Render(int index)
    {
        var (scene, camera) = Build(index);
        var forward = Vector3.Normalize(camera.Target - camera.Eye);
        var right = Vector3.Normalize(Vector3.Cross(forward, Vector3.UnitY));
        var up = Vector3.Cross(right, forward);
        float tanHalf = MathF.Tan(camera.FovDegrees * MathF.PI / 360);
        float aspect = (float)Width / Height;

        Vector3 Sky(Vector3 dir)
        {
            float el = Math.Clamp(dir.Y, -0.2f, 1f);
            var horizon = new Vector3(0.80f, 0.87f, 0.95f);
            var zenith = new Vector3(0.30f, 0.53f, 0.84f);
            float t = MathF.Pow(Math.Clamp(el / 0.65f, 0, 1), 0.7f);
            var c = Vector3.Lerp(horizon, zenith, t);
            // Облака: значение шума по направлению луча.
            float cloud = Fbm(dir.X / Math.Max(0.12f, dir.Y) * 1.6f, dir.Z / Math.Max(0.12f, dir.Y) * 1.6f);
            float k = Math.Clamp((cloud - 0.55f) * 2.2f, 0, 0.75f) * Math.Clamp(el * 5, 0, 1);
            c = Vector3.Lerp(c, new Vector3(0.96f, 0.97f, 0.98f), k);
            float sun = MathF.Pow(Math.Max(0, Vector3.Dot(dir, ToSun)), 400);
            return c + SunColor * sun * 0.6f;
        }

        Vector3 Background(float fx, float fy)
        {
            var dir = Vector3.Normalize(forward + right * ((fx * 2 - 1) * tanHalf * aspect) + up * ((1 - fy * 2) * tanHalf));
            return Sky(dir);
        }

        Vector3 Ambient(Vector3 n) => Vector3.Lerp(new Vector3(0.36f, 0.34f, 0.31f), new Vector3(0.48f, 0.56f, 0.68f), n.Y * 0.5f + 0.5f) * 0.62f;

        Vector3 Shade(RasterFragment f)
        {
            var surf = scene.Surfaces[f.Material];
            float diff = MathF.Max(0, Vector3.Dot(f.Normal, ToSun)) * f.SunVisibility;
            Vector3 Lambert(Vector3 albedo) => albedo * (SunColor * diff * 0.95f + Ambient(f.Normal));
            var reflect = Vector3.Reflect(-f.ToEye, f.Normal);
            float ndv = Math.Clamp(Vector3.Dot(f.Normal, f.ToEye), 0, 1);
            switch (surf.Kind)
            {
                case Mat.Ground:
                {
                    float noise = Hash(MathF.Floor(f.Position.X / 0.6f), MathF.Floor(f.Position.Z / 0.6f)) * 0.06f - 0.03f;
                    bool joint = Frac(f.Position.X / 3f) < 0.02f || Frac(f.Position.Z / 3f) < 0.02f;
                    var albedo = surf.Albedo * (1 + noise) * (joint ? 0.9f : 1f);
                    return Lambert(albedo);
                }
                case Mat.Glass:
                {
                    float fu = Frac(f.Uv.X / surf.Bay), fv = Frac(f.Uv.Y / surf.FloorH);
                    if (fu < 0.045f || fv < 0.05f) return Lambert(new Vector3(0.20f, 0.22f, 0.24f));
                    if (fv < 0.26f) return Lambert(surf.Albedo * 0.55f + new Vector3(0.05f));
                    float fresnel = 0.22f + 0.65f * MathF.Pow(1 - ndv, 4);
                    var sky = Sky(Vector3.Normalize(reflect with { Y = MathF.Abs(reflect.Y) }));
                    var interior = surf.Albedo * 0.45f;
                    var c = Vector3.Lerp(interior, sky * surf.Albedo * 1.35f, fresnel + 0.25f);
                    float spec = MathF.Pow(Math.Max(0, Vector3.Dot(reflect, ToSun)), 120) * f.SunVisibility;
                    return c + SunColor * spec * 0.8f;
                }
                case Mat.Facade:
                {
                    float fu = Frac(f.Uv.X / surf.Bay), fv = Frac(f.Uv.Y / surf.FloorH);
                    bool window = MathF.Abs(fu - 0.5f) < surf.WinW / 2 && fv > surf.Sill && fv < surf.Sill + surf.WinH && MathF.Abs(f.Normal.Y) < 0.5f && f.Uv.Y > 0.8f;
                    if (window)
                    {
                        bool frame = MathF.Abs(fu - 0.5f) > surf.WinW / 2 - 0.035f || fv < surf.Sill + 0.03f || fv > surf.Sill + surf.WinH - 0.03f;
                        if (frame) return Lambert(new Vector3(0.92f, 0.91f, 0.88f));
                        var sky = Sky(Vector3.Normalize(reflect with { Y = MathF.Abs(reflect.Y) }));
                        return Vector3.Lerp(new Vector3(0.12f, 0.15f, 0.18f), sky * 0.7f, 0.18f + 0.4f * MathF.Pow(1 - ndv, 3));
                    }
                    bool cornice = fv < 0.04f && f.Uv.Y > 0.8f;
                    return Lambert(surf.Albedo * (cornice ? 0.82f : 1f));
                }
                case Mat.Metal:
                {
                    bool rib = Frac(f.Uv.X / 1.6f) < 0.07f;
                    float spec = MathF.Pow(Math.Max(0, Vector3.Dot(reflect, ToSun)), 24) * f.SunVisibility;
                    return Lambert(surf.Albedo * (rib ? 0.86f : 1f)) + SunColor * spec * 0.25f;
                }
                case Mat.Dome:
                {
                    bool rib = Frac(f.Uv.X / 2.2f) < 0.06f;
                    float spec = MathF.Pow(Math.Max(0, Vector3.Dot(reflect, ToSun)), 30) * f.SunVisibility;
                    return Lambert(surf.Albedo * (rib ? 0.8f : 1f)) + SunColor * spec * 0.18f;
                }
                case Mat.Green:
                    return Lambert(surf.Albedo * (0.85f + 0.3f * Hash(MathF.Floor(f.Position.X * 3), MathF.Floor(f.Position.Z * 3))));
                default:
                    return Lambert(surf.Albedo);
            }
        }

        var bgra = scene.Raster.Render(camera, Width, Height, 2, ToSun, Shade, Background, 4096);
        // Виньетирование и шум матрицы (детерминированный).
        for (int y = 0; y < Height; y++)
            for (int x = 0; x < Width; x++)
            {
                float dx = (x - Width / 2f) / Width, dy = (y - Height / 2f) / Height;
                float vig = 1 - 0.28f * (dx * dx + dy * dy) * 2.2f;
                float noise = (Hash(x * 0.37f + index * 17, y * 0.73f) - 0.5f) * 5f;
                int o = (y * Width + x) * 4;
                for (int c = 0; c < 3; c++) bgra[o + c] = (byte)Math.Clamp((int)(bgra[o + c] * vig + noise), 0, 255);
            }
        return bgra;
    }

    private static float Frac(float v) => v - MathF.Floor(v);

    private static float Hash(float x, float y)
    {
        float h = MathF.Sin(x * 127.1f + y * 311.7f) * 43758.5453f;
        return h - MathF.Floor(h);
    }

    private static float Noise(float x, float y)
    {
        float ix = MathF.Floor(x), iy = MathF.Floor(y), fx = x - ix, fy = y - iy;
        float a = Hash(ix, iy), b = Hash(ix + 1, iy), c = Hash(ix, iy + 1), d = Hash(ix + 1, iy + 1);
        float ux = fx * fx * (3 - 2 * fx), uy = fy * fy * (3 - 2 * fy);
        return a + (b - a) * ux + (c - a) * uy + (a - b - c + d) * ux * uy;
    }

    private static float Fbm(float x, float y)
    {
        float v = 0, amp = 0.5f;
        for (int i = 0; i < 4; i++)
        {
            v += amp * Noise(x, y);
            x *= 2.03f; y *= 2.03f;
            amp *= 0.5f;
        }
        return v;
    }
}
#endif
