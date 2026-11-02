using System.Collections.Specialized;
using AIArchitectStudio.Models;
using AIArchitectStudio.Models.Transactions;

namespace AIArchitectStudio.Services;

/// <summary>
/// Этажи и типы документа (фазы 3–4): элемент, попавший в документ, привязывается к этажу своей постройки
/// (<see cref="BimLevelTable.Bind"/>), отметки этажей синхронизируются с геометрией лениво — при первом обращении к
/// <see cref="Levels"/> после изменения сцены. Типы — общий реестр <see cref="BimTypeRegistry"/>, документ видит используемые.
/// </summary>
public sealed partial class SceneStateService
{
    private BimLevelTable _levels = new();
    private bool _levelsDirty = true;

    public SceneStateService()
    {
        _journal = new TransactionManager(this, MaxHistory);
        Elements.CollectionChanged += OnElementsCollectionChanged;
    }

    /// <summary>Этажи построек документа (отметки — по текущей геометрии).</summary>
    public BimLevelTable Levels
    {
        get
        {
            if (_levelsDirty)
            {
                _levelsDirty = false;
                if (Elements.Count > 0) _levels.Synchronize(Elements);
            }
            return _levels;
        }
    }

    /// <summary>Типы, используемые элементами документа: Guid типа → тип (реестр типов документа).</summary>
    public IReadOnlyDictionary<Guid, IBimElementType> Types
    {
        get
        {
            var types = new Dictionary<Guid, IBimElementType>();
            foreach (var e in Elements) types.TryAdd(e.TypeId, e.ElementType);
            return types;
        }
    }

    /// <summary>Экземпляры типа в документе.</summary>
    public List<ArchitecturalElement> InstancesOf(IBimElementType type) => Elements.Where(e => e.TypeId == type.UniqueId).ToList();

    /// <summary>
    /// «Изменить тип» (Edit Type Revit): одна правка всем экземплярам типа одним шагом Undo. Каждый экземпляр проходит через
    /// сеттеры фасада, поэтому все они переходят на один новый тип; экземпляры других типов не затрагиваются. Возвращает число экземпляров.
    /// </summary>
    public int UpdateTypeInstances(IBimElementType type, Action<ArchitecturalElement> mutate, bool recordUndo = true)
    {
        var instances = InstancesOf(type);
        if (instances.Count > 0) UpdateElements(instances, mutate, recordUndo);
        return instances.Count;
    }

    private void ResetLevels()
    {
        _levels = new BimLevelTable();
        _levelsDirty = true;
    }

    /// <summary>
    /// Состав документа: удалённый элемент уходит в журнал (тот же объект вернёт Undo) и отвязывается от журнала; добавленный
    /// привязывается к этажу (до журнала — привязка входит в «добавление»), получает журнал и записывается в шаг.
    /// Очистка (Reset) старых элементов не сообщает — её пишет <see cref="ClearElements"/>.
    /// </summary>
    private void OnElementsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is not null)
        {
            int index = e.OldStartingIndex;
            foreach (ArchitecturalElement element in e.OldItems)
            {
                if (_journal.IsRecording) _journal.Record(new ElementDeleted(element, index));
                if (ReferenceEquals(element.Journal, _journal)) element.Journal = null;
            }
        }
        if (e.NewItems is not null)
        {
            int index = e.NewStartingIndex;
            foreach (ArchitecturalElement element in e.NewItems)
            {
                _levels.Bind(element);
                element.Journal = _journal;
                if (_journal.IsRecording) _journal.Record(new ElementAdded(element, index));
                index++;
            }
        }
        _levelsDirty = true;
    }
}
