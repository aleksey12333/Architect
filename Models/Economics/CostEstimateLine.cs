namespace AIArchitectStudio.Models.Economics;

/// <summary>
/// Строка сметы в блоке costEstimate файла .aia: позиция ведомости, количество, цена, поставщик и документ-основание.
/// <see cref="Manual"/> — цена задана вручную в окне ведомости (иначе — снимок цены базы на момент сохранения).
/// </summary>
public sealed class CostEstimateLine
{
    /// <summary>Строка ведомости: concrete, rebar-working, rebar-a240, embedded, rolled, glazing.</summary>
    public string Key { get; set; } = string.Empty;

    public string? Code { get; set; }
    public string? Title { get; set; }
    public string? Unit { get; set; }
    public double Quantity { get; set; }
    public double UnitPriceRub { get; set; }
    public string? Supplier { get; set; }
    public string? SourceDocument { get; set; }
    public bool Manual { get; set; }
    public double SumRub { get; set; }

    public CostEstimateLine Clone() => (CostEstimateLine)MemberwiseClone();
}
