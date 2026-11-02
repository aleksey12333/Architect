using System.Globalization;
using AIArchitectStudio.Models;
using AIArchitectStudio.Models.Economics;

namespace AIArchitectStudio.Services;

/// <summary>Происхождение цены строки сметы.</summary>
public enum PriceOrigin
{
    /// <summary>Расценки проекта (настройки «Общие → Смета» или библиотека материалов).</summary>
    ProjectRates,

    /// <summary>Позиция загруженного прайс-листа поставщика.</summary>
    SupplierCatalog,

    /// <summary>Цена и поставщик заданы вручную в окне ведомости (сохраняются в .aia).</summary>
    Manual
}

/// <summary>
/// Строка доказательной сметы: позиция ведомости, количество, цена за единицу, поставщик, документ-основание и сумма.
/// <see cref="InFrameTotal"/> — строка входит в итог стоимости каркаса.
/// </summary>
public sealed record PricedLine(
    string Key, string Code, string Category, string Title, string Unit, double Quantity, double UnitPriceRub,
    string Supplier, string SourceDocument, PriceOrigin Origin, bool InFrameTotal)
{
    public double SumRub => Quantity * UnitPriceRub;
}

/// <summary>Доказательная смета каркаса по ведомости объёмов и базе цен.</summary>
public sealed record PricedEstimate(IReadOnlyList<PricedLine> Lines, string CatalogName, DateTime? CatalogDate)
{
    public static readonly PricedEstimate Empty = new([], string.Empty, null);

    /// <summary>Итог стоимости каркаса: строки, входящие в итог (по умолчанию — бетон, арматура и закладные).</summary>
    public double FrameTotal => Lines.Where(l => l.InFrameTotal).Sum(l => l.SumRub);

    /// <summary>Все позиции, включая не вошедшие в итог каркаса (прокат и остекление при выключенном учёте).</summary>
    public double GrandTotal => Lines.Sum(l => l.SumRub);

    public PricedLine? Line(string key) => Lines.FirstOrDefault(l => l.Key == key);
}

/// <summary>
/// Доказательная смета: каждая позиция ведомости объёмов (бетон класса проекта, рабочая арматура, А240, закладные С245, стальной
/// прокат, остекление) получает цену по цепочке «ручная правка → прайс поставщика → библиотека материалов → расценки проекта»
/// с поставщиком и документом-основанием. Итог каркаса без поставщиков и правок совпадает с <see cref="FrameCostEstimate"/>.
/// </summary>
public static class CostEstimateService
{
    public const string Concrete = "concrete", RebarWorking = "rebar-working", RebarA240 = "rebar-a240", Embedded = "embedded", Rolled = "rolled", Glazing = "glazing";

    public static readonly string[] LineKeys = [Concrete, RebarWorking, RebarA240, Embedded, Rolled, Glazing];

    /// <summary>Расценки проекта по умолчанию (без поставщика): цены из настроек и библиотека материалов проекта.</summary>
    public sealed record ProjectRates(double ConcretePricePerM3, double RebarPricePerTon, double GlazingPricePerM2, IReadOnlyList<MaterialGrade>? Materials = null);

    public static PricedEstimate Build(QuantitySchedule schedule, ConcreteClass concrete, ProjectRates rates, MaterialPriceCatalog? catalog = null,
        IReadOnlyList<CostEstimateLine>? manual = null, bool includeSteelAndGlazing = false)
    {
        var working = schedule.WorkingClass == RebarClass.A240 ? RebarClass.A400 : schedule.WorkingClass;
        string concreteCode = StructuralNorms.Title(concrete);
        string workingCode = MaterialLibrary.NormalizeCode(StructuralNorms.Title(working));
        var lines = new List<PricedLine>
        {
            Line(Concrete, concreteCode, PriceCodes.CategoryConcrete, $"Бетон {concreteCode}", "м³", schedule.ConcreteTotal, rates.ConcretePricePerM3, true),
            Line(RebarWorking, workingCode, PriceCodes.CategoryRebar, $"Арматура {StructuralNorms.Title(working)} (рабочая)", "т", schedule.Rebar.WorkingTotal / 1000.0, rates.RebarPricePerTon, true),
            Line(RebarA240, "A240", PriceCodes.CategoryRebar, "Арматура А240 (хомуты)", "т", schedule.Rebar.A240Total / 1000.0, rates.RebarPricePerTon, true),
            Line(Embedded, PriceCodes.EmbeddedPlates, PriceCodes.CategorySteel, "Закладные изделия С245", "т", schedule.Rebar.PlatesTotal / 1000.0, rates.RebarPricePerTon, true),
            Line(Rolled, PriceCodes.RolledSteel, PriceCodes.CategorySteel, "Прокат С245 (колонны, балки)", "т", schedule.RolledSteelTotal / 1000.0, rates.RebarPricePerTon, includeSteelAndGlazing),
            Line(Glazing, PriceCodes.Glazing, PriceCodes.CategoryGlazing, "Остекление (окна, витражи)", "м²", schedule.GlazingTotal, rates.GlazingPricePerM2, includeSteelAndGlazing)
        };
        return new PricedEstimate(lines, catalog?.Name ?? string.Empty, catalog?.UpdatedAt);

        PricedLine Line(string key, string code, string category, string title, string unit, double quantity, double fallbackPrice, bool inTotal)
        {
            if (manual?.LastOrDefault(m => m.Key == key && m.Manual) is { } edit)
                return new PricedLine(key, code, category, title, unit, quantity, Math.Max(0, edit.UnitPriceRub), edit.Supplier ?? string.Empty,
                    string.IsNullOrWhiteSpace(edit.SourceDocument) ? "Ручной ввод в ведомости" : edit.SourceDocument!, PriceOrigin.Manual, inTotal);
            if (catalog?.Find(code) is { } item)
                return new PricedLine(key, code, string.IsNullOrWhiteSpace(item.Category) ? category : item.Category, title, unit, quantity, item.UnitPriceRub,
                    item.SupplierName, string.IsNullOrWhiteSpace(item.SourceDocument) ? catalog.Title : item.SourceDocument, PriceOrigin.SupplierCatalog, inTotal);
            // Библиотека материалов проекта: базовая цена и поставщик марки (бетон, арматура, сталь), иначе расценки проекта.
            var grade = key is Glazing ? null : MaterialLibrary.Find(rates.Materials, code);
            double price = grade?.UnitPriceRub ?? fallbackPrice;
            string supplier = grade?.Supplier ?? string.Empty;
            string source = grade?.UnitPriceRub is not null ? "Библиотека материалов проекта" : "Расценки проекта (Настройки → Смета)";
            return new PricedLine(key, code, category, title, unit, quantity, price, supplier, source, PriceOrigin.ProjectRates, inTotal);
        }
    }

    /// <summary>Строки для блока costEstimate файла .aia: снимок цен (с поставщиком и основанием) и признак ручной правки.</summary>
    public static List<CostEstimateLine> ToFileLines(PricedEstimate estimate) =>
        estimate.Lines.Select(l => new CostEstimateLine
        {
            Key = l.Key,
            Code = l.Code,
            Title = l.Title,
            Unit = l.Unit,
            Quantity = Math.Round(l.Quantity, 4),
            UnitPriceRub = l.UnitPriceRub,
            Supplier = string.IsNullOrEmpty(l.Supplier) ? null : l.Supplier,
            SourceDocument = l.SourceDocument,
            Manual = l.Origin == PriceOrigin.Manual,
            SumRub = Math.Round(l.SumRub, 2)
        }).ToList();

    /// <summary>Шаблон прайса: позиции ведомости с текущими ценами (поставщик и основание — для заполнения сметчиком).</summary>
    public static MaterialPriceCatalog TemplateFor(PricedEstimate estimate)
    {
        var items = new List<SupplierPriceItem>();
        foreach (var l in estimate.Lines)
        {
            if (items.Any(i => i.Key == PriceCodes.Normalize(l.Code))) continue;
            items.Add(new SupplierPriceItem
            {
                MaterialCode = l.Code,
                Category = l.Category,
                DisplayName = l.Key == Embedded || l.Key == Rolled ? "Прокат стальной С245 (ГОСТ 27772)" : l.Title,
                Unit = l.Unit,
                UnitPriceRub = l.UnitPriceRub,
                SupplierName = l.Supplier,
                SourceDocument = l.Origin == PriceOrigin.ProjectRates ? string.Empty : l.SourceDocument
            });
        }
        // Остальные классы бетона и арматуры — для смены класса проекта без нового прайса.
        foreach (var g in MaterialLibrary.StandardGrades)
            if (!items.Any(i => i.Key == g.Code))
                items.Add(new SupplierPriceItem
                {
                    MaterialCode = g.Code,
                    Category = PriceCodes.CategoryOf(g.Code),
                    DisplayName = g.Family switch
                    {
                        MaterialFamily.Concrete => "Бетон " + g.Code,
                        MaterialFamily.Rebar => "Арматура " + MaterialLibrary.DisplayCode(g),
                        _ => "Прокат стальной " + MaterialLibrary.DisplayCode(g)
                    },
                    Unit = g.PriceUnit,
                    UnitPriceRub = 0
                });
        return MaterialPriceCatalog.Template(items);
    }

    /// <summary>Таблица для Excel (табуляции): позиция, количество, единица, цена, сумма, поставщик, основание цены.</summary>
    public static string ToTsv(PricedEstimate estimate, CultureInfo culture)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("Позиция\tКоличество\tЕд. изм.\tЦена за ед., руб.\tСумма, руб.\tПоставщик\tОснование цены\tВ итоге каркаса");
        foreach (var l in estimate.Lines)
            sb.AppendLine($"{l.Title}\t{l.Quantity.ToString("0.###", culture)}\t{l.Unit}\t{l.UnitPriceRub.ToString("0.##", culture)}\t" +
                          $"{l.SumRub.ToString("0.00", culture)}\t{l.Supplier}\t{l.SourceDocument}\t{(l.InFrameTotal ? "да" : "нет")}");
        sb.AppendLine($"Итого стоимость каркаса\t\t\t\t{estimate.FrameTotal.ToString("0.00", culture)}");
        return sb.ToString();
    }
}
