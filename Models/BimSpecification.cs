using System.Globalization;
using AIArchitectStudio.Services;

namespace AIArchitectStudio.Models;

/// <summary>Строка BIM-сметы.</summary>
public sealed class BimSpecificationLine
{
    public string Section { get; set; } = string.Empty;
    public string Item { get; set; } = string.Empty;
    public double Quantity { get; set; }
    public string Unit { get; set; } = string.Empty;
    public double Cost { get; set; }
    public bool IsHeader { get; set; }
    public bool IsTotal { get; set; }

    /// <summary>Культура интерфейса (RU / EN) для форматирования чисел.</summary>
    public CultureInfo Culture { get; set; } = CultureInfo.CurrentCulture;

    /// <summary>Количество: пусто для заголовков и итоговой стоимости, целое — для штучных величин (этажность).</summary>
    public string QuantityText => IsHeader || string.IsNullOrEmpty(Unit)
        ? string.Empty
        : Quantity.ToString(Quantity == Math.Round(Quantity) ? "N0" : Quantity < 10 ? "N2" : "N1", Culture) + " " + Unit;
    public string CostText => Cost > 0 ? Cost.ToString("N0", Culture) + " ₽" : string.Empty;
}

/// <summary>Объём (м³) или площадь (м²) слоя многослойных конструкций.</summary>
public sealed record LayerQuantity(string Name, MaterialLayerRole Role, double Volume, double Area);

/// <summary>Сводные показатели одной постройки (или участка целиком).</summary>
public sealed class BuildingMetrics
{
    public string GroupId { get; set; } = string.Empty;
    public int Storeys { get; set; }
    public double FootprintArea { get; set; }
    public double FloorArea { get; set; }
    public double ConcreteVolume { get; set; }
    public double GlazingArea { get; set; }
    public double PoolVolume { get; set; }
    public double ConcreteCost { get; set; }
    public double GlazingCost { get; set; }

    /// <summary>Арматурная сталь и закладной прокат.</summary>
    public SteelBreakdown Steel { get; set; } = new();
    public double SteelCost { get; set; }

    /// <summary>Сталь по группам конструкций (фундаменты, колонны, балки, плиты, стены…).</summary>
    public Dictionary<string, SteelBreakdown> SteelByType { get; set; } = [];

    /// <summary>Слои пирогов: название и назначение → объём/площадь.</summary>
    public Dictionary<(string Name, MaterialLayerRole Role), (double Volume, double Area)> Layers { get; set; } = [];

    public double TotalCost => ConcreteCost + GlazingCost + SteelCost;
}

/// <summary>BIM-спецификация (смета) с разбивкой по постройкам участка; рассчитывается <c>BimAnalyticsService</c>.</summary>
public sealed class BimSpecification
{
    public static readonly BimSpecification Empty = new();

    public DateTime GeneratedAt { get; set; } = DateTime.Now;
    public double ConcretePricePerM3 { get; set; }
    public double GlazingPricePerM2 { get; set; }
    public double RebarPricePerTon { get; set; }
    public RebarClass RebarClass { get; set; } = RebarClass.A400;
    public List<BuildingMetrics> Buildings { get; set; } = [];
    public BuildingMetrics? SiteTotal { get; set; }
    public List<BimSpecificationLine> Lines { get; set; } = [];

    public double TotalConcrete => Buildings.Sum(b => b.ConcreteVolume);
    public double TotalGlazing => Buildings.Sum(b => b.GlazingArea);
    public double TotalPool => Buildings.Sum(b => b.PoolVolume);
    public double TotalSteelKg => Buildings.Sum(b => b.Steel.Total);
    public double TotalSteelCost => Buildings.Sum(b => b.SteelCost);
    public double TotalCost => Buildings.Sum(b => b.TotalCost);
}
