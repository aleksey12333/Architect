#if DEBUG
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using AIArchitectStudio.Models;
using AIArchitectStudio.Models.Transactions;
using AIArchitectStudio.Rendering;
using AIArchitectStudio.Services.Verification;

namespace AIArchitectStudio.Services;

/// <summary>
/// Автотест блока девелопера 3–4: библиотека типовых элементов ГОСТ/СП (каталог серий, применение типа одним шагом с Undo до
/// бита, пользовательская библиотека и обмен .aialib.json туда и обратно), двухпроходная доводка каркаса до K_исп ≤ 1,0 на
/// дефектной сцене блока 2 и ненесущая перегородка (только собственный вес), ручное моделирование (колонна в узел осей, стена,
/// врезка окна, разделение пополам с переносом проёма, дублирование, примыкание к уровню — каждая операция ровно 1 шаг),
/// прыжок по журналу транзакций с одним уведомлением DocumentChanged и контрольные точки вариантов со сравнением стоимости
/// каркаса и K_исп; справочный центр (вкладки, поиск, горячие клавиши) и чистота видимых строк.
/// </summary>
public static partial class CadSelfTestService
{
    private static async Task<int> CheckDeveloperBlock34Async(string directory, StringBuilder report)
    {
        int failures = 0;
        failures += CheckStandardLibrary(directory, report);
        failures += CheckCascadeAndPartitions(report);
        failures += CheckManualModeling(report);
        failures += CheckHistoryAndCheckpoints(report);
        failures += CheckHelpCenter(report);
        await Task.CompletedTask;
        return failures;
    }

    /// <summary>Состояние документа без выделения (переход по истории и загрузка варианта восстанавливают своё выделение).</summary>
    private static string StateFingerprint(SceneStateService s)
    {
        s.SelectedElement = null;
        return DocumentFingerprint(s);
    }

    // ═════════════════════════════ 1. Библиотека ГОСТ/СП ═════════════════════════════

    private static int CheckStandardLibrary(string directory, StringBuilder report)
    {
        int failures = 0;
        var inv = CultureInfo.InvariantCulture;
        var lib = StandardLibraryService.BuiltIn;
        int Count(StandardLibrarySection s) => lib.Entries.Count(e => e.Section == s);
        bool columns = new[] { "300x300", "400x400", "400x600", "500x500", "600x600" }.All(s => lib.Find("col-" + s) is { Category: BimCategory.Column, Standard: var st } && st.Contains("ГОСТ 25628"));
        bool beams = new[] { "300x500", "300x600", "400x600" }.All(s => lib.Find("beam-" + s) is { Standard: var st } && st.Contains("ГОСТ 18980")) &&
                     new[] { "20Б1", "30Б1", "35Ш1", "40Ш1" }.All(m => lib.Entries.Any(e => e.Mark == m && e.ProfileType == ProfileTypes.IBeam && e.Standard.Contains("ГОСТ Р 57837-2017") && e.FlangeMm > 0 && e.WebMm > 0));
        bool slabs = new[] { 180, 200, 220, 250 }.All(t => lib.Find($"slab-{t}") is { WidthMm: var w } && Math.Abs(w - t) < 1e-9) &&
                     new[] { 400, 600, 800 }.All(t => lib.Find($"fnd-{t}") is { Category: BimCategory.Foundation });
        var ext = lib.Find("wall-ext-d400");
        bool walls = ext is { Layers.Count: 4 } && Math.Abs(ext.ThicknessMm - 540) < 1e-9 && ext.Layers.Any(l => l.Name.Contains("D400") && l.ThicknessMm == 300 && l.Role == MaterialLayerRole.Structural) &&
                     ext.Layers.Any(l => l.Role == MaterialLayerRole.Insulation && l.ThicknessMm == 100) && ext.Standard.Contains("СП 50") &&
                     lib.Find("wall-mono-200") is { IsLoadBearing: true } && lib.Find("wall-mono-250") is { ThicknessMm: 250 } &&
                     lib.Find("part-gkl-100") is { IsLoadBearing: false, ThicknessMm: 100 } && lib.Find("part-brick-120") is { IsLoadBearing: false, ThicknessMm: 120 };
        Check(report, ref failures, columns && beams && slabs && walls,
            $"библиотека ГОСТ/СП: колонн {Count(StandardLibrarySection.Columns)} (ГОСТ 25628 / Серия 1.020), ригелей и двутавров {Count(StandardLibrarySection.Beams)} " +
            $"(ГОСТ 18980; 20Б1, 30Б1, 35Ш1, 40Ш1 по ГОСТ Р 57837-2017), плит {Count(StandardLibrarySection.Slabs)} (180…250 мм), фундаментных плит {Count(StandardLibrarySection.Foundations)} (400/600/800), " +
            $"стеновых пирогов {Count(StandardLibrarySection.Walls)} (СП 50: газобетон D400 300 + минвата 100 + облицовка = {ext?.ThicknessMm.ToString("0", inv)} мм), перегородок {Count(StandardLibrarySection.Partitions)} (ненесущие)");

        // Применение типа к колоннам этажа: один шаг, общий тип, низ и высота сохраняются, Undo/Redo до бита.
        var scene = LoadPreset(PresetScenesService.Frame);
        var cols = scene.Elements.Where(e => e.Category == BimCategory.Column && e.LevelIndex == 0).ToList();
        var bases = cols.Select(c => (c.BaseY, c.Height)).ToList();
        string before = StateFingerprint(scene);
        int undo0 = scene.Transactions.UndoCount;
        var entry = lib.Find("col-500x500")!;
        var applied = StandardLibraryService.Apply(scene, entry, cols);
        int steps = scene.Transactions.UndoCount - undo0;
        string stepName = scene.Transactions.UndoStack[^1].Name;
        bool sized = cols.All(c => Math.Abs(c.Width - 0.5) < 1e-12 && Math.Abs(c.Depth - 0.5) < 1e-12) &&
                     cols.Select((c, i) => Math.Abs(c.BaseY - bases[i].BaseY) < 1e-9 && Math.Abs(c.Height - bases[i].Height) < 1e-9).All(x => x) &&
                     cols.Select(c => c.TypeId).Distinct().Count() == 1 && cols.All(c => StandardTypeMark.Read(c) == "К 500×500");
        string afterApply = StateFingerprint(scene);
        scene.Undo();
        string undone = StateFingerprint(scene);
        scene.Redo();
        string redone = StateFingerprint(scene);
        // Двутавр 30Б1 к балке: профиль, b × h, толщины полки/стенки, верх сохраняется.
        var beam = scene.Elements.First(e => e.Category == BimCategory.Beam);
        double beamTop = beam.TopY;
        StandardLibraryService.Apply(scene, lib.Entries.First(e => e.Mark == "30Б1"), [beam]);
        bool ibeam = beam.ProfileType == ProfileTypes.IBeam && Math.Abs(beam.Depth - 0.14) < 1e-12 && Math.Abs(beam.Height - 0.296) < 1e-12 &&
                     Math.Abs(beam.ProfileWallThickness - 0.0085) < 1e-12 && Math.Abs(beam.ProfileWebThickness - 0.0058) < 1e-12 && Math.Abs(beam.TopY - beamTop) < 1e-9 && beam.Material == "steel";
        // Стеновой пирог наружной стены: толщина 540 мм, проёмы режут новую толщину, ось сохранена.
        var cottage = LoadPreset(PresetScenesService.Cottage);
        var wall = cottage.Elements.First(e => e.Category == BimCategory.Wall && cottage.GetHostedElements(e.Id).Any(o => ElementTypes.IsHostedType(o.Category)));
        double wx = wall.X, wz = wall.Z;
        StandardLibraryService.Apply(cottage, ext!, [wall]);
        var hosted = cottage.GetHostedElements(wall.Id).Where(o => ElementTypes.IsHostedType(o.Category)).ToList();
        bool layered = Math.Abs(wall.Depth - 0.54) < 1e-12 && wall.Layers.Count == 4 && wall.X == wx && wall.Z == wz &&
                       hosted.All(o => Math.Abs(o.Depth - (0.54 + ParametricSolverService.OpeningClearance)) < 1e-12) && wall.IsLoadBearing;
        Check(report, ref failures, applied.Applied.Count == cols.Count && steps == 1 && stepName.Contains("К 500×500") && sized && afterApply != before && undone == before && redone == afterApply && ibeam && layered,
            $"тип из библиотеки: «{entry.Caption}» → {applied.Applied.Count} колонн 1-го этажа одним шагом («{stepName}»), общий тип, низ и высота сохранены, марка в свойствах; " +
            $"Undo ≡ до бита, Redo ≡ применённому; 30Б1 → балка двутавр 140 × 296 (tf 8,5 / tw 5,8 мм), верх сохранён; пирог НС-1 → стена 540 мм, проёмов {hosted.Count} с новой глубиной выреза");

        // Пользовательская библиотека: сохранение типов, повторная загрузка с диска, экспорт и импорт .aialib.json.
        string libDir = Path.Combine(directory, "libraries");
        if (Directory.Exists(libDir)) Directory.Delete(libDir, recursive: true);
        var service = new StandardLibraryService(libDir);
        var columnEntry = service.AddToUserLibrary(StandardLibraryService.CaptureEntry(cols[0], "К-1 корп."));
        var wallEntry = service.AddToUserLibrary(StandardLibraryService.CaptureEntry(wall));
        var beamEntry = service.AddToUserLibrary(StandardLibraryService.CaptureEntry(beam));
        var reloaded = new StandardLibraryService(libDir);
        string exportPath = Path.Combine(libDir, "Корпоративные конструкции" + StandardLibrary.FileExtension);
        StandardLibraryService.Write(service.UserLibrary, exportPath);
        var importer = new StandardLibraryService(Path.Combine(libDir, "other")) { IsSessionOnly = true };
        var imported = importer.Import(exportPath);
        string Canon(StandardLibrary l) => StandardLibraryService.Serialize(new StandardLibrary { Name = "x", CreatedAt = default, Entries = l.Entries });
        bool sameReload = Canon(reloaded.UserLibrary) == Canon(service.UserLibrary);
        bool sameImport = Canon(imported) == Canon(service.UserLibrary);
        // Тип из импортированной библиотеки применяется к другой колонне — то же сечение.
        var other = scene.Elements.First(e => e.Category == BimCategory.Column && e.LevelIndex == 1);
        StandardLibraryService.Apply(scene, imported.Entries[0], [other]);
        bool roundApply = Math.Abs(other.Width - 0.5) < 1e-12 && StandardTypeMark.Read(other) == "К-1 корп.";
        string bad = Path.Combine(libDir, "bad.aialib.json");
        File.WriteAllText(bad, "{\"format\":\"other\",\"entries\":[]}");
        bool rejected = false;
        try { importer.Import(bad); }
        catch (InvalidDataException) { rejected = true; }
        bool userFile = File.Exists(Path.Combine(libDir, StandardLibraryService.UserFileName));
        Check(report, ref failures, sameReload && sameImport && roundApply && rejected && userFile && imported.Entries.Count == 3 && wallEntry.Layers.Count == 4 && beamEntry.ProfileType == ProfileTypes.IBeam,
            $"пользовательская библиотека: сохранено типов {service.UserLibrary.Entries.Count} («{columnEntry.Mark}», «{wallEntry.Mark}» {wallEntry.ThicknessMm:0} мм с 4 слоями, «{beamEntry.Mark}»), " +
            $"файл {StandardLibraryService.UserFileName} перечитан ≡ сохранённому; экспорт .aialib.json {new FileInfo(exportPath).Length / 1024.0:0.0} КБ → импорт ≡ исходной, тип импортированной библиотеки применён к колонне 2-го этажа; чужой формат отклонён");
        return failures;
    }

    // ═════════════════════════════ 2. Двухпроходная доводка и перегородки ═════════════════════════════

    private static int CheckCascadeAndPartitions(StringBuilder report)
    {
        int failures = 0;
        var inv = CultureInfo.InvariantCulture;
        var loads = new StructuralLoadSettings();
        var scene = new SceneStateService();
        scene.LoadScene([], [], recordUndo: false, clearHistory: true, axes: []);
        var (defect, groups, axes) = CreateGateDefectScene();
        scene.LoadScene(defect, groups, recordUndo: true, axes: axes, dimensions: []);
        string generator = StateFingerprint(scene);

        // Один проход (как в блоке 2): после утолщения плит колонны выше остаются перегруженными.
        var single = VerificationGateService.Inspect(scene, loads, "тест · один проход", cascade: false);
        double kSingle = VerificationGateService.Simulate(scene, single.Fixes, loads).K;
        // Двухпроходный лист: каскадные правки под новой нагрузкой, «Принять все» — K_исп всей сцены ≤ 1,0 одним шагом.
        var gate = VerificationGateService.Inspect(scene, loads, "тест · каскад");
        foreach (var f in gate.Fixes) f.IsAccepted = true;
        int undo0 = scene.Transactions.UndoCount;
        var applied = VerificationGateService.ApplyAccepted(scene, gate, loads);
        int steps = scene.Transactions.UndoCount - undo0;
        var analysis = StructuralAnalysisService.Analyze([.. scene.Elements], [.. scene.BuildingGroups], loads);
        var columnK = scene.Elements.Where(e => e.Category == BimCategory.Column).Select(e => analysis.For(e.Id)?.Utilization ?? 0).ToList();
        var slabs = scene.Elements.Where(e => e.Category is BimCategory.Slab or BimCategory.Roof).ToList();
        bool thickened = slabs.Any(s => s.Height > 0.16 + 1e-6);
        scene.Undo();
        bool undoOk = StateFingerprint(scene) == generator;
        bool cascadeOk = gate.CascadeEnabled && gate.Passes >= 2 && gate.CascadeFixCount > 0 && gate.Fixes.Where(f => f.IsCascade).All(f => f.DefectDescription.StartsWith("Проход ", StringComparison.Ordinal) && scene.FindById(f.ElementId) is not null) &&
                         kSingle > 1.0 && applied.KAfter <= 1.0 && analysis.MaxUtilization <= 1.0 && columnK.All(k => k <= 1.0) && steps == 1 && thickened && undoOk;
        Check(report, ref failures, cascadeOk,
            $"двухпроходная доводка: правок прохода 1 — {gate.Fixes.Count(f => !f.IsCascade)}, один проход оставляет K_исп {kSingle.ToString("0.00", inv)} (колонны под утолщёнными плитами); " +
            $"каскад: проходов {gate.Passes}, правок под новой нагрузкой {gate.CascadeFixCount}, K по проходам {string.Join(" → ", gate.PassUtilization.Select(k => k.ToString("0.00", inv)))}; " +
            $"«Принять все» — шагов журнала {steps}, стабилизация {applied.StabilizationActions} действ., K_исп всей сцены {analysis.MaxUtilization.ToString("0.00", inv)} (колонны max {columnK.Max().ToString("0.00", inv)}) ≤ 1,0; Undo ≡ сцене генератора");

        // Ненесущая перегородка: под центром плиты 6 × 6 м на 4 колоннах — кирпичная перегородка 120 мм.
        static (List<ArchitecturalElement> Elements, List<BuildingGroup> Groups) PartitionScene(bool? bearing, bool slab)
        {
            const string g = "Перегородка · тест";
            var list = new List<ArchitecturalElement>();
            int n = 0;
            ArchitecturalElement Add(string type, string name, double x, double baseY, double z, double w, double h, double d)
            {
                var e = new ArchitecturalElement { Id = $"el_part{++n:D3}", Type = type, Name = name, BuildingGroupId = g, Material = "concrete" };
                e.SetSize(w, h, d);
                e.SetPosition(x, baseY + h / 2, z);
                list.Add(e);
                return e;
            }
            foreach (var (x, z) in new[] { (0.0, 0.0), (6.0, 0.0), (0.0, 6.0), (6.0, 6.0) }) Add(ElementTypes.Column, $"Колонна {x}/{z}", x, 0, z, 0.4, 2.8, 0.4);
            var wall = Add(ElementTypes.Wall, "Перегородка П-1", 3, 0, 3, 5.6, 2.8, 0.12);
            StandardLibraryService.ApplyTo(wall, StandardLibraryService.BuiltIn.Find("part-brick-120")!);
            StructuralRole.Set(wall, bearing);
            if (slab) Add(ElementTypes.Slab, "Плита", 3, 2.8, 3, 6.4, 0.2, 6.4);
            return (list, [new BuildingGroup(g)]);
        }
        double N(bool? bearing, bool slab, string id, out StructuralAnalysisResult result)
        {
            var (els, grs) = PartitionScene(bearing, slab);
            result = StructuralAnalysisService.Analyze(els, grs, loads);
            return result.For(id)?.AxialForce ?? double.NaN;
        }
        double nBearing = N(true, true, "el_part005", out var rBearing);
        double nPartition = N(false, true, "el_part005", out var rPartition);
        double nSelf = N(true, false, "el_part005", out _);
        double cols(StructuralAnalysisResult r) => Enumerable.Range(1, 4).Sum(i => r.For($"el_part{i:D3}")?.AxialForce ?? 0);
        var partitionWall = PartitionScene(false, true).Elements[4];
        bool partitionOk = nBearing > nSelf * 1.5 && Math.Abs(nPartition - nSelf) <= 1e-9 * Math.Max(1, nSelf) && cols(rPartition) > cols(rBearing) + 1 &&
                           !partitionWall.IsLoadBearing && StructuralRole.IsNonBearingPartition(partitionWall) && PartitionScene(null, true).Elements[4].IsLoadBearing &&
                           !StructuralTopology.IsVerticalBearing(partitionWall);
        Check(report, ref failures, partitionOk,
            $"ненесущая перегородка (кирпич 120 мм под плитой 6 × 6 м): несущая стена N = {nBearing.ToString("0.0", inv)} кН, перегородка «Несущая конструкция = нет» N = {nPartition.ToString("0.0", inv)} кН ≡ собственному весу " +
            $"({nSelf.ToString("0.0", inv)} кН без плиты), колонны принимают {cols(rBearing).ToString("0", inv)} → {cols(rPartition).ToString("0", inv)} кН; без признака стена несущая (эталонные сцены не меняются)");
        return failures;
    }

    // ═════════════════════════════ 3. Ручное моделирование ═════════════════════════════

    private static int CheckManualModeling(StringBuilder report)
    {
        int failures = 0;
        var inv = CultureInfo.InvariantCulture;
        var scene = LoadPreset(PresetScenesService.Frame);
        scene.ActiveLevelFilter = 0;
        var stepCounts = new List<int>();
        int Step(Func<ManualEditResult> op, out ManualEditResult r)
        {
            int u = scene.Transactions.UndoCount;
            r = op();
            int d = scene.Transactions.UndoCount - u;
            stepCounts.Add(d);
            return d;
        }

        // Колонна — в свободный узел осей активного этажа (узел у центра освобождён), тип К 400×400 по ГОСТ 25628.
        var removedNode = scene.Elements.Where(e => e.Category == BimCategory.Column && e.LevelIndex == 0).OrderBy(e => e.X * e.X + e.Z * e.Z).First();
        scene.RemoveElement(removedNode, recordUndo: false);
        var columnsBefore = scene.Elements.Where(e => e.Category == BimCategory.Column && e.LevelIndex == 0).ToList();
        Step(() => ManualModelingService.AddElement(scene, ManualElementKind.Column), out var colResult);
        var column = colResult.Primary!;
        var axes = scene.AxesOf(column.BuildingGroupId).ToList();
        bool onNode = axes.Any(a => a.IsNumeric && Math.Abs(a.Coordinate - column.X) < 1e-9) && axes.Any(a => !a.IsNumeric && Math.Abs(a.Coordinate - column.Z) < 1e-9);
        bool free = columnsBefore.All(c => Math.Abs(c.X - column.X) > 0.25 || Math.Abs(c.Z - column.Z) > 0.25);
        double elevation = StructuralAxesBuilder.LevelElevations(scene.Elements.Where(e => e.BuildingGroupId == column.BuildingGroupId).ToList()).First(m => m.Level == 0).Elevation;
        bool columnOk = onNode && free && Math.Abs(column.X - removedNode.X) < 1e-9 && Math.Abs(column.Z - removedNode.Z) < 1e-9 &&Math.Abs(column.Width - 0.4) < 1e-12 && StandardTypeMark.Read(column) == "К 400×400" && Math.Abs(column.BaseY - elevation) < 1e-9 && column.LevelIndex == 0;

        // Стена и врезка окна в выделенную стену.
        Step(() => ManualModelingService.AddElement(scene, ManualElementKind.Wall), out var wallResult);
        var wall = wallResult.Primary!;
        scene.SelectedElement = wall;
        Step(() => ManualModelingService.AddElement(scene, ManualElementKind.Window), out var winResult);
        var window = winResult.Primary!;
        var (lx, _) = ArchitecturalElement.RotateY(window.X - wall.X, window.Z - wall.Z, -wall.RotationY);
        bool windowOk = window.HostElementId == wall.Id && Math.Abs(lx) + window.Width / 2 <= wall.Width / 2 + 1e-9 && window.BaseY >= wall.BaseY - 1e-9 && window.TopY <= wall.TopY + 1e-9 &&
                        Math.Abs(window.Depth - (wall.Depth + ParametricSolverService.OpeningClearance)) < 1e-12;
        // Окно без выделенной стены — отказ без шага.
        scene.SelectedElement = column;
        int noWall = Step(() => ManualModelingService.AddElement(scene, ManualElementKind.Door), out var doorFail);
        stepCounts.RemoveAt(stepCounts.Count - 1);

        // Разделение стены пополам: проём в середине — точка сдвигается к краю проёма, окно остаётся в своей части.
        var (a0, b0) = Ends(wall);
        double w0 = wall.Width;
        string beforeSplit = StateFingerprint(scene);
        Step(() => ManualModelingService.Split(scene, wall), out var splitResult);
        var second = splitResult.Created[0];
        var (a1, b1) = Ends(wall);
        var (a2, b2) = Ends(second);
        bool joined = Near(a1, a0) && Near(b2, b0) && Near(b1, a2) && Math.Abs(wall.Width + second.Width - w0) < 1e-9 && second.TypeId == wall.TypeId && second.Id != wall.Id;
        var host = scene.FindById(window.HostElementId)!;
        var (hx, _) = ArchitecturalElement.RotateY(window.X - host.X, window.Z - host.Z, -host.RotationY);
        bool windowFollows = (host == wall || host == second) && Math.Abs(hx) + window.Width / 2 <= host.Width / 2 + 1e-9;
        scene.Undo();
        bool splitUndo = StateFingerprint(scene) == beforeSplit;
        scene.Redo();

        // Дублирование стены с окном вдоль оси: копии со своим проёмом.
        Step(() => ManualModelingService.Duplicate(scene, [second], ManualModelingService.DefaultDuplicateOffset(second).Dx, ManualModelingService.DefaultDuplicateOffset(second).Dz), out var dupResult);
        var copyWall = dupResult.Created.FirstOrDefault(e => e.Category == BimCategory.Wall);
        bool dupOk = copyWall is not null && dupResult.Created.Where(e => e.HostElementId is not null).All(o => dupResult.Created.Any(c => c.Id == o.HostElementId)) &&
                     Math.Abs(copyWall.Width - second.Width) < 1e-12;

        // Дублирование на этаж выше и примыкание к уровню.
        Step(() => ManualModelingService.Duplicate(scene, [column], 0, 0, floorUp: true), out var upResult);
        var upper = upResult.Created[0];
        scene.SetTransform(column, column.X, column.Y + 0.35, column.Z, column.Width, column.Height - 0.2, column.Depth, column.RotationY, column.LevelIndex);
        Step(() => ManualModelingService.SnapToLevel(scene, [column]), out var snapResult);
        var ceiling = ManualModelingService.CeilingAbove(scene, column.BuildingGroupId, elevation, column.X, column.Z, column);
        bool snapOk = Math.Abs(column.BaseY - elevation) < 1e-9 && (ceiling is null || Math.Abs(column.TopY - ceiling.BaseY) < 1e-9) && upper.LevelIndex == 1 && upper.BaseY > column.TopY - 1e-6;

        bool oneStep = stepCounts.All(d => d == 1) && noWall == 0 && !doorFail.Success;
        Check(report, ref failures, columnOk && windowOk && joined && windowFollows && splitUndo && dupOk && snapOk && oneStep,
            $"ручное моделирование: колонна «{column.Name}» в свободном узле осей ({colResult.Message.Split(',').Skip(1).FirstOrDefault()?.Trim()}), К 400×400, низ {elevation.ToString("0.000", inv)}; стена {wall.Width * 1000 + second.Width * 1000:0} мм + окно " +
            $"{window.Width * 1000:0} × {window.Height * 1000:0} в стене-хосте; дверь без выделенной стены — отказ без шага; разделение пополам {wall.Width * 1000:0} + {second.Width * 1000:0} мм (торцы и стык совпадают, тип общий, окно в своей части), Undo ≡ до бита; " +
            $"дублирование со смещением — копия с проёмом ({dupResult.Created.Count} эл.), на этаж выше; примыкание к уровню — низ на отметке пола, верх под перекрытием; " +
            $"операций {stepCounts.Count}, каждая — ровно 1 шаг журнала");
        return failures;

        // Торцы оси линейного элемента (начало — локальная −X, конец — +X).
        static ((double X, double Z) A, (double X, double Z) B) Ends(ArchitecturalElement e)
        {
            var (dx, dz) = ArchitecturalElement.RotateY(e.Width / 2, 0, e.RotationY);
            return ((e.X - dx, e.Z - dz), (e.X + dx, e.Z + dz));
        }

        static bool Near((double X, double Z) p, (double X, double Z) q) => Math.Abs(p.X - q.X) < 1e-9 && Math.Abs(p.Z - q.Z) < 1e-9;
    }

    // ═════════════════════════════ 4. История и контрольные точки ═════════════════════════════

    private static int CheckHistoryAndCheckpoints(StringBuilder report)
    {
        int failures = 0;
        var inv = CultureInfo.InvariantCulture;
        var loads = new StructuralLoadSettings();
        var scene = LoadPreset(PresetScenesService.Frame);
        scene.ActiveLevelFilter = 0;
        var fp = new List<string> { StateFingerprint(scene) };
        var cpA = default(ProjectCheckpoint);
        var lib = StandardLibraryService.BuiltIn;
        var ops = new List<Func<string>>
        {
            () => ManualModelingService.AddElement(scene, ManualElementKind.Column).StepName,
            () => StandardLibraryService.Apply(scene, lib.Find("col-600x600")!, scene.Elements.Where(e => e.Category == BimCategory.Column && e.LevelIndex == 0).ToList()).StepName,
            () => ManualModelingService.AddElement(scene, ManualElementKind.Beam).StepName,
            () => ManualModelingService.Split(scene, scene.Elements.Last(e => e.Category == BimCategory.Beam)).StepName,
            () => StandardLibraryService.Apply(scene, lib.Find("slab-250")!, scene.Elements.Where(e => e.Category == BimCategory.Slab).ToList()).StepName
        };
        var names = new List<string>();
        for (int i = 0; i < ops.Count; i++)
        {
            names.Add(ops[i]());
            fp.Add(StateFingerprint(scene));
            if (i == 1) cpA = ProjectCheckpointService.Capture(scene, "Вариант A · колонны 600 × 600", loads);
        }
        var cpB = ProjectCheckpointService.Capture(scene, "Вариант B · плиты 250 мм", loads);
        bool fiveSteps = scene.Transactions.UndoCount == 5 && scene.Transactions.UndoStack.Select(t => t.Name).SequenceEqual(names) &&
                         scene.Transactions.UndoStack.All(t => t.AffectedElementCount > 0 && t.CreatedAt <= DateTime.Now);

        int events = 0;
        scene.DocumentChanged += (_, _) => events++;
        int back = scene.JumpToHistory(2);
        int eventsBack = events;
        bool atTwo = StateFingerprint(scene) == fp[2] && scene.Transactions.UndoCount == 2 && scene.Transactions.RedoCount == 3;
        events = 0;
        int forward = scene.JumpToHistory(5);
        bool atFive = StateFingerprint(scene) == fp[5] && events == 1;
        events = 0;
        scene.JumpToHistory(0);
        bool atZero = StateFingerprint(scene) == fp[0] && events == 1;
        scene.JumpToHistory(5);

        // Контрольные точки: переход к варианту A — один шаг, состояние ≡ шагу 2; Undo — снова вариант B.
        int undo0 = scene.Transactions.UndoCount;
        ProjectCheckpointService.Restore(scene, cpA!);
        bool restoreA = scene.Transactions.UndoCount == undo0 + 1 && StateFingerprint(scene) == fp[2] && scene.Transactions.UndoStack[^1].Name == "Вариант: " + cpA!.Name;
        scene.Undo();
        bool backToB = StateFingerprint(scene) == fp[5];
        var cmp = ProjectCheckpointService.Compare(cpB, cpA.FrameCostRub, cpA.MaxUtilization, cpA.ConcreteM3, cpA.ElementCount);
        var (costNow, kNow, _, concreteNow, _) = ProjectCheckpointService.Metrics([.. scene.Elements], [.. scene.BuildingGroups], loads, GateCostRates.Default);
        bool metrics = cpA.FrameCostRub > 0 && cpB.FrameCostRub > cpA.FrameCostRub && Math.Abs(cmp.CostDeltaRub - (cpB.FrameCostRub - cpA.FrameCostRub)) < 1e-6 &&
                       cpB.ConcreteM3 > cpA.ConcreteM3 && Math.Abs(costNow - cpB.FrameCostRub) < 1e-6 && Math.Abs(kNow - cpB.MaxUtilization) < 1e-12 && cpA.MaxUtilization > 0;
        Check(report, ref failures, fiveSteps && back == -3 && eventsBack == 1 && atTwo && forward == 3 && atFive && atZero && restoreA && backToB && metrics,
            $"журнал и версии: 5 операций — 5 именованных шагов («{names[0]}», …, «{names[4]}»); прыжок 5 → 2 (3 Undo) и 2 → 5 (3 Redo) — одно уведомление DocumentChanged, " +
            $"состояние ≡ шагу до бита, 5 → 0 ≡ исходному; контрольные точки: «{cpA.Name}» {cpA.FrameCostRub.ToString("N0", inv)} ₽, K_исп {cpA.MaxUtilization.ToString("0.00", inv)} · " +
            $"«{cpB.Name}» {cpB.FrameCostRub.ToString("N0", inv)} ₽, K_исп {cpB.MaxUtilization.ToString("0.00", inv)} (Δ {cmp.CostDeltaRub.ToString("+0;-0", inv)} ₽, бетон +{(cpB.ConcreteM3 - cpA.ConcreteM3).ToString("0.0", inv)} м³); " +
            "переход к варианту A — 1 шаг, ≡ шагу 2; Undo ≡ варианту B");
        return failures;
    }

    // ═════════════════════════════ 5. Справочный центр и чистота строк ═════════════════════════════

    private static int CheckHelpCenter(StringBuilder report)
    {
        int failures = 0;
        bool tabs = Enum.GetValues<HelpTab>().Where(t => t != HelpTab.Keys).All(t => HelpCenterContent.Topics.Any(x => x.Tab == t)) && HelpCenterContent.Workflow.Count == 5 &&
                    HelpCenterContent.HotKeys.Count >= 15;
        bool keys = new[] { "Ctrl + Z", "Ctrl + Y", "Delete", "M", "F1" }.All(k => HelpCenterContent.HotKeys.Any(h => h.Keys == k));
        bool norms = new[] { "СП 63.13330.2018", "СП 20.13330.2016", "СП 16.13330.2017", "СП 50.13330", "ГОСТ 27751-2014", "СанПиН 1.2.3685-21" }
            .All(n => HelpCenterContent.Topics.Any(t => t.Tab == HelpTab.Methods && (t.Title + t.Body + t.Reference).Contains(n)));
        int punching = HelpCenterContent.Search("продавливание").Count();
        int csv = HelpCenterContent.Search("csv поставщик").Count();
        int none = HelpCenterContent.Search("несуществующее слово xyz").Count();

        // Видимые строки нового интерфейса: без эмодзи и слов «ИИ / AI / LLM».
        var texts = new List<string>();
        texts.AddRange(HelpCenterContent.Topics.SelectMany(t => new[] { t.Title, t.Body, t.Reference }));
        texts.AddRange(HelpCenterContent.Workflow.SelectMany(s => new[] { s.Title, s.Body, s.Where }));
        texts.AddRange(HelpCenterContent.HotKeys.SelectMany(k => new[] { k.Keys, k.Action, k.Context }));
        texts.AddRange(StandardLibraryService.BuiltIn.Entries.SelectMany(e => new[] { e.Mark, e.Name, e.Standard, e.Notes, e.MaterialGrade }));
        if (typeof(LocalizationService).GetField("LibraryStrings", BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null) is Dictionary<string, (string Ru, string En)> strings)
            texts.AddRange(strings.Values.SelectMany(v => new[] { v.Ru, v.En }));
        var banned = new Regex(@"(?<![\p{L}])(ИИ|AI|LLM)(?![\p{L}])", RegexOptions.CultureInvariant);
        var dirty = texts.Where(t => banned.IsMatch(t) || t.EnumerateRunes().Any(r => r.Value >= 0x1F300 && r.Value <= 0x1FAFF || r.Value is >= 0x2600 and <= 0x27BF)).ToList();
        Check(report, ref failures, tabs && keys && norms && punching >= 1 && csv >= 1 && none == 0 && dirty.Count == 0 && texts.Count > 300,
            $"справочный центр: 5 шагов рабочего процесса, карточек {HelpCenterContent.Topics.Count} (методика: СП 63, СП 20, СП 16, СП 50, ГОСТ 27751, СанПиН), горячих клавиш {HelpCenterContent.HotKeys.Count}; " +
            $"поиск «продавливание» — {punching}, «csv поставщик» — {csv}, несуществующее — 0; строк интерфейса проверено {texts.Count}: без эмодзи и упоминаний «ИИ / AI / LLM»" +
            (dirty.Count > 0 ? " [" + string.Join(" | ", dirty.Take(3)) + "]" : string.Empty));
        return failures;
    }
}
#endif
