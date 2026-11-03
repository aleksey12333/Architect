using System.Globalization;
using AIArchitectStudio.Models;
using AIArchitectStudio.Rendering;

namespace AIArchitectStudio.Services;

/// <summary>Вид элемента меню «Добавить элемент».</summary>
public enum ManualElementKind
{
    Column,
    Beam,
    Wall,
    Slab,
    Foundation,
    Window,
    Door,
    Stairs
}

/// <summary>Итог ручной операции: созданные и изменённые элементы, имя шага журнала и сообщение (или причина отказа).</summary>
public sealed record ManualEditResult(bool Success, string Message, string StepName, IReadOnlyList<ArchitecturalElement> Created, IReadOnlyList<ArchitecturalElement> Modified)
{
    public static ManualEditResult Fail(string message) => new(false, message, string.Empty, [], []);

    public ArchitecturalElement? Primary => Created.Count > 0 ? Created[0] : Modified.Count > 0 ? Modified[0] : null;
}

/// <summary>Место вставки элемента: постройка, этаж, отметка пола и высота этажа, точка в плане и как она выбрана.</summary>
public sealed record ManualPlacement(string GroupId, int Level, double FloorElevation, double FloorHeight, double X, double Z, string Anchor);

/// <summary>
/// Ручное моделирование без генератора: «Добавить элемент» (колонна, балка, стена, плита, фундамент, окно, дверь, марш) на
/// активном этаже — в свободном узле координационных осей или в центре этажа, сразу с типом из библиотеки ГОСТ/СП; окно и дверь
/// врезаются в выделенную стену с проверкой габаритов. Быстрые операции инспектора: дублирование со смещением или на этаж выше,
/// разделение стены/балки пополам с переносом проёмов, примыкание низа и высоты к уровню этажа и перекрытию над ним.
/// Каждая операция — ровно один шаг журнала транзакций (именованный), вьюпорт обновляется по DocumentChanged.
/// </summary>
public static class ManualModelingService
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>Типовая толщина перекрытия над этажом, м (если перекрытия нет — для высоты стоек).</summary>
    public const double DefaultSlabThickness = 0.2;

    public static string KindTitle(ManualElementKind kind) => kind switch
    {
        ManualElementKind.Column => "Колонна",
        ManualElementKind.Beam => "Балка",
        ManualElementKind.Wall => "Стена",
        ManualElementKind.Slab => "Плита перекрытия",
        ManualElementKind.Foundation => "Фундамент",
        ManualElementKind.Window => "Окно",
        ManualElementKind.Door => "Дверь",
        _ => "Лестничный марш"
    };

    public static string ElementTypeOf(ManualElementKind kind) => kind switch
    {
        ManualElementKind.Column => ElementTypes.Column,
        ManualElementKind.Beam => ElementTypes.Beam,
        ManualElementKind.Wall => ElementTypes.Wall,
        ManualElementKind.Slab => ElementTypes.Slab,
        ManualElementKind.Foundation => ElementTypes.Foundation,
        ManualElementKind.Window => ElementTypes.Window,
        ManualElementKind.Door => ElementTypes.Door,
        _ => ElementTypes.Stairs
    };

    // ───────────────────────────── Место вставки ─────────────────────────────

    /// <summary>
    /// Постройка и этаж вставки: активный этаж фильтра (иначе 1-й), постройка выделенного элемента или самая населённая на этаже;
    /// отметка пола — по этажам постройки, высота этажа — <see cref="SceneStateService.FloorHeight"/>.
    /// Точка: колонна — свободный узел координационных осей, ближайший к центру этажа; остальное — центр этажа (или узел осей рядом).
    /// </summary>
    public static ManualPlacement Placement(SceneStateService scene, ManualElementKind kind, (double X, double Z)? at = null)
    {
        int level = Math.Max(0, scene.ActiveLevelFilter);
        var onLevel = scene.Elements.Where(e => e.LevelIndex == level && !ElementTypes.IsVoidType(e.Category)).ToList();
        string group = scene.SelectedElement?.BuildingGroupId
                       ?? onLevel.GroupBy(e => e.BuildingGroupId).OrderByDescending(g => g.Count()).FirstOrDefault()?.Key
                       ?? scene.BuildingGroups.FirstOrDefault()?.Id ?? BuildingGroup.DefaultGroupId;
        var items = scene.Elements.Where(e => e.BuildingGroupId == group).ToList();
        var marks = StructuralAxesBuilder.LevelElevations(items);
        double elevation = marks.FirstOrDefault(m => m.Level == level)?.Elevation ?? (level == 0 ? 0 : level * 3.0);
        double height = items.Count == 0 ? 3.0 : scene.FloorHeight(group, level);
        var floor = onLevel.Where(e => e.BuildingGroupId == group).ToList();
        var (minX, maxX, minZ, maxZ) = SceneStateService.Bounds(floor);
        double cx = (minX + maxX) / 2, cz = (minZ + maxZ) / 2;
        if (at is { } p) return new ManualPlacement(group, level, elevation, height, p.X, p.Z, "точка вида");

        var axes = scene.AxesOf(group).ToList();
        if (axes.Count == 0 && items.Count > 0) axes = StructuralAxesBuilder.AutoAxes(items, group);
        var xs = axes.Where(a => a.IsNumeric).Select(a => a.Coordinate).OrderBy(v => v).ToList();
        var zs = axes.Where(a => !a.IsNumeric).Select(a => a.Coordinate).OrderBy(v => v).ToList();
        if (xs.Count > 0 && zs.Count > 0)
        {
            var nodes = xs.SelectMany(x => zs.Select(z => (X: x, Z: z))).OrderBy(n => (n.X - cx) * (n.X - cx) + (n.Z - cz) * (n.Z - cz)).ToList();
            if (kind == ManualElementKind.Column)
            {
                var columns = floor.Where(e => e.Category is BimCategory.Column or BimCategory.Core).ToList();
                foreach (var n in nodes)
                    if (!columns.Any(c => Math.Abs(c.X - n.X) < Math.Max(0.3, c.Width / 2) && Math.Abs(c.Z - n.Z) < Math.Max(0.3, c.Depth / 2)))
                        return new ManualPlacement(group, level, elevation, height, n.X, n.Z, $"узел осей {AxisLabel(axes, n.X, true)}/{AxisLabel(axes, n.Z, false)}");
                // Все узлы заняты — центр ближайшей к середине этажа ячейки сетки (промежуточная колонна пролёта).
                if (xs.Count > 1 && zs.Count > 1)
                {
                    var cells = Enumerable.Range(0, xs.Count - 1).SelectMany(i => Enumerable.Range(0, zs.Count - 1).Select(j => (I: i, J: j, X: (xs[i] + xs[i + 1]) / 2, Z: (zs[j] + zs[j + 1]) / 2)))
                        .OrderBy(c => (c.X - cx) * (c.X - cx) + (c.Z - cz) * (c.Z - cz)).ToList();
                    foreach (var c in cells)
                        if (!columns.Any(o => Math.Abs(o.X - c.X) < 0.3 && Math.Abs(o.Z - c.Z) < 0.3))
                            return new ManualPlacement(group, level, elevation, height, c.X, c.Z,
                                $"центр ячейки {AxisLabel(axes, xs[c.I], true)}–{AxisLabel(axes, xs[c.I + 1], true)}/{AxisLabel(axes, zs[c.J], false)}–{AxisLabel(axes, zs[c.J + 1], false)}");
                }
            }
            else if (kind is ManualElementKind.Beam or ManualElementKind.Wall && xs.Count > 1)
            {
                // Балка и стена — по буквенной оси между двумя соседними цифровыми у центра этажа.
                var n = nodes[0];
                int i = Math.Max(0, xs.FindIndex(x => Math.Abs(x - n.X) < 1e-6));
                double x1 = xs[Math.Min(i, xs.Count - 2)], x2 = xs[Math.Min(i, xs.Count - 2) + 1];
                return new ManualPlacement(group, level, elevation, height, (x1 + x2) / 2, n.Z, $"ось {AxisLabel(axes, n.Z, false)}, пролёт {AxisLabel(axes, x1, true)}–{AxisLabel(axes, x2, true)}");
            }
        }
        return new ManualPlacement(group, level, elevation, height, Math.Round(cx, 3), Math.Round(cz, 3), floor.Count > 0 ? "центр этажа" : "начало координат");
    }

    private static string AxisLabel(IReadOnlyList<StructuralGridAxis> axes, double coordinate, bool numeric) =>
        axes.Where(a => a.IsNumeric == numeric).OrderBy(a => Math.Abs(a.Coordinate - coordinate)).FirstOrDefault()?.Label ?? "?";

    /// <summary>Пролёт между соседними цифровыми осями около точки (для длины балки и стены), иначе 6 м.</summary>
    private static double SpanAt(SceneStateService scene, ManualPlacement p)
    {
        var xs = scene.AxesOf(p.GroupId).Where(a => a.IsNumeric).Select(a => a.Coordinate).OrderBy(v => v).ToList();
        if (xs.Count < 2) return 6.0;
        for (int i = 0; i + 1 < xs.Count; i++)
            if (p.X >= xs[i] - 1e-6 && p.X <= xs[i + 1] + 1e-6 && xs[i + 1] - xs[i] > 1) return xs[i + 1] - xs[i];
        return 6.0;
    }

    /// <summary>Перекрытие над элементом этажа: плита/плоская кровля постройки, чей низ выше пола этажа и чей контур содержит точку.</summary>
    public static ArchitecturalElement? CeilingAbove(SceneStateService scene, string group, double floorY, double x, double z, ArchitecturalElement? exclude = null) =>
        scene.Elements
            .Where(e => !ReferenceEquals(e, exclude) && e.BuildingGroupId == group && (e.Category is BimCategory.Slab or BimCategory.CantileverSlab || e.IsFlatRoof) &&
                        e.BaseY > floorY + 0.5 && SceneStateService.PointInPolygon(x, z, ElementGeometryBuilder.WorldOutline(e)))
            .OrderBy(e => e.BaseY)
            .FirstOrDefault();

    // ───────────────────────────── «Добавить элемент» ─────────────────────────────

    /// <summary>
    /// Новый элемент на активном этаже с типом из библиотеки (null — тип по умолчанию категории). Окно/дверь — в выделенную стену
    /// (иначе отказ с подсказкой), с проверкой, что проём помещается между торцами и по высоте. Один шаг журнала.
    /// </summary>
    public static ManualEditResult AddElement(SceneStateService scene, ManualElementKind kind, StandardLibraryEntry? entry = null, (double X, double Z)? at = null)
    {
        if (kind is ManualElementKind.Window or ManualElementKind.Door)
            return scene.SelectedElement is { Category: BimCategory.Wall } wall
                ? InsertOpening(scene, wall, kind == ManualElementKind.Door)
                : ManualEditResult.Fail("Выделите стену, в которую нужно врезать " + (kind == ManualElementKind.Door ? "дверь" : "окно"));

        var p = Placement(scene, kind, at);
        string type = ElementTypeOf(kind);
        entry ??= kind == ManualElementKind.Stairs ? null : StandardLibraryService.DefaultFor(type);
        if (entry is not null && entry.ElementType != type) entry = StandardLibraryService.DefaultFor(type);
        int index = scene.Elements.Count(e => e.Type == type && e.BuildingGroupId == p.GroupId) + 1;
        var ceiling = CeilingAbove(scene, p.GroupId, p.FloorElevation, p.X, p.Z);
        double top = ceiling?.BaseY ?? p.FloorElevation + p.FloorHeight - DefaultSlabThickness;
        double clear = Math.Max(1.0, top - p.FloorElevation);
        var e = new ArchitecturalElement { Type = type, BuildingGroupId = p.GroupId, LevelIndex = p.Level, Material = entry?.Material ?? "concrete" };
        switch (kind)
        {
            case ManualElementKind.Column:
                e.Name = $"Колонна К-{p.Level + 1}.{index}";
                e.SetSize(0.4, clear, 0.4);
                e.SetPosition(p.X, p.FloorElevation + clear / 2, p.Z);
                break;
            case ManualElementKind.Beam:
            {
                double len = entry is { DefaultLengthMm: > 0 } ? Math.Min(entry.DefaultLengthMm / 1000, SpanAt(scene, p)) : SpanAt(scene, p);
                e.Name = $"Балка Б-{p.Level + 1}.{index}";
                e.SetSize(len, 0.6, 0.3);
                e.SetPosition(p.X, top - 0.3, p.Z);
                break;
            }
            case ManualElementKind.Wall:
                e.Name = $"Стена С-{p.Level + 1}.{index}";
                e.SetSize(SpanAt(scene, p), clear, 0.2);
                e.SetPosition(p.X, p.FloorElevation + clear / 2, p.Z);
                break;
            case ManualElementKind.Slab:
            {
                double slabTop = p.FloorElevation + p.FloorHeight;
                e.Name = $"Плита перекрытия П-{p.Level + 1}.{index}";
                e.SetSize(6, 0.2, 6);
                e.SetPosition(p.X, slabTop - 0.1, p.Z);
                break;
            }
            case ManualElementKind.Foundation:
                e.Name = $"Фундаментная плита Фп-{index}";
                e.LevelIndex = 0;
                e.SetSize(6, 0.6, 6);
                e.SetPosition(p.X, p.FloorElevation - 0.3, p.Z);
                break;
            default:
            {
                // Одномаршевая лестница на высоту этажа: проступь 300 мм, подъём ~170 мм, ширина марша 1 200 мм (вдоль локальной +Z).
                double h = Math.Max(1.0, p.FloorHeight);
                int risers = Math.Max(3, (int)Math.Round(h / 0.17));
                e.Name = $"Марш ЛМ-{p.Level + 1}.{index}";
                e.Material = "concrete";
                e.SetSize(1.2, h, Math.Round((risers - 1) * 0.3, 3));
                e.SetPosition(p.X, p.FloorElevation + h / 2, p.Z);
                break;
            }
        }
        if (entry is not null) StandardLibraryService.ApplyTo(e, entry);
        // Плита — верх на отметке пола следующего этажа, фундамент — верх на отметке пола: толщина типоразмера уходит вниз.
        if (kind == ManualElementKind.Slab) e.PlaceOnBase(p.FloorElevation + p.FloorHeight - e.Height);
        else if (kind == ManualElementKind.Foundation) e.PlaceOnBase(p.FloorElevation - e.Height);
        string caption = entry is null ? KindTitle(kind) : $"{KindTitle(kind)} {entry.Caption}";
        string step = $"Добавление: {caption}";
        using (scene.BeginUndoTransaction(step))
            scene.AddElement(e);
        string where = $"этаж {p.Level + 1}, {p.Anchor} (X {Mm(e.X)} · Z {Mm(e.Z)} мм), низ {Elev(e.BaseY)}";
        return new ManualEditResult(true, $"{caption}: {where}", step, [e], []);
    }

    /// <summary>
    /// Врезка окна (1 500 × 1 500 мм, подоконник 900 мм) или двери (900 × 2 100 мм) в стену: в свободный участок стены ближе к
    /// середине, проём — в теле стены (глубина = толщина + 0,06 м), HostElementId = стена. Проём не помещается — отказ.
    /// </summary>
    public static ManualEditResult InsertOpening(SceneStateService scene, ArchitecturalElement wall, bool door)
    {
        double w = door ? 0.9 : 1.5, h = door ? 2.1 : 1.5, sill = door ? 0 : 0.9;
        if (wall.Height < h + sill + 0.05) h = Math.Round(wall.Height - sill - 0.1, 3);
        if (wall.Width < w + 0.2 || h < 0.6)
            return ManualEditResult.Fail($"Проём {Mm(w)} × {Mm(door ? 2.1 : 1.5)} мм не помещается в стену «{wall.DisplayTitle}» ({Mm(wall.Width)} × {Mm(wall.Height)} мм)");
        // Свободное место вдоль стены: участки между существующими проёмами (локальная X), ближе к середине.
        var busy = new List<(double A, double B)>();
        foreach (var h0 in scene.GetHostedElements(wall.Id).Where(o => ElementTypes.IsHostedType(o.Category)))
        {
            double lx = ArchitecturalElement.RotateY(h0.X - wall.X, h0.Z - wall.Z, -wall.RotationY).X;
            busy.Add((lx - h0.Width / 2, lx + h0.Width / 2));
        }
        double half = wall.Width / 2 - 0.1 - w / 2;
        double? best = null;
        for (double s = 0; s <= half + 1e-9 && best is null; s += 0.1)
            foreach (double c in s == 0 ? new[] { 0.0 } : new[] { s, -s })
                if (c >= -half - 1e-9 && c <= half + 1e-9 && busy.All(b => c + w / 2 + 0.1 <= b.A || c - w / 2 - 0.1 >= b.B))
                {
                    best = c;
                    break;
                }
        if (best is not { } local) return ManualEditResult.Fail($"В стене «{wall.DisplayTitle}» нет свободного участка для проёма {Mm(w)} мм");
        var (dx, dz) = ArchitecturalElement.RotateY(local, 0, wall.RotationY);
        int n = scene.Elements.Count(e => e.Type == (door ? ElementTypes.Door : ElementTypes.Window)) + 1;
        var o = new ArchitecturalElement
        {
            Type = door ? ElementTypes.Door : ElementTypes.Window,
            Name = door ? $"Дверь Д-{n}" : $"Окно ОК-{n}",
            BuildingGroupId = wall.BuildingGroupId,
            LevelIndex = wall.LevelIndex,
            Material = door ? "wood" : "glass",
            HostElementId = wall.Id,
            Position = [wall.X + dx, wall.BaseY + sill + h / 2, wall.Z + dz],
            Size = [w, h, wall.Depth + ParametricSolverService.OpeningClearance],
            Rotation = [0, wall.RotationY, 0]
        };
        // Проём не врезается в колонны и поперечные стены: сдвиг в свободный простенок (отступ 150 мм) или отказ без шага.
        var guard = BuildingLogicGuard.Enforce(scene.Elements.ToList(), o);
        if (!guard.Success) return ManualEditResult.Fail(guard.Message);
        local = ArchitecturalElement.RotateY(o.X - wall.X, o.Z - wall.Z, -wall.RotationY).X;
        string step = $"Врезка проёма: {(door ? "дверь" : "окно")} {Mm(w)} × {Mm(h)} в «{wall.DisplayTitle}»";
        using (scene.BeginUndoTransaction(step))
            scene.AddElement(o);
        string note = guard.Message.Length > 0 ? $"; {guard.Message}" : string.Empty;
        return new ManualEditResult(true, $"{(door ? "Дверь" : "Окно")} {Mm(w)} × {Mm(h)} мм врезано в стену «{wall.DisplayTitle}» (смещение от середины {Mm(local)} мм){note}", step, [o], [wall]);
    }

    // ───────────────────────────── Быстрые операции ─────────────────────────────

    /// <summary>
    /// «Дублировать со смещением»: копии выделенных элементов (стены — с проёмами) на (dx, dz) м или на этаж выше. Один шаг журнала.
    /// </summary>
    public static ManualEditResult Duplicate(SceneStateService scene, IReadOnlyList<ArchitecturalElement> sources, double dx, double dz, bool floorUp = false)
    {
        var list = sources.Where(scene.Elements.Contains).ToList();
        if (list.Count == 0) return ManualEditResult.Fail("Нет выделенных элементов");
        string what = list.Count == 1 ? $"«{list[0].DisplayTitle}»" : $"{list.Count} эл.";
        string step = floorUp ? $"Дублирование на этаж выше: {what}" : $"Дублирование со смещением {Mm(dx)} / {Mm(dz)} мм: {what}";
        List<ArchitecturalElement> created;
        using (var transaction = scene.BeginUndoTransaction(step))
        {
            created = floorUp ? scene.CopyFloorUp(list) : scene.CreateLinearArray(list, 2, dx, dz);
            if (created.Count == 0) transaction.Discard();
        }
        if (created.Count == 0) return ManualEditResult.Fail("Смещение не задано");
        return new ManualEditResult(true, floorUp ? $"Скопировано на этаж выше: {created.Count} эл." : $"Создано копий: {created.Count} (ΔX {Mm(dx)}, ΔZ {Mm(dz)} мм)", step, created, []);
    }

    /// <summary>Шаг дублирования по умолчанию: вдоль длинной стороны элемента на её длину + 1 м (стены и балки — по их оси).</summary>
    public static (double Dx, double Dz) DefaultDuplicateOffset(ArchitecturalElement e)
    {
        bool linear = e.Category is BimCategory.Wall or BimCategory.Beam or BimCategory.CurtainWall;
        double step = linear ? e.Width : Math.Max(e.Width, e.Depth) + 1.0;
        if (!linear) return (Math.Round(step, 3), 0);
        var (x, z) = ArchitecturalElement.RotateY(step, 0, e.RotationY);
        return (Math.Round(x, 6), Math.Round(z, 6));
    }

    public static bool CanSplit(ArchitecturalElement e) =>
        e.Category is BimCategory.Wall or BimCategory.Beam or BimCategory.CurtainWall && e.Polygon2D.Count < 3 && e.RepeatLevels <= 1 && e.Width >= 0.4;

    /// <summary>
    /// «Разделить пополам»: линейный элемент делится на два независимых сегмента (тип, пирог, этаж, свойства сохраняются).
    /// Точка разделения — середина; если на неё приходится проём, она сдвигается к ближайшему краю проёма. Исходный элемент
    /// становится первой частью (Id прежний), вторая — новый элемент; проёмы, чья ось во второй части, переходят в неё.
    /// Сумма длин и крайние точки оси совпадают с исходным элементом. Один шаг журнала.
    /// </summary>
    public static ManualEditResult Split(SceneStateService scene, ArchitecturalElement e)
    {
        if (!scene.Elements.Contains(e) || !CanSplit(e)) return ManualEditResult.Fail($"«{e.DisplayTitle}» нельзя разделить (нужна прямая стена или балка длиной от 400 мм)");
        double w = e.Width, rot = e.RotationY;
        var hosted = scene.GetHostedElements(e.Id).ToList();
        var spans = hosted.Where(o => ElementTypes.IsHostedType(o.Category))
            .Select(o => (Item: o, X: ArchitecturalElement.RotateY(o.X - e.X, o.Z - e.Z, -rot).X))
            .Select(p => (p.Item, p.X, A: p.X - p.Item.Width / 2, B: p.X + p.Item.Width / 2)).ToList();
        double cut = 0;
        foreach (var s in spans.Where(s => s.A < cut && s.B > cut))
            cut = Math.Abs(s.A) <= Math.Abs(s.B) ? s.A - 0.05 : s.B + 0.05;
        if (spans.Any(s => s.A < cut && s.B > cut) || cut <= -w / 2 + 0.2 || cut >= w / 2 - 0.2)
            return ManualEditResult.Fail($"Точку разделения «{e.DisplayTitle}» не удаётся выбрать вне проёмов");

        double w1 = cut + w / 2, w2 = w / 2 - cut;
        var (c1x, c1z) = ArchitecturalElement.RotateY(-w / 2 + w1 / 2, 0, rot);
        var (c2x, c2z) = ArchitecturalElement.RotateY(w / 2 - w2 / 2, 0, rot);
        double x0 = e.X, z0 = e.Z, y = e.Y;
        string name = string.IsNullOrWhiteSpace(e.Name) ? e.DisplayTitle : e.Name;
        string step = $"Разделение пополам: «{name}»";
        var second = e.Clone(keepId: false);
        using (scene.BeginUndoTransaction(step))
        {
            e.SetSize(w1, e.Height, e.Depth);
            e.SetPosition(x0 + c1x, y, z0 + c1z);
            e.Name = name + " (1/2)";
            second.SetSize(w2, second.Height, second.Depth);
            second.SetPosition(x0 + c2x, y, z0 + c2z);
            second.Name = name + " (2/2)";
            scene.AddElement(second);
            foreach (var s in spans.Where(s => s.X > cut)) s.Item.HostElementId = second.Id;
            // Привязанные не-проёмы (элементы на стене) — по положению центра.
            foreach (var o in hosted.Where(o => !ElementTypes.IsHostedType(o.Category)))
                if (ArchitecturalElement.RotateY(o.X - x0, o.Z - z0, -rot).X > cut) o.HostElementId = second.Id;
            scene.NotifyElementsChanged([e, second, .. hosted]);
        }
        return new ManualEditResult(true, $"«{name}» разделена на {Mm(w1)} + {Mm(w2)} мм; проёмов во второй части: {spans.Count(s => s.X > cut)}", step, [second], [e]);
    }

    public static bool CanSnapToLevel(ArchitecturalElement e) =>
        e.Category is BimCategory.Column or BimCategory.Wall or BimCategory.CurtainWall or BimCategory.Core or BimCategory.Beam or BimCategory.Slab or BimCategory.CantileverSlab;

    /// <summary>
    /// «Примкнуть к уровню / колоннам»: стойки и стены — низ на отметку пола этажа, верх — под перекрытие над ними (иначе пол
    /// следующего этажа минус 200 мм); балка — верх под перекрытие (низ сечения сохраняет высоту h); плита — верх на отметку пола
    /// следующего этажа (толщина сохраняется). Проёмы стены смещаются вместе с ней. Один шаг журнала для всех элементов.
    /// </summary>
    public static ManualEditResult SnapToLevel(SceneStateService scene, IReadOnlyList<ArchitecturalElement> targets)
    {
        var list = targets.Where(scene.Elements.Contains).Where(CanSnapToLevel).ToList();
        if (list.Count == 0) return ManualEditResult.Fail("Нет элементов для выравнивания по уровню (колонны, стены, балки, плиты)");
        string step = list.Count == 1 ? $"Примыкание к уровню: «{list[0].DisplayTitle}»" : $"Примыкание к уровню: {list.Count} эл.";
        var modified = new List<ArchitecturalElement>();
        var notes = new List<string>();
        using (var transaction = scene.BeginUndoTransaction(step))
        {
            foreach (var e in list)
            {
                var items = scene.Elements.Where(i => i.BuildingGroupId == e.BuildingGroupId).ToList();
                var marks = StructuralAxesBuilder.LevelElevations(items);
                double floor = marks.FirstOrDefault(m => m.Level == e.LevelIndex)?.Elevation ?? e.BaseY;
                double? next = marks.FirstOrDefault(m => m.Level == e.LevelIndex + 1)?.Elevation;
                double floorHeight = scene.FloorHeight(e.BuildingGroupId, e.LevelIndex);
                double baseY, height;
                switch (e.Category)
                {
                    case BimCategory.Slab or BimCategory.CantileverSlab:
                    {
                        double top = next ?? floor + floorHeight;
                        height = e.Height;
                        baseY = top - height;
                        break;
                    }
                    case BimCategory.Beam:
                    {
                        var ceiling = CeilingAbove(scene, e.BuildingGroupId, floor, e.X, e.Z, e);
                        double top = ceiling?.BaseY ?? (next ?? floor + floorHeight) - DefaultSlabThickness;
                        height = e.Height;
                        baseY = top - height;
                        break;
                    }
                    default:
                    {
                        var ceiling = CeilingAbove(scene, e.BuildingGroupId, floor, e.X, e.Z, e);
                        double top = ceiling?.BaseY ?? (next ?? floor + floorHeight) - DefaultSlabThickness;
                        baseY = floor;
                        height = Math.Max(0.3, top - floor);
                        break;
                    }
                }
                if (Math.Abs(baseY - e.BaseY) < 1e-6 && Math.Abs(height - e.Height) < 1e-6) continue;
                notes.Add($"«{e.DisplayTitle}»: низ {Elev(e.BaseY)} → {Elev(baseY)}, высота {Mm(e.Height)} → {Mm(height)} мм");
                scene.SetTransform(e, e.X, baseY + height / 2, e.Z, e.Width, height, e.Depth, e.RotationY, e.LevelIndex, recordUndo: false);
                modified.Add(e);
            }
            if (modified.Count == 0) transaction.Discard();
        }
        if (modified.Count == 0) return new ManualEditResult(true, "Элементы уже примыкают к уровню этажа и перекрытию", step, [], []);
        return new ManualEditResult(true, string.Join("; ", notes), step, [], modified);
    }

    private static string Mm(double m) => Math.Round(m * 1000).ToString("N0", CultureInfo.GetCultureInfo("ru-RU"));

    private static string Elev(double m) => (m >= 0 ? "+" : "−") + Math.Abs(m).ToString("0.000", CultureInfo.GetCultureInfo("ru-RU"));
}
