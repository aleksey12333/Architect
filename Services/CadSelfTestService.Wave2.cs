#if DEBUG
using System.Numerics;
using System.Text;
using AIArchitectStudio.Models;
using AIArchitectStudio.Models.Analytical;
using AIArchitectStudio.Models.Geometry;
using AIArchitectStudio.Rendering;
using AIArchitectStudio.ViewModels;

namespace AIArchitectStudio.Services;

/// <summary>
/// Автотест волны 2 продуктового ревью: долги волны 1 (полые кровли по Г-образному Polygon2D, Round-Trip парапета и полой кровли в IFC,
/// логика проёмов при вводе координат в Инспекторе), логический аудит КЭ-расчёта (баланс ΣR_y = ΣF, неразрывность, монотонность,
/// σ ↔ K_исп, пути до грунта), напряжения σ / τ в МПа, закрепление концов μ и поперечная арматура продавливания, режим «Конструкции»,
/// свойства Renga (V_нетто, S_опал, R0, Qto_* в IFC, марки), мастер размеров (шаги осей и высоты этажей одним шагом Undo, пакетная
/// правка) и трёхрядные размерные цепочки листов ГОСТ с автопроверкой.
/// </summary>
public static partial class CadSelfTestService
{
    private static async Task<int> CheckWave2Async(string directory, StringBuilder report)
    {
        int failures = 0;
        failures += CheckPolygonRoofs(report);
        failures += await CheckRoofIfcRoundTripAsync(directory, report);
        failures += CheckInspectorGuard(report);
        failures += CheckFeaAudit(report);
        failures += CheckFixityAndPunching(report);
        failures += CheckStructuralMode(report);
        failures += await CheckRengaPropertiesAsync(directory, report);
        failures += CheckBatchDimensions(report);
        failures += await CheckGostDimensionsAsync(directory, report);
        return failures;
    }

    private static ArchitecturalElement W2(string type, string name, double x, double baseY, double z, double w, double h, double d, double rot = 0, string material = "concrete", string group = "Волна 2")
    {
        var e = new ArchitecturalElement { Type = type, Name = name, BuildingGroupId = group, Material = material };
        e.SetSize(w, h, d);
        e.SetPosition(x, baseY + h / 2, z);
        e.SetRotationY(rot);
        return e;
    }

    /// <summary>Г-образный контур 12 × 10 м с вырезом 6 × 5 м (6 вершин, против часовой в локальных X, Z).</summary>
    private static List<Point2D> LShape() => [new(-6, -5), new(6, -5), new(6, 0), new(0, 0), new(0, 5), new(-6, 5)];

    private static double PolyArea(IReadOnlyList<(double X, double Z)> p) => Geometry2D.Area(p);

    // ═════════════════════════════ Долг 1: полые кровли по Polygon2D ═════════════════════════════

    private static int CheckPolygonRoofs(StringBuilder report)
    {
        int failures = 0;
        var lines = new List<string>();
        bool all = true;
        var contour = LShape();
        double plan = PolyArea(contour.Select(p => (p.X, p.Z)).ToList());
        foreach (string shape in new[] { RoofShapes.GableHollow, RoofShapes.HipHollow, RoofShapes.ShedHollow })
        {
            var roof = W2(ElementTypes.Roof, "Кровля Г", 0, 6, 0, 12, 2.6, 10, 0, "metal_roof");
            roof.RoofShape = shape;
            roof.Polygon2D = contour;
            RoofDesign.Write(roof, 0.25, 0.45);
            var g = ElementGeometryBuilder.Build(roof, [], AllLevels);
            var info = ElementGeometryBuilder.DescribeHollowRoof(roof);
            var eaves = ElementGeometryBuilder.OffsetOutline(MeshAccumulator.EnsureCounterClockwise(contour.Select(p => new Vector2((float)p.X, (float)p.Z)).ToList()), 0.45f);
            double eavesArea = PolyArea(eaves.Select(v => ((double)v.X, (double)v.Y)).ToList());
            // Все вершины сетки — внутри контура карниза (а не по описанному прямоугольнику: в вырезе Г ничего нет).
            bool outsideNotch = g.Parts.All(part => part.Mesh.Positions.All(v => !(v.X > 0.5f && v.Z > 0.5f)));
            double plaster = PartsVolume(g, k => k == MaterialKind.Plaster);
            bool frontons = shape == RoofShapes.HipHollow ? plaster < 1e-6 : plaster > 0.3;
            double target = info is null ? 0 : info.Thickness * info.SlopeArea;
            bool ok = info is not null && Math.Abs(info.ShellVolume - target) / Math.Max(1e-9, target) < 0.01 && info.ShellVolume < 0.45 * info.SolidPrismVolume &&
                      outsideNotch && frontons && Math.Abs(info.SlopeArea / Math.Sqrt(1 + info.Slope * info.Slope) - eavesArea) < 1e-2;
            all &= ok;
            lines.Add($"{RoofGeneratorService.ShapeTitle(shape)}: оболочка {info?.ShellVolume:0.##} м³ ≈ t·S {target:0.##}, план карниза {eavesArea:0.##} м² (контур {plan:0} м² + свес), " +
                      $"фронтоны {plaster:0.##} м³, вырез Г пуст {(outsideNotch ? "да" : "НЕТ")}{(ok ? string.Empty : " — НЕ ПРОШЛО")}");
        }
        // Плоская с парапетом по Г-контуру: кольцо парапета строго по 6 рёбрам, без нахлёста и щели во входящем углу.
        var flat = W2(ElementTypes.Roof, "Покрытие Г", 0, 6, 0, 12, 0.25, 10);
        flat.RoofShape = RoofShapes.FlatParapet;
        flat.Polygon2D = contour;
        RoofDesign.Write(flat, 0.25, 0.45, 0.9);
        var fg = ElementGeometryBuilder.Build(flat, [], AllLevels);
        var (_, inner) = ElementGeometryBuilder.ParapetRoofOutlines(flat);
        double ring = plan - PolyArea(inner.Select(v => ((double)v.X, (double)v.Y)).ToList());
        double parapet = PartsVolume(fg, k => k == MaterialKind.Concrete);
        bool flatOk = Math.Abs(parapet - ring * 0.9) < 1e-3 && Math.Abs(ring - (2 * (12 + 10) * 0.2 - (5 - 1) * 0.04)) < 1e-3;
        lines.Add($"с парапетом: кольцо {ring:0.###} м² × 0,9 м = {parapet:0.###} м³ (P·t − (5 выпуклых − 1 входящий)·t², без нахлёста и щели){(flatOk ? string.Empty : " — НЕ ПРОШЛО")}");
        Check(report, ref failures, all && flatOk, "полые кровли по Г-образному Polygon2D (6 вершин): " + string.Join("; ", lines));
        return failures;
    }

    // ═════════════════════════════ Долг 2: IFC Round-Trip парапета и полой кровли ═════════════════════════════

    private static async Task<int> CheckRoofIfcRoundTripAsync(string directory, StringBuilder report)
    {
        int failures = 0;
        var parapet = W2(ElementTypes.Roof, "Покрытие с парапетом", 0, 3, 0, 12, 0.25, 10);
        parapet.RoofShape = RoofShapes.FlatParapet;
        parapet.Polygon2D = LShape();
        RoofDesign.Write(parapet, 0.25, 0.45, 1.1);
        var gable = W2(ElementTypes.Roof, "Двускатная Г", 30, 3, 0, 12, 2.6, 10, 0, "metal_roof");
        gable.RoofShape = RoofShapes.GableHollow;
        gable.Polygon2D = LShape();
        RoofDesign.Write(gable, 0.3, 0.6);
        var elements = new List<ArchitecturalElement> { parapet, gable };
        string path = Path.Combine(directory, "wave2_roofs.ifc");
        await new CadExportService().ExportAsync(CadExportFormat.Ifc, path, elements.Select(e => e.Clone()).ToList(), [new BuildingGroup("Волна 2")], "Волна 2 · кровли");
        string text = await File.ReadAllTextAsync(path);
        bool ringProfile = text.Contains("IFCARBITRARYPROFILEDEFWITHVOIDS(.AREA.,'\\X2\\", StringComparison.Ordinal) || text.Contains("IFCARBITRARYPROFILEDEFWITHVOIDS(.AREA.,'", StringComparison.Ordinal);
        var imported = await new CadIfcImporterService().ImportAsync(path);
        var ip = imported.Elements.FirstOrDefault(e => e.Id == parapet.Id);
        var ig = imported.Elements.FirstOrDefault(e => e.Id == gable.Id);
        bool Same(ArchitecturalElement? a, ArchitecturalElement b) => a is not null && a.RoofShape == b.RoofShape && a.Polygon2D.Count == b.Polygon2D.Count &&
            a.Polygon2D.Zip(b.Polygon2D).All(p => Math.Abs(p.First.X - p.Second.X) < 1e-3 && Math.Abs(p.First.Z - p.Second.Z) < 1e-3) &&
            Math.Abs(a.X - b.X) < 1e-3 && Math.Abs(a.Z - b.Z) < 1e-3 && Math.Abs(a.BaseY - b.BaseY) < 1e-3 && Math.Abs(a.Height - b.Height) < 1e-3 &&
            Math.Abs(RoofDesign.SlabThickness(a) - RoofDesign.SlabThickness(b)) < 1e-9 && Math.Abs(RoofDesign.Overhang(a) - RoofDesign.Overhang(b)) < 1e-9 &&
            Math.Abs(RoofDesign.ParapetHeight(a) - RoofDesign.ParapetHeight(b)) < 1e-9;
        double vBefore = PartsVolume(ElementGeometryBuilder.Build(parapet, [], AllLevels)), vAfter = ip is null ? 0 : PartsVolume(ElementGeometryBuilder.Build(ip, [], AllLevels));
        double gBefore = PartsVolume(ElementGeometryBuilder.Build(gable, [], AllLevels)), gAfter = ig is null ? 0 : PartsVolume(ElementGeometryBuilder.Build(ig, [], AllLevels));
        Check(report, ref failures, ringProfile && Same(ip, parapet) && Same(ig, gable) && Math.Abs(vBefore - vAfter) < 1e-3 && Math.Abs(gBefore - gAfter) < 1e-2,
            $"IFC Round-Trip: плоская с парапетом — плита + кольцо парапета вторым телом IfcExtrudedAreaSolid (IfcArbitraryProfileDefWithVoids), " +
            $"полая двускатная по Г-контуру — поверхность + Polygon2D и точка вставки; после импорта форма, контур, отметка, свес, толщина ската и " +
            $"высота парапета 1 100 мм сохранены, объём {vBefore:0.###} ≡ {vAfter:0.###} м³ и {gBefore:0.##} ≡ {gAfter:0.##} м³");
        return failures;
    }

    // ═════════════════════════════ Долг 3: логика проёмов при вводе в Инспекторе ═════════════════════════════

    private static int CheckInspectorGuard(StringBuilder report)
    {
        int failures = 0;
        if (App.MainWindowInstance is not MainWindow window)
        {
            Check(report, ref failures, false, "главное окно недоступно для проверки Инспектора");
            return failures;
        }
        var vm = window.ViewModel;
        var wall = W2(ElementTypes.Wall, "Стена Инспектора", 0, 0, 0, 8, 3, 0.3, 0, "brick");
        var column = W2(ElementTypes.Column, "Колонна в стене", 1.0, 0, 0, 0.4, 3, 0.4);
        var door = W2(ElementTypes.Door, "Дверь Д-И", -2.5, 0, 0, 0.9, 2.1, 0.36, 0, "wood");
        door.HostElementId = wall.Id;
        var pier = W2(ElementTypes.Wall, "Простенок", 0, 0, 12, 2.0, 3, 0.3, 0, "brick");
        var ca = W2(ElementTypes.Column, "К-лев", -0.6, 0, 12, 0.4, 3, 0.4);
        var cb = W2(ElementTypes.Column, "К-прав", 0.6, 0, 12, 0.4, 3, 0.4);
        var tight = W2(ElementTypes.Window, "Окно в простенке", 0, 0.9, 12, 0.5, 1.2, 0.36, 0, "glass");
        tight.HostElementId = pier.Id;
        vm.Scene.LoadScene([wall, column, door, pier, ca, cb, tight], [new BuildingGroup("Волна 2")], recordUndo: false, clearHistory: true, axes: [], dimensions: []);
        var liveDoor = vm.Scene.Elements.First(e => e.Name == "Дверь Д-И");
        vm.SelectElement(liveDoor);
        int depth = vm.Scene.UndoDepth;
        vm.PosX = 1.0; // в координаты колонны
        double gap = Math.Abs(liveDoor.X - 1.0) - liveDoor.Width / 2 - 0.2;
        bool shifted = vm.LastInspectorGuard is { Success: true } && gap >= BuildingLogicGuard.Clearance - 1e-9 && vm.Scene.UndoDepth == depth + 1 &&
                       liveDoor.HostElementId == wall.Id && Math.Abs(vm.PosX - Math.Round(liveDoor.X, 3)) < 1e-9;
        double xAfterShift = liveDoor.X;
        vm.PosZ = 2.5; // за пределы стены поперёк
        bool aligned = Math.Abs(liveDoor.Z) < 1e-9 && liveDoor.HostElementId == wall.Id && Math.Abs(liveDoor.X - xAfterShift) < 1e-9;
        vm.PosX = 20; // за торец стены
        bool clamped = liveDoor.X + liveDoor.Width / 2 <= wall.Width / 2 - BuildingLogicGuard.Clearance + 1e-9;

        var liveTight = vm.Scene.Elements.First(e => e.Name == "Окно в простенке");
        vm.SelectElement(liveTight);
        var before = (liveTight.X, liveTight.Width);
        int depthTight = vm.Scene.UndoDepth;
        vm.SizeW = 1.4; // шире простенка между колоннами — отказ и откат ввода
        bool rolledBack = vm.LastInspectorGuard is { Success: false } && (liveTight.X, liveTight.Width) == before && vm.Scene.UndoDepth == depthTight &&
                          Math.Abs(vm.SizeW - Math.Round(before.Width, 3)) < 1e-9;
        Check(report, ref failures, shifted && aligned && clamped && rolledBack,
            $"Инспектор → логика проёмов: X двери в колонну — сдвиг в простенок (зазор {gap * 1000:0} мм ≥ 150, тот же шаг журнала, поле X обновлено); " +
            $"Z за пределы стены — проём возвращён на ось стены; X за торец — до отступа 150 мм; ширина окна 1 400 мм в простенке между колоннами — " +
            $"ввод откатан («{vm.LastInspectorGuard?.Message}»)");
        return failures;
    }

    // ═════════════════════════════ Задача 1: аудит КЭ-расчёта ═════════════════════════════

    private static int CheckFeaAudit(StringBuilder report)
    {
        int failures = 0;
        var (elements, groups, _) = CreateFrameScene();
        var loads = new StructuralLoadSettings();
        var audit = FeaLogicalAuditService.Audit(elements, groups, loads);
        string criteria = string.Join("; ", audit.Criteria.Select(c => $"{c.Number}) {(c.Passed ? "да" : "НЕТ")} — {c.Detail}" + (c.Offenders.Count > 0 ? ": " + string.Join(", ", c.Offenders.Take(2)) : string.Empty)));
        Check(report, ref failures, audit.Passed && Math.Abs(audit.ImbalancePercent) <= 0.1 && audit.LostLoad < 1e-6 && audit.Stresses.Count > 10,
            $"аудит расчётной схемы каркаса: ΣF_верт = {audit.AppliedLoad:0.#} кН, ΣR_y = {audit.GroundReaction:0.#} кН (невязка {audit.ImbalancePercent:0.0000} %), " +
            $"строк напряжений {audit.Stresses.Count}, узлов продавливания {audit.Punching.Count}, {audit.ElapsedMs:0} мс; {criteria}");

        // Баланс на всех пресетах (информация о потерянной нагрузке — висящие элементы демонстрационных сцен).
        var lines = new List<string>();
        bool balanced = true;
        foreach (string id in new[] { PresetScenesService.Cottage, PresetScenesService.Frame, PresetScenesService.Tower, PresetScenesService.Pavilion })
        {
            var scene = PresetScenesService.Create(id);
            var a = FeaLogicalAuditService.Audit(scene.Elements, scene.Groups, loads, monotonicity: false);
            double residual = a.AppliedLoad - a.GroundReaction - a.LostLoad;
            balanced &= Math.Abs(residual) <= FeaLogicalAuditService.BalanceTolerance * a.AppliedLoad && a.Criteria[1].Passed && a.Criteria[3].Passed;
            lines.Add($"{id}: ΣF {a.AppliedLoad:N0} = ΣR {a.GroundReaction:N0} + без опоры {a.LostLoad:N0} кН (невязка {residual / Math.Max(1, a.AppliedLoad) * 100:0.000} %), " +
                      $"критерии {a.Criteria.Count(c => c.Passed)}/5");
        }
        Check(report, ref failures, balanced, "статика ΣF = ΣR_y + потерянное до 0,1 %, неразрывность и σ ↔ K_исп на 4 пресетах: " + string.Join("; ", lines));

        // Напряжения по формулам: стальная колонна σ = N/A + M/W ≡ K прочности·Ry; ж/б балка σ = 6M / (b·h²), τ = 1,5·Q / (b·h).
        var steelCol = W2(ElementTypes.Column, "Колонна стальная", 0, 0, 0, 0.3, 3.0, 0.3, 0, "steel");
        steelCol.ProfileType = ProfileTypes.Pipe;
        var cap = W2(ElementTypes.Slab, "Плита на стальной", 0, 3.0, 0, 4, 0.2, 4);
        var found = W2(ElementTypes.Foundation, "Фундамент", 0, -0.5, 0, 4, 0.5, 4);
        var model = AnalyticalModelBuilder.Build([found, steelCol, cap], [new BuildingGroup("Волна 2")], loads, parallel: false);
        var result = StructuralAnalysisService.Analyze(model);
        var row = FeaLogicalAuditService.Stresses(model, result).FirstOrDefault(r => r.ElementId == steelCol.Id);
        var r0 = result.For(steelCol.Id)!;
        var bar = (AnalyticalMember)model.Elements.First(e => e.Source.Id == steelCol.Id);
        double expect = (r0.AxialForce / bar.Section.A + r0.Moment / (bar.Section.IWeak / (bar.Section.HWeak / 2))) / 1000;
        bool steelOk = row is not null && Math.Abs(row.SigmaMax - expect) < 1e-9 && Math.Abs(row.StressRatio - r0.KStrength) < 1e-9 && row.Tau >= 0;
        var beamRow = audit.Stresses.FirstOrDefault(r => r.Kind == "Балка" && r.Material == "железобетон");
        var beamEl = beamRow is null ? null : elements.First(e => e.Id == beamRow.ElementId);
        double beamSigma = beamEl is null ? 0 : beamRow!.Mx / (beamEl.Depth * beamEl.Height * beamEl.Height / 6) / 1000;
        double beamTau = beamEl is null ? 0 : 1.5 * beamRow!.Q / (beamEl.Depth * beamEl.Height) / 1000;
        bool beamOk = beamRow is not null && Math.Abs(beamRow.SigmaMax - beamSigma) < 1e-9 && Math.Abs(beamRow.Tau - beamTau) < 1e-9;
        Check(report, ref failures, steelOk && beamOk,
            $"напряжения, МПа: стальная колонна σmax = N/A + M/W = {row?.SigmaN:0.###} + {row?.SigmaMy:0.###} = {row?.SigmaMax:0.###} при Ry = {row?.R:0}, η = {row?.StressRatio:0.####} ≡ K прочности {r0.KStrength:0.####}; " +
            $"ж/б балка «{beamEl?.Name}» σ = 6M/(b·h²) = {beamRow?.SigmaMax:0.###}, τ = 1,5·Q/(b·h) = {beamRow?.Tau:0.###} (Rbt {beamRow?.RShear:0.##}), R_сеч = {beamRow?.RSection:0.##}");
        return failures;
    }

    private static int CheckFixityAndPunching(StringBuilder report)
    {
        int failures = 0;
        var loads = new StructuralLoadSettings();
        // Закрепление: одна ж/б колонна 300 × 300, H = 4,2 м; без параметра ≡ μ = 0,7 до бита; консоль μ = 2,0 — гибкость ×2,857, K устойчивости выше.
        (StructuralAnalysisResult R, string Id) Run(EndFixity? f)
        {
            var col = W2(ElementTypes.Column, "Колонна μ", 0, 0, 0, 0.3, 4.2, 0.3);
            col.Id = "el_mu_column";
            if (f is { } fx) StructuralDesign.WriteFixity(col, fx);
            var slab = W2(ElementTypes.Slab, "Плита μ", 0, 4.2, 0, 5, 0.22, 5);
            slab.Id = "el_mu_slab";
            var found = W2(ElementTypes.Foundation, "Фундамент μ", 0, -0.6, 0, 5, 0.6, 5);
            found.Id = "el_mu_found";
            return (StructuralAnalysisService.Analyze([found, col, slab], [new BuildingGroup("Волна 2")], loads), col.Id);
        }
        var plain = Run(null);
        var fixedEnds = Run(EndFixity.Fixed);
        var cantilever = Run(EndFixity.Cantilever);
        var pinned = Run(EndFixity.Pinned);
        var a = plain.R.For(plain.Id)!;
        var f = fixedEnds.R.For(fixedEnds.Id)!;
        var c = cantilever.R.For(cantilever.Id)!;
        var p = pinned.R.For(pinned.Id)!;
        bool fixity = AnalysisFingerprint(plain.R) == AnalysisFingerprint(fixedEnds.R) && Math.Abs(c.Slenderness / a.Slenderness - 2.0 / 0.7) < 1e-9 &&
                      c.KStability > p.KStability && p.KStability > f.KStability && c.Phi < f.Phi;
        Check(report, ref failures, fixity,
            $"закрепление концов ж/б колонны 300×300, H = 4,2 м: без параметра ≡ «жёсткое защемление μ = 0,7» до бита (λ = {a.Slenderness:0.#}); " +
            $"шарнир μ = 1,0 — λ {p.Slenderness:0.#}, K уст {p.KStability:0.###}; консоль μ = 2,0 — λ {c.Slenderness:0.#} (×{c.Slenderness / a.Slenderness:0.###}), φ {f.Phi:0.###} → {c.Phi:0.###}, K уст {f.KStability:0.###} → {c.KStability:0.###}");

        // Продавливание: безбалочная плита 180 мм на колонне 400×400 с пролётами 7,2 м — K > 1; каркасы Кр снижают K по п. 8.1.48.
        (StructuralAnalysisResult R, PunchingNode? Node) Punch(bool rebar)
        {
            var list = new List<ArchitecturalElement> { W2(ElementTypes.Foundation, "Фундамент", 0, -0.6, 0, 22, 0.6, 22) };
            int i = 0;
            foreach (double x in new[] { -7.2, 0, 7.2 })
                foreach (double z in new[] { -7.2, 0, 7.2 })
                {
                    var col = W2(ElementTypes.Column, $"К{++i}", x, 0, z, 0.4, 3.0, 0.4);
                    col.Id = $"el_punch_c{i}";
                    list.Add(col);
                }
            var slab = W2(ElementTypes.Slab, "Плита 180", 0, 3.0, 0, 15.2, 0.18, 15.2);
            slab.Id = "el_punch_slab";
            if (rebar) StructuralDesign.WritePunchingRebar(slab, true);
            list.Add(slab);
            var r = StructuralAnalysisService.Analyze(list, [new BuildingGroup("Волна 2")], loads);
            return (r, r.PunchingNodes.FirstOrDefault(n => n.ColumnId == "el_punch_c5"));
        }
        var bare = Punch(false);
        var reinforced = Punch(true);
        double qsw = StructuralDesign.PunchingQsw(new ArchitecturalElement());
        bool punching = bare.Node is { } n0 && reinforced.Node is { } n1 && n0.SteelCapacity == 0 && n1.SteelCapacity > 0 &&
                        Math.Abs(n1.SteelCapacity - Math.Min(0.8 * qsw * n1.Perimeter, n0.Capacity)) < 1e-6 && Math.Abs(n1.Capacity - n0.Capacity - n1.SteelCapacity) < 1e-6 &&
                        n1.Ratio < n0.Ratio && Math.Abs(n1.Ratio - n0.Force / (n0.Capacity + n1.SteelCapacity)) < 1e-9;
        Check(report, ref failures, punching,
            $"поперечное армирование продавливания (СП 63 п. 8.1.48): центральная колонна, F = {bare.Node?.Force:0} кН, F_b,ult = {bare.Node?.Capacity:0} кН, K = {bare.Node?.Ratio:0.###}; " +
            $"с каркасами Кр Ø8 А240 шаг 100 мм q_sw = {qsw:0} кН/м: F_sw,ult = min(0,8·q_sw·u; F_b,ult) = {reinforced.Node?.SteelCapacity:0} кН, K = {reinforced.Node?.Ratio:0.###}");
        return failures;
    }

    // ═════════════════════════════ Задача 2: режим «Конструкции» ═════════════════════════════

    private static int CheckStructuralMode(StringBuilder report)
    {
        int failures = 0;
        if (App.MainWindowInstance is not MainWindow window)
        {
            Check(report, ref failures, false, "главное окно недоступно для режима «Конструкции»");
            return failures;
        }
        var vm = window.ViewModel;
        var (elements, groups, axes) = CreateFrameScene();
        vm.Scene.LoadScene(elements, groups, recordUndo: false, clearHistory: true, axes: axes, dimensions: []);
        vm.ViewMode = ViewportMode.Plan2D;
        vm.IsStructuralSegment = true;
        bool on = vm.IsStructuralMode && vm.ViewMode == ViewportMode.Perspective3D && !vm.IsPerspectiveMode && vm.StructuralModel is { Members.Count: > 0 };
        var column = vm.Scene.Elements.First(e => e.Category == BimCategory.Column);
        vm.SelectElement(column);
        var info = vm.StructuralSelection();
        var values = Enum.GetValues<StructuralFactor>().Select(f =>
        {
            vm.StructuralFactorIndex = (int)f;
            return vm.StructuralValue(column.Id);
        }).ToList();
        vm.StructuralFactorIndex = 0;
        bool beamValue = vm.StructuralValue(vm.Scene.Elements.First(e => e.Category == BimCategory.Beam).Id) is not null;
        // Закрепление из панели — один шаг журнала и пересчёт гибкости.
        int depth = vm.Scene.UndoDepth;
        vm.SetFixity(EndFixity.Cantilever);
        bool fixity = StructuralDesign.Fixity(column) == EndFixity.Cantilever && vm.Scene.UndoDepth == depth + 1 &&
                      vm.StructuralModel!.Elements.First(e => e.Source.Id == column.Id).LengthFactor == 2.0;
        vm.Scene.Undo();
        vm.IsPerspectiveMode = true;
        bool off = !vm.IsStructuralMode && vm.IsPerspectiveMode && vm.StructuralModel is null;
        Check(report, ref failures, on && info?.Stress is not null && values.Where((v, i) => i != (int)StructuralFactor.Deflection).All(v => v is not null) && beamValue && fixity && off && StructuralDesign.Fixity(column) == EndFixity.Default,
            $"режим «Конструкции»: сегмент рядом с «3D | План | Фасад» включает 3D и аналитическую схему (стержней {vm.Scene.Elements.Count(e => e.Category is BimCategory.Column or BimCategory.Beam)}), " +
            $"факторы шкалы у колонны ({string.Join(", ", values.Select(v => v?.Text))}), σ = {info?.Stress?.SigmaMax:0.##} МПа; закрепление «консоль» — 1 шаг журнала, μ = 2,0 в схеме; «3D» выключает режим");
        return failures;
    }

    // ═════════════════════════════ Задача 3: свойства Renga ═════════════════════════════

    private static async Task<int> CheckRengaPropertiesAsync(string directory, StringBuilder report)
    {
        int failures = 0;
        var wall = W2(ElementTypes.Wall, "Стена НС-1", 0, 0, 0, 6, 3, 0.3, 0, "concrete");
        var bare = RengaPropertyService.Compute(wall, [wall]);
        var window = W2(ElementTypes.Window, "Окно 1200×1500", 0.5, 0.9, 0, 1.2, 1.5, 0.36, 0, "glass");
        window.HostElementId = wall.Id;
        var cut = RengaPropertyService.Compute(wall, [wall, window]);
        double windowVolume = 1.2 * 1.5 * 0.3;
        double formwork = 2 * (6 * 3 - 1.8) + 2 * (1.2 + 1.5) * 0.3;
        bool volumes = Math.Abs(bare.NetVolume - cut.NetVolume - windowVolume) < 1e-12 && Math.Abs(cut.GrossVolume - 5.4) < 1e-12 && Math.Abs(cut.OpeningArea - 1.8) < 1e-12 &&
                       Math.Abs(cut.FormworkArea - formwork) < 1e-9 && Math.Abs(cut.MassTonnes - cut.NetVolume * 2.5) < 1e-9 && cut.RebarKg > 0 && cut.Role == ConstructiveRole.Bearing;

        // R0 по пирогу СП 50 (НС-1 библиотеки): 1/8,7 + Σ δ/λ + 1/23.
        var pie = W2(ElementTypes.Wall, "Стена с утеплением", 0, 0, 5, 6, 3, 0.54, 0, "brick");
        pie.Layers = [new MaterialLayer("Облицовочный кирпич", 120, 1800, MaterialLayerRole.Finish), new MaterialLayer("Минеральная вата", 100, 50, MaterialLayerRole.Insulation),
            new MaterialLayer("Газобетон D400", 300, 400, MaterialLayerRole.Structural), new MaterialLayer("Штукатурка", 20, 1600, MaterialLayerRole.Finish)];
        var thermal = RengaPropertyService.Compute(pie, [pie]);
        double r0 = 1 / 8.7 + 0.12 / 0.70 + 0.1 / 0.042 + 0.3 / 0.12 + 0.02 / 0.76 + 1 / 23.0;
        var slab = W2(ElementTypes.Slab, "Плита 200", 0, 3, 0, 6, 0.2, 6);
        var hole = W2(ElementTypes.SlabOpening, "Отверстие 1×1", 1, 3, 1, 1, 0.2, 1);
        hole.HostElementId = slab.Id;
        var slabProps = RengaPropertyService.Compute(slab, [slab, hole]);
        bool slabOk = Math.Abs(slabProps.NetVolume - (36 - 1) * 0.2) < 1e-9 && Math.Abs(slabProps.FormworkArea - (35 + (24 + 4) * 0.2)) < 1e-9 && slabProps.FireResistance == "REI 150";
        bool engineering = thermal.ThermalResistance is { } rr && Math.Abs(rr - r0) < 1e-9 && cut.FireResistance == "REI 150";
        Check(report, ref failures, volumes && engineering && slabOk,
            $"свойства Renga: стена 6 × 3 × 0,3 м с окном 1 200 × 1 500 — V_нетто {bare.NetVolume:0.###} → {cut.NetVolume:0.###} м³ (ровно −{windowVolume:0.###}), V_брутто {cut.GrossVolume:0.##}, " +
            $"S_проём {cut.OpeningArea:0.##} м², S_опал {cut.FormworkArea:0.###} м² (две грани + откосы), m {cut.MassTonnes:0.###} т, арматура {cut.RebarKg:0} кг, {cut.FireResistance}; " +
            $"пирог НС-1 R0 = {thermal.ThermalResistance:0.###} ≡ {r0:0.###} м²·°С/Вт; плита 200 мм с отверстием 1 × 1 — V_нетто {slabProps.NetVolume:0.##} м³, S_опал {slabProps.FormworkArea:0.##} м², {slabProps.FireResistance}");

        // Qto_* в IFC (экспорт приложения), без флага — файл прежний; марки — один шаг журнала, ведомость группируется по марке.
        var (elements, groups, axes) = CreateFrameScene();
        var export = new CadExportService();
        string withQto = Path.Combine(directory, "wave2_qto.ifc"), withoutQto = Path.Combine(directory, "wave2_noqto.ifc");
        await export.ExportAsync(CadExportFormat.Ifc, withQto, elements.Select(e => e.Clone()).ToList(), groups, "Волна 2 · Qto", axes, baseQuantities: true);
        await export.ExportAsync(CadExportFormat.Ifc, withoutQto, elements.Select(e => e.Clone()).ToList(), groups, "Волна 2 · Qto", axes);
        string qtoText = await File.ReadAllTextAsync(withQto), plainText = await File.ReadAllTextAsync(withoutQto);
        var sampleWall = elements.First(e => e.Category == BimCategory.Wall);
        var sampleProps = RengaPropertyService.Compute(sampleWall, elements);
        string netLine = $"IFCQUANTITYVOLUME('NetVolume',$,$,{Math.Round(sampleProps.NetVolume, 6).ToString("0.0#####", System.Globalization.CultureInfo.InvariantCulture)})";
        int qtoSets = qtoText.Split("IFCELEMENTQUANTITY(").Length - 1;
        bool qto = qtoText.Contains("Qto_WallBaseQuantities", StringComparison.Ordinal) && qtoText.Contains("'FormworkArea'", StringComparison.Ordinal) && qtoText.Contains(netLine, StringComparison.Ordinal) &&
                   !plainText.Contains("IFCELEMENTQUANTITY", StringComparison.Ordinal) && qtoSets > 20;

        var scene = new SceneStateService();
        scene.LoadScene(elements, groups, recordUndo: false, clearHistory: true, axes: axes, dimensions: []);
        var marks = RengaPropertyService.PlanMarks(scene.Elements.ToList());
        using (scene.BeginUndoTransaction("Назначение марок (автотест)"))
            foreach (var e in scene.Elements.Where(e => marks.ContainsKey(e.Id))) RengaPropertyService.WriteMark(e, marks[e.Id].Mark, marks[e.Id].Position);
        var grouped = QuantityScheduleService.GroupByParameter(scene.Elements.ToList(), RengaPropertyService.MarkDefinition);
        int distinct = marks.Values.Select(v => v.Mark).Distinct().Count();
        int columnsMarks = marks.Where(p => scene.FindById(p.Key)?.Category == BimCategory.Column).Select(p => p.Value.Mark).Distinct().Count();
        scene.Undo();
        bool undone = scene.Elements.All(e => RengaPropertyService.StoredMark(e) is null);
        Check(report, ref failures, qto && grouped.Groups.Count(g => !g.IsUnset) == distinct && undone && columnsMarks >= 1,
            $"Qto_* в IFC: наборов IfcElementQuantity {qtoSets} (NetVolume, GrossVolume, FormworkArea, OpeningArea, GrossFootprintArea, Perimeter; стена «{sampleWall.Name}» NetVolume = {sampleProps.NetVolume:0.###} м³), " +
            $"экспорт без флага — без Qto (эталон прежний); марки по типоразмерам — {distinct} марок одним шагом журнала, ведомость по «Марка конструкции» — {grouped.Groups.Count(g => !g.IsUnset)} строк, Undo снимает все");
        return failures;
    }

    // ═════════════════════════════ Задача 4: мастер размеров ═════════════════════════════

    /// <summary>Каркас 2 × 1 пролёта 6 м, 2 этажа по 3,0 м, явные оси 1–3 / А–Б, наружная стена с окном по оси А.</summary>
    private static (List<ArchitecturalElement> Elements, List<BuildingGroup> Groups, List<StructuralGridAxis> Axes) CreateBatchScene()
    {
        const string g = "Мастер";
        var list = new List<ArchitecturalElement>();
        ArchitecturalElement Add(ArchitecturalElement e, int level)
        {
            e.LevelIndex = level;
            list.Add(e);
            return e;
        }
        Add(W2(ElementTypes.Foundation, "Фундаментная плита", 6, -0.6, 3, 13.2, 0.6, 7.2, group: g), 0);
        for (int level = 0; level < 2; level++)
        {
            double b = level * 3.0;
            int k = 0;
            foreach (double x in new[] { 0.0, 6, 12 })
                foreach (double z in new[] { 0.0, 6 })
                    Add(W2(ElementTypes.Column, $"Колонна {level + 1}.{++k}", x, b, z, 0.4, 2.8, 0.4, group: g), level);
            foreach (double z in new[] { 0.0, 6 })
                foreach (double x in new[] { 3.0, 9 })
                    Add(W2(ElementTypes.Beam, $"Балка {level + 1}.{x}.{z}", x, b + 2.3, z, 6.0, 0.5, 0.3, group: g), level);
            var wall = Add(W2(ElementTypes.Wall, $"Стена А-{level + 1}", 6, b, 6.25, 12.4, 2.8, 0.25, 0, "brick", g), level);
            var win = Add(W2(ElementTypes.Window, $"Окно {level + 1}", 3, b + 0.9, 6.25, 1.5, 1.5, 0.31, 0, "glass", g), level);
            win.HostElementId = wall.Id;
            Add(W2(ElementTypes.Slab, $"Перекрытие {level + 1}", 6, b + 2.8, 3, 12.4, 0.2, 6.4, group: g), level);
        }
        var axes = new List<StructuralGridAxis>
        {
            new("1", StructuralGridAxis.AlongZ, 0, g), new("2", StructuralGridAxis.AlongZ, 6, g), new("3", StructuralGridAxis.AlongZ, 12, g),
            new("А", StructuralGridAxis.AlongX, 6, g), new("Б", StructuralGridAxis.AlongX, 0, g)
        };
        return (list, [new BuildingGroup(g)], axes);
    }

    private static int CheckBatchDimensions(StringBuilder report)
    {
        int failures = 0;
        var (elements, groups, axes) = CreateBatchScene();
        var scene = new SceneStateService();
        scene.LoadScene(elements, groups, recordUndo: false, clearHistory: true, axes: axes, dimensions: []);
        string original = DocumentFingerprint(scene);
        var info = BatchDimensionService.Describe(scene, "Мастер")!;
        // Шаг осей 2–3: 6000 → 7200.
        var spans = BatchDimensionService.SetSpans(scene, "Мастер", true, new Dictionary<int, double> { [1] = 7.2 });
        var axis3 = scene.GridAxes.First(a => a.Label == "3");
        var colAt3 = scene.Elements.Where(e => e.Category == BimCategory.Column && Math.Abs(e.X - 13.2) < 1e-9).ToList();
        var beam = scene.Elements.First(e => e.Name == "Балка 1.9.0");
        var slab = scene.Elements.First(e => e.Name == "Перекрытие 1");
        var win = scene.Elements.First(e => e.Name == "Окно 1");
        var after = BatchDimensionService.Describe(scene, "Мастер")!;
        bool spanOk = spans.Success && scene.UndoDepth == 1 && Math.Abs(axis3.Coordinate - 13.2) < 1e-9 && colAt3.Count == 4 && Math.Abs(beam.Width - 7.2) < 1e-9 && Math.Abs(beam.X - 9.6) < 1e-9 &&
                      Math.Abs(slab.Width - 13.6) < 1e-9 && Math.Abs(win.X - 3) < 1e-9 && Math.Abs(after.LengthX - info.LengthX - 1.2) < 1e-9 &&
                      Math.Abs(after.SpansX[1].Length - 7.2) < 1e-9;
        (double a3, double beamW, double slabW) = (axis3.Coordinate, beam.Width, slab.Width);
        scene.Undo();
        bool spanUndo = DocumentFingerprint(scene) == original;

        // Высоты этажей 3000 → 3600 (оба этажа).
        var heights = BatchDimensionService.SetStoreyHeights(scene, "Мастер", new Dictionary<int, double> { [0] = 3.6, [1] = 3.6 });
        var col1 = scene.Elements.First(e => e.Name == "Колонна 1.1");
        var col2 = scene.Elements.First(e => e.Name == "Колонна 2.1");
        var slab2 = scene.Elements.First(e => e.Name == "Перекрытие 2");
        var beam2 = scene.Elements.First(e => e.Name == "Балка 2.3.0");
        var win2 = scene.Elements.First(e => e.Name == "Окно 2");
        var storeys = BatchDimensionService.Describe(scene, "Мастер")!.Storeys;
        bool heightOk = heights.Success && scene.UndoDepth == 1 && Math.Abs(col1.Height - 3.4) < 1e-9 && Math.Abs(col2.BaseY - 3.6) < 1e-9 && Math.Abs(col2.Height - 3.4) < 1e-9 &&
                        Math.Abs(slab2.TopY - 7.2) < 1e-9 && Math.Abs(beam2.TopY - (7.0)) < 1e-9 && Math.Abs(win2.BaseY - 4.5) < 1e-9 &&
                        storeys.Count == 2 && storeys.All(s => Math.Abs(s.Height - 3.6) < 1e-9);
        (double c1H, double c2B, double s2T, double w2B) = (col1.Height, col2.BaseY, slab2.TopY, win2.BaseY);
        scene.Undo();
        bool heightUndo = DocumentFingerprint(scene) == original;
        Check(report, ref failures, spanOk && spanUndo && heightOk && heightUndo,
            $"мастер размеров: шаг осей 2–3 6 000 → 7 200 мм — ось 3 на X = {a3:0.###} м, 4 колонны переставлены, балка {beamW:0.##} м, плита {slabW:0.##} м, окно на месте, " +
            $"L_X {info.LengthX * 1000:N0} → {after.LengthX * 1000:N0} мм, 1 шаг журнала, Undo ≡ исходному; высоты этажей 3 000 → 3 600 мм — колонны {c1H:0.##} м, " +
            $"низ 2-го этажа +{c2B:0.##}, верх перекрытия 2 +{s2T:0.##}, окно 2 на +{w2B:0.##}, 1 шаг, Undo ≡ исходному");

        // Пакетная правка: колонны 1-го этажа → 500 × 500, окна → высота 1 800 с подоконником 800.
        int before = scene.UndoDepth;
        var cols = BatchDimensionService.Apply(scene, new BatchFilter(BimCategory.Column, 0, null, "Мастер"), new BatchEdit(500, 500));
        var windows = BatchDimensionService.Apply(scene, new BatchFilter(BimCategory.Window, null, null, "Мастер"), new BatchEdit(OpeningHeightMm: 1400, SillMm: 800));
        bool batch = cols.Success && cols.Changed == 6 && scene.Elements.Where(e => e.Category == BimCategory.Column && e.LevelIndex == 0).All(e => Math.Abs(e.Width - 0.5) < 1e-9 && Math.Abs(e.Depth - 0.5) < 1e-9) &&
                     scene.Elements.Where(e => e.Category == BimCategory.Column && e.LevelIndex == 1).All(e => Math.Abs(e.Width - 0.4) < 1e-9) &&
                     windows.Success && scene.Elements.Where(e => e.Category == BimCategory.Window).All(e => Math.Abs(e.Height - 1.4) < 1e-9 && Math.Abs(e.BaseY - (e.LevelIndex * 3.0 + 0.8)) < 1e-9) &&
                     scene.UndoDepth == before + 2;
        Check(report, ref failures, batch, $"пакетная правка по фильтру: «{cols.Message}» (колонны 1-го этажа 500 × 500, 2-й этаж без изменений), «{windows.Message}» (окна 1 400 мм на подоконнике 800 мм, под балками) — по шагу журнала на правку");
        return failures;
    }

    // ═════════════════════════════ Задача 5: размерные цепочки ГОСТ ═════════════════════════════

    private static async Task<int> CheckGostDimensionsAsync(string directory, StringBuilder report)
    {
        int failures = 0;
        var (elements, groups, axes) = CreateBatchScene();
        var plan = GostDrawingPdfExporter.Export(new GostDrawingRequest(GostSheetFormat.A3, GostDrawingView.FloorPlan, elements, groups, axes, "Волна 2 · размеры", "Мастер", 0, null, null),
            Path.Combine(directory, "wave2_plan_dimensions.pdf"));
        var section = GostDrawingPdfExporter.Export(new GostDrawingRequest(GostSheetFormat.A3, GostDrawingView.Section, elements, groups, axes, "Волна 2 · размеры", "Мастер", 0, null, null),
            Path.Combine(directory, "wave2_section_dimensions.pdf"));
        var album = GostDrawingPdfExporter.ExportAlbum(new GostDrawingRequest(GostSheetFormat.A3, GostDrawingView.FloorPlan, elements, groups, axes, "Волна 2 · размеры", "Мастер", 0, null, null),
            Path.Combine(directory, "wave2_album_dimensions.pdf"));
        await RenderPdfAsync(Path.Combine(directory, "wave2_plan_dimensions.pdf"), Path.Combine(directory, "wave2_plan_dimensions.png"), 3200);
        await RenderPdfAsync(Path.Combine(directory, "wave2_section_dimensions.pdf"), Path.Combine(directory, "wave2_section_dimensions.png"), 3200);
        var chains = plan.DimensionLayout?.Chains ?? [];
        var bottom = chains.Where(c => c.Side == "низ плана").ToList();
        long overall = bottom.FirstOrDefault(c => c.Row == 3)?.Total ?? 0;
        bool threeRows = bottom.Select(c => c.Row).Order().SequenceEqual([1, 2, 3]) && bottom.All(c => c.Values.Sum() == c.Total) &&
                         bottom.First(c => c.Row == 2).Values.Sum() == overall && bottom.First(c => c.Row == 2).Values.Count == 2 && bottom.First(c => c.Row == 1).Values.Count >= 3;
        var heights = section.DimensionLayout?.Chains.Where(c => c.Side == "разрез, высоты").ToList() ?? [];
        bool storeyChain = heights.Count == 2 && heights.First(c => c.Row == 2).Values.Sum() == heights.First(c => c.Row == 3).Total;
        bool passed = plan.DimensionCheck is { Passed: true, Overlaps: 0 } && section.DimensionCheck is { Passed: true, Overlaps: 0 } && album.DimensionCheck is { Passed: true, Overlaps: 0 };
        Check(report, ref failures, passed && threeRows && storeyChain,
            $"размеры по ГОСТ 2.307-2011: план — 3 ряда снизу (проёмы и простенки {string.Join(" + ", bottom.FirstOrDefault(c => c.Row == 1)?.Values ?? [])}; " +
            $"межосевые {string.Join(" + ", bottom.FirstOrDefault(c => c.Row == 2)?.Values ?? [])}; между крайними осями {overall}) — Σ звеньев = габарит каждой цепочки, Σ межосевых = общему; " +
            $"разрез — цепочка высот этажей {string.Join(" + ", heights.FirstOrDefault(c => c.Row == 2)?.Values ?? [])} мм и общая высота; " +
            $"автопроверка: план — {plan.DimensionCheck?.Summary(System.Globalization.CultureInfo.GetCultureInfo("ru-RU"))}; разрез — наложений {section.DimensionCheck?.Overlaps}; альбом — " +
            $"{album.DimensionCheck?.Chains} цепочек, наложений {album.DimensionCheck?.Overlaps}");

        // Узкие звенья: простенок 100 мм между двумя окнами — текст вразбежку / на полке без наложений.
        var (narrow, ng, na) = CreateBatchScene();
        var wallA = narrow.First(e => e.Name == "Стена А-1");
        var extra = W2(ElementTypes.Window, "Окно узкое", 4.35, 0.9, 6.25, 1.0, 1.5, 0.31, 0, "glass", "Мастер");
        extra.HostElementId = wallA.Id;
        narrow.Add(extra);
        var tight = GostDrawingPdfExporter.Export(new GostDrawingRequest(GostSheetFormat.A3, GostDrawingView.FloorPlan, narrow, ng, na, "Волна 2 · узкие звенья", "Мастер", 0, null, null, ScaleDenominator: 200),
            Path.Combine(directory, "wave2_plan_narrow.pdf"));
        Check(report, ref failures, tight.DimensionCheck is { Passed: true, Overlaps: 0, Relocated: > 0 },
            $"узкие звенья (простенок 100 мм, М 1:200): вынесено вразбежку / на полку {tight.DimensionCheck?.Relocated}, наложений текста {tight.DimensionCheck?.Overlaps}, " +
            $"цепочек {tight.DimensionCheck?.Chains}, замкнуто {tight.DimensionCheck?.ClosedChains}");
        return failures;
    }
}
#endif
