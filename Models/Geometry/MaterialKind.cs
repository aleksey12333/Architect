namespace AIArchitectStudio.Models.Geometry;

/// <summary>Материал части геометрии: определяет цвет во вьюпорте, в OBJ/MTL и IFC, прозрачность и заливку среза.</summary>
public enum MaterialKind
{
    Concrete,
    FoundationConcrete,
    CoreConcrete,
    Plaster,
    Timber,
    DeckWood,
    DoorWood,
    Glass,
    Frame,
    Steel,
    MetalRoof,
    Water,
    PoolTile,
    ShellConcrete,
    Generic,

    /// <summary>Объём выреза проёма (wall_opening / slab_opening): почти прозрачный, с контуром.</summary>
    Void,

    /// <summary>Заливка плоскости разреза Section Box: контрастный тёмный тон бетона.</summary>
    SectionCut,

    /// <summary>Слои многослойного пирога: изоляция, стяжка, гидроизоляция, отделка.</summary>
    Insulation,
    Screed,
    Waterproofing,
    Finish,

    /// <summary>Заливка среза слоёв пирога — каждый слой виден в разрезе своим тоном.</summary>
    InsulationCut,
    ScreedCut,
    WaterproofingCut,
    FinishCut
}

/// <summary>Цвет RGBA (0…1) без привязки к графическому движку.</summary>
public readonly record struct BimColor(float Red, float Green, float Blue, float Alpha)
{
    public static BimColor FromRgb(int rgb) =>
        new(((rgb >> 16) & 0xFF) / 255f, ((rgb >> 8) & 0xFF) / 255f, (rgb & 0xFF) / 255f, 1f);
}

/// <summary>
/// Классификация материалов геометрического ядра: материал элемента и слоя пирога, прозрачность, материал среза и базовый цвет.
/// Материалы рендера (PhongMaterial) строятся из этих данных в слое отображения (ArchitecturalMaterials).
/// </summary>
public static class MaterialCatalog
{
    public static bool IsTransparent(MaterialKind kind) => kind is MaterialKind.Glass or MaterialKind.Water or MaterialKind.Void;

    /// <summary>Глянцевый материал (стекло, вода, сталь): сильный блик во вьюпорте и Ks/Ns в MTL.</summary>
    public static bool IsGlossy(MaterialKind kind) => kind is MaterialKind.Glass or MaterialKind.Water or MaterialKind.Steel;

    public static BimColor BaseColor(MaterialKind kind) => kind switch
    {
        MaterialKind.Concrete => BimColor.FromRgb(0xC4C1BB),
        MaterialKind.FoundationConcrete => BimColor.FromRgb(0x8F8C87),
        MaterialKind.CoreConcrete => BimColor.FromRgb(0xA8A49E),
        MaterialKind.Plaster => BimColor.FromRgb(0xEDE9E2),
        MaterialKind.Timber => BimColor.FromRgb(0xB98F62),
        MaterialKind.DeckWood => BimColor.FromRgb(0x9A7450),
        MaterialKind.DoorWood => BimColor.FromRgb(0x6E5038),
        MaterialKind.Glass => new BimColor(0.62f, 0.78f, 0.90f, 0.30f),
        MaterialKind.Frame => BimColor.FromRgb(0x2F3338),
        MaterialKind.Steel => BimColor.FromRgb(0x7C848E),
        MaterialKind.MetalRoof => BimColor.FromRgb(0x41464E),
        MaterialKind.Water => new BimColor(0.18f, 0.58f, 0.92f, 0.55f),
        MaterialKind.PoolTile => BimColor.FromRgb(0xD6EAF2),
        MaterialKind.ShellConcrete => BimColor.FromRgb(0xDCD9D3),
        MaterialKind.Void => new BimColor(1.0f, 0.62f, 0.04f, 0.14f),
        MaterialKind.SectionCut => BimColor.FromRgb(0x6B6158),
        MaterialKind.Insulation => BimColor.FromRgb(0xE9D28A),
        MaterialKind.Screed => BimColor.FromRgb(0xB4AFA6),
        MaterialKind.Waterproofing => BimColor.FromRgb(0x3A3D44),
        MaterialKind.Finish => BimColor.FromRgb(0xC98B6B),
        MaterialKind.InsulationCut => BimColor.FromRgb(0xD6AE3C),
        MaterialKind.ScreedCut => BimColor.FromRgb(0x8E897F),
        MaterialKind.WaterproofingCut => BimColor.FromRgb(0x1E2025),
        MaterialKind.FinishCut => BimColor.FromRgb(0xA4654A),
        _ => BimColor.FromRgb(0xC8C8CC)
    };

    /// <summary>Материал среза Section Box для части геометрии: слои пирога — своим тоном, остальное — тёмный тон бетона.</summary>
    public static MaterialKind CutKind(MaterialKind kind) => kind switch
    {
        MaterialKind.Insulation => MaterialKind.InsulationCut,
        MaterialKind.Screed => MaterialKind.ScreedCut,
        MaterialKind.Waterproofing => MaterialKind.WaterproofingCut,
        MaterialKind.Finish => MaterialKind.FinishCut,
        _ => MaterialKind.SectionCut
    };

    /// <summary>Материал слоя пирога: несущий — материал самого элемента, прочие — по назначению слоя.</summary>
    public static MaterialKind LayerKind(MaterialLayer layer, MaterialKind structural) => layer.Role switch
    {
        MaterialLayerRole.Insulation => MaterialKind.Insulation,
        MaterialLayerRole.Screed => MaterialKind.Screed,
        MaterialLayerRole.Waterproofing => MaterialKind.Waterproofing,
        MaterialLayerRole.Finish => MaterialKind.Finish,
        _ => layer.IsConcrete
            ? structural is MaterialKind.Concrete or MaterialKind.FoundationConcrete or MaterialKind.CoreConcrete or MaterialKind.ShellConcrete ? structural : MaterialKind.Concrete
            : structural == MaterialKind.Timber || layer.Name.Contains("дерев", StringComparison.OrdinalIgnoreCase) || layer.Name.Contains("брус", StringComparison.OrdinalIgnoreCase)
                ? MaterialKind.Timber
                : MaterialKind.Plaster
    };

    /// <summary>Основной материал элемента по его типу и строке Material.</summary>
    public static MaterialKind ForElement(ArchitecturalElement e)
    {
        string m = (e.Material ?? string.Empty).ToLowerInvariant();
        return e.Type switch
        {
            ElementTypes.WallOpening or ElementTypes.SlabOpening => MaterialKind.Void,
            ElementTypes.Foundation => MaterialKind.FoundationConcrete,
            ElementTypes.Core => MaterialKind.CoreConcrete,
            ElementTypes.Wall => m.Contains("timber") || m.Contains("wood") ? MaterialKind.Timber
                : m.Contains("glass") ? MaterialKind.Glass
                : m.Contains("concrete") ? MaterialKind.Concrete
                : MaterialKind.Plaster,
            ElementTypes.CurtainWall or ElementTypes.Window => MaterialKind.Glass,
            ElementTypes.Door => MaterialKind.DoorWood,
            ElementTypes.Truss or ElementTypes.Diagrid => MaterialKind.Steel,
            ElementTypes.Roof => m.Contains("tile") ? MaterialKind.Timber : MaterialKind.MetalRoof,
            ElementTypes.Shell => MaterialKind.ShellConcrete,
            ElementTypes.Terrace => m.Contains("concrete") ? MaterialKind.Concrete : MaterialKind.DeckWood,
            ElementTypes.Pool => MaterialKind.PoolTile,
            ElementTypes.Column or ElementTypes.Beam => m.Contains("steel") ? MaterialKind.Steel : MaterialKind.Concrete,
            ElementTypes.Asset => MaterialKind.Generic,
            _ => m.Contains("steel") ? MaterialKind.Steel : m.Contains("wood") || m.Contains("timber") ? MaterialKind.Timber : MaterialKind.Concrete
        };
    }
}
