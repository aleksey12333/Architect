namespace AIArchitectStudio.Models;

public enum ClashSeverity
{
    Critical,
    Warning
}

public enum ClashCategory
{
    /// <summary>Отверстие плиты перерезает балку или попадает в зону опирания колонны.</summary>
    SlabOpeningCutsFrame,

    /// <summary>Проём стены (окно, дверь, wall_opening) врезается в колонну.</summary>
    WallOpeningHitsColumn,

    /// <summary>Проём выходит за габарит своей стены.</summary>
    WallOpeningOutsideWall,

    /// <summary>Над лестничным маршем нет отверстия в вышележащей плите.</summary>
    StairsWithoutOpening,

    /// <summary>Балка или край плиты над маршем — высота в свету меньше 2.0 м.</summary>
    StairsHeadroom,

    /// <summary>Дубликат или пересечение однотипных конструкций (более 35 % объёма).</summary>
    Duplicate
}

/// <summary>Автоисправление коллизии одной кнопкой.</summary>
public enum ClashFix
{
    None,
    MoveSlabOpening,
    MoveWallOpening,
    ClampWallOpening,
    CreateStairsOpening,
    DeleteDuplicate
}

/// <summary>Найденная коллизия: пара элементов, категория, описание, точка узла и способ исправления.</summary>
public sealed class ClashItem
{
    public string Id { get; set; } = string.Empty;
    public ClashSeverity Severity { get; set; }
    public ClashCategory Category { get; set; }
    public string ElementAId { get; set; } = string.Empty;
    public string ElementBId { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;

    /// <summary>Точка узла пересечения [X, Y, Z], м.</summary>
    public double[] LocationPoint { get; set; } = [0, 0, 0];

    /// <summary>Характерный размер узла, м (для кадрирования камеры).</summary>
    public double Extent { get; set; } = 2;

    public ClashFix Fix { get; set; }

    public bool IsCritical => Severity == ClashSeverity.Critical;
    public bool HasFix => Fix != ClashFix.None;

    /// <summary>Заголовок категории.</summary>
    public string CategoryTitle => Category switch
    {
        ClashCategory.SlabOpeningCutsFrame => "Отверстие режет каркас",
        ClashCategory.WallOpeningHitsColumn => "Проём врезается в колонну",
        ClashCategory.WallOpeningOutsideWall => "Проём вне габарита стены",
        ClashCategory.StairsWithoutOpening => "Нет проёма над лестницей",
        ClashCategory.StairsHeadroom => "Высота в свету над маршем",
        _ => "Дубликат конструкций"
    };

    public string FixLabel => Fix switch
    {
        ClashFix.MoveSlabOpening => "Сдвинуть отверстие с каркаса",
        ClashFix.MoveWallOpening => "Сдвинуть проём от колонны",
        ClashFix.ClampWallOpening => "Вписать проём в стену",
        ClashFix.CreateStairsOpening => "Создать проём в плите над лестницей",
        ClashFix.DeleteDuplicate => "Удалить дубликат",
        _ => string.Empty
    };

    public string SeverityGlyph => IsCritical ? "" : "";
}
