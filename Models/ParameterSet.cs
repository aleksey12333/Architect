using System.Runtime.CompilerServices;

namespace AIArchitectStudio.Models;

/// <summary>
/// Хранилище параметров экземпляра (ParameterSet Revit API) под фасадом <see cref="ArchitecturalElement"/>.
/// <para>
/// Встроенные параметры — в фиксированных слотах: встроенные буферы <c>[InlineArray]</c> по типу хранения (double, int, string,
/// bool и ссылки на векторы [X, Y, Z]) лежат прямо в объекте. Номер слота — константа (<see cref="DoubleSlot"/>, <see cref="IntSlot"/>…),
/// поэтому чтение Width или Material — это загрузка поля без словаря, поиска по строке и упаковки значений; запись не аллоцирует.
/// Векторный слот хранит массив, переданный фасаду, как есть (длина, −0 и идентичность массива не меняются — расчёт и .aia до бита).
/// </para>
/// <para>Пользовательские (общие) параметры — в словаре по Guid определения: создаётся при первом значении, читается редко.</para>
/// <para>Тот же класс хранит параметры типа (<see cref="BimElementType"/>); у экземпляра слоты скалярных параметров типа пусты (<see cref="ForInstance"/>).</para>
/// </summary>
public sealed class ParameterSet
{
    [InlineArray(DoubleSlot.Count)]
    private struct DoubleBuffer
    {
        private double _element;
    }

    [InlineArray(IntSlot.Count)]
    private struct IntBuffer
    {
        private int _element;
    }

    [InlineArray(StringSlot.Count)]
    private struct StringBuffer
    {
        private string? _element;
    }

    [InlineArray(BoolSlot.Count)]
    private struct BoolBuffer
    {
        private bool _element;
    }

    [InlineArray(VectorSlot.Count)]
    private struct VectorBuffer
    {
        private double[] _element;
    }

    private DoubleBuffer _doubles;
    private IntBuffer _ints;
    private StringBuffer _strings;
    private BoolBuffer _bools;
    private VectorBuffer _vectors;
    private Dictionary<Guid, ParameterValue>? _user;

    /// <summary>Значения нового элемента (те же, что у прежних инициализаторов свойств фасада).</summary>
    public ParameterSet()
    {
        _strings[StringSlot.Name] = string.Empty;
        _strings[StringSlot.Type] = ElementTypes.Wall;
        _strings[StringSlot.BuildingGroupId] = BuildingGroup.DefaultGroupId;
        _strings[StringSlot.Material] = "concrete";
        _strings[StringSlot.ProfileType] = ProfileTypes.Rectangle;
        _strings[StringSlot.RoofShape] = RoofShapes.Gable;
        _ints[IntSlot.RepeatLevels] = 1;
        _doubles[DoubleSlot.LevelHeight] = 3.0;
        _doubles[DoubleSlot.TaperScalePerFloor] = 1.0;
        _vectors[VectorSlot.Position] = [0, 0, 0];
        _vectors[VectorSlot.Size] = [1, 1, 1];
        _vectors[VectorSlot.Rotation] = [0, 0, 0];
    }

    /// <summary>Глубокая копия: буферы слотов копируются целиком (по значению), векторы и словарь пользовательских значений — новые.</summary>
    public ParameterSet(ParameterSet source)
    {
        _doubles = source._doubles;
        _ints = source._ints;
        _strings = source._strings;
        _bools = source._bools;
        for (int i = 0; i < VectorSlot.Count; i++)
            _vectors[i] = source._vectors[i] is { } v ? (double[])v.Clone() : null!;
        if (source._user is { Count: > 0 } user) _user = new Dictionary<Guid, ParameterValue>(user);
    }

    /// <summary>
    /// Набор экземпляра: скалярные параметры типа (материал, профиль и его толщины, форма кровли) хранит тип элемента,
    /// их слоты у экземпляра пусты — фасад читает их у типа.
    /// </summary>
    internal static ParameterSet ForInstance()
    {
        var set = new ParameterSet();
        set._strings[StringSlot.Material] = null;
        set._strings[StringSlot.ProfileType] = null;
        set._strings[StringSlot.RoofShape] = null;
        return set;
    }

    // ───────────────────────────── Слоты встроенных параметров (для фасада) ─────────────────────────────

    internal ref double DoubleAt(int slot) => ref _doubles[slot];

    internal ref int IntAt(int slot) => ref _ints[slot];

    internal ref string? StringAt(int slot) => ref _strings[slot];

    internal ref bool BoolAt(int slot) => ref _bools[slot];

    internal ref double[] VectorAt(int slot) => ref _vectors[slot];

    // ───────────────────────────── Встроенные параметры по идентификатору ─────────────────────────────

    public double GetDouble(BuiltInParameter parameter)
    {
        ref readonly var info = ref Expect(parameter, ParameterStorageType.Double);
        if (!info.IsVectorComponent) return _doubles[info.Slot];
        var v = _vectors[info.Slot];
        return v is not null && v.Length > info.Component ? v[info.Component] : 0;
    }

    public int GetInt(BuiltInParameter parameter) => _ints[Expect(parameter, ParameterStorageType.Int).Slot];

    public bool GetBool(BuiltInParameter parameter) => _bools[Expect(parameter, ParameterStorageType.Bool).Slot];

    /// <summary>Строковый параметр или ссылка ElementId.</summary>
    public string? GetString(BuiltInParameter parameter)
    {
        ref readonly var info = ref BuiltInParameters.Info(parameter);
        if (info.StorageType is not (ParameterStorageType.String or ParameterStorageType.ElementId))
            throw new InvalidOperationException($"Параметр {parameter} хранится как {info.StorageType}, а не строка.");
        return _strings[info.Slot];
    }

    /// <summary>Значение любого встроенного параметра в типизированной обёртке (без упаковки).</summary>
    public ParameterValue Get(BuiltInParameter parameter) => BuiltInParameters.Info(parameter).StorageType switch
    {
        ParameterStorageType.Double => ParameterValue.FromDouble(GetDouble(parameter)),
        ParameterStorageType.Int => ParameterValue.FromInt(GetInt(parameter)),
        ParameterStorageType.Bool => ParameterValue.FromBool(GetBool(parameter)),
        ParameterStorageType.ElementId => ParameterValue.FromElementId(GetString(parameter)),
        _ => ParameterValue.FromString(GetString(parameter))
    };

    private static ref readonly BuiltInParameterInfo Expect(BuiltInParameter parameter, ParameterStorageType storage)
    {
        ref readonly var info = ref BuiltInParameters.Info(parameter);
        if (info.StorageType != storage) throw new InvalidOperationException($"Параметр {parameter} хранится как {info.StorageType}, а не {storage}.");
        return ref info;
    }

    // ───────────────────────────── Пользовательские (общие) параметры ─────────────────────────────

    public int UserCount => _user?.Count ?? 0;

    /// <summary>Значения по Guid определения; null — у элемента нет ни одного (в .aia блок не пишется).</summary>
    public IReadOnlyDictionary<Guid, ParameterValue>? UserValues => _user is { Count: > 0 } ? _user : null;

    public bool TryGetUser(Guid definition, out ParameterValue value)
    {
        if (_user is not null && _user.TryGetValue(definition, out value)) return true;
        value = ParameterValue.Empty;
        return false;
    }

    /// <summary>Записывает значение (пустое — удаляет); true — значение изменилось.</summary>
    internal bool SetUser(Guid definition, ParameterValue value)
    {
        if (value.IsEmpty) return RemoveUser(definition);
        _user ??= [];
        if (_user.TryGetValue(definition, out var old) && old == value) return false;
        _user[definition] = value;
        return true;
    }

    internal bool RemoveUser(Guid definition) => _user is not null && _user.Remove(definition);

    /// <summary>Замена всех пользовательских значений (чтение .aia); пустые значения и нулевой Guid отбрасываются.</summary>
    internal void ReplaceUser(IEnumerable<KeyValuePair<Guid, ParameterValue>>? values)
    {
        _user = null;
        if (values is null) return;
        foreach (var (key, value) in values)
            if (key != Guid.Empty && !value.IsEmpty)
                (_user ??= [])[key] = value;
    }
}
