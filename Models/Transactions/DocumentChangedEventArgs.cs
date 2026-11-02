namespace AIArchitectStudio.Models.Transactions;

/// <summary>Причина уведомления документа.</summary>
public enum DocumentChangeReason
{
    /// <summary>Правка (операция документа или закрытие явной транзакции).</summary>
    Edit,
    Undo,
    Redo,

    /// <summary>Полная замена сцены (новый проект, загрузка, очистка).</summary>
    Reset,

    /// <summary>Сменился фильтр этажа: видимость элементов, сами элементы не менялись.</summary>
    Filter
}

/// <summary>
/// DocumentChanged(added, modified, deleted): что именно изменилось в документе. Вьюпорт перестраивает только геометрию
/// изменённых элементов (и их хостов), добавляет новые и убирает удалённые; полная перестройка — только при <see cref="IsReset"/>.
/// </summary>
public sealed class DocumentChangedEventArgs(
    IReadOnlyList<ArchitecturalElement> added,
    IReadOnlyList<ArchitecturalElement> modified,
    IReadOnlyList<ArchitecturalElement> deleted,
    DocumentChangeReason reason,
    bool isReset,
    bool isStructural) : EventArgs
{
    public IReadOnlyList<ArchitecturalElement> Added { get; } = added;
    public IReadOnlyList<ArchitecturalElement> Modified { get; } = modified;
    public IReadOnlyList<ArchitecturalElement> Deleted { get; } = deleted;
    public DocumentChangeReason Reason { get; } = reason;

    /// <summary>Нужна полная перестройка: новая сцена, фильтр этажа или изменение построек (начала координат групп).</summary>
    public bool IsReset { get; } = isReset;

    /// <summary>Изменился состав документа, этажи или категории (дерево объектов, список этажей).</summary>
    public bool IsStructural { get; } = isStructural;

    public bool IsEmpty => !IsReset && Added.Count == 0 && Modified.Count == 0 && Deleted.Count == 0;

    public static DocumentChangedEventArgs ResetOf(DocumentChangeReason reason) => new([], [], [], reason, true, reason != DocumentChangeReason.Filter);

    public override string ToString() => $"{Reason}: +{Added.Count} ~{Modified.Count} −{Deleted.Count}{(IsReset ? " (полная)" : string.Empty)}";
}
