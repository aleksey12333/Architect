using System.Collections.ObjectModel;
using AIArchitectStudio.Models;
using AIArchitectStudio.Models.Transactions;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AIArchitectStudio.Services;

/// <summary>
/// Центральное хранилище BIM-сцены: элементы, постройки участка, фильтр этажей, выделение,
/// журнал транзакций Undo/Redo (дельты изменений, а не снимки сцены), уведомление DocumentChanged(added, modified, deleted)
/// и синхронное перемещение окон/дверей вместе со стеной-родителем (HostElementId).
/// </summary>
public sealed partial class SceneStateService : ObservableObject, ITransactionDocument
{
    public const int AllLevels = -1;

    /// <summary>Шагов Undo: шаг хранит только изменённые слоты, поэтому история длиннее прежних 100 снимков всей сцены.</summary>
    public const int MaxHistory = 1000;

    private const double SiteGap = 4.0;

    public ObservableCollection<ArchitecturalElement> Elements { get; } = [];
    public ObservableCollection<BuildingGroup> BuildingGroups { get; } = [];

    /// <summary>
    /// Явные координационные оси построек (с чертежа или введённые пользователем). Для постройки без явных осей
    /// сетка вычисляется автоматически по несущим конструкциям.
    /// </summary>
    public ObservableCollection<StructuralGridAxis> GridAxes { get; } = [];

    /// <summary>Изменился набор или координаты явных осей.</summary>
    public event EventHandler? GridAxesChanged;

    /// <summary>-1 — показывать все этажи, иначе индекс активного этажа.</summary>
    [ObservableProperty]
    public partial int ActiveLevelFilter { get; set; } = AllLevels;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    public partial ArchitecturalElement? SelectedElement { get; set; }

    [ObservableProperty]
    public partial string ProjectName { get; set; } = "Новый проект";

    [ObservableProperty]
    public partial string? ProjectFilePath { get; set; }

    [ObservableProperty]
    public partial bool IsDirty { get; set; }

    public bool HasSelection => SelectedElement is not null;

    /// <summary>
    /// Мультивыделение (Shift + клик): все выделенные элементы; SelectedElement — главный (последний добавленный) элемент группы.
    /// Обычное присваивание SelectedElement сбрасывает мультивыделение к одному элементу.
    /// </summary>
    public ObservableCollection<ArchitecturalElement> SelectedElements { get; } = [];

    /// <summary>Изменился набор выделенных элементов (одиночный или групповой выбор).</summary>
    public event EventHandler? SelectionChanged;

    public bool IsMultiSelection => SelectedElements.Count > 1;

    private bool _selectionSync;

    partial void OnSelectedElementChanged(ArchitecturalElement? value)
    {
        if (_selectionSync) return;
        SelectedElements.Clear();
        if (value is not null) SelectedElements.Add(value);
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Набор выделенных элементов с главным элементом primary (null — последний из набора).</summary>
    public void SetSelection(IEnumerable<ArchitecturalElement> elements, ArchitecturalElement? primary = null)
    {
        var list = elements.Where(Elements.Contains).Distinct().ToList();
        primary = primary is not null && list.Contains(primary) ? primary : list.LastOrDefault();
        _selectionSync = true;
        try
        {
            SelectedElements.Clear();
            foreach (var e in list) SelectedElements.Add(e);
            if (!ReferenceEquals(SelectedElement, primary)) SelectedElement = primary;
        }
        finally
        {
            _selectionSync = false;
        }
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Shift + клик: добавляет элемент в мультивыделение (он становится главным) или убирает его из набора.</summary>
    public void ToggleSelection(ArchitecturalElement element)
    {
        var list = SelectedElements.ToList();
        if (list.Remove(element))
            SetSelection(list, ReferenceEquals(SelectedElement, element) ? list.LastOrDefault() : SelectedElement);
        else
            SetSelection(list.Append(element), element);
    }

    /// <summary>Однотипные элементы на этаже: тот же тип, этаж и постройка, что у образца (видимые фильтром этажа).</summary>
    public List<ArchitecturalElement> FindSimilarOnFloor(ArchitecturalElement sample) =>
        Elements.Where(e => e.Type == sample.Type && e.LevelIndex == sample.LevelIndex && e.BuildingGroupId == sample.BuildingGroupId && IsElementVisible(e)).ToList();

    /// <summary>Выделяет в 1 клик все однотипные элементы этажа (колонны, балки, окна…) главного элемента.</summary>
    public int SelectSimilarOnFloor()
    {
        if (SelectedElement is not { } sample) return 0;
        var list = FindSimilarOnFloor(sample);
        SetSelection(list, sample);
        return list.Count;
    }

    public bool CanUndo => _journal.UndoCount > 0;
    public bool CanRedo => _journal.RedoCount > 0;

    /// <summary>Число шагов в стеке Undo (атомарность пакетных операций проверяется автотестом).</summary>
    public int UndoDepth => _journal.UndoCount;

    /// <summary>
    /// Что изменилось в документе: добавленные, изменённые и удалённые элементы (полная перестройка — только при IsReset).
    /// Внутри явной транзакции (<see cref="BeginUndoTransaction"/>) изменения копятся и приходят одним событием при её закрытии.
    /// </summary>
    public event EventHandler<DocumentChangedEventArgs>? DocumentChanged;

    /// <summary>
    /// Грубый сигнал для списков интерфейса (дерево объектов, этажи): изменился состав документа, этажи, Undo/Redo или загрузка.
    /// Выводится из DocumentChanged; вьюпорт и расчёты подписаны на DocumentChanged.
    /// </summary>
    public event EventHandler? SceneChanged;

    /// <summary>Изменённые элементы (выводится из DocumentChanged.Modified): синхронизация инспектора.</summary>
    public event EventHandler<IReadOnlyList<ArchitecturalElement>>? ElementsTransformed;

    /// <summary>Максимальный индекс этажа с учётом RepeatLevels.</summary>
    public int MaxLevelIndex => Elements.Count == 0 ? 0 : Elements.Max(e => e.LevelIndex + Math.Max(1, e.RepeatLevels) - 1);

    public ArchitecturalElement? FindById(string? id) =>
        string.IsNullOrEmpty(id) ? null : Elements.FirstOrDefault(e => e.Id == id);

    public BuildingGroup? FindGroup(string? id) =>
        string.IsNullOrEmpty(id) ? null : BuildingGroups.FirstOrDefault(g => g.Id == id);

    public IEnumerable<ArchitecturalElement> GetHostedElements(string hostId) =>
        Elements.Where(e => e.HostElementId == hostId);

    /// <summary>
    /// Проёмы, которые вырезаются в элементе: окна, двери и wall_opening стены; slab_opening плиты, покрытия или фундамента
    /// (привязанные по HostElementId, а также непривязанные отверстия той же постройки и этажа, попадающие в контур плиты).
    /// </summary>
    public List<ArchitecturalElement> GetOpeningsFor(ArchitecturalElement host) => OpeningsFor(host, Elements);

    public static List<ArchitecturalElement> OpeningsFor(ArchitecturalElement host, IEnumerable<ArchitecturalElement> elements)
    {
        if (ElementTypes.IsHostType(host.Type))
            return elements.Where(e => e.HostElementId == host.Id && ElementTypes.IsHostedType(e.Type)).ToList();
        if (!host.CanHostSlabOpenings) return [];

        var result = new List<ArchitecturalElement>();
        List<(double X, double Z)>? outline = null;
        foreach (var o in elements)
        {
            if (o.Type != ElementTypes.SlabOpening) continue;
            if (o.HostElementId == host.Id) { result.Add(o); continue; }
            if (o.HostElementId is not null || o.BuildingGroupId != host.BuildingGroupId || o.LevelIndex != host.LevelIndex) continue;
            outline ??= AIArchitectStudio.Rendering.ElementGeometryBuilder.WorldOutline(host);
            if (PointInPolygon(o.X, o.Z, outline)) result.Add(o);
        }
        return result;
    }

    public static bool PointInPolygon(double x, double z, IReadOnlyList<(double X, double Z)> poly)
    {
        bool inside = false;
        for (int i = 0, j = poly.Count - 1; i < poly.Count; j = i++)
        {
            var a = poly[i];
            var b = poly[j];
            if ((a.Z > z) != (b.Z > z) && x < (b.X - a.X) * (z - a.Z) / (b.Z - a.Z) + a.X) inside = !inside;
        }
        return inside;
    }

    /// <summary>Видимость элемента по фильтру этажа: верхние этажи и перекрытие/крыша активного этажа скрыты.</summary>
    public bool IsElementVisible(ArchitecturalElement element)
    {
        // Отверстие плиты видно вместе со своей плитой.
        if (element.Type == ElementTypes.SlabOpening && FindById(element.HostElementId) is { } slab && !ReferenceEquals(slab, element))
            return IsElementVisible(slab);
        var group = FindGroup(element.BuildingGroupId);
        if (group is { IsVisible: false }) return false;
        if (ActiveLevelFilter < 0) return true;
        if (element.LevelIndex > ActiveLevelFilter) return false;
        if (element.LevelIndex == ActiveLevelFilter && ElementTypes.IsCeilingType(element.Type)) return false;
        return true;
    }

    public IEnumerable<ArchitecturalElement> VisibleElements => Elements.Where(IsElementVisible);

    partial void OnActiveLevelFilterChanged(int value)
    {
        // Видимость меняется у всей сцены, сами элементы — нет: полная перестройка вида без пометки «изменён».
        DocumentChanged?.Invoke(this, DocumentChangedEventArgs.ResetOf(DocumentChangeReason.Filter));
        SceneChanged?.Invoke(this, EventArgs.Empty);
    }

    // ───────────────────────────── Структурные операции ─────────────────────────────

    public void AddElement(ArchitecturalElement element, bool recordUndo = true)
    {
        if (recordUndo) PushUndo("Добавление элемента");
        EnsureGroup(element.BuildingGroupId);
        Elements.Add(element);
        Changed(added: [element]);
    }

    public void AddElements(IEnumerable<ArchitecturalElement> elements, bool recordUndo = true)
    {
        if (recordUndo) PushUndo("Добавление элементов");
        var added = new List<ArchitecturalElement>();
        foreach (var e in elements)
        {
            EnsureGroup(e.BuildingGroupId);
            Elements.Add(e);
            added.Add(e);
        }
        Changed(added: added, structural: true);
    }

    /// <summary>
    /// Честное удаление по клавише Delete: окно или дверь не замуровывают стену, а превращаются в сквозной проём
    /// wall_opening с теми же координатами и габаритами; удаление самого проёма (wall_opening / slab_opening) делает стену
    /// или плиту снова сплошной; остальные элементы удаляются вместе с привязанными проёмами.
    /// Возвращает элемент, который нужно оставить выделенным (проём на месте удалённого заполнения), или null.
    /// </summary>
    public ArchitecturalElement? DeleteElement(ArchitecturalElement element)
    {
        if (!Elements.Contains(element)) return null;
        if (element.Type is ElementTypes.Window or ElementTypes.Door)
        {
            PushUndo("Удаление заполнения проёма");
            bool door = element.Type == ElementTypes.Door;
            element.Type = ElementTypes.WallOpening;
            element.Material = "void";
            element.Name = (door ? "Дверной проём" : "Оконный проём") + (string.IsNullOrWhiteSpace(element.Name) ? string.Empty : $" ({element.Name})");
            Changed(modified: [element], structural: true);
            return element;
        }
        RemoveElement(element);
        return null;
    }

    /// <summary>Добавляет сквозной проём wall_opening в стену (по центру стены, 1.2 × 1.5 м, низ на 0.9 м от пола).</summary>
    public ArchitecturalElement AddWallOpening(ArchitecturalElement wall, double width = 1.2, double height = 1.5, double sill = 0.9)
    {
        width = Math.Min(width, Math.Max(0.3, wall.Width - 0.2));
        height = Math.Min(height, Math.Max(0.3, wall.Height - 0.1));
        sill = Math.Clamp(sill, 0, Math.Max(0, wall.Height - height - 0.05));
        var opening = new ArchitecturalElement
        {
            Type = ElementTypes.WallOpening,
            Name = "Проём",
            BuildingGroupId = wall.BuildingGroupId,
            LevelIndex = wall.LevelIndex,
            Material = "void",
            HostElementId = wall.Id,
            Position = [wall.X, wall.BaseY + sill + height / 2, wall.Z],
            Size = [width, height, wall.Depth + ParametricSolverService.OpeningClearance],
            Rotation = [0, wall.RotationY, 0]
        };
        AddElement(opening);
        return opening;
    }

    /// <summary>Добавляет сквозное отверстие slab_opening в плиту, плоское покрытие или фундамент (по центру, 1.5 × 1.5 м).</summary>
    public ArchitecturalElement AddSlabOpening(ArchitecturalElement slab, double width = 1.5, double depth = 1.5)
    {
        var (cx, cz) = PlanCenter(slab);
        var opening = new ArchitecturalElement
        {
            Type = ElementTypes.SlabOpening,
            Name = "Отверстие в плите",
            BuildingGroupId = slab.BuildingGroupId,
            LevelIndex = slab.LevelIndex,
            Material = "void",
            HostElementId = slab.Id,
            Position = [cx, slab.Y, cz],
            Size = [Math.Min(width, Math.Max(0.2, slab.Width * 0.8)), slab.Height + ParametricSolverService.OpeningClearance, Math.Min(depth, Math.Max(0.2, slab.Depth * 0.8))],
            Rotation = [0, slab.RotationY, 0]
        };
        AddElement(opening);
        return opening;
    }

    /// <summary>Центр элемента в плане (для полигональных плит — центр габарита контура).</summary>
    public static (double X, double Z) PlanCenter(ArchitecturalElement e)
    {
        var (minX, maxX, minZ, maxZ) = Bounds([e]);
        return ((minX + maxX) / 2, (minZ + maxZ) / 2);
    }

    /// <summary>
    /// Изменение параметров элемента (профиль сечения, форма кровли и т.п.) одним шагом Undo с перестройкой его
    /// геометрии и геометрии хоста/проёмов.
    /// </summary>
    public void UpdateElement(ArchitecturalElement element, Action<ArchitecturalElement> mutate)
    {
        PushUndo("Изменение параметров");
        mutate(element);
        var changed = new List<ArchitecturalElement> { element };
        changed.AddRange(GetHostedElements(element.Id));
        Changed(modified: changed);
    }

    /// <summary>Сообщает вьюпорту и расчётам, что параметры элементов изменены на месте (в рамках уже записанного шага Undo).</summary>
    public void NotifyElementsChanged(IReadOnlyList<ArchitecturalElement> changed) => Changed(modified: changed, levelsDirty: true);

    // ───────────────────────────── Координационные оси ─────────────────────────────

    /// <summary>Явные оси постройки (пусто — оси вычисляются автоматически).</summary>
    public IEnumerable<StructuralGridAxis> AxesOf(string groupId) => GridAxes.Where(a => a.BuildingGroupId == groupId);

    /// <summary>Замена осей одной постройки (одним шагом Undo).</summary>
    public void SetGroupAxes(string groupId, IEnumerable<StructuralGridAxis> axes, bool recordUndo = true)
    {
        if (recordUndo) PushUndo("Координационные оси");
        foreach (var a in GridAxes.Where(a => a.BuildingGroupId == groupId).ToList()) GridAxes.Remove(a);
        foreach (var a in axes)
        {
            a.BuildingGroupId = groupId;
            GridAxes.Add(a);
        }
        IsDirty = true;
        GridAxesChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Сообщает вьюпорту и экспорту, что ось отредактирована на месте (подпись или координата).</summary>
    public void NotifyGridAxesChanged()
    {
        IsDirty = true;
        GridAxesChanged?.Invoke(this, EventArgs.Empty);
    }

    private void ReplaceAxes(IEnumerable<StructuralGridAxis> axes)
    {
        GridAxes.Clear();
        foreach (var a in axes) GridAxes.Add(a);
        GridAxesChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Удаляет элемент вместе со всеми привязанными к нему окнами и дверями.</summary>
    public void RemoveElement(ArchitecturalElement element, bool recordUndo = true)
    {
        if (!Elements.Contains(element)) return;
        if (recordUndo) PushUndo("Удаление");

        var toRemove = new List<ArchitecturalElement>();
        CollectWithChildren(element, toRemove);
        foreach (var e in toRemove) Elements.Remove(e);

        DropRemovedFromSelection(toRemove);
        RemoveEmptyGroups();
        ClampLevelFilter();
        Changed(deleted: toRemove);
    }

    /// <summary>Удаляет набор элементов (мультивыделение) вместе с их проёмами одним шагом Undo.</summary>
    public void RemoveElements(IReadOnlyList<ArchitecturalElement> elements, bool recordUndo = true)
    {
        var toRemove = new List<ArchitecturalElement>();
        foreach (var e in elements.Where(Elements.Contains)) CollectWithChildren(e, toRemove);
        if (toRemove.Count == 0) return;
        if (recordUndo) PushUndo("Удаление");
        foreach (var e in toRemove) Elements.Remove(e);
        DropRemovedFromSelection(toRemove);
        RemoveEmptyGroups();
        ClampLevelFilter();
        Changed(deleted: toRemove);
    }

    private void DropRemovedFromSelection(List<ArchitecturalElement> removed)
    {
        if (!SelectedElements.Any(removed.Contains) && (SelectedElement is null || !removed.Contains(SelectedElement))) return;
        var keep = SelectedElements.Where(e => !removed.Contains(e)).ToList();
        SetSelection(keep, SelectedElement is { } p && keep.Contains(p) ? p : null);
    }

    /// <summary>Дублирует элемент (и его проёмы) со смещением, возвращает копию-родителя.</summary>
    public ArchitecturalElement DuplicateElement(ArchitecturalElement element, double offsetX = 1.0, double offsetZ = 1.0)
    {
        PushUndo("Дублирование");
        var source = new List<ArchitecturalElement>();
        CollectWithChildren(element, source);

        var idMap = source.ToDictionary(e => e.Id, _ => ArchitecturalElement.NewId());
        ArchitecturalElement? root = null;
        var added = new List<ArchitecturalElement>();
        foreach (var src in source)
        {
            var copy = src.Clone();
            copy.Id = idMap[src.Id];
            if (copy.HostElementId is not null && idMap.TryGetValue(copy.HostElementId, out var newHost))
                copy.HostElementId = newHost;
            copy.SetPosition(copy.X + offsetX, copy.Y, copy.Z + offsetZ);
            if (src == element)
            {
                copy.Name = string.IsNullOrWhiteSpace(src.Name) ? string.Empty : src.Name + " (копия)";
                root = copy;
            }
            Elements.Add(copy);
            added.Add(copy);
        }
        Changed(added: added);
        return root!;
    }

    public void ClearScene(bool recordUndo = true)
    {
        if (recordUndo && Elements.Count > 0) PushUndo("Очистка сцены");
        ClearElements();
        BuildingGroups.Clear();
        ReplaceAxes([]);
        ReplaceDimensions([]);
        SelectedElement = null;
        ActiveLevelFilter = AllLevels;
        Changed(reset: true);
    }

    /// <summary>Полная замена сцены (новая сцена или открытие файла).</summary>
    /// <param name="axes">Явные оси построек; null — оставить оси, относящиеся к оставшимся постройкам.</param>
    /// <param name="dimensions">Закреплённые размеры рулетки; null — оставить текущие (при clearHistory — очистить).</param>
    public void LoadScene(IEnumerable<ArchitecturalElement> elements, IEnumerable<BuildingGroup> groups, bool recordUndo, bool clearHistory = false,
        IEnumerable<StructuralGridAxis>? axes = null, IEnumerable<PinnedDimension>? dimensions = null)
    {
        if (recordUndo && !clearHistory) PushUndo("Новая сцена");
        ClearElements();
        // Новый документ (без истории) — новые этажи: этажи прежнего проекта с тем же именем постройки не наследуются.
        if (clearHistory)
        {
            ResetLevels();
            SetSitePlan(null);
        }
        BuildingGroups.Clear();
        foreach (var g in groups) BuildingGroups.Add(g);
        var groupIds = BuildingGroups.Select(g => g.Id).ToHashSet();
        ReplaceAxes(axes?.ToList() ?? GridAxes.Where(a => groupIds.Contains(a.BuildingGroupId)).ToList());
        if (dimensions is not null || clearHistory) ReplaceDimensions((dimensions ?? []).ToList());
        foreach (var e in elements)
        {
            EnsureGroup(e.BuildingGroupId);
            Elements.Add(e);
        }
        SelectedElement = null;
        ClampLevelFilter();
        if (clearHistory)
        {
            ClearHistory();
            IsDirty = false;
        }
        Changed(reset: true);
    }

    /// <summary>
    /// Режим AppendToSite: размещает новые постройки на свободном месте участка (отступ 4 м от существующих зданий,
    /// без пересечения габаритов), не затрагивая существующие здания. Возвращает фактическое смещение по X.
    /// </summary>
    /// <param name="offset">Готовое смещение от C#-солвера; null — рассчитать здесь.</param>
    public double AppendBuildings(IReadOnlyList<BuildingGroup> groups, IReadOnlyList<ArchitecturalElement> elements, (double X, double Z)? offset = null,
        IReadOnlyList<StructuralGridAxis>? axes = null)
    {
        PushUndo("Постройка на участке");

        // Уникальные имена групп, чтобы не слить новую постройку со старой.
        var rename = new Dictionary<string, string>();
        foreach (var g in groups)
        {
            var id = g.Id;
            int n = 2;
            while (BuildingGroups.Any(x => x.Id == id) || rename.ContainsValue(id))
                id = $"{g.Id} ({n++})";
            rename[g.Id] = id;
        }

        var (offsetX, offsetZ) = offset ?? ParametricSolverService.FindFreeSiteOffset(Elements, elements, SiteGap);

        foreach (var g in groups)
        {
            var copy = g.Clone();
            copy.Id = rename[g.Id];
            copy.Name = copy.Id;
            copy.OriginX += offsetX;
            copy.OriginZ += offsetZ;
            BuildingGroups.Add(copy);
        }
        foreach (var e in elements)
        {
            if (rename.TryGetValue(e.BuildingGroupId, out var newGroup)) e.BuildingGroupId = newGroup;
            e.SetPosition(e.X + offsetX, e.Y, e.Z + offsetZ);
            EnsureGroup(e.BuildingGroupId);
            Elements.Add(e);
        }
        foreach (var axis in axes ?? [])
        {
            var copy = axis.Clone();
            if (rename.TryGetValue(copy.BuildingGroupId, out var newGroup)) copy.BuildingGroupId = newGroup;
            copy.Coordinate += copy.IsNumeric ? offsetX : offsetZ;
            GridAxes.Add(copy);
        }
        if (axes is { Count: > 0 }) GridAxesChanged?.Invoke(this, EventArgs.Empty);
        Changed(added: elements, structural: true);
        return offsetX;
    }

    public void SelectById(string? id) => SelectedElement = FindById(id);

    // ───────────────────────────── Трансформации ─────────────────────────────

    /// <summary>
    /// Устанавливает трансформацию элемента. Если элемент — хост (стена), все его окна и двери
    /// (HostElementId) перемещаются и поворачиваются синхронно, сохраняя положение относительно стены.
    /// </summary>
    public void SetTransform(ArchitecturalElement element,
        double x, double y, double z,
        double width, double height, double depth,
        double rotationY, int levelIndex,
        bool recordUndo = true)
    {
        if (!AllFinite(x, y, z, width, height, depth, rotationY)) return;

        double oldX = element.X, oldY = element.Y, oldZ = element.Z, oldRot = element.RotationY;
        double newRot = ArchitecturalElement.NormalizeAngle(rotationY);
        bool moved = oldX != x || oldY != y || oldZ != z || oldRot != newRot;
        bool resized = element.Width != width || element.Height != height || element.Depth != depth;
        bool levelChanged = element.LevelIndex != levelIndex;
        if (!moved && !resized && !levelChanged) return;

        if (recordUndo) PushUndo("Изменение размеров и положения");

        var changed = new List<ArchitecturalElement> { element };
        element.SetPosition(x, y, z);
        element.SetSize(width, height, depth);
        element.SetRotationY(newRot);
        element.LevelIndex = Math.Max(0, levelIndex);

        if (moved)
            MoveHostedChildren(element, oldX, oldY, oldZ, oldRot, changed);
        if (levelChanged)
            foreach (var child in GetHostedElements(element.Id))
            {
                child.LevelIndex = element.LevelIndex;
                if (!changed.Contains(child)) changed.Add(child);
            }

        // Смена этажа — структурное изменение (дерево объектов, список этажей), как прежде.
        Changed(modified: changed, structural: levelChanged, levelsDirty: levelChanged);
    }

    /// <summary>Смещение элемента на вектор (используется Gizmo и кнопками быстрой панели), с привязкой к шагу.</summary>
    public void Translate(ArchitecturalElement element, double dx, double dy, double dz, double snap = 0.1, bool recordUndo = true)
    {
        SetTransform(element,
            Snap(element.X + dx, snap), Snap(element.Y + dy, snap), Snap(element.Z + dz, snap),
            element.Width, element.Height, element.Depth, element.RotationY, element.LevelIndex, recordUndo);
    }

    public void RotateBy(ArchitecturalElement element, double deltaDegrees, bool recordUndo = true)
    {
        SetTransform(element, element.X, element.Y, element.Z,
            element.Width, element.Height, element.Depth, element.RotationY + deltaDegrees, element.LevelIndex, recordUndo);
    }

    /// <summary>
    /// Общий центр группы в плане — центр габаритного прямоугольника всех элементов набора с учётом их поворота и контуров
    /// Polygon2D (пустоты учитываются, только если в наборе нет ничего, кроме них).
    /// </summary>
    public static (double X, double Z) GroupPivot(IReadOnlyList<ArchitecturalElement> elements)
    {
        var solid = elements.Where(e => !ElementTypes.IsVoidType(e.Type)).ToList();
        var (minX, maxX, minZ, maxZ) = Bounds(solid.Count > 0 ? solid : elements);
        return ((minX + maxX) / 2, (minZ + maxZ) / 2);
    }

    /// <summary>
    /// Групповой поворот как единого жёсткого блока: центры корней набора поворачиваются вокруг общего пивота
    /// (по умолчанию — центр габарита группы) по формуле x' = Px + R(δ)·(x − Px), их собственный поворот RotationY
    /// получает то же приращение δ. Проёмы выделенных стен движутся вместе со стеной (MoveHostedChildren), проёмы,
    /// чья стена не выделена, остаются в своей стене. Возвращает фактический пивот.
    /// </summary>
    public (double X, double Z) RotateGroup(IReadOnlyList<ArchitecturalElement> elements, double deltaDegrees, (double X, double Z)? pivot = null, bool recordUndo = true)
    {
        var list = elements.Where(Elements.Contains).Distinct().ToList();
        var ids = list.Select(e => e.Id).ToHashSet();
        // Корни: элементы без хоста в наборе; проёмы с невыделенной стеной поворот не трогает.
        var roots = list.Where(e => e.HostElementId is not { } h || (!ids.Contains(h) && FindById(h) is null)).ToList();
        var center = pivot ?? GroupPivot(roots.Count > 0 ? roots : list);
        if (roots.Count == 0 || !double.IsFinite(deltaDegrees) || Math.Abs(ArchitecturalElement.NormalizeAngle(deltaDegrees)) < 1e-9) return center;
        if (recordUndo) PushUndo("Поворот группы");

        var changed = new List<ArchitecturalElement>();
        foreach (var e in roots)
        {
            double oldX = e.X, oldY = e.Y, oldZ = e.Z, oldRot = e.RotationY;
            var (rx, rz) = ArchitecturalElement.RotateY(oldX - center.X, oldZ - center.Z, deltaDegrees);
            e.SetPosition(center.X + rx, oldY, center.Z + rz);
            e.SetRotationY(oldRot + deltaDegrees);
            changed.Add(e);
            MoveHostedChildren(e, oldX, oldY, oldZ, oldRot, changed);
        }
        Changed(modified: changed);
        return center;
    }

    // ───────────────────────────── Push/Pull, группы, массивы ─────────────────────────────

    /// <summary>
    /// Вытягивание грани (Push/Pull): размер вдоль оси грани становится newLength, противоположная грань неподвижна.
    /// Окна, двери и проёмы стены сохраняют мировые координаты вдоль стены и удерживаются в пределах её новых торцов
    /// и высоты; при изменении толщины стены они остаются в её теле (глубина выреза = толщина + 0.06 м).
    /// </summary>
    public PushPullResult? PushPull(ArchitecturalElement element, PushPullFace face, double newLength, bool recordUndo = true)
    {
        if (!Elements.Contains(element) || !double.IsFinite(newLength)) return null;
        var r = PushPullService.Compute(element, face, newLength);
        if (Math.Abs(r.Delta) < 1e-9) return r;
        if (recordUndo) PushUndo("Вытягивание грани");

        double shiftX = r.CenterX - element.X, shiftZ = r.CenterZ - element.Z;
        element.SetPosition(r.CenterX, r.CenterY, r.CenterZ);
        element.SetSize(r.Width, r.Height, r.Depth);
        var changed = new List<ArchitecturalElement> { element };
        if (ElementTypes.IsHostType(element.Type))
        {
            bool thickness = PushPullService.AxisIndex(face) == 2;
            foreach (var child in GetHostedElements(element.Id).ToList())
            {
                if (thickness)
                {
                    // Проём остаётся в теле стены: смещается вместе с её осью и режет новую толщину насквозь.
                    child.SetPosition(child.X + shiftX, child.Y, child.Z + shiftZ);
                    if (ElementTypes.IsHostedType(child.Type))
                        child.SetSize(child.Width, child.Height, element.Depth + ParametricSolverService.OpeningClearance);
                }
                FitHostedChild(element, child);
                changed.Add(child);
            }
        }
        Changed(modified: changed);
        return r;
    }

    /// <summary>Удерживает проём внутри стены: вдоль оси — между торцами, по высоте — между низом и верхом стены.</summary>
    private static void FitHostedChild(ArchitecturalElement wall, ArchitecturalElement child)
    {
        var (lx, lz) = ArchitecturalElement.RotateY(child.X - wall.X, child.Z - wall.Z, -wall.RotationY);
        double half = Math.Max(0, wall.Width / 2 - child.Width / 2);
        lx = Math.Clamp(lx, -half, half);
        double y = child.Height <= wall.Height
            ? Math.Clamp(child.Y, wall.BaseY + child.Height / 2, wall.TopY - child.Height / 2)
            : wall.Y;
        var (wx, wz) = ArchitecturalElement.RotateY(lx, lz, wall.RotationY);
        child.SetPosition(wall.X + wx, y, wall.Z + wz);
    }

    /// <summary>Пакетное изменение параметров набора элементов одним атомарным шагом Undo.</summary>
    public void UpdateElements(IReadOnlyList<ArchitecturalElement> elements, Action<ArchitecturalElement> mutate, bool recordUndo = true)
    {
        var list = elements.Where(Elements.Contains).Distinct().ToList();
        if (list.Count == 0) return;
        if (recordUndo) PushUndo("Изменение параметров");
        var changed = new List<ArchitecturalElement>();
        foreach (var e in list)
        {
            mutate(e);
            changed.Add(e);
            changed.AddRange(GetHostedElements(e.Id));
        }
        Changed(modified: changed.Distinct().ToList());
    }

    /// <summary>
    /// Синхронное смещение группы элементов (Gizmo, инспектор): проёмы, чей хост тоже выделен, двигаются вместе с ним.
    /// Смещение отсчитывается от исходных позиций starts (Id → [x, y, z]).
    /// </summary>
    public void MoveGroup(IReadOnlyList<ArchitecturalElement> elements, IReadOnlyDictionary<string, double[]> starts, double dx, double dy, double dz)
    {
        var ids = elements.Select(e => e.Id).ToHashSet();
        var changed = new List<ArchitecturalElement>();
        foreach (var e in elements)
        {
            if (e.HostElementId is { } host && ids.Contains(host)) continue;
            if (!starts.TryGetValue(e.Id, out var s)) continue;
            double oldX = e.X, oldY = e.Y, oldZ = e.Z;
            double nx = s[0] + dx, ny = s[1] + dy, nz = s[2] + dz;
            if (Math.Abs(nx - oldX) < 1e-9 && Math.Abs(ny - oldY) < 1e-9 && Math.Abs(nz - oldZ) < 1e-9) continue;
            e.SetPosition(nx, ny, nz);
            changed.Add(e);
            MoveHostedChildren(e, oldX, oldY, oldZ, e.RotationY, changed);
        }
        if (changed.Count == 0) return;
        Changed(modified: changed);
    }

    /// <summary>Применяет профиль сечения (квадрат/круг/труба/двутавр) к колонне или балке — общая логика инспектора и пакетной правки.</summary>
    public static void ApplyProfile(ArchitecturalElement e, string profile)
    {
        if (!ElementTypes.IsProfiledType(e.Type) || e.Type == ElementTypes.Diagrid) return;
        e.ProfileType = profile;
        if (e.Type == ElementTypes.Column) e.Polygon2D = [];
        if (profile is ProfileTypes.Circle or ProfileTypes.Pipe)
        {
            if (e.Type == ElementTypes.Beam) e.SetSize(e.Width, e.Height, e.Height);
            else e.SetSize(e.Width, e.Height, e.Width);
        }
    }

    /// <summary>
    /// Линейный массив: в ряду count экземпляров (исходный + count − 1 копий) с шагом (stepX, stepZ) м; копии стен — вместе
    /// с их проёмами, копии окон и проёмов остаются в той же стене. Один шаг Undo; возвращает все новые элементы-корни.
    /// </summary>
    public List<ArchitecturalElement> CreateLinearArray(IReadOnlyList<ArchitecturalElement> sources, int count, double stepX, double stepZ, bool recordUndo = true)
    {
        count = Math.Clamp(count, 2, 20);
        var roots = Roots(sources);
        if (roots.Count == 0 || !double.IsFinite(stepX) || !double.IsFinite(stepZ) || Math.Abs(stepX) + Math.Abs(stepZ) < 1e-6) return [];
        if (recordUndo) PushUndo("Массив");
        var created = new List<ArchitecturalElement>();
        for (int i = 1; i < count; i++)
            created.AddRange(CopyWithChildren(roots, stepX * i, 0, stepZ * i, 0));
        Changed(added: created, structural: true);
        return created;
    }

    /// <summary>
    /// Тиражирование этажа вверх: копии на LevelIndex + 1 с подъёмом на высоту этажа (по отметкам этажей постройки,
    /// иначе — по LevelHeight). Источник — выделенная группа или весь этаж главного элемента.
    /// </summary>
    public List<ArchitecturalElement> CopyFloorUp(IReadOnlyList<ArchitecturalElement> sources, bool recordUndo = true)
    {
        var roots = Roots(sources);
        if (roots.Count == 0) return [];
        var first = roots[0];
        double height = FloorHeight(first.BuildingGroupId, first.LevelIndex);
        if (recordUndo) PushUndo("Копировать этаж вверх");
        var created = CopyWithChildren(roots, 0, height, 0, 1);
        Changed(added: created, structural: true);
        return created;
    }

    /// <summary>Весь этаж элемента в его постройке (вместе с отверстиями плит и проёмами).</summary>
    public List<ArchitecturalElement> FloorOf(ArchitecturalElement sample) =>
        Elements.Where(e => e.BuildingGroupId == sample.BuildingGroupId && e.LevelIndex == sample.LevelIndex).ToList();

    /// <summary>Высота этажа level постройки: разность отметок соседних этажей, иначе LevelHeight / высота несущих этажа.</summary>
    public double FloorHeight(string groupId, int level)
    {
        var items = Elements.Where(e => e.BuildingGroupId == groupId).ToList();
        var marks = AIArchitectStudio.Rendering.StructuralAxesBuilder.LevelElevations(items);
        var here = marks.FirstOrDefault(m => m.Level == level);
        var above = marks.FirstOrDefault(m => m.Level == level + 1);
        if (here is not null && above is not null && above.Elevation - here.Elevation > 0.5) return above.Elevation - here.Elevation;
        var floor = items.Where(e => e.LevelIndex == level && !ElementTypes.IsVoidType(e.Type)).ToList();
        var bearers = floor.Where(e => e.Type is ElementTypes.Wall or ElementTypes.Column or ElementTypes.CurtainWall).ToList();
        if (bearers.Count > 0)
        {
            double top = floor.Max(e => e.TopY), bottom = bearers.Min(e => e.BaseY);
            if (top - bottom > 0.5) return top - bottom;
        }
        return floor.Count > 0 ? floor.Max(e => e.LevelHeight) : 3.0;
    }

    /// <summary>Корни набора: элементы, чей хост не входит в набор (проёмы выделенной стены копируются вместе с ней).</summary>
    private List<ArchitecturalElement> Roots(IReadOnlyList<ArchitecturalElement> sources)
    {
        var set = sources.Where(Elements.Contains).ToHashSet();
        return set.Where(e => e.HostElementId is not { } h || !set.Any(s => s.Id == h)).ToList();
    }

    /// <summary>Копирует элементы с их проёмами со смещением; ссылки HostElementId внутри копируемого набора переназначаются.</summary>
    private List<ArchitecturalElement> CopyWithChildren(IReadOnlyList<ArchitecturalElement> roots, double dx, double dy, double dz, int levelDelta)
    {
        var source = new List<ArchitecturalElement>();
        foreach (var r in roots) CollectWithChildren(r, source);
        var idMap = source.ToDictionary(e => e.Id, _ => ArchitecturalElement.NewId());
        var created = new List<ArchitecturalElement>();
        foreach (var src in source)
        {
            var copy = src.Clone();
            copy.Id = idMap[src.Id];
            if (copy.HostElementId is not null && idMap.TryGetValue(copy.HostElementId, out var newHost)) copy.HostElementId = newHost;
            copy.SetPosition(copy.X + dx, copy.Y + dy, copy.Z + dz);
            copy.LevelIndex = Math.Max(0, copy.LevelIndex + levelDelta);
            EnsureGroup(copy.BuildingGroupId);
            Elements.Add(copy);
            created.Add(copy);
        }
        return created;
    }

    private void MoveHostedChildren(ArchitecturalElement host, double oldX, double oldY, double oldZ, double oldRot, List<ArchitecturalElement> changed)
    {
        double dy = host.Y - oldY;
        double deltaRot = host.RotationY - oldRot;
        foreach (var child in GetHostedElements(host.Id).ToList())
        {
            // Локальные координаты проёма в системе стены до перемещения.
            var (lx, lz) = ArchitecturalElement.RotateY(child.X - oldX, child.Z - oldZ, -oldRot);
            // Переводим обратно в мир с новой позицией и поворотом стены.
            var (wx, wz) = ArchitecturalElement.RotateY(lx, lz, host.RotationY);

            double cx = child.X, cy = child.Y, cz = child.Z, cRot = child.RotationY;
            child.SetPosition(host.X + wx, child.Y + dy, host.Z + wz);
            child.SetRotationY(child.RotationY + deltaRot);
            changed.Add(child);

            // Вложенные зависимости (например, элемент, привязанный к окну).
            MoveHostedChildren(child, cx, cy, cz, cRot, changed);
        }
    }

    // ───────────────────────────── Вспомогательное ─────────────────────────────

    public void EnsureGroup(string groupId)
    {
        if (string.IsNullOrWhiteSpace(groupId)) return;
        if (BuildingGroups.All(g => g.Id != groupId))
            BuildingGroups.Add(new BuildingGroup(groupId));
    }

    private void RemoveEmptyGroups()
    {
        foreach (var g in BuildingGroups.Where(g => Elements.All(e => e.BuildingGroupId != g.Id)).ToList())
            BuildingGroups.Remove(g);
    }

    private void CollectWithChildren(ArchitecturalElement element, List<ArchitecturalElement> result)
    {
        if (result.Contains(element)) return;
        result.Add(element);
        foreach (var child in GetHostedElements(element.Id).ToList())
            CollectWithChildren(child, result);
    }

    private void ClampLevelFilter()
    {
        if (ActiveLevelFilter > MaxLevelIndex) ActiveLevelFilter = AllLevels;
    }

    /// <summary>Габариты набора элементов в плане с учётом поворота.</summary>
    public static (double MinX, double MaxX, double MinZ, double MaxZ) Bounds(IEnumerable<ArchitecturalElement> elements)
    {
        double minX = double.MaxValue, maxX = double.MinValue, minZ = double.MaxValue, maxZ = double.MinValue;
        foreach (var e in elements)
        {
            IEnumerable<(double X, double Z)> local = e.Polygon2D.Count >= 3
                ? e.Polygon2D.Select(p => (p.X, p.Z))
                : [(-e.Width / 2, -e.Depth / 2), (e.Width / 2, -e.Depth / 2), (e.Width / 2, e.Depth / 2), (-e.Width / 2, e.Depth / 2)];
            foreach (var (px, pz) in local)
            {
                var (rx, rz) = ArchitecturalElement.RotateY(px, pz, e.RotationY);
                minX = Math.Min(minX, e.X + rx); maxX = Math.Max(maxX, e.X + rx);
                minZ = Math.Min(minZ, e.Z + rz); maxZ = Math.Max(maxZ, e.Z + rz);
            }
        }
        return minX == double.MaxValue ? (0, 0, 0, 0) : (minX, maxX, minZ, maxZ);
    }

    public static double Snap(double value, double step) =>
        step <= 0 ? value : Math.Round(Math.Round(value / step, MidpointRounding.AwayFromZero) * step, 6);

    private static bool AllFinite(params double[] values) => values.All(double.IsFinite);
}
