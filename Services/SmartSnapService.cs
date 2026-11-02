using AIArchitectStudio.Models;
using AIArchitectStudio.Rendering;

namespace AIArchitectStudio.Services;

/// <summary>Вид объектной привязки (OSnap).</summary>
public enum SnapKind
{
    GridIntersection,
    GridAxis,
    ColumnCenter,
    ColumnFace,
    WallEndpoint,
    WallCorner,
    WallMidpoint,
    WallFace,
    SlabEdge,
    Elevation,

    /// <summary>Вершина или середина ребра 3D-габарита элемента (рулетка: экранная привязка к углам).</summary>
    Vertex
}

/// <summary>Форма цели: точка в плане, отрезок в плане или горизонтальная отметка (для перемещения по Y).</summary>
public enum SnapShape
{
    Point,
    Line,
    Elevation
}

/// <summary>Цель привязки: точка (AX, AZ), отрезок (AX, AZ) → (BX, BZ) или отметка Y; направления — для отрисовки направляющих.</summary>
public sealed record SnapTarget(SnapKind Kind, SnapShape Shape, string Label, double AX, double AZ, double BX = 0, double BZ = 0, double Y = 0)
{
    /// <summary>Привязка к координационным осям (синяя направляющая), иначе — к геометрии элементов (оранжевая).</summary>
    public bool IsAxis => Kind is SnapKind.GridIntersection or SnapKind.GridAxis;

    /// <summary>Направления осей, проходящих через цель (для пересечения осей — обе).</summary>
    public IReadOnlyList<(double X, double Z)> GuideDirections { get; init; } = [];

    /// <summary>Приоритет при равном расстоянии: узлы осей, торцы и центры — раньше граней и кромок.</summary>
    public int Priority => Kind switch
    {
        SnapKind.GridIntersection => 0,
        SnapKind.WallEndpoint or SnapKind.ColumnCenter or SnapKind.Vertex => 1,
        SnapKind.WallCorner or SnapKind.WallMidpoint or SnapKind.Elevation => 2,
        SnapKind.SlabEdge when Shape == SnapShape.Point => 2,
        SnapKind.GridAxis or SnapKind.ColumnFace => 3,
        _ => 4
    };
}

/// <summary>Сработавшая привязка: скорректированное смещение, точка элемента после привязки и точка касания цели.</summary>
public sealed record SnapHit(SnapTarget Target, double Delta, (double X, double Y, double Z) Anchor, (double X, double Y, double Z) Contact, double Distance)
{
    public string Label => Target.Label;
    public bool IsAxis => Target.IsAxis;
}

/// <summary>
/// Умные магнитные привязки (OSnap) для Gizmo и ручек Push/Pull: узлы и линии координационных осей, центры и грани колонн,
/// торцы, углы, середины и грани стен и балок, края плит; при перемещении по вертикали — отметки этажей, верх и низ плит.
/// Цели собираются один раз в начале перетаскивания (SnapContext), поиск — по всем точкам перетаскиваемого элемента вдоль оси
/// в радиусе 0.25 м.
/// </summary>
public static class SmartSnapService
{
    /// <summary>Радиус захвата магнитной привязки, м.</summary>
    public const double Radius = 0.25;

    /// <summary>Шаг без привязки (OSnap выключен), м.</summary>
    public const double DefaultStep = 0.1;

    /// <summary>Точный шаг при зажатом Alt (привязки временно отключены), м.</summary>
    public const double FineStep = 0.01;

    /// <summary>Цели привязки сцены: оси построек, колонны, стены, балки, плиты (кроме исключённых — перетаскиваемых — элементов).</summary>
    public static List<SnapTarget> BuildTargets(IReadOnlyList<ArchitecturalElement> elements, IReadOnlyList<StructuralGridAxis> axes,
        ISet<string>? exclude = null, bool english = false)
    {
        var targets = new List<SnapTarget>();
        string axisWord = english ? "Axis" : "Ось";

        foreach (var grid in StructuralAxesBuilder.BuildGrids(elements, axes))
        {
            var nums = grid.NumericAxes.ToList();
            var lets = grid.LetterAxes.ToList();
            foreach (var a in grid.Axes)
            {
                // Цифровая ось — линия x = Coordinate вдоль Z, буквенная — z = Coordinate вдоль X.
                targets.Add(a.IsNumeric
                    ? new SnapTarget(SnapKind.GridAxis, SnapShape.Line, $"{axisWord} {a.Label}", a.Coordinate, a.From, a.Coordinate, a.To) { GuideDirections = [(0, 1)] }
                    : new SnapTarget(SnapKind.GridAxis, SnapShape.Line, $"{axisWord} {a.Label}", a.From, a.Coordinate, a.To, a.Coordinate) { GuideDirections = [(1, 0)] });
            }
            foreach (var n in nums)
                foreach (var l in lets)
                    targets.Add(new SnapTarget(SnapKind.GridIntersection, SnapShape.Point, $"{axisWord} {n.Label} / {l.Label}", n.Coordinate, l.Coordinate)
                    {
                        GuideDirections = [(0, 1), (1, 0)]
                    });
            foreach (var level in grid.Levels)
                targets.Add(new SnapTarget(SnapKind.Elevation, SnapShape.Elevation,
                    (english ? "Level " : "Отметка ") + GostDrawingPdfExporter.Elevation(level.Elevation), 0, 0, Y: level.Elevation));
        }

        foreach (var e in elements)
        {
            if (exclude?.Contains(e.Id) == true || ElementTypes.IsVoidType(e.Category)) continue;
            switch (e.Type)
            {
                case ElementTypes.Column:
                    targets.Add(Point(e, SnapKind.ColumnCenter, english ? "Column center" : "Центр колонны", 0, 0));
                    AddBoxEdges(targets, e, SnapKind.ColumnFace, english ? "Column face" : "Грань колонны");
                    break;
                case ElementTypes.Wall or ElementTypes.CurtainWall or ElementTypes.Beam when e.Polygon2D.Count < 3:
                {
                    bool beam = e.Category == BimCategory.Beam;
                    string end = english ? beam ? "Beam end" : "Wall end" : beam ? "Торец балки" : "Торец стены";
                    double hw = e.Width / 2, hd = e.Depth / 2;
                    targets.Add(Point(e, SnapKind.WallEndpoint, end, -hw, 0));
                    targets.Add(Point(e, SnapKind.WallEndpoint, end, hw, 0));
                    targets.Add(Point(e, SnapKind.WallMidpoint, english ? "Midpoint" : "Середина", 0, 0));
                    foreach (var (cx, cz) in new[] { (-hw, -hd), (hw, -hd), (hw, hd), (-hw, hd) })
                        targets.Add(Point(e, SnapKind.WallCorner, english ? beam ? "Beam corner" : "Wall corner" : beam ? "Угол балки" : "Угол стены", cx, cz));
                    string face = english ? beam ? "Beam face" : "Wall face" : beam ? "Грань балки" : "Грань стены";
                    targets.Add(Segment(e, SnapKind.WallFace, face, -hw, -hd, hw, -hd));
                    targets.Add(Segment(e, SnapKind.WallFace, face, -hw, hd, hw, hd));
                    targets.Add(Segment(e, SnapKind.WallFace, end, -hw, -hd, -hw, hd));
                    targets.Add(Segment(e, SnapKind.WallFace, end, hw, -hd, hw, hd));
                    if (beam) targets.Add(new SnapTarget(SnapKind.Elevation, SnapShape.Elevation, english ? "Beam soffit" : "Низ балки", 0, 0, Y: e.BaseY));
                    else targets.Add(new SnapTarget(SnapKind.Elevation, SnapShape.Elevation, english ? "Wall top" : "Верх стены", 0, 0, Y: e.TopY));
                    break;
                }
                case ElementTypes.Slab or ElementTypes.CantileverSlab or ElementTypes.Foundation or ElementTypes.Terrace:
                case ElementTypes.Roof when e.IsFlatRoof:
                {
                    var outline = ElementGeometryBuilder.WorldOutline(e);
                    string edge = english ? "Slab edge" : "Край плиты";
                    for (int i = 0; i < outline.Count; i++)
                    {
                        var a = outline[i];
                        var b = outline[(i + 1) % outline.Count];
                        targets.Add(new SnapTarget(SnapKind.SlabEdge, SnapShape.Line, edge, a.X, a.Z, b.X, b.Z) { GuideDirections = [Unit(b.X - a.X, b.Z - a.Z)] });
                        targets.Add(new SnapTarget(SnapKind.SlabEdge, SnapShape.Point, english ? "Slab corner" : "Угол плиты", a.X, a.Z));
                    }
                    targets.Add(new SnapTarget(SnapKind.Elevation, SnapShape.Elevation, english ? "Slab top" : "Верх плиты", 0, 0, Y: e.TopY));
                    targets.Add(new SnapTarget(SnapKind.Elevation, SnapShape.Elevation, english ? "Slab soffit" : "Низ плиты", 0, 0, Y: e.BaseY));
                    break;
                }
            }
        }
        return targets;
    }

    /// <summary>
    /// Привязка смещения вдоль оси перетаскивания: каждая точка элемента (до смещения) сдвигается на rawDelta вдоль axis,
    /// ищется ближайшая цель в радиусе захвата; возвращается скорректированное смещение или null (привязки нет).
    /// </summary>
    public static SnapHit? SnapAlongAxis(IReadOnlyList<(double X, double Y, double Z)> points, (double X, double Y, double Z) axis, double rawDelta,
        IReadOnlyList<SnapTarget> targets, double radius = Radius)
    {
        SnapHit? best = null;
        double bestScore = double.MaxValue;
        bool vertical = Math.Abs(axis.Y) > 0.9;
        double planLen = Math.Sqrt(axis.X * axis.X + axis.Z * axis.Z);
        if (!vertical && planLen < 1e-9) return null;
        double ax = vertical ? 0 : axis.X / planLen, az = vertical ? 0 : axis.Z / planLen;

        void Offer(SnapTarget t, double delta, (double, double, double) anchor, (double, double, double) contact, double distance)
        {
            // Точечные цели (узлы осей, торцы, центры) побеждают линии в пределах радиуса захвата, как в AutoCAD OSnap.
            double score = distance + t.Priority * 0.05;
            if (score < bestScore)
            {
                bestScore = score;
                best = new SnapHit(t, delta, anchor, contact, distance);
            }
        }

        foreach (var p in points)
        {
            if (vertical)
            {
                double qy = p.Y + axis.Y * rawDelta;
                foreach (var t in targets)
                {
                    if (t.Shape != SnapShape.Elevation) continue;
                    double along = (t.Y - qy) / axis.Y;
                    if (Math.Abs(along) > radius) continue;
                    Offer(t, rawDelta + along, (p.X, t.Y, p.Z), (p.X, t.Y, p.Z), Math.Abs(along));
                }
                continue;
            }

            double qx = p.X + ax * rawDelta, qz = p.Z + az * rawDelta;
            foreach (var t in targets)
            {
                switch (t.Shape)
                {
                    case SnapShape.Point:
                    {
                        double vx = t.AX - qx, vz = t.AZ - qz;
                        double along = vx * ax + vz * az;
                        double perp = Math.Abs(vx * az - vz * ax);
                        if (Math.Abs(along) > radius || perp > radius) continue;
                        Offer(t, rawDelta + along, (qx + ax * along, p.Y, qz + az * along), (t.AX, p.Y, t.AZ), Math.Sqrt(along * along + perp * perp));
                        break;
                    }
                    case SnapShape.Line:
                    {
                        double ux = t.BX - t.AX, uz = t.BZ - t.AZ;
                        double len = Math.Sqrt(ux * ux + uz * uz);
                        if (len < 1e-6) continue;
                        ux /= len;
                        uz /= len;
                        // Нормаль линии; линия, почти параллельная оси перетаскивания (< 14°), привязки по оси не даёт.
                        double nx = -uz, nz = ux;
                        double denom = ax * nx + az * nz;
                        if (Math.Abs(denom) < 0.25) continue;
                        double full = ((t.AX - p.X) * nx + (t.AZ - p.Z) * nz) / denom;
                        double along = full - rawDelta;
                        if (Math.Abs(along) > radius) continue;
                        double ix = p.X + ax * full, iz = p.Z + az * full;
                        double s = (ix - t.AX) * ux + (iz - t.AZ) * uz;
                        if (s < -radius || s > len + radius) continue;
                        Offer(t, full, (ix, p.Y, iz), (ix, p.Y, iz), Math.Abs(along));
                        break;
                    }
                }
            }
        }
        return best;
    }

    /// <summary>
    /// Привязка свободной точки в плане (перетаскивание вершины контура): ближайшая точечная цель (узел осей, торец, угол)
    /// или проекция на линию (ось, грань, край плиты) в радиусе захвата; точки побеждают линии при сопоставимом расстоянии.
    /// Delta — расстояние до цели, Contact — точка после привязки.
    /// </summary>
    public static SnapHit? SnapPoint(double x, double y, double z, IReadOnlyList<SnapTarget> targets, double radius = Radius)
    {
        SnapHit? best = null;
        double bestScore = double.MaxValue;
        foreach (var t in targets)
        {
            double cx, cz;
            switch (t.Shape)
            {
                case SnapShape.Point:
                    cx = t.AX;
                    cz = t.AZ;
                    break;
                case SnapShape.Line:
                {
                    double ux = t.BX - t.AX, uz = t.BZ - t.AZ, l2 = ux * ux + uz * uz;
                    if (l2 < 1e-12) continue;
                    double s = Math.Clamp(((x - t.AX) * ux + (z - t.AZ) * uz) / l2, 0, 1);
                    cx = t.AX + ux * s;
                    cz = t.AZ + uz * s;
                    break;
                }
                default:
                    continue;
            }
            double d = Math.Sqrt((cx - x) * (cx - x) + (cz - z) * (cz - z));
            if (d > radius) continue;
            double score = d + t.Priority * 0.05;
            if (score >= bestScore) continue;
            bestScore = score;
            best = new SnapHit(t, d, (x, y, z), (cx, y, cz), d);
        }
        // Две линии (ось и грань, две оси) в радиусе — точка их пересечения, если она тоже в радиусе захвата.
        if (best is { Target.Shape: SnapShape.Line } line)
            foreach (var t in targets)
            {
                if (t.Shape != SnapShape.Line || ReferenceEquals(t, line.Target)) continue;
                if (Intersect(line.Target, t) is not { } p) continue;
                double d = Math.Sqrt((p.X - x) * (p.X - x) + (p.Z - z) * (p.Z - z));
                if (d > radius || d > line.Distance + radius * 0.5) continue;
                best = new SnapHit(line.Target, d, (x, y, z), (p.X, y, p.Z), d);
                break;
            }
        return best;
    }

    private static (double X, double Z)? Intersect(SnapTarget a, SnapTarget b)
    {
        double ux = a.BX - a.AX, uz = a.BZ - a.AZ, vx = b.BX - b.AX, vz = b.BZ - b.AZ;
        double den = ux * vz - uz * vx;
        if (Math.Abs(den) < 1e-9) return null;
        double s = ((b.AX - a.AX) * vz - (b.AZ - a.AZ) * vx) / den, r = ((b.AX - a.AX) * uz - (b.AZ - a.AZ) * ux) / den;
        if (s < -1e-6 || s > 1 + 1e-6 || r < -1e-6 || r > 1 + 1e-6) return null;
        return (a.AX + ux * s, a.AZ + uz * s);
    }

    /// <summary>
    /// Характерные точки элемента для привязки при перемещении: центр, торцы по оси (стена, балка), середины граней в плане
    /// (колонна, плита, фундамент) — все на отметке центра элемента.
    /// </summary>
    public static List<(double X, double Y, double Z)> KeyPoints(ArchitecturalElement e)
    {
        var list = new List<(double X, double Y, double Z)> { (e.X, e.Y, e.Z) };
        if (e.Polygon2D.Count >= 3) return list;
        (double X, double Y, double Z) Local(double lx, double lz)
        {
            var (wx, wz) = ArchitecturalElement.RotateY(lx, lz, e.RotationY);
            return (e.X + wx, e.Y, e.Z + wz);
        }
        double hw = e.Width / 2, hd = e.Depth / 2;
        if (e.Category is BimCategory.Wall or BimCategory.CurtainWall or BimCategory.Beam)
        {
            list.Add(Local(-hw, 0));
            list.Add(Local(hw, 0));
        }
        else if (e.Category is BimCategory.Column or BimCategory.Slab or BimCategory.Foundation or BimCategory.Terrace or BimCategory.Pool or BimCategory.SlabOpening)
        {
            list.Add(Local(-hw, 0));
            list.Add(Local(hw, 0));
            list.Add(Local(0, -hd));
            list.Add(Local(0, hd));
        }
        // Вертикальная привязка — по низу и верху элемента.
        list.Add((e.X, e.BaseY, e.Z));
        list.Add((e.X, e.TopY, e.Z));
        return list;
    }

    private static SnapTarget Point(ArchitecturalElement e, SnapKind kind, string label, double lx, double lz)
    {
        var (wx, wz) = ArchitecturalElement.RotateY(lx, lz, e.RotationY);
        return new SnapTarget(kind, SnapShape.Point, label, e.X + wx, e.Z + wz);
    }

    private static SnapTarget Segment(ArchitecturalElement e, SnapKind kind, string label, double x1, double z1, double x2, double z2)
    {
        var (ax, az) = ArchitecturalElement.RotateY(x1, z1, e.RotationY);
        var (bx, bz) = ArchitecturalElement.RotateY(x2, z2, e.RotationY);
        return new SnapTarget(kind, SnapShape.Line, label, e.X + ax, e.Z + az, e.X + bx, e.Z + bz) { GuideDirections = [Unit(bx - ax, bz - az)] };
    }

    private static void AddBoxEdges(List<SnapTarget> targets, ArchitecturalElement e, SnapKind kind, string label)
    {
        double hw = e.Width / 2, hd = e.Depth / 2;
        targets.Add(Segment(e, kind, label, -hw, -hd, hw, -hd));
        targets.Add(Segment(e, kind, label, hw, -hd, hw, hd));
        targets.Add(Segment(e, kind, label, hw, hd, -hw, hd));
        targets.Add(Segment(e, kind, label, -hw, hd, -hw, -hd));
    }

    private static (double X, double Z) Unit(double x, double z)
    {
        double len = Math.Sqrt(x * x + z * z);
        return len < 1e-9 ? (1, 0) : (x / len, z / len);
    }
}
