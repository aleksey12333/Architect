#if DEBUG
using System.Globalization;
using System.Text;
using AIArchitectStudio.Models;
using AIArchitectStudio.Models.Economics;

namespace AIArchitectStudio.Services;

/// <summary>
/// Проверка демонстрационного комплекта DemoKit (вне основного автотеста — счёт проверок не меняется): прайс поставщиков CSV
/// читается <see cref="MaterialPriceCatalog"/> и попадает в доказательную смету, библиотека .aialib.json —
/// <see cref="StandardLibraryService"/> без отброшенных записей и назначается элементам одним шагом журнала, PNG плана —
/// <see cref="RasterPlanTracer"/> → <see cref="PlanToBimPipelineService"/> с K_исп ≤ 1,0. Нет PNG — записывается синтетический план.
/// </summary>
public static partial class CadSelfTestService
{
    public const string DemoKitPriceFile = "Прайс_Екатеринбург_Осень_2026.csv";
    public const string DemoKitLibraryFile = "Стандарт_Девелопера_ЖБИ.aialib.json";
    public const string DemoKitPlanFile = "План_этажа_секция_А.png";

    public static async Task<bool> VerifyDemoKitAsync(string kitDirectory, string reportPath)
    {
        var report = new StringBuilder();
        int failures = 0;
        var inv = CultureInfo.InvariantCulture;

        // ═══ 1. Прайс поставщиков: CSV для Excel → база цен → доказательная смета каркаса ═══
        string pricePath = Path.Combine(kitDirectory, DemoKitPriceFile);
        try
        {
            byte[] raw = await File.ReadAllBytesAsync(pricePath);
            bool bom = raw.Length > 3 && raw[0] == 0xEF && raw[1] == 0xBB && raw[2] == 0xBF;
            var catalog = MaterialPriceCatalog.Load(pricePath);
            string[] codes = ["B25", "B30", "B35", "A400", "A500C", "A240", "C245", "C345", "GLAZING"];
            bool items = catalog.Items.Count == codes.Length && codes.All(c => catalog.Find(c) is { UnitPriceRub: > 0, SupplierName.Length: > 0, SourceDocument.Length: > 0 }) &&
                         catalog.Items.All(i => i.Category.Length > 0 && i.Unit.Length > 0);
            var preset = PresetScenesService.Create(PresetScenesService.Frame);
            var elements = preset.Elements.ToList();
            var loads = new StructuralLoadSettings();
            var analysis = StructuralAnalysisService.Analyze(elements, preset.Groups.ToList(), loads);
            var schedule = QuantityScheduleService.Calculate(elements, analysis.UtilizationById, BimAnalyticsService.PunchingReinforcedIds(analysis), loads.Rebar);
            var priced = CostEstimateService.Build(schedule, loads.Concrete, new CostEstimateService.ProjectRates(8500, 75000, 18000), catalog, includeSteelAndGlazing: true);
            bool fromSupplier = priced.Lines.All(l => l.Origin == PriceOrigin.SupplierCatalog && l.Supplier.Length > 0 && l.SourceDocument.Length > 0);
            Check(report, ref failures, bom && items && fromSupplier && catalog.UpdatedAt == new DateTime(2026, 10, 20),
                $"прайс «{catalog.Name}» от {catalog.UpdatedAt.ToString("dd.MM.yyyy", inv)}: {raw.Length} Б, UTF-8 с BOM {(bom ? "да" : "нет")}, позиций {catalog.Items.Count} " +
                $"({string.Join(", ", catalog.Items.Select(i => $"{i.MaterialCode} {i.UnitPriceRub.ToString("0", inv)}"))}); смета каркасного корпуса по прайсу — " +
                $"{string.Join("; ", priced.Lines.Select(l => $"{l.Code}: {l.Supplier}"))}; итог {priced.GrandTotal.ToString("N0", inv)} ₽");
        }
        catch (Exception ex)
        {
            Check(report, ref failures, false, $"прайс {DemoKitPriceFile}: {ex.GetType().Name}: {ex.Message}");
        }

        // ═══ 2. Библиотека девелопера: .aialib.json → импорт без потерь → «Назначить выделенным» одним шагом журнала, Undo до бита ═══
        string libraryPath = Path.Combine(kitDirectory, DemoKitLibraryFile);
        try
        {
            int declared = System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(libraryPath, Encoding.UTF8)).RootElement.GetProperty("entries").GetArrayLength();
            var service = new StandardLibraryService(Path.Combine(Path.GetTempPath(), "AIArchitectStudio", "Libraries-demokit")) { IsSessionOnly = true };
            var library = service.Import(libraryPath);
            var (frame, frameGroups, frameAxes) = CreateFrameScene();
            var scene = new SceneStateService();
            scene.LoadScene(frame, frameGroups, recordUndo: false, clearHistory: true, axes: frameAxes, dimensions: []);
            string original = DocumentFingerprint(scene);
            int applied = 0, oneStep = 0, undone = 0;
            var lines = new List<string>();
            foreach (var entry in library.Entries)
            {
                var target = scene.Elements.FirstOrDefault(entry.IsCompatible);
                if (target is null)
                {
                    lines.Add($"{entry.Mark}: нет элемента категории");
                    continue;
                }
                int before = scene.Transactions.UndoCount;
                var result = StandardLibraryService.Apply(scene, entry, [target]);
                if (result.Applied.Count == 1) applied++;
                if (scene.Transactions.UndoCount - before == 1) oneStep++;
                lines.Add($"{entry.Mark} → «{target.Name}» ({entry.SizeText})");
                scene.Undo();
                if (DocumentFingerprint(scene) == original) undone++;
            }
            int n = library.Entries.Count;
            Check(report, ref failures, n == declared && n >= 10 && applied == n && oneStep == n && undone == n &&
                                        library.Entries.Any(e => !e.IsLoadBearing) && library.Entries.Any(e => e.Layers.Count > 1),
                $"библиотека «{library.Name}»: записей {n} из {declared} в файле (колонн {library.Entries.Count(e => e.Section == StandardLibrarySection.Columns)}, " +
                $"ригелей {library.Entries.Count(e => e.Section == StandardLibrarySection.Beams)}, плит {library.Entries.Count(e => e.Section == StandardLibrarySection.Slabs)}, " +
                $"фундаментов {library.Entries.Count(e => e.Section == StandardLibrarySection.Foundations)}, стен и перегородок " +
                $"{library.Entries.Count(e => e.Section is StandardLibrarySection.Walls or StandardLibrarySection.Partitions)}); назначено {applied}, по 1 шагу журнала {oneStep}, " +
                $"Undo ≡ исходной сцене {undone}: {string.Join("; ", lines)}");
        }
        catch (Exception ex)
        {
            Check(report, ref failures, false, $"библиотека {DemoKitLibraryFile}: {ex.GetType().Name}: {ex.Message}");
        }

        // ═══ 3. Поэтажный план PNG: трассировка → нормализация → каркас, K_исп ≤ 1,0 при 1…5 этажах ═══
        string planPath = Path.Combine(kitDirectory, DemoKitPlanFile);
        try
        {
            bool written = false;
            if (!File.Exists(planPath))
            {
                await WriteDemoPlanAsync(planPath);
                written = true;
            }
            var plan = await RasterPlanTracer.LoadAsync(planPath);
            int windows = plan.Openings.Count(o => o.Kind == PlanOpeningKind.Window), doors = plan.Openings.Count(o => o.Kind == PlanOpeningKind.Door);
            var runs = Enumerable.Range(1, 5).Select(storeys => PlanToBimPipelineService.Run(plan,
                new PlanToBimOptions { Storeys = storeys, GroupId = Path.GetFileNameWithoutExtension(DemoKitPlanFile) }, new StructuralLoadSettings())).ToList();
            var two = runs[1];
            bool ok = Math.Abs(plan.MmPerPixel - 20) < 0.01 && windows == 4 && doors == 3 && Math.Abs(two.WidthM - 16) < 0.03 && Math.Abs(two.DepthM - 10) < 0.03 &&
                      runs.All(r => r.ExteriorWalls == 4 && r.InteriorWalls == 2 && r.PhysicsErrors == 0 && r.MaxUtilization <= 1.0 && r.IsStructurallySound && r.IsClosedContour);
            Check(report, ref failures, ok,
                $"план {DemoKitPlanFile}{(written ? " (записан синтетический план)" : string.Empty)}: {plan.PixelWidth}×{plan.PixelHeight} px, {plan.MmPerPixel.ToString("0.0", inv)} мм/пикс ({plan.ScaleSource}), " +
                $"габарит {two.WidthM.ToString("0.00", inv)} × {two.DepthM.ToString("0.00", inv)} м, стен {two.ExteriorWalls} + {two.InteriorWalls}, окон {windows}, дверей {doors}, колонн на 2 этажа {two.Columns}, осей {two.Axes.Count}; " +
                $"K_исп max по этажности 1…5: {string.Join(" / ", runs.Select(r => r.MaxUtilization.ToString("0.00", inv)))}, без опоры {runs.Sum(r => r.PhysicsErrors)}, " +
                $"армирование (2 эт.) {(two.RebarKg / 1000).ToString("0.00", inv)} т");
        }
        catch (Exception ex)
        {
            Check(report, ref failures, false, $"план {DemoKitPlanFile}: {ex.GetType().Name}: {ex.Message}");
        }

        report.Insert(0, (failures == 0 ? "ИТОГ: УСПЕХ" : $"ИТОГ: ОШИБОК {failures}") + Environment.NewLine + $"DemoKit: {Path.GetFullPath(kitDirectory)}" + Environment.NewLine);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(reportPath)) ?? ".");
        await File.WriteAllTextAsync(reportPath, report.ToString(), new UTF8Encoding(true));
        return failures == 0;
    }
}
#endif
