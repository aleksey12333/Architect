using System.Globalization;
using System.Text.Json.Serialization;

namespace AIArchitectStudio.Models;

/// <summary>
/// Конструктивная роль элемента: признак «Несущая конструкция» — общий параметр с постоянным Guid («Свойства пользователя»,
/// IFC AIA_UserParameters, DXF XData). Без явно заданного значения элемент несущий, как раньше: эталонные сцены считаются до бита.
/// Ненесущая перегородка (значение «нет») передаёт вниз только собственный вес и не служит опорой перекрытиям и балкам.
/// </summary>
public static class StructuralRole
{
    public static readonly Guid LoadBearingGuid = new("8a3e5d21-4b6c-4f0e-9d17-3c2b1a0f6e51");

    public const string Group = "Конструктивная роль";

    // Определение регистрируется в общем реестре только при первой записи признака (не при чтении расчётом).
    public static ParameterDefinition LoadBearingDefinition => Definitions.LoadBearing;

    private static class Definitions
    {
        public static readonly ParameterDefinition LoadBearing = Register(new ParameterDefinition
        {
            Guid = LoadBearingGuid, Name = "Несущая конструкция", SpecType = ParameterSpecType.YesNo, StorageType = ParameterStorageType.Bool, Group = Group
        });
    }

    internal static ParameterDefinition Register(ParameterDefinition d) => ParameterRegistry.Shared.Register(d) ?? d;

    /// <summary>Несущий элемент: признак не задан (по умолчанию) или задан «да». Без пользовательских параметров — одна проверка на null.</summary>
    public static bool IsLoadBearing(ArchitecturalElement e)
    {
        if (e.UserParameters is null) return true;
        var v = e.GetUserParameter(LoadBearingGuid);
        return v.IsEmpty || v.AsBool();
    }

    /// <summary>Явно задан признак «ненесущая» (стена-перегородка).</summary>
    public static bool IsNonBearingPartition(ArchitecturalElement e) => e.Category == BimCategory.Wall && !IsLoadBearing(e);

    /// <summary>Задаёт признак (null — снять, элемент снова несущий по умолчанию). true — значение изменилось.</summary>
    public static bool Set(ArchitecturalElement e, bool? loadBearing) =>
        loadBearing is { } value ? e.SetUserParameter(LoadBearingDefinition, ParameterValue.FromBool(value)) : e.RemoveUserParameter(LoadBearingGuid);
}

/// <summary>Обозначение типоразмера из библиотеки ГОСТ/СП у элемента (общий параметр «Типоразмер по ГОСТ/СП»): «К 400×400 · ГОСТ 25628».</summary>
public static class StandardTypeMark
{
    public static readonly Guid MarkGuid = new("8a3e5d21-4b6c-4f0e-9d17-3c2b1a0f6e52");
    public static readonly Guid StandardGuid = new("8a3e5d21-4b6c-4f0e-9d17-3c2b1a0f6e53");

    public const string Group = "Типоразмер (библиотека)";

    public static ParameterDefinition MarkDefinition => Definitions.Mark;
    public static ParameterDefinition StandardDefinition => Definitions.Standard;

    private static class Definitions
    {
        public static readonly ParameterDefinition Mark = StructuralRole.Register(new ParameterDefinition
        {
            Guid = MarkGuid, Name = "Типоразмер по ГОСТ/СП", SpecType = ParameterSpecType.Text, StorageType = ParameterStorageType.String, Group = Group
        });

        public static readonly ParameterDefinition Standard = StructuralRole.Register(new ParameterDefinition
        {
            Guid = StandardGuid, Name = "Нормативный документ типоразмера", SpecType = ParameterSpecType.Text, StorageType = ParameterStorageType.String, Group = Group
        });
    }

    public static string? Read(ArchitecturalElement e)
    {
        if (e.UserParameters is null) return null;
        var v = e.GetUserParameter(MarkGuid);
        return v.IsEmpty ? null : v.AsString();
    }

    public static void Write(ArchitecturalElement e, string mark, string standard)
    {
        e.SetUserParameter(MarkDefinition, ParameterValue.FromString(mark));
        e.SetUserParameter(StandardDefinition, ParameterValue.FromString(standard));
    }
}

/// <summary>Раздел библиотеки (группа типовых семейств).</summary>
public enum StandardLibrarySection
{
    Columns,
    Beams,
    Slabs,
    Foundations,
    Walls,
    Partitions
}

/// <summary>Слой стенового или плитного пирога в записи библиотеки (мм, кг/м³, роль).</summary>
public sealed class StandardLayerSpec
{
    public string Name { get; set; } = string.Empty;
    public double ThicknessMm { get; set; }
    public double DensityKgM3 { get; set; }
    public MaterialLayerRole Role { get; set; } = MaterialLayerRole.Structural;
    public string? ColorHex { get; set; }

    public StandardLayerSpec() { }

    public StandardLayerSpec(string name, double thicknessMm, double densityKgM3, MaterialLayerRole role)
    {
        Name = name;
        ThicknessMm = thicknessMm;
        DensityKgM3 = densityKgM3;
        Role = role;
    }

    public MaterialLayer ToLayer() => new(Name, ThicknessMm, DensityKgM3, Role, ColorHex);

    public static StandardLayerSpec From(MaterialLayer l) => new(l.Name, l.ThicknessMm, l.DensityKgM3, l.Role) { ColorHex = l.ColorHex };
}

/// <summary>
/// Типовой элемент (семейство) библиотеки: категория, материал, профиль, размеры типа по категории, пирог и признак несущей
/// конструкции; нормативный документ и марка. Применяется к элементу через фасад <see cref="ArchitecturalElement"/> — тип
/// экземпляра находится или создаётся в <see cref="BimTypeRegistry"/> по сигнатуре (как «Дублировать тип»).
/// <para>Размеры типа (мм): колонна — B × H сечения (W × D), балка — b × h (D × H), стена и перегородка — толщина (сумма слоёв),
/// плита и фундамент — толщина H.</para>
/// </summary>
public sealed class StandardLibraryEntry
{
    /// <summary>Постоянный ключ записи: «col-400x400», «beam-i-30b1», «user-…».</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Марка типоразмера: «К 400×400», «30Б1», «П 200».</summary>
    public string Mark { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    /// <summary>Нормативный документ или серия: «ГОСТ 25628 / Серия 1.020», «ГОСТ Р 57837-2017».</summary>
    public string Standard { get; set; } = string.Empty;

    public StandardLibrarySection Section { get; set; }

    /// <summary>Тип элемента (<see cref="ElementTypes"/>): column, beam, slab, foundation, wall.</summary>
    public string ElementType { get; set; } = ElementTypes.Column;

    public string Material { get; set; } = "concrete";

    /// <summary>Класс материала: «B25–B40», «С245/С345», «D400».</summary>
    public string MaterialGrade { get; set; } = string.Empty;

    public string ProfileType { get; set; } = ProfileTypes.Rectangle;

    /// <summary>Первый размер типа, мм: колонна — W, балка — ширина b (D), плита/фундамент/стена — толщина.</summary>
    public double WidthMm { get; set; }

    /// <summary>Второй размер типа, мм: колонна — D, балка — высота h (H).</summary>
    public double DepthMm { get; set; }

    /// <summary>Толщина полки двутавра / стенки трубы, мм.</summary>
    public double FlangeMm { get; set; }

    /// <summary>Толщина стенки двутавра, мм.</summary>
    public double WebMm { get; set; }

    public List<StandardLayerSpec> Layers { get; set; } = [];

    /// <summary>Несущая конструкция (false — ненесущая перегородка: только собственный вес).</summary>
    public bool IsLoadBearing { get; set; } = true;

    /// <summary>Типовая высота (стена, колонна) или длина (балка) при вставке нового элемента, мм; 0 — по этажу.</summary>
    public double DefaultLengthMm { get; set; }

    public string Notes { get; set; } = string.Empty;

    /// <summary>Встроенная запись ГОСТ/СП (только чтение) или пользовательская.</summary>
    [JsonIgnore] public bool IsBuiltIn { get; set; }

    [JsonIgnore] public BimCategory Category => BimCategories.FromType(ElementType);

    /// <summary>Толщина стены/плиты из пирога (сумма слоёв), иначе WidthMm.</summary>
    [JsonIgnore] public double ThicknessMm => Layers.Count > 0 ? Layers.Sum(l => Math.Max(0, l.ThicknessMm)) : WidthMm;

    /// <summary>Подпись размеров: «400 × 400 мм», «h = 200 мм», «140 × 296 мм, tf 8.5 / tw 5.8».</summary>
    [JsonIgnore]
    public string SizeText
    {
        get
        {
            var c = CultureInfo.InvariantCulture;
            string Mm(double v) => v.ToString("0.#", c);
            return Category switch
            {
                BimCategory.Column => $"{Mm(WidthMm)} × {Mm(DepthMm)} мм",
                BimCategory.Beam when ProfileType == ProfileTypes.IBeam => $"b × h {Mm(WidthMm)} × {Mm(DepthMm)} мм, tf {Mm(FlangeMm)} / tw {Mm(WebMm)}",
                BimCategory.Beam => $"b × h {Mm(WidthMm)} × {Mm(DepthMm)} мм",
                BimCategory.Wall when Layers.Count > 1 => $"{MaterialLayers.Summary(Layers.Select(l => l.ToLayer()).ToList())}",
                _ => $"h = {Mm(ThicknessMm)} мм"
            };
        }
    }

    /// <summary>Подпись для журнала и марки элемента: «К 400×400 (ГОСТ 25628)».</summary>
    [JsonIgnore] public string Caption => string.IsNullOrWhiteSpace(Standard) ? Mark : $"{Mark} ({ShortStandard})";

    [JsonIgnore] public string ShortStandard => Standard.Split('/', ';')[0].Trim();

    /// <summary>Совместима ли запись с элементом (та же категория; перегородка — стена; плита — также консоль, терраса и плоская кровля).</summary>
    public bool IsCompatible(ArchitecturalElement e) =>
        e.Category == Category || Category == BimCategory.Slab && (e.Category is BimCategory.CantileverSlab or BimCategory.Terrace || e.IsFlatRoof);

    public StandardLibraryEntry Clone() => new()
    {
        Id = Id, Mark = Mark, Name = Name, Standard = Standard, Section = Section, ElementType = ElementType, Material = Material, MaterialGrade = MaterialGrade,
        ProfileType = ProfileType, WidthMm = WidthMm, DepthMm = DepthMm, FlangeMm = FlangeMm, WebMm = WebMm, IsLoadBearing = IsLoadBearing,
        DefaultLengthMm = DefaultLengthMm, Notes = Notes, IsBuiltIn = IsBuiltIn,
        Layers = Layers.Select(l => new StandardLayerSpec(l.Name, l.ThicknessMm, l.DensityKgM3, l.Role) { ColorHex = l.ColorHex }).ToList()
    };

    public override string ToString() => $"{Id}: {Mark} · {Standard}";
}

/// <summary>
/// Библиотека типовых элементов: встроенная (ГОСТ/СП) или пользовательская (корпоративные стандарты проектного отдела).
/// Файл обмена — <c>.aialib.json</c> (JSON camelCase, <see cref="Format"/> и <see cref="Version"/> в шапке).
/// </summary>
public sealed class StandardLibrary
{
    public const string FileFormat = "aialib";
    public const string FileExtension = ".aialib.json";

    public string Format { get; set; } = FileFormat;
    public int Version { get; set; } = 1;
    public string Name { get; set; } = string.Empty;
    public string Author { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public string Description { get; set; } = string.Empty;
    public List<StandardLibraryEntry> Entries { get; set; } = [];

    [JsonIgnore] public bool IsBuiltIn { get; set; }

    public StandardLibraryEntry? Find(string id) => Entries.FirstOrDefault(e => string.Equals(e.Id, id, StringComparison.OrdinalIgnoreCase));
}
