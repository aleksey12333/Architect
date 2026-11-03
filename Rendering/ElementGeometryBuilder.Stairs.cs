using System.Numerics;
using AIArchitectStudio.Models;
using AIArchitectStudio.Models.Geometry;

namespace AIArchitectStudio.Rendering;

/// <summary>
/// Геометрия лестничного марша по конструкции (<see cref="StairDesign"/>): без параметра — прежний сплошной монолит; монолитный по
/// наклонной плите — ступени на косой плите толщиной 160 мм; на двух косоурах — стальные косоуры и проступи без подступенков;
/// консольный — ступени 60 мм, заделанные в стену, с больцами у свободного края (без заливки до пола); винтовой — секторные
/// ступени вокруг центральной стойки со стойками ограждения. Подъём — вдоль локальной +Z.
/// </summary>
public static partial class ElementGeometryBuilder
{
    /// <summary>Толщина косой плиты монолитного марша (по нормали), м.</summary>
    private const float WaistThickness = 0.16f;

    private static void BuildStairElement(PartSet set, ArchitecturalElement e, float w, float h, float d)
    {
        if (StairDesign.IsSpiral(e))
        {
            BuildSpiralStair(set, e, w, h, d);
            return;
        }
        switch (StairDesign.Construction(e))
        {
            case StairConstruction.MonolithicSlab:
                BuildSlabFlight(set[MaterialKind.Concrete], w, h, d, e.StepCount);
                break;
            case StairConstruction.Stringers:
                BuildStringerFlight(set, w, h, d, e.StepCount);
                break;
            case StairConstruction.Cantilever:
                BuildCantileverFlight(set, w, h, d, e.StepCount);
                break;
            default:
                BuildStairs(set[MaterialKind.Concrete], w, h, d, e.StepCount);
                break;
        }
    }

    private static int StepsOf(float h, int stepCount) => stepCount > 0 ? Math.Clamp(stepCount, 1, 60) : Math.Clamp((int)Math.Round(h / 0.17f), 3, 40);

    /// <summary>Монолитный марш: профиль «ступени + косая плита» в плоскости (z, y), выдавленный по ширине марша.</summary>
    private static void BuildSlabFlight(MeshAccumulator acc, float w, float h, float d, int stepCount)
    {
        int steps = StepsOf(h, stepCount);
        float rise = h / steps, run = d / steps, k = rise / run;
        float v = WaistThickness * MathF.Sqrt(1 + k * k);
        var profile = new List<Vector2>(2 * steps + 4) { new(-d / 2, -h / 2) };
        for (int i = 0; i < steps; i++)
        {
            float z = -d / 2 + i * run, top = -h / 2 + (i + 1) * rise;
            profile.Add(new Vector2(z, top));
            profile.Add(new Vector2(z + run, top));
        }
        // Низ косой плиты — параллельно линии внутренних углов ступеней; у нижнего конца — горизонтальный срез по полу.
        profile.Add(new Vector2(d / 2, h / 2 - v));
        float zBottom = Math.Min(d / 2 - 0.01f, -d / 2 + v / k);
        profile.Add(new Vector2(zBottom, -h / 2));
        ExtrudeAlongX(acc, profile, -w / 2, w / 2);
    }

    /// <summary>Марш на двух косоурах: стальные косоуры 100 × 300 мм по краям и проступи 50 мм между ними, подступенков нет.</summary>
    private static void BuildStringerFlight(PartSet set, float w, float h, float d, int stepCount)
    {
        int steps = StepsOf(h, stepCount);
        float rise = h / steps, run = d / steps, k = rise / run, root = MathF.Sqrt(1 + k * k);
        const float sw = 0.1f, depth = 0.3f, lift = 0.06f;
        float T(float z) => -h / 2 + (z + d / 2) * k + lift * root;
        float B(float z) => T(z) - depth * root;
        var band = new List<Vector2> { new(-d / 2, B(-d / 2)), new(d / 2, B(d / 2)), new(d / 2, T(d / 2)), new(-d / 2, T(-d / 2)) };
        var stringer = ClipBandY(band, -h / 2, h / 2);
        var steel = set[MaterialKind.Steel];
        if (stringer.Count >= 3)
        {
            ExtrudeAlongX(steel, stringer, -w / 2, -w / 2 + sw);
            ExtrudeAlongX(steel, stringer, w / 2 - sw, w / 2);
        }
        var tread = set[MaterialKind.Timber];
        for (int i = 0; i < steps; i++)
        {
            float z = -d / 2 + i * run, top = -h / 2 + (i + 1) * rise;
            tread.AddBox(new Vector3(0, top - 0.025f, z + run / 2), w - 2 * sw, 0.05f, run + 0.03f);
        }
    }

    /// <summary>Консольный марш: ступени 60 мм, заделанные в стену у локальной −X, свободный край связан больцами Ø24.</summary>
    private static void BuildCantileverFlight(PartSet set, float w, float h, float d, int stepCount)
    {
        int steps = StepsOf(h, stepCount);
        float rise = h / steps, run = d / steps;
        var concrete = set[MaterialKind.Concrete];
        var steel = set[MaterialKind.Steel];
        const float tt = 0.06f;
        for (int i = 0; i < steps; i++)
        {
            float z = -d / 2 + i * run, top = -h / 2 + (i + 1) * rise;
            concrete.AddBox(new Vector3(0, top - tt / 2, z + run / 2), w, tt, run + 0.02f);
            if (i == 0) continue;
            // Больц: от верха предыдущей ступени до низа текущей у свободного края.
            float below = -h / 2 + i * rise;
            steel.AddCylinder(new Vector3(w / 2 - 0.06f, below, z + run * 0.15f), new Vector3(w / 2 - 0.06f, top - tt, z + run * 0.15f), 0.012f, 8);
        }
    }

    /// <summary>Винтовая лестница: секторные ступени 60 мм вокруг стойки Ø160 (шаг 22,5°), стойки ограждения 900 мм у наружного края.</summary>
    private static void BuildSpiralStair(PartSet set, ArchitecturalElement e, float w, float h, float d)
    {
        int steps = e.StepCount > 0 ? Math.Clamp(e.StepCount, 3, 60) : Math.Clamp((int)Math.Round(h / 0.18f), 6, 40);
        float rise = h / steps, outer = Math.Min(w, d) / 2, inner = 0.08f;
        const float stepAngle = MathF.PI / 8, overlap = 0.04f, tt = 0.06f;
        var steel = set[MaterialKind.Steel];
        steel.AddCylinder(new Vector3(0, -h / 2, 0), new Vector3(0, h / 2 + 0.9f, 0), inner, 16);
        var treads = StairDesign.Construction(e) == StairConstruction.MonolithicSlab ? set[MaterialKind.Concrete] : set[MaterialKind.Timber];
        for (int i = 0; i < steps; i++)
        {
            float a0 = i * stepAngle, a1 = (i + 1) * stepAngle + overlap, top = -h / 2 + (i + 1) * rise;
            var sector = new List<Vector2> { new(inner * MathF.Cos(a0), inner * MathF.Sin(a0)) };
            for (int k = 0; k <= 3; k++)
            {
                float a = a0 + (a1 - a0) * k / 3;
                sector.Add(new Vector2(outer * MathF.Cos(a), outer * MathF.Sin(a)));
            }
            sector.Add(new Vector2(inner * MathF.Cos(a1), inner * MathF.Sin(a1)));
            treads.AddPrism(sector, top - tt, top);
            float am = (a0 + a1) / 2;
            var post = new Vector3((outer - 0.04f) * MathF.Cos(am), top, (outer - 0.04f) * MathF.Sin(am));
            steel.AddCylinder(post, post + new Vector3(0, 0.9f, 0), 0.01f, 6, caps: false);
        }
    }

    /// <summary>Отсечение выпуклого многоугольника (z, y) полосой ymin ≤ y ≤ ymax (Сазерленд — Ходжмен).</summary>
    private static List<Vector2> ClipBandY(List<Vector2> poly, float ymin, float ymax)
    {
        static List<Vector2> Clip(List<Vector2> src, Func<Vector2, float> f)
        {
            var dst = new List<Vector2>();
            for (int i = 0; i < src.Count; i++)
            {
                var a = src[i];
                var b = src[(i + 1) % src.Count];
                float fa = f(a), fb = f(b);
                if (fa >= 0) dst.Add(a);
                if (fa >= 0 != fb >= 0) dst.Add(a + (b - a) * (fa / (fa - fb)));
            }
            return dst;
        }
        return Clip(Clip(poly, p => p.Y - ymin), p => ymax - p.Y);
    }
}
