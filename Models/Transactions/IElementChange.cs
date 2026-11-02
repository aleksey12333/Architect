namespace AIArchitectStudio.Models.Transactions;

/// <summary>
/// Одно записанное изменение документа (журнал транзакции): Undo возвращает состояние «до», Redo — «после».
/// Изменения элемента применяются напрямую к его слотам, типу, этажу и спискам (без сеттеров фасада и без записи в журнал),
/// уведомления PropertyChanged поднимаются так же, как при правке через фасад.
/// </summary>
public interface IElementChange
{
    /// <summary>Изменённый элемент (null — изменение документа: постройки, оси, размеры или набор элементов).</summary>
    ArchitecturalElement? Element { get; }

    void Undo(ITransactionDocument document);

    void Redo(ITransactionDocument document);
}

/// <summary>
/// Изменение, которое сливается с более поздним изменением той же ячейки состояния (элемент + слот/поле) в пределах транзакции:
/// перетаскивание Gizmo из сотен сдвигов — одна запись «было → стало». Слияние корректно, потому что Undo пишет значения
/// «как есть» в независимые ячейки: порядок отката разных ячеек не влияет на итог.
/// </summary>
internal interface IMergeableChange : IElementChange
{
    /// <summary>Владелец ячейки (элемент или список слоёв).</summary>
    object MergeTarget { get; }

    /// <summary>Номер ячейки у владельца.</summary>
    int MergeKey { get; }

    /// <summary>Поглощает более позднее изменение той же ячейки («было» остаётся своим, «стало» — от позднего).</summary>
    void Merge(IElementChange later);
}

/// <summary>Документ, к которому применяются изменения журнала (SceneStateService).</summary>
public interface ITransactionDocument
{
    void InsertElement(int index, ArchitecturalElement element);

    void RemoveElement(int index, ArchitecturalElement element);

    void ClearElements();

    /// <summary>Снимок состояния документа вне элементов (постройки, оси, размеры) на момент открытия транзакции.</summary>
    object CaptureState();

    /// <summary>Изменение состояния документа с момента снимка before (null — ничего не изменилось).</summary>
    IElementChange? DiffState(object before);

    /// <summary>Выделение (главный элемент и набор) — возвращается вместе с шагом Undo/Redo.</summary>
    object CaptureSelection();

    void RestoreSelection(object? selection);
}

/// <summary>Тип хранения слота встроенного параметра.</summary>
public enum ParameterSlotKind : byte
{
    Double,
    Int,
    String,
    Bool,
    Vector
}

/// <summary>Значение слота без упаковки: число (double/int/bool) или ссылка (строка, массив вектора — как есть, без копии).</summary>
public readonly struct SlotValue(double number, object? reference)
{
    public double Number { get; } = number;
    public object? Reference { get; } = reference;

    public static SlotValue Of<T>(T value)
    {
        if (typeof(T) == typeof(double)) return new((double)(object)value!, null);
        if (typeof(T) == typeof(int)) return new((int)(object)value!, null);
        if (typeof(T) == typeof(bool)) return new((bool)(object)value! ? 1 : 0, null);
        return new(0, value);
    }

    public string Format(ParameterSlotKind kind) => kind switch
    {
        ParameterSlotKind.Double => Number.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
        ParameterSlotKind.Int => ((int)Number).ToString(System.Globalization.CultureInfo.InvariantCulture),
        ParameterSlotKind.Bool => Number != 0 ? "true" : "false",
        ParameterSlotKind.Vector => Reference is double[] v
            ? "[" + string.Join(", ", v.Select(x => x.ToString("R", System.Globalization.CultureInfo.InvariantCulture))) + "]"
            : "null",
        _ => Reference as string ?? "null"
    };
}

/// <summary>Привязка элемента к этажу: этаж документа (или null) и слоты LevelId / LevelIndex.</summary>
public readonly record struct LevelState(BimLevel? Level, string? LevelId, int LevelIndex);

/// <summary>Поля элемента вне слотов параметров.</summary>
public enum ElementField : byte
{
    Id,
    Polygon2D,
    Layers,
    UserParameters
}
