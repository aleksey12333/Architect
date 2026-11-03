namespace AIArchitectStudio.Models;

/// <summary>
/// Параметры полой кровли (общие параметры с постоянными Guid — «Свойства пользователя», IFC AIA_UserParameters, DXF XData):
/// толщина пирога ската, карнизный свес, высота парапета и фронтонное окно. Читаются только у полых форм <see cref="RoofShapes"/>
/// (gable_hollow, hip_hollow, shed_hollow, flat_parapet, mansard, barrel_vault); у прежних форм кровля строится как раньше.
/// Без заданного значения — типовое (250 мм, 450 мм, 900 мм).
/// </summary>
public static class RoofDesign
{
    public static readonly Guid SlabThicknessGuid = new("8a3e5d21-4b6c-4f0e-9d17-3c2b1a0f6e61");
    public static readonly Guid OverhangGuid = new("8a3e5d21-4b6c-4f0e-9d17-3c2b1a0f6e62");
    public static readonly Guid ParapetHeightGuid = new("8a3e5d21-4b6c-4f0e-9d17-3c2b1a0f6e63");
    public static readonly Guid GableWindowGuid = new("8a3e5d21-4b6c-4f0e-9d17-3c2b1a0f6e64");

    public const string Group = "Кровля (конструкция)";

    public const double DefaultSlabThickness = 0.25;
    public const double DefaultOverhang = 0.45;
    public const double DefaultParapetHeight = 0.9;

    /// <summary>Толщина ж/б парапета плоской эксплуатируемой кровли, м.</summary>
    public const double ParapetThickness = 0.2;

    /// <summary>Толщина фронтонной стены, м.</summary>
    public const double GableWallThickness = 0.3;

    private static class Definitions
    {
        public static readonly ParameterDefinition SlabThickness = StructuralRole.Register(new ParameterDefinition
        {
            Guid = SlabThicknessGuid, Name = "Толщина пирога ската", SpecType = ParameterSpecType.Length, StorageType = ParameterStorageType.Double, Group = Group
        });

        public static readonly ParameterDefinition Overhang = StructuralRole.Register(new ParameterDefinition
        {
            Guid = OverhangGuid, Name = "Карнизный свес", SpecType = ParameterSpecType.Length, StorageType = ParameterStorageType.Double, Group = Group
        });

        public static readonly ParameterDefinition ParapetHeight = StructuralRole.Register(new ParameterDefinition
        {
            Guid = ParapetHeightGuid, Name = "Высота парапета", SpecType = ParameterSpecType.Length, StorageType = ParameterStorageType.Double, Group = Group
        });

        public static readonly ParameterDefinition GableWindow = StructuralRole.Register(new ParameterDefinition
        {
            Guid = GableWindowGuid, Name = "Фронтонное окно", SpecType = ParameterSpecType.YesNo, StorageType = ParameterStorageType.Bool, Group = Group
        });
    }

    /// <summary>Толщина ската (перпендикулярно скату), м: 80…600 мм.</summary>
    public static double SlabThickness(ArchitecturalElement e) => Clamp(Read(e, SlabThicknessGuid), DefaultSlabThickness, 0.08, 0.6);

    /// <summary>Свес карниза в плане от грани стены, м: 0…1 500 мм.</summary>
    public static double Overhang(ArchitecturalElement e) => Clamp(Read(e, OverhangGuid), DefaultOverhang, 0, 1.5);

    /// <summary>Высота парапета над верхом плиты покрытия, м: 300…1 500 мм (типовое 600–900).</summary>
    public static double ParapetHeight(ArchitecturalElement e) => Clamp(Read(e, ParapetHeightGuid), DefaultParapetHeight, 0.3, 1.5);

    public static bool HasGableWindow(ArchitecturalElement e) => e.UserParameters is not null && e.GetUserParameter(GableWindowGuid) is { IsEmpty: false } v && v.AsBool();

    public static void Write(ArchitecturalElement e, double slabThickness, double overhang, double? parapetHeight = null, bool? gableWindow = null)
    {
        e.SetUserParameter(Definitions.SlabThickness, ParameterValue.FromDouble(slabThickness));
        e.SetUserParameter(Definitions.Overhang, ParameterValue.FromDouble(overhang));
        if (parapetHeight is { } p) e.SetUserParameter(Definitions.ParapetHeight, ParameterValue.FromDouble(p));
        if (gableWindow is { } g) e.SetUserParameter(Definitions.GableWindow, ParameterValue.FromBool(g));
    }

    private static double? Read(ArchitecturalElement e, Guid guid)
    {
        if (e.UserParameters is null) return null;
        var v = e.GetUserParameter(guid);
        return v.IsEmpty ? null : v.AsDouble();
    }

    private static double Clamp(double? value, double fallback, double min, double max) =>
        value is { } v && double.IsFinite(v) ? Math.Clamp(v, min, max) : fallback;
}

/// <summary>Форма лестницы в плане (генератор лестниц).</summary>
public enum StairShape
{
    Straight,
    LShaped,
    UShaped,
    ThreeFlight,
    Spiral
}

/// <summary>Конструкция марша: монолитный по наклонной плите, на двух косоурах, консольные ступени на больцах.</summary>
public enum StairConstruction
{
    MonolithicSlab,
    Stringers,
    Cantilever
}

/// <summary>
/// Конструкция лестничного марша и винтовая форма — общие параметры с постоянными Guid. Марш без параметра строится прежним
/// сплошным монолитом (эталонные сцены не меняются); генератор лестниц всегда записывает конструкцию явно.
/// </summary>
public static class StairDesign
{
    public static readonly Guid ConstructionGuid = new("8a3e5d21-4b6c-4f0e-9d17-3c2b1a0f6e71");
    public static readonly Guid ShapeGuid = new("8a3e5d21-4b6c-4f0e-9d17-3c2b1a0f6e72");

    public const string Group = "Лестница (конструкция)";

    public const string MonolithicCode = "monolithic_slab";
    public const string StringersCode = "stringers";
    public const string CantileverCode = "cantilever";
    public const string SpiralCode = "spiral";

    private static class Definitions
    {
        public static readonly ParameterDefinition Construction = StructuralRole.Register(new ParameterDefinition
        {
            Guid = ConstructionGuid, Name = "Конструкция марша", SpecType = ParameterSpecType.Text, StorageType = ParameterStorageType.String, Group = Group
        });

        public static readonly ParameterDefinition Shape = StructuralRole.Register(new ParameterDefinition
        {
            Guid = ShapeGuid, Name = "Форма марша", SpecType = ParameterSpecType.Text, StorageType = ParameterStorageType.String, Group = Group
        });
    }

    public static string Code(StairConstruction c) => c switch
    {
        StairConstruction.Stringers => StringersCode,
        StairConstruction.Cantilever => CantileverCode,
        _ => MonolithicCode
    };

    /// <summary>Конструкция марша; null — параметр не задан (прежний сплошной монолит).</summary>
    public static StairConstruction? Construction(ArchitecturalElement e)
    {
        if (e.UserParameters is null) return null;
        var v = e.GetUserParameter(ConstructionGuid);
        if (v.IsEmpty) return null;
        return v.AsString() switch
        {
            StringersCode => StairConstruction.Stringers,
            CantileverCode => StairConstruction.Cantilever,
            MonolithicCode => StairConstruction.MonolithicSlab,
            _ => null
        };
    }

    /// <summary>Винтовой марш (одним элементом вокруг центральной стойки).</summary>
    public static bool IsSpiral(ArchitecturalElement e) =>
        e.UserParameters is not null && e.GetUserParameter(ShapeGuid) is { IsEmpty: false } v && v.AsString() == SpiralCode;

    public static void Write(ArchitecturalElement e, StairConstruction construction, bool spiral = false)
    {
        e.SetUserParameter(Definitions.Construction, ParameterValue.FromString(Code(construction)));
        if (spiral) e.SetUserParameter(Definitions.Shape, ParameterValue.FromString(SpiralCode));
    }
}
