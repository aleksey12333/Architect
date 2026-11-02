using AIArchitectStudio.Models;

namespace AIArchitectStudio.Services;

/// <summary>
/// Редактирование вершин контура Polygon2D полигональных плит, фундаментов, террас и плоских кровель: перемещение вершины,
/// вставка новой вершины на ребре и удаление лишней (не меньше трёх). Контур хранится в локальных координатах элемента
/// (относительно Position, с поворотом RotationY); центр элемента не смещается, габарит W × D следует за контуром. Каждая
/// операция — один шаг Undo; самопересекающийся, вырожденный или вывернутый контур не принимается.
/// </summary>
public sealed partial class SceneStateService
{
    /// <summary>Минимальная площадь контура, м².</summary>
    public const double MinContourArea = 0.01;

    /// <summary>Контур элемента редактируется по вершинам (полигональная плита, консоль, фундамент, терраса или плоская кровля).</summary>
    public static bool IsContourEditable(ArchitecturalElement e) =>
        e.Polygon2D.Count >= 3 && (e.Type is ElementTypes.Slab or ElementTypes.CantileverSlab or ElementTypes.Foundation or ElementTypes.Terrace || e.IsFlatRoof);

    /// <summary>Вершины контура в мировых координатах плана (X, Z).</summary>
    public static List<(double X, double Z)> WorldVertices(ArchitecturalElement e) =>
        e.Polygon2D.Select(p =>
        {
            var (rx, rz) = ArchitecturalElement.RotateY(p.X, p.Z, e.RotationY);
            return (e.X + rx, e.Z + rz);
        }).ToList();

    /// <summary>Мировая точка плана → локальные координаты контура элемента.</summary>
    public static (double X, double Z) ToLocal(ArchitecturalElement e, double worldX, double worldZ) =>
        ArchitecturalElement.RotateY(worldX - e.X, worldZ - e.Z, -e.RotationY);

    /// <summary>Перемещение вершины index в мировую точку (x, z). false — контур стал бы недопустимым (изменений нет).</summary>
    public bool MoveVertex(ArchitecturalElement e, int index, double worldX, double worldZ, bool recordUndo = true)
    {
        if (!Elements.Contains(e) || !IsContourEditable(e) || index < 0 || index >= e.Polygon2D.Count || !double.IsFinite(worldX) || !double.IsFinite(worldZ)) return false;
        var (lx, lz) = ToLocal(e, worldX, worldZ);
        var old = e.Polygon2D[index];
        if (Math.Abs(old.X - lx) < 1e-9 && Math.Abs(old.Z - lz) < 1e-9) return true;
        var points = e.Polygon2D.Select(p => p.Clone()).ToList();
        points[index] = new Point2D(Math.Round(lx, 6), Math.Round(lz, 6));
        return CommitContour(e, points, recordUndo);
    }

    /// <summary>
    /// Новая вершина на ребре edgeIndex (между вершинами edgeIndex и edgeIndex + 1): в точке (x, z) или в середине ребра.
    /// Возвращает индекс новой вершины или −1.
    /// </summary>
    public int InsertVertex(ArchitecturalElement e, int edgeIndex, double? worldX = null, double? worldZ = null, bool recordUndo = true)
    {
        if (!Elements.Contains(e) || !IsContourEditable(e) || edgeIndex < 0 || edgeIndex >= e.Polygon2D.Count) return -1;
        var a = e.Polygon2D[edgeIndex];
        var b = e.Polygon2D[(edgeIndex + 1) % e.Polygon2D.Count];
        var (lx, lz) = worldX is { } x && worldZ is { } z ? ToLocal(e, x, z) : ((a.X + b.X) / 2, (a.Z + b.Z) / 2);
        var points = e.Polygon2D.Select(p => p.Clone()).ToList();
        points.Insert(edgeIndex + 1, new Point2D(Math.Round(lx, 6), Math.Round(lz, 6)));
        return CommitContour(e, points, recordUndo) ? edgeIndex + 1 : -1;
    }

    /// <summary>Удаление вершины index (контур остаётся не меньше чем из трёх вершин).</summary>
    public bool RemoveVertex(ArchitecturalElement e, int index, bool recordUndo = true)
    {
        if (!Elements.Contains(e) || !IsContourEditable(e) || e.Polygon2D.Count <= 3 || index < 0 || index >= e.Polygon2D.Count) return false;
        var points = e.Polygon2D.Select(p => p.Clone()).ToList();
        points.RemoveAt(index);
        return CommitContour(e, points, recordUndo);
    }

    /// <summary>Простой (без самопересечений) невырожденный контур площадью не меньше 0.01 м².</summary>
    public static bool IsValidContour(IReadOnlyList<(double X, double Z)> poly)
    {
        if (poly.Count < 3 || Math.Abs(Geometry2D.SignedArea(poly)) < MinContourArea) return false;
        int n = poly.Count;
        for (int i = 0; i < n; i++)
        {
            var a = poly[i];
            var b = poly[(i + 1) % n];
            if ((a.X - b.X) * (a.X - b.X) + (a.Z - b.Z) * (a.Z - b.Z) < 1e-8) return false;
            for (int j = i + 1; j < n; j++)
            {
                // Соседние рёбра имеют общую вершину — не проверяются.
                if (j == i || (j + 1) % n == i || (i + 1) % n == j) continue;
                if (SegmentsIntersect(a, b, poly[j], poly[(j + 1) % n])) return false;
            }
        }
        return true;
    }

    private static bool SegmentsIntersect((double X, double Z) a, (double X, double Z) b, (double X, double Z) c, (double X, double Z) d)
    {
        static double Cross((double X, double Z) o, (double X, double Z) p, (double X, double Z) q) => (p.X - o.X) * (q.Z - o.Z) - (p.Z - o.Z) * (q.X - o.X);
        double d1 = Cross(c, d, a), d2 = Cross(c, d, b), d3 = Cross(a, b, c), d4 = Cross(a, b, d);
        if ((d1 > 1e-12 && d2 < -1e-12 || d1 < -1e-12 && d2 > 1e-12) && (d3 > 1e-12 && d4 < -1e-12 || d3 < -1e-12 && d4 > 1e-12)) return true;
        // Касание вершиной чужого ребра — тоже недопустимо (контур перестаёт быть простым).
        static bool On((double X, double Z) p, (double X, double Z) q, (double X, double Z) r) =>
            Math.Min(p.X, q.X) - 1e-9 <= r.X && r.X <= Math.Max(p.X, q.X) + 1e-9 && Math.Min(p.Z, q.Z) - 1e-9 <= r.Z && r.Z <= Math.Max(p.Z, q.Z) + 1e-9;
        return Math.Abs(d1) <= 1e-12 && On(c, d, a) || Math.Abs(d2) <= 1e-12 && On(c, d, b) || Math.Abs(d3) <= 1e-12 && On(a, b, c) || Math.Abs(d4) <= 1e-12 && On(a, b, d);
    }

    /// <summary>
    /// Применение нового контура: проверка простоты и направления обхода (как у исходного — заливка отверстий по ненулевой
    /// обмотке), габарит W × D по контуру, перестройка геометрии элемента и его отверстий slab_opening.
    /// </summary>
    private bool CommitContour(ArchitecturalElement e, List<Point2D> points, bool recordUndo)
    {
        var poly = points.Select(p => (p.X, p.Z)).ToList();
        if (!IsValidContour(poly)) return false;
        double oldSign = Math.Sign(Geometry2D.SignedArea(e.Polygon2D.Select(p => (p.X, p.Z)).ToList()));
        if (oldSign != 0 && Math.Sign(Geometry2D.SignedArea(poly)) != oldSign) return false;
        if (recordUndo) PushUndo("Контур перекрытия");
        e.Polygon2D = points;
        double w = poly.Max(p => p.X) - poly.Min(p => p.X), d = poly.Max(p => p.Z) - poly.Min(p => p.Z);
        e.SetSize(w, e.Height, d);
        var changed = new List<ArchitecturalElement> { e };
        changed.AddRange(GetOpeningsFor(e));
        Changed(modified: changed.Distinct().ToList());
        return true;
    }
}
