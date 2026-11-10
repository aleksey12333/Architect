using AIArchitectStudio.Models;
using AIArchitectStudio.Rendering;

namespace AIArchitectStudio.Services;

/// <summary>Состояние наблюдателя: положение в плане, отметка ног, вертикальная скорость (падение), последний упор.</summary>
public readonly record struct WalkState(double X, double Z, double Feet, double VerticalSpeed = 0)
{
    public double Eye => Feet + WalkPhysicsService.EyeHeight;
}

/// <summary>Итог шага: новое состояние, упор (стена, колонна, закрытая дверь), вектор скольжения вдоль поверхности.</summary>
public readonly record struct WalkStepResult(WalkState State, bool Blocked, string? BlockedBy, string? BlockedType, double SlideX, double SlideZ, bool OnStairs);

/// <summary>Поверхность пола прогулки: плита / фундамент / терраса / плоская кровля (контур, отверстия, повторы) или марш.</summary>
public sealed record WalkFloorSurface(ArchitecturalElement Element, List<(double X, double Z)> Outline, List<List<(double X, double Z)>> Holes, bool IsStairs, bool IsSpiral);

/// <summary>
/// Мир физического прохода: препятствия (стены по участкам между проёмами, колонны, ядро, витражи, парапеты и закрытые двери),
/// поверхности пола (плиты с отверстиями, ступени маршей — дискретно по подступенкам, винтовые ступени по углу, рельеф участка).
/// Строится по снимку видимых элементов; открытые двери — набор Id (клавиша E / клик).
/// </summary>
public sealed class WalkPhysicsWorld
{
    public required List<WalkObstacle> Obstacles { get; init; }
    public required List<WalkFloorSurface> Surfaces { get; init; }
    public required IReadOnlyList<ArchitecturalElement> Doors { get; init; }
    public HashSet<string> OpenDoors { get; init; } = new(StringComparer.Ordinal);

    /// <summary>Рельеф участка: высота земли в точке (м, 0 — нулевая отметка); null — плоская земля Y = 0.</summary>
    public Func<double, double, double>? Ground { get; init; }

    /// <summary>Препятствия с учётом состояния дверей (закрытые — полотно в проёме).</summary>
    public List<WalkObstacle> ActiveObstacles { get; private set; } = [];

    internal void RefreshDoors()
    {
        var list = new List<WalkObstacle>(Obstacles);
        foreach (var d in Doors)
        {
            if (OpenDoors.Contains(d.Id)) continue;
            // Полотно закрытой двери — тонкая призма по оси проёма на всю его высоту (порог перешагивается).
            double hw = d.Width / 2, t = Math.Max(0.05, Math.Min(d.Depth, 0.12)) / 2;
            var local = new[] { (-hw, -t), (hw, -t), (hw, t), (-hw, t) };
            var world = local.Select(p =>
            {
                var (rx, rz) = ArchitecturalElement.RotateY(p.Item1, p.Item2, d.RotationY);
                return (X: d.X + rx, Z: d.Z + rz);
            }).ToList();
            list.Add(new WalkObstacle
            {
                ElementId = d.Id, Type = ElementTypes.Door, Y0 = d.BaseY, Y1 = d.TopY, Outline = world,
                CX = d.X, CZ = d.Z, Radius = Math.Sqrt(hw * hw + t * t)
            });
        }
        ActiveObstacles = list;
    }
}

/// <summary>
/// Волна 6 — физический режим «Проход»: капсула человека (радиус плеч R = 0,28 м, глаза 1,70 м, макушка 1,85 м). На каждом шаге
/// движение делится на отрезки ≤ R/2, капсула выталкивается из препятствий по нормали ближайшей грани (скольжение вдоль стены —
/// касательная составляющая сохраняется), закрытая дверь — препятствие, открытая и пустые проёмы — проход. Ноги стоят на
/// наивысшей поверхности не выше «ноги + 0,22 м» (подступенок марша преодолевается автоматически; выше — упор, как в стену),
/// ступени прямого марша — по подступенкам вдоль локальной +Z, винтовые — по углу (шаг 22,5°), ниже — плавное падение
/// (гравитация 9,81 м/с²). Чистая логика без вьюпорта: её использует прогулка и автотест.
/// </summary>
public static class WalkPhysicsService
{
    public const double Radius = 0.28;
    public const double EyeHeight = 1.70;
    public const double HeadHeight = 1.85;

    /// <summary>Наибольшая ступень, преодолеваемая без прыжка (подступенок ≤ 0,22 м).</summary>
    public const double MaxStep = 0.22;

    /// <summary>Нижняя граница тела для препятствий (колени): всё ниже — ступени и пороги.</summary>
    public const double KneeHeight = 0.30;

    public const double Gravity = 9.81;

    /// <summary>Радиус действия «E — открыть / закрыть дверь» (м) и сектор перед взглядом (косинус угла 60°).</summary>
    public const double DoorReach = 2.2;

    public static WalkPhysicsWorld Build(IReadOnlyList<ArchitecturalElement> elements, Func<double, double, double>? ground = null, IEnumerable<string>? openDoors = null)
    {
        var obstacles = WalkCollisionService.BuildObstacles(elements);
        // Парапеты кровли — тоже ограждение (поверх кровли пройти к краю нельзя).
        var surfaces = new List<WalkFloorSurface>();
        var openingsByHost = elements.Where(e => e.Type == ElementTypes.SlabOpening).ToList();
        foreach (var e in elements)
        {
            if (e.Type == ElementTypes.Stairs)
            {
                surfaces.Add(new WalkFloorSurface(e, [], [], true, StairDesign.IsSpiral(e)));
                continue;
            }
            if (e.Type is not (ElementTypes.Slab or ElementTypes.CantileverSlab or ElementTypes.Foundation or ElementTypes.Terrace) && !e.IsFlatRoof) continue;
            var holes = SceneStateService.OpeningsFor(e, openingsByHost).Select(ElementGeometryBuilder.WorldOutline).ToList();
            surfaces.Add(new WalkFloorSurface(e, ElementGeometryBuilder.WorldOutline(e), holes, false, false));
        }
        var world = new WalkPhysicsWorld
        {
            Obstacles = obstacles,
            Surfaces = surfaces,
            Doors = elements.Where(e => e.Type == ElementTypes.Door).ToList(),
            Ground = ground
        };
        if (openDoors is not null) foreach (var id in openDoors) world.OpenDoors.Add(id);
        world.RefreshDoors();
        return world;
    }

    /// <summary>Отметка пола под точкой: наивысшая поверхность не выше feet + reach (ступени — дискретно), иначе земля.</summary>
    public static double FloorAt(WalkPhysicsWorld world, double x, double z, double feet, double reach = MaxStep) => FloorAt(world, x, z, feet, reach, out _);

    public static double FloorAt(WalkPhysicsWorld world, double x, double z, double feet, double reach, out bool onStairs)
    {
        onStairs = false;
        double best = world.Ground?.Invoke(x, z) ?? 0;
        if (best > feet + reach) best = double.NegativeInfinity;
        foreach (var s in world.Surfaces)
        {
            var e = s.Element;
            if (s.IsStairs)
            {
                if (StairTop(e, s.IsSpiral, x, z, feet + reach) is { } top && top > best)
                {
                    best = top;
                    onStairs = true;
                }
                continue;
            }
            if (!SceneStateService.PointInPolygon(x, z, s.Outline) || s.Holes.Any(h => SceneStateService.PointInPolygon(x, z, h))) continue;
            int repeat = Math.Max(1, e.RepeatLevels);
            for (int i = 0; i < repeat; i++)
            {
                double top = e.TopY + e.LevelHeight * i;
                if (top > feet + reach + 1e-6) break;
                if (top > best)
                {
                    best = top;
                    onStairs = false;
                }
            }
        }
        return double.IsNegativeInfinity(best) ? (world.Ground?.Invoke(x, z) ?? 0) : best;
    }

    /// <summary>
    /// Верх ступени под точкой (не выше limit): прямой марш — проступь k вдоль локальной +Z, верх (k + 1)·подступенок от низа марша;
    /// за верхним торцом (0,3 м) — верх марша; винтовой — ступень по углу от локальной +X к +Z, шаг π/8.
    /// </summary>
    public static double? StairTop(ArchitecturalElement e, bool spiral, double x, double z, double limit)
    {
        var (lx, lz) = ArchitecturalElement.RotateY(x - e.X, z - e.Z, -e.RotationY);
        double h = e.Height;
        if (spiral)
        {
            int steps = e.StepCount > 0 ? Math.Clamp(e.StepCount, 3, 60) : Math.Clamp((int)Math.Round(h / 0.18), 6, 40);
            double rise = h / steps, outer = Math.Min(e.Width, e.Depth) / 2, r = Math.Sqrt(lx * lx + lz * lz);
            if (r > outer + 0.05) return null;
            double a = Math.Atan2(lz, lx);
            if (a < 0) a += 2 * Math.PI;
            double? best = null;
            for (int turn = 0; turn * 16 < steps + 16; turn++)
            {
                int i = (int)Math.Floor((a + turn * 2 * Math.PI) / (Math.PI / 8));
                if (i < 0 || i >= steps) continue;
                double top = e.BaseY + (i + 1) * rise;
                if (top <= limit + 1e-6 && (best is null || top > best)) best = top;
            }
            // Стойка в центре — опора ног тоже (r < 0,1 м), верх последней ступени — выход на площадку.
            return best;
        }
        if (Math.Abs(lx) > e.Width / 2 + 1e-6) return null;
        double d = Math.Max(0.01, e.Depth);
        double s = lz + d / 2;
        if (s < 0 || s > d + 0.3) return null;
        int n = e.StepCount > 0 ? e.StepCount : Math.Max(1, (int)Math.Round(h / 0.17));
        double riser = h / n, tread = d / n;
        int k = Math.Min(n - 1, (int)Math.Floor(s / tread));
        double stepTop = s > d ? e.BaseY + h : e.BaseY + (k + 1) * riser;
        return stepTop <= limit + 1e-6 ? stepTop : null;
    }

    /// <summary>
    /// Шаг движения на (dx, dz) за время dt: отрезки ≤ R/2, после каждого — выталкивание капсулы из препятствий её высоты
    /// (колени … макушка); подъём на поверхность выше ног + 0,22 м — упор; ниже — падение с ускорением g.
    /// </summary>
    public static WalkStepResult Step(WalkPhysicsWorld world, WalkState state, double dx, double dz, double dt)
    {
        double length = Math.Sqrt(dx * dx + dz * dz);
        double x = state.X, z = state.Z, feet = state.Feet;
        bool blocked = false;
        string? by = null, byType = null;
        if (length > 1e-9 && double.IsFinite(length))
        {
            int steps = Math.Max(1, (int)Math.Ceiling(length / (Radius * 0.5)));
            double sx = dx / steps, sz = dz / steps;
            for (int i = 0; i < steps; i++)
            {
                double nx = x + sx, nz = z + sz;
                var near = Near(world, nx, nz, feet);
                var (rx, rz) = WalkCollisionService.Resolve(near, nx, nz, Radius);
                if (Math.Abs(rx - nx) + Math.Abs(rz - nz) > 1e-6)
                {
                    blocked = true;
                    var hit = Closest(near, nx, nz);
                    by ??= hit?.ElementId;
                    byType ??= hit?.Type;
                }
                // Ступень выше 0,22 м (подиум, бок марша, край плиты над головой) — упор, как в стену.
                if (Obstructs(world, rx, rz, feet, out var block))
                {
                    blocked = true;
                    by ??= block?.Id;
                    byType ??= block?.Type;
                    break;
                }
                double floor = FloorAt(world, rx, rz, feet, MaxStep);
                x = rx;
                z = rz;
                if (floor > feet && floor - feet <= MaxStep + 1e-6) feet = floor;
            }
        }
        // Вертикаль: подъём по ступени — сразу, вниз — падение (короткий спуск по ступени — тоже сразу).
        double ground = FloorAt(world, x, z, feet, MaxStep, out bool onStairs);
        double vy = state.VerticalSpeed;
        if (ground >= feet - 1e-6)
        {
            feet = ground;
            vy = 0;
        }
        else if (feet - ground <= MaxStep + 1e-6)
        {
            feet = ground;
            vy = 0;
        }
        else
        {
            vy -= Gravity * dt;
            feet = Math.Max(ground, feet + vy * dt);
            if (feet <= ground + 1e-6) vy = 0;
        }
        double moved = Math.Sqrt((x - state.X) * (x - state.X) + (z - state.Z) * (z - state.Z));
        if (moved < length - 1e-3) blocked = true;
        return new WalkStepResult(new WalkState(x, z, feet, vy), blocked, by, byType, x - state.X, z - state.Z, onStairs);
    }

    /// <summary>
    /// Поверхность в полосе тела (верх выше ног + 0,22 м, низ ниже макушки): плита-подиум, ступень марша не по ходу, край плиты
    /// над головой у лестничного проёма — упор.
    /// </summary>
    public static bool Obstructs(WalkPhysicsWorld world, double x, double z, double feet, out ArchitecturalElement? by)
    {
        by = null;
        double low = feet + MaxStep + 1e-6, high = feet + HeadHeight;
        foreach (var s in world.Surfaces)
        {
            var e = s.Element;
            if (s.IsStairs)
            {
                if (RawStairTop(e, s.IsSpiral, x, z, feet) is { } top && top > low && e.BaseY < high && top - (s.IsSpiral ? 0.06 : top - e.BaseY) < high)
                {
                    by = e;
                    return true;
                }
                continue;
            }
            if (!SceneStateService.PointInPolygon(x, z, s.Outline) || s.Holes.Any(h => SceneStateService.PointInPolygon(x, z, h))) continue;
            int repeat = Math.Max(1, e.RepeatLevels);
            for (int i = 0; i < repeat; i++)
            {
                double top = e.TopY + e.LevelHeight * i, bottom = e.BaseY + e.LevelHeight * i;
                if (top > low && bottom < high)
                {
                    by = e;
                    return true;
                }
            }
        }
        return false;
    }

    /// <summary>Верх ступени под точкой без ограничения (для упора): прямой марш — по проступи; винтовой — ближайшая выше ног ступень по углу.</summary>
    private static double? RawStairTop(ArchitecturalElement e, bool spiral, double x, double z, double feet)
    {
        var (lx, lz) = ArchitecturalElement.RotateY(x - e.X, z - e.Z, -e.RotationY);
        double h = e.Height;
        if (spiral)
        {
            int steps = e.StepCount > 0 ? Math.Clamp(e.StepCount, 3, 60) : Math.Clamp((int)Math.Round(h / 0.18), 6, 40);
            double rise = h / steps, outer = Math.Min(e.Width, e.Depth) / 2;
            if (Math.Sqrt(lx * lx + lz * lz) > outer + 0.05) return null;
            double a = Math.Atan2(lz, lx);
            if (a < 0) a += 2 * Math.PI;
            double? best = null;
            for (int turn = 0; turn * 16 < steps + 16; turn++)
            {
                int i = (int)Math.Floor((a + turn * 2 * Math.PI) / (Math.PI / 8));
                if (i < 0 || i >= steps) continue;
                double top = e.BaseY + (i + 1) * rise;
                if (top > feet + MaxStep && (best is null || top < best)) best = top;
            }
            return best;
        }
        if (Math.Abs(lx) > e.Width / 2 + 1e-6) return null;
        double d = Math.Max(0.01, e.Depth), s = lz + d / 2;
        if (s < 0 || s > d) return null;
        int n = e.StepCount > 0 ? e.StepCount : Math.Max(1, (int)Math.Round(h / 0.17));
        int k = Math.Min(n - 1, (int)Math.Floor(s / (d / n)));
        return e.BaseY + (k + 1) * h / n;
    }

    private static List<WalkObstacle> Near(WalkPhysicsWorld world, double x, double z, double feet)
    {
        double band0 = feet + KneeHeight, band1 = feet + HeadHeight;
        var near = new List<WalkObstacle>();
        foreach (var o in world.ActiveObstacles)
        {
            if (o.Y1 <= band0 || o.Y0 >= band1) continue;
            double ddx = o.CX - x, ddz = o.CZ - z, r = o.Radius + Radius + 0.5;
            if (ddx * ddx + ddz * ddz <= r * r) near.Add(o);
        }
        return near;
    }

    private static WalkObstacle? Closest(List<WalkObstacle> near, double x, double z) =>
        near.OrderBy(o => (o.CX - x) * (o.CX - x) + (o.CZ - z) * (o.CZ - z) - o.Radius * o.Radius).FirstOrDefault();

    /// <summary>Капсула пересекает препятствие (проверка инварианта: сквозь стену не прошли).</summary>
    public static bool Overlaps(WalkPhysicsWorld world, WalkState s)
    {
        var (x, z) = WalkCollisionService.Resolve(Near(world, s.X, s.Z, s.Feet), s.X, s.Z, Radius - 1e-3);
        return Math.Abs(x - s.X) + Math.Abs(z - s.Z) > 1e-4;
    }

    /// <summary>Дверь перед наблюдателем (в пределах 2,2 м и сектора ±60° от взгляда) — ближайшая.</summary>
    public static ArchitecturalElement? DoorAhead(WalkPhysicsWorld world, WalkState s, double yaw)
    {
        double fx = Math.Sin(yaw), fz = -Math.Cos(yaw);
        ArchitecturalElement? best = null;
        double bestD = double.MaxValue;
        foreach (var d in world.Doors)
        {
            if (d.TopY < s.Feet || d.BaseY > s.Feet + HeadHeight) continue;
            double vx = d.X - s.X, vz = d.Z - s.Z, dist = Math.Sqrt(vx * vx + vz * vz);
            if (dist > DoorReach || dist < 1e-6) continue;
            if ((vx * fx + vz * fz) / dist < 0.5 && dist > 0.7) continue;
            if (dist < bestD) { bestD = dist; best = d; }
        }
        return best;
    }

    /// <summary>Открыть / закрыть дверь; true — теперь открыта.</summary>
    public static bool ToggleDoor(WalkPhysicsWorld world, ArchitecturalElement door)
    {
        bool open = world.OpenDoors.Add(door.Id) || !world.OpenDoors.Remove(door.Id);
        world.RefreshDoors();
        return open;
    }
}
