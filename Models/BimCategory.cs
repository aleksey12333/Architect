namespace AIArchitectStudio.Models;

/// <summary>
/// Категория BIM-объекта (аналог BuiltInCategory Revit). Имена категорий элементов совпадают с константами
/// <see cref="ElementTypes"/>: строка Type остаётся тем, что сериализуется в .aia и что видит ИИ, категория — её типизированное зеркало.
/// </summary>
public enum BimCategory : byte
{
    /// <summary>Тип элемента, неизвестный ядру (строка вне <see cref="ElementTypes.All"/>).</summary>
    Unknown = 0,

    Foundation,
    Wall,
    CurtainWall,
    Column,
    Core,
    Beam,
    Truss,
    Diagrid,
    Slab,
    CantileverSlab,
    Shell,
    Roof,
    Stairs,
    Window,
    Door,
    Pool,
    Terrace,
    Asset,
    WallOpening,
    SlabOpening,

    /// <summary>Постройка участка (<see cref="BuildingGroup"/>).</summary>
    Building,

    /// <summary>Закреплённый размер 3D-рулетки (<see cref="PinnedDimension"/>).</summary>
    Dimension,

    /// <summary>Этаж постройки (<see cref="BimLevel"/>).</summary>
    Level,

    /// <summary>Тип элемента — свойства «семейства» (<see cref="BimElementType"/>).</summary>
    ElementType
}

/// <summary>Взаимное отображение строкового типа элемента и <see cref="BimCategory"/> (точное порядковое сравнение, как у ==).</summary>
public static class BimCategories
{
    public static BimCategory FromType(string? type) => type switch
    {
        ElementTypes.Foundation => BimCategory.Foundation,
        ElementTypes.Wall => BimCategory.Wall,
        ElementTypes.CurtainWall => BimCategory.CurtainWall,
        ElementTypes.Column => BimCategory.Column,
        ElementTypes.Core => BimCategory.Core,
        ElementTypes.Beam => BimCategory.Beam,
        ElementTypes.Truss => BimCategory.Truss,
        ElementTypes.Diagrid => BimCategory.Diagrid,
        ElementTypes.Slab => BimCategory.Slab,
        ElementTypes.CantileverSlab => BimCategory.CantileverSlab,
        ElementTypes.Shell => BimCategory.Shell,
        ElementTypes.Roof => BimCategory.Roof,
        ElementTypes.Stairs => BimCategory.Stairs,
        ElementTypes.Window => BimCategory.Window,
        ElementTypes.Door => BimCategory.Door,
        ElementTypes.Pool => BimCategory.Pool,
        ElementTypes.Terrace => BimCategory.Terrace,
        ElementTypes.Asset => BimCategory.Asset,
        ElementTypes.WallOpening => BimCategory.WallOpening,
        ElementTypes.SlabOpening => BimCategory.SlabOpening,
        _ => BimCategory.Unknown
    };

    /// <summary>Строковый тип элемента категории; null — у категории нет типа элемента (Unknown, постройка, размер).</summary>
    public static string? ToType(BimCategory category) => category switch
    {
        BimCategory.Foundation => ElementTypes.Foundation,
        BimCategory.Wall => ElementTypes.Wall,
        BimCategory.CurtainWall => ElementTypes.CurtainWall,
        BimCategory.Column => ElementTypes.Column,
        BimCategory.Core => ElementTypes.Core,
        BimCategory.Beam => ElementTypes.Beam,
        BimCategory.Truss => ElementTypes.Truss,
        BimCategory.Diagrid => ElementTypes.Diagrid,
        BimCategory.Slab => ElementTypes.Slab,
        BimCategory.CantileverSlab => ElementTypes.CantileverSlab,
        BimCategory.Shell => ElementTypes.Shell,
        BimCategory.Roof => ElementTypes.Roof,
        BimCategory.Stairs => ElementTypes.Stairs,
        BimCategory.Window => ElementTypes.Window,
        BimCategory.Door => ElementTypes.Door,
        BimCategory.Pool => ElementTypes.Pool,
        BimCategory.Terrace => ElementTypes.Terrace,
        BimCategory.Asset => ElementTypes.Asset,
        BimCategory.WallOpening => ElementTypes.WallOpening,
        BimCategory.SlabOpening => ElementTypes.SlabOpening,
        _ => null
    };

    /// <summary>Категория физического элемента модели (не постройка, не размер и не Unknown).</summary>
    public static bool IsElement(BimCategory category) => category is >= BimCategory.Foundation and <= BimCategory.SlabOpening;
}
