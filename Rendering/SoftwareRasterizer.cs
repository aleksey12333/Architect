using System.Numerics;

namespace AIArchitectStudio.Rendering;

/// <summary>Вершина программного растеризатора: мировая точка, нормаль и координаты развёртки (u, v — метры по фасаду).</summary>
public readonly record struct RasterVertex(Vector3 Position, Vector3 Normal, Vector2 Uv);

/// <summary>Вход шейдера пикселя: мировая точка, нормаль, развёртка, материал треугольника, направление к камере, освещённость Солнцем.</summary>
public readonly record struct RasterFragment(Vector3 Position, Vector3 Normal, Vector2 Uv, int Material, Vector3 ToEye, float SunVisibility, int PixelX, int PixelY);

/// <summary>Перспективная камера растеризатора (Y-Up, правая система).</summary>
public sealed record RasterCamera(Vector3 Eye, Vector3 Target, float FovDegrees, Vector3? Up = null)
{
    public Vector3 UpVector => Up ?? Vector3.UnitY;
}

/// <summary>
/// Программный растеризатор без графического движка (System.Numerics): треугольники с перспективно-корректной интерполяцией нормали,
/// мировой точки и развёртки, z-буфер, карта теней от направленного Солнца (ортогональная проекция по габариту сцены), суперсэмплинг
/// со сглаживанием при уменьшении, шейдер пикселя и фон — функции вызывающего. Используется для вращаемого 3D-превью студии
/// «Реконструкция по фото и плану» и для фотореалистичных рендеров демонстрационных снимков. Результат — BGRA8.
/// </summary>
public sealed class SoftwareRasterizer
{
    private readonly List<(RasterVertex A, RasterVertex B, RasterVertex C, int Material)> _triangles = [];
    private readonly List<(Vector3 A, Vector3 B, uint Bgra, float Width)> _lines = [];

    public int TriangleCount => _triangles.Count;

    public void AddTriangle(RasterVertex a, RasterVertex b, RasterVertex c, int material) => _triangles.Add((a, b, c, material));

    /// <summary>Отрезок поверх заливки с проверкой глубины (рёбра элементов превью).</summary>
    public void AddLine(Vector3 a, Vector3 b, uint bgra, float width = 1f) => _lines.Add((a, b, bgra, width));

    public void Clear()
    {
        _triangles.Clear();
        _lines.Clear();
    }

    /// <summary>Габарит всех треугольников.</summary>
    public (Vector3 Min, Vector3 Max) Bounds()
    {
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        foreach (var (a, b, c, _) in _triangles)
        {
            min = Vector3.Min(min, Vector3.Min(a.Position, Vector3.Min(b.Position, c.Position)));
            max = Vector3.Max(max, Vector3.Max(a.Position, Vector3.Max(b.Position, c.Position)));
        }
        return _triangles.Count == 0 ? (Vector3.Zero, Vector3.Zero) : (min, max);
    }

    /// <summary>
    /// Рендер: width × height пикселей результата, supersample — множитель внутреннего разрешения (1…3), sunDirection — направление
    /// к Солнцу (null — без теней), shade — цвет пикселя (линейный RGB 0…1), background — цвет пустого пикселя по нормированным
    /// координатам кадра (0…1).
    /// </summary>
    public byte[] Render(RasterCamera camera, int width, int height, int supersample, Vector3? sunDirection,
        Func<RasterFragment, Vector3> shade, Func<float, float, Vector3> background, int shadowMapSize = 2048)
    {
        int ss = Math.Clamp(supersample, 1, 3);
        int w = width * ss, h = height * ss;
        var view = Matrix4x4.CreateLookAt(camera.Eye, camera.Target, camera.UpVector);
        float aspect = (float)width / height;
        var proj = Matrix4x4.CreatePerspectiveFieldOfView(camera.FovDegrees * MathF.PI / 180f, aspect, 0.5f, 20000f);
        var viewProj = view * proj;

        ShadowMap? shadow = sunDirection is { } sun ? BuildShadowMap(Vector3.Normalize(sun), shadowMapSize) : null;

        var depth = new float[w * h];
        Array.Fill(depth, float.MaxValue);
        var color = new Vector3[w * h];
        var filled = new bool[w * h];

        foreach (var (a, b, c, material) in _triangles)
            RasterTriangle(a, b, c, material, viewProj, w, h, camera.Eye, depth, color, filled, shadow, shade);

        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int i = y * w + x;
                if (!filled[i]) color[i] = background((x + 0.5f) / w, (y + 0.5f) / h);
            }

        foreach (var (a, b, bgra, lw) in _lines) DrawLine(a, b, bgra, lw * ss, viewProj, w, h, depth, color);

        // Уменьшение с усреднением (сглаживание) и запись BGRA.
        var result = new byte[width * height * 4];
        float inv = 1f / (ss * ss);
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                var sum = Vector3.Zero;
                for (int dy = 0; dy < ss; dy++)
                    for (int dx = 0; dx < ss; dx++)
                        sum += color[(y * ss + dy) * w + x * ss + dx];
                sum *= inv;
                int o = (y * width + x) * 4;
                result[o] = ToByte(sum.Z);
                result[o + 1] = ToByte(sum.Y);
                result[o + 2] = ToByte(sum.X);
                result[o + 3] = 255;
            }
        return result;
    }

    private static byte ToByte(float v) => (byte)Math.Clamp((int)MathF.Round(v * 255f), 0, 255);

    private void RasterTriangle(RasterVertex a, RasterVertex b, RasterVertex c, int material, Matrix4x4 viewProj, int w, int h, Vector3 eye,
        float[] depth, Vector3[] color, bool[] filled, ShadowMap? shadow, Func<RasterFragment, Vector3> shade)
    {
        var ca = Vector4.Transform(new Vector4(a.Position, 1), viewProj);
        var cb = Vector4.Transform(new Vector4(b.Position, 1), viewProj);
        var cc = Vector4.Transform(new Vector4(c.Position, 1), viewProj);
        if (ca.W < 0.5f || cb.W < 0.5f || cc.W < 0.5f) return; // за ближней плоскостью — отбрасывается
        Vector3 Ndc(Vector4 v) => new((v.X / v.W * 0.5f + 0.5f) * w, (1 - (v.Y / v.W * 0.5f + 0.5f)) * h, v.Z / v.W);
        var pa = Ndc(ca); var pb = Ndc(cb); var pc = Ndc(cc);
        float area = (pb.X - pa.X) * (pc.Y - pa.Y) - (pc.X - pa.X) * (pb.Y - pa.Y);
        if (MathF.Abs(area) < 1e-6f) return;
        int minX = Math.Max(0, (int)MathF.Floor(MathF.Min(pa.X, MathF.Min(pb.X, pc.X))));
        int maxX = Math.Min(w - 1, (int)MathF.Ceiling(MathF.Max(pa.X, MathF.Max(pb.X, pc.X))));
        int minY = Math.Max(0, (int)MathF.Floor(MathF.Min(pa.Y, MathF.Min(pb.Y, pc.Y))));
        int maxY = Math.Min(h - 1, (int)MathF.Ceiling(MathF.Max(pa.Y, MathF.Max(pb.Y, pc.Y))));
        if (minX > maxX || minY > maxY) return;
        float iwa = 1 / ca.W, iwb = 1 / cb.W, iwc = 1 / cc.W;
        float invArea = 1 / area;

        for (int y = minY; y <= maxY; y++)
        {
            float py = y + 0.5f;
            for (int x = minX; x <= maxX; x++)
            {
                float px = x + 0.5f;
                float w0 = ((pb.X - px) * (pc.Y - py) - (pc.X - px) * (pb.Y - py)) * invArea;
                float w1 = ((pc.X - px) * (pa.Y - py) - (pa.X - px) * (pc.Y - py)) * invArea;
                float w2 = 1 - w0 - w1;
                if (w0 < -1e-5f || w1 < -1e-5f || w2 < -1e-5f) continue;
                float z = w0 * pa.Z + w1 * pb.Z + w2 * pc.Z;
                int i = y * w + x;
                if (z >= depth[i]) continue;
                // Перспективно-корректные веса.
                float q0 = w0 * iwa, q1 = w1 * iwb, q2 = w2 * iwc;
                float qs = 1 / (q0 + q1 + q2);
                q0 *= qs; q1 *= qs; q2 *= qs;
                var pos = a.Position * q0 + b.Position * q1 + c.Position * q2;
                var nrm = a.Normal * q0 + b.Normal * q1 + c.Normal * q2;
                float len = nrm.Length();
                nrm = len > 1e-6f ? nrm / len : Vector3.UnitY;
                var uv = a.Uv * q0 + b.Uv * q1 + c.Uv * q2;
                var toEye = Vector3.Normalize(eye - pos);
                if (Vector3.Dot(nrm, toEye) < 0) nrm = -nrm; // двусторонние грани
                float sunVis = shadow?.Visibility(pos, nrm) ?? 1f;
                depth[i] = z;
                color[i] = shade(new RasterFragment(pos, nrm, uv, material, toEye, sunVis, x, y));
                filled[i] = true;
            }
        }
    }

    private static void DrawLine(Vector3 a, Vector3 b, uint bgra, float width, Matrix4x4 viewProj, int w, int h, float[] depth, Vector3[] color)
    {
        var ca = Vector4.Transform(new Vector4(a, 1), viewProj);
        var cb = Vector4.Transform(new Vector4(b, 1), viewProj);
        if (ca.W < 0.5f || cb.W < 0.5f) return;
        var pa = new Vector3((ca.X / ca.W * 0.5f + 0.5f) * w, (1 - (ca.Y / ca.W * 0.5f + 0.5f)) * h, ca.Z / ca.W);
        var pb = new Vector3((cb.X / cb.W * 0.5f + 0.5f) * w, (1 - (cb.Y / cb.W * 0.5f + 0.5f)) * h, cb.Z / cb.W);
        float len = MathF.Max(MathF.Abs(pb.X - pa.X), MathF.Abs(pb.Y - pa.Y));
        int steps = Math.Max(1, (int)MathF.Ceiling(len));
        var col = new Vector3(((bgra >> 16) & 0xFF) / 255f, ((bgra >> 8) & 0xFF) / 255f, (bgra & 0xFF) / 255f);
        float alpha = ((bgra >> 24) & 0xFF) / 255f;
        int r = Math.Max(0, (int)MathF.Floor((width - 1) / 2));
        for (int s = 0; s <= steps; s++)
        {
            float t = (float)s / steps;
            float x = pa.X + (pb.X - pa.X) * t, y = pa.Y + (pb.Y - pa.Y) * t, z = pa.Z + (pb.Z - pa.Z) * t - 2e-4f;
            int cx = (int)x, cy = (int)y;
            for (int dy = -r; dy <= r; dy++)
                for (int dx = -r; dx <= r; dx++)
                {
                    int px = cx + dx, py = cy + dy;
                    if (px < 0 || py < 0 || px >= w || py >= h) continue;
                    int i = py * w + px;
                    if (z > depth[i] + 1e-3f) continue;
                    color[i] = color[i] * (1 - alpha) + col * alpha;
                }
        }
    }

    // ───────────────────────────── Карта теней ─────────────────────────────

    private sealed class ShadowMap(Matrix4x4 lightViewProj, float[] depth, int size, float texel)
    {
        public float Visibility(Vector3 p, Vector3 n)
        {
            // Сдвиг по нормали против «теневых угрей» и мерцания на освещённых гранях.
            var q = Vector4.Transform(new Vector4(p + n * texel * 1.5f, 1), lightViewProj);
            float u = (q.X * 0.5f + 0.5f) * size, v = (1 - (q.Y * 0.5f + 0.5f)) * size;
            int x0 = (int)MathF.Floor(u), y0 = (int)MathF.Floor(v);
            float lit = 0;
            // PCF 3 × 3 — мягкая граница тени.
            for (int dy = -1; dy <= 1; dy++)
                for (int dx = -1; dx <= 1; dx++)
                {
                    int x = x0 + dx, y = y0 + dy;
                    if (x < 0 || y < 0 || x >= size || y >= size) { lit += 1; continue; }
                    lit += q.Z <= depth[y * size + x] + 2e-3f ? 1 : 0;
                }
            return lit / 9f;
        }
    }

    private ShadowMap BuildShadowMap(Vector3 toSun, int size)
    {
        var (min, max) = Bounds();
        var center = (min + max) / 2;
        float radius = MathF.Max(1, (max - min).Length() / 2);
        var up = MathF.Abs(toSun.Y) > 0.98f ? Vector3.UnitZ : Vector3.UnitY;
        var view = Matrix4x4.CreateLookAt(center + toSun * radius * 2, center, up);
        var proj = Matrix4x4.CreateOrthographic(radius * 2, radius * 2, radius * 0.5f, radius * 4f);
        var vp = view * proj;
        var depth = new float[size * size];
        Array.Fill(depth, float.MaxValue);
        foreach (var (a, b, c, _) in _triangles)
        {
            Vector3 P(Vector3 p)
            {
                var v = Vector4.Transform(new Vector4(p, 1), vp);
                return new Vector3((v.X * 0.5f + 0.5f) * size, (1 - (v.Y * 0.5f + 0.5f)) * size, v.Z);
            }
            var pa = P(a.Position); var pb = P(b.Position); var pc = P(c.Position);
            float area = (pb.X - pa.X) * (pc.Y - pa.Y) - (pc.X - pa.X) * (pb.Y - pa.Y);
            if (MathF.Abs(area) < 1e-9f) continue;
            int minX = Math.Max(0, (int)MathF.Floor(MathF.Min(pa.X, MathF.Min(pb.X, pc.X))));
            int maxX = Math.Min(size - 1, (int)MathF.Ceiling(MathF.Max(pa.X, MathF.Max(pb.X, pc.X))));
            int minY = Math.Max(0, (int)MathF.Floor(MathF.Min(pa.Y, MathF.Min(pb.Y, pc.Y))));
            int maxY = Math.Min(size - 1, (int)MathF.Ceiling(MathF.Max(pa.Y, MathF.Max(pb.Y, pc.Y))));
            float invArea = 1 / area;
            for (int y = minY; y <= maxY; y++)
                for (int x = minX; x <= maxX; x++)
                {
                    float px = x + 0.5f, py = y + 0.5f;
                    float w0 = ((pb.X - px) * (pc.Y - py) - (pc.X - px) * (pb.Y - py)) * invArea;
                    float w1 = ((pc.X - px) * (pa.Y - py) - (pa.X - px) * (pc.Y - py)) * invArea;
                    float w2 = 1 - w0 - w1;
                    if (w0 < -1e-4f || w1 < -1e-4f || w2 < -1e-4f) continue;
                    float z = w0 * pa.Z + w1 * pb.Z + w2 * pc.Z;
                    int i = y * size + x;
                    if (z < depth[i]) depth[i] = z;
                }
        }
        return new ShadowMap(vp, depth, size, radius * 2 / size);
    }
}
