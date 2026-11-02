#if DEBUG
using System.Globalization;
using System.Text;
using System.Text.Json;
using AIArchitectStudio.Models;
using AIArchitectStudio.Models.Economics;

namespace AIArchitectStudio.Services;

/// <summary>
/// Автотест блока девелопера: прайс-листы поставщиков (CSV для Excel и JSON, разбор чужого CSV), доказательная смета с поставщиком
/// и документом-основанием, ручные правки и блок costEstimate .aia; паспорт расчёта колонны, балки, плиты и фундамента
/// (K_исп паспорта ≡ StructuralAnalysisService до бита, ссылки на СП 63 и СП 20), паспорта всех несущих элементов пресетов;
/// переопределение характеристик материалов (класс проекта, сертификат элемента, сброс к СП), γn по ГОСТ 27751, строгий μmin,
/// неизменность результата расчёта и его аллокаций.
/// </summary>
public static partial class CadSelfTestService
{
    private static async Task<int> CheckDeveloperBlockAsync(string directory, StringBuilder report)
    {
        int failures = 0;
        var inv = CultureInfo.InvariantCulture;

        // ═══ 1. Прайс-лист поставщика: CSV (UTF-8 с BOM, «;») и JSON туда и обратно, разбор чужого CSV ═══
        var catalog = new MaterialPriceCatalog
        {
            Name = "УралБетонГрупп · Екатеринбург",
            UpdatedAt = new DateTime(2026, 10, 1),
            Items =
            [
                new() { MaterialCode = "В25", Category = "Бетон", DisplayName = "Бетон B25 W6 F150 П4", Unit = "м³", UnitPriceRub = 7950.5, SupplierName = "ООО \"УралБетонГрупп\"", SourceDocument = "Прайс №14 от 01.10.2026" },
                new() { MaterialCode = "А400", Category = "Арматура", DisplayName = "Арматура А400 Ø12–Ø25", Unit = "т", UnitPriceRub = 68900, SupplierName = "МеталлСервис-Екб; склад №2", SourceDocument = "КП 2026/117 от 28.09.2026" },
                new() { MaterialCode = "A240", Category = "Арматура", DisplayName = "Арматура А240 Ø6–Ø10", Unit = "т", UnitPriceRub = 72400, SupplierName = "МеталлСервис-Екб; склад №2", SourceDocument = "КП 2026/117 от 28.09.2026" },
                new() { MaterialCode = "С245", Category = "Стальной прокат", DisplayName = "Прокат С245", Unit = "т", UnitPriceRub = 91500, SupplierName = "УралСтальКомплект", SourceDocument = "Прайс от 15.09.2026" },
                new() { MaterialCode = "Glazing", Category = "Остекление", DisplayName = "Стеклопакет 2К, алюминиевый профиль", Unit = "м²", UnitPriceRub = 16800, SupplierName = "ФасадСтройЕкб", SourceDocument = "Счёт №3301" }
            ]
        };
        string csvPath = Path.Combine(directory, "supplier-price.csv"), jsonPath = Path.Combine(directory, "supplier-price.json");
        await File.WriteAllBytesAsync(csvPath, catalog.ToCsvBytes());
        await File.WriteAllTextAsync(jsonPath, catalog.ToJson(), new UTF8Encoding(false));
        var fromCsv = MaterialPriceCatalog.Load(csvPath);
        var fromJson = MaterialPriceCatalog.Load(jsonPath);
        var raw = await File.ReadAllBytesAsync(csvPath);
        bool bom = raw.Length > 3 && raw[0] == 0xEF && raw[1] == 0xBB && raw[2] == 0xBF;
        bool SameItems(MaterialPriceCatalog c) => c.Name == catalog.Name && c.UpdatedAt == catalog.UpdatedAt && c.Items.Count == catalog.Items.Count &&
            c.Items.Zip(catalog.Items).All(p => p.First.Key == p.Second.Key && p.First.UnitPriceRub.Equals(p.Second.UnitPriceRub) && p.First.SupplierName == p.Second.SupplierName &&
                                                p.First.SourceDocument == p.Second.SourceDocument && p.First.DisplayName == p.Second.DisplayName && p.First.Unit == p.Second.Unit);
        // Прайс сметчика из Excel: свой порядок колонок, пробелы разрядов, «руб.», десятичная запятая, кавычки, пустые строки.
        const string foreign = "﻿Поставщик;Цена;Код;Основание\r\n\r\n\"ООО \"\"Бетон-Сервис\"\"\";\"9 250,75 руб.\";B30;Письмо №5\r\nЗавод ЖБИ-3;10 100;в35;\r\n;;;\r\nбез цены;;B40;\r\n";
        var parsed = MaterialPriceCatalog.FromCsv(foreign, "Прайс сметчика");
        bool foreignOk = parsed.Name == "Прайс сметчика" && parsed.Items.Count == 2 && parsed.Find("B30") is { UnitPriceRub: 9250.75, SupplierName: "ООО \"Бетон-Сервис\"", SourceDocument: "Письмо №5" } &&
                         parsed.Find("В35") is { UnitPriceRub: 10100, Unit: "м³", Category: "Бетон" };
        Check(report, ref failures, bom && SameItems(fromCsv) && SameItems(fromJson) && foreignOk && catalog.Find("b25")?.UnitPriceRub == 7950.5 && catalog.Find("A500C") is null,
            $"прайс поставщика: CSV {raw.Length} Б (UTF-8 с BOM, «;», кавычки и «;» в названии поставщика) и JSON туда и обратно — {fromCsv.Items.Count} позиций, цены, поставщики и документы совпали; " +
            $"чужой CSV (свой порядок колонок, «9 250,75 руб.», кириллические коды «в35») — {parsed.Items.Count} позиции, строки без цены отброшены");

        // ═══ 2. Доказательная смета по ведомости объёмов: поставщик и основание у каждой позиции ═══
        var preset = PresetScenesService.Create(PresetScenesService.Frame);
        var elements = preset.Elements.ToList();
        var groups = preset.Groups.ToList();
        var loads = new StructuralLoadSettings();
        var analysis = StructuralAnalysisService.Analyze(elements, groups, loads);
        var punching = BimAnalyticsService.PunchingReinforcedIds(analysis);
        var schedule = QuantityScheduleService.Calculate(elements, analysis.UtilizationById, punching, loads.Rebar);
        var rates = new CostEstimateService.ProjectRates(8500, 75000, 18000);
        var plain = CostEstimateService.Build(schedule, loads.Concrete, rates);
        var frameCost = QuantityScheduleService.EstimateFrameCost(schedule, 8500, 75000);
        bool plainOk = plain.Lines.All(l => l.Origin == PriceOrigin.ProjectRates) && Math.Abs(plain.FrameTotal - frameCost.Total) <= 1e-9 * Math.Max(1, frameCost.Total);
        var priced = CostEstimateService.Build(schedule, loads.Concrete, rates, fromCsv);
        var concreteLine = priced.Line(CostEstimateService.Concrete)!;
        var working = priced.Line(CostEstimateService.RebarWorking)!;
        double expectedTotal = priced.Lines.Where(l => l.InFrameTotal).Sum(l => l.Quantity * l.UnitPriceRub);
        bool attributed = concreteLine is { UnitPriceRub: 7950.5, Supplier: "ООО \"УралБетонГрупп\"", SourceDocument: "Прайс №14 от 01.10.2026", Origin: PriceOrigin.SupplierCatalog } &&
                          working is { UnitPriceRub: 68900, Origin: PriceOrigin.SupplierCatalog } && priced.Line(CostEstimateService.RebarA240)!.UnitPriceRub == 72400 &&
                          priced.Line(CostEstimateService.Embedded)!.Supplier == "УралСтальКомплект" && !priced.Line(CostEstimateService.Rolled)!.InFrameTotal &&
                          !priced.Line(CostEstimateService.Glazing)!.InFrameTotal && Math.Abs(priced.FrameTotal - expectedTotal) < 1e-6 &&
                          Math.Abs(concreteLine.SumRub - schedule.ConcreteTotal * 7950.5) < 1e-6;
        var full = CostEstimateService.Build(schedule, loads.Concrete, rates, fromCsv, includeSteelAndGlazing: true);
        var manual = CostEstimateService.Build(schedule, loads.Concrete, rates, fromCsv,
            [new CostEstimateLine { Key = CostEstimateService.Concrete, UnitPriceRub = 8100, Supplier = "БетонЛюкс", SourceDocument = "Счёт №42", Manual = true }]);
        bool manualOk = manual.Line(CostEstimateService.Concrete) is { UnitPriceRub: 8100, Supplier: "БетонЛюкс", SourceDocument: "Счёт №42", Origin: PriceOrigin.Manual } &&
                        manual.Line(CostEstimateService.RebarWorking)!.Origin == PriceOrigin.SupplierCatalog;
        string tsv = CostEstimateService.ToTsv(priced, CultureInfo.GetCultureInfo("ru-RU"));
        bool tsvOk = tsv.Contains("Поставщик\tОснование цены") && tsv.Contains("МеталлСервис-Екб; склад №2") && tsv.Contains("Прайс №14 от 01.10.2026") && tsv.Split('\n')[1].Count(c => c == '\t') == 7;
        Check(report, ref failures, plainOk && attributed && manualOk && tsvOk && full.FrameTotal > priced.FrameTotal,
            $"доказательная смета «Каркас»: без поставщика итог {plain.FrameTotal:N0} ₽ = смета каркаса {frameCost.Total:N0} ₽; по прайсу — бетон B25 {schedule.ConcreteTotal:0.00} м³ × 7 950,50 = {concreteLine.SumRub:N0} ₽ " +
            $"(«{concreteLine.Supplier}», {concreteLine.SourceDocument}), А400 {working.Quantity:0.000} т × 68 900, итог каркаса {priced.FrameTotal:N0} ₽ (= Σ строк), с прокатом и остеклением {full.FrameTotal:N0} ₽; " +
            "ручная правка побеждает прайс; таблица для Excel со столбцами «Поставщик» и «Основание цены»");

        // ═══ 3. Блок costEstimate .aia: доказательные строки только при поставщиках и правках ═══
        var estimate = new ProjectCostEstimate
        {
            ConcretePricePerM3 = 8500, RebarPricePerTon = 75000, ConcreteM3 = 1, RebarKg = 1, FrameCostRub = Math.Round(manual.FrameTotal), SavedAt = new DateTime(2026, 10, 31),
            CatalogName = fromCsv.Name, CatalogDate = fromCsv.UpdatedAt, Lines = CostEstimateService.ToFileLines(manual)
        };
        string withLines = JsonSerializer.Serialize(new SceneProjectParameters { CostEstimate = estimate }, SceneProjectFile.JsonOptions);
        var back = JsonSerializer.Deserialize<SceneProjectParameters>(withLines, SceneProjectFile.JsonOptions)?.CostEstimate;
        string legacy = JsonSerializer.Serialize(new SceneProjectParameters { CostEstimate = new ProjectCostEstimate { ConcretePricePerM3 = 8500, RebarPricePerTon = 75000, SavedAt = estimate.SavedAt } },
            SceneProjectFile.JsonOptions);
        bool aiaOk = back?.Lines is { Count: 6 } lines && lines.Single(l => l.Key == CostEstimateService.Concrete) is { Manual: true, Supplier: "БетонЛюкс", UnitPriceRub: 8100 } &&
                     lines.Single(l => l.Key == CostEstimateService.RebarWorking) is { Manual: false, Supplier: "МеталлСервис-Екб; склад №2" } && back.CatalogName == fromCsv.Name &&
                     !legacy.Contains("lines", StringComparison.OrdinalIgnoreCase) && !legacy.Contains("catalogName", StringComparison.OrdinalIgnoreCase) &&
                     !JsonSerializer.Serialize(new StructuralLoadSettings(), SceneProjectFile.JsonOptions).Contains("consequenceClass", StringComparison.OrdinalIgnoreCase) &&
                     !JsonSerializer.Serialize(new StructuralLoadSettings(), SceneProjectFile.JsonOptions).Contains("materials", StringComparison.OrdinalIgnoreCase);
        var template = CostEstimateService.TemplateFor(plain);
        string templatePath = Path.Combine(directory, "price-template.csv");
        await File.WriteAllBytesAsync(templatePath, template.ToCsvBytes());
        var templateBack = MaterialPriceCatalog.Load(templatePath);
        bool templateOk = templateBack.Items.Count == template.Items.Count && template.Items.Count >= MaterialLibrary.StandardGrades.Count + 1 && templateBack.Find("B25")?.UnitPriceRub == 8500;
        Check(report, ref failures, aiaOk && templateOk,
            $"costEstimate .aia: {back?.Lines?.Count} строк с поставщиком, основанием и признаком ручной правки туда и обратно, база «{back?.CatalogName}»; без поставщиков и правок блоков lines/catalogName нет " +
            $"(как прежде), класс ответственности и библиотека материалов по умолчанию не пишутся; шаблон прайса — {template.Items.Count} позиций (все марки СП + остекление), читается обратно");

        // ═══ 4. Паспорт расчёта: колонна, балка, плита, фундамент — K_исп ≡ StructuralAnalysisService до бита ═══
        var (frame, frameGroups, _) = CreateFrameScene();
        var seismic = new StructuralLoadSettings { Seismicity = 8 };
        var model = AnalyticalModelBuilder.Build(frame, frameGroups, seismic);
        var solved = StructuralAnalysisService.Analyze(model);
        var independent = StructuralAnalysisService.Analyze(frame, frameGroups, seismic);
        var lines4 = new List<string>();
        bool passportsOk = true;
        // Ж/б колонна, балка, плита и фундамент — ссылки СП 63 и СП 20; стальная балка (труба) — СП 16 и СП 20.
        foreach (var (category, steel) in new[] { (BimCategory.Column, false), (BimCategory.Beam, false), (BimCategory.Slab, false), (BimCategory.Foundation, false), (BimCategory.Beam, true) })
        {
            var worst = frame.Where(e => e.Category == category && solved.For(e.Id) is not null && ReinforcementCalculator.IsReinforced(e) != steel)
                .MaxBy(e => solved.For(e.Id)!.Utilization);
            var p = worst is null ? null : CalculationJustificationService.Build(worst.Id, model, solved);
            string text = p?.ToReportText() ?? string.Empty;
            string code = steel ? CalculationJustificationService.Sp16 : CalculationJustificationService.Sp63;
            bool ok = p is not null && p.MatchesSolver && BitConverter.DoubleToInt64Bits(p.Utilization) == BitConverter.DoubleToInt64Bits(independent.For(worst!.Id)!.Utilization) &&
                      p.RecomputedChecks >= 2 && p.Checks.Where(s => s.Recomputed).All(s => s.Verified) &&
                      p.Norms.Any(n => n.Document == code) && p.Norms.Any(n => n.Document == CalculationJustificationService.Sp20) &&
                      p.Loads.Count >= 2 && p.MaterialSource.Contains(steel ? "СП 16.13330.2017, табл. В.5" : "СП 63.13330.2018, табл. 6.8") &&
                      text.Contains("K_исп") && text.Contains(code) && text.Contains("СП 20.13330.2016");
            passportsOk &= ok;
            string why = p is null || ok ? string.Empty
                : $" [сверка {p.MatchesSolver}, отдельный расчёт {BitConverter.DoubleToInt64Bits(p.Utilization) == BitConverter.DoubleToInt64Bits(independent.For(worst!.Id)!.Utilization)}, " +
                  $"проверки {string.Join("/", p.Checks.Where(s => s.Recomputed && !s.Verified).Select(s => $"{s.Title} {s.Value:R}≠{s.SolverValue:R}"))}, нагрузок {p.Loads.Count}, " +
                  $"СП63 {p.Norms.Any(n => n.Document == CalculationJustificationService.Sp63)}, источник «{p.MaterialSource}»]";
            lines4.Add(p is null ? $"{category}: нет" : $"{p.CategoryTitle} «{p.ElementTitle}» K = {p.Utilization.ToString("0.000", inv)} ({p.Governing}), пересчитано {p.RecomputedChecks} из {p.Checks.Count()}, шагов {p.Steps.Count}{why}");
        }
        Check(report, ref failures, passportsOk,
            "паспорт расчёта (сцена «Каркас», сейсмика 8): " + string.Join("; ", lines4) +
            " — K_исп паспорта ≡ StructuralAnalysisService до бита (и ≡ отдельному расчёту), все пересчитанные проверки совпали, ссылки на СП 63.13330.2018 и СП 20.13330.2016 в записке");

        // Паспорта всех несущих элементов четырёх пресетов (ядро, диагрид, стены, оболочки, фермы) — тот же K_исп до бита.
        int total = 0, matched = 0, recomputed = 0;
        var mismatches = new List<string>();
        foreach (var id in new[] { PresetScenesService.Cottage, PresetScenesService.Frame, PresetScenesService.Tower, PresetScenesService.Pavilion })
        {
            var demo = PresetScenesService.Create(id);
            var demoModel = AnalyticalModelBuilder.Build(demo.Elements.ToList(), demo.Groups.ToList(), seismic);
            var demoResult = StructuralAnalysisService.Analyze(demoModel);
            foreach (var elementId in demoResult.Elements.Keys)
            {
                total++;
                var p = CalculationJustificationService.Build(elementId, demoModel, demoResult);
                if (p is null) continue;
                recomputed += p.RecomputedChecks;
                if (p.MatchesSolver && p.Checks.Where(s => s.Recomputed).All(s => s.Verified)) matched++;
                else if (mismatches.Count < 4) mismatches.Add($"{id}/{p.CategoryTitle} «{p.ElementTitle}»: {p.RecomputedUtilization:R} ≠ {p.Utilization:R}");
            }
        }
        Check(report, ref failures, total > 0 && matched == total,
            $"паспорта всех несущих элементов 4 пресетов: {matched} из {total} воспроизводят K_исп до бита, пересчитано проверок {recomputed}" +
            (mismatches.Count > 0 ? " · " + string.Join("; ", mismatches) : string.Empty));

        // ═══ 5. Переопределение материалов: класс проекта, сертификат элемента, сброс к СП, γn, строгий μmin ═══
        string baseFingerprint = AnalysisFingerprint(independent);
        var column = frame.Where(e => e.Category == BimCategory.Column).MaxBy(e => independent.For(e.Id)!.Utilization)!;
        var stronger = seismic.Clone();
        var b25 = MaterialLibrary.Standard("B25")!.Clone();
        b25.Strength = 19.5;
        b25.Certificate = "Протокол испытаний №7 от 12.10.2026";
        stronger.Materials = [b25];
        var strongerResult = StructuralAnalysisService.Analyze(frame, frameGroups, stronger);
        var strongerPassport = CalculationJustificationService.Build(column.Id, frame, frameGroups, stronger).Passport;
        // Сброс: те же марки со значениями СП — список нормализуется в null, результат ≡ эталону до бита.
        var reset = seismic.Clone();
        reset.Materials = MaterialLibrary.StandardGrades.Select(g => g.Clone()).ToList();
        reset.Sanitize();
        var resetResult = StructuralAnalysisService.Analyze(frame, frameGroups, reset);
        // Только цена и поставщик марки: расчёт не меняется, смета берёт цену библиотеки.
        var priceOnly = seismic.Clone();
        var b25Price = MaterialLibrary.Standard("B25")!.Clone();
        b25Price.UnitPriceRub = 8800;
        b25Price.Supplier = "ЖБИ-Урал";
        priceOnly.Materials = MaterialLibrary.Normalize([b25Price]);
        var priceOnlyResult = StructuralAnalysisService.Analyze(frame, frameGroups, priceOnly);
        var libraryLine = CostEstimateService.Build(schedule, loads.Concrete, rates with { Materials = priceOnly.Materials }).Line(CostEstimateService.Concrete)!;
        var settings = new AppSettings();
        settings.StructuralLoads.Materials = [b25];
        var settingsBack = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(settings));
        settingsBack!.StructuralLoads.Sanitize();
        bool classOk = strongerResult.For(column.Id)!.Utilization < independent.For(column.Id)!.Utilization &&
                       strongerPassport is { MaterialFromCertificate: true } sp && sp.MaterialSource.Contains("Протокол испытаний №7") &&
                       sp.MaterialProperties.First(m => m.Symbol == "Rb").Value == 19.5 && sp.MatchesSolver &&
                       reset.Materials is null && AnalysisFingerprint(resetResult) == baseFingerprint && AnalysisFingerprint(priceOnlyResult) == baseFingerprint &&
                       libraryLine is { UnitPriceRub: 8800, Supplier: "ЖБИ-Урал" } && settingsBack.StructuralLoads.Materials is [{ Code: "B25", Strength: 19.5 }];
        Check(report, ref failures, classOk,
            $"библиотека материалов: B25 по сертификату Rb = 19,5 МПа — K колонны «{column.Name}» {independent.For(column.Id)!.Utilization:0.000} → {strongerResult.For(column.Id)!.Utilization:0.000}, " +
            "паспорт: «Пользовательский сертификат материала» с номером протокола; «Сбросить к нормативам СП 63.13330» — переопределений нет, КЭ-расчёт ≡ эталону до бита; " +
            "цена и поставщик марки не меняют расчёт и попадают в смету; марка сохраняется в settings.json");

        // Сертификат элемента («Свойства пользователя»): меняется только этот элемент.
        var certified = frame.Select(e => e.Clone()).ToList();
        var target = certified.First(e => e.Id == column.Id);
        target.SetUserParameter(MaterialCertificate.StrengthDefinition, ParameterValue.FromDouble(25));
        var certifiedResult = StructuralAnalysisService.Analyze(certified, frameGroups, seismic);
        bool othersSame = certifiedResult.Elements.Count == independent.Elements.Count &&
                          certifiedResult.Elements.Where(p => p.Key != column.Id).All(p => SameResult(p.Value, independent.For(p.Key)!));
        var certPassport = CalculationJustificationService.Build(column.Id, certified, frameGroups, seismic).Passport;
        Check(report, ref failures, othersSame && certifiedResult.For(column.Id)!.Utilization < independent.For(column.Id)!.Utilization &&
                                    certPassport is { MaterialFromCertificate: true, MatchesSolver: true } && certPassport.MaterialProperties.First(m => m.Symbol == "Rb").Source.Contains("сертификат"),
            $"сертификат элемента (Rb = 25 МПа в «Материал (сертификат)»): K колонны {certifiedResult.For(column.Id)!.Utilization:0.000}, остальные {certifiedResult.Elements.Count - 1} элементов ≡ прежним до бита, паспорт ссылается на сертификат");

        // γn по ГОСТ 27751-2014: КС-3 — проверки I группы × 1,1 (до бита), прогиб без изменений; КС-2 — как прежде.
        var ks3 = seismic.Clone();
        ks3.ConsequenceClass = 3;
        var ks3Result = StructuralAnalysisService.Analyze(frame, frameGroups, ks3);
        bool gammaOk = ks3Result.Elements.All(p => independent.For(p.Key) is { } r0 && p.Value.KStrength.Equals(r0.KStrength * 1.1) && p.Value.KStability.Equals(r0.KStability * 1.1) &&
                                                   p.Value.KShear.Equals(r0.KShear * 1.1) && p.Value.KDeflection.Equals(r0.KDeflection) && p.Value.KPunching.Equals(r0.KPunching * 1.1));
        var ks3Passport = CalculationJustificationService.Build(column.Id, frame, frameGroups, ks3).Passport;
        Check(report, ref failures, gammaOk && ks3Passport is { GammaN: 1.1, MatchesSolver: true } && ks3Passport.Norms.Any(n => n.Document == CalculationJustificationService.Gost27751),
            $"класс ответственности КС-3 (γn = 1,1): K прочности, устойчивости, среза и продавливания ×1,1 до бита у {ks3Result.Elements.Count} элементов, прогиб без изменений; паспорт с γn и ГОСТ 27751-2014");

        // Строгий контроль μmin: колонна получает не меньше μ расчёта (1,5 %), отключение возвращает прежний расход.
        double before = ReinforcementCalculator.Compute(column, 1, 0).Total;
        bool strict = ReinforcementCalculator.StrictMinimum;
        double strictMass;
        try
        {
            ReinforcementCalculator.StrictMinimum = true;
            strictMass = ReinforcementCalculator.Compute(column, 1, 0).Total;
        }
        finally
        {
            ReinforcementCalculator.StrictMinimum = strict;
        }
        double after = ReinforcementCalculator.Compute(column, 1, 0).Total;
        Check(report, ref failures, strictMass > before && Math.Abs(strictMass - ReinforcementCalculator.StrictMinimumRho(ElementTypes.Column)) < 1e-9 && after.Equals(before),
            $"строгий μmin (СП 63, п. 10.3.6 и μ расчёта): колонна при K = 0 — {before:0.0} → {strictMass:0.0} кг/м³, выключение возвращает {after:0.0} кг/м³");

        // ═══ 6. Горячий путь: результат расчёта без новых полей, паспорт не добавляет аллокаций солверу ═══
        int resultProps = typeof(ElementStructuralResult).GetProperties().Length;
        StructuralAnalysisService.Analyze(model);
        long Measure()
        {
            var samples = new long[5];
            for (int i = 0; i < samples.Length; i++)
            {
                long a0 = GC.GetAllocatedBytesForCurrentThread();
                StructuralAnalysisService.Analyze(model);
                samples[i] = GC.GetAllocatedBytesForCurrentThread() - a0;
            }
            Array.Sort(samples);
            return samples[2];
        }
        long solverBefore = Measure();
        long p0 = GC.GetAllocatedBytesForCurrentThread();
        var hotPassport = CalculationJustificationService.Build(column.Id, model, solved);
        long passportBytes = GC.GetAllocatedBytesForCurrentThread() - p0;
        long solverAfter = Measure();
        Check(report, ref failures, resultProps == 25 && hotPassport is not null && Math.Abs(solverAfter - solverBefore) <= solverBefore / 50,
            $"горячий путь: ElementStructuralResult без новых полей ({resultProps} свойств), расчёт схемы «Каркас» — {solverBefore / 1024.0:0.0} КБ до паспорта и {solverAfter / 1024.0:0.0} КБ после " +
            $"(паспорт строится отдельно по запросу — {passportBytes / 1024.0:0.0} КБ), эталон ядра 7 сцен проверен в начале прогона без перезаписи");
        return failures;
    }

    private static bool SameResult(ElementStructuralResult a, ElementStructuralResult b) =>
        a.AxialForce.Equals(b.AxialForce) && a.Moment.Equals(b.Moment) && a.Shear.Equals(b.Shear) && a.KStrength.Equals(b.KStrength) && a.KStability.Equals(b.KStability) &&
        a.KShear.Equals(b.KShear) && a.KDeflection.Equals(b.KDeflection) && a.KPunching.Equals(b.KPunching) && a.KSoil.Equals(b.KSoil) && a.DeflectionMm.Equals(b.DeflectionMm);
}
#endif
