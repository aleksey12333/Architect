namespace AIArchitectStudio.Models.Transactions;

/// <summary>
/// Журнал изменений документа (Transaction Manager): стеки Undo/Redo из транзакций <see cref="BimTransaction"/>.
/// <para>
/// Элемент документа знает свой журнал (<see cref="ArchitecturalElement"/>, ставится при добавлении в документ): сеттер фасада
/// перед записью слота проверяет <see cref="IsRecording"/> и записывает «было/стало». Элементы вне документа (солвер, ИИ,
/// песочницы Совета без открытого шага, клоны экспорта и расчёта) журнала не имеют — запись им ничего не стоит.
/// </para>
/// <para>
/// Шаг открывается <see cref="Start"/> и записывает все изменения до начала следующего шага, Undo/Redo или очистки истории —
/// ровно ту же область, что раньше восстанавливал снимок сцены, снятый в PushUndo. Undo откатывает изменения шага в обратном
/// порядке, Redo применяет их снова; во время применения журнал не пишет.
/// </para>
/// </summary>
public sealed class TransactionManager(ITransactionDocument document, int maxHistory)
{
    private readonly List<BimTransaction> _undo = [];
    private readonly List<BimTransaction> _redo = [];
    private readonly object _gate = new();
    private BimTransaction? _open;
    private bool _applying;

    /// <summary>Наибольшее число шагов Undo (старые шаги отбрасываются).</summary>
    public int MaxHistory { get; } = maxHistory;

    /// <summary>Открыт шаг и журнал не применяет Undo/Redo — сеттеры фасада записывают изменения.</summary>
    public bool IsRecording => _open is not null && !_applying;

    /// <summary>Идёт откат или повтор шага.</summary>
    public bool IsApplying => _applying;

    public BimTransaction? OpenTransaction => _open;
    public IReadOnlyList<BimTransaction> UndoStack => _undo;
    public IReadOnlyList<BimTransaction> RedoStack => _redo;
    public int UndoCount => _undo.Count;
    public int RedoCount => _redo.Count;

    /// <summary>Всего записанных изменений в обоих стеках (оценка памяти истории).</summary>
    public int StoredChangeCount => _undo.Sum(t => t.Changes.Count) + _redo.Sum(t => t.Changes.Count);

    /// <summary>Новый шаг: предыдущий закрывается, история Redo сбрасывается.</summary>
    public BimTransaction Start(string name)
    {
        Commit();
        _redo.Clear();
        var t = new BimTransaction(name)
        {
            StateBefore = document.CaptureState(),
            SelectionBefore = document.CaptureSelection()
        };
        _undo.Add(t);
        if (_undo.Count > MaxHistory) _undo.RemoveAt(0);
        _open = t;
        return t;
    }

    /// <summary>
    /// Закрывает открытый шаг: изменения построек, осей и размеров (снимок при открытии против текущего) добавляются
    /// одним изменением документа, выделение «после» запоминается для Redo.
    /// </summary>
    public void Commit()
    {
        if (_open is not { } t) return;
        _open = null;
        if (t.StateBefore is { } before && document.DiffState(before) is { } state) t.Add(state);
        t.SelectionAfter = document.CaptureSelection();
        t.Seal();
    }

    /// <summary>Записывает изменение в открытый шаг (вне шага и во время Undo/Redo — пропускается).</summary>
    public void Record(IElementChange change)
    {
        lock (_gate)
        {
            if (_open is { } t && !_applying) t.Add(change);
        }
    }

    /// <summary>Отменяет запись открытого шага (изменения остаются в документе): прежний Discard транзакции Undo.</summary>
    public bool DiscardOpen()
    {
        if (_open is not { } t || _undo.Count == 0 || !ReferenceEquals(_undo[^1], t)) return false;
        _open = null;
        t.Seal();
        _undo.RemoveAt(_undo.Count - 1);
        return true;
    }

    /// <summary>Шаг, который откатит следующий Undo (открытый шаг перед этим закрывается).</summary>
    public BimTransaction? PeekUndo()
    {
        Commit();
        return _undo.Count > 0 ? _undo[^1] : null;
    }

    public BimTransaction? PeekRedo()
    {
        Commit();
        return _redo.Count > 0 ? _redo[^1] : null;
    }

    public BimTransaction? Undo()
    {
        if (PeekUndo() is not { } t) return null;
        _undo.RemoveAt(_undo.Count - 1);
        Apply(() =>
        {
            t.Undo(document);
            document.RestoreSelection(t.SelectionBefore);
        });
        _redo.Add(t);
        return t;
    }

    public BimTransaction? Redo()
    {
        if (PeekRedo() is not { } t) return null;
        _redo.RemoveAt(_redo.Count - 1);
        Apply(() =>
        {
            t.Redo(document);
            document.RestoreSelection(t.SelectionAfter);
        });
        _undo.Add(t);
        return t;
    }

    /// <summary>Очистка истории (новый документ): открытый шаг закрывается без записи.</summary>
    public void Clear()
    {
        _open?.Seal();
        _open = null;
        _undo.Clear();
        _redo.Clear();
    }

    private void Apply(Action action)
    {
        _applying = true;
        try
        {
            action();
        }
        finally
        {
            _applying = false;
        }
    }
}
