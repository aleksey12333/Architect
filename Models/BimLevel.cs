using System.Text.Json.Serialization;
using AIArchitectStudio.Rendering;

namespace AIArchitectStudio.Models;

/// <summary>
/// Этаж постройки (Level Revit API): Id, имя, отметка и постройка. Порядковый номер <see cref="Index"/> не хранится —
/// это позиция этажа в списке этажей постройки, отсортированном по отметке (<see cref="BimLevelTable"/>), поэтому
/// ArchitecturalElement.LevelIndex, привязанный к этажу через LevelId, вычисляется, а не задаётся.
/// <para>Фаза 3, шаг 1: Position.Y элементов остаётся абсолютным, этаж — метаданные (отметка синхронизируется с геометрией).</para>
/// </summary>
public sealed class BimLevel : IBimElement
{
    private BimUniqueId.Cache? _uniqueId;
    private double _elevation;

    public string Id { get; init; } = string.Empty;

    [JsonIgnore] public Guid UniqueId => BimUniqueId.Resolve(ref _uniqueId, Id, BimUniqueId.LevelNamespace);

    [JsonIgnore] public BimCategory Category => BimCategory.Level;

    public string Name { get; set; } = string.Empty;

    /// <summary>Постройка, которой принадлежит этаж (BuildingGroup.Id).</summary>
    public string BuildingGroupId { get; init; } = BuildingGroup.DefaultGroupId;

    /// <summary>Отметка этажа, м. Меняется через таблицу (<see cref="BimLevelTable.SetElevation"/>) — она пересортировывает этажи.</summary>
    public double Elevation
    {
        get => _elevation;
        init => _elevation = value;
    }

    /// <summary>Порядковый номер этажа в постройке (позиция по отметке + начало нумерации); пересчитывается таблицей.</summary>
    [JsonIgnore] public int Index { get; internal set; }

    /// <summary>Таблица этажей документа; null — этаж выбыл из таблицы (элементы с такой привязкой берут номер из своего слота).</summary>
    [JsonIgnore] public BimLevelTable? Table { get; internal set; }

    internal void SetElevationCore(double elevation) => _elevation = elevation;

    public BimLevel Clone() => new() { Id = Id, Name = Name, BuildingGroupId = BuildingGroupId, Elevation = Elevation };

    /// <summary>Подпись этажа по порядковому номеру: «Этаж 1» для нулевого, «Подземный этаж 1» для −1.</summary>
    public static string DefaultName(int index) => index >= 0 ? $"Этаж {index + 1}" : $"Подземный этаж {-index}";
}

/// <summary>
/// Этажи документа по постройкам: в каждой постройке — список, отсортированный по отметке (устойчиво: при равных отметках
/// сохраняется прежний порядок), и начало нумерации Origin (≤ 0 — подземные этажи). Номер этажа = Origin + позиция.
/// <para>
/// Элемент привязывается к этажу (<see cref="Bind"/>) при попадании в документ; привязанный элемент читает LevelIndex у этажа,
/// присваивание LevelIndex — выбор (или создание) этажа с этим номером через <see cref="Resolve"/>. Отметки этажей
/// синхронизируются с геометрией (<see cref="Synchronize"/>) без перестановки этажей: порядок номеров не меняется сам собой.
/// </para>
/// Потокобезопасна: песочницы Совета КБ и фоновое сохранение обращаются к ней не из UI-потока.
/// </summary>
public sealed class BimLevelTable
{
    /// <summary>Шаг отметки нового этажа без геометрии, м (как LevelHeight по умолчанию).</summary>
    public const double DefaultStoreyHeight = 3.0;

    private sealed class Stack
    {
        public readonly List<BimLevel> Levels = [];
        public int Origin;
    }

    private readonly Dictionary<string, Stack> _stacks = new(StringComparer.Ordinal);
    private readonly List<string> _groupOrder = [];
    private readonly Dictionary<string, BimLevel> _byId = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    public int Count
    {
        get
        {
            lock (_gate) return _byId.Count;
        }
    }

    /// <summary>Все этажи: постройки в порядке появления, этажи — по номеру.</summary>
    public IReadOnlyList<BimLevel> All
    {
        get
        {
            lock (_gate) return [.. _groupOrder.SelectMany(g => _stacks[g].Levels)];
        }
    }

    /// <summary>Этажи постройки по номеру (снимок).</summary>
    public IReadOnlyList<BimLevel> LevelsOf(string groupId)
    {
        lock (_gate) return _stacks.TryGetValue(groupId, out var s) ? [.. s.Levels] : [];
    }

    public BimLevel? Find(string? id)
    {
        if (id is null) return null;
        lock (_gate) return _byId.GetValueOrDefault(id);
    }

    /// <summary>Этаж постройки с номером index; недостающие этажи создаются (выше — с шагом 3 м, ниже — подземные).</summary>
    public BimLevel Resolve(string? groupId, int index)
    {
        groupId ??= BuildingGroup.DefaultGroupId;
        lock (_gate)
        {
            var stack = StackOf(groupId);
            if (stack.Levels.Count == 0) stack.Origin = Math.Min(0, index);
            while (index < stack.Origin)
            {
                double below = stack.Levels.Count > 0 ? stack.Levels[0].Elevation - DefaultStoreyHeight : 0;
                stack.Origin--;
                stack.Levels.Insert(0, Create(groupId, stack.Origin, below));
            }
            while (index >= stack.Origin + stack.Levels.Count)
            {
                double above = stack.Levels.Count > 0 ? stack.Levels[^1].Elevation + DefaultStoreyHeight : 0;
                stack.Levels.Add(Create(groupId, stack.Origin + stack.Levels.Count, above));
            }
            Reindex(stack);
            return stack.Levels[index - stack.Origin];
        }
    }

    /// <summary>
    /// Привязывает элемент к этажу этой таблицы: уже привязан к ней — ничего; привязан к этажу другой таблицы (файл, документ,
    /// из которого взят клон) — этажи той постройки копируются сюда с теми же Id, именами и отметками; иначе — этаж по LevelId,
    /// если он есть в постройке под тем же номером, или по номеру LevelIndex.
    /// </summary>
    public void Bind(ArchitecturalElement e)
    {
        var current = e.Level;
        if (current?.Table == this) return;
        lock (_gate)
        {
            if (current?.Table is { } foreign && !_stacks.ContainsKey(current.BuildingGroupId)) Import(foreign, current.BuildingGroupId);
            if (e.LevelId is { } id && _byId.TryGetValue(id, out var own) && own.BuildingGroupId == e.BuildingGroupId && own.Index == e.LevelIndex)
            {
                e.AttachLevel(own);
                return;
            }
        }
        e.AttachLevel(Resolve(e.BuildingGroupId, e.LevelIndex));
    }

    /// <summary>
    /// Новая отметка этажа: этажи постройки пересортировываются по отметке, номера пересчитываются — LevelIndex привязанных
    /// элементов следует за позицией этажа.
    /// </summary>
    public void SetElevation(BimLevel level, double elevation)
    {
        lock (_gate)
        {
            if (level.Table != this || !_stacks.TryGetValue(level.BuildingGroupId, out var stack)) return;
            level.SetElevationCore(elevation);
            var sorted = stack.Levels.OrderBy(l => l.Elevation).ToList();
            stack.Levels.Clear();
            stack.Levels.AddRange(sorted);
            Reindex(stack);
        }
    }

    /// <summary>
    /// Отметки этажей по геометрии (StructuralAxesBuilder.LevelElevations: низ стен/колонн этажа, верх перекрытий). Этаж без
    /// геометрии — на 3 м выше предыдущего; отметка не опускается ниже предыдущей, поэтому порядок этажей не меняется.
    /// Недостающие этажи (номера элементов) создаются.
    /// </summary>
    public void Synchronize(IEnumerable<ArchitecturalElement> elements)
    {
        foreach (var group in elements.GroupBy(e => e.BuildingGroupId ?? BuildingGroup.DefaultGroupId))
        {
            var items = group.ToList();
            var marks = StructuralAxesBuilder.LevelElevations(items).ToDictionary(m => m.Level, m => m.Elevation);
            int min = items.Min(e => e.LevelIndex), max = items.Max(e => e.LevelIndex);
            if (marks.Count > 0) max = Math.Max(max, marks.Keys.Max());
            Resolve(group.Key, min);
            Resolve(group.Key, max);
            lock (_gate)
            {
                var stack = _stacks[group.Key];
                double previous = double.NegativeInfinity;
                for (int i = 0; i < stack.Levels.Count; i++)
                {
                    int index = stack.Origin + i;
                    double target = marks.TryGetValue(index, out var y) ? y
                        : double.IsFinite(previous) ? previous + DefaultStoreyHeight : stack.Levels[i].Elevation;
                    stack.Levels[i].SetElevationCore(Math.Max(target, previous));
                    previous = stack.Levels[i].Elevation;
                }
            }
        }
    }

    /// <summary>
    /// Таблица из этажей файла: в постройке — по отметке (при равных — порядок файла); начало нумерации — из старого LevelIndex
    /// элементов, привязанных по LevelId (у согласованного файла номера совпадают), иначе 0. Элементы привязываются к этажам.
    /// </summary>
    public static BimLevelTable FromLevels(IEnumerable<BimLevel> levels, IReadOnlyList<ArchitecturalElement> elements)
    {
        var table = new BimLevelTable();
        lock (table._gate)
        {
            foreach (var group in levels.Where(l => !string.IsNullOrWhiteSpace(l.Id)).GroupBy(l => l.BuildingGroupId ?? BuildingGroup.DefaultGroupId))
            {
                var stack = table.StackOf(group.Key);
                foreach (var source in group.OrderBy(l => l.Elevation))
                {
                    if (table._byId.ContainsKey(source.Id)) continue;
                    var level = source.Clone();
                    level.Table = table;
                    stack.Levels.Add(level);
                    table._byId[level.Id] = level;
                }
                var anchor = elements.FirstOrDefault(e => e.BuildingGroupId == group.Key && e.LevelId is { } id && table._byId.TryGetValue(id, out var l) && l.BuildingGroupId == group.Key);
                stack.Origin = anchor is null ? 0 : anchor.LevelIndex - stack.Levels.IndexOf(table._byId[anchor.LevelId!]);
                Reindex(stack);
            }
        }
        foreach (var e in elements)
        {
            if (e.LevelId is { } id && table.Find(id) is { } level && level.BuildingGroupId == e.BuildingGroupId) e.AttachLevel(level);
            else e.AttachLevel(table.Resolve(e.BuildingGroupId, e.LevelIndex));
        }
        return table;
    }

    /// <summary>Миграция файлов без этажей (до v6 и v6 фазы 2): этажи из LevelElevations, привязка по старому LevelIndex.</summary>
    public static BimLevelTable FromElevations(IReadOnlyList<ArchitecturalElement> elements)
    {
        var table = new BimLevelTable();
        foreach (var e in elements) e.AttachLevel(table.Resolve(e.BuildingGroupId, e.LevelIndex));
        table.Synchronize(elements);
        return table;
    }

    // ───────────────────────────── Внутреннее (под _gate) ─────────────────────────────

    private Stack StackOf(string groupId)
    {
        if (!_stacks.TryGetValue(groupId, out var stack))
        {
            stack = new Stack();
            _stacks[groupId] = stack;
            _groupOrder.Add(groupId);
        }
        return stack;
    }

    private BimLevel Create(string groupId, int index, double elevation)
    {
        string id = DeterministicId(groupId, index);
        for (int n = 2; _byId.ContainsKey(id); n++) id = DeterministicId(groupId, index) + "_" + n;
        var level = new BimLevel { Id = id, Name = BimLevel.DefaultName(index), BuildingGroupId = groupId, Elevation = elevation, Table = this };
        _byId[id] = level;
        return level;
    }

    /// <summary>Копия этажей постройки другой таблицы (те же Id, имена, отметки и начало нумерации).</summary>
    private void Import(BimLevelTable foreign, string groupId)
    {
        var source = foreign.LevelsOf(groupId);
        if (source.Count == 0) return;
        var stack = StackOf(groupId);
        stack.Origin = source[0].Index;
        foreach (var l in source)
        {
            var level = l.Clone();
            if (_byId.ContainsKey(level.Id)) level = new BimLevel { Id = level.Id + "_" + _byId.Count, Name = level.Name, BuildingGroupId = groupId, Elevation = level.Elevation };
            level.Table = this;
            stack.Levels.Add(level);
            _byId[level.Id] = level;
        }
        Reindex(stack);
    }

    private static void Reindex(Stack stack)
    {
        for (int i = 0; i < stack.Levels.Count; i++) stack.Levels[i].Index = stack.Origin + i;
    }

    /// <summary>Id этажа без случайности: «lvl_» + 10 hex UUID v5 от постройки и номера (файл и эталон детерминированы).</summary>
    private static string DeterministicId(string groupId, int index) =>
        "lvl_" + BimUniqueId.Create(BimUniqueId.LevelNamespace, groupId + "\u001f" + index.ToString(System.Globalization.CultureInfo.InvariantCulture)).ToString("N")[..10];
}
