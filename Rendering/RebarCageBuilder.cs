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

    /// <summary>Защитный слой бетона, м (до хомута у каркаса, до стержня у сетки).</summary>
    public double Cover { get; init; } = StructuralDesign.DefaultCover;

    /// <summary>Каркас построен по явным параметрам режима «Конструкции» (Ø, шаг хомутов, защитный слой).</summary>
    public bool IsExplicit { get; init; }

    /// <summary>Поперечные стержни каркасов продавливания Кр вокруг оголовков колонн (вертикальные шпильки и поясные стержни).</summary>
    public List<Vector3> Punching { get; } = [];

    /// <summary>Вертикальных стержней продавливания (все колонны плиты).</summary>
    public int PunchingBarCount { get; set; }

    /// <summary>Колонн, у которых построены крестообразные каркасы продавливания.</summary>
    public int PunchingZoneCount { get; set; }
}

/// <summary>Оголовок колонны в локальной системе плиты: центр и полуразмеры сечения в плане, м.</summary>
public readonly record struct PunchingColumn(double X, double Z, double HalfX, double HalfZ);

/// <summary>
/// Визуализация 3D-армирования (КЖ): конструктивная схема по габариту локальной геометрии элемента. Колонна — продольные стержни
/// по контуру сечения (шаг ≤ 200 мм, у круглой — по окружности) и хомуты с шагом 15d (100…300 мм); балка — каркас по контуру
/// сечения вдоль длинной оси, хомуты 200 мм; прямоугольная плита, фундамент, терраса, плоская кровля — нижняя и верхняя сетки
/// 200 × 200 мм. Защитный слой 40 мм до хомута. Диаметры — из <see cref="ReinforcementCalculator"/> по K_исп (как в ведомости стали).
/// Стены и ядра (проёмы), полигональные и повторяемые плиты, трубы и двутавры не схематизируются.
/// </summary>
public static class RebarCageBuilder
{

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

    /// <param name="punchingColumns">
    /// Колонны, опирающие плиту, в её локальной системе (<see cref="LocalColumns"/>): при включённом поперечном армировании зоны
    /// продавливания (<see cref="StructuralDesign.HasPunchingRebar"/>) вокруг каждой строится крестообразный каркас Кр.
    /// </param>
    public static RebarCage? Build(ArchitecturalElement e, BimBounds local, double utilization = 0, bool punching = false,
        IReadOnlyList<PunchingColumn>? punchingColumns = null)
    {
        if (!Supports(e)) return null;
        var size = local.Maximum - local.Minimum;
        if (size.X <= 0.05f || size.Y <= 0.05f || size.Z <= 0.05f) return null;

        // Диаметры — по пропорциям ведомости стали (объём 1 м³ задаёт только доли); явные параметры режима «Конструкции» — приоритетнее.
        var steel = ReinforcementCalculator.Compute(e, 1, utilization, punching);
        var layout = StructuralDesign.Layout(e);
        int working = layout is { } l ? (int)Math.Round(l.Diameter * 1000) : steel.Working.Count > 0 ? steel.Working.MaxBy(p => p.Value).Key : 12;
        int stirrup = layout is not null ? (int)Math.Round(StructuralDesign.StirrupBarDiameter * 1000) : steel.A240.Count > 0 ? steel.A240.Keys.Max() : 8;
        double cover = layout?.Cover ?? StructuralDesign.DefaultCover;
        double? spacing = layout is not null ? StructuralDesign.StirrupSpacing(e) : null;
        bool explicitLayout = layout is not null;

        var cage = e.Type switch
        {
            ElementTypes.Column => Frame(local, axis: 1, working, stirrup, spacing ?? Math.Clamp(15 * working / 1000.0, 0.1, 0.3), e.EffectiveProfile == ProfileTypes.Circle, cover, explicitLayout),
            ElementTypes.Beam => Frame(local, axis: LongestAxis(size), working, stirrup, spacing ?? 0.2, round: false, cover, explicitLayout),
            _ => Mesh(local, working, stirrup, both: e.Category == BimCategory.Foundation || size.Y >= 0.12f, cover, explicitLayout ? StructuralDesign.MeshPitch(e) : 0.2, explicitLayout)
        };
        if (cage.IsMesh && punchingColumns is { Count: > 0 } && StructuralDesign.HasPunchingRebar(e))
            AddPunchingCages(cage, local, punchingColumns, StructuralDesign.StirrupSpacing(e) ?? 0.1);
        return cage;
    }

    /// <summary>
    /// Колонны под плитой (и стоящие на ней) в локальной системе плиты: центр — поворот на −RotationY вокруг Position плиты (как
    /// у <see cref="ElementGeometryBuilder.WorldMatrix"/>), сечение W × D — с перестановкой при относительном повороте 90°.
    /// </summary>
    public static List<PunchingColumn> LocalColumns(ArchitecturalElement slab, IEnumerable<ArchitecturalElement> columns)
    {
        var list = new List<PunchingColumn>();
        foreach (var c in columns)
        {
            var (x, z) = ArchitecturalElement.RotateY(c.X - slab.X, c.Z - slab.Z, -slab.RotationY);
            double rel = Math.Abs(((c.RotationY - slab.RotationY) % 180 + 180) % 180);
            bool swap = rel > 45 && rel < 135;
            double hx = Math.Max(0.05, (swap ? c.Depth : c.Width) / 2), hz = Math.Max(0.05, (swap ? c.Width : c.Depth) / 2);
            list.Add(new PunchingColumn(x, z, hx, hz));
        }
        return list;
    }

    /// <summary>
    /// Крестообразные каркасы продавливания Кр (СП 63.13330, п. 8.1.48, 10.3.20): по четырём сторонам оголовка — ряды вертикальных
    /// шпилек Ø8 А240 от нижней до верхней сетки на расстояниях h0/2 … 1,5·h0 от грани колонны с шагом s_w вдоль и поперёк контура u,
    /// полосой шириной стороны колонны; шпильки ряда связаны верхним и нижним поясными стержнями. Зоны за краем плиты отбрасываются.
    /// </summary>
    private static void AddPunchingCages(RebarCage cage, BimBounds box, IReadOnlyList<PunchingColumn> columns, double sw)
    {
        double c = cage.Cover + cage.WorkingDiameter / 1000.0;
        double y0 = box.Minimum.Y + c, y1 = box.Maximum.Y - c;
        if (y1 - y0 < 0.03) return;
        double h0 = Math.Max(0.03, box.Maximum.Y - box.Minimum.Y - cage.Cover - cage.WorkingDiameter / 2000.0);
        sw = Math.Clamp(sw, 0.05, 0.3);
        int rows = Math.Max(2, (int)Math.Floor((1.5 * h0 - 0.5 * h0) / sw + 1e-9) + 1);
        double rowPitch = h0 / Math.Max(1, rows - 1);
        bool Inside(double x, double z) => x > box.Minimum.X + c && x < box.Maximum.X - c && z > box.Minimum.Z + c && z < box.Maximum.Z - c;

        foreach (var col in columns)
        {
            int before = cage.PunchingBarCount;
            // Четыре луча креста: нормаль грани (nx, nz) и полуширина полосы поперёк луча.
            foreach (var (nx, nz) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) })
            {
                double faceHalf = nx != 0 ? col.HalfX : col.HalfZ, stripHalf = nx != 0 ? col.HalfZ : col.HalfX;
                int across = Math.Max(2, (int)Math.Ceiling(2 * stripHalf / sw) + 1);
                for (int j = 0; j < across; j++)
                {
                    double t = -stripHalf + 2 * stripHalf * j / (across - 1);
                    Vector3? first = null, last = null;
                    for (int i = 0; i < rows; i++)
                    {
                        double off = faceHalf + 0.5 * h0 + rowPitch * i;
                        double x = col.X + (nx != 0 ? nx * off : t), z = col.Z + (nz != 0 ? nz * off : t);
                        if (!Inside(x, z)) continue;
                        var bottom = new Vector3((float)x, (float)y0, (float)z);
                        cage.Punching.Add(bottom);
                        cage.Punching.Add(new Vector3((float)x, (float)y1, (float)z));
                        cage.PunchingBarCount++;
                        first ??= bottom;
                        last = bottom;
                    }
                    if (first is { } f && last is { } lb && f != lb)
                    {
                        // Поясные стержни ряда (каркас Кр): по низу и по верху шпилек.
                        cage.Punching.Add(f);
                        cage.Punching.Add(lb);
                        cage.Punching.Add(f with { Y = (float)y1 });
                        cage.Punching.Add(lb with { Y = (float)y1 });
                    }
                }
            }
            if (cage.PunchingBarCount > before) cage.PunchingZoneCount++;
        }
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
    private static RebarCage Frame(BimBounds box, int axis, int working, int stirrup, double step, bool round, double cover, bool isExplicit)
    {
        var cage = new RebarCage { WorkingDiameter = working, StirrupDiameter = stirrup, Step = step, Cover = cover, IsExplicit = isExplicit };
        int ua = (axis + 1) % 3, va = (axis + 2) % 3;
        double a0 = Get(box.Minimum, axis), a1 = Get(box.Maximum, axis);
        double uMin = Get(box.Minimum, ua), uMax = Get(box.Maximum, ua), vMin = Get(box.Minimum, va), vMax = Get(box.Maximum, va);
        double uc = (uMin + uMax) / 2, vc = (vMin + vMax) / 2;
        double ds = stirrup / 1000.0, dw = working / 1000.0;
        // Хомут — на защитном слое, ось рабочего стержня — внутри хомута.
        double hs = Math.Max(0.02, (uMax - uMin) / 2 - cover - ds / 2), vs = Math.Max(0.02, (vMax - vMin) / 2 - cover - ds / 2);
        double hb = Math.Max(0.01, hs - ds / 2 - dw / 2), vb = Math.Max(0.01, vs - ds / 2 - dw / 2);
        double end0 = a0 + cover, end1 = a1 - cover;

        var section = new List<(double U, double V)>();
        if (round)
        {
            double r = Math.Min(hb, vb);
            int n = StructuralDesign.RoundBarCount(r);
            for (int i = 0; i < n; i++)
                section.Add((uc + r * Math.Cos(2 * Math.PI * i / n), vc + r * Math.Sin(2 * Math.PI * i / n)));
        }
        else
        {
            int nu = StructuralDesign.RowBarCount(2 * hb), nv = StructuralDesign.RowBarCount(2 * vb);
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
    private static RebarCage Mesh(BimBounds box, int working, int stirrup, bool both, double cover, double step, bool isExplicit)
    {
        var cage = new RebarCage { WorkingDiameter = working, StirrupDiameter = stirrup, Step = step, IsMesh = true, Cover = cover, IsExplicit = isExplicit };
        double c = cover + working / 2000.0;
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
