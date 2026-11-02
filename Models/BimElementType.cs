using System.Globalization;
using System.Text;
using System.Text.Json.Serialization;

namespace AIArchitectStudio.Models;

/// <summary>Габариты элемента, которые принадлежат типу (сечение, толщина), а не экземпляру (длина, высота этажа).</summary>
[Flags]
public enum TypeDimensions : byte
{
    None = 0,
    Width = 1,
    Height = 2,
    Depth = 4
}

/// <summary>
/// Тип элемента (ElementType Revit API, «семейство и типоразмер»): общие свойства всех экземпляров — категория, материал,
/// профиль сечения и его толщины, форма кровли, пирог и габариты типа по категории (<see cref="BimElementType.DimensionsFor"/>).
/// Значения лежат в собственном <see cref="ParameterSet"/> типа. Тип неизменяем: правка свойства у экземпляра
/// не трогает тип, а переключает экземпляр на тип с новой сигнатурой («Дублировать тип» Revit).
/// </summary>
public interface IBimElementType : IBimElement
{
    /// <summary>Параметры типа: строки Type, Material, ProfileType, RoofShape, толщины профиля и вектор габаритов [W, H, D] (только габариты типа).</summary>
    ParameterSet Parameters { get; }

    /// <summary>Строковый тип элемента (ElementTypes): column, wall…</summary>
    string ElementTypeName { get; }

    /// <summary>Какие из габаритов W/H/D задаёт тип.</summary>
    TypeDimensions Dimensions { get; }

    string Material { get; }
    string ProfileType { get; }
    string RoofShape { get; }
    double ProfileWallThickness { get; }
    double ProfileWebThickness { get; }

    /// <summary>Габарит типа, м (0 — габарит принадлежит экземпляру, см. <see cref="Dimensions"/>).</summary>
    double Width { get; }

    double Height { get; }
    double Depth { get; }

    /// <summary>Пирог типа (собственные копии слоёв; не изменять).</summary>
    IReadOnlyList<MaterialLayer> Layers { get; }

    /// <summary>Каноническая сигнатура: по ней ищется дубликат в реестре, от неё — Guid типа (UUID v5).</summary>
    string Signature { get; }
}

/// <summary>
/// Ключ поиска типа в реестре — сигнатура без строк и аллокаций: строки сравниваются порядково, числа — по битам
/// (−0 ≠ +0, как в эталоне ядра), габариты — только принадлежащие типу (остальные обнуляются), пирог — послойно
/// (имя, толщина, плотность, роль, цвет). Хэш считается один раз; совпадение хэшей проверяется полным сравнением полей.
/// </summary>
internal readonly struct BimTypeKey : IEquatable<BimTypeKey>
{
    public readonly string? Type, Material, Profile, Roof;
    public readonly double Wall, Web, W, H, D;
    public readonly TypeDimensions Dims;
    public readonly IReadOnlyList<MaterialLayer>? Layers;
    private readonly int _hash;

    public BimTypeKey(string? type, string? material, string? profile, string? roof, double wall, double web, double[]? size, IReadOnlyList<MaterialLayer>? layers)
    {
        Type = type;
        Material = material;
        Profile = profile;
        Roof = roof;
        Wall = wall;
        Web = web;
        Dims = BimElementType.DimensionsFor(BimCategories.FromType(type));
        W = (Dims & TypeDimensions.Width) != 0 ? Component(size, 0) : 0;
        H = (Dims & TypeDimensions.Height) != 0 ? Component(size, 1) : 0;
        D = (Dims & TypeDimensions.Depth) != 0 ? Component(size, 2) : 0;
        Layers = layers is { Count: > 0 } ? layers : null;

        var hc = new HashCode();
        hc.Add(Type, StringComparer.Ordinal);
        hc.Add(Material, StringComparer.Ordinal);
        hc.Add(Profile, StringComparer.Ordinal);
        hc.Add(Roof, StringComparer.Ordinal);
        hc.Add(Bits(Wall));
        hc.Add(Bits(Web));
        hc.Add(Bits(W));
        hc.Add(Bits(H));
        hc.Add(Bits(D));
        if (Layers is not null)
        {
            hc.Add(Layers.Count);
            for (int i = 0; i < Layers.Count; i++)
            {
                var l = Layers[i];
                if (l is null) { hc.Add(0); continue; }
                hc.Add(l.Name, StringComparer.Ordinal);
                hc.Add(Bits(l.ThicknessMm));
                hc.Add(Bits(l.DensityKgM3));
                hc.Add(l.Role);
                hc.Add(l.ColorHex, StringComparer.Ordinal);
            }
        }
        _hash = hc.ToHashCode();
    }

    private BimTypeKey(in BimTypeKey source, IReadOnlyList<MaterialLayer>? frozen)
    {
        this = source;
        Layers = frozen;
    }

    /// <summary>Тот же ключ со своими (замороженными) копиями слоёв — для хранения в реестре.</summary>
    public BimTypeKey WithLayers(IReadOnlyList<MaterialLayer>? frozen) => new(this, frozen);

    public static double Component(double[]? v, int i) => v is not null && v.Length > i ? v[i] : 0;

    public static long Bits(double v) => BitConverter.DoubleToInt64Bits(v);

    /// <summary>Совпадают ли габариты типа с массивом экземпляра (по битам).</summary>
    public bool SameSize(double[]? size) =>
        ((Dims & TypeDimensions.Width) == 0 || Bits(W) == Bits(Component(size, 0))) &&
        ((Dims & TypeDimensions.Height) == 0 || Bits(H) == Bits(Component(size, 1))) &&
        ((Dims & TypeDimensions.Depth) == 0 || Bits(D) == Bits(Component(size, 2)));

    public bool SameLayers(IReadOnlyList<MaterialLayer>? other)
    {
        int a = Layers?.Count ?? 0, b = other?.Count ?? 0;
        if (a != b) return false;
        for (int i = 0; i < a; i++)
            if (!SameLayer(Layers![i], other![i])) return false;
        return true;
    }

    private static bool SameLayer(MaterialLayer? x, MaterialLayer? y) =>
        ReferenceEquals(x, y) || x is not null && y is not null && string.Equals(x.Name, y.Name, StringComparison.Ordinal) &&
        Bits(x.ThicknessMm) == Bits(y.ThicknessMm) && Bits(x.DensityKgM3) == Bits(y.DensityKgM3) && x.Role == y.Role &&
        string.Equals(x.ColorHex, y.ColorHex, StringComparison.Ordinal);

    public bool Equals(BimTypeKey other) =>
        _hash == other._hash && string.Equals(Type, other.Type, StringComparison.Ordinal) && string.Equals(Material, other.Material, StringComparison.Ordinal) &&
        string.Equals(Profile, other.Profile, StringComparison.Ordinal) && string.Equals(Roof, other.Roof, StringComparison.Ordinal) &&
        Bits(Wall) == Bits(other.Wall) && Bits(Web) == Bits(other.Web) && Bits(W) == Bits(other.W) && Bits(H) == Bits(other.H) && Bits(D) == Bits(other.D) &&
        SameLayers(other.Layers);

    public override bool Equals(object? obj) => obj is BimTypeKey k && Equals(k);

    public override int GetHashCode() => _hash;
}

/// <summary>Неизменяемый тип элемента из реестра <see cref="BimTypeRegistry"/>; создаётся только реестром по сигнатуре.</summary>
public sealed class BimElementType : IBimElementType
{
    private readonly ParameterSet _parameters;
    private readonly MaterialLayer[] _layers;

    internal BimElementType(in BimTypeKey key)
    {
        _layers = key.Layers is null ? [] : [.. key.Layers.Select(l => l?.Clone() ?? new MaterialLayer())];
        Key = key.WithLayers(_layers.Length > 0 ? _layers : null);
        Dimensions = key.Dims;
        Signature = BuildSignature(Key);
        UniqueId = BimUniqueId.Create(BimUniqueId.TypeNamespace, Signature);
        Id = "ty_" + UniqueId.ToString("N")[..10];

        _parameters = new ParameterSet();
        _parameters.StringAt(StringSlot.Type) = key.Type;
        _parameters.StringAt(StringSlot.BuildingGroupId) = null;
        _parameters.StringAt(StringSlot.Material) = key.Material;
        _parameters.StringAt(StringSlot.ProfileType) = key.Profile;
        _parameters.StringAt(StringSlot.RoofShape) = key.Roof;
        _parameters.DoubleAt(DoubleSlot.ProfileWallThickness) = key.Wall;
        _parameters.DoubleAt(DoubleSlot.ProfileWebThickness) = key.Web;
        _parameters.VectorAt(VectorSlot.Size) = [key.W, key.H, key.D];
        _parameters.StringAt(StringSlot.Name) = DefaultName(this);
    }

    internal BimTypeKey Key { get; }

    public string Id { get; }
    public Guid UniqueId { get; }
    public BimCategory Category => BimCategory.ElementType;

    /// <summary>Категория экземпляров типа.</summary>
    public BimCategory InstanceCategory => BimCategories.FromType(ElementTypeName);

    /// <summary>Имя типа («column 400×400 · concrete»); не входит в сигнатуру — переименование видно всем экземплярам.</summary>
    public string Name
    {
        get => _parameters.StringAt(StringSlot.Name) ?? string.Empty;
        set => _parameters.StringAt(StringSlot.Name) = string.IsNullOrWhiteSpace(value) ? DefaultName(this) : value.Trim();
    }

    public ParameterSet Parameters => _parameters;
    public string Signature { get; }
    public TypeDimensions Dimensions { get; }

    public string ElementTypeName => _parameters.StringAt(StringSlot.Type)!;
    public string Material => _parameters.StringAt(StringSlot.Material)!;
    public string ProfileType => _parameters.StringAt(StringSlot.ProfileType)!;
    public string RoofShape => _parameters.StringAt(StringSlot.RoofShape)!;
    public double ProfileWallThickness => _parameters.DoubleAt(DoubleSlot.ProfileWallThickness);
    public double ProfileWebThickness => _parameters.DoubleAt(DoubleSlot.ProfileWebThickness);
    public double Width => _parameters.VectorAt(VectorSlot.Size)[0];
    public double Height => _parameters.VectorAt(VectorSlot.Size)[1];
    public double Depth => _parameters.VectorAt(VectorSlot.Size)[2];
    public IReadOnlyList<MaterialLayer> Layers => _layers;

    public bool Owns(TypeDimensions dimension) => (Dimensions & dimension) != 0;

    /// <summary>
    /// Габариты типа по категории (таблица «тип / экземпляр» плана рефакторинга): колонна — сечение W × D, балка — сечение D × H
    /// (длина W — экземпляра), стена и витраж — толщина D, плита, консоль, фундамент, терраса — толщина H, окно и дверь — W × H.
    /// Длина стены, высота колонны, контур плиты, высота конька кровли — свойства экземпляра.
    /// </summary>
    public static TypeDimensions DimensionsFor(BimCategory category) => category switch
    {
        BimCategory.Column => TypeDimensions.Width | TypeDimensions.Depth,
        BimCategory.Beam => TypeDimensions.Height | TypeDimensions.Depth,
        BimCategory.Wall or BimCategory.CurtainWall => TypeDimensions.Depth,
        BimCategory.Slab or BimCategory.CantileverSlab or BimCategory.Foundation or BimCategory.Terrace => TypeDimensions.Height,
        BimCategory.Window or BimCategory.Door => TypeDimensions.Width | TypeDimensions.Height,
        _ => TypeDimensions.None
    };

    private static string DefaultName(BimElementType t)
    {
        string Mm(double m) => (m * 1000).ToString("0.#", CultureInfo.InvariantCulture);
        var sb = new StringBuilder(t.ElementTypeName ?? "element");
        switch (t.Dimensions)
        {
            case TypeDimensions.Width | TypeDimensions.Depth: sb.Append(' ').Append(Mm(t.Width)).Append('×').Append(Mm(t.Depth)); break;
            case TypeDimensions.Height | TypeDimensions.Depth: sb.Append(' ').Append(Mm(t.Depth)).Append('×').Append(Mm(t.Height)); break;
            case TypeDimensions.Width | TypeDimensions.Height: sb.Append(' ').Append(Mm(t.Width)).Append('×').Append(Mm(t.Height)); break;
            case TypeDimensions.Depth: sb.Append(" t=").Append(Mm(t.Depth)); break;
            case TypeDimensions.Height: sb.Append(" t=").Append(Mm(t.Height)); break;
        }
        if (!string.IsNullOrEmpty(t.ProfileType) && t.ProfileType != ProfileTypes.Rectangle && ElementTypes.IsProfiledType(t.InstanceCategory)) sb.Append(' ').Append(t.ProfileType);
        if (!string.IsNullOrEmpty(t.Material)) sb.Append(" · ").Append(t.Material);
        if (t._layers.Length > 0) sb.Append(" · ").Append(MaterialLayers.Summary(t._layers));
        return sb.ToString();
    }

    /// <summary>
    /// Каноническая сигнатура: поля с префиксом длины (без экранирования), числа — 16 hex битов double, габариты — только типа,
    /// слои — по порядку. Одинаковая сигнатура в любом сеансе и файле даёт один Guid.
    /// </summary>
    private static string BuildSignature(in BimTypeKey k)
    {
        var sb = new StringBuilder(160);
        void Text(char tag, string? s)
        {
            sb.Append(tag);
            if (s is null) sb.Append('~');
            else sb.Append(s.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(s);
            sb.Append('|');
        }
        void Number(char tag, double v) => sb.Append(tag).Append(BimTypeKey.Bits(v).ToString("X16", CultureInfo.InvariantCulture)).Append('|');

        Text('t', k.Type);
        Text('m', k.Material);
        Text('p', k.Profile);
        Text('r', k.Roof);
        Number('w', k.Wall);
        Number('b', k.Web);
        if ((k.Dims & TypeDimensions.Width) != 0) Number('W', k.W);
        if ((k.Dims & TypeDimensions.Height) != 0) Number('H', k.H);
        if ((k.Dims & TypeDimensions.Depth) != 0) Number('D', k.D);
        var layers = k.Layers ?? [];
        sb.Append('L').Append(layers.Count.ToString(CultureInfo.InvariantCulture)).Append('|');
        foreach (var l in layers)
        {
            Text('n', l.Name);
            Number('s', l.ThicknessMm);
            Number('d', l.DensityKgM3);
            Text('o', l.Role.ToString());
            Text('c', l.ColorHex);
        }
        return sb.ToString();
    }

    public override string ToString() => Name;
}

/// <summary>
/// Реестр типов: тип ищется по сигнатуре (ключ без аллокаций при попадании), Guid типа — UUID v5 от сигнатуры, поэтому
/// реестр — «интернирование» неизменяемых значений, общий для документа, его снимков Undo и песочниц Совета КБ
/// (тип никогда не меняется и не удаляется, ссылаться на него из любого клона безопасно). Документ видит используемые
/// им типы (SceneStateService.Types), файл .aia пишет только их. Потокобезопасен.
/// </summary>
public sealed class BimTypeRegistry
{
    public static BimTypeRegistry Shared { get; } = new();

    private readonly Dictionary<BimTypeKey, BimElementType> _byKey = [];
    private readonly Dictionary<Guid, BimElementType> _byGuid = [];
    private readonly object _gate = new();

    public int Count
    {
        get
        {
            lock (_gate) return _byGuid.Count;
        }
    }

    /// <summary>Снимок реестра: Guid типа → тип.</summary>
    public IReadOnlyDictionary<Guid, IBimElementType> Types
    {
        get
        {
            lock (_gate) return _byGuid.ToDictionary(p => p.Key, p => (IBimElementType)p.Value);
        }
    }

    public BimElementType? Find(Guid guid)
    {
        lock (_gate) return _byGuid.GetValueOrDefault(guid);
    }

    /// <summary>Тип с такой сигнатурой: найденный (тот же объект) или новый, добавленный в реестр.</summary>
    internal BimElementType Resolve(in BimTypeKey key)
    {
        lock (_gate)
        {
            if (_byKey.TryGetValue(key, out var found)) return found;
            var created = new BimElementType(key);
            _byKey[created.Key] = created;
            _byGuid[created.UniqueId] = created;
            return created;
        }
    }

    /// <summary>Тип для набора значений (поиск дубликата по сигнатуре, при отсутствии — регистрация).</summary>
    public BimElementType Resolve(string? type, string? material, string? profile, string? roof, double wall, double web, double[]? size, IReadOnlyList<MaterialLayer>? layers) =>
        Resolve(new BimTypeKey(type, material, profile, roof, wall, web, size, layers));
}

/// <summary>Запись типа в .aia (блок «types»): Id, Guid, имя и значения; габарит, не принадлежащий типу, не пишется.</summary>
public sealed class BimElementTypeRecord
{
    public string Id { get; set; } = string.Empty;
    public Guid UniqueId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public string? Material { get; set; }
    public string? ProfileType { get; set; }
    public string? RoofShape { get; set; }
    public double ProfileWallThickness { get; set; }
    public double ProfileWebThickness { get; set; }
    public double? Width { get; set; }
    public double? Height { get; set; }
    public double? Depth { get; set; }
    public List<MaterialLayer>? Layers { get; set; }

    public static BimElementTypeRecord From(IBimElementType t) => new()
    {
        Id = t.Id,
        UniqueId = t.UniqueId,
        Name = t.Name,
        Type = t.ElementTypeName,
        Material = t.Material,
        ProfileType = t.ProfileType,
        RoofShape = t.RoofShape,
        ProfileWallThickness = t.ProfileWallThickness,
        ProfileWebThickness = t.ProfileWebThickness,
        Width = (t.Dimensions & TypeDimensions.Width) != 0 ? t.Width : null,
        Height = (t.Dimensions & TypeDimensions.Height) != 0 ? t.Height : null,
        Depth = (t.Dimensions & TypeDimensions.Depth) != 0 ? t.Depth : null,
        Layers = t.Layers.Count > 0 ? [.. t.Layers.Select(l => l.Clone())] : null
    };

    /// <summary>Тип записи в реестре (по значениям; Guid записи совпадает с вычисленным, если файл не правили вручную).</summary>
    public BimElementType Resolve(BimTypeRegistry registry) =>
        registry.Resolve(Type, Material, ProfileType, RoofShape, ProfileWallThickness, ProfileWebThickness, [Width ?? 0, Height ?? 0, Depth ?? 0], Layers);
}
