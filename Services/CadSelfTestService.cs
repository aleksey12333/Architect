#if DEBUG
using System.Globalization;
using System.Text;
using AIArchitectStudio.Models;
using AIArchitectStudio.Rendering;
using Windows.Data.Pdf;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;

namespace AIArchitectStudio.Services;

/// <summary>
/// Только Debug (AIA_DEBUG_SELFTEST=папка): автотест двустороннего обмена и выпуска чертежей — тестовый каркас
/// (колонны всех профилей, балки, плиты с отверстиями, стены с окном, дверью и проёмом, оси) экспортируется в IFC и DXF,
/// импортируется обратно и сравнивается с исходником с допуском 0.1 мм; листы ГОСТ выпускаются в PDF и растеризуются в PNG.
/// </summary>
public static partial class CadSelfTestService
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    public const string FrameGroup = "Каркасный корпус";
    private const double Tol = 1e-4;

    /// <summary>Тестовое каркасное здание: 3 этажа, сетка 4 × 3 оси, отметки −0,620 / −0,020 / +2,830 / +5,680 / +8,530.</summary>
    public static (List<ArchitecturalElement> Elements, List<BuildingGroup> Groups, List<StructuralGridAxis> Axes) CreateFrameScene()
    {
        const string g = FrameGroup;
        var elements = new List<ArchitecturalElement>();
        int n = 0;
        ArchitecturalElement Add(string type, string name, double x, double baseY, double z, double w, double h, double d, double rot = 0, int level = 0, string material = "concrete")
        {
            var e = new ArchitecturalElement { Id = $"el_test{++n:D4}", Type = type, Name = name, BuildingGroupId = g, LevelIndex = level, Material = material };
            e.SetSize(w, h, d);
            e.SetPosition(x, baseY + h / 2, z);
            e.SetRotationY(rot);
            elements.Add(e);
            return e;
        }

        double[] xs = [-9, -3, 3, 9];
        double[] zs = [6, 0, -6];
        double[] levels = [-0.02, 2.83, 5.68];
        const double colH = 2.65, slabH = 0.2;

        var foundation = Add(ElementTypes.Foundation, "Фундаментная плита", 0, -0.62, 0, 20.4, 0.6, 13.6);
        AddSlabHole(elements, foundation, "Приямок", -6, 3, 1.2, 1.2, ref n);

        for (int level = 0; level < 3; level++)
        {
            double b = levels[level];
            for (int i = 0; i < xs.Length; i++)
                for (int j = 0; j < zs.Length; j++)
                {
                    var c = Add(ElementTypes.Column, $"Колонна {level + 1}-{i + 1}{j + 1}", xs[i], b, zs[j], 0.4, colH, 0.4, 0, level);
                    if (level == 0)
                    {
                        // Первый этаж — все виды сечений.
                        switch ((i + j) % 5)
                        {
                            case 1: c.ProfileType = ProfileTypes.Circle; c.SetSize(0.45, colH, 0.45); break;
                            case 2: c.SetSize(0.4, colH, 0.6); c.SetRotationY(90); break;
                            case 3: c.ProfileType = ProfileTypes.Pipe; c.Material = "steel"; c.SetSize(0.325, colH, 0.325); c.ProfileWallThickness = 0.008; break;
                            case 4: c.ProfileType = ProfileTypes.IBeam; c.Material = "steel"; c.SetSize(0.3, colH, 0.4); c.ProfileWallThickness = 0.016; c.ProfileWebThickness = 0.01; break;
                        }
                        c.PlaceOnBase(b);
                    }
                }

            // Балки по оси Б и по оси 2 (под плитой).
            double beamTop = b + colH;
            for (int i = 0; i + 1 < xs.Length; i++)
            {
                var beam = Add(ElementTypes.Beam, $"Балка {level + 1}-Б{i + 1}", (xs[i] + xs[i + 1]) / 2, beamTop - 0.5, 0, 6, 0.5, 0.3, 0, level);
                if (level == 0 && i == 1) { beam.ProfileType = ProfileTypes.IBeam; beam.Material = "steel"; beam.SetSize(6, 0.5, 0.2); beam.PlaceOnBase(beamTop - 0.5); beam.ProfileWallThickness = 0.016; beam.ProfileWebThickness = 0.01; }
                if (level == 0 && i == 2) { beam.ProfileType = ProfileTypes.Pipe; beam.Material = "steel"; beam.SetSize(6, 0.325, 0.325); beam.PlaceOnBase(beamTop - 0.325); beam.ProfileWallThickness = 0.008; }
            }
            for (int j = 0; j + 1 < zs.Length; j++)
                Add(ElementTypes.Beam, $"Балка {level + 1}-2{j + 1}", -3, beamTop - 0.5, (zs[j] + zs[j + 1]) / 2, 6, 0.5, 0.3, 90, level);

            if (level < 2)
            {
                var slab = Add(ElementTypes.Slab, $"Плита перекрытия {level + 1}", 0, beamTop, 0, 18.8, slabH, 12.8, 0, level);
                AddSlabHole(elements, slab, "Лестничный проём", 4.6, 1.8, 2.4, 3.2, ref n);
                if (level == 0)
                {
                    // Полигональное отверстие (шахта) — контур Polygon2D.
                    var poly = new ArchitecturalElement
                    {
                        Id = $"el_test{++n:D4}", Type = ElementTypes.SlabOpening, Name = "Шахта", BuildingGroupId = g, LevelIndex = slab.LevelIndex,
                        Material = "void", HostElementId = slab.Id,
                        Polygon2D = [new(-0.8, -0.6), new(0.8, -0.6), new(1.1, 0.4), new(0, 1.0), new(-1.1, 0.4)]
                    };
                    poly.SetSize(2.2, slab.Height + ParametricSolverService.OpeningClearance, 1.6);
                    poly.SetPosition(-6.2, slab.Y, -2.4);
                    elements.Add(poly);
                }
            }
            else
            {
                var roof = Add(ElementTypes.Roof, "Плоская кровля", 0, beamTop, 0, 18.8, slabH, 12.8, 0, level);
                roof.RoofShape = RoofShapes.Flat;
                AddSlabHole(elements, roof, "Люк выхода на кровлю", -4.5, -3, 1.0, 1.0, ref n);
            }
        }

        // Наружная стена по оси А с окном и дверью, стена по оси 1 со сквозным проёмом.
        var wallA = Add(ElementTypes.Wall, "Стена по оси А", 0, -0.02, 6.15, 18, colH, 0.3, 0, 0, "brick");
        var window = Add(ElementTypes.Window, "Окно ОК-1", 2.2, 0.88, 6.15, 1.5, 1.5, 0.36, 0, 0, "glass");
        window.HostElementId = wallA.Id;
        var door = Add(ElementTypes.Door, "Дверь Д-1", -5.4, -0.02, 6.15, 1.0, 2.1, 0.36, 0, 0, "wood");
        door.HostElementId = wallA.Id;
        var wall1 = Add(ElementTypes.Wall, "Стена по оси 1", -9.15, -0.02, 0, 12, colH, 0.3, 90, 0, "concrete");
        var opening = Add(ElementTypes.WallOpening, "Проём П-1", -9.15, 0.78, -1.5, 1.2, 1.4, 0.36, 90, 0, "void");
        opening.HostElementId = wall1.Id;

        var axes = new List<StructuralGridAxis>();
        for (int i = 0; i < xs.Length; i++) axes.Add(new StructuralGridAxis((i + 1).ToString(Inv), StructuralGridAxis.AlongZ, xs[i], g));
        for (int j = 0; j < zs.Length; j++) axes.Add(new StructuralGridAxis(StructuralAxesBuilder.Letter(j), StructuralGridAxis.AlongX, zs[j], g));
        return (elements, [new BuildingGroup(g, "office", "#5AC8FA")], axes);
    }

    private static void AddSlabHole(List<ArchitecturalElement> elements, ArchitecturalElement slab, string name, double x, double z, double w, double d, ref int n)
    {
        var hole = new ArchitecturalElement
        {
            Id = $"el_test{++n:D4}", Type = ElementTypes.SlabOpening, Name = name, BuildingGroupId = slab.BuildingGroupId, LevelIndex = slab.LevelIndex,
            Material = "void", HostElementId = slab.Id
        };
        hole.SetSize(w, slab.Height + ParametricSolverService.OpeningClearance, d);
        hole.SetPosition(x, slab.Y, z);
        elements.Add(hole);
    }

    /// <summary>Полный прогон: экспорт → импорт → сравнение, листы ГОСТ → PNG; отчёт — selftest.txt в папке.</summary>
    public static async Task<string> RunAsync(string directory)
    {
        Directory.CreateDirectory(directory);
        var report = new StringBuilder();
        var (elements, groups, axes) = CreateFrameScene();
        report.AppendLine($"Тестовая сцена: элементов {elements.Count}, осей {axes.Count}");
        int failures = 0;
        // Строгий μmin из настроек пользователя не должен менять эталон: автотест считает с нормами расхода по умолчанию.
        bool strictMinimum = ReinforcementCalculator.StrictMinimum;
        ReinforcementCalculator.StrictMinimum = false;

        // ── Эталон ядра: слепок сцен до бита (страховочная сетка рефакторинга BIM-ядра) — снимается первым, в чистом процессе.
        report.AppendLine().AppendLine("═══ Эталон ядра: координаты, геометрия, КЭ, смета, IFC/DXF/OBJ, .aia до бита ═══");
        failures += await CheckCoreBaselineAsync(directory, report);
        report.AppendLine().AppendLine("═══ Идентичность и категории BIM: IBimElement, UniqueId (UUID v5), BimCategory ═══");
        failures += CheckBimIdentity(report);
        report.AppendLine().AppendLine("═══ Система параметров: ParameterSet, слоты встроенных параметров, общие параметры, .aia v6 ═══");
        failures += await CheckParameterSystemAsync(directory, report);
        report.AppendLine().AppendLine("═══ Этажи и Type-Instance: BimLevel, вычисляемый LevelIndex, сигнатура и реестр типов, .aia v6 ═══");
        failures += await CheckLevelsAndTypesAsync(directory, report);
        report.AppendLine().AppendLine("═══ Свойства пользователя во внешних форматах: IFC AIA_UserParameters, DXF XData, ведомость по свойствам ═══");
        failures += await CheckUserParameterExchangeAsync(directory, report);

        var export = new CadExportService();
        string ifcPath = Path.Combine(directory, "roundtrip.ifc"), dxfPath = Path.Combine(directory, "roundtrip.dxf");
        var ifcExport = await export.ExportAsync(CadExportFormat.Ifc, ifcPath, elements.Select(e => e.Clone()).ToList(), groups, "Самопроверка", axes);
        var dxfExport = await export.ExportAsync(CadExportFormat.Dxf, dxfPath, elements.Select(e => e.Clone()).ToList(), groups, "Самопроверка", axes);
        report.AppendLine($"Экспорт IFC: {ifcExport.EntityCount:N0} STEP-сущностей, {ifcExport.Bytes / 1024} КБ; DXF: {dxfExport.EntityCount:N0} примитивов, {dxfExport.Bytes / 1024} КБ");

        // ── IFC Round-Trip: элементы сопоставляются по Id (Tag / AIA_Parameters).
        var ifc = await new CadIfcImporterService().ImportAsync(ifcPath);
        report.AppendLine().AppendLine("═══ IFC Round-Trip ═══");
        foreach (var line in ifc.Report) report.AppendLine("  " + line);
        int ifcChecked = 0;
        foreach (var src in elements)
        {
            var dst = ifc.Elements.FirstOrDefault(e => e.Id == src.Id);
            if (dst is null) { failures++; report.AppendLine($"  НЕТ элемента {src.Id} {src.Type} «{src.Name}»"); continue; }
            ifcChecked++;
            foreach (var problem in Compare(src, dst, elements, ifc.Elements, byId: true)) { failures++; report.AppendLine($"  ОТЛИЧИЕ {src.Type} «{src.Name}»: {problem}"); }
        }
        failures += CompareAxes(axes, ifc.Axes, report, "IFC");
        report.AppendLine($"  Проверено элементов: {ifcChecked} из {elements.Count}; лишних: {ifc.Elements.Count - ifcChecked}");

        // ── DXF Round-Trip: сопоставление по типу и ближайшему положению.
        var drawing = await new CadDxfImporterService().LoadAsync(dxfPath);
        var lift = CadDxfImporterService.LiftTo3D(drawing, new DxfLiftOptions(FrameGroup));
        report.AppendLine().AppendLine("═══ DXF Round-Trip ═══");
        foreach (var line in lift.Report) report.AppendLine("  " + line);
        int dxfChecked = 0;
        var used = new HashSet<ArchitecturalElement>();
        var match = new Dictionary<ArchitecturalElement, ArchitecturalElement>();
        foreach (var src in elements)
        {
            var dst = lift.Elements.Where(e => e.Type == src.Type && !used.Contains(e))
                .OrderBy(e => Math.Abs(e.X - src.X) + Math.Abs(e.Y - src.Y) + Math.Abs(e.Z - src.Z)).FirstOrDefault();
            if (dst is null) { failures++; report.AppendLine($"  НЕТ элемента {src.Type} «{src.Name}»"); continue; }
            used.Add(dst);
            match[src] = dst;
        }
        foreach (var (src, dst) in match)
        {
            dxfChecked++;
            foreach (var problem in Compare(src, dst, elements, lift.Elements, byId: false, match)) { failures++; report.AppendLine($"  ОТЛИЧИЕ {src.Type} «{src.Name}»: {problem}"); }
        }
        failures += CompareAxes(axes, lift.Axes, report, "DXF");
        report.AppendLine($"  Проверено элементов: {dxfChecked} из {elements.Count}; лишних: {lift.Elements.Count - dxfChecked}");

        // ── Подложка DXF чужого формата (миллиметры, слои AutoCAD): единицы и центрирование.
        string foreignPath = Path.Combine(directory, "foreign_mm.dxf");
        await File.WriteAllBytesAsync(foreignPath, BuildForeignDxf());
        var fd = await new CadDxfImporterService().LoadAsync(foreignPath);
        var fl = CadDxfImporterService.LiftTo3D(fd, new DxfLiftOptions("DXF · foreign"));
        report.AppendLine().AppendLine("═══ DXF AutoCAD (мм, слои СТЕНЫ/КОЛОННЫ/ОСИ) ═══");
        foreach (var line in fl.Report) report.AppendLine("  " + line);
        report.AppendLine($"  Габарит после пересчёта: {fd.MaxX - fd.MinX:0.0000} × {fd.MaxY - fd.MinY:0.0000} м, центр ({(fd.MinX + fd.MaxX) / 2:0.0000}; {(fd.MinY + fd.MaxY) / 2:0.0000})");
        var cols = fl.Elements.Where(e => e.Type == ElementTypes.Column).ToList();
        var walls = fl.Elements.Where(e => e.Type == ElementTypes.Wall).ToList();
        // Колонна в узле 1/А: в мм — (100200; 50200), в сцене — (100.2 + сдвиг X; −(50.2 + сдвиг Y)) с точностью 0.1 мм.
        double nodeX = 100.2 + fd.Offset.X, nodeZ = -(50.2 + fd.Offset.Y);
        bool nodeOk = cols.Any(c => Math.Abs(c.X - nodeX) < Tol && Math.Abs(c.Z - nodeZ) < Tol && Math.Abs(c.Width - 0.4) < Tol);
        bool foreignOk = Math.Abs(fd.MaxX - fd.MinX - 23.5) < Tol && Math.Abs(fd.MaxY - fd.MinY - 16.0) < Tol && fd.UnitScale == 0.001 &&
                         cols.Count == 6 && cols.Count(c => c.EffectiveProfile == ProfileTypes.Circle) == 2 && nodeOk &&
                         walls.Count == 2 && walls.All(w => Math.Abs(w.Depth - 0.3) < Tol && Math.Abs(w.Width - 9.4) < Tol) &&
                         fl.Axes.Count == 5 && fl.Axes.Any(a => a.Label == "Б" && !a.IsNumeric);
        if (!foreignOk) failures++;
        report.AppendLine(foreignOk
            ? "  OK: мм → м (×0.001), центрирование, 6 колонн (2 круглые Ø500), 2 стены 9400×300 из спаренных линий, 5 осей с марками (кириллица 1251)"
            : $"  ОШИБКА распознавания чужого DXF: колонн {cols.Count}, стен {walls.Count}, осей {fl.Axes.Count}, узел 1/А {(nodeOk ? "OK" : "нет")}");

        // ── Режим Б: создание по клику на подложке.
        report.AppendLine().AppendLine("═══ DXF: создание по клику (режим Б) ═══");
        var pickOptions = new DxfLiftOptions("DXF · pick", 0, 3.0, 0);
        var wallLine = fd.Contours.First(c => c.Category == DxfLayerCategory.Wall && !c.Closed);
        var mid = new DxfPoint((wallLine.Points[0].X + wallLine.Points[1].X) / 2, (wallLine.Points[0].Y + wallLine.Points[1].Y) / 2);
        var (madeWall, _) = CadDxfImporterService.CreateFromContour(fd, wallLine, mid, CadDxfImporterService.MakeWall, pickOptions, []);
        var circle = fd.Contours.First(c => c.IsCircle && c.Category == DxfLayerCategory.Column);
        var (madeColumn, _) = CadDxfImporterService.CreateFromContour(fd, circle, circle.Points[0], CadDxfImporterService.MakeColumn, pickOptions, []);
        var square = fd.Contours.First(c => c.Closed && !c.IsCircle && c.Category == DxfLayerCategory.Column);
        var (madeSlab, _) = CadDxfImporterService.CreateFromContour(fd, square, square.Center, CadDxfImporterService.MakeSlab, pickOptions, []);
        var (madeOpening, _) = madeWall.Count > 0
            ? CadDxfImporterService.CreateFromContour(fd, wallLine, mid, CadDxfImporterService.MakeOpening, pickOptions, madeWall)
            : (new List<ArchitecturalElement>(), "нет стены");
        bool pickOk = madeWall is [{ Type: ElementTypes.Wall } w0] && Math.Abs(w0.Depth - 0.3) < Tol && Math.Abs(w0.Width - 9.4) < Tol && Math.Abs(w0.Height - 3.0) < Tol &&
                      madeColumn is [{ Type: ElementTypes.Column } c0] && c0.EffectiveProfile == ProfileTypes.Circle && Math.Abs(c0.Width - 0.5) < Tol &&
                      madeSlab is [{ Type: ElementTypes.Slab } s0] && Math.Abs(s0.Width - 0.4) < Tol && Math.Abs(s0.BaseY - 3.0) < Tol &&
                      madeOpening is [{ Type: ElementTypes.WallOpening } o0] && o0.HostElementId == madeWall[0].Id;
        if (!pickOk) failures++;
        report.AppendLine(pickOk
            ? "  OK: стена по паре линий 9400×300 (H 3000), колонна Ø500 по окружности, плита по контуру на отметке +3,000, проём в созданной стене (HostElementId)"
            : $"  ОШИБКА: стена {string.Join(";", madeWall.Select(e => $"{e.Width:0.###}×{e.Depth:0.###}×{e.Height:0.###}"))}, " +
              $"колонна {string.Join(";", madeColumn.Select(e => $"{e.EffectiveProfile} {e.Width:0.###}"))}, " +
              $"плита {string.Join(";", madeSlab.Select(e => $"{e.Width:0.###} @ {e.BaseY:0.###}"))}, " +
              $"проём {string.Join(";", madeOpening.Select(e => $"{e.Type} → {e.HostElementId}"))}");

        // ── Section Box: честное отсечение и площадь заливки среза.
        report.AppendLine().AppendLine("═══ Section Box ═══");
        failures += CheckSectionBox(elements, groups, report);

        // ── Экспресс-расчёт, пироги, коллизии, ведомость стали.
        report.AppendLine().AppendLine("═══ Экспресс-расчёт конструктива (КЭ) ═══");
        failures += CheckStructuralAnalysis(elements, groups, report);
        report.AppendLine().AppendLine("═══ Расчётная схема: IAnalyticalModel, стержни, пластины, узлы ═══");
        failures += CheckAnalyticalModel(report);
        report.AppendLine().AppendLine("═══ Геометрический слой: BimMesh, IGeometryElement, ядро без HelixToolkit/SharpDX ═══");
        failures += CheckGeometryLayer(report);
        report.AppendLine().AppendLine("═══ Транзакции и журнал изменений: дельты вместо снимков, 1000 × Undo/Redo, DocumentChanged ═══");
        failures += CheckTransactions(report);
        report.AppendLine().AppendLine("═══ Многослойные конструкции: IfcMaterialLayerSet Round-Trip ═══");
        failures += await CheckLayersRoundTripAsync(directory, report);
        report.AppendLine().AppendLine("═══ Clash Detection ═══");
        failures += CheckClashDetection(report);
        report.AppendLine().AppendLine("═══ Ведомость расхода стали ═══");
        failures += CheckSteel(elements, groups, report);

        // ── Тактильное моделирование (Shapr3D): Push/Pull, OSnap, мультивыделение, массивы, инсоляция.
        report.AppendLine().AppendLine("═══ Push/Pull: торцевые ручки с неподвижным противоположным торцом ═══");
        failures += CheckPushPull(report);
        report.AppendLine().AppendLine("═══ SmartSnapService (OSnap) ═══");
        failures += CheckSmartSnap(report);
        report.AppendLine().AppendLine("═══ Мультивыделение и пакетная правка ═══");
        failures += CheckMultiSelection(report);
        report.AppendLine().AppendLine("═══ Массив и тиражирование этажа ═══");
        failures += CheckArrays(report);
        report.AppendLine().AppendLine("═══ Инсоляция и падающие тени ═══");
        failures += CheckSolar(report);

        // ── Финальная полировка: групповой поворот, вершины контура, коллизии прогулки, регион и СанПиН, DXF, зачистка.
        report.AppendLine().AppendLine("═══ Групповой поворот вокруг общего центра (Group Pivot Rotation) ═══");
        failures += CheckGroupRotation(report);
        report.AppendLine().AppendLine("═══ Вершины контура Polygon2D (Vertex Push/Pull) ═══");
        failures += CheckContourVertices(report);
        report.AppendLine().AppendLine("═══ Коллизии прогулки (сфера R = 0.25 м) ═══");
        failures += CheckWalkCollision(report);
        report.AppendLine().AppendLine("═══ Регион строительства и инсоляция по СанПиН 1.2.3685-21 ═══");
        failures += CheckInsolation(report);
        report.AppendLine().AppendLine("═══ Окно верификации плоского DXF ═══");
        failures += await CheckDxfVerificationAsync(directory, report);
        report.AppendLine().AppendLine("═══ Зачистка: локализация чата, пресеты ═══");
        failures += CheckCleanup(report);

        // ── Листы ГОСТ.
        report.AppendLine().AppendLine("═══ Рабочие чертежи ГОСТ (PDF) ═══");
        var box = new SectionBox(-12, 12, -1.5, 10, -9, 0.05);
        var requests = new (string File, GostDrawingRequest Request)[]
        {
            ("gost_frame_A3.pdf", new GostDrawingRequest(GostSheetFormat.A3, GostDrawingView.FrameScheme, elements, groups, axes, "Самопроверка · каркасный корпус", FrameGroup, 0, null, null)),
            ("gost_plan_A3.pdf", new GostDrawingRequest(GostSheetFormat.A3, GostDrawingView.FloorPlan, elements, groups, axes, "Самопроверка · каркасный корпус", FrameGroup, 0, null, null)),
            ("gost_section_A3.pdf", new GostDrawingRequest(GostSheetFormat.A3, GostDrawingView.Section, elements, groups, axes, "Самопроверка · каркасный корпус", FrameGroup, 0, box, SectionFace.MaxZ)),
            ("gost_frame_A2.pdf", new GostDrawingRequest(GostSheetFormat.A2, GostDrawingView.FrameScheme, elements, groups, axes, "Самопроверка · каркасный корпус", FrameGroup, 1, null, null, 50))
        };
        foreach (var (file, request) in requests)
        {
            string pdfPath = Path.Combine(directory, file);
            var result = GostDrawingPdfExporter.Export(request, pdfPath);
            string png = Path.ChangeExtension(pdfPath, ".png");
            await RenderPdfAsync(pdfPath, png, 3200);
            report.AppendLine($"  {file}: «{result.SheetTitle}», М 1:{result.ScaleDenominator}, строк спецификации {result.SpecRows}, {new FileInfo(pdfPath).Length / 1024} КБ → {Path.GetFileName(png)}");
            foreach (var d in result.Details) report.AppendLine("    " + d);
            // Hidden Line Occlusion: на разрезе линии заднего плана обрезаны сечениями и ни одна не проходит по заливке.
            if (request.View == GostDrawingView.Section)
            {
                bool occluded = result.Occlusion is { Verified: true, ClippedSegments: > 0 };
                if (!occluded) failures++;
                report.AppendLine($"    {(occluded ? "OK" : "ОШИБКА")}: окклюзия разреза — обрезано линий заднего плана {result.Occlusion?.ClippedSegments ?? 0} из {result.Occlusion?.BeyondSegments ?? 0}, " +
                                  $"видимые участки не пересекают сечения: {(result.Occlusion?.Verified == true ? "да" : "нет")}");
            }
        }

        report.AppendLine().AppendLine("═══ Font Subsetting и альбом ГОСТ (3 листа PDF) ═══");
        failures += await CheckAlbumAndFontAsync(directory, report);

        // ── Тени по реальному контуру, прямая компиляция без ИИ и векторный слой PDF.
        report.AppendLine().AppendLine("═══ Тени полигональных (Г-образных) плит ═══");
        failures += CheckPolygonShadow(report);
        report.AppendLine().AppendLine("═══ Прямая компиляция 3D по параметрам (без ИИ) ═══");
        var compiled = CheckDirectCompile(report, ref failures);
        report.AppendLine().AppendLine("═══ PdfNativeContentExtractor: Round-Trip листов ГОСТ и чужого PDF ═══");
        failures += await CheckPdfExtractorAsync(directory, report, compiled);

        // ── Мультиагентное конструкторское бюро: плагины-проверщики, согласование с ГИПом, маршрутизация Multi-LLM.
        report.AppendLine().AppendLine("═══ Совет КБ: плагины-проверщики BimVerificationPipeline ═══");
        failures += CheckVerifierPlugins(report);
        report.AppendLine().AppendLine("═══ Совет КБ: согласование решений с ГИПом (User-in-the-Loop) ═══");
        failures += await CheckCouncilConsultationAsync(report);
        report.AppendLine().AppendLine("═══ Совет КБ: маршрутизация ролей Multi-LLM ═══");
        failures += await CheckCouncilRoutingAsync(report);

        // ── Интеллектуальное ядро на локальном ИИ: рассуждения <think>, Ollama, дефекты, самоисправление.
        report.AppendLine().AppendLine("═══ Рассуждения <think> (deepseek-r1) и разбор JSON ═══");
        failures += CheckReasoningSeparation(report);
        report.AppendLine().AppendLine("═══ Ollama: потоковый провайдер (заглушка NDJSON), /api/tags, маршрутизация ролей ═══");
        failures += await CheckOllamaProviderAsync(report);
        report.AppendLine().AppendLine("═══ Ollama: живой запрос (AIA_DEBUG_OLLAMALIVE) ═══");
        failures += await CheckOllamaLiveAsync(report);
        report.AppendLine().AppendLine("═══ BimVerificationPipeline: сцена с намеренными ошибками и ProposedFixOptions ═══");
        failures += CheckDefectScenePipeline(report);
        report.AppendLine().AppendLine("═══ Совет КБ: цикл самоисправления «ИИ ↔ C#-движок» ═══");
        failures += await CheckSelfCorrectionLoopAsync(report);

        // ── Архитектурная подача: 3D-рулетка, закреплённые размеры (Undo, .aia, DXF, ГОСТ), White Clay и альфа рендера 4K.
        report.AppendLine().AppendLine("═══ 3D-рулетка, PinnedDimensions (DXF DIMENSIONS, листы ГОСТ), White Clay, рендер 4K ═══");
        failures += await CheckMeasurementAsync(directory, report);

        // ── Финальный аудит: детерминизм параллельного КЭ-расчёта, Ollama (адрес, задание Windows), устойчивость конвейера, Undo осей.
        report.AppendLine().AppendLine("═══ Финальный аудит: параллельный КЭ-расчёт, Ollama, устойчивость Совета, Undo осей ═══");
        failures += await CheckProductionAuditAsync(report);

        // ── Модули главного окна: ведомость объёмов, наборы модулей по уровню опыта, схема 3D-армирования.
        report.AppendLine().AppendLine("═══ Модули: ведомость объёмов, уровни опыта, 3D-армирование ═══");
        failures += CheckModules(report);

        // ── Блок девелопера: прайс-листы поставщиков, доказательная смета, паспорт расчёта по СП, материалы и нормативы.
        report.AppendLine().AppendLine("═══ Блок девелопера: прайсы поставщиков, доказательная смета, паспорт расчёта по СП, материалы и γn ═══");
        failures += await CheckDeveloperBlockAsync(directory, report);

        // ── Блок девелопера 2: план → BIM, шлюз достоверности с листом правок, вложения запроса без буфера обмена.
        report.AppendLine().AppendLine("═══ Блок девелопера 2: план → BIM, проверка достоверности и лист правок, авто-снимок и вложения ═══");
        failures += await CheckDeveloperBlock2Async(directory, report);

        // ── Блок девелопера 3–4: библиотеки ГОСТ/СП, двухпроходная доводка и перегородки, ручное моделирование, журнал и версии, справка.
        report.AppendLine().AppendLine("═══ Блок девелопера 3–4: библиотека ГОСТ/СП, доводка каркаса, ручное моделирование, история и версии, справочный центр ═══");
        failures += await CheckDeveloperBlock34Async(directory, report);

        // ── Волна 1: чистый старт, «Начальный экран», боковой док Коллоквиума, логика проёмов, полые кровли, конструктор лестниц.
        report.AppendLine().AppendLine("═══ Волна 1: чистый старт и начальный экран, неперекрывающий Коллоквиум, честные проёмы, полые кровли, конструктор лестниц ═══");
        failures += await CheckWave1Async(report);
        ReinforcementCalculator.StrictMinimum = strictMinimum;

        report.Insert(0, failures == 0 ? "ИТОГ: УСПЕХ — расхождений нет\n\n" : $"ИТОГ: ОШИБОК {failures}\n\n");
        string reportPath = Path.Combine(directory, "selftest.txt");
        await File.WriteAllTextAsync(reportPath, report.ToString(), new UTF8Encoding(true));
        return reportPath;
    }

    /// <summary>Сравнение элемента после Round-Trip: тип, этаж, положение, габариты, поворот, профиль, контур, хост.</summary>
    private static IEnumerable<string> Compare(ArchitecturalElement a, ArchitecturalElement b, List<ArchitecturalElement> srcAll, List<ArchitecturalElement> dstAll, bool byId,
        Dictionary<ArchitecturalElement, ArchitecturalElement>? match = null)
    {
        if (a.Type != b.Type) yield return $"тип {a.Type} → {b.Type}";
        if (a.LevelIndex != b.LevelIndex) yield return $"этаж {a.LevelIndex} → {b.LevelIndex}";
        if (byId && a.BuildingGroupId != b.BuildingGroupId) yield return $"постройка {a.BuildingGroupId} → {b.BuildingGroupId}";
        if (byId && a.Material != b.Material) yield return $"материал {a.Material} → {b.Material}";
        if (Math.Abs(a.Y - b.Y) > Tol || Math.Abs(a.Height - b.Height) > Tol) yield return $"отметки Y {a.Y:0.####}/{a.Height:0.####} → {b.Y:0.####}/{b.Height:0.####}";

        // Контур в плане (мировые координаты) — сравнение без учёта начала обхода и эквивалентных W/D/поворота.
        var oa = Outline(a);
        var ob = Outline(b);
        double dev = oa.Max(p => ob.Min(q => Math.Sqrt((p.X - q.X) * (p.X - q.X) + (p.Z - q.Z) * (p.Z - q.Z))));
        double dev2 = ob.Max(p => oa.Min(q => Math.Sqrt((p.X - q.X) * (p.X - q.X) + (p.Z - q.Z) * (p.Z - q.Z))));
        if (Math.Max(dev, dev2) > Tol) yield return $"контур в плане отклоняется на {Math.Max(dev, dev2) * 1000:0.###} мм (W×D {a.Width:0.###}×{a.Depth:0.###}@{a.RotationY:0.#}° → {b.Width:0.###}×{b.Depth:0.###}@{b.RotationY:0.#}°)";
        if (Math.Abs(a.X - b.X) > Tol || Math.Abs(a.Z - b.Z) > Tol)
        {
            if (a.Polygon2D.Count < 3) yield return $"центр ({a.X:0.####}; {a.Z:0.####}) → ({b.X:0.####}; {b.Z:0.####})";
        }

        if (ElementTypes.IsProfiledType(a.Type))
        {
            if (a.EffectiveProfile != b.EffectiveProfile) yield return $"профиль {a.EffectiveProfile} → {b.EffectiveProfile}";
            var (ba, ha) = a.Type == ElementTypes.Beam ? (a.Depth, a.Height) : (a.Width, a.Depth);
            var (bb, hb) = b.Type == ElementTypes.Beam ? (b.Depth, b.Height) : (b.Width, b.Depth);
            var ta = a.ProfileThicknesses(ba, ha);
            var tb = b.ProfileThicknesses(bb, hb);
            if (a.EffectiveProfile is ProfileTypes.Pipe or ProfileTypes.IBeam && Math.Abs(ta.Wall - tb.Wall) > Tol) yield return $"толщина стенки/полки {ta.Wall:0.####} → {tb.Wall:0.####}";
            if (a.EffectiveProfile == ProfileTypes.IBeam && Math.Abs(ta.Web - tb.Web) > Tol) yield return $"толщина стенки двутавра {ta.Web:0.####} → {tb.Web:0.####}";
            if (a.Type == ElementTypes.Beam && Math.Abs(a.Width - b.Width) > Tol) yield return $"длина балки {a.Width:0.####} → {b.Width:0.####}";
        }
        if (a.Type == ElementTypes.Roof && RoofShapes.Normalize(a.RoofShape) != RoofShapes.Normalize(b.RoofShape)) yield return $"форма кровли {a.RoofShape} → {b.RoofShape}";

        if (a.HostElementId is { } hostA)
        {
            var srcHost = srcAll.FirstOrDefault(e => e.Id == hostA);
            string? expected = byId ? hostA : srcHost is not null && match is not null && match.TryGetValue(srcHost, out var mh) ? mh.Id : null;
            if (b.HostElementId != expected) yield return $"хост {hostA} → {b.HostElementId ?? "нет"}";
        }
    }

    /// <summary>
    /// Отсечение плоскостью z = 0 (середина здания): площадь заливки среза колонны 400×600, стены и плиты должна совпасть
    /// с их сечением, а ни одна вершина отсечённой геометрии не выходит за коробку.
    /// </summary>
    private static int CheckSectionBox(List<ArchitecturalElement> elements, List<BuildingGroup> groups, StringBuilder report)
    {
        int failures = 0;
        var box = new SectionBox(-20, 20, -5, 20, -20, 0);
        var group = groups[0];
        foreach (var (name, expected) in new[] { ("Колонна 1-22", 0.0), ("Плита перекрытия 1", 18.8 * 0.2), ("Стена по оси 1", 0.3 * 2.65) })
        {
            var e = elements.First(x => x.Name == name);
            var geometry = ElementGeometryBuilder.Build(e, SceneStateService.OpeningsFor(e, elements), new GeometryContext(SceneStateService.AllLevels, group.OriginX, group.OriginZ));
            var clipped = SectionBoxClipper.Clip(geometry, box);
            double area = 0;
            float maxZ = float.MinValue;
            if (clipped?.Caps is { } caps)
                for (int i = 0; i + 2 < caps.TriangleIndices.Count; i += 3)
                {
                    var a = caps.Positions[caps.TriangleIndices[i]];
                    var b = caps.Positions[caps.TriangleIndices[i + 1]];
                    var c = caps.Positions[caps.TriangleIndices[i + 2]];
                    area += Models.Geometry.BimMath.Length(Models.Geometry.BimMath.Cross(b - a, c - a)) / 2;
                }
            foreach (var part in clipped?.Parts ?? [])
                foreach (var p in part.Mesh.Positions) maxZ = Math.Max(maxZ, p.Z);
            // Колонна 1-22 на оси Б (z = 0) — 400×600, повёрнута на 90°: в плоскости z = 0 сечение 0.6 × 2.65.
            double want = name == "Колонна 1-22" ? Math.Max(e.Width, e.Depth) * e.Height : expected;
            bool ok = clipped is not null && Math.Abs(area - want) < 1e-3 && maxZ <= 1e-4;
            if (!ok) failures++;
            report.AppendLine($"  {(ok ? "OK" : "ОШИБКА")}: «{name}» — заливка среза {area:0.0000} м² (ожидалось {want:0.0000}), max Z отсечённой геометрии {maxZ:0.0000}");
        }
        return failures;
    }

    // ═════════════════════════════ КЭ-расчёт ═════════════════════════════

    /// <summary>
    /// Каркас: N и K_исп колонн нарастают сверху вниз, балки и плиты получают моменты и прогибы, время пересчёта &lt; 10 мс.
    /// Башня 30 этажей: ядро и диагрид воспринимают нагрузку, узел ядра на плите стилобата найден как продавливание.
    /// </summary>
    private static int CheckStructuralAnalysis(List<ArchitecturalElement> elements, List<BuildingGroup> groups, StringBuilder report)
    {
        int failures = 0;
        var loads = new StructuralLoadSettings();
        var a = Median(elements, groups, loads);
        report.AppendLine($"  Каркас: элементов с результатом {a.Elements.Count}, узлов продавливания {a.PunchingNodes.Count}, время {a.ElapsedMilliseconds:0.00} мс (медиана 5 прогонов)");
        report.AppendLine($"    Профиль: {a.Profile}");
        bool fast = a.ElapsedMilliseconds < 10;
        if (!fast) failures++;
        report.AppendLine($"  {(fast ? "OK" : "ОШИБКА")}: время пересчёта {a.ElapsedMilliseconds:0.00} мс (норматив < 10 мс)");

        var columns = elements.Where(e => e.Type == ElementTypes.Column).Select(e => (e, r: a.For(e.Id))).Where(x => x.r is not null).ToList();
        foreach (var level in columns.GroupBy(x => x.e.LevelIndex).OrderBy(g => g.Key))
            report.AppendLine($"    Колонны этажа {level.Key + 1}: N ср. {level.Average(x => x.r!.AxialForce):0} кН, макс. {level.Max(x => x.r!.AxialForce):0} кН; " +
                              $"K_исп ср. {level.Average(x => x.r!.Utilization):0.00}, макс. {level.Max(x => x.r!.Utilization):0.00}");
        double nBottom = columns.Where(x => x.e.LevelIndex == 0).Average(x => x.r!.AxialForce);
        double nTop = columns.Where(x => x.e.LevelIndex == 2).Average(x => x.r!.AxialForce);
        double kBottom = columns.Where(x => x.e.LevelIndex == 0 && x.e.Material == "concrete").Average(x => x.r!.Utilization);
        double kTop = columns.Where(x => x.e.LevelIndex == 2).Average(x => x.r!.Utilization);
        bool gradient = nBottom > 2 * nTop && kBottom > kTop;
        if (!gradient) failures++;
        report.AppendLine($"  {(gradient ? "OK" : "ОШИБКА")}: нагрузка нарастает сверху вниз — N₁ = {nBottom:0} кН > N₃ = {nTop:0} кН, K₁ = {kBottom:0.00} > K₃ = {kTop:0.00}");

        foreach (var type in new[] { ElementTypes.Beam, ElementTypes.Slab, ElementTypes.Roof, ElementTypes.Foundation, ElementTypes.Wall })
        {
            var list = elements.Where(e => e.Type == type).Select(e => (e, r: a.For(e.Id))).Where(x => x.r is not null).ToList();
            if (list.Count == 0) continue;
            var worst = list.MaxBy(x => x.r!.Utilization);
            report.AppendLine($"    {type}: {list.Count} шт., K_исп {list.Min(x => x.r!.Utilization):0.00}…{worst.r!.Utilization:0.00}");
            foreach (var (e, r) in list.Where(x => type != ElementTypes.Beam || x == worst))
                report.AppendLine($"      «{e.Name}»: M = {r!.Moment:0.0} кН·м, Q = {r.Shear:0.0} кН, f = {r.DeflectionMm:0.0}/{r.DeflectionLimitMm:0.0} мм, " +
                                  $"L = {r.Span:0.00} м{(r.IsCantilever ? " (консоль)" : "")}, q = {r.DesignLoad:0.0}, K {r.Utilization:0.00} ({r.Governing})");
        }
        // Сходимость по сетке: K_исп плит и балок при 220 и 480 точках на плиту отличаются не более чем на 10 % (точность экспресс-метода).
        var fine = StructuralAnalysisService.Analyze(elements, groups, loads, slabSamples: 480);
        var plates = elements.Where(e => e.Type is ElementTypes.Slab or ElementTypes.Roof or ElementTypes.Beam).Select(e => (e, c: a.For(e.Id), f: fine.For(e.Id)))
            .Where(x => x.c is not null && x.f is not null).ToList();
        double maxDiff = plates.Max(x => Math.Abs(x.c!.Utilization - x.f!.Utilization) / Math.Max(0.05, x.f.Utilization));
        var worstDiff = plates.MaxBy(x => Math.Abs(x.c!.Utilization - x.f!.Utilization) / Math.Max(0.05, x.f.Utilization));
        bool converged = maxDiff <= 0.10;
        if (!converged) failures++;
        report.AppendLine($"  {(converged ? "OK" : "ОШИБКА")}: сходимость по сетке плит (220 / 480 точек): макс. расхождение K {maxDiff * 100:0.0} % (допуск 10 %) " +
                          $"(«{worstDiff.e.Name}»: {worstDiff.c!.Utilization:0.000} / {worstDiff.f!.Utilization:0.000}, L {worstDiff.c.Span:0.00} / {worstDiff.f.Span:0.00} м, " +
                          $"q {worstDiff.c.DesignLoad:0.0} / {worstDiff.f.DesignLoad:0.0} кН/м, M {worstDiff.c.Moment:0.0} / {worstDiff.f.Moment:0.0}, Q {worstDiff.c.Shear:0.0} / {worstDiff.f.Shear:0.0}, " +
                          $"f {worstDiff.c.DeflectionMm:0.0} / {worstDiff.f.DeflectionMm:0.0}, {worstDiff.c.Governing} / {worstDiff.f.Governing})");

        var beams = elements.Where(e => e.Type == ElementTypes.Beam && e.Material == "concrete").Select(e => a.For(e.Id)).Where(r => r is not null).ToList();
        bool beamsOk = beams.Count > 0 && beams.All(r => r!.Moment > 1 && r.DeflectionMm > 0 && double.IsFinite(r.Utilization));
        if (!beamsOk) failures++;
        report.AppendLine($"  {(beamsOk ? "OK" : "ОШИБКА")}: ж/б балки — моменты M = qL²/12 и прогибы вычислены ({beams.Count} шт.)");
        foreach (var node in a.PunchingNodes.Take(4))
            report.AppendLine($"    Продавливание «{elements.First(e => e.Id == node.ColumnId).Name}» → «{elements.First(e => e.Id == node.SlabId).Name}»: " +
                              $"F = {node.Force:0} кН, F_b,ult = {node.Capacity:0} кН (u = {node.Perimeter:0.00} м, h0 = {node.H0:0.000} м), {node.Ratio:0.00} — {node.Status}");
        foreach (var (g, (wind, seismic)) in a.BaseShear) report.AppendLine($"    Сдвиг в основании «{g}»: ветер {wind:0} кН, сейсмика {seismic:0} кН");
        bool finite = a.Elements.Values.All(r => double.IsFinite(r.Utilization) && r.Utilization >= 0);
        if (!finite) failures++;

        // 6 баллов — сейсмический расчёт не выполняется (СП 14.13330, п. 4.1); 9 баллов против 7 — ускорение A ×4.
        bool noSeismic6 = a.BaseShear.Values.All(v => v.Seismic == 0);
        if (!noSeismic6) failures++;
        report.AppendLine($"  {(noSeismic6 ? "OK" : "ОШИБКА")}: 6 баллов — сейсмические силы не прикладываются (СП 14.13330, п. 4.1)");
        a = StructuralAnalysisService.Analyze(elements, groups, new StructuralLoadSettings { Seismicity = 7 });
        var seismic9 = StructuralAnalysisService.Analyze(elements, groups, new StructuralLoadSettings { Seismicity = 9 });
        // Диафрагмы (стены в своей плоскости) жёстче колонн на порядки и воспринимают почти весь сдвиг — проверяется их K_исп.
        var walls = elements.Where(e => e.Type == ElementTypes.Wall && e.LevelIndex == 0).ToList();
        double k6 = walls.Max(e => a.For(e.Id)!.Utilization), k9 = walls.Max(e => seismic9.For(e.Id)!.Utilization);
        double q6 = walls.Max(e => a.For(e.Id)!.Shear), q9 = walls.Max(e => seismic9.For(e.Id)!.Shear);
        bool seismicOk = seismic9.BaseShear.Values.Sum(v => v.Seismic) > a.BaseShear.Values.Sum(v => v.Seismic) * 3.9 && k9 > k6 * 1.3 && q9 > q6 * 2.5;
        if (!seismicOk) failures++;
        report.AppendLine($"  {(seismicOk ? "OK" : "ОШИБКА")}: 7 → 9 баллов — сейсмический сдвиг {a.BaseShear.Values.Sum(v => v.Seismic):0} → {seismic9.BaseShear.Values.Sum(v => v.Seismic):0} кН, " +
                          $"Q стен 1 эт. {q6:0} → {q9:0} кН, K_исп стен {k6:0.00} → {k9:0.00}");
        a = Median(elements, groups, loads);

        // Башня 30 этажей.
        var tower = PresetScenesService.Create(PresetScenesService.Tower, LocalizationService.Russian);
        var t = Median(tower.Elements, tower.Groups, loads);
        var core = tower.Elements.First(e => e.Type == ElementTypes.Core);
        var coreR = t.For(core.Id);
        var podiumColumns = tower.Elements.Where(e => e.Type == ElementTypes.Column).Select(e => t.For(e.Id)).Where(r => r is not null).ToList();
        report.AppendLine($"  Башня: элементов с результатом {t.Elements.Count}, время {t.ElapsedMilliseconds:0.00} мс (медиана 5 прогонов), узлов продавливания {t.PunchingNodes.Count} " +
                          $"(крит. {t.PunchingCritical}, арм. {t.PunchingReinforce})");
        report.AppendLine($"    Профиль: {t.Profile}");
        foreach (var e in tower.Elements.Where(e => e.Type is ElementTypes.Core or ElementTypes.Diagrid or ElementTypes.CantileverSlab or ElementTypes.Slab or ElementTypes.Foundation))
            if (t.For(e.Id) is { } r)
                report.AppendLine($"    «{e.Name}»: N = {r.AxialForce:0} кН, M = {r.Moment:0} кН·м, L = {r.Span:0.00} м{(r.IsCantilever ? " (консоль)" : "")}, q = {r.DesignLoad:0.0}, " +
                                  $"f = {r.DeflectionMm:0}/{r.DeflectionLimitMm:0} мм, K_исп {r.Utilization:0.00} ({r.Governing}{(r.Floor > 0 ? $", этаж +{r.Floor}" : "")})");
        report.AppendLine($"    Колонны стилобата: K_исп {podiumColumns.Min(r => r!.Utilization):0.00}…{podiumColumns.Max(r => r!.Utilization):0.00}");
        foreach (var (g, (wind, seismic)) in t.BaseShear) report.AppendLine($"    Сдвиг в основании «{g}»: ветер {wind:0} кН, сейсмика {seismic:0} кН");
        bool towerOk = coreR is { AxialForce: > 50000 } && t.PunchingNodes.Count > 0 && t.ElapsedMilliseconds < 10 &&
                       podiumColumns.Max(r => r!.Utilization) > podiumColumns.Min(r => r!.Utilization) + 0.2;
        if (!towerOk) failures++;
        report.AppendLine($"  {(towerOk ? "OK" : "ОШИБКА")}: ядро собирает N = {coreR?.AxialForce:0} кН с 30 этажей, градиент K колонн стилобата, узлы продавливания найдены, " +
                          $"время {t.ElapsedMilliseconds:0.00} мс");
        return failures;
    }

    /// <summary>Пять прогонов расчёта (после прогрева JIT): результат с медианным временем.</summary>
    private static StructuralAnalysisResult Median(IReadOnlyList<ArchitecturalElement> elements, IReadOnlyList<BuildingGroup> groups, StructuralLoadSettings loads)
    {
        StructuralAnalysisService.Analyze(elements, groups, loads);
        var runs = Enumerable.Range(0, 5).Select(_ => StructuralAnalysisService.Analyze(elements, groups, loads)).OrderBy(r => r.ElapsedMilliseconds).ToList();
        return runs[2];
    }

    // ═════════════════════════════ Пироги и IFC ═════════════════════════════

    /// <summary>Пироги плиты, стены, кровли и фундамента → IFC (IfcMaterialLayerSet) → импорт: слои и толщины совпадают до 0.01 мм.</summary>
    private static async Task<int> CheckLayersRoundTripAsync(string directory, StringBuilder report)
    {
        int failures = 0;
        var (elements, groups, axes) = CreateFrameScene();
        var assignments = new (string Name, string Preset)[]
        {
            ("Плита перекрытия 1", MaterialLayers.PresetFloor),
            ("Стена по оси А", MaterialLayers.PresetExteriorWall),
            ("Плоская кровля", MaterialLayers.PresetFlatRoof),
            ("Фундаментная плита", MaterialLayers.PresetFoundation)
        };
        foreach (var (name, preset) in assignments)
        {
            var e = elements.First(x => x.Name == name);
            e.Layers = MaterialLayers.PresetFor(e, preset);
            MaterialLayers.ApplyThickness(e, SceneStateService.OpeningsFor(e, elements).Concat(elements.Where(o => o.HostElementId == e.Id)).Distinct());
            report.AppendLine($"  «{name}»: {MaterialLayers.Summary(e.Layers)}; толщина элемента {MaterialLayers.ElementThickness(e) * 1000:0.##} мм");
        }
        string path = Path.Combine(directory, "layers.ifc");
        var export = await new CadExportService().ExportAsync(CadExportFormat.Ifc, path, elements.Select(e => e.Clone()).ToList(), groups, "Самопроверка · пироги", axes);
        string text = await File.ReadAllTextAsync(path);
        int sets = CountOf(text, "IFCMATERIALLAYERSET("), usages = CountOf(text, "IFCMATERIALLAYERSETUSAGE("), layerCount = CountOf(text, "IFCMATERIALLAYER(");
        report.AppendLine($"  Экспорт: IFCMATERIALLAYERSET × {sets}, IFCMATERIALLAYERSETUSAGE × {usages}, IFCMATERIALLAYER × {layerCount}; " +
                          $"{export.Details.FirstOrDefault(d => d.StartsWith("Многослойные", StringComparison.Ordinal))}");
        var imported = await new CadIfcImporterService().ImportAsync(path);
        report.AppendLine("  " + imported.Report.First(r => r.StartsWith("Многослойные", StringComparison.Ordinal)));
        foreach (var (name, _) in assignments)
        {
            var src = elements.First(x => x.Name == name);
            var dst = imported.Elements.FirstOrDefault(x => x.Id == src.Id);
            var problems = new List<string>();
            if (dst is null) problems.Add("элемент не импортирован");
            else
            {
                if (dst.Layers.Count != src.Layers.Count) problems.Add($"слоёв {src.Layers.Count} → {dst.Layers.Count}");
                for (int i = 0; i < Math.Min(src.Layers.Count, dst.Layers.Count); i++)
                {
                    var (ls, ld) = (src.Layers[i], dst.Layers[i]);
                    if (ls.Name != ld.Name) problems.Add($"слой {i + 1}: «{ls.Name}» → «{ld.Name}»");
                    if (Math.Abs(ls.ThicknessMm - ld.ThicknessMm) > 0.01) problems.Add($"слой {i + 1}: {ls.ThicknessMm} → {ld.ThicknessMm} мм");
                    if (ls.Role != ld.Role) problems.Add($"слой {i + 1}: назначение {ls.Role} → {ld.Role}");
                    if (Math.Abs(ls.DensityKgM3 - ld.DensityKgM3) > 1e-6) problems.Add($"слой {i + 1}: плотность {ls.DensityKgM3} → {ld.DensityKgM3}");
                }
                if (Math.Abs(MaterialLayers.ElementThickness(src) - MaterialLayers.ElementThickness(dst)) > Tol) problems.Add("толщина элемента");
            }
            if (problems.Count > 0) failures++;
            report.AppendLine(problems.Count == 0
                ? $"  OK: «{name}» — {dst!.Layers.Count} слоя(ёв): {string.Join(" | ", dst.Layers.Select(l => $"{l.Name} {l.ThicknessMm:0.##} мм ({l.Role})"))}"
                : $"  ОШИБКА: «{name}» — {string.Join("; ", problems)}");
        }
        bool exportOk = sets >= assignments.Length && usages >= assignments.Length;
        if (!exportOk) failures++;

        // Чужой IFC (Revit-стиль): IFC4-слои с направлением .NEGATIVE., без AIA_Parameters — назначение по названию.
        string foreign = "ISO-10303-21;\nHEADER;\nFILE_SCHEMA(('IFC4'));\nENDSEC;\nDATA;\n" +
                         "#1=IFCMATERIAL('Concrete C25/30',$,$);\n#2=IFCMATERIAL('Rockwool insulation',$,$);\n#3=IFCMATERIAL('Plaster finish',$,$);\n" +
                         "#4=IFCMATERIALLAYER(#1,200.,$,'Concrete C25/30',$,$,$);\n#5=IFCMATERIALLAYER(#2,120.,$,'Rockwool insulation',$,$,$);\n#6=IFCMATERIALLAYER(#3,15.,$,'Plaster finish',$,$,$);\n" +
                         "#7=IFCMATERIALLAYERSET((#4,#5,#6),'Wall build-up',$);\n#8=IFCMATERIALLAYERSETUSAGE(#7,.AXIS2.,.NEGATIVE.,0.,$);\nENDSEC;\nEND-ISO-10303-21;\n";
        var foreignLayers = CadIfcImporterService.FirstLayerSet(foreign);
        var roles = foreignLayers.Select(l => MaterialLayers.InferRole(l.Name)).ToList();
        bool foreignOk = foreignLayers.Count == 3 && foreignLayers[0].Name == "Plaster finish" && Math.Abs(foreignLayers[2].Thickness - 200) < 1e-9 &&
                         roles.SequenceEqual([MaterialLayerRole.Finish, MaterialLayerRole.Insulation, MaterialLayerRole.Structural]);
        if (!foreignOk) failures++;
        report.AppendLine($"  {(foreignOk ? "OK" : "ОШИБКА")}: чужой IFC4 (Revit, .NEGATIVE.): {string.Join(" | ", foreignLayers.Select((l, i) => $"{l.Name} {l.Thickness:0.##} ({roles[i]})"))} — порядок обращён, назначение по названию");
        return failures;
    }

    private static int CountOf(string text, string token)
    {
        int count = 0, i = 0;
        while ((i = text.IndexOf(token, i, StringComparison.Ordinal)) >= 0) { count++; i += token.Length; }
        return count;
    }

    // ═════════════════════════════ Коллизии ═════════════════════════════

    /// <summary>Тестовые коллизии: проём на колонне, отверстие на балке, марш без проёма в плите, дубликат колонны — поиск и автоисправление.</summary>
    private static int CheckClashDetection(StringBuilder report)
    {
        int failures = 0;
        var (elements, groups, axes) = CreateFrameScene();
        var scene = new SceneStateService();
        scene.LoadScene(elements, groups, recordUndo: false, clearHistory: true, axes: axes);

        var before = ClashDetectionService.Detect(scene.Elements.ToList());
        report.AppendLine($"  Исходный каркас: коллизий {before.Count}" + (before.Count > 0 ? ": " + string.Join("; ", before.Select(c => $"{c.Id} {c.CategoryTitle}")) : string.Empty));

        var wallA = scene.Elements.First(e => e.Name == "Стена по оси А");
        var opening = scene.AddWallOpening(wallA, 1.2, 1.4, 0.8);
        scene.SetTransform(opening, -3, opening.Y, opening.Z, opening.Width, opening.Height, opening.Depth, opening.RotationY, opening.LevelIndex, recordUndo: false);
        opening.Name = "Тест: проём на колонне";
        var slab = scene.Elements.First(e => e.Name == "Плита перекрытия 1");
        var hole = scene.AddSlabOpening(slab, 1.2, 1.2);
        scene.SetTransform(hole, 0, hole.Y, 0, hole.Width, hole.Height, hole.Depth, hole.RotationY, hole.LevelIndex, recordUndo: false);
        hole.Name = "Тест: отверстие на балке";
        var stairs = new ArchitecturalElement { Type = ElementTypes.Stairs, Name = "Тест: марш", BuildingGroupId = FrameGroup, Material = "concrete" };
        stairs.SetSize(1.2, 2.85, 3.0);
        stairs.SetPosition(-6.0, -0.02 + 2.85 / 2, 3.2);
        scene.AddElement(stairs, recordUndo: false);
        var source = scene.Elements.First(e => e.Name == "Колонна 2-11");
        var duplicate = scene.DuplicateElement(source, 0.02, 0);
        duplicate.Name = "Тест: дубликат колонны";

        var found = ClashDetectionService.Detect(scene.Elements.ToList());
        var expected = new (ClashCategory Category, string Id, string Title)[]
        {
            (ClashCategory.WallOpeningHitsColumn, opening.Id, "проём врезается в колонну"),
            (ClashCategory.SlabOpeningCutsFrame, hole.Id, "отверстие режет балку"),
            (ClashCategory.StairsWithoutOpening, stairs.Id, "марш без проёма в плите"),
            (ClashCategory.Duplicate, duplicate.Id, "дубликат колонны")
        };
        report.AppendLine($"  После внесения 4 тестовых коллизий найдено: {found.Count}");
        foreach (var c in found) report.AppendLine($"    {c.Id} [{(c.IsCritical ? "крит." : "предупр.")}] {c.CategoryTitle}: {c.Description} → {c.FixLabel}");
        foreach (var (category, id, title) in expected)
        {
            var item = found.FirstOrDefault(c => c.Category == category && (c.ElementAId == id || c.ElementBId == id));
            if (item is null)
            {
                failures++;
                report.AppendLine($"  ОШИБКА: не найдена коллизия «{title}»");
                continue;
            }
            string? result = ClashDetectionService.ApplyFix(scene, item);
            var after = ClashDetectionService.Detect(scene.Elements.ToList());
            bool resolved = result is not null && !after.Any(c => c.Category == category && (c.ElementAId == id || c.ElementBId == id || category == ClashCategory.StairsWithoutOpening && c.ElementAId == stairs.Id));
            if (!resolved) failures++;
            report.AppendLine($"  {(resolved ? "OK" : "ОШИБКА")}: «{title}» найдена ({item.Id}) и исправлена — {result ?? "исправление не применено"}");
        }
        var final = ClashDetectionService.Detect(scene.Elements.ToList());
        report.AppendLine($"  Осталось коллизий: {final.Count} (исходные коллизии каркаса не изменялись); Undo-шагов: {(scene.CanUndo ? "есть" : "нет")}");
        return failures;
    }

    // ═════════════════════════════ Сталь ═════════════════════════════

    private static int CheckSteel(List<ArchitecturalElement> elements, List<BuildingGroup> groups, StringBuilder report)
    {
        var loads = new StructuralLoadSettings();
        var a = StructuralAnalysisService.Analyze(elements, groups, loads);
        var spec = BimAnalyticsService.Calculate(elements, groups, 9500, 18000, key => key, a.UtilizationById, 85000, loads.Rebar, BimAnalyticsService.PunchingReinforcedIds(a));
        var site = spec.SiteTotal!;
        double ratio = site.Steel.Total / Math.Max(1e-6, spec.TotalConcrete);
        report.AppendLine($"  Бетон {spec.TotalConcrete:0.00} м³, сталь {site.Steel.Total:0} кг ({ratio:0} кг/м³): А240 {site.Steel.A240Total:0} кг, А400 {site.Steel.WorkingTotal:0} кг, С245 {site.Steel.PlatesTotal:0} кг; " +
                          $"стоимость {site.SteelCost:N0} ₽");
        report.AppendLine($"    А240: {string.Join(", ", site.Steel.A240.Select(p => $"Ø{p.Key} — {p.Value:0} кг"))}");
        report.AppendLine($"    А400: {string.Join(", ", site.Steel.Working.Select(p => $"Ø{p.Key} — {p.Value:0} кг"))}");
        foreach (var (key, s) in site.SteelByType) report.AppendLine($"    {key}: {s.Total:0} кг");
        bool ok = site.Steel.A240Total > 0 && site.Steel.WorkingTotal > 0 && site.Steel.PlatesTotal > 0 && ratio is > 40 and < 260 &&
                  site.SteelByType.ContainsKey("SteelColumns") && site.SteelByType.ContainsKey("SteelBeams") && site.SteelByType.ContainsKey("SteelSlabs");
        report.AppendLine($"  {(ok ? "OK" : "ОШИБКА")}: ведомость стали по классам, диаметрам и типам конструкций");
        return ok ? 0 : 1;
    }

    // ═════════════════════════════ Тактильное моделирование ═════════════════════════════

    private static SceneStateService FrameSceneState()
    {
        var (elements, groups, axes) = CreateFrameScene();
        var scene = new SceneStateService();
        scene.LoadScene(elements, groups, recordUndo: false, clearHistory: true, axes: axes);
        return scene;
    }

    private static string Mm(double meters) => (meters * 1000).ToString("0.0", Inv) + " мм";

    /// <summary>
    /// Стена 4000 мм, повёрнутая на 30°, с окном: вытягивание за грань +X до 6000 мм — торец −X неподвижен (0.0 мм), центр
    /// смещается на +1000 мм вдоль грани, окно сохраняет мировые координаты; один шаг Undo; верх колонны (+Y) — низ на месте;
    /// минимальный размер 0.1 м; окно удерживается в пределах укороченной стены.
    /// </summary>
    private static int CheckPushPull(StringBuilder report)
    {
        int failures = 0;
        var scene = FrameSceneState();
        var wall = new ArchitecturalElement { Id = "el_test_pp_wall", Type = ElementTypes.Wall, Name = "Тест: стена Push/Pull", BuildingGroupId = FrameGroup, Material = "brick" };
        wall.SetSize(4.0, 3.0, 0.3);
        wall.SetPosition(20, -0.02 + 1.5, -10);
        wall.SetRotationY(30);
        var (wx, wz) = ArchitecturalElement.RotateY(-1.0, 0, 30);
        var window = new ArchitecturalElement { Id = "el_test_pp_win", Type = ElementTypes.Window, Name = "Тест: окно в стене", BuildingGroupId = FrameGroup, Material = "glass", HostElementId = wall.Id };
        window.SetSize(1.2, 1.5, 0.36);
        window.SetPosition(20 + wx, 1.6, -10 + wz);
        window.SetRotationY(30);
        scene.AddElements([wall, window], recordUndo: false);

        var negBefore = PushPullService.FaceCenter(wall, PushPullFace.NegX);
        var (cx0, cy0, cz0) = (wall.X, wall.Y, wall.Z);
        var (wx0, wz0) = (window.X, window.Z);
        var dir = PushPullService.Direction(wall, PushPullFace.PosX);
        int depth = scene.UndoDepth;
        scene.PushPull(wall, PushPullFace.PosX, 6.0);
        var negAfter = PushPullService.FaceCenter(wall, PushPullFace.NegX);
        double anchorShift = Math.Sqrt(Math.Pow(negAfter.X - negBefore.X, 2) + Math.Pow(negAfter.Y - negBefore.Y, 2) + Math.Pow(negAfter.Z - negBefore.Z, 2));
        double centerAlong = (wall.X - cx0) * dir.X + (wall.Z - cz0) * dir.Z;
        double centerAcross = Math.Abs((wall.X - cx0) * dir.Z - (wall.Z - cz0) * dir.X) + Math.Abs(wall.Y - cy0);
        double windowShift = Math.Abs(window.X - wx0) + Math.Abs(window.Z - wz0);
        bool ok = Math.Abs(wall.Width - 6.0) < 1e-9 && anchorShift < 1e-9 && Math.Abs(centerAlong - 1.0) < 1e-9 && centerAcross < 1e-9 && windowShift < 1e-9 &&
                  scene.UndoDepth == depth + 1;
        if (!ok) failures++;
        report.AppendLine($"  {(ok ? "OK" : "ОШИБКА")}: стена 4000 → {wall.Width * 1000:0} мм за грань +X (поворот 30°): торец −X сместился на {Mm(anchorShift)}, " +
                          $"центр — на {(centerAlong >= 0 ? "+" : "")}{Mm(centerAlong)} вдоль грани (поперёк {Mm(centerAcross)}), окно — на {Mm(windowShift)}; шагов Undo: {scene.UndoDepth - depth}");

        scene.Undo();
        bool undoOk = Math.Abs(scene.FindById(wall.Id)!.Width - 4.0) < 1e-9 && Math.Abs(scene.FindById(wall.Id)!.X - cx0) < 1e-9;
        if (!undoOk) failures++;
        report.AppendLine($"  {(undoOk ? "OK" : "ОШИБКА")}: Undo вернул длину {scene.FindById(wall.Id)!.Width * 1000:0} мм и центр стены");

        // Грань −X: торец +X неподвижен, центр смещается в обратную сторону.
        wall = scene.FindById(wall.Id)!;
        var posBefore = PushPullService.FaceCenter(wall, PushPullFace.PosX);
        scene.PushPull(wall, PushPullFace.NegX, 4.5);
        var posAfter = PushPullService.FaceCenter(wall, PushPullFace.PosX);
        double posShift = Math.Abs(posAfter.X - posBefore.X) + Math.Abs(posAfter.Z - posBefore.Z);
        bool negOk = posShift < 1e-9 && Math.Abs(wall.Width - 4.5) < 1e-9;
        if (!negOk) failures++;
        report.AppendLine($"  {(negOk ? "OK" : "ОШИБКА")}: грань −X до 4500 мм — торец +X сместился на {Mm(posShift)}");

        // Укорачивание до 1.5 м: окно 1200 мм удерживается между новыми торцами.
        scene.PushPull(wall, PushPullFace.PosX, 1.5);
        window = scene.FindById(window.Id)!;
        var (lx, _) = ArchitecturalElement.RotateY(window.X - wall.X, window.Z - wall.Z, -wall.RotationY);
        bool fitOk = Math.Abs(lx) <= (wall.Width - window.Width) / 2 + 1e-9;
        if (!fitOk) failures++;
        report.AppendLine($"  {(fitOk ? "OK" : "ОШИБКА")}: стена укорочена до {wall.Width * 1000:0} мм — окно в пределах торцов (смещение от оси {Mm(lx)})");

        // Минимальный габарит 0.1 м при «продавливании» грани насквозь.
        var negFixed = PushPullService.FaceCenter(wall, PushPullFace.NegX);
        scene.PushPull(wall, PushPullFace.PosX, -3.0);
        var negFixedAfter = PushPullService.FaceCenter(wall, PushPullFace.NegX);
        bool minOk = Math.Abs(wall.Width - PushPullService.MinSize) < 1e-9 && Math.Abs(negFixedAfter.X - negFixed.X) + Math.Abs(negFixedAfter.Z - negFixed.Z) < 1e-9;
        if (!minOk) failures++;
        report.AppendLine($"  {(minOk ? "OK" : "ОШИБКА")}: L' = max(0.1, L + ΔL) — длина {wall.Width * 1000:0} мм, противоположный торец неподвижен");

        // Верх колонны (+Y): низ (BaseY) неподвижен, растёт только высота.
        var column = scene.Elements.First(e => e.Name == "Колонна 1-11");
        double baseY = column.BaseY, h0 = column.Height;
        scene.PushPull(column, PushPullFace.PosY, h0 + 0.5);
        bool colOk = Math.Abs(column.BaseY - baseY) < 1e-9 && Math.Abs(column.Height - (h0 + 0.5)) < 1e-9 && Math.Abs(column.Y - (baseY + column.Height / 2)) < 1e-9;
        if (!colOk) failures++;
        report.AppendLine($"  {(colOk ? "OK" : "ОШИБКА")}: колонна за грань +Y: H {h0 * 1000:0} → {column.Height * 1000:0} мм, низ {GostDrawingPdfExporter.Elevation(column.BaseY)} (был {GostDrawingPdfExporter.Elevation(baseY)})");
        return failures;
    }

    /// <summary>Привязки OSnap: узел осей 1/А, торец стены, отметка низа плиты при подъёме по Y; вне радиуса — без привязки.</summary>
    private static int CheckSmartSnap(StringBuilder report)
    {
        int failures = 0;
        var (elements, _, axes) = CreateFrameScene();
        var testWall = new ArchitecturalElement { Id = "el_test_snap_wall", Type = ElementTypes.Wall, Name = "Тест: стена OSnap", BuildingGroupId = FrameGroup };
        testWall.SetSize(6, 2.65, 0.3);
        testWall.SetPosition(16, -0.02 + 1.325, -12);
        elements.Add(testWall);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var targets = SmartSnapService.BuildTargets(elements, axes, new HashSet<string>());
        sw.Stop();
        report.AppendLine($"  Целей привязки: {targets.Count} (узлов осей {targets.Count(t => t.Kind == SnapKind.GridIntersection)}, " +
                          $"торцов {targets.Count(t => t.Kind == SnapKind.WallEndpoint)}, граней колонн {targets.Count(t => t.Kind == SnapKind.ColumnFace)}, " +
                          $"краёв плит {targets.Count(t => t.Kind == SnapKind.SlabEdge)}) за {sw.Elapsed.TotalMilliseconds:0.0} мс");

        // 1) Пересечение осей 1/А (−9; 6).
        //    Колонна заходит изнутри каркаса из (−8.75; 5.97) по −X на 0.17 м: рядом торцы стен по осям А и 1, угол стены и центр
        //    колонны в том же узле — узел осей приоритетнее при равном расстоянии.
        var hit = SmartSnapService.SnapAlongAxis([(-8.75, 1.3, 5.97)], (-1, 0, 0), 0.17, targets);
        bool nodeOk = hit is { Target.Kind: SnapKind.GridIntersection } && hit.Label == "Ось 1 / А" && Math.Abs(-8.75 - hit.Delta - -9.0) < 1e-9;
        if (!nodeOk) failures++;
        report.AppendLine($"  {(nodeOk ? "OK" : "ОШИБКА")}: подведение к пересечению осей — «{hit?.Label ?? "нет"}» ({hit?.Target.Kind}), смещение {Mm(hit?.Delta ?? 0)} → X = {-8.75 - (hit?.Delta ?? 0):0.000}");

        // 2) Конец балки в (19.60; −12.05) по −X на 0.45 м → торец тестовой стены (19; −12).
        hit = SmartSnapService.SnapAlongAxis([(19.60, 1.3, -12.05)], (-1, 0, 0), 0.45, targets);
        bool endOk = hit is { Target.Kind: SnapKind.WallEndpoint } && hit.Label == "Торец стены" && Math.Abs(19.60 - hit.Delta - 19.0) < 1e-9;
        if (!endOk) failures++;
        report.AppendLine($"  {(endOk ? "OK" : "ОШИБКА")}: подведение к торцу стены — «{hit?.Label ?? "нет"}» ({hit?.Target.Kind}), X = {19.60 - (hit?.Delta ?? 0):0.000}");

        // 3) Вертикаль: верх колонны 2.50 → +0.10 м — к низу плиты перекрытия (+2,630).
        hit = SmartSnapService.SnapAlongAxis([(0, 2.50, 3)], (0, 1, 0), 0.10, targets);
        bool elevOk = hit is { Target.Shape: SnapShape.Elevation } && Math.Abs(2.50 + hit.Delta - 2.63) < 1e-9;
        if (!elevOk) failures++;
        report.AppendLine($"  {(elevOk ? "OK" : "ОШИБКА")}: подъём по Y — «{hit?.Label ?? "нет"}», отметка {GostDrawingPdfExporter.Elevation(2.50 + (hit?.Delta ?? 0))}");

        // 4) Линия оси между узлами: точка в (−8.84; 2.40) по −X на 0.05 м → линия цифровой оси 1 (x = −9), приоритетнее грани стены по той же линии.
        hit = SmartSnapService.SnapAlongAxis([(-8.84, 1.3, 2.4)], (-1, 0, 0), 0.05, targets);
        bool lineOk = hit is { Target.Kind: SnapKind.GridAxis } && hit.Label == "Ось 1" && Math.Abs(-8.84 - hit.Delta - -9.0) < 1e-9;
        if (!lineOk) failures++;
        report.AppendLine($"  {(lineOk ? "OK" : "ОШИБКА")}: привязка к линии — «{hit?.Label ?? "нет"}» ({hit?.Target.Kind}), X = {-8.84 - (hit?.Delta ?? 0):0.000}");

        // 5) Вне радиуса захвата 0.25 м привязки нет (свободное шаговое перемещение).
        hit = SmartSnapService.SnapAlongAxis([(30.0, 1.3, 30.0)], (1, 0, 0), 0.3, targets);
        bool freeOk = hit is null;
        if (!freeOk) failures++;
        report.AppendLine($"  {(freeOk ? "OK" : "ОШИБКА")}: вдали от целей — без привязки ({hit?.Label ?? "нет"})");
        return failures;
    }

    /// <summary>«Однотипные на этаже»: 12 колонн 1-го этажа; пакетная смена сечения на круг — ровно 1 шаг Undo; Undo возвращает исходные профили.</summary>
    private static int CheckMultiSelection(StringBuilder report)
    {
        int failures = 0;
        var scene = FrameSceneState();
        var sample = scene.Elements.First(e => e.Name == "Колонна 1-11");
        scene.SelectedElement = sample;
        int n = scene.SelectSimilarOnFloor();
        var selected = scene.SelectedElements.ToList();
        bool selOk = n == 12 && selected.Count == 12 && selected.All(e => e.Type == ElementTypes.Column && e.LevelIndex == 0) && scene.SelectedElement == sample;
        if (!selOk) failures++;
        report.AppendLine($"  {(selOk ? "OK" : "ОШИБКА")}: «Однотипные на этаже» — выделено {n} колонн этажа 1 (главная — «{scene.SelectedElement?.Name}»)");

        var before = selected.ToDictionary(e => e.Id, e => (e.EffectiveProfile, e.Width, e.Depth));
        int depth = scene.UndoDepth;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        scene.UpdateElements(selected, e => SceneStateService.ApplyProfile(e, ProfileTypes.Circle));
        sw.Stop();
        bool batchOk = scene.UndoDepth == depth + 1 && selected.All(e => e.EffectiveProfile == ProfileTypes.Circle && Math.Abs(e.Width - e.Depth) < 1e-9);
        if (!batchOk) failures++;
        report.AppendLine($"  {(batchOk ? "OK" : "ОШИБКА")}: пакетная смена сечения → круг у {selected.Count(e => e.EffectiveProfile == ProfileTypes.Circle)} из 12 колонн, " +
                          $"шагов Undo: {scene.UndoDepth - depth}, {sw.Elapsed.TotalMilliseconds:0.0} мс (исходные профили: {string.Join(", ", before.Values.GroupBy(v => v.EffectiveProfile).Select(g => $"{g.Key} ×{g.Count()}"))})");

        scene.Undo();
        var restored = before.Keys.Select(scene.FindById).ToList();
        bool undoOk = restored.All(e => e is not null && before[e.Id] == (e.EffectiveProfile, e.Width, e.Depth)) && scene.SelectedElements.Count == 12;
        if (!undoOk) failures++;
        report.AppendLine($"  {(undoOk ? "OK" : "ОШИБКА")}: Undo вернул исходные сечения всех 12 колонн одним шагом, мультивыделение сохранено ({scene.SelectedElements.Count})");

        // Синхронное смещение группы (Gizmo): все 12 колонн сдвигаются на +0.5 м по X.
        var group = scene.SelectedElements.ToList();
        var starts = group.ToDictionary(g => g.Id, g => new[] { g.X, g.Y, g.Z });
        scene.MoveGroup(group, starts, 0.5, 0, 0);
        bool moveOk = group.All(e => Math.Abs(e.X - starts[e.Id][0] - 0.5) < 1e-9);
        if (!moveOk) failures++;
        report.AppendLine($"  {(moveOk ? "OK" : "ОШИБКА")}: перемещение группы за Gizmo — все {group.Count} колонн сдвинуты синхронно на +500 мм по X");

        // Shift + клик: убрать и вернуть элемент в набор.
        scene.ToggleSelection(group[3]);
        int afterRemove = scene.SelectedElements.Count;
        scene.ToggleSelection(group[3]);
        bool toggleOk = afterRemove == 11 && scene.SelectedElements.Count == 12 && scene.SelectedElement == group[3];
        if (!toggleOk) failures++;
        report.AppendLine($"  {(toggleOk ? "OK" : "ОШИБКА")}: Shift + клик — {afterRemove} → {scene.SelectedElements.Count}, главный элемент — последний добавленный");
        return failures;
    }

    /// <summary>Линейный массив 4 колонн с шагом 6000 мм (1 шаг Undo) и копирование этажа вверх с подъёмом на высоту этажа.</summary>
    private static int CheckArrays(StringBuilder report)
    {
        int failures = 0;
        var scene = FrameSceneState();
        var probe = new ArchitecturalElement { Id = "el_test_array", Type = ElementTypes.Column, Name = "Тест: колонна массива", BuildingGroupId = FrameGroup, Material = "concrete" };
        probe.SetSize(0.4, 2.65, 0.4);
        probe.SetPosition(-9, -0.02 + 1.325, 12);
        scene.AddElement(probe, recordUndo: false);
        int count0 = scene.Elements.Count, depth = scene.UndoDepth;
        var created = scene.CreateLinearArray([probe], 4, 6.0, 0);
        var row = new[] { probe }.Concat(created).OrderBy(e => e.X).ToList();
        bool arrayOk = created.Count == 3 && scene.Elements.Count == count0 + 3 && scene.UndoDepth == depth + 1 &&
                       row.Select((e, i) => Math.Abs(e.X - (-9 + 6.0 * i)) < 1e-9 && Math.Abs(e.Z - 12) < 1e-9 && Math.Abs(e.Y - probe.Y) < 1e-9).All(b => b) &&
                       created.All(e => e.Id != probe.Id && e.Type == ElementTypes.Column);
        if (!arrayOk) failures++;
        report.AppendLine($"  {(arrayOk ? "OK" : "ОШИБКА")}: линейный массив 4 колонн с шагом 6000 мм — X = {string.Join(" / ", row.Select(e => (e.X * 1000).ToString("0", Inv)))} мм, шагов Undo: {scene.UndoDepth - depth}");
        scene.Undo();
        bool undoOk = scene.Elements.Count == count0;
        if (!undoOk) failures++;
        report.AppendLine($"  {(undoOk ? "OK" : "ОШИБКА")}: Undo убрал весь массив ({scene.Elements.Count} элементов)");

        // Массив стены с проёмами: копии проёмов привязаны к своим копиям стены.
        var wallA = scene.Elements.First(e => e.Name == "Стена по оси 1");
        var wallCopies = scene.CreateLinearArray([wallA], 2, 0, -14);
        var copyWall = wallCopies.First(e => e.Type == ElementTypes.Wall);
        bool hostOk = wallCopies.Count(e => e.HostElementId == copyWall.Id) == scene.GetHostedElements(wallA.Id).Count() && wallCopies.Count == 2;
        if (!hostOk) failures++;
        report.AppendLine($"  {(hostOk ? "OK" : "ОШИБКА")}: массив стены — копия с {wallCopies.Count - 1} проёмом(ами), HostElementId переназначен на копию стены");

        // Копирование этажа 3 → 4: подъём на высоту этажа 2850 мм, отверстие кровли — в копии кровли.
        var floor = scene.FloorOf(scene.Elements.First(e => e.Name == "Колонна 3-11"));
        double height = scene.FloorHeight(FrameGroup, 2);
        int maxLevel = scene.MaxLevelIndex;
        var copies = scene.CopyFloorUp(floor);
        var colCopy = copies.FirstOrDefault(e => e.Name == "Колонна 3-11");
        var roofCopy = copies.FirstOrDefault(e => e.Type == ElementTypes.Roof);
        var hatchCopy = copies.FirstOrDefault(e => e.Type == ElementTypes.SlabOpening);
        bool floorOk = copies.Count == floor.Count && Math.Abs(height - 2.85) < 1e-9 && colCopy is { LevelIndex: 3 } && Math.Abs(colCopy.BaseY - (5.68 + 2.85)) < 1e-9 &&
                       hatchCopy is not null && roofCopy is not null && hatchCopy.HostElementId == roofCopy.Id && scene.MaxLevelIndex == maxLevel + 1;
        if (!floorOk) failures++;
        report.AppendLine($"  {(floorOk ? "OK" : "ОШИБКА")}: этаж 3 → этаж 4: {copies.Count} элементов, подъём +{height * 1000:0} мм, колонна на отметке " +
                          $"{GostDrawingPdfExporter.Elevation(colCopy?.BaseY ?? 0)}, люк — в копии кровли; этажей {maxLevel + 1} → {scene.MaxLevelIndex + 1}");
        return failures;
    }

    /// <summary>
    /// Солнце (Москва, лето) в 08:00, 13:00 и 19:00: высота и азимут, длина и направление тени 10-метровой стойки
    /// (L = H / tg h), объединение силуэтов без двойного затемнения (площадь = площадь растра объединения).
    /// </summary>
    private static int CheckSolar(StringBuilder report)
    {
        int failures = 0;
        var pole = new ArchitecturalElement { Type = ElementTypes.Column, Name = "Тест: стойка 10 м" };
        pole.SetSize(0.4, 10, 0.4);
        pole.PlaceOnBase(0);
        var results = new List<(double Hours, SunPosition Sun, double Length, double DirDot, double ExpectedLength)>();
        foreach (double hours in new[] { 8.0, 13.0, 19.0 })
        {
            var sun = SolarShadowService.Compute(hours, SolarSeason.Summer);
            var shadow = SolarShadowService.Build([pole], sun);
            var tip = shadow.Silhouettes.SelectMany(p => p).MaxBy(p => p.X * p.X + p.Z * p.Z);
            double length = Math.Sqrt(tip.X * tip.X + tip.Z * tip.Z);
            double hx = -sun.DirX, hz = -sun.DirZ, hl = Math.Sqrt(hx * hx + hz * hz);
            double dot = (tip.X * hx + tip.Z * hz) / Math.Max(1e-9, length * hl);
            double expected = 10 / Math.Tan(sun.AltitudeDeg * Math.PI / 180);
            results.Add((hours, sun, length, dot, expected));
            // Допуск — полудиагональ сечения стойки 0.4 × 0.4 м (дальний угол оголовка) и отметка теневого меша 0.004 м.
            bool ok = sun.IsAboveHorizon && Math.Abs(length - expected) < 0.35 && dot > 0.995 && Math.Abs(shadow.Area - Math.Abs(SolarShadowService.SignedArea(shadow.Silhouettes[0]))) < 0.01;
            if (!ok) failures++;
            report.AppendLine($"  {(ok ? "OK" : "ОШИБКА")}: {SolarShadowService.TimeText(hours)} — высота {sun.AltitudeDeg:0.0}°, азимут {sun.AzimuthDeg:0.0}°; " +
                              $"тень стойки 10 м: {length:0.00} м (H / tg h = {expected:0.00} м), направление от Солнца cos = {dot:0.0000}, площадь {shadow.Area:0.00} м²");
        }
        bool order = results[0].Sun.AzimuthDeg is > 60 and < 120 && results[1].Sun.AzimuthDeg is > 165 and < 215 && results[2].Sun.AzimuthDeg is > 260 and < 310 &&
                     results[2].Length > results[0].Length && results[0].Length > results[1].Length;
        if (!order) failures++;
        report.AppendLine($"  {(order ? "OK" : "ОШИБКА")}: Солнце восток → юг → запад; вечерняя тень самая длинная ({results[2].Length:0.0} м), полуденная — самая короткая ({results[1].Length:0.0} м)");

        bool warm = results[2].Sun.Warmth > results[1].Sun.Warmth && SolarShadowService.LightColor(results[2].Sun).B < SolarShadowService.LightColor(results[1].Sun).B;
        if (!warm) failures++;
        report.AppendLine($"  {(warm ? "OK" : "ОШИБКА")}: цветовая температура — вечером теплее (тёплость {results[2].Sun.Warmth:0.00} против {results[1].Sun.Warmth:0.00})");

        // Объединение перекрывающихся теней: площадь трапеций = площадь растра объединения (без двойного затемнения).
        var a = new ArchitecturalElement { Type = ElementTypes.Wall };
        a.SetSize(6, 6, 0.3);
        a.PlaceOnBase(0);
        var b = new ArchitecturalElement { Type = ElementTypes.Column };
        b.SetSize(1, 8, 1);
        b.SetPosition(1.5, 4, 1.0);
        var evening = results[2].Sun;
        var union = SolarShadowService.Build([a, b], evening);
        double sum = union.Silhouettes.Sum(p => Math.Abs(SolarShadowService.SignedArea(p)));
        double minX = union.Silhouettes.SelectMany(p => p).Min(p => p.X), maxX = union.Silhouettes.SelectMany(p => p).Max(p => p.X);
        double minZ = union.Silhouettes.SelectMany(p => p).Min(p => p.Z), maxZ = union.Silhouettes.SelectMany(p => p).Max(p => p.Z);
        const double cell = 0.04;
        int covered = 0;
        for (double x = minX + cell / 2; x < maxX; x += cell)
            for (double z = minZ + cell / 2; z < maxZ; z += cell)
                if (union.Silhouettes.Any(p => SceneStateService.PointInPolygon(x, z, p))) covered++;
        double raster = covered * cell * cell;
        bool unionOk = union.Area < sum - 0.5 && Math.Abs(union.Area - raster) / raster < 0.01;
        if (!unionOk) failures++;
        report.AppendLine($"  {(unionOk ? "OK" : "ОШИБКА")}: объединение теней стены и колонны в {SolarShadowService.TimeText(evening.Hours)}: {union.Area:0.00} м² " +
                          $"(растр {raster:0.00} м², сумма силуэтов {sum:0.00} м² — перекрытие не затемняется дважды), трапеций {union.Union.Count}");

        // Производительность на демо-сцене (коттедж) и башне.
        foreach (var preset in new[] { PresetScenesService.Cottage, PresetScenesService.Tower })
        {
            var demo = PresetScenesService.Create(preset, LocalizationService.Russian);
            SolarShadowService.Build(demo.Elements, evening);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var s = SolarShadowService.Build(demo.Elements, evening);
            sw.Stop();
            report.AppendLine($"    «{demo.Title}» в {SolarShadowService.TimeText(evening.Hours)}: силуэтов {s.Silhouettes.Count}, трапеций {s.Union.Count}, площадь тени {s.Area:0} м², {sw.Elapsed.TotalMilliseconds:0.0} мс");
        }
        return failures;
    }

    private static List<(double X, double Z)> Outline(ArchitecturalElement e)
    {
        if (e.Type == ElementTypes.Column && e.EffectiveProfile is ProfileTypes.Circle or ProfileTypes.Pipe && e.Polygon2D.Count < 3)
            return Enumerable.Range(0, 24).Select(i => 2 * Math.PI * i / 24)
                .Select(a => ArchitecturalElement.RotateY(Math.Cos(a) * e.Width / 2, Math.Sin(a) * e.Depth / 2, e.RotationY))
                .Select(p => (e.X + p.X, e.Z + p.Z)).Select(p => (Math.Round(p.Item1, 6), Math.Round(p.Item2, 6))).ToList();
        return ElementGeometryBuilder.WorldOutline(e);
    }

    private static int CompareAxes(List<StructuralGridAxis> src, List<StructuralGridAxis> dst, StringBuilder report, string tag)
    {
        int failures = 0;
        foreach (var a in src)
            if (!dst.Any(b => b.Label == a.Label && b.Direction == a.Direction && Math.Abs(b.Coordinate - a.Coordinate) <= Tol))
            {
                failures++;
                report.AppendLine($"  {tag}: ось «{a.Label}» ({a.DirectionTitle} = {a.Coordinate}) не восстановлена");
            }
        if (dst.Count != src.Count) { failures++; report.AppendLine($"  {tag}: осей {src.Count} → {dst.Count}"); }
        else report.AppendLine($"  Оси: {dst.Count} из {src.Count} совпали ({string.Join(" ", dst.OrderBy(a => a.IsNumeric ? 0 : 1).ThenBy(a => a.Coordinate).Select(a => $"{a.Label}={a.Coordinate:0.###}"))})");
        return failures;
    }

    /// <summary>Чертёж «как из AutoCAD»: миллиметры без $INSUNITS, слои «СТЕНЫ», «КОЛОННЫ», «ОСИ», кириллица ANSI-1251.</summary>
    private static byte[] BuildForeignDxf()
    {
        var sb = new StringBuilder();
        void P(int code, string v) => sb.Append(code).Append('\n').Append(v).Append('\n');
        void Line(string layer, double x1, double y1, double x2, double y2)
        {
            P(0, "LINE"); P(8, layer); P(10, F(x1)); P(20, F(y1)); P(30, "0"); P(11, F(x2)); P(21, F(y2)); P(31, "0");
        }
        P(0, "SECTION"); P(2, "ENTITIES");
        // Габарит 20400 × 12500 мм со смещением: колонны 400×400 и круглые Ø500.
        double ox = 100000, oy = 50000;
        foreach (var (x, y, round) in new[] { (200.0, 200.0, false), (10200.0, 200.0, true), (20200.0, 200.0, false), (200.0, 12300.0, false), (10200.0, 12300.0, true), (20200.0, 12300.0, false) })
        {
            if (round) { P(0, "CIRCLE"); P(8, "КОЛОННЫ"); P(10, F(ox + x)); P(20, F(oy + y)); P(30, "0"); P(40, "250"); continue; }
            P(0, "LWPOLYLINE"); P(8, "КОЛОННЫ"); P(90, "4"); P(70, "1");
            foreach (var (dx, dy) in new[] { (-200.0, -200.0), (200.0, -200.0), (200.0, 200.0), (-200.0, 200.0) }) { P(10, F(ox + x + dx)); P(20, F(oy + y + dy)); }
        }
        // Две стены 300 мм — спаренными линиями.
        Line("СТЕНЫ", ox + 500, oy + 50, ox + 9900, oy + 50);
        Line("СТЕНЫ", ox + 500, oy + 350, ox + 9900, oy + 350);
        Line("СТЕНЫ", ox + 10500, oy + 12150, ox + 19900, oy + 12150);
        Line("СТЕНЫ", ox + 10500, oy + 12450, ox + 19900, oy + 12450);
        // Оси: 3 цифровые, 2 буквенные, марки — текстом в кружках.
        foreach (var (x, label) in new[] { (200.0, "1"), (10200.0, "2"), (20200.0, "3") })
        {
            Line("ОСИ", ox + x, oy - 1500, ox + x, oy + 14000);
            P(0, "CIRCLE"); P(8, "ОСИ"); P(10, F(ox + x)); P(20, F(oy - 2000)); P(30, "0"); P(40, "400");
            P(0, "TEXT"); P(8, "ОСИ"); P(10, F(ox + x)); P(20, F(oy - 2000)); P(30, "0"); P(40, "350"); P(1, label);
        }
        foreach (var (y, label) in new[] { (200.0, "А"), (12300.0, "Б") })
        {
            Line("ОСИ", ox - 1500, oy + y, ox + 22000, oy + y);
            P(0, "TEXT"); P(8, "ОСИ"); P(10, F(ox - 2000)); P(20, F(oy + y)); P(30, "0"); P(40, "350"); P(1, label);
        }
        P(0, "ENDSEC"); P(0, "EOF");
        // Кириллица ANSI-1251 как в старых версиях AutoCAD.
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(1251).GetBytes(sb.ToString());
    }

    private static string F(double v) => v.ToString("0.###", Inv);

    /// <summary>Растеризация первой страницы PDF через Windows.Data.Pdf в PNG (для визуальной проверки).</summary>
    public static async Task RenderPdfAsync(string pdfPath, string pngPath, uint longSide, uint pageIndex = 0)
    {
        var file = await StorageFile.GetFileFromPathAsync(Path.GetFullPath(pdfPath));
        var pdf = await PdfDocument.LoadFromFileAsync(file);
        using var page = pdf.GetPage(Math.Min(pageIndex, pdf.PageCount - 1));
        double scale = longSide / Math.Max(page.Size.Width, page.Size.Height);
        using var stream = new InMemoryRandomAccessStream();
        await page.RenderToStreamAsync(stream, new PdfPageRenderOptions
        {
            DestinationWidth = (uint)Math.Round(page.Size.Width * scale),
            DestinationHeight = (uint)Math.Round(page.Size.Height * scale),
            BackgroundColor = Windows.UI.Color.FromArgb(255, 255, 255, 255),
            BitmapEncoderId = BitmapEncoder.PngEncoderId
        });
        stream.Seek(0);
        var bytes = new byte[stream.Size];
        using (var reader = new DataReader(stream.GetInputStreamAt(0)))
        {
            await reader.LoadAsync((uint)stream.Size);
            reader.ReadBytes(bytes);
        }
        await File.WriteAllBytesAsync(pngPath, bytes);
    }
}
#endif
