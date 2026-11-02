using AIArchitectStudio.Models;
using AIArchitectStudio.Models.Transactions;

namespace AIArchitectStudio.Services;

/// <summary>
/// Транзакции и журнал изменений (фаза 7). Шаг Undo — <see cref="BimTransaction"/> с дельтами (слоты параметров, тип, этаж,
/// контур, пирог, добавления/удаления элементов, постройки/оси/размеры), а не клон всей сцены. Внешняя семантика прежняя:
/// PushUndo открывает шаг перед изменением, всё изменённое до следующего шага откатывается одним Undo;
/// BeginUndoTransaction поглощает вложенные PushUndo (ровно один шаг), Discard снимает шаг с истории.
/// Уведомление <see cref="DocumentChanged"/> несёт добавленные, изменённые и удалённые элементы; внутри явной транзакции
/// изменения копятся и приходят одним событием при её закрытии (Commit).
/// </summary>
public sealed partial class SceneStateService
{
    private readonly TransactionManager _journal;
    private readonly PendingChanges _pending = new();
    private int _transactionDepth;

    /// <summary>Журнал изменений документа (стеки Undo/Redo, открытый шаг).</summary>
    public TransactionManager Transactions => _journal;

    /// <summary>
    /// Транзакция Undo: шаг открывается один раз, все PushUndo внутри (автоисправления коллизий, SetTransform, AddElement и т.п.)
    /// поглощаются — пакет любых правок становится ровно одним шагом Undo. Discard() убирает шаг, если в итоге ничего не изменилось.
    /// </summary>
    public UndoTransaction BeginUndoTransaction(string? name = null)
    {
        if (_transactionDepth++ == 0) StartStep(name ?? "Пакет изменений");
        return new UndoTransaction(this);
    }

    public bool InUndoTransaction => _transactionDepth > 0;

    private void EndUndoTransaction(bool discard)
    {
        if (_transactionDepth == 0 || --_transactionDepth > 0) return;
        if (discard && _journal.DiscardOpen()) NotifyHistory();
        // Commit: накопленные изменения пакета — одним DocumentChanged.
        FlushChanges();
    }

    /// <summary>Открытая транзакция Undo (using): Dispose закрывает её, Discard — отменяет запись шага.</summary>
    public sealed class UndoTransaction(SceneStateService scene) : IDisposable
    {
        private SceneStateService? _scene = scene;
        private bool _discard;

        public void Discard() => _discard = true;

        public void Dispose()
        {
            _scene?.EndUndoTransaction(_discard);
            _scene = null;
        }
    }

    /// <summary>Открывает новый шаг Undo (вызывать ПЕРЕД изменением); внутри транзакции поглощается.</summary>
    public void PushUndo(string? name = null)
    {
        if (_transactionDepth > 0)
        {
            IsDirty = true;
            return;
        }
        StartStep(name ?? "Изменение модели");
    }

    private void StartStep(string name)
    {
        // Undo/Redo пишет значения в те же объекты: обработчики их PropertyChanging (поле оси в редакторе) не открывают шаг.
        if (_journal.IsApplying) return;
        _journal.Start(name);
        IsDirty = true;
        NotifyHistory();
    }

    public bool Undo() => ApplyHistory(redo: false);

    public bool Redo() => ApplyHistory(redo: true);

    /// <summary>
    /// Откат/повтор шага: изменения применяются к тем же объектам элементов (без клонов), уведомление — только по затронутым
    /// элементам (добавленные/удалённые — по составу документа до и после).
    /// </summary>
    private bool ApplyHistory(bool redo)
    {
        FlushChanges();
        var t = redo ? _journal.PeekRedo() : _journal.PeekUndo();
        if (t is null) return false;
        var before = t.HasMembershipChanges ? new HashSet<ArchitecturalElement>(Elements, ReferenceEqualityComparer.Instance) : null;
        if (redo) _journal.Redo();
        else _journal.Undo();
        QueueTransaction(t, before);
        ClampLevelFilter();
        NotifyHistory();
        FlushChanges(redo ? DocumentChangeReason.Redo : DocumentChangeReason.Undo);
        // Как при прежней замене объектов снимком: инспектор и вьюпорт перечитывают выделенный элемент.
        OnPropertyChanged(nameof(SelectedElement));
        OnPropertyChanged(nameof(HasSelection));
        return true;
    }

    /// <summary>
    /// Прыжок по истории: серия Undo или Redo до состояния, в котором в стеке Undo ровно <paramref name="undoCount"/> шагов
    /// (0 — исходное состояние). Шаги применяются к тем же объектам, уведомление DocumentChanged — одно на весь прыжок
    /// (вьюпорт перестраивается один раз). Возвращает число применённых шагов (отрицательное — откат).
    /// </summary>
    public int JumpToHistory(int undoCount)
    {
        FlushChanges();
        _journal.Commit();
        int target = Math.Clamp(undoCount, 0, _journal.UndoCount + _journal.RedoCount);
        int delta = target - _journal.UndoCount;
        if (delta == 0) return 0;
        bool redo = delta > 0;
        var before = new HashSet<ArchitecturalElement>(Elements, ReferenceEqualityComparer.Instance);
        var touched = new List<BimTransaction>();
        for (int i = 0; i < Math.Abs(delta); i++)
        {
            var t = redo ? _journal.Redo() : _journal.Undo();
            if (t is null) break;
            touched.Add(t);
        }
        // Состав документа сравнивается один раз — до и после всего прыжка.
        foreach (var t in touched) QueueTransaction(t, before);
        ClampLevelFilter();
        NotifyHistory();
        FlushChanges(redo ? DocumentChangeReason.Redo : DocumentChangeReason.Undo);
        OnPropertyChanged(nameof(SelectedElement));
        OnPropertyChanged(nameof(HasSelection));
        return redo ? touched.Count : -touched.Count;
    }

    public void ClearHistory()
    {
        _journal.Clear();
        NotifyHistory();
    }

    private void NotifyHistory()
    {
        OnPropertyChanged(nameof(CanUndo));
        OnPropertyChanged(nameof(CanRedo));
    }

    // ───────────────────────────── DocumentChanged ─────────────────────────────

    /// <summary>Изменения операции документа: внутри явной транзакции копятся, иначе уходят сразу.</summary>
    private void Changed(IEnumerable<ArchitecturalElement>? added = null, IEnumerable<ArchitecturalElement>? modified = null,
        IEnumerable<ArchitecturalElement>? deleted = null, bool structural = false, bool reset = false, bool levelsDirty = false)
    {
        if (added is not null) foreach (var e in added) _pending.Add(e);
        if (deleted is not null) foreach (var e in deleted) _pending.Delete(e);
        if (modified is not null) foreach (var e in modified) _pending.Modify(e);
        _pending.Touched = true;
        _pending.Structural |= structural;
        _pending.Reset |= reset;
        _pending.LevelsDirty |= levelsDirty;
        if (_transactionDepth == 0) FlushChanges();
    }

    /// <summary>Элементы шага Undo/Redo: в документе до и после — изменён, появился — добавлен, исчез — удалён.</summary>
    private void QueueTransaction(BimTransaction t, HashSet<ArchitecturalElement>? before)
    {
        HashSet<ArchitecturalElement>? now = before is null ? null : new(Elements, ReferenceEqualityComparer.Instance);
        var seen = new HashSet<ArchitecturalElement>(ReferenceEqualityComparer.Instance);
        var touched = new List<ArchitecturalElement>();
        foreach (var change in t.Changes)
        {
            switch (change)
            {
                case DocumentStateChange { GroupsChanged: true }:
                    // Начала координат построек входят в геометрию всех их элементов.
                    _pending.Reset = true;
                    break;
                case ElementsCleared cleared:
                    foreach (var e in cleared.Removed) if (seen.Add(e)) touched.Add(e);
                    break;
                default:
                    if (change.Element is { } element && seen.Add(element)) touched.Add(element);
                    break;
            }
        }
        if (now is null && touched.Count > 8) now = new(Elements, ReferenceEqualityComparer.Instance);
        foreach (var e in touched)
        {
            bool isIn = now?.Contains(e) ?? Elements.Contains(e);
            bool wasIn = before?.Contains(e) ?? isIn;
            if (!wasIn && isIn) _pending.Add(e);
            else if (wasIn && !isIn) _pending.Delete(e);
            else if (isIn) _pending.Modify(e);
        }
        _pending.Touched = true;
        _pending.Structural = true;
        _pending.LevelsDirty = true;
    }

    private void FlushChanges(DocumentChangeReason reason = DocumentChangeReason.Edit)
    {
        if (!_pending.Touched) return;
        bool reset = _pending.Reset;
        bool structural = reset || _pending.Structural || _pending.Added.Count > 0 || _pending.Deleted.Count > 0;
        bool levels = structural || _pending.LevelsDirty;
        var args = new DocumentChangedEventArgs([.. _pending.Added], [.. _pending.Modified], [.. _pending.Deleted],
            reset && reason == DocumentChangeReason.Edit ? DocumentChangeReason.Reset : reason, reset, structural);
        _pending.Clear();

        IsDirty = true;
        if (levels) _levelsDirty = true;
        DocumentChanged?.Invoke(this, args);
        if (args.Modified.Count > 0) ElementsTransformed?.Invoke(this, args.Modified);
        if (structural)
        {
            OnPropertyChanged(nameof(MaxLevelIndex));
            SceneChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Накопитель уведомления: элемент, добавленный и удалённый в одном пакете, не попадает никуда.</summary>
    private sealed class PendingChanges
    {
        private readonly HashSet<ArchitecturalElement> _added = new(ReferenceEqualityComparer.Instance);
        private readonly HashSet<ArchitecturalElement> _modified = new(ReferenceEqualityComparer.Instance);
        private readonly HashSet<ArchitecturalElement> _deleted = new(ReferenceEqualityComparer.Instance);

        public List<ArchitecturalElement> Added { get; } = [];
        public List<ArchitecturalElement> Modified { get; } = [];
        public List<ArchitecturalElement> Deleted { get; } = [];
        public bool Touched, Structural, Reset, LevelsDirty;

        public void Add(ArchitecturalElement e)
        {
            if (_deleted.Remove(e))
            {
                Deleted.Remove(e);
                Modify(e);
                return;
            }
            if (!_added.Add(e)) return;
            Added.Add(e);
            if (_modified.Remove(e)) Modified.Remove(e);
        }

        public void Delete(ArchitecturalElement e)
        {
            if (_added.Remove(e))
            {
                Added.Remove(e);
                return;
            }
            if (_modified.Remove(e)) Modified.Remove(e);
            if (_deleted.Add(e)) Deleted.Add(e);
        }

        public void Modify(ArchitecturalElement e)
        {
            if (_added.Contains(e) || _deleted.Contains(e)) return;
            if (_modified.Add(e)) Modified.Add(e);
        }

        public void Clear()
        {
            _added.Clear();
            _modified.Clear();
            _deleted.Clear();
            Added.Clear();
            Modified.Clear();
            Deleted.Clear();
            Touched = Structural = Reset = LevelsDirty = false;
        }
    }

    // ───────────────────────────── Документ журнала (ITransactionDocument) ─────────────────────────────

    /// <summary>Очистка списка элементов с записью в журнал (событие Reset коллекции старых элементов не сообщает).</summary>
    private void ClearElements()
    {
        if (Elements.Count == 0) return;
        var removed = Elements.ToArray();
        if (_journal.IsRecording) _journal.Record(new ElementsCleared(removed));
        foreach (var e in removed)
            if (ReferenceEquals(e.Journal, _journal)) e.Journal = null;
        Elements.Clear();
    }

    void ITransactionDocument.InsertElement(int index, ArchitecturalElement element) => Elements.Insert(Math.Clamp(index, 0, Elements.Count), element);

    void ITransactionDocument.RemoveElement(int index, ArchitecturalElement element)
    {
        if ((uint)index < (uint)Elements.Count && ReferenceEquals(Elements[index], element)) Elements.RemoveAt(index);
        else Elements.Remove(element);
    }

    void ITransactionDocument.ClearElements() => ClearElements();

    object ITransactionDocument.CaptureState() => CaptureDocumentState();

    IElementChange? ITransactionDocument.DiffState(object before)
    {
        var was = (DocumentState)before;
        var now = CaptureDocumentState();
        bool groups = !Same(was.Groups, now.Groups, SameGroup);
        bool axes = !Same(was.Axes, now.Axes, SameAxis);
        bool dimensions = !Same(was.Dimensions, now.Dimensions, SameDimension);
        return groups || axes || dimensions ? new DocumentStateChange(was, now, groups, axes, dimensions) : null;
    }

    object ITransactionDocument.CaptureSelection() =>
        new SelectionState(SelectedElement, SelectedElements.Count == 0 ? [] : [.. SelectedElements]);

    void ITransactionDocument.RestoreSelection(object? selection)
    {
        if (selection is SelectionState s) SetSelection(s.All, s.Primary);
    }

    private sealed record SelectionState(ArchitecturalElement? Primary, ArchitecturalElement[] All);

    /// <summary>Постройки, оси и размеры на момент открытия шага: объект и копия его значений (их немного — десятки).</summary>
    private sealed class DocumentState
    {
        public required (BuildingGroup Item, BuildingGroup Values)[] Groups { get; init; }
        public required (StructuralGridAxis Item, StructuralGridAxis Values)[] Axes { get; init; }
        public required (PinnedDimension Item, PinnedDimension Values)[] Dimensions { get; init; }
    }

    private DocumentState CaptureDocumentState() => new()
    {
        Groups = BuildingGroups.Select(g => (g, g.Clone())).ToArray(),
        Axes = GridAxes.Select(a => (a, a.Clone())).ToArray(),
        Dimensions = PinnedDimensions.Select(d => (d, d.Clone())).ToArray()
    };

    private static bool Same<T>((T Item, T Values)[] a, (T Item, T Values)[] b, Func<T, T, bool> sameValues) where T : class
    {
        if (a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++)
            if (!ReferenceEquals(a[i].Item, b[i].Item) || !sameValues(a[i].Values, b[i].Values)) return false;
        return true;
    }

    private static bool SameGroup(BuildingGroup a, BuildingGroup b) =>
        a.Id == b.Id && a.Name == b.Name && a.Purpose == b.Purpose && a.OriginX.Equals(b.OriginX) && a.OriginZ.Equals(b.OriginZ) &&
        a.IsVisible == b.IsVisible && a.ColorHex == b.ColorHex;

    private static bool SameAxis(StructuralGridAxis a, StructuralGridAxis b) =>
        a.Label == b.Label && a.Direction == b.Direction && a.Coordinate.Equals(b.Coordinate) && a.BuildingGroupId == b.BuildingGroupId;

    private static bool SameDimension(PinnedDimension a, PinnedDimension b) =>
        a.Id == b.Id && a.Label == b.Label && a.Start.AsSpan().SequenceEqual(b.Start) && a.End.AsSpan().SequenceEqual(b.End);

    /// <summary>Возвращает постройки, оси или размеры к состоянию: значения копируются в те же объекты, порядок — как в снимке.</summary>
    private void RestoreDocumentState(DocumentState state, bool groups, bool axes, bool dimensions)
    {
        if (groups)
        {
            foreach (var (item, v) in state.Groups)
            {
                item.Id = v.Id;
                item.Name = v.Name;
                item.Purpose = v.Purpose;
                item.OriginX = v.OriginX;
                item.OriginZ = v.OriginZ;
                item.IsVisible = v.IsVisible;
                item.ColorHex = v.ColorHex;
            }
            BuildingGroups.Clear();
            foreach (var (item, _) in state.Groups) BuildingGroups.Add(item);
        }
        if (axes)
        {
            foreach (var (item, v) in state.Axes)
            {
                item.Label = v.Label;
                item.Direction = v.Direction;
                item.Coordinate = v.Coordinate;
                item.BuildingGroupId = v.BuildingGroupId;
            }
            ReplaceAxes(state.Axes.Select(a => a.Item).ToList());
        }
        if (dimensions)
        {
            foreach (var (item, v) in state.Dimensions)
            {
                item.Id = v.Id;
                item.Start = [.. v.Start];
                item.End = [.. v.End];
                item.Label = v.Label;
            }
            PinnedDimensions.Clear();
            foreach (var (item, _) in state.Dimensions) PinnedDimensions.Add(item);
            DimensionsChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Изменение построек, осей или закреплённых размеров за шаг (сравнение снимка при открытии с состоянием при закрытии).</summary>
    private sealed class DocumentStateChange(DocumentState before, DocumentState after, bool groups, bool axes, bool dimensions) : IElementChange
    {
        public ArchitecturalElement? Element => null;
        public bool GroupsChanged => groups;

        public void Undo(ITransactionDocument document) => ((SceneStateService)document).RestoreDocumentState(before, groups, axes, dimensions);

        public void Redo(ITransactionDocument document) => ((SceneStateService)document).RestoreDocumentState(after, groups, axes, dimensions);

        public override string ToString() => $"документ:{(groups ? " постройки" : "")}{(axes ? " оси" : "")}{(dimensions ? " размеры" : "")}";
    }
}
