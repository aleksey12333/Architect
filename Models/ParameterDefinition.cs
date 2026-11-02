using System.Globalization;
using System.Text.Json.Serialization;

namespace AIArchitectStudio.Models;

/// <summary>Тип хранения значения параметра (StorageType Revit API): определяет слот ParameterSet и формат в .aia.</summary>
public enum ParameterStorageType : byte
{
    None = 0,
    Double,
    Int,
    String,

    /// <summary>Ссылка на элемент модели — строковый Id («el_…»), как HostElementId.</summary>
    ElementId,
    Bool
}

/// <summary>
/// Смысл значения (SpecType Revit API). Внутри хранится всегда в СИ: длина — м, площадь — м², объём — м³, масса — кг,
/// плотность — кг/м³, сила — Н. Угол — в градусах: так его хранит фасад (Rotation, TwistAnglePerFloor), перевод в радианы
/// изменил бы биты сохранённых значений. Единицы показа в интерфейсе — <see cref="ParameterSpecs"/>.
/// </summary>
public enum ParameterSpecType : byte
{
    Number = 0,
    Integer,
    Text,
    YesNo,
    ElementReference,
    Material,
    Length,
    Area,
    Volume,
    Angle,
    Mass,
    Density,
    Force
}

/// <summary>Единицы и тип хранения по смыслу параметра: внутреннее значение (СИ) ↔ значение в интерфейсе.</summary>
public static class ParameterSpecs
{
    /// <summary>Смыслы, доступные для пользовательских параметров (порядок списка в инспекторе).</summary>
    public static readonly IReadOnlyList<ParameterSpecType> UserSpecs =
    [
        ParameterSpecType.Text, ParameterSpecType.Number, ParameterSpecType.Integer, ParameterSpecType.YesNo, ParameterSpecType.Length,
        ParameterSpecType.Area, ParameterSpecType.Volume, ParameterSpecType.Angle, ParameterSpecType.Mass, ParameterSpecType.Density, ParameterSpecType.Force
    ];

    public static ParameterStorageType StorageFor(ParameterSpecType spec) => spec switch
    {
        ParameterSpecType.Integer => ParameterStorageType.Int,
        ParameterSpecType.Text or ParameterSpecType.Material => ParameterStorageType.String,
        ParameterSpecType.YesNo => ParameterStorageType.Bool,
        ParameterSpecType.ElementReference => ParameterStorageType.ElementId,
        _ => ParameterStorageType.Double
    };

    /// <summary>Множитель «внутреннее (СИ) → показ»: длина — в мм, сила — в кН, остальное — как хранится.</summary>
    public static double DisplayScale(ParameterSpecType spec) => spec switch
    {
        ParameterSpecType.Length => 1000.0,
        ParameterSpecType.Force => 0.001,
        _ => 1.0
    };

    public static double ToDisplay(ParameterSpecType spec, double internalValue) => internalValue * DisplayScale(spec);

    public static double FromDisplay(ParameterSpecType spec, double displayValue) => displayValue / DisplayScale(spec);

    /// <summary>Подпись единицы показа («мм», «м²»…; пусто — безразмерное).</summary>
    public static string DisplayUnit(ParameterSpecType spec, bool english = false) => spec switch
    {
        ParameterSpecType.Length => english ? "mm" : "мм",
        ParameterSpecType.Area => english ? "m²" : "м²",
        ParameterSpecType.Volume => english ? "m³" : "м³",
        ParameterSpecType.Angle => "°",
        ParameterSpecType.Mass => english ? "kg" : "кг",
        ParameterSpecType.Density => english ? "kg/m³" : "кг/м³",
        ParameterSpecType.Force => english ? "kN" : "кН",
        _ => string.Empty
    };

    /// <summary>Значение для показа: число в единицах интерфейса с подписью, «Да/Нет», текст или Id.</summary>
    public static string Format(ParameterSpecType spec, ParameterValue value, CultureInfo culture, bool english = false)
    {
        switch (value.StorageType)
        {
            case ParameterStorageType.Double:
                string unit = DisplayUnit(spec, english);
                string number = ToDisplay(spec, value.AsDouble()).ToString("0.###", culture);
                return unit.Length == 0 ? number : $"{number} {unit}";
            case ParameterStorageType.Int:
                return value.AsInt().ToString(culture);
            case ParameterStorageType.Bool:
                return value.AsBool() ? english ? "Yes" : "Да" : english ? "No" : "Нет";
            case ParameterStorageType.String:
            case ParameterStorageType.ElementId:
                return value.AsString() ?? string.Empty;
            default:
                return string.Empty;
        }
    }
}

/// <summary>
/// Определение общего (Shared) параметра: постоянный Guid — ключ значения в ParameterSet и в .aia, имя, тип хранения и смысл.
/// Одинаковый Guid в разных проектах означает один и тот же параметр (как файл общих параметров Revit).
/// </summary>
public sealed class ParameterDefinition
{
    public Guid Guid { get; init; }
    public string Name { get; init; } = string.Empty;
    public ParameterStorageType StorageType { get; init; }
    public ParameterSpecType SpecType { get; init; }

    /// <summary>Группа показа в инспекторе («Данные», «Идентификация»…; пусто — общая).</summary>
    public string Group { get; init; } = string.Empty;

    [JsonIgnore]
    public bool IsValid => Guid != Guid.Empty && !string.IsNullOrWhiteSpace(Name) && Enum.IsDefined(StorageType) && StorageType != ParameterStorageType.None &&
                           Enum.IsDefined(SpecType);

    public static ParameterDefinition Create(string name, ParameterSpecType spec, string group = "") => new()
    {
        Guid = Guid.NewGuid(),
        Name = name.Trim(),
        SpecType = spec,
        StorageType = ParameterSpecs.StorageFor(spec),
        Group = group?.Trim() ?? string.Empty
    };

    /// <summary>Определение для значения из файла без своего определения (имя — по Guid, смысл — по типу хранения).</summary>
    public static ParameterDefinition Fallback(Guid guid, ParameterValue value) => new()
    {
        Guid = guid,
        Name = "Параметр " + guid.ToString("N")[..8],
        StorageType = value.StorageType,
        SpecType = value.StorageType switch
        {
            ParameterStorageType.Int => ParameterSpecType.Integer,
            ParameterStorageType.Bool => ParameterSpecType.YesNo,
            ParameterStorageType.String => ParameterSpecType.Text,
            ParameterStorageType.ElementId => ParameterSpecType.ElementReference,
            _ => ParameterSpecType.Number
        }
    };

    /// <summary>Значение по умолчанию нового параметра элемента: 0, пустая строка или «Нет».</summary>
    [JsonIgnore]
    public ParameterValue DefaultValue => StorageType switch
    {
        ParameterStorageType.Double => ParameterValue.FromDouble(0),
        ParameterStorageType.Int => ParameterValue.FromInt(0),
        ParameterStorageType.Bool => ParameterValue.FromBool(false),
        ParameterStorageType.ElementId => ParameterValue.FromElementId(null),
        _ => ParameterValue.FromString(string.Empty)
    };
}

/// <summary>
/// Встроенные параметры элемента (BuiltInParameter Revit API). Значения лежат в фиксированных слотах <see cref="ParameterSet"/>,
/// а не в словаре: номер слота — константа, чтение — одно поле объекта без поиска и упаковки (boxing).
/// </summary>
public enum BuiltInParameter : byte
{
    Name,
    ElementType,
    BuildingGroup,
    Material,
    ProfileType,
    RoofShape,
    HostElement,
    LevelIndex,
    RepeatLevels,
    StepCount,
    LevelHeight,
    TwistAnglePerFloor,
    TaperScalePerFloor,
    ProfileWallThickness,
    ProfileWebThickness,
    HasPhysicsError,
    PositionX,
    PositionY,
    PositionZ,
    Width,
    Height,
    Depth,
    RotationX,
    RotationY,
    RotationZ,

    /// <summary>Ссылка на этаж (BimLevel.Id); LevelIndex привязанного элемента вычисляется по этажу.</summary>
    LevelId
}

/// <summary>Описание встроенного параметра: имя свойства фасада, тип хранения, смысл, слот и компонента векторного слота (−1 — скаляр).</summary>
public readonly record struct BuiltInParameterInfo(BuiltInParameter Id, string Name, ParameterStorageType StorageType, ParameterSpecType SpecType, int Slot, int Component)
{
    public bool IsVectorComponent => Component >= 0;

    /// <summary>
    /// Скалярный параметр типа (ElementType): хранится в ParameterSet типа, а не экземпляра. Габариты W/H/D принадлежат типу
    /// по категории (<see cref="BimElementType.DimensionsFor"/>) и у экземпляра остаются компонентами вектора Size.
    /// </summary>
    public bool IsTypeParameter => Id is BuiltInParameter.Material or BuiltInParameter.ProfileType or BuiltInParameter.RoofShape
        or BuiltInParameter.ProfileWallThickness or BuiltInParameter.ProfileWebThickness;
}

/// <summary>Номера слотов ParameterSet по типам хранения (константы — индекс встроенного буфера известен при компиляции).</summary>
public static class DoubleSlot
{
    public const int LevelHeight = 0, TwistAnglePerFloor = 1, TaperScalePerFloor = 2, ProfileWallThickness = 3, ProfileWebThickness = 4;
    public const int Count = 5;
}

public static class IntSlot
{
    public const int LevelIndex = 0, RepeatLevels = 1, StepCount = 2;
    public const int Count = 3;
}

/// <summary>Строковые слоты: тексты и ссылки ElementId (HostElementId) — один буфер string.</summary>
public static class StringSlot
{
    public const int Name = 0, Type = 1, BuildingGroupId = 2, Material = 3, ProfileType = 4, RoofShape = 5, HostElementId = 6, LevelId = 7;
    public const int Count = 8;
}

public static class BoolSlot
{
    public const int HasPhysicsError = 0;
    public const int Count = 1;
}

/// <summary>Векторные слоты: массивы [X, Y, Z] центра, габаритов [W, H, D] и поворота — компоненты встроенных параметров.</summary>
public static class VectorSlot
{
    public const int Position = 0, Size = 1, Rotation = 2;
    public const int Count = 3;
}

/// <summary>Каталог встроенных параметров: таблица по значению перечисления (поиск — индекс массива).</summary>
public static class BuiltInParameters
{
    private static readonly BuiltInParameterInfo[] Table =
    [
        new(BuiltInParameter.Name, nameof(ArchitecturalElement.Name), ParameterStorageType.String, ParameterSpecType.Text, StringSlot.Name, -1),
        new(BuiltInParameter.ElementType, nameof(ArchitecturalElement.Type), ParameterStorageType.String, ParameterSpecType.Text, StringSlot.Type, -1),
        new(BuiltInParameter.BuildingGroup, nameof(ArchitecturalElement.BuildingGroupId), ParameterStorageType.String, ParameterSpecType.Text, StringSlot.BuildingGroupId, -1),
        new(BuiltInParameter.Material, nameof(ArchitecturalElement.Material), ParameterStorageType.String, ParameterSpecType.Material, StringSlot.Material, -1),
        new(BuiltInParameter.ProfileType, nameof(ArchitecturalElement.ProfileType), ParameterStorageType.String, ParameterSpecType.Text, StringSlot.ProfileType, -1),
        new(BuiltInParameter.RoofShape, nameof(ArchitecturalElement.RoofShape), ParameterStorageType.String, ParameterSpecType.Text, StringSlot.RoofShape, -1),
        new(BuiltInParameter.HostElement, nameof(ArchitecturalElement.HostElementId), ParameterStorageType.ElementId, ParameterSpecType.ElementReference, StringSlot.HostElementId, -1),
        new(BuiltInParameter.LevelIndex, nameof(ArchitecturalElement.LevelIndex), ParameterStorageType.Int, ParameterSpecType.Integer, IntSlot.LevelIndex, -1),
        new(BuiltInParameter.RepeatLevels, nameof(ArchitecturalElement.RepeatLevels), ParameterStorageType.Int, ParameterSpecType.Integer, IntSlot.RepeatLevels, -1),
        new(BuiltInParameter.StepCount, nameof(ArchitecturalElement.StepCount), ParameterStorageType.Int, ParameterSpecType.Integer, IntSlot.StepCount, -1),
        new(BuiltInParameter.LevelHeight, nameof(ArchitecturalElement.LevelHeight), ParameterStorageType.Double, ParameterSpecType.Length, DoubleSlot.LevelHeight, -1),
        new(BuiltInParameter.TwistAnglePerFloor, nameof(ArchitecturalElement.TwistAnglePerFloor), ParameterStorageType.Double, ParameterSpecType.Angle, DoubleSlot.TwistAnglePerFloor, -1),
        new(BuiltInParameter.TaperScalePerFloor, nameof(ArchitecturalElement.TaperScalePerFloor), ParameterStorageType.Double, ParameterSpecType.Number, DoubleSlot.TaperScalePerFloor, -1),
        new(BuiltInParameter.ProfileWallThickness, nameof(ArchitecturalElement.ProfileWallThickness), ParameterStorageType.Double, ParameterSpecType.Length, DoubleSlot.ProfileWallThickness, -1),
        new(BuiltInParameter.ProfileWebThickness, nameof(ArchitecturalElement.ProfileWebThickness), ParameterStorageType.Double, ParameterSpecType.Length, DoubleSlot.ProfileWebThickness, -1),
        new(BuiltInParameter.HasPhysicsError, nameof(ArchitecturalElement.HasPhysicsError), ParameterStorageType.Bool, ParameterSpecType.YesNo, BoolSlot.HasPhysicsError, -1),
        new(BuiltInParameter.PositionX, nameof(ArchitecturalElement.X), ParameterStorageType.Double, ParameterSpecType.Length, VectorSlot.Position, 0),
        new(BuiltInParameter.PositionY, nameof(ArchitecturalElement.Y), ParameterStorageType.Double, ParameterSpecType.Length, VectorSlot.Position, 1),
        new(BuiltInParameter.PositionZ, nameof(ArchitecturalElement.Z), ParameterStorageType.Double, ParameterSpecType.Length, VectorSlot.Position, 2),
        new(BuiltInParameter.Width, nameof(ArchitecturalElement.Width), ParameterStorageType.Double, ParameterSpecType.Length, VectorSlot.Size, 0),
        new(BuiltInParameter.Height, nameof(ArchitecturalElement.Height), ParameterStorageType.Double, ParameterSpecType.Length, VectorSlot.Size, 1),
        new(BuiltInParameter.Depth, nameof(ArchitecturalElement.Depth), ParameterStorageType.Double, ParameterSpecType.Length, VectorSlot.Size, 2),
        new(BuiltInParameter.RotationX, "RotationX", ParameterStorageType.Double, ParameterSpecType.Angle, VectorSlot.Rotation, 0),
        new(BuiltInParameter.RotationY, nameof(ArchitecturalElement.RotationY), ParameterStorageType.Double, ParameterSpecType.Angle, VectorSlot.Rotation, 1),
        new(BuiltInParameter.RotationZ, "RotationZ", ParameterStorageType.Double, ParameterSpecType.Angle, VectorSlot.Rotation, 2),
        new(BuiltInParameter.LevelId, nameof(ArchitecturalElement.LevelId), ParameterStorageType.ElementId, ParameterSpecType.ElementReference, StringSlot.LevelId, -1)
    ];

    public static IReadOnlyList<BuiltInParameterInfo> All => Table;

    public static ref readonly BuiltInParameterInfo Info(BuiltInParameter parameter) => ref Table[(int)parameter];
}
