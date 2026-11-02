using System.Text.Json.Serialization;

namespace AIArchitectStudio.Models;

/// <summary>Семейство материала библиотеки: бетон (класс по прочности), арматура (класс), стальной прокат (марка).</summary>
public enum MaterialFamily
{
    Concrete,
    Rebar,
    Steel
}

/// <summary>
/// Марка материала проекта: расчётные характеристики (МПа, кг/м³), базовая цена и поставщик по умолчанию.
/// Бетон: Strength = Rb, Tensile = Rbt, ElasticModulus = Eb. Арматура: Strength = Rs, Tensile = Rsc, ElasticModulus = Es.
/// Сталь: Strength = Ry, Tensile — не используется, ElasticModulus = E.
/// </summary>
public sealed class MaterialGrade
{
    /// <summary>Код марки латиницей: B25, A400, A500C, C245…</summary>
    public string Code { get; set; } = string.Empty;

    public MaterialFamily Family { get; set; }
    public double Strength { get; set; }
    public double Tensile { get; set; }
    public double ElasticModulus { get; set; }
    public double Density { get; set; }

    /// <summary>Базовая цена за единицу (бетон — ₽/м³, арматура и сталь — ₽/т); null — по расценкам проекта.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? UnitPriceRub { get; set; }

    /// <summary>Поставщик по умолчанию (пусто — не указан).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Supplier { get; set; }

    /// <summary>Документ-основание характеристик (сертификат, ТУ, протокол испытаний); пусто — таблицы СП.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Certificate { get; set; }

    public MaterialGrade Clone() => (MaterialGrade)MemberwiseClone();

    /// <summary>Расчётные характеристики отличаются от нормативных (бит в бит): марка взята из сертификата, а не из СП.</summary>
    [JsonIgnore]
    public bool IsStructurallyOverridden => MaterialLibrary.Standard(Code) is not { } norm ||
                                            !norm.Strength.Equals(Strength) || !norm.Tensile.Equals(Tensile) ||
                                            !norm.ElasticModulus.Equals(ElasticModulus) || !norm.Density.Equals(Density);

    /// <summary>Единица цены: м³ — бетон, т — арматура и прокат.</summary>
    [JsonIgnore]
    public string PriceUnit => Family == MaterialFamily.Concrete ? "м³" : "т";

    /// <summary>Пункт норм с табличными значениями марки.</summary>
    [JsonIgnore]
    public string NormReference => MaterialLibrary.NormReference(Family);
}

/// <summary>
/// Библиотека материалов проекта: нормативные марки (бетон B15–B60 — СП 63.13330.2018, табл. 6.8, 6.11; арматура A240/A400/A500C —
/// табл. 6.14; сталь С245/С345 — СП 16.13330.2017, табл. В.5) и переопределения проекта (сертификаты поставщиков). Переопределения
/// хранятся только для изменённых марок: без них расчёт берёт значения из тех же таблиц, что и прежде, до бита.
/// </summary>
public static class MaterialLibrary
{
    public const string SteelC245 = "C245", SteelC345 = "C345";

    /// <summary>Все нормативные марки в порядке таблицы настроек.</summary>
    public static IReadOnlyList<MaterialGrade> StandardGrades { get; } = BuildStandard();

    private static List<MaterialGrade> BuildStandard()
    {
        var list = new List<MaterialGrade>();
        foreach (var c in StructuralNorms.ConcreteClasses)
        {
            var (rb, rbt, eb, _) = StructuralNorms.Concrete(c);
            list.Add(new MaterialGrade { Code = StructuralNorms.Title(c), Family = MaterialFamily.Concrete, Strength = rb, Tensile = rbt, ElasticModulus = eb, Density = 2500 });
        }
        foreach (var r in new[] { RebarClass.A240, RebarClass.A400, RebarClass.A500C })
        {
            var (rs, rsc, _) = StructuralNorms.Rebar(r);
            list.Add(new MaterialGrade { Code = NormalizeCode(StructuralNorms.Title(r)), Family = MaterialFamily.Rebar, Strength = rs, Tensile = rsc, ElasticModulus = StructuralNorms.Es, Density = 7850 });
        }
        list.Add(new MaterialGrade { Code = SteelC245, Family = MaterialFamily.Steel, Strength = StructuralNorms.SteelRy, ElasticModulus = StructuralNorms.SteelE, Density = 7850 });
        list.Add(new MaterialGrade { Code = SteelC345, Family = MaterialFamily.Steel, Strength = StructuralNorms.SteelC345Ry, ElasticModulus = StructuralNorms.SteelE, Density = 7850 });
        return list;
    }

    /// <summary>Нормативная марка по коду (кириллица и регистр не важны); null — неизвестный код.</summary>
    public static MaterialGrade? Standard(string? code)
    {
        string key = NormalizeCode(code);
        foreach (var g in StandardGrades)
            if (g.Code == key) return g;
        return null;
    }

    /// <summary>Переопределение марки в списке проекта (null — марка по СП).</summary>
    public static MaterialGrade? Find(IReadOnlyList<MaterialGrade>? overrides, string code)
    {
        if (overrides is null || overrides.Count == 0) return null;
        string key = NormalizeCode(code);
        for (int i = overrides.Count - 1; i >= 0; i--)
            if (overrides[i].Code == key) return overrides[i];
        return null;
    }

    /// <summary>Действующая марка: переопределение проекта или нормативная.</summary>
    public static MaterialGrade? Effective(IReadOnlyList<MaterialGrade>? overrides, string code) => Find(overrides, code) ?? Standard(code);

    /// <summary>
    /// Нормализация списка переопределений: коды латиницей, неизвестные и некорректные (≤ 0, не число) — отбрасываются, дубликаты —
    /// последний побеждает, марки без отличий от нормативной — не хранятся. Пустой результат — null.
    /// </summary>
    public static List<MaterialGrade>? Normalize(IReadOnlyList<MaterialGrade>? grades)
    {
        if (grades is null || grades.Count == 0) return null;
        var byCode = new Dictionary<string, MaterialGrade>(StringComparer.Ordinal);
        foreach (var g in grades)
        {
            if (g is null || Standard(g.Code) is not { } norm) continue;
            var copy = g.Clone();
            copy.Code = norm.Code;
            copy.Family = norm.Family;
            if (!Positive(copy.Strength) || !Positive(copy.ElasticModulus) || !Positive(copy.Density)) continue;
            if (!double.IsFinite(copy.Tensile) || copy.Tensile < 0) copy.Tensile = norm.Tensile;
            if (copy.UnitPriceRub is { } p && (!double.IsFinite(p) || p < 0)) copy.UnitPriceRub = null;
            copy.Supplier = string.IsNullOrWhiteSpace(copy.Supplier) ? null : copy.Supplier.Trim();
            copy.Certificate = string.IsNullOrWhiteSpace(copy.Certificate) ? null : copy.Certificate.Trim();
            bool same = !copy.IsStructurallyOverridden && copy.UnitPriceRub is null && copy.Supplier is null && copy.Certificate is null;
            if (same) byCode.Remove(copy.Code);
            else byCode[copy.Code] = copy;
        }
        if (byCode.Count == 0) return null;
        // Порядок — как в таблице настроек: файл не зависит от порядка правки.
        return StandardGrades.Where(s => byCode.ContainsKey(s.Code)).Select(s => byCode[s.Code]).ToList();
    }

    private static bool Positive(double v) => double.IsFinite(v) && v > 0;

    /// <summary>Код марки латиницей в верхнем регистре: «В25» → «B25», «А500С» → «A500C», «С245» → «C245».</summary>
    public static string NormalizeCode(string? code)
    {
        if (string.IsNullOrWhiteSpace(code)) return string.Empty;
        Span<char> buffer = stackalloc char[Math.Min(code.Length, 64)];
        int n = 0;
        foreach (char raw in code)
        {
            if (n == buffer.Length) break;
            if (char.IsWhiteSpace(raw) || raw is '-' or '_' or '.') continue;
            char c = char.ToUpperInvariant(raw);
            buffer[n++] = c switch
            {
                'А' => 'A', 'В' => 'B', 'С' => 'C', 'Е' => 'E', 'К' => 'K', 'М' => 'M', 'Н' => 'H', 'О' => 'O', 'Р' => 'P', 'Т' => 'T', 'Х' => 'X',
                _ => c
            };
        }
        return new string(buffer[..n]);
    }

    /// <summary>Подпись марки для интерфейса: бетон — латиницей (B25), арматура и сталь — по ГОСТ кириллицей (А400, С245).</summary>
    public static string DisplayCode(MaterialGrade g) => g.Family switch
    {
        MaterialFamily.Concrete => g.Code,
        _ => g.Code.Replace('A', 'А').Replace('C', 'С')
    };

    public static string NormReference(MaterialFamily family) => family switch
    {
        MaterialFamily.Concrete => "СП 63.13330.2018, табл. 6.8, 6.11",
        MaterialFamily.Rebar => "СП 63.13330.2018, п. 6.2.8, табл. 6.14",
        _ => "СП 16.13330.2017, табл. В.5"
    };
}

/// <summary>
/// Характеристики материала конкретного элемента по сертификату (общие параметры с постоянными Guid — «Свойства пользователя»):
/// Rb (сжатие) и E, МПа, плотность, кг/м³. Пустые значения — характеристики класса проекта.
/// </summary>
public static class MaterialCertificate
{
    public static readonly Guid StrengthGuid = new("6f5c1b0e-3a2d-4c71-9b8e-0a1d5e7c2b41");
    public static readonly Guid ModulusGuid = new("6f5c1b0e-3a2d-4c71-9b8e-0a1d5e7c2b42");
    public static readonly Guid DensityGuid = new("6f5c1b0e-3a2d-4c71-9b8e-0a1d5e7c2b43");

    public const string Group = "Материал (сертификат)";

    // Определения регистрируются в общем реестре только при первой записи сертификата (не при чтении расчётом).
    public static ParameterDefinition StrengthDefinition => Definitions.Strength;
    public static ParameterDefinition ModulusDefinition => Definitions.Modulus;
    public static ParameterDefinition DensityDefinition => Definitions.Density;

    private static class Definitions
    {
        public static readonly ParameterDefinition Strength = Define(StrengthGuid, "Расчётное сопротивление R (сертификат), МПа", ParameterSpecType.Number);
        public static readonly ParameterDefinition Modulus = Define(ModulusGuid, "Модуль упругости E (сертификат), МПа", ParameterSpecType.Number);
        public static readonly ParameterDefinition Density = Define(DensityGuid, "Плотность ρ (сертификат)", ParameterSpecType.Density);

        private static ParameterDefinition Define(Guid guid, string name, ParameterSpecType spec)
        {
            var d = new ParameterDefinition { Guid = guid, Name = name, SpecType = spec, StorageType = ParameterSpecs.StorageFor(spec), Group = Group };
            return ParameterRegistry.Shared.Register(d) ?? d;
        }
    }

    /// <summary>Значения сертификата элемента (NaN — не задано), без аллокаций, если пользовательских параметров нет.</summary>
    public static (double StrengthMPa, double ModulusMPa, double Density) Read(ArchitecturalElement e)
    {
        if (e.UserParameters is null) return (double.NaN, double.NaN, double.NaN);
        return (Value(e, StrengthGuid), Value(e, ModulusGuid), Value(e, DensityGuid));
    }

    public static bool HasAny(ArchitecturalElement e)
    {
        var (r, m, d) = Read(e);
        return !double.IsNaN(r) || !double.IsNaN(m) || !double.IsNaN(d);
    }

    private static double Value(ArchitecturalElement e, Guid guid)
    {
        var v = e.GetUserParameter(guid);
        if (v.IsEmpty) return double.NaN;
        double d = v.AsDouble();
        return double.IsFinite(d) && d > 0 ? d : double.NaN;
    }
}
