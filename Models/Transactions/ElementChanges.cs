namespace AIArchitectStudio.Models.Transactions;

/// <summary>Изменение слота встроенного параметра экземпляра: Id элемента, номер слота, значение «было» и «стало».</summary>
public sealed class ParameterChange : IMergeableChange
{
    internal ParameterChange(ArchitecturalElement element, ParameterSlotKind kind, int slot, SlotValue oldValue, SlotValue newValue)
    {
        Element = element;
        Kind = kind;
        Slot = slot;
        OldValue = oldValue;
        NewValue = newValue;
    }

    public ArchitecturalElement Element { get; }
    ArchitecturalElement? IElementChange.Element => Element;

    public string ElementId => Element.Id;
    public ParameterSlotKind Kind { get; }
    public int Slot { get; }
    public SlotValue OldValue { get; }
    public SlotValue NewValue { get; private set; }

    /// <summary>Свойство фасада, которое хранит слот (Width/Height/Depth — компоненты Size).</summary>
    public string ParameterName => SlotNames.Name(Kind, Slot);

    object IMergeableChange.MergeTarget => Element;
    int IMergeableChange.MergeKey => (int)Kind << 8 | Slot;
    void IMergeableChange.Merge(IElementChange later) => NewValue = ((ParameterChange)later).NewValue;

    public void Undo(ITransactionDocument document) => Element.ApplySlot(Kind, Slot, OldValue);

    public void Redo(ITransactionDocument document) => Element.ApplySlot(Kind, Slot, NewValue);

    public override string ToString() => $"{Element.Id}.{ParameterName}: {OldValue.Format(Kind)} → {NewValue.Format(Kind)}";
}

/// <summary>Переход экземпляра на другой тип (Type-Instance: «Дублировать тип» в сеттере фасада, «Изменить тип»).</summary>
public sealed class TypeChange : IMergeableChange
{
    internal TypeChange(ArchitecturalElement element, BimElementType oldType, BimElementType newType)
    {
        Element = element;
        OldType = oldType;
        NewType = newType;
    }

    public ArchitecturalElement Element { get; }
    ArchitecturalElement? IElementChange.Element => Element;

    public string ElementId => Element.Id;
    public BimElementType OldType { get; }
    public BimElementType NewType { get; private set; }
    public Guid OldTypeId => OldType.UniqueId;
    public Guid NewTypeId => NewType.UniqueId;

    object IMergeableChange.MergeTarget => Element;
    int IMergeableChange.MergeKey => 0x1000;
    void IMergeableChange.Merge(IElementChange later) => NewType = ((TypeChange)later).NewType;

    public void Undo(ITransactionDocument document) => Element.ApplyType(OldType);

    public void Redo(ITransactionDocument document) => Element.ApplyType(NewType);

    public override string ToString() => $"{Element.Id}.TypeId: {OldType.Id} → {NewType.Id}";
}

/// <summary>Смена этажа документа (ссылка на BimLevel и слоты LevelId / LevelIndex вместе).</summary>
public sealed class LevelChange : IMergeableChange
{
    internal LevelChange(ArchitecturalElement element, LevelState oldState, LevelState newState)
    {
        Element = element;
        OldState = oldState;
        NewState = newState;
    }

    public ArchitecturalElement Element { get; }
    ArchitecturalElement? IElementChange.Element => Element;

    public LevelState OldState { get; }
    public LevelState NewState { get; private set; }

    object IMergeableChange.MergeTarget => Element;
    int IMergeableChange.MergeKey => 0x1001;
    void IMergeableChange.Merge(IElementChange later) => NewState = ((LevelChange)later).NewState;

    public void Undo(ITransactionDocument document) => Element.ApplyLevel(OldState);

    public void Redo(ITransactionDocument document) => Element.ApplyLevel(NewState);

    public override string ToString() => $"{Element.Id}.Level: {OldState.LevelId}#{OldState.LevelIndex} → {NewState.LevelId}#{NewState.LevelIndex}";
}

/// <summary>
/// Замена поля вне слотов: Id, контур Polygon2D, список слоёв пирога (ссылка на список), пользовательские параметры (копия словаря).
/// </summary>
public sealed class ElementFieldChange : IMergeableChange
{
    internal ElementFieldChange(ArchitecturalElement element, ElementField field, object? oldValue, object? newValue)
    {
        Element = element;
        Field = field;
        OldValue = oldValue;
        NewValue = newValue;
    }

    public ArchitecturalElement Element { get; }
    ArchitecturalElement? IElementChange.Element => Element;

    public ElementField Field { get; }
    public object? OldValue { get; }
    public object? NewValue { get; private set; }

    object IMergeableChange.MergeTarget => Element;
    int IMergeableChange.MergeKey => 0x1100 | (int)Field;
    void IMergeableChange.Merge(IElementChange later) => NewValue = ((ElementFieldChange)later).NewValue;

    public void Undo(ITransactionDocument document) => Element.ApplyField(Field, OldValue);

    public void Redo(ITransactionDocument document) => Element.ApplyField(Field, NewValue);

    public override string ToString() => $"{Element.Id}.{Field}";
}

/// <summary>
/// Правка слоёв пирога на месте (MaterialLayers.AbsorbThickness меняет толщину несущего слоя в том же списке): копия содержимого
/// «до» снимается перед первой правкой, «после» — при первом откате. Применяется к тому же объекту списка.
/// </summary>
public sealed class LayerContentChange : IMergeableChange
{
    private List<MaterialLayer>? _after;

    internal LayerContentChange(ArchitecturalElement element, List<MaterialLayer> target)
    {
        Element = element;
        Target = target;
        Before = target.Select(l => l.Clone()).ToList();
    }

    public ArchitecturalElement Element { get; }
    ArchitecturalElement? IElementChange.Element => Element;

    public List<MaterialLayer> Target { get; }
    public IReadOnlyList<MaterialLayer> Before { get; }

    // Повторная правка того же списка в той же транзакции: «до» уже снято перед первой.
    object IMergeableChange.MergeTarget => Target;
    int IMergeableChange.MergeKey => 0x1200;
    void IMergeableChange.Merge(IElementChange later) { }

    public void Undo(ITransactionDocument document)
    {
        _after ??= Target.Select(l => l.Clone()).ToList();
        Element.ApplyLayerContent(Target, Before);
    }

    public void Redo(ITransactionDocument document)
    {
        if (_after is not null) Element.ApplyLayerContent(Target, _after);
    }
}

/// <summary>Элемент добавлен в документ (позиция в списке элементов сохраняется — порядок влияет на экспорт и расчёт).</summary>
public sealed class ElementAdded(ArchitecturalElement element, int index) : IElementChange
{
    public ArchitecturalElement Element { get; } = element;
    ArchitecturalElement? IElementChange.Element => Element;

    public string ElementId => Element.Id;
    public int Index { get; } = index;

    public void Undo(ITransactionDocument document) => document.RemoveElement(Index, Element);

    public void Redo(ITransactionDocument document) => document.InsertElement(Index, Element);

    public override string ToString() => $"+{Element.Id} @{Index}";
}

/// <summary>Элемент удалён из документа (тот же объект возвращается на прежнюю позицию).</summary>
public sealed class ElementDeleted(ArchitecturalElement element, int index) : IElementChange
{
    public ArchitecturalElement Element { get; } = element;
    ArchitecturalElement? IElementChange.Element => Element;

    public string ElementId => Element.Id;
    public int Index { get; } = index;

    public void Undo(ITransactionDocument document) => document.InsertElement(Index, Element);

    public void Redo(ITransactionDocument document) => document.RemoveElement(Index, Element);

    public override string ToString() => $"−{Element.Id} @{Index}";
}

/// <summary>Очистка списка элементов (загрузка сцены): ссылки на прежние элементы, без копий.</summary>
public sealed class ElementsCleared(IReadOnlyList<ArchitecturalElement> removed) : IElementChange
{
    public IReadOnlyList<ArchitecturalElement> Removed { get; } = removed;
    ArchitecturalElement? IElementChange.Element => null;

    public void Undo(ITransactionDocument document)
    {
        for (int i = 0; i < Removed.Count; i++) document.InsertElement(i, Removed[i]);
    }

    public void Redo(ITransactionDocument document) => document.ClearElements();

    public override string ToString() => $"−{Removed.Count} (очистка)";
}

/// <summary>Имена свойств фасада по слотам (журнал, отчёты автотеста).</summary>
public static class SlotNames
{
    public static string Name(ParameterSlotKind kind, int slot) => kind switch
    {
        ParameterSlotKind.Double => slot switch
        {
            DoubleSlot.LevelHeight => nameof(ArchitecturalElement.LevelHeight),
            DoubleSlot.TwistAnglePerFloor => nameof(ArchitecturalElement.TwistAnglePerFloor),
            DoubleSlot.TaperScalePerFloor => nameof(ArchitecturalElement.TaperScalePerFloor),
            DoubleSlot.ProfileWallThickness => nameof(ArchitecturalElement.ProfileWallThickness),
            _ => nameof(ArchitecturalElement.ProfileWebThickness)
        },
        ParameterSlotKind.Int => slot switch
        {
            IntSlot.LevelIndex => nameof(ArchitecturalElement.LevelIndex),
            IntSlot.RepeatLevels => nameof(ArchitecturalElement.RepeatLevels),
            _ => nameof(ArchitecturalElement.StepCount)
        },
        ParameterSlotKind.String => slot switch
        {
            StringSlot.Name => nameof(ArchitecturalElement.Name),
            StringSlot.Type => nameof(ArchitecturalElement.Type),
            StringSlot.BuildingGroupId => nameof(ArchitecturalElement.BuildingGroupId),
            StringSlot.Material => nameof(ArchitecturalElement.Material),
            StringSlot.ProfileType => nameof(ArchitecturalElement.ProfileType),
            StringSlot.RoofShape => nameof(ArchitecturalElement.RoofShape),
            StringSlot.HostElementId => nameof(ArchitecturalElement.HostElementId),
            _ => nameof(ArchitecturalElement.LevelId)
        },
        ParameterSlotKind.Bool => nameof(ArchitecturalElement.HasPhysicsError),
        _ => slot switch
        {
            VectorSlot.Position => nameof(ArchitecturalElement.Position),
            VectorSlot.Size => nameof(ArchitecturalElement.Size),
            _ => nameof(ArchitecturalElement.Rotation)
        }
    };
}
