using AIArchitectStudio.Models;
using AIArchitectStudio.Rendering;
using static AIArchitectStudio.Services.Verification.VerificationText;

namespace AIArchitectStudio.Services.Verification;

/// <summary>Итог применения набора исправлений.</summary>
public sealed class FixOutcome
{
    public int Applied { get; set; }
    public List<string> Log { get; } = [];
    public List<string> Failed { get; } = [];
    public List<string> CreatedIds { get; } = [];
}

/// <summary>
/// Движок исправлений Совета КБ: применяет действия <see cref="FixAction"/> к сцене (по Id элементов, без записи Undo —
/// вызывающий код открывает одну транзакцию <see cref="SceneStateService.BeginUndoTransaction"/>), моделирует варианты на
/// копии сцены (песочнице) и доводит модель до K_исп ≤ 1.0 стабилизирующими проходами.
/// </summary>
public static class DesignFixEngine
{
    /// <summary>Целевой K_исп при подборе сечений (запас 5 % до предельного 1.0).</summary>
    public const double TargetUtilization = 0.95;

    // ───────────────────────────── Песочница ─────────────────────────────

    /// <summary>Независимая копия сцены (клоны элементов, построек и осей) для моделирования вариантов.</summary>
    public static SceneStateService Sandbox(IEnumerable<ArchitecturalElement> elements, IEnumerable<BuildingGroup> groups, IEnumerable<StructuralGridAxis> axes)
    {
        var s = new SceneStateService();
        s.LoadScene(elements.Select(e => e.Clone()).ToList(), groups.Select(g => g.Clone()).ToList(), recordUndo: false, clearHistory: true,
            axes: axes.Select(a => a.Clone()).ToList());
        return s;
    }

    public static SceneStateService Sandbox(VerificationContext ctx) => Sandbox(ctx.Elements, ctx.Groups, ctx.Axes);

    public static SceneStateService Sandbox(SceneStateService scene) => Sandbox(scene.Elements, scene.BuildingGroups, scene.GridAxes);

    public static StructuralAnalysisResult Analyze(SceneStateService scene, StructuralLoadSettings loads) =>
        StructuralAnalysisService.Analyze(scene.Elements.ToList(), scene.BuildingGroups.ToList(), loads);

    /// <summary>Наибольший K_исп среди элементов с указанными Id (и созданных исправлением).</summary>
    public static double MaxK(StructuralAnalysisResult analysis, IEnumerable<string> ids) =>
        ids.Select(analysis.For).Where(r => r is not null).Select(r => r!.Utilization).DefaultIfEmpty(0).Max();

    /// <summary>Объём бетона сцены (смета BimAnalyticsService), м³.</summary>
    public static double Concrete(SceneStateService scene) =>
        BimAnalyticsService.Calculate(scene.Elements.ToList(), scene.BuildingGroups.ToList(), 1, 1, k => k).TotalConcrete;

    // ───────────────────────────── Применение действий ─────────────────────────────

    /// <summary>Порядок применения: удаления и проёмы, затем каркас, затем отверстия плит (по новому каркасу) и жёсткость.</summary>
    private static int Stage(FixActionKind kind) => kind switch
    {
        FixActionKind.RemoveElement => 0,
        FixActionKind.AttachToWall or FixActionKind.ClampWallOpening or FixActionKind.ResizeOpening => 1,
        FixActionKind.MoveWallOpening or FixActionKind.NarrowWallOpening => 2,
        FixActionKind.MoveOntoSupport or FixActionKind.AddSupportColumnBelow => 3,
        FixActionKind.ThickenSlab or FixActionKind.AddBeamsUnderSlab or FixActionKind.AddIntermediateColumns => 4,
        FixActionKind.EnlargeColumn or FixActionKind.DeepenBeam or FixActionKind.ThickenWall => 5,
        FixActionKind.CreateStairsOpening or FixActionKind.ShiftBeamOffStairs => 6,
        FixActionKind.MoveSlabOpening => 7,
        _ => 8
    };

    public static FixOutcome Apply(SceneStateService scene, IEnumerable<FixAction> actions)
    {
        var outcome = new FixOutcome();
        foreach (var action in actions.Select((a, i) => (a, i)).OrderBy(p => Stage(p.a.Kind)).ThenBy(p => p.i).Select(p => p.a))
        {
            string? done;
            try
            {
                done = ApplyOne(scene, action, outcome);
            }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
            {
                done = null;
                outcome.Failed.Add($"{action.Kind}: {ex.Message}");
            }
            if (done is null)
            {
                outcome.Failed.Add($"{action.Kind} «{scene.FindById(action.TargetId)?.DisplayTitle ?? action.TargetId}»: не применимо к текущей модели");
                continue;
            }
            if (done.Length == 0) continue; // Уже выполнено другим исправлением (плита толще требуемого и т.п.).
            outcome.Applied++;
            outcome.Log.Add(done);
        }
        return outcome;
    }

    private static string? ApplyOne(SceneStateService scene, FixAction a, FixOutcome outcome)
    {
        var target = scene.FindById(a.TargetId);
        switch (a.Kind)
        {
            case FixActionKind.ThickenSlab when target is not null:
                return ThickenSlab(scene, target, a.Value);
            case FixActionKind.AddBeamsUnderSlab when target is not null:
                return AddBeams(scene, target, a, outcome);
            case FixActionKind.AddIntermediateColumns when target is not null:
                return AddIntermediateColumns(scene, target, a, outcome);
            case FixActionKind.EnlargeColumn when target is not null:
            {
                double baseY = target.BaseY;
                double w = Math.Max(target.Width, a.Value), d = target.EffectiveProfile is ProfileTypes.Circle or ProfileTypes.Pipe ? w : Math.Max(target.Depth, a.Value2);
                if (target.Polygon2D.Count >= 3) target.Polygon2D = [];
                target.SetSize(w, target.Height, d);
                target.PlaceOnBase(baseY);
                scene.NotifyElementsChanged([target]);
                return $"Сечение колонны «{target.DisplayTitle}» увеличено до {Mm(w)} × {Mm(d)} мм";
            }
            case FixActionKind.DeepenBeam when target is not null:
            {
                double top = target.TopY, h = Math.Max(target.Height, a.Value);
                target.SetSize(target.Width, h, Math.Max(target.Depth, a.Value2));
                target.SetPosition(target.X, top - h / 2, target.Z);
                scene.NotifyElementsChanged([target]);
                return $"Высота сечения балки «{target.DisplayTitle}» — {Mm(h)} мм (верх сохранён)";
            }
            case FixActionKind.AddSupportColumnBelow when target is not null:
                return AddSupportBelow(scene, target, a, outcome);
            case FixActionKind.MoveOntoSupport when target is not null:
            {
                double dist = Math.Sqrt((target.X - a.X) * (target.X - a.X) + (target.Z - a.Z) * (target.Z - a.Z));
                scene.SetTransform(target, a.X, target.Y, a.Z, target.Width, target.Height, target.Depth, target.RotationY, target.LevelIndex, recordUndo: false);
                return $"Колонна «{target.DisplayTitle}» перенесена на ось нижележащей колонны (сдвиг {M(dist)} м)";
            }
            case FixActionKind.MoveWallOpening when target is not null:
                return ClashDetectionService.ApplyFix(scene, new ClashItem { Fix = ClashFix.MoveWallOpening, ElementAId = target.Id, ElementBId = a.SecondaryId ?? string.Empty });
            case FixActionKind.ClampWallOpening when target is not null && a.SecondaryId is not null:
                return ClashDetectionService.ApplyFix(scene, new ClashItem { Fix = ClashFix.ClampWallOpening, ElementAId = target.Id, ElementBId = a.SecondaryId });
            case FixActionKind.MoveSlabOpening when target is not null:
                return ClashDetectionService.ApplyFix(scene, new ClashItem { Fix = ClashFix.MoveSlabOpening, ElementAId = target.Id, ElementBId = a.SecondaryId ?? string.Empty });
            case FixActionKind.CreateStairsOpening when target is not null && scene.FindById(a.SecondaryId) is { } stairs:
            {
                string? r = ClashDetectionService.ApplyFix(scene, new ClashItem { Fix = ClashFix.CreateStairsOpening, ElementAId = stairs.Id, ElementBId = target.Id });
                if (r is not null && scene.Elements.LastOrDefault(e => e.Type == ElementTypes.SlabOpening && e.HostElementId == target.Id) is { } hole) outcome.CreatedIds.Add(hole.Id);
                return r;
            }
            case FixActionKind.NarrowWallOpening when target is not null:
                return NarrowOpening(scene, target);
            case FixActionKind.ResizeOpening when target is not null:
                return ResizeOpening(scene, target, a.Value, a.Value2);
            case FixActionKind.ShiftBeamOffStairs when target is not null && scene.FindById(a.SecondaryId) is { } st:
                return ShiftBeamOffStairs(scene, target, st);
            case FixActionKind.AttachToWall when target is not null:
                return AttachToWall(scene, target);
            case FixActionKind.RemoveElement when target is not null:
                scene.RemoveElement(target, recordUndo: false);
                return $"Удалён элемент «{target.DisplayTitle}»";
            case FixActionKind.AddCore:
                return AddCore(scene, a, outcome);
            case FixActionKind.AddDiaphragm:
                return AddDiaphragm(scene, a, outcome);
            case FixActionKind.ThickenWall when target is { Category: BimCategory.Wall }:
                return ThickenWall(scene, target, a.Value);
            default:
                return null;
        }
    }

    /// <summary>Утолщение стены: ось сохраняется, несущий слой пирога принимает прирост, проёмы режут новую толщину насквозь.</summary>
    public static string? ThickenWall(SceneStateService scene, ArchitecturalElement wall, double thickness)
    {
        if (thickness <= wall.Depth + 1e-4) return string.Empty;
        double before = wall.Depth;
        wall.SetSize(wall.Width, wall.Height, thickness);
        MaterialLayers.AbsorbThickness(wall);
        var changed = new List<ArchitecturalElement> { wall };
        foreach (var o in scene.GetHostedElements(wall.Id).ToList())
        {
            if (!ElementTypes.IsHostedType(o.Category)) continue;
            o.SetSize(o.Width, o.Height, thickness + ParametricSolverService.OpeningClearance);
            changed.Add(o);
        }
        scene.NotifyElementsChanged(changed);
        return $"Стена «{wall.DisplayTitle}» утолщена с {Mm(before)} до {Mm(thickness)} мм (ось сохранена)";
    }

    // ───────────────────────────── Плиты и балки ─────────────────────────────

    /// <summary>Утолщение плиты: низ сохраняется (плита остаётся на опорах), несущий слой пирога принимает прирост, отверстия режут новую толщину.</summary>
    public static string? ThickenSlab(SceneStateService scene, ArchitecturalElement slab, double thickness)
    {
        if (thickness <= slab.Height + 1e-4) return string.Empty;
        double before = slab.Height, baseY = slab.BaseY;
        slab.SetSize(slab.Width, thickness, slab.Depth);
        slab.PlaceOnBase(baseY);
        MaterialLayers.AbsorbThickness(slab);
        var changed = new List<ArchitecturalElement> { slab };
        foreach (var o in SceneStateService.OpeningsFor(slab, scene.Elements))
        {
            o.SetSize(o.Width, thickness + ParametricSolverService.OpeningClearance, o.Depth);
            o.SetPosition(o.X, slab.Y, o.Z);
            changed.Add(o);
        }
        scene.NotifyElementsChanged(changed);
        return $"Плита «{slab.DisplayTitle}» утолщена с {Mm(before)} до {Mm(thickness)} мм";
    }

    private static string? AddBeams(SceneStateService scene, ArchitecturalElement slab, FixAction a, FixOutcome outcome)
    {
        var all = scene.Elements.ToList();
        var segments = StructuralTopology.GridSegments(slab, StructuralTopology.SupportsUnder(slab, all));
        if (segments.Count == 0) return null;
        double h = Math.Max(0.2, a.Value), b = Math.Max(0.12, a.Value2);
        string profile = string.IsNullOrEmpty(a.Profile) ? ProfileTypes.Rectangle : ProfileTypes.Normalize(a.Profile);
        string material = string.IsNullOrEmpty(a.Material) ? profile == ProfileTypes.IBeam ? "steel" : "concrete" : a.Material;
        var created = new List<ArchitecturalElement>();
        int n = 1;
        foreach (var (c1, c2) in segments)
        {
            double len = Math.Sqrt((c1.X - c2.X) * (c1.X - c2.X) + (c1.Z - c2.Z) * (c1.Z - c2.Z));
            bool alongZ = Math.Abs(c1.X - c2.X) < 0.05;
            var beam = new ArchitecturalElement
            {
                Type = ElementTypes.Beam,
                Name = $"Балка КБ-{slab.LevelIndex + 1}.{n++}",
                BuildingGroupId = slab.BuildingGroupId,
                LevelIndex = slab.LevelIndex,
                Material = material,
                ProfileType = profile
            };
            beam.SetSize(len, h, b);
            beam.SetPosition((c1.X + c2.X) / 2, slab.BaseY - h / 2, (c1.Z + c2.Z) / 2);
            beam.SetRotationY(alongZ ? 90 : 0);
            created.Add(beam);
        }
        scene.AddElements(created, recordUndo: false);
        outcome.CreatedIds.AddRange(created.Select(c => c.Id));
        string kind = profile == ProfileTypes.IBeam ? "двутавровые" : profile == ProfileTypes.Pipe ? "трубчатые" : string.Empty;
        string mat = material.Contains("steel", StringComparison.OrdinalIgnoreCase) ? " стальные" : " ж/б";
        return $"Под плитой «{slab.DisplayTitle}» добавлены {kind}{mat} балки {Mm(b)} × {Mm(h)} мм по осям колонн: {created.Count} шт.".Replace("  ", " ");
    }

    private static string? AddIntermediateColumns(SceneStateService scene, ArchitecturalElement slab, FixAction a, FixOutcome outcome)
    {
        int added = 0;
        int count = Math.Clamp(a.Count, 1, 24);
        for (int i = 0; i < count; i++)
        {
            var all = scene.Elements.ToList();
            var supports = StructuralTopology.SupportsUnder(slab, all);
            var (x, z, d) = StructuralTopology.WorstPoint(slab, supports, StructuralTopology.KeepOutZones(slab, all));
            if (d < 1.2) break;
            x = Math.Round(x / 0.05) * 0.05;
            z = Math.Round(z / 0.05) * 0.05;
            var ids = AddColumnStack(scene, x, z, slab.BaseY, a.Value, a.Value2, a.Profile, a.Material, slab.BuildingGroupId, "Колонна КБ");
            if (ids.Count == 0) break;
            outcome.CreatedIds.AddRange(ids);
            added++;
        }
        return added == 0 ? null : $"Под плитой «{slab.DisplayTitle}» поставлены промежуточные колонны {Mm(a.Value)} × {Mm(a.Value2)} мм в центрах пролётов: {added} шт. (сквозные до опоры)";
    }

    /// <summary>
    /// Колонна от отметки topY вниз до ближайшей поверхности; если под ней нет вертикальной опоры, стойка продолжается
    /// через нижележащие перекрытия до фундамента или земли (непрерывная вертикальная несущая ось).
    /// </summary>
    public static List<string> AddColumnStack(SceneStateService scene, double x, double z, double topY, double w, double d,
        string? profile, string? material, string groupId, string name)
    {
        var ids = new List<string>();
        w = Math.Max(0.3, w);
        d = Math.Max(0.3, d);
        string prof = string.IsNullOrEmpty(profile) ? ProfileTypes.Rectangle : ProfileTypes.Normalize(profile);
        if (prof is ProfileTypes.Circle or ProfileTypes.Pipe) d = w;
        double y = topY;
        for (int guard = 0; guard < 80; guard++)
        {
            var all = scene.Elements.ToList();
            if (StructuralTopology.VerticalBelow(x, z, y, all) is not null) break;
            var surface = StructuralTopology.SurfaceBelow(x, z, y - 0.02, all, tolerance: 0);
            double baseY = surface?.TopY ?? 0;
            if (y - baseY < 0.3) break;
            var column = new ArchitecturalElement
            {
                Type = ElementTypes.Column,
                Name = $"{name} {ids.Count + 1}",
                BuildingGroupId = groupId,
                LevelIndex = StructuralTopology.LevelAbove(surface),
                Material = string.IsNullOrEmpty(material) ? prof is ProfileTypes.IBeam or ProfileTypes.Pipe ? "steel" : "concrete" : material,
                ProfileType = prof
            };
            column.SetSize(w, y - baseY, d);
            column.PlaceOnBase(baseY);
            column.SetPosition(x, column.Y, z);
            scene.AddElement(column, recordUndo: false);
            ids.Add(column.Id);
            if (surface is null || surface.Type is ElementTypes.Foundation or ElementTypes.Terrace) break;
            y = surface.BaseY;
        }
        return ids;
    }

    private static string? AddSupportBelow(SceneStateService scene, ArchitecturalElement e, FixAction a, FixOutcome outcome)
    {
        if (e.Type == ElementTypes.Wall)
        {
            // Под стеной — ригель по её оси и стойки под концами ригеля.
            var (p, q) = StructuralTopology.Axis(e);
            double h = Math.Max(0.4, Math.Ceiling(e.Width / 12 / 0.05) * 0.05), b = Math.Max(0.25, e.Depth);
            var beam = new ArchitecturalElement
            {
                Type = ElementTypes.Beam, Name = $"Ригель под стеной «{e.DisplayTitle}»", BuildingGroupId = e.BuildingGroupId,
                LevelIndex = Math.Max(0, e.LevelIndex - 1), Material = "concrete"
            };
            beam.SetSize(e.Width, h, b);
            beam.SetPosition(e.X, e.BaseY - h / 2, e.Z);
            beam.SetRotationY(e.RotationY);
            scene.AddElement(beam, recordUndo: false);
            outcome.CreatedIds.Add(beam.Id);
            int columns = 0;
            foreach (var (px, pz) in new[] { p, q })
            {
                double t = 0.2 / Math.Max(0.4, e.Width);
                double cx = px + (e.X - px) * t * 2, cz = pz + (e.Z - pz) * t * 2;
                var ids = AddColumnStack(scene, cx, cz, beam.BaseY, Math.Max(0.4, b), Math.Max(0.4, b), null, "concrete", e.BuildingGroupId, "Стойка КБ");
                outcome.CreatedIds.AddRange(ids);
                columns += ids.Count;
            }
            return $"Под стеной «{e.DisplayTitle}» устроен ригель {Mm(b)} × {Mm(h)} мм и стойки ({columns} шт.) до опоры";
        }
        double w = a.Value > 0 ? a.Value : Math.Max(0.35, e.Width), d = a.Value2 > 0 ? a.Value2 : Math.Max(0.35, e.Depth);
        var created = AddColumnStack(scene, e.X, e.Z, e.BaseY, w, d, a.Profile ?? e.EffectiveProfile, a.Material ?? e.Material, e.BuildingGroupId, "Колонна КБ");
        if (created.Count == 0) return null;
        outcome.CreatedIds.AddRange(created);
        return $"Под «{e.DisplayTitle}» подставлена колонна {Mm(w)} × {Mm(d)} мм до опоры (ярусов: {created.Count})";
    }

    // ───────────────────────────── Проёмы и лестницы ─────────────────────────────

    /// <summary>Свободные от колонн участки стены вдоль её оси (локальная X) на высоте проёма.</summary>
    private static List<(double A, double B)> FreeSpans(ArchitecturalElement wall, ArchitecturalElement opening, IReadOnlyList<ArchitecturalElement> all)
    {
        double limit = wall.Width / 2 - 0.05;
        var blocked = new List<(double A, double B)>();
        foreach (var c in all.Where(e => e.Type == ElementTypes.Column))
        {
            if (Math.Min(opening.TopY, c.TopY) - Math.Max(opening.BaseY, c.BaseY) <= 0.05) continue;
            var co = ClashDetectionService.PlanOutline(c);
            var local = co.Select(p => ArchitecturalElement.RotateY(p.X - wall.X, p.Z - wall.Z, -wall.RotationY)).ToList();
            if (local.All(p => Math.Abs(p.Z) > wall.Depth / 2 + 0.3)) continue;
            blocked.Add((local.Min(p => p.X) - 0.05, local.Max(p => p.X) + 0.05));
        }
        foreach (var o in all.Where(e => e != opening && e.HostElementId == wall.Id && ElementTypes.IsHostedType(e.Type)))
        {
            if (Math.Min(opening.TopY, o.TopY) - Math.Max(opening.BaseY, o.BaseY) <= 0.02) continue;
            double ox = ArchitecturalElement.RotateY(o.X - wall.X, o.Z - wall.Z, -wall.RotationY).X;
            blocked.Add((ox - o.Width / 2 - 0.1, ox + o.Width / 2 + 0.1));
        }
        var spans = new List<(double A, double B)>();
        double cursor = -limit;
        foreach (var (s, e) in blocked.OrderBy(b => b.A))
        {
            if (s > cursor) spans.Add((cursor, Math.Min(s, limit)));
            cursor = Math.Max(cursor, e);
        }
        if (cursor < limit) spans.Add((cursor, limit));
        return spans.Where(s => s.B - s.A > 0.3).ToList();
    }

    private static string? NarrowOpening(SceneStateService scene, ArchitecturalElement o)
    {
        if (scene.FindById(o.HostElementId) is not { } wall) return null;
        var (lx, lz) = ArchitecturalElement.RotateY(o.X - wall.X, o.Z - wall.Z, -wall.RotationY);
        var spans = FreeSpans(wall, o, scene.Elements.ToList());
        if (spans.Count == 0) return null;
        // Проём сужается до своей наибольшей части на свободном участке: внешний край остаётся на месте, край у колонны
        // отодвигается от неё. Если на свободном участке остаётся меньше 0.6 м — сужение не имеет смысла.
        double x0 = lx - o.Width / 2, x1 = lx + o.Width / 2;
        var best = spans.Select(s => (A: Math.Max(x0, s.A + 0.05), B: Math.Min(x1, s.B - 0.05))).OrderByDescending(p => p.B - p.A).First();
        double width = best.B - best.A;
        if (width < 0.6 || width >= o.Width - 1e-3) return null;
        double nx = (best.A + best.B) / 2;
        var (wx, wz) = ArchitecturalElement.RotateY(nx, lz, wall.RotationY);
        double before = o.Width;
        scene.SetTransform(o, Math.Round(wall.X + wx, 3), o.Y, Math.Round(wall.Z + wz, 3), Math.Round(width, 3), o.Height, o.Depth, o.RotationY, o.LevelIndex, recordUndo: false);
        return $"Проём «{o.DisplayTitle}» сужен с {Mm(before)} до {Mm(width)} мм в свободном участке стены между колоннами";
    }

    private static string? ResizeOpening(SceneStateService scene, ArchitecturalElement o, double width, double height)
    {
        if (scene.FindById(o.HostElementId) is not { } wall) return null;
        double w = width > 0 ? Math.Min(width, Math.Max(0.3, wall.Width - 0.2)) : o.Width;
        double baseY = o.BaseY;
        double h = height > 0 ? Math.Min(height, Math.Max(0.3, wall.TopY - baseY - 0.05)) : o.Height;
        var (lx, lz) = ArchitecturalElement.RotateY(o.X - wall.X, o.Z - wall.Z, -wall.RotationY);
        lx = Math.Clamp(lx, -wall.Width / 2 + w / 2 + 0.05, wall.Width / 2 - w / 2 - 0.05);
        var (wx, wz) = ArchitecturalElement.RotateY(lx, lz, wall.RotationY);
        string before = $"{Mm(o.Width)} × {Mm(o.Height)}";
        scene.SetTransform(o, Math.Round(wall.X + wx, 3), baseY + h / 2, Math.Round(wall.Z + wz, 3), Math.Round(w, 3), Math.Round(h, 3), o.Depth, o.RotationY, o.LevelIndex, recordUndo: false);
        return $"Проём «{o.DisplayTitle}»: {before} → {Mm(w)} × {Mm(h)} мм";
    }

    private static string? ShiftBeamOffStairs(SceneStateService scene, ArchitecturalElement beam, ArchitecturalElement stairs)
    {
        var (nx, nz) = ArchitecturalElement.RotateY(0, 1, beam.RotationY);
        var proj = ClashDetectionService.PlanOutline(stairs).Select(p => (p.X - beam.X) * nx + (p.Z - beam.Z) * nz).ToList();
        double plus = proj.Max() + beam.Depth / 2 + 0.1, minus = proj.Min() - beam.Depth / 2 - 0.1;
        double s = Math.Abs(plus) <= Math.Abs(minus) ? plus : minus;
        if (Math.Abs(s) > 4) return null;
        scene.SetTransform(beam, Math.Round(beam.X + nx * s, 3), beam.Y, Math.Round(beam.Z + nz * s, 3), beam.Width, beam.Height, beam.Depth, beam.RotationY, beam.LevelIndex, recordUndo: false);
        return $"Балка «{beam.DisplayTitle}» перенесена на {M(Math.Abs(s))} м за пределы марша «{stairs.DisplayTitle}»";
    }

    private static string? AttachToWall(SceneStateService scene, ArchitecturalElement o)
    {
        var wall = scene.Elements.Where(w => ElementTypes.IsHostType(w.Type) && w.BuildingGroupId == o.BuildingGroupId && w.BaseY <= o.BaseY + 0.05 && w.TopY >= o.TopY - 0.05)
            .Select(w =>
            {
                var (a, b) = StructuralTopology.Axis(w);
                return (Wall: w, D: Geometry2D.DistanceToSegment(o.X, o.Z, a.X, a.Z, b.X, b.Z));
            })
            .Where(p => p.D < 0.6).OrderBy(p => p.D).Select(p => p.Wall).FirstOrDefault();
        if (wall is null) return null;
        var (lx, _) = ArchitecturalElement.RotateY(o.X - wall.X, o.Z - wall.Z, -wall.RotationY);
        lx = Math.Clamp(lx, -wall.Width / 2 + o.Width / 2 + 0.05, wall.Width / 2 - o.Width / 2 - 0.05);
        var (wx, wz) = ArchitecturalElement.RotateY(lx, 0, wall.RotationY);
        o.HostElementId = wall.Id;
        scene.SetTransform(o, Math.Round(wall.X + wx, 3), o.Y, Math.Round(wall.Z + wz, 3), o.Width, o.Height, wall.Depth + ParametricSolverService.OpeningClearance, wall.RotationY, wall.LevelIndex, recordUndo: false);
        return $"Проём «{o.DisplayTitle}» привязан к стене «{wall.DisplayTitle}» (HostElementId)";
    }

    // ───────────────────────────── Жёсткость ─────────────────────────────

    private static string? AddCore(SceneStateService scene, FixAction a, FixOutcome outcome)
    {
        var items = scene.Elements.Where(e => e.BuildingGroupId == a.GroupId).ToList();
        var slabs = items.Where(StructuralTopology.IsSpanningSlab).ToList();
        if (slabs.Count == 0) return null;
        double baseY = items.Where(e => e.Type == ElementTypes.Foundation).Select(e => e.TopY).DefaultIfEmpty(0).Max();
        double top = slabs.Max(StructuralTopology.Top);
        var core = new ArchitecturalElement
        {
            Type = ElementTypes.Core, Name = "Ядро жёсткости КБ", BuildingGroupId = a.GroupId ?? BuildingGroup.DefaultGroupId,
            LevelIndex = 0, Material = "concrete"
        };
        core.SetSize(Math.Max(3, a.Value), top - baseY, Math.Max(3, a.Value2));
        core.PlaceOnBase(baseY);
        core.SetPosition(a.X, core.Y, a.Z);
        scene.AddElement(core, recordUndo: false);
        outcome.CreatedIds.Add(core.Id);
        return $"В постройке «{a.GroupId}» устроено ядро жёсткости {M(core.Width)} × {M(core.Depth)} м на высоту {M(core.Height)} м";
    }

    private static string? AddDiaphragm(SceneStateService scene, FixAction a, FixOutcome outcome)
    {
        var all = scene.Elements.ToList();
        var slabs = all.Where(e => e.BuildingGroupId == a.GroupId && StructuralTopology.IsSpanningSlab(e) && e.RepeatLevels <= 1 &&
                                   Geometry2D.Contains(StructuralTopology.Outline(e), a.X, a.Z)).OrderBy(e => e.BaseY).ToList();
        int added = 0;
        foreach (var slab in slabs)
        {
            var surface = StructuralTopology.SurfaceBelow(a.X, a.Z, slab.BaseY - 0.02, scene.Elements.ToList(), tolerance: 0);
            double baseY = surface?.TopY ?? 0, h = slab.BaseY - baseY;
            if (h < 1.5) continue;
            var wall = new ArchitecturalElement
            {
                Type = ElementTypes.Wall, Name = $"Диафрагма жёсткости КБ {slab.LevelIndex + 1}", BuildingGroupId = slab.BuildingGroupId,
                LevelIndex = slab.LevelIndex, Material = "concrete"
            };
            wall.SetSize(Math.Max(1.5, a.Value), h, 0.2);
            wall.PlaceOnBase(baseY);
            wall.SetPosition(a.X, wall.Y, a.Z);
            wall.SetRotationY(a.Value2);
            scene.AddElement(wall, recordUndo: false);
            outcome.CreatedIds.Add(wall.Id);
            added++;
        }
        return added == 0 ? null : $"Добавлены диафрагмы жёсткости ж/б 200 мм длиной {M(a.Value)} м: {added} шт. (по этажам постройки «{a.GroupId}»)";
    }
}
