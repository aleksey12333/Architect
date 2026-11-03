using System.Diagnostics;
using AIArchitectStudio.Models;
using AIArchitectStudio.Services.AI;

namespace AIArchitectStudio.Services;

/// <summary>Входные данные солвера: разобранный ответ ИИ и текущее состояние сцены.</summary>
public sealed class SolverInput
{
    public required AiSceneDocument Document { get; init; }
    public SolverMode Mode { get; init; } = SolverMode.NewScene;
    public IReadOnlyList<ArchitecturalElement> ExistingElements { get; init; } = [];
    public IReadOnlyList<BuildingGroup> ExistingGroups { get; init; } = [];
    public string? SelectedElementId { get; init; }

    /// <summary>
    /// Строгая конструктивная логика проёмов (<see cref="BuildingLogicGuard"/>): сдвиг окон и дверей от колонн и поперечных стен,
    /// отказ проёму без места в стене. Включает живое приложение; по умолчанию выключено — эталонные сцены считаются как раньше.
    /// </summary>
    public bool EnforceBuildingLogic { get; init; }
}

/// <summary>Результат солвера: готовые элементы и постройки + отчёт.</summary>
public sealed class SolverResult
{
    /// <summary>NewScene / AppendToSite — только новые элементы; Modify — вся сцена целиком.</summary>
    public List<ArchitecturalElement> Elements { get; } = [];
    public List<BuildingGroup> Groups { get; } = [];
    public required SolverReport Report { get; init; }

    /// <summary>Смещение новой постройки на участке (AppendToSite).</summary>
    public double OffsetX { get; set; }
    public double OffsetZ { get; set; }

    /// <summary>Элемент, который нужно выделить после применения (Modify).</summary>
    public string? SelectId { get; set; }

    /// <summary>Координационные оси, считанные с чертежа (пусто — оси вычисляются автоматически).</summary>
    public List<StructuralGridAxis> GridAxes { get; } = [];
}

/// <summary>
/// Главный инженерный C#-движок. Превращает высокоуровневый JSON любой нейросети в точную BIM-сцену за 1–2 мс:
/// развёртка параметрических высоток (RepeatLevels / Twist / Taper), сквозное ядро, авто-гравитация Y-Up,
/// стыковка углов стен, привязка окон и дверей к стенам (HostElementId), размещение построек на участке.
/// </summary>
public sealed class ParametricSolverService
{
    public const double SiteGap = 4.0;
    public const double CornerTolerance = 0.5;
    public const double OpeningClearance = 0.06;
    private const int MaxRepeat = 300;
    private const int MaxExpandedElements = 6000;
    private const double Eps = 1e-3;

    private static readonly string[] GroupColors = ["#3B82F6", "#10B981", "#F59E0B", "#8B5CF6", "#EF4444", "#14B8A6", "#64748B", "#EC4899"];

    /// <summary>Служебные сведения об элементе на время работы солвера.</summary>
    private sealed class Meta
    {
        public string? AiId;
        public string? HostRef;
        public double? Offset;
        public double? Sill;
        public bool HasExplicitY;
        public double OriginalBaseY;
        public double FloorHeight;
        public bool ThroughCore;
        public bool Affected;
        public bool Updated;

        /// <summary>Отметка взята с чертежа (BlueprintCompilerService) — гравитация не передвигает элемент.</summary>
        public bool Pinned;
    }

    private sealed class Run(SolverInput input, SolverReport report)
    {
        public readonly SolverInput Input = input;
        public readonly SolverReport Report = report;
        public readonly List<ArchitecturalElement> Elements = [];
        public readonly Dictionary<ArchitecturalElement, Meta> Meta = [];
        public readonly Dictionary<string, string> IdMap = new(StringComparer.Ordinal);
        public readonly HashSet<string> Corrected = [];

        public Meta M(ArchitecturalElement e) => Meta[e];
        public IEnumerable<ArchitecturalElement> Affected => Elements.Where(e => Meta[e].Affected);
        public ArchitecturalElement? Find(string? id) => id is null ? null : Elements.FirstOrDefault(e => e.Id == id);
        public void Correct(ArchitecturalElement e) => Corrected.Add(e.Id);
    }

    // ───────────────────────────── Точка входа ─────────────────────────────

    public SolverResult Solve(SolverInput input)
    {
        var sw = Stopwatch.StartNew();
        var report = new SolverReport { InputElements = input.Document.Elements.Count };
        var run = new Run(input, report);

        if (input.Mode == SolverMode.Modify) LoadExisting(run);
        ApplyDeletes(run);
        string defaultGroup = ResolveDefaultGroup(run);
        string? selectId = input.Mode == SolverMode.Modify ? input.SelectedElementId : null;

        foreach (var spec in input.Document.Elements)
        {
            var existing = input.Mode == SolverMode.Modify ? FindExistingTarget(run, spec) : null;
            if (existing is not null) ApplyUpdate(run, existing, spec);
            else
            {
                var created = Create(run, spec, defaultGroup);
                if (input.Mode == SolverMode.Modify && selectId is null) selectId = created.Id;
            }
        }

        ResolveHostReferences(run);
        foreach (var e in run.Affected.ToList()) Sanitize(run, e);
        ExpandParametricStructures(run);

        var groups = BuildGroups(run, defaultGroup);
        var affectedGroups = run.Affected.Select(e => e.BuildingGroupId).ToHashSet();
        foreach (var e in run.Elements) run.M(e).OriginalBaseY = e.BaseY;

        foreach (var gid in affectedGroups) ApplyGravity(run, gid);
        JoinWallCorners(run);
        AttachOpenings(run, affectedGroups);
        AttachSlabOpenings(run, affectedGroups);

        var audited = run.Elements.Where(e => affectedGroups.Contains(e.BuildingGroupId)).ToList();
        StructuralAuditor.Audit(audited, report);

        var result = new SolverResult { Report = report, SelectId = selectId is not null && run.Find(selectId) is not null ? selectId : null };
        result.Elements.AddRange(run.Elements);
        result.Groups.AddRange(groups);
        foreach (var axis in input.Document.GridAxes)
            if (groups.Any(g => g.Id == axis.BuildingGroupId)) result.GridAxes.Add(axis.Clone());
        foreach (var action in input.Document.CompilerActions) report.Actions.Insert(0, action);
        report.Warnings.AddRange(input.Document.ParseWarnings);

        if (input.Mode == SolverMode.AppendToSite && input.ExistingElements.Count > 0)
        {
            var (ox, oz) = FindFreeSiteOffset(input.ExistingElements, run.Elements, SiteGap);
            result.OffsetX = ox;
            result.OffsetZ = oz;
            report.Actions.Add($"Генплан: новая постройка размещена на свободном месте участка (смещение X {ox:0.##} м, Z {oz:0.##} м, отступ {SiteGap:0} м)");
        }

        sw.Stop();
        report.ElapsedMilliseconds = sw.Elapsed.TotalMilliseconds;
        report.GeneratedElements = input.Mode == SolverMode.Modify ? run.Affected.Count() : run.Elements.Count;
        report.CorrectedElements = run.Corrected.Count;
        report.Success = report.Errors.Count == 0;
        return result;
    }

    // ───────────────────────────── Загрузка, создание, частичное обновление ─────────────────────────────

    private static void LoadExisting(Run run)
    {
        foreach (var src in run.Input.ExistingElements)
        {
            var e = src.Clone();
            e.HasPhysicsError = false;
            run.Elements.Add(e);
            run.Meta[e] = new Meta { HasExplicitY = true, AiId = e.Id };
            run.IdMap[e.Id] = e.Id;
        }
    }

    private static void ApplyDeletes(Run run)
    {
        foreach (var id in run.Input.Document.DeleteIds)
        {
            var target = run.Find(run.IdMap.GetValueOrDefault(id, id));
            if (target is null) continue;
            var doomed = new List<ArchitecturalElement>();
            Collect(run, target, doomed);
            foreach (var d in doomed)
            {
                run.Elements.Remove(d);
                run.Meta.Remove(d);
            }
            // Удаление влияет на гравитацию постройки: помечаем соседей.
            foreach (var e in run.Elements.Where(e => e.BuildingGroupId == target.BuildingGroupId)) run.M(e).Affected = true;
            run.Report.Actions.Add($"Удалено: {target.DisplayTitle}" + (doomed.Count > 1 ? $" и привязанных проёмов: {doomed.Count - 1}" : string.Empty));
        }
    }

    private static void Collect(Run run, ArchitecturalElement e, List<ArchitecturalElement> result)
    {
        if (result.Contains(e)) return;
        result.Add(e);
        foreach (var child in run.Elements.Where(c => c.HostElementId == e.Id).ToList()) Collect(run, child, result);
    }

    private static string ResolveDefaultGroup(Run run)
    {
        var doc = run.Input.Document;
        if (doc.Groups.FirstOrDefault(g => !string.IsNullOrWhiteSpace(g.Id)) is { } first) return first.Id.Trim();
        if (run.Input.Mode == SolverMode.Modify && run.Find(run.Input.SelectedElementId) is { } sel) return sel.BuildingGroupId;
        if (doc.Elements.FirstOrDefault(e => !string.IsNullOrWhiteSpace(e.Group)) is { } withGroup) return withGroup.Group!.Trim();
        return run.Input.Mode == SolverMode.AppendToSite ? "Новая постройка" : BuildingGroup.DefaultGroupId;
    }

    private static ArchitecturalElement? FindExistingTarget(Run run, AiElementSpec spec)
    {
        if (spec.Id is not null)
            return run.Find(run.IdMap.GetValueOrDefault(spec.Id, spec.Id));

        // Ответ без id в режиме modify относится к выбранному элементу (первый подходящий по типу).
        var selected = run.Find(run.Input.SelectedElementId);
        if (selected is not null && !run.M(selected).Updated &&
            (spec.Type is null || NormalizeType(spec.Type, out _) == selected.Type))
            return selected;
        return null;
    }

    private static ArchitecturalElement Create(Run run, AiElementSpec spec, string defaultGroup)
    {
        string type = NormalizeType(spec.Type, out bool known);
        if (!known) run.Report.Warnings.Add($"Неизвестный тип «{spec.Type}» у «{spec.Name ?? spec.Id}» — построен как asset");

        var (dw, dh, dd) = DefaultSize(type);
        var e = new ArchitecturalElement
        {
            Type = type,
            Name = string.IsNullOrWhiteSpace(spec.Name) ? DefaultName(type) : spec.Name.Trim(),
            BuildingGroupId = string.IsNullOrWhiteSpace(spec.Group) ? defaultGroup : spec.Group.Trim(),
            LevelIndex = Math.Clamp(spec.Level ?? 0, 0, 500),
            Material = string.IsNullOrWhiteSpace(spec.Material) ? DefaultMaterial(type) : spec.Material.Trim(),
            Size = [dw, dh, dd],
            Position = [0, dh / 2, 0]
        };
        if (type == ElementTypes.Roof)
        {
            // Форма кровли — только из проекта; без явного указания плоской считается плита-покрытие по контуру
            // или тонкая кровля (≤ 0.6 м), иначе — двускатная.
            bool flatLike = spec.Polygon is { Count: >= 3 } || (spec.Height ?? (spec.Size is { Length: >= 3 } s ? s[1] : dh)) <= 0.6 || spec.Thickness is not null;
            e.RoofShape = RoofShapes.Normalize(spec.RoofShape, flatLike ? RoofShapes.Flat : RoofShapes.Gable);
            if (e.RoofShape == RoofShapes.Flat && spec.Thickness is { } t) e.SetSize(e.Width, t, e.Depth);
        }
        var meta = new Meta { AiId = spec.Id, Affected = true, Pinned = spec.Pinned };
        run.Elements.Add(e);
        run.Meta[e] = meta;
        if (!string.IsNullOrWhiteSpace(spec.Id) && !run.IdMap.ContainsKey(spec.Id)) run.IdMap[spec.Id] = e.Id;

        ApplySpecGeometry(run, e, spec, meta, isNew: true);
        ApplyLayers(run, e, spec);
        return e;
    }

    /// <summary>Пирог из ответа ИИ (Конструктор КБ): готовый набор по имени или явные слои; толщина элемента = сумма слоёв.</summary>
    private static void ApplyLayers(Run run, ArchitecturalElement e, AiElementSpec spec)
    {
        if (!MaterialLayers.IsLayerHost(e)) return;
        List<MaterialLayer>? layers = spec.Layers?.Select(l => l.Clone()).ToList();
        if (layers is null && !string.IsNullOrWhiteSpace(spec.LayersPreset))
        {
            var preset = MaterialLayers.PresetFor(e, spec.LayersPreset.Trim().ToLowerInvariant());
            if (preset.Count > 0) layers = preset;
        }
        if (layers is not { Count: > 0 }) return;
        e.Layers = layers;
        MaterialLayers.ApplyThickness(e);
        run.Report.Actions.Add($"Пирог «{e.DisplayTitle}»: {layers.Count} слоёв, {MaterialLayers.TotalMm(layers):0} мм");
    }

    private static void ApplyUpdate(Run run, ArchitecturalElement e, AiElementSpec spec)
    {
        var meta = run.M(e);
        double oldX = e.X, oldY = e.Y, oldZ = e.Z, oldRot = e.RotationY;
        int oldLevel = e.LevelIndex;
        string oldGroup = e.BuildingGroupId;

        if (spec.Type is not null)
        {
            var type = NormalizeType(spec.Type, out bool known);
            if (known) e.Type = type;
        }
        if (!string.IsNullOrWhiteSpace(spec.Name)) e.Name = spec.Name.Trim();
        if (!string.IsNullOrWhiteSpace(spec.Group)) e.BuildingGroupId = spec.Group.Trim();
        if (spec.Level is { } level) e.LevelIndex = Math.Clamp(level, 0, 500);
        if (!string.IsNullOrWhiteSpace(spec.Material)) e.Material = spec.Material.Trim();
        if (spec.RoofShape is not null && e.Type == ElementTypes.Roof) e.RoofShape = RoofShapes.Normalize(spec.RoofShape, e.RoofShape);

        meta.Affected = true;
        meta.Updated = true;
        if (spec.Pinned) meta.Pinned = true;
        ApplySpecGeometry(run, e, spec, meta, isNew: false);
        ApplyLayers(run, e, spec);
        run.Report.Actions.Add($"Изменён элемент: {e.DisplayTitle}");

        // Окна и двери следуют за стеной-родителем (как при ручном перемещении в 3D).
        if (ElementTypes.IsHostType(e.Type) && (oldX != e.X || oldY != e.Y || oldZ != e.Z || oldRot != e.RotationY))
            MoveHostedChildren(run, e, oldX, oldY, oldZ, oldRot);
        foreach (var child in run.Elements.Where(c => c.HostElementId == e.Id))
        {
            if (oldLevel != e.LevelIndex) child.LevelIndex = e.LevelIndex;
            if (oldGroup != e.BuildingGroupId) child.BuildingGroupId = e.BuildingGroupId;
            run.M(child).Affected = true;
        }
    }

    /// <summary>Геометрия из спецификации: отрезок стены, позиция, габариты, поворот, контур, параметрика и привязка.</summary>
    private static void ApplySpecGeometry(Run run, ArchitecturalElement e, AiElementSpec spec, Meta meta, bool isNew)
    {
        string type = e.Type;
        bool wallLike = type is ElementTypes.Wall or ElementTypes.CurtainWall or ElementTypes.Beam or ElementTypes.Truss;
        bool slabLike = type is ElementTypes.Slab or ElementTypes.CantileverSlab or ElementTypes.Foundation or ElementTypes.Terrace or ElementTypes.SlabOpening
                        || (type == ElementTypes.Roof && e.IsFlatRoof);

        // Сечение стержня — строго как в проекте: квадрат остаётся квадратом, круг — кругом, двутавр — двутавром.
        if (spec.Profile is not null) e.ProfileType = ProfileTypes.Normalize(spec.Profile);
        if (spec.ProfileWallThickness is { } pwt && pwt > 0) e.ProfileWallThickness = pwt;
        if (spec.ProfileWebThickness is { } pweb && pweb > 0) e.ProfileWebThickness = pweb;

        double w = e.Width, h = e.Height, d = e.Depth;
        if (spec.Size is { Length: >= 3 } s3) { w = s3[0]; h = s3[1]; d = s3[2]; }
        else if (spec.Size is { Length: 2 } s2) { w = s2[0]; d = s2[1]; }
        else if (spec.Size is { Length: 1 } s1) w = s1[0];
        if (spec.Width is { } sw) w = sw;
        if (spec.Height is { } sh) h = sh;
        if (spec.Depth is { } sd) d = sd;
        if (spec.Thickness is { } st)
        {
            if (slabLike) h = st;
            else d = st;
        }

        if (spec.Polygon is { Count: >= 3 } poly)
        {
            e.Polygon2D = poly.Select(p => p.Clone()).ToList();
            if (spec.Size is null && spec.Width is null && spec.Depth is null)
            {
                w = poly.Max(p => p.X) - poly.Min(p => p.X);
                d = poly.Max(p => p.Z) - poly.Min(p => p.Z);
            }
        }

        double x = e.X, y = e.Y, z = e.Z;
        double rot = e.RotationY;
        if (wallLike && spec.Start is { } a && spec.End is { } b)
        {
            double dx = b.X - a.X, dz = b.Z - a.Z;
            double length = Math.Sqrt(dx * dx + dz * dz);
            if (length > 0.05)
            {
                w = length;
                rot = Math.Atan2(-dz, dx) * 180 / Math.PI;
                x = (a.X + b.X) / 2;
                z = (a.Z + b.Z) / 2;
            }
        }
        if (spec.Position is { Length: 2 } p2) { x = p2[0]; z = p2[1]; }
        else if (spec.Position is { Length: >= 3 } p3) { x = p3[0]; y = p3[1]; z = p3[2]; }
        if (spec.RotationY is { } ry) rot = ry;

        e.SetSize(w, h, d);
        if (spec.BaseY is { } baseY) y = baseY + e.Height / 2;
        else if (isNew && spec.Position is not { Length: >= 3 }) y = e.Height / 2;
        e.SetPosition(x, y, z);
        e.Rotation = [spec.RotationX ?? (e.Rotation.Length > 0 ? e.Rotation[0] : 0), ArchitecturalElement.NormalizeAngle(rot), spec.RotationZ ?? (e.Rotation.Length > 2 ? e.Rotation[2] : 0)];

        if (spec.RepeatLevels is { } repeat) e.RepeatLevels = Math.Clamp(repeat, 1, MaxRepeat);
        if (spec.LevelHeight is { } lh) e.LevelHeight = lh;
        else if (isNew && e.RepeatLevels > 1) e.LevelHeight = 3.6;
        if (spec.Twist is { } twist) e.TwistAnglePerFloor = twist;
        if (spec.Taper is { } taper) e.TaperScalePerFloor = taper;

        if (spec.Host is not null) meta.HostRef = spec.Host;
        if (spec.Offset is { } offset) meta.Offset = offset;
        if (spec.Sill is { } sill) meta.Sill = sill;
        if (spec.HasExplicitY) meta.HasExplicitY = true;
        else if (isNew) meta.HasExplicitY = false;
    }

    private static void ResolveHostReferences(Run run)
    {
        foreach (var e in run.Affected.Where(e => ElementTypes.IsHostedType(e.Type) || e.Type == ElementTypes.SlabOpening))
        {
            var meta = run.M(e);
            if (meta.HostRef is null) continue;
            string? hostId = run.IdMap.GetValueOrDefault(meta.HostRef) ?? (run.Find(meta.HostRef) is { } direct ? direct.Id : null);
            if (hostId is null)
            {
                run.Report.Warnings.Add(e.Type == ElementTypes.SlabOpening
                    ? $"Плита «{meta.HostRef}» для «{e.DisplayTitle}» не найдена — отверстие привязано к плите своего этажа"
                    : $"Стена «{meta.HostRef}» для «{e.DisplayTitle}» не найдена — проём привязан к ближайшей стене");
                e.HostElementId = null;
            }
            else e.HostElementId = hostId;
        }
    }

    /// <summary>Перемещает проёмы вместе со стеной, сохраняя их положение относительно стены.</summary>
    private static void MoveHostedChildren(Run run, ArchitecturalElement host, double oldX, double oldY, double oldZ, double oldRot)
    {
        double dy = host.Y - oldY, deltaRot = host.RotationY - oldRot;
        foreach (var child in run.Elements.Where(c => c.HostElementId == host.Id).ToList())
        {
            var (lx, lz) = ArchitecturalElement.RotateY(child.X - oldX, child.Z - oldZ, -oldRot);
            var (wx, wz) = ArchitecturalElement.RotateY(lx, lz, host.RotationY);
            child.SetPosition(host.X + wx, child.Y + dy, host.Z + wz);
            child.SetRotationY(child.RotationY + deltaRot);
        }
    }

    private static void Sanitize(Run run, ArchitecturalElement e)
    {
        bool fixedSomething = false;
        double[] Fix(double[] v, double fallback, double min, double max)
        {
            var r = new double[3];
            for (int i = 0; i < 3; i++)
            {
                double value = i < v.Length ? v[i] : fallback;
                if (!double.IsFinite(value)) { value = fallback; fixedSomething = true; }
                double clamped = Math.Clamp(value, min, max);
                if (clamped != value) fixedSomething = true;
                r[i] = clamped;
            }
            return r;
        }

        e.Position = Fix(e.Position, 0, -50_000, 50_000);
        // Отверстие плиты по схеме ТЗ задаётся "size": [ширина, 0, длина] — толщину берёт плита-хост, это не ошибка ИИ.
        if (e.Type == ElementTypes.SlabOpening && e.Size.Length == 3 && double.IsFinite(e.Size[1]) && e.Size[1] is >= 0 and < 0.02)
            e.Size = [e.Size[0], 0.02, e.Size[2]];
        e.Size = Fix(e.Size, 1, 0.02, 2_000);
        e.Rotation = Fix(e.Rotation, 0, -360, 360);
        if (!double.IsFinite(e.LevelHeight) || e.LevelHeight < 2 || e.LevelHeight > 12) { e.LevelHeight = Math.Clamp(double.IsFinite(e.LevelHeight) ? e.LevelHeight : 3.6, 2, 12); fixedSomething = true; }
        if (!double.IsFinite(e.TwistAnglePerFloor) || Math.Abs(e.TwistAnglePerFloor) > 45) { e.TwistAnglePerFloor = double.IsFinite(e.TwistAnglePerFloor) ? Math.Clamp(e.TwistAnglePerFloor, -45, 45) : 0; fixedSomething = true; }
        if (!double.IsFinite(e.TaperScalePerFloor) || e.TaperScalePerFloor <= 0) { e.TaperScalePerFloor = 1; fixedSomething = true; }
        else if (e.TaperScalePerFloor is < 0.85 or > 1.15) { e.TaperScalePerFloor = Math.Clamp(e.TaperScalePerFloor, 0.85, 1.15); fixedSomething = true; }
        e.Polygon2D = e.Polygon2D.Where(p => double.IsFinite(p.X) && double.IsFinite(p.Z)).ToList();
        if (e.Polygon2D.Count is > 0 and < 3) e.Polygon2D = [];
        NormalizeWallAxis(run, e);
        if (fixedSomething)
        {
            run.Correct(e);
            run.Report.Warnings.Add($"Недопустимые значения у «{e.DisplayTitle}» приведены к допустимому диапазону");
        }
    }

    /// <summary>
    /// Стена, заданная «поперёк» ([толщина, высота, длина] без поворота): длина лежит по локальной Z, а стыковка углов, привязка
    /// проёмов и вырезы считают длиной W. Такая стена разворачивалась поперёк здания и ложилась на проём; здесь W и D меняются
    /// местами с поворотом на −90° — положение и габарит в плане те же, локальная X снова вдоль стены.
    /// </summary>
    private static void NormalizeWallAxis(Run run, ArchitecturalElement e)
    {
        if (e.Type is not (ElementTypes.Wall or ElementTypes.CurtainWall) || e.Polygon2D.Count >= 3) return;
        if (!(e.Depth >= 1.0 && e.Width <= 0.8 && e.Depth > 1.5 * e.Width)) return;
        e.SetSize(e.Depth, e.Height, e.Width);
        e.SetRotationY(ArchitecturalElement.NormalizeAngle(e.RotationY - 90));
        run.Correct(e);
        run.Report.Warnings.Add($"«{e.DisplayTitle}»: длина стены была задана по толщине — ось стены развёрнута вдоль её длины");
    }

    // ───────────────────────────── Развёртка высоток ─────────────────────────────

    /// <summary>
    /// RepeatLevels > 1: этажи с шагом Y = level × LevelHeight, поворот TwistAnglePerFloor и сужение TaperScalePerFloor
    /// вокруг оси башни; ядро жёсткости становится сквозным, диагрид остаётся параметрической оболочкой.
    /// </summary>
    private static void ExpandParametricStructures(Run run)
    {
        var parametric = run.Affected.Where(e => e.RepeatLevels > 1).ToList();
        if (parametric.Count == 0) return;

        var axes = parametric.GroupBy(e => e.BuildingGroupId).ToDictionary(
            g => g.Key,
            g => (X: (g.Min(e => e.X) + g.Max(e => e.X)) / 2, Z: (g.Min(e => e.Z) + g.Max(e => e.Z)) / 2));

        var clonesByHost = new Dictionary<string, List<ArchitecturalElement>>();
        int floorsExpanded = 0, cores = 0, diagrids = 0, kept = 0;
        int budget = MaxExpandedElements - run.Elements.Count;

        // Сначала несущие элементы и стены, затем проёмы (им нужны копии стен по этажам).
        foreach (var e in parametric.Where(e => !ElementTypes.IsHostedType(e.Type)))
        {
            int n = e.RepeatLevels;
            var meta = run.M(e);
            if (e.Type == ElementTypes.Core)
            {
                double full = n * e.LevelHeight;
                if (e.Height < full - Eps) e.SetSize(e.Width, full, e.Depth);
                e.RepeatLevels = 1;
                meta.ThroughCore = true;
                cores++;
                continue;
            }
            if (e.Type == ElementTypes.Diagrid)
            {
                e.SetSize(e.Width, e.LevelHeight, e.Depth);
                diagrids++;
                continue;
            }
            if (n - 1 > budget)
            {
                kept++;
                run.Report.Warnings.Add($"«{e.DisplayTitle}»: {n} этажей оставлены параметрическими (лимит {MaxExpandedElements} элементов сцены)");
                continue;
            }

            var axis = axes[e.BuildingGroupId];
            var floors = new List<ArchitecturalElement>(n) { e };
            for (int i = 1; i < n; i++)
            {
                var copy = FloorCopy(e, i, axis, scaleThickness: e.Polygon2D.Count >= 3 || e.Type is not (ElementTypes.Wall or ElementTypes.CurtainWall));
                run.Elements.Add(copy);
                run.Meta[copy] = new Meta { Affected = true, FloorHeight = e.LevelHeight, HasExplicitY = false };
                floors.Add(copy);
            }
            budget -= n - 1;
            meta.FloorHeight = e.LevelHeight;
            meta.HasExplicitY = false;
            ResetParametric(e, 0);
            clonesByHost[e.Id] = floors;
            floorsExpanded += n - 1;
        }

        // Проёмы стен и отверстия плит, развёрнутых по этажам, повторяются на каждом этаже своей копии хоста.
        foreach (var o in run.Affected.Where(e => ElementTypes.IsHostedType(e.Type) || e.Type == ElementTypes.SlabOpening).ToList())
        {
            if (o.HostElementId is null || !clonesByHost.TryGetValue(o.HostElementId, out var hostFloors))
            {
                if (o.RepeatLevels > 1) o.RepeatLevels = 1;
                continue;
            }
            var host0 = hostFloors[0];
            var (lx, lz) = ArchitecturalElement.RotateY(o.X - host0.X, o.Z - host0.Z, -host0.RotationY);
            var srcMeta = run.M(o);
            double baseLevelHeight = host0.LevelHeight;
            for (int i = 1; i < hostFloors.Count; i++)
            {
                var host = hostFloors[i];
                double scale = host0.Width > Eps ? host.Width / host0.Width : 1;
                bool slabHole = o.Type == ElementTypes.SlabOpening;
                var (wx, wz) = ArchitecturalElement.RotateY(lx * scale, slabHole ? lz * scale : lz, host.RotationY);
                var copy = o.Clone(keepId: false);
                copy.HostElementId = host.Id;
                copy.LevelIndex = host.LevelIndex;
                copy.RepeatLevels = 1;
                copy.SetPosition(host.X + wx, o.Y + i * baseLevelHeight, host.Z + wz);
                copy.SetRotationY(slabHole ? o.RotationY + (host.RotationY - host0.RotationY) : host.RotationY);
                run.Elements.Add(copy);
                run.Meta[copy] = new Meta { Affected = true, Sill = srcMeta.Sill, HasExplicitY = srcMeta.HasExplicitY, Offset = srcMeta.Offset is { } off ? off * scale : null };
                floorsExpanded++;
            }
            o.RepeatLevels = 1;
        }

        run.Report.Actions.Add($"Развёртка высоток: параметрических элементов {parametric.Count}, создано поэтажных элементов {floorsExpanded}" +
                               (cores > 0 ? $", сквозных ядер {cores}" : string.Empty) +
                               (diagrids > 0 ? $", диагрид-оболочек {diagrids}" : string.Empty) +
                               (kept > 0 ? $", оставлено параметрическими {kept}" : string.Empty));
    }

    private static ArchitecturalElement FloorCopy(ArchitecturalElement e, int i, (double X, double Z) axis, bool scaleThickness)
    {
        double scale = Math.Pow(e.TaperScalePerFloor, i);
        double angle = e.TwistAnglePerFloor * i;
        var copy = e.Clone(keepId: false);
        var (rx, rz) = ArchitecturalElement.RotateY((e.X - axis.X) * scale, (e.Z - axis.Z) * scale, angle);
        copy.SetPosition(axis.X + rx, e.Y + i * e.LevelHeight, axis.Z + rz);

        bool point = e.Type is ElementTypes.Column or ElementTypes.Asset or ElementTypes.Stairs;
        copy.SetSize(point ? e.Width : e.Width * scale, e.Height, point || !scaleThickness ? e.Depth : e.Depth * scale);
        copy.Polygon2D = e.Polygon2D.Select(p => new Point2D(p.X * scale, p.Z * scale)).ToList();
        copy.SetRotationY(e.RotationY + angle);
        copy.LevelIndex = e.LevelIndex + i;
        copy.Name = $"{e.Name} · этаж {e.LevelIndex + i + 1}";
        ResetParametric(copy, i);
        return copy;
    }

    private static void ResetParametric(ArchitecturalElement e, int floor)
    {
        e.RepeatLevels = 1;
        e.TwistAnglePerFloor = 0;
        e.TaperScalePerFloor = 1;
        if (floor == 0 && !e.Name.Contains("· этаж", StringComparison.Ordinal)) e.Name = $"{e.Name} · этаж {e.LevelIndex + 1}";
    }

    // ───────────────────────────── Постройки участка ─────────────────────────────

    private static List<BuildingGroup> BuildGroups(Run run, string defaultGroup)
    {
        var groups = new List<BuildingGroup>();
        if (run.Input.Mode == SolverMode.Modify)
            groups.AddRange(run.Input.ExistingGroups.Select(g => g.Clone()));

        var ids = run.Input.Document.Groups.Select(g => g.Id.Trim()).Where(id => id.Length > 0)
            .Concat(run.Elements.Select(e => e.BuildingGroupId))
            .Append(defaultGroup)
            .Distinct()
            .Where(id => run.Elements.Any(e => e.BuildingGroupId == id))
            .ToList();

        foreach (var id in ids)
        {
            if (groups.Any(g => g.Id == id)) continue;
            var spec = run.Input.Document.Groups.FirstOrDefault(g => g.Id.Trim() == id);
            var members = run.Elements.Where(e => e.BuildingGroupId == id && !(e.Type == ElementTypes.Diagrid && e.RepeatLevels > 1)).ToList();
            var (minX, maxX, minZ, maxZ) = SceneStateService.Bounds(members.Count > 0 ? members : run.Elements.Where(e => e.BuildingGroupId == id));
            groups.Add(new BuildingGroup(id, spec?.Purpose ?? GuessPurpose(id), GroupColors[groups.Count % GroupColors.Length])
            {
                OriginX = (minX + maxX) / 2,
                OriginZ = (minZ + maxZ) / 2
            });
        }
        groups.RemoveAll(g => run.Elements.All(e => e.BuildingGroupId != g.Id));
        return groups;
    }

    private static string GuessPurpose(string id)
    {
        var s = id.ToLowerInvariant();
        if (s.Contains("бан") || s.Contains("саун") || s.Contains("sauna") || s.Contains("bath")) return "bathhouse";
        if (s.Contains("гараж") || s.Contains("garage")) return "garage";
        if (s.Contains("башн") || s.Contains("небоскр") || s.Contains("tower")) return "tower";
        if (s.Contains("павильон") || s.Contains("pavilion")) return "pavilion";
        if (s.Contains("бассейн") || s.Contains("pool")) return "pool";
        if (s.Contains("стилобат") || s.Contains("podium")) return "podium";
        return "residential";
    }

    // ───────────────────────────── Авто-гравитация (Y-Up) ─────────────────────────────

    /// <summary>
    /// Поэтажная посадка постройки: стены и колонны этажа L — на перекрытие этажа L-1 (этаж 0 — на фундамент/землю),
    /// плиты — на верхний срез стен, кровли и купола — на верхнее перекрытие, бассейны утоплены в землю.
    /// </summary>
    private static void ApplyGravity(Run run, string groupId)
    {
        var items = run.Elements.Where(e => e.BuildingGroupId == groupId && !ElementTypes.IsHostedType(e.Type) && !ElementTypes.IsVoidType(e.Type)).ToList();
        if (items.Count == 0) return;

        bool Frozen(ArchitecturalElement e) => e.RepeatLevels > 1 && run.M(e).HasExplicitY && e.Type != ElementTypes.Diagrid;

        // Фундамент и отметка «земли» постройки.
        var foundations = items.Where(e => e.Type == ElementTypes.Foundation && e.LevelIndex == 0).ToList();
        foreach (var f in foundations.Where(f => !run.M(f).HasExplicitY)) Place(run, f, 0);
        double ground;
        if (foundations.Count > 0) ground = foundations.Max(f => f.TopY);
        else
        {
            var firstFloor = items.Where(e => e.LevelIndex == 0 && IsVertical(e)).ToList();
            ground = firstFloor.Count > 0 && firstFloor.All(e => run.M(e).HasExplicitY) ? Math.Max(0, firstFloor.Min(e => e.BaseY)) : 0;
        }

        int maxLevel = items.Max(e => e.LevelIndex);
        double levelBase = ground;
        for (int level = 0; level <= maxLevel; level++)
        {
            var onLevel = items.Where(e => e.LevelIndex == level && !Frozen(e)).ToList();
            var verticals = onLevel.Where(e => IsVertical(e) && !run.M(e).ThroughCore).ToList();
            foreach (var v in verticals) Place(run, v, levelBase);
            foreach (var core in onLevel.Where(e => run.M(e).ThroughCore)) Place(run, core, levelBase);

            var slabs = onLevel.Where(e => e.Type is ElementTypes.Slab or ElementTypes.CantileverSlab).ToList();
            double slabT = slabs.Count > 0 ? slabs.Max(s => s.Height) : 0;
            double vertH = verticals.Count > 0 ? verticals.Max(v => v.Height) : 0;
            double floorH = onLevel.Count > 0 ? onLevel.Max(e => run.M(e).FloorHeight) : 0;
            if (floorH > 0) vertH = Math.Max(vertH, floorH - slabT);
            double levelTop = levelBase + vertH;

            foreach (var s in slabs) Place(run, s, levelTop);

            double beamTop = levelTop;
            foreach (var b in onLevel.Where(e => e.Type == ElementTypes.Beam))
            {
                Place(run, b, slabs.Count > 0 ? levelTop - b.Height : levelTop);
                if (slabs.Count == 0) beamTop = Math.Max(beamTop, b.TopY);
            }
            foreach (var t in onLevel.Where(e => e.Type == ElementTypes.Truss))
            {
                var m = run.M(t);
                if (m.HasExplicitY && t.BaseY >= levelTop - 0.01) continue;
                Place(run, t, slabs.Count > 0 ? levelTop - t.Height : levelTop);
            }

            double ceiling = slabs.Count > 0 ? levelTop + slabT : beamTop;
            foreach (var r in onLevel.Where(e => e.Type is ElementTypes.Roof or ElementTypes.Shell)) Place(run, r, ceiling);

            foreach (var dg in onLevel.Where(e => e.Type == ElementTypes.Diagrid))
            {
                if (dg.RepeatLevels > 1) dg.SetSize(dg.Width, dg.LevelHeight, dg.Depth);
                Place(run, dg, levelBase);
            }

            foreach (var t in onLevel.Where(e => e.Type == ElementTypes.Terrace))
            {
                if (run.M(t).HasExplicitY && level == 0 && t.BaseY is > -0.6 and < 1.5) continue;
                Place(run, t, level == 0 ? 0 : levelBase);
            }

            foreach (var p in onLevel.Where(e => e.Type == ElementTypes.Pool))
            {
                if (run.M(p).HasExplicitY && level > 0) continue;
                Place(run, p, level == 0 ? -p.Height : levelBase);
            }

            foreach (var a in onLevel.Where(e => e.Type == ElementTypes.Asset))
            {
                if (run.M(a).HasExplicitY && a.BaseY >= levelBase - 0.05) continue;
                Place(run, a, levelBase);
            }

            foreach (var f in onLevel.Where(e => e.Type == ElementTypes.Foundation && e.LevelIndex > 0))
                Place(run, f, levelBase);

            // Закреплённые по чертежу перекрытия задают реальную отметку следующего этажа.
            var pinnedCeilings = onLevel.Where(e => run.M(e).Pinned && (e.Type is ElementTypes.Slab or ElementTypes.CantileverSlab || e.IsFlatRoof)).ToList();
            if (pinnedCeilings.Count > 0) ceiling = Math.Max(ceiling, pinnedCeilings.Max(s => s.TopY));

            bool levelUsed = verticals.Count > 0 || slabs.Count > 0 || floorH > 0;
            levelBase = levelUsed ? ceiling : levelBase;
        }
        run.Report.Actions.Add($"Авто-гравитация «{groupId}»: этажей {maxLevel + 1}, отметка земли постройки {ground:0.##} м");
    }

    private static bool IsVertical(ArchitecturalElement e) =>
        e.Type is ElementTypes.Wall or ElementTypes.CurtainWall or ElementTypes.Column or ElementTypes.Core or ElementTypes.Stairs;

    private static void Place(Run run, ArchitecturalElement e, double baseY)
    {
        // Отметки с чертежа точны: гравитация их не переписывает.
        if (run.M(e).Pinned || Math.Abs(e.BaseY - baseY) <= Eps) return;
        e.PlaceOnBase(baseY);
        run.Correct(e);
    }

    // ───────────────────────────── Стыковка углов стен ─────────────────────────────

    /// <summary>Замыкает торцы перпендикулярных стен одного этажа (L- и T-узлы), если зазор меньше 0.5 м.</summary>
    private static void JoinWallCorners(Run run)
    {
        var walls = run.Elements.Where(e => e.Type is ElementTypes.Wall or ElementTypes.CurtainWall && e.Polygon2D.Count < 3).ToList();
        int joints = 0;
        foreach (var bucket in walls.GroupBy(w => (w.BuildingGroupId, w.LevelIndex)))
        {
            var list = bucket.ToList();
            for (int i = 0; i < list.Count; i++)
            {
                for (int j = i + 1; j < list.Count; j++)
                {
                    var a = list[i];
                    var b = list[j];
                    if (!run.M(a).Affected && !run.M(b).Affected) continue;
                    // null — узла нет; false — узел уже сомкнут (повторный прогон не переворачивает стыковку).
                    if ((TryJoin(a, b) ?? TryJoin(b, a)) == true)
                    {
                        run.Correct(a);
                        run.Correct(b);
                        joints++;
                    }
                }
            }
        }
        if (joints > 0) run.Report.Actions.Add($"Стыковка углов: замкнуто узлов стен {joints}");
    }

    /// <summary>a — стена, к которой примыкает b. L-узел: a продлевается до наружной грани b, b — до внутренней грани a.</summary>
    private static bool? TryJoin(ArchitecturalElement a, ArchitecturalElement b)
    {
        var (ax, az) = Axis(a);
        var (bx, bz) = Axis(b);
        double dot = Math.Abs(ax * bx + az * bz);
        if (dot > Math.Sin(15 * Math.PI / 180)) return null; // не перпендикулярны (±15°)

        var (a0, a1) = Ends(a);
        var (b0, b1) = Ends(b);

        // Пересечение осевых линий.
        double denom = ax * bz - az * bx;
        if (Math.Abs(denom) < 1e-9) return null;
        double t = ((b0.X - a0.X) * bz - (b0.Z - a0.Z) * bx) / denom;
        var p = (X: a0.X + ax * t, Z: a0.Z + az * t);

        int bNear = Dist(b0, p) <= Dist(b1, p) ? 0 : 1;
        var bEnd = bNear == 0 ? b0 : b1;
        if (Dist(bEnd, p) > CornerTolerance + a.Depth / 2 + 0.01) return null;

        double aLen = a.Width;
        bool pInsideA = t > CornerTolerance && t < aLen - CornerTolerance;
        int aNear = Dist(a0, p) <= Dist(a1, p) ? 0 : 1;
        var aEnd = aNear == 0 ? a0 : a1;
        bool aEndNear = Dist(aEnd, p) <= CornerTolerance + b.Depth / 2 + 0.01;
        if (!pInsideA && !aEndNear) return null;

        // Направление b к узлу: от дальнего торца к ближнему.
        var bFar = bNear == 0 ? b1 : b0;
        var (ubx, ubz) = Unit(bEnd.X - bFar.X, bEnd.Z - bFar.Z);
        var newBEnd = (X: p.X - ubx * a.Depth / 2, Z: p.Z - ubz * a.Depth / 2);

        bool changed = false;
        if (Dist(newBEnd, bEnd) > Eps)
        {
            SetEnds(b, bNear == 0 ? newBEnd : b0, bNear == 0 ? b1 : newBEnd);
            changed = true;
        }

        if (!pInsideA && aEndNear)
        {
            var aFar = aNear == 0 ? a1 : a0;
            var (uax, uaz) = Unit(aEnd.X - aFar.X, aEnd.Z - aFar.Z);
            var newAEnd = (X: p.X + uax * b.Depth / 2, Z: p.Z + uaz * b.Depth / 2);
            if (Dist(newAEnd, aEnd) > Eps)
            {
                SetEnds(a, aNear == 0 ? newAEnd : a0, aNear == 0 ? a1 : newAEnd);
                changed = true;
            }
        }
        return changed;
    }

    private static (double X, double Z) Axis(ArchitecturalElement w) => ArchitecturalElement.RotateY(1, 0, w.RotationY);

    private static ((double X, double Z) Start, (double X, double Z) End) Ends(ArchitecturalElement w)
    {
        var (ux, uz) = Axis(w);
        double h = w.Width / 2;
        return ((w.X - ux * h, w.Z - uz * h), (w.X + ux * h, w.Z + uz * h));
    }

    /// <summary>Перестраивает стену по новым торцам, сохраняя направление локальной оси X.</summary>
    private static void SetEnds(ArchitecturalElement w, (double X, double Z) start, (double X, double Z) end)
    {
        double dx = end.X - start.X, dz = end.Z - start.Z;
        double length = Math.Sqrt(dx * dx + dz * dz);
        if (length < 0.05) return;
        w.SetSize(length, w.Height, w.Depth);
        w.SetPosition((start.X + end.X) / 2, w.Y, (start.Z + end.Z) / 2);
    }

    private static double Dist((double X, double Z) a, (double X, double Z) b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Z - b.Z) * (a.Z - b.Z));

    private static (double X, double Z) Unit(double x, double z)
    {
        double l = Math.Sqrt(x * x + z * z);
        return l < 1e-9 ? (0, 0) : (x / l, z / l);
    }

    // ───────────────────────────── Привязка окон и дверей ─────────────────────────────

    /// <summary>
    /// Каждое окно/дверь примагничивается к своей (или ближайшей) стене: ось проёма совпадает с осью стены,
    /// толщина = толщина стены + 0.06 м против мерцания, прописывается HostElementId для булевых вырезов.
    /// </summary>
    private static void AttachOpenings(Run run, HashSet<string> groups)
    {
        int attached = 0, orphan = 0;
        foreach (var o in run.Elements.Where(e => ElementTypes.IsHostedType(e.Type) && groups.Contains(e.BuildingGroupId)).ToList())
        {
            var meta = run.M(o);
            var host = run.Find(o.HostElementId);
            if (host is null || !ElementTypes.IsHostType(host.Type) || host.Polygon2D.Count >= 3)
            {
                host = NearestWall(run, o);
                if (host is null)
                {
                    o.HostElementId = null;
                    o.HasPhysicsError = true;
                    orphan++;
                    run.Report.Errors.Add($"«{o.DisplayTitle}»: рядом нет стены для проёма");
                    continue;
                }
                meta.Offset = null;
            }

            var hostMeta = run.M(host);
            var (lx, _) = ArchitecturalElement.RotateY(o.X - host.X, o.Z - host.Z, -host.RotationY);
            if (meta.Offset is { } offset) lx = offset;

            double length = host.Width;
            double width = Math.Clamp(o.Width, 0.3, Math.Max(0.3, length - 0.2));
            double limit = Math.Max(0, length / 2 - width / 2 - 0.05);
            lx = Math.Clamp(lx, -limit, limit);

            // Сквозной проём высотой от 1.9 м — дверной (от пола), ниже — оконный.
            bool door = o.Type == ElementTypes.Door || (o.Type == ElementTypes.WallOpening && o.Height >= 1.9);
            double defaultSill = door ? 0 : 0.9;
            double sill = defaultSill;
            if (meta.Sill is { } explicitSill) sill = explicitSill;
            else if (meta.HasExplicitY)
            {
                // Отметка проёма задана ИИ в абсолютных координатах: подоконник считаем от исходного низа стены,
                // а если и у стены отметки не было — от оценочного уровня этажа.
                double hostBase = hostMeta.HasExplicitY || o.LevelIndex == 0 ? hostMeta.OriginalBaseY : o.LevelIndex * (host.Height + 0.25);
                sill = meta.OriginalBaseY - hostBase;
                if (sill < -0.05 || sill > host.Height - 0.3) sill = defaultSill;
            }
            if (door && sill < 0.25) sill = 0;
            sill = Math.Max(0, sill);
            double height = Math.Clamp(o.Height, 0.3, Math.Max(0.3, host.Height - 0.05));
            if (sill + height > host.Height - 0.02) sill = Math.Max(0, host.Height - height - 0.05);

            var (wx, wz) = ArchitecturalElement.RotateY(lx, 0, host.RotationY);
            double x = host.X + wx, z = host.Z + wz, y = host.BaseY + sill + height / 2;
            double depth = host.Depth + OpeningClearance;

            bool moved = Math.Abs(o.X - x) > Eps || Math.Abs(o.Y - y) > Eps || Math.Abs(o.Z - z) > Eps ||
                         Math.Abs(o.Width - width) > Eps || Math.Abs(o.Height - height) > Eps || Math.Abs(o.Depth - depth) > Eps ||
                         Math.Abs(ArchitecturalElement.NormalizeAngle(o.RotationY - host.RotationY)) > Eps || o.HostElementId != host.Id;
            o.SetSize(width, height, depth);
            o.SetPosition(x, y, z);
            o.Rotation = [0, host.RotationY, 0];
            o.LevelIndex = host.LevelIndex;
            o.HostElementId = host.Id;
            if (moved) run.Correct(o);
            attached++;

            if (!run.Input.EnforceBuildingLogic || !BuildingLogicGuard.IsGuarded(o)) continue;
            // Проём не врезается в колонны и поперечные стены: сдвиг в свободный простенок или отказ.
            var guard = BuildingLogicGuard.Enforce(run.Elements, o);
            if (!guard.Success)
            {
                run.Elements.Remove(o);
                run.Meta.Remove(o);
                attached--;
                run.Report.Warnings.Add(guard.Message);
                continue;
            }
            if (guard.Message.Length > 0)
            {
                run.Correct(o);
                run.Report.Actions.Add(guard.Message);
            }
        }
        run.Report.HostedOpenings = attached;
        if (attached > 0 || orphan > 0)
            run.Report.Actions.Add($"Авто-привязка проёмов (HostElementId): {attached}" + (orphan > 0 ? $", без стены: {orphan}" : string.Empty));
    }

    /// <summary>
    /// Отверстия slab_opening привязываются к своей плите (HostElementId) или к плите/плоскому покрытию/фундаменту того же
    /// этажа и постройки, в контур которой попадает центр отверстия; высота выреза = толщина плиты + 0.06 м.
    /// </summary>
    private static void AttachSlabOpenings(Run run, HashSet<string> groups)
    {
        int attached = 0, orphan = 0;
        foreach (var o in run.Elements.Where(e => e.Type == ElementTypes.SlabOpening && groups.Contains(e.BuildingGroupId)).ToList())
        {
            var host = run.Find(o.HostElementId);
            if (host is null || !host.CanHostSlabOpenings)
            {
                host = run.Elements
                    .Where(s => s.CanHostSlabOpenings && s.BuildingGroupId == o.BuildingGroupId && s.LevelIndex == o.LevelIndex)
                    .Where(s => SceneStateService.PointInPolygon(o.X, o.Z, AIArchitectStudio.Rendering.ElementGeometryBuilder.WorldOutline(s)))
                    .OrderBy(s => s.Type == ElementTypes.Foundation ? 1 : 0)
                    .FirstOrDefault();
                if (host is null)
                {
                    o.HostElementId = null;
                    o.HasPhysicsError = true;
                    orphan++;
                    run.Report.Errors.Add($"«{o.DisplayTitle}»: на этаже {o.LevelIndex + 1} нет плиты, в которую попадает отверстие");
                    continue;
                }
            }

            double height = host.Height + OpeningClearance;
            bool moved = Math.Abs(o.Y - host.Y) > Eps || Math.Abs(o.Height - height) > Eps || o.HostElementId != host.Id;
            o.SetSize(o.Width, height, o.Depth);
            o.SetPosition(o.X, host.Y, o.Z);
            o.LevelIndex = host.LevelIndex;
            o.HostElementId = host.Id;
            if (moved) run.Correct(o);
            attached++;
        }
        if (attached > 0 || orphan > 0)
            run.Report.Actions.Add($"Отверстия в плитах (slab_opening): привязано {attached}" + (orphan > 0 ? $", без плиты: {orphan}" : string.Empty));
    }

    private static ArchitecturalElement? NearestWall(Run run, ArchitecturalElement o)
    {
        ArchitecturalElement? best = null;
        double bestScore = double.MaxValue;
        foreach (var w in run.Elements)
        {
            if (!ElementTypes.IsHostType(w.Type) || w.Polygon2D.Count >= 3 || w.BuildingGroupId != o.BuildingGroupId) continue;
            var (lx, lz) = ArchitecturalElement.RotateY(o.X - w.X, o.Z - w.Z, -w.RotationY);
            double along = Math.Max(0, Math.Abs(lx) - w.Width / 2);
            double dist = Math.Sqrt(along * along + lz * lz);
            if (dist > 2.5) continue;
            double score = dist + (w.LevelIndex == o.LevelIndex ? 0 : 5 * Math.Abs(w.LevelIndex - o.LevelIndex));
            if (score < bestScore)
            {
                bestScore = score;
                best = w;
            }
        }
        return best;
    }

    // ───────────────────────────── Генплан: свободное место на участке ─────────────────────────────

    /// <summary>
    /// Ищет свободное место для новой постройки вокруг существующих (справа, слева, спереди, сзади) с отступом gap
    /// так, чтобы габариты не пересекались ни с одной постройкой; выбирает ближайшее к центру участка.
    /// </summary>
    public static (double X, double Z) FindFreeSiteOffset(IEnumerable<ArchitecturalElement> existing, IEnumerable<ArchitecturalElement> added, double gap)
    {
        var old = existing.ToList();
        var fresh = added.ToList();
        if (old.Count == 0 || fresh.Count == 0) return (0, 0);

        var (nMinX, nMaxX, nMinZ, nMaxZ) = SceneStateService.Bounds(fresh);
        double w = nMaxX - nMinX, d = nMaxZ - nMinZ;

        var rects = old.GroupBy(e => e.BuildingGroupId).Select(g => SceneStateService.Bounds(g)).ToList();
        var site = SceneStateService.Bounds(old);
        double cx = (site.MinX + site.MaxX) / 2, cz = (site.MinZ + site.MaxZ) / 2;

        var candidates = new List<(double MinX, double MinZ)>();
        foreach (var r in rects.Prepend(site))
        {
            candidates.Add((r.MaxX + gap, r.MinZ));
            candidates.Add((r.MaxX + gap, r.MaxZ - d));
            candidates.Add((r.MinX - gap - w, r.MinZ));
            candidates.Add((r.MinX, r.MaxZ + gap));
            candidates.Add((r.MaxX - w, r.MaxZ + gap));
            candidates.Add((r.MinX, r.MinZ - gap - d));
        }

        (double MinX, double MinZ)? best = null;
        double bestScore = double.MaxValue;
        foreach (var c in candidates)
        {
            bool overlaps = rects.Any(r =>
                c.MinX < r.MaxX + gap - 0.01 && c.MinX + w > r.MinX - gap + 0.01 &&
                c.MinZ < r.MaxZ + gap - 0.01 && c.MinZ + d > r.MinZ - gap + 0.01);
            if (overlaps) continue;
            double dx = c.MinX + w / 2 - cx, dz = c.MinZ + d / 2 - cz;
            double score = Math.Sqrt(dx * dx + dz * dz);
            if (score < bestScore - 1e-6)
            {
                bestScore = score;
                best = c;
            }
        }
        var target = best ?? (site.MaxX + gap, site.MinZ);
        return (target.MinX - nMinX, target.MinZ - nMinZ);
    }

    // ───────────────────────────── Справочники ─────────────────────────────

    public static string NormalizeType(string? raw, out bool known)
    {
        known = true;
        var t = (raw ?? string.Empty).Trim().ToLowerInvariant().Replace('-', '_').Replace(' ', '_');
        switch (t)
        {
            case var _ when ElementTypes.All.Contains(t): return t;
            case "curtainwall" or "glass_wall" or "glazing" or "facade" or "витраж": return ElementTypes.CurtainWall;
            case "floor" or "ceiling" or "plate" or "перекрытие" or "плита": return ElementTypes.Slab;
            case "cantilever" or "balcony" or "overhang" or "консоль" or "балкон": return ElementTypes.CantileverSlab;
            case "pillar" or "post" or "колонна": return ElementTypes.Column;
            case "stair" or "staircase" or "лестница": return ElementTypes.Stairs;
            case "dome" or "vault" or "купол" or "оболочка": return ElementTypes.Shell;
            case "deck" or "patio" or "veranda" or "терраса": return ElementTypes.Terrace;
            case "girder" or "lintel" or "ring_beam" or "балка": return ElementTypes.Beam;
            case "shear_core" or "elevator_core" or "ядро": return ElementTypes.Core;
            case "base" or "footing" or "plinth" or "фундамент": return ElementTypes.Foundation;
            case "swimming_pool" or "бассейн": return ElementTypes.Pool;
            case "стена" or "partition" or "перегородка": return ElementTypes.Wall;
            case "окно": return ElementTypes.Window;
            case "дверь" or "gate": return ElementTypes.Door;
            case "кровля" or "крыша": return ElementTypes.Roof;
            case "ферма": return ElementTypes.Truss;
            case "furniture" or "tree" or "car" or "object" or "model": return ElementTypes.Asset;
            case "wallopening" or "opening" or "wall_hole" or "проём" or "проем" or "проём_в_стене": return ElementTypes.WallOpening;
            case "slabopening" or "slab_hole" or "floor_opening" or "hole" or "shaft" or "atrium" or "hatch" or "stairwell"
                or "отверстие" or "шахта" or "атриум" or "люк" or "отверстие_в_плите": return ElementTypes.SlabOpening;
            default:
                known = false;
                return ElementTypes.Asset;
        }
    }

    private static (double W, double H, double D) DefaultSize(string type) => type switch
    {
        ElementTypes.Foundation => (10, 0.5, 10),
        ElementTypes.Wall => (4, 3, 0.3),
        ElementTypes.CurtainWall => (4, 3, 0.15),
        ElementTypes.Column => (0.4, 3, 0.4),
        ElementTypes.Core => (6, 3, 6),
        ElementTypes.Beam => (6, 0.5, 0.3),
        ElementTypes.Truss => (12, 1.5, 0.4),
        ElementTypes.Diagrid => (0.4, 3.6, 0.4),
        ElementTypes.Slab => (10, 0.22, 10),
        ElementTypes.CantileverSlab => (10, 0.25, 10),
        ElementTypes.Shell => (16, 6, 16),
        ElementTypes.Roof => (10, 2.5, 8),
        ElementTypes.Stairs => (1.1, 3, 3.6),
        ElementTypes.Window => (1.5, 1.5, 0.36),
        ElementTypes.Door => (1.0, 2.2, 0.36),
        ElementTypes.Pool => (4, 1.5, 8),
        ElementTypes.Terrace => (6, 0.3, 4),
        ElementTypes.WallOpening => (1.0, 2.1, 0.36),
        ElementTypes.SlabOpening => (1.5, 0.3, 1.5),
        _ => (1, 1, 1)
    };

    private static string DefaultMaterial(string type) => type switch
    {
        ElementTypes.Wall => "brick",
        ElementTypes.CurtainWall or ElementTypes.Window => "glass",
        ElementTypes.Door or ElementTypes.Terrace => "wood",
        ElementTypes.Pool => "water",
        ElementTypes.Roof => "metal_roof",
        ElementTypes.Truss or ElementTypes.Diagrid => "steel",
        ElementTypes.Shell => "concrete_shell",
        ElementTypes.WallOpening or ElementTypes.SlabOpening => "void",
        _ => "concrete"
    };

    private static string DefaultName(string type) => type switch
    {
        ElementTypes.Foundation => "Фундамент",
        ElementTypes.Wall => "Стена",
        ElementTypes.CurtainWall => "Витраж",
        ElementTypes.Column => "Колонна",
        ElementTypes.Core => "Ядро жёсткости",
        ElementTypes.Beam => "Балка",
        ElementTypes.Truss => "Ферма",
        ElementTypes.Diagrid => "Диагрид",
        ElementTypes.Slab => "Перекрытие",
        ElementTypes.CantileverSlab => "Консольное перекрытие",
        ElementTypes.Shell => "Оболочка",
        ElementTypes.Roof => "Кровля",
        ElementTypes.Stairs => "Лестница",
        ElementTypes.Window => "Окно",
        ElementTypes.Door => "Дверь",
        ElementTypes.Pool => "Бассейн",
        ElementTypes.Terrace => "Терраса",
        ElementTypes.WallOpening => "Проём",
        ElementTypes.SlabOpening => "Отверстие в плите",
        _ => "Объект"
    };
}
