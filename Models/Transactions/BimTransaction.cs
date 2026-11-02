namespace AIArchitectStudio.Models.Transactions;

/// <summary>
/// Транзакция = один шаг Undo: имя и список записанных изменений <see cref="IElementChange"/> (дельты, а не снимок сцены).
/// Undo откатывает изменения в обратном порядке, Redo применяет их заново в прямом. Повторные правки одной ячейки
/// (элемент + слот) сливаются в одну запись «было → стало».
/// </summary>
public sealed class BimTransaction
{
    private readonly List<IElementChange> _changes = [];
    private Dictionary<(object Target, int Key), int>? _merge;
    private int _membership;

    internal BimTransaction(string name) => Name = name;

    /// <summary>Имя шага («Изменение размеров и положения», «Удаление»…).</summary>
    public string Name { get; }

    /// <summary>Время открытия шага (журнал «История изменений»).</summary>
    public DateTime CreatedAt { get; } = DateTime.Now;

    public IReadOnlyList<IElementChange> Changes => _changes;

    /// <summary>Число различных элементов, затронутых шагом (очистка списка — все удалённые).</summary>
    public int AffectedElementCount
    {
        get
        {
            var set = new HashSet<ArchitecturalElement>(ReferenceEqualityComparer.Instance);
            foreach (var c in _changes)
            {
                if (c is ElementsCleared cleared) foreach (var e in cleared.Removed) set.Add(e);
                else if (c.Element is { } e) set.Add(e);
            }
            return set.Count;
        }
    }

    /// <summary>Транзакция ещё записывает изменения (до начала следующего шага, Undo или очистки истории).</summary>
    public bool IsOpen { get; internal set; } = true;

    /// <summary>Есть добавления или удаления элементов (для уведомления нужен состав документа до и после).</summary>
    public bool HasMembershipChanges => _membership > 0;

    internal object? StateBefore { get; set; }
    internal object? SelectionBefore { get; set; }
    internal object? SelectionAfter { get; set; }

    internal void Add(IElementChange change)
    {
        if (change is IMergeableChange mergeable)
        {
            _merge ??= [];
            var key = (mergeable.MergeTarget, mergeable.MergeKey);
            if (_merge.TryGetValue(key, out int index))
            {
                ((IMergeableChange)_changes[index]).Merge(change);
                return;
            }
            _merge[key] = _changes.Count;
        }
        else if (change is ElementAdded or ElementDeleted or ElementsCleared) _membership++;
        _changes.Add(change);
    }

    /// <summary>Закрытие записи: словарь слияния больше не нужен (шаг в истории хранит только список изменений).</summary>
    internal void Seal()
    {
        IsOpen = false;
        _merge = null;
        StateBefore = null;
        _changes.TrimExcess();
    }

    internal void Undo(ITransactionDocument document)
    {
        for (int i = _changes.Count - 1; i >= 0; i--) _changes[i].Undo(document);
    }

    internal void Redo(ITransactionDocument document)
    {
        foreach (var change in _changes) change.Redo(document);
    }

    public override string ToString() => $"{Name} ({_changes.Count})";
}
