using System.Globalization;
using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AIArchitectStudio.Models;

/// <summary>Назначение слоя многослойной конструкции.</summary>
public enum MaterialLayerRole
{
    /// <summary>Несущий слой (монолитный ж/б, кладка) — принимает проектную толщину элемента и работает в расчёте.</summary>
    Structural,

    /// <summary>Тепло- или звукоизоляция (пеноплекс, минвата).</summary>
    Insulation,

    /// <summary>Стяжка, разуклонка, бетонная подготовка.</summary>
    Screed,

    /// <summary>Гидро- и пароизоляция (учитывается в м²).</summary>
    Waterproofing,

    /// <summary>Отделка и облицовка.</summary>
    Finish
}

/// <summary>
/// Слой «пирога» конструкции (IfcMaterialLayer): название материала, толщина в мм, плотность и назначение.
/// Порядок слоёв: у стены — от лицевой грани +Z локальной системы к грани −Z, у плит, кровли и фундамента — снизу вверх
/// (так же, как IfcMaterialLayerSetUsage .AXIS2./.AXIS3. с направлением .POSITIVE. в экспорте IFC).
/// </summary>
public sealed partial class MaterialLayer : ObservableObject
{
    [ObservableProperty]
    public partial string Name { get; set; } = string.Empty;

    [ObservableProperty]
    public partial double ThicknessMm { get; set; } = 100;

    [ObservableProperty]
    public partial double DensityKgM3 { get; set; } = 2500;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RoleGlyph), nameof(RoleTitle))]
    public partial MaterialLayerRole Role { get; set; } = MaterialLayerRole.Structural;

    [ObservableProperty]
    public partial string ColorHex { get; set; } = "#C4C1BB";

    public MaterialLayer() { }

    public MaterialLayer(string name, double thicknessMm, double densityKgM3, MaterialLayerRole role, string? colorHex = null)
    {
        Name = name;
        ThicknessMm = thicknessMm;
        DensityKgM3 = densityKgM3;
        Role = role;
        ColorHex = colorHex ?? MaterialLayers.DefaultColor(role, name);
    }

    [JsonIgnore] public double ThicknessM => Math.Max(0, ThicknessMm) / 1000.0;

    /// <summary>Вес 1 м² слоя, кН/м² (ρ·t·g).</summary>
    [JsonIgnore] public double WeightKPa => Math.Max(0, DensityKgM3) * ThicknessM * 9.81 / 1000.0;

    [JsonIgnore] public bool IsConcrete => MaterialLayers.IsConcreteName(Name);

    /// <summary>Несущий железобетонный слой (армируется; бетонная подготовка — без арматуры).</summary>
    [JsonIgnore] public bool IsReinforcedConcrete => Role == MaterialLayerRole.Structural && IsConcrete && !MaterialLayers.IsLeanConcrete(Name);

    [JsonIgnore] public string RoleGlyph => Role switch
    {
        MaterialLayerRole.Structural => "",
        MaterialLayerRole.Insulation => "",
        MaterialLayerRole.Screed => "",
        MaterialLayerRole.Waterproofing => "",
        _ => ""
    };

    [JsonIgnore] public string RoleTitle => MaterialLayers.RoleTitle(Role);

    public MaterialLayer Clone() => new()
    {
        Name = Name,
        ThicknessMm = ThicknessMm,
        DensityKgM3 = DensityKgM3,
        Role = Role,
        ColorHex = ColorHex
    };
}

/// <summary>Правила многослойных конструкций: допустимые хосты, синхронизация толщины, готовые инженерные пресеты.</summary>
public static class MaterialLayers
{
    public const string PresetFloor = "floor";
    public const string PresetFlatRoof = "flat_roof";
    public const string PresetFoundation = "foundation";
    public const string PresetExteriorWall = "exterior_wall";

    /// <summary>Элементы, которым назначается пирог: стена, плита, консольная плита, кровля и фундамент.</summary>
    public static bool IsLayerHost(ArchitecturalElement e) =>
        e.Category is BimCategory.Wall or BimCategory.Slab or BimCategory.CantileverSlab or BimCategory.Roof or BimCategory.Foundation;

    /// <summary>Стена — слои по толщине D (локальная Z); плиты, плоская кровля и фундамент — по высоте H.</summary>
    public static bool IsVerticalStack(ArchitecturalElement e) => e.Category == BimCategory.Wall;

    /// <summary>Толщина пирога определяет габарит элемента (у скатной кровли H — высота конька, слои её не меняют).</summary>
    public static bool DrivesThickness(ArchitecturalElement e) =>
        IsLayerHost(e) && (e.Category != BimCategory.Roof || e.IsFlatRoof);

    public static double TotalMm(IEnumerable<MaterialLayer> layers) => layers.Sum(l => Math.Max(0, l.ThicknessMm));

    /// <summary>Текущая толщина элемента в направлении пирога, м.</summary>
    public static double ElementThickness(ArchitecturalElement e) => IsVerticalStack(e) ? e.Depth : e.Height;

    /// <summary>
    /// Синхронизирует габарит элемента с суммой толщин слоёв: стена — D (ось сохраняется), плита — H (низ сохраняется:
    /// элемент остаётся на своей опоре). Привязанные проёмы стены получают новую глубину выреза.
    /// </summary>
    public static void ApplyThickness(ArchitecturalElement e, IEnumerable<ArchitecturalElement>? hosted = null)
    {
        if (!DrivesThickness(e) || e.Layers.Count == 0) return;
        double total = TotalMm(e.Layers) / 1000.0;
        if (total < 0.01) return;
        if (IsVerticalStack(e))
        {
            if (Math.Abs(e.Depth - total) < 1e-9) return;
            e.SetSize(e.Width, e.Height, total);
            foreach (var o in hosted ?? [])
                if (ElementTypes.IsHostedType(o.Category)) o.SetSize(o.Width, o.Height, total + 0.06);
        }
        else
        {
            if (Math.Abs(e.Height - total) < 1e-9) return;
            double baseY = e.BaseY;
            e.SetSize(e.Width, total, e.Depth);
            e.PlaceOnBase(baseY);
            foreach (var o in hosted ?? [])
                if (o.Category == BimCategory.SlabOpening)
                {
                    o.SetSize(o.Width, total + 0.06, o.Depth);
                    o.SetPosition(o.X, e.Y, o.Z);
                }
        }
    }

    /// <summary>
    /// Обратная синхронизация: проектную толщину, заданную в инспекторе, принимает основной несущий слой
    /// (остальные слои сохраняют свои толщины). Возвращает true, если слои изменились.
    /// </summary>
    public static bool AbsorbThickness(ArchitecturalElement e)
    {
        if (!DrivesThickness(e) || e.Layers.Count == 0) return false;
        double targetMm = ElementThickness(e) * 1000.0;
        double totalMm = TotalMm(e.Layers);
        if (Math.Abs(targetMm - totalMm) < 0.01) return false;
        var main = e.Layers.Where(l => l.Role == MaterialLayerRole.Structural).OrderByDescending(l => l.ThicknessMm).FirstOrDefault()
                   ?? e.Layers.OrderByDescending(l => l.ThicknessMm).First();
        double others = totalMm - main.ThicknessMm;
        // Правка списка на месте: содержимое «до» — в журнал открытого шага Undo.
        e.BeginLayersEdit();
        main.ThicknessMm =Math.Round(Math.Max(10, targetMm - others), 2);
        e.RefreshType();
        return true;
    }

    /// <summary>Толщина несущих слоёв, м (без слоёв — полная толщина элемента).</summary>
    public static double StructuralThickness(ArchitecturalElement e)
    {
        if (e.Layers.Count == 0) return ElementThickness(e);
        double t = e.Layers.Where(l => l.Role == MaterialLayerRole.Structural).Sum(l => l.ThicknessM);
        return t > 0.005 ? t : ElementThickness(e);
    }

    /// <summary>Толщина бетонных слоёв (несущий ж/б и бетонная подготовка), м — для объёма бетона в смете.</summary>
    public static double ConcreteThickness(ArchitecturalElement e) =>
        e.Layers.Where(l => l.IsConcrete && l.Role is MaterialLayerRole.Structural or MaterialLayerRole.Screed && !IsCementScreed(l.Name)).Sum(l => l.ThicknessM);

    /// <summary>Толщина армируемых несущих слоёв ж/б, м.</summary>
    public static double ReinforcedThickness(ArchitecturalElement e) => e.Layers.Where(l => l.IsReinforcedConcrete).Sum(l => l.ThicknessM);

    /// <summary>Вес 1 м² пирога, кН/м².</summary>
    public static double WeightKPa(ArchitecturalElement e) => e.Layers.Sum(l => l.WeightKPa);

    public static bool IsConcreteName(string? name)
    {
        string n = (name ?? string.Empty).ToLowerInvariant();
        return n.Contains("ж/б") || n.Contains("жб") || n.Contains("бетон") || n.Contains("concrete") || n.Contains("монолит") || n.Contains("reinforced");
    }

    /// <summary>Бетонная подготовка (B3.5 / B7.5) — неармированный бетон.</summary>
    public static bool IsLeanConcrete(string? name)
    {
        string n = (name ?? string.Empty).ToLowerInvariant();
        return n.Contains("подготовк") || n.Contains("lean") || n.Contains("blinding") || n.Contains("b3.5") || n.Contains("в3,5") || n.Contains("b3,5") || n.Contains("в3.5");
    }

    private static bool IsCementScreed(string? name)
    {
        string n = (name ?? string.Empty).ToLowerInvariant();
        return n.Contains("стяжк") || n.Contains("screed") || n.Contains("разуклон");
    }

    /// <summary>Назначение слоя по названию материала (для IFC чужих программ без AIA_Parameters).</summary>
    public static MaterialLayerRole InferRole(string? name)
    {
        string n = (name ?? string.Empty).ToLowerInvariant();
        if (n.Contains("гидро") || n.Contains("пароизол") || n.Contains("мембран") || n.Contains("waterproof") || n.Contains("vapour") || n.Contains("vapor") || n.Contains("membrane") || n.Contains("битум") || n.Contains("техноэласт"))
            return MaterialLayerRole.Waterproofing;
        if (n.Contains("утепл") || n.Contains("пеноплекс") || n.Contains("минват") || n.Contains("мин. ват") || n.Contains("xps") || n.Contains("eps") || n.Contains("пенополи") || n.Contains("insulation") || n.Contains("wool") || n.Contains("звукоизол") || n.Contains("rockwool"))
            return MaterialLayerRole.Insulation;
        if (n.Contains("стяжк") || n.Contains("разуклон") || n.Contains("screed") || IsLeanConcrete(n) || n.Contains("керамзит"))
            return MaterialLayerRole.Screed;
        if (n.Contains("облицов") || n.Contains("отделк") || n.Contains("штукатур") || n.Contains("плитк") || n.Contains("finish") || n.Contains("cladding") || n.Contains("plaster") || n.Contains("фасад") || n.Contains("керамогранит"))
            return MaterialLayerRole.Finish;
        return MaterialLayerRole.Structural;
    }

    /// <summary>Плотность по умолчанию, кг/м³.</summary>
    public static double DefaultDensity(MaterialLayerRole role, string? name)
    {
        string n = (name ?? string.Empty).ToLowerInvariant();
        if (n.Contains("газо") || n.Contains("d700") || n.Contains("aerated")) return 700;
        if (n.Contains("кирпич") || n.Contains("brick")) return 1800;
        if (n.Contains("пеноплекс") || n.Contains("xps")) return 35;
        if (n.Contains("минват") || n.Contains("wool")) return 110;
        if (n.Contains("дерев") || n.Contains("брус") || n.Contains("wood") || n.Contains("timber")) return 500;
        return role switch
        {
            MaterialLayerRole.Structural => 2500,
            MaterialLayerRole.Insulation => 35,
            MaterialLayerRole.Screed => IsLeanConcrete(n) ? 2300 : 1800,
            MaterialLayerRole.Waterproofing => 1000,
            _ => 1600
        };
    }

    public static string DefaultColor(MaterialLayerRole role, string? name) => role switch
    {
        MaterialLayerRole.Structural => (name ?? string.Empty).ToLowerInvariant() is var n && (n.Contains("газо") || n.Contains("кирпич") || n.Contains("brick")) ? "#E3DED6" : "#C4C1BB",
        MaterialLayerRole.Insulation => "#E9D28A",
        MaterialLayerRole.Screed => "#B4AFA6",
        MaterialLayerRole.Waterproofing => "#3A3D44",
        _ => "#C98B6B"
    };

    public static string RoleTitle(MaterialLayerRole role) => role switch
    {
        MaterialLayerRole.Structural => "Несущий",
        MaterialLayerRole.Insulation => "Изоляция",
        MaterialLayerRole.Screed => "Стяжка",
        MaterialLayerRole.Waterproofing => "Гидроизоляция",
        _ => "Отделка"
    };

    public static MaterialLayerRole ParseRole(string? raw) =>
        Enum.TryParse<MaterialLayerRole>(raw?.Trim(), true, out var role) ? role : InferRole(raw);

    /// <summary>Пресеты инженерных пирогов (порядок слоёв — см. <see cref="MaterialLayer"/>).</summary>
    public static List<MaterialLayer> Preset(string id) => id switch
    {
        // Плита перекрытия с полом: ж/б 200 + звукоизоляция 30 + стяжка 50 (снизу вверх).
        PresetFloor =>
        [
            new("Монолитный ж/б B25", 200, 2500, MaterialLayerRole.Structural),
            new("Звукоизоляция (минвата)", 30, 110, MaterialLayerRole.Insulation),
            new("Цементно-песчаная стяжка", 50, 1800, MaterialLayerRole.Screed)
        ],
        // Плоская кровля: ж/б 200 + пароизоляция + пеноплекс 150 + разуклонка 50 + гидроизоляция (снизу вверх).
        PresetFlatRoof =>
        [
            new("Монолитный ж/б B25", 200, 2500, MaterialLayerRole.Structural),
            new("Пароизоляция", 2, 1000, MaterialLayerRole.Waterproofing),
            new("Утеплитель Пеноплекс", 150, 35, MaterialLayerRole.Insulation),
            new("Разуклонка (керамзитобетон)", 50, 1200, MaterialLayerRole.Screed),
            new("Гидроизоляция (Техноэласт, 2 слоя)", 8, 1100, MaterialLayerRole.Waterproofing)
        ],
        // Фундаментная плита: бетонная подготовка B3.5 100 + гидроизоляция + ж/б B25 400 (снизу вверх).
        PresetFoundation =>
        [
            new("Бетонная подготовка B3.5", 100, 2300, MaterialLayerRole.Screed),
            new("Гидроизоляция (мембрана)", 4, 1000, MaterialLayerRole.Waterproofing),
            new("Монолитный ж/б B25", 400, 2500, MaterialLayerRole.Structural)
        ],
        // Наружная стена с утеплением: несущий слой 200 + минвата 120 + облицовка (от грани +Z к грани −Z).
        PresetExteriorWall =>
        [
            new("Газосиликатный блок D700", 200, 700, MaterialLayerRole.Structural),
            new("Утеплитель (минвата)", 120, 110, MaterialLayerRole.Insulation),
            new("Облицовка (фиброцемент)", 20, 1600, MaterialLayerRole.Finish)
        ],
        _ => []
    };

    /// <summary>Пресет наружной стены с несущим ж/б вместо газоблока (для монолитных стен).</summary>
    public static List<MaterialLayer> PresetFor(ArchitecturalElement e, string id)
    {
        var layers = Preset(id);
        if (id == PresetExteriorWall && (e.Material ?? string.Empty).Contains("concrete", StringComparison.OrdinalIgnoreCase))
            layers[0] = new MaterialLayer("Монолитный ж/б B25", 200, 2500, MaterialLayerRole.Structural);
        return layers;
    }

    /// <summary>Пресет, подходящий типу элемента по умолчанию.</summary>
    public static string DefaultPresetFor(ArchitecturalElement e) => e.Type switch
    {
        ElementTypes.Wall => PresetExteriorWall,
        ElementTypes.Foundation => PresetFoundation,
        ElementTypes.Roof => PresetFlatRoof,
        _ => PresetFloor
    };

    /// <summary>Сводка пирога «200 + 30 + 50 = 280 мм».</summary>
    public static string Summary(IReadOnlyList<MaterialLayer> layers) =>
        layers.Count == 0
            ? string.Empty
            : string.Join(" + ", layers.Select(l => l.ThicknessMm.ToString("0.##", CultureInfo.InvariantCulture))) +
              " = " + TotalMm(layers).ToString("0.##", CultureInfo.InvariantCulture) + " мм";
}
