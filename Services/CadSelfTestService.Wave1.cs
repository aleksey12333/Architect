#if DEBUG
using System.Text;
using AIArchitectStudio.Models;
using AIArchitectStudio.Models.Geometry;
using AIArchitectStudio.Rendering;
using AIArchitectStudio.Services.AI;
using AIArchitectStudio.Services.Verification;
using AIArchitectStudio.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace AIArchitectStudio.Services;

/// <summary>
/// Автотест волны 1 продуктового ревью: чистый старт (онлайн-панель и процесс браузера не запускаются восстановлением модели),
/// кнопка «Начальный экран» (стартовое окно поверх редактора, выбор применяется без перезапуска, закрытие — без изменений),
/// неперекрывающий боковой док Коллоквиума и листа согласования (вьюпорт и док рядом, свёртка в полосу); конструктивная логика
/// проёмов (дверь в координатах колонны уходит в простенок с привязкой к стене, сквозной вырез в теле стены, отказ без места,
/// солвер со строгой логикой и без неё, стена «поперёк» разворачивается вдоль длины); 6 полых кровель (оболочка толщиной ската, а
/// не сплошная призма) и 5 форм лестниц × 3 конструкции со сквозным проёмом в перекрытии.
/// </summary>
public static partial class CadSelfTestService
{
    private static async Task<int> CheckWave1Async(StringBuilder report)
    {
        int failures = 0;
        failures += CheckOpeningGuard(report);
        failures += CheckSolverOpenings(report);
        failures += CheckHollowRoofs(report);
        failures += CheckStairGenerator(report);
        failures += await CheckStartShellAndDockAsync(report);
        return failures;
    }

    private static readonly GeometryContext AllLevels = new(-1, 0, 0);

    private static double PartsVolume(ElementGeometry g, Func<MaterialKind, bool>? filter = null) =>
        g.Parts.Where(p => filter is null || filter(p.Material)).Sum(p => ElementGeometryBuilder.MeshVolume(p.Mesh));

    private static ArchitecturalElement W1(string type, string name, double x, double baseY, double z, double w, double h, double d, double rot = 0, string material = "concrete")
    {
        var e = new ArchitecturalElement { Type = type, Name = name, BuildingGroupId = "Волна 1", Material = material };
        e.SetSize(w, h, d);
        e.SetPosition(x, baseY + h / 2, z);
        e.SetRotationY(rot);
        return e;
    }

    // ═════════════════════════════ 1. Логика проёмов ═════════════════════════════

    private static int CheckOpeningGuard(StringBuilder report)
    {
        int failures = 0;
        var wall = W1(ElementTypes.Wall, "Стена по оси А", 0, 0, 0, 6, 3, 0.3, 0, "brick");
        var column = W1(ElementTypes.Column, "Колонна К-1", 0, 0, 0, 0.4, 3, 0.4);
        // Дверь поставлена прямо в координаты колонны, без хоста, с чужим поворотом и в 0,3 м от оси стены.
        var door = W1(ElementTypes.Door, "Дверь Д-1", 0, 0.4, 0.3, 0.9, 2.1, 0.2, 37, "wood");
        var scene = new List<ArchitecturalElement> { wall, column, door };
        var guard = BuildingLogicGuard.Enforce(scene, door);
        double edgeGap = Math.Abs(door.X) - door.Width / 2 - column.Width / 2;
        bool placed = guard.Success && guard.Reattached && door.HostElementId == wall.Id && Math.Abs(door.RotationY - wall.RotationY) < 1e-9 &&
                      edgeGap >= BuildingLogicGuard.Clearance - 1e-9 && Math.Abs(door.Z) < 1e-9 && Math.Abs(door.BaseY - wall.BaseY) < 1e-9 &&
                      Math.Abs(door.Depth - (wall.Depth + ParametricSolverService.OpeningClearance)) < 1e-12 &&
                      Math.Abs(door.X) + door.Width / 2 <= wall.Width / 2 - BuildingLogicGuard.Clearance + 1e-9;
        Check(report, ref failures, placed,
            $"дверь в координатах колонны → простенок: сдвиг {Math.Abs(guard.Shift) * 1000:0} мм, зазор до грани колонны {edgeGap * 1000:0} мм (≥ 150), HostElementId = стена, поворот и ось по стене, низ на полу стены — «{guard.Message}»");

        // Тело стены режется сквозным проёмом: левый и правый простенки + перемычка (3 тела), объём = стена − проём.
        var wallGeometry = ElementGeometryBuilder.Build(wall, [door], AllLevels);
        double wallVolume = PartsVolume(wallGeometry);
        double expected = wall.Width * wall.Height * wall.Depth - door.Width * door.Height * wall.Depth;
        int vertices = wallGeometry.Parts.Sum(p => p.Mesh.Positions.Count);
        var window = W1(ElementTypes.Window, "Окно ОК-1", 1.9, 0.9, 0, 1.2, 1.5, 0.36, 0, "glass");
        window.HostElementId = wall.Id;
        var guardWindow = BuildingLogicGuard.Enforce([wall, column, door, window], window);
        var both = ElementGeometryBuilder.Build(wall, [door, window], AllLevels);
        double bothVolume = PartsVolume(both);
        double expectedBoth = expected - window.Width * window.Height * wall.Depth;
        Check(report, ref failures, Math.Abs(wallVolume - expected) < 1e-3 && vertices == 3 * 24 && guardWindow.Success && Math.Abs(bothVolume - expectedBoth) < 1e-3 &&
                                    Math.Abs(window.BaseY - (wall.BaseY + 0.9)) < 1e-9,
            $"сквозной проём в BimMesh стены: 3 тела (простенки и перемычка), объём {wallVolume:0.###} ≡ {expected:0.###} м³; с окном (подоконная зона и перемычка) {bothVolume:0.###} ≡ {expectedBoth:0.###} м³, а не сплошной параллелепипед {wall.Width * wall.Height * wall.Depth:0.###} м³");

        // Нет места: короткая стена; окно между двумя колоннами; окно без стены в радиусе 0,5 м.
        var shortWall = W1(ElementTypes.Wall, "Короткая стена", 20, 0, 0, 1.2, 3, 0.25);
        var tooWide = W1(ElementTypes.Window, "Окно 1500", 20, 0.9, 0, 1.5, 1.5, 0.3, 0, "glass");
        tooWide.HostElementId = shortWall.Id;
        var failShort = BuildingLogicGuard.Enforce([shortWall, tooWide], tooWide);
        var pier = W1(ElementTypes.Wall, "Простенок между колоннами", 40, 0, 0, 2.4, 3, 0.3);
        var c1 = W1(ElementTypes.Column, "К-а", 39.4, 0, 0, 0.4, 3, 0.4);
        var c2 = W1(ElementTypes.Column, "К-б", 40.6, 0, 0, 0.4, 3, 0.4);
        var squeezed = W1(ElementTypes.Window, "Окно между колоннами", 40, 0.9, 0, 1.0, 1.5, 0.3, 0, "glass");
        squeezed.HostElementId = pier.Id;
        double squeezedX = squeezed.X;
        var failColumns = BuildingLogicGuard.Enforce([pier, c1, c2, squeezed], squeezed);
        var orphan = W1(ElementTypes.Door, "Дверь в поле", 60, 0, 5, 0.9, 2.1, 0.2, 0, "wood");
        var failOrphan = BuildingLogicGuard.Enforce([wall, orphan], orphan);
        Check(report, ref failures, !failShort.Success && failShort.Message.Contains("короткая") && !failColumns.Success && failColumns.Message.Contains("колонна") &&
                                    Math.Abs(squeezed.X - squeezedX) < 1e-12 && squeezed.HostElementId == pier.Id && !failOrphan.Success && orphan.HostElementId is null,
            $"отказ без места: «{failShort.Message}»; «{failColumns.Message}»; «{failOrphan.Message}» — элемент не меняется");

        // Ручная врезка двери в стену с колонной посередине: проём уходит в простенок, ровно один шаг журнала.
        var live = new SceneStateService();
        var lw = W1(ElementTypes.Wall, "Стена Б", 0, 0, 10, 6, 3, 0.3);
        var lc = W1(ElementTypes.Column, "Колонна Б-2", 0, 0, 10, 0.4, 3, 0.4);
        live.LoadScene([lw, lc], [new BuildingGroup("Волна 1", "house", "#5AC8FA")], recordUndo: false, clearHistory: true, axes: []);
        var manual = ManualModelingService.InsertOpening(live, live.Elements.First(e => e.Name == "Стена Б"), door: true);
        var inserted = manual.Created.FirstOrDefault();
        double manualGap = inserted is null ? 0 : Math.Abs(inserted.X) - inserted.Width / 2 - 0.2;
        Check(report, ref failures, manual.Success && inserted is not null && manualGap >= BuildingLogicGuard.Clearance - 1e-9 && live.UndoDepth == 1 &&
                                    inserted.HostElementId == live.Elements.First(e => e.Name == "Стена Б").Id,
            $"«Добавить дверь» в стену с колонной посередине: зазор до колонны {manualGap * 1000:0} мм, 1 шаг журнала — {manual.Message}");
        return failures;
    }

    private static SolverResult? SolveJson(string json, bool enforce) =>
        AiJsonExtractor.TryExtract(json, out var doc)
            ? new ParametricSolverService().Solve(new SolverInput { Document = doc, Mode = SolverMode.NewScene, EnforceBuildingLogic = enforce })
            : null;

    private static int CheckSolverOpenings(StringBuilder report)
    {
        int failures = 0;
        const string doorJson = """
{"mode":"new","message":"Волна 1","elements":[
{"id":"w","type":"wall","name":"Стена","start":[0,0],"end":[6,0],"height":3,"thickness":0.3},
{"id":"c","type":"column","name":"Колонна","position":[3,0],"size":[0.4,3,0.4]},
{"id":"d","type":"door","name":"Дверь","host":"w","position":[3,0],"size":[0.9,2.1,0.3]}]}
""";
        var strict = SolveJson(doorJson, enforce: true);
        var legacy = SolveJson(doorJson, enforce: false);
        var strictDoor = strict?.Elements.FirstOrDefault(e => e.Type == ElementTypes.Door);
        var legacyDoor = legacy?.Elements.FirstOrDefault(e => e.Type == ElementTypes.Door);
        var strictColumn = strict?.Elements.FirstOrDefault(e => e.Type == ElementTypes.Column);
        double gap = strictDoor is null || strictColumn is null ? 0 : Math.Abs(strictDoor.X - strictColumn.X) - strictDoor.Width / 2 - strictColumn.Width / 2;
        Check(report, ref failures, strictDoor is not null && legacyDoor is not null && gap >= BuildingLogicGuard.Clearance - 1e-6 && Math.Abs(legacyDoor.X - 3) < 1e-6 &&
                                    strictDoor.HostElementId == strict!.Elements.First(e => e.Type == ElementTypes.Wall).Id,
            $"солвер: строгая логика сдвигает дверь от колонны (зазор {gap * 1000:0} мм); без флага (эталонные сцены) — прежняя позиция X = {legacyDoor?.X:0.###} м");

        // Стена «поперёк»: [толщина, высота, длина] без поворота — ось разворачивается вдоль длины, окно остаётся на своём месте стены.
        const string wallJson = """
{"mode":"new","message":"Волна 1","elements":[
{"id":"w","type":"wall","name":"Стена поперёк","position":[0,0],"size":[0.3,3,8]},
{"id":"o","type":"window","name":"Окно","host":"w","position":[0,2],"size":[1.2,1.5,0.3]}]}
""";
        var turned = SolveJson(wallJson, enforce: false);
        var tw = turned?.Elements.FirstOrDefault(e => e.Type == ElementTypes.Wall);
        var to = turned?.Elements.FirstOrDefault(e => e.Type == ElementTypes.Window);
        var axis = tw is null ? (0.0, 0.0) : ArchitecturalElement.RotateY(1, 0, tw.RotationY);
        bool oriented = tw is not null && to is not null && Math.Abs(tw.Width - 8) < 1e-9 && Math.Abs(tw.Depth - 0.3) < 1e-9 && Math.Abs(Math.Abs(axis.Item2) - 1) < 1e-9 &&
                        Math.Abs(to.X) < 1e-6 && Math.Abs(to.Z - 2) < 1e-6 && to.HostElementId == tw.Id && Math.Abs(ArchitecturalElement.NormalizeAngle(to.RotationY - tw.RotationY)) < 1e-9;
        Check(report, ref failures, oriented,
            $"стена «поперёк здания» (size [0,3; 3; 8]): длина {tw?.Width:0.###} м по локальной X, ось вдоль Z, окно на своём месте (Z = {to?.Z:0.###} м) и в теле стены");
        return failures;
    }

    // ═════════════════════════════ 2. Полые кровли ═════════════════════════════

    private static int CheckHollowRoofs(StringBuilder report)
    {
        int failures = 0;
        var lines = new List<string>();
        bool all = true;
        foreach (string shape in RoofShapes.Hollow)
        {
            var slab = W1(ElementTypes.Slab, "Плита покрытия", 0, 2.8, 0, 10, 0.2, 8);
            var result = RoofGeneratorService.Apply([slab], slab, new RoofDesignRequest(shape, 30, 0.25, 0.45, 0.9, true));
            if (result is null) { all = false; lines.Add($"{shape}: не создана"); continue; }
            var roof = result.Roof;
            var info = ElementGeometryBuilder.DescribeHollowRoof(roof);
            var geometry = ElementGeometryBuilder.Build(roof, [], AllLevels);
            bool ok;
            string detail;
            if (shape == RoofShapes.FlatParapet)
            {
                double plate = roof.Width * roof.Depth * roof.Height;
                double parapet = PartsVolume(geometry, k => k == MaterialKind.Concrete);
                double slabPart = PartsVolume(geometry) - parapet;
                double perimeter = 2 * (roof.Width + roof.Depth);
                double expected = perimeter * RoofDesign.ParapetThickness * 0.9;
                ok = info is not null && roof.IsFlatRoof && roof.CanHostSlabOpenings && Math.Abs(parapet - expected) / expected < 0.06 && Math.Abs(slabPart - plate) < 1e-3 &&
                     geometry.LocalBounds.Maximum.Y >= roof.Height / 2 + 0.9 - 1e-4 && Math.Abs(roof.BaseY - slab.TopY) < 1e-9;
                detail = $"плита {plate:0.##} м³ + парапет 900 мм {parapet:0.##} м³ (≈ {expected:0.##})";
            }
            else
            {
                double target = info is null ? 0 : info.Thickness * info.SlopeArea;
                bool frontons = shape is RoofShapes.HipHollow || PartsVolume(geometry, k => k == MaterialKind.Plaster) > 0.5;
                bool soffit = shape is RoofShapes.BarrelVault || PartsVolume(geometry, k => k == MaterialKind.Finish) > 0.01;
                bool gableWindow = shape is not (RoofShapes.GableHollow or RoofShapes.Mansard or RoofShapes.BarrelVault) || geometry.Parts.Any(p => p.Material == MaterialKind.Glass);
                ok = info is not null && info.ShellVolume > 0 && Math.Abs(info.ShellVolume - target) / target < 0.06 && info.ShellVolume < 0.4 * info.SolidPrismVolume &&
                     frontons && soffit && gableWindow && Math.Abs(info.Thickness - 0.25) < 1e-12 && Math.Abs(roof.BaseY - slab.TopY) < 1e-9 && !roof.CanHostSlabOpenings;
                detail = $"оболочка {info?.ShellVolume:0.##} м³ ≈ t·S = 0,25 × {info?.SlopeArea:0.#} м² = {target:0.##}, сплошная призма {info?.SolidPrismVolume:0.#} м³ ({info?.ShellVolume / info?.SolidPrismVolume:P0})";
            }
            all &= ok;
            lines.Add($"{RoofGeneratorService.ShapeTitle(shape)}: {detail}{(ok ? string.Empty : " — НЕ ПРОШЛО")}");
        }
        Check(report, ref failures, all, "6 полых кровель (скат 250 мм, свес 450 мм, уклон 30°): " + string.Join("; ", lines));

        // Прежние формы без параметров строятся по-старому (сплошное тело), полые параметры не читаются.
        var legacy = W1(ElementTypes.Roof, "Двускатная (прежняя)", 0, 3, 0, 10, 2.6, 8, 0, "metal_roof");
        legacy.RoofShape = RoofShapes.Gable;
        double legacyVolume = PartsVolume(ElementGeometryBuilder.Build(legacy, [], AllLevels));
        Check(report, ref failures, Math.Abs(legacyVolume - 10 * 8 * 2.6 / 2) < 1e-2 && ElementGeometryBuilder.DescribeHollowRoof(legacy) is null && legacy.UserParameters is null,
            $"прежняя двускатная кровля (эталонные сцены) — сплошная призма {legacyVolume:0.##} м³ без новых параметров");
        return failures;
    }

    // ═════════════════════════════ 3. Конструктор лестниц ═════════════════════════════

    private static int CheckStairGenerator(StringBuilder report)
    {
        int failures = 0;
        var lines = new List<string>();
        bool all = true;
        var constructions = Enum.GetValues<StairConstruction>();
        int n = 0;
        foreach (var shape in Enum.GetValues<StairShape>())
        {
            var construction = constructions[n++ % constructions.Length];
            var foundation = W1(ElementTypes.Foundation, "Фундамент", 0, -0.5, 0, 14, 0.5, 14);
            var slab = W1(ElementTypes.Slab, "Перекрытие 1 этажа", 0, 2.8, 0, 14, 0.2, 14);
            var scene = new SceneStateService();
            scene.LoadScene([foundation, slab], [new BuildingGroup("Волна 1", "house", "#5AC8FA")], recordUndo: false, clearHistory: true, axes: []);
            var liveSlab = scene.Elements.First(e => e.Type == ElementTypes.Slab);
            var gen = StairGeneratorService.Build(scene.Elements.ToList(), new StairDesignRequest(shape, construction), "Волна 1", 0, 0, 3.0, 1, -1, 15, "Т-1");
            using (scene.BeginUndoTransaction("Лестница (автотест)"))
                scene.AddElements(gen.Created, recordUndo: false);
            var opening = gen.Openings.FirstOrDefault();
            var flights = gen.Flights.ToList();
            List<(double X, double Z)> outline = opening is null ? [] : ElementGeometryBuilder.WorldOutline(opening);
            // Точки марша чуть внутрь от его контура (граница клетки П-образной лестницы совпадает с краем проёма).
            var samples = flights.SelectMany(f => StairDesign.IsSpiral(f)
                ? Enumerable.Range(0, 16).Select(i => (X: f.X + Math.Min(f.Width, f.Depth) / 2 * Math.Cos(i * Math.PI / 8), Z: f.Z + Math.Min(f.Width, f.Depth) / 2 * Math.Sin(i * Math.PI / 8)))
                : ElementGeometryBuilder.WorldOutline(f).Select(p => (X: f.X + (p.X - f.X) * 0.98, Z: f.Z + (p.Z - f.Z) * 0.98))).ToList();
            bool covered = opening is not null && samples.All(p => SceneStateService.PointInPolygon(p.X, p.Z, outline));
            bool noClash = ClashDetectionService.Detect(scene.Elements.ToList()).All(c => c.Category != ClashCategory.StairsWithoutOpening);
            int risers = flights.Sum(f => f.StepCount);
            bool parametrized = flights.All(f => StairDesign.Construction(f) is not null) && (shape != StairShape.Spiral || flights.All(StairDesign.IsSpiral));
            // Конструкция без сплошной заливки: объём марша против прежнего монолита до пола.
            double ratio = flights.Select(f =>
            {
                var plain = f.Clone(keepId: false);
                foreach (var g in new[] { StairDesign.ConstructionGuid, StairDesign.ShapeGuid }) plain.RemoveUserParameter(g);
                double was = PartsVolume(ElementGeometryBuilder.Build(plain, [], AllLevels));
                return PartsVolume(ElementGeometryBuilder.Build(f, [], AllLevels)) / Math.Max(1e-9, was);
            }).Max();
            double limit = construction == StairConstruction.MonolithicSlab && shape != StairShape.Spiral ? 0.8 : 0.4;
            int expectedFlights = shape switch { StairShape.Straight or StairShape.Spiral => 1, StairShape.ThreeFlight => 3, _ => 2 };
            bool ok = gen.OpeningsCut == 1 && opening!.HostElementId == liveSlab.Id && covered && noClash && risers == gen.Risers && risers == 20 - (shape == StairShape.Spiral ? 3 : 0) &&
                      flights.Count == expectedFlights && parametrized && ratio < limit && scene.UndoDepth == 1;
            all &= ok;
            lines.Add($"{StairGeneratorService.ShapeTitle(shape)} · {StairGeneratorService.ConstructionTitle(construction)}: маршей {flights.Count}, подступенков {risers}, клетка {gen.FootprintWidth:0.##} × {gen.FootprintLength:0.##} м, проём над маршами {(covered ? "да" : "НЕТ")}, объём к монолиту {ratio:P0}{(ok ? string.Empty : " — НЕ ПРОШЛО")}");
        }
        Check(report, ref failures, all, "5 форм лестниц (этаж 3,0 м, подступенок 150 мм, винтовая — 180 мм): " + string.Join("; ", lines));
        return failures;
    }

    // ═════════════════════════════ 4. Старт, начальный экран, боковой док ═════════════════════════════

    private static async Task<int> CheckStartShellAndDockAsync(StringBuilder report)
    {
        int failures = 0;
        if (App.MainWindowInstance is not MainWindow window || window.LayoutRoot.XamlRoot is null)
        {
            Check(report, ref failures, false, "главное окно недоступно для проверки компоновки");
            return failures;
        }
        var vm = window.ViewModel;

        // Чистый старт: восстановление веб-модели (настройки, проект) не открывает онлайн-панель и не запускает браузер.
        string model = vm.SelectedAiModel, saved = vm.Settings.Current.AiModel;
        string web = AiModelCatalog.WebOptions[0].Id;
        vm.Settings.Current.AiModel = web;
        vm.SelectedAiModel = web;
        bool clean = !vm.IsWebBridgeOpen && !window.IsWebBrowserStarted;
        vm.Settings.Current.AiModel = model;
        vm.SelectedAiModel = model;
        vm.Settings.Current.AiModel = saved;
        Check(report, ref failures, clean && !vm.IsWebBridgeOpen,
            "чистый старт: выбранная веб-модель из настроек не разворачивает онлайн-панель (IsWebBridgeOpen = false) и не запускает процесс браузера WebView2");

        // «Начальный экран»: кнопка в левой группе бара; стартовое окно поверх редактора, закрытие — без изменений, выбор — шаблон в этом же окне.
        bool inBar = IsDescendant(window.StartScreenButtonElement, window.TopLeftToolsElement) && window.StartScreenButtonElement.Visibility == Visibility.Visible;
        string before = DocumentFingerprint(vm.Scene);
        vm.Scene.IsDirty = false;
        vm.OpenStartScreenCommand.Execute(null);
        await Task.Delay(400);
        bool opened = MainWindow.IsStartScreenOpen;
        App.StartScreenWindow?.Close();
        await Task.Delay(300);
        bool closedClean = !MainWindow.IsStartScreenOpen && DocumentFingerprint(vm.Scene) == before;
        vm.OpenStartScreenCommand.Execute(null);
        await Task.Delay(400);
        if (App.StartScreenWindow is { } welcome) await welcome.DebugCompleteAsync(StartupChoice.Preset(PresetScenesService.Frame));
        await Task.Delay(500);
        bool switched = !MainWindow.IsStartScreenOpen && ReferenceEquals(App.MainWindowInstance, window) && vm.Scene.Elements.Count > 50 &&
                        vm.Scene.ProjectName.Length > 0 && !vm.Scene.IsDirty;
        Check(report, ref failures, inBar && opened && closedClean && switched,
            $"«Начальный экран»: кнопка в левой группе бара, стартовое окно открыто поверх редактора, закрытие без выбора — проект без изменений, выбор шаблона «Каркасный корпус» — {vm.Scene.Elements.Count} эл. в том же окне без перезапуска");

        // Боковой док: лента Коллоквиума справа от вьюпорта, без перекрытия; свёртка в полосу; закрытие возвращает холст во всю ширину.
        bool wasOpen = vm.IsCouncilTimelineOpen, wasCollapsed = vm.IsCouncilDockCollapsed;
        vm.IsCouncilDockCollapsed = false;
        vm.IsCouncilTimelineOpen = true;
        await Layout(window);
        var root = window.LayoutRoot;
        Rect Bounds(FrameworkElement e) => e.TransformToVisual(root).TransformBounds(new Rect(0, 0, e.ActualWidth, e.ActualHeight));
        var vp = Bounds(window.ViewportElement);
        var dock = Bounds(window.CouncilDockExpandedElement);
        bool sideBySide = window.CouncilDockElement.Visibility == Visibility.Visible && Math.Abs(dock.Width - 400) < 1 && vp.Right <= dock.Left + 0.5 &&
                          vp.Width >= root.ActualWidth - dock.Width - 14 - 1 && HitsViewport(window, new Point(vp.Left + vp.Width * 0.4, vp.Top + vp.Height * 0.45));
        double vpOpen = vp.Width;

        // Лист согласования — в том же доке (вместо ленты), вьюпорт не перекрыт.
        var (defect, defectGroups, defectAxes) = CreateGateDefectScene();
        var gateScene = new SceneStateService();
        gateScene.LoadScene(defect, defectGroups, recordUndo: false, clearHistory: true, axes: defectAxes, dimensions: []);
        var gate = VerificationGateService.Inspect(gateScene, new StructuralLoadSettings(), "Автотест волны 1");
        vm.ShowReview(gate);
        await Layout(window);
        var review = Bounds(window.ReviewPanelElement);
        vp = Bounds(window.ViewportElement);
        bool reviewDocked = vm.IsReviewOpen && IsDescendant(window.ReviewPanelElement, window.CouncilDockElement) && window.ReviewPanelElement.Visibility == Visibility.Visible &&
                            review.Left >= vp.Right - 0.5 && review.Height > 300;
        vm.KeepGeneratorVariantCommand.Execute(null);

        vm.IsCouncilDockCollapsed = true;
        await Layout(window);
        var strip = Bounds(window.CouncilDockStripElement);
        double vpStrip = Bounds(window.ViewportElement).Width;
        bool collapsed = window.CouncilDockStripElement.Visibility == Visibility.Visible && Math.Abs(strip.Width - 48) < 1 && vpStrip > vpOpen + 300;
        vm.CloseCouncilDockCommand.Execute(null);
        await Layout(window);
        double vpClosed = Bounds(window.ViewportElement).Width;
        bool closed = window.CouncilDockElement.Visibility == Visibility.Collapsed && Math.Abs(vpClosed - root.ActualWidth) < 1;
        vm.IsCouncilTimelineOpen = wasOpen;
        vm.IsCouncilDockCollapsed = wasCollapsed;
        Check(report, ref failures, sideBySide && reviewDocked && collapsed && closed,
            $"Коллоквиум в боковом доке: вьюпорт {vpOpen:0} px + док {dock.Width:0} px рядом (клик в центре холста попадает во вьюпорт), лист согласования в доке ({review.Height:0} px), полоса {strip.Width:0} px → вьюпорт {vpStrip:0} px, закрыт → {vpClosed:0} px из {root.ActualWidth:0}");
        return failures;
    }

    // ═════════════════════════════ Сцены съёмки волны 1 (AIA_DEBUG_WAVE1) ═════════════════════════════

    /// <summary>
    /// roof — двухэтажный дом 12 × 9 м с полой мансардной кровлей (скат 250 мм, свес 450 мм, фронтонные окна) и прямым маршем на
    /// косоурах со сквозным проёмом в перекрытии; guard — каркас с окнами и дверями, поставленными в координаты колонн и
    /// разведёнными <see cref="BuildingLogicGuard"/> в простенки.
    /// </summary>
    public static (List<ArchitecturalElement> Elements, List<BuildingGroup> Groups, List<StructuralGridAxis> Axes) CreateWave1DemoScene(string kind)
    {
        const string g = "Волна 1";
        var list = new List<ArchitecturalElement>();
        ArchitecturalElement Add(ArchitecturalElement e, int level = 0)
        {
            e.LevelIndex = level;
            list.Add(e);
            return e;
        }
        Add(W1(ElementTypes.Foundation, "Фундаментная плита", 0, -0.5, 0, 12.6, 0.5, 9.6));
        int storeys = kind == "guard" ? 1 : 2;
        var openings = new List<ArchitecturalElement>();
        for (int level = 0; level < storeys; level++)
        {
            double b = level * 3.0;
            var south = Add(W1(ElementTypes.Wall, $"Стена Ю-{level + 1}", 0, b, -4.5, 12.3, 2.8, 0.3, 0, "brick"), level);
            var north = Add(W1(ElementTypes.Wall, $"Стена С-{level + 1}", 0, b, 4.5, 12.3, 2.8, 0.3, 0, "brick"), level);
            Add(W1(ElementTypes.Wall, $"Стена З-{level + 1}", -6, b, 0, 8.7, 2.8, 0.3, 90, "brick"), level);
            Add(W1(ElementTypes.Wall, $"Стена В-{level + 1}", 6, b, 0, 8.7, 2.8, 0.3, 90, "brick"), level);
            int mark = 0;
            foreach (double x in new[] { -6.0, -2.0, 2.0, 6.0 })
                foreach (double z in new[] { -4.5, 4.5 })
                    Add(W1(ElementTypes.Column, $"Колонна К-{level + 1}.{++mark}", x, b, z, 0.4, 2.8, 0.4), level);
            // Проёмы поставлены прямо в координаты колонн: логика проёмов разводит их в простенки.
            openings.Add(Add(W1(ElementTypes.Door, $"Дверь Д-{level + 1}", -2.0, b, -4.5, 0.9, 2.1, 0.36, 0, "wood"), level));
            openings.Add(Add(W1(ElementTypes.Window, $"Окно ОК-{level + 1}.1", 2.0, b + 0.9, -4.5, 1.5, 1.5, 0.36, 0, "glass"), level));
            openings.Add(Add(W1(ElementTypes.Window, $"Окно ОК-{level + 1}.2", -4.3, b + 0.9, -4.5, 1.5, 1.5, 0.36, 0, "glass"), level));
            openings.Add(Add(W1(ElementTypes.Window, $"Окно ОК-{level + 1}.3", 4.1, b + 0.9, -4.5, 1.5, 1.5, 0.36, 0, "glass"), level));
            openings.Add(Add(W1(ElementTypes.Window, $"Окно ОК-{level + 1}.4", 0, b + 0.9, 4.5, 1.8, 1.5, 0.36, 0, "glass"), level));
            if (kind != "guard" || level < storeys - 1)
                Add(W1(ElementTypes.Slab, $"Перекрытие {level + 1}", 0, b + 2.8, 0, 12.6, 0.2, 9.6), level);
        }
        foreach (var o in openings) BuildingLogicGuard.Enforce(list, o);
        if (kind == "guard")
        {
            var cover = Add(W1(ElementTypes.Roof, "Плоская кровля", 0, 2.8, 0, 12.6, 0.2, 9.6), 0);
            cover.RoofShape = RoofShapes.Flat;
        }
        else
        {
            var top = list.Last(e => e.Type == ElementTypes.Slab);
            if (RoofGeneratorService.Apply(list, top, new RoofDesignRequest(RoofShapes.Mansard, 30, 0.25, 0.45, 0.9, true)) is { Created: true } roof)
                Add(roof.Roof, 1);
            var stair = StairGeneratorService.Build(list, new StairDesignRequest(StairShape.Straight, StairConstruction.Stringers), g, 0, 0, 3.0, -3.8, 0, 0, "Л-1");
            foreach (var r in stair.Removed) list.Remove(r);
            foreach (var e in stair.Created) Add(e, e.Category == BimCategory.SlabOpening ? e.LevelIndex : 0);
        }
        foreach (var e in list) e.BuildingGroupId = g;
        return (list, [new BuildingGroup(g, "house", "#5AC8FA")], []);
    }

    private static async Task Layout(MainWindow window)
    {
        await Task.Delay(250);
        window.LayoutRoot.UpdateLayout();
        await Task.Delay(50);
    }

    private static bool IsDescendant(DependencyObject child, DependencyObject ancestor)
    {
        for (var d = child; d is not null; d = VisualTreeHelper.GetParent(d))
            if (ReferenceEquals(d, ancestor)) return true;
        return false;
    }

    /// <summary>Верхний элемент под точкой (координаты корня) принадлежит вьюпорту: холст доступен для кликов и вращения.</summary>
    private static bool HitsViewport(MainWindow window, Point point)
    {
        var top = VisualTreeHelper.FindElementsInHostCoordinates(point, (UIElement)window.LayoutRoot, includeAllElements: false).FirstOrDefault();
        return top is not null && IsDescendant(top, window.ViewportElement);
    }
}
#endif
