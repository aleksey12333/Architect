using AIArchitectStudio.Models;

namespace AIArchitectStudio.Services;

/// <summary>
/// Препятствие прогулки: вертикальная призма в плане — выпуклый многоугольник (участок стены, колонна, перегородка, стенка
/// ядра) или круг (круглая колонна, труба) — между отметками Y0…Y1.
/// </summary>
public sealed class WalkObstacle
{
    public required string ElementId { get; init; }
    public required string Type { get; init; }
    public required double Y0 { get; init; }
    public required double Y1 { get; init; }

    /// <summary>Контур в плане (мир, X/Z) для многоугольного препятствия; null — круг.</summary>
    public List<(double X, double Z)>? Outline { get; init; }

    public double CX { get; init; }
    public double CZ { get; init; }

    /// <summary>Радиус круга или радиус описанной окружности контура (быстрый отсев).</summary>
    public double Radius { get; init; }

    public bool IsCircle => Outline is null;
}

/// <summary>
/// Коллизии игрока в режиме прогулки: тело — вертикальная колонна сфер R = 0.25 м от «пол + 0.45 м» (ниже — перешагивается,
/// как ступень) до «глаза + 0.15 м». Монолитные стены, перегородки, витражи, колонны и стенки ядра — препятствия; дверные
/// проёмы (door) и пустые проёмы стен (wall_opening) вырезаются из тела стены по своей ширине и высоте, поэтому камера
/// свободно проходит сквозь них, а над перемычкой и под подоконником (если он выше ступени) — упирается. Окна остаются
/// глухой частью стены. Движение делится на шаги ≤ R / 2 (без туннелирования сквозь тонкие перегородки), на каждом шаге
/// игрок выталкивается из препятствий по нормали ближайшей грани — вдоль стен он скользит.
/// </summary>
public static class WalkCollisionService
{
    public const double PlayerRadius = 0.25;

    /// <summary>Высота, которую игрок перешагивает (ступень, порог) — совпадает с подъёмом пола в прогулке.</summary>
    public const double StepHeight = 0.45;

    /// <summary>Верх тела над глазами (макушка).</summary>
    public const double HeadClearance = 0.15;

    /// <summary>Препятствия видимых элементов сцены (с повторами этажей, закруткой и сужением параметрических элементов).</summary>
    public static List<WalkObstacle> BuildObstacles(IReadOnlyList<ArchitecturalElement> elements)
    {
        var result = new List<WalkObstacle>();
        var openingsByHost = elements.Where(e => e.Type is ElementTypes.Door or ElementTypes.WallOpening && e.HostElementId is not null)
            .GroupBy(e => e.HostElementId!).ToDictionary(g => g.Key, g => g.ToList());
        foreach (var e in elements)
        {
            if (!IsObstacleType(e)) continue;
            int repeat = Math.Max(1, e.RepeatLevels);
            double taper = e.TaperScalePerFloor <= 0 ? 1 : e.TaperScalePerFloor, scale = 1;
            for (int i = 0; i < repeat; i++, scale *= taper)
            {
                double dy = e.LevelHeight * i, rot = e.RotationY + e.TwistAnglePerFloor * i;
                switch (e.Type)
                {
                    case ElementTypes.Wall when e.Polygon2D.Count >= 3:
                        AddPrism(result, e, LocalOutline(e, scale), e.BaseY + dy, e.TopY + dy, rot);
                        break;
                    case ElementTypes.Wall:
                        AddWall(result, e, openingsByHost.GetValueOrDefault(e.Id) ?? [], dy, scale, rot);
                        break;
                    case ElementTypes.Core:
                        AddCore(result, e, dy, scale, rot);
                        break;
                    case ElementTypes.CurtainWall when e.Polygon2D.Count >= 3:
                        // Полигональный витраж — замкнутая лента стекла: каждое ребро контура — тонкая стенка.
                        AddRing(result, e, dy, scale, rot, Math.Max(0.05, Math.Min(e.Depth, 0.3)));
                        break;
                    case ElementTypes.Column when e.Polygon2D.Count < 3 && e.EffectiveProfile is ProfileTypes.Circle or ProfileTypes.Pipe:
                        // Закрутка этажа поворачивает колонну вокруг её собственного центра — круг остаётся на месте.
                        result.Add(new WalkObstacle
                        {
                            ElementId = e.Id, Type = e.Type, Y0 = e.BaseY + dy, Y1 = e.TopY + dy,
                            CX = e.X, CZ = e.Z, Radius = Math.Max(e.Width, e.Depth) * scale / 2
                        });
                        break;
                    default:
                        AddPrism(result, e, LocalOutline(e, scale), e.BaseY + dy, e.TopY + dy, rot);
                        break;
                }
            }
        }
        return result;
    }

    /// <summary>
    /// Препятствия прогулки: стены, перегородки, витражи, колонны, ядро и объекты участка (балки и фермы — если опускаются
    /// в зону тела). Плиты, кровли, лестницы, бассейны, проёмы и заполнения проёмов — не препятствия (по ним ходят или
    /// они уже входят в тело стены).
    /// </summary>
    public static bool IsObstacleType(ArchitecturalElement e) =>
        e.Type is ElementTypes.Wall or ElementTypes.CurtainWall or ElementTypes.Column or ElementTypes.Core or ElementTypes.Asset
        || e.Type == ElementTypes.Beam && e.Polygon2D.Count < 3;

    /// <summary>
    /// Перемещение игрока из (x, z) на (dx, dz) при отметке пола feet: шаги не длиннее R / 2, после каждого — выталкивание
    /// из препятствий, перекрывающих тело по высоте. Blocked — итоговое смещение заметно короче заданного.
    /// </summary>
    public static (double X, double Z, bool Blocked) Move(IReadOnlyList<WalkObstacle> obstacles, double x, double z, double dx, double dz,
        double feet, double eyeHeight = 1.70, double radius = PlayerRadius)
    {
        double length = Math.Sqrt(dx * dx + dz * dz);
        if (!double.IsFinite(length)) return (x, z, true);
        double band0 = feet + StepHeight, band1 = feet + eyeHeight + HeadClearance;
        var near = new List<WalkObstacle>();
        double reach = length + radius + 0.5;
        foreach (var o in obstacles)
        {
            if (o.Y1 <= band0 || o.Y0 >= band1) continue;
            double ddx = o.CX - x, ddz = o.CZ - z, r = o.Radius + reach;
            if (ddx * ddx + ddz * ddz <= r * r) near.Add(o);
        }
        double sx = x, sz = z;
        int steps = Math.Max(1, (int)Math.Ceiling(length / (radius * 0.5)));
        (x, z) = Resolve(near, x, z, radius);
        for (int s = 0; s < steps; s++)
        {
            x += dx / steps;
            z += dz / steps;
            (x, z) = Resolve(near, x, z, radius);
        }
        double moved = Math.Sqrt((x - sx) * (x - sx) + (z - sz) * (z - sz));
        return (x, z, moved < length - 1e-3);
    }

    /// <summary>Выталкивание точки из всех препятствий (до 4 проходов — углы и стыки стен).</summary>
    public static (double X, double Z) Resolve(IReadOnlyList<WalkObstacle> obstacles, double x, double z, double radius = PlayerRadius)
    {
        for (int pass = 0; pass < 4; pass++)
        {
            bool pushed = false;
            foreach (var o in obstacles)
            {
                double ddx = x - o.CX, ddz = z - o.CZ, reach = o.Radius + radius;
                if (ddx * ddx + ddz * ddz >= reach * reach) continue;
                if (o.IsCircle)
                {
                    double d = Math.Sqrt(ddx * ddx + ddz * ddz);
                    if (d < 1e-9) { ddx = 1; ddz = 0; d = 1; }
                    x = o.CX + ddx / d * reach;
                    z = o.CZ + ddz / d * reach;
                    pushed = true;
                    continue;
                }
                var (qx, qz) = ClosestOnBoundary(o.Outline!, x, z);
                bool inside = SceneStateService.PointInPolygon(x, z, o.Outline!);
                double vx = x - qx, vz = z - qz, dist = Math.Sqrt(vx * vx + vz * vz);
                if (!inside && dist >= radius - 1e-9) continue;
                if (dist < 1e-9)
                {
                    // Точно на грани: наружу — от центра препятствия.
                    vx = x - o.CX;
                    vz = z - o.CZ;
                    dist = Math.Max(1e-9, Math.Sqrt(vx * vx + vz * vz));
                }
                double sign = inside ? -1 : 1;
                x = qx + sign * vx / dist * radius;
                z = qz + sign * vz / dist * radius;
                pushed = true;
            }
            if (!pushed) break;
        }
        return (x, z);
    }

    /// <summary>Пересекает ли круг игрока хотя бы одно препятствие тела (проверка инварианта автотеста).</summary>
    public static bool Overlaps(IReadOnlyList<WalkObstacle> obstacles, double x, double z, double feet, double eyeHeight = 1.70, double radius = PlayerRadius)
    {
        double band0 = feet + StepHeight, band1 = feet + eyeHeight + HeadClearance;
        foreach (var o in obstacles)
        {
            if (o.Y1 <= band0 || o.Y0 >= band1) continue;
            if (o.IsCircle)
            {
                if ((x - o.CX) * (x - o.CX) + (z - o.CZ) * (z - o.CZ) < (o.Radius + radius - 1e-6) * (o.Radius + radius - 1e-6)) return true;
                continue;
            }
            if (SceneStateService.PointInPolygon(x, z, o.Outline!)) return true;
            var (qx, qz) = ClosestOnBoundary(o.Outline!, x, z);
            if ((x - qx) * (x - qx) + (z - qz) * (z - qz) < (radius - 1e-6) * (radius - 1e-6)) return true;
        }
        return false;
    }

    // ───────────────────────────── Построение препятствий ─────────────────────────────

    /// <summary>
    /// Стена: вдоль локальной X тело делится проёмами дверей и wall_opening; в пролёте проёма остаются подоконная часть
    /// (низ стены … низ проёма) и перемычка (верх проёма … верх стены) — сквозь сам проём проход свободен.
    /// </summary>
    private static void AddWall(List<WalkObstacle> result, ArchitecturalElement w, List<ArchitecturalElement> openings, double dy, double scale, double rot)
    {
        double half = w.Width * scale / 2, t = w.Depth * scale / 2;
        double y0 = w.BaseY + dy, y1 = w.TopY + dy;
        var gaps = new List<(double A, double B, double Bottom, double Top)>();
        foreach (var o in openings)
        {
            // Повторённый этаж стены режется проёмами того же этажа (проём, привязанный к параметрической стене, — на каждом).
            double oy0 = o.BaseY + (w.RepeatLevels > 1 ? dy : 0), oy1 = o.TopY + (w.RepeatLevels > 1 ? dy : 0);
            if (oy1 <= y0 || oy0 >= y1) continue;
            var (lx, _) = ArchitecturalElement.RotateY(o.X - w.X, o.Z - w.Z, -w.RotationY);
            lx *= scale;
            double a = Math.Max(-half, lx - o.Width * scale / 2), b = Math.Min(half, lx + o.Width * scale / 2);
            if (b - a > 0.02) gaps.Add((a, b, Math.Max(y0, oy0), Math.Min(y1, oy1)));
        }
        gaps.Sort((p, q) => p.A.CompareTo(q.A));
        double cursor = -half;
        void Piece(double a, double b, double bottom, double top)
        {
            if (b - a < 0.005 || top - bottom < 0.005) return;
            AddPrism(result, w, [(a, -t), (b, -t), (b, t), (a, t)], bottom, top, rot);
        }
        foreach (var (a, b, bottom, top) in gaps)
        {
            Piece(cursor, a, y0, y1);
            double from = Math.Max(cursor, a);
            Piece(from, b, y0, bottom);
            Piece(from, b, top, y1);
            cursor = Math.Max(cursor, b);
        }
        Piece(cursor, half, y0, y1);
    }

    /// <summary>Ядро жёсткости — четыре стенки по контуру (толщина как на чертеже ГОСТ), шахта внутри свободна.</summary>
    private static void AddCore(List<WalkObstacle> result, ArchitecturalElement c, double dy, double scale, double rot)
    {
        double hw = c.Width * scale / 2, hd = c.Depth * scale / 2;
        double t = Math.Clamp(Math.Min(c.Width, c.Depth) * scale * 0.06, 0.25, 0.5);
        double y0 = c.BaseY + dy, y1 = c.TopY + dy;
        AddPrism(result, c, [(-hw, -hd), (hw, -hd), (hw, -hd + t), (-hw, -hd + t)], y0, y1, rot);
        AddPrism(result, c, [(-hw, hd - t), (hw, hd - t), (hw, hd), (-hw, hd)], y0, y1, rot);
        AddPrism(result, c, [(-hw, -hd), (-hw + t, -hd), (-hw + t, hd), (-hw, hd)], y0, y1, rot);
        AddPrism(result, c, [(hw - t, -hd), (hw, -hd), (hw, hd), (hw - t, hd)], y0, y1, rot);
    }

    private static void AddRing(List<WalkObstacle> result, ArchitecturalElement e, double dy, double scale, double rot, double thickness)
    {
        var pts = e.Polygon2D.Select(p => (X: p.X * scale, Z: p.Z * scale)).ToList();
        for (int i = 0; i < pts.Count; i++)
        {
            var a = pts[i];
            var b = pts[(i + 1) % pts.Count];
            double len = Math.Sqrt((b.X - a.X) * (b.X - a.X) + (b.Z - a.Z) * (b.Z - a.Z));
            if (len < 1e-6) continue;
            double nx = -(b.Z - a.Z) / len * thickness / 2, nz = (b.X - a.X) / len * thickness / 2;
            AddPrism(result, e, [(a.X - nx, a.Z - nz), (b.X - nx, b.Z - nz), (b.X + nx, b.Z + nz), (a.X + nx, a.Z + nz)], e.BaseY + dy, e.TopY + dy, rot);
        }
    }

    /// <summary>Локальный контур препятствия: Polygon2D (выпуклые части невыпуклого контура) или прямоугольник W × D.</summary>
    private static List<(double X, double Z)> LocalOutline(ArchitecturalElement e, double scale) =>
        e.Polygon2D.Count >= 3
            ? e.Polygon2D.Select(p => (p.X * scale, p.Z * scale)).ToList()
            : [(-e.Width * scale / 2, -e.Depth * scale / 2), (e.Width * scale / 2, -e.Depth * scale / 2), (e.Width * scale / 2, e.Depth * scale / 2), (-e.Width * scale / 2, e.Depth * scale / 2)];

    private static void AddPrism(List<WalkObstacle> result, ArchitecturalElement e, List<(double X, double Z)> local, double y0, double y1, double rot)
    {
        if (local.Count < 3 || y1 - y0 < 1e-6) return;
        // Невыпуклый контур (Г-образная колонна-пилон) — выпуклыми частями: выталкивание по ближайшей грани корректно для каждой.
        var pieces = Geometry2D.IsConvex(local) ? [local] : Geometry2D.ConvexPieces(local);
        foreach (var piece in pieces.Count > 0 ? pieces : [local])
        {
            var world = piece.Select(p =>
            {
                var (rx, rz) = ArchitecturalElement.RotateY(p.X, p.Z, rot);
                return (X: e.X + rx, Z: e.Z + rz);
            }).ToList();
            double cx = world.Average(p => p.X), cz = world.Average(p => p.Z);
            double r = world.Max(p => Math.Sqrt((p.X - cx) * (p.X - cx) + (p.Z - cz) * (p.Z - cz)));
            result.Add(new WalkObstacle { ElementId = e.Id, Type = e.Type, Y0 = y0, Y1 = y1, Outline = world, CX = cx, CZ = cz, Radius = r });
        }
    }

    private static (double X, double Z) ClosestOnBoundary(List<(double X, double Z)> poly, double x, double z)
    {
        double best = double.MaxValue, bx = x, bz = z;
        for (int i = 0; i < poly.Count; i++)
        {
            var a = poly[i];
            var b = poly[(i + 1) % poly.Count];
            double vx = b.X - a.X, vz = b.Z - a.Z, l2 = vx * vx + vz * vz;
            double t = l2 < 1e-12 ? 0 : Math.Clamp(((x - a.X) * vx + (z - a.Z) * vz) / l2, 0, 1);
            double px = a.X + vx * t, pz = a.Z + vz * t;
            double d = (x - px) * (x - px) + (z - pz) * (z - pz);
            if (d < best)
            {
                best = d;
                bx = px;
                bz = pz;
            }
        }
        return (bx, bz);
    }
}
