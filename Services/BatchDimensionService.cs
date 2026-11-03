using System.Globalization;
using AIArchitectStudio.Models;
using AIArchitectStudio.Rendering;

namespace AIArchitectStudio.Services;

/// <summary>Пролёт между соседними координационными осями: номер, марки осей, длина (м).</summary>
public sealed record AxisSpan(int Index, string From, string To, double Length);

/// <summary>Этаж постройки: номер, отметка низа и высота до следующей отметки (м).</summary>
public sealed record StoreyInfo(int Level, double Elevation, double Height);

/// <summary>Габариты постройки (м), пролёты осей по X и Z, этажи.</summary>
public sealed record BuildingDimensionsInfo(string GroupId, double LengthX, double WidthZ, double HeightY,
    IReadOnlyList<AxisSpan> SpansX, IReadOnlyList<AxisSpan> SpansZ, IReadOnlyList<StoreyInfo> Storeys, bool ExplicitAxes);

/// <summary>Итог пакетной операции: успех, число изменённых элементов и пояснение.</summary>
public sealed record BatchDimensionResult(bool Success, int Changed, string Message);

/// <summary>Фильтр пакетной правки: категория, этаж (null — все), марка Renga или имя типа (пусто — все).</summary>
public sealed record BatchFilter(BimCategory? Category, int? Level, string? Mark, string? GroupId = null);

/// <summary>Правка: сечение W × D (мм), толщина (мм), высота окна/двери (мм), отметка подоконника от пола стены (мм), материал; null — без изменения.</summary>
public sealed record BatchEdit(double? WidthMm = null, double? DepthMm = null, double? ThicknessMm = null, double? OpeningHeightMm = null,
    double? SillMm = null, string? Material = null);

/// <summary>
/// «Мастер размеров и пакетное редактирование»: габариты постройки, шаги координационных осей и высоты этажей; изменение
/// шагов и высот одним шагом журнала — оси сдвигаются, колонны переставляются, балки, стены и плиты растягиваются кусочно-линейным
/// отображением координат (между осями — пропорционально, за крайними осями — сдвигом), высоты колонн и стен, отметки плит, балок и
/// проёмов пересчитываются по отметкам этажей; пакетная правка элементов по фильтру (категория, этаж, марка) — тоже одним шагом.
/// Элементы с поворотом не кратным 90° и параметрические этажи только переносятся.
/// </summary>
public static class BatchDimensionService
{
    private static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");

    /// <summary>Размер элемента вдоль направления, начиная с которого он растягивается (меньше — толщина, переносится центр), м.</summary>
    private const double StretchThreshold = 1.0;

    private const double LevelTolerance = 0.05;

    public static BuildingDimensionsInfo? Describe(SceneStateService scene, string groupId)
    {
        var items = scene.Elements.Where(e => e.BuildingGroupId == groupId && !ElementTypes.IsVoidType(e.Category)).ToList();
        if (items.Count == 0) return null;
        var (minX, maxX, minZ, maxZ) = SceneStateService.Bounds(items);
        double minY = items.Min(e => e.BaseY), maxY = items.Max(e => e.RepeatLevels > 1 ? e.TopY + e.LevelHeight * (e.RepeatLevels - 1) : e.TopY);
        var grid = StructuralAxesBuilder.BuildGrids(items, scene.GridAxes.Where(a => a.BuildingGroupId == groupId).ToList()).FirstOrDefault(g => g.GroupId == groupId);
        var nums = grid?.NumericAxes.ToList() ?? [];
        var letters = grid?.Axes.Where(a => !a.IsNumeric).OrderBy(a => a.Coordinate).ToList() ?? [];
        static List<AxisSpan> Spans(List<PlanAxis> axes) =>
            Enumerable.Range(0, Math.Max(0, axes.Count - 1)).Select(i => new AxisSpan(i, axes[i].Label, axes[i + 1].Label, axes[i + 1].Coordinate - axes[i].Coordinate)).ToList();
        var marks = StructuralAxesBuilder.LevelElevations(items);
        var storeys = new List<StoreyInfo>();
        for (int i = 0; i < marks.Count; i++)
        {
            double next = i + 1 < marks.Count ? marks[i + 1].Elevation : maxY;
            storeys.Add(new StoreyInfo(marks[i].Level, marks[i].Elevation, Math.Max(0, next - marks[i].Elevation)));
        }
        // Последняя отметка — верх покрытия: этажом считается, только если над ней есть конструкции выше 0,5 м.
        if (storeys.Count > 1 && storeys[^1].Height < 0.5) storeys.RemoveAt(storeys.Count - 1);
        return new BuildingDimensionsInfo(groupId, maxX - minX, maxZ - minZ, maxY - minY, Spans(nums), Spans(letters), storeys, grid?.IsExplicit == true);
    }

    // ───────────────────────────── Шаги осей ─────────────────────────────

    /// <summary>Новые длины пролётов (номер пролёта → м) вдоль X (цифровые оси) или Z (буквенные): одна транзакция журнала.</summary>
    public static BatchDimensionResult SetSpans(SceneStateService scene, string groupId, bool alongX, IReadOnlyDictionary<int, double> lengths)
    {
        var info = Describe(scene, groupId);
        var spans = info is null ? [] : alongX ? info.SpansX : info.SpansZ;
        if (info is null || spans.Count == 0) return new(false, 0, "У постройки нет координационных осей в этом направлении");
        if (lengths.Any(p => !double.IsFinite(p.Value) || p.Value < 0.3 || p.Value > 60))
            return new(false, 0, "Шаг осей должен быть от 300 до 60 000 мм");

        var items = scene.Elements.Where(e => e.BuildingGroupId == groupId).ToList();
        var grid = StructuralAxesBuilder.BuildGrids(items, scene.GridAxes.Where(a => a.BuildingGroupId == groupId).ToList()).First(g => g.GroupId == groupId);
        var axes = grid.Axes.Where(a => a.IsNumeric == alongX).OrderBy(a => a.Coordinate).ToList();
        var from = axes.Select(a => a.Coordinate).ToArray();
        var to = new double[from.Length];
        to[0] = from[0];
        for (int i = 0; i + 1 < from.Length; i++) to[i + 1] = to[i] + (lengths.TryGetValue(i, out double l) ? l : from[i + 1] - from[i]);
        if (from.Zip(to).All(p => Math.Abs(p.First - p.Second) < 1e-9)) return new(true, 0, "Шаги осей не изменились");

        double Map(double v) => PiecewiseMap(from, to, v);
        int changed;
        string axisWord = alongX ? "цифровых" : "буквенных";
        using (scene.BeginUndoTransaction($"Мастер размеров: шаги {axisWord} осей"))
        {
            changed = MapPlan(scene, items, alongX, Map);
            foreach (var a in scene.GridAxes.Where(a => a.BuildingGroupId == groupId && a.IsNumeric == alongX))
                a.Coordinate = Math.Round(Map(a.Coordinate), 6);
            scene.NotifyGridAxesChanged();
        }
        double growth = (to[^1] - to[0]) - (from[^1] - from[0]);
        return new(true, changed, $"Шаги {axisWord} осей изменены: общий размер между крайними осями {Mm(from[^1] - from[0])} → {Mm(to[^1] - to[0])} мм " +
                                  $"({(growth >= 0 ? "+" : "−")}{Mm(Math.Abs(growth))} мм), перестроено элементов: {changed}");
    }

    /// <summary>Кусочно-линейное отображение координат: между узлами — пропорционально, за крайними — сдвигом крайнего узла.</summary>
    public static double PiecewiseMap(IReadOnlyList<double> from, IReadOnlyList<double> to, double v)
    {
        if (v <= from[0]) return v + (to[0] - from[0]);
        if (v >= from[^1]) return v + (to[^1] - from[^1]);
        for (int i = 0; i + 1 < from.Count; i++)
        {
            if (v > from[i + 1]) continue;
            double span = from[i + 1] - from[i];
            return span < 1e-9 ? to[i] : to[i] + (v - from[i]) * (to[i + 1] - to[i]) / span;
        }
        return v + (to[^1] - from[^1]);
    }

    private static bool IsRightAngle(double rotation, out bool quarter)
    {
        double r = ArchitecturalElement.NormalizeAngle(rotation);
        double m = ((r % 180) + 180) % 180;
        quarter = Math.Abs(m - 90) < 1e-6;
        return quarter || m < 1e-6 || Math.Abs(m - 180) < 1e-6;
    }

    /// <summary>Перестройка плана: контуры — по вершинам, протяжённые элементы — по крайним точкам, прочие — перенос центра.</summary>
    private static int MapPlan(SceneStateService scene, List<ArchitecturalElement> items, bool alongX, Func<double, double> map)
    {
        var changed = new List<ArchitecturalElement>();
        foreach (var e in items)
        {
            double cx = e.X, cz = e.Z;
            double c = alongX ? cx : cz;
            double nc = map(c);
            if (e.Polygon2D.Count >= 3)
            {
                var world = e.Polygon2D.Select(p => ArchitecturalElement.RotateY(p.X, p.Z, e.RotationY)).Select(p => (X: cx + p.Item1, Z: cz + p.Item2)).ToList();
                var mapped = world.Select(p => alongX ? (X: map(p.X), p.Z) : (p.X, Z: map(p.Z))).ToList();
                double ncx = alongX ? nc : cx, ncz = alongX ? cz : nc;
                var local = mapped.Select(p => ArchitecturalElement.RotateY(p.X - ncx, p.Z - ncz, -e.RotationY)).Select(p => new Point2D(Math.Round(p.Item1, 6), Math.Round(p.Item2, 6))).ToList();
                double minX = local.Min(p => p.X), maxX = local.Max(p => p.X), minZ = local.Min(p => p.Z), maxZ = local.Max(p => p.Z);
                e.SetPosition(ncx, e.Y, ncz);
                e.Polygon2D = local;
                e.SetSize(Math.Max(0.01, maxX - minX), e.Height, Math.Max(0.01, maxZ - minZ));
                changed.Add(e);
                continue;
            }
            bool stretchable = e.Category is not (BimCategory.Column or BimCategory.Window or BimCategory.Door or BimCategory.WallOpening or BimCategory.SlabOpening
                or BimCategory.Stairs or BimCategory.Asset or BimCategory.Pool or BimCategory.Core or BimCategory.Diagrid) && e.RepeatLevels <= 1;
            if (stretchable && IsRightAngle(e.RotationY, out bool quarter))
            {
                // Локальная X стены/балки при повороте 90° идёт по мировой Z.
                bool widthAlong = alongX ? !quarter : quarter;
                double half = (widthAlong ? e.Width : e.Depth) / 2;
                if (2 * half >= StretchThreshold)
                {
                    double lo = map(c - half), hi = map(c + half);
                    double size = Math.Max(0.01, hi - lo), center = (lo + hi) / 2;
                    e.SetPosition(alongX ? center : cx, e.Y, alongX ? cz : center);
                    e.SetSize(widthAlong ? size : e.Width, e.Height, widthAlong ? e.Depth : size);
                    changed.Add(e);
                    continue;
                }
            }
            if (Math.Abs(nc - c) < 1e-12) continue;
            e.SetPosition(alongX ? nc : cx, e.Y, alongX ? cz : nc);
            changed.Add(e);
        }
        if (changed.Count > 0) scene.NotifyElementsChanged(changed);
        return changed.Count;
    }

    // ───────────────────────────── Высоты этажей ─────────────────────────────

    /// <summary>Новые высоты этажей (номер этажа в списке <see cref="BuildingDimensionsInfo.Storeys"/> → м): одна транзакция журнала.</summary>
    public static BatchDimensionResult SetStoreyHeights(SceneStateService scene, string groupId, IReadOnlyDictionary<int, double> heights)
    {
        var info = Describe(scene, groupId);
        if (info is null || info.Storeys.Count == 0) return new(false, 0, "У постройки нет этажей");
        if (heights.Any(p => !double.IsFinite(p.Value) || p.Value < 1.8 || p.Value > 30))
            return new(false, 0, "Высота этажа должна быть от 1 800 до 30 000 мм");
        var storeys = info.Storeys;
        // Отметки: низ каждого этажа и верх последнего (верх покрытия над ним).
        var from = storeys.Select(s => s.Elevation).Append(storeys[^1].Elevation + storeys[^1].Height).ToArray();
        var to = new double[from.Length];
        to[0] = from[0];
        for (int i = 0; i < storeys.Count; i++) to[i + 1] = to[i] + (heights.TryGetValue(i, out double h) ? h : storeys[i].Height);
        var delta = from.Zip(to, (a, b) => b - a).ToArray();
        if (delta.All(d => Math.Abs(d) < 1e-9)) return new(true, 0, "Высоты этажей не изменились");

        // Опорная отметка: верх — ближайшая отметка не ниже верха элемента, низ — ближайшая не выше низа.
        double Up(double y)
        {
            for (int k = 0; k < from.Length; k++)
                if (from[k] >= y - LevelTolerance) return delta[k];
            return delta[^1];
        }
        double Down(double y)
        {
            for (int k = from.Length - 1; k >= 0; k--)
                if (from[k] <= y + LevelTolerance) return delta[k];
            return delta[0];
        }

        var items = scene.Elements.Where(e => e.BuildingGroupId == groupId).ToList();
        var changed = new List<ArchitecturalElement>();
        using (scene.BeginUndoTransaction("Мастер размеров: высоты этажей"))
        {
            foreach (var e in items)
            {
                double baseY = e.BaseY, top = e.TopY;
                bool vertical = e.Category is BimCategory.Column or BimCategory.Wall or BimCategory.CurtainWall or BimCategory.Core or BimCategory.Stairs && e.RepeatLevels <= 1;
                double nb, nt;
                if (vertical)
                {
                    nb = baseY + Down(baseY);
                    nt = top + Up(top);
                }
                else
                {
                    // Плиты, балки, плоские покрытия, отверстия плит — по отметке верха; проёмы, фундаменты, кровли — по низу.
                    bool byTop = e.Category is BimCategory.Slab or BimCategory.CantileverSlab or BimCategory.Beam or BimCategory.SlabOpening or BimCategory.Truss || e.IsFlatRoof;
                    double d = byTop ? Up(top) : Down(baseY);
                    nb = baseY + d;
                    nt = top + d;
                }
                if (Math.Abs(nb - baseY) < 1e-12 && Math.Abs(nt - top) < 1e-12) continue;
                double height = Math.Max(0.05, nt - nb);
                e.SetSize(e.Width, height, e.Depth);
                e.SetPosition(e.X, nb + height / 2, e.Z);
                changed.Add(e);
            }
            if (changed.Count > 0) scene.NotifyElementsChanged(changed);
        }
        string list = string.Join(", ", storeys.Select((s, i) => $"{i + 1}-й {Mm(to[i + 1] - to[i])}"));
        return new(true, changed.Count, $"Высоты этажей: {list} мм; верх здания {Mm(from[^1])} → {Mm(to[^1])} мм, перестроено элементов: {changed.Count}");
    }

    // ───────────────────────────── Пакетная правка по фильтру ─────────────────────────────

    public static List<ArchitecturalElement> Filter(IReadOnlyList<ArchitecturalElement> elements, BatchFilter filter)
    {
        Dictionary<string, (string Mark, int Position)>? marks = null;
        var result = new List<ArchitecturalElement>();
        foreach (var e in elements)
        {
            if (ElementTypes.IsVoidType(e.Category) && filter.Category != e.Category) continue;
            if (filter.GroupId is { } g && e.BuildingGroupId != g) continue;
            if (filter.Category is { } c && e.Category != c) continue;
            if (filter.Level is { } l && (l < e.LevelIndex || l > e.LevelIndex + Math.Max(1, e.RepeatLevels) - 1)) continue;
            if (!string.IsNullOrWhiteSpace(filter.Mark))
            {
                marks ??= RengaPropertyService.PlanMarks(elements);
                string mark = RengaPropertyService.StoredMark(e) ?? marks.GetValueOrDefault(e.Id).Mark ?? string.Empty;
                string want = filter.Mark.Trim();
                if (!string.Equals(mark, want, StringComparison.OrdinalIgnoreCase) && !string.Equals(e.ElementType.Name, want, StringComparison.OrdinalIgnoreCase)) continue;
            }
            result.Add(e);
        }
        return result;
    }

    /// <summary>Правка всех совпавших элементов одним шагом журнала; окна и двери после правки проходят логику проёмов.</summary>
    public static BatchDimensionResult Apply(SceneStateService scene, BatchFilter filter, BatchEdit edit)
    {
        var targets = Filter(scene.Elements.ToList(), filter);
        if (targets.Count == 0) return new(false, 0, "Под фильтр не попал ни один элемент");
        static double? M(double? mm) => mm is { } v && double.IsFinite(v) && v > 0 ? v / 1000 : null;
        double? w = M(edit.WidthMm), dd = M(edit.DepthMm), t = M(edit.ThicknessMm), oh = M(edit.OpeningHeightMm);
        double? sill = edit.SillMm is { } s && double.IsFinite(s) && s >= 0 ? s / 1000 : null;
        var changed = new List<ArchitecturalElement>();
        int refused = 0;
        using (var tx = scene.BeginUndoTransaction($"Пакетная правка: {targets.Count} эл."))
        {
            foreach (var e in targets)
            {
                bool any = false;
                switch (e.Category)
                {
                    case BimCategory.Column:
                        if (w is not null || dd is not null) { e.SetSize(w ?? e.Width, e.Height, dd ?? e.Depth); any = true; }
                        break;
                    case BimCategory.Beam:
                        // Сечение балки b × h: ширина — D, высота — H, верх сохраняется (опирание плит).
                        if (w is not null || dd is not null)
                        {
                            double top = e.TopY, h = dd ?? e.Height;
                            e.SetSize(e.Width, h, w ?? e.Depth);
                            e.SetPosition(e.X, top - h / 2, e.Z);
                            any = true;
                        }
                        break;
                    case BimCategory.Wall:
                    case BimCategory.CurtainWall:
                        if (t is not null) { e.SetSize(e.Width, e.Height, t.Value); MaterialLayers.AbsorbThickness(e); any = true; }
                        break;
                    case BimCategory.Slab:
                    case BimCategory.CantileverSlab:
                    case BimCategory.Foundation:
                    case BimCategory.Roof:
                    case BimCategory.Terrace:
                        if (t is not null)
                        {
                            double bottom = e.BaseY;
                            e.SetSize(e.Width, t.Value, e.Depth);
                            e.SetPosition(e.X, bottom + t.Value / 2, e.Z);
                            MaterialLayers.AbsorbThickness(e);
                            any = true;
                        }
                        break;
                    case BimCategory.Window:
                    case BimCategory.Door:
                    case BimCategory.WallOpening:
                    {
                        var host = e.HostElementId is { } hid ? scene.FindById(hid) : null;
                        double baseY = e.BaseY, height = oh ?? e.Height, width = w ?? e.Width;
                        if (sill is not null && host is not null && e.Category != BimCategory.Door) baseY = host.BaseY + sill.Value;
                        if (oh is not null || w is not null || sill is not null)
                        {
                            e.SetSize(width, height, e.Depth);
                            e.SetPosition(e.X, baseY + height / 2, e.Z);
                            any = true;
                        }
                        break;
                    }
                }
                if (edit.Material is { Length: > 0 } mat && e.Material != mat && !ElementTypes.IsVoidType(e.Category))
                {
                    e.Material = mat.Trim();
                    any = true;
                }
                if (!any) continue;
                if (BuildingLogicGuard.IsGuarded(e) && !BuildingLogicGuard.Enforce(scene.Elements.ToList(), e).Success) refused++;
                changed.Add(e);
                if (e.HostElementId is { } hostId && scene.FindById(hostId) is { } wall && !changed.Contains(wall)) changed.Add(wall);
                foreach (var child in scene.GetHostedElements(e.Id)) if (!changed.Contains(child)) changed.Add(child);
            }
            if (changed.Count == 0) tx.Discard();
            else scene.NotifyElementsChanged(changed);
        }
        if (refused > 0 && changed.Count > 0)
        {
            // Проёмы без простенка: шаг откатывается целиком, как при перетаскивании Gizmo.
            scene.Undo();
            return new(false, 0, $"Изменение отменено: {refused} проём(ов) не помещаются в свои стены с отступом 150 мм от колонн и торцов");
        }
        return new(changed.Count > 0, targets.Count, changed.Count > 0 ? $"Изменено элементов: {targets.Count} — одним шагом журнала" : "Значения совпадают с текущими");
    }

    private static string Mm(double m) => Math.Round(m * 1000).ToString("N0", Ru);
}
