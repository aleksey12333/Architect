#if DEBUG
using System.Numerics;
using System.Text;
using System.Text.Json;
using AIArchitectStudio.Models;
using AIArchitectStudio.Models.Geometry;
using AIArchitectStudio.Rendering;

namespace AIArchitectStudio.Services;

/// <summary>
/// Автотест модулей главного окна: ведомость объёмов совпадает со сметой (бетон, арматура, остекление) и считает прокат двутавра,
/// наборы модулей по уровню опыта и их сохранение в settings.json, схема 3D-армирования колонны и плиты.
/// </summary>
public static partial class CadSelfTestService
{
    private static int CheckModules(StringBuilder report)
    {
        int failures = 0;

        // 1. Ведомость объёмов ≡ смета на каркасном корпусе + масса стального двутавра.
        var frame = PresetScenesService.Create(PresetScenesService.Frame);
        var elements = frame.Elements.ToList();
        var loads = new StructuralLoadSettings();
        var analysis = StructuralAnalysisService.Analyze(elements, frame.Groups.ToList(), loads);
        var punching = BimAnalyticsService.PunchingReinforcedIds(analysis);
        var spec = BimAnalyticsService.Calculate(elements, frame.Groups, 9500, 18000, key => key, analysis.UtilizationById, 85000, loads.Rebar, punching);
        var schedule = QuantityScheduleService.Calculate(elements, analysis.UtilizationById, punching, loads.Rebar);
        var beam = new ArchitecturalElement
        {
            Type = ElementTypes.Beam, Material = "steel", ProfileType = ProfileTypes.IBeam, ProfileWallThickness = 0.012, ProfileWebThickness = 0.008
        };
        beam.SetSize(6, 0.4, 0.2);
        var steelOnly = QuantityScheduleService.Calculate([beam]);
        double expected = (2 * 0.2 * 0.012 + (0.4 - 2 * 0.012) * 0.008) * 6 * QuantityScheduleService.SteelDensity;
        bool same = Math.Abs(schedule.ConcreteTotal - spec.TotalConcrete) < 1e-6 && Math.Abs(schedule.Rebar.Total - spec.TotalSteelKg) < 1e-6 &&
                    Math.Abs(schedule.GlazingTotal - spec.TotalGlazing) < 1e-6 && schedule.Concrete.Any(c => c.GroupKey == "QtySlabs") &&
                    schedule.Concrete.Any(c => c.GroupKey == "QtyColumns") && schedule.Concrete.Any(c => c.GroupKey == "QtyBeams");
        Check(report, ref failures, same && Math.Abs(steelOnly.RolledSteelTotal - expected) < 0.01 && steelOnly.ConcreteTotal == 0,
            $"ведомость объёмов: бетон {schedule.ConcreteTotal:0.00} м³ ({string.Join(", ", schedule.Concrete.Select(c => $"{c.GroupKey} {c.Volume:0.0}"))}) = смета {spec.TotalConcrete:0.00}, " +
            $"арматура {schedule.Rebar.Total:0} кг = {spec.TotalSteelKg:0}, остекление {schedule.GlazingTotal:0.0} м² = {spec.TotalGlazing:0.0}; " +
            $"двутавр 400×200 L = 6 м — {steelOnly.RolledSteelTotal:0.0} кг (ожидается {expected:0.0}, «{steelOnly.RolledSteel.FirstOrDefault()?.Profile}»)");

        // 2. Наборы модулей по уровню опыта и сохранение в settings.json.
        var beginner = WorkspaceModules.ForProfile(UserExperienceProfile.Beginner);
        var architect = WorkspaceModules.ForProfile(UserExperienceProfile.Architect);
        var engineer = WorkspaceModules.ForProfile(UserExperienceProfile.StructuralEngineer);
        var custom = beginner with { Reinforcement3D = true, QuantitySchedule = true };
        var settings = new AppSettings { ExperienceProfile = UserExperienceProfile.Beginner, Modules = custom };
        var restored = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(settings));
        var legacy = JsonSerializer.Deserialize<AppSettings>("{\"ExperienceProfile\":\"Architect\"}");
        bool modulesOk = !beginner.Insolation && !beginner.StructuralAnalysis && !beginner.Reinforcement3D && !beginner.ClashInspector && !beginner.QuantitySchedule &&
                         architect is { Insolation: true, QuantitySchedule: true, StructuralAnalysis: false, Reinforcement3D: false, ClashInspector: false } &&
                         engineer is { Insolation: true, StructuralAnalysis: true, Reinforcement3D: true, ClashInspector: true, QuantitySchedule: true } &&
                         restored?.EffectiveModules == custom && legacy?.Modules is null && legacy.EffectiveModules == architect;
        Check(report, ref failures, modulesOk,
            "модули по уровню опыта: студент — ни одного, архитектор — инсоляция и ведомость, ГИП — все пять; свой набор сохраняется в settings.json, " +
            "настройки без Modules берут набор уровня");

        // 3. Схема армирования: колонна 400 × 400 × 3000 и плита 6 × 6 м h = 200.
        var column = new ArchitecturalElement { Type = ElementTypes.Column };
        column.SetSize(0.4, 3, 0.4);
        var columnCage = RebarCageBuilder.Build(column, new BimBounds(new Vector3(-0.2f, -1.5f, -0.2f), new Vector3(0.2f, 1.5f, 0.2f)));
        var slab = new ArchitecturalElement { Type = ElementTypes.Slab };
        slab.SetSize(6, 0.2, 6);
        var slabCage = RebarCageBuilder.Build(slab, new BimBounds(new Vector3(-3f, -0.1f, -3f), new Vector3(3f, 0.1f, 3f)));
        bool inside = columnCage is not null && columnCage.Working.Concat(columnCage.Stirrups).All(p => Math.Abs(p.X) < 0.2f && Math.Abs(p.Z) < 0.2f && Math.Abs(p.Y) <= 1.5f);
        bool cageOk = inside && columnCage!.BarCount == 8 && columnCage.WorkingDiameter == 16 && columnCage.StirrupDiameter == 8 && columnCage.Stirrups.Count == 13 * 8 &&
                      slabCage is { IsMesh: true, BarCount: 60, WorkingDiameter: 10 } && slabCage.Working.Count == 2 * 60 * 2 &&
                      RebarCageBuilder.Build(new ArchitecturalElement { Type = ElementTypes.Wall }, new BimBounds(BimMath.Negate(Vector3.One), Vector3.One)) is null;
        Check(report, ref failures, cageOk,
            $"3D-армирование: колонна 400×400 — {columnCage?.BarCount} Ø{columnCage?.WorkingDiameter}, хомуты Ø{columnCage?.StirrupDiameter} шаг {columnCage?.Step * 1000:0} мм " +
            $"({(columnCage?.Stirrups.Count ?? 0) / 8} шт., все внутри сечения); плита 6×6 h 200 — сетки Ø{slabCage?.WorkingDiameter} {slabCage?.Step * 1000:0}×{slabCage?.Step * 1000:0}, " +
            $"стержней {slabCage?.BarCount} в слое; стена не схематизируется");

        failures += CheckCostAndParametricTools(report, schedule, frame);
        return failures;
    }

    /// <summary>
    /// Смета монолитного каркаса (бетон × цена + арматура, т × цена) и её Round-Trip в .aia; генератор П-образной лестницы
    /// (подступенки 150 мм под высоту этажа, марши, площадка, проём в перекрытии) и скатная кровля по углу.
    /// </summary>
    private static int CheckCostAndParametricTools(StringBuilder report, QuantitySchedule schedule, DemoScene frame)
    {
        int failures = 0;

        // 4. Смета-калькулятор: итог = V·Цб + m/1000·Ца, сохранение в проект и чтение.
        var cost = QuantityScheduleService.EstimateFrameCost(schedule, 8500, 75000);
        double expectedCost = schedule.ConcreteTotal * 8500 + schedule.Rebar.Total / 1000 * 75000;
        var file = new SceneProjectFile
        {
            Parameters = new SceneProjectParameters
            {
                CostEstimate = new ProjectCostEstimate
                {
                    ConcretePricePerM3 = 8500, RebarPricePerTon = 75000, ConcreteM3 = cost.ConcreteM3, RebarKg = cost.RebarKg, FrameCostRub = Math.Round(cost.Total)
                }
            }
        };
        var back = JsonSerializer.Deserialize<SceneProjectFile>(JsonSerializer.Serialize(file, SceneProjectFile.JsonOptions), SceneProjectFile.JsonOptions);
        var est = back?.Parameters.CostEstimate;
        bool costOk = Math.Abs(cost.Total - expectedCost) < 1e-6 && cost.Total > 0 && est is { ConcretePricePerM3: 8500, RebarPricePerTon: 75000 } &&
                      Math.Abs(est.FrameCostRub - Math.Round(expectedCost)) < 0.5 && new AppSettings() is { ConcretePricePerM3: 8500, RebarPricePerTon: 75000 };
        Check(report, ref failures, costOk,
            $"смета каркаса: {cost.ConcreteM3:0.00} м³ × 8 500 ₽ + {cost.RebarTonnes:0.00} т × 75 000 ₽ = {cost.Total:N0} ₽; цены и итог сохраняются в .aia " +
            $"(costEstimate → {est?.FrameCostRub:N0} ₽), расценки по умолчанию 8 500 / 75 000 ₽");

        // 5. П-образная лестница в каркасном корпусе: этаж 1 → 2, проём в плите перекрытия над клеткой.
        var elements = frame.Elements.Select(e => e.Clone()).ToList();
        var scene = new SceneStateService();
        scene.LoadScene(elements, frame.Groups, recordUndo: false, clearHistory: true);
        double floor = scene.FloorHeight(FrameGroup, 0);
        var levels = StructuralAxesBuilder.LevelElevations(scene.Elements.Where(e => e.BuildingGroupId == FrameGroup).ToList());
        double baseY = levels.First(l => l.Level == 0).Elevation;
        var plan = ParametricToolsService.PlanUShaped(floor);
        var stair = ParametricToolsService.BuildUShapedStair(scene.Elements.ToList(), plan, FrameGroup, 0, baseY, 3, 0, 0, "Т-1");
        var flights = stair.Created.Where(e => e.Type == ElementTypes.Stairs).ToList();
        var landing = stair.Created.Single(e => e.Type == ElementTypes.Terrace);
        var hole = stair.Created.FirstOrDefault(e => e.Type == ElementTypes.SlabOpening);
        double riserMm = plan.Riser * 1000;
        bool stairOk = flights.Count == 2 && Math.Abs(plan.Risers - Math.Round(floor / 0.15)) < 0.5 && Math.Abs(riserMm - 150) < 8 &&
                       flights.Sum(f => f.StepCount) == plan.Risers && Math.Abs(flights.Sum(f => f.Height) - floor) < 1e-6 &&
                       Math.Abs(flights[0].TopY - landing.TopY) < 1e-6 && Math.Abs(flights[1].BaseY - landing.TopY) < 1e-6 &&
                       Math.Abs(flights[1].TopY - (baseY + floor)) < 1e-6 && Math.Abs(flights[0].Depth - flights[0].StepCount * 0.3) < 1e-6 &&
                       Math.Abs(Math.Abs(flights[1].RotationY - flights[0].RotationY) - 180) < 1e-6 && plan.StepFormula is >= 0.59 and <= 0.65 &&
                       hole is not null && stair.OpeningsCut >= 1 && hole.Width >= plan.TotalWidth - 1e-6 && hole.Depth >= plan.TotalLength - 1e-6;
        scene.AddElements(stair.Created);
        var clashes = ClashDetectionService.Detect(scene.Elements.ToList())
            .Where(c => c.Category == ClashCategory.StairsWithoutOpening && stair.Created.Any(s => s.Id == c.ElementAId)).ToList();
        Check(report, ref failures, stairOk && clashes.Count == 0,
            $"П-образная лестница: этаж {floor * 1000:0} мм → {plan.Risers} подступенков по {riserMm:0.#} мм (2h + b = {plan.StepFormula * 1000:0} мм), " +
            $"марши {plan.FirstFlightRisers} + {plan.SecondFlightRisers}, площадка на +{(landing.TopY - baseY) * 1000:0} мм, клетка {plan.TotalWidth * 1000:0} × {plan.TotalLength * 1000:0} мм, " +
            $"проёмов в перекрытии {stair.OpeningsCut}, коллизий «марш без проёма» {clashes.Count}");

        // 6. Скатная кровля: плоское покрытие 12 × 9 м → двускатная 25° (конёк = tg 25° · 4.5 м, низ на месте) и вальмовая поверх плиты.
        var roof = new ArchitecturalElement { Type = ElementTypes.Roof, RoofShape = RoofShapes.Flat, Name = "Покрытие" };
        roof.SetSize(12, 0.3, 9);
        roof.PlaceOnBase(6);
        var roofHole = new ArchitecturalElement { Type = ElementTypes.SlabOpening, HostElementId = roof.Id };
        var gable = ParametricToolsService.MakePitchedRoof([roof, roofHole], roof, RoofShapes.Gable, 25);
        var topSlab = new ArchitecturalElement { Type = ElementTypes.Slab };
        topSlab.SetSize(10, 0.2, 8);
        topSlab.PlaceOnBase(3);
        var hip = ParametricToolsService.MakePitchedRoof([topSlab], topSlab, RoofShapes.Hip, 30);
        double ridge = Math.Tan(25 * Math.PI / 180) * 4.5;
        bool roofOk = gable is { Created: false, Shape: RoofShapes.Gable } && Math.Abs(roof.Height - Math.Round(ridge, 3)) < 1e-9 && Math.Abs(roof.BaseY - 6) < 1e-9 &&
                      roof.RoofShape == RoofShapes.Gable && gable.Removed.Count == 1 &&
                      hip is { Created: true, Roof.RoofShape: RoofShapes.Hip } && Math.Abs(hip.Roof.BaseY - topSlab.TopY) < 1e-9 &&
                      Math.Abs(hip.RidgeHeight - Math.Round(Math.Tan(30 * Math.PI / 180) * 4, 3)) < 1e-9;
        Check(report, ref failures, roofOk,
            $"скатная кровля: покрытие 12 × 9 м → двускатная 25°, конёк {roof.Height * 1000:0} мм (tg 25° × 4 500), низ +6,000, отверстие плоского покрытия снято; " +
            $"плита 10 × 8 м → вальмовая 30° поверх плиты, конёк {hip?.RidgeHeight * 1000:0} мм");
        return failures;
    }
}
#endif
