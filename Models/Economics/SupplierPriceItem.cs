using System.Text.Json.Serialization;

namespace AIArchitectStudio.Models.Economics;

/// <summary>
/// Позиция прайс-листа поставщика: код материала (B25, A400, A240, C245, Glazing…), категория, наименование, единица (м³, т, м²),
/// цена за единицу в рублях, поставщик (компания, завод, магазин) и документ-основание (номер и дата прайса, ссылка, письмо).
/// </summary>
public sealed class SupplierPriceItem
{
    public string MaterialCode { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string Unit { get; set; } = string.Empty;
    public double UnitPriceRub { get; set; }
    public string SupplierName { get; set; } = string.Empty;
    public string SourceDocument { get; set; } = string.Empty;

    /// <summary>Код для сопоставления с позициями ведомости: латиница, без пробелов и дефисов, верхний регистр.</summary>
    [JsonIgnore]
    public string Key => PriceCodes.Normalize(MaterialCode);

    [JsonIgnore]
    public bool IsValid => Key.Length > 0 && double.IsFinite(UnitPriceRub) && UnitPriceRub >= 0 && UnitPriceRub < 1e10;

    public SupplierPriceItem Clone() => (SupplierPriceItem)MemberwiseClone();
}

/// <summary>Коды позиций ведомости, по которым ищутся цены прайс-листов, и категории материалов.</summary>
public static class PriceCodes
{
    public const string Glazing = "GLAZING";
    public const string EmbeddedPlates = "C245";
    public const string RolledSteel = "C245";

    public const string CategoryConcrete = "Бетон";
    public const string CategoryRebar = "Арматура";
    public const string CategorySteel = "Стальной прокат";
    public const string CategoryGlazing = "Остекление";

    /// <summary>«В25» → «B25», «А500С» → «A500C», «glazing» → «GLAZING» (кириллица-двойники — латиницей).</summary>
    public static string Normalize(string? code) => MaterialLibrary.NormalizeCode(code);

    /// <summary>Категория по коду (для строк прайса без категории).</summary>
    public static string CategoryOf(string code)
    {
        string key = Normalize(code);
        if (key == Glazing) return CategoryGlazing;
        return MaterialLibrary.Standard(key)?.Family switch
        {
            MaterialFamily.Concrete => CategoryConcrete,
            MaterialFamily.Rebar => CategoryRebar,
            MaterialFamily.Steel => CategorySteel,
            _ => string.Empty
        };
    }

    /// <summary>Единица по коду: бетон — м³, остекление — м², арматура и сталь — т.</summary>
    public static string UnitOf(string code)
    {
        string key = Normalize(code);
        if (key == Glazing) return "м²";
        return MaterialLibrary.Standard(key)?.Family == MaterialFamily.Concrete ? "м³" : "т";
    }
}
