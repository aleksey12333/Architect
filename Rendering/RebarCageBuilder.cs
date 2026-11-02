using AIArchitectStudio.Models;
using AIArchitectStudio.Services;
using System.Numerics;
using AIArchitectStudio.Models.Geometry;

namespace AIArchitectStudio.Rendering;

/// <summary>Схема армирования элемента в его локальной системе: отрезки рабочих стержней и хомутов (пары точек).</summary>
public sealed class RebarCage
{
    public List<Vector3> Working { get; } = [];
    public List<Vector3> Stirrups { get; } = [];
    public int WorkingDiameter { get; init; }
    public int StirrupDiameter { get; init; }

    /// <summary>Рабочих стержней (колонна, балка) или стержней сетки в одном направлении (плита, фундамент).</summary>
    public int BarCount { get; set; }

    /// <summary>Шаг хомутов (колонна, балка) или стержней сетки (плита, фундамент), м.</summary>
    public double Step { get; init; }

    /// <summary>Сетка плиты или фундамента (иначе — пространственный каркас).</summary>
    public bool IsMesh { get; init; }
}

/// <summary>
/// Визуализация 3D-армирования (КЖ): конструктивная схема по габариту локальной геометрии элемента. Колонна — продольные стержни
/// по контуру сечения (шаг ≤ 200 мм, у круглой — по окружности) и хомуты с шагом 15d (100…300 мм); балка — каркас по контуру
/// сечения вдоль длинной оси, хомуты 200 мм; прямоугольная плита, фундамент, терраса, плоская кровля — нижняя и верхняя сетки
/// 200 × 200 мм. Защитный слой 40 мм до хомута. Диаметры — из <see cref="ReinforcementCalculator"/> по K_исп (как в ведомости стали).
/// Стены и ядра (проёмы), полигональные и повторяемые плиты, трубы и двутавры не схематизируются.
/// </summary>
public static class RebarCageBuilder
{
    private const double Cover = 0.04;

    public static bool Supports(ArchitecturalElement e)
    {
        if (!ReinforcementCalculator.IsReinforced(e)) return false;
        return e.Type switch
        {
            ElementTypes.Column => e.EffectiveProfile is ProfileTypes.Rectangle or ProfileTypes.Circle,
            ElementTypes.Beam => e.Polygon2D.Count < 3 && ProfileTypes.Normalize(e.ProfileType) == ProfileTypes.Rectangle,
            ElementTypes.Slab or ElementTypes.Foundation or ElementTypes.Terrace or ElementTypes.Roof or ElementTypes.CantileverSlab =>
                e.Polygon2D.Count < 3 && Math.Max(1, e.RepeatLevels) == 1 && e.TwistAnglePerFloor == 0,
            _ => false
        };
    }

    public static RebarCage? Build(ArchitecturalElement e, BimBounds local, double utilization = 0, bool punching = false)
    {
        if (!Supports(e)) return null;
        var size = local.Maximum - local.Minimum;
        if (size.X <= 0.05f || size.Y <= 0.05f || size.Z <= 0.05f) return null;

        // Диаметры — по пропорциям ведомости стали (объём 1 м³ задаёт только доли).
        var steel = ReinforcementCalculator.Compute(e, 1, utilization, punching);
        int working = steel.Working.Count > 0 ? steel.Working.MaxBy(p => p.Value).Key : 12;
        int stirrup = steel.A240.Count > 0 ? steel.A240.Keys.Max() : 8;

        return e.Type switch
        {
            ElementTypes.Column => Frame(local, axis: 1, working, stirrup, Math.Clamp(15 * working / 1000.0, 0.1, 0.3), e.EffectiveProfile == ProfileTypes.Circle),
            ElementTypes.Beam => Frame(local, axis: LongestAxis(size), working, stirrup, 0.2, round: false),
            _ => Mesh(local, working, stirrup, both: e.Category == BimCategory.Foundation || size.Y >= 0.12f)
        };
    }

    private static int LongestAxis(Vector3 size) => size.X >= size.Y && size.X >= size.Z ? 0 : size.Z >= size.Y ? 2 : 1;

    private static float Get(Vector3 v, int axis) => axis == 0 ? v.X : axis == 1 ? v.Y : v.Z;

    /// <summary>Точка по координате вдоль оси стержня (along) и двум координатам сечения (u, v — следующие оси по кругу).</summary>
    private static Vector3 Point(int axis, double along, double u, double v) => axis switch
    {
        0 => new Vector3((float)along, (float)u, (float)v),
        1 => new Vector3((float)v, (float)along, (float)u),
        _ => new Vector3((float)u, (float)v, (float)along)
    };

    /// <summary>Пространственный каркас вдоль оси axis: стержни по контуру сечения и замкнутые хомуты.</summary>
    private static RebarCage Frame(BimBounds box, int axis, int working, int stirrup, double step, bool round)
    {
        var cage = new RebarCage { WorkingDiameter = working, StirrupDiameter = stirrup, Step = step };
        int ua = (axis + 1) % 3, va = (axis + 2) % 3;
        double a0 = Get(box.Minimum, axis), a1 = Get(box.Maximum, axis);
        double uMin = Get(box.Minimum, ua), uMax = Get(box.Maximum, ua), vMin = Get(box.Minimum, va), vMax = Get(box.Maximum, va);
        double uc = (uMin + uMax) / 2, vc = (vMin + vMax) / 2;
        double ds = stirrup / 1000.0, dw = working / 1000.0;
        // Хомут — на защитном слое, ось рабочего стержня — внутри хомута.
        double hs = Math.Max(0.02, (uMax - uMin) / 2 - Cover - ds / 2), vs = Math.Max(0.02, (vMax - vMin) / 2 - Cover - ds / 2);
        double hb = Math.Max(0.01, hs - ds / 2 - dw / 2), vb = Math.Max(0.01, vs - ds / 2 - dw / 2);
        double end0 = a0 + Cover, end1 = a1 - Cover;

        var section = new List<(double U, double V)>();
        if (round)
        {
            double r = Math.Min(hb, vb);
            int n = Math.Max(6, (int)Math.Round(2 * Math.PI * r / 0.15));
            for (int i = 0; i < n; i++)
                section.Add((uc + r * Math.Cos(2 * Math.PI * i / n), vc + r * Math.Sin(2 * Math.PI * i / n)));
        }
        else
        {
            int nu = Math.Max(2, (int)Math.Ceiling(2 * hb / 0.2) + 1), nv = Math.Max(2, (int)Math.Ceiling(2 * vb / 0.2) + 1);
            for (int i = 0; i < nu; i++)
            {
                double u = uc - hb + 2 * hb * i / (nu - 1);
                section.Add((u, vc - vb));
                section.Add((u, vc + vb));
            }
            for (int j = 1; j < nv - 1; j++)
            {
                double v = vc - vb + 2 * vb * j / (nv - 1);
                section.Add((uc - hb, v));
                section.Add((uc + hb, v));
            }
        }
        foreach (var (u, v) in section)
        {
            cage.Working.Add(Point(axis, end0, u, v));
            cage.Working.Add(Point(axis, end1, u, v));
        }
        cage.BarCount = section.Count;

        int count = Math.Max(2, (int)Math.Floor((end1 - end0) / step) + 1);
        double pitch = (end1 - end0) / (count - 1);
        for (int k = 0; k < count; k++)
        {
            double a = end0 + pitch * k;
            if (round)
            {
                double r = Math.Min(hs, vs);
                const int segments = 20;
                for (int i = 0; i < segments; i++)
                {
                    double t0 = 2 * Math.PI * i / segments, t1 = 2 * Math.PI * (i + 1) / segments;
                    cage.Stirrups.Add(Point(axis, a, uc + r * Math.Cos(t0), vc + r * Math.Sin(t0)));
                    cage.Stirrups.Add(Point(axis, a, uc + r * Math.Cos(t1), vc + r * Math.Sin(t1)));
                }
            }
            else
            {
                var p00 = Point(axis, a, uc - hs, vc - vs);
                var p10 = Point(axis, a, uc + hs, vc - vs);
                var p11 = Point(axis, a, uc + hs, vc + vs);
                var p01 = Point(axis, a, uc - hs, vc + vs);
                cage.Stirrups.AddRange([p00, p10, p10, p11, p11, p01, p01, p00]);
            }
        }
        return cage;
    }

    /// <summary>Сетки плиты/фундамента 200 × 200 мм: нижняя и (при h ≥ 120 мм или у фундамента) верхняя.</summary>
    private static RebarCage Mesh(BimBounds box, int working, int stirrup, bool both)
    {
        const double step = 0.2;
        var cage = new RebarCage { WorkingDiameter = working, StirrupDiameter = stirrup, Step = step, IsMesh = true };
        double c = Cover + working / 2000.0;
        double x0 = box.Minimum.X + c, x1 = box.Maximum.X - c, z0 = box.Minimum.Z + c, z1 = box.Maximum.Z - c;
        if (x1 <= x0 || z1 <= z0) return cage;
        var levels = new List<double> { box.Minimum.Y + c };
        if (both && box.Maximum.Y - c - working / 1000.0 > levels[0]) levels.Add(box.Maximum.Y - c);
        int nx = (int)Math.Floor((x1 - x0) / step) + 1, nz = (int)Math.Floor((z1 - z0) / step) + 1;
        foreach (double y in levels)
        {
            // Стержни вдоль X — нижний ряд, вдоль Z — над ними (на диаметр выше).
            float yz = (float)(y + working / 1000.0 * (y > levels[0] ? -1 : 1));
            for (int j = 0; j < nz; j++)
            {
                float z = (float)(z0 + j * step);
                cage.Working.Add(new Vector3((float)x0, (float)y, z));
                cage.Working.Add(new Vector3((float)x1, (float)y, z));
            }
            for (int i = 0; i < nx; i++)
            {
                float x = (float)(x0 + i * step);
                cage.Working.Add(new Vector3(x, yz, (float)z0));
                cage.Working.Add(new Vector3(x, yz, (float)z1));
            }
        }
        cage.BarCount = nx + nz;
        return cage;
    }
}
